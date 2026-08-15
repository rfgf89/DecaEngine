// DecaFfxShim - нативный мост DecaEngine <-> AMD FidelityFX ffx-api (FSR upscaler, D3D12).
//
// Зачем шим вообще: managed-биндинг Diligent не открывает D3D12-интерфейсы, но у каждого
// обёрнутого объекта есть NativePointer (нативный Diligent-объект), а у текстур -
// GetNativeHandle() (ID3D12Resource*). Этого достаточно:
//   - ID3D12Device достаётся из ЛЮБОГО ресурса через ID3D12Resource::GetDevice - заголовки
//     Diligent для этого не нужны вовсе;
//   - командный лист текущего кадра - через Diligent::IDeviceContextD3D12::GetD3D12CommandList
//     (QueryInterface от NativePointer immediate-контекста). Заголовки DiligentCore взяты РОВНО
//     той версии, что нативные DLL биндинга (v2.5.6) - раскладка vtable обязана совпадать.
//
// Контракт вызывающего (C#, см. FsrUpscaler.cs):
//   - все входные ресурсы переведены в ShaderResource, выход - в UnorderedAccess, ДО Dispatch;
//     ffx-api расставляет свои барьеры сам и ВОЗВРАЩАЕТ ресурсы в заявленные состояния;
//   - после Dispatch вызывающий обязан позвать IDeviceContext.InvalidateState() (командный лист
//     трогали мимо Diligent - его кэш стейтов протух; это прямо прописано в доке
//     GetD3D12CommandList).

#include <windows.h>
#include <d3d12.h>
#include <cstdint>
#include <cstdio>
#include <cwchar>

#define PLATFORM_WIN32 1
#define NOMINMAX 1
#include "Graphics/GraphicsEngineD3D12/interface/DeviceContextD3D12.h"

#include "ffx_api.h"
#include "dx12/ffx_api_dx12.h"
#include "ffx_upscale.h"

// ---------------------------------------------------------------------------------------------
// Динамическая загрузка amd_fidelityfx_upscaler_dx12.dll: она экспортирует плоский ffx-api
// (ffxCreateContext/ffxDispatch/...) напрямую, отдельный loader не нужен (проверено dumpbin).
// ---------------------------------------------------------------------------------------------

static PfnFfxCreateContext  s_ffxCreateContext  = nullptr;
static PfnFfxDestroyContext s_ffxDestroyContext = nullptr;
static PfnFfxDispatch       s_ffxDispatch       = nullptr;
static PfnFfxQuery          s_ffxQuery          = nullptr;
static PfnFfxConfigure      s_ffxConfigure      = nullptr;

// Последнее сообщение рантайма FSR (ошибки валидации и т.п.) - C# забирает его для логов пробы.
static wchar_t s_lastMessage[1024] = L"";

static void FfxMessageCallback(uint32_t type, const wchar_t* message)
{
    swprintf(s_lastMessage, _countof(s_lastMessage), L"[ffx %s] %s",
        type == FFX_API_MESSAGE_TYPE_ERROR ? L"error" : L"warning", message ? message : L"");
}

static bool EnsureFfxLoaded()
{
    if (s_ffxCreateContext)
    {
        return true;
    }

    // Рядом с шимом (оба кладутся в bin редактора), затем стандартный поиск.
    HMODULE mod = LoadLibraryW(L"amd_fidelityfx_upscaler_dx12.dll");
    if (!mod)
    {
        swprintf(s_lastMessage, _countof(s_lastMessage),
            L"LoadLibrary(amd_fidelityfx_upscaler_dx12.dll) failed, error=%lu", GetLastError());
        return false;
    }

    s_ffxCreateContext  = (PfnFfxCreateContext)GetProcAddress(mod, "ffxCreateContext");
    s_ffxDestroyContext = (PfnFfxDestroyContext)GetProcAddress(mod, "ffxDestroyContext");
    s_ffxDispatch       = (PfnFfxDispatch)GetProcAddress(mod, "ffxDispatch");
    s_ffxQuery          = (PfnFfxQuery)GetProcAddress(mod, "ffxQuery");
    s_ffxConfigure      = (PfnFfxConfigure)GetProcAddress(mod, "ffxConfigure");

    if (!s_ffxCreateContext || !s_ffxDestroyContext || !s_ffxDispatch)
    {
        swprintf(s_lastMessage, _countof(s_lastMessage), L"ffx exports missing in upscaler dll");
        s_ffxCreateContext = nullptr;
        return false;
    }

    return true;
}

