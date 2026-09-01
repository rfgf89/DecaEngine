using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using DecaEngine.Core;
using DecaEngine.Graphics;
using DecaEngine.Animation;

namespace DecaEngine.Graphics.Diligent;

/// <summary>
/// GPU-скиннинг: диспетчеризация SkinningCS.hlsl, которая переписывает bind-позу скиннед-мешей в
/// отведённые каждому инстансу участки мега-буфера вершин.
///
/// Живёт отдельным классом, а не полями <see cref="DiligentBatchRenderer"/>, по одной причине: ему
/// нужен ровно один чужой ресурс - сам мега-буфер вершин, - а всё остальное (скин-стрим, палитра,
/// список регионов) существует только ради скиннинга и в батч-рендерере было бы шумом.
///
/// ВСЯ работа кадра идёт ОДНОЙ диспетчеризацией на все скиннед-инстансы: команды рендера
/// заморожены (см. DiligentBatchRenderer), и диспетчеризация на инстанс требовала бы перезаписи
/// командного буфера при каждом появлении и исчезновении персонажа. Регион, которому принадлежит
/// поток, шейдер находит бинарным поиском по префиксным суммам.
/// </summary>
public sealed class DiligentSkinningPass
{
	/// <summary>Тредов в группе. Зеркалит numthreads в SkinningCS.hlsl - менять только парой.</summary>
	private const int GroupSize = 64;

	/// <summary>Зеркало SkinRegion в SkinningCS.hlsl. Один регион - один скиннед-инстанс.</summary>
	[StructLayout(LayoutKind.Sequential)]
	private struct SkinRegion
	{
		public uint SourceBaseVertex;
		public uint DestBaseVertex;
		public uint VertexCount;
		public uint SkinBase;

		public uint PaletteOffset;
		public uint FirstThread;
		public uint Pad0, Pad1;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct SkinConstants
	{
		public uint RegionCount;
		public uint ThreadCount;
		public uint Pad0, Pad1;
	}

	private readonly DiligentGraphicsApi _api;
	private readonly DiligentComputeMaterial _material;
	private readonly DiligentBufferHandle _constantsBuffer;

	// Скин-стримы всех зарегистрированных мешей, склеенные подряд, - тот же приём, что у мега-буфера
	// вершин: один буфер на сцену, каждый меш знает свой офсет.
	private readonly List<SkinVertex> _skinStreamCpu = new();
	private DiligentBufferHandle _skinStreamBuffer;
	private int _skinStreamCapacity;
	private int _skinStreamUploaded;

	private readonly List<SkinRegion> _regions = new();
	private DiligentBufferHandle _regionsBuffer;
	private int _regionsCapacity;
	private bool _regionsDirty = true;

	// Палитра всех инстансов подряд, по четыре Vector4 на матрицу (см. SkinPalette в шейдере о том,
	// почему не Matrix4x4).
	private readonly List<Vector4> _paletteCpu = new();
	private DiligentBufferHandle _paletteBuffer;
	private int _paletteCapacity;
	private bool _paletteDirty;

	private int _threadCount;

	public DiligentSkinningPass(DiligentGraphicsApi api)
	{
		_api = api;

		var shader = new DiligentShader(_api, "Skinning CS", "EditorAssets/shader", "SkinningCS.hlsl",
			ShaderObjectType.Compute, "CSMain");

		_material = new DiligentComputeMaterial("Skinning CS", _api);
		_material.SetShader(shader);

		_constantsBuffer = (DiligentBufferHandle)_api.CreateBuffer<SkinConstants>(new BufferInfo
		{
			name = "Skin Constants",
			type = BufferHandleType.Constant,
		});

		_material.SetBuffer("SkinConstants", _constantsBuffer);
	}

	/// <summary>Есть ли что скиннить в этом кадре. Потребитель пропускает и диспетчеризацию, и
	/// переходы состояний ресурсов - у статической сцены проход обязан стоить ровно ноль.</summary>
	public bool HasWork => _regions.Count > 0 && _threadCount > 0;

