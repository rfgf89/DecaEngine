using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using DecaEngine.Core;
using DecaEngine.Graphics.Diligent.RenderGraph;
using Diligent;
using ClearDepthStencilFlags = DecaEngine.Graphics.ClearDepthStencilFlags;
using ResourceState = DecaEngine.Graphics.ResourceState;
using SetVertexBuffersFlags = DecaEngine.Graphics.SetVertexBuffersFlags;
using ValueType = Diligent.ValueType;

namespace DecaEngine.Graphics.Diligent
{
	public class ShadowRenderer : IReleaseObject
	{
		private readonly DiligentGraphicsApi _api;
		private IMaterialObject _shadowMaterial;
		private IMaterialObject _punctualShadowMaterial;
		private ISamplerObject _shadowComparisonSampler;
		private ISamplerObject _shadowPointSampler;
		private IRenderTarget _shadowMaps;
		private IRenderTarget _punctualShadowMaps;

		// 1x1 D32 stub: Vulkan needs a valid descriptor per declared slot at Register(),
		// before any real shadow draw allocates the full arrays.
		private IRenderTarget _shadowMapPlaceholder;

		// Layout numbers live in ShadowLayout so the scene-side cascade builder shares them.
		public const int MaxCascades = ShadowLayout.MaxCascades;
		public const int ShadowMapSize = ShadowLayout.ShadowMapSize;

		/// <summary>Ortho cascade margin, in shadow-map texels: the ortho matrix covers the cascade
		/// sphere PLUS this margin, so the shader's edge rejection (SUN_CASCADE_MARGIN_TEXELS),
		/// PCF taps, normal offset and texel-grid snap never sample outside the map.</summary>
		public const float CascadeMarginTexels = ShadowLayout.CascadeMarginTexels;

		/// <summary>Real cascade array once allocated, else the placeholder; safe before the first shadow frame.</summary>
		public IRenderTarget ShadowMapsTarget => _shadowMaps ?? _shadowMapPlaceholder;

		/// <summary>Punctual shadow array (spot = one slice, point = six cube faces). Depth convention
		/// matches cascades: standard Z, write Less, compare LessEqual. Placeholder until first punctual draw.</summary>
		public IRenderTarget PunctualShadowMapsTarget => _punctualShadowMaps ?? _shadowMapPlaceholder;
		public ISamplerObject ShadowComparisonSampler => _shadowComparisonSampler;
		public ISamplerObject ShadowPointSampler => _shadowPointSampler;

		// One material per alpha-tested caster: rebinding a texture on a shared SRB while the
		// previous frame is in flight trips Vulkan validation ("bound VkDescriptorSet was destroyed").
		private readonly Dictionary<int, IMaterialObject> _alphaTestedMaterials = new();

		// Punctual variant of _alphaTestedMaterials (smaller-bias PSO), built lazily on first punctual draw.
		private readonly Dictionary<int, IMaterialObject> _punctualAlphaTestedMaterials = new();

		// Registration params kept so the punctual variant can be built later, on first punctual draw.
		private readonly Dictionary<int, AlphaTestedMaterialParams> _alphaTestedMaterialParams = new();

		// Materials given SetShadowResources; rebound in place when the placeholder swaps for the real array.
		private readonly List<IMaterialObject> _shadowBoundMaterials = new();

		private IBufferHandle _viewConstants, _lightConstants, _gpuInstances;

		/// <summary>Number of materials writing shadows with alpha test (selection diagnostic).</summary>
		public int AlphaTestedMaterialCount => _alphaTestedMaterials.Count;

		// Materials excluded from shadow casting; see IBatchRenderer.SetMaterialShadowCasting.
		private readonly HashSet<int> _nonCastingMaterials = new();

		/// <summary>Number of materials excluded from shadow casting.</summary>
		public int NonCastingMaterialCount => _nonCastingMaterials.Count;

