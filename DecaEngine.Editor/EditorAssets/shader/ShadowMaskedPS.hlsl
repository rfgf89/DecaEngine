// Alpha test during shadow map writes, for cut-out geometry such as foliage
// (ShadowRenderer.RegisterAlphaTestedMaterial). Without it the depth-only pass writes the
// quad geometry, not the cut-out shape, so foliage shadows solid rectangles.
// The pass stays depth-only: no color targets, the shader only clips pixels.
Texture2D    _MainTex;
SamplerState _MainTex_sampler;

cbuffer ShadowMaterial
{
    // glTF alphaCutoff. Pushed ONCE at material creation: SetConstant re-binds the SRB
    // variable and must not run mid-frame (see FogPassResources).
    float shadowAlphaCutoff;
    float shadowPad0, shadowPad1, shadowPad2;
}

struct PSInput
{
    float4 pos : SV_POSITION;
    float2 uv  : TEXCOORD0;
};

void Main(in PSInput input)
{
    // Mip is NOT forced to 0: shadow texels are much coarser than screen texels and mip 0
    // sampling flickers; hardware mip selection averages alpha over the texel footprint.
    float alpha = _MainTex.Sample(_MainTex_sampler, input.uv).a;

    // Threshold slightly BELOW the material cutoff: shadows clipped at the color threshold
    // read a couple texels narrower than the foliage and sun leaks around each leaf.
    clip(alpha - shadowAlphaCutoff * 0.8);
}
