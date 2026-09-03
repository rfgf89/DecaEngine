Texture2DArray  g_texture;
SamplerState    g_texture_sampler; // Diligent convention: sampler name = texture name + '_sampler'

struct PSInput
{
    float4 pos      : SV_POSITION;
    float2 uv       : TEX_COORD;
    float texIndex  : TEX_ARRAY_INDEX;
};

struct PSOutput
{
    float4 color    : SV_TARGET;
};

// Without separate shader objects (old GLES3.0), VS output names/structs must match PS inputs exactly.
PSOutput main(in PSInput input)
{
    PSOutput output;
    output.color = g_texture.Sample(g_texture_sampler, float3(input.uv, input.texIndex));
    return output;
}