using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DecaEngine.Core;
using DecaEngine.Graphics.Diligent.RenderGraph;
using Diligent;
using UnsafeCollections.Collections.Native;
using UnsafeCollections.Collections.Unsafe;
using ResourceState = DecaEngine.Graphics.ResourceState;
using SetVertexBuffersFlags = DecaEngine.Graphics.SetVertexBuffersFlags;
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

		// Mesh vertex count in the mega-buffer from BaseVertex; skinning sizes per-instance copies from it.
		public int VertexCount;
	}

	public readonly OrderedDictionary<int, MaterialDrawRange> _materialDrawRanges = new();
	private bool _isDrawRangesCacheDirty = true;

	// Batches sorted by materialId, cached: recomputing per frame allocated and re-sorted needlessly.
	private readonly List<KeyValuePair<int, IndirectBatch>> _sortedBatchesCache = new();
	private bool _isSortedBatchesCacheDirty = true;

	private NativeList<Vertex> _megaVertexBufferCPU;
	private NativeList<uint> _megaIndexBufferCPU;
	private readonly OrderedDictionary<int, MeshInfo> _meshInfos = new();
	private bool _isMeshBuffersDirty = true;

	private DiligentBufferHandle? _megaVertexBufferGPU;
	private DiligentBufferHandle? _megaIndexBufferGPU;

	// GPU capacity vs uploaded count: tail sub-allocation (grow x2 rarely, upload only the new
	// tail) avoids re-uploading the whole mega-buffer on every mesh registration.
	private int _megaVertexBufferCapacity;
	private int _megaIndexBufferCapacity;
	private int _megaVertexUploadedCount;
	private int _megaIndexUploadedCount;

	private readonly OrderedDictionary<int, DiligentMaterial> _materialObjects = new();
	private readonly OrderedDictionary<int, IndirectBatch> _indirectBatches = new();

	private BatchSubset _instancesSubset;

	private readonly UnsafeList* _perMeshData;

	// Per-BATCH snapshot of PerMeshData for the GPU: the culling shader (BatchingInstancingCS.hlsl)
	// indexes MeshBatchData by batchId, while _perMeshData is indexed by meshId - the two diverge
	// once a scene is repopulated, so uploading _perMeshData as-is reads wrong offsets/bounds.
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

	// Materials drawn in the separate TransparentOnly loop after the color-target copy (ForwardPass).
	private readonly HashSet<int> _transparentMaterials = new();



	// Geometry color-target format baked into all PSOs; Unknown = use the swap-chain format.
	private readonly TextureObjectFormat _colorFormat = TextureObjectFormat.Unknown;

	private TextureObjectFormat RenderColorFormat =>
		_colorFormat != TextureObjectFormat.Unknown ? _colorFormat : _api.SwapChainColorFormat;

	private readonly bool _reflectionGbuffer;

	/// <summary>Whether geometry PSOs carry the reflection G-buffer MRT slots (see constructor).</summary>
	public bool ReflectionGbuffer => _reflectionGbuffer;

	private TextureObjectFormat[] GeometryTargetFormats => _reflectionGbuffer
		? [RenderColorFormat, TextureObjectFormat.R16G16B16A16Float, TextureObjectFormat.R16G16B16A16Float]
		: [RenderColorFormat];

	private readonly DiligentBufferHandle? _lightConstantsBuffer;
	private readonly DiligentBufferHandle? _viewConstantsBuffer;
	private readonly DiligentBufferHandle? _cullConstantsBuffer;
	private readonly IComputeMaterial _cullingMaterial;

	// Punctual light clustering (LightClusterCS.hlsl): fixed-size froxel buffers; compute writes
	// UAV, batch-material pixel shaders read SRV.
	private readonly DiligentBufferHandle? _punctualLightsBuffer;
	private readonly DiligentBufferHandle? _clusterCountsBuffer;
	private readonly DiligentBufferHandle? _clusterIndicesBuffer;
	private readonly IComputeMaterial _lightClusterMaterial;

	// Punctual shadow slice viewProj matrices (SRV); uploaded once per frame from stable memory
	// because the frozen command re-reads the pointer on every replay.
	private readonly DiligentBufferHandle? _punctualShadowMatricesBuffer;

	private readonly int _instanceBufferSectorCapacity = 64;
	private int _instanceBufferCapacity = 0;
	private int _meshBatchDataCapacity = 0;

	// DECA_ANIM_LOG=1: verbose skinning-path log; buffer hash codes prove whether a buffer was
	// recreated underneath already-recorded graph commands.
	private static readonly bool AnimLog = Environment.GetEnvironmentVariable("DECA_ANIM_LOG") == "1";

	/// <summary>Adds a UAV to the mega vertex buffer (compute skinning only); with skinning off
	/// the buffer must be created byte-identically to the pre-skinning layout.</summary>
	public static bool SkinningUav { get; set; } = true;

	// Writes to console AND a flushed file: native crashes kill the process before buffered
	// console output reaches the last (most important) lines.
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
			// A busy log file must not take the frame down.
		}
	}

	private static string Id(DiligentBufferHandle? buffer) =>
		buffer == null ? "null" : $"#{buffer.GetHashCode():X}";

	// Batch counters capacity: grow-only with slack (exact sizing recreates buffers under
	// recorded commands, see CheckAndReallocateBuffers).
	private int _batchCountersCapacity = 0;

	private int _totalCommands = 0;

	// Must be INSTANCE fields: each renderer's registries are indexed from 0, so static counters
	// would make a second renderer hand out ids past the end of its own _perMeshData.
	private int gMeshIndex;
	private int gMaterialIndex;
	private int gBatchIndex;

	private BatchRendererInfo _batchRendererInfo;

	private readonly ResourceStateTracker _stateTracker = new();
	public ResourceStateTracker StateTracker => _stateTracker;

	private bool _isDrawBatchCmdDirty = true;

	// Set when instances change on an EXISTING batch: without it, instance data and indirect
	// offsets are only re-uploaded when a buffer is recreated, leaving stale instances live.
	private bool _instancesContentDirty = true;
	public void MarkInstancesContentDirty() => _instancesContentDirty = true;

	public bool IsDirty => _isDrawBatchCmdDirty;
	public void ClearDirty() => _isDrawBatchCmdDirty = false;

	public int ShadowCascadeCount => ShadowRenderer.MaxCascades;

	/// <summary>Cascaded sun-shadow renderer; exposed only for debug shadow-map readback.</summary>
	public ShadowRenderer WorldShadowRenderer => _shadowRenderer;

	/// <summary>reflectionGbuffer must match whether ForwardPass binds those targets: on Vulkan a
	/// PSO whose attachment count differs from the bound set breaks the render pass.</summary>
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

		// Element stride is Vector4, NOT Matrix4x4: matrix majorness in structured-buffer elements
		// ignores PackMatrixRowMajor and differs between D3D12 and Vulkan; the shader reads four
		// row-major rows instead (UnlitInstancedPS.hlsl::LoadPunctualShadowMatrix).
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

	/// <summary>Scene GPU skinning; lives here because it writes into the renderer-owned mega vertex buffer.</summary>
	public DiligentSkinningPass Skinning { get; }

	/// <summary>Snapshot of the counters whose growth forces native indirect-array reallocation.</summary>
	public string DiagCounters =>
		$"meshes={gMeshIndex} batches={_indirectBatches.Count} instances={_instancesSubset.instances.Length} " +
		$"commands={_totalCommands} megaVerts={(_megaVertexBufferCPU.IsCreated ? _megaVertexBufferCPU.Count : 0)} " +
		$"instCap={_instanceBufferCapacity} meshBatchCap={_meshBatchDataCapacity}";

	/// <summary>Dispatches skinning; must run BEFORE frame command recording - mega-buffer growth
	/// recreates the buffer that already-recorded commands reference.</summary>
	public void ExecuteSkinning()
	{
		if (!Skinning.HasWork)
		{
			return;
		}

		// Mega-buffer must be uploaded before dispatch: the skinned destination region does not
		// exist in the GPU buffer until then.
		var megaBefore = _megaVertexBufferGPU;
		UpdateGpuMegaBuffers();

		if (AnimLog)
		{
			AnimWrite($"[anim] ExecuteSkinning: mega {Id(megaBefore)}" +
				(ReferenceEquals(megaBefore, _megaVertexBufferGPU) ? "" : $" -> {Id(_megaVertexBufferGPU)} RECREATED") +
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

		// Block copy, not per-element Add: Sponza-scale scenes mean tens of millions of Adds.
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

	/// <summary>Registers a per-INSTANCE skinned copy of a mesh region in the mega vertex buffer;
	/// shares indices/LODs with the source (only baseVertex changes). The copy is seeded with the
	/// bind pose - a zeroed region would flash collapsed geometry for a frame.</summary>
	public (MeshId Mesh, int PaletteOffset) RegisterSkinnedInstance(MeshId sourceMesh, int jointCount, int skinBase)
	{
		var source = _meshInfos[sourceMesh.meshId];
		int destBaseVertex = _megaVertexBufferCPU.Count;

		if (source.VertexCount > 0)
		{
			// Copy within one list: AddRange from a pointer into the same list breaks on growth,
			// so the bind pose is staged through a temp array.
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

		// PerMeshData must exist for EVERY registered mesh; a gap would shift the whole table.
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
				$"after: {DiagCounters}");
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

	// Sorted by materialId with batch-index tie-break: must reproduce stable OrderBy order.
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

	/// <summary>Drops ALL mesh/material/batch registrations and geometry. Caller MUST first wait
	/// for the GPU (Flush + WaitForIdle) and rebuild the graph: old ids become invalid while
	/// frozen commands still reference them.</summary>
	public void ResetRegistrations()
	{
		// Dispose, not Clear: Clear would keep the capacity this reset exists to reclaim.
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

		_megaVertexBufferGPU?.Release();
		_megaVertexBufferGPU = null;
		_megaIndexBufferGPU?.Release();
		_megaIndexBufferGPU = null;

		// Skinning regions hold offsets into a mega-buffer that no longer exists.
		Skinning.Reset();

		_batchCountersCapacity = 0;

		_megaVertexBufferCapacity = 0;
		_megaIndexBufferCapacity = 0;
		_megaVertexUploadedCount = 0;
		_megaIndexUploadedCount = 0;

		// Reset must be TOTAL: any non-empty collection desyncs its keys from re-issued ids.
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

	/// <summary>Evicts ONE model. Ids are dense and never reused, so removals shift nothing; mesh
	/// geometry stays (unreferenced) in the mega-buffer. Caller MUST remove instance entities
	/// referencing the batchIds BEFORE this call, or stale instances index out of bounds.</summary>
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

	/// <summary>Binds the shared View cbuffer to a material that is not a registered batch material.</summary>
	public void BindViewConstants(IMaterialObject material)
	{
		((DiligentMaterial)material).SetBuffer("View", _viewConstantsBuffer, HandleAccess.Vertex | HandleAccess.Pixel);
	}

	/// <summary>See <see cref="IBatchRenderer.BindShadowResources"/>.</summary>
	public void BindShadowResources(IMaterialObject material)
	{
		var diligentMaterial = (DiligentMaterial)material;
		diligentMaterial.SetBuffer("Light", _lightConstantsBuffer, HandleAccess.Vertex | HandleAccess.Pixel);
		_shadowRenderer.SetShadowResources(material);

		// Shaders lacking these declarations ignore the bindings.
		diligentMaterial.SetBuffer("PunctualLights", _punctualLightsBuffer, HandleAccess.Pixel);
		diligentMaterial.SetBuffer("PunctualShadowMatrices", _punctualShadowMatricesBuffer, HandleAccess.Pixel);
	}

	/// <summary>See <see cref="IBatchRenderer.SetMaterialAlphaTestedShadow"/>.</summary>
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

	/// <summary>See <see cref="IBatchRenderer.SetMaterialShadowCasting"/>.</summary>
	public void SetMaterialShadowCasting(int materialId, bool casts) =>
		_shadowRenderer.SetMaterialShadowCasting(materialId, casts);

	/// <summary>DepthRead, not ShaderResource - see ShadowRenderer.ExecuteDrawShadows.</summary>
	public void TransitionShadowMapsForRead(ICommandBuffer cmd)
	{
		cmd.TransitionResource(_shadowRenderer.ShadowMapsTarget, ResourceState.DepthRead);
	}

	// Explicit CopyDest -> ConstantBuffer barrier after each UpdateBuffer: without it D3D12 may
	// coalesce several cbuffer updates before the draws execute (shadow cascades got each other's
	// matrices); Vulkan happened to forgive it.

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

		// Grow-only with 2x slack: exact sizing recreated the indirect-args buffer and
		// _indirectDatas under already-recorded commands. Slack is safe here (extra counters are
		// zero and unaddressed); the indirect-command buffer itself must NOT have slack (below).
		int batchCount = _indirectBatches.Count;
		if (_batchCountersBuffer == null || batchCount > _batchCountersCapacity)
		{
			_batchCountersCapacity = Math.Max(batchCount, Math.Max(64, _batchCountersCapacity * 2));

			CreateStructuredBuffer<uint>(ref _batchCountersBuffer, _batchCountersCapacity, "Batch Counters Buffer");
			UnsafeArray.Resize<uint>(ref _cpuBatchCounters, _batchCountersCapacity);
			buffersRecreated = true;
		}

		// _meshBatchDataBuffer is indexed by BATCH id on the GPU, so it must cover every id ever
		// handed out: ids are dense 0..gBatchIndex-1, so gBatchIndex is the required length.
		int batchTotal = gBatchIndex;
		if (batchTotal > _meshBatchDataCapacity)
		{
			_meshBatchDataCapacity = batchTotal;
			buffersRecreated = true;
		}

		// _indirectDatas must be EXACTLY _totalCommands long (upload loop relies on it); exact
		// inequality is deliberate - the indirect-command buffer is uploaded whole, no slack.
		int commandCapacity = _indirectDatas == null ? 0 : UnsafeArray.GetLength(_indirectDatas);
		if (commandCapacity != _totalCommands)
		{
			buffersRecreated = true;
		}

		bool needsInstanceUpload = buffersRecreated || _instancesContentDirty;
		_instancesContentDirty = false;

		if (buffersRecreated)
		{
			// Exactly _totalCommands, no slack: the array is uploaded WHOLE, so an uninitialized
			// tail would become garbage draw commands.
			CreateIndirectBuffer(ref _indirectArgsBuffers, _totalCommands);
			UnsafeArray.Resize<DrawIndexedIndirectCommand>(ref _indirectDatas, _totalCommands);

			CreateStructuredBuffer<PerMeshData>(ref _meshBatchDataBuffer, _meshBatchDataCapacity, "Mesh Batch Data Buffer");
			UnsafeArray.Resize<PerMeshData>(ref _perBatchData, _meshBatchDataCapacity);
		}

		if (AnimLog && buffersRecreated)
		{
			AnimWrite($"[anim] CheckAndReallocateBuffers: buffers RECREATED -> " +
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

				// Per-batch copy: writing the offset into shared _perMeshData[meshId] lets two
				// batches of one mesh clobber each other's physicalCommandOffset.
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

	/// <summary>Frozen command re-reads the pointer on each replay - memory must be stable.</summary>
	public void SetupPunctualLights(ICommandBuffer cmd, UnsafeArray* lights)
	{
		if (lights == null) return;
		cmd.UpdateBuffer<PunctualLight>(_punctualLightsBuffer, lights);
	}

	/// <summary>Frozen command re-reads the pointer on each replay - memory must be stable.</summary>
	public void SetupPunctualShadowMatrices(ICommandBuffer cmd, UnsafeArray* matrices)
	{
		if (matrices == null) return;
		cmd.UpdateBuffer<Matrix4x4>(_punctualShadowMatricesBuffer, matrices);
	}

	/// <summary>Draws one punctual shadow slice using the last SetupCullData/SetupLightData.</summary>
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

	/// <summary>See <see cref="IBatchRenderer.TransitionPunctualShadowsForRead"/>.</summary>
	public void TransitionPunctualShadowsForRead(ICommandBuffer cmd)
	{
		_shadowRenderer.TransitionPunctualShadowsForRead(cmd);
	}

	/// <summary>Must dispatch even for an empty light range: ClusterCounts has to be zeroed or
	/// shading reads the previous camera's clusters.</summary>
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

	/// <summary>Only affects draw-range selection in ExecuteDrawBatching; does not touch PSO/states.</summary>
	public void SetMaterialTransparent(MaterialId materialId, bool transparent) =>
		SetMaterialTransparent(materialId.materialId, transparent);

	public void SetMaterialTransparent(int materialId, bool transparent)
	{
		bool changed = transparent
			? _transparentMaterials.Add(materialId)
			: _transparentMaterials.Remove(materialId);

		if (changed)
		{
			// Frozen ForwardPass commands bake the opaque/transparent split - must re-record.
			SetAllCommandsDirty();
		}
	}

	// Tail sub-allocation: buffers grow rarely (x2); normal registration uploads only the new tail.
	private void UpdateGpuMegaBuffers()
	{
		if (!_isMeshBuffersDirty) return;

		if (_megaVertexBufferCPU.IsCreated)
		{
			// UAV flag only when skinning is on: with it off the buffer must match the
			// pre-skinning description exactly so disabling is a clean rollback.
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

		// Conservative: invalidate the graph on both paths, even when the buffer object survived.
		SetAllCommandsDirty();
	}

	// Tail sub-allocation: cpuList holds all accumulated geometry, but only the new tail is
	// uploaded unless capacity grows (x2), which re-uploads the whole list.
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
				// Growth path: if this prints every frame, scene geometry is being re-uploaded
				// per frame - hundreds of MB of dynamic memory and a forced GPU idle.
				AnimWrite($"[anim] MegaBuffer '{name}': GROWTH to {newCapacity} elems, " +
					$"full upload {(long)currentCount * Unsafe.SizeOf<T>() / (1024 * 1024)} MB");
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
				AnimWrite($"[anim] MegaBuffer '{name}': tail top-up {currentCount - uploadedCount} elems");
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

		// ResourceStateTracker is backend-internal and works in native Diligent states.
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

		// DepthRead, not ShaderResource - see ShadowRenderer.ExecuteDrawShadows.
		cmd.TransitionResource(_shadowRenderer.ShadowMapsTarget, ResourceState.DepthRead);

		cmd.TransitionResource(cullResult.FinallyInstancesBuffer, ResourceState.VertexBuffer);
		cmd.TransitionResource(cullResult.GpuInstancesDataBuffer, ResourceState.ShaderResource);
		cmd.TransitionResource(cullResult.IndirectArgsBuffers, ResourceState.IndirectArgument);

		// Light-cluster buffers move to read here, not in clustering: every draw path passes here,
		// including previews that never cluster.
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
		return _api.CreateGraphicsState(BuildBaseStateInfo());
	}

	/// <summary><see cref="GetBaseState"/> for alpha-blended materials: straight-alpha blending on
	/// slot 0 only, depth test on but depth WRITE off, so transparents stay out of the opaque depth
	/// that SSR, fog and motion vectors read.</summary>
	public IStateObject GetBlendedState()
	{
		var info = BuildBaseStateInfo();
		info.Name = "Instancing PSO (blend)";
		info.BlendState = BlendStateInfo.AlphaBlend;
		info.DepthStencilState = new DepthStencilStateInfo
		{
			DepthEnable = true,
			DepthFunc = ComparisonFunctionType.Greater,
			DepthWriteEnable = false,
		};
		return _api.CreateGraphicsState(info);
	}

	private GraphicsStateInfo BuildBaseStateInfo()
	{
		return new GraphicsStateInfo
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
				// TEXCOORD_1 is last in Vertex, so it must be last here too: Diligent packs
				// auto-offsets in element order.
				new InputLayoutElementInfo { InputIndex = 6, NumComponents = 2, ValueType = InputElementValueType.Float32, IsNormalized = false },
				new InputLayoutElementInfo { InputIndex = 3, BufferSlot = 1, NumComponents = 1, ValueType = InputElementValueType.Int32, IsNormalized = false, Frequency = InputElementFrequencyType.PerInstance }
			]
		};
	}

	/// <summary><see cref="GetBaseState"/> for point/line topologies, with culling off since
	/// points and lines have no facing.</summary>
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
				// TEXCOORD_1 is last in Vertex, so it must be last here too: Diligent packs
				// auto-offsets in element order.
				new InputLayoutElementInfo { InputIndex = 6, NumComponents = 2, ValueType = InputElementValueType.Float32, IsNormalized = false },
				new InputLayoutElementInfo { InputIndex = 3, BufferSlot = 1, NumComponents = 1, ValueType = InputElementValueType.Int32, IsNormalized = false, Frequency = InputElementFrequencyType.PerInstance }
			]
		});
	}

	/// <summary>Wireframe <see cref="GetBaseState"/>, culling off; GreaterEqual depth so the overlay
	/// passes against the depth-coincident solid pass instead of z-fighting it.</summary>
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
				// TEXCOORD_1 is last in Vertex, so it must be last here too: Diligent packs
				// auto-offsets in element order.
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