struct DecaFsrContext
{
    ffxContext context = nullptr;
};

extern "C" {

// Подключает DirectX Agility SDK: последующие D3D12CreateDevice пойдут через редист из sdkPath
// (относительный путь от экзешника, файл обязан называться D3D12Core.dll), а не через встроенный
// рантайм Windows. ЗВАТЬ ДО создания устройства Diligent-ом. Требует включённого режима
// разработчика Windows (контракт ID3D12SDKConfiguration::SetSDKVersion) - при выключенном вернёт
// ошибку, и процесс просто останется на встроенном рантайме.
__declspec(dllexport) int32_t __cdecl DecaAgility_Init(uint32_t sdkVersion, const wchar_t* sdkPath)
{
    ID3D12SDKConfiguration* config = nullptr;
    HRESULT hr = D3D12GetInterface(CLSID_D3D12SDKConfiguration, IID_PPV_ARGS(&config));
    if (FAILED(hr) || !config)
    {
        return (int32_t)hr;
    }

    char utf8Path[512] = {};
    WideCharToMultiByte(CP_UTF8, 0, sdkPath ? sdkPath : L".\\D3D12\\", -1, utf8Path, _countof(utf8Path) - 1,
        nullptr, nullptr);

    hr = config->SetSDKVersion(sdkVersion, utf8Path);
    config->Release();

    printf("[shim] agility: SetSDKVersion(%u, \"%s\") hr=0x%08x%s\n", sdkVersion, utf8Path,
        (unsigned)hr, FAILED(hr) ? " (нужен режим разработчика Windows?)" : "");
    fflush(stdout);
    return (int32_t)hr;
}

} // extern "C"

// Флаги DecaFsr_Create - зеркалятся в C# (FsrUpscaler.CreateFlags).
enum DecaFsrCreateFlags : uint32_t
{
    DECA_FSR_HDR            = 1u << 0,
    DECA_FSR_DEPTH_INVERTED = 1u << 1,
    DECA_FSR_DEPTH_INFINITE = 1u << 2,
    DECA_FSR_AUTO_EXPOSURE  = 1u << 3,
    DECA_FSR_DEBUG_CHECKING = 1u << 4,
    DECA_FSR_DEBUG_VISUALIZATION = 1u << 5,
};

