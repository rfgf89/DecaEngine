// Log-luminance reduction step: the source is exactly 8x the target per axis, so 8x8 taps cover it
// fully. Chain is 64x64 -> 8x8 -> 1x1, same shader with different bindings.
#include "EyeAdaptationCommon.hlsl"

Texture2D    _LumTex;
SamplerState _LumTex_sampler;

PSOutput Main(in VSOutput input)
{
    PSOutput output;

    float2 tile = floor(input.pos.xy) * 8.0;

    float sum = 0.0;
    [unroll]
    for (int y = 0; y < 8; y++)
    {
        [unroll]
        for (int x = 0; x < 8; x++)
        {
            float2 uv = (tile + float2(x, y) + 0.5) * EaSource.zw;
            sum += _LumTex.SampleLevel(_LumTex_sampler, uv, 0).r;
        }
    }

    output.color = (sum / 64.0).xxxx;
    return output;
}