		/// <summary>See <see cref="IBatchRenderer.SetMaterialShadowCasting"/>.</summary>
		public void SetMaterialShadowCasting(int materialId, bool casts)
		{
			if (casts)
			{
				_nonCastingMaterials.Remove(materialId);
			}
			else
			{
				_nonCastingMaterials.Add(materialId);
			}
		}

		private readonly struct AlphaTestedMaterialParams
		{
			public readonly IGpuTexture BaseColorTexture;
			public readonly ISamplerObject BaseColorSampler;
			public readonly float AlphaCutoff;
			public readonly ModelLoader.StreamedTexture Stream;

			public AlphaTestedMaterialParams(IGpuTexture baseColorTexture, ISamplerObject baseColorSampler,
				float alphaCutoff, ModelLoader.StreamedTexture stream)
			{
				BaseColorTexture = baseColorTexture;
				BaseColorSampler = baseColorSampler;
				AlphaCutoff = alphaCutoff;
				Stream = stream;
			}
		}

		public ShadowRenderer(DiligentGraphicsApi api)
		{
			_api = api;
			CreateSamplers();
			CreatePlaceholder();

			// Shadow arrays and base materials/PSOs are created lazily on the first real shadow draw.
		}

		/// <summary>Registers a depth-only alpha-tested shadow material (clip() on base-color alpha).
		/// The shadow material must join the texture's streaming bindings, or it stays on the 1x1
		/// white filler (alpha 1, clip never fires) while the screen material upgrades.</summary>
		public void RegisterAlphaTestedMaterial(int materialId, IGpuTexture baseColorTexture,
			ISamplerObject baseColorSampler, float alphaCutoff, ModelLoader.StreamedTexture stream)
		{
			if (baseColorTexture is null || _alphaTestedMaterials.ContainsKey(materialId))
			{
				return;
			}

			var parms = new AlphaTestedMaterialParams(baseColorTexture, baseColorSampler, alphaCutoff, stream);
			_alphaTestedMaterialParams[materialId] = parms;

			_alphaTestedMaterials[materialId] =
				CreateAlphaTestedShadowMaterial(materialId, "Shadow Masked", GetBaseState(), parms);

			// Punctual variant only if the punctual set is already up; otherwise built lazily
			// on first punctual draw so shadow-free environments never pay for it.
			if (_punctualShadowMaterial != null)
			{
				_punctualAlphaTestedMaterials[materialId] =
					CreateAlphaTestedShadowMaterial(materialId, "Punctual Shadow Masked", GetPunctualBaseState(), parms);
			}
		}

		// Shared body for cascade and punctual masked materials; only base rasterizer state (bias) differs.
		private IMaterialObject CreateAlphaTestedShadowMaterial(int materialId, string namePrefix,
			GraphicsStateInfo stateInfo, AlphaTestedMaterialParams parms)
		{
			// Own shader instances per material: a shared one would be released twice.
			var vs = new DiligentShader(_api, $"{namePrefix} VS {materialId}", "EditorAssets/shader",
				"ShadowVS.hlsl", ShaderObjectType.Vertex, "Main");
			var ps = new DiligentShader(_api, $"{namePrefix} PS {materialId}", "EditorAssets/shader",
				"ShadowMaskedPS.hlsl", ShaderObjectType.Pixel, "Main");

			var material = new DiligentMaterial($"{namePrefix} Material {materialId}", _api);
			material.SetShader(vs, ps);

			stateInfo.Name = $"{namePrefix} PSO {materialId}";
			stateInfo.DepthStencilFormat = TextureObjectFormat.D32Float;
			stateInfo.DepthStencilState.DepthFunc = ComparisonFunctionType.Less;
			material.SetState(_api.CreateGraphicsState(stateInfo));

			// Instance buffers may not exist yet; UpdateMaterialResources rebinds on every realloc.
			BindFrameBuffers(material);

			material.SetTexture("_MainTex", parms.BaseColorTexture);
			material.SetImmutableSampler("_MainTex", parms.BaseColorSampler);

			// Constant must be pushed before the first frame (see ShadowMaskedPS.hlsl).
			var cutoffConstant = new Vector4(MathF.Max(parms.AlphaCutoff, 1e-3f), 0f, 0f, 0f);
			material.SetConstant("ShadowMaterial", ref cutoffConstant);

			parms.Stream?.Bindings.Add((material, "_MainTex"));

			return material;
		}

