// Layout mirrored byte-for-byte by EyeAdaptationConstantsData in EyeAdaptationPass.cs.
#ifndef EYE_ADAPTATION_COMMON_HLSL
#define EYE_ADAPTATION_COMMON_HLSL

cbuffer EyeAdaptation
{
    // xy = pass target size in pixels, zw = 1/xy.
    float4 EaTarget;

    // xy = source texture size in pixels, zw = 1/xy.
    float4 EaSource;

    // x = key value, y/z = measured luminance clamp, w = exposure compensation in EV stops.
    float4 EaParams;

    // x = frame delta seconds, y = adapt-to-light speed, z = adapt-to-dark speed, w unused.
    float4 EaParams2;
}

struct VSOutput
{
    float4 pos : SV_POSITION;
    float2 ndc : TEXCOORD0;
};

struct PSOutput
{
    float4 color : SV_TARGET;
};

// Luminance floor under the log: log2(0) = -inf would poison the whole-target average.
static const float EaLuminanceEpsilon = 1e-4;

#endif
