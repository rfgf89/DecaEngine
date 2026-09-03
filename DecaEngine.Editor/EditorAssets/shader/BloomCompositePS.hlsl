// Bloom composite: mixes the finished halo into the frame. _SourceTex is a COPY of the frame
// (a target cannot be read and written at once, see BloomPass); _LowerTex is the top of the
// upsample chain.
#include "BloomCommon.hlsl"

// Declared only by the passes that read it (see BloomCommon.hlsl).
Texture2D    _LowerTex;
SamplerState _LowerTex_sampler;

PSOutput Main(in VSOutput input)
{
    PSOutput output;

    float2 uv = input.pos.xy * bloomTarget.zw;

    float4 scene = _SourceTex.Sample(_SourceTex_sampler, uv);
    float3 bloom = _LowerTex.Sample(_LowerTex_sampler, uv).rgb;

    // Additive, in LINEAR space before tonemap: scattering adds light; lerp would dim the source.
    // Normalized by chain length (bloomSource.x) so intensity doesn't depend on viewport size.
    float3 result = scene.rgb + bloom * (bloomParams.w / max(bloomSource.x, 1.0));

    // Alpha from the scene: own alpha would knock out the icon baker's transparent background
    // (same reason as SsgiCompositeCommon.hlsl and FogCommon.hlsl).
    output.color = float4(max(result, 0.0), scene.a);
    return output;
}
