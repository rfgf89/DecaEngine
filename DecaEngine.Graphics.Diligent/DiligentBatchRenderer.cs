using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DecaEngine.Core;
using DecaEngine.Graphics.Core;
using DecaEngine.Graphics.Diligent.RenderGraph;
using Diligent;
using UnsafeCollections.Collections.Native;
using UnsafeCollections.Collections.Unsafe;
using ResourceState = DecaEngine.Core.ResourceState;
using SetVertexBuffersFlags = DecaEngine.Core.SetVertexBuffersFlags;
using TextureAddressMode = Diligent.TextureAddressMode;
using ValueType = Diligent.ValueType;

namespace DecaEngine.Graphics.Diligent;

public unsafe class DiligentBatchRenderer : IReleaseObject, IBatchRenderer
{
	private readonly DiligentGraphicsApi _api;
	private readonly RenderDeviceType _deviceType;
	private readonly ShadowRenderer _shadowRenderer;

	private struct MeshInfo
	{
		public int IndexCount;
		public uint FirstIndex;
		public int BaseVertex;
		public UnsafeArray* LodLevels;

		/// <summary>Сколько вершин меша лежит в мега-буфере начиная с <see cref="BaseVertex"/>.
		/// Нужен скиннингу: он выделяет инстансу приёмник ровно такого же размера и копирует туда
		/// bind-позу (см. <see cref="RegisterSkinnedInstance"/>).</summary>
		public int VertexCount;
	}

	public readonly OrderedDictionary<int, MaterialDrawRange> _materialDrawRanges = new();
	private bool _isDrawRangesCacheDirty = true;

	// Батчи, отсортированные по materialId - раньше пересчитывались LINQ OrderBy в
	// CheckAndReallocateBuffers И ОТДЕЛЬНО в UpdateDrawRangesCache на каждый вызов, где нужен был
	// порядок (CheckAndReallocateBuffers дёргается КАЖДЫЙ кадр из ForwardPass/ShadowPass, а его
	// OrderBy срабатывал всякий раз, когда менялось содержимое инстансов - т.е. почти каждый кадр
	// во время стриминга - а не только когда менялся сам набор батчей). Общий кэш + грязный флаг,
	// который трогают только CreateBatch/Remove/ResetRegistrations, убирает и сам пересчёт (кроме
	// как при реальном изменении набора), и аллокацию List на каждый вызов.
	private readonly List<KeyValuePair<int, IndirectBatch>> _sortedBatchesCache = new();
	private bool _isSortedBatchesCacheDirty = true;

	private NativeList<Vertex> _megaVertexBufferCPU;
	private NativeList<uint> _megaIndexBufferCPU;
	private readonly OrderedDictionary<int, MeshInfo> _meshInfos = new();
	private bool _isMeshBuffersDirty = true;

	private DiligentBufferHandle? _megaVertexBufferGPU;
	private DiligentBufferHandle? _megaIndexBufferGPU;

	// Ёмкость (в элементах) GPU-буферов и то, сколько CPU-элементов УЖЕ залито в них - суб-аллокация
	// с запасом по хвосту, как у инстанс-буферов (см. _instanceBufferSectorCapacity): без этого
	// UpdateGpuMegaBuffers пересоздавал и перезаливал ВЕСЬ мега-буфер целиком на регистрацию КАЖДОГО
	// меша, даже если добавился один маленький меш к сцене, где уже миллионы вершин - именно это и
	// вызывало хитчи стриминга. Растим капасити редко (x2, см. UpdateMegaBufferRange), а в обычном
	// случае заливаем только НОВЫЙ хвост (uploadedCount..currentCount).
	private int _megaVertexBufferCapacity;
	private int _megaIndexBufferCapacity;
	private int _megaVertexUploadedCount;
	private int _megaIndexUploadedCount;

	private readonly OrderedDictionary<int, DiligentMaterial> _materialObjects = new();
	private readonly OrderedDictionary<int, IndirectBatch> _indirectBatches = new();

	private BatchSubset _instancesSubset;

	private readonly UnsafeList* _perMeshData;

	// Per-BATCH снимок PerMeshData, реально уходящий на GPU в _meshBatchDataBuffer: куллинг-шейдер
	// (BatchingInstancingCS.hlsl) индексирует MeshBatchData по batchId инстанса, а _perMeshData
	// индексирован по meshId. Пока модель регистрируется один раз, id совпадают (оба считаются с 0
	// в одном порядке) и заливка _perMeshData "как есть" случайно работала; но при перезаселении
	// сцены (превью сабмеша -> обратно целая модель, см. ModelPreviewViewport) меши регистрируются
	// заново и нумерации расходятся - шейдер читал ЧУЖОЙ physicalCommandOffset/bounds и инкрементил
	// чужие draw-команды (пустой рендер сабмеша, случайные куски сетки у целой модели).
	private UnsafeArray* _perBatchData;

	private UnsafeArray* _indirectDatas;
	private UnsafeArray* _cpuBatchCounters;

	private DiligentBufferHandle? _inputIndirectInstancesBuffer;
	private DiligentBufferHandle? _instanceDrawDataBuffer;
	private DiligentBufferHandle? _meshBatchDataBuffer;
	private DiligentBufferHandle? _finallyInstancesBuffer;
	private DiligentBufferHandle? _batchCountersBuffer;

	private DiligentBufferHandle? _gpuInstancesDataBuffer;

	private DiligentBufferHandle? _indirectArgsBuffers;

	// Материалы, помеченные как transmissive/transparent (см. SetMaterialTransparent) - рисуются
	// отдельной петлёй TransparentOnly после снятия копии колор-таргета (см. ForwardPass).
	private readonly HashSet<int> _transparentMaterials = new();



	// Формат цветового таргета геометрии - пекётся во все PSO. Unknown =
	// брать формат свопчейна (главная сцена); превью в HDR-режиме передаёт RGBA16F.
	private readonly TextureObjectFormat _colorFormat = TextureObjectFormat.Unknown;

	private TextureObjectFormat RenderColorFormat =>
		_colorFormat != TextureObjectFormat.Unknown ? _colorFormat : _api.SwapChainColorFormat;

	// PSO геометрии несут MRT-слоты G-buffer-а отражений - см. параметр конструктора.
	private readonly bool _reflectionGbuffer;

	/// <summary>Пекутся ли в геометрические PSO MRT-слоты G-buffer-а отражений (см. конструктор).</summary>
	public bool ReflectionGbuffer => _reflectionGbuffer;

	/// <summary>Список форматов цветовых таргетов геометрических PSO - один цветовой, либо
	/// цветовой + два слота G-buffer-а отражений (см. PipelineRenderTargets).</summary>
	private TextureObjectFormat[] GeometryTargetFormats => _reflectionGbuffer
		? [RenderColorFormat, TextureObjectFormat.R16G16B16A16Float, TextureObjectFormat.R16G16B16A16Float]
		: [RenderColorFormat];

	private readonly DiligentBufferHandle? _lightConstantsBuffer;
	private readonly DiligentBufferHandle? _viewConstantsBuffer;
	private readonly DiligentBufferHandle? _cullConstantsBuffer;
	private readonly IComputeMaterial _cullingMaterial;

	// Кластеризация punctual-светов (LightClusterCS.hlsl): пул светов кадра + counts/indices
	// фроксел-сетки. Буферы фиксированного размера (LightClusters), создаются один раз в конструкторе;
	// доступ Compute|Pixel - компьют пишет через UAV, пиксельные шейдеры батч-материалов читают SRV.
	private readonly DiligentBufferHandle? _punctualLightsBuffer;
	private readonly DiligentBufferHandle? _clusterCountsBuffer;
	private readonly DiligentBufferHandle? _clusterIndicesBuffer;
	private readonly IComputeMaterial _lightClusterMaterial;

	// viewProj-матрицы теневых слайсов punctual-светов - SRV пиксельному шейдеру (сэмплинг теней в
	// кластерной петле UnlitInstancedPS); заливается раз в кадр из стабильной памяти
	// (RenderCamerasData.punctualShadowMatrices, замороженная команда).
	private readonly DiligentBufferHandle? _punctualShadowMatricesBuffer;

	private readonly int _instanceBufferSectorCapacity = 64;
	private int _instanceBufferCapacity = 0;
	private int _meshBatchDataCapacity = 0;

	/// <summary>
	/// DECA_ANIM_LOG=1 - подробный лог пути скиннинга: регистрация инстансов, диспетчеризация и,
	/// главное, ИДЕНТИЧНОСТЬ буферов. Печатается HashCode нативных обёрток: команды графа держат
	/// ссылку на конкретный объект, и смена номера между записью и исполнением - прямое
	/// доказательство того, что буфер пересоздали под уже записанными командами. Без этого номера
	/// «буфер тот же или уже другой» по логу не определить.
	/// </summary>
	private static readonly bool AnimLog = Environment.GetEnvironmentVariable("DECA_ANIM_LOG") == "1";

	/// <summary>
	/// Ставить ли UAV на мега-буфер вершин (нужен только compute-скиннингу). Выставляется редактором
	/// из той же настройки, что и сам скиннинг: с выключенным скиннингом буфер обязан создаваться
	/// БАЙТ В БАЙТ так же, как до появления скиннинга, иначе выключение не является чистым откатом
	/// и им нельзя локализовать проблему.
	/// </summary>
	public static bool SkinningUav { get; set; } = true;

