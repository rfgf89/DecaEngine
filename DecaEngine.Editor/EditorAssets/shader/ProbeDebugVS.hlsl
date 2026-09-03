// No vertex/index buffer: one Draw(24 * probeCount), probe and corner come from SV_VertexID.
#include "Instancing.hlsl"

cbuffer View
{
    ViewData viewData;
}

cbuffer ProbeDebugParams
{
    // xyz = grid origin in world, w = sphere radius in world units.
    float4 DebugGridOrigin;
    // xyz = probe grid step, w = probe count.
    float4 DebugGridCell;
    // xyz = probe grid size.
    float4 DebugGridCounts;
    // xyz = volume color tag.
    float4 DebugTint;
};

Texture2D _ProbeOffset;
Texture2D _ProbeSh0;
Texture2D _ProbeSh1;

struct PSInput
{
    float4 pos      : SV_POSITION;
    float3 normal   : NORMAL;
    // rgb = probe SH L0, a = validity (0 = probe considers itself inside geometry).
    float4 color    : COLOR0;
    // Relocation offset length in fractions of the radius.
    float  offsetLen : TEXCOORD0;
    float3 tint      : TEXCOORD1;
};

// Octahedron of 8 triangles, outward winding.
static const float3 OctaVerts[24] =
{
    float3(0, 1, 0), float3(1, 0, 0), float3(0, 0, 1),
    float3(0, 1, 0), float3(0, 0, 1), float3(-1, 0, 0),
    float3(0, 1, 0), float3(-1, 0, 0), float3(0, 0, -1),
    float3(0, 1, 0), float3(0, 0, -1), float3(1, 0, 0),
    float3(0, -1, 0), float3(0, 0, 1), float3(1, 0, 0),
    float3(0, -1, 0), float3(-1, 0, 0), float3(0, 0, 1),
    float3(0, -1, 0), float3(0, 0, -1), float3(-1, 0, 0),
    float3(0, -1, 0), float3(1, 0, 0), float3(0, 0, -1),
};

// Mirrors ProbeGiBaker.ProbeTexel / ProbeAtlasTexel in ProbeRoundCS.hlsl.
int2 DebugProbeTexel(uint probe)
{
    uint width = max((uint)DebugGridCounts.x, 1u);
    return int2(probe % width, probe / width);
}

PSInput Main(uint vid : SV_VertexID)
{
    uint probe = vid / 24;
    uint corner = vid - probe * 24;

    // Mirrors mainProbe in ProbeRoundCS.hlsl: the storage index doubles as grid coordinates.
    int3 counts = (int3)DebugGridCounts.xyz;
    int3 cell = int3((int)probe % counts.x, (int)probe / counts.x % counts.y,
                     (int)probe / (counts.x * counts.y));

    int3 texel = int3(DebugProbeTexel(probe), 0);
    float3 offset = _ProbeOffset.Load(texel).rgb;
    float3 probePos = DebugGridOrigin.xyz + (float3)cell * DebugGridCell.xyz + offset;

    float radius = DebugGridOrigin.w;
    float3 n = normalize(OctaVerts[corner]);

    PSInput result;
    result.pos = mul(float4(probePos + OctaVerts[corner] * radius, 1.0), viewData.viewProj);
    result.normal = n;
    result.color = float4(_ProbeSh0.Load(texel).rgb, _ProbeSh1.Load(texel).a);
    result.offsetLen = length(offset) / max(radius, 1e-4);
    result.tint = DebugTint.xyz;
    return result;
}
