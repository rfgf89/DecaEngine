#ifndef _SHADOWS_FXH_
#define _SHADOWS_FXH_

#include "PCF.hlsl"

#ifndef FILTER_ACROSS_CASCADES
#   define FILTER_ACROSS_CASCADES 0
#endif

#ifndef BEST_CASCADE_SEARCH
#   define BEST_CASCADE_SEARCH 0
#endif

#ifndef NDC_MIN_Z
#   define NDC_MIN_Z 0.0
#endif

#ifndef F3NDC_XYZ_TO_UVD_SCALE
#   define F3NDC_XYZ_TO_UVD_SCALE float3(0.5, -0.5, 1.0)
#endif

// --- PCSS Constants ---
#define PCSS_LIGHT_RADIUS              0.05f
#define PCSS_BLOCKER_SEARCH_RADIUS_TEXELS 8
#define PCSS_MIN_PENUMBRA_SIZE_TEXELS  1.0f
#define PCSS_MAX_PENUMBRA_SIZE_TEXELS  25.0f

float GetDistanceToCascadeMargin(float3 f3PosInCascadeProjSpace, float4 f4MarginProjSpace)
{
    float4 f4DistToEdges;
    f4DistToEdges.xy = float2(1.0, 1.0) - f4MarginProjSpace.xy - abs(f3PosInCascadeProjSpace.xy);
    const float ZScale = 2.0 / (1.0 - NDC_MIN_Z);
    f4DistToEdges.z = (f3PosInCascadeProjSpace.z - (NDC_MIN_Z + f4MarginProjSpace.z)) * ZScale;
    f4DistToEdges.w = (1.0 - f4MarginProjSpace.w - f3PosInCascadeProjSpace.z) * ZScale;
    return min(min(f4DistToEdges.x, f4DistToEdges.y), min(f4DistToEdges.z, f4DistToEdges.w));
}

struct CascadeSamplingInfo
{
    int    iCascadeIdx;
    float2 f2UV;
    float  fDepth;
    float3 f3LightSpaceScale;
    float  fMinDistToMargin;
};

CascadeSamplingInfo GetCascadeSamplingInfo(ShadowMapAttribs ShadowAttribs,
                                           float3           f3PosInLightViewSpace,
                                           int              iCascadeIdx)
{
    CascadeAttribs Cascade = ShadowAttribs.Cascades[iCascadeIdx];
    float3 f3CascadeLightSpaceScale = Cascade.f4LightSpaceScale.xyz;
    float3 f3PosInCascadeProjSpace  = f3PosInLightViewSpace * f3CascadeLightSpaceScale + Cascade.f4LightSpaceScaledBias.xyz;
    CascadeSamplingInfo SamplingInfo;
    SamplingInfo.iCascadeIdx       = iCascadeIdx;
    SamplingInfo.f2UV              = f3PosInCascadeProjSpace.xy * float2(0.5, -0.5) + float2(0.5, 0.5);
    SamplingInfo.fDepth            = f3PosInCascadeProjSpace.z;
    SamplingInfo.f3LightSpaceScale = f3CascadeLightSpaceScale;
    SamplingInfo.fMinDistToMargin  = GetDistanceToCascadeMargin(f3PosInCascadeProjSpace, Cascade.f4MarginProjSpace);
    return SamplingInfo;
}

CascadeSamplingInfo FindCascade(ShadowMapAttribs ShadowAttribs,
                                float3           f3PosInLightViewSpace,
                                float            fCameraViewSpaceZ)
{
    CascadeSamplingInfo SamplingInfo;
    float3 f3PosInCascadeProjSpace  = float3(0.0, 0.0, 0.0);
    int    iCascadeIdx = 0;
#if BEST_CASCADE_SEARCH
    while (iCascadeIdx < ShadowAttribs.iNumCascades)
    {
        CascadeAttribs Cascade         = ShadowAttribs.Cascades[iCascadeIdx];
        SamplingInfo.f3LightSpaceScale = Cascade.f4LightSpaceScale.xyz;
        f3PosInCascadeProjSpace        = f3PosInLightViewSpace * SamplingInfo.f3LightSpaceScale + Cascade.f4LightSpaceScaledBias.xyz;
        SamplingInfo.fMinDistToMargin  = GetDistanceToCascadeMargin(f3PosInCascadeProjSpace, Cascade.f4MarginProjSpace);

        if (SamplingInfo.fMinDistToMargin > 0.0)
        {
            SamplingInfo.f2UV   = f3PosInCascadeProjSpace.xy * float2(0.5, -0.5) + float2(0.5, 0.5);
            SamplingInfo.fDepth = f3PosInCascadeProjSpace.z;
            break;
        }
        else
            iCascadeIdx++;
    }
#else
    [unroll]
    for(int i=0; i< 4; ++i)
    {
        float4 f4CascadeZEnd = ShadowAttribs.f4CascadeCamSpaceZEnd[i];
        float4 v = float4( f4CascadeZEnd.x < fCameraViewSpaceZ ? 1.0 : 0.0,
                           f4CascadeZEnd.y < fCameraViewSpaceZ ? 1.0 : 0.0,
                           f4CascadeZEnd.z < fCameraViewSpaceZ ? 1.0 : 0.0,
                           f4CascadeZEnd.w < fCameraViewSpaceZ ? 1.0 : 0.0);
	    iCascadeIdx += int(dot(float4(1.0, 1.0, 1.0, 1.0), v));
    }

    if (iCascadeIdx < ShadowAttribs.iNumCascades)
    {
        SamplingInfo = GetCascadeSamplingInfo(ShadowAttribs, f3PosInLightViewSpace, iCascadeIdx);
    }
#endif
    SamplingInfo.iCascadeIdx = iCascadeIdx;
    return SamplingInfo;
}

