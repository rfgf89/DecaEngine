using System.Numerics;
using DecaEngine.Graphics;
using DecaEngine.Core;

namespace DecaEngine.Graphics;

/// <summary>Ambient occlusion technique; only the AO pixel shader differs, rest of the pass is shared.</summary>
public enum AmbientOcclusionMode
{
	/// <summary>Classic spiral SSAO, counting occluding taps (SsaoCommon.hlsl).</summary>
	Ssao,

	/// <summary>Ground Truth AO: sliced horizons, cleaner on flat surfaces (GtaoCommon.hlsl).</summary>
	Gtao,
}

/// <summary>Owns the GPU resources for the SSAO post-process: the AO render target plus the two
/// fullscreen materials (depth -&gt; occlusion estimate, then multiplicative composite back into the
/// color target). Created once by <see cref="GraphicsPipelineSimple"/> when SSAO is enabled; drawn
/// inline by <see cref="ForwardPass"/> between the opaque and transmissive draws - see
/// <see cref="WriteInlineCommands"/>.
public sealed class SsaoPassResources : IReleaseObject
{
	public IRenderTarget AoTarget { get; }
	internal IMaterialObject AoMaterial { get; }
	internal IMaterialObject CompositeMaterial { get; }

	// Must match GTAO_DEPTH_MIP_LEVELS in GtaoShared.hlsl and _AoDepth0.._AoDepth4 slots.
	private const int DepthMipLevels = 5;

	// Gtao mode only; null under Ssao.
	private readonly IRenderTarget[]? _gtaoDepth;
	private readonly IRenderTarget? _gtaoDenoiseTarget;
	private readonly IMaterialObject? _gtaoPrefilterMaterial;
	private readonly IMaterialObject[]? _gtaoMipMaterials;
	private readonly IMaterialObject? _gtaoDenoiseMaterial;

