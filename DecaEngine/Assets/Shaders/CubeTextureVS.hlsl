cbuffer Constants
{
    float4x4 g_WorldViewProj;
};

struct VSInput
{
    float3 pos  : ATTRIB0;
    float2 uv   : ATTRIB1;
};

struct PSInput
{
    float4 pos  : SV_POSITION;
    float2 uv   : TEX_COORD;
};

PSInput main(in VSInput input)
{
    PSInput result;
    result.pos = mul(float4(input.pos, 1.0), g_WorldViewProj);
    result.uv = input.uv;
    return result;
}