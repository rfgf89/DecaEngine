#include "Instancing.hlsl"

Texture2D    _MainTex;
SamplerState _MainTex_sampler;

Texture2DArray ShadowMaps;
// Comparison sampler: hardware PCF depth compare.
SamplerComparisonState ShadowMaps_sampler;
// Point sampler: raw depth fetches for the PCSS blocker search.
SamplerState ShadowMaps_sampler_point;

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
    float4 pos            : SV_POSITION;
    float2 uv             : TEX_COORD;
    float3 normal         : NORMAL;
    float3 worldPos       : WORLDPOS;
    float4 lightViewPos[4] : LIGHT_VIEW_POS; // per-cascade light-space position
};

struct PSOutput
{
    float4 color : SV_TARGET;
};

// --- PCSS constants ---

// Light source size in world units; larger = softer shadows.
#define LIGHT_WORLD_SIZE                0.005f

#define PCSS_BLOCKER_SEARCH_RADIUS_TEXELS 640.0f
#define PCSS_SHADOW_RADIUS_TEXELS 1280.0f

#define PCSS_BLOCKER_SEARCH_SAMPLES     32

#define PCSS_FILTER_SAMPLES             64

// Filter radius clamp in texels: min avoids aliasing, max caps cost.
#define PCSS_MIN_FILTER_RADIUS_TEXELS   0.001f
#define PCSS_MAX_FILTER_RADIUS_TEXELS   10.0f

#define PCSS_MIN_AVG_BLOCKER_DEPTH      0.0001f

#define SHADOW_BIAS_SLOPE_FACTOR        0.0002f
#define SHADOW_BIAS_BASE_CONSTANT       0.0018f
#define DITHER_MAGNITUDE                0.000001f
#define SHADOW_MAP_SIZE                 4096.0f

// Screen-space pseudo-random dither to break up shadow banding.
float rand(float2 co)
{
    return frac(sin(dot(co.xy, float2(12.9898, 78.233))) * 43758.5453);
}

// Golden-angle spiral: low-discrepancy disk samples, cheaper than grid/random.
float2 GetFibonacciSample(
    int sampleIndex,
    int numSamples
)
{
    float goldenAngle = 2.39996322973f; // (2.0 * PI) / (1.61803398875)
    float i = float(sampleIndex);
    float num = float(numSamples);
    float r = sqrt(i / num); // sqrt keeps the disk distribution uniform by area
    float theta = i * goldenAngle;
    return float2(r * cos(theta), r * sin(theta));
}

float GetShadow(
    float2 uv,
    float cascade,
    float bias
)
{
    return ShadowMaps.SampleCmpLevelZero(ShadowMaps_sampler, float3(uv, cascade), bias);
}

float GetShadowLinear(
    float2 uv,
    float cascade
)
{
    return ShadowMaps.Sample(ShadowMaps_sampler_point, float3(uv, cascade)).r;
}

void FindBlocker(
    out float avgBlockerDepth,
    out int numBlockers,
    float2 uv,
    float receiverDepth,
    int cascadeIndex)
{
    avgBlockerDepth = 0.0;
    numBlockers = 0;
    float invShadowMapSize = 1.0 / float(SHADOW_MAP_SIZE);
    float searchRadiusUV = PCSS_BLOCKER_SEARCH_RADIUS_TEXELS * invShadowMapSize;

    for (int i = 0; i < PCSS_BLOCKER_SEARCH_SAMPLES; ++i)
    {
        float2 offset = GetFibonacciSample(i, PCSS_BLOCKER_SEARCH_SAMPLES) * searchRadiusUV;
        float blockerDepth = GetShadowLinear(uv + offset, cascadeIndex);
        if (blockerDepth < receiverDepth)
        {
            avgBlockerDepth += blockerDepth;
            numBlockers++;
        }
    }

    if (numBlockers > 0)
    {
        avgBlockerDepth /= numBlockers;
    }
}

float CalculatePenumbra(
    float receiverDepth,
    float avgBlockerDepth)
{
    float blockerReceiverDistWorld = (receiverDepth - avgBlockerDepth);
    float penumbraWidth = blockerReceiverDistWorld * LIGHT_WORLD_SIZE;
    float filterRadiusTexels = penumbraWidth * float(SHADOW_MAP_SIZE);

    return clamp(filterRadiusTexels, PCSS_MIN_FILTER_RADIUS_TEXELS, PCSS_MAX_FILTER_RADIUS_TEXELS);
}

