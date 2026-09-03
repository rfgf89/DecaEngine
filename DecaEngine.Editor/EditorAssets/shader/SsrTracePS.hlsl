// Stochastic SSR (Stachowiak): one GGX importance ray per pixel.
// RT0 = hit color + confidence; RT1 = hit UV (or oct-encoded RT dir) + ray pdf +
// mask (1 = screen hit, 0.5 = RT hit, 0 = miss).
// FEATURE_RT_REFLECTIONS (DXC/SM6.5) continues off-screen rays via inline RayQuery.
#include "SsrCommon.hlsl"

Texture2D<float> _DepthTex;
Texture2D _NormalRoughTex;
Texture2D _EnvFactorTex;
Texture2D _SceneTex;
SamplerState _SceneTex_sampler;

// Half-res pre-blurred scene copy: rough rays read it instead of averaging sharp taps.
Texture2D _SceneBlurTex;
SamplerState _SceneBlurTex_sampler;

Texture2D _EnvMap;
SamplerState _EnvMap_sampler;

// SH L1 probe atlases, declared unconditionally: Vulkan requires declared slots to be
// bound; without a field they hold placeholders, gated off by ssrProbeOrigin.w.
Texture2D _ProbeSh0;
Texture2D _ProbeSh1;
Texture2D _ProbeSh2;
Texture2D _ProbeSh3;

// Static caps: FXC/DXC cannot handle an unbounded [loop].
static const int SsrMaxSteps = 48;
static const int SsrRefineSteps = 6;

// Above this roughness the reflection comes from the probe field; matched to the
// composite's RoughnessFade so there is no seam between techniques.
static const float SsrDiffuseRoughness = 0.75;

// Scale-relative self-intersection bias: a constant world bias exceeds wall
// thickness on small scenes and starts rays on the far side.
float SsrRayEpsilon(float dist)
{
    return clamp(0.005 * dist, 5e-4, 0.05);
}

// Trilinear 8-corner probe sample, no Chebyshev test (full version: ProbeGiSampleBody.hlsl).
// Returns E/PI; valid = 0 when there is no field or the point is outside the volume.
float3 SsrSampleProbeField(float3 worldPos, float3 N, out float valid)
{
    valid = 0.0;
    if (ssrProbeOrigin.w < 0.5)
    {
        return float3(0.0, 0.0, 0.0);
    }

    float3 counts3 = ssrProbeCounts.xyz;
    float3 f = (worldPos - ssrProbeOrigin.xyz) / ssrProbeCell.xyz;
    if (any(f < 0.0) || any(f > counts3 - 1.0))
    {
        return float3(0.0, 0.0, 0.0);
    }

    int3 counts = (int3)counts3;
    int3 localCell = clamp((int3)floor(f), 0, counts - 2);
    float3 t = saturate(f - (float3)localCell);

    float4 sum0 = 0.0;
    float3 sumX = 0.0, sumY = 0.0, sumZ = 0.0;
    float weightSum = 0.0;

    [loop]
    for (int corner = 0; corner < 8; corner++)
    {
        int3 offset = int3(corner & 1, (corner >> 1) & 1, corner >> 2);
        int3 lp = localCell + offset;

        // Node -> texel: Z planes stacked along Y (mirrors ProbeGiBaker.ProbeTexel).
        int3 texel = int3(lp.x, lp.z * counts.y + lp.y, 0);

        float4 sh1 = _ProbeSh1.Load(texel);
        float w = sh1.a;
        if (w < 1e-3)
        {
            continue;
        }

        // DDGI wrap shading weight; constants match ProbeGiSampleBody.
        float3 probeWorld = ssrProbeOrigin.xyz + (float3)lp * ssrProbeCell.xyz;
        float3 toProbe = probeWorld - worldPos;
        float wrap = (dot(toProbe / max(length(toProbe), 1e-4), N) + 1.0) * 0.5;
        w *= wrap * wrap + 0.2;

        float trilinear = (offset.x ? t.x : 1.0 - t.x)
                        * (offset.y ? t.y : 1.0 - t.y)
                        * (offset.z ? t.z : 1.0 - t.z);
        w *= trilinear;
        if (w < 1e-5)
        {
            continue;
        }

        sum0 += _ProbeSh0.Load(texel) * w;
        sumX += sh1.rgb * w;
        sumY += _ProbeSh2.Load(texel).rgb * w;
        sumZ += _ProbeSh3.Load(texel).rgb * w;
        weightSum += w;
    }

    if (weightSum < 1e-4)
    {
        return float3(0.0, 0.0, 0.0);
    }

    valid = 1.0;
    float inv = 1.0 / weightSum;
    return float3(
        SsrIrradianceL1(sum0.r * inv, float3(sumX.r, sumY.r, sumZ.r) * inv, N),
        SsrIrradianceL1(sum0.g * inv, float3(sumX.g, sumY.g, sumZ.g) * inv, N),
        SsrIrradianceL1(sum0.b * inv, float3(sumX.b, sumY.b, sumZ.b) * inv, N));
}

