cbuffer Constants
{
    float4x4 g_WorldViewProj;
};

struct VSInput
{
    float3 pos      : ATTRIB0;
    float4 color    : ATTRIB1;
};

struct PSInput
{
    float4 pos      : SV_POSITION;
    float4 color    : COLOR0;
};

PSInput main(in VSInput input)
{
    PSInput result;
    result.pos      = mul(float4(input.pos, 1.0), g_WorldViewProj);
    result.color    = input.color;
    return result;
}