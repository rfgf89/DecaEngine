// First link of auto exposure: HDR frame -> log luminance in a small square target.
// 4x4 taps per texel is a subsample, not a box filter. The mean is taken in log space
// (geometric mean): a linear mean lets one sun highlight crush the whole scene dark.
#include "Tonemap.hlsl"
#include "EyeAdaptationCommon.hlsl"

Texture2D    _SceneTex;
SamplerState _SceneTex_sampler;

PSOutput Main(in VSOutput input)
{
    PSOutput output;

    // SV_POSITION is a target coordinate: the fullscreen triangle uses a target-sized viewport.
    float2 tile = floor(input.pos.xy);

    float sum = 0.0;
    [unroll]
    for (int y = 0; y < 4; y++)
    {
        [unroll]
        for (int x = 0; x < 4; x++)
        {
            float2 uv = (tile + (float2(x, y) + 0.5) * 0.25) * EaTarget.zw;
            float3 color = _SceneTex.SampleLevel(_SceneTex_sampler, uv, 0).rgb;
            sum += log2(max(TonemapLuminance(max(color, 0.0)), EaLuminanceEpsilon));
        }
    }

    output.color = (sum / 16.0).xxxx;
    return output;
}
