using System.Numerics;
using System.Runtime.InteropServices;
using DecaEngine.Graphics;
using DecaEngine.Core;

namespace DecaEngine.Graphics;

/// <summary>Owns the GPU resources for the volumetric-light post-process.</summary>
// Lives across frames while VolumetricLightPass is rebuilt each frame; no own render target.
public sealed unsafe class VolumetricLightPassResources : IReleaseObject
{
	internal IMaterialObject Material { get; }

	// Filled via UpdateBuffer, not SetConstant: rebinding the SRB in flight fails Vulkan validation.
	private readonly IBufferHandle _constantBuffer;
	private readonly VolumetricConstantsData* _constants;

	// adaptationTarget may be null (LDR); _AdaptTex still gets a placeholder - an empty descriptor
	// fails Vulkan validation (VUID-08114).
	public VolumetricLightPassResources(IGraphicsApi graphicsApi, IBatchRenderer batchRenderer,
		IGpuTexture depthTarget, IGpuTexture sceneCopyTarget,
		TextureObjectFormat colorFormat, IGpuTexture? adaptationTarget, bool shadowsAvailable)
	{
		ShadowsAvailable = shadowsAvailable;

		// Own VS instance: a shared shader would be released twice when the environment is rebuilt.
		var vs = graphicsApi.CreateShader("Volumetric Fullscreen VS", "EditorAssets/shader",
			"SkyBackgroundVS.hlsl", ShaderObjectType.Vertex);
		var ps = graphicsApi.CreateShader("Volumetric PS", "EditorAssets/shader",
			"VolumetricPS.hlsl", ShaderObjectType.Pixel);

		var state = graphicsApi.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "Volumetric PSO",
			RenderTargetFormats = [colorFormat],
			DepthStencilFormat = TextureObjectFormat.Unknown,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = false },
			InputLayout = [],
		});

		Material = graphicsApi.CreateMaterial("Volumetric Material");
		Material.SetShader(vs, ps);
		Material.SetState(state);
		batchRenderer.BindViewConstants(Material);

		// Not Register(): that also pulls in instance buffers a fullscreen quad has no use for.
		batchRenderer.BindShadowResources(Material);

		var sampler = graphicsApi.CreateSampler(
			name: "Volumetric Sampler",
			filter: TextureFilter.Linear,
			address: TextureAddress.Clamp,
			comparisonFunction: CompFunction.Always,
			border: Vector4.Zero);

		Material.SetTexture("_SceneTex", sceneCopyTarget);
		Material.SetImmutableSampler("_SceneTex", sampler);
		Material.SetTexture("_DepthTex", depthTarget);
		Material.SetTexture("_AdaptTex", adaptationTarget ?? sceneCopyTarget);

		// dynamic = false: Diligent updates dynamic buffers via Map, but we need UpdateBuffer.
		_constantBuffer = graphicsApi.CreateBuffer(new BufferInfo
		{
			name = "VolumetricConstants",
			dynamic = false,
			type = BufferHandleType.Constant,
			access = HandleAccess.Pixel,
			sizeInBytes = (uint)sizeof(VolumetricConstantsData),
		});

		Material.SetBuffer("VolumetricConstants", _constantBuffer, HandleAccess.Pixel);

		_constants = (VolumetricConstantsData*)NativeMemory.AllocZeroed(1,
			(nuint)sizeof(VolumetricConstantsData));

		// The pass draws from frame one, so seed the cbuffer before any settings push arrives.
		SetParams(DefaultDensity, DefaultHeightFalloff, DefaultHeightRef, DefaultStartDistance,
			DefaultMaxDistance, DefaultSteps, DefaultMaxOpacity, DefaultShadowStrength);
		SetScattering(DefaultScattering, DefaultExtinction, DefaultAnisotropy);
		SetColors(DefaultSunColor, DefaultSunIntensity, DefaultAmbientColor, DefaultAmbientIntensity,
			DefaultAmbientShadowFloor);
		SetSun(new Vector3(0f, 1f, 0f));
		SetCamera(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
		SetExposure(adaptationTarget is not null, 0.18f);
		SetPunctualScatter(1f);
	}

	/// <summary>Whether the pipeline has a shadow pass; without it shadow strength is forced to 0.</summary>
	public bool ShadowsAvailable { get; }

	// March distance is far shorter than the analytic fog's: the step count is fixed, and
	// stretching it further gives metre-wide steps and banded shafts.
	public const float DefaultDensity = 0.01f;
	public const float DefaultHeightFalloff = 0.05f;
	public const float DefaultHeightRef = 0f;
	public const float DefaultStartDistance = 0.5f;
	public const float DefaultMaxDistance = 120f;
	public const int DefaultSteps = 48;
	public const float DefaultMaxOpacity = 0.9f;
	public const float DefaultShadowStrength = 1f;

	// Scattering and extinction are deliberately unphysical: shafts read without milking the frame.
	public const float DefaultScattering = 1f;
	public const float DefaultExtinction = 0.15f;

	/// <summary>Forward scattering, as with real haze and dust (g ~ 0.6..0.85).</summary>
	public const float DefaultAnisotropy = 0.7f;

	// Per-channel rather than Vector3: these back EditorSettings properties, which need const.
	public const float DefaultSunColorR = 1.00f, DefaultSunColorG = 0.90f, DefaultSunColorB = 0.72f;
	public const float DefaultAmbientColorR = 0.30f, DefaultAmbientColorG = 0.38f, DefaultAmbientColorB = 0.52f;

	// Ambient is kept far weaker than sun: it exists only to keep shadowed medium off pure black.
	public const float DefaultSunIntensity = 1.2f;
	public const float DefaultAmbientIntensity = 0.2f;

	/// <summary>How far the ambient term is damped inside shadowed volume; see VolumetricCommon.hlsl.</summary>
	public const float DefaultAmbientShadowFloor = 0.15f;

	public static Vector3 DefaultSunColor => new(DefaultSunColorR, DefaultSunColorG, DefaultSunColorB);
	public static Vector3 DefaultAmbientColor => new(DefaultAmbientColorR, DefaultAmbientColorG, DefaultAmbientColorB);

	// Mirrors cbuffer VolumetricConstants in VolumetricCommon.hlsl. Rows must stay Vector4:
	// SPIR-V rejects a three-component vector at an unaligned offset outright.
	private struct VolumetricConstantsData
	{
		// x - density, y - height falloff, z - height reference, w - march start.
		public Vector4 Params;

		// x - march distance, y - step count, z - scattering, w - anisotropy.
		public Vector4 March;

		public Vector4 SunColor;
		public Vector4 AmbientColor;

		// xyz - world direction TOWARDS the sun, w - shadow strength.
		public Vector4 Sun;

		// xyz - camera world right, w - opacity ceiling.
		public Vector4 Right;

		// xyz - camera world up, w - extinction.
		public Vector4 Up;

		// xyz - camera world forward, w - ambient shadow floor.
		public Vector4 Forward;

		// x - colors are exposure-relative, y - key value, z - punctual scatter scale.
		public Vector4 Exposure;
	}

	/// <summary>Medium geometry and march parameters; brightness is independent of step count.</summary>
	public void SetParams(float density, float heightFalloff, float heightRef, float startDistance,
		float maxDistance, int steps, float maxOpacity, float shadowStrength)
	{
		_constants->Params = new Vector4(MathF.Max(density, 0f), MathF.Max(heightFalloff, 0f),
			heightRef, MathF.Max(startDistance, 0f));
		_constants->March.X = MathF.Max(maxDistance, 1f);
		_constants->March.Y = Math.Clamp(steps, 4, 256);
		_constants->Right.W = Math.Clamp(maxOpacity, 0f, 1f);

		// Without a shadow pass the shadow map holds undefined data; clamp here so no caller can forget.
		_constants->Sun.W = ShadowsAvailable ? Math.Clamp(shadowStrength, 0f, 1f) : 0f;
	}

	/// <summary>Medium optics: how much light it scatters, absorbs and how directionally.</summary>
	public void SetScattering(float scattering, float extinction, float anisotropy)
	{
		_constants->March.Z = MathF.Max(scattering, 0f);
		_constants->Up.W = MathF.Max(extinction, 1e-4f);
		_constants->March.W = Math.Clamp(anisotropy, -0.95f, 0.95f);
	}

	/// <summary>Sun and ambient scattering colors and intensities, in linear space.</summary>
	public void SetColors(Vector3 sunColor, float sunIntensity, Vector3 ambientColor,
		float ambientIntensity, float ambientShadowFloor)
	{
		_constants->SunColor = new Vector4(sunColor, MathF.Max(sunIntensity, 0f));
		_constants->AmbientColor = new Vector4(ambientColor, MathF.Max(ambientIntensity, 0f));
		_constants->Forward.W = Math.Clamp(ambientShadowFloor, 0f, 1f);
	}

	/// <summary>World direction TOWARDS the sun; scene light direction points away from it.</summary>
	public void SetSun(Vector3 sunDirection)
	{
		var dir = sunDirection.LengthSquared() > 1e-8f ? Vector3.Normalize(sunDirection) : Vector3.UnitY;
		_constants->Sun = new Vector4(dir, _constants->Sun.W);
	}

	/// <summary>Ties scattering brightness to auto-exposure; key must match the tonemapper's.</summary>
	public void SetExposure(bool exposureRelative, float key)
	{
		_constants->Exposure = new Vector4(exposureRelative ? 1f : 0f, MathF.Max(key, 1e-4f),
			_constants->Exposure.Z, 0f);
	}

	/// <summary>Scale for punctual-light scattering: 0 disables it, 1 is the physical share.</summary>
	public void SetPunctualScatter(float intensity)
	{
		_constants->Exposure.Z = MathF.Max(intensity, 0f);
	}

	/// <summary>Switches the mode only, preserving the key value.</summary>
	public void SetExposureRelative(bool exposureRelative)
	{
		_constants->Exposure.X = exposureRelative ? 1f : 0f;
	}

	/// <summary>Camera world basis; vectors must be unit length and built from eye/target directly,
	/// not decomposed from the view matrix.</summary>
	public void SetCamera(Vector3 right, Vector3 up, Vector3 forward)
	{
		_constants->Right = new Vector4(right, _constants->Right.W);
		_constants->Up = new Vector4(up, _constants->Up.W);
		_constants->Forward = new Vector4(forward, _constants->Forward.W);
	}

	/// <summary>Must be called after a resize: it recreates the native textures behind these.</summary>
	public void RebindTargets(IGpuTexture depthTarget, IGpuTexture sceneCopyTarget)
	{
		Material.SetTexture("_DepthTex", depthTarget);
		Material.SetTexture("_SceneTex", sceneCopyTarget);
	}

	// Re-reads CPU memory on every replay, so per-frame values land without rebuilding the graph.
	internal void WriteConstants(ICommandBuffer cmd) => cmd.UpdateBuffer(_constantBuffer, 0, _constants);

	public void Release()
	{
		Material.Release();
		_constantBuffer.Release();
		NativeMemory.Free(_constants);
	}
}

