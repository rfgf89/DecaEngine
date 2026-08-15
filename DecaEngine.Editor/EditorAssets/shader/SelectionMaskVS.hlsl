// Маска выделения Scene View (см. SelectionOutlineOverlay): силуэт выделенного объекта в отдельный
// таргет. Вершины приходят УЖЕ в мировом пространстве - CPU перезапекает буфер при смене
// выделения/трансформа (см. PrefabSceneViewport.SyncSelectionHighlight), поэтому ни матриц
// инстансов, ни инстансинга здесь нет. Пара - SelectionMaskPS.hlsl.
#include "Instancing.hlsl"

cbuffer View
{
    ViewData viewData;
}

struct VSInput
{
    float3 pos : ATTRIB0;
};

float4 Main(in VSInput input) : SV_POSITION
{
    return mul(float4(input.pos, 1.0), viewData.viewProj);
}
