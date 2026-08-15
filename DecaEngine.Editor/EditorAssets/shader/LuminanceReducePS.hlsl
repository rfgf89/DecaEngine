// Звено редукции лог-яркости: источник ровно в 8 раз больше таргета по каждой оси, так что 8x8
// тапов покрывают его ПОЛНОСТЬЮ (в отличие от подвыборки init-пасса). Цепочка превью:
// 64x64 -> 8x8 -> 1x1, один и тот же шейдер с разными привязками (см. EyeAdaptationPass.cs).
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
