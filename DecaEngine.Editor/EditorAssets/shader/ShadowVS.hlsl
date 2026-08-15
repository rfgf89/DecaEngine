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

// UV проносится сквозь вершинный шейдер РАДИ АЛЬФА-ТЕСТА листвы (см. ShadowMaskedPS.hlsl): без него
// каждый лист попадает в shadow map сплошным прямоугольником квада, крона отбрасывает монолитную
// кляксу вместо кружева, и никакие god rays сквозь неё не пробиваются.
//
// Сплошному (не-масочному) теневому PSO пиксельный шейдер по-прежнему не назначается вовсе, и
// лишний выход ему ничего не стоит: держать ради этого второй вариант вершинного шейдера дороже,
// чем пронести две компоненты.
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

    // For the shadow pass, we don't need to select a cascade based on camera view depth.
    // Each cascade is rendered in a separate pass with its own light data.
    // The correct cascade matrix (light view-projection) is already in CascadeMatrix[0]
    // because the C# code sets up a different LightData for each cascade pass.
    output.pos = mul(vertexPos, lightData.CascadeMatrix[0]);
    output.uv = input.uv;
    return output;
}
