#include "Instancing.hlsl"

// --- Resources ---
// These are bound from the C# side.

// Main diffuse texture for the object.
Texture2D    _MainTex;
// Sampler for the main texture.
SamplerState _MainTex_sampler;

// Texture array containing the shadow maps for each cascade.
Texture2DArray ShadowMaps;
// Comparison sampler for performing Percentage-Closer Filtering (PCF).
// This allows for hardware-accelerated depth comparison.
SamplerComparisonState ShadowMaps_sampler;
// Point sampler for fetching raw depth values from the shadow map.
// Used during the blocker search phase of PCSS.
SamplerState ShadowMaps_sampler_point;

// --- Constant Buffers ---

// Contains data about the main directional light (e.g., direction, color, cascade splits).
cbuffer Light
{
    LightData lightData;
}

// Contains data about the camera/view (e.g., camera position).
cbuffer View
{
    ViewData viewData;
}

// --- Structures ---

// Input structure for the Pixel Shader, passed from the Vertex Shader.
struct PSInput
{
    float4 pos            : SV_POSITION;      // Clip space position.
    float2 uv             : TEX_COORD;        // Texture coordinates.
    float3 normal         : NORMAL;           // World space normal.
    float3 worldPos       : WORLDPOS;         // World space position of the pixel.
    float4 lightViewPos[4] : LIGHT_VIEW_POS; // Position in light's view space for each of the 4 cascades.
};

// Output structure for the Pixel Shader.
struct PSOutput
{
    float4 color : SV_TARGET; // Final pixel color.
};

// --- PCSS (Percentage-Closer Soft Shadows) Constants ---
// These settings provide a balance between quality and performance for soft shadows.

// Defines the size of the light source in world units. A larger light source creates softer shadows.
#define LIGHT_WORLD_SIZE                0.005f

// The radius in shadow map texels to search for blockers.
// A smaller radius gives a more local (and accurate) blocker depth, preventing over-blurring for large objects.
#define PCSS_BLOCKER_SEARCH_RADIUS_TEXELS 640.0f
#define PCSS_SHADOW_RADIUS_TEXELS 1280.0f

// The number of samples to use when searching for blockers.
#define PCSS_BLOCKER_SEARCH_SAMPLES     32

// The number of samples to use for the final PCF filtering step.
// More samples result in smoother shadows.
#define PCSS_FILTER_SAMPLES             64

// Minimum filter radius in texels. Prevents shadow aliasing for very sharp shadows.
#define PCSS_MIN_FILTER_RADIUS_TEXELS   0.001f

// Maximum filter radius in texels. Caps the blur to control performance and appearance.
#define PCSS_MAX_FILTER_RADIUS_TEXELS   10.0f

// A small value to prevent division by zero when calculating penumbra size.
#define PCSS_MIN_AVG_BLOCKER_DEPTH      0.0001f

// --- Shadow Bias Constants ---
#define SHADOW_BIAS_SLOPE_FACTOR        0.0002f  // Bias amount based on the slope of the surface relative to the light.
#define SHADOW_BIAS_BASE_CONSTANT       0.0018f  // A constant bias to apply uniformly.
#define DITHER_MAGNITUDE                0.000001f // The strength of the dithering noise applied to reduce shadow banding artifacts.
#define SHADOW_MAP_SIZE                 4096.0f
// --- Helper Functions ---

// Simple pseudo-random number generator based on screen coordinates.
// Used for dithering to break up shadow banding artifacts.
float rand(float2 co)
{
    return frac(sin(dot(co.xy, float2(12.9898, 78.233))) * 43758.5453);
}

