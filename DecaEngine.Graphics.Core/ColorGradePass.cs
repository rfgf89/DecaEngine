using System.Numerics;
using System.Runtime.InteropServices;
using DecaEngine.Graphics;
using DecaEngine.Core;

namespace DecaEngine.Graphics;

/// <summary>Owns the GPU resources for the final colour grading + vignette pass: one fullscreen
/// material plus its own RGBA8 display-space copy of the frame, since CopyTexture cannot cross
/// formats and the shared scene copy is RGBA16F in HDR mode.</summary>
public sealed unsafe class ColorGradePassResources : IReleaseObject
{
	internal IMaterialObject Material { get; }

	private readonly IRenderTarget _copy;
	private readonly IBufferHandle _constantBuffer;
	private readonly ColorGradeConstantsData* _constants;

	internal IRenderTarget Copy => _copy;

	public ColorGradePassResources(IGraphicsApi graphicsApi, IBatchRenderer batchRenderer,
		string colorTargetName, uint width, uint height)
	{
		// Hardcoded RGBA8: grading always runs on the displayable frame.
		_copy = graphicsApi.CreateRenderTarget(new TextureInfo
		{
			name = colorTargetName + " Grade Copy",
			width = width,
			height = height,
			format = TextureObjectFormat.R8G8B8A8UNorm,
		});

		// Own VS instance: a shared shader would be released twice when the environment is rebuilt.
		var vs = graphicsApi.CreateShader("Color Grade VS", "EditorAssets/shader", "SkyBackgroundVS.hlsl",
			ShaderObjectType.Vertex);
		var ps = graphicsApi.CreateShader("Color Grade PS", "EditorAssets/shader", "ColorGradePS.hlsl",
			ShaderObjectType.Pixel);

		var state = graphicsApi.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "Color Grade PSO",
			RenderTargetFormats = [TextureObjectFormat.R8G8B8A8UNorm],
			DepthStencilFormat = TextureObjectFormat.Unknown,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = false },
			InputLayout = [],
		});

		var sampler = graphicsApi.CreateSampler(
			name: "Color Grade Sampler",
			filter: TextureFilter.Linear,
			address: TextureAddress.Clamp,
			comparisonFunction: CompFunction.Always,
			border: Vector4.Zero);

		Material = graphicsApi.CreateMaterial("Color Grade Material");
		Material.SetShader(vs, ps);
		Material.SetState(state);
		batchRenderer.BindViewConstants(Material);
		Material.SetTexture("_SceneTex", _copy);
		Material.SetImmutableSampler("_SceneTex", sampler);

		// dynamic = false: Diligent updates dynamic buffers via Map; we need UpdateBuffer.
		_constantBuffer = graphicsApi.CreateBuffer(new BufferInfo
		{
			name = "GradeConstants",
			dynamic = false,
			type = BufferHandleType.Constant,
			access = HandleAccess.Pixel,
			sizeInBytes = (uint)sizeof(ColorGradeConstantsData),
		});

		Material.SetBuffer("GradeConstants", _constantBuffer, HandleAccess.Pixel);
		_constants = (ColorGradeConstantsData*)NativeMemory.AllocZeroed(
			1, (nuint)sizeof(ColorGradeConstantsData));

		// Defaults, used until the Graphics window pushes its first values.
		SetGrade(DefaultSaturation, DefaultContrast, DefaultGamma, DefaultTemperature, DefaultTint);
		SetTints(Vector3.Zero, Vector3.One);
		SetVignette(DefaultVignetteIntensity, DefaultVignetteRadius, DefaultVignetteSmoothness,
			DefaultVignetteRoundness);
		Resize(width, height);
	}

	/// <summary>Neutral defaults: enabling the pass must not change the frame by itself.</summary>
	public const float DefaultSaturation = 1f;
	public const float DefaultContrast = 1f;
	public const float DefaultGamma = 1f;
	public const float DefaultTemperature = 0f;
	public const float DefaultTint = 0f;
	public const float DefaultVignetteIntensity = 0f;
	public const float DefaultVignetteRadius = 0.75f;
	public const float DefaultVignetteSmoothness = 0.45f;
	public const float DefaultVignetteRoundness = 1f;

	// Mirrors cbuffer "GradeConstants" in ColorGradePS.hlsl: five float4, 80 bytes.
	private struct ColorGradeConstantsData
	{
		// x saturation, y contrast, z gamma, w temperature.
		public Vector4 Params;

		// x tint, y vignette intensity, z radius, w edge smoothness.
		public Vector4 Params2;

		// xyz additive shadow tint, w vignette aspect roundness.
		public Vector4 ShadowTint;

		// xyz multiplicative highlight tint, w reserved.
		public Vector4 HighlightTint;

		// xy target size, zw 1/xy.
		public Vector4 Target;
	}

	/// <summary>Main grading knobs; live, the cbuffer is re-read on every replay.</summary>
	public void SetGrade(float saturation, float contrast, float gamma, float temperature, float tint)
	{
		_constants->Params = new Vector4(MathF.Max(saturation, 0f), MathF.Max(contrast, 0f),
			MathF.Max(gamma, 1e-3f), Math.Clamp(temperature, -1f, 1f));
		_constants->Params2.X = Math.Clamp(tint, -1f, 1f);
	}

	/// <summary>Lift/gain: shadows are additive (neutral black), highlights multiplicative (neutral white).</summary>
	public void SetTints(Vector3 shadows, Vector3 highlights)
	{
		_constants->ShadowTint = new Vector4(shadows, _constants->ShadowTint.W);
		_constants->HighlightTint = new Vector4(highlights, 0f);
	}

	/// <summary>Vignette; roundness 1 = aspect-corrected circle, 0 = oval spanning the frame.</summary>
	public void SetVignette(float intensity, float radius, float smoothness, float roundness)
	{
		_constants->Params2.Y = Math.Clamp(intensity, 0f, 1f);
		_constants->Params2.Z = MathF.Max(radius, 1e-3f);
		_constants->Params2.W = MathF.Max(smoothness, 1e-3f);
		_constants->ShadowTint.W = Math.Clamp(roundness, 0f, 1f);
	}

	/// <summary>Resizes the frame copy and updates the size in the cbuffer.</summary>
	public void Resize(uint width, uint height)
	{
		_copy.Resize(new Vector2(width, height));
		_constants->Target = new Vector4(width, height, 1f / MathF.Max(width, 1f), 1f / MathF.Max(height, 1f));
	}

	/// <summary>Rebind after Resize: it recreates the native texture the SRB points at.</summary>
	public void RebindTargets(uint width, uint height)
	{
		Resize(width, height);
		Material.SetTexture("_SceneTex", _copy);
	}

	internal void WriteConstants(ICommandBuffer cmd) => cmd.UpdateBuffer(_constantBuffer, 0, _constants);

	public void Release()
	{
		Material.Release();
		_copy.Release();
		_constantBuffer.Release();
		NativeMemory.Free(_constants);
	}
}

