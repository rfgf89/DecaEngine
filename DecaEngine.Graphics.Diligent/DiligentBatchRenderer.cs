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
using TextureAddressMode = Diligent.TextureAddressMode;
using ValueType = Diligent.ValueType;

namespace DecaEngine.Graphics.Diligent;

public unsafe class DiligentBatchRenderer : IReleaseObject
{
	private readonly DiligentGraphicsPipeline _pipeline;
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
	private UnsafeArray* _indirectDatas;
	private UnsafeArray* _cpuBatchCounters;

	private DiligentBufferHandle? _inputIndirectInstancesBuffer;
	private DiligentBufferHandle? _instanceDrawDataBuffer;
	private DiligentBufferHandle? _meshBatchDataBuffer;
	private DiligentBufferHandle? _finallyInstancesBuffer;
	private DiligentBufferHandle? _batchCountersBuffer;

	private DiligentBufferHandle? _gpuInstancesDataBuffer;

	private DiligentBufferHandle? _indirectArgsBuffers;

	private readonly DiligentBufferHandle? _lightConstantsBuffer;
	private readonly DiligentBufferHandle? _viewConstantsBuffer;
	private readonly DiligentBufferHandle? _cullConstantsBuffer;
	private readonly IComputeMaterial _cullingMaterial;

	private readonly int _instanceBufferSectorCapacity = 64;
	private int _instanceBufferCapacity = 0;
	private int _totalCommands = 0;

	private static int gMeshIndex;
	private static int gMaterialIndex;
	private static int gBatchIndex;

	private BatchRendererInfo _batchRendererInfo;

	private readonly ResourceStateTracker _stateTracker = new();
	public ResourceStateTracker StateTracker => _stateTracker;

	private bool _isDrawBatchCmdDirty = true;

	public bool IsDirty => _isDrawBatchCmdDirty;
	public void ClearDirty() => _isDrawBatchCmdDirty = false;

	public DiligentBatchRenderer(DiligentGraphicsPipeline pipeline)
	{
		_perMeshData = UnsafeList.Allocate<PerMeshData>(32);
		_pipeline = pipeline;
		_deviceType = _pipeline.Device.GetDeviceInfo().Type;
		
		_cullConstantsBuffer = CreateConstantsBuffer<CullData>("Cull Constants");
		_viewConstantsBuffer = CreateConstantsBuffer<ViewData>("View Constants");
		_lightConstantsBuffer = CreateConstantsBuffer<LightData>("Light Constants");

		var cullingShader = new DiligentShader(_pipeline, "Batching Instancing CS", "EditorAssets/shader", "BatchingInstancingCS.hlsl", ShaderObjectType.Compute, "CSMain");
		_cullingMaterial = new DiligentComputeMaterial("Batching Instancing CS", _pipeline);
		_cullingMaterial.SetShader(cullingShader);
		_cullingMaterial.SetBuffer("Constants", _cullConstantsBuffer);

		_shadowRenderer = new ShadowRenderer(pipeline);
	}

	private void SetAllCommandsDirty()
	{
		_isDrawBatchCmdDirty = true;
	}

	private DiligentBufferHandle CreateConstantsBuffer<T>(string name) where T: unmanaged
	{
		return (DiligentBufferHandle)_pipeline.CreateBuffer<T>(new BufferInfo()
		{
			name = name,
			type = BufferHandleType.Constant,
		});
	}

