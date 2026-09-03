using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using DecaEngine.Core;
using DecaEngine.Core.Diagnostics;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
using DecaEngine.Graphics.ProbeGi;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using Hexa.NET.ImGui;
using DecaEngine.Scene;
using DecaEngine.Animation;

namespace DecaEngine.Editor
{
	/// <summary>Self-contained off-screen model preview: own EntityStore, renderer and targets.</summary>
	public partial class ModelPreviewViewport
	{
		/// <summary>Sub-mesh view mode; only meaningful while a single sub-mesh is isolated.</summary>
		public enum SubMeshPreviewMode
		{
			Highlight,
			Channel,
			Lighting,
		}

		/// <summary>Debug channel visualized in <see cref="SubMeshPreviewMode.Channel"/>.</summary>
		public enum PreviewChannel
		{
			Normal,
			Uv,
			Tangent,
		}

		private const uint InitialWidth = 256;
		private const uint InitialHeight = 256;
		private const float CameraFovDegrees = ModelViewportEnvironment.CameraFovDegrees;

		// Debounce: each resize costs a GPU stall, so wait out the user's drag.
		private const float ResizeSettleSeconds = 0.3f;

		private readonly IGraphicsApi _graphicsApi;
		private readonly EditorSettings _editorSettings;
		private readonly ModelStore _modelStore;
		private readonly SharedViewportResources _sharedResources;
		private ModelViewportEnvironment _env;

		/// <summary>Whether volumetric light has cascaded shadows; god rays need them.</summary>
		public bool VolumetricShadowsAvailable => _env?.VolumetricShadowsAvailable ?? false;

		/// <summary>Current off-screen environment; recreated on env-level settings - do not cache.</summary>
		public ModelViewportEnvironment Environment => _env;

		// Config baked into the CURRENT environment; diffed to decide if it must be recreated.
		private bool _appliedSsao;
		private AmbientOcclusionMode _appliedAoMode;
		private bool _appliedSsgi;
		private bool _appliedSky;
		private string _appliedHdrPath = "";
		private bool _appliedAniso;

		// Baked into the loader's decoder, i.e. into GPU textures: only a reload can apply it.
		private int _appliedMaxTextureSize;

		// Creation-level: turns the preview pipeline HDR, and target format is baked into PSOs.
		private bool _appliedEyeAdaptation;

		// Creation-level: the pass needs depth and scene-copy, built with the pipeline.
		private bool _appliedFog;

		// Creation-level: the pass needs depth, scene-copy and the shadow map.
		private bool _appliedVolumetric;

		// Creation-level: owns its own target chain.
		private bool _appliedBloom;

		// Creation-level: the pass owns its own frame copy.
		private bool _appliedColorGrade;

		// Creation-level: the pass owns its RG16F buffer.
		private bool _appliedMotionVectors;

		// RecreateEnvironment must unbind the old target from ImGui before releasing it.
		private ImGuiRender? _lastImGuiRender;

		// Executed at the top of Update(), before frame recording, while old bindings are idle.
		private bool _pendingEnvironmentRecreate;

		private readonly List<Entity> _instanceEntities = new();

		private string? _loadedPath;
		private int _loadedSubMesh = -1;
		private string? _loadError;
		private string? _loadingPath;
		private int _loadingSubMesh = -1;

		// The editor model is resident in EXACTLY one viewport - here or PrefabSceneViewport, never
		// both. Defaults to active: CLI probes drive the viewport without ever calling SetActive.
		private bool _active = true;
		private bool _activeRequested = true;

		// Selection captured on suspend; restored on activation unless another model was requested.
		private string? _suspendedPath;
		private int _suspendedSubMesh = -1;

		private bool _restoringAfterResume;

		// Preview is the exclusive consumer: the previous model is fully cleared before a new load.
		private readonly ModelStreamer _streamer;

		private ModelStreamer.Resident? _streamingModel;

		// Material set owned by THIS environment: the primary set may belong to the prefab scene,
		// and writing into it would clobber that scene's lighting and color.
		private OrderedDictionary<int, IMaterialObject>? OwnMaterials =>
			_streamingModel?.Materials ?? _residentModel?.materialObjects;

		// Radius from the last FrameAll; PollPendingLoad pushes the AO world range from it only
		// AFTER its own Flush()+WaitForIdle() barrier.
		private float _framedRadius;

