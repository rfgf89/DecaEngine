#include "Instancing.hlsl"

// Same prefiltered equirect panorama the PBR materials reflect; drawn first, depth test off.
Texture2D    _EnvMap;
SamplerState _EnvMap_sampler;

cbuffer View
{
    ViewData viewData;
}

cbuffer SkySettings
{
    // Equirect-U shift in radians; must match PbrEnvYaw so reflections rotate with the backdrop.
    float SkyEnvYaw;

    // >0.5: HDR pipeline, frame stays linear until TonemapPS, so write linear luminance here.
    float SkyHdrOutput;

    float SkyPad1, SkyPad2;
}

static const float PI = 3.14159265359;

// Must match ModelViewportEnvironment.CameraFovDegrees (45): tan(45deg / 2).
static const float TanHalfFov = 0.41421356;

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

    float aspect = viewData.viewport.z / max(viewData.viewport.w, 1.0);
    float3 dirView = normalize(float3(input.ndc.x * TanHalfFov * aspect, input.ndc.y * TanHalfFov, 1.0));
    float3 dirWorld = mul(dirView, transpose((float3x3)viewData.view));

    float2 uv = float2(atan2(dirWorld.z, dirWorld.x) / (2.0 * PI) + 0.5 + SkyEnvYaw / (2.0 * PI),
                       acos(clamp(dirWorld.y, -1.0, 1.0)) / PI);

    // Blurred mip: the backdrop stays softer than the mirror reflections on the model.
    float3 sky = _EnvMap.SampleLevel(_EnvMap_sampler, uv, 1.5).rgb;

    // Manual display encode: the LDR target is not *_SRGB. In HDR mode TonemapPS encodes instead.
    output.color = float4(SkyHdrOutput > 0.5 ? sky : pow(sky, 1.0 / 2.2), 1.0);
    return output;
}
