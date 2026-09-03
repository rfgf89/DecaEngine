using System;
using System.Numerics;
using System.Runtime.InteropServices;
using DecaEngine.Graphics;
using DecaEngine.Core;

namespace DecaEngine.Graphics;

/// <summary>Built-in TAAU resources (see TemporalUpscalePS.hlsl); input contract matches FSR/DLSS so a native backend can take the same graph slot.</summary>
public sealed unsafe class TemporalUpscalePassResources : IReleaseObject
{
	/// <summary>Default current-frame weight of the exponential accumulator (classic TAA 0.1).</summary>
	public const float DefaultBlendAlpha = 0.1f;

	private float _blendAlpha = DefaultBlendAlpha;

	/// <summary>Current-frame weight (0.02..0.5); lower = more stable but slower convergence and longer ghosting.</summary>
	public void SetBlendAlpha(float alpha)
	{
		_blendAlpha = Math.Clamp(alpha, 0.02f, 0.5f);
	}

	internal IMaterialObject Material { get; }

	/// <summary>Display-resolution RGBA16F accumulated frame; tonemap input when upscaling is on.</summary>
	public IRenderTarget OutputTarget { get; }

	private readonly IRenderTarget _historyTarget;
	internal IRenderTarget HistoryTarget => _historyTarget;

	// Unmanaged cbuffer + UpdateBuffer from the pass: jitter changes per frame, and SetConstant
	// would touch the SRB under an in-flight frame (same as MotionVectorPassResources).
	private readonly IBufferHandle _constantBuffer;
	private readonly TemporalUpscaleConstantsData* _constants;

	// Until the first frame the shader takes the current frame whole (see TuFrame.w),
	// otherwise it would blend with uninitialized memory.
	private bool _hasHistory;

	public TemporalUpscalePassResources(IGraphicsApi graphicsApi, IBatchRenderer batchRenderer,
		string colorTargetName, IGpuTexture sceneHdrTarget, IGpuTexture motionTarget,
		uint renderWidth, uint renderHeight, uint displayWidth, uint displayHeight)
	{
		// Own VS instance: a shared shader would be released twice on environment rebuild.
		var vs = graphicsApi.CreateShader("Temporal Upscale VS", "EditorAssets/shader",
			"SkyBackgroundVS.hlsl", ShaderObjectType.Vertex);
		var ps = graphicsApi.CreateShader("Temporal Upscale PS", "EditorAssets/shader",
			"TemporalUpscalePS.hlsl", ShaderObjectType.Pixel);

		OutputTarget = graphicsApi.CreateRenderTarget(new TextureInfo
		{
			name = colorTargetName + " Upscaled",
			width = displayWidth,
			height = displayHeight,
			format = TextureObjectFormat.R16G16B16A16Float,
		});

		_historyTarget = graphicsApi.CreateRenderTarget(new TextureInfo
		{
			name = colorTargetName + " Upscale History",
			width = displayWidth,
			height = displayHeight,
			format = TextureObjectFormat.R16G16B16A16Float,
		});

		var state = graphicsApi.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "Temporal Upscale PSO",
			RenderTargetFormats = [TextureObjectFormat.R16G16B16A16Float],
			DepthStencilFormat = TextureObjectFormat.Unknown,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = false },
			InputLayout = [],
		});

		var linearClamp = graphicsApi.CreateSampler(
			name: "Temporal Upscale Sampler",
			filter: TextureFilter.Linear,
			address: TextureAddress.Clamp,
			comparisonFunction: CompFunction.Always,
			border: Vector4.Zero);

		Material = graphicsApi.CreateMaterial("Temporal Upscale Material");
		Material.SetShader(vs, ps);
		Material.SetState(state);
		batchRenderer.BindViewConstants(Material);
		Material.SetTexture("_SceneTex", sceneHdrTarget);
		Material.SetImmutableSampler("_SceneTex", linearClamp);
		Material.SetTexture("_HistoryTex", _historyTarget);
		Material.SetImmutableSampler("_HistoryTex", linearClamp);

		// Motion vectors are nearest-Load only, no sampler needed (see TemporalUpscalePS.hlsl).
		Material.SetTexture("_MotionTex", motionTarget);

		_constantBuffer = graphicsApi.CreateBuffer(new BufferInfo
		{
			name = "TemporalUpscaleConstants",
			dynamic = false,
			type = BufferHandleType.Constant,
			access = HandleAccess.Pixel,
			sizeInBytes = (uint)sizeof(TemporalUpscaleConstantsData),
		});

		Material.SetBuffer("TemporalUpscaleConstants", _constantBuffer, HandleAccess.Pixel);
		_constants = (TemporalUpscaleConstantsData*)NativeMemory.AllocZeroed(
			1, (nuint)sizeof(TemporalUpscaleConstantsData));

		SetSizes(renderWidth, renderHeight);
		SetFrameParams(Vector2.Zero);
	}

	// Mirrors the "TemporalUpscaleConstants" cbuffer in TemporalUpscalePS.hlsl.
	private struct TemporalUpscaleConstantsData
	{
		// xy = render size, zw = 1/render size.
		public Vector4 Render;

		// xy = frame jitter in render pixels (y down), z = blend alpha, w = has history.
		public Vector4 Frame;
	}

	private void SetSizes(uint renderWidth, uint renderHeight)
	{
		_constants->Render = new Vector4(renderWidth, renderHeight,
			1f / Math.Max(1u, renderWidth), 1f / Math.Max(1u, renderHeight));
	}

	/// <summary>Per-frame parameters; must be called after this frame's jitter is applied.</summary>
	public void SetFrameParams(Vector2 jitterPixels)
	{
		_constants->Frame = new Vector4(jitterPixels.X, jitterPixels.Y, _blendAlpha, _hasHistory ? 1f : 0f);
		_hasHistory = true;
	}

	/// <summary>Drops the accumulator; required wherever motion vector history breaks.</summary>
	public void ResetHistory()
	{
		_hasHistory = false;
	}

	/// <summary>Resizes the display targets and the render size, dropping history.</summary>
	public void Resize(uint renderWidth, uint renderHeight, uint displayWidth, uint displayHeight)
	{
		OutputTarget.Resize(new Vector2(displayWidth, displayHeight));
		_historyTarget.Resize(new Vector2(displayWidth, displayHeight));
		SetSizes(renderWidth, renderHeight);
		ResetHistory();
	}

	/// <summary>Call after the inputs are resized: resizing recreates the native textures and
	/// the SRB would otherwise hold destroyed ones.</summary>
	public void RebindTargets(IGpuTexture sceneHdrTarget, IGpuTexture motionTarget,
		uint renderWidth, uint renderHeight, uint displayWidth, uint displayHeight)
	{
		Material.SetTexture("_SceneTex", sceneHdrTarget);
		Material.SetTexture("_MotionTex", motionTarget);
		Resize(renderWidth, renderHeight, displayWidth, displayHeight);
		Material.SetTexture("_HistoryTex", _historyTarget);
	}

	internal void WriteConstants(ICommandBuffer cmd) => cmd.UpdateBuffer(_constantBuffer, 0, _constants);

	public void Release()
	{
		Material.Release();
		OutputTarget.Release();
		_historyTarget.Release();
		_constantBuffer.Release();
		NativeMemory.Free(_constants);
	}
}

