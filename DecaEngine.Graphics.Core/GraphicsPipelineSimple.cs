using System.Numerics;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
using DecaEngine.Core;

namespace DecaEngine.Graphics;

/// <summary>Pipeline features togglable on a LIVE pipeline via <see cref="GraphicsPipelineSimple.SetFeatures"/>:
/// resources are created lazily on first enable and the graph rebuild is cheap
/// (native textures are pooled, see <see cref="IRenderGraph.ResetPasses"/>).
/// Eye adaptation is the one non-structural feature - see <see cref="EyeAdaptation"/>.</summary>
public struct PipelineFeatures : IEquatable<PipelineFeatures>
{
	/// <summary>World key-light shadows (<see cref="ShadowPass"/>).</summary>
	public bool Shadows;

	/// <summary>Environment background, drawn inline inside <see cref="ForwardPass"/>.</summary>
	public bool SkyBackground;

	/// <summary>Screen-space contact occlusion, inline inside <see cref="ForwardPass"/>.</summary>
	public bool Ssao;

	/// <summary>AO technique used when <see cref="Ssao"/> is on; changing it recreates the AO
	/// materials (the shader is baked into them) but not the environment.</summary>
	public AmbientOcclusionMode AoMode;

	public bool Ssgi;

	/// <summary>Stochastic screen-space reflections (<see cref="SsrPass"/>). Requires
	/// <see cref="MotionVectors"/> - without them the resources are silently not created.</summary>
	public bool Ssr;

	/// <summary>RT fallback for <see cref="Ssr"/>: off-screen rays continue via inline RayQuery
	/// over the scene TLAS (SM6.5). Changing it recreates the SSR materials, like AoMode.
	/// The enabler MUST bind the scene via <see cref="SsrPassResources.SetRayScene"/>.</summary>
	public bool SsrRayTraced;

	/// <summary>RT-hit albedo mode: 0 = per-triangle averaged, 1 = downsampled atlas,
	/// 2 = bindless full-size array (silently degrades to atlas without runtime arrays).
	/// Changing it recreates the SSR materials; the enabler MUST bind textures via
	/// <see cref="SsrPassResources.SetHitTextures"/>.</summary>
	public int SsrHitTextures;

	/// <summary>Expose the frame by MEASURED luminance instead of manual exposure. Non-structural:
	/// the measurement chain always exists in the HDR pipeline, so toggling this never rebuilds
	/// the graph - it only switches tonemap/fog/bloom/god-rays exposure modes.</summary>
	public bool EyeAdaptation;

	public bool Fog;
	public bool Volumetric;
	public bool Bloom;
	public bool ColorGrade;

	/// <summary>Screen motion vectors (<see cref="MotionVectorPass"/>) - input of temporal
	/// techniques (DLSS/FSR, TAA). The pass only fills its buffer; no MSAA exists here,
	/// the upscaler is its own anti-aliasing.</summary>
	public bool MotionVectors;

	/// <summary>Temporal upscale (<see cref="TemporalUpscalePass"/>). Requires
	/// <see cref="MotionVectors"/> (otherwise resources are not created) and enables jitter
	/// itself. Built-in managed backend of the upscaler slot; FSR/DLSS use the same input
	/// contract (HDR frame, vectors, jitter, resolution pair).</summary>
	public bool TemporalUpscale;

	public bool Equals(PipelineFeatures other)
	{
		return Shadows == other.Shadows &&
		       SkyBackground == other.SkyBackground &&
		       Ssao == other.Ssao &&
		       AoMode == other.AoMode &&
		       Ssgi == other.Ssgi &&
		       Ssr == other.Ssr &&
		       SsrRayTraced == other.SsrRayTraced &&
		       SsrHitTextures == other.SsrHitTextures &&
		       EyeAdaptation == other.EyeAdaptation &&
		       Fog == other.Fog &&
		       Volumetric == other.Volumetric &&
		       Bloom == other.Bloom &&
		       ColorGrade == other.ColorGrade &&
		       MotionVectors == other.MotionVectors &&
		       TemporalUpscale == other.TemporalUpscale;
	}

	/// <summary>True when the sets differ by anything but <see cref="EyeAdaptation"/>,
	/// i.e. a graph rebuild is needed (see <see cref="GraphicsPipelineSimple.SetFeatures"/>).</summary>
	public bool StructurallyEquals(PipelineFeatures other)
	{
		var a = this;
		var b = other;
		a.EyeAdaptation = false;
		b.EyeAdaptation = false;
		return a.Equals(b);
	}

	public override bool Equals(object? obj) => obj is PipelineFeatures other && Equals(other);

	public override int GetHashCode()
	{
		var hash = new HashCode();
		hash.Add(Shadows);
		hash.Add(SkyBackground);
		hash.Add(Ssao);
		hash.Add((int)AoMode);
		hash.Add(Ssgi);
		hash.Add(Ssr);
		hash.Add(SsrRayTraced);
		hash.Add(SsrHitTextures);
		hash.Add(EyeAdaptation);
		hash.Add(Fog);
		hash.Add(Volumetric);
		hash.Add(Bloom);
		hash.Add(ColorGrade);
		hash.Add(MotionVectors);
		hash.Add(TemporalUpscale);
		return hash.ToHashCode();
	}
}

/// <summary>
/// <see cref="IGraphicsPipeline"/> without <see cref="ShadowPass"/> - for off-screen consumers
/// (see <see cref="DecaEngine.Editor.ModelViewportEnvironment"/>) that only ever draw unlit
/// geometry through <see cref="SimpleCullingAndRenderSystem"/> and never need shadow-cascade
/// culling/rendering. The <see cref="DirectionalLightCascadeData"/> passed to
/// <see cref="SignalGraph"/> is ignored.
///
/// Off-screen mode is ALWAYS HDR: geometry and post live in linear RGBA16F
/// (<see cref="PipelineRenderTargets.HdrColorTarget"/>) and <see cref="TonemapPass"/> writes the
/// display RGBA8 target last, so feature toggles never force a geometry-PSO rebuild.
/// </summary>
public class GraphicsPipelineSimple : IGraphicsPipeline
{
	private readonly IGraphicsApi _api;
	private readonly IBatchRenderer _batchRenderer;
	private readonly IRenderGraph _renderGraph;
	private readonly PipelineRenderTargets? _targets;
	private readonly string? _colorTargetName;
	private readonly IGpuTexture? _environmentMap;
	private readonly Vector4 _clearColor;

	private PipelineFeatures _features;

	// Feature resources: created lazily on first enable and kept across disables (re-enable is
	// free, see EnsureResources). VRAM is returned only by an explicit ReleaseDisabledResources.
	private SkyPassResources? _skyResources;
	private SsaoPassResources? _ssaoResources;
	private SsgiPassResources? _ssgiResources;
	private SsrPassResources? _ssrResources;
	private FogPassResources? _fogResources;
	private VolumetricLightPassResources? _volumetricResources;
	private BloomPassResources? _bloomResources;
	private ColorGradePassResources? _gradeResources;
	private EyeAdaptationPassResources? _eyeAdaptationResources;
	private TonemapPassResources? _tonemapResources;
	private MotionVectorPassResources? _motionVectorResources;
	private MotionVectorDebugPassResources? _motionVectorDebugResources;
	private TemporalUpscalePassResources? _temporalUpscaleResources;

