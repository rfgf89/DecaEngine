using System.Numerics;
using System.Runtime.InteropServices;
using DecaEngine.Graphics;
using Diligent;
using DecaEngine.Core;

namespace DecaEngine.Graphics;

/// <summary>GPU resources for stochastic screen-space reflections. Requires the reflection
/// G-buffer and motion vectors; with rayTraced the caller must bind the scene via
/// <see cref="SetRayScene"/> before the first frame.</summary>
public sealed unsafe class SsrPassResources : IReleaseObject
{
	public IRenderTarget TraceTarget { get; }

	/// <summary>Trace hit buffer (RT1): hit UV or octahedral RT direction, ray PDF, mask.</summary>
	public IRenderTarget RayHitTarget { get; }
	public IRenderTarget ResolveTarget { get; }
	public IRenderTarget HistoryTarget { get; }

	/// <summary>Half-res blurred scene copy: the cone level for rough rays.</summary>
	public IRenderTarget SceneBlurTarget { get; }
	internal IMaterialObject BlurMaterial { get; }

	internal uint Width { get; private set; }
	internal uint Height { get; private set; }

	internal IMaterialObject TraceMaterial { get; }
	internal IMaterialObject ResolveMaterial { get; }
	internal IMaterialObject CompositeMaterial { get; }

	/// <summary>Whether materials were built with the RT fallback; changing it needs a rebuild.</summary>
	public bool RayTraced { get; }

	/// <summary>RT hit albedo mode the materials were built with: 0 off, 1 tile atlas,
	/// 2 bindless array. Changing it needs a rebuild; enabling it requires
	/// <see cref="SetHitTextures"/>.</summary>
	public int HitTextureMode { get; }

	/// <summary>Must match `_SceneHitTex[64]` in SsrTracePS.hlsl.</summary>
	public const int MaxHitTextures = 64;

	// A declared Texture2DArray slot only accepts an array view, hence a 1x1x2 placeholder.
	private readonly IGpuTexture? _hitAtlasPlaceholder;

	// White, not the env map: an unbound slot then renders as the flat factor colour.
	private readonly IGpuTexture? _hitTexPlaceholder;

	// Owns its unmanaged memory and is filled via UpdateBuffer: the noise phase changes every
	// frame, and SetConstant would rewrite the SRB variable under an in-flight frame.
	private readonly IBufferHandle _constantBuffer;
	private readonly SsrConstantsData* _constants;

	// Placeholder for the probe atlas slots: a declared slot must stay bound (Vulkan VUID-08114).
	private readonly IGpuTexture _environmentMap;

	/// <summary>SSR knob defaults, shared with EditorSettings.</summary>
	public const float DefaultIntensity = 1.0f;
	public const float DefaultMaxRoughness = 1.0f;
	public const int DefaultRaysPerPixel = 2;
	public const float DefaultThickness = 0.35f;
	public const float DefaultMaxDistance = 30.0f;
	public const float DefaultHistoryWeight = 0.9f;

	/// <summary>Total RT ray bounces: primary plus one mirror bounce off metallic hits.</summary>
	public const int DefaultRtBounces = 2;

	// Mirrors the "SsrConstants" cbuffer in SsrCommon.hlsl - change both together.
	[StructLayout(LayoutKind.Sequential)]
	private struct SsrConstantsData
	{
		public float FrameIndex;
		public float MaxRoughness;
		public float Thickness;
		public float MaxDistance;
		public float EnvYaw;
		public float HistoryWeight;
		public float DebugView;
		public float Intensity;
		public Vector4 SunDirWorld;
		public Vector4 SunColor;
		public float RaysPerPixel;

		// Total RT ray bounces (1..4).
		public float Bounces;

		// 0 - screen march then RT for misses; 1 - RT only. RT trace variant only.
		public float TraceMode;
		public float Pad2;
		public Vector4 ProbeOrigin;
		public Vector4 ProbeCell;
		public Vector4 ProbeCounts;