		// Already parsed and registered with _env.BatchRenderer: switching sub-mesh must repopulate
		// from memory rather than re-read the file.
		private string? _residentPath;
		private ModelLoader? _residentModel;

		// Progressive CPU bake of the irradiance probe grid; one round at a time on a background
		// task. Rotating the light keeps accumulated data - only model or grid changes reset it.
		private ProbeGiBaker? _probeBaker;

		// BVH build is tens of CPU-seconds on a heavy scene; must not run on the render thread.
		private Task<ProbeGiBaker>? _probeBakerTask;

		// Model _probeBakerTask is computed for: a result for another model is dropped, and
		// releasing that model must await the task.
		private ModelLoader? _probeBakerModel;

		private System.Diagnostics.Stopwatch? _probeBakerSw;
		private volatile bool _probeBakerFromCache;

		private ProbeGiBakeSession? _probeSession;
		private Task? _probeRoundTask;
		private ProbeGiTextures? _probeTextures;
		private Vector3 _probeBoundsMin, _probeBoundsMax;
		private float _probeSessionDelay = -1f;  // seconds until session rebuild; <0 = not requested
		private int _probeTextureGeneration;     // suffix for GPU texture names (must be unique)

		// GPU round path; owns its own buffers and writes atlases via UAV. null = CPU rounds.
		private ProbeRoundGpu? _probeGpu;

		// ~2 ms of GPU work per chunk; capped so one frame can't swallow a whole round.
		private const int ProbeChunksPerFrame = 8;

		// Outlive session and model: compiling them costs ~650 ms on the render thread.
		private ProbeRoundPipelines? _probePipelines;
		private DiligentGraphicsApi? _probePipelinesApi;

		// Lives per MODEL, not per session: rebuilding BLAS per settings tweak exhausted GPU memory.
		private ProbeSceneAccel? _probeAccel;

		// Instance poses the current TLAS was built from; order matches ProbeInstancedGeometry.
		private readonly List<Matrix4x4> _probeInstancePoses = new();

		// A failed TLAS rebuild is never retried, else it would fail again every moving frame.
		private bool _probeAccelFrozen;

		// Remembers the atlases it was built for; recreating them rebuilds the overlay set.
		private readonly List<(ProbeDebugOverlay Overlay, ProbeGiTextures Textures)> _probeDebugOverlays = new();
		private bool _probeDebugFailed;

		/// <summary>Whether the device supports inline ray tracing.</summary>
		public bool RayTracingSupported => _graphicsApi.RayTracing >= RayTracingSupport.Inline;

		// TLAS for RT shadows, separate from _probeAccel: shadow rays need it without probe GI.
		private DiligentRayTracingScene? _rtShadowScene;

		public ModelPreviewViewport(IGraphicsApi graphicsApi, EditorSettings editorSettings, ModelStore modelStore,
			SharedViewportResources sharedResources = null)
		{
			_graphicsApi = graphicsApi;
			_editorSettings = editorSettings;
			_modelStore = modelStore;

			_sharedResources = sharedResources ?? new SharedViewportResources(graphicsApi);

			_env = CreateEnvironment();

			_streamer = new ModelStreamer(_env, _modelStore, _graphicsApi, BuildLoadOptions);
			_env.Root.Add(new ModelStreamingSystem(_streamer));

			ApplyGraphicsSettings();

			// The viewport lives for the whole editor session, so no unsubscribe is needed.
			SettingsWindow.PreviewGraphicsApplied += OnGraphicsSettingsChanged;
		}

		// Shadows are always created: the pass is cheap and no-ops live via ShadowSettings.Enabled.
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
			_appliedEyeAdaptation = _editorSettings.PreviewEyeAdaptation;
			_appliedFog = _editorSettings.PreviewFog;
			_appliedVolumetric = _editorSettings.PreviewVolumetric;
			_appliedBloom = _editorSettings.PreviewBloom;
			_appliedColorGrade = _editorSettings.PreviewColorGrade;
			_appliedMotionVectors = _editorSettings.PreviewMotionVectors;

