// Звено ВНИЗ по цепочке блума: 13-тапный даунсэмпл (Jimenez, SIGGRAPH 2014 / Call of Duty).
// См. BloomCommon.hlsl.
#include "BloomCommon.hlsl"

PSOutput Main(in VSOutput input)
{
    PSOutput output;

    float2 uv = input.pos.xy * bloomTarget.zw;
    float2 o = bloomSource.zw;

    // Тринадцать тапов - это четыре перекрывающихся квадрата 2x2 плюс центральный. Наивный бокс 2x2
    // на каждом уровне даёт «лестницу» на диагональных бликах и заметный алиасинг при движении:
    // соседние кадры выбирают разные тексели. Перекрытие квадратов усредняет эту разницу, и
    // цепочка перестаёт кипеть.
    float3 a = _SourceTex.Sample(_SourceTex_sampler, uv + float2(-2.0 * o.x,  2.0 * o.y)).rgb;
    float3 b = _SourceTex.Sample(_SourceTex_sampler, uv + float2( 0.0,        2.0 * o.y)).rgb;
    float3 c = _SourceTex.Sample(_SourceTex_sampler, uv + float2( 2.0 * o.x,  2.0 * o.y)).rgb;

    float3 d = _SourceTex.Sample(_SourceTex_sampler, uv + float2(-2.0 * o.x,  0.0)).rgb;
    float3 e = _SourceTex.Sample(_SourceTex_sampler, uv).rgb;
    float3 f = _SourceTex.Sample(_SourceTex_sampler, uv + float2( 2.0 * o.x,  0.0)).rgb;

    float3 g = _SourceTex.Sample(_SourceTex_sampler, uv + float2(-2.0 * o.x, -2.0 * o.y)).rgb;
    float3 h = _SourceTex.Sample(_SourceTex_sampler, uv + float2( 0.0,       -2.0 * o.y)).rgb;
    float3 i = _SourceTex.Sample(_SourceTex_sampler, uv + float2( 2.0 * o.x, -2.0 * o.y)).rgb;

    float3 j = _SourceTex.Sample(_SourceTex_sampler, uv + float2(-o.x,  o.y)).rgb;
    float3 k = _SourceTex.Sample(_SourceTex_sampler, uv + float2( o.x,  o.y)).rgb;
    float3 l = _SourceTex.Sample(_SourceTex_sampler, uv + float2(-o.x, -o.y)).rgb;
    float3 m = _SourceTex.Sample(_SourceTex_sampler, uv + float2( o.x, -o.y)).rgb;

    // Веса: центральный квадрат 0.5, четыре угловых по 0.125 - в сумме единица.
    float3 result = (j + k + l + m) * 0.125;
    result += (a + b + d + e) * 0.03125;
    result += (b + c + e + f) * 0.03125;
    result += (d + e + g + h) * 0.03125;
    result += (e + f + h + i) * 0.03125;

    output.color = float4(max(result, 0.0), 1.0);
    return output;
}
