Texture2D FontTexture ;
SamplerState FontTexture_sampler;

struct PSInput
{
    float4 pos : SV_POSITION;
    float2 uv  : TEX_COORD;
    float4 col : COLOR;
};

struct PSOutput
{
    float4 color : SV_TARGET;
};

PSOutput main(PSInput input)
{
    PSOutput output;
    float2 uv = input.uv;
    //uv.x = 1.0f - uv.x;

    float4 col = FontTexture.Sample(FontTexture_sampler, uv);

    output.color = input.col * col;
    return output;
}