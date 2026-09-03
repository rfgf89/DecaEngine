// Built-in temporal upscaler (TAAU) for the upscaler slot; see TemporalUpscalePass.cs.
// The scene is rasterised at render resolution with a sub-pixel projection jitter and this
// pass accumulates a display-resolution frame over the 16 Halton phases.
// The blend weight is modulated by how close the frame's render sample lands to the display
// pixel centre; unweighted accumulation widens the kernel and comes out softer than a plain
// bilinear upscale.

Texture2D    _SceneTex;    // HDR scene, render resolution, after fog/bloom and before tonemap.
SamplerState _SceneTex_sampler;

Texture2D    _HistoryTex;  // Previous Execute output, display resolution.
SamplerState _HistoryTex_sampler;

Texture2D    _MotionTex;   // Motion vectors (RG16F, screen fractions), render resolution.

// Mirrors TemporalUpscaleConstantsData (TemporalUpscalePass.cs).
cbuffer TemporalUpscaleConstants
{
    float4 TuRender;   // xy render size, zw 1/render size.
    float4 TuFrame;    // xy jitter in render pixels (y down), z blend alpha, w history valid.
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

    float2 uv = input.ndc * float2(0.5, -0.5) + 0.5;

    // Nearest render pixel, unfiltered: averaging across a silhouette invents motion.
    int2 renderPixel = clamp(int2(uv * TuRender.xy), int2(0, 0), int2(TuRender.xy) - 1);
    float2 motion = _MotionTex.Load(int3(renderPixel, 0)).rg;

    // De-jitter: a +J projection jitter shifts content +J pixels; jitter and uv are both y-down.
    float2 currentUv = uv + TuFrame.xy * TuRender.zw;
    float4 current = _SceneTex.SampleLevel(_SceneTex_sampler, currentUv, 0);

    // Neighbourhood clamp against ghosting: history outside the 3x3 box is a trail.
    float3 neighborMin = current.rgb;
    float3 neighborMax = current.rgb;
    [unroll]
    for (int y = -1; y <= 1; y++)
    {
        [unroll]
        for (int x = -1; x <= 1; x++)
        {
            int2 p = clamp(renderPixel + int2(x, y), int2(0, 0), int2(TuRender.xy) - 1);
            float3 c = _SceneTex.Load(int3(p, 0)).rgb;
            neighborMin = min(neighborMin, c);
            neighborMax = max(neighborMax, c);
        }
    }

    // Motion vector convention is prevUV = curUV + motion; off-screen means no history.
    float2 prevUv = uv + motion;
    bool historyValid = TuFrame.w > 0.5 &&
        all(prevUv >= 0.0) && all(prevUv <= 1.0);

    // Gaussian (sigma ~0.4 render pixel) over the sub-pixel offset; the 0.02 floor keeps
    // history from freezing where jitter phases rarely land.
    float2 sampleOffset = frac(currentUv * TuRender.xy + 0.5) - 0.5;
    float proximity = max(exp(-dot(sampleOffset, sampleOffset) / 0.32), 0.02);

    float alpha = historyValid ? TuFrame.z * proximity : 1.0;

    float4 history = _HistoryTex.SampleLevel(_HistoryTex_sampler, prevUv, 0);
    history.rgb = clamp(history.rgb, neighborMin, neighborMax);

    // Alpha is not accumulated: the preview composites over ImGui and would ghost an outline.
    output.color = float4(lerp(history.rgb, current.rgb, alpha), current.a);
    return output;
}
