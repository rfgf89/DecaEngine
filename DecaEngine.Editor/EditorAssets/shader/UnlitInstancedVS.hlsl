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
    // xyz = тангент, w = знак битангента (B = cross(N, T) * w, см. ModelLoader.Vertex.Tangent).
    float4 tangent      : ATTRIB4;
    float4 color        : ATTRIB5;   // glTF COLOR_0, white when the mesh has none (see ModelLoader)
    float2 uv1          : ATTRIB6;   // glTF TEXCOORD_1 (AO uv set, see ModelLoader), zero when absent
};

struct PSInput
{
    float4 pos         : SV_POSITION;
    float3 normal      : NORMAL;
    float2 uv          : TEX_COORD;
    float3 worldPos    : TEXCOORD1;
    float4 tangent     : TEXCOORD2;
    float4 vertexColor : COLOR0;
    float2 uv1         : TEXCOORD3;
};

PSInput Main(in VSInput input)
{
    PSInput result;
    float4x4 instanceTransform = GPURenderInstances[input.instanceId].modelMatrix;

    float4 vertexPos = float4(input.pos, 1.0);
    vertexPos = mul(vertexPos, instanceTransform);

    result.pos = mul(vertexPos, viewData.viewProj);
    result.uv  = input.uv;
    result.normal = mul(input.normal, (float3x3)instanceTransform);
    result.worldPos = vertexPos.xyz;
    // Знак битангента (w) - не направление: трансформации не подлежит, едет как есть.
    result.tangent = float4(mul(input.tangent.xyz, (float3x3)instanceTransform), input.tangent.w);
    result.vertexColor = input.color;
    result.uv1 = input.uv1;

    return result;
}