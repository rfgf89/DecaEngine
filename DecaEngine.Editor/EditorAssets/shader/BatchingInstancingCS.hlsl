// #pragma hlsl profile ps_6_6
// #pragma hlsl entry main

#include "Instancing.hlsl"

cbuffer Constants
{
	CullData cullData;
}

RWStructuredBuffer<IndirectInstance> Instances;
RWStructuredBuffer<DrawData> InstancesDrawData;

RWStructuredBuffer<PerMeshData> MeshBatchData;
RWStructuredBuffer<DrawIndexedIndirectCommand> IndirectCommands;
RWStructuredBuffer<int> OutputInstanceData;

bool projectSphere(float3 c, float r, float znear, float P00, float P11, out float4 aabb)
{
	if (c.z < r + znear)
		return false;

	float3 cr = c * r;
	float czr2 = c.z * c.z - r * r;

	float vx = sqrt(c.x * c.x + czr2);
	float minx = (vx * c.x - cr.z) / (vx * c.z + cr.x);
	float maxx = (vx * c.x + cr.z) / (vx * c.z - cr.x);

	float vy = sqrt(c.y * c.y + czr2);
	float miny = (vy * c.y - cr.z) / (vy * c.z + cr.y);
	float maxy = (vy * c.y + cr.z) / (vy * c.z - cr.y);

	aabb = float4(minx * P00, miny * P11, maxx * P00, maxy * P11);
	aabb = aabb.xwzy * float4(0.5f, -0.5f, 0.5f, -0.5f) + float4(0.5f, 0.5f, 0.5f, 0.5f); // clip space -> uv space

	return true;
}

float getOcclusionMip(float4 aabb, float pyramidWidth, float pyramidHeight)
{
	float2 size = aabb.zw - aabb.xy;

	// Because we only consider 2x2 pixels, we need to make sure we are sampling from a mip that reduces the rectangle to 1x1 texel or smaller.
	float level = ceil(log2(max(size.x * pyramidWidth, size.y * pyramidHeight)));

	// ... however, if the result still covers at most 2x2 texels at the finer mip, we can sample from it for free.
	float2 fmipSize = float2(pyramidWidth, pyramidHeight) * exp2(1 - level);

	float2 lev = frac(aabb.xy * fmipSize) + size * fmipSize;

	if (lev.x <= 2.0 && lev.y <= 2.0)
	{
		level -= 1.0;
	}

	return max(level, 0);
}

float3 rotateQuat(float3 v, float4 q)
{
	return v + 2.0 * cross(q.xyz, cross(q.xyz, v) + q.w * v);
}

bool isVisible(float3 center, float radius)
{
	bool visible = true;
	visible = visible && center.z * cullData.frustum[1] - abs(center.x) * cullData.frustum[0] > -radius;
	visible = visible && center.z * cullData.frustum[3] - abs(center.y) * cullData.frustum[2] > -radius;
	visible = visible && center.z + radius > cullData.znear && center.z - radius < cullData.zfar;
	return visible;
}

[numthreads(64, 1, 1)]
void CSMain(uint3 DTid : SV_DispatchThreadID)
{
	uint instanceIdx = DTid.x;

	if (instanceIdx >= cullData.drawCount)
	{
		return;
	}

	int objectID = Instances[instanceIdx].objectId;
	if (objectID == 0)
	{
		return;
	}

	int batchId = Instances[instanceIdx].batchId;

	DrawData drawData = InstancesDrawData[objectID];
	PerMeshData meshData = MeshBatchData[batchId];

	float3 centerWorld = rotateQuat(meshData.bounds.xyz * drawData.positionScale.w, drawData.orientation) + drawData.positionScale.xyz;
	float3 center = mul(float4(centerWorld, 1), cullData.view).xyz;
	float radius = meshData.bounds.w * drawData.positionScale.w;

	bool visible = (cullData.cullFrustum & 1) == 0 || isVisible(center, radius);

	if (visible)
	{
		uint lodIndex = 0;
		if ((cullData.cullFrustum & 2) != 0 && meshData.lodCount > 1)
		{
			float dist = max(length(center) - radius, 0);
			float threshold = dist * cullData.lodTarget / drawData.positionScale.w;

			for (uint i = 1; i < meshData.lodCount; ++i)
			{
				if (meshData.lods[i].error < threshold)
				{
					lodIndex = i;
				}
			}
		}

		uint commandIndex = meshData.physicalCommandOffset + lodIndex;
		InterlockedAdd(IndirectCommands[commandIndex].numInstances, 1);

		uint instanceSlot;
		InterlockedAdd(BatchCounters[batchId], 1, instanceSlot);

		uint instanceIndex = IndirectCommands[commandIndex].firstInstanceLocation + instanceSlot;
		OutputInstanceData[instanceIndex] = objectID;
	}
}