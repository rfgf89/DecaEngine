// Selection outline composite. There is no blend state in the engine PSO, so the pass reads a copy
// of the frame and writes the whole result instead. Fullscreen triangle comes from SkyBackgroundVS.

// Selection silhouette mask (1 = object).
Texture2D _MaskTex;

// Copy of the finished frame, taken before this pass.
Texture2D _SceneTex;

struct VSOutput
{
    float4 pos : SV_POSITION;
    float2 ndc : TEXCOORD0;
};

float LoadMask(int2 p)
{
    // Load outside the target returns 0, so the outline clips cleanly at screen edges.
    return _MaskTex.Load(int3(p, 0)).r;
}

float4 Main(in VSOutput input) : SV_TARGET
{
    int2 p = int2(input.pos.xy);
    float3 scene = _SceneTex.Load(int3(p, 0)).rgb;
    float center = LoadMask(p);

    // Dilate the mask with a radius 1-2 ring of taps: a ~2px outline.
    float ring = 0.0;
    ring = max(ring, LoadMask(p + int2( 1,  0)));
    ring = max(ring, LoadMask(p + int2(-1,  0)));
    ring = max(ring, LoadMask(p + int2( 0,  1)));
    ring = max(ring, LoadMask(p + int2( 0, -1)));
    ring = max(ring, LoadMask(p + int2( 1,  1)));
    ring = max(ring, LoadMask(p + int2(-1,  1)));
    ring = max(ring, LoadMask(p + int2( 1, -1)));
    ring = max(ring, LoadMask(p + int2(-1, -1)));
    ring = max(ring, LoadMask(p + int2( 2,  0)));
    ring = max(ring, LoadMask(p + int2(-2,  0)));
    ring = max(ring, LoadMask(p + int2( 0,  2)));
    ring = max(ring, LoadMask(p + int2( 0, -2)));

    // Dilated mask minus the mask: the outline sits outside the silhouette.
    float edge = saturate(ring - center);

    const float3 outlineColor = float3(1.0, 0.55, 0.1);
    return float4(lerp(scene, outlineColor, edge), 1.0);
}
