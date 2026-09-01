// Стохастическая трассировка экранных отражений (см. SsrPass.cs) - архитектура Stachowiak
// "Stochastic Screen-Space Reflections" (референс: Xerxes1138/StochasticScreenSpaceReflection):
// ОДИН GGX-важностный луч на пиксель с BRDF bias (выборка поджата к зеркалу), выход двумя MRT:
//   RT0 - цвет хита + confidence (вход AABB темпорального клампа и фолбэк резолва);
//   RT1 - hit-буфер: экранный UV хита (или октаэдральное направление RT-хита) + PDF луча +
//         маска (1 - экранный хит, 0.5 - RT-хит, 0 - промах).
// Резолв (SsrResolvePS) переиспользует лучи соседей с весом BRDF/PDF - форма glossy-лоба
// восстанавливается физикой, а не ручным радиусом размытия.
//
// Маршрутизация по шероховатости (Eto et al., SIGGRAPH Asia 2023):
//   - выше SsrDiffuseRoughness лоб GGX почти косинусный - отражение берётся ПРЯМО из probe-поля
//     (иррадианса в точке поверхности), без единого луча: ноль шума и интерьерно-корректный
//     цвет вместо env-карты;
//   - ниже SsrMirrorRoughness направление детерминированно зеркальное (стохастика на зеркале не
//     сходится, а дрожит);
//   - между ними - стохастический GGX-луч.
//
// Вариант с FEATURE_RT_REFLECTIONS (DXC/SM6.5, см. DiligentShader): лучи, промахнувшиеся мимо
// экрана, добираются inline RayQuery по TLAS сцены (SceneTrace.hlsl - та же геометрия, что у
// probe GI). Хит, видимый на экране, берёт ГОТОВЫЙ пиксель кадра (репроекция, там же у AMD);
// внеэкранный шейдится аналитически: альбедо * (солнце с теневым лучом + probe-поле + лампы),
// с одним дополнительным зеркальным отскоком для металлических хитов (см. SsrMetalAlbedoLum).
#include "SsrCommon.hlsl"

Texture2D<float> _DepthTex;
Texture2D _NormalRoughTex;
Texture2D _EnvFactorTex;
Texture2D _SceneTex;
SamplerState _SceneTex_sampler;

// Полукадровая размытая копия снимка сцены (см. SsrSceneBlurPS) - «cone tracing для бедных»:
// шероховатый луч читает заранее размытый кадр вместо усреднения резких сэмплов резолвом.
Texture2D _SceneBlurTex;
SamplerState _SceneBlurTex_sampler;

Texture2D _EnvMap;
SamplerState _EnvMap_sampler;

// Атласы probe-поля (SH L1) - свет RT-хитов и маршрут шероховатых поверхностей. Объявлены
// безусловно (Vulkan требует привязки объявленных слотов - см. VUID-08114 в памяти проекта),
// без поля держат плейсхолдер и мертвы по ssrProbeOrigin.w.
Texture2D _ProbeSh0;
Texture2D _ProbeSh1;
Texture2D _ProbeSh2;
Texture2D _ProbeSh3;

// Потолки циклов - статические (FXC/DXC не разворачивают неограниченный [loop]).
static const int SsrMaxSteps = 48;
static const int SsrRefineSteps = 6;

// Порог зеркального пути (SsrMirrorRoughness) переехал в SsrCommon.hlsl - он общий с резолвом
// (там по нему выбирается аналитический pdf и репроекция виртуального образа).

// Выше этой шероховатости отражение идёт из probe-поля (см. шапку). Порог сшит с плавным
// переходом веса в композите через RoughnessFade - резкой границы техник в кадре нет.
static const float SsrDiffuseRoughness = 0.75;

// (Эвристика «металл по тёмному альбедо» удалена: металличность запечена у каждого
// треугольника - см. BvhTriangle.metalness; темнота альбедо металлом больше не считается.)

// Смещение старта луча от самопересечения - ОТ МАСШТАБА (доля дистанции), а не константа:
// мировые 0.02 на сцене, где весь зал - три юнита, толще её стен, и теневые лучи с отскоками
// стартовали ПО ТУ СТОРОНУ тонкой геометрии - сквозь стены просвечивало небо.
float SsrRayEpsilon(float dist)
{
    return clamp(0.005 * dist, 5e-4, 0.05);
}