	/// <summary>Native upscaler backend (FSR); replaces the built-in TAAU in the graph when set.
	/// The pipeline owns it (Release on both release paths). See <see cref="SetNativeUpscaler"/>.</summary>
	private INativeUpscalerBackend? _nativeUpscaler;

	// AO technique the current _ssaoResources were built for; changing it requires a rebuild
	// (the shader is baked into the materials).
	private AmbientOcclusionMode _ssaoBuiltMode;

	// Whether the current _ssrResources were built with the RT fallback.
	private bool _ssrBuiltRayTraced;

	// RT-hit texture mode the current _ssrResources were built with (after EnsureResources clamps).
	private int _ssrBuiltHitTextures;

	// Whether shadows were available when _volumetricResources were built: its material takes the
	// cascaded shadow map via IBatchRenderer.BindShadowResources.
	private bool _volumetricBuiltWithShadows;

	// Last data the graph was built from: SetFeatures rebuilds immediately, not waiting for the
	// next SignalGraph (which only happens when the camera count changes).
	private DirectionalLightCascadeData _lastCascadeData;
	private RenderCamerasData _lastCameras;
	private bool _hasGraphData;

	private Ref<Vector2> _viewPortRef;

	/// <summary>SCENE viewport - for passes drawing at render resolution. Display-resolution
	/// passes (tonemap, grade, vector debug, overlays) stay on <see cref="_viewPortRef"/>:
	/// tonemap is the upscale point (see TonemapPS.hlsl).</summary>
	private Ref<Vector2> _renderViewPortRef;

	/// <summary>Fraction of display resolution the scene renders at - see <see cref="SetRenderScale"/>.</summary>
	private float _renderScale = 1f;

	// Temporal jitter - see SetTemporalJitter. The remembered matrix pair distinguishes "Execute
	// without a new Update" (viewData still holds OUR jittered matrix - unwind it first or offsets
	// would accumulate) from "Update rewrote viewData" (fresh clean matrix - nothing to unwind).
	// Exact compare is safe: Update recomputes view*proj bit-identically from the same numbers.
	private bool _jitterEnabled;
	private uint _jitterFrameIndex;
	private Vector2 _jitterPixels;
	private Matrix4x4 _jitteredViewProj;
	private Matrix4x4 _unjitteredViewProj;

	// Disabled passes - BY NAME, outliving the graph: rebuilds recreate pass objects (each with
	// Enabled=true), so flags are stored here and re-applied after every rebuild.
	private readonly Dictionary<string, bool> _passEnabled = new();

	// Outlives the graph for the same reason as _passEnabled: ShadowPass is recreated on every
	// rebuild while the cascade schedule is shared with the view-build system.
	private readonly ShadowCascadeSchedule _cascadeSchedule = new();

	/// <summary>Non-null only in off-screen mode (<paramref name="colorTargetName"/> given to the
	/// constructor) - the pipeline owns and creates these itself, the same way a swap chain would own
	/// the back buffer, so off-screen consumers (see <see cref="DecaEngine.Editor.ModelViewportEnvironment"/>)
	/// resize/bind them through here rather than creating their own.</summary>
	public PipelineRenderTargets? Targets => _targets;

	/// <summary>Current feature set - see <see cref="SetFeatures"/>.</summary>
	public PipelineFeatures Features => _features;

	/// <summary>Shadow-cascade redraw schedule: its frame mask is written by
	/// CullingAndRenderSystem and read by the <see cref="ShadowPass"/> replay callback. While
	/// nobody writes the mask, cascades are drawn every frame.</summary>
	public ShadowCascadeSchedule CascadeSchedule => _cascadeSchedule;

	/// <summary>HDR pipeline (linear frame + separate tonemap) - always in off-screen mode,
	/// never on the swap chain.</summary>
	public bool HdrPipeline => _targets?.HdrColorTarget is not null;

	/// <summary>Whether the frame is exposed by measured luminance - see <see cref="PipelineFeatures.EyeAdaptation"/>.</summary>
	public bool AutoExposure => _features.EyeAdaptation && HdrPipeline;

	/// <summary>Non-null only when a sky background was enabled (see <see cref="SkyPassResources"/>);
	/// lets the preview viewport push the environment yaw into the sky shader.</summary>
	public SkyPassResources? SkyResources => _skyResources;

	/// <summary>Non-null once SSAO has been enabled at least once (resources survive disables).
	/// Live knobs can be pushed even while disabled: they land in cbuffers and revive with the pass.</summary>
	public SsaoPassResources? SsaoResources => _ssaoResources;

	/// <summary>See <see cref="SsaoResources"/>.</summary>
	public SsgiPassResources? SsgiResources => _ssgiResources;

	/// <summary>See <see cref="SsaoResources"/>. Also the viewport's route for live SSR knobs,
	/// env-map yaw, RT-fallback sun and the scene TLAS (SetRayScene).</summary>
	public SsrPassResources? SsrResources => _ssrResources;

	/// <summary>See <see cref="SsaoResources"/>.</summary>
	public FogPassResources? FogResources => _fogResources;

	/// <summary>See <see cref="SsaoResources"/>.</summary>
	public VolumetricLightPassResources? VolumetricResources => _volumetricResources;

	/// <summary>See <see cref="SsaoResources"/>.</summary>
	public BloomPassResources? BloomResources => _bloomResources;

	/// <summary>See <see cref="SsaoResources"/>. Works in BOTH pipelines: ColorTarget is always
	/// RGBA8 display-space.</summary>
	public ColorGradePassResources? ColorGradeResources => _gradeResources;

	/// <summary>ALWAYS non-null in off-screen mode: the luminance-measurement chain is part of the
	/// HDR pipeline, not a feature (see <see cref="PipelineFeatures.EyeAdaptation"/>).</summary>
	public EyeAdaptationPassResources? EyeAdaptationResources => _eyeAdaptationResources;

	/// <summary>Non-null once motion vectors have been enabled at least once; source of the
	/// upscaler's input buffer and the debug vector view.</summary>
	public MotionVectorPassResources? MotionVectorResources => _motionVectorResources;

	/// <summary>Motion-vector debug fill - created together with <see cref="MotionVectorResources"/>.
	/// Display is toggled via its live knob <see cref="MotionVectorDebugPassResources.SetDebugView"/>,
	/// not via the feature set: no graph rebuild for a debug checkbox.</summary>
	public MotionVectorDebugPassResources? MotionVectorDebugResources => _motionVectorDebugResources;

	/// <summary>Non-null after the first enable of <see cref="PipelineFeatures.TemporalUpscale"/> -
	/// and only on a pipeline that has motion vectors (see EnsureResources).</summary>
	public TemporalUpscalePassResources? TemporalUpscaleResources => _temporalUpscaleResources;

	/// <summary>Hands the pipeline a native upscaler backend (or null to return to TAAU) and
	/// rebuilds the graph. The pipeline takes ownership (it calls Release). The caller must wait
	/// for the GPU first if the pipeline has already drawn - swapping the tonemap input on a live
	/// material (see needsWait in SetFeatures).</summary>
	public void SetNativeUpscaler(INativeUpscalerBackend? backend)
	{
		if (ReferenceEquals(_nativeUpscaler, backend))
		{
			return;
		}

		_nativeUpscaler?.Release();
		_nativeUpscaler = backend;

		if (_hasGraphData)
		{
			RebuildGraph();
		}
	}

