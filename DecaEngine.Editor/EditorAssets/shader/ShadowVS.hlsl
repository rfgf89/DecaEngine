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

// UV passes through for foliage alpha-test (see ShadowMaskedPS.hlsl); solid shadow PSOs bind
// no pixel shader at all, so the extra output costs nothing there.
struct VSOutput
{
    float4 pos : SV_POSITION;
    float2 uv  : TEXCOORD0;
};

VSOutput Main(in VSInput input)
{
    float4x4 instanceTransform = GPURenderInstances[input.instanceId].modelMatrix;
    float4 vertexPos = float4(input.pos, 1.0);
    vertexPos = mul(vertexPos, instanceTransform);

    VSOutput output;

    // Each cascade renders in its own pass with its own LightData, so CascadeMatrix[0]
    // already holds the right light view-projection.
    output.pos = mul(vertexPos, lightData.CascadeMatrix[0]);
    output.uv = input.uv;
    return output;
}
