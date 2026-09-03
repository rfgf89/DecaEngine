using System;
using System.Collections.Generic;
using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Core.Diagnostics;
using DecaEngine.Core.Assets;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
using DecaEngine.Graphics.ProbeGi;
using Engine.ImGui.Core;
using System.Threading.Tasks;
using Friflo.Engine.ECS;
using Hexa.NET.ImGui;
using Hexa.NET.ImGuizmo;
using UnsafeCollections.Collections.Unsafe;
using DecaEngine.Scene;
using DecaEngine.Animation;

namespace DecaEngine.Editor
{
	/// <summary>GPU viewport of the Scene View window: renders the edited prefab's entities through its own offscreen <see cref="ModelViewportEnvironment"/>.</summary>
	public partial class PrefabSceneViewport
	{
		/// <summary>Frame shading mode, mapped to the PreviewSettings Mode/Channel cbuffer (see UnlitInstancedPS.hlsl).</summary>
		public enum ShadingMode
		{
			Lighting,
			Textured,
			Normal,
			Uv,
			Tangent,
			// Mode 3 + channel 11: punctual shadow sampling debug (see UnlitInstancedPS.hlsl).
			PunctualShadowDebug,

			// Punctual light clustering debug (LightClusterCS.hlsl); legends in ClusterLegend below.
			ClusterDepthSlices,  // channel 20: froxel depth slice as color
			ClusterScreenTiles,  // channel 21: froxel screen tile
			ClusterLightCount,   // channel 14: lights in the pixel's cluster

			// Both depths the shadow sampler compares, world units along the slice axis (ch 22..24).
			LightDepthReceiver,  // channel 22: receiver depth from the light
			LightDepthOccluder,  // channel 23: occluder depth stored in the slice at the same UV
			LightDepthGap,       // channel 24: their gap in units of the applied bias

			// Sun cascaded shadows (channel 28): hue = cascade, brightness = shadow factor.
			SunShadowCascades,
		}

		/// <summary>Legend text for the debug shading modes' menu tooltips.</summary>
		public static string ClusterLegend(ShadingMode mode) => mode switch
		{
			ShadingMode.ClusterDepthSlices =>
				"Cluster Depth Slices (channel 20) - froxel depth slice, one color per slice.\n" +
				"Expected: bands run with DEPTH, not across the screen - a floor receding from the\n" +
				"camera gets bands across the view direction, packing tighter with distance; a wall\n" +
				"facing the camera is ONE flat color. Color must change as the camera moves forward\n" +
				"and stay put as it rotates in place.\n" +
				"Whole frame one color = depth slices are degenerate; vertical/horizontal screen\n" +
				"bands = screen x/y leaked into the slice. Magenta = grid undefined.",
			ShadingMode.ClusterScreenTiles =>
				"Cluster Screen Tiles (channel 21) - froxel screen tile, checkerboarded.\n" +
				"Expected: an even 16x8 grid over the WHOLE frame (red = tile x, green = tile y).\n" +
				"Fewer cells squeezed into a corner = SV_Position and viewport.zw are in different\n" +
				"resolutions (render scale); grid drifting on resize = camera viewport lags the target.",
			ShadingMode.ClusterLightCount =>
				"Cluster Light Count (channel 14) - lights in this pixel's cluster, raw (before the\n" +
				"32-per-cluster clamp).\n" +
				"black - 0 lights: the cluster is empty\n" +
				"blue -> cyan -> green -> yellow -> red - 1 .. 32 lights, ascending\n" +
				"white - MORE than 32: the cluster overflows and the tail of its lights is dropped\n" +
				"magenta - the cluster branch never ran (the camera has no punctual lights)",
			ShadingMode.LightDepthReceiver or ShadingMode.LightDepthOccluder =>
				"Light Depth - Receiver (ch 22) / Occluder (ch 23): the two depths the shadow sampler\n" +
				"compares, in WORLD units along the slice axis, on a shared ramp\n" +
				"(black at the light -> blue -> cyan -> green -> yellow -> red at the slice far plane).\n" +
				"Receiver = how far THIS surface is from the light. Occluder = what the slice actually\n" +
				"stores at the same UV, i.e. how far the light got in that direction.\n" +
				"Expected: wherever the surface is NOT shadowed, the two views must MATCH - flip\n" +
				"between them and look for differences. A difference means either a real occluder in\n" +
				"front (legit shadow, localized) or the WRONG slice being sampled (mismatch is then\n" +
				"wholesale and patternless). Magenta = shadow sampling never ran here.",
			ShadingMode.LightDepthGap =>
				"Light Depth Gap (channel 24) - (receiver - occluder) measured in units of the bias\n" +
				"actually applied at that pixel, which is the sampler's verdict itself.\n" +
				"green - gap within the bias: the surface is its own occluder, pixel lit (normal)\n" +
				"red   - gap larger than the bias: a real occluder in front, pixel shadowed\n" +
				"blue  - NEGATIVE gap (receiver closer to the light than anything stored): normal only\n" +
				"        where no caster was drawn; a solid blue field means an empty or wrong slice\n" +
				"Brightness is |gap|/bias capped at 4. A thin red rim along contacts over a green field\n" +
				"is the healthy picture; a wide red band trailing an object = bias too large\n" +
				"(peter-panning); ragged red speckle over a lit plane = bias too small (acne).",
			ShadingMode.SunShadowCascades =>
				"Sun Shadow Cascades (channel 28) - the sun's cascaded shadow and WHICH cascade it\n" +
				"came from, in one image. Hue = cascade, brightness = shadow factor.\n" +
				"magenta - no world light (shadows off, or LightDirection is empty)\n" +
				"BLACK   - no cascade was picked at all; the point is declared lit. On geometry that\n" +
				"          should be inside the cascade volumes this IS the gap\n" +
				"red / green / blue / yellow - cascade 0 / 1 / 2 / 3\n" +
				"full hue = lit, darkening toward black = shadowed\n" +
				"\n" +
				"A shadow from a real occluder follows a silhouette and does NOT change hue across\n" +
				"its own edge. A cascade switch IS a hue change - if the darkness starts exactly on\n" +
				"one, the cascade fit is at fault, not the occluder. Acne is fine speckle WITHIN a\n" +
				"single hue, following the shadow map's texel grid.",
			_ =>
				"Punctual Shadow Debug legend:\n" +
				"magenta - shadow sampling branch didn't run (light has no assigned shadow slice,\n" +
				"          or the point is outside the light's radius)\n" +
				"orange  - receiver point is beyond the shadow slice's far plane\n" +
				"cyan    - receiver point is outside the shadow slice's UV square\n" +
				"grey    - actual sampled shadow result (black = shadowed, white = lit)\n" +
				"\n" +
				"DECA_PUNCTUAL_CHANNEL=N switches THIS mode to any other temporary channel\n" +
				"(15 UV excess, 16 slice of cyan pixels, 17 raw UV, 18 toFrag, 19 cube face).",
		};