		// Previous frame's viewProj; identity until the first latch.
		public Matrix4x4 PrevViewProj;
	}

	public SsrPassResources(IGraphicsApi graphicsApi, IBatchRenderer batchRenderer, string colorTargetName,
		uint width, uint height, IGpuTexture depthTarget, IGpuTexture normalRoughTarget,
		IGpuTexture envFactorTarget, IGpuTexture sceneCopyTarget, IGpuTexture motionTarget,
		IGpuTexture environmentMap, bool rayTraced,
		TextureObjectFormat colorFormat = TextureObjectFormat.R16G16B16A16Float,
		int hitTextures = 0)
	{
		RayTraced = rayTraced;
		_environmentMap = environmentMap;
		Width = width;
		Height = height;

		// Hit textures only exist in the RT variant; the mode falls back to 0 on a backend
		// without CreateTextureArray, before keywords are picked.
		hitTextures = rayTraced ? Math.Clamp(hitTextures, 0, 2) : 0;
		if (hitTextures == 1)
		{
			var white = new byte[] { 255, 255, 255, 255 };
			_hitAtlasPlaceholder = graphicsApi.CreateTextureArray(
				colorTargetName + " SSR HitAtlas Placeholder", 1, 1, new[] { white, white });
			if (_hitAtlasPlaceholder is null)
			{
				hitTextures = 0;
			}
		}
		else if (hitTextures == 2)
		{
			_hitTexPlaceholder = graphicsApi.CreateTexture2DWithMips(
				colorTargetName + " SSR HitTex Placeholder",
				new[] { new byte[] { 255, 255, 255, 255 } }, 1, 1);
		}

		HitTextureMode = hitTextures;

		TraceTarget = graphicsApi.CreateRenderTarget(new TextureInfo
		{
			name = colorTargetName + " SSR Trace",
			width = width,
			height = height,
			format = TextureObjectFormat.R16G16B16A16Float,
		});

		RayHitTarget = graphicsApi.CreateRenderTarget(new TextureInfo
		{
			name = colorTargetName + " SSR RayHit",
			width = width,
			height = height,
			format = TextureObjectFormat.R16G16B16A16Float,
		});

		ResolveTarget = graphicsApi.CreateRenderTarget(new TextureInfo
		{
			name = colorTargetName + " SSR Resolve",
			width = width,
			height = height,
			format = TextureObjectFormat.R16G16B16A16Float,
		});

		HistoryTarget = graphicsApi.CreateRenderTarget(new TextureInfo
		{
			name = colorTargetName + " SSR History",
			width = width,
			height = height,
			format = TextureObjectFormat.R16G16B16A16Float,
		});

		SceneBlurTarget = graphicsApi.CreateRenderTarget(new TextureInfo
		{
			name = colorTargetName + " SSR Scene Blur",
			width = Math.Max(1u, width / 2),
			height = Math.Max(1u, height / 2),
			format = TextureObjectFormat.R16G16B16A16Float,
		});

		// Each material gets its own VS instance: a shared shader would be released twice.
		// The RT keyword also goes on the VS for DXC stage parity - a RayQuery PS compiles as
		// DXC/SM6.5, and a PSO pairing it with an FXC vertex shader will not create.
		var traceKeywords = HitTextureMode switch
		{
			1 => new[] { "FEATURE_RT_REFLECTIONS", "FEATURE_RT_HIT_ATLAS" },
			2 => new[] { "FEATURE_RT_REFLECTIONS", "FEATURE_RT_HIT_BINDLESS" },
			_ => new[] { "FEATURE_RT_REFLECTIONS" },
		};
		var traceSuffix = HitTextureMode switch { 1 => " [RT+Atlas]", 2 => " [RT+Bindless]", _ => " [RT]" };

		var traceVs = rayTraced
			? graphicsApi.CreateShader("SSR Trace Fullscreen VS [RT]", "EditorAssets/shader", "SkyBackgroundVS.hlsl",
				ShaderObjectType.Vertex, "Main", new[] { "FEATURE_RT_REFLECTIONS" })
			: graphicsApi.CreateShader("SSR Trace Fullscreen VS", "EditorAssets/shader", "SkyBackgroundVS.hlsl", ShaderObjectType.Vertex);
		var resolveVs = graphicsApi.CreateShader("SSR Resolve Fullscreen VS", "EditorAssets/shader", "SkyBackgroundVS.hlsl", ShaderObjectType.Vertex);
		var blurVs = graphicsApi.CreateShader("SSR Blur Fullscreen VS", "EditorAssets/shader", "SkyBackgroundVS.hlsl", ShaderObjectType.Vertex);
		var compositeVs = graphicsApi.CreateShader("SSR Composite Fullscreen VS", "EditorAssets/shader", "SkyBackgroundVS.hlsl", ShaderObjectType.Vertex);

		// The trace writes two MRTs: colour+confidence and the hit buffer.
		var traceState = graphicsApi.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "SSR Trace PSO",
			RenderTargetFormats = [TextureObjectFormat.R16G16B16A16Float, TextureObjectFormat.R16G16B16A16Float],
			DepthStencilFormat = TextureObjectFormat.Unknown,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = false },
			InputLayout = [],
		});

		var ssrTargetState = graphicsApi.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "SSR Resolve PSO",
			RenderTargetFormats = [TextureObjectFormat.R16G16B16A16Float],
			DepthStencilFormat = TextureObjectFormat.Unknown,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = false },
			InputLayout = [],
		});

		var compositeState = graphicsApi.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "SSR Composite PSO",
			RenderTargetFormats = [colorFormat],
			DepthStencilFormat = TextureObjectFormat.Unknown,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = false },
			InputLayout = [],
		});

		_constantBuffer = graphicsApi.CreateBuffer(new BufferInfo
		{
			name = "SsrConstants",
			dynamic = false,
			type = BufferHandleType.Constant,
			access = HandleAccess.Pixel,
			sizeInBytes = (uint)sizeof(SsrConstantsData),
		});

		_constants = (SsrConstantsData*)NativeMemory.AllocZeroed(1, (nuint)sizeof(SsrConstantsData));
		_constants->MaxRoughness = DefaultMaxRoughness;
		_constants->Thickness = DefaultThickness;
		_constants->MaxDistance = DefaultMaxDistance;
		_constants->HistoryWeight = DefaultHistoryWeight;
		_constants->Intensity = DefaultIntensity;
		_constants->SunDirWorld = new Vector4(0f, 1f, 0f, 0f);
		_constants->SunColor = new Vector4(0f, 0f, 0f, 0.55f);
		_constants->RaysPerPixel = DefaultRaysPerPixel;
		_constants->Bounces = DefaultRtBounces;
		_constants->PrevViewProj = Matrix4x4.Identity;

		var linearClamp = graphicsApi.CreateSampler(
			name: "SSR Sampler",
			filter: TextureFilter.Linear,
			address: TextureAddress.Clamp,
			comparisonFunction: CompFunction.Always,
			border: Vector4.Zero);

		var tracePs = rayTraced
			? graphicsApi.CreateShader("SSR Trace PS" + traceSuffix, "EditorAssets/shader", "SsrTracePS.hlsl",
				ShaderObjectType.Pixel, "Main", traceKeywords)
			: graphicsApi.CreateShader("SSR Trace PS", "EditorAssets/shader", "SsrTracePS.hlsl", ShaderObjectType.Pixel);
		TraceMaterial = graphicsApi.CreateMaterial("SSR Trace Material");
		TraceMaterial.SetShader(traceVs, tracePs);
		TraceMaterial.SetState(traceState);
		batchRenderer.BindViewConstants(TraceMaterial);
		// Punctual lights shade RT hits; the screen-space variant ignores these bindings.
		batchRenderer.BindShadowResources(TraceMaterial);
		TraceMaterial.SetBuffer("SsrConstants", _constantBuffer, HandleAccess.Pixel);
		TraceMaterial.SetTexture("_DepthTex", depthTarget);
		TraceMaterial.SetTexture("_NormalRoughTex", normalRoughTarget);
		TraceMaterial.SetTexture("_EnvFactorTex", envFactorTarget);
		TraceMaterial.SetTexture("_SceneTex", sceneCopyTarget);
		TraceMaterial.SetTexture("_SceneBlurTex", SceneBlurTarget);
		TraceMaterial.SetImmutableSampler("_SceneBlurTex", linearClamp);
		// Bilinear: the hit colour is sampled at the sub-pixel UV of the refined intersection,
		// and an integer-pixel Load shimmered on glossy surfaces.
		TraceMaterial.SetImmutableSampler("_SceneTex", linearClamp);
		// Unread in the screen-space variant, but a declared slot must stay bound (VUID-08114).
		TraceMaterial.SetTexture("_EnvMap", environmentMap);
		TraceMaterial.SetImmutableSampler("_EnvMap", linearClamp);
		TraceMaterial.SetTexture("_ProbeSh0", environmentMap);
		TraceMaterial.SetTexture("_ProbeSh1", environmentMap);
		TraceMaterial.SetTexture("_ProbeSh2", environmentMap);
		TraceMaterial.SetTexture("_ProbeSh3", environmentMap);

		// RT hit texture slots need a live descriptor before the first draw.
		if (HitTextureMode == 1)
		{
			var hitWrap = graphicsApi.CreateSampler(
				name: "SSR HitAtlas Sampler",
				filter: TextureFilter.Linear,
				address: TextureAddress.Wrap,
				comparisonFunction: CompFunction.Always,
				border: Vector4.Zero);
			TraceMaterial.SetImmutableSampler("_SceneHitAtlas", hitWrap);
			TraceMaterial.SetTexture("_SceneHitAtlas", _hitAtlasPlaceholder!);
		}
		else if (HitTextureMode == 2)
		{
			SetHitTextures(null, null);
		}

		// Same RGBA16F format, so the resolve PSO fits the half-res blur too.
		var blurPs = graphicsApi.CreateShader("SSR Scene Blur PS", "EditorAssets/shader", "SsrSceneBlurPS.hlsl", ShaderObjectType.Pixel);
		BlurMaterial = graphicsApi.CreateMaterial("SSR Scene Blur Material");
		BlurMaterial.SetShader(blurVs, blurPs);
		BlurMaterial.SetState(ssrTargetState);
		batchRenderer.BindViewConstants(BlurMaterial);
		BlurMaterial.SetBuffer("SsrConstants", _constantBuffer, HandleAccess.Pixel);
		BlurMaterial.SetTexture("_SceneTex", sceneCopyTarget);
		BlurMaterial.SetImmutableSampler("_SceneTex", linearClamp);

		var resolvePs = graphicsApi.CreateShader("SSR Resolve PS", "EditorAssets/shader", "SsrResolvePS.hlsl", ShaderObjectType.Pixel);
		ResolveMaterial = graphicsApi.CreateMaterial("SSR Resolve Material");
		ResolveMaterial.SetShader(resolveVs, resolvePs);
		ResolveMaterial.SetState(ssrTargetState);
		batchRenderer.BindViewConstants(ResolveMaterial);
		ResolveMaterial.SetBuffer("SsrConstants", _constantBuffer, HandleAccess.Pixel);
		ResolveMaterial.SetTexture("_DepthTex", depthTarget);
		ResolveMaterial.SetTexture("_TraceTex", TraceTarget);
		ResolveMaterial.SetTexture("_RayHitTex", RayHitTarget);
		ResolveMaterial.SetTexture("_NormalRoughTex", normalRoughTarget);
		ResolveMaterial.SetTexture("_HistoryTex", HistoryTarget);
		ResolveMaterial.SetImmutableSampler("_HistoryTex", linearClamp);
		ResolveMaterial.SetTexture("_MotionTex", motionTarget);

		var compositePs = graphicsApi.CreateShader("SSR Composite PS", "EditorAssets/shader", "SsrCompositePS.hlsl", ShaderObjectType.Pixel);
		CompositeMaterial = graphicsApi.CreateMaterial("SSR Composite Material");
		CompositeMaterial.SetShader(compositeVs, compositePs);
		CompositeMaterial.SetState(compositeState);
		batchRenderer.BindViewConstants(CompositeMaterial);
		CompositeMaterial.SetBuffer("SsrConstants", _constantBuffer, HandleAccess.Pixel);
		CompositeMaterial.SetTexture("_DepthTex", depthTarget);
		CompositeMaterial.SetTexture("_SceneTex", sceneCopyTarget);
		CompositeMaterial.SetTexture("_SsrTex", ResolveTarget);
		CompositeMaterial.SetTexture("_NormalRoughTex", normalRoughTarget);
		CompositeMaterial.SetTexture("_EnvFactorTex", envFactorTarget);
		CompositeMaterial.SetTexture("_EnvMap", environmentMap);
		CompositeMaterial.SetImmutableSampler("_EnvMap", linearClamp);
	}

	/// <summary>Live SSR knobs; values reach the GPU on the next constant buffer upload.</summary>
	public void SetParams(float intensity, float maxRoughness, float thickness, float maxDistance,
		float historyWeight, int raysPerPixel, int debugView, int rtBounces = DefaultRtBounces,
		int traceMode = 0)
	{
		_constants->Intensity = intensity;
		_constants->MaxRoughness = Math.Clamp(maxRoughness, 0.05f, 1f);
		_constants->Thickness = Math.Max(0.01f, thickness);
		_constants->MaxDistance = Math.Max(0.5f, maxDistance);
		_constants->HistoryWeight = Math.Clamp(historyWeight, 0f, 0.97f);
		_constants->RaysPerPixel = Math.Clamp(raysPerPixel, 1, 4);
		_constants->DebugView = debugView;
		_constants->Bounces = Math.Clamp(rtBounces, 1, 4);

		// Without the RT variant the screen march is the only source of reflections.
		_constants->TraceMode = RayTraced ? Math.Clamp(traceMode, 0, 1) : 0;
	}

	/// <summary>Env map yaw; must match the PbrEnvYaw used by the forward pass.</summary>
	public void SetEnvironmentYaw(float yawRadians)
	{
		_constants->EnvYaw = yawRadians;
	}

	/// <summary>Sun for RT hit shading: direction toward the sun, colour, off-screen ambient.</summary>
	public void SetSun(Vector3 dirTowardSun, Vector3 color, float ambient, float sunTanHalfAngle = 0f)
	{
		// w is the TANGENT of the sun's half angle: RT shadow rays scatter within that cone.
		_constants->SunDirWorld = new Vector4(dirTowardSun, Math.Max(sunTanHalfAngle, 0f));
		_constants->SunColor = new Vector4(color, ambient);
	}

	/// <summary>Advances the noise phase; must be called exactly once per frame.</summary>
	public void AdvanceFrame()
	{
		_constants->FrameIndex = (_constants->FrameIndex + 1f) % 4096f;
	}

	/// <summary>Latches this frame's viewProj; the cbuffer receives the previous frame's.</summary>
	public void UpdateFromView(in Matrix4x4 viewProj)
	{
		_constants->PrevViewProj = _hasPrevViewProj ? _prevViewProj : viewProj;
		_prevViewProj = viewProj;
		_hasPrevViewProj = true;
	}

	private Matrix4x4 _prevViewProj;
	private bool _hasPrevViewProj;

	/// <summary>Probe field lighting for RT hits; null atlases reset the slots to a placeholder.
	/// Must be called before releasing the atlases, or the SRB keeps destroyed textures.</summary>
	public void SetProbeField(IGpuTexture? sh0, IGpuTexture? sh1, IGpuTexture? sh2, IGpuTexture? sh3,
		Vector4 origin, Vector4 cell, Vector4 counts)
	{
		bool present = sh0 is not null && sh1 is not null && sh2 is not null && sh3 is not null;
		_constants->ProbeOrigin = present ? origin : Vector4.Zero;
		_constants->ProbeCell = present ? cell : new Vector4(1f, 1f, 1f, 0f);
		_constants->ProbeCounts = present ? counts : new Vector4(2f, 2f, 2f, 0f);

		TraceMaterial.SetTexture("_ProbeSh0", present ? sh0! : _environmentMap);
		TraceMaterial.SetTexture("_ProbeSh1", present ? sh1! : _environmentMap);
		TraceMaterial.SetTexture("_ProbeSh2", present ? sh2! : _environmentMap);
		TraceMaterial.SetTexture("_ProbeSh3", present ? sh3! : _environmentMap);
	}

	/// <summary>Textured albedo for RT hits; null arguments reset the slots to placeholders.
	/// Must be called before releasing the atlas or textures, or the SRB keeps dead views.</summary>
	public void SetHitTextures(IGpuTexture? atlas, IReadOnlyList<IGpuTexture?>? textures)
	{
		if (!RayTraced || HitTextureMode == 0)
		{
			return;
		}

		if (HitTextureMode == 1)
		{
			TraceMaterial.SetTexture("_SceneHitAtlas", atlas ?? _hitAtlasPlaceholder!);
			return;
		}

		var slots = new IGpuTexture[MaxHitTextures];
		for (int i = 0; i < MaxHitTextures; i++)
		{
			slots[i] = (textures != null && i < textures.Count ? textures[i] : null)
				?? _hitTexPlaceholder ?? _environmentMap;
		}

		TraceMaterial.SetTextureSrvArray("_SceneHitTex", slots);
	}

	/// <summary>Binds the TLAS and scene attribute tables; required when <see cref="RayTraced"/>.
	/// Rebuilding the TLAS keeps the binding valid, but recreating it needs another call.</summary>
	public void SetRayScene(ITopLevelAS tlas, IBuffer meshTriangles, IBuffer instances)
	{
		if (!RayTraced)
		{
			return;
		}

		TraceMaterial.SetAccelStructure("_SceneTlas", tlas);
		TraceMaterial.SetStructuredBufferSrv("_SceneMeshTriangles", meshTriangles);
		TraceMaterial.SetStructuredBufferSrv("_SceneInstances", instances);
	}

	// The recorded command re-reads CPU memory on every replay of a frozen buffer.
	internal void WriteConstants(ICommandBuffer cmd) => cmd.UpdateBuffer(_constantBuffer, 0, _constants);

	/// <summary>Rebinds and resizes targets; call after the pipeline has resized its own.</summary>
	public void RebindTargets(IGpuTexture depthTarget, IGpuTexture normalRoughTarget,
		IGpuTexture envFactorTarget, IGpuTexture sceneCopyTarget, IGpuTexture motionTarget,
		uint width, uint height)
	{
		Width = width;
		Height = height;
		TraceTarget.Resize(new Vector2(width, height));
		RayHitTarget.Resize(new Vector2(width, height));
		SceneBlurTarget.Resize(new Vector2(Math.Max(1u, width / 2), Math.Max(1u, height / 2)));
		ResolveTarget.Resize(new Vector2(width, height));
		HistoryTarget.Resize(new Vector2(width, height));

		TraceMaterial.SetTexture("_DepthTex", depthTarget);
		TraceMaterial.SetTexture("_NormalRoughTex", normalRoughTarget);
		TraceMaterial.SetTexture("_EnvFactorTex", envFactorTarget);
		TraceMaterial.SetTexture("_SceneTex", sceneCopyTarget);
		TraceMaterial.SetTexture("_SceneBlurTex", SceneBlurTarget);
		BlurMaterial.SetTexture("_SceneTex", sceneCopyTarget);
		// The env map is not rebound: a viewport resize does not recreate it.

		ResolveMaterial.SetTexture("_DepthTex", depthTarget);
		ResolveMaterial.SetTexture("_TraceTex", TraceTarget);
		ResolveMaterial.SetTexture("_RayHitTex", RayHitTarget);
		ResolveMaterial.SetTexture("_NormalRoughTex", normalRoughTarget);
		ResolveMaterial.SetTexture("_HistoryTex", HistoryTarget);
		ResolveMaterial.SetTexture("_MotionTex", motionTarget);

		CompositeMaterial.SetTexture("_DepthTex", depthTarget);
		CompositeMaterial.SetTexture("_SceneTex", sceneCopyTarget);
		CompositeMaterial.SetTexture("_SsrTex", ResolveTarget);
		CompositeMaterial.SetTexture("_NormalRoughTex", normalRoughTarget);
		CompositeMaterial.SetTexture("_EnvFactorTex", envFactorTarget);
	}

	public void Release()
	{
		TraceTarget.Release();
		RayHitTarget.Release();
		SceneBlurTarget.Release();
		BlurMaterial.Release();
		ResolveTarget.Release();
		HistoryTarget.Release();
		TraceMaterial.Release();
		ResolveMaterial.Release();
		CompositeMaterial.Release();
		_hitAtlasPlaceholder?.Release();
		_hitTexPlaceholder?.Release();
		_constantBuffer.Release();
		NativeMemory.Free(_constants);
	}
}

