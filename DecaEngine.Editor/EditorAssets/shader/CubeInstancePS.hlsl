#include "Instancing.hlsl"

// Assume these are bound individually in C#
Texture2D    _MainTex;
SamplerState _MainTex_sampler;
Texture2D ShadowMaps0;
Texture2D ShadowMaps1;
Texture2D ShadowMaps2;
Texture2D ShadowMaps3;
SamplerComparisonState ShadowMaps0_sampler;
SamplerComparisonState ShadowMaps1_sampler;
SamplerComparisonState ShadowMaps2_sampler;
SamplerComparisonState ShadowMaps3_sampler;

cbuffer Light
{
    LightData lightData;
}

cbuffer View
{
    ViewData viewData;
}

struct PSInput
{
    float4 pos      : SV_POSITION;
    float2 uv       : TEX_COORD;
    float3 normal   : NORMAL;
    float3 worldPos : WORLDPOS;
};

struct PSOutput
{
    float4 color : SV_TARGET;
};

float GetVisibility(SamplerComparisonState state, Texture2D tex, float4x4 lightSpaceMatrix, float3 worldPos, float3 normal, float3 lightDir)
{
    float4 lightSpacePos = mul(float4(worldPos, 1.0), lightSpaceMatrix);
    float3 projCoords = lightSpacePos.xyz / lightSpacePos.w;

    float2 shadowUV = projCoords.xy * 0.5 + 0.5;
    shadowUV.y = 1.0 - shadowUV.y;

    if (projCoords.z > 1.0 || projCoords.z < 0.0 ||
        shadowUV.x < 0.0 || shadowUV.x > 1.0 ||
        shadowUV.y < 0.0 || shadowUV.y > 1.0)
    {
        return 1.0; // Outside shadow map -> fully visible
    }

    // Apply a constant bias to avoid shadow acne
    float cosTheta = saturate(dot(normal, lightDir));
    float bias = max(0.001 * (1.0 - cosTheta), 0.001);

    // SampleCmp returns 1.0 if the pixel is not in shadow, 0.0 if it is.
    float visibility = tex.SampleCmp(state, float3(shadowUV, 0.0), projCoords.z - bias);

    return visibility > projCoords.z + bias;
}

PSOutput Main(in PSInput input)
{
    PSOutput output;
    float4 albedo = _MainTex.Sample(_MainTex_sampler, input.uv);

    float3 normal = normalize(input.normal);
    float3 lightDir = normalize(lightData.LightDirection.xyz);

    float diff = max(dot(normal, lightDir), 0.0);

    // The 'w' component of LightColor is used for intensity
    float3 diffuse = lightData.LightColor.rgb * lightData.LightColor.w * diff;

    // A much smaller ambient to not wash out the scene
    float3 ambient = float3(0.05, 0.05, 0.05);

    float shadowIntensity = 1.0; // Default to fully visible

    // Use distance from camera world pos for cascade selection
    float viewDepth = distance(input.worldPos, viewData.CameraWorldPos);

    if (viewDepth <= lightData.CascadeSplits.x) {
        shadowIntensity = GetVisibility(ShadowMaps0_sampler, ShadowMaps0, lightData.CascadeMatrix[0], input.worldPos, normal, lightDir);
    } else if (viewDepth <= lightData.CascadeSplits.y) {
        shadowIntensity = GetVisibility(ShadowMaps1_sampler, ShadowMaps1, lightData.CascadeMatrix[1], input.worldPos, normal, lightDir);
    } else if (viewDepth <= lightData.CascadeSplits.z) {
        shadowIntensity = GetVisibility(ShadowMaps2_sampler, ShadowMaps2, lightData.CascadeMatrix[2], input.worldPos, normal, lightDir);
    } else {
        shadowIntensity = GetVisibility(ShadowMaps3_sampler, ShadowMaps3, lightData.CascadeMatrix[3], input.worldPos, normal, lightDir);
    }

    float shadowStrength = lightData.SpotAngles.z;

    float lightFactor = shadowIntensity * shadowStrength;

    float3 lighting = ambient + (1.0 - lightFactor) * diffuse;
    output.color = float4(albedo.rgb * lighting, albedo.a);

    return output;
}