		private void CreatePlaceholder()
		{
			// arraySize = 2: DiligentRenderTarget infers Texture2DArray only when arraySize > 1,
			// and the shader slots are declared Texture2DArray - a Texture2D descriptor would mismatch.
			_shadowMapPlaceholder = _api.CreateRenderTarget(new TextureInfo
			{
				name = "Shadow Map Placeholder",
				width = 1,
				height = 1,
				format = TextureObjectFormat.D32Float,
				arraySize = 2,
			});
		}

		private void CreateSamplers()
		{
			_shadowComparisonSampler = _api.CreateSampler(
				"Shadow Comparison Sampler",
				TextureFilter.ComparisonLinear,
				TextureAddress.Clamp,
				CompFunction.LessEqual,
				new Vector4(1.0f, 1.0f, 1.0f, 1.0f));

			// Point sampler for raw depth reads (PCSS blocker search).
			_shadowPointSampler = _api.CreateSampler(
				"Shadow Point Sampler",
				TextureFilter.Point,
				TextureAddress.Clamp,
				CompFunction.LessEqual,
				new Vector4(1.0f, 1.0f, 1.0f, 1.0f));
		}

		// Creates the cascade array on first real cascade draw and rebinds materials off the placeholder.
		private void EnsureShadowMaps()
		{
			if (_shadowMaps != null)
			{
				return;
			}

			_shadowMaps = _api.CreateRenderTarget(new TextureInfo
			{
				name = "Shadow Map",
				width = ShadowMapSize,
				height = ShadowMapSize,
				format = TextureObjectFormat.D32Float,
				arraySize = MaxCascades,
			});

			foreach (var material in _shadowBoundMaterials)
			{
				material.SetTexture("ShadowMaps", _shadowMaps);
			}
		}

		// Punctual analog of EnsureShadowMaps; created independently, on first punctual draw.
		private void EnsurePunctualShadowMaps()
		{
			if (_punctualShadowMaps != null)
			{
				return;
			}

			_punctualShadowMaps = _api.CreateRenderTarget(new TextureInfo
			{
				name = "Punctual Shadow Maps",
				width = LightClusters.ShadowMapSize,
				height = LightClusters.ShadowMapSize,
				format = TextureObjectFormat.D32Float,
				arraySize = LightClusters.MaxShadowSlices,
			});

			foreach (var material in _shadowBoundMaterials)
			{
				material.SetTexture("PunctualShadowMaps", _punctualShadowMaps);
			}
		}

		private void EnsureShadowMaterial()
		{
			if (_shadowMaterial != null)
			{
				return;
			}

			_shadowMaterial = new DiligentMaterial("Shadow Material", _api);

			var shadowVs = new DiligentShader(_api, "Shadow VS", "EditorAssets/shader", "ShadowVS.hlsl", ShaderObjectType.Vertex, "Main");
			_shadowMaterial.SetShader(shadowVs);

			var stateInfo = GetBaseState();
			stateInfo.Name = "Shadow PSO";
			stateInfo.RenderTargetFormats = [];
			stateInfo.DepthStencilFormat = TextureObjectFormat.D32Float;
			stateInfo.DepthStencilState.DepthFunc = ComparisonFunctionType.Less;

			_shadowMaterial.SetState(_api.CreateGraphicsState(stateInfo));

			BindFrameBuffers(_shadowMaterial);
		}

