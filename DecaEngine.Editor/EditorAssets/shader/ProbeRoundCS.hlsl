// GPU probe-GI update round (compute port of ProbeGiBaker.RunRound), one thread per probe.
// Hardware and software tracing share this shader; they differ only by compile keyword.

#include "SceneTrace.hlsl"

cbuffer ProbeRoundParams
{
    // xyz = grid origin (world), w = round blend weight (running average).
    float4 ProbeGridOrigin;
    // xyz = probe grid cell size, w = max ray distance.
    float4 ProbeGridCell;
    // xyz = direction TOWARD the sun, w = rays per round.
    float4 ProbeSunDirection;
    // xyz = sun color/intensity, w = total probe count.
    float4 ProbeSunColor;
    // x = shadow-ray offset epsilon, y = distance clamp for the octahedral depth map,
    // z = gather-point offset along the normal, w = field feedback factor (0 = no bounce).
    float4 ProbeRoundParams;
    // Round chunk: x = first element, y = one past last; z = per-ray luminance cap
    // (0 = off), w = per-round step limit as a fraction of brightness (0 = off).
    // Chunked across frames: one dispatch over all probes TDRs the device.
    float4 ProbeChunk;
    // x = relocation limit in world units (0 = relocation off); y = perceptual
    // accumulation gamma (1 = linear); z = 1 means surface cache off (realtime);
    // w = probe sleep: 0 off, else 1 + (fan index & 3) = wake phase (see mainProbe).
    float4 ProbeRelocation;
    // x = count of FIXED fan rays (ProbeGiBaker.FixedRayCount),
    // y = punctual light count in _ProbeBakeLights.
    float4 ProbeRays;
};

// Stride and formulas must match PunctualLight (LightData.cs, Instancing.hlsl) and
// ProbeGiBaker.EvalPunctualLights - change all copies together.
struct ProbeBakeLight
{
    float4 PositionRange;  // xyz = world position, w = range
    float4 ColorIntensity; // rgb = linear color, w = intensity
    float4 DirectionType;  // xyz = spot cone direction, w = type: 0 point, 1 spot
    float4 SpotAngles;     // x = cos outer half-angle, y = 1/(cosInner-cosOuter)
    float4 ShadowParams;   // unused here; kept for stride
};
StructuredBuffer<ProbeBakeLight> _ProbeBakeLights;

// Precomputed on CPU: an in-shader Fibonacci fan diverges in the last bit and breaks
// bit-exact verification against the CPU path.
StructuredBuffer<float4> _ProbeRayDirections;

// Probe field, four float4 per probe (atlas layout):
//   [0] rgb = SH L0,  a = sky visibility
//   [1] rgb = SH L1x, a = probe validity
//   [2] rgb = SH L1y, a = sun fraction
//   [3] rgb = SH L1z, a = reserved
//
// Double buffered: the gather reads neighbors this round overwrites, so a single
// buffer would make the result depend on group execution order.
StructuredBuffer<float4>   _ProbeFieldRead;
RWStructuredBuffer<float4> _ProbeField;

// xyz = total rays / misses / backface hits, summed across rounds.
RWStructuredBuffer<int4> _ProbeCounters;

// Octahedral depth map, PROBE_VIS_RES^2 per probe: x = sum of distances, y = sum of squares, z = count.
RWStructuredBuffer<float4> _ProbeVisibility;

// Second cbuffer: keeps the first one's layout stable.
cbuffer ProbeGridParams
{
    // xyz = dense probe grid size, w = bounce saturation.
    float4 ProbeGridCounts;
    // xyz = surface-cache voxel size, w = live voxel count (0 = cache off).
    float4 SurfaceVoxel;
    // xyz = surface-cache voxel grid size.
    float4 SurfaceCounts;
    // x = environment yaw (radians), y = sky brightness factor, z = visibility
    // octahedral map side ("Visibility res" knob, ProbeGiBakeResult.VisRes), w = reserved.
    float4 ProbeSkyParams;
};

// Mirror of ProbeGiBaker.StorageIndex; the volume never scrolls, so storage == grid.
int3 ProbeStorageCoords(uint probe)
{
    int cx = (int)ProbeGridCounts.x;
    int cy = (int)ProbeGridCounts.y;
    return int3((int)probe % cx, (int)probe / cx % cy, (int)probe / (cx * cy));
}

// Runtime knob, not a define: the CPU sizes the depth buffer with the same value.
// 0 (unfilled cbuffer) means the default 8.
int ProbeVisRes()
{
    int res = (int)ProbeSkyParams.z;
    return res > 0 ? res : 8;
}

Texture2D    _EnvMap;
SamplerState _EnvMap_sampler;

