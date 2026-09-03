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
	/// <summary>Applies the Graphics window to the preview: pipeline features, live knobs, environment recreation.</summary>
	public partial class ModelPreviewViewport
	{
		// Load-level option (shader-variant keyword); changes go through environment recreation.
		private bool RtShadowsEnabled() =>
			_editorSettings.ShadowFilterMode == 4 && RayTracingSupported;
		private bool _appliedRtShadows;

		// Once the GPU path fails, never retry until editor restart (a driver crash would loop).
		private bool _probeGpuDisabled;

		// Options the CURRENT session was created with; sun intensity is excluded - it flows into
		// the live session every round.
		private (bool On, float Sky, int Rays, int Bounces, float BounceSaturation,
			float Density, int MaxProbes, bool HardwareTrace, int VisRes) _probeSessionOptions;

		// Snapshot of live shader knobs; Update pushes the cbuffer on any change directly.
		private (float ShadowFloor, float SkyFloor, float SpecFloor, float Sun, float Boost, float Bias, bool On, bool Debug) _lastLiveProbeParams;
		private readonly System.Diagnostics.Stopwatch _probeRoundSw = new();
		private long _probeRoundMs;
		private string _probeStatus = "no probes";

		/// <summary>Probe-GI status line for the Graphics window.</summary>
		public string ProbeGiStatus
		{
			get
			{
				if (!_editorSettings.PreviewProbeGi)
				{
					return "disabled";
				}

				var session = _probeSession;
				if (session == null)
				{
					return _probeStatus;
				}

				var grid = $"{session.CountX}x{session.CountY}x{session.CountZ} probes";

				// The Graphics checkbox shows intent; the live path is chosen at session start and
				// may legitimately differ (no inline RT, or session not yet recreated).
				grid += _probeGpu == null ? ", GPU path did not come up"
					: _probeGpu.Hardware ? ", hardware tracing"
					: ", software tracing";

				if (session.Realtime)
				{
					// Round number is meaningless in realtime; only the pace matters.
					return $"{grid}, realtime ({_probeRoundMs} ms/round)";
				}

				return session.Converged
					? $"{grid}, done ({_probeRoundMs} ms/round)"
					: $"{grid}, round {session.Round}/{session.TargetRounds}";
			}
		}

		// Session-recreate debounce: sliders fire every drag frame and recreation drops the field.
		private const float ProbeRebakeDebounceSeconds = 0.25f;
		private readonly Dictionary<int, MeshId> _meshIdMap = new();
		private readonly Dictionary<int, MaterialId> _materialIdMap = new();
		private readonly Dictionary<(int, int), BatchId> _batchCache = new();

		// Wireframe overlay: the batch renderer cannot redraw a batch with another PSO, so a second
		// material/batch/instance set draws the same geometry again.
		private IMaterialObject? _wireframeMaterial;
		private MaterialId? _wireframeMaterialId;
		private readonly Dictionary<int, BatchId> _wireframeBatchCache = new();
		private readonly List<Entity> _wireframeEntities = new();
		private bool _wireframeEnabled;

		private SubMeshPreviewMode _viewMode = SubMeshPreviewMode.Highlight;
		private PreviewChannel _previewChannel = PreviewChannel.Normal;

		private PreviewFeatureFlags _featureFlags = PreviewFeatureFlags.All;

		/// <summary>Current Lighting-preview feature toggles.</summary>
		public PreviewFeatureFlags FeatureFlags => _featureFlags;

		/// <summary>Toggles Lighting-preview features; applied to the resident model immediately.</summary>
		public void SetFeatureFlags(PreviewFeatureFlags flags)
		{
			if (_featureFlags == flags)
			{
				return;
			}

			_featureFlags = flags;
			ApplyPreviewSettingsToMaterials();
		}

		// Light slider offsets in degrees FROM the environment's base sun position; stored here,
		// not in ShadowSettings, to survive environment recreation.
		private float _lightYawOffsetDegrees;
		private float _lightElevationOffsetDegrees;

		// Absolute sun elevation clamp: at horizon/zenith the cascade ortho camera degenerates.
		private const float LightElevationMinDegrees = -85f;
		private const float LightElevationMaxDegrees = 85f;

		/// <summary>Current light slider offsets - see <see cref="SetLightRotation"/>.</summary>
		public float LightYawDegrees => _lightYawOffsetDegrees;
		public float LightElevationDegrees => _lightElevationOffsetDegrees;

		/// <summary>Rotates the world key light: yaw around Y plus elevation, both offsets from the
		/// environment's base sun position; applied live, and yaw also feeds sky/IBL shaders.</summary>
		public void SetLightRotation(float yawOffsetDegrees, float elevationOffsetDegrees)
		{
			_lightYawOffsetDegrees = yawOffsetDegrees;
			_lightElevationOffsetDegrees = elevationOffsetDegrees;
			ApplyLightRotation();
		}

		// Elevation is not applied to the equirect map: rotating a panorama is cheap only around Y.
		private void ApplyLightRotation()
		{
			var shadowSettings = _env.ShadowSettings;
			if (shadowSettings == null)
			{
				return;
			}

			shadowSettings.SetAngles(
				shadowSettings.BaseYawDegrees + _lightYawOffsetDegrees,
				Math.Clamp(shadowSettings.BaseElevationDegrees + _lightElevationOffsetDegrees,
					LightElevationMinDegrees, LightElevationMaxDegrees));

			_env.Pipeline.SkyResources?.SetEnvironmentYaw(shadowSettings.EnvYawRadians);
			PushSsrEnvironment();
			ApplyPreviewSettingsToMaterials();

			// No probe rebake needed: PollProbeBake pulls the light into the live session each
			// round and the field flows to the new solution.
		}

		private Vector3 _orbitTarget = Vector3.Zero;
		private float _yaw = -0.6f;
		private float _pitch = 0.35f;
		private float _distance = 4f;
		private bool _orbiting;
		private bool _panning;

		private ImTextureRef _textureRef;
		private bool _textureBound;

		private Vector2 _pendingSize;
		private float _resizeIdleSeconds;

		// Render scale seen by the last TrackAndApplyResize; a change resets the debounce timer.
		private float _pendingRenderScale = 1f;

		// Deferred to start of Update: backend switch waits for the GPU and creates an NGX feature,
		// which cannot happen mid-ImGui-frame.
		private bool _pendingUpscalerApply;

		// DLSS combo index maps to NVSDK_NGX_PerfQuality_Value: {Perf, Balanced, Quality, DLAA} = {0, 1, 2, 5}.
		private void ApplyPendingUpscalerSettings()
		{
			if (!_pendingUpscalerApply || _env is null)
			{
				return;
			}

			_pendingUpscalerApply = false;
			ViewportSettingsPush.Upscaler(_env, _editorSettings);
		}

		public string? LoadedPath => _loadedPath;

		public string? LoadError => _loadError;

		public bool HasModel => _instanceEntities.Count > 0;

		/// <summary>True while a single sub-mesh (rather than the whole model) is isolated - only then
		/// is <see cref="ViewMode"/>/<see cref="Channel"/> meaningful (see <see cref="InspectorWindow"/>).</summary>
		public bool IsSubMeshView => _loadedSubMesh >= 0;

		/// <summary>Current sub-mesh view mode - see <see cref="SetSubMeshViewMode"/>.</summary>
		public SubMeshPreviewMode ViewMode => _viewMode;

		/// <summary>Whether the wireframe overlay is currently on - see <see cref="SetWireframeEnabled"/>.
		/// Orthogonal to <see cref="ViewMode"/>: can be toggled on top of either Highlight or Channel.</summary>
		public bool WireframeEnabled => _wireframeEnabled;

		/// <summary>Current Channel-mode debug channel - see <see cref="SetPreviewChannel"/>.</summary>
		public PreviewChannel Channel => _previewChannel;

		/// <summary>Whether the currently isolated sub-mesh has real UV data, i.e. whether
		/// <see cref="PreviewChannel.Tangent"/> (derived from UV derivatives) is meaningful for it.</summary>
		public bool CurrentSubMeshHasUv =>
			_loadedSubMesh >= 0 && _residentModel != null &&
			_loadedSubMesh < _residentModel.MeshHasUv.Count && _residentModel.MeshHasUv[_loadedSubMesh];

		private void OnGraphicsSettingsChanged()
		{
			// Only options baked outside the pipeline need environment recreation: environment HDRI
			// (IBL rebuild), anisotropy (material samplers), texture size cap (loader decoder).
			bool needsRecreate =
				_appliedHdrPath != (ResolveEnvironmentHdrPath(_editorSettings.PreviewEnvironmentHdr) ?? "") ||
				_appliedAniso != _editorSettings.PreviewAnisotropicFiltering ||
				_appliedMaxTextureSize != ClampedMaxTextureSize() ||
				// RT shadows are a model shader keyword (ModelLoadOptions.RtShadows): crossing
				// the "Ray-traced" boundary reloads the model, other modes stay live.
				_appliedRtShadows != RtShadowsEnabled();

			// Deferred to the start of the next Update: settings "OK" lands mid-ImGui-frame, when the
			// preview image with the old binding may already sit in a draw list.
			_pendingEnvironmentRecreate |= needsRecreate;

			if (!needsRecreate)
			{
				// On recreate CreateEnvironment re-reads the features anyway.
				ApplyPipelineFeatures();
			}

			ApplyGraphicsSettings();
		}

		// Applies features to the live environment; scene, batch renderer and model are untouched.
		private void ApplyPipelineFeatures()
		{
			// BEFORE SetFeatures: the RT fallback predicate looks at the live accel structure.
			EnsureSsrOwnRayScene();

			_appliedSsao = _editorSettings.PreviewSsao;
			_appliedAoMode = _editorSettings.PreviewAoMode;
			_appliedSsgi = _editorSettings.PreviewSsgi;
			_appliedSky = _editorSettings.PreviewSkyBackground;
			_appliedEyeAdaptation = _editorSettings.PreviewEyeAdaptation;
			_appliedFog = _editorSettings.PreviewFog;
			_appliedVolumetric = _editorSettings.PreviewVolumetric;
			_appliedBloom = _editorSettings.PreviewBloom;
			_appliedColorGrade = _editorSettings.PreviewColorGrade;
			_appliedMotionVectors = _editorSettings.PreviewMotionVectors;

			_env.SetFeatures(new PipelineFeatures
			{
				SkyBackground = _appliedSky,
				Ssao = _appliedSsao,
				AoMode = _appliedAoMode,
				Ssgi = _appliedSsgi,
				EyeAdaptation = _appliedEyeAdaptation,
				Fog = _appliedFog,
				Volumetric = _appliedVolumetric,
				Bloom = _appliedBloom,
				ColorGrade = _appliedColorGrade,
				// SSR pulls motion vectors in with it - see CreateEnvironment.
				MotionVectors = _appliedMotionVectors || _editorSettings.PreviewSsr,
				TemporalUpscale = _appliedMotionVectors && _editorSettings.TemporalUpscale,
				Ssr = _editorSettings.PreviewSsr,
				SsrRayTraced = SsrRayTracedEnabled(),
				SsrHitTextures = _editorSettings.SsrHitTextures,
			});

			// The RT trace variant must get its TLAS before the first frame
			// (SsrPassResources.SetRayScene); the probe field for RT hit lighting likewise.
			UpdateSsrRayScene();
			_env.SetSsrProbeField(_probeTextures);

			// Switching the RT fallback recreated SSR resources, so live knobs fell back to defaults.
			ApplySsrSettings();
		}


		/// <summary>Why ray-traced SSR is unavailable, for the Graphics window status line.</summary>
		public string? SsrRayTracedBlockReason
		{
			get
			{
				if (_graphicsApi.RayTracing < RayTracingSupport.Inline)
				{
					return "no inline tracing (D3D12 required)";
				}
				if (_probeAccel == null && _ssrOwnAccel == null)
				{
					return _residentModel == null
						? "no model open in the preview (the preview RT fallback waits for one; Scene View is independent)"
						: "the model's accel is not built yet (model is loading)";
				}
				if (_env.Pipeline.SsrResources is not { RayTraced: true })
				{
					return "SSR resources have not been rebuilt for the RT variant yet";
				}
				return null;
			}
		}

		// Order is mandatory: wait for the GPU -> unbind the ImGui target -> release the environment
		// -> create a new one -> drop the resident cache -> reload the model from disk.
		private void RecreateEnvironment()
		{
			var reloadPath = _loadedPath ?? _loadingPath;
			var reloadSubMesh = _loadedPath != null ? _loadedSubMesh : _loadingSubMesh;

			CancelPendingLoad();

			// Frames using the old environment's resources may still be in flight; releasing
			// without waiting for the GPU crashes the driver (same rule as in ResizeTargets).
			_env.DilApi.ImmediateContext.Flush();
			_env.DilApi.ImmediateContext.WaitForIdle();

			// Probe atlases belong to this viewport, not to the environment, so release them here
			// (behind the barrier above); the new environment rebakes them on model reload.
			ResetProbeGi();

			if (_textureBound && _lastImGuiRender != null)
			{
				_lastImGuiRender.ReleaseRenderTargetBinding(_textureRef.GetTexID());
				_textureBound = false;
			}

			_env.Release();

			// The resident cache and all geometry lived in the old batch renderer / EntityStore.
			_instanceEntities.Clear();
			_wireframeEntities.Clear();
			_wireframeMaterial = null;
			_wireframeMaterialId = null;
			_wireframeBatchCache.Clear();

			// The environment is released whole, so the BVH debug overlay references die with it.
			ReleaseBvhDebugResources();
			_rtShadowScene?.Release();
			_rtShadowScene = null;
			_batchCache.Clear();
			_meshIdMap.Clear();
			_materialIdMap.Clear();
			_residentModel = null;
			_residentPath = null;
			_streamingModel = null;
			_loadedPath = null;
			_loadedSubMesh = -1;
			_loadError = null;

			_env = CreateEnvironment();
			_env.Root.Add(new ModelStreamingSystem(_streamer));

			// Streamer models were built against the old samplers/shaders, so they are dropped.
			_streamer.MigrateEnvironment(_env, dropModels: true);
			ApplyLightRotation();

			if (reloadPath != null)
			{
				LoadModel(reloadPath, reloadSubMesh);
			}
		}

		/// <summary>Applies the live preview graphics settings from <see cref="EditorSettings"/>.</summary>
		public void ApplyGraphicsSettings()
		{
			var flags = PreviewFeatureFlags.None;
			if (_editorSettings.PreviewNormalMaps)
			{
				flags |= PreviewFeatureFlags.NormalMaps;
			}
			if (_editorSettings.PreviewBakedOcclusion)
			{
				flags |= PreviewFeatureFlags.Occlusion;
			}
			if (_editorSettings.PreviewShadows)
			{
				flags |= PreviewFeatureFlags.Shadows;
			}

			// Taken from the created environment, not the settings: auto-exposure is restart-level,
			// and until recreation the shader must keep writing display-space color.
			if (_env.HdrOutput)
			{
				flags |= PreviewFeatureFlags.HdrOutput;
			}

			SetFeatureFlags(flags);

			if (_env.ShadowSettings != null)
			{
				_env.ShadowSettings.Enabled = _editorSettings.PreviewShadows;
			}

			// Live probe-GI/sun knobs go straight into the cbuffer, no rebake.
			ApplyPreviewSettingsToMaterials();

			// AO knobs go live into the AO pass cbuffer.
			_env.SetAoStrength(Math.Clamp(_editorSettings.AoStrength, 0.1f, 4f),
				Math.Clamp(_editorSettings.AoFloor, 0f, 1f));
			_env.SetAoDebugView(_editorSettings.AoDebugView);

			// Motion vector debug view is a live cbuffer knob and, unlike the vectors checkbox,
			// does not rebuild the graph (see MotionVectorDebugPassResources).
			_env.SetMotionVectorDebug(_editorSettings.MotionVectorDebugView,
				Math.Clamp(_editorSettings.MotionVectorDebugRange, 0.25f, 256f));
			_env.SetTemporalJitter(_editorSettings.TemporalJitter);

			// Upscaler backend/quality is NOT applied here: it waits for the GPU and issues NGX
			// init commands, which cannot happen mid-ImGui-frame. See ApplyPendingUpscalerSettings.
			_pendingUpscalerApply = true;

			// Render scale is applied only in TrackAndApplyResize: a synchronous ResizeTargets
			// mid-ImGui-frame breaks the frame, same reason the environment recreate is deferred.

			// Auto-exposure knobs are live (the toggle itself is restart-level). Keep the measured
			// luminance bounds ordered: an inverted range clamps and freezes the exposure.
			var eaMin = Math.Clamp(_editorSettings.EyeAdaptationMinLuminance, 0.0001f, 100f);
			var eaMax = Math.Max(Math.Clamp(_editorSettings.EyeAdaptationMaxLuminance, 0.0001f, 100f), eaMin);
			_env.SetEyeAdaptationParams(
				Math.Clamp(_editorSettings.EyeAdaptationKey, 0.01f, 2f),
				eaMin,
				eaMax,
				Math.Clamp(_editorSettings.EyeAdaptationExposureCompensation, -8f, 8f));
			_env.SetEyeAdaptationSpeed(
				Math.Clamp(_editorSettings.EyeAdaptationSpeedUp, 0.05f, 20f),
				Math.Clamp(_editorSettings.EyeAdaptationSpeedDown, 0.05f, 20f));

			// The world knob works before framing; the bounds fraction only after it
			// (_framedRadius == 0 would mean a zero radius, i.e. AO with no effect).
			if (_framedRadius > 0f || _editorSettings.AoRadiusWorld > 0f)
			{
				_env.SetAoWorldRange(AoWorldRange());
			}

			// SSGI knobs are live; the radius, as with AO, only means something once framed.
			ApplyGiSettings(pushRange: _framedRadius > 0f || _editorSettings.SsgiRadiusWorld > 0f);
			ApplyFogSettings();
			ApplyVolumetricSettings();
			ApplyBloomSettings();
			ApplyColorGradeSettings();
			_env.SetToneCurve(_editorSettings.ToneCurve);

			// Grid/quality changes restart the session, debounced: sliders fire every drag tick.
			var wanted = (_editorSettings.PreviewProbeGi,
				_editorSettings.ProbeGiSkyIntensity,
				_editorSettings.ProbeGiRaysPerProbe,
				_editorSettings.ProbeGiBounces,
				_editorSettings.ProbeGiBounceSaturation,
				_editorSettings.ProbeGiGridDensity,
				_editorSettings.ProbeGiMaxProbes,
				// Hardware tracing belongs in this tuple: the trace path is chosen once, when the
				// session's GPU set comes up (see TryBeginProbeGpu), so it needs a session restart.
				_editorSettings.ProbeGiHardwareRayTracing,
				// Visibility octahedral map size is atlas layout, applied only on session recreate
				// (see ProbeGiBakeResult.VisRes).
				_editorSettings.ProbeGiVisRes);
			if (wanted.Item1 && wanted != _probeSessionOptions)
			{
				RequestProbeSession();
			}
		}


		private void ApplyBloomSettings() => ViewportSettingsPush.Bloom(_env, _editorSettings);


		private void ApplyColorGradeSettings() => ViewportSettingsPush.ColorGrade(_env, _editorSettings);

		private void ApplyFogSettings() => ViewportSettingsPush.Fog(_env, _editorSettings);

		private void ApplyVolumetricSettings() => ViewportSettingsPush.Volumetric(_env, _editorSettings);
		// Explicit "AO radius (world)" knob when set, otherwise a fraction of the model radius.
		private float AoWorldRange()
		{
			var world = _editorSettings.AoRadiusWorld;
			return world > 0f
				? Math.Clamp(world, 0.01f, 1000f)
				: _framedRadius * Math.Clamp(_editorSettings.AoRadiusFraction, 0.01f, 1f);
		}

		// SSGI gather radius; same rule as AoWorldRange.
		private float GiWorldRange()
		{
			var world = _editorSettings.SsgiRadiusWorld;
			return world > 0f
				? Math.Clamp(world, 0.01f, 1000f)
				: _framedRadius * Math.Clamp(_editorSettings.SsgiRadiusFraction, 0.01f, 2f);
		}

		// Per-frame SSR data: env map yaw and the RT fallback sun.
		private void PushSsrEnvironment()
		{
			var shadowSettings = _env.ShadowSettings;
			if (shadowSettings == null)
			{
				return;
			}

			// Same light constants as PrefabSceneViewport.PushSsrEnvironment.
			_env.SetSsrEnvironment(shadowSettings.EnvYawRadians,
				-Vector3.Normalize(shadowSettings.LightDirection),
				new Vector3(1f, 0.97f, 0.9f), 0.55f);
		}

		private void ApplySsrSettings()
		{
			_env.SetSsrParams(
				Math.Clamp(_editorSettings.SsrIntensity, 0f, 4f),
				Math.Clamp(_editorSettings.SsrMaxRoughness, 0.05f, 1f),
				Math.Clamp(_editorSettings.SsrThickness, 0.01f, 5f),
				Math.Clamp(_editorSettings.SsrMaxDistance, 1f, 500f),
				Math.Clamp(_editorSettings.SsrHistoryWeight, 0f, 0.97f),
				Math.Clamp(_editorSettings.SsrRaysPerPixel, 1, 4),
				_editorSettings.SsrDebugView,
				Math.Clamp(_editorSettings.SsrRtBounces, 1, 4),
				Math.Clamp(_editorSettings.SsrTraceMode, 0, 1));
			PushSsrEnvironment();
		}

		private void ApplyGiSettings(bool pushRange)
		{
			ApplySsrSettings();
			_env.SetGiParams(
				Math.Clamp(_editorSettings.SsgiIntensity, 0f, 4f),
				Math.Clamp(_editorSettings.SsgiSamples, 4, SsgiPassResources.MaxSampleCount),
				Math.Max(0f, _editorSettings.SsgiMaxLuminance),
				Math.Clamp(_editorSettings.SsgiSaturation, 0f, 1f));
			_env.SetGiCompositeParams(
				Math.Clamp(_editorSettings.SsgiBlurRadius, 0, SsgiPassResources.MaxBlurRadius),
				_editorSettings.SsgiDebugView);

			if (pushRange)
			{
				_env.SetGiWorldRange(GiWorldRange());
			}
		}

		// Relative paths resolve against "EditorAssets/"; empty means procedural sky.
		private static string ResolveEnvironmentHdrPath(string configured)
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

	}
}
