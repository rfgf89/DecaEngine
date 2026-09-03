// Motion vector visualization (see MotionVectorDebugPass.cs).
// Runs after tonemap/grading into the display RGBA8 target so 0.5 stays exactly 0.5.
// Encoding: R/G = XY offset around 0.5; flat grey (0.5, 0.5, 0.5) = zero vector.

Texture2D _MotionTex;

// Mirrors MotionVectorDebugConstantsData (MotionVectorDebugPass.cs).
cbuffer MotionVectorDebugConstants
{
    // xy = motion buffer size in pixels (render res), z = 1/range in pixels, w = enabled (0 -> discard).
    float4 motionDebugParams;

    // xy = render-to-display size ratio; Load needs render-res coordinates.
    float4 motionDebugParams2;
};

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

    // Disabled pass discards every pixel: toggle lives in the cbuffer, no graph rebuild.
    if (motionDebugParams.w < 0.5)
    {
        discard;
        output.color = float4(0.0, 0.0, 0.0, 1.0);
        return output;
    }

    // Nearest Load, not bilinear: filtering would average vectors across silhouettes.
    int2 pixel = int2(input.pos.xy * motionDebugParams2.xy);
    float2 motionUv = _MotionTex.Load(int3(pixel, 0)).rg;

    // Buffer stores screen-fraction vectors (see MotionVectorPS); convert to render-res pixels.
    float2 motionPixels = motionUv * motionDebugParams.xy;
    float2 n = motionPixels * motionDebugParams.z;

    // Out-of-range kills the blue channel (yellow) so range overflow is visible, not clamped away.
    float clipped = (abs(n.x) > 1.0 || abs(n.y) > 1.0) ? 1.0 : 0.0;
    n = clamp(n, -1.0, 1.0);

    output.color = float4(0.5 + 0.5 * n.x, 0.5 + 0.5 * n.y, 0.5 - 0.5 * clipped, 1.0);
    return output;
}
