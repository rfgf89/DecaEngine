// Shared SSAO body (SsaoPS.hlsl / SsaoMsaaPS.hlsl wrappers define DEPTH_FETCH for
// single vs multisample depth). Fullscreen triangle (SkyBackgroundVS), grayscale AO out.
//
// Position reconstruction assumes infinite reversed-Z (MakePerspectiveReversedZ:
// z_clip = near, w = z_view), so z_view = near / depth. FOV fixed at 45 deg
// (ModelViewportEnvironment.CameraFovDegrees).
#include "Instancing.hlsl"

cbuffer View
{
    ViewData viewData;
}

// Mirrors AoConstantsData (SsaoPass.cs).
// Padding as three scalars, NOT float3: float3 at offset 4 breaks std140/SPIR-V
// 16-byte alignment and Vulkan shader legalization fails.
cbuffer AoConstants
{
    // World AO radius: fraction of the model's bounding radius (pushed by
    // ModelPreviewViewport.FrameAll); 0 = legacy screen-fraction mode.
    float aoWorldRange;
    // Graphics-window knobs (SsaoPassResources.SetStrength); <= 0 means defaults below.
    float aoPower;
    float aoFloor;
    float aoConstantsPad2;
}

static const float PI = 3.14159265359;
static const float TanHalfFov = 0.41421356; // tan(45deg / 2)
static const float NearPlane = 0.05;        // CameraData near in ModelViewportEnvironment

struct VSOutput
{
    float4 pos : SV_POSITION;
    float2 ndc : TEXCOORD0;
};

struct PSOutput
{
    float4 color : SV_TARGET;
};

float ViewDepthAt(int2 pixel, float2 viewportSize)
{
    pixel = clamp(pixel, int2(0, 0), int2(viewportSize) - 1);
    float d = DEPTH_FETCH(pixel);
    return NearPlane / max(d, 1e-7);
}

float3 ViewPosAt(int2 pixel, float2 viewportSize)
{
    float zView = ViewDepthAt(pixel, viewportSize);
    float2 uv = (pixel + 0.5) / viewportSize;
    float2 ndc = float2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
    float aspect = viewportSize.x / max(viewportSize.y, 1.0);
    return float3(ndc.x * TanHalfFov * aspect * zView, ndc.y * TanHalfFov * zView, zView);
}

PSOutput Main(in VSOutput input)
{
    PSOutput output;

    float2 viewportSize = viewData.viewport.zw;
    int2 pixel = int2(input.pos.xy);

    // Background (reversed-Z clears to 0) is not shaded.
    float centerRaw = DEPTH_FETCH(pixel);
    if (centerRaw < 1e-6)
    {
        output.color = float4(1.0, 1.0, 1.0, 1.0);
        return output;
    }

    float3 P = ViewPosAt(pixel, viewportSize);

    // Depth-normal reconstruction: pick the smaller of the +/-1 differences to
    // avoid silhouette discontinuities.
    float3 dxA = ViewPosAt(pixel + int2(1, 0), viewportSize) - P;
    float3 dxB = P - ViewPosAt(pixel - int2(1, 0), viewportSize);
    float3 dyA = ViewPosAt(pixel + int2(0, 1), viewportSize) - P;
    float3 dyB = P - ViewPosAt(pixel - int2(0, 1), viewportSize);
    float3 dx = abs(dxA.z) < abs(dxB.z) ? dxA : dxB;
    float3 dy = abs(dyA.z) < abs(dyB.z) ? dyA : dyB;
    float3 N = normalize(cross(dy, dx));
    if (N.z > 0.0)
    {
        N = -N;
    }

    const int TapCount = 10;
    const float ScreenRadius = 0.045;   // fraction of screen height
    const float RangeFraction = 0.18;   // falloff range as fraction of point depth
    const float Bias = 0.08;
    const float Intensity = 1.15;

    // World mode: falloff up to aoWorldRange, spiral radius is its pixel projection
    // at the point's depth (clamped so zoom cannot blow the tap step up to the
    // whole screen or collapse it at distance). Legacy (aoWorldRange = 0) is
    // screen-relative.
    float range = aoWorldRange > 0.0 ? aoWorldRange : RangeFraction * P.z;
    float radiusPixels = aoWorldRange > 0.0
        ? clamp(aoWorldRange * viewportSize.y / (2.0 * TanHalfFov * P.z), 2.0, 0.25 * viewportSize.y)
        : ScreenRadius * viewportSize.y;
    float noise = frac(sin(dot(float2(pixel), float2(12.9898, 78.233))) * 43758.5453) * 2.0 * PI;

    float occlusion = 0.0;
    [unroll]
    for (int t = 0; t < TapCount; t++)
    {
        float angle = noise + t * 2.39996; // golden angle
        float radius = (t + 0.5) / TapCount * radiusPixels;
        int2 tap = pixel + int2(round(float2(cos(angle), sin(angle)) * radius));

        float3 S = ViewPosAt(tap, viewportSize);
        float3 v = S - P;
        float dist = max(length(v), 1e-5);

        float falloff = saturate(1.0 - dist / range);
        occlusion += saturate(dot(N, v / dist) - Bias) * falloff;
    }

    float ao = saturate(1.0 - occlusion / TapCount * (aoPower > 0.01 ? aoPower * Intensity : Intensity));
    ao = max(ao, aoFloor >= 0.0 ? aoFloor : 0.0);
    output.color = float4(ao.xxx, 1.0);
    return output;
}
