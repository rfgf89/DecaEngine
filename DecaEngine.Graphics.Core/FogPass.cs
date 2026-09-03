using System.Numerics;
using System.Runtime.InteropServices;
using DecaEngine.Graphics;
using DecaEngine.Core;

namespace DecaEngine.Graphics;

/// <summary>GPU resources for the atmospheric fog post-process: one fullscreen material that reads
/// scene depth plus a copy of the frame and writes the frame back with exponential height fog and
/// sun inscattering applied.</summary>
public sealed unsafe class FogPassResources : IReleaseObject
{
	internal IMaterialObject Material { get; }

	// Owns its unmanaged memory and is filled via UpdateBuffer: the camera basis changes every
	// frame, and SetConstant would rewrite the SRB variable while a frame is still in flight
	// ("bound VkDescriptorSet was destroyed or updated").
	private readonly IBufferHandle _constantBuffer;
	private readonly FogConstantsData* _constants;

	// adaptationTarget is null in the LDR pipeline; _AdaptTex still gets a placeholder because a
	// declared slot must stay bound (Vulkan VUID-08114).
	public FogPassResources(IGraphicsApi graphicsApi, IBatchRenderer batchRenderer, IGpuTexture depthTarget,
		IGpuTexture sceneCopyTarget, TextureObjectFormat colorFormat,
		IGpuTexture? adaptationTarget)
	{
		// Own VS instance: a shared shader would be released twice.
		var vs = graphicsApi.CreateShader("Fog Fullscreen VS", "EditorAssets/shader", "SkyBackgroundVS.hlsl",
			ShaderObjectType.Vertex);
		var ps = graphicsApi.CreateShader("Fog PS", "EditorAssets/shader",
			"FogPS.hlsl", ShaderObjectType.Pixel);

		var state = graphicsApi.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "Fog PSO",
			RenderTargetFormats = [colorFormat],
			DepthStencilFormat = TextureObjectFormat.Unknown,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = false },
			InputLayout = [],
		});

		Material = graphicsApi.CreateMaterial("Fog Material");
		Material.SetShader(vs, ps);
		Material.SetState(state);
		batchRenderer.BindViewConstants(Material);

		var sampler = graphicsApi.CreateSampler(
			name: "Fog Sampler",
			filter: TextureFilter.Linear,
			address: TextureAddress.Clamp,
			comparisonFunction: CompFunction.Always,
			border: Vector4.Zero);

		Material.SetTexture("_SceneTex", sceneCopyTarget);
		Material.SetImmutableSampler("_SceneTex", sampler);
		Material.SetTexture("_DepthTex", depthTarget);
		Material.SetTexture("_AdaptTex", adaptationTarget ?? sceneCopyTarget);

		// dynamic = false: Diligent updates dynamic buffers via Map, but this one needs
		// UpdateBuffer from the command buffer (USAGE_DEFAULT).
		_constantBuffer = graphicsApi.CreateBuffer(new BufferInfo
		{
			name = "FogConstants",
			dynamic = false,
			type = BufferHandleType.Constant,
			access = HandleAccess.Pixel,
			sizeInBytes = (uint)sizeof(FogConstantsData),
		});

		Material.SetBuffer("FogConstants", _constantBuffer, HandleAccess.Pixel);

		_constants = (FogConstantsData*)NativeMemory.AllocZeroed(1, (nuint)sizeof(FogConstantsData));

		// The pass draws from frame one, so seed the cbuffer before the first knob push; the
		// default basis looks along +Z so a zero ray cannot produce NaN.
		SetParams(DefaultDensity, DefaultHeightFalloff, DefaultHeightRef, DefaultStartDistance,
			DefaultMaxDistance, DefaultMaxOpacity);
		SetColors(DefaultColor, DefaultSunColor, DefaultSunStrength, DefaultSunSharpness);
		SetSun(new Vector3(0f, 1f, 0f));
		SetCamera(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
		SetExposure(adaptationTarget is not null, 0.18f);
	}

	/// <summary>Fog knob defaults, shared with EditorSettings. Density is per world unit and
	/// tuned by eye rather than physically.</summary>
	public const float DefaultDensity = 0.012f;
	public const float DefaultHeightFalloff = 0.05f;
	public const float DefaultHeightRef = 0f;
	public const float DefaultStartDistance = 1f;
	public const float DefaultMaxDistance = 500f;
	public const float DefaultMaxOpacity = 0.9f;
	public const float DefaultSunStrength = 0.6f;
	public const float DefaultSunSharpness = 8f;

	// Per channel rather than Vector3: EditorSettings serializes these as JSON scalars and needs
	// const, not static readonly.
	public const float DefaultColorR = 0.42f, DefaultColorG = 0.50f, DefaultColorB = 0.62f;
	public const float DefaultSunColorR = 1.00f, DefaultSunColorG = 0.82f, DefaultSunColorB = 0.60f;

	public static Vector3 DefaultColor => new(DefaultColorR, DefaultColorG, DefaultColorB);
	public static Vector3 DefaultSunColor => new(DefaultSunColorR, DefaultSunColorG, DefaultSunColorB);

	// Mirrors the "FogConstants" cbuffer in FogCommon.hlsl. Every row is exactly 16 bytes:
	// SPIR-V rejects a three-component vector at an unaligned offset outright.
	private struct FogConstantsData
	{
		// x density, y height falloff, z reference height, w start distance.
		public Vector4 Params;

		// xyz medium colour, w opacity ceiling.
		public Vector4 Color;

		// xyz sun inscattering colour, w its strength.
		public Vector4 SunColor;

		// xyz world direction TOWARD the sun, w sun spot sharpness.
		public Vector4 Sun;

		// xyz camera right in world space, w max fog distance.
		public Vector4 Right;

		public Vector4 Up;
		public Vector4 Forward;

		// x colour is exposure-relative, y key value.
		public Vector4 Exposure;
	}

	/// <summary>Ties fog brightness to auto-exposure; key must match the value given to the eye
	/// adaptation and tonemap passes or the fog drifts from the frame.</summary>
	public void SetExposure(bool exposureRelative, float key)
	{
		_constants->Exposure = new Vector4(exposureRelative ? 1f : 0f, MathF.Max(key, 1e-4f), 0f, 0f);
	}

	/// <summary>Switches exposure-relative vs absolute units, keeping the key value.</summary>
	public void SetExposureRelative(bool exposureRelative)
	{
		_constants->Exposure.X = exposureRelative ? 1f : 0f;
	}

	/// <summary>Medium geometry: density, height profile, start and max distance.</summary>
	public void SetParams(float density, float heightFalloff, float heightRef, float startDistance,
		float maxDistance, float maxOpacity)
	{
		_constants->Params = new Vector4(MathF.Max(density, 0f), MathF.Max(heightFalloff, 0f),
			heightRef, MathF.Max(startDistance, 0f));
		_constants->Right.W = MathF.Max(maxDistance, 1f);
		_constants->Color.W = Math.Clamp(maxOpacity, 0f, 1f);
	}

	/// <summary>Medium and sun inscattering colours, in linear space.</summary>
	public void SetColors(Vector3 color, Vector3 sunColor, float sunStrength, float sunSharpness)
	{
		_constants->Color = new Vector4(color, _constants->Color.W);
		_constants->SunColor = new Vector4(sunColor, Math.Clamp(sunStrength, 0f, 1f));
		_constants->Sun.W = MathF.Max(sunSharpness, 0.001f);
	}

	/// <summary>World direction TOWARD the sun (scene light direction points away from it).</summary>
	public void SetSun(Vector3 sunDirection)
	{
		var dir = sunDirection.LengthSquared() > 1e-8f ? Vector3.Normalize(sunDirection) : Vector3.UnitY;
		_constants->Sun = new Vector4(dir, _constants->Sun.W);
	}

	/// <summary>Camera world basis as UNIT vectors built from eye/target, not from the view
	/// matrix: a row/column mix-up there glues the fog to the screen. Pushed every frame.</summary>
	public void SetCamera(Vector3 right, Vector3 up, Vector3 forward)
	{
		_constants->Right = new Vector4(right, _constants->Right.W);
		_constants->Up = new Vector4(up, 0f);
		_constants->Forward = new Vector4(forward, 0f);
	}

	/// <summary>Rebinds depth and scene copy after a resize, which recreates the native
	/// textures; without this the SRB would keep the destroyed ones.</summary>
	public void RebindTargets(IGpuTexture depthTarget, IGpuTexture sceneCopyTarget)
	{
		Material.SetTexture("_DepthTex", depthTarget);
		Material.SetTexture("_SceneTex", sceneCopyTarget);
	}

	// The recorded command re-reads CPU memory on every replay of a frozen buffer, so the
	// per-frame camera basis arrives without rebuilding the graph.
	internal void WriteConstants(ICommandBuffer cmd) => cmd.UpdateBuffer(_constantBuffer, 0, _constants);

	public void Release()
	{
		Material.Release();
		_constantBuffer.Release();
		NativeMemory.Free(_constants);
	}
}