/// <summary>
/// Render graph upscaler slot: builds the display frame from render resolution plus history.
///
/// Must run after all scene post-processing and before tonemap, so accumulation happens on
/// linear light; blending past the tonemap curve would crush bright sub-pixel detail.
/// The result is copied into the history target rather than ping-ponged, because tonemap
/// keeps a single fixed input.
/// </summary>
public sealed class TemporalUpscalePass : RenderGraphPass<TemporalUpscalePass.PassData>
{
	public override string Name => "Temporal Upscale Pass";

	private readonly TemporalUpscalePassResources _resources;
	private readonly IGpuTexture _sceneHdrTarget;
	private readonly IGpuTexture _motionTarget;
	private readonly Ref<Vector2> _viewPortRef;

	public struct PassData
	{
	}

	public TemporalUpscalePass(TemporalUpscalePassResources resources, IGpuTexture sceneHdrTarget,
		IGpuTexture motionTarget, Ref<Vector2> viewPortRef)
	{
		_resources = resources;
		_sceneHdrTarget = sceneHdrTarget;
		_motionTarget = motionTarget;
		_viewPortRef = viewPortRef;
	}

	// History is both read (reprojection) and written (result copy).
	public override PassData Setup(IRenderGraphBuilder builder)
	{
		builder.ReadTarget(builder.ImportTexture(_sceneHdrTarget));
		builder.ReadTarget(builder.ImportTexture(_motionTarget));
		var history = builder.ImportTexture(_resources.HistoryTarget);
		builder.ReadTarget(history);
		builder.WriteTarget(history);
		builder.WriteTarget(builder.ImportTexture(_resources.OutputTarget));
		return default;
	}

	public override void WriteCommands(in PassData value, in IRenderGraphContext context)
	{
		var cmd = context.cmd;

		_resources.WriteConstants(cmd);

		cmd.SetRenderTarget(null, null);
		cmd.TransitionResource(_sceneHdrTarget, ResourceState.ShaderResource);
		cmd.TransitionResource(_motionTarget, ResourceState.ShaderResource);
		cmd.TransitionResource(_resources.HistoryTarget, ResourceState.ShaderResource);

		// Display viewport: this pass is the render-to-display resolution step.
		cmd.SetRenderTarget(_resources.OutputTarget, null);
		cmd.SetViewport(_viewPortRef);
		cmd.SetPipelineState(_resources.Material);
		cmd.CommitShaderResources(_resources.Material);
		cmd.Draw(3);

		cmd.SetRenderTarget(null, null);
		cmd.CopyTexture(_resources.OutputTarget, _resources.HistoryTarget);
		cmd.TransitionResource(_resources.HistoryTarget, ResourceState.ShaderResource);
	}
}
