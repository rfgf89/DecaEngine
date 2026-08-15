// Первое звено цепочки авто-экспозиции: HDR-кадр -> лог-яркость в маленький квадратный таргет
// (EyeAdaptationPassResources.LuminanceSize). Каждый тексель усредняет log2(luminance) по своей
// плитке кадра - 4x4 билинейных тапа, то есть подвыборка, а не честный бокс-фильтр: экспозиции
// хватает средней по кадру, а полный сбор всех пикселей стоил бы мип-цепочки на каждый кадр.
//
// Среднее берётся в ЛОГАРИФМЕ (геометрическое среднее): линейное среднее одна солнечная блямба
// в кадре утаскивает в разы, и сцена мгновенно проваливается в темноту.
#include "Tonemap.hlsl"
#include "EyeAdaptationCommon.hlsl"

Texture2D    _SceneTex;
SamplerState _SceneTex_sampler;

PSOutput Main(in VSOutput input)
{
    PSOutput output;

    // Плитка кадра, приходящаяся на этот тексель таргета (SV_POSITION здесь - координата в
    // таргете, потому что фуллскрин-треугольник рисуется во вьюпорт размером с таргет).
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
