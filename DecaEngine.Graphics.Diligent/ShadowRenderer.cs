using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using DecaEngine.Core;
using DecaEngine.Graphics.Core;
using DecaEngine.Graphics.Diligent.RenderGraph;
using Diligent;
using ClearDepthStencilFlags = DecaEngine.Core.ClearDepthStencilFlags;
using ResourceState = DecaEngine.Core.ResourceState;
using SetVertexBuffersFlags = DecaEngine.Core.SetVertexBuffersFlags;
using ValueType = Diligent.ValueType;

namespace DecaEngine.Graphics.Diligent
{
	public class ShadowRenderer : IReleaseObject
	{
		private readonly DiligentGraphicsApi _api;
		private readonly IMaterialObject _shadowMaterial;
		private ISamplerObject _shadowComparisonSampler;
		private ISamplerObject _shadowPointSampler;
		private IRenderTarget _shadowMaps;
		
		public const int MaxCascades = 4;
		public const int ShadowMapSize = 4096;

		public IRenderTarget ShadowMapsTarget => _shadowMaps;
		public ISamplerObject ShadowComparisonSampler => _shadowComparisonSampler;
		public ISamplerObject ShadowPointSampler => _shadowPointSampler;

		public ShadowRenderer(DiligentGraphicsApi api)
		{
			_api = api;
			CreateShadowMapAndSamplers();

			_shadowMaterial = new DiligentMaterial("Shadow Material", api);
			CreateShadowPso();
		}

		private void CreateShadowMapAndSamplers()
		{
			_shadowMaps = _api.CreateRenderTarget(new TextureInfo
			{
				name = $"Shadow Map",
				width = ShadowMapSize,
				height = ShadowMapSize,
				format = TextureObjectFormat.D32Float,
				arraySize = MaxCascades,
			});

			// Sampler for hardware PCF comparison
			_shadowComparisonSampler = _api.CreateSampler(
				"Shadow Comparison Sampler",
				TextureFilter.ComparisonLinear,
				TextureAddress.Clamp,
				CompFunction.LessEqual, // Correct for reversed-Z depth buffer (clear value 0.0, depth func Greater)
				new Vector4(1.0f, 1.0f, 1.0f, 1.0f));
			
			// Sampler for raw depth reads (blocker search in PCSS)
			_shadowPointSampler = _api.CreateSampler(
				"Shadow Point Sampler",
				TextureFilter.Point,
				TextureAddress.Clamp,
				CompFunction.LessEqual,
				new Vector4(1.0f, 1.0f, 1.0f, 1.0f));
		}

		private void CreateShadowPso()
		{
			var shadowVs = new DiligentShader(_api, "Shadow VS", "EditorAssets/shader", "ShadowVS.hlsl", ShaderObjectType.Vertex, "Main");
			_shadowMaterial.SetShader(shadowVs);

			var stateInfo = GetBaseState();
			stateInfo.Name = "Shadow PSO";
			stateInfo.RenderTargetFormats = [];
			stateInfo.DepthStencilFormat = TextureObjectFormat.D32Float;
			// Using Less (with reversed-Z this behaves as "closer to light") during shadow map generation
			stateInfo.DepthStencilState.DepthFunc = ComparisonFunctionType.Less;

			_shadowMaterial.SetState(_api.CreateGraphicsState(stateInfo));
		}

		public void UpdateMaterialResources(IBufferHandle viewConstants, IBufferHandle lightConstants, IBufferHandle gpuInstances)
		{
			_shadowMaterial.SetBuffer("View", viewConstants, HandleAccess.Vertex);
			_shadowMaterial.SetBuffer("Light", lightConstants, HandleAccess.Vertex);
			_shadowMaterial.SetBuffer("GPURenderInstances", gpuInstances, HandleAccess.Vertex);
		}

		public void SetShadowResources(IMaterialObject material)
		{
			material.SetTexture("ShadowMaps", ShadowMapsTarget);
			material.SetSampler("ShadowMaps_sampler", ShadowComparisonSampler);
			material.SetSampler("ShadowMaps_sampler_point", ShadowPointSampler);
		}

