// Camera-only motion vectors reconstructed from depth (upscaler input, see
// MotionVectorPass.cs): assumes the world point is static, so animated/moving
// objects get the camera vector. Transparency writes no depth, so under glass
// this is the vector of what is BEHIND the glass.

Texture2D _DepthTex;

// Mirrors MotionVectorConstantsData (MotionVectorPass.cs).
cbuffer MotionVectorConstants
{
    // invViewProj(current) * viewProj(previous), composed on CPU so world position
    // never materializes in-shader; float32 world coords lose the sub-pixel
    // precision temporal reprojection depends on.
    float4x4 reprojection;
};

struct VSOutput
{
    float4 pos : SV_POSITION;
    float2 ndc : TEXCOORD0;
};

struct PSOutput
{
    float2 motion : SV_TARGET;
};

PSOutput Main(in VSOutput input)
{
    PSOutput output;

    int2 pixel = int2(input.pos.xy);
    float depth = _DepthTex.Load(int3(pixel, 0)).r;

    // Stay homogeneous until the end: infinite reversed-Z gives sky depth == 0
    // (point at infinity, w == 0); an intermediate divide would NaN the whole sky,
    // while the composed projective transform carries it through correctly.
    float4 prevClip = mul(float4(input.ndc, depth, 1.0), reprojection);

    // Point behind the previous camera (w <= 0) projects mirrored; zero motion is
    // safer, the upscaler treats it as disocclusion.
    if (prevClip.w <= 1e-6)
    {
        output.motion = float2(0.0, 0.0);
        return output;
    }

    float2 prevNdc = prevClip.xy / prevClip.w;

    // Convention: vector points current -> previous frame in UV units, i.e.
    // prevUV = curUV + motion (what DLSS/FSR expect, resolution-independent).
    // NDC -> UV flips Y sign.
    output.motion = (prevNdc - input.ndc) * float2(0.5, -0.5);

    return output;
}
