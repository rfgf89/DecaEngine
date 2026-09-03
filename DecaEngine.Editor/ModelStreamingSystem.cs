using System;
using System.Collections.Generic;
using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Core.Diagnostics;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using DecaEngine.Scene;

namespace DecaEngine.Editor.ECS
{
	/// <summary>Per-environment instantiation layer over the process-wide <see cref="ModelStore"/>:
	/// tracks which models this environment wants resident and its own batch-renderer registrations.
	/// Registrations are torn down only on <see cref="ModelStore.BeforeModelEvicted"/>, so a
	/// Release/Acquire round-trip at the streaming radius edge is nearly free.</summary>
	public sealed class ModelStreamer
	{
		/// <summary>This environment's registrations for one model file plus a reference to the
		/// shared <see cref="ModelLoader"/> owned by the store.</summary>
		public sealed class Resident
		{
			public readonly string Path;
			public ModelLoader? Model;
			public readonly Dictionary<int, MeshId> MeshIds = new();
			public readonly Dictionary<int, MaterialId> MaterialIds = new();
			public readonly Dictionary<(int, int), BatchId> BatchCache = new();

			/// <summary>This environment's material set. All constant pushes, rebinds and probe
			/// atlases must go here, never into model.materialObjects (owned by the first claimant).</summary>
			public OrderedDictionary<int, IMaterialObject>? Materials;

			/// <summary>Mesh index to skin-stream offset in the GPU skinning buffer; absent = static.</summary>
			public readonly Dictionary<int, int> SkinBases = new();

			public string? Error;

			/// <summary>World point the load priority (distance to camera) is measured from.</summary>
			public Vector3 Anchor;

			internal int RefCount;

			internal ModelStore.Handle? StoreHandle;

			internal bool TexturesReady;

			/// <summary>Registered in this environment's batch renderer; not yet safe to show.</summary>
			public bool Registered => Model != null;

			/// <summary>Safe to instantiate: registered AND textures upgraded to target quality.</summary>
			public bool Ready => Model != null && TexturesReady;

			public bool Failed => Error != null;

			internal Resident(string path)
			{
				Path = path;
			}
		}

		private readonly Dictionary<string, Resident> _models = new(StringComparer.OrdinalIgnoreCase);
		private readonly ModelStore _store;
		private readonly IGraphicsApi _graphicsApi;
		private readonly Func<ModelLoadOptions> _optionsFactory;
		private ModelViewportEnvironment _env;
		private Vector3 _cameraPos;

		private readonly List<Resident> _evictScratch = new();

		private readonly List<Resident> _readyScratch = new();

		/// <summary>Unload happens only past StreamRadius * this, to stop thrashing at the edge.</summary>
		public const float StreamOutHysteresis = 1.15f;

		/// <summary>Streaming radius from the camera in world units; infinity = load everything.</summary>
		public float StreamRadius { get; set; } = float.PositiveInfinity;

		/// <summary>A model of this environment is registered and ready to instantiate.</summary>
		public event Action<Resident>? ModelReady;

		/// <summary>Fired before one model's registrations are dropped, while its BatchIds are still
		/// valid: subscribers must remove instance entities referencing exactly this resident.</summary>
		public event Action<Resident>? ResidencyResetting;

		/// <summary>The reset finished for this model; it can be instantiated again.</summary>
		public event Action<Resident>? ResidencyReset;

		public ModelStreamer(ModelViewportEnvironment env, ModelStore store, IGraphicsApi graphicsApi,
			Func<ModelLoadOptions> optionsFactory)
		{
			_env = env;
			_store = store;
			_graphicsApi = graphicsApi;
			_optionsFactory = optionsFactory;

			// Live for the whole editor session with this streamer - no unsubscribe needed.
			_store.ModelReady += OnStoreModelReady;
			_store.ModelTexturesReady += OnStoreModelTexturesReady;
			_store.BeforeModelEvicted += OnStoreModelEvicted;
		}

		/// <summary>Read-only resident cache. Do not mutate.</summary>
		public IReadOnlyDictionary<string, Resident> Models => _models;

		/// <summary>Some model is requested but neither ready to show nor failed.</summary>
		public bool HasPendingLoads
		{
			get
			{
				foreach (var resident in _models.Values)
				{
					if (!resident.Ready && resident.Error == null)
					{
						return true;
					}
				}

				return false;
			}
		}

		/// <summary>Camera position of the last Tick; priorities and radius are measured from it.</summary>
		public Vector3 CameraPosition => _cameraPos;

