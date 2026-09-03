// Keywords (see ModelLoader.BuildMaterialKeywords): a keyword-disabled effect is
// compiled out entirely - no branch, no sample, no binding.
// Per-material: HAS_BASECOLOR_TEXTURE, HAS_MR_TEXTURE, MATERIAL_ALPHA_CLIP,
// MATERIAL_TRANSMISSION, MATERIAL_DISPERSION, MATERIAL_SHEEN, HAS_EMISSIVE_TEXTURE.
// Per-preview: FEATURE_NORMAL_MAPS / FEATURE_OCCLUSION / FEATURE_SHADOWS.
// PbrFeatureFlags bits only gate code inside an already-enabled keyword.
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

#if HAS_EMISSIVE_TEXTURE
// glTF emissive texture, sRGB: decoded to linear by hand like _MainTex.
Texture2D    _EmissiveTex;
SamplerState _EmissiveTex_sampler;
#endif

// Procedural equirect environment with roughness-prefiltered mips (see PreviewEnvironmentMap):
// mip N holds the sky analytically re-rendered at the blur a roughness of N/EnvMipMax would
// produce, so a single SampleLevel stands in for a real prefiltered-IBL convolution.
Texture2D    _EnvMap;
SamplerState _EnvMap_sampler;

// Probe-GI SH L1 irradiance atlases (see ProbeGiBaker). Dense grid: atlas width = grid X,
// Z planes stacked vertically (see ProbeGiBakeResult.ShWidth, ProbeGiSampleBody.hlsl).
// Sh0: rgb = L0, a = sky visibility; Sh1: rgb = L1x, a = probe validity (0 = inside wall);
// Sh2/Sh3: rgb = L1y/L1z. Read via Load - trilinear filtering is manual in SampleProbeGi.
// Bound only when probes exist; ProbeGridOrigin.w = 0 keeps the branch dead otherwise.
Texture2D _ProbeSh0;
Texture2D _ProbeSh1;
Texture2D _ProbeSh2;
Texture2D _ProbeSh3;
// DDGI visibility: octahedral 8x8 texels per probe (r = mean distance, g = mean square),
// feeding the Chebyshev occlusion test in SampleProbeGi. Same pool layout scaled 8x on both axes.
Texture2D _ProbeVis;
// rgb = probe offset from its grid node, world units (see ProbeGiBakeResult.Offset). Both the
// trilinear weight and the Chebyshev test must apply it - they measure distance to the probe.
// Zero in the bake; relocation is runtime-only.
Texture2D _ProbeOffset;

// Cascades: same atlas sets for smaller volumes - _C1 twice as dense, _C2 four times.
// Sampled fine-to-coarse; the base volume guarantees whole-scene coverage.
// ProbeGridOrigin1/2.w = 0 means "not created" and the slots hold placeholders.
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

// Zero LightDirection = no shadow pass; the key light stays camera-relative.
cbuffer Light
{
    LightData lightData;
}

// Punctual light clustering (LightClusterCS.hlsl); ClusterParams.y == 0 keeps the branch dead.
StructuredBuffer<PunctualLight> PunctualLights;
StructuredBuffer<uint> ClusterCounts;
StructuredBuffer<uint> ClusterIndices;

// Per-slice viewProj as FOUR float4 ROWS, not a float4x4: PackMatrixRowMajor governs cbuffers
// only, and matrix majorness inside a StructuredBuffer differs per backend (D3D12 transposed
// the element, Vulkan did not). Assembling rows by hand is layout-independent.
StructuredBuffer<float4> PunctualShadowMatrices;

// Row-major viewProj of a shadow slice; each slice occupies four consecutive elements.
float4x4 LoadPunctualShadowMatrix(uint slice)
{
    uint row = slice * 4;
    return float4x4(PunctualShadowMatrices[row + 0], PunctualShadowMatrices[row + 1],
                    PunctualShadowMatrices[row + 2], PunctualShadowMatrices[row + 3]);
}
// One slice per spot, six cube faces per point light. Standard Z (clear 1.0, Less on write),
// compare LessEqual - same convention as the sun cascades. ShadowParams.x < 0 = branch dead.
Texture2DArray PunctualShadowMaps;
SamplerComparisonState PunctualShadowMaps_sampler;

#if FEATURE_SHADOWS
// Sun cascades. Standard Z (clear 1.0, Less on write), compare LessEqual: SampleCmp 1 = lit.
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
    // KHR_texture_transform, precomputed: row-major 2x2 (u' = dot(uv, xy), v' = dot(uv, zw))
    // plus offset. Applied only when PbrUvHasTransform != 0, so a zeroed cbuffer is identity.
    float2 PbrUvOffset;
    float4 PbrUvTransform;
    int PbrUvHasTransform;
    // occlusionTexture UV set (glTF texCoord 0/1): AO is often baked onto TEXCOORD_1.
    int PbrOcclusionUvSet;
    // Environment yaw around Y, radians - shifts equirect U so reflections/ambient follow the key.
    float PbrEnvYaw;
    // SHADOW_MODE_*. Zero must stay PCSS: scenes outside preview leave this cbuffer zeroed and
    // must get default quality, not the cheapest filter.
    int PbrShadowMode;
    // KHR_materials_sheen: rgb = sheenColorFactor (linear, zero = off), w = sheenRoughnessFactor.
    float4 PbrSheenColorRoughness;
    // KHR_materials_specular: rgb = specularColorFactor (may exceed 1; per spec it multiplies the
    // IOR F0 and is clamped to 1 AFTER the multiply), w = specularFactor. Every Lighting-mode push
    // must send (1,1,1,1) for materials without the extension - w = 0 kills specular to black.
    float4 PbrSpecularColorFactor;

    // xyz = world position of probe (0,0,0), w = 1 when probes are baked and bound (0 = feature
    // off and the atlases may be unbound).
    float4 ProbeGridOrigin;
    // xyz = grid spacing in world units, w = sample-point normal bias as a fraction of a cell.
    float4 ProbeGridCell;
    // xyz = probe counts per axis (float for cbuffer packing), w = view-bias fraction.
    float4 ProbeGridCounts;
    // xyz = toroidal grid scroll in probes: node c lives in texel (c + scroll) mod counts.
    float4 ProbeGridScroll;
    // x = floor for damping the sun ambient share by key shadow (default 0.3), y = floor for
    // damping env specular by sky visibility (0.2), z = sun intensity (0 = default 2.0),
    // w = probe irradiance multiplier (0 = default 1.0).
    float4 ProbeGiParams;
    // x = floor for damping the SKY ambient share by key shadow (1 = no damping; 0-init reads
    // as 1). yzw reserved.
    float4 ProbeGiParams2;

    // Finer cascade grids, same semantics as the base ProbeGrid*; Origin.w = 1 means the cascade
    // exists and its _C1/_C2 atlases are bound.
    float4 ProbeGridOrigin1;
    float4 ProbeGridCell1;
    float4 ProbeGridCounts1;
    float4 ProbeGridScroll1;
    float4 ProbeGridOrigin2;
    float4 ProbeGridCell2;
    float4 ProbeGridCounts2;
    float4 ProbeGridScroll2;

    // Linear emissive: glTF emissiveFactor x KHR_materials_emissive_strength, folded at import.
    // TAIL ORDER: this float3 must OPEN a 16-byte register, not follow an int. HLSL allows
    // int + float3 in one register, but SPIR-V std140 needs vec3 aligned to 16 and spirv-opt
    // refuses reflection ("member at offset 388 is not aligned to 16"). float3 + int + int is
    // legal in both layouts. The C# mirror PreviewSettingsData must match byte for byte.
    float3 PbrEmissiveFactor;
    // Tone curve (Tonemap.hlsl), LDR only: in HDR the curve is applied later by TonemapPS.
    int PbrToneCurve;
    // 1 = drawn by the BLENDING PSO: clip only culls invisible texels and the authored alpha is
    // written out for the PSO to blend (straight alpha). Per-ENVIRONMENT, not a keyword - icon
    // baking has no opaque/transparent split and cuts out the same material instead.
    int PbrAlphaBlend;
}

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

