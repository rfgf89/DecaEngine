Texture2D       g_texture;
SamplerState    g_texture_sampler;

struct PSInput
{
    float4 pos      : SV_POSITION;
    float2 uv       : TEX_COORD;
};

struct PSOutput
{
    float4 color    : SV_TARGET;
};

PSOutput main(in PSInput input)
{
    PSOutput output;
    float2 uv = input.uv;
    uv.x = 1.0f - uv.x;
    output.color = g_texture.Sample(g_texture_sampler, uv);
    return output;
}