	/// <summary>Кладёт скин-стрим меша в общий буфер и возвращает его начало. Зовётся один раз на
	/// меш при регистрации модели, а не на инстанс: веса у всех инстансов одного меша общие.</summary>
	public int RegisterSkinStream(ReadOnlySpan<SkinVertex> skin)
	{
		int skinBase = _skinStreamCpu.Count;
		foreach (var vertex in skin)
		{
			_skinStreamCpu.Add(vertex);
		}

		return skinBase;
	}

	/// <summary>
	/// Заводит скиннед-инстанс: участок мега-буфера <paramref name="destBaseVertex"/> будет каждый
	/// кадр переписываться деформированной копией <paramref name="sourceBaseVertex"/>. Возвращает
	/// офсет палитры инстанса (В МАТРИЦАХ) - его же потребитель передаёт в
	/// <see cref="SetPalette"/>.
	/// </summary>
	public int AddInstance(int sourceBaseVertex, int destBaseVertex, int vertexCount, int skinBase, int jointCount)
	{
		int paletteOffset = _paletteCpu.Count / 4;

		// Палитра инстанса резервируется сразу: регионы и палитра индексируются независимо, и
		// «дырка» в палитре, оставленная под ещё не посчитанную позу, читалась бы шейдером как
		// нулевые матрицы - персонаж схлопнулся бы в точку до первого обновления позы. Единичные
		// матрицы дают bind-позу, то есть корректный кадр даже без аниматора.
		for (int i = 0; i < jointCount; i++)
		{
			AppendMatrix(Matrix4x4.Identity);
		}

		_regions.Add(new SkinRegion
		{
			SourceBaseVertex = (uint)sourceBaseVertex,
			DestBaseVertex = (uint)destBaseVertex,
			VertexCount = (uint)vertexCount,
			SkinBase = (uint)skinBase,
			PaletteOffset = (uint)paletteOffset,
			// FirstThread проставляется в RebuildThreadOffsets: он зависит от ВСЕХ регионов сразу.
		});

		_regionsDirty = true;
		_paletteDirty = true;

		// Префиксные суммы пересчитываются ПРЯМО ЗДЕСЬ, а не лениво в Execute: от них зависит
		// _threadCount, по которому потребитель решает, звать ли Execute вообще (см. HasWork), -
		// ленивый пересчёт сделал бы это условие вечно ложным.
		RebuildThreadOffsets();
		return paletteOffset;
	}

	/// <summary>Обновляет палитру одного инстанса (обычно - результат
	/// <see cref="SkeletonPose.SkinMatrices"/> текущего кадра).</summary>
	public void SetPalette(int paletteOffset, ReadOnlySpan<Matrix4x4> matrices)
	{
		int start = paletteOffset * 4;
		int needed = start + matrices.Length * 4;

		// Палитра длиннее зарезервированного - признак того, что скелет инстанса не тот, под который
		// он регистрировался. Молча обрезать нельзя: обрезанная палитра даёт не «чуть хуже», а
		// вывернутые кости, причём у СОСЕДНЕГО инстанса, чей участок затёрли бы.
		if (needed > _paletteCpu.Count)
		{
			throw new ArgumentException(
				$"Skinning palette overflow: instance at {paletteOffset} needs {matrices.Length} matrices, " +
				$"reserved up to {_paletteCpu.Count / 4 - paletteOffset}.");
		}

		for (int i = 0; i < matrices.Length; i++)
		{
			var m = matrices[i];
			int slot = start + i * 4;
			_paletteCpu[slot + 0] = new Vector4(m.M11, m.M12, m.M13, m.M14);
			_paletteCpu[slot + 1] = new Vector4(m.M21, m.M22, m.M23, m.M24);
			_paletteCpu[slot + 2] = new Vector4(m.M31, m.M32, m.M33, m.M34);
			_paletteCpu[slot + 3] = new Vector4(m.M41, m.M42, m.M43, m.M44);
		}

		_paletteDirty = true;
	}

	/// <summary>Полный сброс - под <see cref="DiligentBatchRenderer.ResetRegistrations"/>: офсеты
	/// регионов указывают в мега-буфер, который там пересоздаётся с нуля.</summary>
	public void Reset()
	{
		_skinStreamCpu.Clear();
		_regions.Clear();
		_paletteCpu.Clear();

		_skinStreamUploaded = 0;
		_regionsDirty = true;
		_paletteDirty = true;
		_threadCount = 0;
	}

