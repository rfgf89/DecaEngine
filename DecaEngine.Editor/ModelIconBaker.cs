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
	/// <summary>
	/// Фоновый "бейкер" превью-иконок моделей для AssetBrowser: при первом выборе .gltf/.glb
	/// ассета (см. AssetBrowserWindow) модель ставится в очередь на иконку ЦЕЛОЙ модели +
	/// манифест имён сабмешей; иконки отдельных SubMesh-ей бейкаются ЛЕНИВО, по одной, только
	/// когда соответствующая строка сабмеша реально стала видна в развёрнутом дереве Asset
	/// Browser (см. AssetBrowserWindow.RenderEntry -> EnqueueSubMeshIcon). Так модель с
	/// большим числом сабмешей не фризит редактор целиком одним нажатием - каждый бейк-этап
	/// делает Flush+WaitForIdle на общем GPU-контексте (см. BakeNextStage), и до этого рефакторинга
	/// это происходило разом для всех сабмешей сразу при первом выборе модели.
	///
	/// Каждая задача в очереди - это ровно один этап (128x128, целая модель или один сабмеш):
	/// рендерится оффскрин, пиксели считываются на CPU (<see cref="DiligentTextureReadback"/>)
	/// и сохраняются PNG в EditorCache проекта через <see cref="ModelIconCache"/>. Устроен как
	/// урезанный <see cref="ModelPreviewViewport"/> (свой EntityStore/DiligentBatchRenderer/
	/// GraphicsPipeline), но без интерактива и с фиксированным размером таргета.
	///
	/// Update() должен вызываться на GPU-потоке под lock (GameHostBridge.GpuSync) - как и
	/// ModelPreviewViewport.Update (см. EditorManager.OnUpdate).
	/// </summary>
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

		// Этапы, у которых при бейке не оказалось ни одной renderable-геометрии (пустой сабмеш без
		// треугольников, мёртвая ссылка meshId и т.п. - см. BakeNextStage): PNG для них не сохраняется
		// (AssetBrowser рисует векторный глиф-фолбэк), а без этой пометки браузер, видя что иконки
		// по-прежнему нет, ставил бы тот же этап в очередь заново КАЖДЫЙ кадр - бесконечный цикл
		// холостых бейков. Пометки этой модели сбрасываются при перебейке её целой иконки (модель
		// изменилась на диске - у сабмешей появляется новый шанс).
		private readonly HashSet<string> _emptyStages = new(StringComparer.OrdinalIgnoreCase);

		// Состояние текущей задачи (один этап - целая модель либо один сабмеш - за раз).
		private string? _currentPath;
		private string? _currentProjectDirectory;
		private int _currentStage;

		/// <summary>Заявка на модель в <see cref="ModelStore"/>, пока она ещё не готова - см.
		/// <see cref="PollPendingLoad"/>. Загрузка/декод/финализация теперь целиком в столе (общие на
		/// весь редактор), бейкер только ждёт готовности и регистрирует СВОИ материалы/меши.</summary>
		private ModelStore.Handle? _pendingHandle;
		private EditorLoadingStatus.Handle? _statusHandle;

		// Резидентные модели: сабмеши - это логические части уже распарсенного файла, так что
		// задачи по одному и тому же modelPath (например все видимые сабмеши только что
		// развёрнутого узла) не должны каждый раз заново грузить .gltf/.glb с диска. Держим LRU
		// на несколько последних моделей (не одну) - при бейке нескольких моделей вперемешку
		// (например разворачивают то один, то другой узел в браузере) это избавляет от повторной
		// загрузки уже виденных файлов. Вытеснение из кеша теперь корректно освобождает GPU-
		// регистрации этого бейкера (см. ReleaseResident) - раньше (до переезда на ModelStore) они
		// молча утекали, потому что у батч-рендерера не было API частичного удаления; теперь есть
		// (DiligentBatchRenderer.UnregisterModel), а сама модель отпускается через _store.Release,
		// а не разваливается вместе с бейкером.
		private sealed class ResidentModel
		{
			public ModelStore.Handle Handle = null!;
			public ModelLoader Model = null!;
			public readonly Dictionary<int, MeshId> MeshIdMap = new();
			public readonly Dictionary<int, MaterialId> MaterialIdMap = new();
			public readonly Dictionary<(int, int), BatchId> BatchCache = new();
		}

		private const int DefaultResidentCacheCapacity = 4;

		private readonly Dictionary<string, ResidentModel> _residentModels = new(StringComparer.OrdinalIgnoreCase);
		private readonly LinkedList<string> _residentLru = new();
		private int _residentCacheCapacity = DefaultResidentCacheCapacity;
		private ResidentModel? _currentResident;
		private readonly List<Entity> _stageEntities = new();

		/// <summary>
		/// Сколько последних моделей держать резидентными одновременно (см. class-doc у поля
		/// <see cref="_residentModels"/>). При уменьшении лишние сразу вытесняются. Минимум 1.
		/// </summary>
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

			// Бейкер не рисует небо/тени/HDRI (см. ctor defaults ниже) - shared-контейнер ему нужен
			// только затем, чтобы получить ТУ ЖЕ процедурную энвайронмент-текстуру и сэмплеры, что и у
			// остальных вьюпортов, вместо своей копии (см. class-doc SharedViewportResources).
			_env = new ModelViewportEnvironment(graphicsApi, IconSize, IconSize,
				"Model Icon Bake Color", "Model Icon Bake Depth", sharedResources);
		}

		/// <summary>
		/// Ставит в очередь бейк иконки ЦЕЛОЙ модели (плюс манифест имён сабмешей - см.
		/// <see cref="BakeNextStage"/>). Дубликаты (уже в очереди или в работе) игнорируются -
		/// проверять актуальность кеша должен вызывающий (через <see cref="ModelIconCache.GetManifest"/>),
		/// тут только защита от повторной постановки.
		/// </summary>
		public void Enqueue(string modelPath, string projectDirectory) =>
			EnqueueInternal(modelPath, projectDirectory, ModelIconCache.WholeModelIndex);

		/// <summary>
		/// Ставит в очередь бейк иконки ОДНОГО сабмеша - вызывается лениво, когда строка этого
		/// сабмеша реально стала видна в развёрнутом дереве Asset Browser, а не сразу для всех
		/// сабмешей при выборе модели целиком (см. class-doc выше).
		/// </summary>
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

		/// <summary>
		/// Убирает бейк сабмеша из очереди, если его строка перестала быть видна в AssetBrowser
		/// (проскроллили мимо, свернули узел модели и т.п.) - вызывается каждый кадр из
		/// AssetBrowserWindow.RenderEntry для невидимых строк, поэтому дешёвый no-op, если задачи
		/// нет. Если сабмеш уже бейкается прямо сейчас (see cref="_currentPath"/) - не трогаем,
		/// прервать бейк в процессе (модель уже грузится/рендерится на GPU) безопасно нельзя,
		/// достраиваем его до конца.
		/// </summary>
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
			// _currentPath != null - есть активная задача (грузится или уже готова к бейку);
			// в отличие от _residentModels, которые остаются в LRU-кеше между задачами (см. поле выше).
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
				// Модель уже резидентна в LRU-кеше (эта же модель с предыдущей задачи или одна из
				// недавних, к которой вернулись) - грузить .gltf/.glb заново незачем, следующий
				// Update() сразу перейдёт к BakeNextStage. Но _statusHandle предыдущей задачи уже
				// обнулён в FinishCurrentJob - без нового хендла BakeNextStage упадёт с NullReferenceException
				// на _statusHandle!.Progress.
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

			// Опции бейка ОТЛИЧАЮТСЯ от опций вьюпортов (MaxTextureSize=512, без стриминга текстур -
			// иконка не нуждается в полном качестве) - это намеренно даёт ДРУГОЙ ключ в столе
			// (см. ModelLoadOptions.Signature), т.е. свою, отдельную от вьюпортов запись/ModelLoader,
			// даже для того же самого файла.
			_pendingHandle = _store.Acquire(modelPath, BuildBakeOptions());
			_statusHandle = EditorLoadingStatus.Begin($"Baking icon: {Path.GetFileName(modelPath)}");
		}

		private ModelLoadOptions BuildBakeOptions() => new()
		{
			VertexShader = _editorSettings.DefaultVertexShader,
			PixelShader = _editorSettings.DefaultPixelShader,
			OptimizeMesh = false,
			GenerateLods = false,
			// Иконка - крохотный офскрин-кадр; полноразмерные текстуры (Intel Sponza: сотни
			// 4K) кладут VRAM ещё на стадии бейка, до открытия самого превью.
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
				// Стол не даёт прогресс на отдельный handle (общий на процесс) - фиксированное
				// значение "идёт загрузка", вторая половина (см. ниже) - сам бейк.
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
				// Помечаем модель резидентной только ПОСЛЕ успешной регистрации ресурсов - если
				// RegisterModelResources упадёт на середине, MeshIdMap/MaterialIdMap останутся не
				// полностью заполненными, и в кеш эта модель попадать не должна (см. StartNextJob).
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

		/// <summary>
		/// Рендерит и сохраняет одну иконку (<see cref="_currentStage"/>): создаёт сущности этапа,
		/// кадрирует камеру, гоняет пайплайн, читает пиксели и пишет PNG. Для этапа "целая модель"
		/// заодно сохраняет манифест имён сабмешей (нужен только для списка имён и не требует
		/// бейка их иконок - те бейкаются отдельными ленивыми задачами, см. EnqueueSubMeshIcon).
		/// Всегда завершает задачу целиком - здесь больше нет цикла по всем сабмешам модели.
		/// </summary>
		private void BakeNextStage(float deltaTime, float time)
		{
			var model = _currentResident!.Model;
			_statusHandle!.Progress = 0.75f;

			if (_currentStage == ModelIconCache.WholeModelIndex)
			{
				// Перебейк целой модели = исходник изменился (или кеша не было) - сбрасываем
				// empty-пометки всех её этапов ДО бейка (ниже пустой этап может пометиться заново):
				// в новой версии файла у пустых прежде сабмешей могла появиться геометрия.
				var prefix = _currentPath!;
				_emptyStages.RemoveWhere(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
			}

			CreateStageEntities(model, _currentStage);

			if (_stageEntities.Count > 0)
			{
				FrameCamera(model, _currentStage);
				ApplyIconPreviewSettings(model, _currentStage);

				_env.Pipeline.InvalidateGraph();

				_env.Root.Update(new UpdateTick(deltaTime, time));
				_env.Pipeline.Execute();

				var pixels = DiligentTextureReadback.ReadRgba8(_env.DilApi, (DiligentRenderTarget)_env.ColorTarget,
					out var width, out var height);

				_cache.SaveIcon(_currentProjectDirectory!, _currentPath!, _currentStage, pixels, width, height);
			}
			else
			{
				// Нечего рендерить (сабмеш без треугольников, мёртвая ссылка meshId и т.п.) - PNG не
				// сохраняем (браузер оставит векторный глиф) и помечаем этап пустым, чтобы браузер не
				// ставил его в очередь заново каждый кадр (см. _emptyStages/EnqueueInternal).
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

			// Сбросить рантайм-состояние кеша ПОСЛЕ записи файлов - чтобы AssetBrowser перечитал
			// свежий манифест/иконку и подхватил новый PNG вместо запомненного "кеша нет". Только для
			// этой стадии - иначе уже показанные иконки других сабмешей той же модели без нужды
			// перегружались бы с диска (см. ModelIconCache.Invalidate).
			_cache.Invalidate(bakedPath, bakedStage);
		}

		/// <summary>
		/// Создаёт сущности для этапа: stage &lt; 0 - все инстансы модели, иначе только инстансы
		/// данного сабмеша. Если у сабмеша нет ни одного инстанса (бывает у неиспользуемых мешей
		/// в glTF) - ничего не создаём: BakeNextStage увидит пустой _stageEntities и корректно
		/// пропустит бейк этого этапа (см. _emptyStages), вместо того чтобы рисовать несуществующий
		/// в сцене инстанс с единичным трансформом.
		/// </summary>
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

		/// <summary>
		/// Ставит камеру так, чтобы объект этапа целиком влезал в кадр (та же математика, что
		/// ModelPreviewViewport.FrameAll + его Update): bounding-сфера вокруг центра AABB, дистанция
		/// из вертикального FOV, фиксированный ракурс yaw=-0.6 / pitch=0.35.
		/// </summary>
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

		/// <summary>
		/// Baked icons never expose a mode picker (see <see cref="ModelPreviewViewport"/> for the
		/// interactive version) - the whole-model icon is always Textured, sub-mesh icons always
		/// Highlight (today's flat camera-rim-lit look), matching PreviewSettings' shared layout in
		/// UnlitInstancedPS.hlsl. Must be re-applied every stage: whole-model and sub-mesh bake jobs for
		/// the same resident model share the same <see cref="ModelLoader.materialObjects"/> instances.
		/// </summary>
		private static void ApplyIconPreviewSettings(ModelLoader model, int stage)
		{
			var data = new PreviewSettingsData { Mode = stage == ModelIconCache.WholeModelIndex ? 0 : 1, Channel = 0 };

			// KHR_texture_transform - пер-материальный, а Textured-режим иконки сэмплирует _MainTex:
			// без матрицы UV дерево/ткань в иконке тайлились бы не так, как в превью (см.
			// MaterialPbrFactors.UvTransform).
			for (int i = 0; i < model.materialObjects.Count; i++)
			{
				var kvp = model.materialObjects.GetAt(i);

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

		/// <summary>Снимает регистрации ЭТОГО бейкера (мешы/материалы/батчи в его батч-рендерере) для
		/// вытесненного из LRU резидента и отпускает его заявку в столе. Безопасно без GPU-барьера
		/// (см. тот же приём в ModelStreamer.OnStoreModelEvicted/DiligentBatchRenderer.UnregisterModel):
		/// у вытесняемого резидента к этому моменту нет живых инстанс-сущностей - ClearStageEntities
		/// снимает их сразу после КАЖДОГО этапа бейка (см. BakeNextStage), а вытесняется тут только НЕ
		/// текущий (см. AddResident - только что добавленный/тронутый резидент всегда в голове LRU).</summary>
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

			// _currentResident НЕ сбрасывается здесь - модель остаётся в LRU-кеше (см.
			// _residentModels выше) для следующей задачи по тому же (или недавнему) пути;
			// StartNextJob сам подставит нужную запись или создаст новую при вытеснении.

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
