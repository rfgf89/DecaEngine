// Композит SSGI: к финальному кадру (копия в _SceneTex, см. SsgiPass) аддитивно подмешивается
// размытый bounce (_GiTex, 3x3 бокс - глушит шум спиральных тапов). Отдельный пасс вместо
// блендинга по той же причине, что и у SsaoCompositePS.hlsl: PSO-абстракция движка блендинг
// не описывает, а читать и писать один таргет одновременно нельзя.
#include "Instancing.hlsl"

Texture2D    _SceneTex;
SamplerState _SceneTex_sampler;
Texture2D    _GiTex;
SamplerState _GiTex_sampler;

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

    float3 gi = float3(0.0, 0.0, 0.0);
    [unroll]
    for (int y = -1; y <= 1; y++)
    {
        [unroll]
        for (int x = -1; x <= 1; x++)
        {
            gi += _GiTex.Sample(_GiTex_sampler, uv + float2(x, y) * texel).rgb;
        }
    }
    gi /= 9.0;

    // Аддитивно, БЕЗ модуляции цветом сцены: bounce нужен именно там, где прямого света мало
    // (затенённая сторона), а там scene.rgb тёмный и умножение съело бы весь эффект.
    float4 scene = _SceneTex.Sample(_SceneTex_sampler, uv);
    output.color = float4(saturate(scene.rgb + gi), scene.a);
    return output;
}