	private void AppendMatrix(Matrix4x4 m)
	{
		_paletteCpu.Add(new Vector4(m.M11, m.M12, m.M13, m.M14));
		_paletteCpu.Add(new Vector4(m.M21, m.M22, m.M23, m.M24));
		_paletteCpu.Add(new Vector4(m.M31, m.M32, m.M33, m.M34));
		_paletteCpu.Add(new Vector4(m.M41, m.M42, m.M43, m.M44));
	}

	/// <summary>Префиксные суммы вершин по регионам: по ним шейдер находит свой регион (см.
	/// FindRegion). Пересчитываются только при изменении набора регионов.</summary>
	private void RebuildThreadOffsets()
	{
		uint thread = 0;
		for (int i = 0; i < _regions.Count; i++)
		{
			var region = _regions[i];
			region.FirstThread = thread;
			_regions[i] = region;
			thread += region.VertexCount;
		}

		_threadCount = (int)thread;
	}

	/// <summary>
	/// Заливает изменившиеся буферы и диспетчеризует скиннинг. Мега-буфер вершин приходит
	/// параметром: он живёт в батч-рендерере и пересоздаётся при росте, так что кешировать его
	/// здесь значило бы держать ссылку на уже отпущенный объект.
	///
	/// Диспетчеризация идёт ImmediateContext-ом, МИМО замороженного графа, - ровно как заливка
	/// самих мега-буферов (см. DiligentBatchRenderer.UpdateMegaBufferRange). Причин две. Во-первых,
	/// число групп зависит от суммарного числа скиннимых вершин, и в замороженной команде оно
	/// протухало бы при появлении персонажа в кадре. Во-вторых, скиннинг обязан отработать ДО
	/// ВСЕГО, что читает геометрию, - а её читают и каскады теней, и forward, и трассировка; здесь
	/// это выходит само собой, без вклинивания в порядок пассов графа.
	/// </summary>
	public void Execute(DiligentBufferHandle megaVertexBuffer)
	{
		if (megaVertexBuffer == null || !HasWork)
		{
			return;
		}

		UploadSkinStream();
		UploadRegions();
		UploadPalette();

		_material.SetBuffer("MegaVertices", megaVertexBuffer);

		// Константы заливаются ImmediateContext-ом, как и остальные буферы прохода: они меняются
		// только при появлении/исчезновении скиннед-инстансов, то есть заведомо не внутри реплея
		// замороженного командного буфера.
		UploadConstants(new SkinConstants
		{
			RegionCount = (uint)_regions.Count,
			ThreadCount = (uint)_threadCount,
		});

		// Все буферы прохода обязаны существовать К МОМЕНТУ диспетчеризации. Создание буфера может
		// провалиться (у Diligent это не исключение, а сообщение в лог и обёртка с пустым нативным
		// объектом), и тогда SRB остаётся с непривязанной переменной, а DispatchCompute падает по
		// доступу. Пропустить кадр честнее: персонаж останется в bind-позе, а причина будет видна
		// в логе создания буфера, а не в стеке падения, где виновника уже нет.
		if (_skinStreamBuffer?.Buffer == null || _regionsBuffer?.Buffer == null ||
			_paletteBuffer?.Buffer == null || megaVertexBuffer.Buffer == null)
		{
			Console.WriteLine("[skinning] проход пропущен: не создан один из буферов " +
				$"(stream={_skinStreamBuffer?.Buffer != null}, regions={_regionsBuffer?.Buffer != null}, " +
				$"palette={_paletteBuffer?.Buffer != null}, mega={megaVertexBuffer.Buffer != null})");
			return;
		}

		// Переходы состояний ресурсов делает сам Dispatch: CommitShaderResources идёт с
		// ResourceStateTransitionMode.Transition, то есть мега-буфер уезжает в UnorderedAccess перед
		// диспетчеризацией, а обратно в вершинный его переведёт SetVertexBuffers отрисовки - там
		// режим перехода тот же.
		_material.Dispatch((uint)((_threadCount + GroupSize - 1) / GroupSize), 1, 1);
	}

