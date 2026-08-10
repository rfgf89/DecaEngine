// Композит SSAO: финальный кадр (копия в _SceneTex, см. ForwardPass) умножается на размытое
// AO (_AoTex, 3x3 бокс - глушит шум спиральных тапов). Отдельный пасс вместо блендинга,
// потому что PSO-абстракция движка блендинг не описывает, а читать и писать один таргет
// одновременно нельзя.
#include "Instancing.hlsl"

Texture2D    _SceneTex;
SamplerState _SceneTex_sampler;
Texture2D    _AoTex;
SamplerState _AoTex_sampler;

cbuffer View
{
    ViewData viewData;
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

PSOutput Main(in VSOutput input)
{
    PSOutput output;

    float2 viewportSize = viewData.viewport.zw;
    float2 uv = input.pos.xy / viewportSize;
    float2 texel = 1.0 / viewportSize;

    float ao = 0.0;
    [unroll]
    for (int y = -1; y <= 1; y++)
    {
        [unroll]
        for (int x = -1; x <= 1; x++)
        {
            ao += _AoTex.Sample(_AoTex_sampler, uv + float2(x, y) * texel).r;
        }
    }
    ao /= 9.0;

    float4 scene = _SceneTex.Sample(_SceneTex_sampler, uv);
    output.color = float4(scene.rgb * ao, scene.a);
    return output;
}
