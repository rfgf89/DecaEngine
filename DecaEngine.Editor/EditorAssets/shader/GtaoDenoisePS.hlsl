// Edge-preserving GTAO denoiser (XeGTAO_Denoise), last stage of the pipeline (GtaoCommon.hlsl).
// Reads visibility + packed edges from one RGBA8 target and writes filtered visibility scaled
// back by GtaoOcclusionTermScale. Uses the main pass's edges instead of a depth-bilateral blur:
// they already encode same-surface membership with slope correction, cheaper and more accurate.
// Edges are asymmetric by construction, so center edges are multiplied by the neighbors'
// opposing edges to make the link symmetric (otherwise AO leaks one-way across silhouettes).
#include "Instancing.hlsl"
#include "GtaoShared.hlsl"

// Main pass output: .r = visibility / GtaoOcclusionTermScale, .g = packed edges.
Texture2D _AoTex;
SamplerState _AoTex_sampler;

cbuffer View
{
    ViewData viewData;
}

// Mirrors AoConstantsData (SsaoPass.cs); the denoiser only needs the visibility floor,
// which must apply after filtering.
cbuffer AoConstants
{
    float aoWorldRange;
    float aoPower;
    float aoFloor;
    float aoConstantsPad2;
}

// Diagonal weight: half the orthogonal one, with the 0.85 correction from XeGTAO.
static const float DiagWeight = 0.85 * 0.5;

// Small AO leak for nearly isolated pixels (3-4 closed sides), otherwise thin geometry is
// left alone with its noise (spatial and temporal aliasing).
static const float LeakThreshold = 2.5;
static const float LeakStrength = 0.5;

struct VSOutput
{
    float4 pos : SV_POSITION;
    float2 ndc : TEXCOORD0;
};

struct PSOutput
{
    float4 color : SV_TARGET;
};

float2 LoadAo(int2 pixel, float2 viewportSize)
{
    pixel = clamp(pixel, int2(0, 0), int2(viewportSize) - 1);
    return _AoTex.Load(int3(pixel, 0)).rg;
}

float4 LoadEdges(int2 pixel, float2 viewportSize)
{
    return GtaoUnpackEdges(LoadAo(pixel, viewportSize).g);
}

PSOutput Main(in VSOutput input)
{
    float2 viewportSize = viewData.viewport.zw;
    int2 pixel = int2(input.pos.xy);

    float4 edgesC = LoadEdges(pixel, viewportSize);
    float4 edgesL = LoadEdges(pixel + int2(-1, 0), viewportSize);
    float4 edgesR = LoadEdges(pixel + int2(1, 0), viewportSize);
    float4 edgesT = LoadEdges(pixel + int2(0, -1), viewportSize);
    float4 edgesB = LoadEdges(pixel + int2(0, 1), viewportSize);

    // Symmetrize: center-left edge times left neighbor's right edge, etc.
    edgesC *= float4(edgesL.y, edgesR.x, edgesT.w, edgesB.z);

    float edginess = (saturate(4.0 - LeakThreshold - dot(edgesC, float4(1.0, 1.0, 1.0, 1.0))) / (4.0 - LeakThreshold)) * LeakStrength;
    edgesC = saturate(edgesC + edginess);

    // A diagonal is reachable only through a continuous two-edge orthogonal path, so AO
    // does not jump across a diagonal silhouette.
    float weightTL = DiagWeight * (edgesC.x * edgesL.z + edgesC.z * edgesT.x);
    float weightTR = DiagWeight * (edgesC.z * edgesT.y + edgesC.y * edgesR.z);
    float weightBL = DiagWeight * (edgesC.w * edgesB.x + edgesC.x * edgesL.w);
    float weightBR = DiagWeight * (edgesC.y * edgesR.w + edgesC.w * edgesB.y);

    float sumWeight = GtaoDenoiseBlurBeta;
    float sum = LoadAo(pixel, viewportSize).r * sumWeight;

    sum += edgesC.x * LoadAo(pixel + int2(-1, 0), viewportSize).r;
    sum += edgesC.y * LoadAo(pixel + int2(1, 0), viewportSize).r;
    sum += edgesC.z * LoadAo(pixel + int2(0, -1), viewportSize).r;
    sum += edgesC.w * LoadAo(pixel + int2(0, 1), viewportSize).r;
    sumWeight += dot(edgesC, float4(1.0, 1.0, 1.0, 1.0));

    sum += weightTL * LoadAo(pixel + int2(-1, -1), viewportSize).r;
    sum += weightTR * LoadAo(pixel + int2(1, -1), viewportSize).r;
    sum += weightBL * LoadAo(pixel + int2(-1, 1), viewportSize).r;
    sum += weightBR * LoadAo(pixel + int2(1, 1), viewportSize).r;
    sumWeight += weightTL + weightTR + weightBL + weightBR;

    float ao = saturate(sum / sumWeight * GtaoOcclusionTermScale);

    // Floor from the Graphics window (SsaoPassResources.SetStrength); negative = shader default.
    ao = max(ao, aoFloor >= 0.0 ? aoFloor : GtaoDefaultFloor);

    PSOutput output;
    output.color = float4(ao.xxx, 1.0);
    return output;
}
