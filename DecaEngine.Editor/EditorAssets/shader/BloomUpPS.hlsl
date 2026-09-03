// Bloom up-chain link: tent upsample of the lower level added onto this resolution.
#include "BloomCommon.hlsl"

Texture2D    _LowerTex;
SamplerState _LowerTex_sampler;

PSOutput Main(in VSOutput input)
{
    PSOutput output;

    float2 uv = input.pos.xy * bloomTarget.zw;

    // Radius in TARGET texels: the tent must be equally wide in screen pixels on every level.
    float2 o = bloomTarget.zw * max(bloomParams.z, 0.0);

    // 3x3 tent, weights 1-2-1 per axis, sum 16.
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

    // Additive, not lerp: each level is its own frequency band; the composite normalizes.
    float3 current = _SourceTex.Sample(_SourceTex_sampler, uv).rgb;

    output.color = float4(max(current + s, 0.0), 1.0);
    return output;
}