// Упрощённый сэмпл probe-поля: трилинейная интерполяция восьми угловых проб плотной сетки с
// DDGI wrap-весом по нормали и валидностью из альфы Sh1. БЕЗ теста Чебышёва и релокации
// (полный вариант - ProbeGiSampleBody.hlsl): отражение шероховатостью и без того фильтруется,
// редкая протечка в нём дешевле шести дополнительных Load-ов на угол.
// Возвращает E/PI - готовый ламбертов множитель альбедо; valid = 0 - поля нет/точка вне объёма.
float3 SsrSampleProbeField(float3 worldPos, float3 N, out float valid)
{
    valid = 0.0;
    if (ssrProbeOrigin.w < 0.5)
    {
        return float3(0.0, 0.0, 0.0);
    }

    float3 counts3 = ssrProbeCounts.xyz;
    float3 f = (worldPos - ssrProbeOrigin.xyz) / ssrProbeCell.xyz;
    if (any(f < 0.0) || any(f > counts3 - 1.0))
    {
        return float3(0.0, 0.0, 0.0);
    }

    int3 counts = (int3)counts3;
    int3 localCell = clamp((int3)floor(f), 0, counts - 2);
    float3 t = saturate(f - (float3)localCell);

    float4 sum0 = 0.0;
    float3 sumX = 0.0, sumY = 0.0, sumZ = 0.0;
    float weightSum = 0.0;

    [loop]
    for (int corner = 0; corner < 8; corner++)
    {
        int3 offset = int3(corner & 1, (corner >> 1) & 1, corner >> 2);
        int3 lp = localCell + offset;

        // Узел -> тексель: плоскости Z столбиком (зеркало ProbeGiBaker.ProbeTexel; прокрутка
        // с плотной сеткой снята, заворота нет).
        int3 texel = int3(lp.x, lp.z * counts.y + lp.y, 0);

        // Валидность (пробы в стенах не интерполируются) - первой, чтобы не тянуть Load-ы зря.
        float4 sh1 = _ProbeSh1.Load(texel);
        float w = sh1.a;
        if (w < 1e-3)
        {
            continue;
        }

        // Мягкий backface-вес (DDGI wrap shading) - проба позади поверхности хита не тянет своё
        // поле сквозь стену; константы - как в ProbeGiSampleBody.
        float3 probeWorld = ssrProbeOrigin.xyz + (float3)lp * ssrProbeCell.xyz;
        float3 toProbe = probeWorld - worldPos;
        float wrap = (dot(toProbe / max(length(toProbe), 1e-4), N) + 1.0) * 0.5;
        w *= wrap * wrap + 0.2;

        float trilinear = (offset.x ? t.x : 1.0 - t.x)
                        * (offset.y ? t.y : 1.0 - t.y)
                        * (offset.z ? t.z : 1.0 - t.z);
        w *= trilinear;
        if (w < 1e-5)
        {
            continue;
        }

        sum0 += _ProbeSh0.Load(texel) * w;
        sumX += sh1.rgb * w;
        sumY += _ProbeSh2.Load(texel).rgb * w;
        sumZ += _ProbeSh3.Load(texel).rgb * w;
        weightSum += w;
    }

    if (weightSum < 1e-4)
    {
        return float3(0.0, 0.0, 0.0);
    }

    valid = 1.0;
    float inv = 1.0 / weightSum;
    return float3(
        SsrIrradianceL1(sum0.r * inv, float3(sumX.r, sumY.r, sumZ.r) * inv, N),
        SsrIrradianceL1(sum0.g * inv, float3(sumX.g, sumY.g, sumZ.g) * inv, N),
        SsrIrradianceL1(sum0.b * inv, float3(sumX.b, sumY.b, sumZ.b) * inv, N));
}

// Цвет кадра в точке экранного хита: резкий снимок и его размытая полукадровая копия смешиваются
// по шероховатости ОТРАЖАЮЩЕЙ поверхности - шероховатый луч видит уже размытую сцену, и резолву
// остаётся меньше дисперсии на усреднение (см. mip-цепочку у Stachowiak - здесь один уровень).
float3 SsrSceneColor(float2 uv, float roughness)
{
    float3 sharp = _SceneTex.SampleLevel(_SceneTex_sampler, uv, 0.0).rgb;
    float blurAmount = smoothstep(0.15, 0.6, roughness);
    if (blurAmount <= 0.0)
    {
        return sharp;
    }

    float3 blurred = _SceneBlurTex.SampleLevel(_SceneBlurTex_sampler, uv, 0.0).rgb;
    return lerp(sharp, blurred, blurAmount);
}

// Видна ли мировая точка на экране ЭТОГО кадра: проекция + сверка глубины (допуск - доля
// глубины: хит и пиксель обязаны быть одной поверхностью, а не совпадением вдоль луча взгляда)
// + сверка НОРМАЛИ хита с G-buffer-ом пикселя: одна глубина вдоль луча взгляда законно
// принадлежит РАЗНЫМ поверхностям (кромки, параллельные стены), и репроекция без нормали
// подсовывала отражению цвет чужой стены - «текстуры уехали». Несовпавший хит честно уходит
// в аналитический шейдинг со СВОИМИ текстурами. abs() - хит может быть изнанкой двусторонней
// плоскости, чей экранный пиксель показывает лицо.
bool SsrTryScreenHit(float3 worldPos, float3 hitNormal, float2 viewportSize, out float2 uv)
{
    uv = float2(0.0, 0.0);

    float4 clip = mul(float4(worldPos, 1.0), viewData.viewProj);
    if (clip.w <= 1e-4)
    {
        return false;
    }

    uv = float2(clip.x / clip.w * 0.5 + 0.5, 0.5 - clip.y / clip.w * 0.5);
    if (any(uv < 0.0) || any(uv > 1.0))
    {
        return false;
    }

    int2 pixel = clamp(int2(uv * viewportSize), int2(0, 0), int2(viewportSize) - 1);
    float raw = _DepthTex.Load(int3(pixel, 0));
    if (raw < 1e-6)
    {
        return false;
    }

    float sceneZ = SsrViewDepth(raw);
    float pointZ = mul(float4(worldPos, 1.0), viewData.view).z;

    // Допуск - доля глубины с МАЛЫМ полом: абсолютные 0.05 на мелкомасштабной сцене «сшивали»
    // хит с чужой поверхностью в полкомнаты от него.
    if (abs(sceneZ - pointZ) >= max(0.025 * sceneZ, 0.005))
    {
        return false;
    }

    float3 screenN = _NormalRoughTex.Load(int3(pixel, 0)).xyz;
    return dot(screenN, screenN) < 0.5 || abs(dot(normalize(screenN), hitNormal)) > 0.35;
}

