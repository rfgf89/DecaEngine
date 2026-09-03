using System.Numerics;
using DecaEngine.Graphics;
using DecaEngine.Core;

namespace DecaEngine.Graphics;

/// <summary>GPU resources for the final HDR -&gt; display conversion: exposure, tonemap curve and
/// the manual sRGB encode. In LDR mode this pass is absent and UnlitInstancedPS.hlsl does both.</summary>
public sealed class TonemapPassResources : IReleaseObject
{
	internal IMaterialObject Material { get; }

	public TonemapPassResources(IGraphicsApi graphicsApi, IBatchRenderer batchRenderer, IGpuTexture hdrColorTarget,
		IGpuTexture adaptationTarget)
	{
		var vs = graphicsApi.CreateShader("Tonemap Fullscreen VS", "EditorAssets/shader", "SkyBackgroundVS.hlsl", ShaderObjectType.Vertex);
		var ps = graphicsApi.CreateShader("Tonemap PS", "EditorAssets/shader", "TonemapPS.hlsl", ShaderObjectType.Pixel);

		// Displayable RGBA8, no depth and no MSAA: the frame is already resolved by this point.
		var state = graphicsApi.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "Tonemap PSO",
			RenderTargetFormats = [TextureObjectFormat.R8G8B8A8UNorm],
			DepthStencilFormat = TextureObjectFormat.Unknown,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = false },
			InputLayout = [],
		});

		// Tonemap is the pipeline's upscale point: bilinear lifts a sub-scale HDR frame to display.
		var sampler = graphicsApi.CreateSampler(
			name: "Tonemap Scene Sampler",
			filter: TextureFilter.Linear,
			address: TextureAddress.Clamp,
			comparisonFunction: CompFunction.Always,
			border: Vector4.Zero);

		Material = graphicsApi.CreateMaterial("Tonemap Material");
		Material.SetShader(vs, ps);
		Material.SetState(state);
		batchRenderer.BindViewConstants(Material);
		Material.SetTexture("_SceneTex", hdrColorTarget);
		Material.SetImmutableSampler("_SceneTex", sampler);
		Material.SetTexture("_AdaptTex", adaptationTarget);

		// The pass draws from frame one, so the cbuffer must not stay uninitialized.
		SetParams(0.18f, 0f);
	}

	// Must match the "TonemapConstants" cbuffer layout in TonemapPS.hlsl.
	private struct TonemapConstantsData
	{
		public float Key;
		public float ExposureCompensation;

		// Flags below are floats: >0.5 means on.
		public float Passthrough;

		// Curve id, see Tonemap.hlsl: 0 PBR Neutral, 1 ACES, 2 AgX.
		public float Curve;

		public float AutoExposure;
		public float ForceOpaque;

		public float Pad2;
		public float Pad3;
	}

	private float _key = 0.18f;
	private float _exposureCompensation;
	private bool _passthrough;
	private int _curve;
	private bool _autoExposure = true;
	private bool _forceOpaque;

	/// <summary>Forces frame alpha to 1; required after FSR, whose output carries alpha 0.</summary>
	public void SetForceOpaque(bool forceOpaque)
	{
		_forceOpaque = forceOpaque;
		PushConstants();
	}

	/// <summary>Switches between measured-luminance and manual exposure; the measuring chain stays
	/// in the graph either way.</summary>
	public void SetAutoExposure(bool autoExposure)
	{
		_autoExposure = autoExposure;
		PushConstants();
	}

	/// <summary>Key value and exposure compensation; must match
	/// <see cref="EyeAdaptationPassResources.SetParams"/>.</summary>
	public void SetParams(float key, float exposureCompensation)
	{
		_key = key;
		_exposureCompensation = exposureCompensation;
		PushConstants();
	}

	/// <summary>Copies the frame through untouched, for debug views that already write display
	/// values.</summary>
	public void SetPassthrough(bool passthrough)
	{
		_passthrough = passthrough;
		PushConstants();
	}

	/// <summary>Tonemap curve: 0 PBR Neutral, 1 ACES, 2 AgX. Runtime branch, not a shader variant.</summary>
	public void SetCurve(int curve)
	{
		_curve = curve;
		PushConstants();
	}

	private void PushConstants()
	{
		var data = new TonemapConstantsData
		{
			Key = _key,
			ExposureCompensation = _exposureCompensation,
			Passthrough = _passthrough ? 1f : 0f,
			Curve = _curve,
			AutoExposure = _autoExposure ? 1f : 0f,
			ForceOpaque = _forceOpaque ? 1f : 0f,
		};
		Material.SetConstant("TonemapConstants", ref data, HandleAccess.Pixel);
	}

	/// <summary>Call after resizing the HDR target: resize recreates the native texture, so the
	/// SRB would otherwise hold a destroyed one.</summary>
	public void RebindTargets(IGpuTexture hdrColorTarget)
	{
		Material.SetTexture("_SceneTex", hdrColorTarget);
	}

	public void Release()
	{
		Material.Release();
	}
}

/// <summary>Converts the linear HDR frame into the displayable color target. Must run last:
/// after it ColorTarget is in display space.</summary>
public sealed class TonemapPass : RenderGraphPass<TonemapPass.PassData>
{
	public override string Name => "Tonemap Pass";

	private readonly TonemapPassResources _resources;
	private readonly IGpuTexture _hdrColorTarget;
	private readonly IGpuTexture _colorTarget;
	private readonly Ref<Vector2> _viewPortRef;

	public struct PassData
	{
	}

	public TonemapPass(TonemapPassResources resources, IGpuTexture hdrColorTarget, IGpuTexture colorTarget,
		Ref<Vector2> viewPortRef)
	{
		_resources = resources;
		_hdrColorTarget = hdrColorTarget;
		_colorTarget = colorTarget;
		_viewPortRef = viewPortRef;
	}

	public override PassData Setup(IRenderGraphBuilder builder)
	{
		builder.ReadTarget(builder.ImportTexture(_hdrColorTarget));
		builder.WriteTarget(builder.ImportTexture(_colorTarget));
		return default;
	}

	public override void WriteCommands(in PassData value, in IRenderGraphContext context)
	{
		var cmd = context.cmd;

		cmd.SetRenderTarget(null, null);
		cmd.TransitionResource(_hdrColorTarget, ResourceState.ShaderResource);

		cmd.SetRenderTarget(_colorTarget, null);
		cmd.SetViewport(_viewPortRef);
		cmd.SetPipelineState(_resources.Material);
		cmd.CommitShaderResources(_resources.Material);
		cmd.Draw(3);
	}
}
