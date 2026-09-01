using System.Numerics;
using DecaEngine.Core;
using UnsafeCollections.Collections.Unsafe;

namespace DecaEngine.Graphics;

public struct LodLevel
{
	public float error;
	public int firstIndex;
	public int indexCount;
	public int vertexOffset;
}

public interface IMeshObject : IReleaseObject
{
	public string Name { get; }
	public bool IsU32 { get; }
	public int IndexCount { get; }
	
	public Vector3 Center { get; }
	public float Radius { get; }

	public void SetVertices<T>(T[] vertices) where T : unmanaged;
	public void SetIndices<T>(T[] indices) where T : unmanaged;
	
	public unsafe void SetVertices(UnsafeArray* vertices);
	public unsafe void SetIndices(UnsafeArray* indices);

	public unsafe UnsafeArray* VertexData { get; }
	public unsafe UnsafeArray* IndexData { get; }

	public unsafe void SetLodGroup(UnsafeArray* lodLevels);
	public unsafe UnsafeArray* GetLodLevels();

	public void SetBounds(Vector3 center, float radius);
}