#if FEATURE_RT_REFLECTIONS
#define SCENE_TRACE_HARDWARE 1
#include "SceneTrace.hlsl"

// Свет камерного сегмента punctual-пула - лампы в шейдинге RT-хитов (привязка -
// IBatchRenderer.BindShadowResources, как у объёмника; см. VolumetricCommon.hlsl).
cbuffer Light
{
    LightData lightData;
}

StructuredBuffer<PunctualLight> PunctualLights;

#if FEATURE_RT_HIT_ATLAS
// Текстуры хитов, ДЕШЁВЫЙ режим: атлас-массив со слоем 128^2 на base color текстуру сцены
// (даунсемпл CPU-плиток либо плитка среднего цвета - см. SsrHitTextures). Слой - индекс из
// таблицы инстансов; Wrap-сэмплер отрабатывает заворот UV сам, слои независимы.
Texture2DArray _SceneHitAtlas;
SamplerState _SceneHitAtlas_sampler;
#endif

#if FEATURE_RT_HIT_BINDLESS
// Текстуры хитов, ДОРОГОЙ режим: полноразмерные base color текстуры сцены фиксированным
// массивом (= ProbeInstancedGeometry.MaxHitTextures; свободные слоты добиты плейсхолдером).
// Выборка через Load без сэмплера: combined-sampler режиму Diligent не нужна пара
// _sampler на чисто Load-текстуру, а заворот и выбор мипа делаются вручную ниже.
Texture2D _SceneHitTex[64];
#endif

// Альбедо RT-хита: настоящая текстура по UV хита, когда режим текстур включён и у инстанса
// она есть; иначе - потриугольное усреднённое альбедо из TLAS-таблиц. Кламп 0.85 и линейный
// BaseColorFactor - те же, что у потриугольного пути (единый баланс энергии мультибаунса).
float3 SsrHitAlbedo(SceneHit hit, float roughness)
{
#if FEATURE_RT_HIT_ATLAS
    if (hit.textureIndex >= 0)
    {
        float3 texel = _SceneHitAtlas.SampleLevel(_SceneHitAtlas_sampler,
            float3(hit.uv, (float)hit.textureIndex), 0.0).rgb;
        // Текстуры движка без sRGB-формата - линеаризация вручную (2.2, как всюду).
        return min(pow(max(texel, 0.0), 2.2) * hit.baseColorFactor, 0.85);
    }
#elif FEATURE_RT_HIT_BINDLESS
    if (hit.textureIndex >= 0)
    {
        uint index = NonUniformResourceIndex((uint)hit.textureIndex);
        uint w, h, mips;
        _SceneHitTex[index].GetDimensions(0, w, h, mips);

        // Мип по ФУТПРИНТУ луча: мировое пятно = дистанция хита * угловой размер пикселя;
        // плотность текселей у хита неизвестна (производных UV нет), берётся эвристика
        // «текстура растянута на ~4 мировых юнита» - для архитектуры даёт мип в +-1 от
        // честного, а ошибка в СТОРОНУ мелкого мипа безопасна: билинейная выборка ниже всё
        // равно фильтрует. Прежний фикс-мип «~256px» точечным Load-ом рассыпал вышитую ткань
        // в близком зеркале конфетти. Шероховатость расширяет конус лоба.
        float pixelAngle = 2.0 / max(viewData.viewport.w, 1.0);
        float texelsAcross = hit.t * pixelAngle * (float)max(w, h) * 0.25;
        float mipF = log2(max(texelsAcross, 1.0)) + roughness * 2.0;
        uint mip = min((uint)mipF, mips - 1u);
        uint mipW = max(w >> mip, 1u);
        uint mipH = max(h >> mip, 1u);

        // Билинейная выборка вручную (Load не фильтрует) с заворотом соседей как у Wrap.
        float2 texelPos = frac(hit.uv) * float2(mipW, mipH) - 0.5;
        float2 baseFloor = floor(texelPos);
        float2 blend = texelPos - baseFloor;
        int2 size = int2(mipW, mipH);
        int2 c00 = (int2(baseFloor) % size + size) % size;
        int2 c11 = (c00 + 1) % size;
        float3 t00 = _SceneHitTex[index].Load(int3(c00, mip)).rgb;
        float3 t10 = _SceneHitTex[index].Load(int3(c11.x, c00.y, mip)).rgb;
        float3 t01 = _SceneHitTex[index].Load(int3(c00.x, c11.y, mip)).rgb;
        float3 t11 = _SceneHitTex[index].Load(int3(c11, mip)).rgb;
        float3 texel = lerp(lerp(t00, t10, blend.x), lerp(t01, t11, blend.x), blend.y);

        return min(pow(max(texel, 0.0), 2.2) * hit.baseColorFactor, 0.85);
    }
#endif
    return hit.albedo;
}

