// Звено ВВЕРХ по цепочке блума: тентовый апсэмпл нижнего (более размытого) уровня плюс уровень
// этого разрешения. См. BloomCommon.hlsl.
#include "BloomCommon.hlsl"

PSOutput Main(in VSOutput input)
{
    PSOutput output;

    float2 uv = input.pos.xy * bloomTarget.zw;

    // Радиус в текселях ТАРГЕТА, а не источника: тент обязан быть одинаковой ширины в экранных
    // пикселях на всех уровнях, иначе крупные уровни размывались бы вдвое слабее мелких и ореол
    // получал бы видимые кольца на границах уровней.
    float2 o = bloomTarget.zw * max(bloomParams.z, 0.0);

    // 3x3 тент (веса 1-2-1 по обеим осям, сумма 16). Билинейная выборка нижнего уровня и так
    // сглаживает, но её одной мало: между уровнями вдвое разное разрешение, и без тента переход
    // виден как ступенька яркости вокруг источника.
    float3 s = _LowerTex.Sample(_LowerTex_sampler, uv + float2(-o.x,  o.y)).rgb;
    s += _LowerTex.Sample(_LowerTex_sampler, uv + float2( 0.0,   o.y)).rgb * 2.0;
    s += _LowerTex.Sample(_LowerTex_sampler, uv + float2( o.x,   o.y)).rgb;

    s += _LowerTex.Sample(_LowerTex_sampler, uv + float2(-o.x,  0.0)).rgb * 2.0;
    s += _LowerTex.Sample(_LowerTex_sampler, uv).rgb * 4.0;
    s += _LowerTex.Sample(_LowerTex_sampler, uv + float2( o.x,  0.0)).rgb * 2.0;

    s += _LowerTex.Sample(_LowerTex_sampler, uv + float2(-o.x, -o.y)).rgb;
    s += _LowerTex.Sample(_LowerTex_sampler, uv + float2( 0.0,  -o.y)).rgb * 2.0;
    s += _LowerTex.Sample(_LowerTex_sampler, uv + float2( o.x, -o.y)).rgb;

    s *= 1.0 / 16.0;

    // Накопление СЛОЖЕНИЕМ, а не lerp: каждый уровень - это своя полоса частот ореола, и они
    // складываются, а не заменяют друг друга. Нормировку на число уровней делает композит одной
    // общей интенсивностью.
    float3 current = _SourceTex.Sample(_SourceTex_sampler, uv).rgb;

    output.color = float4(max(current + s, 0.0), 1.0);
    return output;
}
