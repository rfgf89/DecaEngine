// Общие объявления SSR-пассов (см. SsrPass.cs): реконструкция вида, шум, кбуфер ручек.
// Конвенции реконструкции - ровно те же, что в SsaoCommon/SsgiCommon: infinite reversed-Z
// (z_view = near / depth), фиксированный FOV 45 (ModelViewportEnvironment.CameraFovDegrees).
#ifndef SSR_COMMON_INCLUDED
#define SSR_COMMON_INCLUDED

#include "Instancing.hlsl"

cbuffer View
{
    ViewData viewData;
}

// Живые ручки SSR. Заливается командой UpdateBuffer из пасса (см. SsrPassResources - тот же
// приём живой CPU-памяти, что у MotionVectorConstants: SetConstant на замороженном графе
// переустанавливал бы переменную SRB под летящим кадром). Паддинг скалярами - см. SsaoCommon.
cbuffer SsrConstants
{
    // Счётчик кадров - фаза шума стохастической выборки (голден-ратио сдвиг IGN).
    float ssrFrameIndex;
    // Потолок perceptual roughness: выше отражения плавно гаснут (луч всё равно один на пиксель,
    // и на матовых поверхностях остаточный шум дороже, чем недостающий спекуляр).
    float ssrMaxRoughness;
    // Толщина поверхности при проверке пересечения, мировые единицы: луч, ушедший за глубину
    // ГЛУБЖЕ этой толщины, считается прошедшим ПОЗАДИ тонкого объекта и продолжает марш.
    float ssrThickness;
    // Дальность луча в мировых единицах.
    float ssrMaxDistance;

    // Поворот энвайронмента вокруг Y (тот же PbrEnvYaw, что в UnlitInstancedPS) - композит
    // обязан вычесть ровно тот env-цвет, который сложил форвард-пасс.
    float ssrEnvYaw;
    // Вес истории темпоральной аккумуляции (0..0.97): один стохастический луч на пиксель без
    // истории - это снег, а не отражение.
    float ssrHistoryWeight;
    // 0 - обычный кадр, 1 - только отражения (rgb*conf), 2 - confidence, 3 - нормали G-buffer.
    float ssrDebugView;
    // Множитель заменяющего отражения (художественная ручка; 1 - энергетически честно).
    float ssrIntensity;

    // Направление НА солнце (мир) - шейдинг хитов RT-фолбэка.
    float4 ssrSunDirWorld;
    // rgb - цвет*интенсивность солнца (те же константы, что у ключа превью - см.
    // SimpleCullingAndRenderSystem), w - ambient-уровень RT-хитов: множитель диффузной
    // env-иррадианса (SampleEnvironment по нормали хита, roughness 1) - тот же ambientLevel,
    // что у форвард-пасса.
    float4 ssrSunColor;

    // x - пар переиспользуемых лучей в резолве (1..4, кламп в шейдере): главный рычаг шум/цена.
    // Остальное - паддинг (см. SsaoCommon про выравнивание).
    float ssrRaysPerPixel;

    // ВСЕГО отскоков RT-луча (1..4): 1 - только первичный луч, 2+ - зеркальные продолжения
    // с металлических хитов («зеркало в зеркале», см. цикл в SsrTracePS). Только RT-вариант.
    float ssrBounces;

    // Режим трассировки: 0 - экранный марш, затем RT для промахнувшихся лучей; 1 - СРАЗУ RT
    // (марш пропускается целиком). Экранные ДАННЫЕ во втором режиме никуда не деваются -
    // радианс в точке хита по-прежнему берётся с экрана репроекцией; уходит только сам марш
    // с его артефактами (ложные хиты за тонкой геометрией, ошибки толщины, затухание у краёв
    // кадра). Действует только в варианте с FEATURE_RT_REFLECTIONS.
    float ssrTraceMode;
    float ssrQualityPad2;

    // Сетка probe-поля для шейдинга RT-хитов (см. SsrSampleProbeField в SsrTracePS): те же
    // origin/cell/counts, что материалы получают в ProbeGrid* (ProbeGiViewportShared.PushGrid).
    // origin.w = 1 - поле привязано; 0 - атласы держат плейсхолдер, ветка мертва.
    float4 ssrProbeOrigin;
    float4 ssrProbeCell;
    float4 ssrProbeCounts;

    // viewProj ПРОШЛОГО кадра - репроекция ВИРТУАЛЬНОГО образа отражения у зеркал (RTG гл.32,
    // Reflection Motion Vectors; см. SsrResolvePS). До первой защёлки - единичная.
    float4x4 ssrPrevViewProj;
}

