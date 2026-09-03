using System.Linq;
using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Core.Diagnostics;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;

namespace DecaEngine.Editor
{
	/// <summary>Background baker of 128x128 model preview icons; Update() must run on the GPU thread under GpuSync.</summary>
	public class ModelIconBaker
	{
		private const uint IconSize = 128;
		private const float CameraFovDegrees = ModelViewportEnvironment.CameraFovDegrees;

		private readonly IGraphicsApi _graphicsApi;
		private readonly EditorSettings _editorSettings;
		private readonly ModelIconCache _cache;
		private readonly ModelStore _store;

		private readonly ModelViewportEnvironment _env;

		private readonly Queue<(string ModelPath, string ProjectDirectory, int Stage)> _queue = new();
		private readonly HashSet<string> _queued = new(StringComparer.OrdinalIgnoreCase);

		// Stages with no geometry: without this mark the browser re-queues them every frame.
		private readonly HashSet<string> _emptyStages = new(StringComparer.OrdinalIgnoreCase);

		private string? _currentPath;
		private string? _currentProjectDirectory;
		private int _currentStage;

		private ModelStore.Handle? _pendingHandle;
		private EditorLoadingStatus.Handle? _statusHandle;

		// LRU of parsed models: sub-mesh stages of one file must not re-parse it from disk.
		private sealed class ResidentModel
		{
			public ModelStore.Handle Handle = null!;
			public ModelLoader Model = null!;
			public readonly Dictionary<int, MeshId> MeshIdMap = new();
			public readonly Dictionary<int, MaterialId> MaterialIdMap = new();
			public readonly Dictionary<(int, int), BatchId> BatchCache = new();

			// This baker's own material set: pushing into model.materialObjects would clobber the live scene.
			public OrderedDictionary<int, IMaterialObject> Materials = null!;
		}

		private const int DefaultResidentCacheCapacity = 4;

		private readonly Dictionary<string, ResidentModel> _residentModels = new(StringComparer.OrdinalIgnoreCase);
		private readonly LinkedList<string> _residentLru = new();
		private int _residentCacheCapacity = DefaultResidentCacheCapacity;
		private ResidentModel? _currentResident;
		private readonly List<Entity> _stageEntities = new();

		/// <summary>How many recently baked models stay parsed in memory; clamped to at least 1.</summary>
		public int ResidentCacheCapacity
		{
			get => _residentCacheCapacity;
			set
			{
				_residentCacheCapacity = Math.Max(1, value);
				EvictExcessResidents();
			}
		}

		public ModelIconBaker(IGraphicsApi graphicsApi, EditorSettings editorSettings, ModelIconCache cache,
			ModelStore modelStore, SharedViewportResources sharedResources)
		{
			_graphicsApi = graphicsApi;
			_editorSettings = editorSettings;
			_cache = cache;
			_store = modelStore;

			// Shared container only to reuse the viewports' environment texture and samplers.
			_env = new ModelViewportEnvironment(graphicsApi, IconSize, IconSize,
				"Model Icon Bake Color", "Model Icon Bake Depth", sharedResources);
		}

		/// <summary>Queues a whole-model icon bake; duplicates are ignored, cache freshness is the caller's job.</summary>
		public void Enqueue(string modelPath, string projectDirectory) =>
			EnqueueInternal(modelPath, projectDirectory, ModelIconCache.WholeModelIndex);

		/// <summary>Queues one sub-mesh icon; call lazily, only when that row becomes visible.</summary>
		public void EnqueueSubMeshIcon(string modelPath, string projectDirectory, int subMeshIndex) =>
			EnqueueInternal(modelPath, projectDirectory, subMeshIndex);

		private void EnqueueInternal(string modelPath, string projectDirectory, int stage)
		{
			if (_emptyStages.Contains(MakeQueueKey(modelPath, stage)))
			{
				return;
			}

			if (!_queued.Add(MakeQueueKey(modelPath, stage)))
			{
				return;
			}

			_queue.Enqueue((modelPath, projectDirectory, stage));
		}