		public void ExecuteDrawShadows(
			ICommandBuffer cmd,
			DiligentBufferHandle megaVertexBufferGPU,
			DiligentBufferHandle megaIndexBufferGPU,
			CullResult cullResult,
			uint cascadeIndex)
		{
			if (cascadeIndex < 0 || cascadeIndex >= MaxCascades) return;

			cmd.TransitionResource(_shadowMaps, ResourceState.DepthWrite);

			cmd.TransitionResource(cullResult.FinallyInstancesBuffer, ResourceState.VertexBuffer);
			cmd.TransitionResource(cullResult.GpuInstancesDataBuffer, ResourceState.ShaderResource);
			cmd.TransitionResource(cullResult.IndirectArgsBuffers, ResourceState.IndirectArgument);

			cmd.SetRenderTarget(null, _shadowMaps, 0, cascadeIndex);
			cmd.SetViewport(ShadowMapSize, ShadowMapSize);
			// Clear depth to 0.0 for reversed-Z
			cmd.ClearDepthStencil(_shadowMaps, ClearDepthStencilFlags.Depth, 1.0f, 0, cascadeIndex);

			cmd.SetVertexBuffers(0, [megaVertexBufferGPU, cullResult.FinallyInstancesBuffer], [0ul, 0ul], SetVertexBuffersFlags.Reset);
			cmd.SetIndexBuffer(megaIndexBufferGPU, 0);

			cmd.SetPipelineState(_shadowMaterial);
			cmd.CommitShaderResources(_shadowMaterial);

			var drawRange = new MaterialDrawRange();
			foreach (var kvp in cullResult.MaterialDrawRanges)
			{
				drawRange.DrawCount += kvp.Value.DrawCount;
			}

			cmd.DrawIndexedIndirect(cullResult.IndirectArgsBuffers, drawRange, IndexType.UInt32);

			// DepthRead, а не общий ShaderResource: SRV депт-текстуры на Vulkan биндится с лейаутом
			// DEPTH_STENCIL_READ_ONLY_OPTIMAL (VUID-VkDescriptorImageInfo-imageLayout-00344).
			cmd.TransitionResource(_shadowMaps, ResourceState.DepthRead);
		}

		private GraphicsStateInfo GetBaseState()
		{
			return new GraphicsStateInfo
			{
				Name = "Shadow PSO",
				RenderTargetFormats = [],
				PrimitiveTopology = PrimitiveTopologyType.TriangleList,
				RasterizerState = new RasterizerStateInfo
				{
					CullMode = CullModeType.Front,
					DepthBias = 0,
				},
				DepthStencilState = new DepthStencilStateInfo
				{
					DepthEnable = true,
					DepthFunc = ComparisonFunctionType.Less
				},
				InputLayout =
				[
					new InputLayoutElementInfo { InputIndex = 0, NumComponents = 3, ValueType = InputElementValueType.Float32, IsNormalized = false },
					new InputLayoutElementInfo { InputIndex = 1, NumComponents = 2, ValueType = InputElementValueType.Float32, IsNormalized = false },
					new InputLayoutElementInfo { InputIndex = 2, NumComponents = 3, ValueType = InputElementValueType.Float32, IsNormalized = false },
					// Unused by ShadowVS.hlsl (it only reads position), but must still be declared: this
					// PSO reads from the same mega vertex buffer as DiligentBatchRenderer's (see
					// GetBaseState there), and Diligent auto-computes each buffer slot's stride from its
					// declared layout elements - omitting Tangent/Color here would under-report slot 0's
					// true per-vertex stride and misalign every vertex after the first.
					new InputLayoutElementInfo { InputIndex = 4, NumComponents = 4, ValueType = InputElementValueType.Float32, IsNormalized = false },
					new InputLayoutElementInfo { InputIndex = 5, NumComponents = 4, ValueType = InputElementValueType.Float32, IsNormalized = false },
					new InputLayoutElementInfo { InputIndex = 6, NumComponents = 2, ValueType = InputElementValueType.Float32, IsNormalized = false },
					new InputLayoutElementInfo
					{
						InputIndex = 3, BufferSlot = 1, NumComponents = 1, ValueType = InputElementValueType.Int32,
						IsNormalized = false,
						Frequency = InputElementFrequencyType.PerInstance
					}
				]
			};
		}

		public void Release()
		{
			if (_shadowMaps != null)
			{
				_shadowMaps?.Release();
			}

			_shadowMaterial?.Release();
		}
	}
}