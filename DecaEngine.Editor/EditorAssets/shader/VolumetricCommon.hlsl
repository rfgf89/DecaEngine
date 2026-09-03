// Shared body of the volumetric light pass (god rays + volumetric fog). Wrappers
// VolumetricPS/VolumetricMsaaPS define DEPTH_FETCH for single- vs multi-sampled depth.
// Unlike FogPass (analytic aerial perspective), this pass ray-marches the sun cascade
// shadow map to get shafts consistent with scene geometry. Runs BEFORE FogPass.
// Reads a copy of the frame (_SceneTex) and rewrites it whole: the engine PSO
// abstraction has no blend state, and a target cannot be read and written at once.
// Operates in linear space, before tonemap.
#include "Instancing.hlsl"

Texture2D    _SceneTex;
SamplerState _SceneTex_sampler;

// 1x1 adapted luminance (EyeAdaptationPS). In LDR mode a placeholder is bound and NOT read
// (see volExposureRelative); the slot must exist: an empty descriptor fails Vulkan VUID-08114.
Texture2D    _AdaptTex;

// Sun cascade shadow map, same array/sampler as geometry (UnlitInstancedPS).
// Standard Z (clear 1.0, Less on write), LessEqual compare: SampleCmp 1 = lit.
Texture2DArray         ShadowMaps;
SamplerComparisonState ShadowMaps_sampler;

// Punctual lights and their shadows; bound via BindShadowResources. The screen-space light
// cluster grid is NOT used here: march points do not belong to the surface frustum clusters,
// and a linear loop over the camera's light segment is cheaper than mis-addressing.
StructuredBuffer<PunctualLight> PunctualLights;
StructuredBuffer<float4> PunctualShadowMatrices;
Texture2DArray         PunctualShadowMaps;
SamplerComparisonState PunctualShadowMaps_sampler;

// Row-major matrix load, mirroring UnlitInstancedPS.LoadPunctualShadowMatrix (cbuffer matrix
// transposition differs between backends).
float4x4 LoadPunctualShadowMatrix(uint slice)
{
    uint row = slice * 4;
    return float4x4(PunctualShadowMatrices[row + 0], PunctualShadowMatrices[row + 1],
                    PunctualShadowMatrices[row + 2], PunctualShadowMatrices[row + 3]);
}

cbuffer View
{
    ViewData viewData;
}

// Cascade matrices arrive in the same Light cbuffer as geometry; ForwardPass fills it last
// and this pass runs strictly after it.
cbuffer Light
{
    LightData lightData;
}

// Mirrors VolumetricConstantsData (VolumetricLightPass.cs). Padding uses SCALARS, not float3:
// SPIR-V rejects a float3 at an unaligned offset (see SsaoCommon.hlsl).
cbuffer VolumetricConstants
{
    // Medium density at reference height, 1/world-unit.
    float volDensity;
    // Density falloff rate with height, 1/world-unit. 0 = uniform medium.
    float volHeightFalloff;
    // Height at which density equals volDensity.
    float volHeightRef;
    // Distance at which the march starts; near the camera the medium is only noise.
    float volStartDistance;

    // Max march distance. Step count is fixed, so larger distance = coarser shafts.
    float volMaxDistance;
    // Number of march steps: the main quality/cost knob.
    float volSteps;
    // Overall scattering coefficient.
    float volScatter;
    // Henyey-Greenstein anisotropy, -1..1; >0 = forward scattering.
    float volAnisotropy;

    // Sun scattering color (linear) and intensity: the god rays themselves.
    float volSunColorR, volSunColorG, volSunColorB;
    float volSunIntensity;

    // Sky (ambient) scattering; not cut by shadows, keeps shafts from reading as cutouts.
    float volAmbientColorR, volAmbientColorG, volAmbientColorB;
    float volAmbientIntensity;

    // Direction TOWARD the sun in world space (normalized on CPU).
    float volSunDirX, volSunDirY, volSunDirZ;
    // How much shadow cuts sun scattering: 1 = fully. Forced to 0 when there is no shadow
    // pass and shadow map contents are undefined (VolumetricLightPassResources.SetShadow).
    float volShadowStrength;

    // World camera basis: UNIT right/up/forward built on CPU from eye/target (same scheme
    // and reason as FogCommon.hlsl: decomposing the view matrix is row/column error-prone).
    float volRightX, volRightY, volRightZ;
    // Opacity ceiling 0..1: how much the march may eat of the source frame.
    float volMaxOpacity;

    float volUpX, volUpY, volUpZ;
    // Extinction coefficient relative to density; separate from volScatter so the medium
    // can glow while staying nearly transparent.
    float volExtinction;

    float volForwardX, volForwardY, volForwardZ;
    // Floor for shadow-damping of the SKY term: keyed off the sun shadow as a proxy for sky
    // visibility (same compromise as skyDamp in UnlitInstancedPS); without it the ambient
    // term lays a milky film over covered interiors.
    float volAmbientShadowFloor;

    // >0.5: colors are exposure-relative (see VolumetricExposureScale).
    float volExposureRelative;
    // Same key value as auto-exposure/tonemap (TonemapConstants.x).
    float volExposureKey;
    // Punctual scattering share (0 = medium ignores lamps). Lights come from the pool in
    // scene-linear units and are NOT exposure-scaled.
    float volPunctualIntensity;
    float volPad2;
}