// Convention must match SampleEnvironment in UnlitInstancedPS: yaw around Y shifts U.
float3 ProbeSampleSky(float3 dir)
{
    const float PI = 3.14159265;
    float2 uv = float2(atan2(dir.z, dir.x) / (2.0 * PI) + 0.5 + ProbeSkyParams.x / (2.0 * PI),
                       acos(clamp(dir.y, -1.0, 1.0)) / PI);
    // Mip 0: the bake needs unblurred radiance; prefiltered mips are for specular.
    return _EnvMap.SampleLevel(_EnvMap_sampler, uv, 0.0).rgb * ProbeSkyParams.y;
}

// Surface radiance cache (SurfaceCache in ProbeGi.cs). _SurfaceIndex is dense over the
// voxel grid with -1 = no surface; the other buffers are per live voxel.
StructuredBuffer<int>    _SurfaceIndex;
StructuredBuffer<float4> _SurfacePosition;
StructuredBuffer<float4> _SurfaceNormal;
StructuredBuffer<float4> _SurfaceAlbedo;

// rgb = voxel outgoing radiance, a = its sun fraction.
RWStructuredBuffer<float4> _SurfaceRadiance;

RWTexture2D<float4> _ProbeAtlasSh0;
RWTexture2D<float4> _ProbeAtlasSh1;
RWTexture2D<float4> _ProbeAtlasSh2;
RWTexture2D<float4> _ProbeAtlasSh3;
RWTexture2D<float4> _ProbeAtlasVis;
RWTexture2D<float4> _ProbeAtlasOffset;

// Probe offsets from grid nodes, world units (ProbeGiBakeResult.Offset).
RWStructuredBuffer<float4> _ProbeOffsets;

// RTXGI-DDGI probe variability: x = coefficient of variation of probe brightness
// (dimensionless), y = averaging weight (0 for in-wall/settled probes).
RWStructuredBuffer<float2> _ProbeVariability;

// Mirror of ProbeGiBaker.ProbeTexel; atlas width equals grid X.
uint2 ProbeAtlasTexel(uint probe)
{
    uint width = max((uint)ProbeGridCounts.x, 1u);
    return uint2(probe % width, probe / width);
}

// Voxel index covering the point, or -1. Mirror of SurfaceCache.Lookup.
int SurfaceLookup(float3 worldPos)
{
    if (SurfaceVoxel.w < 0.5)
    {
        return -1;
    }

    float3 f = (worldPos - ProbeGridOrigin.xyz) / SurfaceVoxel.xyz;
    int3 v = (int3)floor(f);
    if (any(v < 0) || any(v >= (int3)SurfaceCounts.xyz))
    {
        return -1;
    }

    return _SurfaceIndex[(v.z * (int)SurfaceCounts.y + v.y) * (int)SurfaceCounts.x + v.x];
}

static const float SH_Y00 = 0.28209479;
static const float SH_Y1  = 0.48860251;

float ProbeLuminance(float3 c)
{
    return 0.2126 * c.x + 0.7152 * c.y + 0.0722 * c.z;
}

// Inverse of ProbeOctEncode: direction at a depth-map texel center.
float3 ProbeOctDecode(float2 uv)
{
    float2 p = uv * 2.0 - 1.0;
    float3 d = float3(p.x, p.y, 1.0 - abs(p.x) - abs(p.y));
    if (d.z < 0.0)
    {
        d.xy = (1.0 - abs(d.yx)) * float2(d.x >= 0.0 ? 1.0 : -1.0, d.y >= 0.0 ? 1.0 : -1.0);
    }

    return normalize(d);
}

// Depth-map splat lobe (Majercik 4.4): power 64 via six squarings, cutoff ~26 degrees.
#define PROBE_DEPTH_SHARPNESS_SQUARINGS 6
#define PROBE_DEPTH_WEIGHT_EPSILON      0.001

// Sliding window for geometry accumulators, in rounds; realtime only (see main).
#define PROBE_GEOMETRY_WINDOW 64.0

// Must match ProbeGiBaker.OctEncode and OctEncode in UnlitInstancedPS.
float2 ProbeOctEncode(float3 d)
{
    float sum = abs(d.x) + abs(d.y) + abs(d.z);
    float2 p = d.xy / sum;
    if (d.z < 0.0)
    {
        p = (1.0 - abs(p.yx)) * float2(p.x >= 0.0 ? 1.0 : -1.0, p.y >= 0.0 ? 1.0 : -1.0);
    }

    return p * 0.5 + 0.5;
}

