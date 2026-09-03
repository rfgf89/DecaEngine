// GTAO main pass, XeGTAO variant; alternative to SsaoCommon.hlsl (see AmbientOcclusionMode).
// Stage 2 of 3: GtaoDepthPrefilter/GtaoDepthMipPS -> this pass -> GtaoDenoisePS.
// Depth is read only from the prefiltered chain, so MSAA never reaches this pass.
// Infinite reversed-Z, fixed 45 degree FOV (see ModelViewportEnvironment).
#include "Instancing.hlsl"
#include "GtaoShared.hlsl"

// Linear view-space depth chain, [0] full res; separate textures, not mips, because
// IRenderTarget cannot render into a specific mip level (see IGraphicsApi.CreateRenderTarget).
// Point samplers: bilinear would blend neighbouring depths and skew the surface slope.
Texture2D _AoDepth0;
SamplerState _AoDepth0_sampler;
Texture2D _AoDepth1;
SamplerState _AoDepth1_sampler;
Texture2D _AoDepth2;
SamplerState _AoDepth2_sampler;
Texture2D _AoDepth3;
SamplerState _AoDepth3_sampler;
Texture2D _AoDepth4;
SamplerState _AoDepth4_sampler;

cbuffer View
{
    ViewData viewData;
}

// aoWorldRange in world units, 0 = legacy screen-fraction radius (see GtaoEffectRadius).
// Padded with scalars, not float3: float3 at offset 4 breaks std140/SPIR-V alignment and
// Vulkan fails to legalize the shader. Mirrors AoConstantsData (SsaoPass.cs).
cbuffer AoConstants
{
    float aoWorldRange;
    float aoPower;
    float aoFloor;
    float aoConstantsPad2;
}

// More slices than XeGTAO High (3): there is no TAA here to accumulate them over time.
static const int SliceCount = 5;
static const int StepsPerSlice = 3;

struct VSOutput
{
    float4 pos : SV_POSITION;
    float2 ndc : TEXCOORD0;
};

struct PSOutput
{
    float4 color : SV_TARGET;
};

// Pixel index on a 64x64 Hilbert curve: makes every 3x3 neighbourhood cover the whole
// slice-direction set evenly, which is what the 3x3 denoiser assumes (white noise clumps).
uint GtaoHilbertIndex(uint2 pos)
{
    // The curve has period 64; the (63 - pos) flip below is valid only within one period.
    pos &= 63;

    uint index = 0;
    [unroll]
    for (uint level = 32; level > 0; level /= 2)
    {
        uint regionX = (pos.x & level) > 0 ? 1 : 0;
        uint regionY = (pos.y & level) > 0 ? 1 : 0;
        index += level * level * ((3 * regionX) ^ regionY);
        if (regionY == 0)
        {
            if (regionX == 1)
            {
                pos.x = 63 - pos.x;
                pos.y = 63 - pos.y;
            }

            uint temp = pos.x;
            pos.x = pos.y;
            pos.y = temp;
        }
    }

    return index;
}

// Per-pixel noise pair: x rotates the slice orientation, y jitters steps along the slice.
float2 GtaoSpatialNoise(uint2 pixel)
{
    uint index = GtaoHilbertIndex(pixel);

    // R2 low-discrepancy sequence (Roberts): higher-order golden-ratio shifts.
    return frac(0.5 + index * float2(0.75487766624669276005, 0.5698402909980532659114));
}

float LoadViewDepth(int2 pixel, float2 viewportSize)
{
    pixel = clamp(pixel, int2(0, 0), int2(viewportSize) - 1);
    return _AoDepth0.Load(int3(pixel, 0)).r;
}

// Integer chain level only: the levels live in separate textures, so SampleLevel cannot pick.
float SampleDepthMip(float2 uv, int mip)
{
    if (mip <= 0)
    {
        return _AoDepth0.SampleLevel(_AoDepth0_sampler, uv, 0).r;
    }

    if (mip == 1)
    {
        return _AoDepth1.SampleLevel(_AoDepth1_sampler, uv, 0).r;
    }

    if (mip == 2)
    {
        return _AoDepth2.SampleLevel(_AoDepth2_sampler, uv, 0).r;
    }

    if (mip == 3)
    {
        return _AoDepth3.SampleLevel(_AoDepth3_sampler, uv, 0).r;
    }

    return _AoDepth4.SampleLevel(_AoDepth4_sampler, uv, 0).r;
}