/// <summary>Render-graph pass for stochastic screen-space reflections. Must run after
/// <see cref="SsgiPass"/> and <see cref="MotionVectorPass"/> but before the luminance
/// measurement, so reflections feed into exposure.</summary>
public sealed class SsrPass : RenderGraphPass<SsrPass.PassData>
{
	public override string Name => "SSR Pass";

	private readonly SsrPassResources _resources;
	private readonly IGpuTexture _colorTarget;
	private readonly IGpuTexture _sceneCopy;
	private readonly IGpuTexture _renderDepth;
	private readonly IGpuTexture _normalRough;
	private readonly IGpuTexture _envFactor;
	private readonly IGpuTexture _motionTarget;
	private readonly Ref<Vector2> _viewPortRef;

	public struct PassData
	{
	}

	public SsrPass(SsrPassResources resources, IGpuTexture colorTarget, IGpuTexture sceneCopy,
		IGpuTexture renderDepth, IGpuTexture normalRough, IGpuTexture envFactor,
		IGpuTexture motionTarget, Ref<Vector2> viewPortRef)
	{
		_resources = resources;
		_colorTarget = colorTarget;
		_sceneCopy = sceneCopy;
		_renderDepth = renderDepth;
		_normalRough = normalRough;
		_envFactor = envFactor;
		_motionTarget = motionTarget;
		_viewPortRef = viewPortRef;
	}