// Аналитический свет в точке внеэкранного хита (без альбедо): солнце с теневым лучом по TLAS +
// probe-поле (фолбэк - диффузная env-иррадианса с ambientLevel) + punctual-света сегмента камеры
// (линейный проход - кластерная сетка экранная, точке хита не принадлежит; формулы затухания и
// конуса - как в VolumetricCommon.hlsl, тень - теневым лучом вместо карт).
float3 SsrAnalyticHitLight(float3 pos, float3 hitN, float eps, int2 noisePixel)
{
    float3 sunDir = normalize(ssrSunDirWorld.xyz);
    float ndl = saturate(dot(hitN, sunDir));
    float sunLit = 1.0;
    if (ndl > 0.0)
    {
        // Теневой луч рассеивается в конусе УГЛОВОГО РАЗМЕРА СОЛНЦА (ssrSunDirWorld.w - тангенс
        // половинного угла, та же ручка, что у PCSS прямого вида). Один луч на пиксель остаётся
        // бинарным, но направление джиттерится по пикселю и кадру, и темпоральная аккумуляция
        // собирает из этого полутень. Без конуса край тени в отражении был ступенькой в чёрное:
        // прямой вид даёт мягкую границу, отражение - жёсткую, и глаз читал это как ошибку.
        float tanHalf = ssrSunDirWorld.w;
        if (tanHalf > 1e-5)
        {
            float3 up = abs(sunDir.y) < 0.95 ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0);
            float3 tx = normalize(cross(up, sunDir));
            float3 ty = cross(sunDir, tx);

            float u1 = SsrNoise(float2(noisePixel) + float2(23.0, 7.0), ssrFrameIndex * 1.37);
            float u2 = SsrNoise(float2(noisePixel) + float2(5.0, 41.0), ssrFrameIndex * 2.11);
            float radius = tanHalf * sqrt(saturate(u1));
            float phi = 2.0 * SsrPI * u2;
            sunDir = normalize(sunDir + (tx * cos(phi) + ty * sin(phi)) * radius);
        }

        sunLit = SceneTraceAnyHit(pos + hitN * eps, sunDir, 1e4) ? 0.0 : 1.0;
    }

    float probeValid;
    float3 probeIrr = SsrSampleProbeField(pos, hitN, probeValid);
    float3 ambient = probeValid > 0.5
        ? probeIrr
        : SsrSampleEnvironment(_EnvMap, _EnvMap_sampler, hitN, 1.0) * ssrSunColor.w;

    float3 punctual = float3(0.0, 0.0, 0.0);
    uint punctualOffset = (uint)lightData.ClusterParams.x;
    uint punctualCount = min((uint)lightData.ClusterParams.y, 16u);
    [loop]
    for (uint li = 0; li < punctualCount; li++)
    {
        PunctualLight l = PunctualLights[punctualOffset + li];
        float3 toLight = l.PositionRange.xyz - pos;
        float distSq = dot(toLight, toLight);
        float range = l.PositionRange.w;
        if (distSq > range * range)
        {
            continue;
        }

        float distL = sqrt(max(distSq, 1e-6));
        float3 dirToLight = toLight / distL;
        float pndl = saturate(dot(hitN, dirToLight));
        if (pndl <= 0.0)
        {
            continue;
        }

        float distRatio2 = distSq / (range * range);
        float distFactor = saturate(1.0 - distRatio2 * distRatio2);
        float atten = distFactor * distFactor / (distSq + 1e-2);
        if (l.DirectionType.w > 0.5)
        {
            float cd = dot(-dirToLight, l.DirectionType.xyz);
            float spotFactor = saturate((cd - l.SpotAngles.x) * l.SpotAngles.y);
            atten *= spotFactor * spotFactor;
        }

        if (atten <= 1e-6)
        {
            continue;
        }

        if (SceneTraceAnyHit(pos + hitN * eps, dirToLight, distL))
        {
            continue;
        }

        punctual += l.ColorIntensity.rgb * l.ColorIntensity.w * (pndl * atten);
    }

    return ssrSunColor.rgb * ndl * sunLit + ambient + punctual;
}

// Дешёвая зеркальная замена для МЕТАЛЛИЧЕСКОГО хита, которому не досталось честной цепочки
// отскоков (шероховатый пиксель, лимит ssrBounces, частичная металличность): env-карта по
// отражённому направлению, тонированная альбедо (F0 металла - его base color). Без неё металл
// с погашенным ламбертом рендерился ЧЁРНЫМ (см. кадр RT0 в RenderDoc - чёрные сферы в
// отражениях); шероховатость поднята к 0.35 - это заведомо приближение, резкость ему не идёт.
float3 SsrMetalEnvSpec(SceneHit hit, float3 rayDir, float3 hitN, float roughness)
{
    // Та же маска ЯВНОГО металла, что и в основном блоке: сырая потриугольная металличность
    // шумит и давала пятна по треугольникам (см. комментарий там).
    float metalMask = smoothstep(0.5, 0.9, hit.metalness);
    if (metalMask <= 0.0)
    {
        return float3(0.0, 0.0, 0.0);
    }

    // Размытие env-выборки - по ЗАПЕЧЁННОЙ шероховатости САМОГО хита (не по фиксированным
    // 0.35 и не по шероховатости отражающего пикселя): зеркальный хром отражал небо так же
    // мутно, как матовое железо, и на глянце это читалось как «шероховатость перемножается».
    // Шероховатость смотрящего пикселя лишь ДОБАВЛЯЕТ размытия (свёртка двух лобов).
    float envRough = saturate(max(hit.roughness, roughness * 0.5));
    return SsrHitAlbedo(hit, roughness) * metalMask
        * SsrSampleEnvironment(_EnvMap, _EnvMap_sampler, reflect(rayDir, hitN), envRough);
}

