// GPU/CPU parity harness: replays the CPU tracer's rays through SceneTrace.hlsl (see PreviewProbe).

#include "SceneTrace.hlsl"

cbuffer TraceTestParams
{
    // x = ray count; threads past it must exit without writing.
    uint4 TraceTestCount;
};

// xyz = ray origin, w = max distance.
StructuredBuffer<float4> _TestRayOrigin;
// xyz = direction, normalized by the caller.
StructuredBuffer<float4> _TestRayDirection;

// x = hit distance (< 0 miss), y = 1 on backface hit, zw reserved.
RWStructuredBuffer<float4> _TestResult;

[numthreads(64, 1, 1)]
void main(uint3 threadId : SV_DispatchThreadID)
{
    uint index = threadId.x;
    if (index >= TraceTestCount.x)
    {
        return;
    }

    float4 origin = _TestRayOrigin[index];
    float3 direction = _TestRayDirection[index].xyz;

    SceneHit hit = SceneTraceClosest(origin.xyz, direction, origin.w);

    // z = shader-reached marker, w = BVH node count as seen by the shader:
    // distinguishes "dispatch/bind failed" (zeros) from "traversal ran but wrong".
    uint nodeCount, nodeStride;
    _SceneBvhNodes.GetDimensions(nodeCount, nodeStride);
    _TestResult[index] = float4(hit.hit ? hit.t : -1.0, hit.backface ? 1.0 : 0.0,
        777.0, (float)nodeCount);
}
