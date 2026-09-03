using System.Numerics;
using DecaEngine.Core;
using Diligent;

namespace DecaEngine.Graphics.Diligent;

/// <summary>Hardware acceleration structures (BLAS/TLAS) for dynamic GI. Only create this when
/// <see cref="IGraphicsApi.RayTracing"/> is not <see cref="RayTracingSupport.None"/>.</summary>
public sealed class DiligentRayTracingScene : IReleaseObject
{
	/// <summary>One TLAS instance; CustomId reaches the shader as InstanceID().</summary>
	public readonly record struct Instance(DiligentMesh Mesh, Matrix4x4 Transform, uint CustomId);

	private readonly IRenderDevice _device;
	private readonly IDeviceContext _context;
	private readonly Dictionary<DiligentMesh, IBottomLevelAS> _blas = new();

	private ITopLevelAS? _tlas;
	private IBuffer? _scratch;
	private IBuffer? _instanceBuffer;
	private ulong _scratchSize;
	private uint _tlasCapacity;

	/// <summary>TLAS to bind as RaytracingAccelerationStructure; null until <see cref="Rebuild"/>.</summary>
	public ITopLevelAS? Tlas => _tlas;

	/// <summary>Instance count of the last build.</summary>
	public int InstanceCount { get; private set; }

	public DiligentRayTracingScene(DiligentGraphicsApi api)
	{
		_device = api.Device;
		_context = api.ImmediateContext;
	}

	// BLAS geometry is in object space, so one per mesh is shared by all of its instances.
	private IBottomLevelAS GetOrBuildBlas(DiligentMesh mesh)
	{
		if (_blas.TryGetValue(mesh, out var cached))
		{
			return cached;
		}

		if (mesh.VertexBuffer == null || mesh.IndexBuffer == null || mesh.IndexCount < 3)
		{
			throw new InvalidOperationException(
				$"Mesh '{mesh.Name}' has no indexed geometry to build a BLAS from");
		}

		uint primitiveCount = (uint)(mesh.IndexCount / 3);
		var triangleDesc = new BLASTriangleDesc
		{
			GeometryName = "geometry",
			MaxVertexCount = (uint)mesh.VertexCount,
			VertexValueType = global::Diligent.ValueType.Float32,
			VertexComponentCount = 3,
			MaxPrimitiveCount = primitiveCount,
			IndexType = mesh.IsU32 ? global::Diligent.ValueType.UInt32 : global::Diligent.ValueType.UInt16,
		};

		var blas = _device.CreateBLAS(new BottomLevelASDesc
		{
			Name = $"{mesh.Name} BLAS",
			Triangles = [triangleDesc],
			// Built once, traced every frame: pay build time for the fastest traversal.
			Flags = RaytracingBuildAsFlags.PreferFastTrace,
		});

		EnsureScratch(blas.GetScratchBufferSizes().Build);

		// Position is the first vertex field, so offset 0 with the full vertex stride.
		_context.BuildBLAS(new BuildBLASAttribs
		{
			Blas = blas,
			TriangleData =
			[
				new BLASBuildTriangleData
				{
					GeometryName = "geometry",
					VertexBuffer = mesh.VertexBuffer,
					VertexOffset = 0,
					VertexStride = (uint)mesh.VertexStride,
					VertexCount = (uint)mesh.VertexCount,
					VertexValueType = global::Diligent.ValueType.Float32,
					VertexComponentCount = 3,
					IndexBuffer = mesh.IndexBuffer,
					IndexOffset = 0,
					IndexType = mesh.IsU32 ? global::Diligent.ValueType.UInt32 : global::Diligent.ValueType.UInt16,
					PrimitiveCount = primitiveCount,
					Flags = RaytracingGeometryFlags.Opaque,
				}
			],
			ScratchBuffer = _scratch,
			BLASTransitionMode = ResourceStateTransitionMode.Transition,
			GeometryTransitionMode = ResourceStateTransitionMode.Transition,
			ScratchBufferTransitionMode = ResourceStateTransitionMode.Transition,
		});

		_blas[mesh] = blas;
		return blas;
	}

