// Scene View selection mask (see SelectionOutlineOverlay): silhouette into its own target.
// Vertices arrive ALREADY in world space (CPU rebakes the buffer on selection/transform change),
// so no instance matrices or instancing here. Pixel half: SelectionMaskPS.hlsl.
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