// Blends by the REFLECTOR's roughness; single-level stand-in for Stachowiak's mip chain.
float3 SsrSceneColor(float2 uv, float roughness)
{
    float3 sharp = _SceneTex.SampleLevel(_SceneTex_sampler, uv, 0.0).rgb;
    float blurAmount = smoothstep(0.15, 0.6, roughness);
    if (blurAmount <= 0.0)
    {
        return sharp;
    }

    float3 blurred = _SceneBlurTex.SampleLevel(_SceneBlurTex_sampler, uv, 0.0).rgb;
    return lerp(sharp, blurred, blurAmount);
}

// Is a world point visible on THIS frame's screen. The normal test is required: equal
// depth along the view ray can belong to a different surface. abs() because the hit may
// be the back side of a two-sided plane whose screen pixel shows the front.
bool SsrTryScreenHit(float3 worldPos, float3 hitNormal, float2 viewportSize, out float2 uv)
{
    uv = float2(0.0, 0.0);

    float4 clip = mul(float4(worldPos, 1.0), viewData.viewProj);
    if (clip.w <= 1e-4)
    {
        return false;
    }

    uv = float2(clip.x / clip.w * 0.5 + 0.5, 0.5 - clip.y / clip.w * 0.5);
    if (any(uv < 0.0) || any(uv > 1.0))
    {
        return false;
    }

    int2 pixel = clamp(int2(uv * viewportSize), int2(0, 0), int2(viewportSize) - 1);
    float raw = _DepthTex.Load(int3(pixel, 0));
    if (raw < 1e-6)
    {
        return false;
    }

    float sceneZ = SsrViewDepth(raw);
    float pointZ = mul(float4(worldPos, 1.0), viewData.view).z;

    // Depth-relative tolerance with a small floor; absolute values fail on small scenes.
    if (abs(sceneZ - pointZ) >= max(0.025 * sceneZ, 0.005))
    {
        return false;
    }

    float3 screenN = _NormalRoughTex.Load(int3(pixel, 0)).xyz;
    return dot(screenN, screenN) < 0.5 || abs(dot(normalize(screenN), hitNormal)) > 0.35;
}

#if FEATURE_RT_REFLECTIONS
#define SCENE_TRACE_HARDWARE 1
#include "SceneTrace.hlsl"

// Punctual lights bound via IBatchRenderer.BindShadowResources, as VolumetricCommon.hlsl.
cbuffer Light
{
    LightData lightData;
}

StructuredBuffer<PunctualLight> PunctualLights;

#if FEATURE_RT_HIT_ATLAS
// Cheap hit-texture mode: one 128^2 atlas layer per scene base-color texture.
Texture2DArray _SceneHitAtlas;
SamplerState _SceneHitAtlas_sampler;
#endif

#if FEATURE_RT_HIT_BINDLESS
// Full-size base-color textures, array size = ProbeInstancedGeometry.MaxHitTextures.
// Load-only, so Diligent's combined-sampler mode needs no _sampler pair.
Texture2D _SceneHitTex[64];
#endif

