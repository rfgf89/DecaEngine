using System.Numerics;
using DecaEngine.Core;
using Diligent;

namespace DecaEngine.Graphics.Diligent;

/// <summary>
/// Аппаратные структуры ускорения сцены (BLAS/TLAS) под динамический GI. Двухуровневая схема - это
/// и есть ответ на «движущийся мир»: BLAS строится на МЕШ один раз и переживает любые перемещения,
/// а TLAS пересобирается из матриц инстансов - операция на порядки дешевле, её не жалко делать хоть
/// каждый кадр. Ровно поэтому статичный CPU-BVH из ProbeGiBaker для динамики не годился: он знает
/// только мировые треугольники и при любом движении строится заново целиком.
///
/// Владеет BLAS-ами, TLAS-ом и скретч-буферами; создавать только когда
/// <see cref="IGraphicsApi.RayTracing"/> не <see cref="RayTracingSupport.None"/>.
/// </summary>
public sealed class DiligentRayTracingScene : IReleaseObject
{
	/// <summary>Один инстанс для TLAS: какой меш и куда поставлен. CustomId уезжает в шейдер как
	/// InstanceID() - по нему трассировка узнаёт, во что попала.</summary>
	public readonly record struct Instance(DiligentMesh Mesh, Matrix4x4 Transform, uint CustomId);

	private readonly IRenderDevice _device;
	private readonly IDeviceContext _context;
	private readonly Dictionary<DiligentMesh, IBottomLevelAS> _blas = new();

	private ITopLevelAS? _tlas;
	private IBuffer? _scratch;
	private IBuffer? _instanceBuffer;
	private ulong _scratchSize;
	private uint _tlasCapacity;

	/// <summary>Готовый TLAS для привязки в шейдер (RaytracingAccelerationStructure). null, пока
	/// <see cref="Rebuild"/> не позвали ни разу.</summary>
	public ITopLevelAS? Tlas => _tlas;

	/// <summary>Сколько инстансов в последней сборке - диагностика.</summary>
	public int InstanceCount { get; private set; }

	public DiligentRayTracingScene(DiligentGraphicsApi api)
	{
		_device = api.Device;
		_context = api.ImmediateContext;
	}

	/// <summary>BLAS для меша, с кешированием: геометрия в объектном пространстве, от положения
	/// инстанса не зависит, поэтому строится один раз на меш и переиспользуется всеми его
	/// инстансами.</summary>
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
			// Трассировка тут - основная нагрузка кадра, а строим мы BLAS один раз, поэтому берём
			// самый быстрый обход ценой более долгой сборки.
			Flags = RaytracingBuildAsFlags.PreferFastTrace,
		});

		EnsureScratch(blas.GetScratchBufferSizes().Build);

		// Позиция лежит первым полем вершины (см. ModelLoader.Vertex), поэтому смещение нулевое, а
		// шаг - полный размер вершины: трассировка сама выберет из неё только xyz.
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

	/// <summary>Пересобирает TLAS под текущий набор инстансов. Это и есть покадровая операция
	/// динамического мира: BLAS-ы не трогаются, платим только за верхний уровень.</summary>
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
			// Таблиц привязки шейдеров нет: трассировка планируется inline (RayQuery), а там hit-группы
			// не участвуют - луч возвращает попадание прямо в вызывающий шейдер.
			HitGroupStride = 0,
			BindingMode = HitGroupBindingMode.PerInstance,
			TLASTransitionMode = ResourceStateTransitionMode.Transition,
			BLASTransitionMode = ResourceStateTransitionMode.Transition,
			InstanceBufferTransitionMode = ResourceStateTransitionMode.Transition,
			ScratchBufferTransitionMode = ResourceStateTransitionMode.Transition,
		});
	}

	/// <summary>TLAS пересоздаётся только когда инстансов стало БОЛЬШЕ, чем влезает: ёмкость растёт
	/// с запасом, иначе добавление одного пропа пересоздавало бы структуру каждый кадр.</summary>
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
			// AllowUpdate - чтобы позже можно было делать дешёвый refit вместо полной пересборки,
			// когда инстансы только сдвинулись, а их состав не поменялся.
			Flags = RaytracingBuildAsFlags.AllowUpdate | RaytracingBuildAsFlags.PreferFastTrace,
		});
	}

	private void EnsureInstanceBuffer(uint instanceCount)
	{
		// Размер записи инстанса задаёт рантайм (TLAS_INSTANCE_DATA_SIZE = 64 байта в DXR и Vulkan).
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

	/// <summary>Один скретч-буфер на все сборки: он нужен только ВНУТРИ построения и переиспользуется,
	/// растёт до максимума запрошенного.</summary>
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

	/// <summary>Матрица движка (строковая, перенос в последней СТРОКЕ) в матрицу инстанса
	/// трассировки (3x4, перенос в последнем СТОЛБЦЕ) - то же преобразование, что VS делает через
	/// mul(pos, modelMatrix).</summary>
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
