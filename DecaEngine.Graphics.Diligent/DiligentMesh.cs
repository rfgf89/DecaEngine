using System.Numerics;
using Diligent;
using UnsafeCollections.Collections.Unsafe;

namespace DecaEngine.Graphics.Diligent;

public class DiligentMesh : IMeshObject
{
	private readonly IRenderDevice _device;

	public string Name { get; }

	public bool IsU32 { get; private set; }

	public int VertexCount { get; private set; }
	public int IndexCount { get; private set; }

	/// <summary>Vertex size in bytes; BLAS builds read positions straight from the vertex buffer.</summary>
	public int VertexStride { get; private set; }

	public Vector3 Center { get; private set; }
	public float Radius { get; private set; }

	public IBuffer? VertexBuffer { get; private set; }
	public IBuffer? IndexBuffer { get; private set; }

	public unsafe UnsafeArray* VertexData { get; private set; }
	public unsafe UnsafeArray* IndexData { get; private set; }

	private unsafe UnsafeArray* _lodLevels;

	public DiligentMesh(string name, IRenderDevice device)
	{
		Name = name;
		_device = device;
	}

	// Only valid when the device enabled ray tracing; otherwise buffer creation fails.
	private BindFlags RayTracingBindFlag() =>
		_device.GetDeviceInfo().Features.RayTracing == DeviceFeatureState.Enabled
			? BindFlags.RayTracing
			: BindFlags.None;

	public unsafe void SetVertices<T>(T[] vertices) where T : unmanaged
	{
		VertexBuffer?.Dispose();
		VertexCount = vertices.Length;

		if (vertices.Length == 0)
		{
			return;
		}

		if (VertexData != null) UnsafeArray.Free(VertexData);
		VertexData = UnsafeArray.Allocate<T>(vertices.Length);
		fixed (T* ptr = vertices)
		{
			VertexData->CopyFrom<T>(ptr, 0, vertices.Length);
		}

		UpdateVertexBuffer<T>();
	}

	public unsafe void SetVertices(UnsafeArray* vertices)
	{
		VertexBuffer?.Dispose();
		VertexCount = UnsafeArray.GetLength(vertices);

		if (VertexCount == 0)
		{
			return;
		}

		if (VertexData != null && VertexData != vertices) UnsafeArray.Free(VertexData);
		VertexData = vertices;
		UpdateVertexBuffer<Vertex>();
	}

	private unsafe void UpdateVertexBuffer<T>() where T : unmanaged
	{
		ulong size = (ulong)(VertexCount * sizeof(T));
		VertexStride = sizeof(T);

		var vbDesc = new BufferDesc
		{
			Name = $"{Name} Vertex Buffer",
			Usage = Usage.Immutable,
			BindFlags = BindFlags.VertexBuffer | RayTracingBindFlag(),
			Size = size
		};

		var bufferData = new BufferData
		{
			Data = new IntPtr(UnsafeArray.GetPtr<T>(VertexData, 0)),
			DataSize = size
		};

		VertexBuffer = _device.CreateBuffer(vbDesc, bufferData);
	}

	public unsafe void SetIndices<T>(T[] indices) where T : unmanaged
	{
		IndexBuffer?.Dispose();
		IndexCount = indices.Length;

		IsU32 = sizeof(T) != sizeof(ushort);

		if (indices.Length == 0)
		{
			return;
		}

		if (IndexData != null) UnsafeArray.Free(IndexData);
		IndexData = UnsafeArray.Allocate<T>(indices.Length);
		fixed (T* ptr = indices)
		{
			IndexData->CopyFrom<T>(ptr, 0, indices.Length);
		}

		UpdateIndexBuffer<T>();
	}

	public unsafe void SetIndices(UnsafeArray* indices)
	{
		IndexBuffer?.Dispose();
		IndexCount = UnsafeArray.GetLength(indices);
		IsU32 = true; 

		if (IndexCount == 0)
		{
			return;
		}

		if (IndexData != null && IndexData != indices) UnsafeArray.Free(IndexData);
		IndexData = indices;
		UpdateIndexBuffer<uint>();
	}

	private unsafe void UpdateIndexBuffer<T>() where T : unmanaged
	{
		ulong size = (ulong)(IndexCount * sizeof(T));

		var ibDesc = new BufferDesc
		{
			Name = $"{Name} Index Buffer",
			Usage = Usage.Immutable,
			BindFlags = BindFlags.IndexBuffer | RayTracingBindFlag(),
			Size = size
		};

		var bufferData = new BufferData
		{
			Data = new IntPtr(UnsafeArray.GetPtr<uint>(IndexData, 0)),
			DataSize = size
		};

		IndexBuffer = _device.CreateBuffer(ibDesc, bufferData);
	}

	public unsafe void SetLodGroup(UnsafeArray* lodLevels)
	{
		if (_lodLevels != null && _lodLevels != lodLevels) UnsafeArray.Free(_lodLevels);
		_lodLevels = lodLevels;
	}

	public unsafe UnsafeArray* GetLodLevels() => _lodLevels;

	public void SetBounds(Vector3 center, float radius)
	{
		Center = center;
		Radius = radius;
	}

	public unsafe void Release()
	{
		VertexBuffer?.Dispose();
		IndexBuffer?.Dispose();

		VertexBuffer = null;
		IndexBuffer = null;
		VertexCount = 0;
		IndexCount = 0;

		// Must null out: callers null-check these pointers to detect a CPU copy of the geometry.
		if (VertexData != null)
		{
			UnsafeArray.Free(VertexData);
			VertexData = null;
		}

		if (IndexData != null)
		{
			UnsafeArray.Free(IndexData);
			IndexData = null;
		}

		if (_lodLevels != null)
		{
			UnsafeArray.Free(_lodLevels);
			_lodLevels = null;
		}
	}
}