// Same reversed-Z near plane and fixed FOV as FogCommon.hlsl / SkyBackgroundPS.hlsl.
static const float VolNearPlane = 0.05;
static const float VolTanHalfFov = 0.41421356; // tan(45deg / 2)

static const float VolPi = 3.14159265359;

// Must match ShadowRenderer.ShadowMapSize.
static const float VolShadowMapSize = 4096.0;

struct VSOutput
{
    float4 pos : SV_POSITION;
    float2 ndc : TEXCOORD0;
};

struct PSOutput
{
    float4 color : SV_TARGET;
};

// Scatter colors are authored in display-referred units; undo the tonemap's adapted/key
// division (same scheme as FogCommon.FogExposureScale).
float VolumetricExposureScale()
{
    if (volExposureRelative < 0.5)
    {
        // LDR pipeline: frame is already display-referred.
        return 1.0;
    }

    float adapted = max(_AdaptTex.Load(int3(0, 0, 0)).r, 1e-4);
    return adapted / max(volExposureKey, 1e-4);
}

// Henyey-Greenstein phase NORMALIZED to the isotropic case (x 4pi, so g = 0 returns 1):
// changing anisotropy redistributes light without changing overall brightness.
float VolumetricPhase(float cosTheta, float g)
{
    float g2 = g * g;
    float denom = 1.0 + g2 - 2.0 * g * cosTheta;
    return (1.0 - g2) / max(pow(max(denom, 1e-4), 1.5), 1e-4);
}

// Exponential height density profile, same as FogCommon.FogAverageDensity but pointwise.
float VolumetricDensityAt(float height)
{
    if (volHeightFalloff < 1e-5)
    {
        return 1.0;
    }

    // Clamp from above: below the reference height the exponential grows without bound and
    // a camera below the scene floor would hit an opaque white wall.
    return min(exp(-volHeightFalloff * (height - volHeightRef)), 64.0);
}

// Sun shadow at a VOLUME point. Differs from UnlitInstancedPS.SampleWorldLightShadow by
// design: no normal-offset bias (no normal in a medium; slightly larger constant bias
// instead) and one sample instead of PCF 3x3 (step jitter hides the banding far cheaper).
float VolumetricShadow(float3 worldPos)
{
    [unroll]
    for (int c = 0; c < 4; c++)
    {
        // Nonzero width = cascade populated (SimpleCullingAndRenderSystem.BuildLightData).
        if (lightData.CascadeSizes[c] <= 0.0)
        {
            continue;
        }

        float4 lightClip = mul(float4(worldPos, 1.0), lightData.CascadeMatrix[c]);
        float3 lightNdc = lightClip.xyz / max(lightClip.w, 1e-6);
        float2 shadowUv = float2(lightNdc.x * 0.5 + 0.5, 0.5 - lightNdc.y * 0.5);

        // Edge margin like SUN_CASCADE_MARGIN_TEXELS but narrower (no PCF here, only the
        // half-texel comparison filter); without it edge samples read another cascade's
        // depth and shafts get a hard seam. No blend band on purpose: it would cost a
        // second sample per march step and step jitter hides the transition anyway.
        const float volMargin = 1.0 / VolShadowMapSize;
        if (any(shadowUv < volMargin) || any(shadowUv > 1.0 - volMargin)
            || lightNdc.z <= 0.0 || lightNdc.z >= 1.0)
        {
            // Outside this cascade: try the next, coarser one.
            continue;
        }

        return ShadowMaps.SampleCmpLevelZero(ShadowMaps_sampler,
            float3(shadowUv, (float)c), lightNdc.z - 0.0015);
    }

    // Beyond all cascades: lit. Keep march distance within the last cascade or shafts
    // switch off abruptly.
    return 1.0;
}