	/// <summary>Current native backend (null = built-in TAAU).</summary>
	public INativeUpscalerBackend? NativeUpscaler => _nativeUpscaler;

	/// <summary>Non-null exactly when <see cref="EyeAdaptationResources"/> is: the final tonemap
	/// exists only in the HDR pipeline (on the swap chain UnlitInstancedPS.hlsl does it itself).</summary>
	public TonemapPassResources? TonemapResources => _tonemapResources;

	/// <summary>Sub-pixel projection jitter - a live knob without a graph rebuild: the offset is
	/// written into camera 0's viewProj in native memory before each Execute and the frozen
	/// SetupViewData re-reads it on every replay (see <see cref="ApplyTemporalJitter"/>).
	/// Without temporal accumulation the image visibly shakes sub-pixel - expected; the knob
	/// exists to debug the jitter and its NON-leakage into motion vectors.</summary>
	public void SetTemporalJitter(bool enabled)
	{
		_jitterEnabled = enabled;
	}

	/// <summary>This frame's offset in PIXELS (y down, as frame rows go), range [-0.5..0.5).
	/// Exactly the value an upscaler must receive as jitter offset. Vector2.Zero while jitter is off.</summary>
	public Vector2 CurrentJitterPixels => _jitterPixels;

	/// <summary>Inline overlay on top of geometry (see ForwardPass), e.g. the probe debug view.
	/// Read at graph-command RECORD time, so after changing it the caller must call
	/// <see cref="InvalidateGraph"/> - frozen commands keep replaying the old value otherwise.</summary>
	public Action<ICommandBuffer>? InlineOverlay { get; set; }

	/// <summary>
	/// SECOND inline overlay, executed right after <see cref="InlineOverlay"/> - animation/physics
	/// debug lines (see DebugLineOverlay in the editor). A separate property because the two hooks
	/// are owned by DIFFERENT viewport subsystems that clear them independently; a shared slot
	/// would let disabling probes silently kill the lines.
	/// </summary>
	public Action<ICommandBuffer>? DebugOverlay { get; set; }

	/// <summary>Overlay as a SEPARATE pass at the very end of the frame, on top of the finished
	/// display ColorTarget (Scene View selection outline etc., see <see cref="PostOverlayPass"/>).
	/// The hook binds its own targets/viewport. Read at graph-command RECORD time - after changing
	/// it the caller must call <see cref="InvalidateGraph"/>, same as <see cref="InlineOverlay"/>.</summary>
	public Action<ICommandBuffer>? PostOverlay { get; set; }

	public GraphicsPipelineSimple(IGraphicsApi api, IBatchRenderer batchRenderer, string? debugName = null)
		: this(api, batchRenderer, null, null, 0, 0, new Vector4(0.1f, 0.1f, 0.1f, 1f), debugName: debugName)
	{
	}

	/// <param name="colorTargetName">Non-null selects off-screen mode: the pipeline creates and owns
	/// its own color/depth/scene-copy targets instead of drawing to the swap chain's back
	/// buffer (see <see cref="DecaEngine.Editor.ModelViewportEnvironment"/>). Null draws straight to
	/// the back buffer and every other creation parameter below is ignored.</param>
	/// <param name="debugName">Pipeline name in the render-graph debug window (see
	/// <see cref="GraphicsPipelineRegistry"/>). Null derives it from <paramref name="colorTargetName"/>.</param>
	public GraphicsPipelineSimple(IGraphicsApi api, IBatchRenderer batchRenderer, string? colorTargetName,
		string? depthTargetName, uint width, uint height, Vector4 clearColor,
		bool skyBackground = false, IGpuTexture? environmentMap = null, bool ssao = false, bool enableShadowPass = false,
		AmbientOcclusionMode aoMode = AmbientOcclusionMode.Ssao, bool ssgi = false, bool eyeAdaptation = false,
		bool fog = false, bool bloom = false, bool colorGrade = false, bool volumetric = false,
		bool motionVectors = false, bool temporalUpscale = false, bool ssr = false,
		bool ssrRayTraced = false, string? debugName = null)
	{
		_api = api;
		_batchRenderer = batchRenderer;
		_clearColor = clearColor;
		_colorTargetName = colorTargetName;
		_environmentMap = environmentMap;
		_renderGraph = _api.CreateRenderGraph();

		_features = new PipelineFeatures
		{
			Shadows = enableShadowPass,
			SkyBackground = skyBackground,
			Ssao = ssao,
			AoMode = aoMode,
			Ssgi = ssgi,
			EyeAdaptation = eyeAdaptation,
			Fog = fog,
			Volumetric = volumetric,
			Bloom = bloom,
			ColorGrade = colorGrade,
			MotionVectors = motionVectors,
			TemporalUpscale = temporalUpscale,
			Ssr = ssr,
			SsrRayTraced = ssrRayTraced,
		};

		if (colorTargetName is not null)
		{
			// hdr: true unconditionally - see the class comment: the color-target format no longer
			// depends on the feature set, so toggles never rebuild geometry PSOs.
			_targets = new PipelineRenderTargets(api, colorTargetName, depthTargetName!, width, height,
				hdr: true);

			_viewPortRef = new Ref<Vector2>(new Vector2(width, height));

			// Own instance: render scale changes only this one, leaving the display viewport alone.
			_renderViewPortRef = new Ref<Vector2>(new Vector2(width, height));
		}
		else
		{
			_viewPortRef = new Ref<Vector2>(_api.WindowHandle.Size);

			// Swap chain has no render scale - both viewports DELIBERATELY share the same native
			// memory; OnViewportChange updates both at once.
			_renderViewPortRef = _viewPortRef;
			_api.WindowHandle.OnWindowResize += OnViewportChange;
		}

		EnsureResources();

		// Self-register: the render-graph debug window builds its list from the registry, which
		// holds a weak reference and does not extend the pipeline's lifetime.
		GraphicsPipelineRegistry.Register(this, debugName ?? DefaultDebugName(colorTargetName));
	}

	/// <summary>Debug-window name when none was given: off-screen color targets are already named
	/// after their consumer ("Model Preview Color", ...), so just drop the " Color" suffix.</summary>
	private static string DefaultDebugName(string? colorTargetName)
	{
		if (string.IsNullOrWhiteSpace(colorTargetName))
		{
			return "Simple (swap chain)";
		}

		var name = colorTargetName!.Trim();
		return name.EndsWith(" Color", StringComparison.OrdinalIgnoreCase) ? name[..^" Color".Length] : name;
	}

	/// <summary>Format of the target geometry and post-processing draw into.</summary>
	private TextureObjectFormat RenderColorFormat =>
		_targets?.RenderColorFormat ?? TextureObjectFormat.R8G8B8A8UNorm;

	/// <summary>Current off-screen target size. Resources of a feature enabled after a viewport
	/// resize must be created at the ACTUAL size, not the constructor's.</summary>
	private (uint Width, uint Height) TargetSize
	{
		get
		{
			var size = _targets!.ColorTarget.Size;
			return (Math.Max(1u, (uint)size.X), Math.Max(1u, (uint)size.Y));
		}
	}