	public override PassData Setup(IRenderGraphBuilder builder)
	{
		var color = builder.ImportTexture(_colorTarget);
		builder.ReadTarget(color);
		builder.WriteTarget(color);

		// This pass retakes the scene copy itself, so it is both an input and an output.
		var sceneCopy = builder.ImportTexture(_sceneCopy);
		builder.WriteTarget(sceneCopy);
		builder.ReadTarget(sceneCopy);

		builder.ReadTarget(builder.ImportTexture(_renderDepth));
		builder.ReadTarget(builder.ImportTexture(_normalRough));
		builder.ReadTarget(builder.ImportTexture(_envFactor));
		builder.ReadTarget(builder.ImportTexture(_motionTarget));

		var sceneBlur = builder.ImportTexture(_resources.SceneBlurTarget);
		builder.WriteTarget(sceneBlur);
		builder.ReadTarget(sceneBlur);

		var trace = builder.ImportTexture(_resources.TraceTarget);
		builder.WriteTarget(trace);
		builder.ReadTarget(trace);

		var rayHit = builder.ImportTexture(_resources.RayHitTarget);
		builder.WriteTarget(rayHit);
		builder.ReadTarget(rayHit);

		var resolve = builder.ImportTexture(_resources.ResolveTarget);
		builder.WriteTarget(resolve);
		builder.ReadTarget(resolve);

		var history = builder.ImportTexture(_resources.HistoryTarget);
		builder.WriteTarget(history);
		builder.ReadTarget(history);

		return default;
	}

