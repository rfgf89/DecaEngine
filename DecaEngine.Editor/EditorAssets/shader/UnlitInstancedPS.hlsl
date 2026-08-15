// SHADER KEYWORDS (см. IGraphicsApi.CreateShader с keywords / ModelLoader.BuildMaterialKeywords):
// вариант компилируется под конкретный материал, выключенный эффект не существует в коде вовсе -
// ни ветвлений, ни сэмплов, ни привязок. Пер-материальные (статичны с загрузки):
//   HAS_BASECOLOR_TEXTURE  - у материала есть base color текстура (_MainTex в Lighting-режиме)
//   HAS_MR_TEXTURE         - есть metallic-roughness текстура
//   MATERIAL_ALPHA_CLIP    - alphaMode MASK/BLEND (clip по PbrAlphaCutoff)
//   MATERIAL_TRANSMISSION  - KHR_materials_transmission (рефракция/просвет)
//   MATERIAL_DISPERSION    - KHR_materials_dispersion (пер-канальная рефракция)
//   MATERIAL_SHEEN         - KHR_materials_sheen (велюровый Charlie-лоб, см. PbrSheenColorRoughness)
// Фичи превью (по ModelLoadOptions; live-тумблеры настроек остаются битами PbrFeatureFlags
// ВНУТРИ скомпилированной фичи - выключенный кейвордом код недостижим и для бита):
//   FEATURE_NORMAL_MAPS / FEATURE_OCCLUSION / FEATURE_SHADOWS
// Неопределённый кейворд в #if == 0 (стандарт препроцессора).
#include "Instancing.hlsl"

Texture2D    _MainTex;
SamplerState _MainTex_sampler;

#if HAS_MR_TEXTURE
// glTF metallic-roughness texture (G = roughness, B = metallic).
Texture2D    _MetallicRoughnessTex;
SamplerState _MetallicRoughnessTex_sampler;
#endif

#if MATERIAL_TRANSMISSION
// Snapshot of the color target taken AFTER the opaque draw and BEFORE the transmissive one (see
// ForwardPass's refraction pass) - what actually sits behind the glass being shaded. Alpha carries
// coverage: 0 where only the cleared background is visible (the preview clears with alpha 0), so
// the shader can fall back to the analytic backdrop gradient there.
Texture2D    _SceneColor;
SamplerState _SceneColor_sampler;

// KHR_materials_volume thickness texture (G channel per spec) - a multiplier over the precomputed
// Beer-Lambert exponent in PbrVolumeAttenuation.w. Materials without one get a white 1x1 fallback
// (see ModelLoader), so no "has texture" flag is needed: G=1 leaves the factor untouched.
Texture2D    _ThicknessTex;
SamplerState _ThicknessTex_sampler;
#endif

#if FEATURE_NORMAL_MAPS
// Tangent-space normal map (linear, OpenGL green-up convention per glTF). Materials without one
// get a flat-normal 1x1 fallback (128,128,255 -> (0,0,1), see ModelLoader) - no "has" flag needed.
Texture2D    _NormalTex;
SamplerState _NormalTex_sampler;
#endif

#if FEATURE_OCCLUSION
// Baked ambient occlusion (R channel per glTF, often the shared ORM texture). White 1x1 fallback
// (R=1 = unoccluded) for materials without one. Applied to ambient/env terms only - direct light
// is not occluded per the spec.
Texture2D    _OcclusionTex;
SamplerState _OcclusionTex_sampler;
#endif

// Procedural equirect environment with roughness-prefiltered mips (see PreviewEnvironmentMap):
// mip N holds the sky analytically re-rendered at the blur a roughness of N/EnvMipMax would
// produce, so a single SampleLevel stands in for a real prefiltered-IBL convolution.
Texture2D    _EnvMap;
SamplerState _EnvMap_sampler;

// Probe-GI (DDGI-лайт, см. DecaEngine.Editor.ProbeGiBaker): атласы SH L1 irradiance-проб,
// запечённых CPU-трассировкой. Сетка РАЗРЕЖЕННАЯ - пробы существуют только в кирпичах рядом с
// геометрией, а атлас хранит пул таких кирпичей (кирпич - блок 4 в ширину и 4*4 в высоту,
// z-слайсы столбиком). Тексель пробы ищется через _ProbeIndirection, см. ProbeTexel/SampleProbeGi.
// Sh0: rgb = L0, a = sky visibility; Sh1: rgb = L1x, a = валидность пробы (0 = в стене);
// Sh2/Sh3: rgb = L1y/L1z. Читаются через Load - сэмплер не нужен, трилинейность ручная
// (SampleProbeGi). Привязаны только в превью с пробами (ProbeGridOrigin.w = 0 иначе - как _EnvMap,
// объявлены безусловно, но мёртвая ветка их не трогает).
Texture2D _ProbeSh0;
Texture2D _ProbeSh1;
Texture2D _ProbeSh2;
Texture2D _ProbeSh3;
// DDGI visibility: окто-атлас 8x8 текселей на пробу (r = средняя дистанция до геометрии по
// направлению, g = средний квадрат) - тест Чебышёва в SampleProbeGi отбрасывает пробы,
// заслонённые стеной от точки сэмпла. Та же раскладка пула, умноженная на 8 по обеим осям.
Texture2D _ProbeVis;
// Релокация: rgb = смещение пробы от её узла сетки в мировых единицах (см.
// ProbeGiBakeResult.Offset). Проба, стоявшая внутри стены или колонны, отодвинута наружу, и знать
// об этом обязаны ОБА потребителя: и трилинейный вес, и тест Чебышёва меряют расстояние до пробы.
// В запечке атлас нулевой - релокация работает только в реальном времени.
Texture2D _ProbeOffset;
// Карта индирекции RGBA8 размером с сетку КИРПИЧЕЙ, тексель (bx, bz*BrickCountY+by): r/g -
// младший/старший байт индекса кирпича в пуле, b > 0 - кирпич существует. Читается один раз на
// сэмпл: все восемь углов трилинейной ячейки по построению лежат в одном кирпиче.
Texture2D _ProbeIndirection;

// КАСКАДЫ (см. SampleProbeGi): те же комплекты атласов для одного-двух дополнительных, более
// МЕЛКИХ объёмов вокруг точки интереса - _C1 вдвое плотнее базового, _C2 вчетверо. Выборка идёт от
// мелкого к крупному, базовый объём остаётся гарантией покрытия всей сцены. Активность каскада -
// ProbeGridOrigin1/2.w (0 = не создан, слоты держат плейсхолдер, мёртвая ветка их не читает).
Texture2D _ProbeSh0_C1;
Texture2D _ProbeSh1_C1;
Texture2D _ProbeSh2_C1;
Texture2D _ProbeSh3_C1;
Texture2D _ProbeVis_C1;
Texture2D _ProbeOffset_C1;
Texture2D _ProbeIndirection_C1;
Texture2D _ProbeSh0_C2;
Texture2D _ProbeSh1_C2;
Texture2D _ProbeSh2_C2;
Texture2D _ProbeSh3_C2;
Texture2D _ProbeVis_C2;
Texture2D _ProbeOffset_C2;
Texture2D _ProbeIndirection_C2;

// Contains data about the camera/view (e.g., camera position).
cbuffer View
{
    ViewData viewData;
}

// Мировой направленный свет превью (см. SimpleCullingAndRenderSystem.BuildLightData):
// LightDirection нулевой = теневой пасс выключен, ключевой свет остаётся камерным.
cbuffer Light
{
    LightData lightData;
}

// Результаты кластеризации punctual-светов (LightClusterCS.hlsl, привязка -
// DiligentBatchRenderer.Register). Объявлены безусловно: при ClusterParams.y == 0 (превью,
// камеры без punctual-светов) кластерная ветка мёртвая и буферы не читаются.
StructuredBuffer<PunctualLight> PunctualLights;
StructuredBuffer<uint> ClusterCounts;
StructuredBuffer<uint> ClusterIndices;

// Тени punctual-светов: texture array слайсов (спот - один, точечный - шесть граней куба) и
// viewProj-матрицы слайсов (см. PunctualShadowScheduler). Обычный Z (запись Less, clear 1.0),
// сравнение LessEqual - та же конвенция, что у каскадов солнца. Объявлены безусловно: при
// ShadowParams.x < 0 ветка мёртвая, а лейаут текстуры держит валидным ForwardPass.
StructuredBuffer<float4x4> PunctualShadowMatrices;
Texture2DArray PunctualShadowMaps;
SamplerComparisonState PunctualShadowMaps_sampler;

#if FEATURE_SHADOWS
// Shadow map мирового света (каскад 0; привязывается DiligentBatchRenderer.Register ->
// ShadowRenderer.SetShadowResources). Обычный Z (clear 1.0 + Less при записи), сравнение
// LessEqual: SampleCmp возвращает 1 = освещено.
Texture2DArray ShadowMaps;
SamplerComparisonState ShadowMaps_sampler;
#endif