// Радианс из точки RT-хита по направлению луча: репроекция в кадр (полный пиксель со всеми
// термами - см. шапку), иначе аналитический шейдинг двусторонней нормалью (backface = изнанка
// односторонней плоскости, а не «внутри монолита» - см. историю чёрных дыр от панелей).
float3 SsrRtHitRadiance(SceneHit hit, float3 rayDir, float roughness, float2 viewportSize,
    int2 noisePixel)
{
    float2 seenUv;
    if (SsrTryScreenHit(hit.position, hit.smoothNormal, viewportSize, seenUv))
    {
        return SsrSceneColor(seenUv, roughness);
    }

    // Сглаженная нормаль (вершинная интерполяция) - геометрическая на плотных сферах давала
    // фасетки («нет смешивания между вершинами»); диффуз гасится металличностью - у металла
    // энергия в зеркальном отражении, и её место занимает env-замена ниже (честная цепочка
    // отскоков живёт только в основном блоке, сюда приходят её окончания и вторые отскоки).
    float3 hitN = hit.backface ? -hit.smoothNormal : hit.smoothNormal;
    return SsrHitAlbedo(hit, roughness) * (1.0 - smoothstep(0.5, 0.9, hit.metalness))
        * SsrAnalyticHitLight(hit.position, hitN, SsrRayEpsilon(hit.t), noisePixel)
        + SsrMetalEnvSpec(hit, rayDir, hitN, roughness);
}
#endif

struct PSOutput
{
    float4 rayColor : SV_TARGET0;
    float4 rayHit : SV_TARGET1;
};