float GetNextCascadeBlendAmount(ShadowMapAttribs    ShadowAttribs,
                                float               fCameraViewSpaceZ,
                                CascadeSamplingInfo SamplingInfo,
                                CascadeSamplingInfo NextCscdSamplingInfo)
{
    float4 f4CascadeStartEndZ = ShadowAttribs.Cascades[SamplingInfo.iCascadeIdx].f4StartEndZ;
    float fDistToTransitionEdge = (f4CascadeStartEndZ.y - fCameraViewSpaceZ) / (f4CascadeStartEndZ.y - f4CascadeStartEndZ.x);

#if BEST_CASCADE_SEARCH
    fDistToTransitionEdge = max(fDistToTransitionEdge, SamplingInfo.fMinDistToMargin);
#endif

    return saturate(1.0 - fDistToTransitionEdge / ShadowAttribs.fCascadeTransitionRegion) *
           saturate(NextCscdSamplingInfo.fMinDistToMargin / 0.01);
}

float2 ComputeReceiverPlaneDepthBias(float3 ShadowUVDepthDX,
                                     float3 ShadowUVDepthDY)
{
    float2 biasUV;
    biasUV.x =   ShadowUVDepthDY.y * ShadowUVDepthDX.z - ShadowUVDepthDX.y * ShadowUVDepthDY.z;
    biasUV.y = - ShadowUVDepthDY.x * ShadowUVDepthDX.z + ShadowUVDepthDX.x * ShadowUVDepthDY.z;

    float Det = (ShadowUVDepthDX.x * ShadowUVDepthDY.y) - (ShadowUVDepthDX.y * ShadowUVDepthDY.x);
	biasUV /= sign(Det) * max( abs(Det), 1e-10 );
    return biasUV;
}

// --- PCSS Blocker Search ---
// Finds the average depth of samples that are closer to the light than the receiver.
float FindAverageBlockerDepth(in Texture2DArray<float> tex2DShadowMap,
                              in SamplerState          tex2DShadowMap_sampler, // Use regular sampler
                              in float4                f4ShadowMapSize,
                              in float2                f2UV,
                              in float                 fSlice,
                              in float                 fReceiverDepth)
{
    float avgBlockerDepth = 0.0;
    int numBlockers = 0;
    float invShadowMapSize = f4ShadowMapSize.z; // 1.0 / size

    for (int y = -PCSS_BLOCKER_SEARCH_RADIUS_TEXELS; y <= PCSS_BLOCKER_SEARCH_RADIUS_TEXELS; ++y)
    {
        for (int x = -PCSS_BLOCKER_SEARCH_RADIUS_TEXELS; x <= PCSS_BLOCKER_SEARCH_RADIUS_TEXELS; ++x)
        {
            float2 offset = float2(x, y) * invShadowMapSize;
            float blockerDepth = tex2DShadowMap.Sample(tex2DShadowMap_sampler, float3(f2UV + offset, fSlice));

            // A blocker is closer to the light, so its depth is smaller than the receiver's depth.
            if (blockerDepth < fReceiverDepth)
            {
                avgBlockerDepth += blockerDepth;
                numBlockers++;
            }
        }
    }

    if (numBlockers == 0)
        return fReceiverDepth; // No blockers, return receiver depth to get 0 penumbra.

    return avgBlockerDepth / numBlockers;
}


