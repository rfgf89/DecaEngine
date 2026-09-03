cbuffer Constants
{
    float4x4 g_viewProj;
    float4x4 g_rotation;
};

struct VSInput
{
    // Vertex attributes
    float3 pos          : ATTRIB0;
    float2 uv           : ATTRIB1;

    // Instance attributes
    float4 matrixRow0   : ATTRIB2;
    float4 matrixRow1   : ATTRIB3;
    float4 matrixRow2   : ATTRIB4;
    float4 matrixRow3   : ATTRIB5;
};

struct PSInput
{
    float4 pos : SV_POSITION;
    float2 uv  : TEX_COORD;
};

// On GLES3.0 without separate shader objects, VS output names/structs must match PS inputs exactly.
PSInput main(in VSInput input)
{
    PSInput result;
    float4x4 instanceTransform = float4x4(
        input.matrixRow0,
        input.matrixRow1,
        input.matrixRow2,
        input.matrixRow3
    );
    float4 vertexPos = mul(float4(input.pos, 1.0), g_rotation);
    vertexPos = mul(vertexPos, instanceTransform);
    result.pos = mul(vertexPos, g_viewProj);
    result.uv  = input.uv;

    return result;
}