// Ниже этой шероховатости пиксель считается зеркальным: трейс берёт детерминированное
// направление (стохастика на зеркале не сходится, а дрожит), резолв считает его pdf
// аналитически (трейс у таких пикселей кладёт в rayHit.z ДЛИНУ луча - для репроекции
// виртуального образа). Общая константа трейса и резолва.
static const float SsrMirrorRoughness = 0.08;

// PDF зеркального пути по пику лоба (H = N) - та же формула у трейса и резолва.
// Литеральное пи: SsrPI объявлен ниже по файлу.
float SsrMirrorPdf(float roughness)
{
    float m = max(roughness * roughness, 1e-3);
    float m2 = m * m;
    return m2 / (3.14159265359 * m2 * m2);
}

// Нелинейная реконструкция иррадианса из SH L1 - копия NonLinearIrradianceL1 из
// UnlitInstancedPS.hlsl (обоснование смеси линейной и нелинейной форм - там же).
float SsrIrradianceL1(float R0, float3 R1v, float3 n)
{
    float len = length(R1v);
    if (R0 <= 1e-6 || len <= 1e-8)
    {
        return max(R0, 0.0);
    }

    float r = saturate(len / R0);
    float linearForm = R0 + 2.0 * dot(R1v, n);
    if (r <= 0.5)
    {
        return linearForm;
    }

    float q = 0.5 * (1.0 + dot(R1v / len, n));
    float p = 1.0 + 2.0 * r;
    float a = (1.0 - r) / (1.0 + r);
    float nonLinear = R0 * (a + (1.0 - a) * (p + 1.0) * pow(q, p));

    return lerp(linearForm, nonLinear, smoothstep(0.5, 0.8, r));
}

static const float SsrPI = 3.14159265359;
static const float SsrTanHalfFov = 0.41421356; // tan(45deg / 2)
static const float SsrNearPlane = 0.05;        // CameraData near (ModelViewportEnvironment)

struct VSOutput
{
    float4 pos : SV_POSITION;
    float2 ndc : TEXCOORD0;
};

float SsrViewDepth(float rawDepth)
{
    return SsrNearPlane / max(rawDepth, 1e-7);
}

float3 SsrViewPos(int2 pixel, float rawDepth, float2 viewportSize)
{
    float zView = SsrViewDepth(rawDepth);
    float2 uv = (pixel + 0.5) / viewportSize;
    float2 ndc = float2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
    float aspect = viewportSize.x / max(viewportSize.y, 1.0);
    return float3(ndc.x * SsrTanHalfFov * aspect * zView, ndc.y * SsrTanHalfFov * zView, zView);
}

// Проекция view-точки в UV экрана (обратная SsrViewPos).
float2 SsrProjectUv(float3 viewPos, float2 viewportSize)
{
    float aspect = viewportSize.x / max(viewportSize.y, 1.0);
    return float2(
        viewPos.x / (viewPos.z * SsrTanHalfFov * aspect) * 0.5 + 0.5,
        0.5 - viewPos.y / (viewPos.z * SsrTanHalfFov) * 0.5);
}

// Interleaved gradient noise (Jimenez) с покадровым сдвигом: соседние пиксели И соседние кадры
// получают разные фазы - пространственный шум собирает резолв соседей, временной - история.
float SsrNoise(float2 pixel, float offset)
{
    pixel += offset * 5.588238;
    return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
}

// BRDF bias (Frostbite/Stachowiak, значение из референсной реализации Stochastic SSR):
// выборка лоба поджимается к зеркальному направлению - лучи почти не разбредаются, а
// корректная ФОРМА лоба восстанавливается в резолве весом BRDF/PDF (ratio estimator).
static const float SsrBrdfBias = 0.7;

// Важностная выборка GGX-полусферы вокруг нормали (Karis, "Real Shading in UE4"): возвращает
// половинный вектор, pdf - в w (в мере половинных векторов, D * cos(theta) - ровно то, чем
// потом делится вес резолва, см. SsrBrdfWeight).
float4 SsrSampleGgxHalfVector(float3 N, float roughness, float u1, float u2)
{
    float m = max(roughness * roughness, 1e-3);
    float m2 = m * m;

    // Поджатие к зеркалу - см. SsrBrdfBias.
    u1 = lerp(u1, 0.0, SsrBrdfBias);

    float cosTheta = sqrt((1.0 - u1) / (1.0 + (m2 - 1.0) * u1));
    float sinTheta = sqrt(saturate(1.0 - cosTheta * cosTheta));
    float phi = 2.0 * SsrPI * u2;

    float3 up = abs(N.z) < 0.9 ? float3(0.0, 0.0, 1.0) : float3(1.0, 0.0, 0.0);
    float3 tangent = normalize(cross(up, N));
    float3 bitangent = cross(N, tangent);

    float d = (cosTheta * m2 - cosTheta) * cosTheta + 1.0;
    float D = m2 / (SsrPI * d * d);
    float pdf = D * cosTheta;

    return float4(normalize(tangent * (sinTheta * cos(phi))
                          + bitangent * (sinTheta * sin(phi))
                          + N * cosTheta), pdf);
}