		// DECA_PUNCTUAL_CHANNEL=N overrides the diagnostic channel shown by PunctualShadowDebug (default 11).
		private static readonly int PunctualDebugChannel =
			// Full name: the class's own Environment property shadows the short name.
			int.TryParse(System.Environment.GetEnvironmentVariable("DECA_PUNCTUAL_CHANNEL"), out var ch) && ch > 0
				? ch
				: 11;

		private const uint InitialWidth = 256;
		private const uint InitialHeight = 256;
		private const float CameraFovDegrees = ModelViewportEnvironment.CameraFovDegrees;
		private const float CameraNear = 0.05f;
		private const float CameraFar = 2000f;

		// Resize targets only after the user releases the window edge.
		private const float ResizeSettleSeconds = 0.3f;

		// Clamp sun elevation: the cascade ortho camera degenerates at horizon/zenith.
		private const float LightElevationMinDegrees = -85f;
		private const float LightElevationMaxDegrees = 85f;

		// Per prefab-entity render record: env entities, instance slots, streamer residency.
		private sealed class RenderedModel
		{
			// Owning PREFAB entity id; animation components live on it, not on env entities.
			public int EntityId;

			public string AssetPath = "";
			public string? ResolvedPath;
			public ModelStreamer.Resident? Resident;
			public readonly List<Entity> EnvEntities = new();
			public readonly List<int> InstanceIndices = new();
			public Matrix4x4 LastWorld = Matrix4x4.Identity;
			public bool Instantiated;
		}

		private readonly IGraphicsApi _graphicsApi;
		private readonly EditorSettings _editorSettings;
		private readonly ProjectSession _projectSession;
		private readonly ModelStore _modelStore;
		private readonly SharedViewportResources _sharedResources;
		private ModelViewportEnvironment _env;

		/// <summary>Whether the volumetric light has cascaded shadows.</summary>
		public bool VolumetricShadowsAvailable => _env?.VolumetricShadowsAvailable ?? false;

		/// <summary>Current scene environment; recreated on env-level settings changes — do not cache.</summary>
		public ModelViewportEnvironment Environment => _env;

		// Config the CURRENT environment was created with; diffed in OnGraphicsSettingsChanged
		// to decide whether recreation is needed (see RecreateEnvironment).
		private bool _appliedSsao;
		private AmbientOcclusionMode _appliedAoMode;
		private bool _appliedSsgi;
		private bool _appliedSky;
		private string _appliedHdrPath = "";
		private bool _appliedAniso;

		// Baked at load time: changing it re-reads resident models instead of recreating the env.
		private int _appliedMaxTextureSize;

		private bool _appliedSceneHdr;

		private bool _appliedFog;

		private bool _appliedVolumetric;

		private bool _appliedBloom;

		private bool _appliedColorGrade;

		private bool _appliedMotionVectors;
		private bool _pendingEnvironmentRecreate;

		// Distance-prioritized model streaming; the per-frame step runs in ModelStreamingSystem.
		private readonly ModelStreamer _streamer;

		private IReadOnlyDictionary<string, ModelStreamer.Resident> _models => _streamer.Models;

		// The editor model is loaded in exactly ONE place: here or ModelPreviewViewport, never both,
		// toggled by EditorManager per Inspector mode (see SetActive). Unlike the preview, this
		// viewport still renders while paused: it shows the empty environment sky, not a stale frame.
		private bool _active = true;

		private readonly Dictionary<int, RenderedModel> _rendered = new();

		// Keyed by PREFAB entity: animation components live there, not on env instance entities.
		private AnimationDriver? _animation;

		private AnimationDriver EnsureAnimation() => _animation ??= new AnimationDriver(_env.BatchRenderer.Skinning);

