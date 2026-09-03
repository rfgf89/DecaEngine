// Shared SSGI composite body; wrappers define DEPTH_FETCH for single or multisampled depth.
// A separate pass instead of blending: the engine's PSO abstraction has no blend state.
#include "Instancing.hlsl"

Texture2D    _SceneTex;
SamplerState _SceneTex_sampler;
Texture2D    _GiTex;
SamplerState _GiTex_sampler;

cbuffer View
{
    ViewData viewData;
}

// Mirrors GiCompositeData (SsgiPass.cs).
cbuffer GiComposite
{
    // Bilateral window radius in pixels; 0 disables the blur, clamped to GiMaxBlurRadius.
    float giBlurRadius;
    // 0 - normal composite, 1 - bounce only.
    float giDebugView;
    float giCompositePad0;
    float giCompositePad1;
}

// Must match CameraData near in ModelViewportEnvironment; reverse-Z, as in SsgiCommon.hlsl.
static const float CompositeNearPlane = 0.05;

// Bilateral weight tolerance as a fraction of the pixel's view depth.
static const float DepthTolerance = 0.02;

// Fixed upper bound: the compiler cannot unroll dynamic loop bounds.
static const int GiMaxBlurRadius = 3;

struct VSOutput
{
    float4 pos : SV_POSITION;
    float2 ndc : TEXCOORD0;
};

struct PSOutput
{
    float4 color : SV_TARGET;
};

float GiCompositeViewDepth(int2 pixel, float2 viewportSize)
{
    pixel = clamp(pixel, int2(0, 0), int2(viewportSize) - 1);
    float d = DEPTH_FETCH(pixel);
    // Background clears to zero under reverse-Z; map it to infinity so the weight rejects it.
    return d < 1e-6 ? 1e9 : CompositeNearPlane / d;
}

PSOutput Main(in VSOutput input)
{
    PSOutput output;

    float2 viewportSize = viewData.viewport.zw;
    int2 pixel = int2(input.pos.xy);
    float2 uv = input.pos.xy / viewportSize;
    float2 texel = 1.0 / viewportSize;

    int radius = (int)clamp(giBlurRadius, 0.0, (float)GiMaxBlurRadius);
    float centerDepth = GiCompositeViewDepth(pixel, viewportSize);
    float tolerance = max(DepthTolerance * centerDepth, 1e-4);

    // Center weight is pinned to 1 so isolated pixels do not collapse to zero.
    float3 gi = _GiTex.Sample(_GiTex_sampler, uv).rgb;
    float weightSum = 1.0;

    [loop]
    for (int y = -GiMaxBlurRadius; y <= GiMaxBlurRadius; y++)
    {
        if (abs(y) > radius)
        {
            continue;
        }

        [loop]
        for (int x = -GiMaxBlurRadius; x <= GiMaxBlurRadius; x++)
        {
            if (abs(x) > radius || (x == 0 && y == 0))
            {
                continue;
            }

            float tapDepth = GiCompositeViewDepth(pixel + int2(x, y), viewportSize);
            float weight = saturate(1.0 - abs(tapDepth - centerDepth) / tolerance);
            gi += weight * _GiTex.Sample(_GiTex_sampler, uv + float2(x, y) * texel).rgb;
            weightSum += weight;
        }
    }
    gi /= weightSum;

    float4 scene = _SceneTex.Sample(_SceneTex_sampler, uv);

    if (giDebugView > 0.5)
    {
        // Alpha comes from the scene: zero alpha would punch a hole in the icon baker's frame.
        output.color = float4(gi, scene.a);
        return output;
    }

    // Additive and unclamped: the HDR target is linear RGBA16F and saturate() would clip
    // headroom before tonemapping.
    output.color = float4(max(scene.rgb + gi, 0.0), scene.a);
    return output;
}