// Вес переиспользования чужого луча (ratio estimator, Stachowiak "Stochastic SSR"): BRDF
// ЭТОГО пикселя по НАПРАВЛЕНИЮ соседа, делённый на pdf соседа. D-GGX * G-Walter, без
// френеля/нормировки - постоянные множители сокращаются при делении на сумму весов.
float SsrBrdfWeight(float3 V, float3 L, float3 N, float roughness)
{
    float3 H = normalize(L + V);
    float NdotH = saturate(dot(N, H));
    float NdotL = saturate(dot(N, L));
    float NdotV = saturate(dot(N, V));

    float m = max(roughness * roughness, 1e-3);
    float m2 = m * m;

    float d = (NdotH * m2 - NdotH) * NdotH + 1.0;
    float D = m2 / (SsrPI * d * d);

    float gl = 1.0 / (NdotL + sqrt(m2 + (1.0 - m2) * NdotL * NdotL));
    float gv = 1.0 / (NdotV + sqrt(m2 + (1.0 - m2) * NdotV * NdotV));

    return D * gl * gv * (SsrPI / 4.0);
}

// Октаэдральная упаковка направления в [0..1]^2 - RT-хиты хранят в hit-буфере направление
// луча вместо экранного UV (см. SsrTracePS/SsrResolvePS).
float2 SsrOctEncode(float3 v)
{
    v /= abs(v.x) + abs(v.y) + abs(v.z);
    float2 oct = v.z >= 0.0
        ? v.xy
        : (1.0 - abs(v.yx)) * float2(v.x >= 0.0 ? 1.0 : -1.0, v.y >= 0.0 ? 1.0 : -1.0);
    return oct * 0.5 + 0.5;
}

float3 SsrOctDecode(float2 e)
{
    e = e * 2.0 - 1.0;
    float3 v = float3(e.x, e.y, 1.0 - abs(e.x) - abs(e.y));
    if (v.z < 0.0)
    {
        v.xy = (1.0 - abs(v.yx)) * float2(v.x >= 0.0 ? 1.0 : -1.0, v.y >= 0.0 ? 1.0 : -1.0);
    }
    return normalize(v);
}

// Мировая equirect-карта окружения с GGX-префильтрованными мипами - тот же контракт, что у
// SampleEnvironment в UnlitInstancedPS (EnvMipMax обязан совпадать с PreviewEnvironmentMap).
static const float SsrEnvMipMax = 6.0;

float3 SsrSampleEnvironment(Texture2D envMap, SamplerState envSampler, float3 dir, float roughness)
{
    float2 uv = float2(atan2(dir.z, dir.x) / (2.0 * SsrPI) + 0.5 + ssrEnvYaw / (2.0 * SsrPI),
                       acos(clamp(dir.y, -1.0, 1.0)) / SsrPI);
    return envMap.SampleLevel(envSampler, uv, roughness * SsrEnvMipMax).rgb;
}

// Затухание у кромок экрана: луч, чей хит уехал к границе, вот-вот потеряет данные - плавный
// спад вместо мигающего обрыва на движении камеры.
float SsrEdgeFade(float2 uv)
{
    float2 fade = saturate((0.5 - abs(uv - 0.5)) / 0.08);
    return fade.x * fade.y;
}

// Спад по шероховатости к потолку ssrMaxRoughness.
float SsrRoughnessFade(float roughness)
{
    return 1.0 - smoothstep(ssrMaxRoughness * 0.7, ssrMaxRoughness, roughness);
}

// world -> view для направлений (view - ортонормальная ротация + перенос, строчная конвенция).
float3 SsrWorldDirToView(float3 dir)
{
    return normalize(mul(float4(dir, 0.0), viewData.view).xyz);
}

// view -> world для направлений: v*M = u  =>  v = u*M^T.
float3 SsrViewDirToWorld(float3 dir)
{
    return normalize(mul(dir, transpose((float3x3)viewData.view)));
}

#endif