	/// <summary>Actual SCENE target size - taken from the depth target, not derived from the scale:
	/// the caller resizes targets (see <see cref="SetRenderScale"/>), so between a scale change and
	/// the resize only the target itself knows the truth.</summary>
	private (uint Width, uint Height) RenderTargetSize
	{
		get
		{
			var size = _targets!.DepthTarget.Size;
			return (Math.Max(1u, (uint)size.X), Math.Max(1u, (uint)size.Y));
		}
	}

	/// <summary>Creates resources required by the current <see cref="_features"/> and rebuilds those
	/// whose baked state changed (AO technique, shadow availability for god rays). Existing
	/// resources of disabled features are left alone: re-enabling is free.</summary>
	private void EnsureResources()
	{
		if (_features.SkyBackground && _skyResources is null && _environmentMap is not null)
		{
			_skyResources = new SkyPassResources(_api, _batchRenderer, _environmentMap,
				RenderColorFormat);
		}

		if (_targets is null)
		{
			// Swap chain: no owned targets, and all post-processing needs at least frame/depth
			// copies - only ForwardPass remains (plus inline sky).
			return;
		}

		// Scene resources (AO/GI/bloom/vectors) live at RENDER resolution: they work off depth and
		// the HDR frame, which shrink with the scale. Display size remains only for grading and
		// vector debug - they live after the upscale point (tonemap).
		var (width, height) = RenderTargetSize;
		var (displayWidth, displayHeight) = TargetSize;
		var renderDepth = _targets.DepthTarget;

		// HDR backbone: luminance measurement and tonemap ALWAYS exist; the auto-exposure toggle
		// only switches tonemap between measured and manual exposure. Created FIRST: fog, bloom
		// and god rays take its 1x1 target.
		if (_eyeAdaptationResources is null)
		{
			_eyeAdaptationResources = new EyeAdaptationPassResources(_api, _batchRenderer, _colorTargetName!,
				_targets.HdrColorTarget!);
			_tonemapResources = new TonemapPassResources(_api, _batchRenderer, _targets.HdrColorTarget!,
				_eyeAdaptationResources.AdaptationTarget);
		}

		if (_features.Ssao && _ssaoResources is not null && _ssaoBuiltMode != _features.AoMode)
		{
			// The technique's shader is baked into the AO materials - cannot be swapped live.
			_ssaoResources.Release();
			_ssaoResources = null;
		}

		if (_features.Ssao && _ssaoResources is null)
		{
			_ssaoResources = new SsaoPassResources(_api, _batchRenderer, _colorTargetName!, width, height,
				renderDepth, _targets.SceneCopyTarget, _features.AoMode, RenderColorFormat);
			_ssaoBuiltMode = _features.AoMode;
		}

		if (_features.Ssgi && _ssgiResources is null)
		{
			_ssgiResources = new SsgiPassResources(_api, _batchRenderer, _colorTargetName!, width, height,
				renderDepth, _targets.SceneCopyTarget, RenderColorFormat);
		}

		if (_features.Fog && _fogResources is null)
		{
			_fogResources = new FogPassResources(_api, _batchRenderer, renderDepth, _targets.SceneCopyTarget,
				RenderColorFormat,
				_eyeAdaptationResources!.AdaptationTarget);
		}

		if (_features.Volumetric && _volumetricResources is not null &&
		    _volumetricBuiltWithShadows != _features.Shadows)
		{
			// The god-rays material is built for the presence/absence of the cascaded shadow map.
			_volumetricResources.Release();
			_volumetricResources = null;
		}

		if (_features.Volumetric && _volumetricResources is null)
		{
			_volumetricResources = new VolumetricLightPassResources(_api, _batchRenderer, renderDepth,
				_targets.SceneCopyTarget, RenderColorFormat,
				_eyeAdaptationResources!.AdaptationTarget, _features.Shadows);
			_volumetricBuiltWithShadows = _features.Shadows;
		}

		if (_features.Bloom && _bloomResources is null)
		{
			_bloomResources = new BloomPassResources(_api, _batchRenderer, _colorTargetName!, width, height,
				_targets.SceneCopyTarget, RenderColorFormat, _eyeAdaptationResources!.AdaptationTarget);
		}

		if (_features.ColorGrade && _gradeResources is null)
		{
			_gradeResources = new ColorGradePassResources(_api, _batchRenderer, _colorTargetName!,
				displayWidth, displayHeight);
		}

		if (_features.MotionVectors && _motionVectorResources is null)
		{
			_motionVectorResources = new MotionVectorPassResources(_api, _batchRenderer, _colorTargetName!,
				_targets.DepthTarget, width, height);

			// The debug view is created together with the buffer, not behind its own flag: without
			// it vectors cannot be inspected at all (they do not change the frame), and it costs one
			// material and one cbuffer. It needs BOTH sizes: the vector buffer lives at render
			// resolution while the pass draws into the display frame.
			_motionVectorDebugResources = new MotionVectorDebugPassResources(_api, _batchRenderer,
				_motionVectorResources.MotionTarget, width, height, displayWidth, displayHeight);
		}

		// SSR - strictly AFTER the vector block (resolve needs their buffer) and only where the
		// thin reflection G-buffer exists (HDR mode) and an env map is present (the composite
		// subtracts its contribution). Changing the RT fallback recreates the resources: the shader
		// variant is baked into the trace material, like the AO technique.
		var ssrRayTraced = _features.SsrRayTraced && _api.RayTracing >= RayTracingSupport.Inline;

		// RT-hit textures: pointless without the RT fallback; bindless silently degrades to the
		// atlas when the backend lacks dynamic array indexing.
		var ssrHitTextures = ssrRayTraced ? Math.Clamp(_features.SsrHitTextures, 0, 2) : 0;
		if (ssrHitTextures == 2 && !_api.SupportsShaderResourceArrays)
		{
			ssrHitTextures = 1;
		}

		if (_features.Ssr && _ssrResources is not null &&
		    (_ssrBuiltRayTraced != ssrRayTraced || _ssrBuiltHitTextures != ssrHitTextures))
		{
			_ssrResources.Release();
			_ssrResources = null;
		}

		if (_features.Ssr && _ssrResources is null && _motionVectorResources is not null &&
		    _targets.NormalRoughnessTarget is not null && _environmentMap is not null)
		{
			_ssrResources = new SsrPassResources(_api, _batchRenderer, _colorTargetName!, width, height,
				_targets.DepthTarget, _targets.NormalRoughnessTarget, _targets.EnvFactorTarget!,
				_targets.SceneCopyTarget, _motionVectorResources.MotionTarget, _environmentMap,
				ssrRayTraced, RenderColorFormat, ssrHitTextures);
			_ssrBuiltRayTraced = ssrRayTraced;
			_ssrBuiltHitTextures = ssrHitTextures;
		}

		// Upscaler slot - strictly AFTER the vector block: without their buffer the accumulator
		// cannot reproject history, so the feature silently does nothing, exactly like the vectors
		// themselves; the Graphics window warns the same way.
		if (_features.TemporalUpscale && _temporalUpscaleResources is null &&
		    _motionVectorResources is not null)
		{
			_temporalUpscaleResources = new TemporalUpscalePassResources(_api, _batchRenderer,
				_colorTargetName!, _targets.HdrColorTarget!, _motionVectorResources.MotionTarget,
				width, height, displayWidth, displayHeight);
		}

		ApplyExposureMode();
	}