	private void CreateStructuredBuffer<T>(ref DiligentBufferHandle? buffer, int bufferCapacity, string name = "Indirect Instance Data Buffer") where T : struct
	{
		buffer?.Release();

		buffer = (DiligentBufferHandle)_pipeline.CreateBuffer<T>(bufferCapacity,
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

		buffer = (DiligentBufferHandle)_pipeline.CreateBuffer<int>(bufferCapacity * _instanceBufferSectorCapacity, new BufferInfo
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

		buffer = (DiligentBufferHandle)_pipeline.CreateBuffer<T>(count,
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

		buffer = (DiligentBufferHandle)_pipeline.CreateBuffer<DrawIndexedIndirectCommand>(maxCommands,
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

		material.SetTexture($"ShadowMaps", _shadowRenderer.ShadowMaps, HandleAccess.Pixel);
		material.SetSampler($"ShadowMaps_sampler", _shadowRenderer.ShadowSampler, HandleAccess.Pixel, false);

		if (_gpuInstancesDataBuffer != null)
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

	public unsafe void SetupViewData(ICommandBuffer? cmd, ref ViewData viewData)
	{
		fixed(ViewData* ptr = &viewData)
		cmd.UpdateBuffer<ViewData>(_viewConstantsBuffer, 0, ptr);
	}

	public unsafe void SetupCullData(ICommandBuffer? cmd, ref CullData cullData)
	{
		fixed (CullData* ptr = &cullData)
		cmd.UpdateBuffer(_cullConstantsBuffer, 0, ptr);
	}

	public unsafe void SetupLightData(ICommandBuffer? cmd, ref LightData lightData)
	{
		//_shadowRenderer.CalculateCascades(cullData, viewData, ref lightData, out var cascadeProjMatrices);
		
		fixed (LightData* ptr = &lightData)
		cmd.UpdateBuffer(_lightConstantsBuffer, 0, ptr);
		
		//_lastCascadeProjMatrices = cascadeProjMatrices;
	}

	public IReadOnlyList<KeyValuePair<int, DiligentMaterial>> GetMaterials() => _materialObjects;
	public IReadOnlyList<KeyValuePair<int, IndirectBatch>> GetBatches() => _indirectBatches;
	public BatchRendererInfo ReadInfo() => _batchRendererInfo;

	public DrawIndexedIndirectCommand[] ReadIndirectArgsForDebugging()
	{
		if (_totalCommands == 0) return [];
		var data = new DrawIndexedIndirectCommand[_totalCommands];
		fixed (void* ptr = data)
			_pipeline.ImmediateContext.ReadBufferExt<DrawIndexedIndirectCommand>(_pipeline.Device, _indirectArgsBuffers.Buffer, ptr, (uint)(_totalCommands * sizeof(DrawIndexedIndirectCommand)));
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

		if (buffersRecreated)
		{
			CreateIndirectBuffer(ref _indirectArgsBuffers, _totalCommands);
			CreateStructuredBuffer<PerMeshData>(ref _meshBatchDataBuffer, batchCount, "Mesh Batch Data Buffer");

			UnsafeArray.Resize<DrawIndexedIndirectCommand>(ref _indirectDatas, _totalCommands);

			IDeviceContext ctx = _pipeline.ImmediateContext;
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
				var pmd = UnsafeList.GetPtr<PerMeshData>(_perMeshData, batch.mesh.meshId);
				pmd->physicalCommandOffset = commandOffset;

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
			ctx.UploadBufferExt<PerMeshData>(_meshBatchDataBuffer.Buffer, _perMeshData);
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

	private void UpdateGpuMegaBuffers()
	{
		if (!_isMeshBuffersDirty) return;

		_megaVertexBufferGPU?.Release();
		_megaIndexBufferGPU?.Release();

		_megaVertexBufferGPU = (DiligentBufferHandle)_pipeline.CreateBuffer<Vertex>(_megaVertexBufferCPU.Count, new BufferInfo { name = "Mega Vertex Buffer", type = BufferHandleType.Vertex });
		_megaIndexBufferGPU = (DiligentBufferHandle)_pipeline.CreateBuffer<uint>(_megaIndexBufferCPU.Count, new BufferInfo { name = "Mega Index Buffer", type = BufferHandleType.Index });

		_pipeline.ImmediateContext.UploadBufferExt<Vertex>(_megaVertexBufferGPU.Buffer, _megaVertexBufferCPU.GetNative());
		_pipeline.ImmediateContext.UploadBufferExt<uint>(_megaIndexBufferGPU.Buffer, _megaIndexBufferCPU.GetNative());

		_stateTracker.SetState(_megaVertexBufferGPU.Buffer, ResourceState.CopyDest);
		_stateTracker.SetState(_megaIndexBufferGPU.Buffer, ResourceState.CopyDest);

		_isMeshBuffersDirty = false;
		SetAllCommandsDirty();
	}

	private void UpdateDrawRangesCache()
	{
		if (!_isDrawRangesCacheDirty) return;
		_materialDrawRanges.Clear();
		if (_indirectBatches.Count == 0) { _isDrawRangesCacheDirty = false; return; }

		var sortedPairs = _indirectBatches.OrderBy(p => p.Value.material.materialId).ToList();
		if (sortedPairs.Count == 0) { _isDrawRangesCacheDirty = false; return; }
		
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
	}

	public void ExecuteDrawShadows(ICommandBuffer cmd, CullResult cullResult, int cascadeIndex)
	{
		if (_instancesSubset.instances.Length == 0 || _totalCommands == 0) return;
		UpdateGpuMegaBuffers();
		UpdateDrawRangesCache();
		_shadowRenderer.ExecuteDrawShadows(cmd, _megaVertexBufferGPU, _megaIndexBufferGPU, cullResult, (uint)cascadeIndex);
	}

	public void ExecuteDrawBatching(ICommandBuffer cmd, CullResult cullResult)
	{
		if (_instancesSubset.instances.Length == 0 || _totalCommands == 0) return;

		UpdateGpuMegaBuffers();
		UpdateDrawRangesCache();

		cmd.TransitionResource(_shadowRenderer.ShadowMaps, ResourceState.ShaderResource);

		cmd.TransitionResource(cullResult.FinallyInstancesBuffer, ResourceState.VertexBuffer);
		cmd.TransitionResource(cullResult.GpuInstancesDataBuffer, ResourceState.ShaderResource);
		cmd.TransitionResource(cullResult.IndirectArgsBuffers, ResourceState.IndirectArgument);

		cmd.SetVertexBuffers(0, [_megaVertexBufferGPU, cullResult.FinallyInstancesBuffer], [0ul, 0ul], SetVertexBuffersFlags.Reset);
		cmd.SetIndexBuffer(_megaIndexBufferGPU, 0);

		var scDesc = _pipeline.SwapChain.GetDesc();
		cmd.SetViewport(scDesc.Width, scDesc.Height);

		foreach (var kvp in cullResult.MaterialDrawRanges)
		{
			var materialId = kvp.Key;
			var drawRange = kvp.Value;
			if (drawRange.DrawCount == 0) continue;

			var material = _materialObjects[materialId];
			cmd.SetPipelineState(material);
			cmd.CommitShaderResources(material);
			cmd.DrawIndexedIndirect(cullResult.IndirectArgsBuffers, drawRange, IndexType.UInt32);
		}

		_batchRendererInfo.pipelineStateCount = cullResult.MaterialDrawRanges.Count;
	}

	public GraphicsPipelineStateCreateInfo GetBaseState()
	{
		var pipelineCreateInfo = new GraphicsPipelineStateCreateInfo
		{
			PSODesc = new PipelineStateDesc
			{
				Name = "Instancing PSO",
				PipelineType = PipelineType.Graphics,
				ResourceLayout = new PipelineResourceLayoutDesc { DefaultVariableType = ShaderResourceVariableType.Mutable }
			},
			GraphicsPipeline = new GraphicsPipelineDesc
			{
				NumRenderTargets = 1,
				RTVFormats = [_pipeline.SwapChain.GetDesc().ColorBufferFormat],
				DSVFormat = _pipeline.SwapChain.GetDesc().DepthBufferFormat,
				PrimitiveTopology = PrimitiveTopology.TriangleList,
				RasterizerDesc = new RasterizerStateDesc { CullMode = CullMode.Back },
				DepthStencilDesc = new DepthStencilStateDesc { DepthEnable = true, DepthFunc = ComparisonFunction.Greater },
				InputLayout = new InputLayoutDesc
				{
					LayoutElements =
					[
						new LayoutElement { InputIndex = 0, NumComponents = 3, ValueType = ValueType.Float32, IsNormalized = false },
						new LayoutElement { InputIndex = 1, NumComponents = 2, ValueType = ValueType.Float32, IsNormalized = false },
						new LayoutElement { InputIndex = 2, NumComponents = 3, ValueType = ValueType.Float32, IsNormalized = false },
						new LayoutElement { InputIndex = 3, BufferSlot = 1, NumComponents = 1, ValueType = ValueType.Int32, IsNormalized = false, Frequency = InputElementFrequency.PerInstance }
					]
				}
			}
		};

		return pipelineCreateInfo;
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