		// Lazy: the static build is a BVH over all scene triangles — skip it in scenes without characters.
		private ScenePhysics? _physics;

		private readonly CharacterMotionDriver _motion = new();

		// Collected in Render, consumed (and zeroed) in PollScenePhysics across the frame boundary,
		// so a hidden viewport stops feeding the last held direction forever.
		private PlayerInput _playerInput;

		/// <summary>Whether Play Mode is running; set by EditorManager each frame (temporary wiring between the viewport and the Play Mode systems).</summary>
		public bool IsPlaying { get; set; }

		// Detects the Play->Stop edge; this state lives outside ECS and is not rolled back by the snapshot.
		private bool _wasPlaying;

		// Statics rebuild flag; kept separate from selection-outline flags (those self-consume same frame).
		private bool _physicsStaticsDirty = true;

		// Scratch reused across static rebuilds to avoid re-allocating the whole scene.
		private readonly List<Vector3> _physicsPositions = new();
		private readonly List<uint> _physicsIndices = new();

		private readonly DebugDraw _debugDraw = new();
		private DebugLineOverlay? _debugLineOverlay;

		// Overlay creation failed once — don't retry every frame (log spam + shader recompiles).
		private bool _debugOverlayFailed;

		// A frame threw; the viewport stays stopped until the prefab reloads (avoids per-frame rebuild).
		private bool _renderFailed;

		// Not reset by prefab reload: no point repeating the same stack.
		private bool _renderFailureLogged;

		// Line by line: the editor console shows one line per record; a multi-line ToString loses the stack.
		private void LogRenderFailure(Exception ex)
		{
			if (_renderFailureLogged)
			{
				return;
			}

			_renderFailureLogged = true;

			EngineLog.Add(LogLevel.Error,
				$"Prefab scene: render failed: {ex.GetType().Name}: {ex.Message}");

			for (var inner = ex; inner != null; inner = inner.InnerException)
			{
				if (!ReferenceEquals(inner, ex))
				{
					EngineLog.Add(LogLevel.Error,
						$"  ---> {inner.GetType().Name}: {inner.Message}");
				}

				// Cap at 20 frames: the tail is the editor loop, identical for every failure.
				var frames = (inner.StackTrace ?? string.Empty)
					.Split('\n', StringSplitOptions.RemoveEmptyEntries);

				for (int i = 0; i < frames.Length && i < 20; i++)
				{
					EngineLog.Add(LogLevel.Error, "  " + frames[i].TrimEnd('\r').Trim());
				}
			}
		}

		private readonly List<AnimationDriver.CharacterInfo> _debugCharacters = new();

		/// <summary>Snapshot of last frame's character info for the debug window.</summary>
		public IReadOnlyList<AnimationDriver.CharacterInfo> DebugCharacters => _debugCharacters;

		/// <summary>Scene physics world for the debug window; null when the scene has no physics.</summary>
		public ScenePhysics? DebugPhysics => _physics;

		/// <summary>Debug-line vertices uploaded last frame and whether the cap was hit.</summary>
		public (int Vertices, bool Overflowed) DebugLineStats => (_debugDraw.TotalCount, _debugDraw.Overflowed);

		// Frame counter for per-frame diagnostics (DECA_ANIM_DIAG=1).
		private int _animDiagFrame;

		private bool RtShadowsEnabled() =>
			_editorSettings.ShadowFilterMode == 4 && _graphicsApi.RayTracing >= RayTracingSupport.Inline;
		private readonly HashSet<int> _visitedLightsThisSync = new();

		private EntityStore? _lastStore;
		private string? _currentPrefabPath;

		// Entities moved this frame; instance buffers must be re-uploaded to GPU after Root.Update.
		private bool _transformsDirty;

		// Progressive irradiance-probe bake over the whole scene (see ProbeGi.cs); scene edits
		// recreate the session debounced, sun rotation feeds the live session without a rebake.
		private ProbeGiBaker? _probeBaker;

		// Scene BVH build takes tens of seconds of CPU — must run off the render thread.
		private Task<ProbeGiBaker>? _probeBakerTask;

		public PrefabSceneViewport(IGraphicsApi graphicsApi, EditorSettings editorSettings, ProjectSession projectSession,
			ModelStore modelStore, SharedViewportResources sharedResources)
		{
			_graphicsApi = graphicsApi;
			_editorSettings = editorSettings;
			_projectSession = projectSession;
			_modelStore = modelStore;
			_sharedResources = sharedResources;

			_camera = new SceneCamera(_editorSettings.SceneCameraSpeed);

			_env = CreateEnvironment();

			_streamer = new ModelStreamer(_env, _modelStore, _graphicsApi, BuildLoadOptions);
			_streamer.ModelReady += OnStreamedModelReady;
			_streamer.ResidencyResetting += OnStreamerResidencyResetting;
			_streamer.ResidencyReset += OnStreamerResidencyReset;
			_env.Root.Add(new ModelStreamingSystem(_streamer));

			ApplyGraphicsSettings();

			// Env-level option changes recreate the environment at the start of the next Update.
			// The viewport lives for the whole editor session, so no unsubscribe is needed.
			SettingsWindow.PreviewGraphicsApplied += OnGraphicsSettingsChanged;
		}

