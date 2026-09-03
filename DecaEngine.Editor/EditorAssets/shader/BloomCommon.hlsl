// Shared bloom plumbing; progressive mip chain (Jimenez, SIGGRAPH 2014).
// Separate passes, not additive blending: the PSO abstraction has no blend state.
#ifndef BLOOM_COMMON_INCLUDED
#define BLOOM_COMMON_INCLUDED

#include "Instancing.hlsl"

Texture2D    _SourceTex;
SamplerState _SourceTex_sampler;

// _LowerTex is declared only by the passes that read it: elsewhere the compiler strips it
// and Diligent warns about the unassigned immutable sampler.

// 1x1 adapted frame luminance; in LDR mode a placeholder is bound and never read.
Texture2D    _AdaptTex;

cbuffer View
{
    ViewData viewData;
}

// Mirrors BloomConstantsData (BloomPass.cs); one instance per chain link (sizes differ).
cbuffer BloomConstants
{
    // xy - target size in pixels, zw - 1/xy.
    float4 bloomTarget;
    // xy - source size in pixels, zw - 1/xy.
    float4 bloomSource;

    // x - brightness threshold, y - soft knee width, z - upsample tent radius,
    // w - composite intensity.
    float4 bloomParams;

    // x - >0.5 if the threshold is relative to exposure, y - auto-exposure key value.
    float4 bloomExposure;
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

float BloomLuminance(float3 c)
{
    return dot(c, float3(0.2126, 0.7152, 0.0722));
}

// Same linear-to-display factor as the tonemapper: the threshold must be in display units.
float BloomExposure()
{
    if (bloomExposure.x < 0.5)
    {
        return 1.0;
    }

    float adapted = max(_AdaptTex.Load(int3(0, 0, 0)).r, 1e-4);
    return max(bloomExposure.y, 1e-4) / adapted;
}

#endif // BLOOM_COMMON_INCLUDED
