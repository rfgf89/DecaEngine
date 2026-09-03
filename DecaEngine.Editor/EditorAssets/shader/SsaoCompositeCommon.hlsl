// Shared AO composite body; wrappers define DEPTH_FETCH for single- or multi-sample depth.
// A separate pass rather than blending: the engine's PSO abstraction has no blend state.
#include "Instancing.hlsl"

Texture2D    _SceneTex;
SamplerState _SceneTex_sampler;
Texture2D    _AoTex;
SamplerState _AoTex_sampler;

cbuffer View
{
    ViewData viewData;
}

// Mirrors AoCompositeData (SsaoPass.cs).
cbuffer AoComposite
{
    // 0 = normal composite, 1 = grayscale AO over the frame.
    float aoDebugView;

    // On for SSAO (its only filter); off for GTAO, already denoised by GtaoDenoisePS.
    float aoCompositeBlur;

    float aoCompositePad1;
    float aoCompositePad2;
}

// Must match CameraData near in ModelViewportEnvironment; reverse-Z, as in SsaoCommon/GtaoCommon.
static const float CompositeNearPlane = 0.05;

// Bilateral tolerance as a fraction of pixel depth, so it stays scale invariant.
static const float DepthTolerance = 0.02;

struct VSOutput
{
    float4 pos : SV_POSITION;
    float2 ndc : TEXCOORD0;
};

struct PSOutput
{
    float4 color : SV_TARGET;
};

float CompositeViewDepth(int2 pixel, float2 viewportSize)
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

    float centerDepth = CompositeViewDepth(pixel, viewportSize);
    float tolerance = max(DepthTolerance * centerDepth, 1e-4);

    // Center weight is fixed at 1 so the filter cannot collapse on an isolated pixel.
    float ao = _AoTex.Sample(_AoTex_sampler, uv).r;
    float weightSum = 1.0;

    [branch]
    if (aoCompositeBlur > 0.5)
    {
        [unroll]
        for (int y = -1; y <= 1; y++)
        {
            [unroll]
            for (int x = -1; x <= 1; x++)
            {
                if (x == 0 && y == 0)
                {
                    continue;
                }

                float tapDepth = CompositeViewDepth(pixel + int2(x, y), viewportSize);
                float weight = saturate(1.0 - abs(tapDepth - centerDepth) / tolerance);
                ao += weight * _AoTex.Sample(_AoTex_sampler, uv + float2(x, y) * texel).r;
                weightSum += weight;
            }
        }

        ao /= weightSum;
    }

    float4 scene = _SceneTex.Sample(_SceneTex_sampler, uv);

    if (aoDebugView > 0.5)
    {
        // AO is already in display space (UNORM target), so no gamma encode here.
        // Alpha comes from the scene: zero alpha would punch a hole in the icon baker's background.
        output.color = float4(ao.xxx, scene.a);
        return output;
    }

    output.color = float4(scene.rgb * ao, scene.a);
    return output;
}