	/// <summary>
	/// Пишет строку диагностики в консоль И в файл рядом с экзешником, сбрасывая его сразу.
	///
	/// Файл здесь не дублирование ради удобства, а необходимость: падение происходит в нативном
	/// вызове и убивает процесс без раскрутки, поэтому буферизованный вывод консоли теряет как раз
	/// последние строки - те самые, ради которых лог и включали. Открытие-закрытие на строку
	/// расточительно, но диагностика включается вручную и живёт секунды.
	/// </summary>
	private static void AnimWrite(string message)
	{
		Console.WriteLine(message);

		try
		{
			File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "anim-diag.log"),
				message + System.Environment.NewLine);
		}
		catch (IOException)
		{
			// Лог диагностики не должен ронять кадр: занятый файл - не повод падать.
		}
	}

	private static string Id(DiligentBufferHandle? buffer) =>
		buffer == null ? "null" : $"#{buffer.GetHashCode():X}";

	/// <summary>Ёмкость буфера счётчиков батчей. Растёт только вверх и с запасом - см.
	/// CheckAndReallocateBuffers о том, почему точное соответствие числу батчей опасно.</summary>
	private int _batchCountersCapacity = 0;

	private int _totalCommands = 0;

	// NOTE: these must be INSTANCE fields, not static. Each DiligentBatchRenderer owns its own
	// _perMeshData/_meshInfos/_materialObjects/_indirectBatches registries, which are indexed (or,
	// for _perMeshData, positionally *appended*) starting from 0 - see Register()/CreateBatch().
	// When these counters used to be `static`, a second DiligentBatchRenderer instance (e.g. the
	// editor's off-screen ModelPreviewViewport render graph, created after the main scene's
	// renderer had already registered N meshes) would hand out mesh/material/batch ids continuing
	// from the main renderer's counters instead of starting at 0, while _perMeshData in the new
	// instance is still empty/short - causing UnsafeList.GetPtr(..., batch.mesh.meshId) in
	// CheckAndReallocateBuffers() to index far past the end of that renderer's own (much smaller)
	// _perMeshData list.
	private int gMeshIndex;
	private int gMaterialIndex;
	private int gBatchIndex;

	private BatchRendererInfo _batchRendererInfo;

	private readonly ResourceStateTracker _stateTracker = new();
	public ResourceStateTracker StateTracker => _stateTracker;

	private bool _isDrawBatchCmdDirty = true;

	// Set whenever RenderResourceManager registers/unregisters an instance on an EXISTING batch
	// (e.g. switching which sub-mesh is shown in ModelPreviewViewport re-populates a batch that
	// was already created for a previously-visited sub-mesh). CheckAndReallocateBuffers only
	// re-uploads _instancesSubset to the GPU and rebakes each batch's FirstInstanceLocation when
	// `buffersRecreated` is true (new batch / capacity growth) - without this flag, re-selecting
	// a previously-visited sub-mesh left the GPU-side instance buffer and indirect draw offsets
	// exactly as they were the last time a buffer was actually recreated, so the compute culling
	// pass kept compacting/drawing whichever sub-mesh's instances happened to be live back then
	// instead of the newly selected one.
	private bool _instancesContentDirty = true;
	public void MarkInstancesContentDirty() => _instancesContentDirty = true;

	public bool IsDirty => _isDrawBatchCmdDirty;
	public void ClearDirty() => _isDrawBatchCmdDirty = false;

	public int ShadowCascadeCount => ShadowRenderer.MaxCascades;

	/// <summary>Рендерер каскадных теней мирового света - наружу только ради отладочного
	/// ридбека shadow map (см. PreviewProbe, DECA_PROBE_SHADOWDUMP).</summary>
	public ShadowRenderer WorldShadowRenderer => _shadowRenderer;

	/// <param name="colorFormat">Формат цветового таргета, в который рисует геометрия - тоже пекётся
	/// во все PSO. Unknown = формат свопчейна; офскрин-превью в HDR-режиме передаёт сюда RGBA16F
	/// (см. PipelineRenderTargets.RenderColorFormat).</param>
	/// <param name="reflectionGbuffer">Добавляет во все геометрические PSO два MRT-слота тонкого
	/// G-buffer-а отражений (RGBA16F, см. PipelineRenderTargets.NormalRoughnessTarget). Обязан
	/// совпадать с тем, биндит ли ForwardPass эти таргеты: на Vulkan пайплайн с числом аттачментов,
	/// отличным от привязанного, ломает рендер-пасс. Шейдеру писать в слоты не обязательно -
	/// вариант без FEATURE_REFLECTION_GBUFFER просто оставляет их очищенными.</param>
	public DiligentBatchRenderer(DiligentGraphicsApi api,
		TextureObjectFormat colorFormat = TextureObjectFormat.Unknown, bool reflectionGbuffer = false)
	{
		_perMeshData = UnsafeList.Allocate<PerMeshData>(32);
		_api = api;
		_colorFormat = colorFormat;
		_reflectionGbuffer = reflectionGbuffer;
		_deviceType = _api.Device.GetDeviceInfo().Type;
		
		_cullConstantsBuffer = CreateConstantsBuffer<CullData>("Cull Constants");
		_viewConstantsBuffer = CreateConstantsBuffer<ViewData>("View Constants");
		_lightConstantsBuffer = CreateConstantsBuffer<LightData>("Light Constants");

		var cullingShader = new DiligentShader(_api, "Batching Instancing CS", "EditorAssets/shader", "BatchingInstancingCS.hlsl", ShaderObjectType.Compute, "CSMain");
		_cullingMaterial = new DiligentComputeMaterial("Batching Instancing CS", _api);
		_cullingMaterial.SetShader(cullingShader);
		_cullingMaterial.SetBuffer("Constants", _cullConstantsBuffer);

		_punctualLightsBuffer = (DiligentBufferHandle)_api.CreateBuffer<PunctualLight>(LightClusters.MaxLights,
			new BufferInfo
			{
				name = "Punctual Lights Buffer",
				type = BufferHandleType.Structured,
				access = HandleAccess.Compute | HandleAccess.Pixel,
			});
		_clusterCountsBuffer = (DiligentBufferHandle)_api.CreateBuffer<uint>(LightClusters.ClusterCount,
			new BufferInfo
			{
				name = "Light Cluster Counts Buffer",
				type = BufferHandleType.Structured,
				access = HandleAccess.Compute | HandleAccess.Pixel,
			});
		_clusterIndicesBuffer = (DiligentBufferHandle)_api.CreateBuffer<uint>(
			LightClusters.ClusterCount * LightClusters.MaxLightsPerCluster,
			new BufferInfo
			{
				name = "Light Cluster Indices Buffer",
				type = BufferHandleType.Structured,
				access = HandleAccess.Compute | HandleAccess.Pixel,
			});

		// Шаг элемента - Vector4, а НЕ Matrix4x4: пиксельный шейдер читает слайс как четыре
		// row-major строки (UnlitInstancedPS.hlsl::LoadPunctualShadowMatrix). Матрицу в элементе
		// структурного буфера держать нельзя - её majorness там не подчиняется PackMatrixRowMajor и
		// отличается у D3D12 и Vulkan, из-за чего тени punctual-светов не работали на D3D12 вообще.
		// Содержимое буфера при этом не меняется: заливка идёт тем же UpdateBuffer<Matrix4x4> по
		// массиву RenderCamerasData.punctualShadowMatrices, байты те же, меняется только объявленный
		// шаг вью.
		_punctualShadowMatricesBuffer = (DiligentBufferHandle)_api.CreateBuffer<Vector4>(
			LightClusters.MaxShadowSlices * 4,
			new BufferInfo
			{
				name = "Punctual Shadow Matrices Buffer",
				type = BufferHandleType.Structured,
				access = HandleAccess.Pixel,
			});

		var clusterShader = new DiligentShader(_api, "Light Cluster CS", "EditorAssets/shader", "LightClusterCS.hlsl", ShaderObjectType.Compute, "CSMain");
		_lightClusterMaterial = new DiligentComputeMaterial("Light Cluster CS", _api);
		_lightClusterMaterial.SetShader(clusterShader);
		_lightClusterMaterial.SetBuffer("Constants", _cullConstantsBuffer);
		_lightClusterMaterial.SetBuffer("Light", _lightConstantsBuffer);
		_lightClusterMaterial.SetBuffer("PunctualLights", _punctualLightsBuffer);
		_lightClusterMaterial.SetBuffer("ClusterCounts", _clusterCountsBuffer);
		_lightClusterMaterial.SetBuffer("ClusterIndices", _clusterIndicesBuffer);

		_shadowRenderer = new ShadowRenderer(api);
		Skinning = new DiligentSkinningPass(_api);
	}

	/// <summary>GPU-скиннинг сцены (см. <see cref="DiligentSkinningPass"/>). Живёт здесь, потому что
	/// пишет он в мега-буфер вершин, которым владеет рендерер.</summary>
	public DiligentSkinningPass Skinning { get; }

	/// <summary>
	/// Диспетчеризует скиннинг всех зарегистрированных скиннед-инстансов. Зовётся раз в кадр ПЕРЕД
	/// исполнением графа: и тени, и forward, и трассировка читают уже деформированную геометрию
	/// (см. <see cref="DiligentSkinningPass.Execute"/>).
	///
	/// ВАЖНО ПРО ПОРЯДОК: вызывать строго ДО записи команд кадра (у сцены редактора это значит до
	/// SystemRoot.Update, внутри которого CullingAndRenderSystem их пишет). Причина в заливке ниже:
	/// UpdateGpuMegaBuffers на пути РОСТА пересоздаёт мега-буфер и отпускает старый, а уже
	/// записанные команды держат ссылку именно на старый объект - вызов после записи освобождал
	/// буфер прямо под ними, и кадр падал в DrawIndexedIndirect по освобождённой памяти
	/// (0xC0000005 при появлении скиннед-модели в сцене).
	/// </summary>
	/// <summary>
	/// Снимок счётчиков, от которых зависит переселение нативных массивов indirect-команд
	/// (см. CheckAndReallocateBuffers). Нужен для диагностики падения в UpdateBuffer внутри
	/// ЗАМОРОЖЕННОЙ команды: та держит указатель на _indirectDatas/_cpuBatchCounters и перечитывает
	/// его при каждом реплее, поэтому любое их перевыделение под уже записанными командами - это
	/// чтение освобождённой памяти. Растущее здесь число прямо называет источник роста.
	/// </summary>
	public string DiagCounters =>
		$"meshes={gMeshIndex} batches={_indirectBatches.Count} instances={_instancesSubset.instances.Length} " +
		$"commands={_totalCommands} megaVerts={(_megaVertexBufferCPU.IsCreated ? _megaVertexBufferCPU.Count : 0)} " +
		$"instCap={_instanceBufferCapacity} meshBatchCap={_meshBatchDataCapacity}";

	public void ExecuteSkinning()
	{
		if (!Skinning.HasWork)
		{
			return;
		}

		// Мега-буфер обязан быть залит до диспетчеризации: приёмник скиннед-инстанса появляется в
		// нём той же регистрацией, что и регион, и до заливки в GPU-буфере его просто нет.
		var megaBefore = _megaVertexBufferGPU;
		UpdateGpuMegaBuffers();

		if (AnimLog)
		{
			// Смена номера мега-буфера здесь означает, что заливка его ПЕРЕСОЗДАЛА - а на него
			// ссылаются уже записанные команды отрисовки.
			AnimWrite($"[anim] ExecuteSkinning: mega {Id(megaBefore)}" +
				(ReferenceEquals(megaBefore, _megaVertexBufferGPU) ? "" : $" -> {Id(_megaVertexBufferGPU)} ПЕРЕСОЗДАН") +
				$", indirect={Id(_indirectArgsBuffers)}, counters={Id(_batchCountersBuffer)}, {DiagCounters}");
		}

		Skinning.Execute(_megaVertexBufferGPU);
	}

	private void SetAllCommandsDirty()
	{
		_isDrawBatchCmdDirty = true;
	}

	private DiligentBufferHandle CreateConstantsBuffer<T>(string name) where T: unmanaged
	{
		return (DiligentBufferHandle)_api.CreateBuffer<T>(new BufferInfo()
		{
			name = name,
			type = BufferHandleType.Constant,
		});
	}

	private void CreateStructuredBuffer<T>(ref DiligentBufferHandle? buffer, int bufferCapacity, string name = "Indirect Instance Data Buffer") where T : struct
	{
		buffer?.Release();

		buffer = (DiligentBufferHandle)_api.CreateBuffer<T>(bufferCapacity,
			new BufferInfo
			{
				name = name,
				type = BufferHandleType.Structured,
				access = HandleAccess.Compute,
			});
	}

	private void CreateVertexParamBuffer<T>(ref DiligentBufferHandle? buffer, int bufferCapacity) where T : struct
	{
		buffer?.Release();

		buffer = (DiligentBufferHandle)_api.CreateBuffer<int>(bufferCapacity * _instanceBufferSectorCapacity, new BufferInfo
		{
			name = $"{typeof(T).Name} Param Instance Data Buffer",
			type = BufferHandleType.Vertex,
			access = HandleAccess.Compute
		});
	}

	private void CreateInstanceParamBuffer<T>(ref DiligentBufferHandle? buffer, int bufferCapacity) where T : struct
	{
		buffer?.Release();

		int count = bufferCapacity * _instanceBufferSectorCapacity;

		buffer = (DiligentBufferHandle)_api.CreateBuffer<T>(count,
			new BufferInfo
			{
				name = $"{typeof(T).Name} Param Instance Data Buffer",
				dynamic = false,
				type = BufferHandleType.Structured,
				access = HandleAccess.Vertex
			});
	}

	private void CreateIndirectBuffer(ref DiligentBufferHandle? buffer, int maxCommands)
	{
		buffer?.Release();

		buffer = (DiligentBufferHandle)_api.CreateBuffer<DrawIndexedIndirectCommand>(maxCommands,
			new BufferInfo
			{
				name = "Indirect Draw Args Buffer",
				type = BufferHandleType.IndirectArgs,
				access = HandleAccess.Compute,
			});
	}

	public MeshId Register(IMeshObject meshObject)
	{
		var meshId = gMeshIndex;
		var mesh = (DiligentMesh)meshObject;
		var vertices = mesh.VertexData;
		var indices = mesh.IndexData;

		if (!_megaVertexBufferCPU.IsCreated)
		{
			_megaVertexBufferCPU = new NativeList<Vertex>(UnsafeArray.GetLength(vertices));
			_megaIndexBufferCPU = new NativeList<uint>(UnsafeArray.GetLength(indices));
		}

		int baseVertex = _megaVertexBufferCPU.Count;
		uint firstIndex = (uint)_megaIndexBufferCPU.Count;

		// Копирование БЛОКОМ, а не поэлементно: у сцены уровня Sponza это миллионы вершин и десятки
		// миллионов индексов, то есть десятки миллионов вызовов Add, каждый из которых ещё и мог
		// вызвать рост ёмкости удвоением с перекопированием всего накопленного.
		var vertexCount = UnsafeArray.GetLength(vertices);
		var indexCount = UnsafeArray.GetLength(indices);

		if (vertexCount > 0)
		{
			UnsafeList.AddRange(_megaVertexBufferCPU.GetNative(),
				UnsafeArray.GetPtr<Vertex>(vertices, 0), vertexCount);
		}

		if (indexCount > 0)
		{
			UnsafeList.AddRange(_megaIndexBufferCPU.GetNative(),
				UnsafeArray.GetPtr<uint>(indices, 0), indexCount);
		}

		_meshInfos[meshId] = new MeshInfo
		{
			IndexCount = UnsafeArray.GetLength(indices),
			FirstIndex = firstIndex,
			BaseVertex = baseVertex,
			VertexCount = vertexCount,
			LodLevels = mesh.GetLodLevels()
		};

		var pmd = new PerMeshData()
		{
			bounds = new Vector4(meshObject.Center, meshObject.Radius)
		};
		pmd.SetLods(mesh.GetLodLevels());
		UnsafeList.Add(_perMeshData, pmd);

		_isMeshBuffersDirty = true;
		SetAllCommandsDirty();
		gMeshIndex++;
		return new MeshId(meshId);
	}

	/// <summary>
	/// Заводит СКИННЕД-ИНСТАНС меша: отдельный участок мега-буфера вершин, который каждый кадр
	/// переписывает <see cref="DiligentSkinningPass"/> деформированной копией bind-позы. Возвращает
	/// новый meshId (индексы и LOD-уровни разделяются с исходным мешом - меняется только baseVertex)
	/// и офсет палитры инстанса.
	///
	/// Копия на ИНСТАНС, а не на меш: два персонажа с одной моделью стоят в разных позах, и общий
	/// приёмник означал бы, что второй затирает первого. Цена - vertexCount вершин VRAM на
	/// персонажа; за неё покупается то, что скиннед-меш для всего остального движка (кулинг,
	/// индиректные дроу, тени, BVH RT-отражений) остаётся обычным мешем.
	///
	/// Приёмник инициализируется bind-позой, а не нулями: между регистрацией и первой
	/// диспетчеризацией скиннинга проходит кадр, и нулевой приёмник дал бы вспышку схлопнутой в
	/// точку геометрии.
	/// </summary>
	public (MeshId Mesh, int PaletteOffset) RegisterSkinnedInstance(MeshId sourceMesh, int jointCount, int skinBase)
	{
		var source = _meshInfos[sourceMesh.meshId];
		int destBaseVertex = _megaVertexBufferCPU.Count;

		if (source.VertexCount > 0)
		{
			// Копирование ВНУТРИ одного списка: AddRange по указателю в него же сломался бы при
			// росте ёмкости (указатель на старую память), поэтому bind-поза сначала снимается в
			// промежуточный массив.
			var bindPose = new Vertex[source.VertexCount];
			for (int i = 0; i < bindPose.Length; i++)
			{
				bindPose[i] = UnsafeList.Get<Vertex>(_megaVertexBufferCPU.GetNative(), source.BaseVertex + i);
			}

			fixed (Vertex* ptr = bindPose)
			{
				UnsafeList.AddRange(_megaVertexBufferCPU.GetNative(), ptr, bindPose.Length);
			}
		}

		int meshId = gMeshIndex;
		_meshInfos[meshId] = new MeshInfo
		{
			IndexCount = source.IndexCount,
			FirstIndex = source.FirstIndex,
			BaseVertex = destBaseVertex,
			VertexCount = source.VertexCount,
			LodLevels = source.LodLevels,
		};

		// PerMeshData индексируется meshId и обязан существовать для КАЖДОГО зарегистрированного
		// меша: куллинг-шейдер читает его по batchId, и пропуск сдвинул бы всю таблицу.
		var perMesh = UnsafeList.Get<PerMeshData>(_perMeshData, sourceMesh.meshId);
		UnsafeList.Add(_perMeshData, perMesh);

		_isMeshBuffersDirty = true;
		SetAllCommandsDirty();
		gMeshIndex++;

		int paletteOffset = Skinning.AddInstance(source.BaseVertex, destBaseVertex, source.VertexCount,
			skinBase, jointCount);

		if (AnimLog)
		{
			AnimWrite($"[anim] RegisterSkinnedInstance: src mesh={sourceMesh.meshId} -> new mesh={meshId}, " +
				$"baseVertex {source.BaseVertex}->{destBaseVertex}, verts={source.VertexCount}, " +
				$"indices[{source.FirstIndex}..+{source.IndexCount}], joints={jointCount}, palette={paletteOffset}; " +
				$"после: {DiagCounters}");
		}

		return (new MeshId(meshId), paletteOffset);
	}

	public MaterialId Register(IMaterialObject materialObject)
	{
		var materialId = gMaterialIndex;
		var material = (DiligentMaterial)materialObject;
		_materialObjects[materialId] = material;

		material.SetBuffer("View", _viewConstantsBuffer, HandleAccess.Vertex | HandleAccess.Pixel);
		material.SetBuffer("Light", _lightConstantsBuffer, HandleAccess.Vertex | HandleAccess.Pixel);

		_shadowRenderer.SetShadowResources(materialObject);

		material.SetBuffer("GPURenderInstances", _gpuInstancesDataBuffer, HandleAccess.Vertex);

		// Результаты кластеризации светов - пиксельному шейдеру (SRV): пул светов + counts/indices
		// фроксел-сетки (см. UnlitInstancedPS.hlsl, петля clustered-шейдинга).
		material.SetBuffer("PunctualLights", _punctualLightsBuffer, HandleAccess.Pixel);
		material.SetBuffer("ClusterCounts", _clusterCountsBuffer, HandleAccess.Pixel);
		material.SetBuffer("ClusterIndices", _clusterIndicesBuffer, HandleAccess.Pixel);
		material.SetBuffer("PunctualShadowMatrices", _punctualShadowMatricesBuffer, HandleAccess.Pixel);

		gMaterialIndex++;
		_isDrawBatchCmdDirty = true;
		return new MaterialId(materialId);
	}

	public BatchId CreateBatch(MeshId meshId, MaterialId materialId)
	{
		var batchIndex = gBatchIndex;
		_indirectBatches.Add(batchIndex, new IndirectBatch(meshId, materialId));
		_isDrawRangesCacheDirty = true;
		_isSortedBatchesCacheDirty = true;
		SetAllCommandsDirty();
		gBatchIndex++;
		return new BatchId(batchIndex);
	}

	public void Remove(int batchId)
	{
		_isDrawRangesCacheDirty = true;
		_isSortedBatchesCacheDirty = true;
		SetAllCommandsDirty();
	}

	/// <summary>Кэш _indirectBatches, отсортированный по materialId (см. <see cref="_sortedBatchesCache"/>).
	/// Пересчитывается только когда меняется НАБОР батчей; переупорядочение/добавление ИНСТАНСОВ
	/// существующих батчей его не трогает. Тай-брейк по ключу (индексу батча) воспроизводит порядок
	/// прежнего `_indirectBatches.OrderBy(...)` - OrderBy стабилен, а OrderedDictionary итерируется в
	/// порядке добавления, то есть по возрастанию индекса батча.</summary>
	private List<KeyValuePair<int, IndirectBatch>> GetSortedBatches()
	{
		if (_isSortedBatchesCacheDirty)
		{
			_sortedBatchesCache.Clear();
			foreach (var kvp in _indirectBatches)
			{
				_sortedBatchesCache.Add(kvp);
			}

			_sortedBatchesCache.Sort(static (a, b) =>
			{
				int cmp = a.Value.material.materialId.CompareTo(b.Value.material.materialId);
				return cmp != 0 ? cmp : a.Key.CompareTo(b.Key);
			});

			_isSortedBatchesCacheDirty = false;
		}

		return _sortedBatchesCache;
	}

	/// <summary>Выбрасывает ВСЕ регистрации меша/материала/батча и накопленную геометрию, возвращая
	/// рендерер в состояние сразу после создания.
	///
	/// Для СЦЕНЫ с несколькими резидентными моделями снятие ОДНОЙ модели без трогания остальных
	/// делает <see cref="UnregisterModel"/> - id плотные и никогда не переиспользуются, так что
	/// удаление подмножества id других не сдвигает. Этот же (полный) сброс остаётся нужен
	/// потребителю, который держит ровно ОДНУ модель за раз (превью, бейкер иконок): дешевле
	/// обнулить счётчики id и мега-буферы CPU целиком, чем гонять UnregisterModel по всем
	/// когда-либо зарегистрированным id, и он же переиспользуется в ClearAll как более грубый, но
	/// проверенный путь.
	///
	/// Мега-буферы GPU у этого пути ПОЛНОСТЬЮ пересоздаются на следующий UpdateGpuMegaBuffers (см.
	/// сброс капасити/uploaded-счётчиков ниже) - в отличие от обычной регистрации, которая теперь
	/// доливает только новый хвост (см. UpdateMegaBufferRange).
	///
	/// Вызывающий ОБЯЗАН сперва дождаться GPU (Flush + WaitForIdle) и пересобрать граф: старые
	/// MeshId/MaterialId/BatchId становятся недействительными, а замороженные команды на них
	/// ссылаются.</summary>
	public void ResetRegistrations()
	{
		// Мега-буферы CPU освобождаются, а не очищаются: Clear сохранил бы ёмкость, накопленную под
		// все прошлые модели, - а это ровно та память, ради которой сброс и делается. Следующий
		// Register создаст их заново под размер новой модели (см. проверку IsCreated в Register).
		if (_megaVertexBufferCPU.IsCreated)
		{
			_megaVertexBufferCPU.Dispose();
			_megaVertexBufferCPU = default;
		}

		if (_megaIndexBufferCPU.IsCreated)
		{
			_megaIndexBufferCPU.Dispose();
			_megaIndexBufferCPU = default;
		}

		// GPU-буферы отпускаем здесь же: UpdateGpuMegaBuffers пересоздаст их по _isMeshBuffersDirty,
		// а держать до тех пор буфер с геометрией уже несуществующих мешей незачем.
		_megaVertexBufferGPU?.Release();
		_megaVertexBufferGPU = null;
		_megaIndexBufferGPU?.Release();
		_megaIndexBufferGPU = null;

		// Капасити/uploaded-счётчики суб-аллокатора мега-буферов (см. поля выше) - без сброса первая
		// же регистрация новой модели унаследовала бы капасити прошлой сцены (не баг, просто лишний
		// запас памяти на один цикл роста).
		// Регионы скиннинга указывают офсетами в мега-буфер, которого больше нет.
		Skinning.Reset();

		// Ёмкости батч-буферов - вместе с остальными: их буферы отпускаются полным сбросом ниже, и
		// сохранённый запас указывал бы на несуществующие ресурсы.
		_batchCountersCapacity = 0;

		_megaVertexBufferCapacity = 0;
		_megaIndexBufferCapacity = 0;
		_megaVertexUploadedCount = 0;
		_megaIndexUploadedCount = 0;

		// Сброс обязан быть ПОЛНЫМ: оставь любую из коллекций непустой - и её ключи разойдутся с
		// заново выданными с нуля индексами.
		_meshInfos.Clear();
		_materialObjects.Clear();
		_indirectBatches.Clear();
		_materialDrawRanges.Clear();
		_sortedBatchesCache.Clear();
		UnsafeList.Clear(_perMeshData);

		gMeshIndex = 0;
		gMaterialIndex = 0;
		gBatchIndex = 0;

		_isMeshBuffersDirty = true;
		_isDrawRangesCacheDirty = true;
		_isSortedBatchesCacheDirty = true;
		_isDrawBatchCmdDirty = true;
		SetAllCommandsDirty();
	}

	/// <summary>См. <see cref="DiligentBatchRenderer.UnregisterModel"/>: партиционное выселение ОДНОЙ модели.
	/// mesh/material/batch id плотные и никогда не переиспользуются (см. комментарий у
	/// <see cref="ResetRegistrations"/>), поэтому снятие id из соответствующих словарей не требует
	/// сдвига чужих id - ничего их не переиндексирует. Материалы явно Release-ятся (SRB/PSO/свои
	/// константные буферы - НЕ общие View/Light/GPURenderInstances и т.п., они у материала не в
	/// собственности, см. DiligentMaterial.SetBuffer/Release); геометрия снятых мешей в мега-буфере
	/// НЕ освобождается - её диапазон просто больше ни на один батч не ссылается, до суб-аллокатора
	/// это неиспользуемая, но безопасная память.
	///
	/// Вызывающий ОБЯЗАН снять инстанс-сущности, ссылающиеся на удаляемые batchId, ДО этого вызова
	/// (см. интерфейсный комментарий) - иначе на следующем CheckAndReallocateBuffers индирект-буферы
	/// пересоздадутся под уменьшившийся _totalCommands/batchCount, а старые инстансы будут указывать
	/// на офсеты вне их границ.</summary>
	public void UnregisterModel(IEnumerable<BatchId> batchIds, IEnumerable<MaterialId> materialIds,
		IEnumerable<MeshId> meshIds)
	{
		foreach (var batchId in batchIds)
		{
			if (_indirectBatches.Remove(batchId.batchId))
			{
				_isDrawRangesCacheDirty = true;
				_isSortedBatchesCacheDirty = true;
			}
		}

		foreach (var materialId in materialIds)
		{
			if (_materialObjects.Remove(materialId.materialId, out var material))
			{
				material.Release();
			}

			_transparentMaterials.Remove(materialId.materialId);
		}

		foreach (var meshId in meshIds)
		{
			_meshInfos.Remove(meshId.meshId);
		}

		SetAllCommandsDirty();
	}

	public void PinInstances(BatchSubset subset)
	{
		_instancesSubset = subset;
		SetAllCommandsDirty();
	}

	/// <summary>Привязывает общий View-кбуфер (обновляемый SetupViewData) к материалу, который НЕ
	/// регистрируется как батч-материал - например, фуллскрин-скай превью (см. ForwardPass): ему
	/// нужна камера, но Register() тянет за собой лишние ресурсы (инстанс-буферы, тени).</summary>
	public void BindViewConstants(IMaterialObject material)
	{
		((DiligentMaterial)material).SetBuffer("View", _viewConstantsBuffer, HandleAccess.Vertex | HandleAccess.Pixel);
	}

	/// <summary>См. <see cref="IBatchRenderer.BindShadowResources"/>: кбуфер Light + массив shadow
	/// map фуллскрин-материалу, минуя Register().</summary>
	public void BindShadowResources(IMaterialObject material)
	{
		var diligentMaterial = (DiligentMaterial)material;
		diligentMaterial.SetBuffer("Light", _lightConstantsBuffer, HandleAccess.Vertex | HandleAccess.Pixel);
		_shadowRenderer.SetShadowResources(material);

		// Пул punctual-светов и матрицы их слайсов - фуллскрин-пассам, которые рассеивают свет
		// ламп в среде (VolumetricCommon.hlsl). Шейдеры без этих объявлений привязку игнорируют -
		// как и карты punctual-теней в SetShadowResources выше.
		diligentMaterial.SetBuffer("PunctualLights", _punctualLightsBuffer, HandleAccess.Pixel);
		diligentMaterial.SetBuffer("PunctualShadowMatrices", _punctualShadowMatricesBuffer, HandleAccess.Pixel);
	}

	/// <summary>См. <see cref="IBatchRenderer.SetMaterialAlphaTestedShadow"/>.</summary>
	public void SetMaterialAlphaTestedShadow(int materialId, DecaEngine.Graphics.ModelLoader.BaseColorBinding baseColor,
		float alphaCutoff)
	{
		if (baseColor is null)
		{
			return;
		}

		_shadowRenderer.RegisterAlphaTestedMaterial(materialId, baseColor.Texture, baseColor.Sampler,
			alphaCutoff, baseColor.Stream);
	}

	/// <summary>См. <see cref="IBatchRenderer.SetMaterialShadowCasting"/>.</summary>
	public void SetMaterialShadowCasting(int materialId, bool casts) =>
		_shadowRenderer.SetMaterialShadowCasting(materialId, casts);

	/// <summary>См. <see cref="IBatchRenderer.TransitionShadowMapsForRead"/>. DepthRead, а не
	/// ShaderResource - см. комментарий в ShadowRenderer.ExecuteDrawShadows.</summary>
	public void TransitionShadowMapsForRead(ICommandBuffer cmd)
	{
		cmd.TransitionResource(_shadowRenderer.ShadowMapsTarget, ResourceState.DepthRead);
	}

	// После каждого UpdateBuffer - явный барьер CopyDest -> ConstantBuffer. Без него между
	// копией констант и дроу/диспатчами нет зависимости, и D3D12 вправе слить НЕСКОЛЬКО
	// апдейтов одного кбуфера в пачку до исполнения дроу: теневые каскады (4 апдейта View/Light
	// за пасс, см. ShadowPass) рендерились с матрицами ЧУЖИХ каскадов - слайсы 1/3 выходили
	// побитовыми копиями 0/2. На Vulkan драйвер прощал, на D3D12 - нет (видно в RenderDoc).

	public unsafe void SetupViewData(ICommandBuffer? cmd, ref ViewData viewData)
	{
		fixed(ViewData* ptr = &viewData)
		{
			cmd.UpdateBuffer(_viewConstantsBuffer, 0, ptr);
		}
		cmd.TransitionResource(_viewConstantsBuffer, ResourceState.ConstantBuffer);
	}

	public unsafe void SetupCullData(ICommandBuffer? cmd, ref CullData cullData)
	{
		fixed (CullData* ptr = &cullData)
		{
			cmd.UpdateBuffer(_cullConstantsBuffer, 0, ptr);
		}
		cmd.TransitionResource(_cullConstantsBuffer, ResourceState.ConstantBuffer);
	}

	public unsafe void SetupLightData(ICommandBuffer? cmd, ref LightData lightData)
	{
		fixed (LightData* ptr = &lightData)
		{
			cmd.UpdateBuffer(_lightConstantsBuffer, 0, ptr);
		}
		cmd.TransitionResource(_lightConstantsBuffer, ResourceState.ConstantBuffer);
	}

	public IReadOnlyList<KeyValuePair<int, DiligentMaterial>> GetMaterials() => _materialObjects;
	public IReadOnlyList<KeyValuePair<int, IndirectBatch>> GetBatches() => _indirectBatches;
	public BatchRendererInfo ReadInfo() => _batchRendererInfo;

	public DrawIndexedIndirectCommand[] ReadIndirectArgsForDebugging()
	{
		if (_totalCommands == 0) return [];
		var data = new DrawIndexedIndirectCommand[_totalCommands];
		fixed (void* ptr = data)
			_api.ImmediateContext.ReadBufferExt<DrawIndexedIndirectCommand>(_api.Device, _indirectArgsBuffers.Buffer, ptr, (uint)(_totalCommands * sizeof(DrawIndexedIndirectCommand)));
		return data;
	}

	public IReadOnlyDictionary<int, MaterialDrawRange> GetDebugDrawRanges() => _materialDrawRanges;

	public void CheckAndReallocateBuffers()
	{
		int totalInstances = _instancesSubset.instances.Length;
		if (totalInstances == 0) return;

		_totalCommands = 0;
		foreach(var batch in _indirectBatches)
		{
			var meshInfo = _meshInfos[batch.Value.mesh.meshId];
			_totalCommands += (meshInfo.LodLevels != null && UnsafeArray.GetLength(meshInfo.LodLevels) > 0) ? UnsafeArray.GetLength(meshInfo.LodLevels) : 1;
		}

		if (_totalCommands == 0) return;

		bool buffersRecreated = false;
		if (totalInstances > _instanceBufferCapacity * _instanceBufferSectorCapacity)
		{
			_instanceBufferCapacity = (int)MathF.Ceiling((float)totalInstances / _instanceBufferSectorCapacity);
			CreateStructuredBuffer<IndirectInstance>(ref _inputIndirectInstancesBuffer, _instanceBufferCapacity * _instanceBufferSectorCapacity);
			CreateVertexParamBuffer<int>(ref _finallyInstancesBuffer, _instanceBufferCapacity);
			CreateStructuredBuffer<DrawData>(ref _instanceDrawDataBuffer, _instanceBufferCapacity * _instanceBufferSectorCapacity);
			CreateInstanceParamBuffer<GPURenderInstance>(ref _gpuInstancesDataBuffer, _instanceBufferCapacity);
			buffersRecreated = true;
		}

		// Рост ТОЛЬКО ВВЕРХ и с запасом (x2), как у инстанс-буферов выше. Прежнее условие сравнивало
		// размер на точное неравенство, поэтому пересоздание случалось на КАЖДОЕ изменение числа
		// батчей - в том числе на +1 и на уменьшение. А пересоздание здесь ставит buffersRecreated,
		// который ниже пересоздаёт буфер indirect-аргументов и переселяет _indirectDatas: ровно те
		// объекты, на которые ссылаются УЖЕ ЗАПИСАННЫЕ команды (теневой проход пишет их раньше
		// forward-а). Отсюда и падение в DrawIndexedIndirect с живой managed-обёрткой и мёртвым
		// нативным объектом за ней. С запасом перевыделение становится редким, а лишние элементы
		// буфера просто не адресуются: куллинг-шейдер читает счётчики по batchId < числа батчей.
		// Рост ТОЛЬКО ВВЕРХ и с запасом (x2), как у инстанс-буферов выше. Прежнее условие сравнивало
		// размер на точное неравенство, поэтому пересоздание случалось на КАЖДОЕ изменение числа
		// батчей - в том числе на +1 и на уменьшение, - а оно тянет за собой пересоздание буфера
		// indirect-аргументов и переселение _indirectDatas, на которые ссылаются уже записанные
		// команды. Запас здесь безопасен: лишние счётчики нулевые, а куллинг-шейдер адресует их по
		// batchId в пределах реального числа батчей. (У буфера indirect-команд запас, наоборот,
		// НЕДОПУСТИМ - см. ниже: он заливается целиком, и хвост уехал бы мусорными командами.)
		int batchCount = _indirectBatches.Count;
		if (_batchCountersBuffer == null || batchCount > _batchCountersCapacity)
		{
			_batchCountersCapacity = Math.Max(batchCount, Math.Max(64, _batchCountersCapacity * 2));

			CreateStructuredBuffer<uint>(ref _batchCountersBuffer, _batchCountersCapacity, "Batch Counters Buffer");
			UnsafeArray.Resize<uint>(ref _cpuBatchCounters, _batchCountersCapacity);
			buffersRecreated = true;
		}

		// _meshBatchDataBuffer is indexed by BATCH id on the GPU (the culling shader reads
		// MeshBatchData[instance.batchId], see _perBatchData above), so it must cover every batch id
		// ever handed out - batch ids are dense 0..gBatchIndex-1 and never removed, so gBatchIndex is
		// exactly the required length. Sizing it by mesh count (as before) desynced from batch ids as
		// soon as a scene was repopulated (preview switching model/sub-mesh) and mesh/batch numbering
		// diverged.
		int batchTotal = gBatchIndex;
		if (batchTotal > _meshBatchDataCapacity)
		{
			_meshBatchDataCapacity = batchTotal;
			buffersRecreated = true;
		}

		bool needsInstanceUpload = buffersRecreated || _instancesContentDirty;
		_instancesContentDirty = false;

		if (buffersRecreated)
		{
			// Буфер indirect-команд и парный ему CPU-массив - РОВНО по числу команд, без запаса.
			// Запас здесь недопустим: массив заливается в буфер ЦЕЛИКОМ (см.
			// ClearIndirectDrawBuffers), и неинициализированный хвост уехал бы в него мусорными
			// командами отрисовки - то есть DrawIndexedIndirect с произвольными смещениями.
			// У счётчиков батчей запас безопасен (лишние элементы нулевые и не адресуются), а здесь -
			// нет, и это не симметричные случаи.
			CreateIndirectBuffer(ref _indirectArgsBuffers, _totalCommands);
			UnsafeArray.Resize<DrawIndexedIndirectCommand>(ref _indirectDatas, _totalCommands);

			CreateStructuredBuffer<PerMeshData>(ref _meshBatchDataBuffer, _meshBatchDataCapacity, "Mesh Batch Data Buffer");
			UnsafeArray.Resize<PerMeshData>(ref _perBatchData, _meshBatchDataCapacity);
		}

		if (AnimLog && buffersRecreated)
		{
			// Самая важная строка лога: здесь ресурсы ПЕРЕСОЗДАНЫ. Если после неё нет пересборки
			// графа, все ранее записанные команды держат уже освобождённые объекты.
			AnimWrite($"[anim] CheckAndReallocateBuffers: ПЕРЕСОЗДАНЫ буферы -> " +
				$"indirect={Id(_indirectArgsBuffers)}, counters={Id(_batchCountersBuffer)}, " +
				$"meshBatch={Id(_meshBatchDataBuffer)}, instances={Id(_inputIndirectInstancesBuffer)}; {DiagCounters}\n" +
				$"{Environment.StackTrace}");
		}

		if (needsInstanceUpload)
		{
			IDeviceContext ctx = _api.ImmediateContext;
			ctx.UploadBufferExt<IndirectInstance>(_inputIndirectInstancesBuffer.Buffer, _instancesSubset.instances.GetNative());
			ctx.UploadBufferExt<DrawData>(_instanceDrawDataBuffer.Buffer, _instancesSubset.drawData.GetNative());
			ctx.UploadBufferExt<GPURenderInstance>(_gpuInstancesDataBuffer.Buffer, _instancesSubset.gpuData.GetNative());

			var sortedBatches = GetSortedBatches();
			uint commandOffset = 0;
			for (var i = 0; i < sortedBatches.Count; i++)
			{
				var batchKvp = sortedBatches[i];
				var batch = batchKvp.Value;
				var batchIndex = batchKvp.Key;

				var meshInfo = _meshInfos[batch.mesh.meshId];

				// Копия per-mesh данных ПОД ЭТОТ батч со своим physicalCommandOffset: раньше offset
				// писался прямо в общий _perMeshData[meshId], так что два батча одного меша (один меш
				// с разными материалами) затирали offset друг друга - последний выигрывал, и инстансы
				// обоих батчей инкрементили одну и ту же draw-команду.
				var pmd = *UnsafeList.GetPtr<PerMeshData>(_perMeshData, batch.mesh.meshId);
				pmd.physicalCommandOffset = commandOffset;
				UnsafeArray.Set(_perBatchData, batchIndex, pmd);

#if DEBUG
				if (Environment.GetEnvironmentVariable("DECA_BATCH_DEBUG") == "1")
				{
					Console.WriteLine($"[batch-debug] alloc batch={batchIndex} mesh={batch.mesh.meshId} mat={batch.material.materialId} offset={commandOffset} lodCount={pmd.lodCount} indexCount={meshInfo.IndexCount} firstInstance={_instancesSubset.countData[batchIndex]}");
				}
#endif

				if (meshInfo.LodLevels != null && UnsafeArray.GetLength(meshInfo.LodLevels) > 0)
				{
					for (int lodIndex = 0; lodIndex < UnsafeArray.GetLength(meshInfo.LodLevels); lodIndex++)
					{
						var lod = UnsafeArray.Get<LodLevel>(meshInfo.LodLevels, lodIndex);
						var cmd = UnsafeArray.GetPtr<DrawIndexedIndirectCommand>(_indirectDatas, (int)commandOffset + lodIndex);
						cmd->NumIndices = (uint)lod.indexCount;
						cmd->FirstIndexLocation = meshInfo.FirstIndex + (uint)lod.firstIndex;
						cmd->BaseVertex = meshInfo.BaseVertex + lod.vertexOffset;
						cmd->NumInstances = 0;
						cmd->FirstInstanceLocation = (uint)_instancesSubset.countData[batchIndex];
					}
					commandOffset += (uint)UnsafeArray.GetLength(meshInfo.LodLevels);
				}
				else
				{
					var cmd = UnsafeArray.GetPtr<DrawIndexedIndirectCommand>(_indirectDatas, (int)commandOffset);
					cmd->NumIndices = (uint)meshInfo.IndexCount;
					cmd->FirstIndexLocation = meshInfo.FirstIndex;
					cmd->BaseVertex = meshInfo.BaseVertex;
					cmd->NumInstances = 0;
					cmd->FirstInstanceLocation = (uint)_instancesSubset.countData[batchIndex];
					commandOffset++;
				}
			}
			ctx.UploadBufferExt<PerMeshData>(_meshBatchDataBuffer.Buffer, _perBatchData);
			ctx.UploadBufferExt<DrawIndexedIndirectCommand>(_indirectArgsBuffers.Buffer, _indirectDatas);

			SetAllCommandsDirty();
		}

		if (buffersRecreated)
		{
			_cullingMaterial.SetBuffer("Instances", _inputIndirectInstancesBuffer);
			_cullingMaterial.SetBuffer("OutputInstanceData", _finallyInstancesBuffer);
			_cullingMaterial.SetBuffer("InstancesDrawData", _instanceDrawDataBuffer);
			_cullingMaterial.SetBuffer("MeshBatchData", _meshBatchDataBuffer);
			_cullingMaterial.SetBuffer("IndirectCommands", _indirectArgsBuffers);
			_cullingMaterial.SetBuffer("BatchCounters", _batchCountersBuffer);

			foreach (var material in _materialObjects.Values)
			{
				material.SetBuffer("GPURenderInstances", _gpuInstancesDataBuffer, HandleAccess.Vertex);
			}

			_shadowRenderer.UpdateMaterialResources(_viewConstantsBuffer, _lightConstantsBuffer, _gpuInstancesDataBuffer);
		}
	}

	public void ClearIndirectDrawBuffers(ICommandBuffer cmd)
	{
		if (_batchCountersBuffer == null || _indirectArgsBuffers == null) return;

		cmd.UpdateBuffer<uint>(_batchCountersBuffer, _cpuBatchCounters);
		cmd.UpdateBuffer<DrawIndexedIndirectCommand>(_indirectArgsBuffers, _indirectDatas);
	}

	public CullResult ExecuteComputeCulling(ICommandBuffer cmd, int cascadeIndex = -1)
	{
		int totalInstances = _instancesSubset.instances.Length;
		if (totalInstances == 0 || _totalCommands == 0) return new CullResult();

		uint threadGroupSize = 64;
		uint threadGroupCount = (uint)((totalInstances + threadGroupSize - 1) / threadGroupSize);

		cmd.TransitionResource(_inputIndirectInstancesBuffer, ResourceState.UnorderedAccess);
		cmd.TransitionResource(_instanceDrawDataBuffer, ResourceState.UnorderedAccess);
		cmd.TransitionResource(_meshBatchDataBuffer, ResourceState.UnorderedAccess);

		if (_deviceType == RenderDeviceType.Vulkan)
		{
			cmd.TransitionResource(_indirectArgsBuffers, ResourceState.UnorderedAccess);
		}

		cmd.SetPipelineState(_cullingMaterial);
		cmd.CommitShaderResources(_cullingMaterial);
		cmd.DispatchCompute(threadGroupCount);

		return new CullResult(_finallyInstancesBuffer, _indirectArgsBuffers, _gpuInstancesDataBuffer, _materialDrawRanges);
	}

	/// <summary>См. <see cref="IBatchRenderer.SetupPunctualLights"/>: заливка пула punctual-светов
	/// кадра. Команда замороженная - указатель на UnsafeArray перечитывается на каждом реплее,
	/// память обязана быть стабильной (см. ViewSubset.punctualLights).</summary>
	public void SetupPunctualLights(ICommandBuffer cmd, UnsafeArray* lights)
	{
		if (lights == null) return;
		cmd.UpdateBuffer<PunctualLight>(_punctualLightsBuffer, lights);
	}

	/// <summary>См. <see cref="IBatchRenderer.SetupPunctualShadowMatrices"/>: заливка viewProj-матриц
	/// теневых слайсов кадра. Команда замороженная - память обязана быть стабильной
	/// (см. ViewSubset.punctualShadowMatrices).</summary>
	public void SetupPunctualShadowMatrices(ICommandBuffer cmd, UnsafeArray* matrices)
	{
		if (matrices == null) return;
		cmd.UpdateBuffer<Matrix4x4>(_punctualShadowMatricesBuffer, matrices);
	}

	/// <summary>См. <see cref="IBatchRenderer.ExecuteDrawPunctualShadow"/>: запись одного слайса
	/// теней punctual-света по последним SetupCullData/SetupLightData (CascadeMatrix0 = viewProj
	/// слайса, см. PunctualShadowScheduler).</summary>
	public void ExecuteDrawPunctualShadow(ICommandBuffer cmd, CullResult cullResult, int sliceIndex)
	{
		if (_instancesSubset.instances.Length == 0 || _totalCommands == 0)
		{
			return;
		}

		UpdateGpuMegaBuffers();
		UpdateDrawRangesCache();
		_shadowRenderer.ExecuteDrawPunctualShadow(cmd, _megaVertexBufferGPU, _megaIndexBufferGPU,
			cullResult, (uint)sliceIndex);
	}

	/// <summary>См. <see cref="IBatchRenderer.TransitionPunctualShadowsForRead"/>.</summary>
	public void TransitionPunctualShadowsForRead(ICommandBuffer cmd)
	{
		_shadowRenderer.TransitionPunctualShadowsForRead(cmd);
	}

	/// <summary>См. <see cref="IBatchRenderer.ExecuteLightClustering"/>: раскладка отрезка пула
	/// текущей камеры (границы - в LightData.ClusterParams последнего SetupLightData) по
	/// фроксел-кластерам. Пустой отрезок не повод пропускать диспатч: ClusterCounts надо
	/// занулить, иначе шейдинг прочтёт кластеры прошлой камеры.</summary>
	public void ExecuteLightClustering(ICommandBuffer cmd)
	{
		cmd.TransitionResource(_punctualLightsBuffer, ResourceState.UnorderedAccess);
		cmd.TransitionResource(_clusterCountsBuffer, ResourceState.UnorderedAccess);
		cmd.TransitionResource(_clusterIndicesBuffer, ResourceState.UnorderedAccess);

		cmd.SetPipelineState(_lightClusterMaterial);
		cmd.CommitShaderResources(_lightClusterMaterial);
		cmd.DispatchCompute((uint)((LightClusters.ClusterCount + LightClusters.CullGroupSize - 1) / LightClusters.CullGroupSize));
	}

	// --- IBatchRenderer explicit implementations (boxed CullResult behind the ICullResult marker) ---
	ICullResult IBatchRenderer.ExecuteComputeCulling(ICommandBuffer cmd, int cascadeIndex) => ExecuteComputeCulling(cmd, cascadeIndex);

	void IBatchRenderer.ExecuteDrawShadows(ICommandBuffer cmd, ICullResult cullResult, int cascadeIndex) => ExecuteDrawShadows(cmd, (CullResult)cullResult, cascadeIndex);

	void IBatchRenderer.ExecuteDrawBatching(ICommandBuffer cmd, ICullResult cullResult) => ExecuteDrawBatching(cmd, (CullResult)cullResult);

	void IBatchRenderer.ExecuteDrawBatching(ICommandBuffer cmd, ICullResult cullResult, BatchDrawFilter filter) =>
		ExecuteDrawBatching(cmd, (CullResult)cullResult, filter);

	void IBatchRenderer.ExecuteDrawPunctualShadow(ICommandBuffer cmd, ICullResult cullResult, int sliceIndex) =>
		ExecuteDrawPunctualShadow(cmd, (CullResult)cullResult, sliceIndex);

	/// <summary>См. <see cref="IBatchRenderer.SetMaterialTransparent"/>. Влияет только на выбор
	/// диапазонов в <see cref="ExecuteDrawBatching(ICommandBuffer, CullResult, BatchDrawFilter)"/> -
	/// PSO/стейты материала не трогает.</summary>
	public void SetMaterialTransparent(MaterialId materialId, bool transparent) =>
		SetMaterialTransparent(materialId.materialId, transparent);

	public void SetMaterialTransparent(int materialId, bool transparent)
	{
		bool changed = transparent
			? _transparentMaterials.Add(materialId)
			: _transparentMaterials.Remove(materialId);

		if (changed)
		{
			// Замороженные команды ForwardPass-а записаны с уже разбитыми на opaque/transparent
			// петлями дроу - изменение принадлежности материала требует перезаписи.
			SetAllCommandsDirty();
		}
	}

	/// <summary>Раньше пересоздавала и заливала ОБА мега-буфера ЦЕЛИКОМ на любую регистрацию меша -
	/// то есть открытие N-й модели во время стриминга стоило O(вся геометрия, когда-либо
	/// зарегистрированная), и по аллокации GPU-памяти, и по копированию. Теперь - суб-аллокация с
	/// запасом по хвосту (см. <see cref="UpdateMegaBufferRange{T}"/>): буфер растёт редко (x2), а
	/// обычная регистрация мешей просто доливает свой хвост в уже готовый буфер.</summary>
	private void UpdateGpuMegaBuffers()
	{
		if (!_isMeshBuffersDirty) return;

		if (_megaVertexBufferCPU.IsCreated)
		{
			// HandleAccess.Compute добавляет буферу UAV: в него ПИШЕТ скиннинг (SkinningCS.hlsl),
			// оставаясь при этом вершинным буфером для отрисовки. Флаг НЕ безусловный: он меняет
			// описание буфера ВСЕЙ геометрии сцены, а не только скиннед-мешей, и при выключенном
			// скиннинге буфер обязан создаваться ровно так же, как до появления скиннинга вообще -
			// иначе выключение перестаёт быть чистым откатом и не годится для локализации проблем.
			UpdateMegaBufferRange<Vertex>(ref _megaVertexBufferGPU, ref _megaVertexBufferCapacity,
				ref _megaVertexUploadedCount, _megaVertexBufferCPU.Count, _megaVertexBufferCPU.GetNative(),
				BufferHandleType.Vertex, SkinningUav ? HandleAccess.Compute : default, "Mega Vertex Buffer");
		}

		if (_megaIndexBufferCPU.IsCreated)
		{
			UpdateMegaBufferRange<uint>(ref _megaIndexBufferGPU, ref _megaIndexBufferCapacity,
				ref _megaIndexUploadedCount, _megaIndexBufferCPU.Count, _megaIndexBufferCPU.GetNative(),
				BufferHandleType.Index, default, "Mega Index Buffer");
		}

		_isMeshBuffersDirty = false;

		// Заливка мега-буферов идёт МИМО замороженного графа (через ImmediateContext, см.
		// UpdateMegaBufferRange) прямо перед реплеем текущего кадра, так что перезаписывать
		// SetVertexBuffers/SetIndexBuffer нужно только когда сменился САМ ОБЪЕКТ буфера (редкий путь
		// роста в UpdateMegaBufferRange). Но раньше буфер пересоздавался ВСЕГДА - на всякий случай
		// сохраняем прежнюю (более консервативную) гарантию инвалидации графа для обоих путей: цена
		// лишней перезаписи команд на порядки меньше цены лишней перезаливки мега-буфера, которую мы
		// как раз и убираем.
		SetAllCommandsDirty();
	}

	/// <summary>Суб-аллокация одного мега-буфера (вершин или индексов) с запасом по хвосту - тот же
	/// приём, что у инстанс-буферов (см. _instanceBufferSectorCapacity). <paramref name="cpuList"/>
	/// уже содержит ВСЮ накопленную геометрию (старую и новую) - Register() только аппендит в него.
	///
	/// Рост капасити (буфера ещё нет или он стал мал) - редкий путь: пересоздаёт GPU-буфер В ЗАПАС
	/// (x2) и заливает CPU-список ЦЕЛИКОМ. Старый буфер отпускается обычным Release (не разделяемая
	/// память, на которую ссылались бы замороженные команды поверх ссылки на объект - см.
	/// UpdateGpuMegaBuffers) - лишнего WaitForIdle здесь не требуется. Обычный путь (капасити хватает)
	/// заливает ТОЛЬКО новый хвост (<paramref name="uploadedCount"/>..<paramref name="currentCount"/>) -
	/// именно это убирает перезаливку всей когда-либо загруженной геометрии на каждую регистрацию.</summary>
	private unsafe void UpdateMegaBufferRange<T>(ref DiligentBufferHandle? gpuBuffer, ref int capacity,
		ref int uploadedCount, int currentCount, UnsafeList* cpuList, BufferHandleType bufferType,
		HandleAccess access, string name)
		where T : unmanaged
	{
		if (gpuBuffer == null || currentCount > capacity)
		{
			int newCapacity = Math.Max(currentCount, Math.Max(64, capacity * 2));

			gpuBuffer?.Release();
			gpuBuffer = (DiligentBufferHandle)_api.CreateBuffer<T>(newCapacity,
				new BufferInfo { name = name, type = bufferType, access = access });

			if (AnimLog)
			{
				// Путь РОСТА: буфер пересоздан и заливается ЦЕЛИКОМ. Если эта строка печатается
				// каждый кадр, значит геометрия сцены перезаливается покадрово - это сотни мегабайт
				// динамической памяти за кадр, выбранный хип, вынужденный idle GPU и, следом,
				// сорванный учёт кадров в полёте (см. ошибки валидации про командный буфер in use).
				AnimWrite($"[anim] MegaBuffer '{name}': РОСТ до {newCapacity} эл., " +
					$"полная заливка {(long)currentCount * Unsafe.SizeOf<T>() / (1024 * 1024)} МБ");
			}

			if (currentCount > 0)
			{
				_api.ImmediateContext.UpdateBuffer(gpuBuffer.Buffer, 0, (uint)(currentCount * Unsafe.SizeOf<T>()),
					new IntPtr(UnsafeList.GetPtr<T>(cpuList, 0)), ResourceStateTransitionMode.Transition);
			}

			capacity = newCapacity;
			uploadedCount = currentCount;
		}
		else if (currentCount > uploadedCount)
		{
			if (AnimLog)
			{
				AnimWrite($"[anim] MegaBuffer '{name}': доливка хвоста {currentCount - uploadedCount} эл.");
			}

			_api.ImmediateContext.UpdateBuffer(gpuBuffer.Buffer, (uint)(uploadedCount * Unsafe.SizeOf<T>()),
				(uint)((currentCount - uploadedCount) * Unsafe.SizeOf<T>()),
				new IntPtr(UnsafeList.GetPtr<T>(cpuList, uploadedCount)), ResourceStateTransitionMode.Transition);
			uploadedCount = currentCount;
		}
		else
		{
			return;
		}

		// ResourceStateTracker - внутренняя кухня бэкенда, работает в нативных состояниях Diligent.
		_stateTracker.SetState(gpuBuffer.Buffer, global::Diligent.ResourceState.CopyDest);
	}

	private void UpdateDrawRangesCache()
	{
		if (!_isDrawRangesCacheDirty)
		{
			return;
		}
		_materialDrawRanges.Clear();
		if (_indirectBatches.Count == 0)
		{
			_isDrawRangesCacheDirty = false; return;
		}

		var sortedPairs = GetSortedBatches();
		if (sortedPairs.Count == 0)
		{
			_isDrawRangesCacheDirty = false; return;
		}
		
		uint currentCommand = 0;
		MaterialId currentMaterialId = sortedPairs[0].Value.material;
		uint firstDrawIndex = 0;

		for (int i = 0; i < sortedPairs.Count; i++)
		{
			var batch = sortedPairs[i].Value;
			var meshInfo = _meshInfos[batch.mesh.meshId];
			uint lodCount = (meshInfo.LodLevels != null && UnsafeArray.GetLength(meshInfo.LodLevels) > 0) ? (uint)UnsafeArray.GetLength(meshInfo.LodLevels) : 1;

			if (batch.material.materialId != currentMaterialId.materialId)
			{
				_materialDrawRanges.Add(currentMaterialId.materialId, new MaterialDrawRange { FirstDrawIndex = firstDrawIndex, DrawCount = currentCommand - firstDrawIndex });
				currentMaterialId = batch.material;
				firstDrawIndex = currentCommand;
			}
			currentCommand += lodCount;
		}

		_materialDrawRanges.Add(currentMaterialId.materialId, new MaterialDrawRange { FirstDrawIndex = firstDrawIndex, DrawCount = currentCommand - firstDrawIndex });
		_isDrawRangesCacheDirty = false;
		SetAllCommandsDirty();

#if DEBUG
		// Временная диагностика (см. PreviewProbe): раскладка индирект-диапазонов по материалам.
		if (Environment.GetEnvironmentVariable("DECA_BATCH_DEBUG") == "1")
		{
			foreach (var kvp in _materialDrawRanges)
			{
				Console.WriteLine($"[batch-debug] range mat={kvp.Key} first={kvp.Value.FirstDrawIndex} count={kvp.Value.DrawCount}");
			}
		}
#endif
	}

	public void ExecuteDrawShadows(ICommandBuffer cmd, CullResult cullResult, int cascadeIndex)
	{
		if (_instancesSubset.instances.Length == 0 || _totalCommands == 0)
		{
			return;
		}

		UpdateGpuMegaBuffers();
		UpdateDrawRangesCache();
		_shadowRenderer.ExecuteDrawShadows(cmd, _megaVertexBufferGPU, _megaIndexBufferGPU, cullResult, (uint)cascadeIndex);
	}

	public void ExecuteDrawBatching(ICommandBuffer cmd, CullResult cullResult) =>
		ExecuteDrawBatching(cmd, cullResult, BatchDrawFilter.All);

	public void ExecuteDrawBatching(ICommandBuffer cmd, CullResult cullResult, BatchDrawFilter filter)
	{
		if (_instancesSubset.instances.Length == 0 || _totalCommands == 0) return;

		UpdateGpuMegaBuffers();
		UpdateDrawRangesCache();

		// DepthRead, а не ShaderResource - см. комментарий в ShadowRenderer.ExecuteDrawShadows.
		cmd.TransitionResource(_shadowRenderer.ShadowMapsTarget, ResourceState.DepthRead);

		// VertexBuffer/IndirectArgument переходы не нужны: их вставляют сами SetVertexBuffers и
		// DrawIndexedIndirect ниже. Инстанс-данные же читает вершинный шейдер через SRB.
		cmd.TransitionResource(cullResult.FinallyInstancesBuffer, ResourceState.VertexBuffer);
		cmd.TransitionResource(cullResult.GpuInstancesDataBuffer, ResourceState.ShaderResource);
		cmd.TransitionResource(cullResult.IndirectArgsBuffers, ResourceState.IndirectArgument);

		// Результаты кластеризации светов (UAV после ExecuteLightClustering) - на чтение пиксельным
		// шейдером. Переход здесь, а не в кластеризации: сюда приходит КАЖДЫЙ рисующий путь,
		// включая превью без кластеризации (тогда буферы просто остаются нулевыми).
		cmd.TransitionResource(_punctualLightsBuffer, ResourceState.ShaderResource);
		cmd.TransitionResource(_clusterCountsBuffer, ResourceState.ShaderResource);
		cmd.TransitionResource(_clusterIndicesBuffer, ResourceState.ShaderResource);
		cmd.TransitionResource(_punctualShadowMatricesBuffer, ResourceState.ShaderResource);

		cmd.SetVertexBuffers(0, [_megaVertexBufferGPU, cullResult.FinallyInstancesBuffer], [0ul, 0ul], SetVertexBuffersFlags.Reset);
		cmd.SetIndexBuffer(_megaIndexBufferGPU, 0);

		int drawnRanges = 0;
		foreach (var kvp in cullResult.MaterialDrawRanges)
		{
			var materialId = kvp.Key;
			var drawRange = kvp.Value;
			if (drawRange.DrawCount == 0) continue;

			if (filter != BatchDrawFilter.All)
			{
				bool transparent = _transparentMaterials.Contains(materialId);
				if (transparent != (filter == BatchDrawFilter.TransparentOnly)) continue;
			}

			var material = _materialObjects[materialId];
			cmd.SetPipelineState(material);
			cmd.CommitShaderResources(material);
			cmd.DrawIndexedIndirect(cullResult.IndirectArgsBuffers, drawRange, IndexType.UInt32);
			drawnRanges++;
		}

		_batchRendererInfo.pipelineStateCount = drawnRanges;
	}

	public IStateObject GetBaseState()
	{
		return _api.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "Instancing PSO",
			RenderTargetFormats = GeometryTargetFormats,
			DepthStencilFormat = _api.SwapChainDepthFormat,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.Back },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = true, DepthFunc = ComparisonFunctionType.Greater },
			InputLayout =
			[
				new InputLayoutElementInfo { InputIndex = 0, NumComponents = 3, ValueType = InputElementValueType.Float32, IsNormalized = false },
				new InputLayoutElementInfo { InputIndex = 1, NumComponents = 2, ValueType = InputElementValueType.Float32, IsNormalized = false },
				new InputLayoutElementInfo { InputIndex = 2, NumComponents = 3, ValueType = InputElementValueType.Float32, IsNormalized = false },
				new InputLayoutElementInfo { InputIndex = 4, NumComponents = 4, ValueType = InputElementValueType.Float32, IsNormalized = false },
				new InputLayoutElementInfo { InputIndex = 5, NumComponents = 4, ValueType = InputElementValueType.Float32, IsNormalized = false },
				// TEXCOORD_1 (Vertex.TexCoord1) - идёт последним в структуре вершины, поэтому и в
				// объявлении слота 0 последний: Diligent пакует авто-оффсеты по порядку элементов.
				new InputLayoutElementInfo { InputIndex = 6, NumComponents = 2, ValueType = InputElementValueType.Float32, IsNormalized = false },
				new InputLayoutElementInfo { InputIndex = 3, BufferSlot = 1, NumComponents = 1, ValueType = InputElementValueType.Int32, IsNormalized = false, Frequency = InputElementFrequencyType.PerInstance }
			]
		});
	}

	/// <summary>Вариант <see cref="GetBaseState"/> с другой примитивной топологией (точки/линии
	/// glTF-примитивов, см. ModelLoader.MeshTopology*) и без backface culling - у точек и линий
	/// нет лицевой стороны.</summary>
	public IStateObject GetTopologyState(PrimitiveTopologyType topology)
	{
		return _api.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = $"Instancing PSO ({topology})",
			RenderTargetFormats = GeometryTargetFormats,
			DepthStencilFormat = _api.SwapChainDepthFormat,
			PrimitiveTopology = topology,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = true, DepthFunc = ComparisonFunctionType.Greater },
			InputLayout =
			[
				new InputLayoutElementInfo { InputIndex = 0, NumComponents = 3, ValueType = InputElementValueType.Float32, IsNormalized = false },
				new InputLayoutElementInfo { InputIndex = 1, NumComponents = 2, ValueType = InputElementValueType.Float32, IsNormalized = false },
				new InputLayoutElementInfo { InputIndex = 2, NumComponents = 3, ValueType = InputElementValueType.Float32, IsNormalized = false },
				new InputLayoutElementInfo { InputIndex = 4, NumComponents = 4, ValueType = InputElementValueType.Float32, IsNormalized = false },
				new InputLayoutElementInfo { InputIndex = 5, NumComponents = 4, ValueType = InputElementValueType.Float32, IsNormalized = false },
				// TEXCOORD_1 (Vertex.TexCoord1) - идёт последним в структуре вершины, поэтому и в
				// объявлении слота 0 последний: Diligent пакует авто-оффсеты по порядку элементов.
				new InputLayoutElementInfo { InputIndex = 6, NumComponents = 2, ValueType = InputElementValueType.Float32, IsNormalized = false },
				new InputLayoutElementInfo { InputIndex = 3, BufferSlot = 1, NumComponents = 1, ValueType = InputElementValueType.Int32, IsNormalized = false, Frequency = InputElementFrequencyType.PerInstance }
			]
		});
	}

	/// <summary>
	/// Same layout as <see cref="GetBaseState"/>, but rasterized as wireframe with no backface
	/// culling and a GreaterEqual depth test - used by <see cref="DecaEngine.Editor.ModelPreviewViewport"/>'s
	/// "Highlight + Wireframe" mode to draw an edge overlay on top of an already-drawn, depth-coincident
	/// solid pass of the same geometry (GreaterEqual lets the overlay pass depth-test equal to what the
	/// solid pass already wrote, instead of failing/z-fighting against it).
	/// </summary>
	public IStateObject GetWireframeState()
	{
		return _api.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "Wireframe Overlay PSO",
			RenderTargetFormats = GeometryTargetFormats,
			DepthStencilFormat = _api.SwapChainDepthFormat,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None, FillMode = FillModeType.Wireframe },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = true, DepthFunc = ComparisonFunctionType.GreaterEqual },
			InputLayout =
			[
				new InputLayoutElementInfo { InputIndex = 0, NumComponents = 3, ValueType = InputElementValueType.Float32, IsNormalized = false },
				new InputLayoutElementInfo { InputIndex = 1, NumComponents = 2, ValueType = InputElementValueType.Float32, IsNormalized = false },
				new InputLayoutElementInfo { InputIndex = 2, NumComponents = 3, ValueType = InputElementValueType.Float32, IsNormalized = false },
				new InputLayoutElementInfo { InputIndex = 4, NumComponents = 4, ValueType = InputElementValueType.Float32, IsNormalized = false },
				new InputLayoutElementInfo { InputIndex = 5, NumComponents = 4, ValueType = InputElementValueType.Float32, IsNormalized = false },
				// TEXCOORD_1 (Vertex.TexCoord1) - идёт последним в структуре вершины, поэтому и в
				// объявлении слота 0 последний: Diligent пакует авто-оффсеты по порядку элементов.
				new InputLayoutElementInfo { InputIndex = 6, NumComponents = 2, ValueType = InputElementValueType.Float32, IsNormalized = false },
				new InputLayoutElementInfo { InputIndex = 3, BufferSlot = 1, NumComponents = 1, ValueType = InputElementValueType.Int32, IsNormalized = false, Frequency = InputElementFrequencyType.PerInstance }
			]
		});
	}

	public void Release()
	{
		_cullConstantsBuffer?.Release();
		_viewConstantsBuffer?.Release();
		_lightConstantsBuffer?.Release();
		_inputIndirectInstancesBuffer?.Release();
		_instanceDrawDataBuffer?.Release();
		_finallyInstancesBuffer?.Release();
		_batchCountersBuffer?.Release();
		_indirectArgsBuffers?.Release();
		_megaVertexBufferGPU?.Release();
		_megaIndexBufferGPU?.Release();
		_punctualLightsBuffer?.Release();
		_clusterCountsBuffer?.Release();
		_clusterIndicesBuffer?.Release();
		_punctualShadowMatricesBuffer?.Release();
		_shadowRenderer?.Release();
		_cullingMaterial?.Release();
		_lightClusterMaterial?.Release();
		UnsafeArray.Free(_indirectDatas);
		UnsafeArray.Free(_cpuBatchCounters);
	}
}