	/// <summary>Pushes the current exposure mode (measured vs manual) to every luminance consumer -
	/// the only thing the auto-exposure toggle does. See <see cref="PipelineFeatures.EyeAdaptation"/>.</summary>
	private void ApplyExposureMode()
	{
		var auto = AutoExposure;
		_tonemapResources?.SetAutoExposure(auto);
		_fogResources?.SetExposureRelative(auto);
		_bloomResources?.SetExposureRelative(auto);
		_volumetricResources?.SetExposureRelative(auto);
	}

	/// <summary>Changes the feature set on a LIVE pipeline: new features' resources are created
	/// lazily, disabled ones stay allocated (re-enable is free), and the graph is rebuilt. Neither
	/// the environment, the batch renderer nor the scene are recreated.
	///
	/// Changing ONLY auto-exposure does not even rebuild the graph (it is live, see
	/// <see cref="PipelineFeatures.EyeAdaptation"/>).
	///
	/// Waits for the GPU before releasing anything, so it may be called from any point of the frame
	/// with no open command recording.</summary>
	public void SetFeatures(in PipelineFeatures features)
	{
		if (_features.Equals(features))
		{
			return;
		}

		var structural = !_features.StructurallyEquals(features);
		var needsWait =
			(features.Ssao && _ssaoResources is not null && _ssaoBuiltMode != features.AoMode) ||
			// SSR materials rebuilt for a different trace variant (see EnsureResources).
			(features.Ssr && _ssrResources is not null &&
			 (_ssrBuiltRayTraced != (features.SsrRayTraced && _api.RayTracing >= RayTracingSupport.Inline) ||
			  _ssrBuiltHitTextures != ((features.SsrRayTraced && _api.RayTracing >= RayTracingSupport.Inline)
				  ? Math.Clamp(features.SsrHitTextures, 0, _api.SupportsShaderResourceArrays ? 2 : 1)
				  : 0))) ||
			(features.Volumetric && _volumetricResources is not null &&
			 _volumetricBuiltWithShadows != features.Shadows) ||
			// Toggling upscale ACTIVITY swaps the tonemap input on a live material - changing an
			// SRB while the previous frame is in flight is invalid (same Vulkan validation as
			// EyeAdaptationPass). Activity depends on BOTH features: the vectors toggle flips it
			// too while upscale is on.
			(features.TemporalUpscale && features.MotionVectors) !=
			(_features.TemporalUpscale && _features.MotionVectors);

		if (needsWait)
		{
			_api.WaitForGpuIdle();
		}

		_features = features;
		EnsureResources();

		if (structural)
		{
			RebuildGraph();
		}
	}

	/// <summary>Releases resources of DISABLED features and the graph's resource pool - the point
	/// where VRAM is actually returned. Deliberately separate from <see cref="SetFeatures"/>:
	/// keeping a disabled feature resident is cheap in time and expensive in VRAM, and that
	/// trade-off belongs to the caller.</summary>
	public void ReleaseDisabledResources()
	{
		_api.WaitForGpuIdle();

		if (!_features.SkyBackground)
		{
			_skyResources?.Release();
			_skyResources = null;
		}

		if (!_features.Ssao)
		{
			_ssaoResources?.Release();
			_ssaoResources = null;
		}

		if (!_features.Ssgi)
		{
			_ssgiResources?.Release();
			_ssgiResources = null;
		}

		if (!_features.Ssr)
		{
			_ssrResources?.Release();
			_ssrResources = null;
		}

		if (!_features.Fog)
		{
			_fogResources?.Release();
			_fogResources = null;
		}

		if (!_features.Volumetric)
		{
			_volumetricResources?.Release();
			_volumetricResources = null;
		}

		if (!_features.Bloom)
		{
			_bloomResources?.Release();
			_bloomResources = null;
		}

		if (!_features.ColorGrade)
		{
			_gradeResources?.Release();
			_gradeResources = null;
		}

		if (!_features.MotionVectors)
		{
			_motionVectorResources?.Release();
			_motionVectorResources = null;
			_motionVectorDebugResources?.Release();
			_motionVectorDebugResources = null;
		}

		// Also when VECTORS are off: without their buffer the upscaler would hold an SRB on a
		// released texture (creation gates on vectors in EnsureResources - release symmetrically).
		if (!_features.TemporalUpscale || !_features.MotionVectors)
		{
			_temporalUpscaleResources?.Release();
			_temporalUpscaleResources = null;
		}

		// Resources may be bound in frozen commands - rebuild the graph, then trim its pool.
		RebuildGraph();
		_renderGraph.TrimResourcePool();
	}

	/// <summary>Rebinds post-processing materials to the current depth/scene-copy/HDR targets
	/// AFTER their Resize (see ModelPreviewViewport.ResizeTargets) - no-op for not-yet-created ones.</summary>
	public void RebindSsaoTargets()
	{
		if (_targets is null)
		{
			return;
		}

		// Scene resources at render resolution; display stays with grading and vector debug
		// (see EnsureResources).
		var (renderWidth, renderHeight) = RenderTargetSize;
		var (displayWidth, displayHeight) = TargetSize;

		var renderDepth = _targets.DepthTarget;
		_ssaoResources?.RebindTargets(renderDepth, _targets.SceneCopyTarget);
		_ssgiResources?.RebindTargets(renderDepth, _targets.SceneCopyTarget);
		_fogResources?.RebindTargets(renderDepth, _targets.SceneCopyTarget);
		_volumetricResources?.RebindTargets(renderDepth, _targets.SceneCopyTarget);
		_bloomResources?.RebindTargets(_targets.SceneCopyTarget, renderWidth, renderHeight);
		_gradeResources?.RebindTargets(displayWidth, displayHeight);
		_eyeAdaptationResources?.RebindTargets(_targets.HdrColorTarget!);

		_motionVectorResources?.RebindTargets(_targets.DepthTarget, renderWidth, renderHeight);

		// Strictly AFTER the vector rebind (SSR reads their buffer, which was just recreated).
		if (_motionVectorResources is not null && _targets.NormalRoughnessTarget is not null)
		{
			_ssrResources?.RebindTargets(_targets.DepthTarget, _targets.NormalRoughnessTarget,
				_targets.EnvFactorTarget!, _targets.SceneCopyTarget,
				_motionVectorResources.MotionTarget, renderWidth, renderHeight);
		}

		// Strictly AFTER the vector rebind: their RebindTargets recreates the native MotionTarget,
		// and binding the debug view or the upscaler earlier would leave them holding a destroyed
		// texture.
		if (_motionVectorResources is not null)
		{
			_motionVectorDebugResources?.RebindTargets(_motionVectorResources.MotionTarget,
				renderWidth, renderHeight, displayWidth, displayHeight);
			_temporalUpscaleResources?.RebindTargets(_targets.HdrColorTarget!,
				_motionVectorResources.MotionTarget, renderWidth, renderHeight,
				displayWidth, displayHeight);
			_nativeUpscaler?.Resize(_targets.HdrColorTarget!, _targets.DepthTarget,
				_motionVectorResources.MotionTarget, renderWidth, renderHeight,
				displayWidth, displayHeight);
		}

		// Tonemap LAST: with upscale on, its input is the upscaler's OutputTarget, which was just
		// recreated by the Resize above.
		var nativeActive = _features.TemporalUpscale && _features.MotionVectors &&
			_nativeUpscaler is not null && _motionVectorResources is not null;
		var upscaleActive = !nativeActive && _features.TemporalUpscale && _features.MotionVectors &&
			_temporalUpscaleResources is not null && _motionVectorResources is not null;
		_tonemapResources?.RebindTargets(nativeActive
			? (IGpuTexture)_nativeUpscaler!.OutputTarget
			: upscaleActive
				? (IGpuTexture)_temporalUpscaleResources!.OutputTarget
				: _targets.HdrColorTarget!);
	}