// Must match LightClusters.ShadowMapSize (1024, not the 4096 sun cascades).
static const float VolPunctualShadowMapSize = 1024.0;

// Punctual shadow at a volume point; mirrors the UnlitInstancedPS cluster loop (cube face
// by dominant axis, world-space bias) with the same two volume liberties as
// VolumetricShadow. toFrag = light -> point vector (for cube face selection).
float VolumetricPunctualShadow(PunctualLight l, float3 samplePos, float3 toFrag)
{
    if (l.ShadowParams.x < 0.0)
    {
        return 1.0;
    }

    uint slice = (uint)l.ShadowParams.x;
    if (l.DirectionType.w < 0.5)
    {
        float3 absDir = abs(toFrag);
        if (absDir.x >= absDir.y && absDir.x >= absDir.z)
            slice += toFrag.x > 0.0 ? 0 : 1;
        else if (absDir.y >= absDir.z)
            slice += toFrag.y > 0.0 ? 2 : 3;
        else
            slice += toFrag.z > 0.0 ? 4 : 5;
    }

    float4x4 shadowMatrix = LoadPunctualShadowMatrix(slice);
    float4 clip = mul(float4(samplePos, 1.0), shadowMatrix);
    if (clip.w <= 1e-4)
    {
        return 1.0;
    }

    float3 ndc = clip.xyz / clip.w;
    float2 uv = ndc.xy * float2(0.5, -0.5) + 0.5;
    if (any(uv < 0.0) || any(uv > 1.0) || ndc.z >= 1.0)
    {
        return 1.0;
    }

    float near = max(l.ShadowParams.z, 1e-4);
    float far = l.PositionRange.w;
    float z = max(clip.w, near);
    float tanHalf = l.DirectionType.w > 0.5 ? l.SpotAngles.z / max(l.SpotAngles.x, 1e-4) : 1.0;
    float texelWorld = 2.0 * tanHalf * z / VolPunctualShadowMapSize;
    float bias = texelWorld * 3.0 * near * far / max((far - near) * z * z, 1e-6);
    return PunctualShadowMaps.SampleCmpLevelZero(PunctualShadowMaps_sampler,
        float3(uv, (float)slice), ndc.z - bias);
}

// Interleaved gradient noise jitter for the first step; without it all pixels step at the
// same distances and the march shows visible rings. No texture or history needed.
float VolumetricDither(float2 pixel)
{
    return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
}

