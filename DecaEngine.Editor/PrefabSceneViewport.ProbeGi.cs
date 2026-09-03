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
	/// <summary>Scene probe GI: bake session, GPU path, instance poses, snapshots and textures.</summary>
	public partial class PrefabSceneViewport
	{
		// Models read by _probeBakerTask; releasing them must wait for the task
		// (it reads CPU vertex copies in unmanaged memory).
		private List<(ModelLoader Model, Matrix4x4 World)>? _probeBakerModels;

		// Scene snapshot the LIVE _probeBaker was built for; BeginProbeSession uses it
		// to decide whether the BVH is stale (the BVH is world-space, welded to these poses).
		private List<(ModelLoader Model, Matrix4x4 World)>? _probeBakerBuiltFor;

		// Scene records in the instance order of the baker being built. Moved into
		// _probeSceneRecords ONLY together with the finished baker: the pair is matched by
		// index, and a mismatch feeds foreign poses into the TLAS.
		private List<RenderedModel>? _probeBakerRecords;

		/// <summary>True if the scene has the same model set in the same order.</summary>
		private static bool SameSceneComposition(List<(ModelLoader Model, Matrix4x4 World)>? a,
			List<(ModelLoader Model, Matrix4x4 World)> b)
		{
			if (a == null || a.Count != b.Count)
			{
				return false;
			}

			for (int i = 0; i < a.Count; i++)
			{
				if (!ReferenceEquals(a[i].Model, b[i].Model))
				{
					return false;
				}
			}

			return true;
		}

		/// <summary>True if record poses match; exact matrix equality on purpose - any difference
		/// means the BVH triangles no longer sit where the geometry is.</summary>
		private static bool SameScenePoses(List<(ModelLoader Model, Matrix4x4 World)>? a,
			List<(ModelLoader Model, Matrix4x4 World)> b)
		{
			if (!SameSceneComposition(a, b))
			{
				return false;
			}

			for (int i = 0; i < a!.Count; i++)
			{
				if (a[i].World != b[i].World)
				{
					return false;
				}
			}

			return true;
		}

		private ProbeGiBakeSession? _probeSession;
		private Task? _probeRoundTask;
		private ProbeGiTextures? _probeTextures;
		private int _probeTextureGeneration;
		private float _probeSessionDelay = -1f;

		// Dynamic GPU path: moving an entity does NOT recreate the session - poses go into
		// the TLAS and the field re-converges on its own.
		private ProbeRoundPipelines? _scenePipelines;
		private ProbeSceneAccel? _sceneAccel;
		private ProbeRoundGpu? _sceneGpu;
		private bool _sceneGpuDisabled;

		// SSR's own accel (RT reflection fallback WITHOUT probe GI). _sceneAccel is always
		// preferred (its poses are live); this one rebuilds on scene changes with a debounce
		// since a full-scene BLAS rebuild is expensive.
		private ProbeSceneAccel? _ssrOwnAccel;
		private List<(ModelLoader Model, Matrix4x4 World)>? _ssrOwnBuiltFor;
		private float _ssrOwnRebuildDelay = -1f;

		// RT-hit texture sets, one per accel: they live and die with it, and binding picks
		// the set of whichever accel actually went into SetRayScene.
		private SsrHitTextures? _sceneAccelHitTextures;
		private SsrHitTextures? _ssrOwnHitTextures;

		// Scene records in the baker's model-list order: ProbeGeometryInstance.SourceModel
		// indexes here to fetch the record's live world matrix (LastWorld is updated by the gizmo).
		private readonly List<RenderedModel> _probeSceneRecords = new();
		private readonly List<Matrix4x4> _probeScenePoses = new();
		private bool _sceneTlasDirty;

		// Scene probe debug view - same lifecycle as the preview (see ProbeGiViewportShared.PollOverlays).
		private readonly List<(ProbeDebugOverlay Overlay, ProbeGiTextures Textures)> _sceneDebugOverlays = new();
		private bool _sceneDebugFailed;

		// Selection (see SyncSelectionHighlight / SelectionOutlineOverlay).
		private SelectionOutlineOverlay? _selectionOverlay;
		private int _highlightedId = -1;
		private bool _structuralDirtySelection;
		private readonly List<Vector3> _selectionPositions = new();
		private readonly List<uint> _selectionIndices = new();

		/// <summary>Viewport click result: Clicked = scene click (not gizmo), Entity = entity under cursor or null.</summary>
		public struct PickResult
		{
			public bool Clicked;
			public Entity? Entity;
		}

		private ShadingMode _shading = ShadingMode.Lighting;
		private PreviewFeatureFlags _featureFlags = PreviewFeatureFlags.All;

		private float _lightYawOffsetDegrees;
		private float _lightElevationOffsetDegrees;

		// Fly/orbit/pan/focus - see SceneCamera. ModelPreviewViewport deliberately keeps
		// the old orbital camera, which is correct there.
		private readonly SceneCamera _camera;
		private bool _framePending = true;

		private ImTextureRef _textureRef;
		private bool _textureBound;
		private ImGuiRender? _lastImGuiRender;
		private Vector2 _pendingSize;
		private float _resizeIdleSeconds;

		// Render scale seen by the last TrackAndApplyResize; a change resets the debounce
		// timer, like a window resize.
		private float _pendingRenderScale = 1f;

		// Same deferred apply as ModelPreviewViewport._pendingUpscalerApply.
		private bool _pendingUpscalerApply;

		/// <summary>Requests a bake session rebuild with a debounce: gizmo drags fire every frame
		/// and a new session discards the accumulated field.</summary>
		private void RequestProbeSession(float delaySeconds)
		{
			if (ProbesEnabled && HasContent)
			{
				_probeSessionDelay = delaySeconds;
			}
		}

		/// <summary>Starts a bake session over the WHOLE scene; the baker is rebuilt per session
		/// because scene geometry changes and the BVH must match it.</summary>
		private void BeginProbeSession()
		{
			if (!ProbesEnabled || _env.ShadowSettings == null || !TryComputeSceneBounds(out var min, out var max))
			{
				return;
			}

			// Records are gathered LOCALLY and only published together with the baker they were
			// built for. Otherwise PollSceneProbePoses, which matches them BY INDEX, would feed
			// foreign matrices into the TLAS and probes would catch light/shadow from nowhere.
			var sceneModels = new List<(ModelLoader Model, Matrix4x4 World)>();
			var sceneRecords = new List<RenderedModel>();
			foreach (var record in _rendered.Values)
			{
				if (record.Instantiated && !string.IsNullOrEmpty(record.ResolvedPath) &&
					_models.TryGetValue(record.ResolvedPath, out var state) && state.Model != null)
				{
					sceneModels.Add((state.Model, record.LastWorld));
					// Index here = baker instance SourceModel; pose tracking uses it to fetch
					// the record's live LastWorld (see PollSceneProbePoses).
					sceneRecords.Add(record);
				}
			}

			if (sceneModels.Count == 0)
			{
				return;
			}

			// BVH build runs in the BACKGROUND: Sponza-sized scenes take tens of seconds of CPU
			// and would hang the editor on the render thread.
			//
			// Staleness: composition changes always invalidate the tree. Pose changes only do on
			// SOFTWARE tracing, where rays walk the world-space BVH; with hardware tracing poses
			// live in the TLAS and update without a rebake.
			bool tlasTracksPoses = _sceneGpu != null && _sceneAccel != null && !_sceneGpuDisabled;
			bool treeStale = _probeBaker == null
				|| !SameSceneComposition(_probeBakerBuiltFor, sceneModels)
				|| (!tlasTracksPoses && !SameScenePoses(_probeBakerBuiltFor, sceneModels));

			if (treeStale)
			{
				if (_probeBakerTask != null)
				{
					return;
				}

				var models = sceneModels;
				_probeBakerModels = models;

				// Records are published together with the finished baker (see PollProbeBake);
				// until then the live baker and _probeSceneRecords must stay a consistent pair.
				_probeBakerRecords = sceneRecords;
				_probeBakerTask = Task.Run(() => new ProbeGiBaker(models));
				return;
			}

			if (!_probeBaker.HasGeometry)
			{
				return;
			}

			// Tree is current: same composition means record order matches the live baker's
			// instance order, so the list can be accepted.
			_probeSceneRecords.Clear();
			_probeSceneRecords.AddRange(sceneRecords);

			_probeSceneBoundsMin = min;
			_probeSceneBoundsMax = max;

			// LightDirection points FROM the sun; the baker expects the direction TO the sun.
			_probeSession = _probeBaker.BeginBake(min, max,
				Vector3.Normalize(-_env.ShadowSettings.LightDirection), ProbeSunColor(),
				_env.ShadowSettings.EnvYawRadians, _env.EnvironmentRadiance, BuildSceneProbeOptions());

			TryBeginSceneProbeGpu();
		}

		private ProbeGiBakeOptions BuildSceneProbeOptions() =>
			ProbeGiViewportShared.BuildOptions(_editorSettings);

		private Vector3 _probeSceneBoundsMin, _probeSceneBoundsMax;

		/// <summary>Brings up the scene GPU path (realtime only); on any failure silently stays
		/// on the CPU path, which works but is static.</summary>
		private void TryBeginSceneProbeGpu()
		{
			var session = _probeSession;
			var baker = _probeBaker;
			if (session == null || baker == null || _sceneGpuDisabled)
			{
				return;
			}

			// The old GPU set must go BEFORE the new one (it is tied to the old session and would
			// leak a full buffer set per rebuild). Overlay first: frozen graph commands hold the atlases.
			ReleaseSceneProbeDebugOverlay();
			ReleaseSceneProbeGpu();
			if (_probeTextures != null)
			{
				// BEFORE release: the SSR trace SRB holds the SH atlases (RT-hit lighting).
				_env.SetSsrProbeField(null);
				_env.DilApi.ImmediateContext.Flush();
				_env.DilApi.ImmediateContext.WaitForIdle();
				_probeTextures.Release();
				_probeTextures = null;
			}

			try
			{
				// Surface cache is bake-only: realtime never reads it, and capturing it costs
				// hundreds of ms ON THE RENDER THREAD (frame stalls on large scenes).
				if (!_editorSettings.ProbeGiRealtime)
				{
					baker.EnsureSurfaceCache(session);
				}

				_probeTextures = new ProbeGiTextures(_graphicsApi, session.Result,
					$"_sceneProbeGi{_probeTextureGeneration++}", gpuWritable: true);
				BindProbeTextures();
				ApplyMaterialSettings();

				bool hardware = _editorSettings.ProbeGiHardwareRayTracing
					&& _graphicsApi.RayTracing >= RayTracingSupport.Inline;
				_sceneAccel = hardware ? new ProbeSceneAccel(_env.DilApi, baker.InstancedGeometry) : null;

				// Hit-texture set goes with the accel: its instance table indexes this set.
				// Models come from _probeBakerBuiltFor (the snapshot the LIVE baker was built for),
				// NOT _probeBakerModels, which PollProbeBake nulls when the task finishes.
				_sceneAccelHitTextures?.Dispose();
				_sceneAccelHitTextures = null;
				if (_sceneAccel != null && _probeBakerBuiltFor != null)
				{
					var hitModels = new List<ModelLoader>(_probeBakerBuiltFor.Count);
					foreach (var (m, _) in _probeBakerBuiltFor)
					{
						hitModels.Add(m);
					}
					_sceneAccelHitTextures = SsrHitTextures.Build(_graphicsApi,
						baker.InstancedGeometry, hitModels);
				}

				if (_scenePipelines != null && _scenePipelines.Hardware != hardware)
				{
					_scenePipelines.Dispose();
					_scenePipelines = null;
				}

				_scenePipelines ??= new ProbeRoundPipelines(_env.DilApi, hardware);
				_sceneGpu = new ProbeRoundGpu(_env.DilApi, _scenePipelines, session, baker,
					_probeTextures, _env.EnvironmentMap, _env.ShadowSettings!.EnvYawRadians, _sceneAccel);

				// SSR's RT fallback feeds off this same accel, which may have just appeared or
				// been recreated (stale descriptor) - refresh features and binding here.
				if (_editorSettings.SsrRayTraced)
				{
					ApplyPipelineFeatures();
				}
				UpdateSsrRayScene();
				_env.SetSsrProbeField(_probeTextures);
			}
			catch (Exception ex)
			{
				EngineLog.Add(LogLevel.Warning,
					$"Scene probe GI: GPU path unavailable, CPU fallback: {ex.Message}");
				_sceneGpuDisabled = true;
				ReleaseSceneProbeGpu();
			}
		}

		/// <summary>Tracks the scene probe debug view against the toggle and atlas lifetime.</summary>
		private void PollSceneProbeDebugOverlay() =>
			ProbeGiViewportShared.PollOverlays(_sceneDebugOverlays,
				ProbesEnabled && _editorSettings.ProbeGiShowProbes && _sceneGpu != null,
				ref _sceneDebugFailed, _env, _graphicsApi, _probeSession, _probeTextures);

		private void ReleaseSceneProbeDebugOverlay() =>
			ProbeGiViewportShared.ReleaseOverlays(_sceneDebugOverlays, _env);

		/// <summary>Releases the scene GPU path behind a barrier; pipelines survive (compilation
		/// is expensive and they are session-independent).</summary>
		private void ReleaseSceneProbeGpu()
		{
			if (_sceneGpu == null && _sceneAccel == null)
			{
				return;
			}

			// The trace must not keep a view of the dying hit-texture atlas.
			if (_sceneAccelHitTextures != null)
			{
				_env.Pipeline.SsrResources?.SetHitTextures(null, null);
			}

			_env.DilApi.ImmediateContext.Flush();
			_env.DilApi.ImmediateContext.WaitForIdle();
			_sceneGpu?.Dispose();
			_sceneGpu = null;

			bool hadAccel = _sceneAccel != null;
			_sceneAccel?.Dispose();
			_sceneAccel = null;
			_sceneAccelHitTextures?.Dispose();
			_sceneAccelHitTextures = null;

			// The RT SSR trace variant held a descriptor to the just-destroyed TLAS - fall back
			// to the screen variant (resources will rebuild).
			if (hadAccel && _editorSettings.SsrRayTraced)
			{
				ApplyPipelineFeatures();
			}
		}

		/// <summary>Tracks the TLAS against entity poses: gizmo moves a record, the TLAS rebuilds
		/// from live LastWorld matrices, the field re-converges; the session is NOT recreated.</summary>
		private void PollSceneProbePoses()
		{
			var session = _probeSession;
			var baker = _probeBaker;
			if (!_sceneTlasDirty || session == null || baker == null
				|| _sceneGpu == null || _sceneAccel == null)
			{
				return;
			}

			// Only at a round boundary - otherwise half the probes trace the old scene and half
			// the new one (see ProbeRoundGpu.AtRoundStart).
			if (!_sceneGpu.AtRoundStart)
			{
				return;
			}

			_sceneTlasDirty = false;

			var instances = baker.InstancedGeometry.Instances;
			_probeScenePoses.Clear();
			for (int i = 0; i < instances.Length; i++)
			{
				int model = instances[i].SourceModel;
				_probeScenePoses.Add(model >= 0 && model < _probeSceneRecords.Count
					? instances[i].LocalTransform * _probeSceneRecords[model].LastWorld
					: instances[i].Transform);
			}

			try
			{
				_sceneAccel.Rebuild(
					System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_probeScenePoses));

				// Deliberately NO relocation reopen or round-weight reset here: a global reset per
				// drag frame destabilized the whole grid (Majercik 2021 s5 forbids moving probes
				// around dynamics; backface heuristics handle covered probes, and the realtime
				// exponential average tracks lighting on its own). Bakes DO need the reset - they
				// stop on Converged - and that is all InvalidateGeometry does.
				session.InvalidateGeometry();
			}
			catch (Exception ex)
			{
				EngineLog.Add(LogLevel.Error,
					$"Scene probe GI: TLAS rebuild failed, scene frozen for tracing: {ex.Message}");
				_sceneGpuDisabled = true;
			}
		}

		/// <summary>Per-frame bake driver; rounds run strictly one at a time - the session is not
		/// thread-safe, and everything touching it (lighting) happens while no task is running.</summary>
		private void PollProbeBake(float deltaTime)
		{
			if (!ProbesEnabled)
			{
				return;
			}

			if (_probeRoundTask != null)
			{
				if (!_probeRoundTask.IsCompleted)
				{
					return;
				}

				var finished = _probeRoundTask;
				_probeRoundTask = null;

				if (finished.IsCompletedSuccessfully)
				{
					UploadProbeSnapshot();
				}
				else
				{
					_probeSession = null;
					EngineLog.Add(LogLevel.Error, "Scene probe GI: bake round failed: " +
						(finished.Exception?.GetBaseException().Message ?? "Unknown error"));
				}
			}

			// Background BVH build finished - accept the result and start the session.
			if (_probeBakerTask != null && _probeBakerTask.IsCompleted)
			{
				var task = _probeBakerTask;
				var builtFor = _probeBakerModels;
				var builtRecords = _probeBakerRecords;
				_probeBakerTask = null;
				_probeBakerModels = null;
				_probeBakerRecords = null;

				if (!task.IsCompletedSuccessfully)
				{
					EngineLog.Add(LogLevel.Error,
						$"Scene probe GI: failed to build BVH: {task.Exception?.GetBaseException().Message}");
				}
				else
				{
					_probeBaker = task.Result;

					// Pose snapshot the tree was built for; BeginProbeSession compares against it.
					_probeBakerBuiltFor = builtFor;

					// Records STRICTLY together with the baker: their order is the SourceModel
					// index space PollSceneProbePoses uses to feed live poses into the TLAS.
					_probeSceneRecords.Clear();
					if (builtRecords != null)
					{
						_probeSceneRecords.AddRange(builtRecords);
					}

					BeginProbeSession();
				}
			}

			if (_probeSessionDelay >= 0f)
			{
				_probeSessionDelay -= deltaTime;
				if (_probeSessionDelay < 0f)
				{
					BeginProbeSession();
				}
			}

			var session = _probeSession;
			var baker = _probeBaker;
			if (session == null || baker == null)
			{
				return;
			}

			// Live realtime knobs and lighting are refreshed before every round.
			session.Realtime = _editorSettings.ProbeGiRealtime && _sceneGpu != null;
			session.RealtimeRaysPerRound = Math.Clamp(_editorSettings.ProbeGiRealtimeRays, 8, 1024);
			session.RealtimeBlend = Math.Clamp(_editorSettings.ProbeGiRealtimeBlend, 0.01f, 0.5f);
			session.RealtimeMaxStep = Math.Clamp(_editorSettings.ProbeGiRealtimeMaxStep, 0f, 0.2f);
			session.RealtimeGamma = Math.Clamp(_editorSettings.ProbeGiRealtimeGamma, 1f, 8f);
			session.VariabilityThreshold = MathF.Max(_editorSettings.ProbeGiVariabilityThreshold, 0f);
			session.RealtimeRelocation = Math.Clamp(_editorSettings.ProbeGiRealtimeRelocation, 0f, 0.45f);

			PollSceneProbePoses();

			// Lighting is pulled before every round: a sun change resets convergence and the
			// field flows to the new solution without discarding what it accumulated.
			if (_env.ShadowSettings != null)
			{
				session.SetLighting(Vector3.Normalize(-_env.ShadowSettings.LightDirection),
					ProbeSunColor(), _env.ShadowSettings.EnvYawRadians, _env.EnvironmentRadiance);
			}

			// Scene punctual lights feed the bake the same way; mirrors already carry WORLD
			// Position/Rotation (see SyncEntity).
			_probeBakeLightsScratch.Clear();
			foreach (var mirror in _lightMirrors.Values)
			{
				if (mirror.IsNull)
				{
					continue;
				}

				ref var mirrorLight = ref mirror.GetComponent<LightComponent>();
				if (LightCulling.TryBuildBakeLight(ref mirrorLight, mirror, out var bakeLight))
				{
					_probeBakeLightsScratch.Add(bakeLight);
				}
			}

			session.SetPunctualLights(
				System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_probeBakeLightsScratch));

			if (session.Converged)
			{
				return;
			}

			// GPU rounds: commands recorded on the render thread, atlases written by shaders.
			if (_sceneGpu != null)
			{
				if (!_sceneGpu.IsReady)
				{
					return;
				}

				try
				{
					// Shared chunk loop (see ProbeGiViewportShared.DriveChunks): the frame's ray
					// budget is spent fully, crossing round boundaries.
					ProbeGiViewportShared.DriveChunks(_sceneGpu, session, baker,
						_sceneGpu.ChunksPerFrame(session.RaysPerRound));
				}
				catch (Exception ex)
				{
					EngineLog.Add(LogLevel.Error,
						$"Scene probe GI: GPU round failed, probes disabled: {ex.Message}");
					_sceneGpuDisabled = true;
					ReleaseSceneProbeGpu();
				}
			}

			// There is no CPU driver for the scene: if the GPU path did not come up, scene
			// probes stand still and the console explains why.
		}

		/// <summary>Uploads the session field into GPU atlases; created once per grid and updated
		/// IN PLACE, a grid change recreates them behind a GPU barrier.</summary>
		private void UploadProbeSnapshot()
		{
			var session = _probeSession;
			if (session == null || _probeBaker == null)
			{
				return;
			}

			try
			{
				var snapshot = _probeBaker.Snapshot(session);

				if (_probeTextures != null && !_probeTextures.Matches(snapshot))
				{
					_env.DilApi.ImmediateContext.Flush();
					_env.DilApi.ImmediateContext.WaitForIdle();
					_probeTextures.Release();
					_probeTextures = null;
				}

				if (_probeTextures == null)
				{
					_probeTextures = new ProbeGiTextures(_graphicsApi, snapshot,
						$"_sceneProbeGi{_probeTextureGeneration++}");
					BindProbeTextures();
					ApplyMaterialSettings();
				}
				else
				{
					_probeTextures.Update(snapshot);
				}
			}
			catch (Exception ex)
			{
				_probeTextures = null;
				EngineLog.Add(LogLevel.Error, $"Scene probe GI: failed to upload atlases: {ex.Message}");
			}
		}

		/// <summary>Binds probe atlases to the materials of ALL loaded models.</summary>
		private void BindProbeTextures()
		{
			if (_probeTextures == null)
			{
				return;
			}

			foreach (var state in _models.Values)
			{
				if (state.Model != null)
				{
					// Into this environment's OWN material set: binding into a shared model's
					// primary set would leak into another environment.
					_probeTextures.Bind(state.Materials ?? state.Model.materialObjects);
				}
			}
		}

		/// <summary>Waits for the background BVH build. REQUIRED before releasing any scene model:
		/// the task reads CPU vertex copies in unmanaged memory.</summary>
		private void WaitProbeBakerTask()
		{
			if (_probeBakerTask == null)
			{
				return;
			}

			try
			{
				_probeBakerTask.Wait();
			}
			catch (Exception)
			{
				// The cause is reported from PollProbeBake; only completion matters here.
			}

			_probeBakerTask = null;
			_probeBakerModels = null;
			_probeBakerRecords = null;
		}

		private void ResetProbeGi()
		{
			// The tree goes with the baker, so its pose snapshot is invalid too (otherwise the
			// next build would consider the scene unchanged and skip).
			_probeBakerBuiltFor = null;
			ResetProbeGiCore();
		}

		private void ResetProbeGiCore()
		{
			// First: the background BVH build may still be reading geometry of models the
			// caller is about to release.
			WaitProbeBakerTask();

			// Overlay first (holds atlases in frozen commands), then the GPU object.
			ReleaseSceneProbeDebugOverlay();
			ReleaseSceneProbeGpu();
			_sceneTlasDirty = false;
			_probeSceneRecords.Clear();

			_probeBaker = null;
			_probeSession = null;
			_probeRoundTask = null;
			_probeSessionDelay = -1f;

			// The GPU-failure flag is PER-SCENE: transient errors (stale descriptor/TLAS during a
			// live scene switch) also set it, and without this reset one hiccup would kill probes
			// until editor restart - the scene has no CPU driver.
			_sceneGpuDisabled = false;

			if (_probeTextures != null)
			{
				_probeTextures.Release();
				_probeTextures = null;
			}
		}

	}
}