// Model Preview view-mode controls (see DecaEngine.Editor.ModelPreviewViewport /
// ModelIconBaker). PreviewMode: 0 = Textured (_MainTex), 1 = Highlight (flat, camera-rim
// shaded), 2 = Channel debug view, 3 = Lighting (PBR metallic-roughness). PreviewChannel
// (used only when PreviewMode == 2): 0 = Normal, 1 = UV, 2 = Tangent. Pbr* (used only when
// PreviewMode == 3) are the material's glTF metallic-roughness factors, pushed per material
// (see ModelLoader.MaterialPbr / ModelPreviewViewport.ApplyPreviewSettingsToMaterials);
// PbrHasBaseColorTexture tells whether _MainTex is actually bound - an unbound texture can't
// be detected from HLSL, and sampling it is undefined. PbrAlphaCutoff > 0 enables alpha
// clipping in Lighting mode (glTF alphaMode MASK/BLEND, see ModelLoader.MaterialPbr). Left at
// zero defaults, this cbuffer is a no-op outside the Model Preview feature - regular scene
// materials never update it.
cbuffer PreviewSettings
{
    int PreviewMode;
    int PreviewChannel;
    float PbrMetallic;
    float PbrRoughness;
    float4 PbrBaseColor;
    int PbrHasBaseColorTexture;
    float PbrAlphaCutoff;
    int PbrHasMetallicRoughnessTexture;
    float PbrTransmission;
    float PbrDispersion;
    float PbrIor;
    // Glass thickness in WORLD units (thicknessFactor x node scale) - the geometric length of the
    // refracted ray inside the volume, used for the refraction offset. 0 = no volume data.
    float PbrThicknessWorld;
    // Global feature toggles (see ModelViewportEnvironment.PreviewFeatureFlags): bit 1 = normal
    // maps, bit 2 = ambient occlusion. Every feature must degrade cleanly when its bit is off.
    int PbrFeatureFlags;
    // KHR_materials_volume, precomputed on CPU: rgb = attenuationColor, w = thicknessFactor /
    // attenuationDistance (Beer-Lambert exponent, 0 = no volume attenuation).
    float4 PbrVolumeAttenuation;
    float PbrNormalScale;
    float PbrOcclusionStrength;
    // KHR_texture_transform (см. ModelLoader.MaterialPbrFactors.UvTransform): предвычисленная
    // 2x2-матрица (row-major: u' = dot(uv, xy), v' = dot(uv, zw)) + offset, применяется к
    // UV0-сэмплам материала ТОЛЬКО при PbrUvHasTransform != 0 - нулевой cbuffer (сцены вне
    // превью его не заполняют) остаётся тождественным преобразованием.
    float2 PbrUvOffset;
    float4 PbrUvTransform;
    int PbrUvHasTransform;
    // Индекс UV-канала occlusionTexture (glTF texCoord 0/1) - AO часто запечён под уникальную
    // развёртку ВТОРОГО канала (TEXCOORD_1, см. ChairDamaskPurplegold).
    int PbrOcclusionUvSet;
    // Пользовательский поворот энвайронмента вокруг Y в радианах (ползунок света в превью, см.
    // PreviewShadowSettings.EnvYawRadians) - сдвиг equirect-U в SampleEnvironment, чтобы
    // отражения/ambient вращались синхронно с ключевым светом. 0 (zero-init) = без поворота.
    float PbrEnvYaw;
    float PbrPad0;
    // KHR_materials_sheen: rgb = sheenColorFactor (линейный; ноль = выключено), w =
    // sheenRoughnessFactor. Читается только под MATERIAL_SHEEN.
    float4 PbrSheenColorRoughness;
    // KHR_materials_specular: rgb = specularColorFactor (может быть >1 - по спеке умножается на
    // F0 от IOR и КЛАМПИТСЯ к 1 после умножения), w = specularFactor (вес диэлектрического
    // спекуляра). Каждый пуш Lighting-режима обязан слать (1,1,1,1) для материалов без
    // расширения - нулевой w глушит спекуляр в чёрный (см. PreviewSettingsData).
    float4 PbrSpecularColorFactor;

    // Probe-GI сетка (см. ProbeGiTextures): xyz = мировая позиция пробы (0,0,0), w = 1 - пробы
    // запечены и привязаны (0 - zero-init, фича выключена, атласы могут быть не привязаны).
    float4 ProbeGridOrigin;
    // xyz = шаг сетки в мировых единицах, w = normal-бейас точки сэмпла (доля ячейки) - от
    // утечек света сквозь тонкие стены (аналог DDGI normal bias).
    float4 ProbeGridCell;
    // xyz = число проб по осям ВИРТУАЛЬНОЙ сетки (float для простоты cbuffer-паковки) - в ней
    // ищется ячейка по мировой позиции; реально выделены только пробы живых кирпичей.
    float4 ProbeGridCounts;
    // xyz = размер сетки кирпичей, w = сколько кирпичей в ряду пула (см. ProbeGiTextures.GridBricks).
    float4 ProbeGridBricks;
    // Ручки probe-GI из окна Graphics (см. GraphicsSettingsWindow / EditorSettings): x = флор
    // глушения солнечной доли эмбиента тенью ключа (дефолт 0.3), y = флор глушения env-спекуляра
    // видимостью неба (0.2), z = интенсивность солнца (0 = zero-init, берём дефолт 2.0),
    // w = множитель probe-irradiance (0 = дефолт 1.0).
    float4 ProbeGiParams;
    // x = флор глушения НЕБЕСНОЙ доли эмбиента тенью ключа (1 = небо в тени не гасится - дефолт,
    // редактор шлёт явно; 0-init вне превью тоже трактуется как 1). yzw - резерв.
    float4 ProbeGiParams2;

    // Сетки каскадов 1 и 2 (мелких) - та же семантика, что у базовых ProbeGrid*; Origin.w = 1
    // означает «каскад создан и атласы _C1/_C2 привязаны» (см. SampleProbeGi).
    float4 ProbeGridOrigin1;
    float4 ProbeGridCell1;
    float4 ProbeGridCounts1;
    float4 ProbeGridBricks1;
    float4 ProbeGridOrigin2;
    float4 ProbeGridCell2;
    float4 ProbeGridCounts2;
    float4 ProbeGridBricks2;

    // Режим кривой тонмапа (см. Tonemap.hlsl). Действует только в LDR-режиме: в HDR-конвейере
    // кривую применяет TonemapPS в самом конце, а здесь кадр остаётся линейным.
    int PbrToneCurve;
}

// KHR_texture_transform поверх UV0 (см. PbrUvTransform выше).
float2 TransformMaterialUv(float2 uv)
{
    if (PbrUvHasTransform != 0)
    {
        uv = float2(dot(uv, PbrUvTransform.xy), dot(uv, PbrUvTransform.zw)) + PbrUvOffset;
    }
    return uv;
}

static const int FeatureNormalMaps = 1;
static const int FeatureOcclusion = 2;
static const int FeatureShadows = 4;

// HDR-конвейер превью (см. GraphicsPipelineSimple с eyeAdaptation): цветовой таргет - RGBA16F,
// шейдинг пишет в него ЛИНЕЙНЫЙ радианс без тонмапа и sRGB-энкода, а экспозиция + кривая
// применяются один раз в TonemapPass после замера яркости кадра. Бит, а не кейворд: HDR - опция
// окружения, а материалы переживают его пересоздание только через перезагрузку модели, и лишний
// вариант шейдера на каждый материал того не стоит.
static const int FeatureHdrOutput = 8;

#if FEATURE_SHADOWS
// PCF 3x3 по shadow map мирового света с выбором каскада: каскады - концентрические ортобоксы
// (мелкие вокруг точки интереса камеры, последний накрывает всю сцену, см.
// SimpleCullingAndRenderSystem.BuildLightData), берётся ПЕРВЫЙ, чей объём содержит точку -
// он самый детальный. Валидность каскада - ненулевая ширина в CascadeSizes (превью моделей
// по-прежнему заполняет один каскад, и цикл вырождается в прежний код). За пределами всех
// каскадов - освещено.
float SampleWorldLightShadow(float3 worldPos, float3 N)
{
    [unroll]
    for (int c = 0; c < 4; c++)
    {
        float cascadeWorld = lightData.CascadeSizes[c];
        if (cascadeWorld <= 0.0)
        {
            continue;
        }

        // Normal-offset bias: точка сэмплирования сдвигается вдоль нормали на ~полтора текселя
        // shadow map В МИРОВЫХ единицах (CascadeSizes[c] = ширина орто-каскада ЭТОГО уровня).
        // Депф-bias один не спасает тонкую геометрию (черепица, ткань): её задняя грань лежит в
        // сантиметрах за передней, и PCF-соседи на рельефе ловят чужие задние грани - крыши
        // затеняют сами себя. Сдвиг по нормали уводит точку из этой зоны независимо от глубины.
        float texelWorld = cascadeWorld / 4096.0;
        float3 samplePos = worldPos + N * texelWorld * 1.5;

        float4 lightClip = mul(float4(samplePos, 1.0), lightData.CascadeMatrix[c]);
        float3 lightNdc = lightClip.xyz / max(lightClip.w, 1e-6);
        float2 shadowUv = float2(lightNdc.x * 0.5 + 0.5, 0.5 - lightNdc.y * 0.5);

        if (any(shadowUv < 0.0) || any(shadowUv > 1.0) || lightNdc.z <= 0.0 || lightNdc.z >= 1.0)
        {
            // Точка вне этого каскада - пробуем следующий, крупнее.
            continue;
        }

        // Небольшое смещение: shadow map пишется БЕЗ отсечения граней (см. ShadowRenderer.GetBaseState -
        // прежний front-cull делал одностороннюю геометрию прозрачной для света: планки крыши без
        // задних граней пропускали солнце полосами). Основную работу против acne делают растеризаторные
        // байасы записи (DepthBias + SlopeScaledDepthBias) и normal-offset выше; константа здесь -
        // добивка, крупнее нельзя (peter-panning у основания фигур). Глубинный диапазон у всех
        // каскадов одинаковый (см. BuildLightData), так что константа работает на любом уровне.
        float referenceDepth = lightNdc.z - 0.0004;

        const float texel = 1.0 / 4096.0;
        float sum = 0.0;
        [unroll]
        for (int y = -1; y <= 1; y++)
        {
            [unroll]
            for (int x = -1; x <= 1; x++)
            {
                sum += ShadowMaps.SampleCmpLevelZero(ShadowMaps_sampler,
                    float3(shadowUv + float2(x, y) * texel, (float)c), referenceDepth);
            }
        }

        return sum / 9.0;
    }

    return 1.0;
}
#endif