		/// <summary>Whether a model anchored at this point should stay resident, with hysteresis.</summary>
		public bool ShouldBeResident(Vector3 anchor, bool currentlyResident)
		{
			if (float.IsPositiveInfinity(StreamRadius))
			{
				return true;
			}

			var distance = Vector3.Distance(anchor, _cameraPos);
			return distance <= (currentlyResident ? StreamRadius * StreamOutHysteresis : StreamRadius);
		}

		/// <summary>Takes a reference to a model file; every Acquire needs a matching Release.</summary>
		public Resident Acquire(string path, Vector3 anchor)
		{
			if (!_models.TryGetValue(path, out var resident))
			{
				resident = new Resident(path) { Anchor = anchor };
				_models[path] = resident;
			}

			resident.RefCount++;
			resident.Anchor = anchor;
			EnsureStoreHandle(resident);

			return resident;
		}

		/// <summary>Drops a reference. Registrations stay valid until the store actually evicts.</summary>
		public void Release(Resident resident)
		{
			resident.RefCount = Math.Max(0, resident.RefCount - 1);

			if (resident.RefCount == 0 && resident.StoreHandle != null)
			{
				_store.Release(resident.StoreHandle);
				resident.StoreHandle = null;
			}
		}

		/// <summary>Per-frame step (main thread, under the GPU lock): updates store priorities and
		/// forgets abandoned entries. Loading itself is stepped once per frame by ModelStore.Tick.</summary>
		public void Tick(float deltaTime, Vector3 cameraPos)
		{
			_cameraPos = cameraPos;

			_readyScratch.Clear();
			foreach (var resident in _models.Values)
			{
				if (resident.StoreHandle != null)
				{
					_store.SetPriority(resident.StoreHandle, DistanceToCamera(resident));
				}

				// The store fires events once per model, not per handle: a claim on an already
				// resident model gets none, so poll here too.
				if (SyncResident(resident))
				{
					_readyScratch.Add(resident);
				}
			}

			// Fired outside the _models walk: subscribers may Acquire, which mutates the dictionary.
			foreach (var resident in _readyScratch)
			{
				ModelReady?.Invoke(resident);
			}

			_readyScratch.Clear();
			EvictAbandoned();
		}

		private float DistanceToCamera(Resident resident) => Vector3.Distance(resident.Anchor, _cameraPos);

		// A path that once failed is never retried (Resident.Error).
		private void EnsureStoreHandle(Resident resident)
		{
			if (resident.StoreHandle != null || resident.Error != null)
			{
				return;
			}

			var handle = _store.Acquire(resident.Path, _optionsFactory(), DistanceToCamera(resident));
			if (handle.Failed)
			{
				resident.Error = handle.Error;
				_store.Release(handle);
				return;
			}

			resident.StoreHandle = handle;
		}

		private void OnStoreModelReady(ModelLoader model) => SyncStoreModel(model);

		private void OnStoreModelTexturesReady(ModelLoader model) => SyncStoreModel(model);

		// Paths are unique keys, so at most one resident matches.
		private void SyncStoreModel(ModelLoader model)
		{
			foreach (var resident in _models.Values)
			{
				if (resident.StoreHandle == null ||
					!_store.TryGetReady(resident.StoreHandle, out var readyModel) ||
					!ReferenceEquals(readyModel, model))
				{
					continue;
				}

				if (SyncResident(resident))
				{
					ModelReady?.Invoke(resident);
				}

				return;
			}
		}

		// Returns true exactly once: on the call where the resident becomes ready to show.
		private bool SyncResident(Resident resident)
		{
			if (resident.StoreHandle == null || resident.Error != null || resident.TexturesReady)
			{
				return false;
			}

			if (resident.Model == null)
			{
				if (!_store.TryGetReady(resident.StoreHandle, out var readyModel))
				{
					return false;
				}

				try
				{
					RegisterResident(resident, readyModel);
					resident.Model = readyModel;
				}
				catch (Exception ex)
				{
					resident.Error = ex.Message;
					EngineLog.Add(LogLevel.Error,
						$"Model streaming: failed to register '{resident.Path}': {ex.Message}");
					return false;
				}
			}

			if (!_store.AreTexturesReady(resident.StoreHandle))
			{
				return false;
			}

			resident.TexturesReady = true;
			return true;
		}

		// Partitioned eviction: only the matching resident's registrations are dropped.
		private void OnStoreModelEvicted(ModelLoader model)
		{
			Resident? found = null;
			foreach (var resident in _models.Values)
			{
				if (ReferenceEquals(resident.Model, model))
				{
					found = resident;
					break;
				}
			}

			if (found == null)
			{
				// Never registered here, or already unregistered by an earlier Release.
				return;
			}

			ResidencyResetting?.Invoke(found);

			_env.BatchRenderer.UnregisterModel(found.BatchCache.Values, found.MaterialIds.Values, found.MeshIds.Values);
			found.Model = null;
			found.TexturesReady = false;
			found.MeshIds.Clear();
			found.MaterialIds.Clear();
			found.BatchCache.Clear();
			found.Materials = null;
			found.StoreHandle = null;

			_env.Pipeline.InvalidateGraph();
			ResidencyReset?.Invoke(found);
		}

