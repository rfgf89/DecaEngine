cbuffer Constants
{
    float4x4 g_WorldViewProj;
    float4x4 g_Rotation;
};

cbuffer InstanceData
{
    float4x4 g_InstanceTransform;
};

struct VSInput
{
    float3 pos      : ATTRIB0;
    float4 uv       : ATTRIB1;
};

struct PSInput
{
    float4 pos      : SV_POSITION;
    float2 uv       : TEX_COORD;
};

PSInput main(in VSInput input)
{
    PSInput result;
    result.pos      = float4(input.pos, 1.0);
    result.pos      = mul(result.pos, g_Rotation);
    result.pos      = mul(result.pos, g_InstanceTransform);
    result.pos      = mul(result.pos, g_WorldViewProj);
    result.uv       = input.uv;
    return result;
}