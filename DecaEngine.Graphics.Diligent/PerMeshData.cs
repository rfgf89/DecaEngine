using System.Numerics;
using System.Runtime.InteropServices;
using DecaEngine.Graphics.Core;
using UnsafeCollections.Collections.Unsafe;

namespace DecaEngine.Graphics.Diligent;

[StructLayout(LayoutKind.Explicit, Size = 160)]
public unsafe struct PerMeshData
{
	[FieldOffset(0)]
	public Vector4 bounds; // 1 - vec3 center , 2 radius

	[FieldOffset(16)]
	public uint lodCount;
	[FieldOffset(20)]
	public uint physicalCommandOffset;

	[FieldOffset(32)]
	public fixed float lods[8 * 4]; // 8 levels * 4 floats (error, first, count, offset)

	public void SetLods(UnsafeArray* sourceLods)
	{
		if (sourceLods == null)
		{
			lodCount = 0;
			return;
		}

		lodCount = (uint)Math.Min(UnsafeArray.GetLength(sourceLods), 8);
		fixed (float* dest = lods)
		{
			for (int i = 0; i < (int)lodCount; i++)
			{
				var lod = UnsafeArray.Get<LodLevel>(sourceLods, i);
				dest[i * 4 + 0] = lod.error;
				dest[i * 4 + 1] = (float)lod.firstIndex;
				dest[i * 4 + 2] = (float)lod.indexCount;
				dest[i * 4 + 3] = (float)lod.vertexOffset;
			}
		}
	}
};