	// colorFormat must match the geometry color target the composite draws into.
	public SsaoPassResources(IGraphicsApi graphicsApi, IBatchRenderer batchRenderer, string colorTargetName,
		uint width, uint height, IGpuTexture depthTarget, IGpuTexture sceneCopyTarget,
		AmbientOcclusionMode aoMode = AmbientOcclusionMode.Ssao,
		TextureObjectFormat colorFormat = TextureObjectFormat.R8G8B8A8UNorm)
	{
		var gtao = aoMode == AmbientOcclusionMode.Gtao;
		AoTarget = graphicsApi.CreateRenderTarget(new TextureInfo
		{
			name = colorTargetName + " SSAO",
			width = width,
			height = height,
			format = TextureObjectFormat.R8G8B8A8UNorm,
		});

		// One VS instance per material: Release() frees its shaders, a shared one is freed twice.
		var aoVs = graphicsApi.CreateShader("SSAO Fullscreen VS", "EditorAssets/shader", "SkyBackgroundVS.hlsl", ShaderObjectType.Vertex);
		var compositeVs = graphicsApi.CreateShader("SSAO Composite Fullscreen VS", "EditorAssets/shader", "SkyBackgroundVS.hlsl", ShaderObjectType.Vertex);

		var postProcessState = graphicsApi.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "SSAO PostProcess PSO",
			RenderTargetFormats = [TextureObjectFormat.R8G8B8A8UNorm],
			DepthStencilFormat = TextureObjectFormat.Unknown,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = false },
			InputLayout = [],
		});

		var aoShaderFile = gtao ? "GtaoPS.hlsl" : "SsaoPS.hlsl";
		var aoPs = graphicsApi.CreateShader("SSAO PS", "EditorAssets/shader", aoShaderFile, ShaderObjectType.Pixel);
		AoMaterial = graphicsApi.CreateMaterial("SSAO Material");
		AoMaterial.SetShader(aoVs, aoPs);
		AoMaterial.SetState(postProcessState);
		batchRenderer.BindViewConstants(AoMaterial);

		if (gtao)
		{
			// RGBA16F because the engine has no single-channel float format; half is enough here.
			_gtaoDepth = new IRenderTarget[DepthMipLevels];
			for (int i = 0; i < DepthMipLevels; i++)
			{
				var (w, h) = MipSize(width, height, i);
				_gtaoDepth[i] = graphicsApi.CreateRenderTarget(new TextureInfo
				{
					name = $"{colorTargetName} GTAO Depth {i}",
					width = w,
					height = h,
					format = TextureObjectFormat.R16G16B16A16Float,
				});
			}

			// Separate target: the denoiser reads a 3x3 neighbourhood of its own input.
			_gtaoDenoiseTarget = graphicsApi.CreateRenderTarget(new TextureInfo
			{
				name = colorTargetName + " GTAO Denoised",
				width = width,
				height = height,
				format = TextureObjectFormat.R8G8B8A8UNorm,
			});

			var depthChainState = graphicsApi.CreateGraphicsState(new GraphicsStateInfo
			{
				Name = "GTAO Depth Chain PSO",
				RenderTargetFormats = [TextureObjectFormat.R16G16B16A16Float],
				DepthStencilFormat = TextureObjectFormat.Unknown,
				PrimitiveTopology = PrimitiveTopologyType.TriangleList,
				RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
				DepthStencilState = new DepthStencilStateInfo { DepthEnable = false },
				InputLayout = [],
			});

			// Point sampling is required: a blend of two depths lies on neither surface.
			var pointSampler = graphicsApi.CreateSampler(
				name: "GTAO Depth Sampler",
				filter: TextureFilter.Point,
				address: TextureAddress.Clamp,
				comparisonFunction: CompFunction.Always,
				border: Vector4.Zero);

			_gtaoPrefilterMaterial = CreateFullscreenMaterial(graphicsApi, batchRenderer, depthChainState,
				"GTAO Depth Prefilter", "GtaoDepthPrefilterPS.hlsl");
			_gtaoPrefilterMaterial.SetTexture("_DepthTex", depthTarget);

			_gtaoMipMaterials = new IMaterialObject[DepthMipLevels - 1];
			for (int i = 0; i < DepthMipLevels - 1; i++)
			{
				var mip = CreateFullscreenMaterial(graphicsApi, batchRenderer, depthChainState,
					$"GTAO Depth Mip {i + 1}", "GtaoDepthMipPS.hlsl");
				mip.SetTexture("_SourceTex", _gtaoDepth[i]);
				mip.SetImmutableSampler("_SourceTex", pointSampler);
				_gtaoMipMaterials[i] = mip;
			}

			for (int i = 0; i < DepthMipLevels; i++)
			{
				AoMaterial.SetTexture($"_AoDepth{i}", _gtaoDepth[i]);
				AoMaterial.SetImmutableSampler($"_AoDepth{i}", pointSampler);
			}

			_gtaoDenoiseMaterial = CreateFullscreenMaterial(graphicsApi, batchRenderer, postProcessState,
				"GTAO Denoise", "GtaoDenoisePS.hlsl");
			_gtaoDenoiseMaterial.SetTexture("_AoTex", AoTarget);
			_gtaoDenoiseMaterial.SetImmutableSampler("_AoTex", pointSampler);
		}
		else
		{
			AoMaterial.SetTexture("_DepthTex", depthTarget);
		}

		// Composite draws into the geometry target inside ForwardPass; formats must match.
		var compositeState = graphicsApi.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "SSAO Composite PSO",
			RenderTargetFormats = [colorFormat],
			DepthStencilFormat = TextureObjectFormat.Unknown,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = false },
			InputLayout = [],
		});

		var compositePs = graphicsApi.CreateShader("SSAO Composite PS", "EditorAssets/shader", "SsaoCompositePS.hlsl", ShaderObjectType.Pixel);
		CompositeMaterial = graphicsApi.CreateMaterial("SSAO Composite Material");
		CompositeMaterial.SetShader(compositeVs, compositePs);
		CompositeMaterial.SetState(compositeState);
		batchRenderer.BindViewConstants(CompositeMaterial);

		var postProcessSampler = graphicsApi.CreateSampler(
			name: "SSAO Sampler",
			filter: TextureFilter.Linear,
			address: TextureAddress.Clamp,
			comparisonFunction: CompFunction.Always,
			border: Vector4.Zero);
		CompositeMaterial.SetTexture("_SceneTex", sceneCopyTarget);
		CompositeMaterial.SetImmutableSampler("_SceneTex", postProcessSampler);

		CompositeMaterial.SetTexture("_AoTex", FinalAoTexture);
		CompositeMaterial.SetImmutableSampler("_AoTex", postProcessSampler);
		CompositeMaterial.SetTexture("_DepthTex", depthTarget);

		// The pass draws from frame one; without this the cbuffer holds garbage until first push.
		SetWorldRange(0f);
		SetDebugView(false);
		SetMipLevelSizes(width, height);
	}

	private IGpuTexture FinalAoTexture => _gtaoDenoiseTarget ?? (IGpuTexture)AoTarget;

	private static (uint W, uint H) MipSize(uint width, uint height, int level)
	{
		uint div = 1u << level;
		return (Math.Max(width / div, 1u), Math.Max(height / div, 1u));
	}

	private static IMaterialObject CreateFullscreenMaterial(IGraphicsApi graphicsApi,
		IBatchRenderer batchRenderer, IStateObject state, string name, string pixelShaderFile)
	{
		var vs = graphicsApi.CreateShader(name + " VS", "EditorAssets/shader", "SkyBackgroundVS.hlsl",
			ShaderObjectType.Vertex);
		var ps = graphicsApi.CreateShader(name + " PS", "EditorAssets/shader", pixelShaderFile,
			ShaderObjectType.Pixel);

		var material = graphicsApi.CreateMaterial(name + " Material");
		material.SetShader(vs, ps);
		material.SetState(state);
		batchRenderer.BindViewConstants(material);
		return material;
	}

	// Mirrors the "AoConstants" cbuffer in SsaoCommon.hlsl/GtaoCommon.hlsl: exactly 16 bytes.
	private struct AoConstantsData
	{
		public float WorldRange;

		// Visibility contrast exponent; 0 means "use shader default".
		public float Power;

		// Visibility lower bound; negative means "use shader default".
		public float Floor;

		public float Pad2;
	}

	/// <summary>AO radius in world units; 0 selects the screen-relative fallback radius.</summary>
	public void SetWorldRange(float worldRange) => SetConstants(worldRange, _power, _floor);

	private float _power;
	private float _floor = -1f;

	/// <summary>Sets AO contrast exponent and visibility floor, keeping the current radius.</summary>
	public void SetStrength(float power, float floor)
	{
		_power = power;
		_floor = floor;
		SetConstants(_worldRange, power, floor);
	}

	// Mirrors the "AoComposite" cbuffer in SsaoCompositePS.hlsl: 16 bytes.
	private struct AoCompositeData
	{
		// 0 = normal composite, 1 = output raw AO as grayscale.
		public float DebugView;

		// Bilateral blur in the composite; off for GTAO, which already ran its own denoiser.
		public float Blur;

		public float Pad1;
		public float Pad2;
	}

	/// <summary>Makes the composite output raw AO instead of modulating the frame.</summary>
	public void SetDebugView(bool enabled)
	{
		var data = new AoCompositeData
		{
			DebugView = enabled ? 1f : 0f,
			Blur = _gtaoDenoiseTarget is null ? 1f : 0f,
		};
		CompositeMaterial.SetConstant("AoComposite", ref data);
	}

	// Mirrors the "GtaoLevel" cbuffer in GtaoDepthMipPS.hlsl: two float4 (32 bytes).
	private struct GtaoLevelData
	{
		// xy = size, zw = 1/xy.
		public Vector4 TargetSize;

		// xy = size, zw = 1/xy.
		public Vector4 SourceSize;
	}

	// viewData.viewport always holds the full frame size, so mips need their sizes pushed here.
	private void SetMipLevelSizes(uint width, uint height)
	{
		if (_gtaoMipMaterials is null)
		{
			return;
		}

		for (int i = 0; i < _gtaoMipMaterials.Length; i++)
		{
			var src = MipSize(width, height, i);
			var dst = MipSize(width, height, i + 1);
			var data = new GtaoLevelData
			{
				TargetSize = new Vector4(dst.W, dst.H, 1f / dst.W, 1f / dst.H),
				SourceSize = new Vector4(src.W, src.H, 1f / src.W, 1f / src.H),
			};
			_gtaoMipMaterials[i].SetConstant("GtaoLevel", ref data);
		}
	}

	private float _worldRange;

	private void SetConstants(float worldRange, float power, float floor)
	{
		_worldRange = worldRange;
		var data = new AoConstantsData { WorldRange = worldRange, Power = power, Floor = floor };
		AoMaterial.SetConstant("AoConstants", ref data);

		// Mip filter must weight depths by the same radius as the main pass, or mips over-average.
		if (_gtaoMipMaterials is not null)
		{
			foreach (var mip in _gtaoMipMaterials)
			{
				mip.SetConstant("AoConstants", ref data);
			}
		}

		_gtaoDenoiseMaterial?.SetConstant("AoConstants", ref data);
	}

	/// <summary>Rebinds resizable targets after a Resize, which recreates the native textures.</summary>
	public void RebindTargets(IGpuTexture depthTarget, IGpuTexture sceneCopyTarget)
	{
		// AoTarget is the only externally owned target; the caller already resized it.
		var size = AoTarget.Size;
		var width = (uint)size.X;
		var height = (uint)size.Y;

		if (_gtaoDepth is not null)
		{
			for (int i = 0; i < _gtaoDepth.Length; i++)
			{
				var (w, h) = MipSize(width, height, i);
				_gtaoDepth[i].Resize(new Vector2(w, h));
			}
		}

		_gtaoDenoiseTarget?.Resize(size);
		SetMipLevelSizes(width, height);

		if (_gtaoDepth is not null)
		{
			_gtaoPrefilterMaterial!.SetTexture("_DepthTex", depthTarget);
			for (int i = 0; i < _gtaoMipMaterials!.Length; i++)
			{
				_gtaoMipMaterials[i].SetTexture("_SourceTex", _gtaoDepth[i]);
			}

			for (int i = 0; i < _gtaoDepth.Length; i++)
			{
				AoMaterial.SetTexture($"_AoDepth{i}", _gtaoDepth[i]);
			}

			_gtaoDenoiseMaterial!.SetTexture("_AoTex", AoTarget);
		}
		else
		{
			AoMaterial.SetTexture("_DepthTex", depthTarget);
		}

		CompositeMaterial.SetTexture("_SceneTex", sceneCopyTarget);
		CompositeMaterial.SetTexture("_AoTex", FinalAoTexture);
		CompositeMaterial.SetTexture("_DepthTex", depthTarget);
	}

	// Must run between the opaque and transmissive draws, after opaque has been copied into
	// sceneCopyTarget and with render targets unbound; AO must not darken transmissive surfaces.
	internal void WriteInlineCommands(ICommandBuffer cmd, IGpuTexture renderColor, IGpuTexture renderDepth,
		Ref<Vector2> viewPortRef)
	{
		// DepthRead, not ShaderResource: Vulkan needs DEPTH_STENCIL_READ_ONLY_OPTIMAL for depth SRVs.
		cmd.TransitionResource(renderDepth, ResourceState.DepthRead);

		// GTAO reads only the linear depth chain; the depth buffer stops at the prefilter.
		if (_gtaoDepth is not null)
		{
			DrawToTarget(cmd, _gtaoPrefilterMaterial!, _gtaoDepth[0]);
			for (int i = 0; i < _gtaoMipMaterials!.Length; i++)
			{
				DrawToTarget(cmd, _gtaoMipMaterials[i], _gtaoDepth[i + 1]);
			}
		}

		cmd.SetRenderTarget(AoTarget, null);
		cmd.SetViewport(viewPortRef);
		cmd.SetPipelineState(AoMaterial);
		cmd.CommitShaderResources(AoMaterial);
		cmd.Draw(3);

		cmd.TransitionResource(AoTarget, ResourceState.ShaderResource);

		if (_gtaoDenoiseTarget is not null)
		{
			cmd.SetRenderTarget(_gtaoDenoiseTarget, null);
			cmd.SetViewport(viewPortRef);
			cmd.SetPipelineState(_gtaoDenoiseMaterial!);
			cmd.CommitShaderResources(_gtaoDenoiseMaterial!);
			cmd.Draw(3);

			cmd.SetRenderTarget(null, null);
			cmd.TransitionResource(_gtaoDenoiseTarget, ResourceState.ShaderResource);
		}

		cmd.SetRenderTarget(renderColor, null);
		cmd.SetViewport(viewPortRef);
		cmd.SetPipelineState(CompositeMaterial);
		cmd.CommitShaderResources(CompositeMaterial);
		cmd.Draw(3);
	}

	// Each link needs its own viewport and an SRV transition before the next link binds it.
	private static void DrawToTarget(ICommandBuffer cmd, IMaterialObject material, IRenderTarget target)
	{
		var size = target.Size;
		cmd.SetRenderTarget(target, null);
		cmd.SetViewport((uint)size.X, (uint)size.Y);
		cmd.SetPipelineState(material);
		cmd.CommitShaderResources(material);
		cmd.Draw(3);

		cmd.SetRenderTarget(null, null);
		cmd.TransitionResource(target, ResourceState.ShaderResource);
	}

	public void Release()
	{
		AoTarget.Release();
		AoMaterial.Release();
		CompositeMaterial.Release();

		_gtaoPrefilterMaterial?.Release();
		_gtaoDenoiseMaterial?.Release();
		_gtaoDenoiseTarget?.Release();

		if (_gtaoMipMaterials is not null)
		{
			foreach (var mip in _gtaoMipMaterials)
			{
				mip.Release();
			}
		}

		if (_gtaoDepth is not null)
		{
			foreach (var depth in _gtaoDepth)
			{
				depth.Release();
			}
		}
	}
}