		private ModelViewportEnvironment CreateEnvironment()
		{
			_appliedSsao = _editorSettings.PreviewSsao;
			_appliedAoMode = _editorSettings.PreviewAoMode;
			_appliedSsgi = _editorSettings.PreviewSsgi;
			_appliedSky = _editorSettings.PreviewSkyBackground;
			_appliedHdrPath = ResolveEnvironmentHdrPath(_editorSettings.PreviewEnvironmentHdr) ?? "";
			_appliedAniso = _editorSettings.PreviewAnisotropicFiltering;
			_appliedMaxTextureSize = ClampedMaxTextureSize();
			_appliedRtShadows = RtShadowsEnabled();
			_appliedSceneHdr = _editorSettings.SceneViewHdr;
			_appliedFog = _editorSettings.PreviewFog;
			_appliedVolumetric = _editorSettings.PreviewVolumetric;
			_appliedBloom = _editorSettings.PreviewBloom;
			_appliedColorGrade = _editorSettings.PreviewColorGrade;
			_appliedMotionVectors = _editorSettings.PreviewMotionVectors;

			// mainCascades: the main CullingAndRenderSystem builds the same camera CSM as GameView,
			// via the sun entity in the environment store (see SyncSunEntity).
			return new ModelViewportEnvironment(_graphicsApi, InitialWidth, InitialHeight,
				"Prefab Scene Color", "Prefab Scene Depth", _sharedResources,
				skyBackground: _appliedSky,
				environmentHdrPath: _appliedHdrPath.Length > 0 ? _appliedHdrPath : null,
				ssao: _appliedSsao,
				shadows: true,
				aoMode: _appliedAoMode,
				ssgi: _appliedSsgi,
				eyeAdaptation: _appliedSceneHdr,
				mainCascades: true,
				fog: _appliedFog,
				bloom: _appliedBloom,
				colorGrade: _appliedColorGrade,
				volumetric: _appliedVolumetric,
				// SSR reprojects history, so it pulls motion vectors in like TemporalUpscale.
				motionVectors: _appliedMotionVectors || _editorSettings.PreviewSsr,
				temporalUpscale: _appliedMotionVectors && _editorSettings.TemporalUpscale,
				ssr: _editorSettings.PreviewSsr,
				// RT fallback is applied later (ApplyPipelineFeatures): the scene TLAS doesn't exist
				// yet at environment creation and the RT material would commit an empty descriptor.
				ssrRayTraced: false,
				upscalerBackend: _appliedMotionVectors && _editorSettings.TemporalUpscale
					? Math.Clamp(_editorSettings.UpscalerBackend, 0, 2)
					: 0);
		}

		// Syncs the sun entity with PreviewShadowSettings: rotation so +Z looks along the light,
		// cascade distances fit where the geometry actually lies. Call each frame BEFORE Root.Update.
		private void SyncSunEntity()
		{
			var shadowSettings = _env.ShadowSettings;
			var sun = _env.SunEntity;
			if (shadowSettings == null || sun.IsNull)
			{
				return;
			}

			var travel = Vector3.Normalize(shadowSettings.LightDirection);
			var up = MathF.Abs(travel.Y) > 0.95f ? Vector3.UnitX : Vector3.UnitY;
			var view = Matrix4x4.CreateLookAtLeftHanded(Vector3.Zero, travel, up);
			sun.Rotation = new Rotation { value = Quaternion.CreateFromRotationMatrix(Matrix4x4.Transpose(view)) };

			// PCSS penumbra width; 0 in the shader means "default", so the slider must be pushed here.
			sun.GetComponent<LightComponent>().SunAngularSize =
				Math.Clamp(_editorSettings.SunAngularSize, 0.05f, 15f);

			if (shadowSettings.BoundsRadius <= 0f)
			{
				return;
			}

			float sceneRadius = shadowSettings.BoundsRadius * 1.15f;
			float distanceToScene = Vector3.Distance(_lastEye, shadowSettings.BoundsCenter);
			float rangeStart = MathF.Max(distanceToScene - sceneRadius, 0.01f);
			float rangeSpan = MathF.Max(distanceToScene + sceneRadius - rangeStart, sceneRadius * 0.1f);

			// ~2.6x progression (0.38^k): the slice nearest the geometry is the densest.
			ref var cascaded = ref sun.GetComponent<CascadedShadowComponent>();
			var distances = cascaded.CascadeDistances;
			distances[0] = rangeStart;
			distances[1] = rangeStart + rangeSpan * 0.055f;
			distances[2] = rangeStart + rangeSpan * 0.144f;
			distances[3] = rangeStart + rangeSpan * 0.38f;
			distances[4] = rangeStart + rangeSpan;
		}

		/// <summary>Current state of the Scene View toolbar HDR toggle.</summary>
		public bool HdrEnabled => _editorSettings.SceneViewHdr;

		/// <summary>Scene View HDR toggle: live — picks auto vs manual exposure without recreating the environment.</summary>
		public void SetHdrEnabled(bool enabled)
		{
			if (_editorSettings.SceneViewHdr == enabled)
			{
				return;
			}

			_editorSettings.SceneViewHdr = enabled;
			ApplyPipelineFeatures();
		}

		/// <summary>Switches the shading mode; pushed to all loaded models' material cbuffers immediately.</summary>
		public void SetShading(ShadingMode shading)
		{
			if (_shading == shading)
			{
				return;
			}

			_shading = shading;
			ApplyMaterialSettings();
		}