static const float PI = 3.14159265359;

// Must equal PreviewEnvironmentMap.MipCount - 1.
static const float EnvMipMax = 6.0;

float3 SampleEnvironment(float3 dir, float roughness)
{
    // Поворот энвайронмента вокруг Y - для equirect-карты это просто сдвиг U (сэмплер Wrap
    // заворачивает шов). Знак: +PbrEnvYaw двигает солнце карты в сторону возрастающего ява
    // ключевого света (см. PreviewShadowSettings.SetAngles).
    float2 uv = float2(atan2(dir.z, dir.x) / (2.0 * PI) + 0.5 + PbrEnvYaw / (2.0 * PI),
                       acos(clamp(dir.y, -1.0, 1.0)) / PI);
    return _EnvMap.SampleLevel(_EnvMap_sampler, uv, roughness * EnvMipMax).rgb;
}

// Ручной трилинейный сэмпл probe-GI сетки: 8 угловых проб ячейки, вес = трилинейный ×
// валидность (пробы внутри геометрии не интерполируются - от утечек тьмы/света). SH L1 →
// диффузная irradiance по нормали (константы Ramamoorthi: A0*Y00 = 0.886, A1*Y1 = 1.023).
// Возвращает E/PI - готовый ламбертов множитель альбедо; skyVisibility - доля неба, видимая
// точкой (глушит env-спекуляр в закрытых местах). Отрицательный x = валидных проб рядом нет,
// вызывающий откатывается на константный ambient.
// Геометрия кирпича разреженной сетки - обязана совпадать с ProbeGiBaker.BrickProbes/BrickCells.
#define PROBE_BRICK_PROBES 4
#define PROBE_BRICK_CELLS  3

// Сторона окто-карты видимости на пробу. Приходит кбуфером (ProbeGiParams2.y - ручка «Visibility
// res», см. ProbeGiBakeResult.VisRes): раскладка атласа задаётся ей же на CPU, и разойтись им
// нельзя. 0 (zero-init кбуфера вне превью) трактуется как дефолтные 8.
int ProbeVisRes()
{
    int res = (int)ProbeGiParams2.y;
    return res > 0 ? res : 8;
}

// Окто-топология при выходе за край тайла: октаэдр развёрнут в квадрат, поэтому сосед за кромкой -
// это тексель ЭТОГО ЖЕ тайла с противоположной стороны и зеркальной второй координатой (переход
// через ребро октаэдра). Нужно для билинейной фильтрации карты глубин: без обёртки края тайла
// пришлось бы либо клампить (ложная "стена" по краю окто-развёртки), либо держать в атласе
// border-тексели, то есть переразмечать его и переписывать обе записи (CPU и ProbeRoundCS).
int2 OctWrapTexel(int2 t, int res)
{
    if (t.x < 0)         { t.x = 0;         t.y = res - 1 - t.y; }
    else if (t.x >= res) { t.x = res - 1;   t.y = res - 1 - t.y; }

    if (t.y < 0)         { t.y = 0;         t.x = res - 1 - t.x; }
    else if (t.y >= res) { t.y = res - 1;   t.x = res - 1 - t.x; }

    return t;
}

// Окто-кодирование направления в [0,1]² - обязано бит-в-бит совпадать с ProbeGiBaker.OctEncode
// (CPU пишет атлас видимости в этой же раскладке).
float2 OctEncode(float3 d)
{
    float sum = abs(d.x) + abs(d.y) + abs(d.z);
    float2 p = d.xy / sum;
    if (d.z < 0.0)
    {
        p = (1.0 - abs(p.yx)) * float2(p.x >= 0.0 ? 1.0 : -1.0, p.y >= 0.0 ? 1.0 : -1.0);
    }

    return p * 0.5 + 0.5;
}

// sunFraction - доля солнечного света (баунс + переотскоки) в поле, альфа Sh2: экранная тень
// ключа глушит только её (см. probeShadow в Main) - небесная часть эмбиента в тени легитимна.
// Нелинейная реконструкция облучённости из L1 (Geomerics/Enlighten, см. Graham Hazel, "Spherical
// Harmonics for Lighting"). Линейная форма I(n) = R0 + 2*R1.n имеет врождённый дефект: длина R1
// доходит до R0, поэтому множитель 2 позволяет второму слагаемому превысить первое, и с обратной
// стороны от яркого направления облучённость становится ОТРИЦАТЕЛЬНОЙ. Физически этого не бывает,
// а на картинке даёт чёрные пятна напротив ярких проёмов (или потерю энергии, если просто
// клампить в ноль).
//
// Здесь R0 и R1 - в «нормированном» соглашении Хейзела (R0 = среднее по сфере), на канал: у
// каждого канала своя направленность.
//
// ВАЖНО: это не строгое улучшение, а размен, замеренный численно против точного интегрирования.
// При сильной направленности (r = |R1|/R0 около 0.85-0.99) линейная форма уходит в минус на всю
// величину R0, и нелинейная точнее вчетверо. Зато на полусферическом источнике (r = 0.5) линейная
// ТОЧНА, а нелинейная замыливает и не умеет темнеть с изнанки. Поэтому смешиваем по r, и порог не
// подобран на глаз: при r <= 0.5 линейная форма неотрицательна по построению (2r <= 1), то есть
// ломаться ей просто негде.
float NonLinearIrradianceL1(float R0, float3 R1v, float3 n)
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

// Выборка одного объёма (каскада) живёт в ProbeGiSampleBody.hlsl и разворачивается по разу на
// каскад: HLSL до SM 6.6 не передаёт текстуры параметрами, поэтому один код с разными атласами -
// это инклюд с макросами, а не функция.
#define PROBE_GI_FN      SampleProbeGiC0
#define PROBE_GI_SH0     _ProbeSh0
#define PROBE_GI_SH1     _ProbeSh1
#define PROBE_GI_SH2     _ProbeSh2
#define PROBE_GI_SH3     _ProbeSh3
#define PROBE_GI_VIS     _ProbeVis
#define PROBE_GI_OFFSET  _ProbeOffset
#define PROBE_GI_IND     _ProbeIndirection
#define PROBE_GI_ORIGIN  ProbeGridOrigin
#define PROBE_GI_CELL    ProbeGridCell
#define PROBE_GI_COUNTS  ProbeGridCounts
#define PROBE_GI_BRICKS  ProbeGridBricks
#include "ProbeGiSampleBody.hlsl"
#undef PROBE_GI_FN
#undef PROBE_GI_SH0
#undef PROBE_GI_SH1
#undef PROBE_GI_SH2
#undef PROBE_GI_SH3
#undef PROBE_GI_VIS
#undef PROBE_GI_OFFSET
#undef PROBE_GI_IND
#undef PROBE_GI_ORIGIN
#undef PROBE_GI_CELL
#undef PROBE_GI_COUNTS
#undef PROBE_GI_BRICKS

#define PROBE_GI_FN      SampleProbeGiC1
#define PROBE_GI_SH0     _ProbeSh0_C1
#define PROBE_GI_SH1     _ProbeSh1_C1
#define PROBE_GI_SH2     _ProbeSh2_C1
#define PROBE_GI_SH3     _ProbeSh3_C1
#define PROBE_GI_VIS     _ProbeVis_C1
#define PROBE_GI_OFFSET  _ProbeOffset_C1
#define PROBE_GI_IND     _ProbeIndirection_C1
#define PROBE_GI_ORIGIN  ProbeGridOrigin1
#define PROBE_GI_CELL    ProbeGridCell1
#define PROBE_GI_COUNTS  ProbeGridCounts1
#define PROBE_GI_BRICKS  ProbeGridBricks1
#include "ProbeGiSampleBody.hlsl"
#undef PROBE_GI_FN
#undef PROBE_GI_SH0
#undef PROBE_GI_SH1
#undef PROBE_GI_SH2
#undef PROBE_GI_SH3
#undef PROBE_GI_VIS
#undef PROBE_GI_OFFSET
#undef PROBE_GI_IND
#undef PROBE_GI_ORIGIN
#undef PROBE_GI_CELL
#undef PROBE_GI_COUNTS
#undef PROBE_GI_BRICKS

#define PROBE_GI_FN      SampleProbeGiC2
#define PROBE_GI_SH0     _ProbeSh0_C2
#define PROBE_GI_SH1     _ProbeSh1_C2
#define PROBE_GI_SH2     _ProbeSh2_C2
#define PROBE_GI_SH3     _ProbeSh3_C2
#define PROBE_GI_VIS     _ProbeVis_C2
#define PROBE_GI_OFFSET  _ProbeOffset_C2
#define PROBE_GI_IND     _ProbeIndirection_C2
#define PROBE_GI_ORIGIN  ProbeGridOrigin2
#define PROBE_GI_CELL    ProbeGridCell2
#define PROBE_GI_COUNTS  ProbeGridCounts2
#define PROBE_GI_BRICKS  ProbeGridBricks2
#include "ProbeGiSampleBody.hlsl"
#undef PROBE_GI_FN
#undef PROBE_GI_SH0
#undef PROBE_GI_SH1
#undef PROBE_GI_SH2
#undef PROBE_GI_SH3
#undef PROBE_GI_VIS
#undef PROBE_GI_OFFSET
#undef PROBE_GI_IND
#undef PROBE_GI_ORIGIN
#undef PROBE_GI_CELL
#undef PROBE_GI_COUNTS
#undef PROBE_GI_BRICKS

