Texture2DArray  g_texture;
SamplerState    g_texture_sampler; // By convention, texture samplers must use the '_sampler' suffix

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

// Note that if separate shader objects are not supported (this is only the case for old GLES3.0 devices), vertex
// shader output variable name must match exactly the name of the pixel shader input variable.
// If the variable has structure type (like in this example), the structure declarations must also be identical.
PSOutput main(in PSInput input)
{
    PSOutput output;
    output.color = g_texture.Sample(g_texture_sampler, float3(input.uv, input.texIndex));
    return output;
}