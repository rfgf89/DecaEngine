Texture2D    g_texture;
SamplerState g_texture_sampler; // By convention, texture samplers must use the '_sampler' suffix

struct PSInput
{
    float4 pos  : SV_POSITION;
    float2 uv   : TEX_COORD;
};

struct PSOutput
{
    float4 color : SV_TARGET;
};

// On GLES3.0 (no separate shader objects) VS output names/structs must match PS inputs exactly.
PSOutput main(in PSInput input)
{
    PSOutput output;
    output.color = g_texture.Sample(g_texture_sampler, input.uv);
    return output;
}