// Textured hit albedo when available, else the per-triangle average from the TLAS tables.
// The 0.85 clamp matches the per-triangle path for a single multibounce energy balance.
float3 SsrHitAlbedo(SceneHit hit, float roughness)
{
#if FEATURE_RT_HIT_ATLAS
    if (hit.textureIndex >= 0)
    {
        float3 texel = _SceneHitAtlas.SampleLevel(_SceneHitAtlas_sampler,
            float3(hit.uv, (float)hit.textureIndex), 0.0).rgb;
        // Engine textures are not sRGB formats: linearize manually.
        return min(pow(max(texel, 0.0), 2.2) * hit.baseColorFactor, 0.85);
    }
#elif FEATURE_RT_HIT_BINDLESS
    if (hit.textureIndex >= 0)
    {
        uint index = NonUniformResourceIndex((uint)hit.textureIndex);
        uint w, h, mips;
        _SceneHitTex[index].GetDimensions(0, w, h, mips);

        // Mip from ray footprint. No UV derivatives here, so texel density is assumed
        // at one texture per ~4 world units; erring finer is covered by the bilinear.
        float pixelAngle = 2.0 / max(viewData.viewport.w, 1.0);
        float texelsAcross = hit.t * pixelAngle * (float)max(w, h) * 0.25;
        float mipF = log2(max(texelsAcross, 1.0)) + roughness * 2.0;
        uint mip = min((uint)mipF, mips - 1u);
        uint mipW = max(w >> mip, 1u);
        uint mipH = max(h >> mip, 1u);

        // Manual bilinear with Wrap-style neighbor wrapping (Load does not filter).
        float2 texelPos = frac(hit.uv) * float2(mipW, mipH) - 0.5;
        float2 baseFloor = floor(texelPos);
        float2 blend = texelPos - baseFloor;
        int2 size = int2(mipW, mipH);
        int2 c00 = (int2(baseFloor) % size + size) % size;
        int2 c11 = (c00 + 1) % size;
        float3 t00 = _SceneHitTex[index].Load(int3(c00, mip)).rgb;
        float3 t10 = _SceneHitTex[index].Load(int3(c11.x, c00.y, mip)).rgb;
        float3 t01 = _SceneHitTex[index].Load(int3(c00.x, c11.y, mip)).rgb;
        float3 t11 = _SceneHitTex[index].Load(int3(c11, mip)).rgb;
        float3 texel = lerp(lerp(t00, t10, blend.x), lerp(t01, t11, blend.x), blend.y);

        return min(pow(max(texel, 0.0), 2.2) * hit.baseColorFactor, 0.85);
    }
#endif
    return hit.albedo;
}

// Analytic light at an off-screen hit, albedo excluded: sun + probe field + punctual.
// The light loop is linear because the cluster grid is screen-space and misses the hit.
float3 SsrAnalyticHitLight(float3 pos, float3 hitN, float eps, int2 noisePixel)
{
    float3 sunDir = normalize(ssrSunDirWorld.xyz);
    float ndl = saturate(dot(hitN, sunDir));
    float sunLit = 1.0;
    if (ndl > 0.0)
    {
        // Shadow ray jittered in the sun cone; ssrSunDirWorld.w = tan of the half-angle.
        float tanHalf = ssrSunDirWorld.w;
        if (tanHalf > 1e-5)
        {
            float3 up = abs(sunDir.y) < 0.95 ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0);
            float3 tx = normalize(cross(up, sunDir));
            float3 ty = cross(sunDir, tx);

            float u1 = SsrNoise(float2(noisePixel) + float2(23.0, 7.0), ssrFrameIndex * 1.37);
            float u2 = SsrNoise(float2(noisePixel) + float2(5.0, 41.0), ssrFrameIndex * 2.11);
            float radius = tanHalf * sqrt(saturate(u1));
            float phi = 2.0 * SsrPI * u2;
            sunDir = normalize(sunDir + (tx * cos(phi) + ty * sin(phi)) * radius);
        }

        sunLit = SceneTraceAnyHit(pos + hitN * eps, sunDir, 1e4) ? 0.0 : 1.0;
    }

    float probeValid;
    float3 probeIrr = SsrSampleProbeField(pos, hitN, probeValid);
    float3 ambient = probeValid > 0.5
        ? probeIrr
        : SsrSampleEnvironment(_EnvMap, _EnvMap_sampler, hitN, 1.0) * ssrSunColor.w;

    float3 punctual = float3(0.0, 0.0, 0.0);
    uint punctualOffset = (uint)lightData.ClusterParams.x;
    uint punctualCount = min((uint)lightData.ClusterParams.y, 16u);
    [loop]
    for (uint li = 0; li < punctualCount; li++)
    {
        PunctualLight l = PunctualLights[punctualOffset + li];
        float3 toLight = l.PositionRange.xyz - pos;
        float distSq = dot(toLight, toLight);
        float range = l.PositionRange.w;
        if (distSq > range * range)
        {
            continue;
        }

        float distL = sqrt(max(distSq, 1e-6));
        float3 dirToLight = toLight / distL;
        float pndl = saturate(dot(hitN, dirToLight));
        if (pndl <= 0.0)
        {
            continue;
        }

        float distRatio2 = distSq / (range * range);
        float distFactor = saturate(1.0 - distRatio2 * distRatio2);
        float atten = distFactor * distFactor / (distSq + 1e-2);
        if (l.DirectionType.w > 0.5)
        {
            float cd = dot(-dirToLight, l.DirectionType.xyz);
            float spotFactor = saturate((cd - l.SpotAngles.x) * l.SpotAngles.y);
            atten *= spotFactor * spotFactor;
        }

        if (atten <= 1e-6)
        {
            continue;
        }

        if (SceneTraceAnyHit(pos + hitN * eps, dirToLight, distL))
        {
            continue;
        }

        punctual += l.ColorIntensity.rgb * l.ColorIntensity.w * (pndl * atten);
    }

    return ssrSunColor.rgb * ndl * sunLit + ambient + punctual;
}