float FilterShadowCascade(in ShadowMapAttribs       ShadowAttribs,
                          in Texture2DArray<float>  tex2DShadowMap,
                          in SamplerComparisonState tex2DShadowMap_sampler,
                          in SamplerState           tex2DShadowMap_Linear_sampler, // Sampler for raw depth
                          in float3                 f3ddXPosInLightViewSpace,
                          in float3                 f3ddYPosInLightViewSpace,
                          in CascadeSamplingInfo    SamplingInfo)
{
    float3 f3ddXShadowMapUVDepth  = f3ddXPosInLightViewSpace * SamplingInfo.f3LightSpaceScale * F3NDC_XYZ_TO_UVD_SCALE;
    float3 f3ddYShadowMapUVDepth  = f3ddYPosInLightViewSpace * SamplingInfo.f3LightSpaceScale * F3NDC_XYZ_TO_UVD_SCALE;
    float2 f2DepthSlopeScaledBias = ComputeReceiverPlaneDepthBias(f3ddXShadowMapUVDepth, f3ddYShadowMapUVDepth);

    float2 f2SlopeScaledBiasClamp = abs( (SamplingInfo.f3LightSpaceScale.z  * F3NDC_XYZ_TO_UVD_SCALE.z) /
                                         (SamplingInfo.f3LightSpaceScale.xy * F3NDC_XYZ_TO_UVD_SCALE.xy) ) *
                                    ShadowAttribs.fReceiverPlaneDepthBiasClamp;
    f2DepthSlopeScaledBias = clamp(f2DepthSlopeScaledBias, -f2SlopeScaledBiasClamp, f2SlopeScaledBiasClamp);
    f2DepthSlopeScaledBias *= ShadowAttribs.f4ShadowMapDim.zw;

    float FractionalSamplingError = dot( float2(1.0, 1.0), abs(f2DepthSlopeScaledBias.xy) ) + ShadowAttribs.fFixedDepthBias;

    // --- PCSS Logic ---
    // 1. Blocker Search
    float avgBlockerDepth = FindAverageBlockerDepth(tex2DShadowMap, tex2DShadowMap_Linear_sampler, ShadowAttribs.f4ShadowMapDim, SamplingInfo.f2UV, SamplingInfo.iCascadeIdx, SamplingInfo.fDepth);

    float filterRadiusTexels = 0.0;
    // Make sure there were blockers and avoid division by zero
    if (avgBlockerDepth < SamplingInfo.fDepth && avgBlockerDepth > 1e-5)
    {
        // 2. Penumbra Size Calculation
        // w_penumbra = (d_receiver - d_blocker) * light_size / d_blocker
        float penumbraRatio = (SamplingInfo.fDepth - avgBlockerDepth) / avgBlockerDepth;
        filterRadiusTexels = PCSS_LIGHT_RADIUS * penumbraRatio * ShadowAttribs.f4ShadowMapDim.x;
        filterRadiusTexels = clamp(filterRadiusTexels, PCSS_MIN_PENUMBRA_SIZE_TEXELS, PCSS_MAX_PENUMBRA_SIZE_TEXELS);
    }

    // 3. PCF Filtering
    // Note: f2DepthSlopeScaledBias is used by FilterShadowMapVaryingPCF internally
    return FilterShadowMapVaryingPCF(tex2DShadowMap, tex2DShadowMap_sampler, ShadowAttribs.f4ShadowMapDim, SamplingInfo.f2UV, SamplingInfo.iCascadeIdx, SamplingInfo.fDepth - FractionalSamplingError, f2DepthSlopeScaledBias, float2(filterRadiusTexels, filterRadiusTexels));
}

struct FilteredShadow
{
    float fLightAmount;
    int   iCascadeIdx;
    float fNextCascadeBlendAmount;
};

FilteredShadow FilterShadowMap(in ShadowMapAttribs       ShadowAttribs,
                               in Texture2DArray<float>  tex2DShadowMap,
                               in SamplerComparisonState tex2DShadowMap_sampler,
                               in SamplerState           tex2DShadowMap_Linear_sampler,
                               in float3                 f3PosInLightViewSpace,
                               in float3                 f3ddXPosInLightViewSpace,
                               in float3                 f3ddYPosInLightViewSpace,
                               in float                  fCameraSpaceZ)
{
    CascadeSamplingInfo SamplingInfo = FindCascade(ShadowAttribs, f3PosInLightViewSpace.xyz, fCameraSpaceZ);
    FilteredShadow Shadow;
    Shadow.iCascadeIdx             = SamplingInfo.iCascadeIdx;
    Shadow.fNextCascadeBlendAmount = 0.0;
    Shadow.fLightAmount            = 1.0;

    if (SamplingInfo.iCascadeIdx == ShadowAttribs.iNumCascades)
        return Shadow;

    Shadow.fLightAmount = FilterShadowCascade(ShadowAttribs, tex2DShadowMap, tex2DShadowMap_sampler, tex2DShadowMap_Linear_sampler, f3ddXPosInLightViewSpace, f3ddYPosInLightViewSpace, SamplingInfo);

#if FILTER_ACROSS_CASCADES
    if (SamplingInfo.iCascadeIdx+1 < ShadowAttribs.iNumCascades)
    {
        CascadeSamplingInfo NextCscdSamplingInfo = GetCascadeSamplingInfo(ShadowAttribs, f3PosInLightViewSpace, SamplingInfo.iCascadeIdx + 1);
        Shadow.fNextCascadeBlendAmount = GetNextCascadeBlendAmount(ShadowAttribs, fCameraSpaceZ, SamplingInfo, NextCscdSamplingInfo);
        float NextCascadeShadow = 1.0;
        if (Shadow.fNextCascadeBlendAmount > 0.0)
        {
            NextCascadeShadow = FilterShadowCascade(ShadowAttribs, tex2DShadowMap, tex2DShadowMap_sampler, tex2DShadowMap_Linear_sampler, f3ddXPosInLightViewSpace, f3ddYPosInLightViewSpace, NextCscdSamplingInfo);
        }
        Shadow.fLightAmount = lerp(Shadow.fLightAmount, NextCascadeShadow, Shadow.fNextCascadeBlendAmount);
    }
#endif

    return Shadow;
}

#endif