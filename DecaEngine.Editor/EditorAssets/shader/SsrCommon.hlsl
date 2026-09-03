// Same reconstruction convention as SsaoCommon/SsgiCommon: reversed-Z, fixed 45 deg FOV.
#ifndef SSR_COMMON_INCLUDED
#define SSR_COMMON_INCLUDED

#include "Instancing.hlsl"

cbuffer View
{
    ViewData viewData;
}

// Filled via UpdateBuffer, not SetConstant: rebinding an SRB variable mid-frame is unsafe.
// Scalars are laid out to satisfy cbuffer packing; see SsaoCommon.
cbuffer SsrConstants
{
    float ssrFrameIndex;
    // Perceptual roughness ceiling: reflections fade out above it.
    float ssrMaxRoughness;
    // Surface thickness in world units for the intersection test.
    float ssrThickness;
    // Ray range in world units.
    float ssrMaxDistance;

    // Must match PbrEnvYaw in UnlitInstancedPS: composite subtracts the forward pass env color.
    float ssrEnvYaw;
    // Temporal history weight (0..0.97).
    float ssrHistoryWeight;
    // 0 - normal, 1 - reflections only (rgb*conf), 2 - confidence, 3 - G-buffer normals.
    float ssrDebugView;
    // Artistic multiplier; 1 is energy-correct.
    float ssrIntensity;

    // Direction TOWARDS the sun (world), for shading RT fallback hits.
    float4 ssrSunDirWorld;
    // rgb - sun color*intensity, w - ambient level applied to RT hits.
    float4 ssrSunColor;

    // x - reused ray pairs in resolve (1..4, clamped in shader). Rest is padding.
    float ssrRaysPerPixel;

    // TOTAL RT ray bounces (1..4). RT variant only.
    float ssrBounces;

    // 0 - screen march then RT for missed rays; 1 - RT immediately. FEATURE_RT_REFLECTIONS only.
    float ssrTraceMode;
    float ssrQualityPad2;

    // Probe field grid for shading RT hits; same origin/cell/counts as ProbeGrid* materials get.
    // origin.w = 1 means the field is bound; 0 means atlases hold a placeholder.
    float4 ssrProbeOrigin;
    float4 ssrProbeCell;
    float4 ssrProbeCounts;

    // PREVIOUS frame viewProj, for reprojecting the virtual mirror image. Identity until latched.
    float4x4 ssrPrevViewProj;
}

// Below this roughness the pixel is treated as a mirror: deterministic direction, analytic pdf,
// and trace stores ray LENGTH in rayHit.z. Shared by trace and resolve.
static const float SsrMirrorRoughness = 0.08;

// Mirror-path PDF at the lobe peak (H = N). Pi is literal: SsrPI is declared further down.
float SsrMirrorPdf(float roughness)
{
    float m = max(roughness * roughness, 1e-3);
    float m2 = m * m;
    return m2 / (3.14159265359 * m2 * m2);
}

// Copy of NonLinearIrradianceL1 from UnlitInstancedPS.hlsl; keep the two in sync.
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

float2 SsrProjectUv(float3 viewPos, float2 viewportSize)
{
    float aspect = viewportSize.x / max(viewportSize.y, 1.0);
    return float2(
        viewPos.x / (viewPos.z * SsrTanHalfFov * aspect) * 0.5 + 0.5,
        0.5 - viewPos.y / (viewPos.z * SsrTanHalfFov) * 0.5);
}

// Interleaved gradient noise (Jimenez) with a per-frame phase shift.
float SsrNoise(float2 pixel, float offset)
{
    pixel += offset * 5.588238;
    return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
}

// BRDF bias from the reference Stochastic SSR implementation (Frostbite/Stachowiak): the lobe
// sampling is pulled towards the mirror direction; resolve restores lobe shape via BRDF/PDF.
static const float SsrBrdfBias = 0.7;

// GGX importance sampling (Karis, "Real Shading in UE4"): half vector in xyz, pdf in w.
float4 SsrSampleGgxHalfVector(float3 N, float roughness, float u1, float u2)
{
    float m = max(roughness * roughness, 1e-3);
    float m2 = m * m;

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

// Ratio estimator weight for reusing a neighbour ray (Stachowiak, "Stochastic SSR").
// D-GGX * G-Walter without Fresnel: constant factors cancel against the weight sum.
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

// Octahedral direction packing into [0..1]^2: RT hits store a ray direction, not a screen UV.
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

// Equirect env map with GGX-prefiltered mips; must match PreviewEnvironmentMap's mip count.
static const float SsrEnvMipMax = 6.0;

float3 SsrSampleEnvironment(Texture2D envMap, SamplerState envSampler, float3 dir, float roughness)
{
    float2 uv = float2(atan2(dir.z, dir.x) / (2.0 * SsrPI) + 0.5 + ssrEnvYaw / (2.0 * SsrPI),
                       acos(clamp(dir.y, -1.0, 1.0)) / SsrPI);
    return envMap.SampleLevel(envSampler, uv, roughness * SsrEnvMipMax).rgb;
}

// Screen-edge falloff: hits near the border lose data, fade instead of popping.
float SsrEdgeFade(float2 uv)
{
    float2 fade = saturate((0.5 - abs(uv - 0.5)) / 0.08);
    return fade.x * fade.y;
}

float SsrRoughnessFade(float roughness)
{
    return 1.0 - smoothstep(ssrMaxRoughness * 0.7, ssrMaxRoughness, roughness);
}

// world -> view for directions; view is row-major orthonormal rotation plus translation.
float3 SsrWorldDirToView(float3 dir)
{
    return normalize(mul(float4(dir, 0.0), viewData.view).xyz);
}

// view -> world for directions: v*M = u  =>  v = u*M^T.
float3 SsrViewDirToWorld(float3 dir)
{
    return normalize(mul(dir, transpose((float3x3)viewData.view)));
}

#endif