float PCF_Filter(
    float2 uv,
    float receiverDepth,
    float filterRadiusTexels,
    int cascadeIndex,
    float3 normal,
    float3 lightDir,
    float2 screenPos)
{
    float shadow = 0.0;
    float invShadowMapSize = 1.0 / float(SHADOW_MAP_SIZE);
    float filterRadiusUV = filterRadiusTexels * invShadowMapSize;

    float cosTheta = saturate(dot(normal, lightDir));
    float slopeBias = (1.0 - cosTheta) * SHADOW_BIAS_SLOPE_FACTOR;
    float cascadeBiasScale = (1.0f * lightData.CascadeSizes[0]) / lightData.CascadeSizes[cascadeIndex];
    float constantBias = SHADOW_BIAS_BASE_CONSTANT * cascadeBiasScale;
    float depthBias = max(slopeBias, constantBias);

    float finalReceiverDepth = receiverDepth - depthBias + (rand(screenPos) * 2.0 - 1.0) * DITHER_MAGNITUDE;

    for (int i = 0; i < PCSS_FILTER_SAMPLES; ++i)
    {
        float2 offset = GetFibonacciSample(i, PCSS_FILTER_SAMPLES) * filterRadiusUV;
        shadow += GetShadow(uv + offset, cascadeIndex, finalReceiverDepth);
    }

    return shadow / float(PCSS_FILTER_SAMPLES);
}

float PCSS(
    float4 lightSpacePos,
    int cascadeIndex,
    float3 normal,
    float3 lightDir,
    float2 screenPos
)
{
    // Homogeneous light space -> NDC -> [0,1] UV with Y flipped for texture space.
    float3 projCoords = lightSpacePos.xyz / lightSpacePos.w;
    float2 shadowUV = projCoords.xy * 0.5 + 0.5;
    shadowUV.y = 1.0 - shadowUV.y;

    if (projCoords.z > 1.0 || projCoords.z < 0.0 ||
        shadowUV.x < 0.0 || shadowUV.x > 1.0 ||
        shadowUV.y < 0.0 || shadowUV.y > 1.0)
    {
        return 1.0;
    }

    float receiverDepth = projCoords.z;

    float avgBlockerDepth;
    int numBlockers;
    FindBlocker(avgBlockerDepth, numBlockers, shadowUV, receiverDepth, cascadeIndex);

    if (numBlockers == 0)
    {
        return 1.0;
    }

    float filterRadiusTexels = CalculatePenumbra(receiverDepth, avgBlockerDepth);

    return PCF_Filter(shadowUV, receiverDepth, filterRadiusTexels, cascadeIndex, normal, lightDir, screenPos);
}

PSOutput Main(in PSInput input)
{
    PSOutput output;
    float4 albedo = _MainTex.Sample(_MainTex_sampler, input.uv);

    float3 normal = normalize(input.normal);
    float3 lightDir = normalize(lightData.LightDirection.xyz);

    float diff = max(dot(normal, lightDir), 0.0);
    float3 diffuse = lightData.LightColor.rgb * lightData.LightColor.w * diff;

    float3 ambient = float3(0.05, 0.05, 0.05);

    float shadowIntensity = 1.0;

    float viewDepth = distance(input.worldPos, viewData.CameraWorldPos);

    const float CASCADE_BLEND_OVERLAP_WORLD_SPACE = 2.0f;

    int currentCascadeIndex = 0;
    float currentCascadeEnd = 0.0f;

    // Pick the cascade by camera distance.
    if (viewDepth <= lightData.CascadeSplits.x)
    {
        currentCascadeIndex = 0;
        currentCascadeEnd = lightData.CascadeSplits.x;
    }
    else if (viewDepth <= lightData.CascadeSplits.y)
    {
        currentCascadeIndex = 1;
        currentCascadeEnd = lightData.CascadeSplits.y;
    }
    else if (viewDepth <= lightData.CascadeSplits.z)
    {
        currentCascadeIndex = 2;
        currentCascadeEnd = lightData.CascadeSplits.z;
    }
    else
    {
        currentCascadeIndex = 3;
    }

    shadowIntensity = PCSS(input.lightViewPos[currentCascadeIndex], currentCascadeIndex , normal, lightDir, input.pos.xy);

    // SpotAngles.z is repurposed as the shadow strength multiplier.
    float shadowStrength = lightData.SpotAngles.z;
    float finalShadowIntensity = lerp(1.0, shadowIntensity, shadowStrength);

    float3 lighting = ambient + finalShadowIntensity * diffuse;
    output.color = float4(albedo.rgb * lighting, albedo.a);

    return output;
}
