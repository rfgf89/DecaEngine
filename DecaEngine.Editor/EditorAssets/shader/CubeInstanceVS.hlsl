#include "Instancing.hlsl"

StructuredBuffer<GPURenderInstance> GPURenderInstances;

cbuffer View
{
    ViewData viewData;
}

struct VSInput
{
    // Vertex attributes
    float3 pos          : ATTRIB0;
    float2 uv           : ATTRIB1;
    float3 normal       : ATTRIB2;
    int instanceId      : ATTRIB3;
};

struct PSInput 
{ 
    float4 pos      : SV_POSITION;
    float2 uv       : TEX_COORD;
    float3 normal   : NORMAL;
    float3 worldPos : WORLDPOS;
};

PSInput Main(in VSInput input)
{
    PSInput result;
    float4x4 instanceTransform = GPURenderInstances[input.instanceId].modelMatrix;

    float4 vertexPos = float4(input.pos, 1.0);
    vertexPos = mul(vertexPos, instanceTransform);

    result.worldPos = vertexPos.xyz;
    result.pos = mul(vertexPos, viewData.viewProj);
    result.uv  = input.uv;
    result.normal = mul(input.normal, (float3x3)instanceTransform);

    return result;
}