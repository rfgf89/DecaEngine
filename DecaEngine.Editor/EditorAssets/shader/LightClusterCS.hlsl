// Punctual light clustering: one thread per froxel cluster, CPU half lives in LightCulling.cs.
// Lights are transformed to view space once per group through groupshared memory, so all loops
// must stay uniform across the group - no early exits between the barriers.

#include "Instancing.hlsl"

cbuffer Constants
{
    CullData cullData;
}

cbuffer Light
{
    LightData lightData;
}

// RW rather than SRV: DiligentComputeMaterial always binds compute buffers as UAVs.
RWStructuredBuffer<PunctualLight> PunctualLights;
RWStructuredBuffer<uint> ClusterCounts;
RWStructuredBuffer<uint> ClusterIndices;

groupshared float4 gsPosRange[CLUSTER_CULL_GROUP];   // xyz view position, w range
groupshared float4 gsDirType[CLUSTER_CULL_GROUP];    // xyz view cone axis, w type (0/1)
groupshared float4 gsBoundSphere[CLUSTER_CULL_GROUP]; // xyz center, w radius
groupshared float2 gsSpotAngles[CLUSTER_CULL_GROUP]; // cos/sin of the outer half angle

// Ericson sphere-AABB test; exact for point lights, conservative for a spot's bounding sphere.
bool SphereIntersectsAabb(float3 center, float radius, float3 aabbMin, float3 aabbMax)
{
    float3 closest = clamp(center, aabbMin, aabbMax);
    float3 d = center - closest;
    return dot(d, d) <= radius * radius;
}

// Wronski cone-sphere test. Too permissive alone (the cluster sphere is much larger than the
// froxel), so it is always paired with SphereIntersectsAabb.
bool ConeIntersectsSphere(float3 apexView, float3 dirView, float range, float cosOuter,
    float sinOuter, float3 sphereCenter, float sphereRadius)
{
    float3 v = sphereCenter - apexView;
    float vLenSq = dot(v, v);
    float v1Len = dot(v, dirView);
    float distClosest = cosOuter * sqrt(max(vLenSq - v1Len * v1Len, 0.0)) - v1Len * sinOuter;

    bool angleCull = distClosest > sphereRadius;
    bool frontCull = v1Len > sphereRadius + range;
    bool backCull = v1Len < -sphereRadius;
    return !(angleCull || frontCull || backCull);
}

// Minimal cone bounding sphere; must mirror LightCulling.IsSpotLightVisible on the CPU.
float4 ConeBoundingSphere(float3 apex, float3 dir, float range, float cosOuter, float sinOuter)
{
    if (sinOuter <= cosOuter)
    {
        float t = range / max(2.0 * cosOuter * cosOuter, 1e-4);
        return float4(apex + dir * t, t);
    }

    return float4(apex + dir * range, range * sinOuter / max(cosOuter, 1e-4));
}

