// Shared tone-mapping curves, included by UnlitInstancedPS (LDR path) and TonemapPS (HDR path).
#ifndef TONEMAP_HLSL
#define TONEMAP_HLSL

// Khronos PBR Neutral (github.com/KhronosGroup/ToneMapping): linear below ~0.76, compresses
// only the top of the range, preserving saturation.
float3 PbrNeutralToneMap(float3 color)
{
    const float startCompression = 0.8 - 0.04;
    const float desaturation = 0.15;

    float x = min(color.r, min(color.g, color.b));
    float offset = x < 0.08 ? x - 6.25 * x * x : 0.04;
    color -= offset;

    float peak = max(color.r, max(color.g, color.b));

    // Single exit point: an early return in the branch trips FXC X4000 (uninitialized variable).
    // No divide-by-zero: peak >= 0.76 inside the branch.
    float3 result = color;
    if (peak >= startCompression)
    {
        float d = 1.0 - startCompression;
        float newPeak = 1.0 - d * d / (peak + d - startCompression);
        result = color * (newPeak / peak);

        float g = 1.0 - 1.0 / (desaturation * (peak - newPeak) + 1.0);
        result = lerp(result, newPeak.xxx, g);
    }

    return result;
}

// ACES, Narkowicz 2015 approximation; known hue shift on bright saturated colors (fit by
// luminance, not hue), which is why the curve is user-selectable rather than forced.
float3 AcesToneMap(float3 color)
{
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    return saturate((color * (a * color + b)) / (color * (c * color + d) + e));
}

// AgX (Troy Sobotka), polynomial approximation: filmic contrast without ACES's hue shift.
// Steps: AgX working space, log2 encode to [-12.47, +4.026] EV, 6th-degree sigmoid polynomial.
float3 AgxToneMap(float3 color)
{
    const float3x3 agxIn = float3x3(
        0.8425640, 0.0784000, 0.0792869,
        0.0423104, 0.8787391, 0.0791720,
        0.0423970, 0.0784000, 0.8791000);

    const float3x3 agxOut = float3x3(
         1.1968790, -0.0980210, -0.0990297,
        -0.0528968,  1.1519110, -0.0989634,
        -0.0529716, -0.0980186,  1.1510500);

    const float minEv = -12.47393;
    const float maxEv = 4.026069;

    color = mul(agxIn, max(color, 0.0));

    // Floor 1e-10, not zero: log2(0) is -inf and rides through the polynomial into NaN.
    color = clamp(log2(max(color, 1e-10)), minEv, maxEv);
    color = (color - minEv) / (maxEv - minEv);

    // Horner-form 6th-degree sigmoid - cheaper and more precise than pow.
    float3 x = color;
    float3 x2 = x * x;
    float3 x4 = x2 * x2;
    color = 15.5 * x4 * x2
          - 40.14 * x4 * x
          + 31.96 * x4
          - 6.868 * x2 * x
          + 0.4298 * x2
          + 0.1191 * x
          - 0.00232;

    color = mul(agxOut, color);
    return saturate(color);
}

// Mirrors EditorSettings.ToneCurveMode. Runtime select, not a keyword: a shader variant per
// curve would rebuild every preview PSO on a dropdown change.
#define TONE_CURVE_PBR_NEUTRAL 0
#define TONE_CURVE_ACES        1
#define TONE_CURVE_AGX         2

float3 ApplyToneCurve(float3 color, int mode)
{
    if (mode == TONE_CURVE_ACES)
    {
        return AcesToneMap(color);
    }

    if (mode == TONE_CURVE_AGX)
    {
        return AgxToneMap(color);
    }

    return PbrNeutralToneMap(color);
}

// Rec. 709 luma; auto-exposure metering uses the same weights (see LuminanceInitPS).
float TonemapLuminance(float3 color)
{
    return dot(color, float3(0.2126, 0.7152, 0.0722));
}

#endif