float3 ViewPosFromUV(float2 uv, float viewZ, float aspect)
{
    float2 ndc = float2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
    return float3(ndc.x * GtaoTanHalfFov * aspect * viewZ, ndc.y * GtaoTanHalfFov * viewZ, viewZ);
}

float3 ViewPosAt(int2 pixel, float2 viewportSize, float aspect)
{
    float z = LoadViewDepth(pixel, viewportSize);
    return ViewPosFromUV((pixel + 0.5) / viewportSize, z, aspect);
}

PSOutput Main(in VSOutput input)
{
    PSOutput output;

    float2 viewportSize = viewData.viewport.zw;
    float2 invViewportSize = 1.0 / viewportSize;
    float aspect = viewportSize.x / max(viewportSize.y, 1.0);
    int2 pixel = int2(input.pos.xy);
    float2 centerUV = (pixel + 0.5) * invViewportSize;

    // Edges are computed for background pixels too: the denoiser reads them unconditionally.
    float viewspaceZ = LoadViewDepth(pixel, viewportSize);
    float leftZ = LoadViewDepth(pixel + int2(-1, 0), viewportSize);
    float rightZ = LoadViewDepth(pixel + int2(1, 0), viewportSize);
    float topZ = LoadViewDepth(pixel + int2(0, -1), viewportSize);
    float bottomZ = LoadViewDepth(pixel + int2(0, 1), viewportSize);

    float4 edgesLRTB = GtaoCalculateEdges(viewspaceZ, leftZ, rightZ, topZ, bottomZ);
    float packedEdges = GtaoPackEdges(edgesLRTB);

    // Background is unoccluded, but still packed divided by GtaoOcclusionTermScale.
    if (viewspaceZ >= GtaoMaxViewDepth * 0.99)
    {
        output.color = float4(1.0 / GtaoOcclusionTermScale, packedEdges, 0.0, 1.0);
        return output;
    }

    // Normal from four edge-weighted neighbour cross products (XeGTAO_CalculateNormal):
    // a single derivative pair skews the slope on grazing surfaces and self-shadows them.
    float3 center = ViewPosFromUV(centerUV, viewspaceZ, aspect);
    float3 posL = ViewPosAt(pixel + int2(-1, 0), viewportSize, aspect);
    float3 posR = ViewPosAt(pixel + int2(1, 0), viewportSize, aspect);
    float3 posT = ViewPosAt(pixel + int2(0, -1), viewportSize, aspect);
    float3 posB = ViewPosAt(pixel + int2(0, 1), viewportSize, aspect);

    float4 acceptedNormals = saturate(float4(edgesLRTB.x * edgesLRTB.z,
                                             edgesLRTB.z * edgesLRTB.y,
                                             edgesLRTB.y * edgesLRTB.w,
                                             edgesLRTB.w * edgesLRTB.x) + 0.01);

    float3 dirL = normalize(posL - center);
    float3 dirR = normalize(posR - center);
    float3 dirT = normalize(posT - center);
    float3 dirB = normalize(posB - center);

    float3 viewspaceNormal = acceptedNormals.x * cross(dirL, dirT)
                           + acceptedNormals.y * cross(dirT, dirR)
                           + acceptedNormals.z * cross(dirR, dirB)
                           + acceptedNormals.w * cross(dirB, dirL);
    viewspaceNormal = normalize(viewspaceNormal);
    if (viewspaceNormal.z > 0.0)
    {
        viewspaceNormal = -viewspaceNormal;
    }

    // Nudge the center toward the camera: depth quantization otherwise lifts same-plane taps
    // above the tangent plane and flat surfaces shadow themselves.
    // Factor is tuned for the half-precision depth chain (XE_GTAO_FP32_DEPTHS off).
    viewspaceZ *= 0.99920;

    float3 pixCenterPos = ViewPosFromUV(centerUV, viewspaceZ, aspect);

    // Camera sits at the view-space origin, +z points into the screen.
    float3 viewVec = normalize(-pixCenterPos);

    // Normals reconstructed from depth can face away at grazing angles (XeGTAO takes them
    // from the G-buffer and has this disabled); pull them back into the visible hemisphere.
    viewspaceNormal = normalize(viewspaceNormal + max(0.0, -dot(viewspaceNormal, viewVec)) * viewVec);

    float NdotV = saturate(dot(viewspaceNormal, viewVec));

    float pixelWorldSize = GtaoPixelWorldSize(viewspaceZ, viewportSize.y);
    float effectRadius = GtaoEffectRadius(viewspaceZ, viewportSize.y, aoWorldRange);
    float falloffRange = max(GtaoFalloffRange * effectRadius, 1e-6);
    float falloffFrom = effectRadius * (1.0 - GtaoFalloffRange);

    // XeGTAO falloff: weight is 1 up to falloffFrom, then linear to 0 at the radius.
    float falloffMul = -1.0 / falloffRange;
    float falloffAdd = falloffFrom / falloffRange + 1.0;

    float screenspaceRadius = max(effectRadius / pixelWorldSize, 1e-3);

    // Fade out at tiny screen radii: all samples land in the same texel and yield only noise.
    float visibility = saturate((10.0 - screenspaceRadius) / 100.0) * 0.5;

    // Minimum step offset: taps next to the center carry only depth quantization noise.
    float minS = GtaoPixelTooCloseThreshold / screenspaceRadius;

    float2 noise = GtaoSpatialNoise(uint2(pixel));

    [loop]
    for (int slice = 0; slice < SliceCount; slice++)
    {
        // Pixel y grows down, view-space y grows up: hence the minus on the screen-space sinPhi.
        float sliceK = (slice + noise.x) / SliceCount;
        float phi = sliceK * GtaoPI;
        float cosPhi = cos(phi);
        float sinPhi = sin(phi);
        float2 omega = float2(cosPhi, -sinPhi) * screenspaceRadius;

        float3 directionVec = float3(cosPhi, sinPhi, 0.0);
        float3 orthoDirectionVec = directionVec - dot(directionVec, viewVec) * viewVec;

        // Slice axis is orthogonal to both the direction and the view; the normal projects onto it.
        float3 axisVec = normalize(cross(orthoDirectionVec, viewVec));
        float3 projectedNormalVec = viewspaceNormal - axisVec * dot(viewspaceNormal, axisVec);

        float signNorm = sign(dot(orthoDirectionVec, projectedNormalVec));
        float projectedNormalVecLength = length(projectedNormalVec);
        float cosNorm = saturate(dot(projectedNormalVec, viewVec) / max(projectedNormalVecLength, 1e-6));

        // Projected normal angle vs view: the center of the arc horizons can cover.
        float n = signNorm * GtaoFastACos(cosNorm);

        // Horizon floor is the tangent plane, not -1 as in the paper: keeps samples lying in
        // that plane (most of them at grazing angles) from self-shadowing the surface.
        float lowHorizonCos0 = cos(n + GtaoHalfPI);
        float lowHorizonCos1 = cos(n - GtaoHalfPI);

        float horizonCos0 = lowHorizonCos0;
        float horizonCos1 = lowHorizonCos1;

        [unroll]
        for (int step = 0; step < StepsPerSlice; step++)
        {
            // R1 golden-ratio shift over (slice, step) so steps do not land on one lattice.
            float stepBaseNoise = float(slice + step * StepsPerSlice) * 0.6180339887498948482;
            float stepNoise = frac(noise.y + stepBaseNoise);

            float s = (step + stepNoise) / StepsPerSlice;
            s = pow(s, GtaoSampleDistributionPower);
            s += minS;

            float2 sampleOffset = s * omega;
            float sampleOffsetLength = length(sampleOffset);

            // Coarser chain level for longer steps - see GtaoDepthMipPS.hlsl.
            int mipLevel = (int)clamp(round(log2(sampleOffsetLength) - GtaoDepthMipSamplingOffset),
                                      0, GTAO_DEPTH_MIP_LEVELS - 1);

            // Snap to texel centers, else the reconstructed point drifts along the slope.
            sampleOffset = round(sampleOffset) * invViewportSize;

            float2 sampleUV0 = centerUV + sampleOffset;
            float2 sampleUV1 = centerUV - sampleOffset;

            float sz0 = SampleDepthMip(sampleUV0, mipLevel);
            float sz1 = SampleDepthMip(sampleUV1, mipLevel);

            float3 samplePos0 = ViewPosFromUV(sampleUV0, sz0, aspect);
            float3 samplePos1 = ViewPosFromUV(sampleUV1, sz1, aspect);

            float3 sampleDelta0 = samplePos0 - pixCenterPos;
            float3 sampleDelta1 = samplePos1 - pixCenterPos;
            float sampleDist0 = max(length(sampleDelta0), 1e-6);
            float sampleDist1 = max(length(sampleDelta1), 1e-6);

            float3 sampleHorizonVec0 = sampleDelta0 / sampleDist0;
            float3 sampleHorizonVec1 = sampleDelta1 / sampleDist1;

            // Thin-occluder compensation: stretching z in the distance metric drops samples
            // behind the point sooner, so occluders are not treated as infinitely deep.
            float falloffBase0 = length(float3(sampleDelta0.x, sampleDelta0.y,
                                               sampleDelta0.z * (1.0 + GtaoThinOccluderCompensation)));
            float falloffBase1 = length(float3(sampleDelta1.x, sampleDelta1.y,
                                               sampleDelta1.z * (1.0 + GtaoThinOccluderCompensation)));
            float weight0 = saturate(falloffBase0 * falloffMul + falloffAdd);
            float weight1 = saturate(falloffBase1 * falloffMul + falloffAdd);

            float shc0 = dot(sampleHorizonVec0, viewVec);
            float shc1 = dot(sampleHorizonVec1, viewVec);

            // Out-of-radius samples ease back to the horizon floor instead of popping.
            shc0 = lerp(lowHorizonCos0, shc0, weight0);
            shc1 = lerp(lowHorizonCos1, shc1, weight1);

            horizonCos0 = max(horizonCos0, shc0);
            horizonCos1 = max(horizonCos1, shc1);
        }

        // XeGTAO empirical fudge against overdarkening on high slopes.
        projectedNormalVecLength = lerp(projectedNormalVecLength, 1.0, 0.05);

        // Analytic visibility arc integral: a(h) = (cos(n) + 2h*sin(n) - cos(2h - n)) / 4.
        float h0 = -GtaoFastACos(horizonCos1);
        float h1 = GtaoFastACos(horizonCos0);

        float iarc0 = (cosNorm + 2.0 * h0 * sin(n) - cos(2.0 * h0 - n)) * 0.25;
        float iarc1 = (cosNorm + 2.0 * h1 * sin(n) - cos(2.0 * h1 - n)) * 0.25;

        visibility += projectedNormalVecLength * (iarc0 + iarc1);
    }

    visibility /= SliceCount;

    // Normal reconstruction degenerates on near-edge-on surfaces (measured NdotV < 0.03),
    // collapsing the integral into black blotches; skip AO there. Drops with a G-buffer normal.
    visibility = lerp(1.0, visibility, smoothstep(0.005, 0.03, NdotV));

    visibility = pow(saturate(visibility), aoPower > 0.01 ? aoPower : GtaoFinalValuePower);

    // Never fully dark: the pixel is visibly shaded, and zero also skews the denoiser average.
    visibility = max(0.03, visibility);

    // Raw visibility can exceed 1 before filtering, so UNORM8 stores it divided; the denoiser
    // scales it back. Edges ride the second channel of the same target (see GtaoPackEdges).
    output.color = float4(saturate(visibility / GtaoOcclusionTermScale), packedEdges, 0.0, 1.0);
    return output;
}
