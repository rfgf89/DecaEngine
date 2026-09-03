using System.Numerics;
using DecaEngine.Graphics;
using DecaEngine.Core;

namespace DecaEngine.Graphics;

/// <summary>Owns the GPU resources for the SSGI post-process: the GI render target plus the two
/// fullscreen materials. <see cref="SsgiPass"/> is rebuilt every frame but only references these.</summary>
public sealed class SsgiPassResources : IReleaseObject
{
	public IRenderTarget GiTarget { get; }
	internal IMaterialObject GiMaterial { get; }
	internal IMaterialObject CompositeMaterial { get; }

	// colorFormat must stay HDR: bounce is gathered from linear HDR, RGBA8 would clip highlights.
	public SsgiPassResources(IGraphicsApi graphicsApi, IBatchRenderer batchRenderer, string colorTargetName,
		uint width, uint height, IGpuTexture depthTarget, IGpuTexture sceneCopyTarget,
		TextureObjectFormat colorFormat = TextureObjectFormat.R8G8B8A8UNorm)
	{
		GiTarget = graphicsApi.CreateRenderTarget(new TextureInfo
		{
			name = colorTargetName + " SSGI",
			width = width,
			height = height,
			format = colorFormat,
		});

		// Each material gets its own VS instance: a shared shader would be released twice.
		var giVs = graphicsApi.CreateShader("SSGI Fullscreen VS", "EditorAssets/shader", "SkyBackgroundVS.hlsl", ShaderObjectType.Vertex);
		var compositeVs = graphicsApi.CreateShader("SSGI Composite Fullscreen VS", "EditorAssets/shader", "SkyBackgroundVS.hlsl", ShaderObjectType.Vertex);

		var postProcessState = graphicsApi.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "SSGI PostProcess PSO",
			RenderTargetFormats = [colorFormat],
			DepthStencilFormat = TextureObjectFormat.Unknown,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = false },
			InputLayout = [],
		});

		var giPs = graphicsApi.CreateShader("SSGI PS", "EditorAssets/shader",
			"SsgiPS.hlsl", ShaderObjectType.Pixel);
		GiMaterial = graphicsApi.CreateMaterial("SSGI Material");
		GiMaterial.SetShader(giVs, giPs);
		GiMaterial.SetState(postProcessState);
		batchRenderer.BindViewConstants(GiMaterial);
		GiMaterial.SetTexture("_DepthTex", depthTarget);
		// Bounce source is the scene copy: per-pixel Load, so no sampler is needed.
		GiMaterial.SetTexture("_SceneTex", sceneCopyTarget);

		var compositePs = graphicsApi.CreateShader("SSGI Composite PS", "EditorAssets/shader",
			"SsgiCompositePS.hlsl", ShaderObjectType.Pixel);
		CompositeMaterial = graphicsApi.CreateMaterial("SSGI Composite Material");
		CompositeMaterial.SetShader(compositeVs, compositePs);
		CompositeMaterial.SetState(postProcessState);
		batchRenderer.BindViewConstants(CompositeMaterial);

		var postProcessSampler = graphicsApi.CreateSampler(
			name: "SSGI Sampler",
			filter: TextureFilter.Linear,
			address: TextureAddress.Clamp,
			comparisonFunction: CompFunction.Always,
			border: Vector4.Zero);
		CompositeMaterial.SetTexture("_SceneTex", sceneCopyTarget);
		CompositeMaterial.SetImmutableSampler("_SceneTex", postProcessSampler);
		CompositeMaterial.SetTexture("_GiTex", GiTarget);
		CompositeMaterial.SetImmutableSampler("_GiTex", postProcessSampler);
		CompositeMaterial.SetTexture("_DepthTex", depthTarget);

		// Seed the cbuffer: the GI pass draws from frame one, before any knob is pushed.
		SetWorldRange(0f);
		SetCompositeParams(DefaultBlurRadius, false);
	}

	public const float DefaultIntensity = 1.0f;
	public const int DefaultSampleCount = 16;
	public const float DefaultMaxLuminance = 4f;
	public const float DefaultSaturation = 0.8f;
	public const int DefaultBlurRadius = 2;

	/// <summary>Mirrors the static loop bounds in SsgiCommon.hlsl / SsgiCompositeCommon.hlsl.</summary>
	public const int MaxSampleCount = 32;
	public const int MaxBlurRadius = 3;

	// Layout of the "GiConstants" cbuffer in SsgiCommon.hlsl: 32 bytes.
	private struct GiConstantsData
	{
		public float WorldRange;
		public float Intensity;
		public float SampleCount;
		public float MaxLuminance;
		public float Saturation;
		public float Pad0, Pad1, Pad2;
	}

	// Layout of the "GiComposite" cbuffer in SsgiCompositeCommon.hlsl: exactly 16 bytes.
	private struct GiCompositeData
	{
		public float BlurRadius;
		public float DebugView;
		public float Pad0, Pad1;
	}

	private float _worldRange;
	private float _intensity = DefaultIntensity;
	private float _sampleCount = DefaultSampleCount;
	private float _maxLuminance = DefaultMaxLuminance;
	private float _saturation = DefaultSaturation;

	/// <summary>World-space GI gather radius; 0 selects the screen-relative radius fallback.</summary>
	public void SetWorldRange(float worldRange)
	{
		_worldRange = worldRange;
		PushGiConstants();
	}

	/// <summary>Live GI knobs; the radius is left unchanged. maxLuminance is a firefly clamp per
	/// tap (0 = unlimited): a single bright HDR tap otherwise turns the pass into colored snow.</summary>
	public void SetParams(float intensity, int sampleCount, float maxLuminance, float saturation)
	{
		_intensity = intensity;
		_sampleCount = Math.Clamp(sampleCount, 4, MaxSampleCount);
		_maxLuminance = maxLuminance;
		_saturation = saturation;
		PushGiConstants();
	}

	/// <summary>Live composite knobs: bilateral blur radius and the GI-only debug view.</summary>
	public void SetCompositeParams(int blurRadius, bool debugView)
	{
		var data = new GiCompositeData
		{
			BlurRadius = Math.Clamp(blurRadius, 0, MaxBlurRadius),
			DebugView = debugView ? 1f : 0f,
		};
		CompositeMaterial.SetConstant("GiComposite", ref data);
	}

	private void PushGiConstants()
	{
		var data = new GiConstantsData
		{
			WorldRange = _worldRange,
			Intensity = _intensity,
			SampleCount = _sampleCount,
			MaxLuminance = _maxLuminance,
			Saturation = _saturation,
		};
		GiMaterial.SetConstant("GiConstants", ref data);
	}

	/// <summary>Must be called AFTER a resize: it recreates the native textures the SRBs point at.</summary>
	public void RebindTargets(IGpuTexture depthTarget, IGpuTexture sceneCopyTarget)
	{
		GiMaterial.SetTexture("_DepthTex", depthTarget);
		GiMaterial.SetTexture("_SceneTex", sceneCopyTarget);
		CompositeMaterial.SetTexture("_SceneTex", sceneCopyTarget);
		CompositeMaterial.SetTexture("_GiTex", GiTarget);
		CompositeMaterial.SetTexture("_DepthTex", depthTarget);
	}

	public void Release()
	{
		GiTarget.Release();
		GiMaterial.Release();
		CompositeMaterial.Release();
	}
}