		/// <summary>Drops a queued sub-mesh bake; a bake already in flight cannot be cancelled and runs to the end.</summary>
		public void CancelSubMeshIcon(string modelPath, int subMeshIndex)
		{
			if (_currentPath != null && _currentStage == subMeshIndex &&
			    string.Equals(_currentPath, modelPath, StringComparison.OrdinalIgnoreCase))
			{
				return;
			}

			if (!_queued.Remove(MakeQueueKey(modelPath, subMeshIndex)))
			{
				return;
			}

			var kept = _queue.Where(job => job.Stage != subMeshIndex ||
				!string.Equals(job.ModelPath, modelPath, StringComparison.OrdinalIgnoreCase)).ToList();
			_queue.Clear();
			foreach (var job in kept)
			{
				_queue.Enqueue(job);
			}
		}

		public bool IsBakingOrQueued(string modelPath) =>
			IsQueuedOrCurrent(modelPath, ModelIconCache.WholeModelIndex);

		public bool IsSubMeshIconBakingOrQueued(string modelPath, int subMeshIndex) =>
			IsQueuedOrCurrent(modelPath, subMeshIndex);

		private bool IsQueuedOrCurrent(string modelPath, int stage) =>
			_queued.Contains(MakeQueueKey(modelPath, stage)) ||
			(_currentPath != null && _currentStage == stage &&
			 string.Equals(_currentPath, modelPath, StringComparison.OrdinalIgnoreCase));

		private static string MakeQueueKey(string modelPath, int stage) => $"{modelPath}{stage}";

		public void Update(float deltaTime, float time)
		{
			if (_currentPath != null)
			{
				if (_pendingHandle != null)
				{
					PollPendingLoad();
					return;
				}

				try
				{
					BakeNextStage(deltaTime, time);
				}
				catch (Exception ex)
				{
					EngineLog.Add(LogLevel.Error, $"Icon bake: failed on '{_currentPath}' (stage {_currentStage}): {ex.Message}");
					FinishCurrentJob();
				}

				return;
			}

			if (_queue.Count > 0)
			{
				StartNextJob();
			}
		}

		private void StartNextJob()
		{
			var (modelPath, projectDirectory, stage) = _queue.Dequeue();
			_currentPath = modelPath;
			_currentProjectDirectory = projectDirectory;
			_currentStage = stage;

			if (_residentModels.TryGetValue(modelPath, out var resident))
			{
				// A fresh status handle is mandatory: BakeNextStage dereferences _statusHandle.
				_currentResident = resident;
				TouchResident(modelPath);
				_statusHandle = EditorLoadingStatus.Begin($"Baking icon: {Path.GetFileName(modelPath)}");
				return;
			}

			_currentResident = null;

			EngineLog.Add(LogLevel.Warning,
				$"Icon bake: FULL reload for '{modelPath}' stage={stage} " +
				$"(resident cache has {_residentModels.Count} model(s): [{string.Join(", ", _residentLru)}]) - " +
				"not found in resident cache, re-parsing from disk.");

			// Bake options differ from the viewports' on purpose: a distinct ModelStore key.
			_pendingHandle = _store.Acquire(modelPath, BuildBakeOptions());
			_statusHandle = EditorLoadingStatus.Begin($"Baking icon: {Path.GetFileName(modelPath)}");
		}

		private ModelLoadOptions BuildBakeOptions() => new()
		{
			VertexShader = _editorSettings.DefaultVertexShader,
			PixelShader = _editorSettings.DefaultPixelShader,
			OptimizeMesh = false,
			GenerateLods = false,
			// Full-size textures (Sponza: hundreds of 4K) blow VRAM during the bake itself.
			MaxTextureSize = 512
		};