		// Punctual analog of EnsureShadowMaterial: smaller-bias PSO plus deferred masked variants.
		private void EnsurePunctualShadowMaterial()
		{
			if (_punctualShadowMaterial != null)
			{
				return;
			}

			_punctualShadowMaterial = new DiligentMaterial("Punctual Shadow Material", _api);

			var shadowVs = new DiligentShader(_api, "Punctual Shadow VS", "EditorAssets/shader", "ShadowVS.hlsl", ShaderObjectType.Vertex, "Main");
			_punctualShadowMaterial.SetShader(shadowVs);

			var stateInfo = GetPunctualBaseState();
			stateInfo.Name = "Punctual Shadow PSO";
			stateInfo.RenderTargetFormats = [];
			stateInfo.DepthStencilFormat = TextureObjectFormat.D32Float;
			stateInfo.DepthStencilState.DepthFunc = ComparisonFunctionType.Less;

			_punctualShadowMaterial.SetState(_api.CreateGraphicsState(stateInfo));

			BindFrameBuffers(_punctualShadowMaterial);

			foreach (var kvp in _alphaTestedMaterialParams)
			{
				if (!_punctualAlphaTestedMaterials.ContainsKey(kvp.Key))
				{
					_punctualAlphaTestedMaterials[kvp.Key] = CreateAlphaTestedShadowMaterial(
						kvp.Key, "Punctual Shadow Masked", GetPunctualBaseState(), kvp.Value);
				}
			}
		}

		public void UpdateMaterialResources(IBufferHandle viewConstants, IBufferHandle lightConstants, IBufferHandle gpuInstances)
		{
			// Also remembered for alpha-tested materials created later at model registration.
			_viewConstants = viewConstants;
			_lightConstants = lightConstants;
			_gpuInstances = gpuInstances;

			// Base shadow materials may not exist yet; Ensure* binds them with current fields later.
			if (_shadowMaterial != null)
			{
				BindFrameBuffers(_shadowMaterial);
			}

			if (_punctualShadowMaterial != null)
			{
				BindFrameBuffers(_punctualShadowMaterial);
			}

			// Mandatory rebind: instance buffers are recreated on every realloc, and a material
			// holding the old one draws with a dead descriptor (Vulkan VUID-08114).
			foreach (var material in _alphaTestedMaterials.Values)
			{
				BindFrameBuffers(material);
			}

			foreach (var material in _punctualAlphaTestedMaterials.Values)
			{
				BindFrameBuffers(material);
			}
		}

		// View is not declared in ShadowVS.hlsl; that bind is a no-op kept for symmetry.
		private void BindFrameBuffers(IMaterialObject material)
		{
			if (_viewConstants is null)
			{
				return;
			}

			material.SetBuffer("View", _viewConstants, HandleAccess.Vertex);
			material.SetBuffer("Light", _lightConstants, HandleAccess.Vertex);
			material.SetBuffer("GPURenderInstances", _gpuInstances, HandleAccess.Vertex);
		}

		public void SetShadowResources(IMaterialObject material)
		{
			material.SetTexture("ShadowMaps", ShadowMapsTarget);
			material.SetSampler("ShadowMaps_sampler", ShadowComparisonSampler);
			material.SetSampler("ShadowMaps_sampler_point", ShadowPointSampler);

			// Shaders without these declarations simply ignore the bind.
			material.SetTexture("PunctualShadowMaps", PunctualShadowMapsTarget);
			material.SetSampler("PunctualShadowMaps_sampler", ShadowComparisonSampler);

			// Remember the material so Ensure* can swap the slot from placeholder to the real array.
			_shadowBoundMaterials.Add(material);
		}