	public void OnViewportChange()
	{
		_viewPortRef.Set(_api.WindowHandle.Size);
	}

	/// <summary>See <see cref="GraphicsPipeline.SetOffscreenViewportSize"/>. Updates BOTH viewports:
	/// display with the given size, scene with it multiplied by <see cref="RenderScale"/>.</summary>
	public void SetOffscreenViewportSize(Vector2 size)
	{
		var renderSize = SceneSizeFor(size);
		var change = _viewPortRef.Value != size || _renderViewPortRef.Value != renderSize;
		_viewPortRef.Set(size);
		_renderViewPortRef.Set(renderSize);
		if (change)
		{
			_renderGraph.Invalidate();
		}
	}

	/// <summary>Current fraction of display resolution the scene renders at (1 = no scaling).</summary>
	public float RenderScale => _renderScale;

	/// <summary>Size the SCENE targets (depth, HDR frame, scene-copy, vectors, AO/GI) live at for
	/// the given display size. The display ColorTarget is untouched by the scale - tonemap does the
	/// upscale (see TonemapPS.hlsl).</summary>
	public Vector2 SceneSizeFor(Vector2 displaySize) => _renderScale >= 1f
		? displaySize
		: new Vector2(MathF.Max(1f, MathF.Round(displaySize.X * _renderScale)),
			MathF.Max(1f, MathF.Round(displaySize.Y * _renderScale)));

	/// <summary>Changes the scene render scale (0.25..1). Only records the value and returns true on
	/// change: RESIZING the scene targets is the caller's job - it owns the whole resize path (GPU
	/// barrier, rebinding _SceneColor to resident materials, ImGui bindings - see
	/// ModelPreviewViewport.ResizeTargets). Until then the pipeline keeps rendering at the old size.</summary>
	public bool SetRenderScale(float scale)
	{
		scale = Math.Clamp(scale, 0.25f, 1f);
		if (_targets is null || _renderScale == scale)
		{
			return false;
		}

		_renderScale = scale;
		return true;
	}

	/// <summary>Pass names of the current graph - for the debug list in the UI.</summary>
	public IReadOnlyList<string> PassNames => _renderGraph.PassNames;

	/// <summary>Enables/disables a pass by name - the graph debug window's toggle. Safe for any
	/// pass: a disabled pass is excluded from the graph entirely, including its state transitions
	/// (see IRenderGraphPass.Enabled). The remembered value survives graph rebuilds.
	/// For pipeline features use <see cref="SetFeatures"/>: it also removes resource-prep work.</summary>
	public void SetPassEnabled(string name, bool enabled)
	{
		_passEnabled[name] = enabled;
		_renderGraph.SetPassEnabled(name, enabled);
	}

	/// <summary>Whether a pass is enabled (by the remembered flag, not the graph state).</summary>
	public bool IsPassEnabled(string name) => !_passEnabled.TryGetValue(name, out var enabled) || enabled;

	/// <summary>See <see cref="GraphicsPipeline.InvalidateGraph"/>.</summary>
	public void InvalidateGraph()
	{
		_renderGraph.Invalidate();
	}

	public void Initialize()
	{
	}

	public void SignalGraph(DirectionalLightCascadeData renderScene, RenderCamerasData renderViews)
	{
		_lastCascadeData = renderScene;
		_lastCameras = renderViews;
		_hasGraphData = true;
		RebuildGraph();
	}