		/// <summary>Rotates the world key light: yaw around Y plus elevation, as offsets from the environment's base sun.</summary>
		public void SetLightRotation(float yawOffsetDegrees, float elevationOffsetDegrees)
		{
			_lightYawOffsetDegrees = yawOffsetDegrees;
			_lightElevationOffsetDegrees = elevationOffsetDegrees;
			ApplyLightRotation();
		}

		/// <summary>Frames the camera on the scene bounds; defers if models are still loading.</summary>
		public void FrameAll()
		{
			if (!TryComputeSceneBounds(out var min, out var max))
			{
				_camera.ResetToDefaults();
				_framePending = true;
				return;
			}

			_framePending = false;
			var center = (min + max) * 0.5f;
			var radius = MathF.Max(0.05f, (max - min).Length() * 0.5f);
			_camera.Frame(center, radius, CameraFovDegrees, resetAngle: true);
			RequestMotionVectorHistoryReset();
		}

		/// <summary>Frames the camera on an entity (F key), keeping the view direction; empty selection frames the whole scene.</summary>
		public void FrameSelection(Entity? selected)
		{
			if (!selected.HasValue || selected.Value.IsNull)
			{
				FrameAll();
				return;
			}

			if (!TryComputeEntityBounds(selected.Value, out var min, out var max))
			{
				FrameAll();
				return;
			}

			var center = (min + max) * 0.5f;
			var radius = MathF.Max(0.05f, (max - min).Length() * 0.5f);
			_camera.Frame(center, radius, CameraFovDegrees, resetAngle: false);
			RequestMotionVectorHistoryReset();
		}

		// Camera teleports must reset TAA/upscaler history or one huge-motion frame smears.
		private void RequestMotionVectorHistoryReset()
		{
			_env.Pipeline.MotionVectorResources?.ResetHistory();
		}

		/// <summary>Whether the viewport is rendering the scene right now (see <see cref="SetActive"/>).</summary>
		public bool IsActive => _active;

		/// <summary>Activates/pauses the prefab scene; the next <see cref="Update"/> detaches the scene under the editor GPU lock.</summary>
		public void SetActive(bool active)
		{
			_active = active;
		}

		/// <summary>Per-frame drive: syncs the env scene with the prefab, moves the camera and executes the offscreen pipeline. Call from EditorManager.OnUpdate BEFORE the main scene renders, under the same GPU lock.</summary>
		public void Update(float deltaTime, float time, Entity? root, string? prefabPath, Entity? selected = null)
		{
			_currentPrefabPath = prefabPath;

			// Pause is indistinguishable from a closed prefab: the scene is torn down and
			// re-instanced by SyncScene when the viewport becomes active again.

			// Recreate the environment before writing the frame: old bindings are not yet referenced.
			if (_pendingEnvironmentRecreate)
			{
				_pendingEnvironmentRecreate = false;
				RecreateEnvironment();
			}

			// Safe point for switching the upscaler backend (GPU barrier + NGX init commands).
			ApplyPendingUpscalerSettings();

			bool hasRoot = _active && root.HasValue && !root.Value.IsNull;
			if (!hasRoot)
			{
				// Prefab closed: drop the scene AND resident models — without Root.Update nothing
				// steps the streamer, so abandoned background loads would hang forever.
				if (_rendered.Count > 0 || _models.Count > 0)
				{
					ClearScene();
				}
				_lastStore = null;

				// The frame still executes below: an empty scene shows the lit environment sky.
			}
			else
			{
				// Reload/prefab switch recreates the EntityStore — every cached entity id is dead.
				var store = root.Value.Store;
				if (!ReferenceEquals(store, _lastStore))
				{
					ClearScene();
					_lastStore = store;
					_framePending = true;

					// Reloading is the retry: the scene rebuilds from scratch.
					_renderFailed = false;
				}

				if (_renderFailed)
				{
					return;
				}

				// Loads are polled by ModelStreamingSystem inside Root.Update below; a finished
				// model is instantiated by the NEXT SyncScene.
				SyncScene(root.Value);
				SyncSelectionHighlight(selected);
				PollProbeBake(deltaTime);
				PollSceneProbeDebugOverlay();
			}

			PollSsrOwnRayScene(deltaTime);

			try
			{
				_lastEye = _camera.Eye;
				_env.SetCameraTransform(_camera.Eye, _camera.Target);

				// Before Root.Update, where CullingAndRenderSystem lays out the cascades.
				SyncSunEntity();

				// Clamp dt to avoid an exposure jump after long editor stalls.
				_env.SetEyeAdaptationDeltaTime(Math.Min(deltaTime, 0.1f));

				// Open the debug frame before the first stage that writes to it: the line list is
				// the whole frame, not an accumulator.
				BeginDebugFrame();

				// Physics BEFORE animation: foot IK rays must probe the world as it will be drawn,
				// and the ragdoll reads its pose from already-integrated bodies.
				PollScenePhysics(deltaTime);

				// Stopped = zero step, not a skipped call: the pose (clip sample, foot IK, spring
				// bones) must still be computed, else fresh skinned models collapse to a point.
				// Skinning must also run BEFORE Root.Update records frame commands: it may grow and
				// recreate the mega vertex buffer under already-recorded DrawIndexedIndirect.
				UpdateAnimation(IsPlaying ? deltaTime : 0f);

				// Play->Stop edge: the stand-up pose transition lives outside the ECS snapshot and
				// must be cleared manually, or the character stays mid-transition forever.
				if (_wasPlaying && !IsPlaying)
				{
					_animation?.EndPlay();
				}

				_wasPlaying = IsPlaying;

				_env.Root.Update(new UpdateTick(deltaTime, time));

				// A frozen graph won't re-upload instance buffers itself (that lives in command
				// recording); movement changes neither instance counts nor buffer capacities, so
				// the recorded commands keep pointing at the same, re-uploaded buffers.
				if (_transformsDirty)
				{
					_transformsDirty = false;
					_env.BatchRenderer.MarkInstancesContentDirty();
					_env.BatchRenderer.CheckAndReallocateBuffers();
				}

				// After all writers, before the graph: upload uses the immediate context, the draw
				// itself is a command inside ForwardPass.
				EndDebugFrame();

				// Execute even without an open prefab: the batch renderer is safe at zero instances
				// and the empty scene shows the environment sky.
				_env.Pipeline.Execute();
			}
			catch (Exception ex)
			{
				// An exception here would abort the whole editor's Present — lose only this frame.
				LogRenderFailure(ex);

				// Stop until the prefab reloads: a persistent error would otherwise rebuild and
				// crash the scene every frame.
				_renderFailed = true;
				ClearScene();
			}
		}

