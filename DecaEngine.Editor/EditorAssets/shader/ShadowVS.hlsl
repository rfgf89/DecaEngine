#include "Instancing.hlsl"

StructuredBuffer<GPURenderInstance> GPURenderInstances;

cbuffer Light
{
    LightData lightData;
}

struct VSInput
{
    float3 pos          : ATTRIB0;
    float2 uv           : ATTRIB1;
    float3 normal       : ATTRIB2;
    int instanceId      : ATTRIB3;
};

float4 Main(in VSInput input) : SV_POSITION
{
    float4x4 instanceTransform = GPURenderInstances[input.instanceId].modelMatrix;
    float4 vertexPos = float4(input.pos, 1.0);
    vertexPos = mul(vertexPos, instanceTransform);

    // For the shadow pass, we don't need to select a cascade based on camera view depth.
    // Each cascade is rendered in a separate pass with its own light data.
    // The correct cascade matrix (light view-projection) is already in CascadeMatrix[0]
    // because the C# code sets up a different LightData for each cascade pass.
    return mul(vertexPos, lightData.CascadeMatrix[0]);
}