// Generates a sample point on a Fibonacci (or golden angle) spiral.
// This provides a set of well-distributed, low-discrepancy sample points within a circular area,
// which is more efficient and produces better results than uniform or random grid sampling.
float2 GetFibonacciSample(
    int sampleIndex,
    int numSamples
)
{
    float goldenAngle = 2.39996322973f; // (2.0 * PI) / (1.61803398875)
    float i = float(sampleIndex);
    float num = float(numSamples);
    float r = sqrt(i / num); // Use sqrt to ensure uniform distribution over the disk area
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
    // Convert normalized depth difference to world space distance
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

    // Apply bias
    float cosTheta = saturate(dot(normal, lightDir));
    float slopeBias = (1.0 - cosTheta) * SHADOW_BIAS_SLOPE_FACTOR;
    float cascadeBiasScale = (1.0f * lightData.CascadeSizes[0]) / lightData.CascadeSizes[cascadeIndex];
    float constantBias = SHADOW_BIAS_BASE_CONSTANT * cascadeBiasScale;
    float depthBias = max(slopeBias, constantBias);

    // Add dithering to the depth to reduce banding
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
) // Screen position is used for dithering.
{
    // Convert from homogeneous light space to normalized device coordinates (NDC) [-1, 1]
    // and then to texture coordinates [0, 1].
    float3 projCoords = lightSpacePos.xyz / lightSpacePos.w;
    float2 shadowUV = projCoords.xy * 0.5 + 0.5;
    shadowUV.y = 1.0 - shadowUV.y;

    // Early exit if the pixel is outside the shadow map's bounds.
    if (projCoords.z > 1.0 || projCoords.z < 0.0 ||
        shadowUV.x < 0.0 || shadowUV.x > 1.0 ||
        shadowUV.y < 0.0 || shadowUV.y > 1.0)
    {
        return 1.0; // Not in shadow.
    }

    float receiverDepth = projCoords.z;

    // STEP 1: Blocker Search
    float avgBlockerDepth;
    int numBlockers;
    FindBlocker(avgBlockerDepth, numBlockers, shadowUV, receiverDepth, cascadeIndex);

    if (numBlockers == 0)
    {
        return 1.0; // No blockers found, so pixel is fully lit.
    }

    // STEP 2: Penumbra Estimation
    float filterRadiusTexels = CalculatePenumbra(receiverDepth, avgBlockerDepth);

    // STEP 3: PCF Filtering
    return PCF_Filter(shadowUV, receiverDepth, filterRadiusTexels, cascadeIndex, normal, lightDir, screenPos);
}

PSOutput Main(in PSInput input)
{
    PSOutput output;
    // Sample the albedo (base color) from the main texture.
    float4 albedo = _MainTex.Sample(_MainTex_sampler, input.uv);

    // Normalize inputs.
    float3 normal = normalize(input.normal);
    float3 lightDir = normalize(lightData.LightDirection.xyz);

    // Calculate standard diffuse lighting (Lambertian).
    float diff = max(dot(normal, lightDir), 0.0);
    float3 diffuse = lightData.LightColor.rgb * lightData.LightColor.w * diff;

    // Add a small amount of ambient light to illuminate areas not hit by direct light.
    float3 ambient = float3(0.05, 0.05, 0.05);

    // --- Cascaded Shadow Mapping (CSM) Logic ---
    float shadowIntensity = 1.0;

    // Calculate the distance from the camera to the current pixel in world space.
    float viewDepth = distance(input.worldPos, viewData.CameraWorldPos);

    // Define a blend overlap distance in world space
    const float CASCADE_BLEND_OVERLAP_WORLD_SPACE = 2.0f; // Adjust this value as needed, e.g., 0.5m to 2.0m

    int currentCascadeIndex = 0;
    float currentCascadeEnd = 0.0f; // The far plane distance of the current cascade.

    // Determine which cascade this pixel falls into based on its distance from the camera.
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
    else // The pixel is in the last cascade (index 3).
    {
        currentCascadeIndex = 3;
    }

    shadowIntensity = PCSS(input.lightViewPos[currentCascadeIndex], currentCascadeIndex , normal, lightDir, input.pos.xy);

    // Apply an overall shadow strength multiplier.
    float shadowStrength = lightData.SpotAngles.z; // This seems to be repurposed to control shadow strength.
    float finalShadowIntensity = lerp(1.0, shadowIntensity, shadowStrength);

    // --- Final Color Calculation ---
    // Combine ambient and diffuse lighting, modulated by the final shadow intensity.
    float3 lighting = ambient + finalShadowIntensity * diffuse;
    output.color = float4(albedo.rgb * lighting, albedo.a);

    return output;
}