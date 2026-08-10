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

#if FEATURE_SHADOWS
// PCF 3x3 по каскаду 0 shadow map мирового света; за пределами каскада - освещено.
float SampleWorldLightShadow(float3 worldPos, float3 N)
{
    // Normal-offset bias: точка сэмплирования сдвигается вдоль нормали на ~полтора текселя
    // shadow map В МИРОВЫХ единицах (CascadeSizes.x = ширина орто-каскада, см. BuildLightData).
    // Депф-bias один не спасает тонкую геометрию (черепица, ткань): её задняя грань лежит в
    // сантиметрах за передней, и PCF-соседи на рельефе ловят чужие задние грани - крыши
    // затеняют сами себя. Сдвиг по нормали уводит точку из этой зоны независимо от глубины.
    float texelWorld = lightData.CascadeSizes.x / 4096.0;
    worldPos += N * texelWorld * 1.5;

    float4 lightClip = mul(float4(worldPos, 1.0), lightData.CascadeMatrix[0]);
    float3 lightNdc = lightClip.xyz / max(lightClip.w, 1e-6);
    float2 shadowUv = float2(lightNdc.x * 0.5 + 0.5, 0.5 - lightNdc.y * 0.5);

    if (any(shadowUv < 0.0) || any(shadowUv > 1.0) || lightNdc.z <= 0.0 || lightNdc.z >= 1.0)
    {
        return 1.0;
    }

    // Минимальное смещение: shadow map пишется с front-face culling (глубина ЗАДНИХ граней, см.
    // ShadowRenderer.GetBaseState) - от acne защищает сама конвенция, а крупный bias поверх неё
    // отклеивал тень от основания фигур (peter-panning).
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
                float3(shadowUv + float2(x, y) * texel, 0.0), referenceDepth);
        }
    }

    return sum / 9.0;
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

// Khronos PBR Neutral tone mapper (https://github.com/KhronosGroup/ToneMapping) - the reference
// curve of the glTF Sample Viewer's "PBR Neutral" mode. Unlike Reinhard (which halves every
// midtone and is a big part of why the preview used to read as unlit), it passes values below
// ~0.76 through unchanged and only compresses the top of the range, preserving color saturation.
float3 PbrNeutralToneMap(float3 color)
{
    const float startCompression = 0.8 - 0.04;
    const float desaturation = 0.15;

    float x = min(color.r, min(color.g, color.b));
    float offset = x < 0.08 ? x - 6.25 * x * x : 0.04;
    color -= offset;

    float peak = max(color.r, max(color.g, color.b));
    if (peak < startCompression)
    {
        return color;
    }

    float d = 1.0 - startCompression;
    float newPeak = 1.0 - d * d / (peak + d - startCompression);
    color *= newPeak / peak;

    float g = 1.0 - 1.0 / (desaturation * (peak - newPeak) + 1.0);
    return lerp(color, newPeak.xxx, g);
}

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
        // Diagnostic hook (PreviewProbe's debug_transmission stage): dump the raw PbrTransmission
        // constant as grayscale to verify the cbuffer value actually reaches the shader.
        if (PreviewChannel == 9)
        {
            output.color = float4(PbrTransmission.xxx, 1.0);
            return output;
        }

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
            keyDir = -normalize(lightData.LightDirection.xyz);
            keyShadow = SampleWorldLightShadow(input.worldPos, N);

            // Мировой ключ слабее камерного (3.5 тюнился под риг без IBL-солнца): источник, из
            // которого он выведен, УЖЕ светит через энвайронмент-отражения - полная двойная
            // интенсивность пересвечивает глянцевые горизонтальные поверхности в белое.
            keyIntensity = 2.0;
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
        // 0.15 тюнился под превью-риг, где тени добирал камерный fill. В сцене с мировым светом
        // fill выключен и эмбиент - ЕДИНСТВЕННЫЙ свет в тени; без буста двор Sponza проваливается
        // в черноту (небо/отскок от камня в реальности много ярче студийной панорамы).
        float ambientLevel = hasWorldLight ? 0.55 : 0.15;
        float3 ambient = SampleEnvironment(N, 1.0) * ambientLevel * albedo * (1.0 - metallic) * occlusion * envShadow;

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
        const float backdropBottom = 0.26;
        const float backdropTop = 0.55;

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
        float3 envSpecular = envColor * Fr * lerp(specularWeight, 1.0, metallic) * occlusion * envShadow;

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

        // Khronos PBR Neutral tone map: the key light intentionally overshoots [0,1] for a
        // specular punch, and a plain saturate would clip it into flat white blotches.
        float3 lit = direct + ambient + envSpecular;
        float3 mapped = PbrNeutralToneMap(lit);

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