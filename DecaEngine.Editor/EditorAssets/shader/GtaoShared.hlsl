// Shared GTAO constants (XeGTAO XE_GTAO_DEFAULT_*): the depth-mip filter must use the
// same effect radius as the main pass, so they live in one place.

static const float GtaoPI = 3.14159265359;
static const float GtaoHalfPI = 1.57079632679;

// Preview camera is a fixed 45-degree FOV (ModelViewportEnvironment.CameraData).
static const float GtaoTanHalfFov = 0.41421356;
static const float GtaoNearPlane = 0.05;

// View-space depth ceiling: the half limit, the mip chain targets are RGBA16F.
static const float GtaoMaxViewDepth = 65504.0;

// Depth chain length including mip 0, matching XE_GTAO_DEPTH_MIP_LEVELS.
#define GTAO_DEPTH_MIP_LEVELS 5

// Compensates for screen space under-estimating occlusion; fitted against ray-traced truth.
static const float GtaoRadiusMultiplier = 1.457;

// Sample weight stays 1 up to 38.5% of the radius, then falls linearly to zero at it.
static const float GtaoFalloffRange = 0.615;

// Step distribution along a slice: >1 pulls samples towards the contact-shadow end.
static const float GtaoSampleDistributionPower = 2.0;

// XeGTAO thin-occluder heuristic, off by default; expected range [0, 0.7].
static const float GtaoThinOccluderCompensation = 0.0;

// Final visibility contrast; overridden by the Graphics window aoPower knob.
static const float GtaoFinalValuePower = 2.2;

// Mip selection bias: higher delays coarse mips, trading bandwidth for thin-object detail.
static const float GtaoDepthMipSamplingOffset = 3.30;

// Pre-denoise visibility can exceed 1, so UNORM8 storage divides by this and the denoiser undoes it.
static const float GtaoOcclusionTermScale = 1.5;

// Below this pixel distance a sample only picks up depth quantisation, not occlusion.
static const float GtaoPixelTooCloseThreshold = 1.3;

// Denoiser blur strength: centre pixel weight vs neighbours (XeGTAO DenoiseBlurBeta).
static const float GtaoDenoiseBlurBeta = 1.2;

// Legacy mode with no world radius pushed: radius as a fraction of screen height.
static const float GtaoLegacyScreenRadius = 0.06;

// Screen radius ceiling: extreme zoom would otherwise stretch the sample step screen-wide.
static const float GtaoMaxScreenRadiusFraction = 0.25;

// Default visibility floor; overridden by the Graphics window aoFloor knob.
static const float GtaoDefaultFloor = 0.12;

// Pixel size in world units at view depth z; width cancels out, only height remains.
float GtaoPixelWorldSize(float viewZ, float viewportHeight)
{
    return 2.0 * GtaoTanHalfFov * viewZ / max(viewportHeight, 1.0);
}

// AO world-space effect radius; the screen ceiling clamps the radius itself, not just the
// sample step, because falloff is derived from the same radius.
float GtaoEffectRadius(float viewZ, float viewportHeight, float worldRange)
{
    float pixelWorldSize = GtaoPixelWorldSize(viewZ, viewportHeight);
    float maxRadius = GtaoMaxScreenRadiusFraction * viewportHeight * pixelWorldSize;
    return worldRange > 0.0
        ? min(worldRange * GtaoRadiusMultiplier, maxRadius)
        : GtaoLegacyScreenRadius * viewportHeight * pixelWorldSize;
}

// Exponent-hack sqrt (Drobot2014a): accurate enough for FastACos, no sqrt in the hot loop.
float GtaoFastSqrt(float x)
{
    return asfloat(0x1fbd1df5 + (asint(x) >> 1));
}

// Polynomial acos, input [-1, 1] -> [0, PI]. The clamp is required: fp32 dots overshoot 1 by
// a few ulp and GtaoFastSqrt would silently return a negative, flipping the horizon angle.
float GtaoFastACos(float inX)
{
    float x = min(abs(inX), 1.0);
    float res = -0.156583 * x + GtaoHalfPI;
    res *= GtaoFastSqrt(1.0 - x);
    return inX >= 0.0 ? res : GtaoPI - res;
}

// LRTB edges: 1 if the neighbour shares the surface, 0 at a depth break. The slope
// correction is what keeps a grazing plane from reading as a silhouette.
float4 GtaoCalculateEdges(float centerZ, float leftZ, float rightZ, float topZ, float bottomZ)
{
    float4 edgesLRTB = float4(leftZ, rightZ, topZ, bottomZ) - centerZ;

    float slopeLR = (edgesLRTB.y - edgesLRTB.x) * 0.5;
    float slopeTB = (edgesLRTB.w - edgesLRTB.z) * 0.5;
    float4 edgesLRTBSlopeAdjusted = edgesLRTB + float4(slopeLR, -slopeLR, slopeTB, -slopeTB);
    edgesLRTB = min(abs(edgesLRTB), abs(edgesLRTBSlopeAdjusted));
    return saturate(1.25 - edgesLRTB / (centerZ * 0.011));
}

// Edges packed into one UNORM8 channel, 2 bits each: all that fits beside visibility in RGBA8.
float GtaoPackEdges(float4 edgesLRTB)
{
    edgesLRTB = round(saturate(edgesLRTB) * 2.9);
    return dot(edgesLRTB, float4(64.0 / 255.0, 16.0 / 255.0, 4.0 / 255.0, 1.0 / 255.0));
}

float4 GtaoUnpackEdges(float packedVal)
{
    uint packed = (uint)(packedVal * 255.5);
    float4 edgesLRTB;
    edgesLRTB.x = float((packed >> 6) & 0x03) / 3.0;
    edgesLRTB.y = float((packed >> 4) & 0x03) / 3.0;
    edgesLRTB.z = float((packed >> 2) & 0x03) / 3.0;
    edgesLRTB.w = float((packed >> 0) & 0x03) / 3.0;
    return saturate(edgesLRTB);
}
