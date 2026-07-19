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

// Note that if separate shader objects are not supported (this is only the case for old GLES3.0 devices), vertex
// shader output variable name must match exactly the name of the pixel shader input variable.
// If the variable has structure type (like in this example), the structure declarations must also be identical.
PSInput main(in VSInput input) 
{
    PSInput result;
    // Could use Built-in DecaEngine Macro MatrixFromRows(float4, float4, float4, float4)
    float4x4 instanceTransform = float4x4(
        input.matrixRow0,
        input.matrixRow1,
        input.matrixRow2,
        input.matrixRow3
    );
    // Apply rotation
    float4 vertexPos = mul(float4(input.pos, 1.0), g_rotation);
    // Apply instance-specific transformation
    vertexPos = mul(vertexPos, instanceTransform);
    // Apply view-projection matrix
    result.pos = mul(vertexPos, g_viewProj);
    result.uv  = input.uv;

    return result;
}