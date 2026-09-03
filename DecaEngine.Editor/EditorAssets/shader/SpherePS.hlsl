struct PSInput
{
    float4 pos  : SV_POSITION;
    float2 uv   : TEX_COORD;
};

struct PSOutput
{
    float4 color : SV_TARGET;
};

PSOutput Main(in PSInput input)
{
    PSOutput output;
    output.color = float4(1.0f - input.uv.x, 0.0f, input.uv.y, 1.0f);
    return output;
}