		public void ExecuteDrawShadows(
			ICommandBuffer cmd,
			DiligentBufferHandle megaVertexBufferGPU,
			DiligentBufferHandle megaIndexBufferGPU,
			CullResult cullResult,
			uint cascadeIndex)
		{
			if (cascadeIndex < 0 || cascadeIndex >= MaxCascades) return;

			EnsureShadowMaps();
			EnsureShadowMaterial();

			cmd.TransitionResource(_shadowMaps, ResourceState.DepthWrite);

			cmd.TransitionResource(cullResult.FinallyInstancesBuffer, ResourceState.VertexBuffer);
			cmd.TransitionResource(cullResult.GpuInstancesDataBuffer, ResourceState.ShaderResource);
			cmd.TransitionResource(cullResult.IndirectArgsBuffers, ResourceState.IndirectArgument);

			cmd.SetRenderTarget(null, _shadowMaps, 0, cascadeIndex);
			cmd.SetViewport(ShadowMapSize, ShadowMapSize);
			cmd.ClearDepthStencil(_shadowMaps, ClearDepthStencilFlags.Depth, 1.0f, 0, cascadeIndex);

			cmd.SetVertexBuffers(0, [megaVertexBufferGPU, cullResult.FinallyInstancesBuffer], [0ul, 0ul], SetVertexBuffersFlags.Reset);
			cmd.SetIndexBuffer(megaIndexBufferGPU, 0);

			// Fast path: no masked/non-casting materials -> one indirect draw. Material ranges
			// are contiguous from zero, so summing counts is enough.
			if (_alphaTestedMaterials.Count == 0 && _nonCastingMaterials.Count == 0)
			{
				cmd.SetPipelineState(_shadowMaterial);
				cmd.CommitShaderResources(_shadowMaterial);

				var drawRange = new MaterialDrawRange();
				foreach (var kvp in cullResult.MaterialDrawRanges)
				{
					drawRange.DrawCount += kvp.Value.DrawCount;
				}

				cmd.DrawIndexedIndirect(cullResult.IndirectArgsBuffers, drawRange, IndexType.UInt32);
			}
			else
			{
				// Per-material draws; merging contiguous solid ranges would save only a few calls.
				foreach (var kvp in cullResult.MaterialDrawRanges)
				{
					if (kvp.Value.DrawCount == 0)
					{
						continue;
					}

					if (_nonCastingMaterials.Contains(kvp.Key))
					{
						continue;
					}

					var material = _alphaTestedMaterials.TryGetValue(kvp.Key, out var masked)
						? masked
						: _shadowMaterial;

					cmd.SetPipelineState(material);
					cmd.CommitShaderResources(material);
					cmd.DrawIndexedIndirect(cullResult.IndirectArgsBuffers, kvp.Value, IndexType.UInt32);
				}
			}

			// DepthRead, not ShaderResource: Vulkan depth SRVs need DEPTH_STENCIL_READ_ONLY_OPTIMAL
			// (VUID-VkDescriptorImageInfo-imageLayout-00344).
			cmd.TransitionResource(_shadowMaps, ResourceState.DepthRead);
		}

		/// <summary>Renders ONE punctual shadow slice; the caller uploads the slice matrix as
		/// CascadeMatrix[0]. Empty slices are still cleared - the pass runs for every frame slice.</summary>
		public void ExecuteDrawPunctualShadow(
			ICommandBuffer cmd,
			DiligentBufferHandle megaVertexBufferGPU,
			DiligentBufferHandle megaIndexBufferGPU,
			CullResult cullResult,
			uint sliceIndex)
		{
			if (sliceIndex >= LightClusters.MaxShadowSlices) return;

			EnsurePunctualShadowMaps();
			EnsurePunctualShadowMaterial();

			cmd.TransitionResource(_punctualShadowMaps, ResourceState.DepthWrite);

			cmd.TransitionResource(cullResult.FinallyInstancesBuffer, ResourceState.VertexBuffer);
			cmd.TransitionResource(cullResult.GpuInstancesDataBuffer, ResourceState.ShaderResource);
			cmd.TransitionResource(cullResult.IndirectArgsBuffers, ResourceState.IndirectArgument);

			cmd.SetRenderTarget(null, _punctualShadowMaps, 0, sliceIndex);
			cmd.SetViewport(LightClusters.ShadowMapSize, LightClusters.ShadowMapSize);
			cmd.ClearDepthStencil(_punctualShadowMaps, ClearDepthStencilFlags.Depth, 1.0f, 0, sliceIndex);

			cmd.SetVertexBuffers(0, [megaVertexBufferGPU, cullResult.FinallyInstancesBuffer], [0ul, 0ul], SetVertexBuffersFlags.Reset);
			cmd.SetIndexBuffer(megaIndexBufferGPU, 0);

			// Same masked-material logic as cascades, but with the punctual set: cascade materials
			// are biased for a 4096^2 ortho cascade and cause acne on a 1024^2 perspective slice.
			if (_punctualAlphaTestedMaterials.Count == 0 && _nonCastingMaterials.Count == 0)
			{
				cmd.SetPipelineState(_punctualShadowMaterial);
				cmd.CommitShaderResources(_punctualShadowMaterial);

				var drawRange = new MaterialDrawRange();
				foreach (var kvp in cullResult.MaterialDrawRanges)
				{
					drawRange.DrawCount += kvp.Value.DrawCount;
				}

				cmd.DrawIndexedIndirect(cullResult.IndirectArgsBuffers, drawRange, IndexType.UInt32);
			}
			else
			{
				foreach (var kvp in cullResult.MaterialDrawRanges)
				{
					if (kvp.Value.DrawCount == 0)
					{
						continue;
					}

					if (_nonCastingMaterials.Contains(kvp.Key))
					{
						continue;
					}

					var material = _punctualAlphaTestedMaterials.TryGetValue(kvp.Key, out var masked)
						? masked
						: _punctualShadowMaterial;

					cmd.SetPipelineState(material);
					cmd.CommitShaderResources(material);
					cmd.DrawIndexedIndirect(cullResult.IndirectArgsBuffers, kvp.Value, IndexType.UInt32);
				}
			}
		}