extern "C" {

__declspec(dllexport) const wchar_t* __cdecl DecaFsr_LastMessage()
{
    return s_lastMessage;
}

// Версия загруженного провайдера апскейла (для лога): пишет имя первой версии в buf.
__declspec(dllexport) int32_t __cdecl DecaFsr_QueryVersion(void* anyResource, char* buf, int32_t bufLen)
{
    if (!EnsureFfxLoaded() || !anyResource || !buf || bufLen < 2)
    {
        return -1;
    }

    ID3D12Resource* res = (ID3D12Resource*)anyResource;
    ID3D12Device* device = nullptr;
    if (FAILED(res->GetDevice(IID_PPV_ARGS(&device))))
    {
        return -2;
    }

    uint64_t count = 0;
    ffxQueryDescGetVersions versions{};
    versions.header.type = FFX_API_QUERY_DESC_TYPE_GET_VERSIONS;
    versions.createDescType = FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE;
    versions.device = device;
    versions.outputCount = &count;
    if (s_ffxQuery(nullptr, &versions.header) != FFX_API_RETURN_OK || count == 0)
    {
        device->Release();
        return -3;
    }

    if (count > 8) count = 8;
    uint64_t ids[8] = {};
    const char* names[8] = {};
    versions.versionIds = ids;
    versions.versionNames = names;
    s_ffxQuery(nullptr, &versions.header);
    device->Release();

    buf[0] = 0;
    int32_t written = 0;
    for (uint64_t i = 0; i < count && written < bufLen - 1; i++)
    {
        written += snprintf(buf + written, bufLen - written, "%s%s", i ? "; " : "",
            names[i] ? names[i] : "?");
    }

    return (int32_t)count;
}

__declspec(dllexport) int32_t __cdecl DecaFsr_Create(
    void* anyResource,   // ID3D12Resource* (ITexture.GetNativeHandle) - источник устройства
    uint32_t maxRenderW, uint32_t maxRenderH,
    uint32_t displayW, uint32_t displayH,
    uint32_t flags,
    int32_t providerMajor,   // 0 - автополитика, 2/3/4 - явная ветка (выбор из UI)
    void** outCtx)
{
    s_lastMessage[0] = 0;

    if (!EnsureFfxLoaded())
    {
        return -100;
    }

    if (!anyResource || !outCtx)
    {
        return -101;
    }

    ID3D12Resource* res = (ID3D12Resource*)anyResource;
    ID3D12Device* device = nullptr;
    if (FAILED(res->GetDevice(IID_PPV_ARGS(&device))))
    {
        return -102;
    }

    ffxCreateBackendDX12Desc backend{};
    backend.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_BACKEND_DX12;
    backend.device = device;

    // ОБЯЗАТЕЛЬНЫЙ дескриптор версии API (см. ffx_upscale.h: "This must be set to
    // FFX_UPSCALER_VERSION"): сообщает провайдеру, под какую раскладку структур собрано
    // приложение. Без него провайдер ГАДАЕТ - и ветка 3.1.x читала наш ffxDispatchDescUpscale по
    // чужим смещениям: мусор вместо джиттера/масштабов, отсюда каша, нечувствительная ни к каким
    // параметрам (все они просто не доезжали).
    ffxCreateContextDescUpscaleVersion apiVersion{};
    apiVersion.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE_VERSION;
    apiVersion.version = FFX_UPSCALER_VERSION;

    ffxCreateContextDescUpscale desc{};
    desc.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE;
    desc.header.pNext = &apiVersion.header;
    apiVersion.header.pNext = &backend.header;
    desc.maxRenderSize = { maxRenderW, maxRenderH };
    desc.maxUpscaleSize = { displayW, displayH };
    desc.fpMessage = FfxMessageCallback;
    desc.flags = 0;
    if (flags & DECA_FSR_HDR)            desc.flags |= FFX_UPSCALE_ENABLE_HIGH_DYNAMIC_RANGE;
    if (flags & DECA_FSR_DEPTH_INVERTED) desc.flags |= FFX_UPSCALE_ENABLE_DEPTH_INVERTED;
    if (flags & DECA_FSR_DEPTH_INFINITE) desc.flags |= FFX_UPSCALE_ENABLE_DEPTH_INFINITE;
    if (flags & DECA_FSR_AUTO_EXPOSURE)  desc.flags |= FFX_UPSCALE_ENABLE_AUTO_EXPOSURE;
    if (flags & DECA_FSR_DEBUG_CHECKING) desc.flags |= FFX_UPSCALE_ENABLE_DEBUG_CHECKING;
    if (flags & DECA_FSR_DEBUG_VISUALIZATION) desc.flags |= FFX_UPSCALE_ENABLE_DEBUG_VISUALIZATION;

    // Диагностика доставки: полная нечувствительность выхода к флагам - сама по себе улика, и
    // первым делом надо видеть, что они вообще доехали.
    printf("[shim] create: flags=0x%x render=%ux%u display=%ux%u\n",
        desc.flags, maxRenderW, maxRenderH, displayW, displayH);

    // Выбор провайдера (запрос версий + ffxOverrideVersion в цепочке создания). ДЕФОЛТ - НОВЕЙШЕЕ
    // поколение, которое рантайм предлагает под ЭТО железо, за одним известным исключением: ветка
    // 3.1.x на этой связке SDK/железа сводит кадр в кашу (замерено: mean|grad| 0.23 против 1.89 у
    // 2.3.4 при идентичных входах - дескрипторы, флаги и параметры диспатча сверены прошивкой),
    // поэтому без явного запроса она не выбирается. Итого: 4.x+ (FSR4, ML-путь RDNA4) > 2.x >
    // (если кроме 3.x ничего нет) дефолт рантайма. DECA_FSR_PROVIDER=2|3 - явный выбор ветки,
    // =0 - отдать выбор рантайму безусловно.
    ffxOverrideVersion versionOverride{};
    char wantMajor = (providerMajor >= 2 && providerMajor <= 9) ? (char)('0' + providerMajor) : 0;

    // Env-переопределение - ПОВЕРХ выбора из UI: диагностика важнее настройки.
    const char* providerEnv = getenv("DECA_FSR_PROVIDER");
    if (providerEnv)
    {
        wantMajor = providerEnv[0];
    }
    if (wantMajor != '0')
    {
        uint64_t count = 8;
        uint64_t versionIds[8] = {};
        const char* versionNames[8] = {};
        ffxQueryDescGetVersions versions{};
        versions.header.type = FFX_API_QUERY_DESC_TYPE_GET_VERSIONS;
        versions.createDescType = FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE;
        versions.device = device;
        versions.outputCount = &count;
        versions.versionIds = versionIds;
        versions.versionNames = versionNames;
        if (s_ffxQuery(nullptr, &versions.header) == FFX_API_RETURN_OK)
        {
            int32_t best = -1;
            char bestMajor = 0;
            for (uint64_t i = 0; i < count; i++)
            {
                if (!versionNames[i] || !versionNames[i][0])
                {
                    continue;
                }

                char major = versionNames[i][0];
                if (wantMajor)
                {
                    // Явный запрос: первая (новейшая в списке рантайма) версия своей ветки.
                    if (major == wantMajor) { best = (int32_t)i; break; }
                }
                else if (major != '3' && major > bestMajor)
                {
                    best = (int32_t)i;
                    bestMajor = major;
                }
            }

            if (best >= 0)
            {
                versionOverride.header.type = FFX_API_DESC_TYPE_OVERRIDE_VERSION;
                versionOverride.versionId = versionIds[best];
                backend.header.pNext = &versionOverride.header;
                printf("[shim] provider: %s (из %llu доступных)\n",
                    versionNames[best], (unsigned long long)count);
            }
        }
    }

    DecaFsrContext* ctx = new DecaFsrContext();
    ffxReturnCode_t rc = s_ffxCreateContext(&ctx->context, &desc.header, nullptr);

    device->Release();

    if (rc != FFX_API_RETURN_OK)
    {
        delete ctx;
        return (int32_t)rc;
    }

    // Что провайдер считает ОБЯЗАТЕЛЬНЫМИ входами (битовое поле FfxApiQueryResourceIdentifiers:
    // 1 color, 2 depth, 4 mv, 8 exposure, 16 reactive, 32 transparency) - диагностика веток,
    // которым молча не хватает входа, который мы передаём пустым.
    ffxQueryDescUpscaleGetResourceRequirements reqs{};
    reqs.header.type = FFX_API_QUERY_DESC_TYPE_UPSCALE_GET_RESOURCE_REQUIREMENTS;
    if (s_ffxQuery(&ctx->context, &reqs.header) == FFX_API_RETURN_OK)
    {
        printf("[shim] resource requirements: required=0x%llx optional=0x%llx\n",
            (unsigned long long)reqs.required_resources, (unsigned long long)reqs.optional_resources);
        fflush(stdout);
    }

    // DECA_FSR_VELFACTOR=<float> - конфиг-ключ fVelocityFactor (расследование каши 3.1.x: 0.0
    // повышает темпоральную стабильность, см. FfxApiConfigureUpscaleKey).
    if (const char* velEnv = getenv("DECA_FSR_VELFACTOR"))
    {
        static float s_velocityFactor;
        s_velocityFactor = (float)atof(velEnv);
        ffxConfigureDescUpscaleKeyValue kv{};
        kv.header.type = FFX_API_CONFIGURE_DESC_TYPE_UPSCALE_KEYVALUE;
        kv.key = FFX_API_CONFIGURE_UPSCALE_KEY_FVELOCITYFACTOR;
        kv.ptr = &s_velocityFactor;
        ffxReturnCode_t kvRc = s_ffxConfigure(&ctx->context, &kv.header);
        printf("[shim] fVelocityFactor=%.2f rc=%u\n", s_velocityFactor, kvRc);
        fflush(stdout);
    }

    *outCtx = ctx;
    return 0;
}

__declspec(dllexport) int32_t __cdecl DecaFsr_Dispatch(
    void* ctxPtr,
    void* diligentContext,   // Diligent::IDeviceContext* (CppObject.NativePointer)
    void* colorRes, void* depthRes, void* motionRes, void* outputRes,   // ID3D12Resource*
    void* reactiveRes, void* transparencyRes,   // опциональные маски (может быть null)
    float jitterX, float jitterY,
    float mvScaleX, float mvScaleY,
    uint32_t renderW, uint32_t renderH,
    uint32_t upscaleW, uint32_t upscaleH,
    float frameTimeDeltaMs,
    float cameraNear, float cameraFar, float fovYRad,
    int32_t reset, int32_t sharpen, float sharpness, int32_t debugView)
{
    s_lastMessage[0] = 0;

    DecaFsrContext* ctx = (DecaFsrContext*)ctxPtr;
    if (!ctx || !ctx->context || !diligentContext)
    {
        return -101;
    }

    // Командный лист текущего кадра из Diligent-контекста. QueryInterface делает AddRef -
    // отпускаем сразу после использования. Лист НЕ кэшировать: любой вызов Diligent может
    // сабмитнуть его и сделать невалидным (см. док DeviceContextD3D12.h).
    Diligent::IObject* obj = (Diligent::IObject*)diligentContext;
    Diligent::IDeviceContextD3D12* ctx12 = nullptr;
    obj->QueryInterface(Diligent::IID_DeviceContextD3D12, (Diligent::IObject**)&ctx12);
    if (!ctx12)
    {
        swprintf(s_lastMessage, _countof(s_lastMessage),
            L"QueryInterface(IID_DeviceContextD3D12) failed - контекст не D3D12?");
        return -103;
    }

    ID3D12GraphicsCommandList* cmdList = ctx12->GetD3D12CommandList();
    ctx12->Release();
    if (!cmdList)
    {
        return -104;
    }

    // Разовая печать дескрипторов входов - сверка, что хэндлы указывают на ОЖИДАЕМЫЕ ресурсы
    // (диагностика GetNativeHandle: формат/размер обязаны совпасть с таргетами конвейера).
    static bool s_loggedDescs = false;
    if (!s_loggedDescs)
    {
        s_loggedDescs = true;
        auto logDesc = [](const char* name, void* p)
        {
            if (!p) { printf("[shim] %s: null\n", name); return; }
            D3D12_RESOURCE_DESC d = ((ID3D12Resource*)p)->GetDesc();
            printf("[shim] %s: fmt=%d %llux%u mips=%u\n", name, (int)d.Format,
                (unsigned long long)d.Width, d.Height, d.MipLevels);
        };
        logDesc("color ", colorRes);
        logDesc("depth ", depthRes);
        logDesc("motion", motionRes);
        logDesc("output", outputRes);
        printf("[shim] dispatch: jitter=(%.3f,%.3f) mvScale=(%.1f,%.1f) render=%ux%u upscale=%ux%u "
               "dt=%.2fms near=%.3f far=%.1f fov=%.3f reset=%d\n",
            jitterX, jitterY, mvScaleX, mvScaleY, renderW, renderH, upscaleW, upscaleH,
            frameTimeDeltaMs, cameraNear, cameraFar, fovYRad, reset);
        fflush(stdout);
    }

    ffxDispatchDescUpscale dispatch{};
    dispatch.header.type = FFX_API_DISPATCH_DESC_TYPE_UPSCALE;
    dispatch.commandList = cmdList;
    dispatch.color = ffxApiGetResourceDX12((ID3D12Resource*)colorRes, FFX_API_RESOURCE_STATE_PIXEL_COMPUTE_READ);
    dispatch.depth = ffxApiGetResourceDX12((ID3D12Resource*)depthRes, FFX_API_RESOURCE_STATE_PIXEL_COMPUTE_READ);

    // Депт - единственный TYPELESS-вход (Diligent создаёт D32-текстуры как R32_TYPELESS ради
    // SRV-бинда), и ffxApiGetResourceDX12 честно записывает R32_TYPELESS в дескриптор. Типизируем
    // принудительно: по typeless SRV не создать, и провайдер, не имеющий special-case (ветка
    // 3.1.x), читал бы глубину нулями - чёрная плитка глубины и залитая маска дисокклюзии в его
    // debug-мозаике ровно об этом. DECA_FSR_DEPTH_TYPELESS=1 возвращает сырой формат для A/B.
    if (dispatch.depth.description.format == FFX_API_SURFACE_FORMAT_R32_TYPELESS &&
        !getenv("DECA_FSR_DEPTH_TYPELESS"))
    {
        dispatch.depth.description.format = FFX_API_SURFACE_FORMAT_R32_FLOAT;
    }
    dispatch.motionVectors = ffxApiGetResourceDX12((ID3D12Resource*)motionRes, FFX_API_RESOURCE_STATE_PIXEL_COMPUTE_READ);
    dispatch.output = ffxApiGetResourceDX12((ID3D12Resource*)outputRes, FFX_API_RESOURCE_STATE_UNORDERED_ACCESS, FFX_API_RESOURCE_USAGE_UAV);

    // Маски по контракту опциональны, но НУЛЕВЫЕ 1x1 честнее пустого дескриптора: официальный
    // сэмпл AMD всегда подаёт обе, и подозрение по нашей мозаике - что null-биндинг опциональной
    // маски в новых ветках читается мусором (расследование каши 3.1.x).
    if (reactiveRes)
    {
        dispatch.reactive = ffxApiGetResourceDX12((ID3D12Resource*)reactiveRes, FFX_API_RESOURCE_STATE_PIXEL_COMPUTE_READ);
    }
    if (transparencyRes)
    {
        dispatch.transparencyAndComposition = ffxApiGetResourceDX12((ID3D12Resource*)transparencyRes, FFX_API_RESOURCE_STATE_PIXEL_COMPUTE_READ);
    }
    dispatch.jitterOffset = { jitterX, jitterY };
    dispatch.motionVectorScale = { mvScaleX, mvScaleY };
    dispatch.renderSize = { renderW, renderH };
    dispatch.upscaleSize = { upscaleW, upscaleH };
    dispatch.enableSharpening = sharpen != 0;
    dispatch.sharpness = sharpness;
    dispatch.frameTimeDelta = frameTimeDeltaMs;
    dispatch.preExposure = 1.0f;
    dispatch.reset = reset != 0;
    dispatch.cameraNear = cameraNear;
    dispatch.cameraFar = cameraFar;
    dispatch.cameraFovAngleVertical = fovYRad;
    dispatch.viewSpaceToMetersFactor = 1.0f;
    dispatch.flags = debugView ? FFX_UPSCALE_FLAG_DRAW_DEBUG_VIEW : 0u;

    // DECA_FSR_DISPATCH_FLAGS=<int> - принудительные биты FfxApiDispatchFsrUpscaleFlags
    // (диагностика цветового пространства: 2 = NON_LINEAR_COLOR_SRGB, 4 = PQ).
    if (const char* dfEnv = getenv("DECA_FSR_DISPATCH_FLAGS"))
    {
        dispatch.flags |= (uint32_t)atoi(dfEnv);
    }

    ffxReturnCode_t rc = s_ffxDispatch(&ctx->context, &dispatch.header);
    return rc == FFX_API_RETURN_OK ? 0 : (int32_t)rc;
}

// Версия АКТИВНОГО провайдера созданного контекста ("2.3.4" и т.п.) - для подписи бэкенда в UI.
__declspec(dllexport) int32_t __cdecl DecaFsr_GetVersion(void* ctxPtr, char* buf, int32_t len)
{
    DecaFsrContext* ctx = (DecaFsrContext*)ctxPtr;
    if (!ctx || !ctx->context || !buf || len < 2)
    {
        return -1;
    }

    ffxQueryGetProviderVersion query{};
    query.header.type = FFX_API_QUERY_DESC_TYPE_GET_PROVIDER_VERSION;
    if (s_ffxQuery(&ctx->context, &query.header) != FFX_API_RETURN_OK || !query.versionName)
    {
        return -2;
    }

    snprintf(buf, len, "%s", query.versionName);
    return 0;
}

__declspec(dllexport) void __cdecl DecaFsr_Destroy(void* ctxPtr)
{
    DecaFsrContext* ctx = (DecaFsrContext*)ctxPtr;
    if (!ctx)
    {
        return;
    }

    if (ctx->context)
    {
        s_ffxDestroyContext(&ctx->context, nullptr);
    }

    delete ctx;
}

} // extern "C"