/// <summary>Render-graph pass that gathers one bounce of indirect light from the already-rendered
/// frame and composites it additively into the color target. Must run after <see cref="ForwardPass"/>
/// so the sampled frame already holds direct lighting and contact shadows.</summary>
public sealed class SsgiPass : RenderGraphPass<SsgiPass.PassData>
{
	public override string Name => "SSGI Pass";

	private readonly SsgiPassResources _resources;
	private readonly IGpuTexture _colorTarget;
	private readonly IGpuTexture _sceneCopy;
	private readonly IGpuTexture _renderDepth;
	private readonly Ref<Vector2> _viewPortRef;

	public struct PassData
	{
	}

	public SsgiPass(SsgiPassResources resources, IGpuTexture colorTarget, IGpuTexture sceneCopy,
		IGpuTexture renderDepth, Ref<Vector2> viewPortRef)
	{
		_resources = resources;
		_colorTarget = colorTarget;
		_sceneCopy = sceneCopy;
		_renderDepth = renderDepth;
		_viewPortRef = viewPortRef;
	}

	public override PassData Setup(IRenderGraphBuilder builder)
	{
		// The frame is both read (bounce source) and written (composite).
		var color = builder.ImportTexture(_colorTarget);
		builder.ReadTarget(color);
		builder.WriteTarget(color);

		// The scene copy is retaken by THIS pass, so it is both an input and an output.
		var sceneCopy = builder.ImportTexture(_sceneCopy);
		builder.WriteTarget(sceneCopy);
		builder.ReadTarget(sceneCopy);

		builder.ReadTarget(builder.ImportTexture(_renderDepth));

		var gi = builder.ImportTexture(_resources.GiTarget);
		builder.WriteTarget(gi);
		builder.ReadTarget(gi);

		return default;
	}

	public override void WriteCommands(in PassData value, in IRenderGraphContext context)
	{
		var cmd = context.cmd;

		// DepthRead, not ShaderResource: Vulkan needs DEPTH_STENCIL_READ_ONLY_OPTIMAL here.
		cmd.TransitionResource(_renderDepth, ResourceState.DepthRead);

		// Unlike AO, the scene copy must be taken BEFORE the estimate pass: the GI shader reads
		// scene color as the bounce source.
		cmd.SetRenderTarget(null, null);
		cmd.CopyTexture(_colorTarget, _sceneCopy);
		cmd.TransitionResource(_sceneCopy, ResourceState.ShaderResource);

		cmd.SetRenderTarget(_resources.GiTarget, null);
		cmd.SetViewport(_viewPortRef);
		cmd.SetPipelineState(_resources.GiMaterial);
		cmd.CommitShaderResources(_resources.GiMaterial);
		cmd.Draw(3);

		cmd.SetRenderTarget(null, null);
		cmd.TransitionResource(_resources.GiTarget, ResourceState.ShaderResource);

		cmd.SetRenderTarget(_colorTarget, null);
		cmd.SetViewport(_viewPortRef);
		cmd.SetPipelineState(_resources.CompositeMaterial);
		cmd.CommitShaderResources(_resources.CompositeMaterial);
		cmd.Draw(3);
	}
}