PSOutput Main(in VSOutput input)
{
    PSOutput output;

    float2 viewportSize = viewData.viewport.zw;
    int2 pixel = clamp(int2(input.pos.xy), int2(0, 0), int2(viewportSize) - 1);
    float2 uv = input.pos.xy / viewportSize;

    float4 scene = _SceneTex.Sample(_SceneTex_sampler, uv);

    // Pixel ray in WORLD space, intentionally NOT normalized (see FogCommon.hlsl): its
    // projection on the camera axis is 1, so world pos = camPos + ray * zView.
    float aspect = viewData.viewport.z / max(viewData.viewport.w, 1.0);
    float3 ray = float3(volForwardX, volForwardY, volForwardZ)
        + float3(volRightX, volRightY, volRightZ) * (input.ndc.x * VolTanHalfFov * aspect)
        + float3(volUpX, volUpY, volUpZ) * (input.ndc.y * VolTanHalfFov);

    // Reversed-Z: zero is background. Sky gets the max distance so shafts stay visible
    // against it.
    float depth = DEPTH_FETCH(pixel);
    float zView = depth < 1e-6 ? volMaxDistance : VolNearPlane / depth;

    float rayLength = length(ray);
    float3 viewDir = ray / max(rayLength, 1e-6);

    float endDistance = min(zView * rayLength, volMaxDistance);
    float startDistance = min(volStartDistance, endDistance);
    float marchLength = endDistance - startDistance;

    if (marchLength <= 1e-4 || volDensity <= 0.0)
    {
        output.color = scene;
        return output;
    }

    int steps = clamp((int)volSteps, 4, 256);
    float stepLength = marchLength / (float)steps;

    // With g > 0 the phase peaks when looking against the sun, where shafts are visible.
    float3 sunDir = float3(volSunDirX, volSunDirY, volSunDirZ);
    float phase = VolumetricPhase(dot(viewDir, sunDir), clamp(volAnisotropy, -0.95, 0.95));

    float3 sunRadiance = float3(volSunColorR, volSunColorG, volSunColorB) * volSunIntensity * phase;
    float3 ambientRadiance = float3(volAmbientColorR, volAmbientColorG, volAmbientColorB)
        * volAmbientIntensity;

    float3 camPos = viewData.CameraWorldPos;
    float jitter = VolumetricDither(input.pos.xy);

    // Camera's punctual segment in the shared pool (LightData.ClusterParams). Lamp phase is
    // evaluated PER STEP: unlike the sun, the light direction changes along the ray.
    uint punctualOffset = (uint)lightData.ClusterParams.x;
    uint punctualCount = volPunctualIntensity > 0.0 ? (uint)lightData.ClusterParams.y : 0u;
    float anisotropy = clamp(volAnisotropy, -0.95, 0.95);

    float3 scattered = float3(0.0, 0.0, 0.0);
    // Lamp scattering accumulates SEPARATELY: pool lights are scene-linear like the frame
    // and get no exposure scaling.
    float3 scatteredScene = float3(0.0, 0.0, 0.0);
    float transmittance = 1.0;

    for (int i = 0; i < steps; i++)
    {
        float t = startDistance + ((float)i + jitter) * stepLength;
        float3 samplePos = camPos + viewDir * t;

        float density = volDensity * VolumetricDensityAt(samplePos.y);
        if (density <= 1e-7)
        {
            continue;
        }

        float shadow = lerp(1.0, VolumetricShadow(samplePos), saturate(volShadowStrength));

        // Sun term cut fully by shadow; sky term only damped down to the floor.
        float3 inScatter = (sunRadiance * shadow
            + ambientRadiance * lerp(saturate(volAmbientShadowFloor), 1.0, shadow))
            * density * volScatter;

        // Punctual scattering; attenuation/cone mirror UnlitInstancedPS cluster shading.
        float3 punctualRadiance = float3(0.0, 0.0, 0.0);
        [loop]
        for (uint li = 0; li < punctualCount; li++)
        {
            PunctualLight l = PunctualLights[punctualOffset + li];
            float3 toLight = l.PositionRange.xyz - samplePos;
            float distSq = dot(toLight, toLight);
            float range = l.PositionRange.w;
            if (distSq > range * range)
            {
                continue;
            }

            float dist = sqrt(max(distSq, 1e-6));
            float3 dirToLight = toLight / dist;

            // Denominator floor larger than the surface one (+0.25 vs +1e-2): a march step
            // landing next to a lamp with pure 1/d^2 makes a flickering hot pixel.
            float distRatio2 = distSq / (range * range);
            float distFactor = saturate(1.0 - distRatio2 * distRatio2);
            float atten = distFactor * distFactor / (distSq + 0.25);

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

            float punctualShadow = VolumetricPunctualShadow(l, samplePos, -toLight);
            punctualShadow = lerp(1.0, punctualShadow,
                saturate(volShadowStrength) * saturate(l.ShadowParams.y));

            float punctualPhase = VolumetricPhase(dot(viewDir, dirToLight), anisotropy);
            punctualRadiance += l.ColorIntensity.rgb * l.ColorIntensity.w
                * (atten * punctualPhase) * punctualShadow;
        }

        float3 inScatterScene = punctualRadiance * volPunctualIntensity * density * volScatter;

        // Analytic per-segment integration: S0 * (1 - exp(-sigma*dx)) / sigma. Keeps
        // brightness independent of step count, so the quality knob stays a quality knob.
        float sigma = max(density * volExtinction, 1e-6);
        float stepTransmittance = exp(-sigma * stepLength);
        float segment = transmittance * (1.0 - stepTransmittance) / sigma;
        scattered += inScatter * segment;
        scatteredScene += inScatterScene * segment;

        transmittance *= stepTransmittance;

        // Early out once remaining contribution is below the target quantum.
        if (transmittance < 1e-3)
        {
            break;
        }
    }

    // Artistic opacity ceiling, same as fog: something always stays visible through the medium.
    float minTransmittance = 1.0 - saturate(volMaxOpacity);
    transmittance = max(transmittance, minTransmittance);

    // Exposure scale applies ONLY to sun/sky scattering; lamp scattering is already scene-linear.
    float3 result = scene.rgb * transmittance + scattered * VolumetricExposureScale()
        + scatteredScene;

    // Alpha taken from the scene: own alpha would break the icon baker's transparent
    // background (same reason as FogCommon.hlsl).
    output.color = float4(result, scene.a);
    return output;
}