		private void RegisterResident(Resident resident, ModelLoader model)
		{
			var materials = _store.AcquireMaterialSet(resident.StoreHandle!);
			ModelViewportGeometry.RegisterModelResources(_env.BatchRenderer, model, resident.MeshIds, resident.MaterialIds,
				_env.SharedResources.EnvMapSampler, _env.SceneCopyTarget, _env.EnvironmentMap, materials,
				_env.SharedResources.SceneColorSampler, resident.SkinBases);
			resident.Materials = materials;
		}

		// Forget unreferenced entries that never became ready, else the dictionary grows forever.
		private void EvictAbandoned()
		{
			_evictScratch.Clear();
			foreach (var resident in _models.Values)
			{
				if (resident.RefCount <= 0 && resident.StoreHandle == null && resident.Model == null)
				{
					_evictScratch.Add(resident);
				}
			}

			foreach (var resident in _evictScratch)
			{
				_models.Remove(resident.Path);
			}
		}

		/// <summary>Clears this environment: resets the batch renderer and releases all store claims.
		/// The caller must remove its own instance entities BEFORE calling this.</summary>
		public void ClearAll()
		{
			var anyRegistered = false;
			foreach (var resident in _models.Values)
			{
				if (resident.Model != null)
				{
					anyRegistered = true;
					break;
				}
			}

			if (anyRegistered)
			{
				// Frames using the old registrations may be in flight; resetting without waiting
				// for the GPU crashes the driver.
				_env.DilApi.ImmediateContext.Flush();
				_env.DilApi.ImmediateContext.WaitForIdle();
				_env.BatchRenderer.ResetRegistrations();
			}

			foreach (var resident in _models.Values)
			{
				if (resident.StoreHandle != null)
				{
					_store.Release(resident.StoreHandle);
					resident.StoreHandle = null;
				}

				resident.Model = null;
				resident.TexturesReady = false;
				resident.MeshIds.Clear();
				resident.MaterialIds.Clear();
				resident.BatchCache.Clear();
				resident.Materials = null;
			}

			_models.Clear();

			if (anyRegistered)
			{
				_env.Pipeline.InvalidateGraph();
			}
		}

		/// <summary>Moves to a recreated environment. dropModels also discards the store claims (e.g.
		/// anisotropy is baked into samplers at load time, so the cached entry is unusable).</summary>
		public void MigrateEnvironment(ModelViewportEnvironment newEnv, bool dropModels)
		{
			_env = newEnv;

			if (dropModels)
			{
				foreach (var resident in _models.Values)
				{
					if (resident.StoreHandle != null)
					{
						_store.Release(resident.StoreHandle);
						resident.StoreHandle = null;
					}
				}

				_models.Clear();
				return;
			}

			foreach (var resident in _models.Values)
			{
				// Old-store entity records are gone; references restart at the first Acquire.
				resident.RefCount = 0;
				resident.MeshIds.Clear();
				resident.MaterialIds.Clear();
				resident.BatchCache.Clear();
				resident.Materials = null;

				if (resident.Model == null)
				{
					continue;
				}

				try
				{
					// A repeat request returns a fresh secondary material set, never steals the
					// primary one from another environment.
					RegisterResident(resident, resident.Model);
				}
				catch (Exception ex)
				{
					resident.Error = ex.Message;
					resident.Model = null;
					resident.TexturesReady = false;
					EngineLog.Add(LogLevel.Error,
						$"Model streaming: failed to re-register '{resident.Path}' after environment recreate: {ex.Message}");
				}
			}
		}
	}

	/// <summary>ECS driver for streaming. Must be added to the environment's SystemRoot LAST, after
	/// GpuInstanceBufferSystem/culling, so registration never lands mid-frame between the instance
	/// buffer write and the batch layout.</summary>
	public sealed class ModelStreamingSystem : QuerySystem<CameraComponent>
	{
		private readonly ModelStreamer _streamer;

		public ModelStreamingSystem(ModelStreamer streamer)
		{
			_streamer = streamer;
		}

		protected override void OnUpdate()
		{
			var cameraPos = _streamer.CameraPosition;
			var found = false;

			Query.ForEachEntity((ref CameraComponent _, Entity entity) =>
			{
				if (!found && entity.HasComponent<Position>())
				{
					cameraPos = entity.GetComponent<Position>().value;
					found = true;
				}
			});

			_streamer.Tick(Tick.deltaTime, cameraPos);
		}
	}
}