// Env-map stand-in for a metallic hit with no bounce chain (metal F0 = base color);
// without it such metal renders black, its Lambert term killed by metalness.
float3 SsrMetalEnvSpec(SceneHit hit, float3 rayDir, float3 hitN, float roughness)
{
    // Smooth mask, not a threshold: raw per-triangle metalness is noisy.
    float metalMask = smoothstep(0.5, 0.9, hit.metalness);
    if (metalMask <= 0.0)
    {
        return float3(0.0, 0.0, 0.0);
    }

    // Blur by the HIT's baked roughness; the viewer's roughness only ADDS blur.
    float envRough = saturate(max(hit.roughness, roughness * 0.5));
    return SsrHitAlbedo(hit, roughness) * metalMask
        * SsrSampleEnvironment(_EnvMap, _EnvMap_sampler, reflect(rayDir, hitN), envRough);
}

// Radiance leaving an RT hit: screen reprojection when visible, else analytic shading.
float3 SsrRtHitRadiance(SceneHit hit, float3 rayDir, float roughness, float2 viewportSize,
    int2 noisePixel)
{
    float2 seenUv;
    if (SsrTryScreenHit(hit.position, hit.smoothNormal, viewportSize, seenUv))
    {
        return SsrSceneColor(seenUv, roughness);
    }

    // Diffuse is scaled by metalness: metal energy lives in the env stand-in below.
    float3 hitN = hit.backface ? -hit.smoothNormal : hit.smoothNormal;
    return SsrHitAlbedo(hit, roughness) * (1.0 - smoothstep(0.5, 0.9, hit.metalness))
        * SsrAnalyticHitLight(hit.position, hitN, SsrRayEpsilon(hit.t), noisePixel)
        + SsrMetalEnvSpec(hit, rayDir, hitN, roughness);
}
#endif

struct PSOutput
{
    float4 rayColor : SV_TARGET0;
    float4 rayHit : SV_TARGET1;
};

