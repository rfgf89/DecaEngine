// Probe-GI sampling body for one cascade volume; included once per cascade with
// redefined macros (see UnlitInstancedPS.hlsl) because HLSL before SM 6.6 cannot
// pass textures as function parameters.
//
// Expected macros:
//   PROBE_GI_FN       - name of the generated function
//   PROBE_GI_SH0..SH3, PROBE_GI_VIS, PROBE_GI_OFFSET - volume atlas textures
//   PROBE_GI_ORIGIN / PROBE_GI_CELL / PROBE_GI_COUNTS / PROBE_GI_SCROLL - grid float4s
//
// Returns (-1,-1,-1) outside the volume or with no usable weights; the caller falls
// through to the next coarser cascade (see SampleProbeGiCascaded).
// sunFraction: sun share of the field (Sh2 alpha); the screen-space key shadow damps only it.
// probeMarker: debug view; x = dist to nearest probe, y = relocation offset (both in cell
// fractions), z = validity. confidence: 0..1 cascade trust, always 1 for the dense grid.
float3 PROBE_GI_FN(float3 worldPos, float3 N, out float skyVisibility, out float sunFraction,
                   out float3 probeMarker, out float confidence)
{
    skyVisibility = 1.0;
    sunFraction = 0.0;
    probeMarker = float3(1e6, 0.0, 0.0);
    confidence = 0.0;

    float3 counts3 = PROBE_GI_COUNTS.xyz;

    // Self-shadow bias (Majercik 2021 4.1), scale baked into PROBE_GI_CELL.w. The biased point
    // feeds ONLY the visibility test; trilinear weights use the true worldPos, else hard edges
    // show triangle facets. PROBE_GI_COUNTS.w blends the bias direction normal->camera.
    float3 toCamera = normalize(viewData.CameraWorldPos - worldPos);
    float3 biasDir = lerp(N, toCamera, PROBE_GI_COUNTS.w);
    float biasLen = length(biasDir);
    float3 samplePos = worldPos + (biasLen > 1e-4 ? biasDir / biasLen : N) * PROBE_GI_CELL.w;
    float3 f = (worldPos - PROBE_GI_ORIGIN.xyz) / PROBE_GI_CELL.xyz;

    // Outside the volume: fall through to the next cascade; clamping would stretch
    // this cascade's border cells over the whole scene.
    if (any(f < 0.0) || any(f > counts3 - 1.0))
    {
        return float3(-1.0, -1.0, -1.0);
    }

    confidence = 1.0;

    int3 counts = (int3)counts3;
    int3 scroll = (int3)PROBE_GI_SCROLL.xyz;
    int3 localCell = clamp((int3)floor(f), 0, counts - 2);
    float3 t = saturate(f - (float3)localCell);
    float3 probeStep = PROBE_GI_CELL.xyz;

    float4 sum0 = 0.0;
    float3 sumX = 0.0, sumY = 0.0, sumZ = 0.0;
    float sunFracSum = 0.0;
    float weightSum = 0.0;

    // [loop] for COMPILE time: unrolling 8 heavy corners across 3 cascades dominated FXC time.
    [loop]
    for (int corner = 0; corner < 8; corner++)
    {
        int3 offset = int3(corner & 1, (corner >> 1) & 1, corner >> 2);

        // Node -> texel: toroidal scroll wrap, planes stacked in Y; mirrors
        // ProbeGiBaker.Wrap/ProbeTexel. "+ counts" is required: HLSL % of a
        // negative dividend is negative.
        int3 lp = localCell + offset;
        int3 sp = ((lp + scroll) % counts + counts) % counts;
        int3 texel = int3(sp.x, sp.z * counts.y + sp.y, 0);

        // Validity (Sh1 alpha) read first so an invalid corner skips the other Loads.
        float4 sh1 = PROBE_GI_SH1.Load(texel);
        float trilinear = (offset.x ? t.x : 1.0 - t.x)
                        * (offset.y ? t.y : 1.0 - t.y)
                        * (offset.z ? t.z : 1.0 - t.z);
        float w = sh1.a;

        // Soft backface weight (DDGI wrap shading): probes behind the surface must not
        // leak through thin geometry; squared half-cosine keeps side probes partial weight.
        float3 probeOffsetWorld = PROBE_GI_OFFSET.Load(texel).rgb;
        float3 probeWorld = PROBE_GI_ORIGIN.xyz + (float3)lp * probeStep + probeOffsetWorld;
        float3 toProbe = probeWorld - samplePos;
        float toProbeLen = length(toProbe);

        // Wrap weight uses the UNBIASED direction (RTXGI convention): the bias is
        // view-dependent here, and feeding it into wrap makes weights swim with the camera.
        float3 toProbeTrue = probeWorld - worldPos;

        // Nearest-probe marker before any weight rejection: the debug view exists
        // precisely to show invalid probes.
        float cellSize = min(probeStep.x, min(probeStep.y, probeStep.z));
        if (toProbeLen < probeMarker.x * cellSize)
        {
            probeMarker = float3(toProbeLen / max(cellSize, 1e-4),
                                 length(probeOffsetWorld) / max(cellSize, 1e-4),
                                 sh1.a);
        }
        float wrap = (dot(toProbeTrue / max(length(toProbeTrue), 1e-4), N) + 1.0) * 0.5;
        // Floor 0.2 per the reference; lower floors make flat walls band per cell.
        w *= wrap * wrap + 0.2;

        // Chebyshev visibility test against the probe's octahedral depth map (DDGI).
        float2 oct = OctEncode(-toProbe / max(toProbeLen, 1e-4));

        // Manual bilinear filter of the vis map with octahedral wrap (OctWrapTexel):
        // point Loads make depth stepped per texel and leak light through thin geometry.
        int visRes = ProbeVisRes();
        float2 visUv = oct * (float)visRes - 0.5;
        int2 visBase = (int2)floor(visUv);
        float2 visFrac = visUv - (float2)visBase;

        int2 tileOrigin = texel.xy * visRes;
        float2 v00 = PROBE_GI_VIS.Load(int3(tileOrigin + OctWrapTexel(visBase + int2(0, 0), visRes), 0)).rg;
        float2 v10 = PROBE_GI_VIS.Load(int3(tileOrigin + OctWrapTexel(visBase + int2(1, 0), visRes), 0)).rg;
        float2 v01 = PROBE_GI_VIS.Load(int3(tileOrigin + OctWrapTexel(visBase + int2(0, 1), visRes), 0)).rg;
        float2 v11 = PROBE_GI_VIS.Load(int3(tileOrigin + OctWrapTexel(visBase + int2(1, 1), visRes), 0)).rg;

        float2 visDepth = lerp(lerp(v00, v10, visFrac.x), lerp(v01, v11, visFrac.x), visFrac.y);
        if (toProbeLen > visDepth.x)
        {
            float variance = abs(visDepth.y - visDepth.x * visDepth.x) + 1e-4;
            float diff = toProbeLen - visDepth.x;
            float cheb = variance / (variance + diff * diff);
            // Floor 0.05 (reference): zeroing occluded probes collapses the weight sum in corners.
            w *= max(cheb * cheb * cheb, 0.05);
        }

        // Perceptual crush of small weights (Majercik 2019 §5.2).
        const float crushThreshold = 0.2;
        if (w < crushThreshold)
        {
            w *= (w * w) / (crushThreshold * crushThreshold);
        }

        // Trilinear AFTER crush (reference order); floor 0.001 keeps the weight
        // sum continuous at cell faces.
        w *= max(trilinear, 0.001);

        if (w <= 1e-5)
        {
            continue;
        }

        float4 sh0 = PROBE_GI_SH0.Load(texel);
        float4 sh2 = PROBE_GI_SH2.Load(texel);
        sum0 += sh0 * w;
        sumX += sh1.rgb * w;
        sumY += sh2.rgb * w;
        sumZ += PROBE_GI_SH3.Load(texel).rgb * w;
        sunFracSum += sh2.a * w;
        weightSum += w;
    }

    if (weightSum < 1e-3)
    {
        return float3(-1.0, -1.0, -1.0);
    }

    float inv = 1.0 / weightSum;
    skyVisibility = saturate(sum0.a * inv);
    sunFraction = saturate(sunFracSum * inv);

    // Normalized convention (R0 = sphere mean): irradiance convolution + Lambert /pi
    // give R0 = L0 * 0.282095, R1 = L1 * 0.1628675.
    float3 R0 = sum0.rgb * (inv * 0.2820948);
    float3 R1x = sumX * (inv * 0.1628675);
    float3 R1y = sumY * (inv * 0.1628675);
    float3 R1z = sumZ * (inv * 0.1628675);

    // Per channel: red/blue have different directionality (blue sky up, warm bounce
    // down); a shared L1 direction would shift color.
    float3 irradiance = float3(
        NonLinearIrradianceL1(R0.r, float3(R1x.r, R1y.r, R1z.r), N),
        NonLinearIrradianceL1(R0.g, float3(R1x.g, R1y.g, R1z.g), N),
        NonLinearIrradianceL1(R0.b, float3(R1x.b, R1y.b, R1z.b), N));

    return max(irradiance, 0.0);
}
