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
// запечённых CPU-трассировкой. Сетка ПЛОТНАЯ - проба есть в каждом узле, а тексель считается из
// её координат арифметикой: ширина атласа равна оси X сетки, плоскости Z уложены столбиком
// (см. ProbeGiBakeResult.ShWidth и ProbeGiSampleBody.hlsl).
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
Texture2D _ProbeSh0_C2;
Texture2D _ProbeSh1_C2;
Texture2D _ProbeSh2_C2;
Texture2D _ProbeSh3_C2;
Texture2D _ProbeVis_C2;
Texture2D _ProbeOffset_C2;

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
// ЧЕТЫРЕ float4-СТРОКИ НА СЛАЙС, а не float4x4. Матрицу в структурном буфере держать нельзя:
// PackMatrixRowMajor (DiligentShader) задаёт раскладку матриц в кбуферах, но на элементы
// StructuredBuffer не распространяется, и majorness там оказывается РАЗНОЙ У БЭКЕНДОВ - D3D12
// отдавал элемент транспонированным, Vulkan нет. Симптом был односторонний и потому обманчивый:
// mul(pos, M) считал не то произведение, w (глубина вдоль оси грани) уходила в минус у 57.5%
// пикселей и они молча отсекались guard'ом, у остальных 8% shadowUv улетал за квадрат слайса, до
// сэмплера доходило 5.7% кадра со средним shadowLit 0.957 - теней от punctual-светов не было вовсе.
// Сборка строк вручную (см. LoadPunctualShadowMatrix) от раскладки не зависит в принципе.
// Каскады солнца этим не болели и подсказать не могли: их матрицы едут в КБУФЕРЕ
// (LightData.CascadeMatrix*), а этот буфер - единственное место во всём движке, где матрица лежала
// в структурном буфере.
StructuredBuffer<float4> PunctualShadowMatrices;

// viewProj слайса как row-major матрица: строки лежат подряд, слайс занимает четыре элемента.
float4x4 LoadPunctualShadowMatrix(uint slice)
{
    uint row = slice * 4;
    return float4x4(PunctualShadowMatrices[row + 0], PunctualShadowMatrices[row + 1],
                    PunctualShadowMatrices[row + 2], PunctualShadowMatrices[row + 3]);
}
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
    // Режим фильтрации теней (SHADOW_MODE_*, см. дефайны выше). 0 (zero-init от сцен вне превью)
    // = PCSS - дефолтное качество.
    int PbrShadowMode;
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
    // xyz = число проб по осям сетки (float для простоты cbuffer-паковки), w = доля взгляда в
    // направлении сдвига сэмпла (ручка View bias).
    float4 ProbeGridCounts;
    // xyz = тороидальное смещение сетки в пробах: узел c лежит в текселе (c + scroll) mod counts
    // (см. ProbeGiTextures.GridScroll).
    float4 ProbeGridScroll;
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
    float4 ProbeGridScroll1;
    float4 ProbeGridOrigin2;
    float4 ProbeGridCell2;
    float4 ProbeGridCounts2;
    float4 ProbeGridScroll2;

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
// Сторона карты теней солнца в текселях (см. ShadowRenderer.ShadowMapSize).
#define SUN_SHADOW_TEXELS 4096.0

// ОТСТУП от края каскада, в текселях. Каскад не берётся, пока точка не окажется глубже отступа
// внутрь, - иначе фильтр вылезает за карту. PCF 3x3 тянет тапы на тексель в стороны, сравнивающий
// сэмплер добавляет свои полтекселя фильтрации, а normal-offset выше уже сдвинул точку на полтора
// текселя. У края всё это адресуется ЗА пределы карты, сэмплер кламмит адрес - и тапы читают
// краевой тексель, то есть глубину совсем другого места сцены. На экране это прямой шов поперёк
// стены на границе каскада (ровно та геометричная линия, что видна на скриншотах).
#define SUN_CASCADE_MARGIN_TEXELS 3.0

// Ширина полосы перехода в долях стороны каскада. В ней тень берётся из ДВУХ каскадов и
// смешивается: без неё переход остаётся видимым и после отступа - у соседних каскадов разное
// разрешение карты, а значит разная мягкость PCF и разный масштаб байеса, и граница читается как
// ступенька резкости.
#define SUN_CASCADE_BLEND_UV 0.06

// --- PCSS: полутень от углового размера солнца ---
// Тангенс ПОЛУугла видимого диска приходит в lightData.SpotAngles.w (см.
// CullingAndRenderSystem.SunTanHalfAngle). 0 - пайплайн поле не заполняет (превью), берём дефолт:
// диск в один градус (реальное солнце ~0.53, чуть крупнее - край читается мягким и на коротких
// тенях).
#define SUN_DEFAULT_TAN_HALF_ANGLE 0.00873

// Режимы фильтрации теней (PbrShadowMode, комбо «Shadow filtering» окна Graphics), по
// возрастанию накладных расходов: HARD - 1 аппаратный тап; PCF - фикс. бокс 3x3; PCSS - полутень
// от углового размера источника (дефолт); PCSS_HQ - тот же PCSS с удвоенным веером и более
// широкой полутенью. НОЛЬ обязан оставаться PCSS: сцены вне превью кбуфер не заполняют, и
// zero-init должен давать дефолтное качество, а не самое дешёвое.
#define SHADOW_MODE_PCSS 0
#define SHADOW_MODE_HARD 1
#define SHADOW_MODE_PCF 2
#define SHADOW_MODE_PCSS_HQ 3
// Теневые лучи по TLAS (inline RayQuery) вместо shadow map - только в варианте шейдера с
// FEATURE_RT_SHADOWS (DXC/SM6.5, см. ниже); без него режим тихо падает в PCSS - в т.ч. на
// Vulkan, где RaytracingAccelerationStructure не доходит до DXC (см. ProbeRoundPipelines).
#define SHADOW_MODE_RT 4

#if FEATURE_RT_SHADOWS
// Лучей в конусе углового размера солнца на пиксель. Зерно усредняет TAAU - как у PCSS.
#define RT_SHADOW_RAYS 8
// Отступ старта луча вдоль нормали от самопересечения с собственным треугольником. Константа в
// МИРОВЫХ единицах: у RT-луча нет текселя shadow map, от которого масштабировался normal-offset
// растрового пути.
#define RT_SHADOW_NORMAL_OFFSET 0.02
#define RT_SHADOW_TMAX 1e4

// TLAS сцены - тот же, что у probe-GI (см. ProbeSceneAccel); привязывается к материалам
// DiligentBatchRenderer-ом только когда вариант с этим кейвордом реально запрошен.
RaytracingAccelerationStructure _SceneTlas;
#endif

// Тапов на диск Фогеля - и в поиске блокеров, и в PCF. 16+16 на пиксель вместо полного прохода
// прямоугольника у классического PCSS: недобор выборок закрывают пер-пиксельное вращение диска
// (IGN ниже) и temporal-сглаживание (TAAU), которому остаточный шум по вкусу. HQ удваивает веер -
// для стоп-кадров и сцен без TAAU, где зерно нечем усреднить.
#define SUN_PCSS_TAPS 16
#define SUN_PCSS_HQ_TAPS 32

// Радиус поиска блокеров, тексели. Блокеры дальше не видны - значит и полутень шире
// SUN_PCSS_MAX_PENUMBRA_TEXELS искать не имеет смысла на этом каскаде: очень длинные мягкие тени
// уходят в более грубый каскад, у которого тексель крупнее.
#define SUN_PCSS_SEARCH_TEXELS 12.0
#define SUN_PCSS_MAX_PENUMBRA_TEXELS 20.0
#define SUN_PCSS_HQ_SEARCH_TEXELS 16.0
#define SUN_PCSS_HQ_MAX_PENUMBRA_TEXELS 32.0

// Мировой диапазон глубины каскада в ДОЛЯХ его ширины: zfar = 4.5r при ширине 2r (znear 0.01
// пренебрежим). Зеркалит casterExtension + receiverExtension в CullingAndRenderSystem.UpdateCascades
// и SimpleCullingAndRenderSystem.BuildLightData - менять только втроём, иначе полутень получит
// неверный мировой масштаб.
#define SUN_CASCADE_DEPTH_RANGE_RATIO 2.25

// --- PCSS punctual-теней (перспективные слайсы) - те же диски Фогеля, что у солнца ---
// Дефолтный мировой радиус светящегося тела, метры - когда ShadowParams.w не заполнен
// (LightComponent.SourceRadius = 0): габарит лампочки.
#define PUNCTUAL_DEFAULT_SOURCE_RADIUS 0.05
// Радиус поиска блокеров и потолок радиуса PCF, тексели слайса. Потолок ЖЁСТКИЙ: грани куба
// рисуются с перехлёстом ~2% (около 20 текселей из 1024, см. PunctualShadowScheduler) - тапы
// дальше уезжали бы на чужую грань.
#define PUNCTUAL_PCSS_SEARCH_TEXELS 10.0
#define PUNCTUAL_PCSS_MAX_PENUMBRA_TEXELS 16.0

// Interleaved gradient noise (Jimenez, 2014): дешёвый пер-пиксельный [0,1) для вращения диска.
// Джиттер TAAU каждый кадр чуть сдвигает привязку пикселей к поверхности, так что паттерн не
// стоит на месте и усредняется темпорально без отдельного счётчика кадров.
float InterleavedGradientNoise(float2 pixel)
{
    return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
}

// i-й из count тапов диска Фогеля, повёрнутого на phi; радиус нормирован в [0,1].
float2 VogelDiskSample(int i, int count, float phi)
{
    float r = sqrt((float(i) + 0.5) / float(count));
    float theta = float(i) * 2.39996323 + phi; // золотой угол
    float s, c;
    sincos(theta, s, c);
    return r * float2(c, s);
}

