// Screen-space one-bounce GI body; wrappers define DEPTH_FETCH. Position/normal reconstruction
// assumes infinite reversed-Z (z_view = near / depth) and a fixed 45-degree FOV.
#include "Instancing.hlsl"

Texture2D _SceneTex;

cbuffer View
{
    ViewData viewData;
}

// Mirrors GiConstantsData (SsgiPass.cs). Pad with scalars, never float3: a float3 at offset 4
// breaks std140/SPIR-V 16-byte alignment.
cbuffer GiConstants
{
    // World-space gather radius; 0 selects the legacy screen-fraction radius.
    float giWorldRange;
    float giIntensity;
    // Taps per pixel, clamped to 4..GiMaxTaps.
    float giSampleCount;
    // Per-tap firefly clamp in luminance; <= 0 disables it.
    float giMaxLuminance;
    // 1 keeps the sender's color, 0 gives a grey bounce.
    float giSaturation;
    float giConstantsPad0;
    float giConstantsPad1;
    float giConstantsPad2;
}

static const float PI = 3.14159265359;
static const float TanHalfFov = 0.41421356; // tan(45deg / 2)
static const float NearPlane = 0.05;        // must match CameraData near

// Fixed loop bound: a cbuffer-driven tap count cannot be unrolled by the compiler.
static const int GiMaxTaps = 32;

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

// Depth-derived normal: takes the smaller of the +/-1 differences to avoid silhouette breaks.
float3 NormalAt(int2 pixel, float3 P, float2 viewportSize)
{
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
    return N;
}

// Interleaved gradient noise (Jimenez): neighbours in the bilateral blur window get
// complementary spiral rotations, so the same tap count leaves far less residual noise.
float GiDitherNoise(float2 pixel)
{
    return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
}

PSOutput Main(in VSOutput input)
{
    PSOutput output;

    float2 viewportSize = viewData.viewport.zw;
    int2 pixel = int2(input.pos.xy);

    // Background: reversed-Z clears to zero.
    float centerRaw = DEPTH_FETCH(pixel);
    if (centerRaw < 1e-6)
    {
        output.color = float4(0.0, 0.0, 0.0, 1.0);
        return output;
    }

    float3 P = ViewPosAt(pixel, viewportSize);
    float3 N = NormalAt(pixel, P, viewportSize);

    // Gather over hemisphere directions, not a screen-space disc: on a flat floor every disc
    // neighbour lies in the plane, so dot(N, dir) = 0 and no tap ever contributes.
    const float RangeFraction = 0.45; // legacy radius, as a fraction of the point's view z

    int tapCount = (int)clamp(giSampleCount, 4.0, (float)GiMaxTaps);
    float intensity = giIntensity > 0.0 ? giIntensity : 1.0;
    float range = giWorldRange > 0.0 ? giWorldRange : RangeFraction * P.z;

    // Blocker thickness as a fraction of range: surfaces far in front of the sample are unrelated
    // foreground geometry and would halo silhouettes with their color.
    const float ThicknessFraction = 0.6;

    float3 up = abs(N.z) < 0.9 ? float3(0.0, 0.0, 1.0) : float3(1.0, 0.0, 0.0);
    float3 tangent = normalize(cross(up, N));
    float3 bitangent = cross(N, tangent);

    float dither = GiDitherNoise(float2(pixel));
    float aspect = viewportSize.x / max(viewportSize.y, 1.0);

    float3 bounce = float3(0.0, 0.0, 0.0);
    [loop]
    for (int t = 0; t < GiMaxTaps; t++)
    {
        if (t >= tapCount)
        {
            break;
        }

        // Cosine-weighted hemisphere: the receiver cosine and 1/PI cancel, so taps need no weight.
        float u1 = (t + dither) / tapCount;
        float discR = sqrt(saturate(u1));
        float discA = (t + dither) * 2.39996; // golden angle
        float3 d = tangent * (discR * cos(discA))
                 + bitangent * (discR * sin(discA))
                 + N * sqrt(saturate(1.0 - u1));

        // Step distance needs its own stratum: reusing u1 ties direction to range.
        float u2 = frac(dither + t * 0.7548776662);
        float3 S = P + d * (range * max(u2, 0.05));
        if (S.z <= NearPlane)
        {
            continue; // sample behind the near plane
        }

        float2 sUv = float2(
            S.x / (S.z * TanHalfFov * aspect) * 0.5 + 0.5,
            0.5 - S.y / (S.z * TanHalfFov) * 0.5);
        if (any(sUv < 0.0) || any(sUv > 1.0))
        {
            continue; // off screen: nothing is known about light from there
        }

        int2 tap = clamp(int2(sUv * viewportSize), int2(0, 0), int2(viewportSize) - 1);
        float tapRaw = DEPTH_FETCH(tap);
        if (tapRaw < 1e-6)
        {
            continue; // background along this direction
        }

        float3 B = ViewPosAt(tap, viewportSize);
        if (B.z > S.z)
        {
            continue; // surface is behind the sample: direction is unoccluded
        }
        if (S.z - B.z > ThicknessFraction * range)
        {
            continue;
        }

        float3 v = B - P;
        float dist = length(v);
        if (dist > range || dist < 1e-4)
        {
            continue;
        }
        if (dot(N, v) <= 0.0)
        {
            continue; // blocker below the receiver plane
        }

        // Sender cosine is skipped on purpose: a NormalAt per tap costs 4 extra depth reads and
        // hit the D3D12 frame-wait timeout on large viewports.
        float falloff = saturate(1.0 - dist / range);
        float3 tapColor = _SceneTex.Load(int3(tap, 0)).rgb;

        // Clamp luminance before weighting and keep the hue: a per-channel clamp turns white.
        if (giMaxLuminance > 0.0)
        {
            float lum = dot(tapColor, float3(0.2126, 0.7152, 0.0722));
            tapColor *= min(1.0, giMaxLuminance / max(lum, 1e-4));
        }

        float tapLum = dot(tapColor, float3(0.2126, 0.7152, 0.0722));
        tapColor = lerp(tapLum.xxx, tapColor, saturate(giSaturation));

        bounce += tapColor * falloff;
    }

    output.color = float4(bounce / tapCount * intensity, 1.0);
    return output;
}