	public override void WriteCommands(in PassData value, in IRenderGraphContext context)
	{
		var cmd = context.cmd;

		_resources.WriteConstants(cmd);

		// Fresh copy: reflections must include the AO/GI composites and transmissive draws,
		// not just the opaque refraction snapshot.
		cmd.SetRenderTarget(null, null);
		cmd.CopyTexture(_colorTarget, _sceneCopy);
		cmd.TransitionResource(_sceneCopy, ResourceState.ShaderResource);

		// DepthRead, not ShaderResource: Vulkan needs DEPTH_STENCIL_READ_ONLY_OPTIMAL here.
		cmd.TransitionResource(_renderDepth, ResourceState.DepthRead);
		cmd.TransitionResource(_normalRough, ResourceState.ShaderResource);
		cmd.TransitionResource(_envFactor, ResourceState.ShaderResource);
		cmd.TransitionResource(_motionTarget, ResourceState.ShaderResource);

		// First frame history is undefined, but the resolve clamps it to this frame's AABB.
		cmd.TransitionResource(_resources.HistoryTarget, ResourceState.ShaderResource);

		// The blur has its own half-res viewport.
		cmd.SetRenderTarget(_resources.SceneBlurTarget, null);
		cmd.SetViewport(Math.Max(1u, _resources.Width / 2), Math.Max(1u, _resources.Height / 2));
		cmd.SetPipelineState(_resources.BlurMaterial);
		cmd.CommitShaderResources(_resources.BlurMaterial);
		cmd.Draw(3);

		cmd.SetRenderTarget(null, null);
		cmd.TransitionResource(_resources.SceneBlurTarget, ResourceState.ShaderResource);

		cmd.SetRenderTargets([_resources.TraceTarget, _resources.RayHitTarget], null);
		cmd.SetViewport(_viewPortRef);
		cmd.SetPipelineState(_resources.TraceMaterial);
		cmd.CommitShaderResources(_resources.TraceMaterial);
		cmd.Draw(3);

		cmd.SetRenderTarget(null, null);
		cmd.TransitionResource(_resources.TraceTarget, ResourceState.ShaderResource);
		cmd.TransitionResource(_resources.RayHitTarget, ResourceState.ShaderResource);

		cmd.SetRenderTarget(_resources.ResolveTarget, null);
		cmd.SetViewport(_viewPortRef);
		cmd.SetPipelineState(_resources.ResolveMaterial);
		cmd.CommitShaderResources(_resources.ResolveMaterial);
		cmd.Draw(3);

		cmd.SetRenderTarget(null, null);
		cmd.CopyTexture(_resources.ResolveTarget, _resources.HistoryTarget);
		cmd.TransitionResource(_resources.ResolveTarget, ResourceState.ShaderResource);

		cmd.SetRenderTarget(_colorTarget, null);
		cmd.SetViewport(_viewPortRef);
		cmd.SetPipelineState(_resources.CompositeMaterial);
		cmd.CommitShaderResources(_resources.CompositeMaterial);
		cmd.Draw(3);
	}
}
