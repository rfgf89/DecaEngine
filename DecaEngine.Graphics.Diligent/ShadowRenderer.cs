using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using DecaEngine.Core;
using DecaEngine.Graphics.Core;
using DecaEngine.Graphics.Diligent.RenderGraph;
using Diligent;
using ValueType = Diligent.ValueType;

namespace DecaEngine.Graphics.Diligent
{
	public class ShadowRenderer : IReleaseObject
	{
		private readonly DiligentGraphicsPipeline _pipeline;
		private readonly IMaterialObject _shadowMaterial;
		private ISamplerObject _shadowSampler;
		private IRenderTarget _shadowMaps;
		
		public const int MaxCascades = 4;
		public const int ShadowMapSize = 8192;

		public IRenderTarget ShadowMaps => _shadowMaps;
		public ISamplerObject ShadowSampler => _shadowSampler;

		public ShadowRenderer(DiligentGraphicsPipeline pipeline)
		{
			_pipeline = pipeline;
			CreateShadowMap();

			_shadowMaterial = new DiligentMaterial("Shadow Material", pipeline);
			CreateShadowPso();
		}

		private void CreateShadowMap()
		{
			_shadowMaps = _pipeline.CreateRenderTarget(new RenderTargetInfo
			{
				name = $"Shadow Map",
				width = ShadowMapSize,
				height = ShadowMapSize,
				textureFormat = RenderTargetInfo.Format.D32_FLOAT,
				arraySize = MaxCascades
			});

			_shadowSampler = _pipeline.CreateSampler(
				$"Shadow Sampler",
				TextureFilter.ComparisonLinear,
				TextureAddress.Border,
				CompFunction.Greater,
				new Vector4(0.0f, 0.0f, 0.0f, 0.0f));
		}

		private void CreateShadowPso()
		{
			var shadowVs = new DiligentShader(_pipeline, "Shadow VS", "EditorAssets/shader", "ShadowVS.hlsl", ShaderObjectType.Vertex, "Main");
			_shadowMaterial.SetShader(shadowVs);

			var psoCi = GetBaseState();
			psoCi.Ps = null;
			psoCi.GraphicsPipeline.NumRenderTargets = 0;
			psoCi.GraphicsPipeline.RTVFormats = [];
			psoCi.GraphicsPipeline.DSVFormat = TextureFormat.D32_Float;

			psoCi.PSODesc.Name = "Shadow PSO";
			psoCi.GraphicsPipeline.DepthStencilDesc.DepthFunc = ComparisonFunction.Greater;

			((DiligentMaterial)_shadowMaterial).SetBasePipelineState(psoCi);
		}

		public void UpdateMaterialResources(IBufferHandle viewConstants, IBufferHandle lightConstants, IBufferHandle gpuInstances)
		{
			_shadowMaterial.SetBuffer("View", viewConstants, HandleAccess.Vertex);
			_shadowMaterial.SetBuffer("Light", lightConstants, HandleAccess.Vertex);
			_shadowMaterial.SetBuffer("GPURenderInstances", gpuInstances, HandleAccess.Vertex);
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
			cmd.ClearDepthStencil(_shadowMaps, ClearDepthStencilFlags.Depth, 0.0f, 0, cascadeIndex);

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

			cmd.TransitionResource(_shadowMaps, ResourceState.ShaderResource);
		}

		private GraphicsPipelineStateCreateInfo GetBaseState()
		{
			var pipelineCreateInfo = new GraphicsPipelineStateCreateInfo
			{
				PSODesc = new PipelineStateDesc
				{
					Name = "Shadow PSO",
					PipelineType = PipelineType.Graphics,
					ResourceLayout = new PipelineResourceLayoutDesc
					{
						DefaultVariableType = ShaderResourceVariableType.Mutable
					}
				},
				GraphicsPipeline = new GraphicsPipelineDesc
				{
					NumRenderTargets = 0,
					PrimitiveTopology = PrimitiveTopology.TriangleList,
					RasterizerDesc = new RasterizerStateDesc
					{
						CullMode = CullMode.None,
						DepthBias = 0,
						SlopeScaledDepthBias = 1.5f
					},
					DepthStencilDesc = new DepthStencilStateDesc
					{
						DepthEnable = true,
						DepthFunc = ComparisonFunction.GreaterEqual
					},
					InputLayout = new InputLayoutDesc
					{
						LayoutElements =
						[
							new LayoutElement { InputIndex = 0, NumComponents = 3, ValueType = ValueType.Float32, IsNormalized = false },
							new LayoutElement { InputIndex = 1, NumComponents = 2, ValueType = ValueType.Float32, IsNormalized = false },
							new LayoutElement { InputIndex = 2, NumComponents = 3, ValueType = ValueType.Float32, IsNormalized = false },
							new LayoutElement
							{
								InputIndex = 3, BufferSlot = 1, NumComponents = 1, ValueType = ValueType.Int32,
								IsNormalized = false,
								Frequency = InputElementFrequency.PerInstance
							}
						]
					}
				}
			};

			return pipelineCreateInfo;
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