	/// <summary>Rebuilds the TLAS for the current instance set; BLASes are left untouched.</summary>
	public void Rebuild(IReadOnlyList<Instance> instances)
	{
		InstanceCount = instances.Count;
		if (instances.Count == 0)
		{
			return;
		}

		var data = new TLASBuildInstanceData[instances.Count];
		for (int i = 0; i < instances.Count; i++)
		{
			var instance = instances[i];
			data[i] = new TLASBuildInstanceData
			{
				InstanceName = $"instance{i}",
				Blas = GetOrBuildBlas(instance.Mesh),
				Transform = ToMatrix3x4(instance.Transform),
				CustomId = instance.CustomId,
				Mask = 0xFF,
				Flags = RaytracingInstanceFlags.None,
			};
		}

		EnsureTlas((uint)instances.Count);
		EnsureInstanceBuffer((uint)instances.Count);
		EnsureScratch(_tlas!.GetScratchBufferSizes().Build);

		_context.BuildTLAS(new BuildTLASAttribs
		{
			Tlas = _tlas,
			Instances = data,
			InstanceBuffer = _instanceBuffer,
			ScratchBuffer = _scratch,
			// Inline tracing (RayQuery) only: no shader binding table, so no hit groups.
			HitGroupStride = 0,
			BindingMode = HitGroupBindingMode.PerInstance,
			TLASTransitionMode = ResourceStateTransitionMode.Transition,
			BLASTransitionMode = ResourceStateTransitionMode.Transition,
			InstanceBufferTransitionMode = ResourceStateTransitionMode.Transition,
			ScratchBufferTransitionMode = ResourceStateTransitionMode.Transition,
		});
	}

	// Capacity grows with slack: exact sizing would recreate the TLAS on every added prop.
	private void EnsureTlas(uint instanceCount)
	{
		if (_tlas != null && _tlasCapacity >= instanceCount)
		{
			return;
		}

		_tlas?.Dispose();
		_tlasCapacity = Math.Max(instanceCount + instanceCount / 2, 16);
		_tlas = _device.CreateTLAS(new TopLevelASDesc
		{
			Name = "Scene TLAS",
			MaxInstanceCount = _tlasCapacity,
			// AllowUpdate leaves room for a cheap refit when only instance transforms change.
			Flags = RaytracingBuildAsFlags.AllowUpdate | RaytracingBuildAsFlags.PreferFastTrace,
		});
	}

	private void EnsureInstanceBuffer(uint instanceCount)
	{
		// TLAS_INSTANCE_DATA_SIZE is 64 bytes in both DXR and Vulkan.
		ulong size = instanceCount * 64UL;
		if (_instanceBuffer != null && _instanceBuffer.GetDesc().Size >= size)
		{
			return;
		}

		_instanceBuffer?.Dispose();
		_instanceBuffer = _device.CreateBuffer(new BufferDesc
		{
			Name = "TLAS Instance Buffer",
			Usage = Usage.Default,
			BindFlags = BindFlags.RayTracing,
			Size = Math.Max(size, 64UL * 16),
		});
	}

	// One scratch buffer shared by all builds; it is only live during a build.
	private void EnsureScratch(ulong size)
	{
		if (_scratch != null && _scratchSize >= size)
		{
			return;
		}

		_scratch?.Dispose();
		_scratchSize = Math.Max(size, 1024);
		_scratch = _device.CreateBuffer(new BufferDesc
		{
			Name = "Acceleration Structure Scratch",
			Usage = Usage.Default,
			BindFlags = BindFlags.RayTracing,
			Size = _scratchSize,
		});
	}

	// Engine matrix is row-major (translation in the last row); RT instances want 3x4 column.
	private static Matrix3x4 ToMatrix3x4(Matrix4x4 m) => new(
		m.M11, m.M21, m.M31, m.M41,
		m.M12, m.M22, m.M32, m.M42,
		m.M13, m.M23, m.M33, m.M43);

	public void Release()
	{
		foreach (var blas in _blas.Values)
		{
			blas.Dispose();
		}

		_blas.Clear();
		_tlas?.Dispose();
		_tlas = null;
		_scratch?.Dispose();
		_scratch = null;
		_instanceBuffer?.Dispose();
		_instanceBuffer = null;
	}
}