[numthreads(CLUSTER_CULL_GROUP, 1, 1)]
void CSMain(uint3 DTid : SV_DispatchThreadID, uint3 GTid : SV_GroupThreadID)
{
    uint clusterIdx = DTid.x;
    // Tail-group threads must reach the shared barriers, so they mask writes instead of returning.
    bool validCluster = clusterIdx < CLUSTER_COUNT;

    uint lightCount = (uint)lightData.ClusterParams.y;
    // Exit before slice math: an empty segment leaves ClusterParams.zw zero and would yield NaN.
    // Uniform across the dispatch, so the barriers below stay in sync. Counts must still be
    // cleared or shading reads clusters left from the previous camera.
    if (lightCount == 0)
    {
        if (validCluster)
            ClusterCounts[clusterIdx] = 0;
        return;
    }

    uint cx = clusterIdx % CLUSTER_GRID_X;
    uint cy = (clusterIdx / CLUSTER_GRID_X) % CLUSTER_GRID_Y;
    uint cz = (clusterIdx / (CLUSTER_GRID_X * CLUSTER_GRID_Y)) % CLUSTER_GRID_Z;

    // Exponential depth slices in view-space z (left-handed camera looks down +Z).
    float zNear = lightData.ClusterParams.z;
    float zFar = lightData.ClusterParams.w;
    float z0 = zNear * pow(zFar / zNear, cz / (float)CLUSTER_GRID_Z);
    float z1 = zNear * pow(zFar / zNear, (cz + 1) / (float)CLUSTER_GRID_Z);

    // Tile y grows downward, NDC y upward, hence the flipped y edges. Must mirror the inverse
    // pixel-to-tile mapping in UnlitInstancedPS.
    float ndcX0 = 2.0 * cx / CLUSTER_GRID_X - 1.0;
    float ndcX1 = 2.0 * (cx + 1) / CLUSTER_GRID_X - 1.0;
    float ndcY0 = 1.0 - 2.0 * (cy + 1) / CLUSTER_GRID_Y;
    float ndcY1 = 1.0 - 2.0 * cy / CLUSTER_GRID_Y;

    // Froxel view-space AABB: clip.x = view.x * P00, w = view.z, so view.x = ndc * z / P00.
    float x00 = ndcX0 * z0 / cullData.P00, x10 = ndcX1 * z0 / cullData.P00;
    float x01 = ndcX0 * z1 / cullData.P00, x11 = ndcX1 * z1 / cullData.P00;
    float y00 = ndcY0 * z0 / cullData.P11, y10 = ndcY1 * z0 / cullData.P11;
    float y01 = ndcY0 * z1 / cullData.P11, y11 = ndcY1 * z1 / cullData.P11;

    float3 aabbMin = float3(min(min(x00, x10), min(x01, x11)), min(min(y00, y10), min(y01, y11)), z0);
    float3 aabbMax = float3(max(max(x00, x10), max(x01, x11)), max(max(y00, y10), max(y01, y11)), z1);

    float3 sphereCenter = (aabbMin + aabbMax) * 0.5;
    float sphereRadius = length(aabbMax - aabbMin) * 0.5;

    uint offset = (uint)lightData.ClusterParams.x;
    uint written = 0; // lights that fit in the cluster slot
    uint hits = 0;    // all intersecting lights, including dropped ones; this goes to ClusterCounts

    for (uint base = 0; base < lightCount; base += CLUSTER_CULL_GROUP)
    {
        // Barrier before the batch write too: a fast thread would otherwise overwrite a light
        // that lagging threads still read from the previous iteration.
        GroupMemoryBarrierWithGroupSync();

        uint loadIdx = base + GTid.x;
        if (loadIdx < lightCount)
        {
            PunctualLight light = PunctualLights[offset + loadIdx];
            float range = light.PositionRange.w;
            float3 posView = mul(float4(light.PositionRange.xyz, 1.0), cullData.view).xyz;
            float type = light.DirectionType.w;

            float3 dirView = float3(0.0, 0.0, 1.0);
            float4 bound = float4(posView, range);
            if (type > 0.5)
            {
                dirView = normalize(mul(float4(light.DirectionType.xyz, 0.0), cullData.view).xyz);
                bound = ConeBoundingSphere(posView, dirView, range, light.SpotAngles.x, light.SpotAngles.z);
            }

            gsPosRange[GTid.x] = float4(posView, range);
            gsDirType[GTid.x] = float4(dirView, type);
            gsBoundSphere[GTid.x] = bound;
            gsSpotAngles[GTid.x] = float2(light.SpotAngles.x, light.SpotAngles.z);
        }

        GroupMemoryBarrierWithGroupSync();

        uint batch = min((uint)CLUSTER_CULL_GROUP, lightCount - base);
        for (uint j = 0; j < batch; j++)
        {
            float4 bound = gsBoundSphere[j];
            if (!SphereIntersectsAabb(bound.xyz, bound.w, aabbMin, aabbMax))
                continue;

            float4 posRange = gsPosRange[j];
            float4 dirType = gsDirType[j];
            if (dirType.w > 0.5 && !ConeIntersectsSphere(posRange.xyz, dirType.xyz, posRange.w,
                    gsSpotAngles[j].x, gsSpotAngles[j].y, sphereCenter, sphereRadius))
                continue;

            hits++;
            // Overflow does not stop the scan: ClusterCounts must stay honest for the overflow
            // diagnostic. Shading clamps the count on read.
            if (written < CLUSTER_MAX_LIGHTS)
            {
                if (validCluster)
                    ClusterIndices[clusterIdx * CLUSTER_MAX_LIGHTS + written] = offset + base + j;
                written++;
            }
        }
    }

    if (validCluster)
        ClusterCounts[clusterIdx] = hits;
}
