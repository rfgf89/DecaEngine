// Compute skinning: bind pose + matrix palette -> deformed vertices in another region of the same
// mega-buffer. Done in compute (not the VS) so RT shadows/SSR/probe GI trace deformed geometry, and
// the VS/shadow paths see a skinned instance as an ordinary mesh with its own baseVertex.
// Source and destination regions never overlap, so no cross-thread synchronization is needed.

#include "Instancing.hlsl"

// Mirrors ModelLoader.Vertex (72 bytes). Scalar fields on purpose: HLSL won't let a vector cross a
// 16-byte boundary in structured buffers and would pad, breaking the tightly packed C# layout.
struct MegaVertex
{
	float px, py, pz;
	float u, v;
	float nx, ny, nz;
	float tx, ty, tz, tw;
	float cr, cg, cb, ca;
	float u1, v1;
};

// Mirrors SkinVertex (16 bytes): two ushorts per uint, little-endian (see SkinningData.cs).
struct SkinInfluence
{
	uint joints01;
	uint joints23;
	uint weights01;
	uint weights23;
};

// Mirrors SkinRegion (SkinningPass.cs). One region = one skinned instance.
struct SkinRegion
{
	uint sourceBaseVertex; // mesh bind-pose start in the mega-buffer
	uint destBaseVertex;   // destination start for this instance
	uint vertexCount;
	uint skinBase;         // mesh skin-stream start in SkinStream

	uint paletteOffset;    // instance palette start, in MATRICES
	uint firstThread;      // global index of the region's first thread (prefix sum of vertexCount)
	uint pad0, pad1;
};

// ALL buffers are RW, even read-only ones: DiligentComputeMaterial binds compute vars as UAV views
// only, and on D3D12 a UAV in an SRV slot reads garbage and faults the device (Vulkan maps both
// to the same STORAGE_BUFFER, hiding the mismatch).
RWStructuredBuffer<MegaVertex> MegaVertices;
RWStructuredBuffer<SkinInfluence> SkinStream;

// Palette stored as float4 rows, not matrices: matrix majorness inside structured-buffer elements
// ignores PackMatrixRowMajor and differs between D3D12 and Vulkan.
RWStructuredBuffer<float4> SkinPalette;
RWStructuredBuffer<SkinRegion> SkinRegions;

cbuffer SkinConstants
{
	// x = region count, y = total skinned vertex count (= useful thread count).
	uint4 skinParams;
}

float4x4 LoadSkinMatrix(uint matrixIndex)
{
	uint base = matrixIndex * 4;
	return float4x4(SkinPalette[base + 0], SkinPalette[base + 1], SkinPalette[base + 2], SkinPalette[base + 3]);
}

// Binary search over prefix sums: one dispatch for all regions keeps the frame's skinning inside
// the frozen command buffer (see DiligentBatchRenderer).
uint FindRegion(uint thread)
{
	uint low = 0;
	uint high = skinParams.x - 1;

	while (low < high)
	{
		uint mid = (low + high + 1) / 2;
		if (SkinRegions[mid].firstThread <= thread)
		{
			low = mid;
		}
		else
		{
			high = mid - 1;
		}
	}

	return low;
}

[numthreads(64, 1, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
	uint thread = dispatchThreadId.x;
	if (skinParams.x == 0 || thread >= skinParams.y)
	{
		return;
	}

	SkinRegion region = SkinRegions[FindRegion(thread)];
	uint local = thread - region.firstThread;
	if (local >= region.vertexCount)
	{
		return;
	}

	MegaVertex source = MegaVertices[region.sourceBaseVertex + local];
	SkinInfluence influence = SkinStream[region.skinBase + local];

	uint4 joints = uint4(
		influence.joints01 & 0xFFFF, influence.joints01 >> 16,
		influence.joints23 & 0xFFFF, influence.joints23 >> 16);

	// 1/65535, not 1/65536: the importer normalizes weight sums to exactly 65535
	// (see SkinningData.SkinVertex.WeightScale), so 65535 must map to exactly 1.
	float4 weights = float4(
		influence.weights01 & 0xFFFF, influence.weights01 >> 16,
		influence.weights23 & 0xFFFF, influence.weights23 >> 16) / 65535.0;

	float4x4 skin = LoadSkinMatrix(region.paletteOffset + joints.x) * weights.x;
	skin += LoadSkinMatrix(region.paletteOffset + joints.y) * weights.y;
	skin += LoadSkinMatrix(region.paletteOffset + joints.z) * weights.z;
	skin += LoadSkinMatrix(region.paletteOffset + joints.w) * weights.w;

	float3 position = mul(float4(source.px, source.py, source.pz, 1.0), skin).xyz;

	// Normal/tangent use the 3x3 part; inverse-transpose is skipped on purpose - it only matters
	// for non-uniform bone scale, which is rare, and would cost per-vertex.
	float3x3 skin3 = (float3x3)skin;
	float3 normal = normalize(mul(float3(source.nx, source.ny, source.nz), skin3));
	float3 tangent = mul(float3(source.tx, source.ty, source.tz), skin3);

	MegaVertex result = source;
	result.px = position.x;
	result.py = position.y;
	result.pz = position.z;
	result.nx = normal.x;
	result.ny = normal.y;
	result.nz = normal.z;
	result.tx = tangent.x;
	result.ty = tangent.y;
	result.tz = tangent.z;
	// tw (bitangent sign) stays untouched: it's a basis orientation, not a direction.

	MegaVertices[region.destBaseVertex + local] = result;
}
