#include "BloomCommon.hlsl"

PSOutput Main(in VSOutput input)
{
    PSOutput output;

    float2 uv = input.pos.xy * bloomTarget.zw;

    // Four taps at the source texel corners, not one centered: a single tap decimates and flickers.
    float2 o = bloomSource.zw;
    float3 c = _SourceTex.Sample(_SourceTex_sampler, uv + float2(-o.x, -o.y)).rgb
             + _SourceTex.Sample(_SourceTex_sampler, uv + float2( o.x, -o.y)).rgb
             + _SourceTex.Sample(_SourceTex_sampler, uv + float2(-o.x,  o.y)).rgb
             + _SourceTex.Sample(_SourceTex_sampler, uv + float2( o.x,  o.y)).rgb;
    c *= 0.25;

    // SSGI and fog sums can round below zero in RGBA16F; without this the chain turns to NaN.
    c = max(c, 0.0);

    // Threshold is tested against exposed luminance but subtracted from linear color.
    float exposure = BloomExposure();
    float luminance = BloomLuminance(c) * exposure;

    float threshold = bloomParams.x;
    float knee = max(bloomParams.y, 1e-4);

    // Karis soft knee: quadratic splice of width knee, C1-continuous across the threshold.
    float soft = clamp(luminance - threshold + knee, 0.0, 2.0 * knee);
    soft = soft * soft / (4.0 * knee);
    float weight = max(soft, luminance - threshold) / max(luminance, 1e-4);

    output.color = float4(c * weight, 1.0);
    return output;
}