		private void PollPendingLoad()
		{
			var handle = _pendingHandle!;

			if (_store.TryGetError(handle, out var error))
			{
				_pendingHandle = null;
				_store.Release(handle);
				EngineLog.Add(LogLevel.Error, $"Icon bake: failed to load '{_currentPath}': {error}");
				FinishCurrentJob();
				return;
			}

			if (!_store.TryGetReady(handle, out var model))
			{
				// ModelStore has no per-handle progress, so this is a fixed "loading" value.
				_statusHandle!.Progress = 0.25f;
				return;
			}

			_pendingHandle = null;

			try
			{
				var resident = new ResidentModel { Handle = handle, Model = model };
				var materials = _store.AcquireMaterialSet(handle);
				ModelViewportGeometry.RegisterModelResources(_env.BatchRenderer, resident.Model, resident.MeshIdMap, resident.MaterialIdMap,
					_env.SharedResources.EnvMapSampler, _env.SceneCopyTarget, _env.EnvironmentMap, materials,
					_env.SharedResources.SceneColorSampler);
				resident.Materials = materials;
				// Cache only after registration succeeds: a half-filled id map must not go resident.
				_currentResident = resident;
				AddResident(_currentPath!, resident);
			}
			catch (Exception ex)
			{
				_store.Release(handle);
				EngineLog.Add(LogLevel.Error, $"Icon bake: failed to load '{_currentPath}': {ex.Message}");
				_currentResident = null;
				FinishCurrentJob();
			}
		}

