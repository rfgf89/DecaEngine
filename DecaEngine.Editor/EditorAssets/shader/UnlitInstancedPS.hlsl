#include "Instancing.hlsl"

Texture2D    _MainTex;
SamplerState _MainTex_sampler;

// Contains data about the camera/view (e.g., camera position).
cbuffer View
{
    ViewData viewData;
}

// Model Preview view-mode controls (see DecaEngine.Editor.ModelPreviewViewport /
// ModelIconBaker). PreviewMode: 0 = Textured (_MainTex), 1 = Highlight (flat, camera-rim
// shaded), 2 = Channel debug view. PreviewChannel (used only when PreviewMode == 2):
// 0 = Normal, 1 = UV, 2 = Tangent. Left at zero defaults, this cbuffer is a no-op outside
// the Model Preview feature - regular scene materials never update it.
cbuffer PreviewSettings
{
    int PreviewMode;
    int PreviewChannel;
}

struct PSInput
{
    float4 pos            : SV_POSITION;      // Clip space position.
    float3 normal         : NORMAL;           // Normal vector.
    float2 uv             : TEX_COORD;        // Texture coordinates.
    float3 worldPos       : TEXCOORD1;        // World-space position.
    float3 tangent        : TEXCOORD2;        // World-space tangent (precomputed per vertex).
};

// Output structure for the Pixel Shader.
struct PSOutput
{
    float4 color : SV_TARGET; // Final pixel color.
};

PSOutput Main(in PSInput input)
{
    PSOutput output;

    float3 normal = normalize(input.normal);

    if (PreviewMode == 2)
    {
        float3 visualized;

        if (PreviewChannel == 1)
        {
            visualized = float3(input.uv, 0.0);
        }
        else if (PreviewChannel == 2)
        {
            // Precomputed per-vertex tangent (see MeshUtility.GenerateTangents), interpolated and
            // re-normalized - stable across camera distance/angle, unlike the screen-space-derivative
            // (ddx/ddy) estimate this used to compute, which is perspective-distorted and gets noisy
            // up close or at grazing angles.
            float3 tangent = normalize(input.tangent);
            visualized = tangent * 0.5 + 0.5;
        }
        else
        {
            visualized = normal * 0.5 + 0.5;
        }

        output.color = float4(visualized, 1.0);
        return output;
    }

    // Two-tone hemisphere ambient (cool "sky" above / warm "ground" below, by normal.y) so the
    // mesh reads as a 3D shape even head-on, instead of a flat gray fill.
    float3 skyColor = float3(0.20, 0.21, 0.24);
    float3 groundColor = float3(0.09, 0.08, 0.07);
    float3 hemi = lerp(groundColor, skyColor, normal.y * 0.5 + 0.5);

    // View-facing rim highlight: real camera direction (ViewData.CameraWorldPos, populated per
    // frame - see RenderingComponents.CreateViewData) rather than a normal pushed through the
    // clip-space viewProj matrix, with a power falloff for a crisper edge than a linear one.
    float3 viewDir = normalize(viewData.CameraWorldPos - input.worldPos);
    const float rimPower = 2.0;
    float rim = pow(saturate(dot(normal, viewDir)), rimPower);

    float3 albedo = PreviewMode == 0 ? _MainTex.Sample(_MainTex_sampler, input.uv).rgb : float3(1.0, 1.0, 1.0);
    output.color = float4(albedo * saturate(hemi + rim), 1.0);
    return output;
}