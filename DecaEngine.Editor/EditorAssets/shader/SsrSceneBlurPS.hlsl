// Half-res blurred copy of the scene snapshot for SSR (see SsrPass.cs): rough rays read this
// instead of the sharp frame. 3x3 gaussian at a 2-source-pixel step over a bilinear downsample
// gives an effective ~6px full-res kernel; the ratio estimator covers the rest.
#include "SsrCommon.hlsl"

Texture2D _SceneTex;
SamplerState _SceneTex_sampler;

struct PSOutput
{
    float4 color : SV_TARGET;
};

static const float SsrBlurWeights[3] = { 0.27901, 0.44198, 0.27901 };

PSOutput Main(in VSOutput input)
{
    PSOutput output;

    // Half-res target: UV comes from THIS pass's viewport (viewport.zw is latched to render
    // size); normalized UV is identical in both resolutions.
    float2 halfSize = max(viewData.viewport.zw * 0.5, 1.0);
    float2 uv = input.pos.xy / halfSize;
    float2 texel = 1.0 / viewData.viewport.zw;

    float3 sum = float3(0.0, 0.0, 0.0);
    [unroll]
    for (int y = 0; y < 3; y++)
    {
        [unroll]
        for (int x = 0; x < 3; x++)
        {
            float2 offset = float2((x - 1) * 2.0, (y - 1) * 2.0) * texel;
            float3 c = _SceneTex.SampleLevel(_SceneTex_sampler, saturate(uv + offset), 0.0).rgb;

            // Firefly suppression: same /(1+lum) weighting as the resolve.
            c /= 1.0 + dot(c, float3(0.2126, 0.7152, 0.0722));
            sum += c * SsrBlurWeights[x] * SsrBlurWeights[y];
        }
    }

    sum /= max(1.0 - dot(sum, float3(0.2126, 0.7152, 0.0722)), 1e-3);
    output.color = float4(sum, 1.0);
    return output;
}