	/// <summary>Rebuilds the PASS LIST for the current feature set. Native resources are reused
	/// (see <see cref="IRenderGraph.ResetPasses"/>), so this is cheap enough per toggle.</summary>
	private void RebuildGraph()
	{
		if (!_hasGraphData)
		{
			return;
		}

		_renderGraph.ResetPasses();

		if (_features.Shadows)
		{
			_renderGraph.AddPass(new ShadowPass(_batchRenderer, _lastCascadeData, _cascadeSchedule));
		}

		// AO draws inline inside ForwardPass, between opaque and transmissive draws: glass refracts
		// the occluded background but is not dimmed by screen-space AO itself
		// (see SsaoPassResources.WriteInlineCommands).
		var renderColor = _targets?.RenderColorTarget;

		// Scene passes (from here through bloom) draw into the RENDER viewport; tonemap lifts the
		// frame to display resolution.
		_renderGraph.AddPass(new ForwardPass(_batchRenderer, _lastCameras, _renderViewPortRef, renderColor,
			_targets?.DepthTarget, _clearColor, _targets?.SceneCopyTarget,
			_features.SkyBackground ? _skyResources : null,
			_features.Ssao ? _ssaoResources : null,
			// Multicast Action runs both hooks in order; Delegate.Combine drops nulls itself, so
			// "no overlay" stays exactly null.
			() => (Action<ICommandBuffer>?)Delegate.Combine(InlineOverlay, DebugOverlay),
			_targets?.NormalRoughnessTarget, _targets?.EnvFactorTarget));

		// Motion vectors right after geometry: the pass only needs finished depth, and the buffer
		// must be ready before the upscaler. It does not touch the frame, so ordering of the
		// remaining passes is unaffected.
		if (_features.MotionVectors && _motionVectorResources is not null)
		{
			_renderGraph.AddPass(new MotionVectorPass(_motionVectorResources, _targets!.DepthTarget,
				_renderViewPortRef));
		}

		// SSGI gathers bounce from the already-rendered frame, so it goes after AO: the light
		// source already contains contact shadows and the bounce accounts for them.
		if (_features.Ssgi && _ssgiResources is not null)
		{
			var renderDepth = _targets!.DepthTarget;
			_renderGraph.AddPass(new SsgiPass(_ssgiResources, renderColor!, _targets.SceneCopyTarget, renderDepth, _renderViewPortRef));
		}

		// SSR after SSGI (reflections see the frame with indirect light; the pass re-snapshots the
		// scene itself) and before luminance measurement: reflections count toward exposure.
		if (_features.Ssr && _ssrResources is not null && _motionVectorResources is not null &&
		    _targets!.NormalRoughnessTarget is not null)
		{
			_renderGraph.AddPass(new SsrPass(_ssrResources, renderColor!, _targets.SceneCopyTarget,
				_targets.DepthTarget, _targets.NormalRoughnessTarget, _targets.EnvFactorTarget!,
				_motionVectorResources.MotionTarget, _renderViewPortRef));
		}

		// Luminance measurement on the FINISHED linear frame, tonemap right after. The pass is
		// ALWAYS in the graph, not only with auto-exposure: it is cheap (five draws 64x64 and
		// smaller), and skipping it would leave the 1x1 adaptation targets never written - on
		// Vulkan that is an UNDEFINED layout under the tonemap SRV. See PipelineFeatures.EyeAdaptation.
		if (_eyeAdaptationResources is not null)
		{
			_renderGraph.AddPass(new EyeAdaptationPass(_eyeAdaptationResources, _targets!.HdrColorTarget!));
		}

		// Fog AFTER SSGI (haze must cover indirect light too) but also AFTER the luminance
		// measurement, deviating from physics deliberately: fog brightness is tied to adaptation
		// (see FogPassResources.SetExposure), and placing it before the measurement created a
		// feedback loop where fog inflated the very value it multiplies by. Predictability beats
		// physical honesty for a preview tool. Tonemap still comes LAST: fog lands in the linear
		// frame, before the curve.
		// God rays go BEFORE fog: light shafts must be hazed by distant fog like geometry, or they
		// read as a sticker on a fogged background. Their "after measurement" reason matches fog's.
		if (_features.Volumetric && _volumetricResources is not null)
		{
			var volumetricDepth = _targets!.DepthTarget;
			_renderGraph.AddPass(new VolumetricLightPass(_volumetricResources, _batchRenderer,
				renderColor!, _targets.SceneCopyTarget, volumetricDepth, _renderViewPortRef));
		}

		if (_features.Fog && _fogResources is not null)
		{
			var renderDepth = _targets!.DepthTarget;
			_renderGraph.AddPass(new FogPass(_fogResources, renderColor!, _targets.SceneCopyTarget,
				renderDepth, _renderViewPortRef));
		}

		// Bloom AFTER fog (optical scattering happens to light that reached the lens, including the
		// haze's own glow) and, like fog, AFTER the measurement: its threshold is tied to
		// adaptation. Before tonemap - light can only be summed in linear space.
		if (_features.Bloom && _bloomResources is not null)
		{
			_renderGraph.AddPass(new BloomPass(_bloomResources, renderColor!, _targets!.SceneCopyTarget,
				_renderViewPortRef));
		}

		// Upscaler slot LAST at render resolution: fog, god rays and bloom are already in the HDR
		// frame (the upscaler must see what the eye would), and accumulation needs LINEAR light -
		// blending after the tonemap curve would suppress bright sub-pixel detail. Tonemap reads
		// its output 1:1 when upscale is on. The native backend (FSR) takes the slot instead of the
		// built-in TAAU when set. Gate on the vectors FEATURE, not just the resources: resources
		// survive disables, and without this check an unticked vectors box would leave the upscaler
		// in the graph on a stale buffer.
		var nativeUpscaleActive = _features.TemporalUpscale && _features.MotionVectors &&
			_nativeUpscaler is not null && _motionVectorResources is not null;
		var upscaleActive = !nativeUpscaleActive &&
			_features.TemporalUpscale && _features.MotionVectors &&
			_temporalUpscaleResources is not null && _motionVectorResources is not null;

		if (nativeUpscaleActive)
		{
			_renderGraph.AddPass(new NativeUpscalePass(_nativeUpscaler!, _targets!.HdrColorTarget!,
				_targets.DepthTarget, _motionVectorResources!.MotionTarget));
		}
		else if (upscaleActive)
		{
			_renderGraph.AddPass(new TemporalUpscalePass(_temporalUpscaleResources!,
				_targets!.HdrColorTarget!, _motionVectorResources!.MotionTarget, _viewPortRef));
		}

		if (_tonemapResources is not null && Environment.GetEnvironmentVariable("DECA_NO_EA_PASS") != "1")
		{
			// The tonemap input depends on the upscale toggle - rebind here where the graph is
			// built: SetFeatures already waited for the GPU on the toggle (see needsWait).
			var tonemapSource = nativeUpscaleActive
				? (IGpuTexture)_nativeUpscaler!.OutputTarget
				: upscaleActive
					? (IGpuTexture)_temporalUpscaleResources!.OutputTarget
					: _targets!.HdrColorTarget!;
			_tonemapResources.RebindTargets(tonemapSource);
			_tonemapResources.SetForceOpaque(nativeUpscaleActive);
			_renderGraph.AddPass(new TonemapPass(_tonemapResources, tonemapSource,
				_targets!.ColorTarget, _viewPortRef));
		}

		// Grading and vignette AFTER tonemap (their scales are defined in display space) and BEFORE
		// overlays: selection outline and gizmos are UI, artistic correction must not touch them.
		if (_features.ColorGrade && _gradeResources is not null)
		{
			_renderGraph.AddPass(new ColorGradePass(_gradeResources, _targets!.ColorTarget, _viewPortRef));
		}

		// Vector debug view AFTER tonemap and grading (it works on the display frame, see
		// MotionVectorDebugPS.hlsl) but BEFORE overlays: gizmos and the selection outline must stay
		// visible or objects cannot be picked in this mode. It is ALWAYS in the graph whenever the
		// vector buffer exists; display is toggled via its cbuffer knob (a disabled pass discards
		// the whole screen), so the checkbox never rebuilds the graph.
		if (_features.MotionVectors && _motionVectorResources is not null &&
		    _motionVectorDebugResources is not null)
		{
			_renderGraph.AddPass(new MotionVectorDebugPass(_motionVectorDebugResources,
				_motionVectorResources.MotionTarget, _targets!.ColorTarget, _viewPortRef));
		}

		// Last - the overlay as its own pass over the finished frame (see PostOverlay): always
		// added, a null hook writes no commands.
		_renderGraph.AddPass(new PostOverlayPass(() => PostOverlay));

		// Passes were recreated with Enabled=true - re-apply the remembered toggles.
		foreach (var (name, enabled) in _passEnabled)
		{
			_renderGraph.SetPassEnabled(name, enabled);
		}
	}

	public void Execute()
	{
		// Strict order: first UNDO last frame's jitter (Execute without a new Update leaves our
		// matrix in viewData), then latch motion vectors from the CLEAN matrix - upscalers expect
		// unjittered vectors and subtract the sub-pixel shake themselves via the jitter offset -
		// and only then apply this frame's offset.
		LatchRenderResolution();
		UnwindTemporalJitter();
		LatchMotionVectors();
		ApplyTemporalJitter();

		// SSR stochastic noise phase advances once per frame (see SsrPassResources.AdvanceFrame);
		// the same call latches last frame's viewProj for virtual-image reprojection of mirrors
		// (see SsrResolvePS, RTG ch.32) - same ordering as LatchMotionVectors.
		if (_features.Ssr && _ssrResources is not null)
		{
			_ssrResources.AdvanceFrame();
			if (_hasGraphData && _lastCameras.IsCreated && _lastCameras.Length > 0)
			{
				_ssrResources.UpdateFromView(_lastCameras.viewData.GetRef(0, false).viewProj);
			}
		}

		// The upscaler needs THIS frame's jitter (hence after ApplyTemporalJitter): the shader
		// subtracts it from the current-frame sample, see TemporalUpscalePS.hlsl.
		if (_features.TemporalUpscale && _features.MotionVectors)
		{
			if (_nativeUpscaler is not null)
			{
				_nativeUpscaler.SetFrameParams(_jitterPixels);
			}
			else
			{
				_temporalUpscaleResources?.SetFrameParams(_jitterPixels);
			}
		}

		_renderGraph.Execute();
	}

