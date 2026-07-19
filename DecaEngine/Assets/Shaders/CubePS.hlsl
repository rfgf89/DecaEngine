struct PSInput
{
    float4 pos      : SV_POSITION;
    float4 color    : COLOR0;
};

struct PSOutput
{
    float4 color    : SV_TARGET;
};

PSOutput main(in PSInput input)
{
    PSOutput output;
    output.color = input.color;
    return output;
}