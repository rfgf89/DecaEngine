// Color grading + vignette, final pass of the frame (see ColorGradePass).
// Runs deliberately in display space on the RGBA8 frame, not in linear before tonemap:
// grading scales (0.5 = midtone, 1.0 = white) are defined in gamma space.
// Uses its own frame copy, not SceneCopyTarget: that one is RGBA16F in HDR, so CopyTexture
// would mismatch formats. Ordered after tonemap but before overlays (selection/gizmo are UI).
#include "Instancing.hlsl"

Texture2D    _SceneTex;
SamplerState _SceneTex_sampler;

cbuffer View
{
    ViewData viewData;
}

// Must match ColorGradeConstantsData (ColorGradePass.cs).
cbuffer GradeConstants
{
    // x = saturation, y = contrast, z = gamma, w = temperature.
    float4 gradeParams;
    // x = tint, y = vignette strength, z = vignette radius, w = edge softness.
    float4 gradeParams2;
    // xyz = shadow tint (additive), w = vignette aspect stretch.
    float4 gradeShadowTint;
    // xyz = highlight tint (multiplicative), w = reserved.
    float4 gradeHighlightTint;
    // xy = target size, zw = 1/xy.
    float4 gradeTarget;
}

struct VSOutput
{
    float4 pos : SV_POSITION;
    float2 ndc : TEXCOORD0;
};

struct PSOutput
{
    float4 color : SV_TARGET;
};

static const float3 LumaWeights = float3(0.2126, 0.7152, 0.0722);

// White balance: a warm/cool approximation, not Planckian colorimetry - judged by eye.
// Normalized by luma so the knob does not also change exposure.
float3 GradeWhiteBalance(float temperature, float tint)
{
    float3 wb = float3(
        1.0 + 0.30 * temperature,
        1.0 - 0.30 * tint,
        1.0 - 0.30 * temperature);

    return wb / max(dot(wb, LumaWeights), 1e-4);
}

PSOutput Main(in VSOutput input)
{
    PSOutput output;

    float2 uv = input.pos.xy * gradeTarget.zw;
    float4 scene = _SceneTex.Sample(_SceneTex_sampler, uv);
    float3 c = max(scene.rgb, 0.0);

    // 1. White balance first: it corrects the source, not the graded result.
    c *= GradeWhiteBalance(gradeParams.w, gradeParams2.x);

    // 2. Shadow tint additive, highlight tint multiplicative: lift/gain without the wheel.
    c = c * gradeHighlightTint.rgb + gradeShadowTint.rgb;

    // 3. Gamma before contrast/saturation: it redistributes midtones they operate on.
    c = pow(max(c, 0.0), 1.0 / max(gradeParams.z, 1e-3));

    // 4. Contrast pivots at 0.5, not 0.18: midtone is half scale in gamma space.
    c = (c - 0.5) * gradeParams.y + 0.5;

    // 5. Saturation last so it sees the final tone.
    float luma = dot(max(c, 0.0), LumaWeights);
    c = lerp(luma.xxx, c, gradeParams.x);

    // 6. Vignette radius measured in aspect-stretched coords so it stays circular.
    float2 d = uv - 0.5;
    d.x *= lerp(1.0, gradeTarget.x / max(gradeTarget.y, 1.0), saturate(gradeShadowTint.w));

    float radius = max(gradeParams2.z, 1e-3);
    float smoothWidth = max(gradeParams2.w, 1e-3);

    // smoothstep high-to-low: zero at the frame edge, one at the center.
    float v = smoothstep(radius, max(radius - smoothWidth, 0.0), length(d));
    c *= lerp(1.0, v, saturate(gradeParams2.y));

    // Alpha comes FROM the scene: previews clear to transparent, and writing our own alpha
    // would break the ImGui backdrop and icon baker (same reason as FogCommon.hlsl).
    output.color = float4(saturate(c), scene.a);
    return output;
}
