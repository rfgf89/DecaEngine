// GTAO linear-depth mip chain (XeGTAO_DepthMIPFilter): halves the previous level.
// Not a box filter: depths weighted by main-pass falloff vs the farthest of the four.
#include "Instancing.hlsl"
#include "GtaoShared.hlsl"

Texture2D _SourceTex;
SamplerState _SourceTex_sampler;

cbuffer View
{
    ViewData viewData;
}

// Mirrors AoConstantsData (SsaoPass.cs): same world radius as the main pass.
cbuffer AoConstants
{
    float aoWorldRange;
    float aoPower;
    float aoFloor;
    float aoConstantsPad2;
}

// Mirrors GtaoLevelData (SsaoPass.cs): viewData.viewport stays at full frame size.
cbuffer GtaoLevel
{
    float4 gtaoTargetSize; // xy = size, zw = 1/xy
    float4 gtaoSourceSize;
}

// XeGTAO depthRangeScaleFactor: filter radius stays slightly under the effect radius.
static const float DepthRangeScaleFactor = 0.75;

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
    int2 dst = int2(input.pos.xy);
    int2 src = dst * 2;
    int2 maxSrc = int2(gtaoSourceSize.xy) - 1;

    float depth0 = _SourceTex.Load(int3(min(src + int2(0, 0), maxSrc), 0)).r;
    float depth1 = _SourceTex.Load(int3(min(src + int2(1, 0), maxSrc), 0)).r;
    float depth2 = _SourceTex.Load(int3(min(src + int2(0, 1), maxSrc), 0)).r;
    float depth3 = _SourceTex.Load(int3(min(src + int2(1, 1), maxSrc), 0)).r;

    float maxDepth = max(max(depth0, depth1), max(depth2, depth3));

    float effectRadius = DepthRangeScaleFactor * GtaoEffectRadius(maxDepth, viewData.viewport.w, aoWorldRange);
    float falloffRange = max(GtaoFalloffRange * effectRadius, 1e-5);
    float falloffFrom = effectRadius * (1.0 - GtaoFalloffRange);
    float falloffMul = -1.0 / falloffRange;
    float falloffAdd = falloffFrom / falloffRange + 1.0;

    float weight0 = saturate((maxDepth - depth0) * falloffMul + falloffAdd);
    float weight1 = saturate((maxDepth - depth1) * falloffMul + falloffAdd);
    float weight2 = saturate((maxDepth - depth2) * falloffMul + falloffAdd);
    float weight3 = saturate((maxDepth - depth3) * falloffMul + falloffAdd);

    // The farthest depth always weighs 1, so the sum can never collapse to zero.
    float weightSum = weight0 + weight1 + weight2 + weight3;
    float filtered = (weight0 * depth0 + weight1 * depth1 + weight2 * depth2 + weight3 * depth3) / weightSum;

    PSOutput output;
    output.color = float4(min(filtered, GtaoMaxViewDepth), 0.0, 0.0, 1.0);
    return output;
}