/// <summary>Render-graph pass applying colour grading and a vignette to the displayable frame;
/// must run after tonemapping and before <see cref="PostOverlayPass"/>, which grading must not touch.</summary>
public sealed class ColorGradePass : RenderGraphPass<ColorGradePass.PassData>
{
	public override string Name => "Color Grade Pass";

	private readonly ColorGradePassResources _resources;
	private readonly IGpuTexture _colorTarget;
	private readonly Ref<Vector2> _viewPortRef;

	public struct PassData
	{
	}

	public ColorGradePass(ColorGradePassResources resources, IGpuTexture colorTarget, Ref<Vector2> viewPortRef)
	{
		_resources = resources;
		_colorTarget = colorTarget;
		_viewPortRef = viewPortRef;
	}

	public override PassData Setup(IRenderGraphBuilder builder)
	{
		var color = builder.ImportTexture(_colorTarget);
		builder.ReadTarget(color);
		builder.WriteTarget(color);

		var copy = builder.ImportTexture(_resources.Copy);
		builder.WriteTarget(copy);
		builder.ReadTarget(copy);

		return default;
	}

	public override void WriteCommands(in PassData value, in IRenderGraphContext context)
	{
		var cmd = context.cmd;

		_resources.WriteConstants(cmd);

		// Cannot read and write one target: go through a copy, as fog and bloom do.
		cmd.SetRenderTarget(null, null);
		cmd.CopyTexture(_colorTarget, _resources.Copy);
		cmd.TransitionResource(_resources.Copy, ResourceState.ShaderResource);

		cmd.SetRenderTarget(_colorTarget, null);
		cmd.SetViewport(_viewPortRef);
		cmd.SetPipelineState(_resources.Material);
		cmd.CommitShaderResources(_resources.Material);
		cmd.Draw(3);
	}
}
