// Shared fog pass body; FogPS.hlsl / FogMsaaPS.hlsl define DEPTH_FETCH.
// Reads a copy of the frame (_SceneTex) instead of blending: the PSO abstraction has no
// blend state, and a target cannot be read and written at once.
// Runs in linear space, before tonemap and before the auto-exposure measurement.
#include "Instancing.hlsl"

Texture2D    _SceneTex;
SamplerState _SceneTex_sampler;

// 1x1 adapted luminance. Unused in LDR mode, but the slot stays bound: an empty descriptor
// trips Vulkan validation (VUID-08114).
Texture2D    _AdaptTex;

cbuffer View
{
    ViewData viewData;
}

// Mirrors FogConstantsData (FogPass.cs). Padded with scalars, not float3: SPIR-V rejects a
// three-component vector at an unaligned offset outright.
cbuffer FogConstants
{
    // Medium density at fogHeightRef, in 1/world-unit.
    float fogDensity;
    // Height falloff rate, 1/world-unit; 0 means uniform fog.
    float fogHeightFalloff;
    float fogHeightRef;
    // Distance with no fog at all, so haze does not settle on held objects.
    float fogStartDistance;

    // Linear space.
    float fogColorR, fogColorG, fogColorB;
    // Artistic opacity ceiling, no physical meaning.
    float fogMaxOpacity;

    // Linear space.
    float fogSunColorR, fogSunColorG, fogSunColorB;
    float fogSunStrength;

    // Direction towards the sun, world space, normalised on the CPU.
    float fogSunDirX, fogSunDirY, fogSunDirZ;
    // Sun glow exponent: small values spread the glow over the sky, large ones tighten it.
    float fogSunSharpness;

    // Unit world-space camera basis, built on the CPU from eye/target rather than the view
    // matrix; FOV/aspect scaling is applied below so resizes need no re-push.
    float fogRightX, fogRightY, fogRightZ;
    float fogMaxDistance;

    float fogUpX, fogUpY, fogUpZ;
    float fogPad0;

    float fogForwardX, fogForwardY, fogForwardZ;
    float fogPad1;

    // >0.5 means the fog colour is specified relative to exposure, see FogExposureScale.
    float fogExposureRelative;
    // Same key value the auto-exposure and tonemap use (TonemapConstants.x).
    float fogExposureKey;
    float fogPad2, fogPad3;
}

// Matches ModelViewportEnvironment.CameraData: reverse-Z with a fixed FOV.
static const float FogNearPlane = 0.05;
static const float FogTanHalfFov = 0.41421356; // tan(45deg / 2)

struct VSOutput
{
    float4 pos : SV_POSITION;
    float2 ndc : TEXCOORD0;
};

struct PSOutput
{
    float4 color : SV_TARGET;
};

// Mean density along the ray, closed form for exponential height falloff:
//   d(h) = exp(-k * (h - href))
//   (1/L) * S d(h(t)) dt = (d(h0) - d(h1)) / (k * (h1 - h0))
// The near-horizontal branch is the k*(h1-h0) -> 0 limit; without it, divide by zero.
float FogAverageDensity(float h0, float h1)
{
    if (fogHeightFalloff < 1e-5)
    {
        return 1.0;
    }

    float d0 = exp(-fogHeightFalloff * (h0 - fogHeightRef));
    float dh = h1 - h0;
    if (abs(dh) < 1e-4)
    {
        return d0;
    }

    float d1 = exp(-fogHeightFalloff * (h1 - fogHeightRef));
    return (d0 - d1) / (fogHeightFalloff * dh);
}

// Pre-multiplies by adapted/key so the authored fog colour survives the tonemap unchanged,
// whatever absolute scene brightness happens to be.
float FogExposureScale()
{
    if (fogExposureRelative < 0.5)
    {
        // LDR pipeline: the frame is already display-referred.
        return 1.0;
    }

    float adapted = max(_AdaptTex.Load(int3(0, 0, 0)).r, 1e-4);
    return adapted / max(fogExposureKey, 1e-4);
}

PSOutput Main(in VSOutput input)
{
    PSOutput output;

    float2 viewportSize = viewData.viewport.zw;
    int2 pixel = clamp(int2(input.pos.xy), int2(0, 0), int2(viewportSize) - 1);
    float2 uv = input.pos.xy / viewportSize;

    float4 scene = _SceneTex.Sample(_SceneTex_sampler, uv);

    // Deliberately not normalised: its projection on the camera axis is exactly 1, so the
    // world point is camPos + ray * zView with no division.
    float aspect = viewData.viewport.z / max(viewData.viewport.w, 1.0);
    float3 ray = float3(fogForwardX, fogForwardY, fogForwardZ)
        + float3(fogRightX, fogRightY, fogRightZ) * (input.ndc.x * FogTanHalfFov * aspect)
        + float3(fogUpX, fogUpY, fogUpZ) * (input.ndc.y * FogTanHalfFov);

    // Reverse-Z: zero means background, not zero depth, so the sky gets the far distance.
    float depth = DEPTH_FETCH(pixel);
    float zView = depth < 1e-6 ? fogMaxDistance : FogNearPlane / depth;

    float rayLength = length(ray);
    float distance = min(zView * rayLength, fogMaxDistance);

    float3 camPos = viewData.CameraWorldPos;
    float3 worldPos = camPos + ray * zView;

    // Subtracted from the ray length rather than thresholded: a threshold shows as a ring.
    float fogged = max(distance - fogStartDistance, 0.0);
    float optical = fogDensity * fogged * FogAverageDensity(camPos.y, worldPos.y);
    float amount = saturate(1.0 - exp(-optical)) * saturate(fogMaxOpacity);

    // Cheap stand-in for single scattering towards the sun.
    float3 viewDir = ray / max(rayLength, 1e-6);
    float sunDot = saturate(dot(viewDir, float3(fogSunDirX, fogSunDirY, fogSunDirZ)));
    float sunAmount = pow(sunDot, max(fogSunSharpness, 1e-3)) * saturate(fogSunStrength);

    float3 fogColor = lerp(
        float3(fogColorR, fogColorG, fogColorB),
        float3(fogSunColorR, fogSunColorG, fogSunColorB),
        sunAmount) * FogExposureScale();

    // Alpha comes from the scene: writing our own would knock out the icon baker background.
    output.color = float4(lerp(scene.rgb, fogColor, amount), scene.a);
    return output;
}