// =============================================================================================
// DLSS (NVIDIA NGX, D3D12) - второй нативный бэкенд того же слота. Статически линкуется
// nvsdk_ngx_d.lib; рантайм nvngx_dlss.dll NGX ищет сам рядом с экзешником. Отличие от ffx-api:
// СОЗДАНИЕ фичи требует командный лист (NGX пишет в него init-работы), поэтому фича создаётся
// ЛЕНИВО на первом диспатче - там лист уже есть.
// =============================================================================================

#include "nvsdk_ngx.h"
#include "nvsdk_ngx_helpers.h"

struct DecaDlssContext
{
    ID3D12Device*        device = nullptr;
    NVSDK_NGX_Parameter* params = nullptr;
    NVSDK_NGX_Handle*    feature = nullptr;
    uint32_t renderW = 0, renderH = 0, displayW = 0, displayH = 0;
    int32_t  quality = (int32_t)NVSDK_NGX_PerfQuality_Value_Balanced;
};

static bool s_ngxInitialized = false;

extern "C" {

__declspec(dllexport) int32_t __cdecl DecaDlss_Create(
    void* anyResource,   // ID3D12Resource* - источник устройства
    uint32_t renderW, uint32_t renderH,
    uint32_t displayW, uint32_t displayH,
    int32_t quality,     // NVSDK_NGX_PerfQuality_Value (0 perf, 1 balanced, 2 quality, 5 DLAA)
    void** outCtx)
{
    s_lastMessage[0] = 0;

    if (!anyResource || !outCtx)
    {
        return -101;
    }

    ID3D12Device* device = nullptr;
    if (FAILED(((ID3D12Resource*)anyResource)->GetDevice(IID_PPV_ARGS(&device))))
    {
        return -102;
    }

    if (!s_ngxInitialized)
    {
        // Project-ID-инициализация - штатный путь для не-зарегистрированных приложений
        // (NVSDK_NGX_ENGINE_TYPE_CUSTOM). Каталог данных - текущий (логи NGX).
        NVSDK_NGX_Result r = NVSDK_NGX_D3D12_Init_with_ProjectID(
            "a0f57b54-1daf-4934-90ae-c4035c19df04", NVSDK_NGX_ENGINE_TYPE_CUSTOM, "1.0",
            L".", device);
        if (NVSDK_NGX_FAILED(r))
        {
            swprintf(s_lastMessage, _countof(s_lastMessage), L"NGX init failed: 0x%x (%s)",
                (unsigned)r, GetNGXResultAsString(r));
            device->Release();
            return (int32_t)r;
        }

        s_ngxInitialized = true;
    }

    NVSDK_NGX_Parameter* caps = nullptr;
    if (NVSDK_NGX_FAILED(NVSDK_NGX_D3D12_GetCapabilityParameters(&caps)) || !caps)
    {
        device->Release();
        return -103;
    }

    int dlssAvailable = 0;
    caps->Get(NVSDK_NGX_Parameter_SuperSampling_Available, &dlssAvailable);
    if (!dlssAvailable)
    {
        int reason = 0;
        caps->Get(NVSDK_NGX_Parameter_SuperSampling_FeatureInitResult, &reason);
        swprintf(s_lastMessage, _countof(s_lastMessage),
            L"DLSS unavailable on this device/driver (init result 0x%x)", (unsigned)reason);
        NVSDK_NGX_D3D12_DestroyParameters(caps);
        device->Release();
        return -104;
    }

    DecaDlssContext* ctx = new DecaDlssContext();
    ctx->device = device;
    ctx->params = caps;
    ctx->renderW = renderW;
    ctx->renderH = renderH;
    ctx->displayW = displayW;
    ctx->displayH = displayH;
    ctx->quality = quality;

    printf("[shim] dlss: доступен, render=%ux%u display=%ux%u quality=%d\n",
        renderW, renderH, displayW, displayH, quality);
    fflush(stdout);

    *outCtx = ctx;
    return 0;
}

// Создание NGX-фичи: пишет init-команды в ТЕКУЩИЙ командный лист Diligent-контекста. Вызывающий
// ОБЯЗАН сразу после успеха сделать Flush + WaitForIdle + InvalidateState - init должен отработать
// на GPU до первого evaluate, а кэш стейтов Diligent после чужих команд недостоверен.
__declspec(dllexport) int32_t __cdecl DecaDlss_CreateFeature(void* ctxPtr, void* diligentContext)
{
    s_lastMessage[0] = 0;

    DecaDlssContext* ctx = (DecaDlssContext*)ctxPtr;
    if (!ctx || !ctx->params || !diligentContext)
    {
        return -101;
    }

    if (ctx->feature)
    {
        return 0;
    }

    Diligent::IObject* obj = (Diligent::IObject*)diligentContext;
    Diligent::IDeviceContextD3D12* ctx12 = nullptr;
    obj->QueryInterface(Diligent::IID_DeviceContextD3D12, (Diligent::IObject**)&ctx12);
    if (!ctx12)
    {
        return -103;
    }

    ID3D12GraphicsCommandList* cmdList = ctx12->GetD3D12CommandList();
    ctx12->Release();
    if (!cmdList)
    {
        return -104;
    }

    NVSDK_NGX_DLSS_Create_Params create{};
    create.Feature.InWidth = ctx->renderW;
    create.Feature.InHeight = ctx->renderH;
    create.Feature.InTargetWidth = ctx->displayW;
    create.Feature.InTargetHeight = ctx->displayH;
    create.Feature.InPerfQualityValue = (NVSDK_NGX_PerfQuality_Value)ctx->quality;
    create.InFeatureCreateFlags =
        NVSDK_NGX_DLSS_Feature_Flags_IsHDR |
        NVSDK_NGX_DLSS_Feature_Flags_MVLowRes |
        NVSDK_NGX_DLSS_Feature_Flags_DepthInverted |
        NVSDK_NGX_DLSS_Feature_Flags_AutoExposure;

    NVSDK_NGX_Result r = NGX_D3D12_CREATE_DLSS_EXT(cmdList, 1, 1, &ctx->feature, ctx->params, &create);
    if (NVSDK_NGX_FAILED(r))
    {
        swprintf(s_lastMessage, _countof(s_lastMessage), L"DLSS create failed: 0x%x (%s)",
            (unsigned)r, GetNGXResultAsString(r));
        ctx->feature = nullptr;
        return (int32_t)r;
    }

    printf("[shim] dlss: фича создана (quality=%d, HDR|MVLowRes|DepthInverted|AutoExposure)\n", ctx->quality);
    fflush(stdout);
    return 0;
}

__declspec(dllexport) int32_t __cdecl DecaDlss_Dispatch(
    void* ctxPtr,
    void* diligentContext,
    void* colorRes, void* depthRes, void* motionRes, void* outputRes,
    float jitterX, float jitterY,
    float mvScaleX, float mvScaleY,
    uint32_t renderW, uint32_t renderH,
    float frameTimeDeltaMs,
    int32_t reset)
{
    s_lastMessage[0] = 0;

    DecaDlssContext* ctx = (DecaDlssContext*)ctxPtr;
    if (!ctx || !ctx->params || !diligentContext)
    {
        return -101;
    }

    Diligent::IObject* obj = (Diligent::IObject*)diligentContext;
    Diligent::IDeviceContextD3D12* ctx12 = nullptr;
    obj->QueryInterface(Diligent::IID_DeviceContextD3D12, (Diligent::IObject**)&ctx12);
    if (!ctx12)
    {
        return -103;
    }

    ID3D12GraphicsCommandList* cmdList = ctx12->GetD3D12CommandList();
    ctx12->Release();
    if (!cmdList)
    {
        return -104;
    }

    // Фича создаётся ЗАРАНЕЕ отдельным вызовом DecaDlss_CreateFeature (её init-команды тяжёлые и
    // обязаны быть засабмичены и исполнены ДО первого кадра): создание внутри кадрового листа с
    // немедленным evaluate в нём же роняло редактор AV-ом на следующем SetPipelineState.
    if (!ctx->feature)
    {
        swprintf(s_lastMessage, _countof(s_lastMessage), L"DLSS feature not created (call DecaDlss_CreateFeature)");
        return -105;
    }

    NVSDK_NGX_D3D12_DLSS_Eval_Params eval{};
    eval.Feature.pInColor = (ID3D12Resource*)colorRes;
    eval.Feature.pInOutput = (ID3D12Resource*)outputRes;
    eval.pInDepth = (ID3D12Resource*)depthRes;
    eval.pInMotionVectors = (ID3D12Resource*)motionRes;
    eval.InJitterOffsetX = jitterX;
    eval.InJitterOffsetY = jitterY;
    eval.InMVScaleX = mvScaleX;
    eval.InMVScaleY = mvScaleY;
    eval.InRenderSubrectDimensions = { renderW, renderH };
    eval.InReset = reset;
    eval.InFrameTimeDeltaInMsec = frameTimeDeltaMs;

    NVSDK_NGX_Result r = NGX_D3D12_EVALUATE_DLSS_EXT(cmdList, ctx->feature, ctx->params, &eval);
    if (NVSDK_NGX_FAILED(r))
    {
        swprintf(s_lastMessage, _countof(s_lastMessage), L"DLSS evaluate failed: 0x%x (%s)",
            (unsigned)r, GetNGXResultAsString(r));
        return (int32_t)r;
    }

    return 0;
}

__declspec(dllexport) void __cdecl DecaDlss_Destroy(void* ctxPtr)
{
    DecaDlssContext* ctx = (DecaDlssContext*)ctxPtr;
    if (!ctx)
    {
        return;
    }

    if (ctx->feature)
    {
        NVSDK_NGX_D3D12_ReleaseFeature(ctx->feature);
    }

    if (ctx->params)
    {
        NVSDK_NGX_D3D12_DestroyParameters(ctx->params);
    }

    if (ctx->device)
    {
        ctx->device->Release();
    }

    delete ctx;
}

} // extern "C"