/// <summary>
/// Render-graph pass that adds shadowed single scattering - god rays and volumetric fog - to the
/// finished frame by raymarching the view ray against the cascaded shadow map.
/// </summary>
// Must run after SSGI and after the luminance measurement (it multiplies by adaptation, so
// measuring afterwards would feed back), and before FogPass so haze also covers the shafts.
public sealed class VolumetricLightPass : RenderGraphPass<VolumetricLightPass.PassData>
{
	public override string Name => "Volumetric Light Pass";

	private readonly VolumetricLightPassResources _resources;
	private readonly IBatchRenderer _batchRenderer;
	private readonly IGpuTexture _colorTarget;
	private readonly IGpuTexture _sceneCopy;
	private readonly IGpuTexture _renderDepth;
	private readonly Ref<Vector2> _viewPortRef;

	public struct PassData
	{
	}

	public VolumetricLightPass(VolumetricLightPassResources resources, IBatchRenderer batchRenderer,
		IGpuTexture colorTarget, IGpuTexture sceneCopy, IGpuTexture renderDepth, Ref<Vector2> viewPortRef)
	{
		_resources = resources;
		_batchRenderer = batchRenderer;
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

		// Repeated even though ForwardPass already did it: passes can be disabled by name, and a
		// disabled pass performs none of its transitions.
		_batchRenderer.TransitionShadowMapsForRead(cmd);

		// Can't read and write one target; the copy is taken here so it includes the SSGI bounce.
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
