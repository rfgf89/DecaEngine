// Probe Variability reduction (RTXGI-DDGI ReductionCS): tells whether a volume converged.
// Separate from the probe round, which is split across frames and compiled for ray tracing;
// this reduction must see all probes at once and needs no acceleration structures.
// Single pass: reduces to PROBE_VARIABILITY_GROUPS partial sums, the CPU adds the rest.

// x = sum of variation coefficients, y = sum of weights (see _ProbeVariability in ProbeRoundCS).
StructuredBuffer<float2>   _ProbeVariability;
RWStructuredBuffer<float2> _ProbeVariabilitySum;

cbuffer ProbeVariabilityParams
{
    // x = probe count, yzw reserved.
    float4 VariabilityParams;
};

#define PROBE_VARIABILITY_THREADS 64
#define PROBE_VARIABILITY_GROUPS  64

groupshared float2 SharedSum[PROBE_VARIABILITY_THREADS];

[numthreads(PROBE_VARIABILITY_THREADS, 1, 1)]
void mainVariability(uint3 groupId : SV_GroupID, uint threadId : SV_GroupIndex)
{
    uint probeCount = (uint)VariabilityParams.x;
    uint stride = PROBE_VARIABILITY_THREADS * PROBE_VARIABILITY_GROUPS;

    // Grid-stride, not a contiguous block per group: keeps reads coalesced and the load even
    // regardless of how variability is distributed across the volume.
    float2 sum = float2(0.0, 0.0);
    for (uint i = groupId.x * PROBE_VARIABILITY_THREADS + threadId; i < probeCount; i += stride)
    {
        sum += _ProbeVariability[i];
    }

    SharedSum[threadId] = sum;
    GroupMemoryBarrierWithGroupSync();

    [unroll]
    for (uint s = PROBE_VARIABILITY_THREADS / 2; s > 0; s >>= 1)
    {
        if (threadId < s)
        {
            SharedSum[threadId] += SharedSum[threadId + s];
        }

        GroupMemoryBarrierWithGroupSync();
    }

    if (threadId == 0)
    {
        _ProbeVariabilitySum[groupId.x] = SharedSum[0];
    }
}