// Mirror of ProbeGiBaker.EvalIrradiance. The soft backface weight is required:
// without it multibounce drags light through walls within a few rounds.
float3 ProbeGatherIrradiance(float3 pos, float3 normal, out float sunFracOut)
{
    sunFracOut = 0.0;

    float3 origin = ProbeGridOrigin.xyz;
    float3 cell = ProbeGridCell.xyz;
    float3 f = clamp((pos - origin) / cell, 0.0, ProbeGridCounts.xyz - 1.0);

    int3 counts = (int3)ProbeGridCounts.xyz;
    int3 l = clamp((int3)floor(f), 0, counts - 2);
    float3 t = saturate(f - (float3)l);

    float3 sh0 = 0.0, shX = 0.0, shY = 0.0, shZ = 0.0;
    float fracSum = 0.0;
    float weightSum = 0.0;

    [unroll]
    for (int corner = 0; corner < 8; corner++)
    {
        int3 o = int3(corner & 1, (corner >> 1) & 1, corner >> 2);
        int3 lp = l + o;
        uint index = (uint)((lp.z * counts.y + lp.y) * counts.x + lp.x);

        float4 field1 = _ProbeFieldRead[index * 4 + 1];
        float w = (o.x ? t.x : 1.0 - t.x) * (o.y ? t.y : 1.0 - t.y) * (o.z ? t.z : 1.0 - t.z)
                * field1.a;

        // Wrap weight needs the RELOCATED neighbor position, not the grid node.
        float3 probePos = origin + (float3)lp * cell + _ProbeOffsets[index].xyz;
        float3 toProbe = probePos - pos;
        float toProbeLen = length(toProbe);
        float wrap = (dot(toProbe / max(toProbeLen, 1e-4), normal) + 1.0) * 0.5;
        w *= wrap * wrap + 0.05;

        float4 field0 = _ProbeFieldRead[index * 4 + 0];
        float4 field2 = _ProbeFieldRead[index * 4 + 2];
        float4 field3 = _ProbeFieldRead[index * 4 + 3];

        sh0 += field0.rgb * w;
        shX += field1.rgb * w;
        shY += field2.rgb * w;
        shZ += field3.rgb * w;
        fracSum += field2.a * w;
        weightSum += w;
    }

    if (weightSum < 1e-4)
    {
        return 0.0;
    }

    float inv = 1.0 / weightSum;
    sunFracOut = saturate(fracSum * inv);
    float3 e = (sh0 * inv) * 0.8862269
             + ((shX * inv) * normal.x + (shY * inv) * normal.y + (shZ * inv) * normal.z) * 1.0233267;
    return max(e, 0.0);
}

// Mirror of ProbeGiBaker.EvalPunctualLights. Counts as STATIC light (sunFrac
// denominator): realtime sun-shadow modulation must not touch lamp light.
float3 ProbeEvalPunctualLights(float3 pos, float3 normal)
{
    float3 sum = 0.0;
    uint count = (uint)ProbeRays.y;
    for (uint i = 0; i < count; i++)
    {
        ProbeBakeLight l = _ProbeBakeLights[i];
        float3 toLight = l.PositionRange.xyz - pos;
        float distSq = dot(toLight, toLight);
        float range = l.PositionRange.w;
        if (distSq > range * range)
        {
            continue;
        }

        float dist = sqrt(max(distSq, 1e-6));
        float3 dir = toLight / dist;
        float ndotl = dot(normal, dir);
        if (ndotl <= 0.0)
        {
            continue;
        }

        // Falloff window must mirror clustered shading in UnlitInstancedPS.
        float distRatio2 = distSq / (range * range);
        float distFactor = saturate(1.0 - distRatio2 * distRatio2);
        float atten = distFactor * distFactor / (distSq + 1e-2);

        if (l.DirectionType.w > 0.5)
        {
            float cd = dot(-dir, l.DirectionType.xyz);
            float spotFactor = saturate((cd - l.SpotAngles.x) * l.SpotAngles.y);
            atten *= spotFactor * spotFactor;
            if (atten <= 0.0)
            {
                continue;
            }
        }

        // Shortened at BOTH ends: surface self-shadowing and geometry at the lamp.
        float shadowStart = ProbeRoundParams.x * 4.0;
        if (SceneTraceAnyHit(pos + dir * shadowStart, dir, dist - shadowStart * 2.0))
        {
            continue;
        }

        sum += l.ColorIntensity.rgb * l.ColorIntensity.w * (ndotl * atten);
    }

    return sum;
}