// Плавный вес объёма: ноль у грани бокса (и снаружи), единица в двух шагах сетки внутрь. Лечит
// сразу две видимые беды стыков (см. скриншоты с «переходами»):
//   1) тело выборки КЛАМПИТ точку к боксу - без веса пиксель ВНЕ мелкого каскада не проваливался
//      на крупный, а получал растянутые крайние кирпичи мелкого;
//   2) даже честная граница переключала разрешение поля жёстко, швом.
// Отступ 0.5 шага держит выборку подальше от кламп-зоны самой грани.
float ProbeCascadeWeight(float3 worldPos, float4 origin, float4 cell, float4 counts)
{
    float3 f = (worldPos - origin.xyz) / cell.xyz;
    float3 hi = counts.xyz - 1.0;
    float d = min(min(f.x, hi.x - f.x), min(min(f.y, hi.y - f.y), min(f.z, hi.z - f.z)));
    return saturate((d - 0.5) / 1.5);
}

// Каскадная выборка: база - гарантия покрытия, мелкие объёмы подмешиваются ПОВЕРХ с весом своего
// бокса. Плата за плавный стык - в зоне каскада выборок больше одной (до трёх в самом мелком);
// это ровно те места, ради которых каскады и заведены, так что цена по адресу.
float3 SampleProbeGi(float3 worldPos, float3 N, out float skyVisibility, out float sunFraction,
                     out float3 probeMarker)
{
    float3 result = SampleProbeGiC0(worldPos, N, skyVisibility, sunFraction, probeMarker);

    if (ProbeGridOrigin1.w > 0.5)
    {
        float w = ProbeCascadeWeight(worldPos, ProbeGridOrigin1, ProbeGridCell1, ProbeGridCounts1);
        if (w > 0.0)
        {
            float sky1, sun1;
            float3 marker1;
            float3 mid = SampleProbeGiC1(worldPos, N, sky1, sun1, marker1);
            if (mid.x >= 0.0)
            {
                // Базе нечем крыть (дыра без кирпича) - мелкий объём целиком, без фейда в мусор.
                if (result.x < 0.0)
                {
                    w = 1.0;
                }

                result = lerp(max(result, 0.0), mid, w);
                skyVisibility = lerp(skyVisibility, sky1, w);
                sunFraction = lerp(sunFraction, sun1, w);
                if (w > 0.5)
                {
                    probeMarker = marker1;
                }
            }
        }
    }

    if (ProbeGridOrigin2.w > 0.5)
    {
        float w = ProbeCascadeWeight(worldPos, ProbeGridOrigin2, ProbeGridCell2, ProbeGridCounts2);
        if (w > 0.0)
        {
            float sky2, sun2;
            float3 marker2;
            float3 fine = SampleProbeGiC2(worldPos, N, sky2, sun2, marker2);
            if (fine.x >= 0.0)
            {
                if (result.x < 0.0)
                {
                    w = 1.0;
                }

                result = lerp(max(result, 0.0), fine, w);
                skyVisibility = lerp(skyVisibility, sky2, w);
                sunFraction = lerp(sunFraction, sun2, w);
                if (w > 0.5)
                {
                    probeMarker = marker2;
                }
            }
        }
    }

    return result;
}

// Кривая тонмапа переехала в общий Tonemap.hlsl - её же применяет TonemapPS.hlsl в HDR-режиме
// (см. FeatureHdrOutput ниже).
#include "Tonemap.hlsl"

// Direct-lighting contribution of one light for the Lighting preview mode: Cook-Torrance GGX
// specular (D - GGX, G - Smith-Schlick with the direct-lighting k remap, F - Schlick) plus
// energy-conserving Lambert diffuse. dielectricF0 - базовое отражение диэлектрика, выведенное
// из IOR и перекрашенное KHR_materials_specular (см. вызов в Main); specularWeight - его же
// specularFactor, вес диэлектрического зеркального лоба (металлы не трогает, по спеке).
float3 ShadePbrLight(float3 N, float3 V, float3 L, float3 lightColor,
                     float3 albedo, float metallic, float roughness, float transmission,
                     float3 dielectricF0, float specularWeight)
{
    float3 H = normalize(V + L);

    float NdotL = saturate(dot(N, L));
    float NdotV = saturate(dot(N, V)) + 1e-4;
    float NdotH = saturate(dot(N, H));
    float VdotH = saturate(dot(V, H));

    float a = roughness * roughness;
    float a2 = a * a;
    float denom = NdotH * NdotH * (a2 - 1.0) + 1.0;
    float D = a2 / (PI * denom * denom);

    float k = (roughness + 1.0) * (roughness + 1.0) / 8.0;
    float G = (NdotV / (NdotV * (1.0 - k) + k)) * (NdotL / (NdotL * (1.0 - k) + k));

    // IOR/specular-derived base reflectance for dielectrics, tinted albedo for metals.
    float3 F0 = lerp(dielectricF0, albedo, metallic);
    float3 F = F0 + (1.0 - F0) * pow(1.0 - VdotH, 5.0);

    float3 specular = D * G * F / max(4.0 * NdotV * NdotL, 1e-4);
    specular *= lerp(specularWeight, 1.0, metallic);
    // Per the glTF transmission model, transmitted light replaces the diffuse response (the
    // ambient side of that swap lives in Main); the specular lobe stays untouched.
    float3 kd = (1.0 - F) * (1.0 - metallic) * (1.0 - transmission);

    return (kd * albedo / PI + specular) * lightColor * NdotL;
}

#if MATERIAL_SHEEN
// KHR_materials_sheen: Charlie-лоб (Estevez & Kulla) - ретрорефлективный "световой ворс" велюра.
// Инвертированный GGX: максимум распределения на КАСАТЕЛЬНЫХ микрогранях, поэтому ткань светится
// ободком по контуру, а не бликом в центре.
float SheenDistributionCharlie(float sheenRoughness, float NdotH)
{
    float alphaG = sheenRoughness * sheenRoughness;
    float invAlpha = 1.0 / alphaG;
    float sin2h = max(1.0 - NdotH * NdotH, 0.0078125);
    return (2.0 + invAlpha) * pow(sin2h, invAlpha * 0.5) / (2.0 * PI);
}

// Ashikhmin visibility - стандартная пара к Charlie в референсном glTF Sample Viewer.
float SheenVisibilityAshikhmin(float NdotL, float NdotV)
{
    return 1.0 / max(4.0 * (NdotL + NdotV - NdotL * NdotV), 1e-4);
}

// Направленное альбедо Charlie-лоба E(NdotV, roughness) - аналитический фит LUT референсного
// вьюера (кусочно-квадратичная аппроксимация из three.js). Двойная служба: albedo-scaling
// базового слоя (энергосохранение - ворс "съедает" часть базового отклика) и вес env-ворса.
float SheenAlbedoE(float NdotV, float sheenRoughness)
{
    float r = sheenRoughness;
    float r2 = r * r;
    float a = r < 0.25 ? -339.36 * r2 + 161.6 * r - 25.147 : -8.48 * r2 + 14.3 * r - 9.95;
    float b = r < 0.25 ? 44.17 * r2 - 23.977 * r + 3.9199 : 1.97 * r2 - 3.27 * r + 0.72;
    float DG = exp(a * NdotV + b) + (r < 0.25 ? 0.0 : 0.1 * (r - 0.25));
    return saturate(DG * (1.0 / PI));
}

// Вклад одного света в sheen-лоб (аналог ShadePbrLight для ворса).
float3 ShadeSheenLight(float3 N, float3 V, float3 L, float3 lightColor,
                       float3 sheenColor, float sheenRoughness)
{
    float3 H = normalize(V + L);

    float NdotL = saturate(dot(N, L));
    float NdotV = saturate(dot(N, V)) + 1e-4;
    float NdotH = saturate(dot(N, H));

    float D = SheenDistributionCharlie(sheenRoughness, NdotH);
    float Vis = SheenVisibilityAshikhmin(NdotL, NdotV);

    return sheenColor * D * Vis * lightColor * NdotL;
}
#endif

struct PSInput
{
    float4 pos            : SV_POSITION;      // Clip space position.
    float3 normal         : NORMAL;           // Normal vector.
    float2 uv             : TEX_COORD;        // Texture coordinates.
    float3 worldPos       : TEXCOORD1;        // World-space position.
    float4 tangent        : TEXCOORD2;        // xyz = world-space tangent, w = bitangent sign.
    float4 vertexColor    : COLOR0;           // glTF COLOR_0 (linear), white when absent.
    float2 uv1            : TEXCOORD3;        // glTF TEXCOORD_1 (AO uv set), zero when absent.
};

// Output structure for the Pixel Shader.
struct PSOutput
{
    float4 color : SV_TARGET; // Final pixel color.
};