// firstCascade - индекс ПЕРВОГО каскада, который реально дал вклад (-1: точка не попала ни в один и
// объявлена освещённой). Нужен отладочному каналу 26: «пятно тени» и «пятно от смены каскада»
// выглядят на экране одинаково, и без индекса их не разделить.
#if FEATURE_RT_SHADOWS
// Тень солнца ТЕНЕВЫМИ ЛУЧАМИ по TLAS - верх лестницы Shadow filtering: физическая полутень без
// каскадов, байасов и краевых текселей вовсе. Конус лучей раскрыт на угловой размер диска
// (Sun angular size), вращение диска - тем же IGN, что у PCSS. Ограничение против shadow map:
// альфа-тест листвы TLAS не видит (BLAS - сплошная геометрия), крона затеняет монолитом.
float SampleWorldLightShadowRT(float3 worldPos, float3 N, float2 pixelPos, float sunTanHalfAngle)
{
    float3 sunDir = normalize(lightData.LightDirection.xyz);

    // Базис поперёк направления на солнце - в нём раскрывается конус.
    float3 tangentSeed = abs(sunDir.y) < 0.99 ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0);
    float3 tangent1 = normalize(cross(tangentSeed, sunDir));
    float3 tangent2 = cross(sunDir, tangent1);

    float3 origin = worldPos + N * RT_SHADOW_NORMAL_OFFSET;
    float phi = InterleavedGradientNoise(pixelPos) * 6.2831853;

    float sum = 0.0;
    [loop]
    for (int r = 0; r < RT_SHADOW_RAYS; r++)
    {
        float2 disk = VogelDiskSample(r, RT_SHADOW_RAYS, phi) * sunTanHalfAngle;
        RayDesc ray;
        ray.Origin = origin;
        ray.Direction = normalize(sunDir + tangent1 * disk.x + tangent2 * disk.y);
        ray.TMin = 0.0;
        ray.TMax = RT_SHADOW_TMAX;

        // ACCEPT_FIRST_HIT - теневому лучу ближайшее попадание не нужно (зеркало SceneTrace.hlsl).
        RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH> query;
        query.TraceRayInline(_SceneTlas, RAY_FLAG_NONE, 0xFF, ray);
        query.Proceed();
        sum += query.CommittedStatus() == COMMITTED_TRIANGLE_HIT ? 0.0 : 1.0;
    }

    return sum / float(RT_SHADOW_RAYS);
}
#endif

