// HDR pipeline finale: linear RGBA16F -> exposure -> tone curve -> manual sRGB encode to RGBA8.
// In LDR mode this pass does not exist and UnlitInstancedPS does the same two steps.
#include "Tonemap.hlsl"

Texture2D    _SceneTex;
SamplerState _SceneTex_sampler;

// 1x1 adapted frame luminance.
Texture2D _AdaptTex;

// Mirrors TonemapConstantsData (TonemapPass.cs).
cbuffer TonemapConstants
{
    // x = key value, y = exposure compensation in stops, z = 1 passthrough, w = curve mode.
    float4 TmParams;

    // x = 1 auto exposure from _AdaptTex, else exp2(EV). y = 1 forces alpha to 1: the native
    // FSR upscaler outputs alpha 0, which would discard the whole preview composite.
    float4 TmParams2;
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

PSOutput Main(in VSOutput input)
{
    PSOutput output;

    // Bilinear sample, not Load: this pass is the upscale point when render scale is below 1.
    float2 uv = input.ndc * float2(0.5, -0.5) + 0.5;
    float4 scene = _SceneTex.SampleLevel(_SceneTex_sampler, uv, 0);

    // Debug views already write display-ready values; exposure and the curve would distort them.
    if (TmParams.z > 0.5)
    {
        output.color = scene;
        return output;
    }

    float adapted = max(_AdaptTex.Load(int3(0, 0, 0)).r, 1e-4);
    float autoScale = TmParams2.x > 0.5 ? (TmParams.x / adapted) : 1.0;
    float exposure = autoScale * exp2(TmParams.y);

    float3 mapped = ApplyToneCurve(max(scene.rgb, 0.0) * exposure, (int)TmParams.w);

    // Alpha passes through: the preview clears transparent and the icon baker keeps it in PNG.
    output.color = float4(pow(saturate(mapped), 1.0 / 2.2), max(scene.a, TmParams2.y));
    return output;
}
