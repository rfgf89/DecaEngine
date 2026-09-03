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
	/// <summary>Probe GI part of the preview viewport: bake session, GPU path, accel, snapshots.</summary>
	public partial class ModelPreviewViewport
	{
		/// <summary>Forces a probe-GI rebake from scratch.</summary>
		public void RequestProbeRebake()
		{
			RequestProbeSession(delaySeconds: 0f);
		}

		// Debounced: a new session throws away the accumulated field, and sliders fire every frame.
		private void RequestProbeSession(float delaySeconds = ProbeRebakeDebounceSeconds)
		{
			if (_residentModel != null && _editorSettings.PreviewProbeGi)
			{
				_probeSessionDelay = delaySeconds;
			}
		}

		// BVH builds on a background task: tens of seconds on Sponza-scale geometry.
		private void BeginProbeSession()
		{
			if (_residentModel == null || _env.ShadowSettings == null || !_editorSettings.PreviewProbeGi)
			{
				return;
			}

			if (_probeBaker == null)
			{
				if (_probeBakerTask != null)
				{
					return;
				}

				var model = _residentModel;
				var modelPath = _residentPath;
				_probeBakerModel = model;
				_probeBakerSw = System.Diagnostics.Stopwatch.StartNew();

				// Reads model CPU vertex copies: releasing the model must await this task first.
				_probeBakerTask = Task.Run(() =>
				{
					var baker = ProbeGiBaker.LoadOrBuild(model, modelPath, out var fromCache);
					_probeBakerFromCache = fromCache;
					return baker;
				});

				return;
			}

			if (!_probeBaker.HasGeometry)
			{
				return;
			}

			var options = BuildProbeOptions();

			_probeSessionOptions = (true, options.SkyIntensity, options.RaysPerProbe, options.Bounces,
				options.BounceSaturation, options.GridDensity, options.MaxProbes,
				_editorSettings.ProbeGiHardwareRayTracing, _editorSettings.ProbeGiVisRes);

			// Old GPU path and atlases must go before the new ones: stale SRBs crash the driver.
			ReleaseProbeGpuAndAtlases();

			// LightDirection points away from the sun; the baker wants the direction towards it.
			_probeSession = _probeBaker.BeginBake(_probeBoundsMin, _probeBoundsMax,
				Vector3.Normalize(-_env.ShadowSettings.LightDirection), ProbeSunColor(),
				_env.ShadowSettings.EnvYawRadians, _env.EnvironmentRadiance, options);

			// GPU drives all rounds; the CPU round survives only as a CLI reference check.
			if (!_probeGpuDisabled)
			{
				TryBeginProbeGpu(_probeSession);
			}
		}

		private ProbeGiBakeOptions BuildProbeOptions() =>
			ProbeGiViewportShared.BuildOptions(_editorSettings);

		private void TryBeginProbeGpu(ProbeGiBakeSession session)
		{
			try
			{
				// Bake only: realtime never reads the cache and capturing it stalls frames.
				if (!_editorSettings.ProbeGiRealtime)
				{
					_probeBaker!.EnsureSurfaceCache(session);
				}

				_probeTextures = new ProbeGiTextures(_graphicsApi, session.Result,
					$"_probeGi{_probeTextureGeneration++}", gpuWritable: true);
				_probeTextures.Bind(OwnMaterials!);
				ApplyPreviewSettingsToMaterials();

				bool hardware = _editorSettings.ProbeGiHardwareRayTracing && RayTracingSupported;
				if (!hardware && _probeAccel != null)
				{
					_env.Pipeline.SsrResources?.SetHitTextures(null, null);
					_probeAccel.Dispose();
					_probeAccel = null;
					_probeAccelHitTextures?.Dispose();
					_probeAccelHitTextures = null;
				}

				// Built once per model; object motion is handled by the TLAS rebuild in PollProbeAccel.
				if (_probeAccel == null && hardware)
				{
					_probeAccel = new ProbeSceneAccel(_env.DilApi, _probeBaker.InstancedGeometry);

					// Hit textures go with the accel: its instance table indexes into this exact set.
					_probeAccelHitTextures?.Dispose();
					_probeAccelHitTextures = _residentModel != null
						? SsrHitTextures.Build(_graphicsApi, _probeBaker.InstancedGeometry,
							new[] { _residentModel })
						: null;
				}

				_probeInstancePoses.Clear();

				// Pipelines outlive a session; rebuild on device change or tracing mode (a compile keyword).
				if (_probePipelines != null
					&& (!ReferenceEquals(_probePipelinesApi, _env.DilApi) || _probePipelines.Hardware != hardware))
				{
					_probePipelines.Dispose();
					_probePipelines = null;
				}

				if (_probePipelines == null)
				{
					_probePipelines = new ProbeRoundPipelines(_env.DilApi, hardware);
					_probePipelinesApi = _env.DilApi;
				}

				_probeGpu = new ProbeRoundGpu(_env.DilApi, _probePipelines, session, _probeBaker,
					_probeTextures, _env.EnvironmentMap, _env.ShadowSettings!.EnvYawRadians, _probeAccel);

				// The ray-traced SSR fallback feeds off this same accel.
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
					$"Probe GI: GPU path unavailable, falling back to CPU: {ex.Message}");
				_probeGpuDisabled = true;
				ReleaseProbeGpu();
			}
		}

		// Behind a Flush+WaitForIdle barrier: an in-flight frame may still read these resources.
		private void ReleaseProbeGpuAndAtlases()
		{
			if (_probeGpu == null && _probeTextures == null)
			{
				return;
			}

			// First: frozen graph commands draw the overlay from these atlases, and SSR holds them too.
			ReleaseProbeDebugOverlay();
			_env.SetSsrProbeField(null);

			_env.DilApi.ImmediateContext.Flush();
			_env.DilApi.ImmediateContext.WaitForIdle();

			ReleaseProbeGpu();
			_probeTextures?.Release();
			_probeTextures = null;
		}

		// Call before releasing the atlases: rounds hold views on them.
		private void ReleaseProbeGpu()
		{
			_probeGpu?.Dispose();
			_probeGpu = null;
		}

		// Rounds run strictly one at a time: the session is not thread-safe.
		private void PollProbeBake(float deltaTime)
		{
			if (_probeRoundTask != null)
			{
				if (!_probeRoundTask.IsCompleted)
				{
					return;
				}

				var finished = _probeRoundTask;
				_probeRoundTask = null;
				_probeRoundMs = _probeRoundSw.ElapsedMilliseconds;

				if (finished.IsCompletedSuccessfully)
				{
					UploadProbeSnapshot();
				}
				else
				{
					// A failed round can leave the session half-updated, so stop driving it.
					_probeSession = null;
					_probeStatus = "bake error";
					EngineLog.Add(LogLevel.Error, "Probe GI: bake round failed: " +
						(finished.Exception?.GetBaseException().Message ?? "Unknown error"));
				}
			}

			if (_probeBakerTask != null && _probeBakerTask.IsCompleted)
			{
				var task = _probeBakerTask;
				var builtFor = _probeBakerModel;
				_probeBakerTask = null;
				_probeBakerModel = null;

				if (!task.IsCompletedSuccessfully)
				{
					EngineLog.Add(LogLevel.Error,
						$"Probe GI: failed to build BVH: {task.Exception?.GetBaseException().Message}");
				}
				else if (ReferenceEquals(builtFor, _residentModel))
				{
					_probeBaker = task.Result;
					(_probeBoundsMin, _probeBoundsMax) = _residentModel!.ComputeBounds();

					var stats = _probeBaker.GetStats();
					EngineLog.Add(LogLevel.Info,
						$"Probe BVH {(_probeBakerFromCache ? "loaded from cache" : "built")} in " +
						$"{_probeBakerSw?.ElapsedMilliseconds ?? 0} ms: {stats.Triangles} tris, {stats.Nodes} nodes, " +
						$"{stats.Leaves} leaves, depth {stats.MaxDepth}, avg {stats.AvgLeafTriangles:F1} tris/leaf" +
						(_probeBakerFromCache ? "" : $" -> cached as '{System.IO.Path.GetFileName(ProbeGiBvhCache.GetCachePath(_residentPath ?? ""))}'"));

					_probeBakerSw = null;
					BeginProbeSession();
				}
				// Otherwise the model changed and the result describes dead geometry.
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
			if (session == null || baker == null || !_editorSettings.PreviewProbeGi)
			{
				return;
			}

			// Pushed into the live session: these are round weights, not layout, so no rebake needed.
			session.Realtime = _editorSettings.ProbeGiRealtime;
			session.RealtimeRaysPerRound = Math.Clamp(_editorSettings.ProbeGiRealtimeRays, 8, 1024);
			session.RealtimeBlend = Math.Clamp(_editorSettings.ProbeGiRealtimeBlend, 0.01f, 0.5f);
			session.RealtimeMaxStep = Math.Clamp(_editorSettings.ProbeGiRealtimeMaxStep, 0f, 0.2f);
			session.RealtimeGamma = Math.Clamp(_editorSettings.ProbeGiRealtimeGamma, 1f, 8f);
			session.RealtimeRelocation = Math.Clamp(_editorSettings.ProbeGiRealtimeRelocation, 0f, 0.45f);
			session.VariabilityThreshold = MathF.Max(_editorSettings.ProbeGiVariabilityThreshold, 0f);

			PollProbeAccel(session);

			// Lighting is refreshed per round: the session reconverges without discarding the field.
			if (_env.ShadowSettings != null)
			{
				session.SetLighting(Vector3.Normalize(-_env.ShadowSettings.LightDirection),
					ProbeSunColor(), _env.ShadowSettings.EnvYawRadians, _env.EnvironmentRadiance);
			}

			if (session.Converged)
			{
				return;
			}

			_probeRoundSw.Restart();

			if (_probeGpu != null)
			{
				// The fence is per round, not per chunk: per-chunk waits cost a whole frame.
				if (!_probeGpu.IsReady)
				{
					return;
				}

				try
				{
					// Chunks and rounds share one loop so the ray budget is spent across round borders.
					ProbeGiViewportShared.DriveChunks(_probeGpu, session, baker,
						Math.Max(ProbeChunksPerFrame,
							_probeGpu.ChunksPerFrame(session.RaysPerRound)));
					_probeRoundMs = _probeRoundSw.ElapsedMilliseconds;
				}
				catch (Exception ex)
				{
					EngineLog.Add(LogLevel.Error,
						$"Probe GI: GPU round failed, probes disabled: {ex.Message}");
					_probeGpuDisabled = true;
					ReleaseProbeGpu();
				}
			}
		}

		// Rebuilds the TLAS when any instance moved; BLASes stay put, they are in object space.
		private void PollProbeAccel(ProbeGiBakeSession session)
		{
			var accel = _probeAccel;
			var baker = _probeBaker;
			if (accel == null || baker == null || _residentModel == null || _probeAccelFrozen)
			{
				return;
			}

			// Round boundary only: rebuilding mid-round seams half the old scene into the field.
			if (_probeGpu is { AtRoundStart: false })
			{
				return;
			}

			var instances = baker.InstancedGeometry.Instances;
			bool moved = _probeInstancePoses.Count != instances.Length;
			if (moved)
			{
				_probeInstancePoses.Clear();
				for (int i = 0; i < instances.Length; i++)
				{
					_probeInstancePoses.Add(instances[i].Transform);
				}
			}

			var modelInstances = _residentModel.instances;
			for (int i = 0; i < instances.Length; i++)
			{
				int source = instances[i].SourceInstance;
				var pose = source >= 0 && source < modelInstances.Count
					? ProbeGiBaker.InstanceMatrix(modelInstances[source].transform)
					: instances[i].Transform;

				if (_probeInstancePoses[i] != pose)
				{
					_probeInstancePoses[i] = pose;
					moved = true;
				}
			}

			if (!moved)
			{
				return;
			}

			try
			{
				accel.Rebuild(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_probeInstancePoses));

				// Motion must not reopen relocation (Majercik 2021 §5: probes move only at init).
				session.InvalidateGeometry();
			}
			catch (Exception ex)
			{
				// Do not dispose the accel: recorded round bindings would be left dangling.
				_probeAccelFrozen = true;
				EngineLog.Add(LogLevel.Error,
					$"Probe GI: TLAS rebuild failed, scene frozen for tracing: {ex.Message}");
			}
		}

		// Adding/removing the overlay rebuilds the render graph, so the want/have check stays cheap.
		private void PollProbeDebugOverlay() =>
			ProbeGiViewportShared.PollOverlays(_probeDebugOverlays,
				_editorSettings.PreviewProbeGi && _editorSettings.ProbeGiShowProbes,
				ref _probeDebugFailed, _env, _graphicsApi, _probeSession, _probeTextures);

		private void ReleaseProbeDebugOverlay() =>
			ProbeGiViewportShared.ReleaseOverlays(_probeDebugOverlays, _env);

		// Atlases are created once per grid and updated in place, never recreated per round.
		private void UploadProbeSnapshot()
		{
			var session = _probeSession;
			if (session == null || _probeBaker == null || _residentModel == null)
			{
				return;
			}

			try
			{
				var snapshot = _probeBaker.Snapshot(session);

				// Grid changed: old atlases may still be read by an in-flight frame, so barrier first.
				if (_probeTextures != null && !_probeTextures.Matches(snapshot))
				{
					// The overlay holds these atlases in frozen graph commands; drop it before Release.
					ReleaseProbeDebugOverlay();
					_env.DilApi.ImmediateContext.Flush();
					_env.DilApi.ImmediateContext.WaitForIdle();
					_probeTextures.Release();
					_probeTextures = null;
				}

				if (_probeTextures == null)
				{
					_probeTextures = new ProbeGiTextures(_graphicsApi, snapshot,
						$"_probeGi{_probeTextureGeneration++}");
					_probeTextures.Bind(OwnMaterials!);
					ApplyPreviewSettingsToMaterials();
				}
				else
				{
					_probeTextures.Update(snapshot);
				}
			}
			catch (Exception ex)
			{
				_probeTextures = null;
				EngineLog.Add(LogLevel.Error, $"Probe GI: failed to upload atlases: {ex.Message}");
			}
		}

		// Mandatory before releasing a model: the task reads unmanaged CPU vertex copies.
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
				// PollProbeBake reports the failure; here only the thread finishing matters.
			}

			_probeBakerTask = null;
			_probeBakerModel = null;
		}

		private void ResetProbeGi()
		{
			// Before anything else: the BVH task may still read geometry the caller is about to free.
			WaitProbeBakerTask();

			// The overlay holds the atlases in a material; dropping it runs the Flush+WaitForIdle barrier.
			ReleaseProbeDebugOverlay();

			// Before the atlases: the GPU object holds views on them.
			ReleaseProbeGpu();

			bool hadProbeAccel = _probeAccel != null;
			if (hadProbeAccel)
			{
				_env.Pipeline.SsrResources?.SetHitTextures(null, null);
			}
			_probeAccel?.Dispose();
			_probeAccel = null;
			_probeAccelHitTextures?.Dispose();
			_probeAccelHitTextures = null;

			// The RT SSR trace held a descriptor on the destroyed TLAS: fall back to screen space.
			if (hadProbeAccel && _editorSettings.SsrRayTraced)
			{
				ApplyPipelineFeatures();
			}
			_probeBaker = null;
			_probeSession = null;
			_probeRoundTask = null;
			_probeSessionDelay = -1f;
			_probeSessionOptions = default;
			_probeStatus = "no probes";

			// Per-scene reset: a transient failure on one model must not disable GPU probes forever.
			_probeGpuDisabled = false;

			if (_probeTextures != null)
			{
				_probeTextures.Release();
				_probeTextures = null;
			}
		}

	}
}
