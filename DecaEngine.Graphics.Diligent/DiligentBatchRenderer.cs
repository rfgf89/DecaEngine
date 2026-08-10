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
	}

	public readonly OrderedDictionary<int, MaterialDrawRange> _materialDrawRanges = new();
	private bool _isDrawRangesCacheDirty = true;

	private NativeList<Vertex> _megaVertexBufferCPU;
	private NativeList<uint> _megaIndexBufferCPU;
	private readonly OrderedDictionary<int, MeshInfo> _meshInfos = new();
	private bool _isMeshBuffersDirty = true;

	private DiligentBufferHandle? _megaVertexBufferGPU;
	private DiligentBufferHandle? _megaIndexBufferGPU;

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

	private readonly uint _sampleCount = 1;

	private readonly DiligentBufferHandle? _lightConstantsBuffer;
	private readonly DiligentBufferHandle? _viewConstantsBuffer;
	private readonly DiligentBufferHandle? _cullConstantsBuffer;
	private readonly IComputeMaterial _cullingMaterial;

	private readonly int _instanceBufferSectorCapacity = 64;
	private int _instanceBufferCapacity = 0;
	private int _meshBatchDataCapacity = 0;
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

	/// <param name="sampleCount">MSAA sample count целевых таргетов - пекётся во все PSO
	/// (GetBaseState/GetTopologyState/GetWireframeState); 1 = без MSAA (главная сцена).</param>
	public DiligentBatchRenderer(DiligentGraphicsApi api, uint sampleCount = 1)
	{
		_perMeshData = UnsafeList.Allocate<PerMeshData>(32);
		_api = api;
		_sampleCount = Math.Max(1u, sampleCount);
		_deviceType = _api.Device.GetDeviceInfo().Type;
		
		_cullConstantsBuffer = CreateConstantsBuffer<CullData>("Cull Constants");
		_viewConstantsBuffer = CreateConstantsBuffer<ViewData>("View Constants");
		_lightConstantsBuffer = CreateConstantsBuffer<LightData>("Light Constants");

		var cullingShader = new DiligentShader(_api, "Batching Instancing CS", "EditorAssets/shader", "BatchingInstancingCS.hlsl", ShaderObjectType.Compute, "CSMain");
		_cullingMaterial = new DiligentComputeMaterial("Batching Instancing CS", _api);
		_cullingMaterial.SetShader(cullingShader);
		_cullingMaterial.SetBuffer("Constants", _cullConstantsBuffer);

		_shadowRenderer = new ShadowRenderer(api);
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

		for (int i = 0; i < UnsafeArray.GetLength(vertices); i++)
			_megaVertexBufferCPU.Add(UnsafeArray.Get<Vertex>(vertices, i));

		for (int i = 0; i < UnsafeArray.GetLength(indices); i++)
			_megaIndexBufferCPU.Add(UnsafeArray.Get<uint>(indices, i));

		_meshInfos[meshId] = new MeshInfo
		{
			IndexCount = UnsafeArray.GetLength(indices),
			FirstIndex = firstIndex,
			BaseVertex = baseVertex,
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

	public MaterialId Register(IMaterialObject materialObject)
	{
		var materialId = gMaterialIndex;
		var material = (DiligentMaterial)materialObject;
		_materialObjects[materialId] = material;

		material.SetBuffer("View", _viewConstantsBuffer, HandleAccess.Vertex | HandleAccess.Pixel);
		material.SetBuffer("Light", _lightConstantsBuffer, HandleAccess.Vertex | HandleAccess.Pixel);

		_shadowRenderer.SetShadowResources(materialObject);

		material.SetBuffer("GPURenderInstances", _gpuInstancesDataBuffer, HandleAccess.Vertex);

		gMaterialIndex++;
		_isDrawBatchCmdDirty = true;
		return new MaterialId(materialId);
	}

	public BatchId CreateBatch(MeshId meshId, MaterialId materialId)
	{
		var batchIndex = gBatchIndex;
		_indirectBatches.Add(batchIndex, new IndirectBatch(meshId, materialId));
		_isDrawRangesCacheDirty = true;
		SetAllCommandsDirty();
		gBatchIndex++;
		return new BatchId(batchIndex);
	}

	public void Remove(int batchId)
	{
		_isDrawRangesCacheDirty = true;
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

	public unsafe void SetupViewData(ICommandBuffer? cmd, ref ViewData viewData)
	{
		fixed(ViewData* ptr = &viewData)
		{
			cmd.UpdateBuffer(_viewConstantsBuffer, 0, ptr);
		}
	}

	public unsafe void SetupCullData(ICommandBuffer? cmd, ref CullData cullData)
	{
		fixed (CullData* ptr = &cullData)
		{
			cmd.UpdateBuffer(_cullConstantsBuffer, 0, ptr);
		}
	}

	public unsafe void SetupLightData(ICommandBuffer? cmd, ref LightData lightData)
	{
		fixed (LightData* ptr = &lightData)
		{
			cmd.UpdateBuffer(_lightConstantsBuffer, 0, ptr);
		}
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

		int batchCount = _indirectBatches.Count;
		if (_batchCountersBuffer == null || (batchCount != (int)_batchCountersBuffer.Buffer.GetDesc().Size / sizeof(uint)))
		{
			CreateStructuredBuffer<uint>(ref _batchCountersBuffer, batchCount, "Batch Counters Buffer");

			UnsafeArray.Resize<uint>(ref _cpuBatchCounters, batchCount);
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
			CreateIndirectBuffer(ref _indirectArgsBuffers, _totalCommands);
			CreateStructuredBuffer<PerMeshData>(ref _meshBatchDataBuffer, _meshBatchDataCapacity, "Mesh Batch Data Buffer");

			UnsafeArray.Resize<DrawIndexedIndirectCommand>(ref _indirectDatas, _totalCommands);
			UnsafeArray.Resize<PerMeshData>(ref _perBatchData, _meshBatchDataCapacity);
		}

		if (needsInstanceUpload)
		{
			IDeviceContext ctx = _api.ImmediateContext;
			ctx.UploadBufferExt<IndirectInstance>(_inputIndirectInstancesBuffer.Buffer, _instancesSubset.instances.GetNative());
			ctx.UploadBufferExt<DrawData>(_instanceDrawDataBuffer.Buffer, _instancesSubset.drawData.GetNative());
			ctx.UploadBufferExt<GPURenderInstance>(_gpuInstancesDataBuffer.Buffer, _instancesSubset.gpuData.GetNative());

			var sortedBatches = _indirectBatches.OrderBy(p => p.Value.material.materialId).ToList();
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

	// --- IBatchRenderer explicit implementations (boxed CullResult behind the ICullResult marker) ---
	ICullResult IBatchRenderer.ExecuteComputeCulling(ICommandBuffer cmd, int cascadeIndex) => ExecuteComputeCulling(cmd, cascadeIndex);

	void IBatchRenderer.ExecuteDrawShadows(ICommandBuffer cmd, ICullResult cullResult, int cascadeIndex) => ExecuteDrawShadows(cmd, (CullResult)cullResult, cascadeIndex);

	void IBatchRenderer.ExecuteDrawBatching(ICommandBuffer cmd, ICullResult cullResult) => ExecuteDrawBatching(cmd, (CullResult)cullResult);

	void IBatchRenderer.ExecuteDrawBatching(ICommandBuffer cmd, ICullResult cullResult, BatchDrawFilter filter) =>
		ExecuteDrawBatching(cmd, (CullResult)cullResult, filter);

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

	private void UpdateGpuMegaBuffers()
	{
		if (!_isMeshBuffersDirty) return;

		_megaVertexBufferGPU?.Release();
		_megaIndexBufferGPU?.Release();

		_megaVertexBufferGPU = (DiligentBufferHandle)_api.CreateBuffer<Vertex>(_megaVertexBufferCPU.Count, new BufferInfo { name = "Mega Vertex Buffer", type = BufferHandleType.Vertex });
		_megaIndexBufferGPU = (DiligentBufferHandle)_api.CreateBuffer<uint>(_megaIndexBufferCPU.Count, new BufferInfo { name = "Mega Index Buffer", type = BufferHandleType.Index });

		_api.ImmediateContext.UploadBufferExt<Vertex>(_megaVertexBufferGPU.Buffer, _megaVertexBufferCPU.GetNative());
		_api.ImmediateContext.UploadBufferExt<uint>(_megaIndexBufferGPU.Buffer, _megaIndexBufferCPU.GetNative());

		// ResourceStateTracker - внутренняя кухня бэкенда, работает в нативных состояниях Diligent.
		_stateTracker.SetState(_megaVertexBufferGPU.Buffer, global::Diligent.ResourceState.CopyDest);
		_stateTracker.SetState(_megaIndexBufferGPU.Buffer, global::Diligent.ResourceState.CopyDest);

		_isMeshBuffersDirty = false;
		SetAllCommandsDirty();
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

		var sortedPairs = _indirectBatches.OrderBy(p => p.Value.material.materialId).ToList();
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
			RenderTargetFormats = [_api.SwapChainColorFormat],
			DepthStencilFormat = _api.SwapChainDepthFormat,
			SampleCount = _sampleCount,
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
			RenderTargetFormats = [_api.SwapChainColorFormat],
			DepthStencilFormat = _api.SwapChainDepthFormat,
			SampleCount = _sampleCount,
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
			RenderTargetFormats = [_api.SwapChainColorFormat],
			DepthStencilFormat = _api.SwapChainDepthFormat,
			SampleCount = _sampleCount,
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
		_shadowRenderer?.Release();
		_cullingMaterial?.Release();
		UnsafeArray.Free(_indirectDatas);
		UnsafeArray.Free(_cpuBatchCounters);
	}
}