			var env = new ModelViewportEnvironment(_graphicsApi, InitialWidth, InitialHeight,
				"Model Preview Color", "Model Preview Depth", _sharedResources,
				skyBackground: _appliedSky,
				environmentHdrPath: _appliedHdrPath.Length > 0 ? _appliedHdrPath : null,
				ssao: _appliedSsao,
				shadows: true,
				aoMode: _appliedAoMode,
				ssgi: _appliedSsgi,
				eyeAdaptation: _appliedEyeAdaptation,
				fog: _appliedFog,
				bloom: _appliedBloom,
				colorGrade: _appliedColorGrade,
				volumetric: _appliedVolumetric,
				// SSR needs motion vectors for history reprojection, same as TemporalUpscale.
				motionVectors: _appliedMotionVectors || _editorSettings.PreviewSsr,
				temporalUpscale: _appliedMotionVectors && _editorSettings.TemporalUpscale,
				upscalerBackend: _appliedMotionVectors && _editorSettings.TemporalUpscale
					? Math.Clamp(_editorSettings.UpscalerBackend, 0, 2)
					: 0,
				ssr: _editorSettings.PreviewSsr,
				// ApplyPipelineFeatures turns RT fallback on later: no probe accel exists yet here.
				ssrRayTraced: false);

			// Must be set BEFORE the first frame: SimpleCullingAndRenderSystem freezes the
			// DirectionalLightCascadeData capacity from it.
			if (env.ShadowSettings != null)
			{
				env.ShadowSettings.CascadeCount = ShadowRenderer.MaxCascades;
			}

			return env;
		}

		private void FrameAll(Vector3 min, Vector3 max)
		{
			if (float.IsNaN(min.X) || float.IsNaN(min.Y) || float.IsNaN(min.Z) ||
			    float.IsNaN(max.X) || float.IsNaN(max.Y) || float.IsNaN(max.Z) ||
			    float.IsInfinity(min.X) || float.IsInfinity(min.Y) || float.IsInfinity(min.Z) ||
			    float.IsInfinity(max.X) || float.IsInfinity(max.Y) || float.IsInfinity(max.Z))
			{
				_orbitTarget = Vector3.Zero;
				_distance = 4f;
				_yaw = -0.6f;
				_pitch = 0.35f;
				_framedRadius = 0f;
				return;
			}

			_orbitTarget = (min + max) * 0.5f;

			// AABB half-diagonal used as a bounding-sphere radius around _orbitTarget.
			var radius = MathF.Max(0.05f, (max - min).Length() * 0.5f);

			// The same bounds drive the sun's ortho camera; shadows follow from the next frame.
			if (_env.ShadowSettings != null)
			{
				_env.ShadowSettings.BoundsCenter = _orbitTarget;
				_env.ShadowSettings.BoundsRadius = radius;
			}

			// World-space AO radius, pushed by PollPendingLoad instead of here: touching the GPU
			// buffer before its Flush()+WaitForIdle() barrier races the in-flight frame.
			_framedRadius = radius;

			_distance = ModelViewportGeometry.ComputeFramingDistance(radius, CameraFovDegrees);

			_yaw = -0.6f;
			_pitch = 0.35f;
		}

		/// <summary>Whether the preview is shown right now; when paused the model is off the GPU.</summary>
		public bool IsActive => _activeRequested;

		/// <summary>Resumes or pauses the preview; applied at the start of the next <see cref="Update"/>.</summary>
		public void SetActive(bool active)
		{
			_activeRequested = active;
		}

		private void ApplyPendingActiveChange()
		{
			_active = _activeRequested;

			if (!_active)
			{
				_suspendedPath = _loadedPath ?? _loadingPath;
				_suspendedSubMesh = _loadedPath != null ? _loadedSubMesh : _loadingSubMesh;

				CancelPendingLoad();
				UnloadResidentModel();
				return;
			}

			var restorePath = _suspendedPath;
			var restoreSubMesh = _suspendedSubMesh;
			_suspendedPath = null;
			_suspendedSubMesh = -1;

			// A request that arrived WHILE paused wins over the saved selection.
			if (restorePath != null && _loadedPath == null && _loadingPath == null)
			{
				_restoringAfterResume = true;
				try
				{
					LoadModel(restorePath, restoreSubMesh);
				}
				finally
				{
					_restoringAfterResume = false;
				}
			}
		}

