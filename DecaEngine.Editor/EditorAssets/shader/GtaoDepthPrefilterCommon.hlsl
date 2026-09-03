// XeGTAO depth prefilter (mip 0): linearizes depth once into the target the mip chain
// and GTAO read. The only pass that touches multisampled depth; wrappers set DEPTH_FETCH.
#include "Instancing.hlsl"

cbuffer View
{
    ViewData viewData;
}

// Infinite reversed-Z near plane, same as SsaoCommon.hlsl.
static const float PrefilterNearPlane = 0.05;

// Max half float: the RGBA16F chain turns anything larger into inf, which poisons mips.
static const float MaxViewDepth = 65504.0;

struct VSOutput
{
    float4 pos : SV_POSITION;
    float2 ndc : TEXCOORD0;
};

struct PSOutput
{
    float4 color : SV_TARGET;
};

PSOutput Main(in VSOutput input)
{
    int2 pixel = int2(input.pos.xy);
    float raw = DEPTH_FETCH(pixel);

    // Reversed-Z clears to 0: background must map to the far cap, not to zero, or it
    // would act as an occluder pressed against the camera.
    float z = raw < 1e-7 ? MaxViewDepth : min(PrefilterNearPlane / raw, MaxViewDepth);

    PSOutput output;
    output.color = float4(z, 0.0, 0.0, 1.0);
    return output;
}