		private void BakeNextStage(float deltaTime, float time)
		{
			var model = _currentResident!.Model;
			_statusHandle!.Progress = 0.75f;

			if (_currentStage == ModelIconCache.WholeModelIndex)
			{
				// Source changed: clear empty marks first, previously empty sub-meshes may have geometry now.
				var prefix = _currentPath!;
				_emptyStages.RemoveWhere(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
			}

			CreateStageEntities(model, _currentStage);

			if (_stageEntities.Count > 0)
			{
				FrameCamera(model, _currentStage);
				ApplyIconPreviewSettings(model, _currentResident!.Materials, _currentStage);

				_env.Pipeline.InvalidateGraph();

				_env.Root.Update(new UpdateTick(deltaTime, time));
				_env.Pipeline.Execute();

				var pixels = DiligentTextureReadback.ReadRgba8(_env.DilApi, (DiligentRenderTarget)_env.ColorTarget,
					out var width, out var height);

				_cache.SaveIcon(_currentProjectDirectory!, _currentPath!, _currentStage, pixels, width, height);
			}
			else
			{
				EngineLog.Add(LogLevel.Warning,
					$"Icon bake: stage {_currentStage} of '{_currentPath}' produced 0 renderable instances " +
					$"(resident MeshIdMap has {_currentResident.MeshIdMap.Count} mesh(es), model has {model.instances.Count} instance(s)) - marked empty.");
				_emptyStages.Add(MakeQueueKey(_currentPath!, _currentStage));
			}

			ClearStageEntities();

			if (_currentStage == ModelIconCache.WholeModelIndex)
			{
				var subMeshNames = new List<string>(model.Meshes.Count);
				foreach (var mesh in model.Meshes)
				{
					subMeshNames.Add(mesh.Name);
				}

				_cache.SaveManifest(_currentProjectDirectory!, _currentPath!, subMeshNames);
			}

			var bakedPath = _currentPath!;
			var bakedStage = _currentStage;
			FinishCurrentJob();

			// Must run after the files are written, or the browser re-reads its stale "no cache" state.
			_cache.Invalidate(bakedPath, bakedStage);
		}

		// stage < 0 means every instance of the model, otherwise only that sub-mesh's instances.
		private void CreateStageEntities(ModelLoader model, int stage)
		{
			for (int i = 0; i < model.instances.Count; i++)
			{
				var instance = model.instances[i];
				if (stage >= 0 && instance.meshId != stage)
				{
					continue;
				}

				var entity = ModelViewportGeometry.CreateInstanceEntity(_env.Store, _env.ResourceManager,
					_env.BatchRenderer, _currentResident!.MeshIdMap, _currentResident.MaterialIdMap, _currentResident.BatchCache,
					instance.meshId, instance.materialId, instance.transform);
				if (entity != null)
				{
					_stageEntities.Add(entity.Value);
				}
			}
		}

		private void ClearStageEntities()
		{
			foreach (var entity in _stageEntities)
			{
				_env.ResourceManager.UnregisterRenderable(entity);
				entity.DeleteEntity();
			}

			_stageEntities.Clear();
		}

		private void FrameCamera(ModelLoader model, int stage)
		{
			Vector3 min, max;
			if (stage < 0)
			{
				(min, max) = model.ComputeBounds();
			}
			else
			{
				(min, max) = ModelViewportGeometry.ComputeSubMeshBounds(model, stage);
			}

			var target = (min + max) * 0.5f;
			if (!float.IsFinite(target.X) || !float.IsFinite(target.Y) || !float.IsFinite(target.Z))
			{
				target = Vector3.Zero;
				min = new Vector3(-1f);
				max = new Vector3(1f);
			}

			var radius = MathF.Max(0.05f, (max - min).Length() * 0.5f);
			var distance = ModelViewportGeometry.ComputeFramingDistance(radius, CameraFovDegrees);

			const float yaw = -0.6f;
			const float pitch = 0.35f;

			var eye = ModelViewportGeometry.ComputeOrbitEye(target, distance, yaw, pitch);
			_env.SetCameraTransform(eye, target);
		}

		// Must be re-applied every stage: stages of one resident model share material instances.
		private static void ApplyIconPreviewSettings(ModelLoader model,
			OrderedDictionary<int, IMaterialObject> materials, int stage)
		{
			var data = new PreviewSettingsData { Mode = stage == ModelIconCache.WholeModelIndex ? 0 : 1, Channel = 0 };

			// Writes the baker's own material set, never model.materialObjects (shared with the live scene).
			for (int i = 0; i < materials.Count; i++)
			{
				var kvp = materials.GetAt(i);

				model.MaterialPbr.TryGetValue(kvp.Key, out var pbr);
				data.UvOffset = pbr.UvOffset;
				data.UvTransform = pbr.UvTransform;
				data.UvHasTransform = pbr.HasUvTransform ? 1 : 0;
				data.OcclusionUvSet = pbr.OcclusionUvSet;

				kvp.Value.SetConstant("PreviewSettings", ref data, HandleAccess.Pixel);
			}
		}

		private void TouchResident(string modelPath)
		{
			_residentLru.Remove(modelPath);
			_residentLru.AddFirst(modelPath);
		}

		private void AddResident(string modelPath, ResidentModel resident)
		{
			_residentModels[modelPath] = resident;
			TouchResident(modelPath);
			EvictExcessResidents();
		}

		private void EvictExcessResidents()
		{
			while (_residentLru.Count > _residentCacheCapacity)
			{
				var oldest = _residentLru.Last!.Value;
				_residentLru.RemoveLast();
				if (_residentModels.Remove(oldest, out var resident))
				{
					ReleaseResident(resident);
				}
			}
		}

		// Safe without a GPU barrier: an evicted resident has no live instance entities left.
		private void ReleaseResident(ResidentModel resident)
		{
			_env.BatchRenderer.UnregisterModel(resident.BatchCache.Values, resident.MaterialIdMap.Values,
				resident.MeshIdMap.Values);
			_store.Release(resident.Handle);

			if (ReferenceEquals(_currentResident, resident))
			{
				_currentResident = null;
			}
		}

		private void FinishCurrentJob()
		{
			ClearStageEntities();

			// _currentResident is deliberately kept: the model stays in the LRU for the next job.

			if (_statusHandle != null)
			{
				EditorLoadingStatus.End(_statusHandle);
				_statusHandle = null;
			}

			if (_currentPath != null)
			{
				_queued.Remove(MakeQueueKey(_currentPath, _currentStage));
			}

			_pendingHandle = null;
			_currentPath = null;
			_currentProjectDirectory = null;
		}
	}
}
