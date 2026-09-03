// Copy of UnlitInstancedVS.hlsl for POINT-topology materials (glTF mode POINTS): Vulkan
// requires a POINT_LIST pipeline's VS to write builtin PointSize (VUID-08773), and PSIZE
// cannot live in the shared VS because non-point topologies forbid it.
#include "Instancing.hlsl"

StructuredBuffer<GPURenderInstance> GPURenderInstances;

cbuffer View
{
    ViewData viewData;
}

struct VSInput
{
    float3 pos          : ATTRIB0;
    float2 uv           : ATTRIB1;
    float3 normal       : ATTRIB2;
    int instanceId      : ATTRIB3;
    // xyz = tangent, w = bitangent sign (see ModelLoader.Vertex.Tangent).
    float4 tangent      : ATTRIB4;
    float4 color        : ATTRIB5;
    float2 uv1          : ATTRIB6;
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
    // Required on Vulkan (builtin PointSize, VUID-08773); FXC rejects [[...]] syntax
    // (error X3000), so on D3D PSIZE is simply ignored (points are always 1px).
#if DECA_VULKAN
    [[vk::builtin("PointSize")]]
#endif
    float pointSize    : PSIZE;
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
    result.tangent = float4(mul(input.tangent.xyz, (float3x3)instanceTransform), input.tangent.w);
    result.vertexColor = input.color;
    result.uv1 = input.uv1;

    // Points large enough to read in previews (1px is nearly invisible on 512+ targets).
    result.pointSize = 3.0;

    return result;
}