		/// <summary>Draws the frame as an ImGui image, handles camera input and the selection gizmo; returns true if the gizmo changed the selected transform.</summary>
		public bool Render(ImGuiRender imGuiRender, Entity root, Entity? selected, Vector2 size, out PickResult pick)
		{
			_lastImGuiRender = imGuiRender;
			pick = default;

			if (size.X <= 1f || size.Y <= 1f)
			{
				return false;
			}

			if (!_textureBound)
			{
				_textureRef = imGuiRender.GetNewTexture();
				imGuiRender.BindRenderTarget(_textureRef.GetTexID(), _env.ColorTarget);
				_textureBound = true;
			}

			if (TrackAndApplyResize(imGuiRender, size))
			{
				imGuiRender.BindRenderTarget(_textureRef.GetTexID(), _env.ColorTarget);
			}

			var cursor = ImGui.GetCursorScreenPos();

			// Dummy + direct drawlist, NOT ImGui.Image: Image registers as a hoverable item, which
			// makes ImGuizmo's CanActivate (!IsAnyItemHovered && !IsAnyItemActive) refuse drags.
			ImGui.Dummy(size);
			bool hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows)
				&& ImGui.IsMouseHoveringRect(cursor, cursor + size);

			// Neutral gradient backdrop: the offscreen target clears with alpha 0.
			var drawList = ImGui.GetWindowDrawList();
			uint backdropTop = ImGui.GetColorU32(new Vector4(0.55f, 0.55f, 0.55f, 1f));
			uint backdropBottom = ImGui.GetColorU32(new Vector4(0.26f, 0.26f, 0.26f, 1f));
			drawList.AddRectFilledMultiColor(cursor, cursor + size, backdropTop, backdropTop, backdropBottom, backdropBottom);
			drawList.AddImage(_textureRef, cursor, cursor + size);

			_camera.HandleInput(hovered, ImGui.GetIO().DeltaTime);

			if (_camera.FlySpeed != _editorSettings.SceneCameraSpeed)
			{
				_editorSettings.SceneCameraSpeed = _camera.FlySpeed;
			}

			// F frames the selection; ignored while text input is active or the cursor is elsewhere.
			if (hovered && !ImGui.GetIO().WantTextInput && ImGui.IsKeyPressed(ImGuiKey.F))
			{
				FrameSelection(selected);
			}

			// Player input in Play; RMB gives WASD to camera fly (it has priority). Converted to a
			// world vector HERE — only the camera knows what "W = away from camera" means.
			if (IsPlaying && hovered && !ImGui.GetIO().WantTextInput &&
				!ImGui.IsMouseDown(ImGuiMouseButton.Right))
			{
				var forward = _camera.Forward;
				forward.Y = 0f;

				// Looking straight down, "away from camera" is undefined — fall back to world +Z.
				forward = forward.LengthSquared() > 1e-6f ? Vector3.Normalize(forward) : Vector3.UnitZ;
				var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));

				var move = Vector3.Zero;
				if (ImGui.IsKeyDown(ImGuiKey.W) || ImGui.IsKeyDown(ImGuiKey.UpArrow)) move += forward;
				if (ImGui.IsKeyDown(ImGuiKey.S) || ImGui.IsKeyDown(ImGuiKey.DownArrow)) move -= forward;
				if (ImGui.IsKeyDown(ImGuiKey.D) || ImGui.IsKeyDown(ImGuiKey.RightArrow)) move += right;
				if (ImGui.IsKeyDown(ImGuiKey.A) || ImGui.IsKeyDown(ImGuiKey.LeftArrow)) move -= right;