PSOutput Main(in VSOutput input)
{
    PSOutput output;
    output.rayColor = float4(0.0, 0.0, 0.0, 0.0);
    output.rayHit = float4(0.0, 0.0, 0.0, 0.0);

    float2 viewportSize = viewData.viewport.zw;
    int2 pixel = int2(input.pos.xy);

    // Reversed-Z clears to 0; a zero G-buffer normal masks sky and non-PBR paths.
    float centerRaw = _DepthTex.Load(int3(pixel, 0));
    if (centerRaw < 1e-6)
    {
        return output;
    }

    float4 gbuffer = _NormalRoughTex.Load(int3(pixel, 0));
    float roughness = gbuffer.a;
    float3 nWorld = gbuffer.xyz;
    if (dot(nWorld, nWorld) < 0.5 || roughness > ssrMaxRoughness)
    {
        return output;
    }

    nWorld = normalize(nWorld);
    float3 P = SsrViewPos(pixel, centerRaw, viewportSize);
    float3 N = SsrWorldDirToView(nWorld);
    float3 V = -normalize(P);
    if (dot(N, V) <= 0.0)
    {
        return output;
    }

    float confBase = SsrRoughnessFade(roughness);

    // Near-cosine lobe: read probe irradiance directly, no ray. Falls through if no field.
    if (roughness > SsrDiffuseRoughness)
    {
        float3 worldPos = viewData.CameraWorldPos + mul(P, transpose((float3x3)viewData.view));
        float probeValid;
        float3 irr = SsrSampleProbeField(worldPos, nWorld, probeValid);
        if (probeValid > 0.5)
        {
            output.rayColor = float4(irr, confBase);
            // pdf = 1 and mirror direction keep neighbor resolve weights consistent.
            output.rayHit = float4(SsrOctEncode(reflect(-V, N)), 1.0, 0.5);
            return output;
        }
    }

    float3 R;
    float pdf;
    if (roughness < SsrMirrorRoughness)
    {
        // pdf evaluated at H = N (lobe peak) so the ratio estimator's weights stay
        // consistent across neighboring mirror pixels.
        R = reflect(-V, N);
        float m = max(roughness * roughness, 1e-3);
        float m2 = m * m;
        pdf = m2 / (SsrPI * m2 * m2);
    }
    else
    {
        float u1 = SsrNoise(float2(pixel), ssrFrameIndex);
        float u2 = SsrNoise(float2(pixel) + float2(37.0, 17.0), ssrFrameIndex * 1.618);
        float4 H = SsrSampleGgxHalfVector(N, roughness, u1, u2);
        pdf = H.w;
        R = reflect(-V, H.xyz);
        if (dot(R, N) < 0.02)
        {
            // Sample fell below the surface; dropping it instead would flicker.
            R = reflect(-V, N);
        }
    }

    // ssrTraceMode = 1: skip the screen march and go straight to RayQuery.
#if FEATURE_RT_REFLECTIONS
    bool rtOnly = ssrTraceMode > 0.5;
#else
    bool rtOnly = false;
#endif

    // March stops at the near plane: the screen holds nothing beyond it.
    float maxT = ssrMaxDistance;
    if (P.z + R.z * maxT < SsrNearPlane * 1.5)
    {
        maxT = (SsrNearPlane * 1.5 - P.z) / min(R.z, -1e-5);
    }

    float3 P1 = P + R * maxT;

    // Perspective-correct march: UV and 1/z interpolate linearly in screen space.
    float2 uv0 = SsrProjectUv(P, viewportSize);
    float2 uv1 = SsrProjectUv(P1, viewportSize);
    float q0 = 1.0 / P.z;
    float q1 = 1.0 / P1.z;

    float jitter = SsrNoise(float2(pixel) + float2(11.0, 53.0), ssrFrameIndex * 2.618);

    float hitS = -1.0;
    float prevS = 0.0;
    [loop]
    for (int i = 0; i < (rtOnly ? 0 : SsrMaxSteps); i++)
    {
        float s = (i + jitter) / SsrMaxSteps;
        float2 uv = lerp(uv0, uv1, s);
        if (any(uv < 0.0) || any(uv > 1.0))
        {
            break;
        }

        float rayZ = 1.0 / lerp(q0, q1, s);
        int2 tap = clamp(int2(uv * viewportSize), int2(0, 0), int2(viewportSize) - 1);
        float tapRaw = _DepthTex.Load(int3(tap, 0));
        if (tapRaw >= 1e-6)
        {
            float sceneZ = SsrViewDepth(tapRaw);

            // Depth-relative bias against false self-hits at grazing angles.
            if (rayZ > sceneZ + max(0.005 * sceneZ, 1e-3))
            {
                if (rayZ - sceneZ < ssrThickness + 0.02 * sceneZ)
                {
                    hitS = s;
                    break;
                }
            }
        }

        prevS = s;
    }

    if (hitS > 0.0)
    {
        // Binary refinement so the reflection edge sticks to geometry, not the step grid.
        float lo = prevS;
        float hi = hitS;
        [unroll]
        for (int r = 0; r < SsrRefineSteps; r++)
        {
            float mid = (lo + hi) * 0.5;
            float2 uv = lerp(uv0, uv1, mid);
            float rayZ = 1.0 / lerp(q0, q1, mid);
            int2 tap = clamp(int2(uv * viewportSize), int2(0, 0), int2(viewportSize) - 1);
            float tapRaw = _DepthTex.Load(int3(tap, 0));
            float sceneZ = tapRaw >= 1e-6 ? SsrViewDepth(tapRaw) : 1e9;
            if (rayZ > sceneZ)
            {
                hi = mid;
            }
            else
            {
                lo = mid;
            }
        }

        float2 hitUv = lerp(uv0, uv1, hi);
        int2 hitPixel = clamp(int2(hitUv * viewportSize), int2(0, 0), int2(viewportSize) - 1);

        // A hit on a surface facing away from the ray is a leak through geometry: reject.
        float3 hitNWorld = _NormalRoughTex.Load(int3(hitPixel, 0)).xyz;
        float3 rWorld = SsrViewDirToWorld(R);
        if (dot(hitNWorld, hitNWorld) < 0.5 || dot(hitNWorld, rWorld) < 0.1)
        {
            // Bilinear at the refined SUB-PIXEL UV; a whole-pixel Load flickers under jitter.
            output.rayColor = float4(SsrSceneColor(hitUv, roughness), confBase * SsrEdgeFade(hitUv));
            output.rayHit = float4(hitUv, pdf, 1.0);
            return output;
        }
    }

#if FEATURE_RT_REFLECTIONS
    {
        float3 originWorld = viewData.CameraWorldPos + mul(P, transpose((float3x3)viewData.view));
        float3 dirWorld = SsrViewDirToWorld(R);

        // No ssrMaxDistance cap: RayQuery costs the same at any tMax, and capping made
        // far-wall reflections fall into the dark lower env hemisphere.
        SceneHit hit = SceneTraceClosest(originWorld + nWorld * SsrRayEpsilon(P.z), dirWorld, 1e4);

        // For MIRROR pixels rayHit.z carries the RAY LENGTH, not pdf: resolve needs it
        // to reproject the virtual image (RTG ch.32) and derives the mirror pdf itself.
        bool mirrorPath = roughness < SsrMirrorRoughness;

        if (!hit.hit)
        {
            // The ray proved sky visibility, so bypass the surface's baked sky occlusion.
            float3 sky = SsrSampleEnvironment(_EnvMap, _EnvMap_sampler, dirWorld, roughness);
            output.rayColor = float4(sky, confBase);
            output.rayHit = float4(SsrOctEncode(R), mirrorPath ? 1e4 : pdf, 0.5);
            return output;
        }

        // Debug view 5, bounce-chain map: red = chain weight, green = metal without a
        // chain, blue = diffuse hit, yellow = untextured hit, black = not reached.
        if (ssrDebugView > 4.5)
        {
            float metalHere = smoothstep(0.5, 0.9, hit.metalness);
            float blendHere = metalHere * (1.0 - smoothstep(0.15, 0.4, hit.roughness))
                * (ssrBounces > 1.5 ? 1.0 : 0.0);
            float3 flag = blendHere > 0.02
                ? float3(blendHere, 0.0, 0.0)
                : (metalHere > 0.5 ? float3(0.0, 1.0, 0.0) : float3(0.0, 0.0, 1.0));

            if (hit.textureIndex < 0)
            {
                flag = float3(1.0, 1.0, 0.0);
            }
            output.rayColor = float4(flag, confBase);
            output.rayHit = float4(SsrOctEncode(R), mirrorPath ? hit.t : pdf, 0.5);
            return output;
        }

        // Debug view 4: raw hit albedo, no lighting or reprojection.
        if (ssrDebugView > 3.5)
        {
            output.rayColor = float4(SsrHitAlbedo(hit, roughness), confBase);
            output.rayHit = float4(SsrOctEncode(R), mirrorPath ? hit.t : pdf, 0.5);
            return output;
        }

        // Decided BEFORE screen reprojection: the scene snapshot predates the SSR
        // composite, so reprojecting a mirror pixel would lock it to one bounce.
        // The weight is smooth because metalness/roughness are baked per triangle and a
        // hard threshold flips shading mode per triangle, reading as an edge mosaic.
        float hitMetal = smoothstep(0.5, 0.9, hit.metalness);
        float chainBlend = hitMetal * (1.0 - smoothstep(0.15, 0.4, hit.roughness));
        bool chainTaken = chainBlend > 0.02 && ssrBounces > 1.5;

        // Resolve needs the screen-hit mask to get an exact L from the view position.
        float2 seenUv;
        if (!chainTaken && SsrTryScreenHit(hit.position, hit.smoothNormal, viewportSize, seenUv))
        {
            output.rayColor = float4(SsrSceneColor(seenUv, roughness), confBase);
            output.rayHit = float4(seenUv, pdf, 1.0);
            return output;
        }

        // Smooth normal here and in the continuations: the geometric normal facets on
        // dense meshes and shows as a mosaic in chained reflections.
        float3 hitN = hit.backface ? -hit.smoothNormal : hit.smoothNormal;
        float hitEps = SsrRayEpsilon(hit.t);

        // Diffuse scaled by (1 - metal): metal energy lives in the mirror continuation.
        float3 lit = SsrHitAlbedo(hit, roughness) * (1.0 - hitMetal)
            * SsrAnalyticHitLight(hit.position, hitN, hitEps, pixel);

        // Mirror-in-mirror continuations up to ssrBounces. Gated on the HIT's baked
        // metalness, never on the viewer pixel's roughness.
        float3 metalEnv = SsrMetalEnvSpec(hit, dirWorld, hitN, roughness);
        float3 chainSpec = float3(0.0, 0.0, 0.0);

        if (chainTaken)
        {
            // Hop tint: metal F0 = its base color.
            float3 bounceDir = reflect(dirWorld, hitN);
            float3 bounceOrigin = hit.position + hitN * hitEps;
            float3 bounceTint = hitMetal * SsrHitAlbedo(hit, roughness);
            int bounceCap = (int)clamp(ssrBounces, 1.0, 4.0);

            [loop]
            for (int bounce = 1; bounce < bounceCap; bounce++)
            {
                SceneHit hitB = SceneTraceClosest(bounceOrigin, bounceDir, 1e4);
                if (!hitB.hit)
                {
                    chainSpec += SsrSampleEnvironment(_EnvMap, _EnvMap_sampler, bounceDir, roughness)
                        * bounceTint;
                    break;
                }

                float3 hitBn = hitB.backface ? -hitB.smoothNormal : hitB.smoothNormal;

                // The remainder goes to hit radiance, so the chain conserves energy.
                float metalB = smoothstep(0.5, 0.9, hitB.metalness);
                float blendB = metalB * (1.0 - smoothstep(0.15, 0.4, hitB.roughness));
                bool lastHop = bounce + 1 >= bounceCap;
                chainSpec += SsrRtHitRadiance(hitB, bounceDir, roughness, viewportSize, pixel)
                    * bounceTint * (lastHop ? 1.0 : 1.0 - blendB);
                if (lastHop || blendB <= 0.02)
                {
                    break;
                }

                bounceOrigin = hitB.position + hitBn * SsrRayEpsilon(hitB.t);
                bounceDir = reflect(bounceDir, hitBn);
                bounceTint *= metalB * SsrHitAlbedo(hitB, roughness);
            }
        }

        lit += lerp(metalEnv, chainSpec, chainTaken ? chainBlend : 0.0);

        output.rayColor = float4(lit, confBase);
        output.rayHit = float4(SsrOctEncode(R), mirrorPath ? hit.t : pdf, 0.5);
        return output;
    }
#endif

    return output;
}