		/// <summary>Transitions the punctual shadow array (or its placeholder) to shader read.
		/// Must run before every ForwardPass: the PS declares the texture unconditionally, so the
		/// layout must be valid even when no slice was drawn (VUID-08114/00344).</summary>
		public void TransitionPunctualShadowsForRead(ICommandBuffer cmd)
		{
			cmd.TransitionResource(PunctualShadowMapsTarget, ResourceState.DepthRead);
		}

		private GraphicsStateInfo GetBaseState() => GetBaseState(2000, 2f);

		// Bias 1000: D32 constant bias scales as 2^(exp(z)-23) and perspective slices pack depth
		// near 1.0, so cascade-sized values are far too small in world terms.
		// Depth clip stays ON: with clip off, a triangle crossing a face's eye plane rasterizes
		// as a smeared near-depth blob that shadows the whole face cone.
		private GraphicsStateInfo GetPunctualBaseState() => GetBaseState(1000, 2f, depthClipDisable: false);

		private GraphicsStateInfo GetBaseState(int depthBias, float slopeScaledDepthBias,
			bool depthClipDisable = true)
		{
			return new GraphicsStateInfo
			{
				Name = "Shadow PSO",
				RenderTargetFormats = [],
				PrimitiveTopology = PrimitiveTopologyType.TriangleList,
				RasterizerState = new RasterizerStateInfo
				{
					// No culling: front-cull makes single-sided geometry (roofs, foliage)
					// transparent to light; acne is handled by bias + normal offset instead.
					CullMode = CullModeType.None,
					DepthBias = depthBias,
					SlopeScaledDepthBias = slopeScaledDepthBias,

					// Casters in front of the near plane are clamped, not clipped: the smallest
					// cascade's near plane would cut casters first and punch holes in its shadow.
					// Punctual slices keep clip ON - see GetPunctualBaseState.
					DepthClipDisable = depthClipDisable,
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
					// Unused by ShadowVS but required: Diligent derives slot 0's stride from the
					// declared elements; omitting Tangent/Color would misalign every vertex.
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
			_shadowMaps?.Release();
			_punctualShadowMaps?.Release();
			_shadowMapPlaceholder?.Release();

			_shadowMaterial?.Release();
			_punctualShadowMaterial?.Release();

			foreach (var material in _alphaTestedMaterials.Values)
			{
				material.Release();
			}
			_alphaTestedMaterials.Clear();
			_nonCastingMaterials.Clear();

			foreach (var material in _punctualAlphaTestedMaterials.Values)
			{
				material.Release();
			}
			_punctualAlphaTestedMaterials.Clear();

			_alphaTestedMaterialParams.Clear();
			_shadowBoundMaterials.Clear();
		}
	}
}