	/// <summary>With render scale active, patches camera 0's viewport in viewData to the RENDER
	/// resolution: scene shaders build screen UVs and pixel steps from viewport.zw of the View
	/// cbuffer while the camera stores the display size. Live-native-memory trick like the jitter,
	/// and idempotent: Update rewrites viewData every frame.</summary>
	private void LatchRenderResolution()
	{
		if (_renderScale >= 1f || _targets is null || !_hasGraphData ||
		    !_lastCameras.IsCreated || _lastCameras.Length == 0)
		{
			return;
		}

		var size = _renderViewPortRef.Value;
		ref var viewData = ref _lastCameras.viewData.GetRef(0, false);
		viewData.viewport = new Vector4(viewData.viewport.X, viewData.viewport.Y, size.X, size.Y);
	}

	/// <summary>Restores the unjittered matrix in camera 0's viewData if our jittered one is still
	/// there (Execute without an Update in between). See the jitter field comment.</summary>
	private void UnwindTemporalJitter()
	{
		if (!_hasGraphData || !_lastCameras.IsCreated || _lastCameras.Length == 0)
		{
			return;
		}

		ref var viewData = ref _lastCameras.viewData.GetRef(0, false);
		if (viewData.viewProj == _jitteredViewProj)
		{
			viewData.viewProj = _unjitteredViewProj;
		}
	}

	/// <summary>Applies the frame's sub-pixel offset to camera 0's live viewProj. Halton(2,3) is the
	/// standard low-discrepancy jitter sequence; the shift is added in clip space AFTER projection
	/// (row-vector convention: post-multiply, x' = x + jx*w), so the w-divide turns it into a
	/// constant sub-pixel shift of the whole frame.
	///
	/// Camera 0 only, same reason as <see cref="LatchMotionVectors"/>. View/culling/cascade matrices
	/// are untouched: the frustum does not change by a sub-pixel, and jittering shadows is actively
	/// harmful - the upscaler cannot resolve shadow-edge shimmer.</summary>
	private void ApplyTemporalJitter()
	{
		_jitterPixels = Vector2.Zero;

		// An active upscaler enables jitter itself: without it the accumulator gets identical
		// samples every frame and recovers no detail. Gate on the vectors feature, matching the
		// slot gate in RebuildGraph.
		var jitterActive = _jitterEnabled ||
			(_features.TemporalUpscale && _features.MotionVectors &&
			 (_temporalUpscaleResources is not null || _nativeUpscaler is not null));

		if (!jitterActive || !_hasGraphData || !_lastCameras.IsCreated || _lastCameras.Length == 0)
		{
			return;
		}

		// RENDER viewport: jitter is in fractions of a pixel at the resolution the scene rasterizes at.
		var size = _renderViewPortRef.Value;
		if (size.X < 1f || size.Y < 1f)
		{
			return;
		}

		// 16 phases: enough for TAA and upscaling up to 2x (FSR uses 8*(scale^2)). Halton starts at
		// index 1 - index 0 would yield (0,0) and break the uniform pixel coverage.
		uint phase = _jitterFrameIndex++ % 16 + 1;
		_jitterPixels = new Vector2(Halton(phase, 2) - 0.5f, Halton(phase, 3) - 0.5f);

		// Pixels -> NDC: one x pixel is 2/width; NDC y grows up while frame rows go down.
		var ndc = new Vector2(_jitterPixels.X * 2f / size.X, -_jitterPixels.Y * 2f / size.Y);

		ref var viewData = ref _lastCameras.viewData.GetRef(0, false);
		_unjitteredViewProj = viewData.viewProj;
		viewData.viewProj = _unjitteredViewProj * Matrix4x4.CreateTranslation(ndc.X, ndc.Y, 0f);
		_jitteredViewProj = viewData.viewProj;
	}

	private static float Halton(uint index, uint radix)
	{
		float result = 0f;
		float fraction = 1f / radix;
		while (index > 0)
		{
			result += index % radix * fraction;
			index /= radix;
			fraction /= radix;
		}

		return result;
	}

	/// <summary>Latches the reprojection matrix exactly once per frame - HERE, not in the pass:
	/// graph commands are frozen and replayed across frames, so anything computed inside
	/// <see cref="MotionVectorPass.WriteCommands"/> would freeze with them (see
	/// <see cref="MotionVectorPassResources.UpdateFromView"/>).
	///
	/// viewProj comes from the same live native memory <see cref="ForwardPass"/> draws with
	/// (<see cref="RenderCamerasData.viewData"/>) - otherwise vectors would be computed for a camera
	/// one frame off from the rendered one, invisible on a static camera.</summary>
	private void LatchMotionVectors()
	{
		if (_motionVectorResources is null || !_hasGraphData)
		{
			return;
		}

		// Camera 0 only: one vector buffer per frame; split-screen would need one per camera
		// (see the MotionVectorPassResources class comment).
		if (!_lastCameras.IsCreated || _lastCameras.Length == 0)
		{
			// Frame without a camera (scene not built yet): break history, or the next live frame
			// would reproject against a matrix of unknown age.
			_motionVectorResources.ResetHistory();
			return;
		}

		_motionVectorResources.UpdateFromView(_lastCameras.viewData.GetRef(0, false).viewProj);
	}

	/// <summary>Releases the render graph (frozen commands and resource pins) - for recreating the
	/// preview environment on the fly (see ModelPreviewViewport.RecreateEnvironment). The caller
	/// must wait for the GPU first (Flush + WaitForIdle).</summary>
	public void Release()
	{
		// Unregister first so the debug window cannot pick the pipeline being released.
		GraphicsPipelineRegistry.Unregister(this);

		_renderGraph.Release();
		_ssaoResources?.Release();
		_ssgiResources?.Release();
		_ssrResources?.Release();
		_fogResources?.Release();
		_volumetricResources?.Release();
		_bloomResources?.Release();
		_gradeResources?.Release();
		_tonemapResources?.Release();
		_eyeAdaptationResources?.Release();
		_motionVectorResources?.Release();
		_motionVectorDebugResources?.Release();
		_temporalUpscaleResources?.Release();
		_nativeUpscaler?.Release();
		_skyResources?.Release();
		_targets?.Release();
	}

	public RenderGraphDebugSnapshot DebugSnapshot => _renderGraph.DebugSnapshot;
	public RenderGraphDebugHistory DebugHistory => _renderGraph.DebugHistory;
}

/// <summary>Final pass hook <see cref="GraphicsPipelineSimple.PostOverlay"/>: runs the user overlay
/// as its own render-graph pass after all post-processing, over the finished display frame. The
/// hook is read at command-record time (same pattern as InlineOverlay in ForwardPass) - changing it
/// requires <see cref="GraphicsPipelineSimple.InvalidateGraph"/>; a null hook writes no commands.</summary>
public sealed class PostOverlayPass : RenderGraphPass<PostOverlayPass.PassData>
{
	public override string Name => "Post Overlay Pass";

	private readonly Func<Action<ICommandBuffer>?> _overlay;

	public struct PassData
	{
	}

	public PostOverlayPass(Func<Action<ICommandBuffer>?> overlay)
	{
		_overlay = overlay;
	}

	public override PassData Setup(IRenderGraphBuilder builder)
	{
		return default;
	}

	public override void WriteCommands(in PassData value, in IRenderGraphContext context)
	{
		_overlay()?.Invoke(context.cmd);
	}
}