// Surface-cache update (ProbeGiBaker.UpdateSurfaceCache): must run BEFORE the round.
[numthreads(64, 1, 1)]
void mainSurface(uint3 threadId : SV_DispatchThreadID)
{
    uint voxel = (uint)ProbeChunk.x + threadId.x;
    if (voxel >= (uint)ProbeChunk.y || voxel >= (uint)SurfaceVoxel.w)
    {
        return;
    }

    float3 normal = _SurfaceNormal[voxel].xyz;
    float3 pos = _SurfacePosition[voxel].xyz + normal * (ProbeRoundParams.x * 4.0);
    float tMax = ProbeGridCell.w;

    float3 sunIrradiance = 0.0;
    float ndotl = dot(normal, ProbeSunDirection.xyz);
    if (ndotl > 0.0 && !SceneTraceAnyHit(pos, ProbeSunDirection.xyz, tMax))
    {
        sunIrradiance = ProbeSunColor.rgb * ndotl;
    }

    float3 lampIrradiance = ProbeEvalPunctualLights(pos, normal);

    float3 ambient = 0.0;
    float ambientFrac = 0.0;
    float feedback = ProbeRoundParams.w;
    if (feedback > 0.0)
    {
        ambient = ProbeGatherIrradiance(pos, normal, ambientFrac) * feedback;
    }

    float3 irradiance = sunIrradiance + lampIrradiance + ambient;
    float3 rawAlbedo = _SurfaceAlbedo[voxel].rgb;
    float3 albedo = lerp((float3)ProbeLuminance(rawAlbedo), rawAlbedo, ProbeGridCounts.w);

    float lumIrr = ProbeLuminance(irradiance);
    float sunFrac = lumIrr > 1e-6
        ? saturate((ProbeLuminance(sunIrradiance) + ProbeLuminance(ambient) * ambientFrac) / lumIrr)
        : 0.0;

    _SurfaceRadiance[voxel] = float4(albedo * irradiance * (1.0 / 3.14159265), sunFrac);
}