/// <summary>Render-graph pass that applies atmospheric fog (aerial perspective) to the finished
/// frame. Must run after <see cref="SsgiPass"/> and before <see cref="EyeAdaptationPass"/>.</summary>
public sealed class FogPass : RenderGraphPass<FogPass.PassData>
{
	public override string Name => "Fog Pass";

	private readonly FogPassResources _resources;
	private readonly IGpuTexture _colorTarget;
	private readonly IGpuTexture _sceneCopy;
	private readonly IGpuTexture _renderDepth;
	private readonly Ref<Vector2> _viewPortRef;

	public struct PassData
	{
	}

	public FogPass(FogPassResources resources, IGpuTexture colorTarget, IGpuTexture sceneCopy,
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
		var color = builder.ImportTexture(_colorTarget);
		builder.ReadTarget(color);
		builder.WriteTarget(color);

		var sceneCopy = builder.ImportTexture(_sceneCopy);
		builder.WriteTarget(sceneCopy);
		builder.ReadTarget(sceneCopy);

		builder.ReadTarget(builder.ImportTexture(_renderDepth));
		return default;
	}

	public override void WriteCommands(in PassData value, in IRenderGraphContext context)
	{
		var cmd = context.cmd;

		_resources.WriteConstants(cmd);

		// DepthRead, not ShaderResource: Vulkan needs DEPTH_STENCIL_READ_ONLY_OPTIMAL here.
		cmd.TransitionResource(_renderDepth, ResourceState.DepthRead);

		// A target cannot be read and written at once. The copy is taken here rather than reused
		// from SSGI, which has since written its bounce into the frame.
		cmd.SetRenderTarget(null, null);
		cmd.CopyTexture(_colorTarget, _sceneCopy);
		cmd.TransitionResource(_sceneCopy, ResourceState.ShaderResource);

		cmd.SetRenderTarget(_colorTarget, null);
		cmd.SetViewport(_viewPortRef);
		cmd.SetPipelineState(_resources.Material);
		cmd.CommitShaderResources(_resources.Material);
		cmd.Draw(3);
	}
}