// HDR pipeline: RGBA16F target takes LINEAR radiance with no tonemap and no sRGB encode;
// exposure and curve are applied once in TonemapPass. A bit, not a keyword: HDR is an
// environment option and materials would otherwise need a variant each.
static const int FeatureHdrOutput = 8;

#if FEATURE_SHADOWS
// Cascades are concentric ortho boxes; the FIRST one containing the point is the most detailed.
// A cascade is valid when its CascadeSizes width is non-zero; outside all cascades = lit.
// Sun shadow map side in texels (see ShadowRenderer.ShadowMapSize).
#define SUN_SHADOW_TEXELS 4096.0

// Cascade edge margin in texels: PCF taps (1) + comparison sampler filtering (0.5) +
// normal-offset (1.5). Nearer the edge the clamped sampler reads unrelated depth and the
// cascade boundary shows up as a straight seam across the wall.
#define SUN_CASCADE_MARGIN_TEXELS 3.0

// Cross-fade band as a fraction of the cascade side. Neighbouring cascades differ in map
// resolution, hence in PCF softness and bias scale; without the blend the seam reads as a
// sharpness step even with the margin.
#define SUN_CASCADE_BLEND_UV 0.06

// --- PCSS: penumbra from the sun's angular size ---
// lightData.SpotAngles.w = TANGENT OF THE SUN HALF-ANGLE. 0 = not filled (preview), so use a
// one-degree disc; the real sun is ~0.53 deg, slightly larger keeps short shadows soft-edged.
#define SUN_DEFAULT_TAN_HALF_ANGLE 0.00873

// Shadow filter modes by ascending cost: HARD = 1 hardware tap; PCF = fixed 3x3 box; PCSS =
// penumbra from source angular size (default); PCSS_HQ = PCSS with a doubled tap fan.
// ZERO must stay PCSS - scenes outside preview leave the cbuffer zeroed.
#define SHADOW_MODE_PCSS 0
#define SHADOW_MODE_HARD 1
#define SHADOW_MODE_PCF 2
#define SHADOW_MODE_PCSS_HQ 3
// Inline RayQuery shadows, only in the FEATURE_RT_SHADOWS variant (DXC/SM6.5); without it the
// mode silently degrades to PCSS, including on Vulkan where the TLAS never reaches DXC.
#define SHADOW_MODE_RT 4

#if FEATURE_RT_SHADOWS
// Rays per pixel inside the sun's angular cone; TAAU averages the grain, as with PCSS.
#define RT_SHADOW_RAYS 8
// Ray start offset along the normal, in WORLD units: an RT ray has no shadow-map texel to
// scale the raster path's normal-offset by.
#define RT_SHADOW_NORMAL_OFFSET 0.02
#define RT_SHADOW_TMAX 1e4

// Same TLAS as probe-GI; bound only when this keyword variant is actually requested.
RaytracingAccelerationStructure _SceneTlas;
#endif

// Vogel disc taps, used for both blocker search and PCF. 16+16 per pixel instead of classic
// PCSS's full rectangle sweep; the undersampling is hidden by per-pixel disc rotation (IGN
// below) and TAAU. HQ doubles the fan for stills and scenes without TAAU.
#define SUN_PCSS_TAPS 16
#define SUN_PCSS_HQ_TAPS 32

// Blocker search radius in texels. Penumbrae wider than SUN_PCSS_MAX_PENUMBRA_TEXELS are not
// worth searching on this cascade - very long soft shadows fall into a coarser one.
#define SUN_PCSS_SEARCH_TEXELS 12.0
#define SUN_PCSS_MAX_PENUMBRA_TEXELS 20.0
#define SUN_PCSS_HQ_SEARCH_TEXELS 16.0
#define SUN_PCSS_HQ_MAX_PENUMBRA_TEXELS 32.0

// Cascade world depth range as a FRACTION of its width: zfar = 4.5r for a width of 2r.
// Mirrors casterExtension + receiverExtension in CullingAndRenderSystem.UpdateCascades and
// SimpleCullingAndRenderSystem.BuildLightData - change all three or the penumbra world scale breaks.
#define SUN_CASCADE_DEPTH_RANGE_RATIO 2.25

// --- Punctual PCSS (perspective slices), same Vogel discs as the sun ---
// Default emitter radius in metres when ShadowParams.w is unset (SourceRadius = 0).
#define PUNCTUAL_DEFAULT_SOURCE_RADIUS 0.05
// Blocker search radius and PCF radius ceiling, slice texels. The ceiling is HARD: cube faces
// are rendered with only ~2% overlap (about 20 of 1024 texels), so wider taps hit a neighbour face.
#define PUNCTUAL_PCSS_SEARCH_TEXELS 10.0
#define PUNCTUAL_PCSS_MAX_PENUMBRA_TEXELS 16.0

// Interleaved gradient noise (Jimenez, 2014): cheap per-pixel [0,1) for disc rotation. The TAAU
// jitter moves the pattern each frame, so it averages temporally without a frame counter.
float InterleavedGradientNoise(float2 pixel)
{
    return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
}

// Tap i of count on a Vogel disc rotated by phi; radius normalized to [0,1].
float2 VogelDiskSample(int i, int count, float phi)
{
    float r = sqrt((float(i) + 0.5) / float(count));
    float theta = float(i) * 2.39996323 + phi; // golden angle
    float s, c;
    sincos(theta, s, c);
    return r * float2(c, s);
}

