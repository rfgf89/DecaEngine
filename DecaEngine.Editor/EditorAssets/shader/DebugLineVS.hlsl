// Debug lines; vertices arrive already in world space. Paired with DebugLinePS.hlsl.
#include "Instancing.hlsl"

cbuffer View
{
    ViewData viewData;
}

cbuffer DebugLineParams
{
    // x - brightness multiplier (lines are written to the HDR target before tonemap), yzw spare.
    float4 debugLineParams;
}

struct VSInput
{
    float3 pos : ATTRIB0;
    float4 color : ATTRIB1;
};

struct VSOutput
{
    float4 pos : SV_POSITION;
    float4 color : COLOR0;
};

VSOutput Main(in VSInput input)
{
    VSOutput output;

    // Alpha is a live-vertex flag, not opacity: the draw's vertex count is baked into the frozen
    // graph command, so leftover vertices are killed here by pushing them outside clip space.
    if (input.color.a <= 0.0)
    {
        output.pos = float4(2.0, 2.0, 2.0, 1.0);
        output.color = float4(0.0, 0.0, 0.0, 0.0);
        return output;
    }

    output.pos = mul(float4(input.pos, 1.0), viewData.viewProj);
    output.color = float4(input.color.rgb * debugLineParams.x, 1.0);

    return output;
}
