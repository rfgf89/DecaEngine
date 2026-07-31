struct PSInput
{
    float4 pos : SV_POSITION;
};

struct PSOutput
{
    float4 color : SV_TARGET;
};

PSOutput main(in PSInput input)
{
    PSOutput output;
    output.color = float4(0.0, 1.0, 0.0, 1.0); // solid green
    return output;
}