float SampleWorldLightShadow(float3 worldPos, float3 N, float2 pixelPos, out float firstCascade)
{
    firstCascade = -1.0;

    const float texel = 1.0 / SUN_SHADOW_TEXELS;
    const float margin = SUN_CASCADE_MARGIN_TEXELS * texel;

    float sunTanHalfAngle = lightData.SpotAngles.w > 0.0
        ? lightData.SpotAngles.w
        : SUN_DEFAULT_TAN_HALF_ANGLE;

#if FEATURE_RT_SHADOWS
    // RT-режим минует каскады целиком; firstCascade остаётся -1 (отладочный канал 26 честно
    // покажет «каскад не выбран»). Punctual-света в RT-режиме остаются на PCSS - их ветка сама
    // падает в PCSS на любом незнакомом значении режима.
    if (PbrShadowMode == SHADOW_MODE_RT)
    {
        return SampleWorldLightShadowRT(worldPos, N, pixelPos, sunTanHalfAngle);
    }
#endif

    // Одно вращение диска на пиксель - блокер-серч и PCF всех каскадов крутятся согласованно,
    // иначе полутень на стыке каскадов шумит двумя несовпадающими паттернами.
    float phi = InterleavedGradientNoise(pixelPos) * 6.2831853;

    // Последний заполненный каскад: ему полосу перехода не даём - смешивать не с чем, а фейд в
    // «освещено» на его краю выглядел бы как срез тени в воздухе. Он же покрывает случай превью
    // одиночной модели, где каскад ровно один.
    int lastValid = -1;
    [unroll]
    for (int k = 0; k < 4; k++)
    {
        if (lightData.CascadeSizes[k] > 0.0)
        {
            lastValid = k;
        }
    }

    float shadow = 0.0;
    float acc = 0.0;

    // [loop] здесь и на всех PCSS-тапах ниже - СОЗНАТЕЛЬНО, ради времени КОМПИЛЯЦИИ: FXC (D3D12
    // компилирует модельные варианты им, см. DiligentShader) разворачивал 4 каскада x 32 тапа плюс
    // пробные циклы в простыню, и вариант стоил 7.5 с компиляции; с [loop] - 1.3 с, байткод втрое
    // меньше. Код итераций идентичен, меняется только развёртка.
    [loop]
    for (int c = 0; c < 4; c++)
    {
        if (acc >= 1.0)
        {
            continue;
        }

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
        float texelWorld = cascadeWorld / SUN_SHADOW_TEXELS;
        float3 samplePos = worldPos + N * texelWorld * 1.5;

        float4 lightClip = mul(float4(samplePos, 1.0), lightData.CascadeMatrix[c]);
        float3 lightNdc = lightClip.xyz / max(lightClip.w, 1e-6);
        float2 shadowUv = float2(lightNdc.x * 0.5 + 0.5, 0.5 - lightNdc.y * 0.5);

        if (lightNdc.z <= 0.0 || lightNdc.z >= 1.0)
        {
            // Точка вне глубинного диапазона этого каскада - пробуем следующий, крупнее.
            continue;
        }

        // Расстояние до края карты за вычетом отступа. Отрицательное - точка в кайме, где фильтр
        // вылез бы за карту: каскад не годится, идём на следующий.
        float edge = min(min(shadowUv.x, 1.0 - shadowUv.x),
                         min(shadowUv.y, 1.0 - shadowUv.y)) - margin;
        if (edge <= 0.0)
        {
            continue;
        }

        // Вес этого каскада: единица в глубине, плавно к нулю в полосе перехода у кромки. Остаток
        // веса доберёт следующий каскад - там та же точка лежит глубоко внутри.
        float w = (c == lastValid) ? 1.0 : saturate(edge / SUN_CASCADE_BLEND_UV);
        float take = min(w, 1.0 - acc);
        if (take <= 0.0)
        {
            continue;
        }

        // Небольшое смещение: shadow map пишется БЕЗ отсечения граней (см. ShadowRenderer.GetBaseState -
        // прежний front-cull делал одностороннюю геометрию прозрачной для света: планки крыши без
        // задних граней пропускали солнце полосами). Основную работу против acne делают растеризаторные
        // байасы записи (DepthBias + SlopeScaledDepthBias) и normal-offset выше; константа здесь -
        // добивка, крупнее нельзя (peter-panning у основания фигур). Глубинный диапазон у всех
        // каскадов одинаковый (см. BuildLightData), так что константа работает на любом уровне.
        float referenceDepth = lightNdc.z - 0.0004;
        float lit;

        // Режим фильтрации - юниформ-ветка (PbrShadowMode одинаков для всего кадра, дивергенции
        // нет), режимы по возрастанию цены: HARD / PCF / PCSS / PCSS_HQ.
        if (PbrShadowMode == SHADOW_MODE_HARD)
        {
            // Один аппаратный тап: билинейное сравнение даёт край в тексель шириной.
            lit = ShadowMaps.SampleCmpLevelZero(ShadowMaps_sampler,
                float3(shadowUv, (float)c), referenceDepth);
        }
        else if (PbrShadowMode == SHADOW_MODE_PCF)
        {
            // Фиксированный бокс 3x3 - прежний путь: постоянная мягкость в тексель, без полутени.
            float pcfSum = 0.0;
            [unroll]
            for (int y = -1; y <= 1; y++)
            {
                [unroll]
                for (int x = -1; x <= 1; x++)
                {
                    pcfSum += ShadowMaps.SampleCmpLevelZero(ShadowMaps_sampler,
                        float3(shadowUv + float2(x, y) * texel, (float)c), referenceDepth);
                }
            }

            lit = pcfSum / 9.0;
        }
        else
        {
        bool hq = PbrShadowMode == SHADOW_MODE_PCSS_HQ;
        int taps = hq ? SUN_PCSS_HQ_TAPS : SUN_PCSS_TAPS;

        // --- PCSS, шаг 1: поиск блокеров - средняя глубина текселей, заслоняющих точку, в диске
        // Фогеля. Load вместо сэмплера: нужна точечная выборка БЕЗ сравнения, а второго сэмплера у
        // ShadowMaps нет и не надо. Радиус диска (и поиска, и PCF ниже) ограничен запасом до кромки
        // каскада: edge уже за вычетом margin, так что тапы не адресуются за карту, а пиксель у
        // кромки всё равно доберёт вес из следующего каскада (полоса перехода шире любого радиуса).
        float maxRadiusTexels = min(hq ? SUN_PCSS_HQ_MAX_PENUMBRA_TEXELS : SUN_PCSS_MAX_PENUMBRA_TEXELS,
            edge * SUN_SHADOW_TEXELS);
        float searchRadius = min(hq ? SUN_PCSS_HQ_SEARCH_TEXELS : SUN_PCSS_SEARCH_TEXELS,
            maxRadiusTexels);

        float avgBlocker = 0.0;
        float blockerCount = 0.0;
        [loop] // время компиляции FXC - см. комментарий у каскадного цикла
        for (int b = 0; b < taps; b++)
        {
            float2 searchUv = shadowUv + VogelDiskSample(b, taps, phi) * searchRadius * texel;
            float d = ShadowMaps.Load(int4(int2(searchUv * SUN_SHADOW_TEXELS), c, 0)).r;
            if (d < referenceDepth)
            {
                avgBlocker += d;
                blockerCount += 1.0;
            }
        }

        // Шаг 2: ширина полутени. Для направленного света это расстояние до блокера, умноженное на
        // тангенс полуугла диска солнца; глубина ndc переводится в мировую через диапазон каскада
        // (SUN_CASCADE_DEPTH_RANGE_RATIO). Без блокеров радиус остаётся минимальным - тексель:
        // полностью освещённые/затенённые области получают дешёвое сглаживание края вместо
        // жёсткого бокса 3x3.
        float filterRadius = 1.0;
        if (blockerCount > 0.0)
        {
            avgBlocker /= blockerCount;
            float blockerDistWorld = (referenceDepth - avgBlocker) * cascadeWorld * SUN_CASCADE_DEPTH_RANGE_RATIO;
            filterRadius = clamp(blockerDistWorld * sunTanHalfAngle / texelWorld, 1.0, maxRadiusTexels);
        }

        // Шаг 3: PCF по тому же диску, повёрнутому на полоборота, - тапы не совпадают с тапами
        // поиска, суммарный паттерн на пиксель вдвое плотнее.
        float sum = 0.0;
        [loop] // время компиляции FXC - см. комментарий у каскадного цикла
        for (int t = 0; t < taps; t++)
        {
            float2 tapUv = shadowUv + VogelDiskSample(t, taps, phi + 3.1415926) * filterRadius * texel;
            sum += ShadowMaps.SampleCmpLevelZero(ShadowMaps_sampler,
                float3(tapUv, (float)c), referenceDepth);
        }

        lit = sum / float(taps);
        }

        if (firstCascade < 0.0)
        {
            firstCascade = (float)c;
        }

        shadow += lit * take;
        acc += take;
    }

    // Недобранный вес - освещён: за пределами всех каскадов тени нет. Раньше здесь стоял ранний
    // выход с 1.0, и «за пределами» наступало разом; теперь край последнего каскада уходит в свет
    // тем же плавным весом, что и стыки между каскадами.
    return shadow + (1.0 - acc);
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
#define PROBE_GI_ORIGIN  ProbeGridOrigin
#define PROBE_GI_CELL    ProbeGridCell
#define PROBE_GI_COUNTS  ProbeGridCounts
#define PROBE_GI_SCROLL  ProbeGridScroll
#include "ProbeGiSampleBody.hlsl"
#undef PROBE_GI_FN
#undef PROBE_GI_SH0
#undef PROBE_GI_SH1
#undef PROBE_GI_SH2
#undef PROBE_GI_SH3
#undef PROBE_GI_VIS
#undef PROBE_GI_OFFSET
#undef PROBE_GI_ORIGIN
#undef PROBE_GI_CELL
#undef PROBE_GI_COUNTS
#undef PROBE_GI_SCROLL

#define PROBE_GI_FN      SampleProbeGiC1
#define PROBE_GI_SH0     _ProbeSh0_C1
#define PROBE_GI_SH1     _ProbeSh1_C1
#define PROBE_GI_SH2     _ProbeSh2_C1
#define PROBE_GI_SH3     _ProbeSh3_C1
#define PROBE_GI_VIS     _ProbeVis_C1
#define PROBE_GI_OFFSET  _ProbeOffset_C1
#define PROBE_GI_ORIGIN  ProbeGridOrigin1
#define PROBE_GI_CELL    ProbeGridCell1
#define PROBE_GI_COUNTS  ProbeGridCounts1
#define PROBE_GI_SCROLL  ProbeGridScroll1
#include "ProbeGiSampleBody.hlsl"
#undef PROBE_GI_FN
#undef PROBE_GI_SH0
#undef PROBE_GI_SH1
#undef PROBE_GI_SH2
#undef PROBE_GI_SH3
#undef PROBE_GI_VIS
#undef PROBE_GI_OFFSET
#undef PROBE_GI_ORIGIN
#undef PROBE_GI_CELL
#undef PROBE_GI_COUNTS
#undef PROBE_GI_SCROLL

#define PROBE_GI_FN      SampleProbeGiC2
#define PROBE_GI_SH0     _ProbeSh0_C2
#define PROBE_GI_SH1     _ProbeSh1_C2
#define PROBE_GI_SH2     _ProbeSh2_C2
#define PROBE_GI_SH3     _ProbeSh3_C2
#define PROBE_GI_VIS     _ProbeVis_C2
#define PROBE_GI_OFFSET  _ProbeOffset_C2
#define PROBE_GI_ORIGIN  ProbeGridOrigin2
#define PROBE_GI_CELL    ProbeGridCell2
#define PROBE_GI_COUNTS  ProbeGridCounts2
#define PROBE_GI_SCROLL  ProbeGridScroll2
#include "ProbeGiSampleBody.hlsl"
#undef PROBE_GI_FN
#undef PROBE_GI_SH0
#undef PROBE_GI_SH1
#undef PROBE_GI_SH2
#undef PROBE_GI_SH3
#undef PROBE_GI_VIS
#undef PROBE_GI_OFFSET
#undef PROBE_GI_ORIGIN
#undef PROBE_GI_CELL
#undef PROBE_GI_COUNTS
#undef PROBE_GI_SCROLL

// Плавный вес объёма: ноль у грани бокса (и снаружи), единица - не ближе КИРПИЧА от грани. Прямой
// аналог margin теневых каскадов (Shadows.hlsl, GetDistanceToCascadeMargin): выборка не берёт
// каскад у самого края, а в полосе перед ним уходит на следующий, более крупный объём.
//
// Лечит сразу три видимые беды стыков:
//   1) тело выборки КЛАМПИТ точку к боксу - без веса пиксель ВНЕ мелкого каскада не проваливался
//      на крупный, а получал растянутые крайние кирпичи мелкого;
//   2) даже честная граница переключала разрешение поля жёстко, швом;
// Отступ 0.5 шага держит выборку подальше от кламп-зоны самой грани.
//
// Ширина - два шага сетки, а не кирпич. Кирпич (три шага) здесь стоял недолго и по другому поводу:
// им гасилась рябь только что въехавших кирпичей, которые прокрутка приводит с краю. Но платить за
// это геометрией неправильно - полоса в кирпич съедала у каскада 13 ячеек по оси больше половины
// полного веса, то есть ровно ту плотность, ради которой каскад и заведён. Свежестью и надо гасить
// свежесть: теперь въехавший кирпич проявляется сам, по своему окну (см. ProbeGiSampleBody,
// confidence), где бы он ни лежал, а этой полосе остаётся её собственная работа - стык объёмов.
#define PROBE_CASCADE_MARGIN_CELLS 2.0

float ProbeCascadeWeight(float3 worldPos, float4 origin, float4 cell, float4 counts)
{
    float3 f = (worldPos - origin.xyz) / cell.xyz;
    float3 hi = counts.xyz - 1.0;
    float d = min(min(f.x, hi.x - f.x), min(min(f.y, hi.y - f.y), min(f.z, hi.z - f.z)));
    return saturate((d - 0.5) / (PROBE_CASCADE_MARGIN_CELLS - 0.5));
}

// Каскадная выборка: база - гарантия покрытия, мелкие объёмы подмешиваются ПОВЕРХ с весом своего
// бокса. Плата за плавный стык - в зоне каскада выборок больше одной (до трёх в самом мелком);
// это ровно те места, ради которых каскады и заведены, так что цена по адресу.
// probeCoverage - НАСКОЛЬКО результату можно верить как замене константного ambient, 0..1.
// Заведён из-за свежих кирпичей. Прокрутка каскада приводит их непрерывно, пока летит камера, и в
// местах, где базовый объём дыряв (он разрежённый - кирпичи есть только у геометрии), свежий кирпич
// оказывается ЕДИНСТВЕННЫМ источником. Смешивать его по уверенности с нулём, как делалось раньше,
// значит проявлять освещение из черноты: на резкое движение камеры перед ней шли тёмные
// прямоугольники размером с кирпич. Смешивать в полную силу - другая крайность, дававшая яркие
// вспышки. Правильно - смешивать с тем, что стоит под полем: с константным ambient, а он считается
// в Main. Поэтому наружу отдаётся ВЕС, а сам размен делает вызывающий.
float3 SampleProbeGi(float3 worldPos, float3 N, out float skyVisibility, out float sunFraction,
                     out float3 probeMarker, out float probeCoverage)
{
    probeCoverage = 1.0;
    // Уверенность базового объёма не читается: он не ездит, его кирпичи не бывают свежими, и
    // гасить последний рубеж покрытия всё равно нечем - под ним только константный ambient.
    float conf0;
    float3 result = SampleProbeGiC0(worldPos, N, skyVisibility, sunFraction, probeMarker, conf0);

    if (ProbeGridOrigin1.w > 0.5)
    {
        float w = ProbeCascadeWeight(worldPos, ProbeGridOrigin1, ProbeGridCell1, ProbeGridCounts1);
        if (w > 0.0)
        {
            float sky1, sun1, conf1;
            float3 marker1;
            float3 mid = SampleProbeGiC1(worldPos, N, sky1, sun1, marker1, conf1);

            // Вес каскада гасится уверенностью кирпича: только что въехавший проявляется за свои
            // раунды, а не вспыхивает готовым (см. ProbeGiSampleBody).
            w *= conf1;
            if (mid.x >= 0.0 && w > 0.0)
            {
                // Базе нечем крыть (дыра без кирпича - базовый объём разрежённый, кирпичи есть
                // только у геометрии). Тогда подмешивать не к чему, и вес каскада по БОКСУ здесь
                // ни при чём: гасить единственный источник за отсутствие второго бессмысленно.
                //
                // Уверенность объёма при этом остаётся в силе и НЕ затирается единицей - строка
                // когда-то ставила w = 1.0 поверх уже домноженного на conf1 веса, и свежий кирпич
                // вспыхивал в полную силу с полем от одного-двух вееров. У плотной сетки conf1
                // всегда 1 (см. ProbeGiSampleBody), так что сегодня это ничего не меняет; порядок
                // сохранён нарочно - механизм плавного проявления может понадобиться снова, и
                // возвращать его придётся ровно сюда.
                // Базе нечем крыть - значение берём ЦЕЛИКОМ, а неуверенность отдаём наружу весом.
                // Именно значение, а не его долю: поле свежего кирпича шумно, но по величине это
                // уже освещение, а не ноль, и тянуть его к нулю - выдумывать темноту, которой нет.
                if (result.x < 0.0)
                {
                    probeCoverage = conf1;
                    result = mid;
                }
                else
                {
                    result = lerp(max(result, 0.0), mid, w);
                }
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
            float sky2, sun2, conf2;
            float3 marker2;
            float3 fine = SampleProbeGiC2(worldPos, N, sky2, sun2, marker2, conf2);
            w *= conf2;
            if (fine.x >= 0.0 && w > 0.0)
            {
                // То же, что у каскада выше: дыра в предыдущих объёмах снимает вес по боксу, но не
                // уверенность кирпича.
                if (result.x < 0.0)
                {
                    probeCoverage = conf2;
                    result = fine;
                }
                else
                {
                    result = lerp(max(result, 0.0), fine, w);
                }
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
#if FEATURE_REFLECTION_GBUFFER
    // Тонкий G-buffer отражений (см. SsrPass / PipelineRenderTargets.NormalRoughnessTarget):
    // RT1 - нормаль ШЕЙДИНГА в мире (после нормал-мапы и two-sided флипа) + perceptual roughness;
    // RT2 - множитель env-спекуляра БЕЗ окклюзии неба (Fr * specularWeight * occlusion, rgb) +
    // сама envOcclusion в альфе. SSR ЗАМЕНЯЕТ префильтрованное окружение трейсом:
    // hdr += conf * factor * (ssr - envOcclusion * envColor) - что вычитается, ровно то форвард
    // и сложил, а трейс окклюзией неба не глушится (см. комментарий у записи ниже).
    float4 gbNormalRough : SV_TARGET1;
    float4 gbEnvFactor : SV_TARGET2;
#endif
};

PSOutput Main(in PSInput input)
{
    PSOutput output;
#if FEATURE_REFLECTION_GBUFFER
    // Нули по умолчанию: ранние return-ы (режимы превью, отладочные каналы) оставляют пиксель
    // невидимым для SSR-композита (w = 0), как и очистка таргета в ForwardPass.
    output.gbNormalRough = float4(0.0, 0.0, 0.0, 1.0);
    output.gbEnvFactor = float4(0.0, 0.0, 0.0, 0.0);
#endif

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

                // Z ВОССТАНАВЛИВАЕТСЯ из XY, а не читается из текстуры. Запечённые карты нормалей
                // ассет-пайплайна лежат в BC5 (см. TextureImportSettings.AutoFor), а он хранит ровно
                // два канала - третий приходит из сэмплера нулём. Для тангенциальной нормали это не
                // потеря: она всегда смотрит наружу поверхности, то есть z однозначно выводится из
                // длины. Незапечённым RGBA8-картам и плоскому 1x1-филлеру (128,128,255 -> xy≈0,
                // z≈1) реконструкция даёт то же самое, что дало бы чтение .z, поэтому ветвление по
                // формату не нужно.
                float2 mappedXY = _NormalTex.Sample(_NormalTex_sampler, uv).xy * 2.0 - 1.0;
                float mappedZ = sqrt(saturate(1.0 - dot(mappedXY, mappedXY)));
                mappedXY *= PbrNormalScale;
                N = normalize(mappedXY.x * T + mappedXY.y * B + mappedZ * N);
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

        // Индекс каскада солнца, выбранного этим пикселем (-1 - каскад не выбран). Только для
        // отладочного канала 26.
        float dbgSunCascade = -1.0;

#if FEATURE_SHADOWS
        hasWorldLight = (PbrFeatureFlags & FeatureShadows)
            && dot(lightData.LightDirection.xyz, lightData.LightDirection.xyz) > 1e-4;

        if (hasWorldLight)
        {
            // Конвенция ОСНОВНОГО пайплайна (см. CullingAndRenderSystem): LightData.LightDirection
            // указывает НА солнце - SimpleCullingAndRenderSystem теперь пишет так же.
            keyDir = normalize(lightData.LightDirection.xyz);
            keyShadow = SampleWorldLightShadow(input.worldPos, N, input.pos.xy, dbgSunCascade);

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
        // Тот же временный хук - какой именно ИНДЕКС слайса (0..LightClusters.MaxShadowSlices-1)
        // шейдер в итоге выбрал для сэмплинга (база из ShadowParams.x + смещение грани куба у
        // точечного света). -1 = ветка не дошла до вычисления слайса. Отдельно от dbgPunctual.w
        // (результат сравнения глубины): белый/лит пиксель сам по себе не отличает "слайс без
        // окклюдера в кадре" от "слайс вообще пустой/чужой" - индекс тут решает спор (см. канал 12).
        float dbgShadowSlice = -1;
        float dbgShadowBase = -1; // ВРЕМЕННЫЙ: punctual.ShadowParams.x ДО добавления смещения грани куба.
        float3 punctualLightPosDbg = 0; // ВРЕМЕННЫЙ: позиция света, которой соответствует dbgPunctual/dbgShadowSlice.
        float dbgClusterRawCount = -1; // ВРЕМЕННЫЙ: ClusterCounts[clusterIdx] сырьём, до клампа CLUSTER_MAX_LIGHTS.
        // ВРЕМЕННЫЙ для каналов 22..24: x - глубина ПРИЁМНИКА (этой поверхности) в системе координат
        // света, y - глубина ОККЛЮДЕРА, лежащая в слайсе по тому же UV, z - far слайса (масштаб
        // рампы). Обе в МИРОВЫХ единицах вдоль оси слайса. -1 = сэмплинг тени сюда не дошёл.
        float3 dbgShadowDepth = -1;
        float dbgShadowBiasWorld = 0; // ВРЕМЕННЫЙ: мировой байас этого пикселя - масштаб канала 24.
        float dbgShadowClipW = -1e9; // ВРЕМЕННЫЙ: shadowClip.w ДО guard'а, для канала 25.
        float3 dbgSliceAxis = 1e9;    // ВРЕМЕННЫЙ: ось грани из СТОЛБЦА матрицы слайса, для канала 26.
        float3 dbgSliceAxisRow = 1e9; // ВРЕМЕННЫЙ: то же из СТРОКИ, для канала 27.
        // ВРЕМЕННЫЙ: координаты фроксела этого пикселя (x/y - тайл экрана, z - экспоненциальный срез
        // глубины) для канала 20. -1 = сетка не определена (ClusterParams.zw пустые - превью-конвейер).
        float3 dbgClusterCell = -1;

        // ----- Clustered punctual-света (point/spot) -------------------------------------------
        // Пиксель находит свой фроксел-кластер (тайл экрана + экспоненциальный срез по view-z,
        // обязано зеркалить прямое отображение в LightClusterCS.hlsl) и шейдит только света его
        // кластера. ClusterParams.y == 0 - punctual-светов у камеры нет, шейдинг мёртвый, но САМО
        // отображение пиксель->фроксел считается всё равно: канал 20 обязан показывать сетку и на
        // сцене без единого punctual-света (иначе "кластеры не работают" неотличимо от "светов нет").
        uint punctualCount = (uint)lightData.ClusterParams.y;
        float clusterZNear = lightData.ClusterParams.z;
        float clusterZFar = lightData.ClusterParams.w;
        bool clusterGridValid = clusterZFar > clusterZNear && clusterZNear > 0.0;

        uint tileX = 0, tileY = 0, tileZ = 0;
        // Условие однородно по кадру во второй половине (PreviewChannel - константа кбуфера), так что
        // без светов на сцене обычный проход не платит за отладочный mul вовсе.
        if (clusterGridValid && (punctualCount > 0 || PreviewChannel == 20 || PreviewChannel == 21))
        {
            float clusterViewZ = mul(float4(input.worldPos, 1.0), viewData.view).z;

            float2 clusterUv = input.pos.xy / viewData.viewport.zw;
            tileX = min((uint)(clusterUv.x * CLUSTER_GRID_X), CLUSTER_GRID_X - 1);
            tileY = min((uint)(clusterUv.y * CLUSTER_GRID_Y), CLUSTER_GRID_Y - 1);
            float clusterSlice = log2(max(clusterViewZ, clusterZNear) / clusterZNear)
                               / log2(clusterZFar / clusterZNear) * CLUSTER_GRID_Z;
            tileZ = (uint)clamp(clusterSlice, 0.0, CLUSTER_GRID_Z - 1.0);
            dbgClusterCell = float3(tileX, tileY, tileZ);
        }

        if (punctualCount > 0 && clusterGridValid)
        {
            uint clusterIdx = ClusterFlatIndex(uint3(tileX, tileY, tileZ));
            uint clusterLightCount = min(ClusterCounts[clusterIdx], CLUSTER_MAX_LIGHTS);
            // ВРЕМЕННЫЙ: сколько записей реально в ClusterCounts у этого пикселя ДО клампа - если
            // >1 при единственном свете в сцене, кластеризация дублирует/мусорит индексы, а
            // >CLUSTER_MAX_LIGHTS значит, что кластер переполнен и хвост светов молча потерян.
            dbgClusterRawCount = (float)ClusterCounts[clusterIdx];

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
                    dbgShadowBase = punctual.ShadowParams.x;
                    punctualLightPosDbg = punctual.PositionRange.xyz;
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
                    dbgShadowSlice = (float)shadowSlice;

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
                    float shadowTexelWorld = 2.0 * shadowTanHalfFov * punctualDist / PUNCTUAL_SHADOW_MAP_SIZE;
                    float3 shadowSamplePos = input.worldPos + N * shadowTexelWorld * 1.5;

                    float4x4 shadowMatrix = LoadPunctualShadowMatrix(shadowSlice);
                    float4 shadowClip = mul(float4(shadowSamplePos, 1.0), shadowMatrix);
                    // ВРЕМЕННЫЙ замер для канала 25 - w ДО guard'а ниже. Отдельно от dbgShadowDepth.x
                    // (тот пишется уже ВНУТРИ guard'а и про отказавшие пиксели молчит), а вопрос
                    // именно в них: w = проекция вектора свет->фрагмент на ось выбранной грани, и по
                    // построению выбора грани она обязана быть положительной. Отрицательная w - это
                    // либо матрица не той грани, либо позиция света в матрице не та, что в шейдинге.
                    dbgShadowClipW = shadowClip.w;
                    dbgShadowDepth.z = punctual.PositionRange.w; // far слайса - масштаб рампы канала 25
                    // ВРЕМЕННЫЙ для канала 26: w-столбец матрицы слайса, как её видит ШЕЙДЕР. Это
                    // ось грани в мире (у корректной матрицы обязана совпасть с FaceDirs[грань]).
                    // Сверяется с дампом PunctualShadowScheduler (sliceAxis) - расхождение CPU/GPU
                    // означает, что до шейдера доезжает не та матрица, а не что выбор грани врёт.
                    dbgSliceAxis = float3(shadowMatrix._m03, shadowMatrix._m13, shadowMatrix._m23);
                    // Та же ось, но из СТРОКИ - канал 27. Держится регресс-тестом раскладки: у
                    // правильно собранной матрицы ось грани лежит в СТОЛБЦЕ, поэтому канал 26 обязан
                    // совпадать с выбранной гранью (канал 19), а канал 27 - НЕТ. Если они поменяются
                    // местами, значит раскладка строк снова разъехалась (см. комментарий у
                    // LoadPunctualShadowMatrix) - на обоих бэкендах это видно одним прогоном.
                    dbgSliceAxisRow = float3(shadowMatrix._m30, shadowMatrix._m31, shadowMatrix._m32);
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
                            // в МИРОВЫХ единицах (в масштабе текселя слайса, см. ниже) и
                            // переводится в NDC локальной производной d(ndc)/dz = n*f/((f-n)*z^2) в точке
                            // приёмника (z = shadowClip.w - view-space глубина вдоль оси света).
                            // near берётся ГОТОВЫМ из ShadowParams.z (PunctualShadowScheduler.
                            // SliceNearPlane - то же число, что ушло в проекцию слайса). Своей копии
                            // формулы здесь больше нет: прошлая - max(0.05, range*0.001) - не знала
                            // про потолок 0.25, добавленный на стороне планировщика, и на дальнобойной
                            // лампе (Range 20000) считала near = 20 вместо 0.25, завышая производную
                            // d(ndc)/dz и с ней депф-байас на два порядка.
                            float shadowFar = punctual.PositionRange.w;
                            float shadowNear = max(punctual.ShadowParams.z, 1e-4);
                            float shadowZ = max(shadowClip.w, shadowNear);
                            // Мировая величина байаса задаётся В ТЕКСЕЛЯХ СЛАЙСА, а не константой в
                            // метрах. Прежняя пара (склон 0.05*(1-N.L) с полом 0.005) не знала о
                            // разрешении и дальности: у перспективного слайса тексель растёт линейно с
                            // дистанцией (shadowTexelWorld выше), и на замеренном кадре - лампа far=6.4
                            // на высоте 5.15 над полом - тексель на полу выходит ~0.010 мировых единиц,
                            // то есть ВДВОЕ больше, чем весь байас в этой точке (N.L=1 -> пол 0.005).
                            // Байас меньше кванта растра акне не давит по определению, а под косыми
                            // углами перепад глубины ВНУТРИ одного текселя ещё и умножается на tan угла
                            // падения - отсюда слагаемое с тангенсом (кламп 4.0 держит скользящие углы
                            // от ухода байаса в бесконечность, дальше работает peter-panning).
                            float shadowNdotL = saturate(dot(N, punctualL));
                            float shadowTanTheta = sqrt(saturate(1.0 - shadowNdotL * shadowNdotL))
                                / max(shadowNdotL, 0.15);
                            float shadowWorldBias = shadowTexelWorld * (1.0 + 2.0 * min(shadowTanTheta, 4.0));
                            float shadowNdcPerWorld = shadowNear * shadowFar
                                / max((shadowFar - shadowNear) * shadowZ * shadowZ, 1e-6);
                            float shadowBias = shadowWorldBias * shadowNdcPerWorld;

                            // PCSS - как у каскадов солнца (см. SampleWorldLightShadow), но слайс
                            // перспективный: глубины NDC нелинейны, поэтому и средний блокер, и
                            // ширина полутени считаются в МИРОВЫХ метрах вдоль оси слайса через
                            // инверсию проекции z = n*f / (f - ndc*(f-n)). Выход за грань куба
                            // держит потолок PUNCTUAL_PCSS_MAX_PENUMBRA_TEXELS (перехлёст граней
                            // ~20 текселей, см. PunctualShadowScheduler) плюс Clamp-адресация.
                            const float punctualTexel = 1.0 / PUNCTUAL_SHADOW_MAP_SIZE;
                            float shadowSum;
                            float shadowTapCount;

                            // Тот же режим фильтрации, что у солнца (PbrShadowMode, юниформ-ветка).
                            if (PbrShadowMode == SHADOW_MODE_HARD)
                            {
                                shadowSum = PunctualShadowMaps.SampleCmpLevelZero(
                                    PunctualShadowMaps_sampler,
                                    float3(shadowUv, shadowSlice),
                                    shadowNdc.z - shadowBias);
                                shadowTapCount = 1.0;
                            }
                            else if (PbrShadowMode == SHADOW_MODE_PCF)
                            {
                                // Фиксированный бокс 3x3 - прежний путь punctual-теней.
                                shadowSum = 0.0;
                                shadowTapCount = 9.0;
                                [unroll]
                                for (int sy = -1; sy <= 1; sy++)
                                {
                                    [unroll]
                                    for (int sx = -1; sx <= 1; sx++)
                                    {
                                        shadowSum += PunctualShadowMaps.SampleCmpLevelZero(
                                            PunctualShadowMaps_sampler,
                                            float3(shadowUv + float2(sx, sy) * punctualTexel, shadowSlice),
                                            shadowNdc.z - shadowBias);
                                    }
                                }
                            }
                            else
                            {
                            bool punctualHq = PbrShadowMode == SHADOW_MODE_PCSS_HQ;
                            int punctualTaps = punctualHq ? SUN_PCSS_HQ_TAPS : SUN_PCSS_TAPS;
                            float punctualPhi = InterleavedGradientNoise(input.pos.xy) * 6.2831853;

                            // Шаг 1: средний блокер по диску Фогеля (Load - точечный тап без
                            // сравнения, второй сэмплер не нужен).
                            float avgBlockerNdc = 0.0;
                            float blockerCount = 0.0;
                            [loop] // время компиляции FXC - см. комментарий у каскадного цикла солнца
                            for (int pb = 0; pb < punctualTaps; pb++)
                            {
                                float2 sUv = shadowUv
                                    + VogelDiskSample(pb, punctualTaps, punctualPhi) * PUNCTUAL_PCSS_SEARCH_TEXELS * punctualTexel;
                                int2 sTexel = clamp(int2(sUv * PUNCTUAL_SHADOW_MAP_SIZE),
                                    0, (int)PUNCTUAL_SHADOW_MAP_SIZE - 1);
                                float d = PunctualShadowMaps.Load(int4(sTexel, shadowSlice, 0)).r;
                                if (d < shadowNdc.z - shadowBias)
                                {
                                    avgBlockerNdc += d;
                                    blockerCount += 1.0;
                                }
                            }

                            // Шаг 2: ширина полутени = (глубина приёмника - глубина блокера) *
                            // радиус тела света / глубина блокера (подобие треугольников источник-
                            // блокер-приёмник), в текселях слайса НА ГЛУБИНЕ ПРИЁМНИКА.
                            float sourceRadius = punctual.ShadowParams.w > 0.0
                                ? punctual.ShadowParams.w
                                : PUNCTUAL_DEFAULT_SOURCE_RADIUS;
                            float filterTexels = 1.0;
                            if (blockerCount > 0.0)
                            {
                                avgBlockerNdc /= blockerCount;
                                float blockerZ = shadowNear * shadowFar
                                    / max(shadowFar - avgBlockerNdc * (shadowFar - shadowNear), 1e-6);
                                float penumbraWorld = max(shadowZ - blockerZ, 0.0) * sourceRadius
                                    / max(blockerZ, shadowNear);
                                float texelAtReceiver = 2.0 * shadowTanHalfFov * shadowZ
                                    / PUNCTUAL_SHADOW_MAP_SIZE;
                                filterTexels = clamp(penumbraWorld / max(texelAtReceiver, 1e-6),
                                    1.0, PUNCTUAL_PCSS_MAX_PENUMBRA_TEXELS);
                            }

                            // Шаг 3: PCF по диску, повёрнутому на полоборота от диска поиска.
                            shadowSum = 0.0;
                            shadowTapCount = (float)punctualTaps;
                            [loop] // время компиляции FXC - см. комментарий у каскадного цикла солнца
                            for (int pt = 0; pt < punctualTaps; pt++)
                            {
                                float2 tapUv = shadowUv
                                    + VogelDiskSample(pt, punctualTaps, punctualPhi + 3.1415926) * filterTexels * punctualTexel;
                                shadowSum += PunctualShadowMaps.SampleCmpLevelZero(
                                    PunctualShadowMaps_sampler,
                                    float3(tapUv, shadowSlice),
                                    shadowNdc.z - shadowBias);
                            }
                            }

                            // ВРЕМЕННЫЙ замер для каналов 22..24 - ДВЕ глубины в системе координат
                            // света, обе в МИРОВЫХ единицах вдоль оси слайса:
                            //   x - глубина ПРИЁМНИКА (этой поверхности), она же shadowClip.w;
                            //   y - глубина ОККЛЮДЕРА, реально лежащая в слайсе по тому же UV.
                            // Вторая берётся Load'ом (точечный тап центрального тексела): у текстуры
                            // сравнивающий сэмплер, обычный Sample с ним невалиден, а Load сэмплера не
                            // требует вовсе. Обратное преобразование перспективной NDC-глубины в
                            // view-z - ровно инверсия проекции слайса (PunctualShadowScheduler.AddSlice,
                            // CreatePerspectiveFieldOfViewLeftHanded): ndc = f/(f-n) - n*f/((f-n)*z)
                            // => z = n*f / (f - ndc*(f-n)). Именно поэтому сравнивать надо ЗДЕСЬ, в
                            // метрах: в NDC обе глубины у дальней плоскости слипаются в неразличимые
                            // тысячные доли и по картинке о зазоре приёмник/окклюдер сказать нечего.
                            uint2 dbgShadowTexel = (uint2)clamp(shadowUv * PUNCTUAL_SHADOW_MAP_SIZE,
                                0.0, PUNCTUAL_SHADOW_MAP_SIZE - 1.0);
                            float dbgOccluderNdc = PunctualShadowMaps.Load(
                                int4(dbgShadowTexel, shadowSlice, 0)).r;
                            float dbgOccluderZ = shadowNear * shadowFar
                                / max(shadowFar - dbgOccluderNdc * (shadowFar - shadowNear), 1e-6);
                            dbgShadowDepth = float3(shadowClip.w, dbgOccluderZ, shadowFar);
                            // Мировой байас этого пикселя - масштаб для канала 24: разница
                            // приёмник/окклюдер осмысленна только В СРАВНЕНИИ с ним (меньше байаса -
                            // пиксель считается освещённым, больше - уходит в тень).
                            dbgShadowBiasWorld = shadowWorldBias;

                            float shadowLit = shadowSum / shadowTapCount;
                            dbgPunctual.w = shadowLit;
                            punctualAtten *= lerp(1.0, shadowLit, saturate(punctual.ShadowParams.y));
                        }
                    }
                }

                float3 punctualRadiance = punctual.ColorIntensity.rgb * punctual.ColorIntensity.w * punctualAtten;
                float3 punctualContrib = ShadePbrLight(N, V, punctualL, punctualRadiance,
                    albedo, metallic, roughness, transmission, dielectricF0, specularWeight);

#if MATERIAL_SHEEN
                // Ворс - как у ключа/заполняющего выше: базовый отклик глушится альбедо лоба
                // (энергосохранение), сверху Charlie-лоб этого света. Без этого ткань под лампой
                // теряла сатиновый блик, который в солнечном свете есть.
                punctualContrib = punctualContrib * sheenScaling
                    + ShadeSheenLight(N, V, punctualL, punctualRadiance, sheenColor, sheenRoughness);
#endif

                direct += punctualContrib;
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
        // Доля, в которой поле проб заменяет константный ambient - см. SampleProbeGi.
        float probeCoverage = 1.0;
        bool probeGi = ProbeGridOrigin.w > 0.5;
        float3 probeIrradiance = 0.0;
        if (probeGi)
        {
            probeIrradiance = SampleProbeGi(input.worldPos, N, skyVisibility, probeSunFraction,
                                            probeMarker, probeCoverage);
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
        // Запасной ambient считается ВСЕГДА: он нужен не только там, где поля нет совсем, но и
        // там, где оно есть, да не в полную силу - свежий кирпич проявляется из него, а не из
        // черноты (см. probeCoverage в SampleProbeGi). Лишний SampleEnvironment на пиксель - цена
        // за то, что перед летящей камерой больше не идут прямоугольники размером с кирпич.
        float3 envAmbient =
            SampleEnvironment(N, 1.0) * ambientLevel * albedo * (1.0 - metallic) * occlusion * envShadow;
        float3 ambient = probeGi
            ? lerp(envAmbient,
                   probeIrradiance * probeBoost * albedo * (1.0 - metallic) * occlusion * probeShadow,
                   saturate(probeCoverage))
            : envAmbient;

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

#if FEATURE_REFLECTION_GBUFFER
        {
            // Множитель БЕЗ envOcclusion: окклюзия неба (запечённая видимость probe GI /
            // аппроксимация envShadow) гасит только ПРЕФИЛЬТРОВАННУЮ карту - интерьеру нечего
            // зеркалить из зенита, - но SSR-трейс отражает реальную экранную геометрию, и глушить
            // его этой окклюзией значило убивать отражения именно там, где они нужнее всего
            // (зеркало в интерьере). envOcclusion уезжает отдельно в альфу: композит вычитает
            // ровно тот env-вклад, что сложил форвард (factor * envOcclusion * envColor), а
            // трейс подмешивает без неё (factor * ssr).
            float3 gbFactor = Fr * lerp(specularWeight, 1.0, metallic) * occlusion;
#if MATERIAL_SHEEN
            gbFactor *= sheenScaling;
#endif
            output.gbNormalRough = float4(N, roughness);
            output.gbEnvFactor = float4(gbFactor, envOcclusion);
        }
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
                // -1: ветка не выполнилась вовсе. Порог именно -0.5, а НЕ -1.5: дефолт dbgPunctual.w
                // равен -1 и условию "< -1.5" не удовлетворял никогда, так что случай "света сюда не
                // дошло" молча проваливался в серую ветку и рисовался чёрным - неотличимо от глухой
                // тени. Значение -2 пишется только транзитом (перед сэмплером) и тут же затирается
                // его результатом, так что магента не появлялась вообще ни при каких условиях.
                : dbgPunctual.w < -0.5 ? float3(1, 0, 1)
                // Сэмплировано: тень .. свет, но поднято с 0 до 0.15 - ЧИСТО ЧЁРНЫЙ обязан остаться
                // только у фона. Пиксельный шейдер на пикселях без геометрии не запускается вовсе, и
                // они держат цвет очистки таргета; при отображении тени нулём небо за силуэтом стены
                // и затенённая стена были В КАНАЛЕ НЕОТЛИЧИМЫ, из-за чего "тень залила весь кадр"
                // читалось там, где полкадра просто фон.
                : lerp(0.15, 1.0, saturate(dbgPunctual.w)).xxx;
            output.color = float4(dbgColor, 1.0);
            return output;
        }
        // ВРЕМЕННЫЙ отладочный канал 12 - какой ИНДЕКС слайса шейдер выбрал для сэмплинга этого
        // пикселя (dbgShadowSlice, см. выше), закодирован как 8-бит серый = slice * 16 (0 слайс -
        // почти чёрный, 15 слайс - почти белый; шаг между соседями хорошо различим на глаз/в ридбеке).
        // Магента (1,0,1) - ветка не выполнилась вовсе (нет назначенного слайса/вне радиуса), та же
        // семантика, что магента в канале 11. Отвечает на вопрос "слайс N дал geometry в SHADOWDUMP,
        // а слайс, который РЕАЛЬНО сэмплит этот пиксель, - тот же N или другой?" - белый/"лит" пиксель
        // из канала 11 сам по себе не отличает "в кадре этого слайса нет окклюдера" от "слайс вообще
        // не тот" (расхождение записи/сэмплинга индекса выглядело бы ИДЕНТИЧНО - см. заметку задачи
        // про Depth-only Pass #5 vs PunctualShadowMaps почти белым на forward-дроу).
        if (PreviewChannel == 12)
        {
            float3 dbgColor = dbgShadowSlice < -0.5
                ? float3(1, 0, 1)
                : saturate(dbgShadowSlice * 16.0 / 255.0).xxx;
            output.color = float4(dbgColor, 1.0);
            return output;
        }
        // ВРЕМЕННЫЙ отладочный канал 13 - punctual.ShadowParams.x (БАЗОВЫЙ слайс света ДО смещения
        // грани куба), та же кодировка *16/255, что канал 12 - изолирует, врёт ли БАЗА (назначение
        // слайса в LightCulling.TryBuildPunctualLight) или смещение грани (UnlitInstancedPS выбор
        // доминирующей оси).
        if (PreviewChannel == 13)
        {
            float3 dbgColor = dbgShadowBase < -0.5
                ? float3(1, 0, 1)
                : saturate(dbgShadowBase * 16.0 / 255.0).xxx;
            output.color = float4(dbgColor, 1.0);
            return output;
        }
        // ВРЕМЕННЫЙ отладочный канал 14 - сырое ClusterCounts[clusterIdx] (до клампа) этого пикселя,
        // та же кодировка *16/255. Магента = punctualCount == 0 (ветка кластеров вообще не вошла).
        // Кодировка ЦВЕТОМ, а не серым *16/255, как было: на 8 битах один свет давал 16/255 - на глаз
        // неотличимо от нуля, и «в кластере нет светов» читалось там, где свет ровно один (ровно эта
        // ловушка съела прогон пробника: канал показывал сплошной чёрный при работающей
        // кластеризации). Теперь ноль отделён от единицы качественно, а не количественно:
        //   чёрный  - 0 светов (кластер пуст);
        //   синий -> циан -> зелёный -> жёлтый -> красный - 1..CLUSTER_MAX_LIGHTS по возрастанию;
        //   белый   - счётчик БОЛЬШЕ CLUSTER_MAX_LIGHTS: кластер переполнен, хвост светов потерян;
        //   магента - ветка кластеров не выполнялась (у камеры нет punctual-светов).
        if (PreviewChannel == 14)
        {
            if (dbgClusterRawCount < -0.5)
            {
                output.color = float4(1, 0, 1, 1);
                return output;
            }
            if (dbgClusterRawCount < 0.5)
            {
                output.color = float4(0, 0, 0, 1);
                return output;
            }
            if (dbgClusterRawCount > CLUSTER_MAX_LIGHTS + 0.5)
            {
                output.color = float4(1, 1, 1, 1);
                return output;
            }

            float dbgCountT = saturate((dbgClusterRawCount - 1.0) / (CLUSTER_MAX_LIGHTS - 1.0));
            float3 dbgColor =
                  dbgCountT < 0.25 ? lerp(float3(0, 0, 1), float3(0, 1, 1), dbgCountT / 0.25)
                : dbgCountT < 0.50 ? lerp(float3(0, 1, 1), float3(0, 1, 0), (dbgCountT - 0.25) / 0.25)
                : dbgCountT < 0.75 ? lerp(float3(0, 1, 0), float3(1, 1, 0), (dbgCountT - 0.50) / 0.25)
                : lerp(float3(1, 1, 0), float3(1, 0, 0), (dbgCountT - 0.75) / 0.25);
            output.color = float4(dbgColor, 1.0);
            return output;
        }

        // ВРЕМЕННЫЙ отладочный канал 15 - величина выхода shadowUv за [0,1] для пикселей канала 11
        // с кодом -3 (циан, UV вне диапазона): dbgPunctual.xy несёт СЫРОЙ shadowUv независимо от
        // guard'а (см. запись dbgPunctual выше), так что маргинальный перехлёст (чуть больше 1) и
        // грубо неверный слайс (UV в разы за пределами) различимы численно, а не только визуально.
        // excess = максимум по x/y расстояния от [0,1] (0 - внутри, растёт с выходом за границу),
        // закодирован r=g=b = saturate(excess / 2.0) - excess=2.0 бьёт в белый потолок; для пикселей
        // ветки, что НЕ дали -3 (не циан), пишем чёрный.
        if (PreviewChannel == 15)
        {
            float excessX = max(-dbgPunctual.x, dbgPunctual.x - 1.0);
            float excessY = max(-dbgPunctual.y, dbgPunctual.y - 1.0);
            float excess = dbgPunctual.w < -2.5 && dbgPunctual.w > -3.5 ? max(excessX, excessY) : 0.0;
            output.color = float4(saturate(excess / 2.0).xxx, 1.0);
            return output;
        }

        // ВРЕМЕННЫЙ отладочный канал 16 - dbgShadowSlice (та же кодировка *16/255, что канал 12), но
        // ТОЛЬКО для пикселей, у которых канал 11 дал циан (dbgPunctual.w == -3, UV вне [0,1]) -
        // остальные чёрные. Отвечает "какой слайс выбран именно у циан-пикселей" напрямую, без
        // визуального сравнения двух отдельных картинок.
        if (PreviewChannel == 16)
        {
            bool isCyan = dbgPunctual.w < -2.5 && dbgPunctual.w > -3.5;
            float3 dbgColor = !isCyan ? float3(0, 0, 0)
                : dbgShadowSlice < -0.5 ? float3(1, 0, 1)
                : saturate(dbgShadowSlice * 16.0 / 255.0).xxx;
            output.color = float4(dbgColor, 1.0);
            return output;
        }

        // ВРЕМЕННЫЙ отладочный канал 17 - сырой shadowUv.xy циан-пикселей канала 11, закодирован как
        // (uv/8 + 0.5) чтобы уместить диапазон примерно [-4, 4] в 8 бит (excess до 2.0 наблюдался,
        // берём запас). r=x, g=y, b=0. Чёрный (0,0,0 ТОЧНО) - не циан-пиксель (excess=0 у циан
        // пикселя тоже даёт крошечное ненулевое значение из-за +0.5, так что чёрный однозначен).
        if (PreviewChannel == 17)
        {
            bool isCyan = dbgPunctual.w < -2.5 && dbgPunctual.w > -3.5;
            float3 dbgColor = isCyan
                ? float3(saturate(dbgPunctual.x / 8.0 + 0.5), saturate(dbgPunctual.y / 8.0 + 0.5), 0.0)
                : float3(0, 0, 0);
            output.color = float4(dbgColor, 1.0);
            return output;
        }

        // ВРЕМЕННЫЙ отладочный канал 18 - toFrag (worldPos - light) циан-пикселей канала 11, r/g/b =
        // (toFrag.xyz/16 + 0.5): нужен, чтобы вручную пересчитать на CPU, какую грань ДОЛЖНА была
        // выбрать доминирующая ось для ЭТОЙ ТОЧКИ, и сравнить с тем, что реально выбрал шейдер
        // (канал 16/dbgShadowSlice) - расхождение укажет, врёт ли выбор грани или сама проекция.
        if (PreviewChannel == 18)
        {
            bool isCyan = dbgPunctual.w < -2.5 && dbgPunctual.w > -3.5;
            float3 toFragDbg = input.worldPos - punctualLightPosDbg;
            float3 dbgColor = isCyan ? saturate(toFragDbg / 16.0 + 0.5) : float3(0, 0, 0);
            output.color = float4(dbgColor, 1.0);
            return output;
        }

        // ВРЕМЕННЫЙ отладочный канал 19 - ВЫБРАННАЯ ГРАНЬ КУБА своим цветом, для ВСЕХ пикселей, куда
        // свет со слайсом вообще дотянулся (не только для циан). Каналы 12/16 кодируют слайс серым
        // (*16/255), и на глаз соседние грани там неразличимы - приходилось сравнивать по пипетке.
        // Здесь грань = dbgShadowSlice - dbgShadowBase, и у каждой свой цвет:
        //   +X красный / -X тёмно-красный, +Y зелёный / -Y тёмно-зелёный, +Z синий / -Z тёмно-синий.
        // Белый - грань вне 0..5 (у спота смещение не добавляется, так что там всегда «+X»/красный:
        // для спота это норма, у него один слайс). Магента - ветка не дошла до выбора слайса.
        //
        // Зачем: раскладка граней на плоскости ПРЕДСКАЗУЕМА аналитически. Пол под точечным светом
        // виден гранью -Y ровно там, где вертикаль до пола больше горизонтального выноса, то есть
        // пока высота лампы над полом > Range/sqrt(2); дальше кольцом идут ±X/±Z, и границы между
        // ними - прямые под 45 градусов от проекции лампы. Если картинка канала расходится с этим
        // рисунком (одна грань на весь кадр, или граница не там), выбор грани врёт; если совпадает -
        // врёт не он, и дальше смотреть надо в саму проекцию.
        if (PreviewChannel == 19)
        {
            float dbgFace = dbgShadowSlice - dbgShadowBase;
            float3 dbgColor =
                  dbgShadowSlice < -0.5 ? float3(1, 0, 1)
                : dbgFace < 0.5 ? float3(1.0, 0.0, 0.0)
                : dbgFace < 1.5 ? float3(0.4, 0.0, 0.0)
                : dbgFace < 2.5 ? float3(0.0, 1.0, 0.0)
                : dbgFace < 3.5 ? float3(0.0, 0.4, 0.0)
                : dbgFace < 4.5 ? float3(0.0, 0.0, 1.0)
                : dbgFace < 5.5 ? float3(0.0, 0.0, 0.4)
                : float3(1, 1, 1);
            output.color = float4(dbgColor, 1.0);
            return output;
        }

        // Канал 20 - СРЕЗ ГЛУБИНЫ фроксел-сетки (dbgClusterCell.z) своим цветом: прямой аналог
        // "Display depth Slices" из статьи aortiz (Clustered Shading), по которой сделан
        // LightClusterCS. Отвечает на вопрос, который каналы 11..19 не задают вовсе: правильно ли
        // пиксель находит СВОЙ кластер. Каналы теней (11..19) диагностируют совсем другую половину -
        // сэмплинг PunctualShadowMaps, и по ним о кластеризации нельзя сказать ничего.
        //
        // Как ЧИТАТЬ картинку (в этом вся ценность канала - ожидаемый вид известен аналитически):
        //   - Полосы обязаны идти по ГЛУБИНЕ, а не по экрану: на плоском полу, уходящем от камеры,
        //     это полосы поперёк направления взгляда, сгущающиеся вдаль (срезы экспоненциальные);
        //     на стене, перпендикулярной взгляду, - ОДИН ровный цвет на всю стену.
        //   - Цвет обязан МЕНЯТЬСЯ при движении камеры вперёд/назад и НЕ меняться при повороте
        //     камеры вокруг своей оси.
        //   - Весь кадр одного цвета = срезы вырождены (ClusterParams.zw пустые/равные, см. магенту)
        //     или view-z считается неверно; полосы ВЕРТИКАЛЬНЫЕ/ГОРИЗОНТАЛЬНЫЕ по экрану вместо
        //     глубинных = в срез утёк экранный x/y.
        // Палитра - 8 цветов по кругу (срез % 8), яркость ступенькой по номеру восьмёрки
        // (0..7 тусклее, 8..15 средние, 16..23 яркие), так что соседние срезы всегда контрастны, а
        // абсолютный номер среза читается по яркости. Магента - сетка не определена (ClusterParams
        // .zw пустые: превью-конвейеры, где кластеризация не гоняется вовсе).
        if (PreviewChannel == 20)
        {
            if (dbgClusterCell.z < -0.5)
            {
                output.color = float4(1, 0, 1, 1);
                return output;
            }

            uint dbgSlice = (uint)dbgClusterCell.z;
            uint dbgHue = dbgSlice % 8;
            float3 dbgPalette =
                  dbgHue == 0 ? float3(1, 0, 0)
                : dbgHue == 1 ? float3(0, 1, 0)
                : dbgHue == 2 ? float3(0, 0, 1)
                : dbgHue == 3 ? float3(1, 1, 0)
                : dbgHue == 4 ? float3(0, 1, 1)
                : dbgHue == 5 ? float3(1, 0, 1)
                : dbgHue == 6 ? float3(1, 0.5, 0)
                : float3(1, 1, 1);
            float dbgBand = 0.35 + 0.325 * (float)(dbgSlice / 8);
            output.color = float4(dbgPalette * dbgBand, 1.0);
            return output;
        }

        // Канал 21 - ТАЙЛ фроксел-сетки по экрану (dbgClusterCell.xy) шахматкой: контроль второй
        // половины отображения пиксель->кластер. Ожидаемый вид известен точно: ровная сетка
        // CLUSTER_GRID_X x CLUSTER_GRID_Y (16x8) клеток НА ВЕСЬ кадр. Если клеток видно меньше и они
        // сжаты в угол - input.pos.xy и viewData.viewport.zw живут в разных разрешениях (рендер-скейл
        // апскейлера, см. GraphicsPipelineSimple.LatchRenderResolution); если сетка съезжает при
        // ресайзе окна - viewport камеры отстаёт от таргета. Красный канал - номер тайла по X,
        // зелёный - по Y (плавная градация), синий - шахматка (x+y) % 2 для видимости границ.
        if (PreviewChannel == 21)
        {
            if (dbgClusterCell.z < -0.5)
            {
                output.color = float4(1, 0, 1, 1);
                return output;
            }

            float dbgChecker = fmod(dbgClusterCell.x + dbgClusterCell.y, 2.0) < 0.5 ? 0.15 : 0.85;
            output.color = float4((dbgClusterCell.x + 0.5) / CLUSTER_GRID_X,
                                  (dbgClusterCell.y + 0.5) / CLUSTER_GRID_Y, dbgChecker, 1.0);
            return output;
        }

        // Каналы 22..24 - ПРОЕЦИРУЕМАЯ ГЛУБИНА СВЕТА НА ПОВЕРХНОСТЬ, то есть обе глубины, которые
        // сравнивает теневой сэмплер, в МИРОВЫХ единицах вдоль оси слайса (см. dbgShadowDepth):
        //   22 - глубина ПРИЁМНИКА: как далеко от света лежит ЭТА поверхность;
        //   23 - глубина ОККЛЮДЕРА: что записано в слайсе по тому же UV, то есть до чего свет
        //        реально "дострелил" в этом направлении;
        //   24 - их знаковая разница в масштабе применённого байаса - собственно вердикт сэмплера.
        // Зачем в метрах, а не в NDC: проекция слайса перспективная, у дальней плоскости NDC-глубины
        // приёмника и окклюдера слипаются в неразличимые тысячные, и по NDC-картинке о зазоре между
        // ними сказать нечего. Рампа общая у 22 и 23 (нормировка на far слайса), поэтому их можно
        // сравнивать переключением туда-сюда: там, где поверхность НЕ в тени, картинки обязаны
        // СОВПАДАТЬ (свет видит ровно её); расхождение = либо перед ней есть окклюдер (законная
        // тень), либо сэмплится ЧУЖОЙ слайс (тогда расхождение сплошное и бессистемное).
        // Рампа: чёрный (у света) -> синий -> циан -> зелёный -> жёлтый -> красный (far слайса).
        // Магента - сэмплинг тени сюда не дошёл (нет слайса, точка вне радиуса/вне квадрата слайса).
        if (PreviewChannel == 22 || PreviewChannel == 23)
        {
            if (dbgShadowDepth.x < 0.0)
            {
                output.color = float4(1, 0, 1, 1);
                return output;
            }

            float dbgDepth = PreviewChannel == 22 ? dbgShadowDepth.x : dbgShadowDepth.y;
            float dbgT = saturate(dbgDepth / max(dbgShadowDepth.z, 1e-4));
            // Шеститочечная рампа кусочными lerp-ами (пять равных отрезков по 0.2, стыки НЕПРЕРЫВНЫ:
            // конец каждого отрезка - начало следующего): у линейной серой шкалы дальняя половина
            // диапазона неразличима на глаз, а весь смысл канала - именно в сравнении двух картинок.
            float3 dbgColor =
                  dbgT < 0.2 ? lerp(float3(0, 0, 0), float3(0, 0, 1), dbgT / 0.2)
                : dbgT < 0.4 ? lerp(float3(0, 0, 1), float3(0, 1, 1), (dbgT - 0.2) / 0.2)
                : dbgT < 0.6 ? lerp(float3(0, 1, 1), float3(0, 1, 0), (dbgT - 0.4) / 0.2)
                : dbgT < 0.8 ? lerp(float3(0, 1, 0), float3(1, 1, 0), (dbgT - 0.6) / 0.2)
                : lerp(float3(1, 1, 0), float3(1, 0, 0), (dbgT - 0.8) / 0.2);
            output.color = float4(dbgColor, 1.0);
            return output;
        }

        // Канал 24 - зазор (приёмник - окклюдер) В ЕДИНИЦАХ ПРИМЕНЁННОГО МИРОВОГО БАЙАСА. Именно
        // байас, а не метры: акне и peter-panning - это всегда вопрос "зазор больше или меньше
        // байаса", и абсолютная величина в метрах на него не отвечает (у перспективного слайса
        // тексель, а с ним и байас, растёт с дистанцией - см. shadowTexelWorld).
        //   зелёный - зазор в пределах байаса: поверхность сама себе окклюдер, пиксель освещён (норма);
        //   красный - зазор БОЛЬШЕ байаса: перед поверхностью реальный окклюдер, пиксель в тени;
        //   синий   - зазор ОТРИЦАТЕЛЬНЫЙ (приёмник ближе к свету, чем всё записанное в слайсе):
        //             в норме это только там, куда каст не рисовался, сплошная синева = слайс пустой
        //             или сэмплится чужой.
        // Яркость - |зазор|/байас с потолком 4: тонкая красная кайма вдоль контактов при зелёном
        // фоне и есть здоровая картинка, широкая красная полоса от объекта = байас велик
        // (peter-panning), рваная красная сыпь по освещённой плоскости = байас мал (акне).
        if (PreviewChannel == 24)
        {
            if (dbgShadowDepth.x < 0.0)
            {
                output.color = float4(1, 0, 1, 1);
                return output;
            }

            float dbgGap = dbgShadowDepth.x - dbgShadowDepth.y;
            float dbgGapInBias = dbgGap / max(dbgShadowBiasWorld, 1e-6);
            float dbgMag = saturate(abs(dbgGapInBias) / 4.0);
            float3 dbgColor = dbgGapInBias < -1.0 ? float3(0, 0, 1)
                : dbgGapInBias <= 1.0 ? float3(0, 1, 0)
                : float3(1, 0, 0);
            output.color = float4(dbgColor * lerp(0.25, 1.0, dbgMag), 1.0);
            return output;
        }

        // Канал 25 - ЗНАК shadowClip.w у пикселей, ДОШЕДШИХ до проекции в слайс (см. dbgShadowClipW).
        // Отвечает на вопрос, который каналы 11..24 обходят стороной: они все живут ЗА guard'ом
        // shadowClip.w > 1e-4 и про отброшенные им пиксели молчат - в канале 11 такой пиксель
        // неотличим от «света сюда не дошло» (и то, и другое магента).
        //   магента - до проекции не дошли (света нет в кластере / вне радиуса / нет слайса);
        //   СИНИЙ   - w <= 0: точка ПОЗАДИ ближней плоскости выбранной грани. По построению выбора
        //             грани (доминирующая ось вектора свет->фрагмент) этого быть не может вовсе:
        //             w и есть проекция того самого вектора на ось той самой грани. Синева здесь -
        //             прямая улика, что матрица слайса не соответствует выбранной грани;
        //   зелёный->жёлтый->красный - w от 0 до far слайса (нормальный случай).
        if (PreviewChannel == 25)
        {
            if (dbgShadowClipW < -1e8)
            {
                output.color = float4(1, 0, 1, 1);
                return output;
            }
            if (dbgShadowClipW <= 1e-4)
            {
                output.color = float4(0, 0, 1, 1);
                return output;
            }

            float dbgWt = saturate(dbgShadowClipW / max(dbgShadowDepth.z, 1e-4));
            output.color = float4(dbgWt < 0.5
                ? lerp(float3(0, 1, 0), float3(1, 1, 0), dbgWt / 0.5)
                : lerp(float3(1, 1, 0), float3(1, 0, 0), (dbgWt - 0.5) / 0.5), 1.0);
            return output;
        }

        // Канал 26 - ОСЬ ГРАНИ ИЗ МАТРИЦЫ СЛАЙСА глазами шейдера (w-столбец viewProj), закодированная
        // цветом ровно как грани в канале 19: +X красный / -X тёмно-красный, +Y зелёный / -Y тёмно-
        // зелёный, +Z синий / -Z тёмно-синий. Белый - ось не единичная и не осевая (единичная матрица
        // даёт (0,0,0), нулевая тоже - обе сюда). Магента - до чтения матрицы не дошли.
        // Смысл: канал 19 показывает, какую грань шейдер ВЫБРАЛ, а этот - какая грань лежит в
        // матрице, которую он по этому выбору ПРОЧИТАЛ. Совпадают - виновата не индексация; расходятся
        // (или белое) - до шейдера доезжает не тот набор матриц, и дальше искать надо в заливке
        // буфера, а не в HLSL.
        if (PreviewChannel == 26 || PreviewChannel == 27)
        {
            if (dbgSliceAxis.x > 1e8)
            {
                output.color = float4(1, 0, 1, 1);
                return output;
            }

            float3 a = PreviewChannel == 26 ? dbgSliceAxis : dbgSliceAxisRow;
            float3 dbgColor =
                  a.x > 0.9 ? float3(1.0, 0.0, 0.0)
                : a.x < -0.9 ? float3(0.4, 0.0, 0.0)
                : a.y > 0.9 ? float3(0.0, 1.0, 0.0)
                : a.y < -0.9 ? float3(0.0, 0.4, 0.0)
                : a.z > 0.9 ? float3(0.0, 0.0, 1.0)
                : a.z < -0.9 ? float3(0.0, 0.0, 0.4)
                : float3(1, 1, 1);
            output.color = float4(dbgColor, 1.0);
            return output;
        }

        // Канал 28 - ТЕНЬ СОЛНЦА и КАСКАД, которым она взята, одной картинкой. Существует потому,
        // что на кадре пятно тени, пятно акне и пятно от смены каскада выглядят одинаково - тёмное
        // пятно на стене, - а лечатся тремя разными вещами. Тон отвечает «каким каскадом», яркость -
        // «сколько тени»:
        //   магента       - мирового света нет (тени выключены / LightDirection пустой);
        //   ЧЁРНЫЙ        - каскад не выбран ни один, точка объявлена освещённой. На геометрии,
        //                   которая обязана быть в объёме каскадов, это и есть просвет;
        //   красный   (0) - каскад 0, зелёный (1), синий (2), жёлтый (3);
        //   яркость тона  - множитель тени: полный тон = свет, чёрный = полная тень.
        // Как читать: тень от реального окклюдера повторяет силуэт и НЕ меняет тон на своей границе;
        // смена каскада - это смена ТОНА, и если тьма начинается ровно на ней, виноват каскад, а не
        // окклюдер; акне - мелкая рябь ВНУТРИ одного тона, повторяющая сетку текселей карты.
        if (PreviewChannel == 28)
        {
            if (!hasWorldLight)
            {
                output.color = float4(1, 0, 1, 1);
                return output;
            }

            float3 cascadeTint =
                  dbgSunCascade < -0.5 ? float3(0, 0, 0)
                : dbgSunCascade < 0.5 ? float3(1, 0, 0)
                : dbgSunCascade < 1.5 ? float3(0, 1, 0)
                : dbgSunCascade < 2.5 ? float3(0, 0, 1)
                : float3(1, 1, 0);

            output.color = float4(cascadeTint * saturate(keyShadow), 1.0);
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