		/// <summary>Steps this viewport's ECS/render graph and records one off-screen frame.</summary>
		public void Update(float deltaTime, float time)
		{
			// Applied here, not in SetActive: unloading needs a GPU barrier and the editor's lock.
			if (_activeRequested != _active)
			{
				ApplyPendingActiveChange();
			}

			if (!_active)
			{
				return;
			}

			PollSsrOwnRayScene(deltaTime);

			if (_pendingEnvironmentRecreate)
			{
				_pendingEnvironmentRecreate = false;
				RecreateEnvironment();
			}

			// Only safe point to swap the upscaler backend: after any recreate, before recording.
			ApplyPendingUpscalerSettings();

			PollPendingLoad();
			PollProbeBake(deltaTime);
			PollBvhDebugOverlay();
			PollProbeDebugOverlay();

			var liveProbeParams = (_editorSettings.ProbeGiShadowFloor, _editorSettings.ProbeGiSkyShadowFloor,
				_editorSettings.ProbeGiSpecularFloor, _editorSettings.ProbeGiSunIntensity,
				_editorSettings.ProbeGiAmbientBoost, _editorSettings.ProbeGiNormalBias,
				_editorSettings.PreviewProbeGi, _editorSettings.ProbeGiDebugView);
			if (liveProbeParams != _lastLiveProbeParams)
			{
				_lastLiveProbeParams = liveProbeParams;
				ApplyPreviewSettingsToMaterials();
			}

			// Clamped: long editor stalls (model load, probe bake) would jump the exposure.
			_env.SetEyeAdaptationDeltaTime(Math.Min(deltaTime, 0.1f));

			try
			{
				var eye = ModelViewportGeometry.ComputeOrbitEye(_orbitTarget, _distance, _yaw, _pitch);
				_env.SetCameraTransform(eye, _orbitTarget);

				// The frame is recorded even with no model: ModelStreamingSystem drives loading
				// from INSIDE Root.Update, off the camera entity SetCameraTransform just updated.
				_env.Root.Update(new UpdateTick(deltaTime, time));
				_env.Pipeline.Execute();
			}
			catch (Exception ex)
			{
				// This runs inside the editor's GPU lock, before the main scene's Present(): an
				// escaping exception would skip Present() on this and every following frame.
				_loadError = ex.Message;
				EngineLog.Add(LogLevel.Error, $"Model preview: render failed for '{_loadedPath}': {ex.Message}");
				ClearInstances();
			}
		}