	private void UploadSkinStream()
	{
		if (_skinStreamCpu.Count == 0 || _skinStreamUploaded == _skinStreamCpu.Count)
		{
			return;
		}

		// Скин-стрим неизменен после регистрации меша, поэтому растим буфер тем же приёмом, что и
		// мега-буферы: с запасом (x2) и с доливкой только нового хвоста.
		if (_skinStreamBuffer == null || _skinStreamCpu.Count > _skinStreamCapacity)
		{
			_skinStreamCapacity = Math.Max(_skinStreamCpu.Count, Math.Max(1024, _skinStreamCapacity * 2));
			_skinStreamBuffer?.Release();
			_skinStreamBuffer = (DiligentBufferHandle)_api.CreateBuffer<SkinVertex>(_skinStreamCapacity,
				new BufferInfo
				{
					name = "Skin Stream Buffer",
					type = BufferHandleType.Structured,
					access = HandleAccess.Compute,
				});

			_skinStreamUploaded = 0;
			_material.SetBuffer("SkinStream", _skinStreamBuffer);
		}

		UploadList(_skinStreamBuffer, CollectionsMarshal.AsSpan(_skinStreamCpu), _skinStreamUploaded);
		_skinStreamUploaded = _skinStreamCpu.Count;
	}

	private void UploadRegions()
	{
		if (!_regionsDirty)
		{
			return;
		}

		if (_regionsBuffer == null || _regions.Count > _regionsCapacity)
		{
			_regionsCapacity = Math.Max(_regions.Count, Math.Max(16, _regionsCapacity * 2));
			_regionsBuffer?.Release();
			_regionsBuffer = (DiligentBufferHandle)_api.CreateBuffer<SkinRegion>(_regionsCapacity,
				new BufferInfo
				{
					name = "Skin Regions Buffer",
					type = BufferHandleType.Structured,
					access = HandleAccess.Compute,
				});

			_material.SetBuffer("SkinRegions", _regionsBuffer);
		}

		UploadList(_regionsBuffer, CollectionsMarshal.AsSpan(_regions), 0);
		_regionsDirty = false;
	}

	private void UploadPalette()
	{
		if (!_paletteDirty || _paletteCpu.Count == 0)
		{
			return;
		}

		if (_paletteBuffer == null || _paletteCpu.Count > _paletteCapacity)
		{
			_paletteCapacity = Math.Max(_paletteCpu.Count, Math.Max(1024, _paletteCapacity * 2));
			_paletteBuffer?.Release();
			_paletteBuffer = (DiligentBufferHandle)_api.CreateBuffer<Vector4>(_paletteCapacity,
				new BufferInfo
				{
					name = "Skin Palette Buffer",
					type = BufferHandleType.Structured,
					access = HandleAccess.Compute,
				});

			_material.SetBuffer("SkinPalette", _paletteBuffer);
		}

		// Палитра переписывается ЦЕЛИКОМ каждый кадр, где хоть одна поза изменилась: считать
		// диапазоны изменившихся инстансов дороже, чем залить пару десятков килобайт, а любая
		// пропущенная матрица - это кость, застрявшая в позе прошлого кадра.
		UploadList(_paletteBuffer, CollectionsMarshal.AsSpan(_paletteCpu), 0);
		_paletteDirty = false;
	}

	private unsafe void UploadConstants(SkinConstants constants)
	{
		_api.ImmediateContext.UpdateBuffer(_constantsBuffer.Buffer, 0, (uint)sizeof(SkinConstants),
			new IntPtr(&constants), global::Diligent.ResourceStateTransitionMode.Transition);
	}

	private unsafe void UploadList<T>(DiligentBufferHandle buffer, ReadOnlySpan<T> items, int from) where T : unmanaged
	{
		int count = items.Length - from;
		if (count <= 0)
		{
			return;
		}

		fixed (T* ptr = items.Slice(from))
		{
			// Заливка идёт ImmediateContext-ом, как и у мега-буферов: она обязана произойти ДО
			// реплея замороженных команд кадра, а не внутри него.
			_api.ImmediateContext.UpdateBuffer(buffer.Buffer,
				(uint)(from * sizeof(T)), (uint)(count * sizeof(T)), new IntPtr(ptr),
				global::Diligent.ResourceStateTransitionMode.Transition);
		}
	}
}