[numthreads(64, 1, 1)]
void main(uint3 threadId : SV_DispatchThreadID)
{
    uint probe = (uint)ProbeChunk.x + threadId.x;
    if (probe >= (uint)ProbeChunk.y || probe >= (uint)ProbeSunColor.w)
    {
        return;
    }

    uint visRes = (uint)ProbeVisRes();

    int3 cell = ProbeStorageCoords(probe);

    // Probe sleep (Majercik 6, simplified): a long-calm probe updates once per 4 rounds.
    // Light or scene changes wake everyone through the CPU-side weight/relocation reset.
    int4 countersPrev = _ProbeCounters[probe];
    int sleepPhase = (int)ProbeRelocation.w;
    if (sleepPhase > 0 && countersPrev.w > 16 && ((int)probe & 3) != sleepPhase - 1)
    {
        return;
    }

    // OFF state: a probe still walled in after the relocation window closed is not
    // traced; its validity is zero anyway. Scene motion reopens the window.
    if (ProbeRelocation.z > 0.5 && ProbeRelocation.x == 0.0
        && countersPrev.x >= 64 && countersPrev.z * 2 > countersPrev.x)
    {
        return;
    }

    // Trace from the CURRENT (relocated) position, or relocation never converges.
    float4 offsetSlot = _ProbeOffsets[probe];
    float3 probeOffset = offsetSlot.xyz;
    float3 probePos = ProbeGridOrigin.xyz + (float3)cell * ProbeGridCell.xyz + probeOffset;

    int rays = (int)ProbeSunDirection.w;
    float tMax = ProbeGridCell.w;
    float sceneEpsilon = ProbeRoundParams.x;
    float visMax = ProbeRoundParams.y;

    // Fixed rays (RTXGI_DDGI_NUM_FIXED_RAYS) do NOT rotate per round and feed only
    // geometry: the relocation decision must not flip with fan rotation, and their
    // over-represented directions would bias radiance. fixedRays == 0 (bake) = no split.
    int fixedRays = (int)ProbeRays.x;
    int blendRays = max(rays - fixedRays, 1);
    float domega = 4.0 * 3.14159265 / (float)blendRays;

    // Probe classification (RTXGI ProbeClassificationCS): a probe with no geometry in
    // its own voxel stops spending estimate rays. Deviations: inactive probes are still
    // sampled, and freezing also requires the probe to be SETTLED. State is the offset
    // slot's .w, NEGATIVE = inactive so that a zeroed buffer means active.
    bool settled = sleepPhase > 0 && countersPrev.w > 16;
    bool probeActive = fixedRays == 0 || !settled || offsetSlot.w > -0.5;
    int rayEnd = probeActive ? rays : fixedRays;

    float3 sum0 = 0.0, sumX = 0.0, sumY = 0.0, sumZ = 0.0;
    float sunLum = 0.0, totalLum = 0.0;
    int missCount = 0, backCount = 0;

    // Geometry-ray stats feed the discrete relocation decision; backCount counts ALL
    // rays because validity needs an accurate fraction rather than a stable one.
    int geomRays = 0, geomBackCount = 0;

    bool nearGeometry = false;

    // Closest BACKFACE is the nearest exit out of geometry; frontfaces measure clearance.
    float closestBackT = tMax; float3 closestBackDir = float3(0.0, 1.0, 0.0);
    float closestFrontT = tMax; float3 closestFrontDir = float3(0.0, 1.0, 0.0);
    float farthestFrontT = 0.0; float3 farthestFrontDir = float3(0.0, 1.0, 0.0);
    uint visBase = probe * visRes * visRes;

    // Realtime only: endless sums would freeze probe geometry at the startup scene.
    // Decay turns them into a sliding window; sum and count scale alike, so means stay
    // unbiased. Must run only on rounds that also REFILL the accumulators, else
    // sleeping probes decay their stats to nothing.
    float geometryDecay = ProbeRelocation.z > 0.5 && probeActive
        ? 1.0 - 1.0 / PROBE_GEOMETRY_WINDOW
        : 1.0;

    if (geometryDecay < 1.0)
    {
        [loop]
        for (uint c = 0; c < visRes * visRes; c++)
        {
            _ProbeVisibility[visBase + c] = _ProbeVisibility[visBase + c] * geometryDecay;
        }

        // The calm counter (.w) is round-based, not a sample statistic: never decay it.
        countersPrev.xyz = (int3)((float3)countersPrev.xyz * geometryDecay);
    }

    // Fan phase as a gentle spatial gradient: a shared fan makes the field blotchy, a
    // golden-ratio phase checkerboards neighbors. Bake uses phase 0 for CPU parity.
    float fanAngle = ProbeRelocation.z > 0.5
        ? frac(dot((float3)cell, float3(0.0731, 0.0937, 0.1181))) * 6.2831853
        : 0.0;
    float fanSin = sin(fanAngle);
    float fanCos = cos(fanAngle);

    [loop]
    for (int r = 0; r < rayEnd; r++)
    {
        float3 dir = _ProbeRayDirections[r].xyz;
        dir = float3(fanCos * dir.x + fanSin * dir.z, dir.y,
                     fanCos * dir.z - fanSin * dir.x);
        SceneHit hit = SceneTraceClosest(probePos, dir, tMax);

        bool isFixed = r < fixedRays;
        bool geometryRay = isFixed || fixedRays == 0;

        if (geometryRay)
        {
            geomRays++;
        }

        if (geometryRay && hit.hit)
        {
            if (hit.backface)
            {
                // FULL distance: the depth-map shortening must not shrink the exit step.
                geomBackCount++;
                if (hit.t < closestBackT)
                {
                    closestBackT = hit.t;
                    closestBackDir = dir;
                }
            }
            else
            {
                if (hit.t < closestFrontT)
                {
                    closestFrontT = hit.t;
                    closestFrontDir = dir;
                }

                if (hit.t > farthestFrontT)
                {
                    farthestFrontT = hit.t;
                    farthestFrontDir = dir;
                }

                // "Near" means closer than the ray's exit from the probe's own voxel.
                float3 spacing = ProbeGridCell.xyz;
                float3 planeT = spacing / max(abs(dir), 1e-6);
                if (hit.t <= min(planeT.x, min(planeT.y, planeT.z)))
                {
                    nearGeometry = true;
                }
            }
        }

        // Fixed rays feed neither radiance nor depth: skip the expensive shading.
        if (isFixed)
        {
            if (!hit.hit)
            {
                missCount++;
            }
            else if (hit.backface)
            {
                backCount++;
            }

            continue;
        }

        float3 radiance = 0.0;
        float sunShare = 0.0;
        float hitT = tMax;

        if (!hit.hit)
        {
            radiance = ProbeSampleSky(dir);
            missCount++;
        }
        else
        {
            hitT = hit.t;
            if (hit.backface)
            {
                backCount++;

                // Shorten by 80% (Majercik 4.1) so Chebyshev treats it as occluding;
                // zeroing instead would skew probes that graze a few bad backfaces.
                hitT *= 0.2;
            }
            else
            {
                // Cache replaces a scattered field gather, but is disabled in realtime:
                // its captured geometry describes a scene that has since moved.
                int surfaceVoxel = ProbeRelocation.z > 0.5
                    ? -1
                    : SurfaceLookup(hit.position + hit.normal * ProbeRoundParams.z);
                if (surfaceVoxel >= 0)
                {
                    float4 cached = _SurfaceRadiance[surfaceVoxel];
                    radiance = cached.rgb;
                    sunShare = cached.a;
                }
                else
                {
                    float3 sunIrradiance = 0.0;
                    float ndotl = dot(hit.normal, ProbeSunDirection.xyz);
                    if (ndotl > 0.0 &&
                        !SceneTraceAnyHit(hit.position + hit.normal * (sceneEpsilon * 4.0),
                                          ProbeSunDirection.xyz, tMax))
                    {
                        sunIrradiance = ProbeSunColor.rgb * ndotl;
                    }

                    float3 lampIrradiance = ProbeEvalPunctualLights(
                        hit.position + hit.normal * (sceneEpsilon * 4.0), hit.normal);

                    float3 prevIrradiance = 0.0;
                    float prevFrac = 0.0;
                    float feedback = ProbeRoundParams.w;
                    if (feedback > 0.0)
                    {
                        prevIrradiance = ProbeGatherIrradiance(
                            hit.position + hit.normal * ProbeRoundParams.z, hit.normal, prevFrac) * feedback;
                    }

                    float3 irradiance = sunIrradiance + lampIrradiance + prevIrradiance;

                    // Chroma clamp toward luma, brightness preserved - mirrors the CPU path.
                    float3 albedo = lerp((float3)ProbeLuminance(hit.albedo), hit.albedo, ProbeGridCounts.w);
                    radiance = albedo * irradiance * (1.0 / 3.14159265);

                    // Bounce inherits its source's sun share.
                    float lumIrr = ProbeLuminance(irradiance);
                    sunShare = lumIrr > 1e-6
                        ? (ProbeLuminance(sunIrradiance) + ProbeLuminance(prevIrradiance) * prevFrac) / lumIrr
                        : 0.0;
                }
            }
        }

        // Outlier suppression: a rare very bright ray (sun disk) never converges away.
        float maxRayLum = ProbeChunk.z;
        if (maxRayLum > 0.0)
        {
            float rayLum = ProbeLuminance(radiance);
            if (rayLum > maxRayLum)
            {
                // Scale, don't clamp per channel: per-channel clipping shifts hue.
                radiance *= maxRayLum / rayLum;
            }
        }

        // Clamp by cell scale, else misses record scene-sized distances and Chebyshev
        // never triggers. Splat over a cone: nearest-texel writes leave octants empty.
        float tv = min(hitT, visMax);
        [loop]
        for (uint dt = 0; dt < visRes * visRes; dt++)
        {
            float2 texelUv = (float2(dt % visRes, dt / visRes) + 0.5)
                           / (float)visRes;
            float w = max(0.0, dot(ProbeOctDecode(texelUv), dir));

            [unroll]
            for (int s = 0; s < PROBE_DEPTH_SHARPNESS_SQUARINGS; s++)
            {
                w *= w;
            }

            if (w < PROBE_DEPTH_WEIGHT_EPSILON)
            {
                continue;
            }

            _ProbeVisibility[visBase + dt] += float4(tv * w, tv * tv * w, w, 0.0);
        }

        float lum = ProbeLuminance(radiance);
        sunLum += lum * sunShare;
        totalLum += lum;

        sum0 += radiance * (SH_Y00 * domega);
        sumX += radiance * (SH_Y1 * dir.x * domega);
        sumY += radiance * (SH_Y1 * dir.y * domega);
        sumZ += radiance * (SH_Y1 * dir.z * domega);
    }

    // Inactive probe: only the state is written; atlases, counters and the depth map
    // must keep last round's still-valid contents.
    if (!probeActive)
    {
        _ProbeOffsets[probe] = float4(probeOffset, nearGeometry ? 1.0 : -1.0);

        // Zero variability at FULL weight: excluding it would average only the restless
        // probes and the volume metric would never drop below its threshold.
        _ProbeVariability[probe] = float2(0.0, 1.0);
        return;
    }

    // A relocated probe restarts its field, which describes the old point. Relocation
    // zeroes the counters, so countersPrev.x == 0 means "moved last round". Realtime
    // only: the CPU path does no such reset and verification must match it.
    bool justRelocated = ProbeRelocation.z > 0.5 && countersPrev.x == 0;

    // Running-average weight, computed on CPU (warm-up rounds land whole).
    float alpha = justRelocated ? 1.0 : ProbeGridOrigin.w;

    uint fieldBase = probe * 4;

    // Must read the READ buffer: under ping-pong, _ProbeField holds the round before
    // last and accumulation would split into flickering even/odd chains.
    float4 prev0 = _ProbeFieldRead[fieldBase + 0];
    float4 prev1 = _ProbeFieldRead[fieldBase + 1];
    float4 prev2 = _ProbeFieldRead[fieldBase + 2];
    float4 prev3 = _ProbeFieldRead[fieldBase + 3];

    // .w = calm-round counter for sleep (updated after blending, see below).
    int4 counters = countersPrev + int4(rays, missCount, backCount, 0);
    _ProbeCounters[probe] = counters;

    float invTotal = 1.0 / max((float)counters.x, 1.0);
    float skyVis = (float)counters.y * invTotal;
    // A probe inside a wall sees mostly backfaces - damp its interpolation weight.
    float validity = saturate(1.0 - (float)counters.z * invTotal * 3.0);
    float roundSunFrac = totalLum > 1e-6 ? saturate(sunLum / totalLum) : 0.0;

    // Deliberately no per-probe adaptive alpha and no zero hysteresis for relocated
    // probes: both go blotchy here. Fast response uses the global weight rollback.
    float alphaEff = alpha;

    float4 out0 = float4(lerp(prev0.rgb, sum0, alphaEff), skyVis);
    float4 out1 = float4(lerp(prev1.rgb, sumX, alphaEff), validity);
    float4 out2 = float4(lerp(prev2.rgb, sumY, alphaEff), lerp(prev2.a, roundSunFrac, alphaEff));
    float4 out3 = float4(lerp(prev3.rgb, sumZ, alphaEff), 1.0);

    // Perceptual accumulation (Majercik 4.2 adapted to SH): the field stays linear, but
    // luminance follows pow(lerp(old^(1/g), new^(1/g), a), g). One factor for all bands
    // so only brightness bends, not directionality.
    float accumGamma = ProbeRelocation.y;
    if (accumGamma > 1.0 && alphaEff < 1.0)
    {
        float lumOld = ProbeLuminance(prev0.rgb);
        float lumNew = ProbeLuminance(sum0);
        float lumLinear = ProbeLuminance(out0.rgb);

        // Darkening only: the symmetric curve leaves dark corridors black.
        if (lumNew < lumOld && lumLinear > 1e-6)
        {
            float invGamma = 1.0 / accumGamma;
            float lumPerceptual = pow(
                lerp(pow(max(lumOld, 0.0), invGamma), pow(max(lumNew, 0.0), invGamma), alphaEff),
                accumGamma);
            float k = lumPerceptual / lumLinear;
            out0.rgb *= k;
            out1.rgb *= k;
            out2.rgb *= k;
            out3.rgb *= k;
        }
    }

    // Step limiter cuts the derivative, not the value, so steady state is unbiased.
    float maxStep = ProbeChunk.w;
    if (maxStep > 0.0 && alphaEff < 1.0)
    {
        float3 delta = out0.rgb - prev0.rgb;
        float deltaLen = length(delta);

        // Scale by the MEAN of old and new, or a probe at zero could never start moving.
        float scale = 0.5 * (length(prev0.rgb) + length(out0.rgb)) + 1e-4;
        float limit = maxStep * scale;

        // RTXGI's minimum absolute darkening step is deliberately absent: it assumes
        // 10-bit quantization and acts as a luminance floor on float32 atlases.
        if (deltaLen > limit)
        {
            // One factor for all four bands, or directionality changes with brightness.
            float k = limit / deltaLen;
            out0.rgb = prev0.rgb + (out0.rgb - prev0.rgb) * k;
            out1.rgb = prev1.rgb + (out1.rgb - prev1.rgb) * k;
            out2.rgb = prev2.rgb + (out2.rgb - prev2.rgb) * k;
            out3.rgb = prev3.rgb + (out3.rgb - prev3.rgb) * k;
        }
    }

    // Calm counter for sleep: the threshold must stay well below the step limit, and
    // the field must match the fresh estimate or slow-crawling probes freeze dark.
    if (ProbeRelocation.z > 0.5)
    {
        float lumPrev = ProbeLuminance(prev0.rgb);
        float lumOut = ProbeLuminance(out0.rgb);
        float rel = abs(lumOut - lumPrev) / (0.5 * (lumPrev + lumOut) + 1e-4);

        float lumEst = ProbeLuminance(sum0);
        float relEst = abs(lumOut - lumEst) / (0.5 * (lumOut + lumEst) + 1e-4);
        counters.w = rel < 0.01 && relEst < 0.25 ? min(counters.w + 1, 255) : 0;
        _ProbeCounters[probe] = counters;
    }

    // Welford-form variance is unbiased for a running average and needs no history.
    // In-wall probes get zero weight: their field is unread and their stats the noisiest.
    {
        float lumSample = ProbeLuminance(sum0);
        float lumPrevMean = ProbeLuminance(prev0.rgb);
        float lumMean = ProbeLuminance(out0.rgb);
        float sigma2 = (lumSample - lumPrevMean) * (lumSample - lumMean);
        float cov = lumMean > 1e-3 ? sqrt(max(sigma2, 0.0)) / lumMean : 0.0;
        _ProbeVariability[probe] = float2(cov, validity > 0.05 ? 1.0 : 0.0);
    }

    _ProbeField[fieldBase + 0] = out0;
    _ProbeField[fieldBase + 1] = out1;
    _ProbeField[fieldBase + 2] = out2;
    _ProbeField[fieldBase + 3] = out3;

    // Relocation pushes a buried probe out: buried probes flicker and leak through walls.
    float relocLimit = ProbeRelocation.x;
    bool relocated = false;
    if (relocLimit > 0.0)
    {
        // Over GEOMETRY rays only: the thresholds below must not flip with fan rotation.
        float backFrac = (float)geomBackCount / (float)max(geomRays, 1);
        float3 newOffset = probeOffset;
        float offsetLen = length(probeOffset);

        float minCell = min(ProbeGridCell.x, min(ProbeGridCell.y, ProbeGridCell.z));

        // Minimum frontface clearance (RTXGI probeMinFrontfaceDistance).
        float minFrontface = 0.3 * minCell;
        if (backFrac > 0.25 && closestBackT < tMax)
        {
            newOffset = probeOffset + closestBackDir * (closestBackT + ProbeRoundParams.z);
        }
        else if (closestFrontT < minFrontface && dot(closestFrontDir, farthestFrontDir) < 0.5)
        {
            // Pressed against a surface: step toward open space. The direction check
            // guards a probe that sees only one surface.
            newOffset = probeOffset + farthestFrontDir * min(0.2 * minCell, farthestFrontT);
        }
        else if (closestFrontT > minFrontface && offsetLen > 1e-5)
        {
            // Open surroundings: drift back toward the grid node, since offsets hurt
            // trilinear interpolation. Clamped so it cannot pierce a thin floor.
            float moveBack = min(closestFrontT - minFrontface, offsetLen);
            newOffset = probeOffset - (probeOffset / offsetLen) * moveBack;
        }

        // Limit is ELLIPSOIDAL in grid-step units, or anisotropic grids trap the probe
        // on the short axis. On overflow the step is DISCARDED, never scaled: a scaled
        // jump out of a wall does not actually exit it.
        float3 normalizedOffset = newOffset / max(ProbeGridCell.xyz, 1e-6);
        float relocLimitCells = relocLimit / max(minCell, 1e-6);
        if (dot(normalizedOffset, normalizedOffset) > relocLimitCells * relocLimitCells)
        {
            newOffset = probeOffset;
        }

        // Only the big jump OUT of a wall resets geometry; resetting on small frontface
        // steps would cold-start a wave of near-surface probes on a dense grid.
        relocated = backFrac > 0.25 && length(newOffset - probeOffset) > relocLimit * 0.1;

        probeOffset = newOffset;
    }

    // Written on every branch: a probe with a closed relocation window must still
    // refresh its classification state.
    float probeState = (fixedRays == 0 || nearGeometry) ? 1.0 : -1.0;
    _ProbeOffsets[probe] = float4(probeOffset, probeState);

    uint2 texel = ProbeAtlasTexel(probe);
    _ProbeAtlasOffset[texel] = float4(probeOffset, probeState);
    _ProbeAtlasSh0[texel] = out0;
    _ProbeAtlasSh1[texel] = out1;
    _ProbeAtlasSh2[texel] = out2;
    _ProbeAtlasSh3[texel] = out3;

    // Empty octants fall back to the probe-wide mean - mirror of ProbeGiBaker.Snapshot.
    float totalT = 0.0;
    float totalCount = 0.0;
    [loop]
    for (uint i = 0; i < visRes * visRes; i++)
    {
        float3 acc = _ProbeVisibility[visBase + i].xyz;
        totalT += acc.x;
        totalCount += acc.z;
    }

    float meanAll = totalCount > 0.0 ? totalT / totalCount : 0.0;
    uint2 visTexelBase = texel * (uint)visRes;
    for (uint ty = 0; ty < (uint)visRes; ty++)
    {
        for (uint tx = 0; tx < (uint)visRes; tx++)
        {
            float3 acc = _ProbeVisibility[visBase + ty * visRes + tx].xyz;
            float mean = acc.z > 0.0 ? acc.x / acc.z : meanAll;
            float mean2 = acc.z > 0.0 ? acc.y / acc.z : meanAll * meanAll;
            _ProbeAtlasVis[visTexelBase + uint2(tx, ty)] = float4(mean, mean2, 0.0, 0.0);
        }
    }

    // Must reset AFTER the atlas writes: this round's rays came from the old position.
    if (relocated)
    {
        _ProbeCounters[probe] = int4(0, 0, 0, 0);
        for (uint v = 0; v < visRes * visRes; v++)
        {
            _ProbeVisibility[visBase + v] = float4(0.0, 0.0, 0.0, 0.0);
        }
    }
}