		/// <summary>Draws the off-screen image as an ImGui.Image and handles orbit/pan/zoom input.</summary>
		public void Render(ImGuiRender imGuiRender, Vector2 size)
		{
			_lastImGuiRender = imGuiRender;

			if (size.X <= 1f || size.Y <= 1f)
			{
				return;
			}

			if (!_textureBound)
			{
				// Bind now, not on the first settled resize: the first ~ResizeSettleSeconds of the
				// viewport's life would otherwise draw an unbound image.
				_textureRef = imGuiRender.GetNewTexture();
				imGuiRender.BindRenderTarget(_textureRef.GetTexID(), _env.ColorTarget);
				_textureBound = true;
			}

			bool resized = TrackAndApplyResize(imGuiRender, size);

			if (resized)
			{
				// Resize recreates the GPU texture; rebind onto the SAME ImTextureID, since a fresh
				// one would leak an entry in ImGuiDiligentRender's texture table per resize.
				imGuiRender.BindRenderTarget(_textureRef.GetTexID(), _env.ColorTarget);
			}

			var cursor = ImGui.GetCursorScreenPos();

			// Backdrop gradient behind the alpha-0 off-screen target. Must stay neutral (R=G=B):
			// the ImGui color path swaps R/B here. Must match UnlitInstancedPS.hlsl's backdrop.
			var backdropDrawList = ImGui.GetWindowDrawList();
			uint backdropTop = ImGui.GetColorU32(new Vector4(0.55f, 0.55f, 0.55f, 1f));
			uint backdropBottom = ImGui.GetColorU32(new Vector4(0.26f, 0.26f, 0.26f, 1f));
			backdropDrawList.AddRectFilledMultiColor(cursor, cursor + size,
				backdropTop, backdropTop, backdropBottom, backdropBottom);

			ImGui.Image(_textureRef, size);

			bool hovered = ImGui.IsItemHovered();
			HandleCameraInput(hovered);

			if (!HasModel)
			{
				var drawList = ImGui.GetWindowDrawList();
				var text = _loadError ?? "No model loaded";
				var textSize = ImGui.CalcTextSize(text);
				var textPos = cursor + (size - textSize) * 0.5f;
				drawList.AddText(textPos, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.6f)), text);
			}
		}

		// Debounces ResizeTargets, and is also the only point in the frame where resizing targets
		// is safe: the preview image is not in the ImGui draw list yet.
		private bool TrackAndApplyResize(ImGuiRender imGuiRender, Vector2 imGuiSize)
		{
			var width = (uint)Math.Max(1, MathF.Round(imGuiSize.X));
			var height = (uint)Math.Max(1, MathF.Round(imGuiSize.Y));
			var requestedSize = new Vector2(width, height);

			var scale = Math.Clamp(_editorSettings.RenderScale, 0.25f, 1f);
			_env.SetRenderScale(scale);

			if (requestedSize != _pendingSize || scale != _pendingRenderScale)
			{
				_pendingSize = requestedSize;
				_pendingRenderScale = scale;
				_resizeIdleSeconds = 0f;
				return false;
			}

			// Compared against actual target sizes, not against a "settings changed" flag: that
			// survives environment recreation and any missed event.
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

		private bool ResizeTargets(ImGuiRender imGuiRender, Vector2 newSize)
		{
			var width = (uint)newSize.X;
			var height = (uint)newSize.Y;

			// There is no frame-in-flight fence, so disposing the old texture races the GPU.
			// Flush() must precede WaitForIdle(), else unsubmitted commands are still pending.
			_env.DilApi.ImmediateContext.Flush();
			_env.DilApi.ImmediateContext.WaitForIdle();

			// Must precede Resize(): releasing the cached ImGui binding after its view is gone crashes.
			imGuiRender.ReleaseRenderTargetBinding(_textureRef.GetTexID());

			// Scene targets live at RENDER size; ColorTarget stays at display size for ImGui.
			var sceneSize = _env.Pipeline.SceneSizeFor(newSize);

			_env.ColorTarget.Resize(newSize);
			_env.DepthTarget.Resize(sceneSize);

			// CopyTexture copies 1:1, so this must match the geometry target size; _SceneColor is
			// rebound below because Resize hands out a different native texture.
			_env.SceneCopyTarget.Resize(sceneSize);

			// Reflection G-buffer lives at render size alongside depth; the SSR passes read it.
			_env.Pipeline.Targets?.NormalRoughnessTarget?.Resize(sceneSize);
			_env.Pipeline.Targets?.EnvFactorTarget?.Resize(sceneSize);
			_env.AoTarget?.Resize(sceneSize);
			_env.GiTarget?.Resize(sceneSize);

			// The luminance-reduction chain is fixed-size and needs no resize.
			_env.HdrColorTarget?.Resize(sceneSize);

			_env.RebindPostProcessTargets();
			if (_residentModel != null)
			{
				foreach (var material in OwnMaterials!.Values)
				{
					material.SetTexture("_SceneColor", _env.SceneCopyTarget);
				}
			}

			// Must run right after Resize(), before anything below that could throw: otherwise the
			// render graph keeps replaying a frozen command buffer over disposed views.
			_env.Pipeline.SetOffscreenViewportSize(newSize);

			ref var cameraComponent = ref _env.CameraEntity.GetComponent<CameraComponent>();
			cameraComponent.data.viewport = new Vector4(0, 0, width, height);
			cameraComponent.data.aspect = width / (float)height;
			cameraComponent.RecalculateProjection();

			return true;
		}

		private void HandleCameraInput(bool hovered)
		{
			var io = ImGui.GetIO();

			if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
			{
				_orbiting = true;
			}
			if (_orbiting && ImGui.IsMouseReleased(ImGuiMouseButton.Right))
			{
				_orbiting = false;
			}

			if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Middle))
			{
				_panning = true;
			}
			if (_panning && ImGui.IsMouseReleased(ImGuiMouseButton.Middle))
			{
				_panning = false;
			}

			if (_orbiting)
			{
				var delta = io.MouseDelta;
				_yaw -= delta.X * 0.01f;
				_pitch = Math.Clamp(_pitch - delta.Y * 0.01f, -1.5f, 1.5f);
			}
			else if (_panning)
			{
				var delta = io.MouseDelta;
				var right = new Vector3(MathF.Cos(_yaw), 0f, -MathF.Sin(_yaw));
				var panScale = MathF.Max(0.01f, _distance * 0.001f);
				_orbitTarget -= right * delta.X * panScale;
				_orbitTarget += Vector3.UnitY * delta.Y * panScale;
			}

			if (hovered && io.MouseWheel != 0f)
			{
				_distance = Math.Clamp(_distance + io.MouseWheel * _distance * 0.1f, 0.2f, 1500f);
			}
		}
	}
}