PSOutput Main(in VSOutput input)
{
    PSOutput output;
    output.rayColor = float4(0.0, 0.0, 0.0, 0.0);
    output.rayHit = float4(0.0, 0.0, 0.0, 0.0);

    float2 viewportSize = viewData.viewport.zw;
    int2 pixel = int2(input.pos.xy);

    // Фон (reversed-Z чистится нулём) не отражает; маска lit-пути - нормаль G-buffer-а
    // (нули у неба/не-PBR режимов, см. очистку в ForwardPass).
    float centerRaw = _DepthTex.Load(int3(pixel, 0));
    if (centerRaw < 1e-6)
    {
        return output;
    }

    float4 gbuffer = _NormalRoughTex.Load(int3(pixel, 0));
    float roughness = gbuffer.a;
    float3 nWorld = gbuffer.xyz;
    if (dot(nWorld, nWorld) < 0.5 || roughness > ssrMaxRoughness)
    {
        return output;
    }

    nWorld = normalize(nWorld);
    float3 P = SsrViewPos(pixel, centerRaw, viewportSize);
    float3 N = SsrWorldDirToView(nWorld);
    float3 V = -normalize(P);
    if (dot(N, V) <= 0.0)
    {
        // Two-sided шейдинг форварда уже флипал нормаль к камере - сюда попадают только
        // артефакты интерполяции на силуэтах.
        return output;
    }

    float confBase = SsrRoughnessFade(roughness);

    // МАРШРУТ ШЕРОХОВАТЫХ: лоб почти косинусный - иррадианса probe-поля в самой точке, без
    // единого луча (см. шапку). Поля нет/точка вне объёма - обычный трейс ниже.
    if (roughness > SsrDiffuseRoughness)
    {
        float3 worldPos = viewData.CameraWorldPos + mul(P, transpose((float3x3)viewData.view));
        float probeValid;
        float3 irr = SsrSampleProbeField(worldPos, nWorld, probeValid);
        if (probeValid > 0.5)
        {
            output.rayColor = float4(irr, confBase);
            // pdf = 1 и зеркальное направление: соседи того же маршрута согласованы, а вес
            // BRDF у широкого лба и так почти изотропный.
            output.rayHit = float4(SsrOctEncode(reflect(-V, N)), 1.0, 0.5);
            return output;
        }
    }

    float3 R;
    float pdf;
    if (roughness < SsrMirrorRoughness)
    {
        // Зеркальный путь - детерминированное направление; pdf считается по H = N (это и есть
        // пик лоба), чтобы вес ratio estimator-а у соседей-зеркал оставался согласованным.
        R = reflect(-V, N);
        float m = max(roughness * roughness, 1e-3);
        float m2 = m * m;
        pdf = m2 / (SsrPI * m2 * m2);
    }
    else
    {
        float u1 = SsrNoise(float2(pixel), ssrFrameIndex);
        float u2 = SsrNoise(float2(pixel) + float2(37.0, 17.0), ssrFrameIndex * 1.618);
        float4 H = SsrSampleGgxHalfVector(N, roughness, u1, u2);
        pdf = H.w;
        R = reflect(-V, H.xyz);
        if (dot(R, N) < 0.02)
        {
            // Сэмпл лоба ушёл под поверхность (крайние углы обзора) - откат на зеркальный луч,
            // выкидывать сэмпл целиком дороже: пиксель мигал бы дырой.
            R = reflect(-V, N);
        }
    }

    // Режим «сразу RT» (ssrTraceMode = 1, только в RT-варианте): экранный марш пропускается
    // целиком, луч уходит в RayQuery, а радианс в точке хита всё равно берётся с экрана
    // репроекцией (см. блок RT ниже). Уходят артефакты марша - ложные хиты за тонкой
    // геометрией, ошибки ssrThickness, затухание у краёв кадра, - и 48 выборок глубины на луч
    // меняются на один обход BVH.
#if FEATURE_RT_REFLECTIONS
    bool rtOnly = ssrTraceMode > 0.5;
#else
    bool rtOnly = false;
#endif

    // Отрезок марша: до дальности либо до плоскости у камеры (за неё экран ничего не знает).
    float maxT = ssrMaxDistance;
    if (P.z + R.z * maxT < SsrNearPlane * 1.5)
    {
        maxT = (SsrNearPlane * 1.5 - P.z) / min(R.z, -1e-5);
    }

    float3 P1 = P + R * maxT;

    // Марш перспективно-корректный: UV и 1/z интерполируются линейно по экрану.
    float2 uv0 = SsrProjectUv(P, viewportSize);
    float2 uv1 = SsrProjectUv(P1, viewportSize);
    float q0 = 1.0 / P.z;
    float q1 = 1.0 / P1.z;

    float jitter = SsrNoise(float2(pixel) + float2(11.0, 53.0), ssrFrameIndex * 2.618);

    float hitS = -1.0;
    float prevS = 0.0;
    [loop]
    for (int i = 0; i < (rtOnly ? 0 : SsrMaxSteps); i++)
    {
        float s = (i + jitter) / SsrMaxSteps;
        float2 uv = lerp(uv0, uv1, s);
        if (any(uv < 0.0) || any(uv > 1.0))
        {
            break;
        }

        float rayZ = 1.0 / lerp(q0, q1, s);
        int2 tap = clamp(int2(uv * viewportSize), int2(0, 0), int2(viewportSize) - 1);
        float tapRaw = _DepthTex.Load(int3(tap, 0));
        if (tapRaw >= 1e-6)
        {
            float sceneZ = SsrViewDepth(tapRaw);

            // Байас от самопересечения - доля глубины точки (суб-пиксельные ступени глубины
            // на скользящих углах дают ложные хиты у самой поверхности).
            if (rayZ > sceneZ + max(0.005 * sceneZ, 1e-3))
            {
                if (rayZ - sceneZ < ssrThickness + 0.02 * sceneZ)
                {
                    hitS = s;
                    break;
                }
                // Луч глубже толщины - прошёл ПОЗАДИ тонкого объекта, марш продолжается.
            }
        }

        prevS = s;
    }

    if (hitS > 0.0)
    {
        // Бинарное уточнение между последним свободным шагом и хитом - край отражения
        // прилипает к геометрии, а не к сетке шагов.
        float lo = prevS;
        float hi = hitS;
        [unroll]
        for (int r = 0; r < SsrRefineSteps; r++)
        {
            float mid = (lo + hi) * 0.5;
            float2 uv = lerp(uv0, uv1, mid);
            float rayZ = 1.0 / lerp(q0, q1, mid);
            int2 tap = clamp(int2(uv * viewportSize), int2(0, 0), int2(viewportSize) - 1);
            float tapRaw = _DepthTex.Load(int3(tap, 0));
            float sceneZ = tapRaw >= 1e-6 ? SsrViewDepth(tapRaw) : 1e9;
            if (rayZ > sceneZ)
            {
                hi = mid;
            }
            else
            {
                lo = mid;
            }
        }

        float2 hitUv = lerp(uv0, uv1, hi);
        int2 hitPixel = clamp(int2(hitUv * viewportSize), int2(0, 0), int2(viewportSize) - 1);

        // Хит по поверхности, обращённой ОТ луча (задник геометрии, сквозь которую луч прошёл
        // марш-байасом), - не отражение, а протечка: отбрасываем.
        float3 hitNWorld = _NormalRoughTex.Load(int3(hitPixel, 0)).xyz;
        float3 rWorld = SsrViewDirToWorld(R);
        if (dot(hitNWorld, hitNWorld) < 0.5 || dot(hitNWorld, rWorld) < 0.1)
        {
            // Билинейная выборка по СУБ-ПИКСЕЛЬНОМУ UV уточнённого хита, а не Load по целому
            // пикселю: квантование до текселя заставляло цвет хита прыгать между соседями на
            // каждый кадр (джиттер марша сдвигает hitS чуть-чуть) - мерцание глянца.
            output.rayColor = float4(SsrSceneColor(hitUv, roughness), confBase * SsrEdgeFade(hitUv));
            output.rayHit = float4(hitUv, pdf, 1.0);
            return output;
        }
    }

#if FEATURE_RT_REFLECTIONS
    // Экран промахнулся - добираем луч по TLAS сцены (см. шапку про репроекцию/аналитику).
    {
        float3 originWorld = viewData.CameraWorldPos + mul(P, transpose((float3x3)viewData.view));
        float3 dirWorld = SsrViewDirToWorld(R);

        // БЕЗ капа ssrMaxDistance: дальность экранного марша ограничивает ЦЕНУ шагов, а RayQuery
        // стоит одинаково при любом tMax. С капом луч в противоположную стену зала «промахивался»
        // и падал в нижнюю полусферу env-карты - тёмную землю неба: RT-отражения дальних стен
        // выглядели чёрными при яркой сцене.
        SceneHit hit = SceneTraceClosest(originWorld + nWorld * SsrRayEpsilon(P.z), dirWorld, 1e4);

        // У ЗЕРКАЛЬНЫХ пикселей rayHit.z несёт ДЛИНУ луча, а не pdf: она нужна резолву для
        // репроекции виртуального образа (RTG гл.32), а pdf зеркала резолв считает сам
        // (SsrMirrorPdf - детерминированное направление, стохастики нет). Промах = «хит» на
        // бесконечности: 1e4 репроецируется практически как направление.
        bool mirrorPath = roughness < SsrMirrorRoughness;

        if (!hit.hit)
        {
            // Луч не встретил геометрию - он ДОКАЗАЛ видимость неба по этому направлению:
            // env-карта сэмплируется с ПОЛНЫМ весом, минуя запечённую окклюзию неба поверхности
            // (композит вычитает env * envOcclusion, а добавляет цвет трейса как есть).
            float3 sky = SsrSampleEnvironment(_EnvMap, _EnvMap_sampler, dirWorld, roughness);
            output.rayColor = float4(sky, confBase);
            output.rayHit = float4(SsrOctEncode(R), mirrorPath ? 1e4 : pdf, 0.5);
            return output;
        }

        // Отладочный вид 5: КАРТА ЦЕПОЧКИ отскоков - почему ручка «RT bounces» что-то делает
        // или не делает. Красный - цепочка пошла (внеэкранный металлический хит на зеркальном
        // пикселе), зелёный - металл БЕЗ цепочки (env-заглушка: не зеркальный пиксель либо
        // bounces=1), синий - обычный диффузный хит. Чёрный - сюда вообще не дошли (экранный
        // хит/промах в небо/шероховатый маршрут).
        if (ssrDebugView > 4.5)
        {
            // Красный - ВЕС цепочки (плавный, см. chainBlend), зелёный - металл без неё,
            // синий - диффузный хит.
            float metalHere = smoothstep(0.5, 0.9, hit.metalness);
            float blendHere = metalHere * (1.0 - smoothstep(0.15, 0.4, hit.roughness))
                * (ssrBounces > 1.5 ? 1.0 : 0.0);
            float3 flag = blendHere > 0.02
                ? float3(blendHere, 0.0, 0.0)
                : (metalHere > 0.5 ? float3(0.0, 1.0, 0.0) : float3(0.0, 0.0, 1.0));

            // ЖЁЛТЫЙ - у хита НЕТ текстуры (не попал в набор/меш без UV): такой хит красится
            // ПОТРИУГОЛЬНЫМ альбедо, то есть плоским цветом на треугольник, и в отражении это
            // и есть мозаика по рёбрам. Отдельный цвет, чтобы отличать её от шейдинга.
            if (hit.textureIndex < 0)
            {
                flag = float3(1.0, 1.0, 0.0);
            }
            output.rayColor = float4(flag, confBase);
            output.rayHit = float4(SsrOctEncode(R), mirrorPath ? hit.t : pdf, 0.5);
            return output;
        }

        // Отладочный вид 4: ЧИСТОЕ альбедо хита вместо шейдинга - диагностика текстур RT-хитов
        // (какой текстурой и по каким UV красится точка, без света и репроекции).
        if (ssrDebugView > 3.5)
        {
            output.rayColor = float4(SsrHitAlbedo(hit, roughness), confBase);
            output.rayHit = float4(SsrOctEncode(R), mirrorPath ? hit.t : pdf, 0.5);
            return output;
        }

        // Гладкий металлический хит - кандидат на ЦЕПОЧКУ отскоков (ниже). Решение принимается
        // ДО репроекции в кадр: снимок сцены делается ПЕРЕД композитом SSR, поэтому у зеркала
        // на экране есть только env-спекуляр форварда, а СВОИХ отражений ещё нет. Брать такой
        // пиксель - значит навсегда остаться на одном отскоке, и ручка «RT bounces» не меняла
        // ничего в кадрах, где отражённое зеркало видно на экране (типовой случай: две сферы
        // друг напротив друга). Диффузным и матовым хитам экранный пиксель по-прежнему лучший
        // источник - там он полный, со всем светом (приём AMD SA2023).
        // Вес цепочки - ПЛАВНЫЙ по свойствам хита, а не бинарный порог: металличность и
        // шероховатость запекаются КОНСТАНТОЙ НА ТРЕУГОЛЬНИК (выборка в центроиде UV), и
        // жёсткое условие «металл > 0.5 И шероховатость < 0.35» щёлкало режимом шейдинга от
        // треугольника к треугольнику - в отражении это читалось мозаикой по рёбрам. Теперь
        // вклад цепочки и env-заглушки СМЕШИВАЕТСЯ этим весом, и переход между соседними
        // треугольниками непрерывен.
        // Маска ЯВНОГО металла. Металличность запечена одним текселем в центроиде UV, а в
        // MR-текстурах реальных ассетов канал шумит: у неметаллической ткани Sponza соседние
        // треугольники получают 0.0 и 0.1-0.2 вперемешку. Брать её как есть нельзя - она
        // множит диффуз хита ((1 - metalness) ниже), и шум читался ПЯТНАМИ ПО ТРЕУГОЛЬНИКАМ.
        // smoothstep отсекает шум у нуля и оставляет металлом только то, что автор пометил
        // металлом (0.9+ у хрома, 1.0 у MetalRoughSpheres).
        float hitMetal = smoothstep(0.5, 0.9, hit.metalness);
        float chainBlend = hitMetal * (1.0 - smoothstep(0.15, 0.4, hit.roughness));
        bool chainTaken = chainBlend > 0.02 && ssrBounces > 1.5;

        // Репроекция хита в кадр - внутри SsrRtHitRadiance; маска экранного хита ниже нужна
        // резолву для честного L из view-позиции.
        float2 seenUv;
        if (!chainTaken && SsrTryScreenHit(hit.position, hit.smoothNormal, viewportSize, seenUv))
        {
            output.rayColor = float4(SsrSceneColor(seenUv, roughness), confBase);
            output.rayHit = float4(seenUv, pdf, 1.0);
            return output;
        }

        // Сглаженная нормаль - и в шейдинге, и в зеркальных продолжениях ниже (фасетки
        // геометрической на плотных мешах = «мозаика» в отражениях цепочки).
        float3 hitN = hit.backface ? -hit.smoothNormal : hit.smoothNormal;
        float hitEps = SsrRayEpsilon(hit.t);

        // Диффуз гасится ЗАПЕЧЁННОЙ металличностью: у металла энергия в зеркальном продолжении
        // (цепочка ниже), и ламберт поверх неё двоил картинку («каша накладывается» при росте
        // числа отскоков). При ssrBounces = 1 хром честно тёмный - продолжений не заказывали.
        float3 lit = SsrHitAlbedo(hit, roughness) * (1.0 - hitMetal)
            * SsrAnalyticHitLight(hit.position, hitN, hitEps, pixel);

        // ЗЕРКАЛЬНЫЕ ПРОДОЛЖЕНИЯ для «зеркала в зеркале» - до ssrBounces отскоков ВСЕГО
        // (ручка «RT bounces»; 1 - только первичный луч).
        //
        // Условие - свойства САМОГО ХИТА: он металлический (запечённая металличность; прежняя
        // эвристика «металл по тёмному альбедо» ЗАПРЕЩЕНА - честно-тёмные диффузные своды
        // получали фантомные наложения) И достаточно гладкий, чтобы его отражение было
        // осмысленным. Шероховатость СМОТРЯЩЕГО пикселя здесь не при чём: раньше гейт требовал
        // roughness < 0.08 у зеркала, и на чуть глянцевых сферах ручка «RT bounces» не делала
        // РОВНО НИЧЕГО (диагностика: вид «RT bounce chain» был сплошь зелёный - металл без
        // цепочки). Матовый металл честно остаётся на env-заглушке: его лоб размывает всё
        // равно, а цена трассировки та же. (hitMetal/chainTaken посчитаны ВЫШЕ - решение
        // принимается до репроекции в кадр, см. комментарий там.)

        // Зеркальный терм металла собирается ОТДЕЛЬНО и подмешивается в конце: env-заглушка и
        // цепочка отскоков смешиваются по chainBlend (см. выше про мозаику по треугольникам).
        float3 metalEnv = SsrMetalEnvSpec(hit, dirWorld, hitN, roughness);
        float3 chainSpec = float3(0.0, 0.0, 0.0);

        if (chainTaken)
        {
            // Тинт хопа: F0 металла - его base color (серебро белит, золото желтит).
            float3 bounceDir = reflect(dirWorld, hitN);
            float3 bounceOrigin = hit.position + hitN * hitEps;
            float3 bounceTint = hitMetal * SsrHitAlbedo(hit, roughness);
            int bounceCap = (int)clamp(ssrBounces, 1.0, 4.0);

            [loop]
            for (int bounce = 1; bounce < bounceCap; bounce++)
            {
                SceneHit hitB = SceneTraceClosest(bounceOrigin, bounceDir, 1e4);
                if (!hitB.hit)
                {
                    // Промах доказал видимость неба - обрыв цепочки env-картой.
                    chainSpec += SsrSampleEnvironment(_EnvMap, _EnvMap_sampler, bounceDir, roughness)
                        * bounceTint;
                    break;
                }

                float3 hitBn = hitB.backface ? -hitB.smoothNormal : hitB.smoothNormal;

                // Продолжать ли зеркально - тем же ПЛАВНЫМ весом, что и на первом хите;
                // остаток веса забирает радианс хита (репроекция/аналитика), поэтому переход
                // между соседними треугольниками с чуть разными метал/шероховатостью
                // непрерывен, а энергия сохраняется.
                float metalB = smoothstep(0.5, 0.9, hitB.metalness);
                float blendB = metalB * (1.0 - smoothstep(0.15, 0.4, hitB.roughness));
                bool lastHop = bounce + 1 >= bounceCap;
                chainSpec += SsrRtHitRadiance(hitB, bounceDir, roughness, viewportSize, pixel)
                    * bounceTint * (lastHop ? 1.0 : 1.0 - blendB);
                if (lastHop || blendB <= 0.02)
                {
                    break;
                }

                // Хит снова металлический и лимит позволяет - продолжаем зеркально.
                bounceOrigin = hitB.position + hitBn * SsrRayEpsilon(hitB.t);
                bounceDir = reflect(bounceDir, hitBn);
                bounceTint *= metalB * SsrHitAlbedo(hitB, roughness);
            }
        }

        // Смешение env-заглушки и цепочки: chainBlend = 0 - только заглушка, 1 - только цепочка.
        lit += lerp(metalEnv, chainSpec, chainTaken ? chainBlend : 0.0);

        output.rayColor = float4(lit, confBase);
        output.rayHit = float4(SsrOctEncode(R), mirrorPath ? hit.t : pdf, 0.5);
        return output;
    }
#endif

    return output;
}