// firstCascade = index of the FIRST cascade that contributed; -1 = outside all cascades, lit.
#if FEATURE_RT_SHADOWS
// Ray-traced sun shadow: physical penumbra with no cascades, biases or edge texels. Unlike the
// shadow map it ignores foliage alpha test - the BLAS is solid, so canopies occlude as a block.
float SampleWorldLightShadowRT(float3 worldPos, float3 N, float2 pixelPos, float sunTanHalfAngle)
{
    float3 sunDir = normalize(lightData.LightDirection.xyz);

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

        // ACCEPT_FIRST_HIT: a shadow ray does not need the nearest hit.
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
    // RT mode skips cascades entirely; punctual lights stay on PCSS.
    if (PbrShadowMode == SHADOW_MODE_RT)
    {
        return SampleWorldLightShadowRT(worldPos, N, pixelPos, sunTanHalfAngle);
    }
#endif

    // One disc rotation per pixel: blocker search and PCF of every cascade must stay in phase,
    // otherwise the cascade seam shows two mismatched noise patterns.
    float phi = InterleavedGradientNoise(pixelPos) * 6.2831853;

    // The last valid cascade gets no cross-fade band - nothing to blend with, and fading to
    // "lit" at its edge would cut the shadow off in mid-air.
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

    // [loop] here and on every PCSS tap loop below is for COMPILE time: FXC unrolled 4 cascades
    // x 32 taps into 7.5 s per variant; with [loop] it is 1.3 s and a third of the bytecode.
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

        // Normal-offset bias of ~1.5 shadow-map texels in WORLD units (CascadeSizes[c] = ortho
        // width of THIS level). Depth bias alone cannot save thin geometry - shingles and cloth
        // have back faces centimetres behind, and PCF neighbours pick them up as self-shadow.
        float texelWorld = cascadeWorld / SUN_SHADOW_TEXELS;
        float3 samplePos = worldPos + N * texelWorld * 1.5;

        float4 lightClip = mul(float4(samplePos, 1.0), lightData.CascadeMatrix[c]);
        float3 lightNdc = lightClip.xyz / max(lightClip.w, 1e-6);
        float2 shadowUv = float2(lightNdc.x * 0.5 + 0.5, 0.5 - lightNdc.y * 0.5);

        if (lightNdc.z <= 0.0 || lightNdc.z >= 1.0)
        {
            continue;
        }

        // Distance to the map edge minus the margin; negative = the filter would sample outside.
        float edge = min(min(shadowUv.x, 1.0 - shadowUv.x),
                         min(shadowUv.y, 1.0 - shadowUv.y)) - margin;
        if (edge <= 0.0)
        {
            continue;
        }

        float w = (c == lastValid) ? 1.0 : saturate(edge / SUN_CASCADE_BLEND_UV);
        float take = min(w, 1.0 - acc);
        if (take <= 0.0)
        {
            continue;
        }

        // Top-up bias only: the shadow map is rendered with NO face culling, and the rasterizer
        // biases plus the normal-offset above do the real anti-acne work. Larger values cause
        // peter-panning. All cascades share one depth range, so a constant works at every level.
        float referenceDepth = lightNdc.z - 0.0004;
        float lit;

        // Uniform branch: PbrShadowMode is constant for the whole frame, so no divergence.
        if (PbrShadowMode == SHADOW_MODE_HARD)
        {
            lit = ShadowMaps.SampleCmpLevelZero(ShadowMaps_sampler,
                float3(shadowUv, (float)c), referenceDepth);
        }
        else if (PbrShadowMode == SHADOW_MODE_PCF)
        {
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

        // PCSS step 1: blocker search. Load, not a sampler - this needs a point fetch WITHOUT
        // comparison, and ShadowMaps has no second sampler. Disc radius is capped by the
        // distance to the cascade edge so taps never address outside the map.
        float maxRadiusTexels = min(hq ? SUN_PCSS_HQ_MAX_PENUMBRA_TEXELS : SUN_PCSS_MAX_PENUMBRA_TEXELS,
            edge * SUN_SHADOW_TEXELS);
        float searchRadius = min(hq ? SUN_PCSS_HQ_SEARCH_TEXELS : SUN_PCSS_SEARCH_TEXELS,
            maxRadiusTexels);

        float avgBlocker = 0.0;
        float blockerCount = 0.0;
        [loop] // FXC compile time - see the cascade loop
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

        // Step 2: penumbra width = blocker distance x tan(sun half-angle); NDC depth becomes
        // world depth via SUN_CASCADE_DEPTH_RANGE_RATIO. No blockers = one-texel radius.
        float filterRadius = 1.0;
        if (blockerCount > 0.0)
        {
            avgBlocker /= blockerCount;
            float blockerDistWorld = (referenceDepth - avgBlocker) * cascadeWorld * SUN_CASCADE_DEPTH_RANGE_RATIO;
            filterRadius = clamp(blockerDistWorld * sunTanHalfAngle / texelWorld, 1.0, maxRadiusTexels);
        }

        // Step 3: PCF over the same disc rotated by half a turn, so its taps miss the search
        // taps and the per-pixel pattern is twice as dense.
        float sum = 0.0;
        [loop] // FXC compile time - see the cascade loop
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

    // Unclaimed weight counts as lit: outside every cascade there is no shadow.
    return shadow + (1.0 - acc);
}
#endif

static const float PI = 3.14159265359;

// Must equal PreviewEnvironmentMap.MipCount - 1.
static const float EnvMipMax = 6.0;

float3 SampleEnvironment(float3 dir, float roughness)
{
    // Yaw around Y is a plain U shift on an equirect map; the Wrap sampler hides the seam.
    // Sign: +PbrEnvYaw moves the map's sun towards increasing key-light yaw.
    float2 uv = float2(atan2(dir.z, dir.x) / (2.0 * PI) + 0.5 + PbrEnvYaw / (2.0 * PI),
                       acos(clamp(dir.y, -1.0, 1.0)) / PI);
    return _EnvMap.SampleLevel(_EnvMap_sampler, uv, roughness * EnvMipMax).rgb;
}

// Octahedral visibility map side per probe. Comes from the cbuffer (ProbeGiParams2.y) because
// the same value lays out the atlas on the CPU - the two must not drift. 0 reads as 8.
int ProbeVisRes()
{
    int res = (int)ProbeGiParams2.y;
    return res > 0 ? res : 8;
}

// Octahedral topology past a tile edge: the neighbour is a texel of the SAME tile on the
// opposite side with the other coordinate mirrored. Clamping instead would fake a wall along
// the octahedral seam; border texels would mean relaying out the atlas on CPU and GPU both.
int2 OctWrapTexel(int2 t, int res)
{
    if (t.x < 0)         { t.x = 0;         t.y = res - 1 - t.y; }
    else if (t.x >= res) { t.x = res - 1;   t.y = res - 1 - t.y; }

    if (t.y < 0)         { t.y = 0;         t.x = res - 1 - t.x; }
    else if (t.y >= res) { t.y = res - 1;   t.x = res - 1 - t.x; }

    return t;
}

// Must match ProbeGiBaker.OctEncode bit for bit - the CPU writes the visibility atlas with it.
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

// Non-linear L1 irradiance reconstruction (Geomerics/Enlighten, Graham Hazel). R0/R1 use
// Hazel's normalized convention (R0 = sphere average) per channel.
// The linear form R0 + 2*R1.n goes NEGATIVE opposite a bright direction when |R1| approaches R0.
// It is a trade, not a strict win: the non-linear form is ~4x more accurate at r = 0.85-0.99 but
// blurs a hemispherical source where the linear form is exact, so the two are blended by r.
// The r <= 0.5 threshold is exact, not tuned: there 2r <= 1 keeps the linear form non-negative.
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

// One volume's sampler lives in ProbeGiSampleBody.hlsl, included once per cascade: HLSL before
// SM 6.6 cannot pass textures as parameters, so sharing code means macros, not a function.
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

// Cascade fade band in grid cells: the sample body CLAMPS the point to the box, so without a
// weight a pixel outside a fine cascade would get its stretched edge probes instead of falling
// through to the coarse one. Wider bands eat the very density the cascade exists for.
#define PROBE_CASCADE_MARGIN_CELLS 2.0

float ProbeCascadeWeight(float3 worldPos, float4 origin, float4 cell, float4 counts)
{
    float3 f = (worldPos - origin.xyz) / cell.xyz;
    float3 hi = counts.xyz - 1.0;
    float d = min(min(f.x, hi.x - f.x), min(min(f.y, hi.y - f.y), min(f.z, hi.z - f.z)));
    return saturate((d - 0.5) / (PROBE_CASCADE_MARGIN_CELLS - 0.5));
}

// Cascaded sampling: the base volume guarantees coverage, finer volumes blend OVER it by their
// box weight, costing up to three samples inside the finest cascade.
// probeCoverage (0..1) = how far the result can be trusted in place of constant ambient. The
// caller owns that trade because the constant ambient it blends against is computed in Main.
float3 SampleProbeGi(float3 worldPos, float3 N, out float skyVisibility, out float sunFraction,
                     out float3 probeMarker, out float probeCoverage)
{
    probeCoverage = 1.0;
    // The base volume's confidence is ignored: it never scrolls, so its probes are never fresh.
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

            // Scale by probe confidence so a freshly scrolled-in probe fades in over its rounds.
            w *= conf1;
            if (mid.x >= 0.0 && w > 0.0)
            {
                // No base value to blend with: take the cascade value WHOLE and pass the
                // uncertainty out as coverage. Scaling it toward zero would invent darkness.
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

#include "Tonemap.hlsl"

// Direct-lighting contribution of one light for the Lighting preview mode: Cook-Torrance GGX
// specular (D - GGX, G - Smith-Schlick with the direct-lighting k remap, F - Schlick) plus
// energy-conserving Lambert diffuse. dielectricF0 comes from IOR tinted by KHR_materials_specular;
// specularWeight is its specularFactor and, per spec, does not affect metals.
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
// KHR_materials_sheen Charlie lobe (Estevez & Kulla): inverted GGX peaking at TANGENT
// microfacets, so cloth glows along the silhouette instead of a central highlight.
float SheenDistributionCharlie(float sheenRoughness, float NdotH)
{
    float alphaG = sheenRoughness * sheenRoughness;
    float invAlpha = 1.0 / alphaG;
    float sin2h = max(1.0 - NdotH * NdotH, 0.0078125);
    return (2.0 + invAlpha) * pow(sin2h, invAlpha * 0.5) / (2.0 * PI);
}

// Ashikhmin visibility - the glTF Sample Viewer's standard pairing with Charlie.
float SheenVisibilityAshikhmin(float NdotL, float NdotV)
{
    return 1.0 / max(4.0 * (NdotL + NdotV - NdotL * NdotV), 1e-4);
}

// Directional albedo E(NdotV, roughness) of the Charlie lobe: piecewise-quadratic fit of the
// reference viewer's LUT (from three.js). Serves both base-layer albedo scaling and env sheen.
float SheenAlbedoE(float NdotV, float sheenRoughness)
{
    float r = sheenRoughness;
    float r2 = r * r;
    float a = r < 0.25 ? -339.36 * r2 + 161.6 * r - 25.147 : -8.48 * r2 + 14.3 * r - 9.95;
    float b = r < 0.25 ? 44.17 * r2 - 23.977 * r + 3.9199 : 1.97 * r2 - 3.27 * r + 0.72;
    float DG = exp(a * NdotV + b) + (r < 0.25 ? 0.0 : 0.1 * (r - 0.25));
    return saturate(DG * (1.0 / PI));
}

// One light's contribution to the sheen lobe; the ShadePbrLight counterpart for cloth.
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
    // Thin reflection G-buffer (see SsrPass). RT1 = world SHADING normal (post normal-map and
    // two-sided flip) + perceptual roughness. RT2 = env specular factor WITHOUT sky occlusion
    // (rgb) and envOcclusion in alpha, so SSR can subtract exactly what forward added:
    // hdr += conf * factor * (ssr - envOcclusion * envColor).
    float4 gbNormalRough : SV_TARGET1;
    float4 gbEnvFactor : SV_TARGET2;
#endif
};

PSOutput Main(in PSInput input)
{
    PSOutput output;
#if FEATURE_REFLECTION_GBUFFER
    // Default zeros: early returns leave the pixel invisible to the SSR composite (w = 0).
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

        // KHR_texture_transform applies to every UV0 texture; occlusion uses its own set below.
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
        // Must run before any shading so discarded texels also skip the depth write.
        // A blending material is not cut at the authored threshold - the PSO does the
        // transparency, and clip only drops fully invisible texels.
        clip(baseColor.a - (PbrAlphaBlend != 0 ? 0.5 / 255.0 : PbrAlphaCutoff));
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
        // diffuse/specular/env/refraction all pick up the authored micro-relief.
        // Degenerate tangents (meshes without UVs -> zero/garbage tangent) skip the perturbation.
        if (PbrFeatureFlags & FeatureNormalMaps)
        {
            float3 tangent = input.tangent.xyz - N * dot(N, input.tangent.xyz);
            float tangentLength = length(tangent);
            if (tangentLength > 1e-4)
            {
                float3 T = tangent / tangentLength;
                // Bitangent sign (glTF TANGENT.w, Z-mirroring corrected): without it mirrored UV
                // shells apply the normal map with a flipped Y and the relief inverts.
                float3 B = cross(N, T) * input.tangent.w;

                // Z is RECONSTRUCTED from XY, not read: baked normal maps are BC5, which stores
                // two channels and returns zero for the third. A tangent normal always points
                // out of the surface, so RGBA8 maps reconstruct to the same value they store.
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

        // Key light: with shadows on and a valid world light, use the WORLD sun direction so
        // shadow and highlight agree; otherwise fall back to the camera-anchored rig.
        float3 keyDir;
        float keyShadow = 1.0;
        float keyIntensity;
        bool hasWorldLight = false;

        float dbgSunCascade = -1.0;

#if FEATURE_SHADOWS
        hasWorldLight = (PbrFeatureFlags & FeatureShadows)
            && dot(lightData.LightDirection.xyz, lightData.LightDirection.xyz) > 1e-4;

        if (hasWorldLight)
        {
            // Convention: LightData.LightDirection points TOWARDS the sun.
            keyDir = normalize(lightData.LightDirection.xyz);
            keyShadow = SampleWorldLightShadow(input.worldPos, N, input.pos.xy, dbgSunCascade);

            // The world key is weaker than the camera rig's 3.5: the same source already lights
            // the scene through environment reflections. This value must also reach
            // ProbeGiBaker.Bake sunColor or the bounce diverges from the direct light.
            keyIntensity = ProbeGiParams.z > 0.01 ? ProbeGiParams.z : 2.0;
        }
        else
#endif
        {
            keyDir = normalize(0.6 * V + 0.9 * up + 0.7 * right);
            keyIntensity = 3.5;
        }
        float3 keyColor = float3(1.0, 0.98, 0.92) * keyIntensity;

        // The fill is camera-anchored, which only makes sense for the single-model preview rig;
        // in a world-lit scene the env ambient plays that role, so the fill is muted.
        float3 fillDir = normalize(V - 0.6 * right - 0.1 * up);
        float3 fillColor = float3(0.55, 0.60, 0.70) * (hasWorldLight ? 0.0 : 0.8);

        // The shadow must damp the environment terms too: the key is DERIVED from the panorama's
        // dominant source, so its glossy reflection is the same light the occluder blocks.
        // Never to zero - the diffuse part of the environment survives in shadow.
        float envShadow;
        if (hasWorldLight)
        {
            // No sunFacing weight here: it would give a surface facing away from the sun more
            // ambient than a shadowed sunlit one, inverting wall brightness in a courtyard.
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

        // KHR_materials_ior / _dispersion: per-channel IOR triple. dispersion = 20/AbbeNumber,
        // so the F-to-C IOR spread is (ior-1) * dispersion / 20, half on each side of centre.
        // Red bends least, blue most. No extensions = ior 1.5, zero spread, F0 = 0.04.
        float ior = max(PbrIor, 1.001);
        float dispersionHalf = (ior - 1.0) * PbrDispersion * 0.025;
        float3 iors = float3(max(ior - dispersionHalf, 1.001), ior, ior + dispersionHalf);
        float3 iorF0 = (iors - 1.0) / (iors + 1.0);
        iorF0 *= iorF0;

        // Spec order: tint the IOR F0 first, clamp to 1 AFTER - authored values above 1 are meant
        // to push a channel to the limit.
        float3 dielectricF0 = min(iorF0 * PbrSpecularColorFactor.rgb, 1.0);
        float specularWeight = PbrSpecularColorFactor.w;

        float3 direct = ShadePbrLight(N, V, keyDir, keyColor, albedo, metallic, roughness, transmission, dielectricF0, specularWeight) * keyShadow
                      + ShadePbrLight(N, V, fillDir, fillColor, albedo, metallic, roughness, transmission, dielectricF0, specularWeight);

#if MATERIAL_SHEEN
        // Sheen sits on top of the base layer, whose response is scaled down by the lobe's
        // directional albedo for energy conservation.
        float3 sheenColor = PbrSheenColorRoughness.rgb;
        float sheenRoughness = clamp(PbrSheenColorRoughness.w, 0.07, 1.0);
        float sheenNdotV = saturate(dot(N, V));
        float sheenScaling = 1.0 - max(sheenColor.r, max(sheenColor.g, sheenColor.b))
                                 * SheenAlbedoE(sheenNdotV, sheenRoughness);

        direct = direct * sheenScaling
               + ShadeSheenLight(N, V, keyDir, keyColor, sheenColor, sheenRoughness) * keyShadow
               + ShadeSheenLight(N, V, fillDir, fillColor, sheenColor, sheenRoughness);
#endif

        // Debug channel taps; -1 means the branch that would fill them never ran.
        float4 dbgPunctual = float4(0, 0, 0, -1);
        float dbgShadowSlice = -1;
        float dbgShadowBase = -1;
        float3 punctualLightPosDbg = 0;
        float dbgClusterRawCount = -1;
        float3 dbgShadowDepth = -1;
        float dbgShadowBiasWorld = 0;
        float dbgShadowClipW = -1e9;
        float3 dbgSliceAxis = 1e9;
        float3 dbgSliceAxisRow = 1e9;
        float3 dbgClusterCell = -1;

        // ----- Clustered punctual lights -------------------------------------------------------
        // The pixel-to-froxel mapping (screen tile + exponential view-z slice) MUST mirror
        // LightClusterCS.hlsl. It is computed even with no punctual lights so the cluster debug
        // channel still shows the grid.
        uint punctualCount = (uint)lightData.ClusterParams.y;
        float clusterZNear = lightData.ClusterParams.z;
        float clusterZFar = lightData.ClusterParams.w;
        bool clusterGridValid = clusterZFar > clusterZNear && clusterZNear > 0.0;

        uint tileX = 0, tileY = 0, tileZ = 0;
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

                // Frostbite/glTF windowed inverse square: fades to zero at the range boundary so
                // the culling cutoff leaves no step.
                float distFactor = saturate(1.0 - pow(punctualDist / punctualRange, 4.0));
                float punctualAtten = distFactor * distFactor / (punctualDistSq + 1e-2);

                if (punctual.DirectionType.w > 0.5)
                {
                    float cd = dot(-punctualL, punctual.DirectionType.xyz);
                    float spotFactor = saturate((cd - punctual.SpotAngles.x) * punctual.SpotAngles.y);
                    punctualAtten *= spotFactor * spotFactor;
                }

                // A point light picks its cube face by the dominant axis; the face order MUST
                // match PunctualShadowScheduler.FaceDirs: +X,-X,+Y,-Y,+Z,-Z.
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

                    // Normal-offset of ~1.5 slice texels in WORLD units. Unlike the ortho cascades
                    // the texel grows with depth (2*tan(halfFov)*z/size); exact view depth is only
                    // known after the transform, so punctualDist stands in for it.
                    // Spot tan(halfFov) comes from the outer cone (SpotAngles.z/.x = sin/cos of the
                    // outer half-angle); a cube face is 90 degrees, hence tan(45) = 1.
                    float shadowTanHalfFov = punctual.DirectionType.w > 0.5
                        ? punctual.SpotAngles.z / max(punctual.SpotAngles.x, 1e-4)
                        : 1.0;
                    float shadowTexelWorld = 2.0 * shadowTanHalfFov * punctualDist / PUNCTUAL_SHADOW_MAP_SIZE;
                    float3 shadowSamplePos = input.worldPos + N * shadowTexelWorld * 1.5;

                    float4x4 shadowMatrix = LoadPunctualShadowMatrix(shadowSlice);
                    float4 shadowClip = mul(float4(shadowSamplePos, 1.0), shadowMatrix);
                    dbgShadowClipW = shadowClip.w;
                    dbgShadowDepth.z = punctual.PositionRange.w;
                    // Face axis from the matrix COLUMN; the row variant below is the layout
                    // regression check - in a correctly assembled matrix only the column matches.
                    dbgSliceAxis = float3(shadowMatrix._m03, shadowMatrix._m13, shadowMatrix._m23);
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
                            // Perspective slice depth bias. NDC depth is non-linear here
                            // (ndc = f/(f-n) - n*f/((f-n)*z)), so a constant in NDC units would be
                            // worth wildly different metres at different z. The bias is therefore
                            // specified in WORLD units and converted with the local derivative
                            // d(ndc)/dz = n*f/((f-n)*z^2) at the receiver (z = shadowClip.w).
                            // near MUST come ready-made from ShadowParams.z - recomputing it here
                            // misses the scheduler's own clamp on long-range lights.
                            float shadowFar = punctual.PositionRange.w;
                            float shadowNear = max(punctual.ShadowParams.z, 1e-4);
                            float shadowZ = max(shadowClip.w, shadowNear);
                            // The world bias is measured in SLICE TEXELS, not metres: a bias below
                            // the raster quantum cannot suppress acne. At grazing angles the depth
                            // step within one texel scales by tan(incidence), hence the tangent
                            // term; the 4.0 clamp stops it running away into peter-panning.
                            float shadowNdotL = saturate(dot(N, punctualL));
                            float shadowTanTheta = sqrt(saturate(1.0 - shadowNdotL * shadowNdotL))
                                / max(shadowNdotL, 0.15);
                            float shadowWorldBias = shadowTexelWorld * (1.0 + 2.0 * min(shadowTanTheta, 4.0));
                            float shadowNdcPerWorld = shadowNear * shadowFar
                                / max((shadowFar - shadowNear) * shadowZ * shadowZ, 1e-6);
                            float shadowBias = shadowWorldBias * shadowNdcPerWorld;

                            // PCSS as for the sun, but the slice is perspective: NDC depths are
                            // non-linear, so blocker depth and penumbra width are computed in
                            // WORLD metres via z = n*f / (f - ndc*(f-n)).
                            const float punctualTexel = 1.0 / PUNCTUAL_SHADOW_MAP_SIZE;
                            float shadowSum;
                            float shadowTapCount;

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

                            // Step 1: average blocker over a Vogel disc (Load = point tap, no
                            // comparison, so no second sampler is needed).
                            float avgBlockerNdc = 0.0;
                            float blockerCount = 0.0;
                            [loop] // FXC compile time - see the sun cascade loop
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

                            // Step 2: penumbra = (receiverZ - blockerZ) * sourceRadius / blockerZ,
                            // expressed in slice texels AT THE RECEIVER's depth.
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

                            // Step 3: PCF over the disc rotated half a turn from the search disc.
                            shadowSum = 0.0;
                            shadowTapCount = (float)punctualTaps;
                            [loop] // FXC compile time - see the sun cascade loop
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

                            // Receiver and occluder depth in WORLD metres along the slice axis;
                            // in NDC the two collapse near the far plane. Load, not Sample: the
                            // texture only has a comparison sampler.
                            uint2 dbgShadowTexel = (uint2)clamp(shadowUv * PUNCTUAL_SHADOW_MAP_SIZE,
                                0.0, PUNCTUAL_SHADOW_MAP_SIZE - 1.0);
                            float dbgOccluderNdc = PunctualShadowMaps.Load(
                                int4(dbgShadowTexel, shadowSlice, 0)).r;
                            float dbgOccluderZ = shadowNear * shadowFar
                                / max(shadowFar - dbgOccluderNdc * (shadowFar - shadowNear), 1e-6);
                            dbgShadowDepth = float3(shadowClip.w, dbgOccluderZ, shadowFar);
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
                punctualContrib = punctualContrib * sheenScaling
                    + ShadeSheenLight(N, V, punctualL, punctualRadiance, sheenColor, sheenRoughness);
#endif

                direct += punctualContrib;
            }
        }

        // The per-channel F0 spread stays at its physical (subtle) level on purpose: an amplified
        // F0 acts at EVERY angle and casts the whole model blue instead of fringing the edges.

        // Environment irradiance is DIFFUSE only - metals take their whole environment response
        // from envSpecular below, and a second F0-tinted ambient here double-counts it.
        // Deliberately kept below the key's level so the NdotL contrast survives.
        // Baked AO darkens ambient/env terms only: per the glTF spec direct light is not occluded.
        float occlusion = 1.0;
#if FEATURE_OCCLUSION
        if (PbrFeatureFlags & FeatureOcclusion)
        {
            // TEXCOORD_1 is used raw; UV0 goes through the material transform, as a shared ORM
            // atlas expects.
            float2 occlusionUv = PbrOcclusionUvSet == 1 ? input.uv1 : uv;
            float occlusionSample = _OcclusionTex.Sample(_OcclusionTex_sampler, occlusionUv).r;
            occlusion = 1.0 + PbrOcclusionStrength * (occlusionSample - 1.0);
        }
#endif

        // KHR_materials_specular must tint the env response too, not just direct light.
        float3 ambientF0 = lerp(dielectricF0, albedo, metallic);

        // Probe-GI replaces the constant ambient level. The key shadow is NOT applied: occlusion
        // is already baked into the probes (envShadow was its screen-space approximation).
        float skyVisibility = 1.0;
        float probeSunFraction = 0.0;
        float3 probeMarker = float3(1e6, 0.0, 0.0);
        // Fraction by which the probe field replaces the constant ambient - see SampleProbeGi.
        float probeCoverage = 1.0;
        bool probeGi = ProbeGridOrigin.w > 0.5;
        float3 probeIrradiance = 0.0;
        if (probeGi)
        {
            probeIrradiance = SampleProbeGi(input.worldPos, N, skyVisibility, probeSunFraction,
                                            probeMarker, probeCoverage);
            probeGi = probeIrradiance.x >= 0.0;
        }

        // 0.15 is tuned for the preview rig where the camera fill lifts shadows. With a world
        // light the fill is off and ambient is the ONLY light in shadow, hence the boost.
        float ambientLevel = hasWorldLight ? 0.55 : 0.15;

        // The screen key shadow damps ONLY the sun share of the probe field: probes are too
        // coarse to resolve contact shadows, so a shadowed point picks up lit neighbours where
        // the sun bounce dominates. The sky share is left alone - a shadowed courtyard really
        // is lit by the sky, and damping all ambient sinks it into black.
        float skyFloor = ProbeGiParams2.x > 0.001 ? saturate(ProbeGiParams2.x) : 1.0;
        float sunDamp = lerp(saturate(ProbeGiParams.x), 1.0, keyShadow);
        float skyDamp = lerp(skyFloor, 1.0, keyShadow);
        float probeShadow = lerp(skyDamp, sunDamp, probeSunFraction);
        float probeBoost = ProbeGiParams.w > 0.01 ? ProbeGiParams.w : 1.0;
        // The fallback ambient is computed ALWAYS: partially-covered probes fade in from it
        // rather than from black. It costs one extra SampleEnvironment per pixel.
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
        // Backdrop constants are in DISPLAY space (exactly what ImGui draws). In HDR the frame
        // stays linear until TonemapPS, so they are expanded with the same 2.2 gamma the
        // tonemap will fold back.
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
        [unroll]
        for (int c = 0; c < 3; c++)
        {
            float3 refr = refract(-V, N, 1.0 / iors[c]);

            // Entry and exit projected through the same viewProj, so only the Y direction
            // matters in the NDC difference (NDC up -> UV down).
            float3 exitPoint = input.worldPos + refr * thicknessWorld;
            float4 exitClip = mul(float4(exitPoint, 1.0), viewData.viewProj);
            float2 ndcDelta = exitClip.xy / max(exitClip.w, 1e-4) - entryNdc;
            float2 uv = saturate(screenUv + ndcDelta * float2(0.5, -0.5));

            float4 scene = _SceneColor.Sample(_SceneColor_sampler, uv);
            float backdrop = lerp(backdropBottom, backdropTop, saturate(refr.y * 0.5 + 0.5));
            transmitted[c] = lerp(backdrop, scene[c], scene.a);
        }
#else
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
        // With probes the env reflection is damped by baked sky visibility and the screen key
        // shadow; without them by the envShadow approximation.
        float envOcclusion = probeGi ? lerp(saturate(ProbeGiParams.y), 1.0, skyVisibility) * probeShadow : envShadow;
        float3 envSpecular = envColor * Fr * lerp(specularWeight, 1.0, metallic) * occlusion * envOcclusion;

#if MATERIAL_SHEEN
        // _EnvMap mips are prefiltered for GGX, not Charlie; the wide sheen lobe approximates to
        // high GGX roughness well enough for the preview.
        float3 envSheen = SampleEnvironment(R, sheenRoughness) * sheenColor
                        * SheenAlbedoE(NdotV, sheenRoughness) * occlusion * envShadow;
        ambient *= sheenScaling;
        envSpecular = envSpecular * sheenScaling + envSheen;
#endif

#if FEATURE_REFLECTION_GBUFFER
        {
            // Factor WITHOUT envOcclusion: sky occlusion only applies to the prefiltered map,
            // while the SSR trace reflects real on-screen geometry and must not be damped by it.
            // envOcclusion travels separately in alpha so the composite can subtract exactly
            // what forward added.
            float3 gbFactor = Fr * lerp(specularWeight, 1.0, metallic) * occlusion;
#if MATERIAL_SHEEN
            gbFactor *= sheenScaling;
#endif
            output.gbNormalRough = float4(N, roughness);
            output.gbEnvFactor = float4(gbFactor, envOcclusion);
        }
#endif

        // Diagnostic hooks (PreviewProbe): raw linear dumps of the individual lighting terms.
        // Channel 10 is probe PLACEMENT, drawn as a blob on whatever surface is near a probe:
        // green = on its grid node, yellow to red = relocated, blue = invalid (walled in).
        // Probes in open air with no nearby surface are not marked - there is nothing to draw on.
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
            // Manual sRGB encode as on the main path: the target is UNORM, so linear 0.1-0.2
            // would read as black.
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
        // Punctual shadow sampling: magenta = sampling branch never ran, orange = past the far
        // plane, cyan = UV outside the slice square, grey ramp = shadowLit.
        if (PreviewChannel == 11)
        {
            float3 dbgColor =
                dbgPunctual.w < -4.5 ? float3(1, 0, 1)
                : dbgPunctual.w < -3.5 ? float3(1, 0.5, 0)
                : dbgPunctual.w < -2.5 ? float3(0, 1, 1)
                : dbgPunctual.w < -0.5 ? float3(1, 0, 1)
                // Floor at 0.15: pure black must stay unique to the background, where the pixel
                // shader never runs and the target keeps its clear colour.
                : lerp(0.15, 1.0, saturate(dbgPunctual.w)).xxx;
            output.color = float4(dbgColor, 1.0);
            return output;
        }
        // Sampled slice index as grey = slice * 16; magenta = no slice, as in channel 11.
        if (PreviewChannel == 12)
        {
            float3 dbgColor = dbgShadowSlice < -0.5
                ? float3(1, 0, 1)
                : saturate(dbgShadowSlice * 16.0 / 255.0).xxx;
            output.color = float4(dbgColor, 1.0);
            return output;
        }
        // Base slice BEFORE the cube-face offset, same encoding as channel 12.
        if (PreviewChannel == 13)
        {
            float3 dbgColor = dbgShadowBase < -0.5
                ? float3(1, 0, 1)
                : saturate(dbgShadowBase * 16.0 / 255.0).xxx;
            output.color = float4(dbgColor, 1.0);
            return output;
        }
        // Raw ClusterCounts before the clamp. Colour-coded, not grey, so zero is qualitatively
        // distinct from one: black = empty, blue..red = 1..CLUSTER_MAX_LIGHTS, white = overflow
        // (tail lights silently dropped), magenta = the cluster branch never ran.
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

        // How far shadowUv left [0,1] on channel-11 cyan pixels, as saturate(excess / 2).
        if (PreviewChannel == 15)
        {
            float excessX = max(-dbgPunctual.x, dbgPunctual.x - 1.0);
            float excessY = max(-dbgPunctual.y, dbgPunctual.y - 1.0);
            float excess = dbgPunctual.w < -2.5 && dbgPunctual.w > -3.5 ? max(excessX, excessY) : 0.0;
            output.color = float4(saturate(excess / 2.0).xxx, 1.0);
            return output;
        }

        // Slice index of channel-11 cyan pixels only; everything else black.
        if (PreviewChannel == 16)
        {
            bool isCyan = dbgPunctual.w < -2.5 && dbgPunctual.w > -3.5;
            float3 dbgColor = !isCyan ? float3(0, 0, 0)
                : dbgShadowSlice < -0.5 ? float3(1, 0, 1)
                : saturate(dbgShadowSlice * 16.0 / 255.0).xxx;
            output.color = float4(dbgColor, 1.0);
            return output;
        }

        // Raw shadowUv of cyan pixels as uv/8 + 0.5, fitting roughly [-4,4] into 8 bits.
        if (PreviewChannel == 17)
        {
            bool isCyan = dbgPunctual.w < -2.5 && dbgPunctual.w > -3.5;
            float3 dbgColor = isCyan
                ? float3(saturate(dbgPunctual.x / 8.0 + 0.5), saturate(dbgPunctual.y / 8.0 + 0.5), 0.0)
                : float3(0, 0, 0);
            output.color = float4(dbgColor, 1.0);
            return output;
        }

        // toFrag (worldPos - light) of cyan pixels as xyz/16 + 0.5, to recheck the face choice.
        if (PreviewChannel == 18)
        {
            bool isCyan = dbgPunctual.w < -2.5 && dbgPunctual.w > -3.5;
            float3 toFragDbg = input.worldPos - punctualLightPosDbg;
            float3 dbgColor = isCyan ? saturate(toFragDbg / 16.0 + 0.5) : float3(0, 0, 0);
            output.color = float4(dbgColor, 1.0);
            return output;
        }

        // Selected cube face by colour: +X red / -X dark red, +Y green / -Y dark green,
        // +Z blue / -Z dark blue. White = face outside 0..5 (normal for a spot, which has one
        // slice); magenta = the branch never chose a slice. The expected on-screen pattern is
        // analytic: -Y under the light, then a ring of +-X/+-Z split by 45-degree lines.
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

        // Froxel DEPTH slice, palette of 8 hues cycling by slice with brightness stepping every
        // eighth, so neighbours contrast and the absolute index stays readable. Magenta = grid
        // undefined. Correct output shows bands running by depth, not across the screen.
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

        // Froxel screen TILE: r = tile X, g = tile Y, b = checkerboard. Expect an even
        // CLUSTER_GRID_X x CLUSTER_GRID_Y grid over the whole frame; cells squeezed into a corner
        // mean input.pos.xy and viewData.viewport.zw are at different resolutions.
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

        // 22 = receiver depth, 23 = occluder depth, 24 = their signed difference scaled by the
        // applied bias, all in WORLD metres along the slice axis. 22 and 23 share one ramp
        // (black at the light -> red at slice far) so they can be compared by toggling; magenta
        // means shadow sampling never reached this pixel.
        if (PreviewChannel == 22 || PreviewChannel == 23)
        {
            if (dbgShadowDepth.x < 0.0)
            {
                output.color = float4(1, 0, 1, 1);
                return output;
            }

            float dbgDepth = PreviewChannel == 22 ? dbgShadowDepth.x : dbgShadowDepth.y;
            float dbgT = saturate(dbgDepth / max(dbgShadowDepth.z, 1e-4));
            // Six-point ramp in five continuous 0.2 segments: a linear grey scale is unreadable
            // over the far half of the range.
            float3 dbgColor =
                  dbgT < 0.2 ? lerp(float3(0, 0, 0), float3(0, 0, 1), dbgT / 0.2)
                : dbgT < 0.4 ? lerp(float3(0, 0, 1), float3(0, 1, 1), (dbgT - 0.2) / 0.2)
                : dbgT < 0.6 ? lerp(float3(0, 1, 1), float3(0, 1, 0), (dbgT - 0.4) / 0.2)
                : dbgT < 0.8 ? lerp(float3(0, 1, 0), float3(1, 1, 0), (dbgT - 0.6) / 0.2)
                : lerp(float3(1, 1, 0), float3(1, 0, 0), (dbgT - 0.8) / 0.2);
            output.color = float4(dbgColor, 1.0);
            return output;
        }

        // Receiver-occluder gap in UNITS OF THE APPLIED WORLD BIAS - acne and peter-panning are
        // always about the gap versus the bias, and metres cannot answer that. Green = within
        // bias (lit), red = beyond it (shadowed), blue = negative gap, which is normal only
        // where no caster was drawn. Brightness = |gap|/bias capped at 4.
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

        // Sign of shadowClip.w BEFORE the guard, which channels 11..24 all live behind. Magenta =
        // never projected; BLUE = w <= 0, impossible by construction (w is the light-to-fragment
        // vector projected on the chosen face axis) and hence proof the slice matrix does not
        // match the chosen face; green->yellow->red = w from 0 to slice far.
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

        // Face axis as it appears in the slice matrix the shader actually read, coloured like
        // channel 19. White = the axis is neither unit nor axis-aligned (identity and zero
        // matrices both land here). Compare with channel 19 to separate a wrong face choice from
        // a wrong matrix reaching the shader.
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

        // Sun shadow and the cascade it came from in one image: hue = cascade (red 0, green 1,
        // blue 2, yellow 3), brightness = shadow term. Magenta = no world light; BLACK = no
        // cascade selected, point declared lit. A real occluder's shadow does not change hue at
        // its boundary; a cascade artifact does.
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

        // Emissive is added AFTER all lighting: per the glTF spec it is not occluded by AO.
        float3 emissive = PbrEmissiveFactor;
#if HAS_EMISSIVE_TEXTURE
        // sRGB -> linear by hand, same pow 2.2 as base color: textures upload as UNORM.
        emissive *= pow(_EmissiveTex.Sample(_EmissiveTex_sampler, uv).rgb, 2.2);
#endif

        float3 lit = direct + ambient + envSpecular + emissive;

        // Blending materials output the authored alpha for the PSO to blend with; everything
        // else outputs 1, because refraction and compositing read target alpha as coverage.
        float outAlpha = PbrAlphaBlend != 0 ? baseColor.a : 1.0;

        // HDR: leave the frame linear for TonemapPass. Tonemapping here would make auto-exposure
        // measure an already-compressed frame.
        if ((PbrFeatureFlags & FeatureHdrOutput) != 0)
        {
            output.color = float4(lit, outAlpha);
            return output;
        }

        // Khronos PBR Neutral tone map: the key light intentionally overshoots [0,1] for a
        // specular punch, and a plain saturate would clip it into flat white blotches.
        float3 mapped = ApplyToneCurve(lit, PbrToneCurve);

        // Back to display (sRGB) space by hand: the color target is UNORM, not *_SRGB, so
        // nothing downstream encodes for the monitor.
        output.color = float4(pow(mapped, 1.0 / 2.2), outAlpha);
        return output;
    }

    float3 viewDir = normalize(viewData.CameraWorldPos - input.worldPos);
    const float rimPower = 2.0;
    float rim = pow(saturate(dot(normal, viewDir)), rimPower);

    float3 albedo = PreviewMode == 0
        ? _MainTex.Sample(_MainTex_sampler, TransformMaterialUv(input.uv)).rgb * input.vertexColor.rgb
        : float3(1.0, 1.0, 1.0);
    output.color = float4(albedo * saturate(hemi + rim), 1.0);
    return output;
}