PSOutput Main(in PSInput input)
{
    PSOutput output;

    float3 normal = normalize(input.normal);

    if (PreviewMode == 2)
    {
        float3 visualized;

        if (PreviewChannel == 1)
        {
            visualized = float3(input.uv, 0.0);
        }
        else if (PreviewChannel == 2)
        {
            // Precomputed per-vertex tangent (see MeshUtility.GenerateTangents), interpolated and
            // re-normalized - stable across camera distance/angle, unlike the screen-space-derivative
            // (ddx/ddy) estimate this used to compute, which is perspective-distorted and gets noisy
            // up close or at grazing angles.
            float3 tangent = normalize(input.tangent.xyz);
            visualized = tangent * 0.5 + 0.5;
        }
        else
        {
            visualized = normal * 0.5 + 0.5;
        }

        output.color = float4(visualized, 1.0);
        return output;
    }

    // Two-tone hemisphere ambient (cool "sky" above / warm "ground" below, by normal.y) so the
    // mesh reads as a 3D shape even head-on, instead of a flat gray fill.
    float3 skyColor = float3(0.20, 0.21, 0.24);
    float3 groundColor = float3(0.09, 0.08, 0.07);
    float3 hemi = lerp(groundColor, skyColor, normal.y * 0.5 + 0.5);

    if (PreviewMode == 3)
    {
        // PBR (Cook-Torrance GGX metallic-roughness) lighting preview - see ShadePbrLight above.
        // Per the glTF spec COLOR_0 multiplies the base color (it is linear, like PbrBaseColor).
        float4 baseColor = PbrBaseColor * input.vertexColor;

        // KHR_texture_transform: все UV0-текстуры материала (base color/MR/normal/thickness)
        // сэмплируются трансформированными координатами; occlusion - своим UV-каналом ниже.
        float2 uv = TransformMaterialUv(input.uv);
#if HAS_BASECOLOR_TEXTURE
        {
            float4 texel = _MainTex.Sample(_MainTex_sampler, uv);
            // glTF stores base color textures in sRGB, but the engine uploads them as plain UNORM
            // (no *_SRGB view), so the decode to linear - where the lighting math below must happen -
            // is manual. The factor (PbrBaseColor) is already linear per the glTF spec; alpha too.
            texel.rgb = pow(texel.rgb, 2.2);
            baseColor *= texel;
        }
#endif

#if MATERIAL_ALPHA_CLIP
        // Alpha clipping (glTF alphaMode MASK, and a near-zero threshold for BLEND - see
        // ModelLoader.MaterialPbr). Must happen before any shading so discarded texels also skip
        // depth write, like foliage/decal cutouts expect.
        clip(baseColor.a - PbrAlphaCutoff);
#endif

        float3 albedo = baseColor.rgb;

        // glTF: the factors are multipliers over the metallic-roughness texture when one exists
        // (G = roughness, B = metallic).
        float metallic = PbrMetallic;
        float roughness = PbrRoughness;
#if HAS_MR_TEXTURE
        {
            float2 mr = _MetallicRoughnessTex.Sample(_MetallicRoughnessTex_sampler, uv).gb;
            roughness *= mr.x;
            metallic *= mr.y;
        }
#endif
        metallic = saturate(metallic);
        // Perceptual roughness floor keeps the GGX lobe wider than a pixel - a true mirror needs
        // an environment map to reflect, which this preview doesn't have.
        roughness = clamp(roughness, 0.06, 1.0);

        float3 N = normal;
        float3 V = normalize(viewData.CameraWorldPos - input.worldPos);

        // Two-sided shading: foliage/cloth cards are routinely seen from their back face, where
        // the authored normal points AWAY from the camera - NdotV clamps to 0, Schlick fresnel
        // shoots to its maximum and the pseudo-IBL term paints the whole leaf as a white blotch.
        // Flipping the normal toward the viewer shades both sides like a front face.
        if (dot(N, V) < 0.0)
        {
            N = -N;
        }

#if FEATURE_NORMAL_MAPS
        // Tangent-space normal mapping: perturbs N by _NormalTex before any lighting math, so
        // diffuse/specular/env/refraction all pick up the authored micro-relief. Кейворд вырезает
        // фичу целиком; бит PbrFeatureFlags остаётся live-тумблером настроек внутри варианта.
        // Degenerate tangents (meshes without UVs -> zero/garbage tangent) skip the perturbation.
        if (PbrFeatureFlags & FeatureNormalMaps)
        {
            float3 tangent = input.tangent.xyz - N * dot(N, input.tangent.xyz);
            float tangentLength = length(tangent);
            if (tangentLength > 1e-4)
            {
                float3 T = tangent / tangentLength;
                // Знак битангента (glTF TANGENT.w с поправкой на зеркалирование Z, либо
                // вычисленный генератором - см. ModelLoader): без него зеркальные UV-развёртки
                // применяют нормал-мапу с перевёрнутым Y - рельеф инвертируется.
                float3 B = cross(N, T) * input.tangent.w;

                float3 mapped = _NormalTex.Sample(_NormalTex_sampler, uv).xyz * 2.0 - 1.0;
                mapped.xy *= PbrNormalScale;
                N = normalize(mapped.x * T + mapped.y * B + mapped.z * N);
            }
        }
#endif

        // Camera-anchored light rig (a warm key above-right of the eye plus a cooler, weaker fill
        // below-left) so orbiting always keeps the model visibly lit from the viewer's side - the
        // preview scene has no light entities, and a world-fixed light would leave the model's far
        // side pitch black. cross(up, V) degenerates when V is vertical, but the orbit camera
        // clamps pitch to ~86 degrees (see ModelPreviewViewport.HandleCameraInput), so the basis
        // stays well-defined.
        float3 up = float3(0.0, 1.0, 0.0);
        float3 right = normalize(cross(up, V));

        // Ключевой свет: при включённых тенях и валидном мировом свете (см.
        // SimpleCullingAndRenderSystem.BuildLightData) - МИРОВОЕ направление «солнца» энвайронмента,
        // затеняемое shadow map-ой; тогда тень и блик согласованы, а модель может повернуться к
        // камере теневой стороной (её вытягивают fill и IBL). Иначе - прежний камерный риг: ключ
        // ~45 градусов сверху-справа от взгляда, всегда освещающий видимую сторону.
        float3 keyDir;
        float keyShadow = 1.0;
        float keyIntensity;
        bool hasWorldLight = false;

#if FEATURE_SHADOWS
        hasWorldLight = (PbrFeatureFlags & FeatureShadows)
            && dot(lightData.LightDirection.xyz, lightData.LightDirection.xyz) > 1e-4;

        if (hasWorldLight)
        {
            // Конвенция ОСНОВНОГО пайплайна (см. CullingAndRenderSystem): LightData.LightDirection
            // указывает НА солнце - SimpleCullingAndRenderSystem теперь пишет так же.
            keyDir = normalize(lightData.LightDirection.xyz);
            keyShadow = SampleWorldLightShadow(input.worldPos, N);

            // Мировой ключ слабее камерного (3.5 тюнился под риг без IBL-солнца): источник, из
            // которого он выведен, УЖЕ светит через энвайронмент-отражения - полная двойная
            // интенсивность пересвечивает глянцевые горизонтальные поверхности в белое.
            // Та же интенсивность обязана уходить в ProbeGiBaker.Bake sunColor - иначе баунс
            // разойдётся с прямым светом (вьюпорт шлёт одно значение в оба места, см.
            // ModelPreviewViewport). Поднимать её "для контраста теней" бесполезно: на светлом
            // альбедо сумма direct+ambient уходит за колено PBR Neutral (~0.76), и тонемап
            // сжирает контраст обратно (проверено на DragonAttenuation: при 5.0 тень на шахматке
            // исчезала совсем). 0 = кбуфер вне превью не заполнен, дефолт 2.0.
            keyIntensity = ProbeGiParams.z > 0.01 ? ProbeGiParams.z : 2.0;
        }
        else
#endif
        {
            keyDir = normalize(0.6 * V + 0.9 * up + 0.7 * right);
            keyIntensity = 3.5;
        }
        float3 keyColor = float3(1.0, 0.98, 0.92) * keyIntensity;

        // Заполняющий свет привязан к камере - осмысленно только для превью-рига одиночной модели
        // (гарантирует видимую сторону освещённой при орбите). Для сцены с мировым светом свет «из
        // глаз» нефизичен и уплощает картинку (при виде сверху пол получает fill, стены - ничего);
        // роль заполняющего там выполняет env-эмбиент, поэтому fill гасится.
        float3 fillDir = normalize(V - 0.6 * right - 0.1 * up);
        float3 fillColor = float3(0.55, 0.60, 0.70) * (hasWorldLight ? 0.0 : 0.8);

        // Тень должна глушить и энвайронмент-состовляющие: ключ ВЫВЕДЕН из доминантного источника
        // панорамы (софтбокс/солнце), и его отражение в глянцевой поверхности - тот же свет, что
        // блокирует окклюдер. Иначе на глянце тень «затирается» ярким зеркальным пятном. Не до
        // нуля: в тени остаётся рассеянная часть окружения.
        float envShadow;
        if (hasWorldLight)
        {
            // БЕЗ веса sunFacing: с ним поверхность, отвёрнутая от солнца, получала бы больше
            // эмбиента, чем затенённая солнечная - в сцене (двор Sponza) это инвертирует яркость
            // стен. Мягкое ослабление работает как дешёвая окклюзия отражённого света.
            envShadow = lerp(0.25, 1.0, keyShadow);
        }
        else
        {
            float sunFacing = saturate(dot(N, keyDir));
            envShadow = lerp(1.0, lerp(0.3, 1.0, keyShadow), sunFacing);
        }

#if MATERIAL_TRANSMISSION
        float transmission = saturate(PbrTransmission);
#else
        const float transmission = 0.0;
#endif

        // KHR_materials_ior / KHR_materials_dispersion: per-channel IOR triple. Dispersion is
        // 20/AbbeNumber per the spec (dragon sample: 2.04); the 0.05 scale is a preview
        // exaggeration - a physically-scaled spread refracted into a smooth gradient backdrop
        // would be invisible. Red bends least (lowest IOR), blue most. With no authored
        // extensions this degenerates to ior 1.5 / zero spread, and the F0 below lands exactly
        // on the classic dielectric 0.04.
        // Physical spread per KHR_materials_dispersion: dispersion = 20/AbbeNumber, and the
        // F-to-C line IOR difference is (ior-1)/Abbe = (ior-1) * dispersion / 20 (half of it on
        // each side of the center IOR). With a real geometric refraction offset below this is
        // enough to fringe high-contrast backgrounds exactly like the reference viewer.
        float ior = max(PbrIor, 1.001);
        float dispersionHalf = (ior - 1.0) * PbrDispersion * 0.025;
        float3 iors = float3(max(ior - dispersionHalf, 1.001), ior, ior + dispersionHalf);
        float3 iorF0 = (iors - 1.0) / (iors + 1.0);
        iorF0 *= iorF0;

        // KHR_materials_specular: перекраска диэлектрического F0 (сатиновый цветной блик).
        // Порядок по спеке: сначала умножение цвета на F0 от IOR, кламп к 1 ПОСЛЕ - авторские
        // значения >1 (ChairDamaskPurplegold: [1,0.25,2]) осмысленно поднимают канал до предела.
        // Вес (specularFactor) применяется к зеркальному лобу диэлектрика внутри ShadePbrLight.
        float3 dielectricF0 = min(iorF0 * PbrSpecularColorFactor.rgb, 1.0);
        float specularWeight = PbrSpecularColorFactor.w;

        float3 direct = ShadePbrLight(N, V, keyDir, keyColor, albedo, metallic, roughness, transmission, dielectricF0, specularWeight) * keyShadow
                      + ShadePbrLight(N, V, fillDir, fillColor, albedo, metallic, roughness, transmission, dielectricF0, specularWeight);

#if MATERIAL_SHEEN
        // KHR_materials_sheen: ворс поверх базового слоя. Базовый отклик глушится направленным
        // альбедо лоба (энергосохранение - что ушло в ворс, не вернётся базой), сверху -
        // Charlie-лобы ключа и заполняющего (та же тень ключа, что у базового слоя).
        float3 sheenColor = PbrSheenColorRoughness.rgb;
        float sheenRoughness = clamp(PbrSheenColorRoughness.w, 0.07, 1.0);
        float sheenNdotV = saturate(dot(N, V));
        float sheenScaling = 1.0 - max(sheenColor.r, max(sheenColor.g, sheenColor.b))
                                 * SheenAlbedoE(sheenNdotV, sheenRoughness);

        direct = direct * sheenScaling
               + ShadeSheenLight(N, V, keyDir, keyColor, sheenColor, sheenRoughness) * keyShadow
               + ShadeSheenLight(N, V, fillDir, fillColor, sheenColor, sheenRoughness);
#endif

        // ВРЕМЕННЫЙ отладочный хук (PreviewChannel == 11, см. ниже) - диагностика punctual-теней:
        // x/y = shadowUv последнего обработанного punctual-света с назначенным слайсом, z = shadowNdc.z,
        // w = shadowLit (1 = сэмплер не увидел окклюдер, 0 = увидел). -1 в w значит "ветка сэмплинга
        // не выполнилась вовсе" (shadowClip.w <= 1e-4 или shadowUv/shadowNdc.z вне диапазона).
        float4 dbgPunctual = float4(0, 0, 0, -1);

        // ----- Clustered punctual-света (point/spot) -------------------------------------------
        // Пиксель находит свой фроксел-кластер (тайл экрана + экспоненциальный срез по view-z,
        // обязано зеркалить прямое отображение в LightClusterCS.hlsl) и шейдит только света его
        // кластера. ClusterParams.y == 0 - punctual-светов у камеры нет, ветка мёртвая.
        uint punctualCount = (uint)lightData.ClusterParams.y;
        if (punctualCount > 0)
        {
            float clusterViewZ = mul(float4(input.worldPos, 1.0), viewData.view).z;
            float clusterZNear = lightData.ClusterParams.z;
            float clusterZFar = lightData.ClusterParams.w;

            float2 clusterUv = input.pos.xy / viewData.viewport.zw;
            uint tileX = min((uint)(clusterUv.x * CLUSTER_GRID_X), CLUSTER_GRID_X - 1);
            uint tileY = min((uint)(clusterUv.y * CLUSTER_GRID_Y), CLUSTER_GRID_Y - 1);
            float clusterSlice = log2(max(clusterViewZ, clusterZNear) / clusterZNear)
                               / log2(clusterZFar / clusterZNear) * CLUSTER_GRID_Z;
            uint tileZ = (uint)clamp(clusterSlice, 0.0, CLUSTER_GRID_Z - 1.0);

            uint clusterIdx = ClusterFlatIndex(uint3(tileX, tileY, tileZ));
            uint clusterLightCount = min(ClusterCounts[clusterIdx], CLUSTER_MAX_LIGHTS);

            for (uint li = 0; li < clusterLightCount; li++)
            {
                PunctualLight punctual = PunctualLights[ClusterIndices[clusterIdx * CLUSTER_MAX_LIGHTS + li]];
                float3 toLight = punctual.PositionRange.xyz - input.worldPos;
                float punctualDistSq = dot(toLight, toLight);
                float punctualRange = punctual.PositionRange.w;
                if (punctualDistSq > punctualRange * punctualRange)
                    continue;

                float punctualDist = sqrt(max(punctualDistSq, 1e-6));
                float3 punctualL = toLight / punctualDist;

                // Гладкое окно затухания (Frostbite/glTF punctual): обратный квадрат, приглушенный
                // к нулю на границе радиуса - без ступеньки на срезе кулинга.
                float distFactor = saturate(1.0 - pow(punctualDist / punctualRange, 4.0));
                float punctualAtten = distFactor * distFactor / (punctualDistSq + 1e-2);

                if (punctual.DirectionType.w > 0.5)
                {
                    float cd = dot(-punctualL, punctual.DirectionType.xyz);
                    float spotFactor = saturate((cd - punctual.SpotAngles.x) * punctual.SpotAngles.y);
                    punctualAtten *= spotFactor * spotFactor;
                }

                // Тень света: спот сэмплирует свой единственный слайс, точечный выбирает грань куба
                // по доминирующей оси вектора свет-фрагмент (индексация граней ОБЯЗАНА совпадать с
                // PunctualShadowScheduler.FaceDirs: +X,-X,+Y,-Y,+Z,-Z).
                if (punctual.ShadowParams.x >= 0.0 && punctualAtten > 0.0)
                {
                    uint shadowSlice = (uint)punctual.ShadowParams.x;
                    if (punctual.DirectionType.w < 0.5)
                    {
                        float3 toFrag = -toLight;
                        float3 absDir = abs(toFrag);
                        if (absDir.x >= absDir.y && absDir.x >= absDir.z)
                            shadowSlice += toFrag.x > 0.0 ? 0 : 1;
                        else if (absDir.y >= absDir.z)
                            shadowSlice += toFrag.y > 0.0 ? 2 : 3;
                        else
                            shadowSlice += toFrag.z > 0.0 ? 4 : 5;
                    }

                    // Normal-offset bias (аналог SampleWorldLightShadow выше): сдвиг точки сэмплирования
                    // вдоль нормали на ~1.5 текселя перспективного слайса В МИРОВЫХ единицах. В отличие
                    // от орто-каскадов тексель здесь растёт с глубиной: 2*tan(halfFov)*z/1024. Точная
                    // view-space глубина известна только ПОСЛЕ трансформации, поэтому для размера текселя
                    // берём дистанцию до света punctualDist - для маленького bias-сдвига этого достаточно.
                    // tan(halfFov) слайса: у спота - из внешнего конуса (SpotAngles.z/.x = sin/cos
                    // внешнего полуугла), у точечного каждая грань куба ~90 градусов -> tan(45) = 1.
                    float shadowTanHalfFov = punctual.DirectionType.w > 0.5
                        ? punctual.SpotAngles.z / max(punctual.SpotAngles.x, 1e-4)
                        : 1.0;
                    float shadowTexelWorld = 2.0 * shadowTanHalfFov * punctualDist / 1024.0;
                    float3 shadowSamplePos = input.worldPos + N * shadowTexelWorld * 1.5;

                    float4 shadowClip = mul(float4(shadowSamplePos, 1.0), PunctualShadowMatrices[shadowSlice]);
                    if (shadowClip.w > 1e-4)
                    {
                        float3 shadowNdc = shadowClip.xyz / shadowClip.w;
                        float2 shadowUv = shadowNdc.xy * float2(0.5, -0.5) + 0.5;
                        bool dbgUvOk = all(shadowUv >= 0.0) && all(shadowUv <= 1.0);
                        bool dbgZOk = shadowNdc.z < 1.0;
                        dbgPunctual = float4(shadowUv, shadowNdc.z, dbgUvOk ? (dbgZOk ? -2 : -4) : -3);
                        if (dbgUvOk && dbgZOk)
                        {
                            // Депф-bias перспективного слайса. Каскады солнца - орто, там NDC-глубина
                            // ЛИНЕЙНА по view-Z, и константа в NDC-единицах работает на любой дистанции.
                            // Здесь проекция перспективная (PunctualShadowScheduler.AddSlice,
                            // CreatePerspectiveFieldOfViewLeftHanded): ndc = f/(f-n) - n*f/((f-n)*z),
                            // так что та же NDC-константа на разной глубине z стоит РАЗНОЕ число метров -
                            // близко к свету это мало, а у границы дальности разгоняется до целого
                            // метра просадки тени под объект (peter-panning). Вместо этого bias задаётся
                            // в МИРОВЫХ единицах (склон по углу к свету + пол, как у каскадов) и
                            // переводится в NDC локальной производной d(ndc)/dz = n*f/((f-n)*z^2) в точке
                            // приёмника (z = shadowClip.w - view-space глубина вдоль оси света).
                            float shadowFar = punctual.PositionRange.w;
                            float shadowNear = max(0.05, shadowFar * 0.001);
                            float shadowZ = max(shadowClip.w, shadowNear);
                            float shadowWorldBias = max(0.05 * (1.0 - dot(N, punctualL)), 0.005);
                            float shadowNdcPerWorld = shadowNear * shadowFar
                                / max((shadowFar - shadowNear) * shadowZ * shadowZ, 1e-6);
                            float shadowBias = shadowWorldBias * shadowNdcPerWorld;

                            float shadowLit = PunctualShadowMaps.SampleCmpLevelZero(
                                PunctualShadowMaps_sampler,
                                float3(shadowUv, shadowSlice), shadowNdc.z - shadowBias);
                            dbgPunctual.w = shadowLit;
                            punctualAtten *= lerp(1.0, shadowLit, saturate(punctual.ShadowParams.y));
                        }
                    }
                }

                direct += ShadePbrLight(N, V, punctualL,
                    punctual.ColorIntensity.rgb * punctual.ColorIntensity.w * punctualAtten,
                    albedo, metallic, roughness, transmission, dielectricF0, specularWeight);
            }
        }

        // NB: the per-channel F0 spread is left at its physical (subtle) level on purpose - an
        // amplified F0 acts at EVERY angle and painted the whole model with a flat blue cast
        // instead of edge fringes; the visible dispersion cue lives in the edge-weighted
        // transmitted term below.

        // Environment irradiance: the env map's top (fully-prefiltered) mip sampled along the
        // normal - a proper diffuse ambient replacing the old two-tone hemisphere. Diffuse ONLY:
        // metals get their entire environment response from envSpecular below - an extra
        // F0-tinted ambient here double-counts the environment and turns chrome into glossy
        // plastic (the pre-IBL "so metals don't go black" hack is obsolete now).
        // Kept deliberately below the key's level: ambient that rivals the key is exactly what makes
        // the render look light-less (it re-flattens the NdotL contrast the key creates).
        // Baked AO (feature-gated): darkens only the ambient/env terms - per the glTF spec direct
        // light is not occluded. Strength remap: lerp(1, sample, strength).
        float occlusion = 1.0;
#if FEATURE_OCCLUSION
        if (PbrFeatureFlags & FeatureOcclusion)
        {
            // AO сэмплится своим UV-каналом (glTF texCoord occlusion-текстуры): второй канал
            // (TEXCOORD_1) - как есть, UV0 - с материальной трансформацией (типичный ORM-атлас
            // делит трансформацию с MR-текстурой).
            float2 occlusionUv = PbrOcclusionUvSet == 1 ? input.uv1 : uv;
            float occlusionSample = _OcclusionTex.Sample(_OcclusionTex_sampler, occlusionUv).r;
            occlusion = 1.0 + PbrOcclusionStrength * (occlusionSample - 1.0);
        }
#endif

        // KHR_materials_specular участвует и в env-отклике - иначе сатиновый цветной блик виден
        // только в прямом свете, а отражение окружения остаётся "бесцветно стеклянным".
        float3 ambientF0 = lerp(dielectricF0, albedo, metallic);

        // Probe-GI: запечённая irradiance (небо × видимость + отскоки) вместо константного
        // ambient-уровня - пол двора светлее ниш под арками, отскок от освещённого камня тёплый.
        // Тень ключа НЕ применяется: заслонённость уже запечена в пробах (envShadow был её
        // экранной аппроксимацией). skyVisibility ниже глушит env-спекуляр.
        float skyVisibility = 1.0;
        float probeSunFraction = 0.0;
        // Отметка ближайшей пробы для канала 10 (расстановка проб); вне его не используется.
        float3 probeMarker = float3(1e6, 0.0, 0.0);
        bool probeGi = ProbeGridOrigin.w > 0.5;
        float3 probeIrradiance = 0.0;
        if (probeGi)
        {
            probeIrradiance = SampleProbeGi(input.worldPos, N, skyVisibility, probeSunFraction,
                                            probeMarker);
            probeGi = probeIrradiance.x >= 0.0;
        }

        // 0.15 тюнился под превью-риг, где тени добирал камерный fill. В сцене с мировым светом
        // fill выключен и эмбиент - ЕДИНСТВЕННЫЙ свет в тени; без буста двор Sponza проваливается
        // в черноту (небо/отскок от камня в реальности много ярче студийной панорамы).
        float ambientLevel = hasWorldLight ? 0.55 : 0.15;

        // Экранное глушение тенью ключа ТОЛЬКО солнечной доли probe-поля (probeSunFraction,
        // печётся бейкером в Sh2.a): пробы стоят на ~1/22 габарита и контактную тень разрешить
        // не могут - точка в тени получает поле соседних ЛИТ-проб, где солнечный баунс
        // доминирует, и тень заливается (шахматка DragonAttenuation). Небесная же часть в тени
        // НЕ трогается: затенённый двор освещён небом - это и есть референсный вид GI (Intel
        // Sponza); равномерное глушение всего эмбиента (прежний lerp(0.1..0.4, 1, keyShadow))
        // topило двор в черноте. sunFraction=0 (чисто небесное поле) - тень эмбиент не трогает,
        // sunFraction=1 (чисто солнечное) - глушение как у прежнего envShadow. Флоры и множитель
        // эмбиента - ручки окна Graphics (ProbeGiParams/ProbeGiParams2, см. кбуфер). Небесная
        // доля по умолчанию тенью не гасится (skyFloor=1, физически честно - см. Intel Sponza),
        // но у художника есть отдельная ручка затемнить тень целиком под нужный муд.
        float skyFloor = ProbeGiParams2.x > 0.001 ? saturate(ProbeGiParams2.x) : 1.0;
        float sunDamp = lerp(saturate(ProbeGiParams.x), 1.0, keyShadow);
        float skyDamp = lerp(skyFloor, 1.0, keyShadow);
        float probeShadow = lerp(skyDamp, sunDamp, probeSunFraction);
        float probeBoost = ProbeGiParams.w > 0.01 ? ProbeGiParams.w : 1.0;
        float3 ambient = probeGi
            ? probeIrradiance * probeBoost * albedo * (1.0 - metallic) * occlusion * probeShadow
            : SampleEnvironment(N, 1.0) * ambientLevel * albedo * (1.0 - metallic) * occlusion * envShadow;

        // KHR_materials_transmission via a real refraction pass (see ForwardPass): _SceneColor
        // holds the opaque scene as drawn this frame, and each channel samples it along its own
        // refracted view ray (per-channel IOR - KHR_materials_dispersion falls out of this as
        // genuine color fringing wherever the refracted background has contrast). The refraction
        // offset is GEOMETRIC, matching the reference viewer: the exit point of the refracted ray
        // after travelling the volume's world-space thickness is projected back to screen, so the
        // bend automatically scales with camera distance and object size instead of smearing a
        // fixed fraction of the screen. Materials with transmission but no volume data get a
        // small distance-proportional thickness so plain glass still visibly bends. Where the
        // refracted sample lands outside any drawn geometry (alpha 0 - the target clears with
        // alpha 0), fall back to the analytic backdrop gradient the UI composites behind the
        // image (constants mirror ModelPreviewViewport.Render).
#if MATERIAL_TRANSMISSION
        // Константы подложки заданы в ОТОБРАЖАЕМОМ пространстве (ровно те, что рисует ImGui). В
        // HDR-конвейере кадр линейный до самого TonemapPS, и подмешивать сюда sRGB-значение
        // нельзя - стекло на фоне подложки поехало бы по яркости; поэтому под FeatureHdrOutput
        // градиент разворачивается в линейное пространство той же гаммой 2.2, какой его потом
        // свернёт обратно тонемап.
        float backdropBottom = 0.26;
        float backdropTop = 0.55;
        if ((PbrFeatureFlags & FeatureHdrOutput) != 0)
        {
            backdropBottom = pow(backdropBottom, 2.2);
            backdropTop = pow(backdropTop, 2.2);
        }

        float2 screenUv = input.pos.xy / viewData.viewport.zw;
        float thicknessSample = _ThicknessTex.Sample(_ThicknessTex_sampler, uv).g;
        float thicknessWorld = PbrThicknessWorld > 0.0
            ? PbrThicknessWorld * thicknessSample
            : 0.03 * length(viewData.CameraWorldPos - input.worldPos);

        float4 entryClip = mul(float4(input.worldPos, 1.0), viewData.viewProj);
        float2 entryNdc = entryClip.xy / max(entryClip.w, 1e-4);

        float3 transmitted;

#if MATERIAL_DISPERSION
        // Пер-канальная рефракция (KHR_materials_dispersion): три преломлённых луча со своими IOR,
        // цветная кайма возникает там, где преломлённый фон контрастен.
        [unroll]
        for (int c = 0; c < 3; c++)
        {
            float3 refr = refract(-V, N, 1.0 / iors[c]);

            // Проекция точки выхода луча и точки входа через один и тот же viewProj - разница их
            // NDC не зависит от соглашений о начале координат, остаётся только направление оси Y
            // (NDC вверх -> UV вниз).
            float3 exitPoint = input.worldPos + refr * thicknessWorld;
            float4 exitClip = mul(float4(exitPoint, 1.0), viewData.viewProj);
            float2 ndcDelta = exitClip.xy / max(exitClip.w, 1e-4) - entryNdc;
            float2 uv = saturate(screenUv + ndcDelta * float2(0.5, -0.5));

            float4 scene = _SceneColor.Sample(_SceneColor_sampler, uv);
            float backdrop = lerp(backdropBottom, backdropTop, saturate(refr.y * 0.5 + 0.5));
            transmitted[c] = lerp(backdrop, scene[c], scene.a);
        }
#else
        // Без дисперсии - одна рефракция средним IOR, один сэмпл сцены.
        {
            float3 refr = refract(-V, N, 1.0 / ior);
            float3 exitPoint = input.worldPos + refr * thicknessWorld;
            float4 exitClip = mul(float4(exitPoint, 1.0), viewData.viewProj);
            float2 ndcDelta = exitClip.xy / max(exitClip.w, 1e-4) - entryNdc;
            float2 uv = saturate(screenUv + ndcDelta * float2(0.5, -0.5));

            float4 scene = _SceneColor.Sample(_SceneColor_sampler, uv);
            float backdrop = lerp(backdropBottom, backdropTop, saturate(refr.y * 0.5 + 0.5));
            transmitted = lerp(backdrop.xxx, scene.rgb, scene.a);
        }
#endif

        // Beer-Lambert volume absorption (KHR_materials_volume): attenuationColor^(thickness/dist),
        // with per-texel thickness from _ThicknessTex (G channel; white fallback = factor alone).
        // This is what gives dense glass its dark, saturated interior instead of a milky fill,
        // while thin features (fins, crests) stay bright and see-through.
        if (PbrVolumeAttenuation.w > 0.0)
        {
            transmitted *= pow(max(PbrVolumeAttenuation.rgb, 1e-4), PbrVolumeAttenuation.w * thicknessSample);
        }

        transmitted *= albedo;
        ambient = lerp(ambient, transmitted, transmission * (1.0 - metallic));
#endif

        // IBL specular: reflect the view ray into the prefiltered environment - the mip chain
        // encodes the roughness blur, so no extra dulling factor is needed. Weighted by
        // roughness-aware Schlick fresnel: smooth surfaces get a bright grazing-angle rim (the
        // classic glass cue), rough dielectrics keep a low F0 = 0.04 response.
        float NdotV = saturate(dot(N, V));
        float3 R = reflect(-V, N);
        float3 envColor = SampleEnvironment(R, roughness);
        float3 Fr = ambientF0 + (max((1.0 - roughness).xxx, ambientF0) - ambientF0) * pow(1.0 - NdotV, 5.0);
        // С пробами env-отражение глушится запечённой видимостью неба (интерьеру арки нечего
        // зеркалить из зенита) И экранной тенью ключа (см. probeShadow выше - глянец в тени иначе
        // затирается ярким зеркальным пятном), без них - прежней аппроксимацией envShadow.
        float envOcclusion = probeGi ? lerp(saturate(ProbeGiParams.y), 1.0, skyVisibility) * probeShadow : envShadow;
        float3 envSpecular = envColor * Fr * lerp(specularWeight, 1.0, metallic) * occlusion * envOcclusion;

#if MATERIAL_SHEEN
        // Env-ворс: окружение вдоль отражённого луча, взвешенное направленным альбедо лоба.
        // Мипы _EnvMap префильтрованы под GGX, а не Charlie - для превью приемлемая аппроксимация
        // (широкий лоб ворса ~ высокая GGX-шероховатость). Базовые env-термы глушатся тем же
        // albedo-scaling, что и direct.
        float3 envSheen = SampleEnvironment(R, sheenRoughness) * sheenColor
                        * SheenAlbedoE(NdotV, sheenRoughness) * occlusion * envShadow;
        ambient *= sheenScaling;
        envSpecular = envSpecular * sheenScaling + envSheen;
#endif

        // Diagnostic hooks (PreviewProbe): raw linear dumps of the individual lighting terms.
        // Канал 9 - отладочный вид probe-GI (чекбокс Probe debug view в окне Graphics): R = доля
        // солнечного света в поле (то, что глушит Sun bounce in shadow), G = видимость неба
        // (то, что глушит Env specular occlusion), B = экранная тень ключа. Позволяет художнику
        // видеть, на какие места сцены реально влияет каждая ручка.
        // Канал 10 - РАССТАНОВКА проб: где каждая проба стоит и что с ней сделала релокация.
        // Рисуется пятном на поверхности, оказавшейся рядом с пробой: зелёная - проба на своём
        // узле сетки, жёлтая-красная - отодвинута (тем сильнее, чем краснее), синяя - признана
        // невалидной, то есть замурована и в интерполяцию не идёт. Фон - валидность поля.
        // Пробы в открытом воздухе, рядом с которыми нет поверхности, не отмечаются: рисовать их
        // нечем, здесь нет своего прохода геометрии - зато именно застрявшие в стенах видны все.
        if (PreviewChannel == 10)
        {
            float3 col = float3(0.05, 0.05, 0.05) * probeMarker.z;
            if (probeMarker.x < 0.14)
            {
                float reloc = saturate(probeMarker.y / 0.45);
                col = probeMarker.z < 0.01
                    ? float3(0.1, 0.2, 1.0)
                    : lerp(float3(0.1, 1.0, 0.1), float3(1.0, 0.1, 0.05), reloc);
            }

            output.color = float4(pow(saturate(col), 1.0 / 2.2), 1.0);
            return output;
        }
        if (PreviewChannel == 9)
        {
            // sRGB-кодирование вручную, как у основного пути ниже: таргет UNORM, и линейные
            // 0.1-0.2 (типичная видимость неба в интерьере) без него читаются как чёрный.
            float3 probeDebug = float3(probeSunFraction, skyVisibility, keyShadow);
            output.color = float4(pow(saturate(probeDebug), 1.0 / 2.2), 1.0);
            return output;
        }
        if (PreviewChannel == 8)
        {
            output.color = float4(ambient, 1.0);
            return output;
        }
        if (PreviewChannel == 7)
        {
            output.color = float4(direct, 1.0);
            return output;
        }
        if (PreviewChannel == 6)
        {
            output.color = float4(envSpecular, 1.0);
            return output;
        }
        // ВРЕМЕННЫЙ отладочный канал 11 - визуализация punctual-теневого сэмплинга (см. dbgPunctual
        // выше), закодирован в цвет для 8-бит ридбека (PreviewProbe.ReadRgba8):
        //   магента (1,0,1) - ветка сэмплинга не выполнилась вовсе (punctual.ShadowParams.x < 0
        //     или punctualAtten <= 0 - свет без назначенного слайса/вне радиуса)
        //   жёлтый  (1,1,0) - shadowClip посчитан, но отброшен guard'ом (shadowUv/shadowNdc.z вне
        //     диапазона - точка приёмника вне фрустума слайса)
        //   градация серого - реальный shadowLit сэмплера (0 чёрный = окклюдер найден/тень,
        //     1 белый = не найден/свет)
        if (PreviewChannel == 11)
        {
            float3 dbgColor =
                dbgPunctual.w < -4.5 ? float3(1, 0, 1)   // -5 (default/no light path) сюда не попадает - оставлено на всякий
                : dbgPunctual.w < -3.5 ? float3(1, 0.5, 0) // -4: UV в допуске, ndc.z >= 1.0 (за дальней плоскостью)
                : dbgPunctual.w < -2.5 ? float3(0, 1, 1)   // -3: UV вне [0,1] (точка вне квадрата слайса)
                : dbgPunctual.w < -1.5 ? float3(1, 0, 1)   // -1: ветка не выполнилась вовсе
                : dbgPunctual.www;                          // сэмплировано: 0 (тень) .. 1 (свет)
            output.color = float4(dbgColor, 1.0);
            return output;
        }

        float3 lit = direct + ambient + envSpecular;

        // HDR-конвейер: таргет RGBA16F, и кадр уходит дальше линейным - экспозицию по замеренной
        // яркости, кривую и sRGB-энкод делает TonemapPass (см. TonemapPS.hlsl). Тонмапить здесь
        // значило бы мерить яркость уже сжатого кадра - авто-экспозиции нечего было бы ловить.
        if ((PbrFeatureFlags & FeatureHdrOutput) != 0)
        {
            output.color = float4(lit, 1.0);
            return output;
        }

        // Khronos PBR Neutral tone map: the key light intentionally overshoots [0,1] for a
        // specular punch, and a plain saturate would clip it into flat white blotches.
        float3 mapped = ApplyToneCurve(lit, PbrToneCurve);

        // Back to display (sRGB) space by hand - the preview color target is UNORM, not *_SRGB,
        // so nothing downstream encodes for the monitor. Without this the physically-linear result
        // reads as "no light at all": shadows crush to black and midtones lose half their level
        // (a linear 0.35 displays like ~0.1).
        output.color = float4(pow(mapped, 1.0 / 2.2), 1.0);
        return output;
    }

    // View-facing rim highlight: real camera direction (ViewData.CameraWorldPos, populated per
    // frame - see RenderingComponents.CreateViewData) rather than a normal pushed through the
    // clip-space viewProj matrix, with a power falloff for a crisper edge than a linear one.
    float3 viewDir = normalize(viewData.CameraWorldPos - input.worldPos);
    const float rimPower = 2.0;
    float rim = pow(saturate(dot(normal, viewDir)), rimPower);

    float3 albedo = PreviewMode == 0
        ? _MainTex.Sample(_MainTex_sampler, TransformMaterialUv(input.uv)).rgb * input.vertexColor.rgb
        : float3(1.0, 1.0, 1.0);
    output.color = float4(albedo * saturate(hemi + rim), 1.0);
    return output;
}
