// Копия UnlitInstancedVS.hlsl для материалов с ТОЧЕЧНОЙ топологией (glTF mode POINTS, см.
// ModelLoader.MeshTopologyPoints): Vulkan требует, чтобы VS пайплайна с POINT_LIST писал
// builtin PointSize (VUID-VkGraphicsPipelineCreateInfo-topology-08773) - обычный VS его не
// пишет, и такой PSO отбраковывается валидацией. Держать PSIZE в общем VS нельзя по той же
// причине наоборот: у не-точечных топологий он запрещён/бессмыслен.
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
    // xyz = тангент, w = знак битангента (см. ModelLoader.Vertex.Tangent).
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
    // Атрибут обязателен на Vulkan (builtin PointSize, VUID-08773), но FXC на D3D не знает
    // синтаксиса [[...]] (error X3000) - там PSIZE просто игнорируется (точки всегда 1px).
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

    // Достаточно крупные точки, чтобы «крапчатые» сферы точечных примитивов читались в превью
    // (1px на 512+ таргете почти невидим).
    result.pointSize = 3.0;

    return result;
}