				_playerInput = new PlayerInput
				{
					MoveWorld = move.LengthSquared() > 0f ? Vector3.Normalize(move) : Vector3.Zero,
					Run = ImGui.IsKeyDown(ImGuiKey.LeftShift) || ImGui.IsKeyDown(ImGuiKey.RightShift),
					// IsKeyPressed: edge, not level — holding Space must not auto-jump.
					Jump = ImGui.IsKeyPressed(ImGuiKey.Space, false),
				};
			}

			var status = CollectStatusText();
			if (status != null)
			{
				var textSize = ImGui.CalcTextSize(status);
				drawList.AddText(cursor + (size - textSize) * 0.5f, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.6f)), status);
			}

			// Labels before the gizmo so the gizmo draws on top of bone names.
			DrawDebugLabels(drawList, cursor, size);

			bool gizmoChanged = RenderGizmo(drawList, cursor, size, selected);

			// Pick after the gizmo so clicks on/over it don't change selection; Alt+LMB is camera
			// orbit, not picking.
			bool gizmoBusy = selected.HasValue && !selected.Value.IsNull &&
				(ImGuizmo.IsUsing() || ImGuizmo.IsOver());
			bool altDown = ImGui.IsKeyDown(ImGuiKey.LeftAlt);
			if (hovered && !altDown && !gizmoBusy && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
			{
				pick = new PickResult
				{
					Clicked = true,
					Entity = Pick(cursor, size, ImGui.GetMousePos()),
				};
			}

			return gizmoChanged;
		}

		// Bone labels via ImGui text (the engine has no 3D text), projected with the SAME view/proj
		// the frame was rendered with — otherwise labels drift off bones at the frame edges.
		private void DrawDebugLabels(ImDrawListPtr drawList, Vector2 cursor, Vector2 size)
		{
			var labels = _debugDraw.Labels;
			if (labels.Count == 0 || size.X < 1f || size.Y < 1f)
			{
				return;
			}

			var view = Matrix4x4.CreateLookAtLeftHanded(_lastEye, _camera.Target, Vector3.UnitY);
			var projection = Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(
				CameraFovDegrees * (MathF.PI / 180f), size.X / size.Y, CameraNear, CameraFar);

			var viewProjection = view * projection;

			for (int i = 0; i < labels.Count; i++)
			{
				var label = labels[i];
				var clip = Vector4.Transform(new Vector4(label.Position, 1f), viewProjection);

				// Behind or exactly in the camera plane: dividing by w would mirror the point into view.
				if (clip.W <= 1e-4f)
				{
					continue;
				}

				var ndc = new Vector2(clip.X / clip.W, clip.Y / clip.W);
				if (ndc.X < -1.2f || ndc.X > 1.2f || ndc.Y < -1.2f || ndc.Y > 1.2f)
				{
					continue;
				}

				var screen = cursor + new Vector2(
					(ndc.X * 0.5f + 0.5f) * size.X,
					(0.5f - ndc.Y * 0.5f) * size.Y);

				// 1px drop shadow keeps names readable on both light and dark geometry.
				drawList.AddText(screen + new Vector2(1f, 1f), ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.75f)),
					label.Text);
				drawList.AddText(screen, ImGui.GetColorU32(label.Color), label.Text);
			}
		}

		// Same view/proj the camera rendered with: LH lookAt + LH perspective WITHOUT reversed-Z
		// (screen projection matches MakePerspectiveReversedZ; ImGuizmo only needs monotonic depth).
		private bool RenderGizmo(ImDrawListPtr drawList, Vector2 cursor, Vector2 size, Entity? selected)
		{
			if (!selected.HasValue || selected.Value.IsNull)
			{
				return false;
			}

			var view = Matrix4x4.CreateLookAtLeftHanded(_lastEye, _camera.Target, Vector3.UnitY);
			var proj = Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(
				CameraFovDegrees * (MathF.PI / 180f), size.X / size.Y, CameraNear, CameraFar);

			ImGuizmo.SetImGuiContext(ImGui.GetCurrentContext());
			ImGuizmo.SetOrthographic(false);
			ImGuizmo.BeginFrame();
			ImGuizmo.SetDrawlist(drawList);
			ImGuizmo.SetRect(cursor.X, cursor.Y, size.X, size.Y);

			var world = ComputeWorldMatrix(selected.Value);
			if (!ImGuizmo.Manipulate(ref view, ref proj, Operation, ImGuizmoMode.Local, ref world))
			{
				return false;
			}

			ApplyWorldMatrix(selected.Value, world);
			return true;
		}

		private string? CollectStatusText()
		{
			int loading = 0;
			int failed = 0;
			foreach (var state in _models.Values)
			{
				if (state.Failed)
				{
					failed++;
				}
				else if (!state.Ready)
				{
					loading++;
				}
			}

			if (loading > 0)
			{
				return $"Loading models... ({loading})";
			}
			if (failed > 0)
			{
				return $"Model load failed ({failed}) - see Console";
			}
			return null;
		}

		// --- Prefab -> env scene sync ---------------------------------------------------------------

		private static string? ResolveEnvironmentHdrPath(string configured)
		{
			if (string.IsNullOrWhiteSpace(configured))
			{
				return null;
			}

			if (File.Exists(configured))
			{
				return configured;
			}

			var relative = Path.Combine("EditorAssets", configured);
			return File.Exists(relative) ? relative : configured;
		}

		// --- Camera / resize ------------------------------------------------------------------------

		// Debounced target resize; see ModelPreviewViewport.TrackAndApplyResize.
		private bool TrackAndApplyResize(ImGuiRender imGuiRender, Vector2 imGuiSize)
		{
			var width = (uint)Math.Max(1, MathF.Round(imGuiSize.X));
			var height = (uint)Math.Max(1, MathF.Round(imGuiSize.Y));
			var requestedSize = new Vector2(width, height);

			// Render scale is applied HERE — the only point in the frame where resizing targets is safe.
			var scale = Math.Clamp(_editorSettings.RenderScale, 0.25f, 1f);
			_env.SetRenderScale(scale);

			if (requestedSize != _pendingSize || scale != _pendingRenderScale)
			{
				_pendingSize = requestedSize;
				_pendingRenderScale = scale;
				_resizeIdleSeconds = 0f;
				return false;
			}

			if (requestedSize == _env.ColorTarget.Size &&
			    _env.Pipeline.SceneSizeFor(requestedSize) == _env.DepthTarget.Size)
			{
				return false;
			}

			_resizeIdleSeconds += ImGui.GetIO().DeltaTime;
			if (_resizeIdleSeconds < ResizeSettleSeconds)
			{
				return false;
			}

			return ResizeTargets(imGuiRender, requestedSize);
		}

		// Same barrier/rebind order as ModelPreviewViewport.ResizeTargets.
		private bool ResizeTargets(ImGuiRender imGuiRender, Vector2 newSize)
		{
			var width = (uint)newSize.X;
			var height = (uint)newSize.Y;

			_env.DilApi.ImmediateContext.Flush();
			_env.DilApi.ImmediateContext.WaitForIdle();

			imGuiRender.ReleaseRenderTargetBinding(_textureRef.GetTexID());

			// Scene targets use RENDER size; ColorTarget always stays at display size.
			var sceneSize = _env.Pipeline.SceneSizeFor(newSize);

			_env.ColorTarget.Resize(newSize);
			_env.DepthTarget.Resize(sceneSize);
			_env.SceneCopyTarget.Resize(sceneSize);

			// Reflection G-buffer lives at render size with depth (read by the SSR passes).
			_env.Pipeline.Targets?.NormalRoughnessTarget?.Resize(sceneSize);
			_env.Pipeline.Targets?.EnvFactorTarget?.Resize(sceneSize);
			_env.AoTarget?.Resize(sceneSize);
			_env.GiTarget?.Resize(sceneSize);

			// HDR frame at scene size; the luminance-measure chain is fixed-size (see EyeAdaptationPass).
			_env.HdrColorTarget?.Resize(sceneSize);

			_env.RebindPostProcessTargets();

			// Selection overlay draws into the display-size frame after tonemap.
			_selectionOverlay?.Resize(newSize);

			// Resize creates a new native texture: transmissive materials must rebind _SceneColor.
			foreach (var state in _models.Values)
			{
				if (state.Model == null)
				{
					continue;
				}
				// Only this environment's material set: another set's _SceneColor points at a
				// different SceneCopyTarget and must not be touched from here.
				var rebindTargets = state.Materials ?? state.Model.materialObjects;
				foreach (var material in rebindTargets.Values)
				{
					material.SetTexture("_SceneColor", _env.SceneCopyTarget);
				}
			}

			_env.Pipeline.SetOffscreenViewportSize(newSize);

			ref var cameraComponent = ref _env.CameraEntity.GetComponent<CameraComponent>();
			cameraComponent.data.viewport = new Vector4(0, 0, width, height);
			cameraComponent.data.aspect = width / (float)height;
			cameraComponent.RecalculateProjection();

			return true;
		}

		// --- Prefab TRS hierarchy -------------------------------------------------------------------

		public static Matrix4x4 ComputeWorldMatrix(Entity entity)
		{
			var local = LocalMatrix(entity);
			var parent = entity.Parent;
			return parent.IsNull ? local : local * ComputeWorldMatrix(parent);
		}

		// Parent-space matrix: multiply the entity's local Position/Rotation by this to get world.
		internal static Matrix4x4 ParentToWorldMatrix(Entity entity)
		{
			var parent = entity.Parent;
			return parent.IsNull ? Matrix4x4.Identity : ComputeWorldMatrix(parent);
		}

		private static Matrix4x4 LocalMatrix(Entity entity)
		{
			Vector3 pos = entity.HasPosition ? entity.Position.value : Vector3.Zero;
			Quaternion rot = entity.HasRotation ? entity.Rotation.value : Quaternion.Identity;
			Vector3 scale = entity.HasScale3 ? entity.Scale3.value : Vector3.One;
			return MathUtils.CreateTrs(pos, rot, scale);
		}

		private static void ApplyWorldMatrix(Entity entity, Matrix4x4 world)
		{
			var parent = entity.Parent;
			var local = world;
			if (!parent.IsNull)
			{
				var parentWorld = ComputeWorldMatrix(parent);
				if (Matrix4x4.Invert(parentWorld, out var parentInv))
				{
					local = world * parentInv;
				}
			}

			if (!Matrix4x4.Decompose(local, out var scale, out var rotation, out var translation))
			{
				return;
			}

			if (!entity.HasPosition)
			{
				entity.AddComponent<Position>();
			}
			entity.Position = new Position(translation.X, translation.Y, translation.Z);

			if (!entity.HasRotation)
			{
				entity.AddComponent<Rotation>();
			}
			entity.Rotation = new Rotation { value = rotation };

			if (!entity.HasScale3)
			{
				entity.AddComponent<Scale3>();
			}
			entity.Scale3 = new Scale3(scale.X, scale.Y, scale.Z);
		}
	}
}
