#include "Instancing.hlsl"

StructuredBuffer<GPURenderInstance> GPURenderInstances;

cbuffer Constants
{
    ViewData viewData;
}

struct VSInput
{
    float3 pos          : ATTRIB0;
    float2 uv           : ATTRIB1;
    int instanceId : ATTRIB2;
};

struct PSInput
{
    float4 pos : SV_POSITION;
    float2 uv  : TEX_COORD;
};

PSInput Main(in VSInput input)
{
    PSInput result;
    float4x4 instanceTransform = GPURenderInstances[input.instanceId].modelMatrix;
    float4 vertexPos = float4(input.pos, 1.0);
    vertexPos = mul(vertexPos, instanceTransform);
    result.pos = mul(vertexPos, viewData.viewProj);
    result.uv  = input.uv;

    return result;
}