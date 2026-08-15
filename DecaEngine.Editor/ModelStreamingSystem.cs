using System;
using System.Collections.Generic;
using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Core;
using DecaEngine.Graphics.Diligent;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;

namespace DecaEngine.Editor.ECS
{
	/// <summary>
	/// Per-environment (<see cref="ModelViewportEnvironment"/>) instantiation layer over the
	/// process-wide <see cref="ModelStore"/>: loading, decoding, GPU-texture streaming and the actual
	/// residency lifetime of a <see cref="ModelLoader"/> all live in the store now (one copy per
	/// (path, options) for the WHOLE editor, see its class-doc). This class only tracks, PER
	/// ENVIRONMENT, which models it currently wants resident (radius-based streaming, same as before),
	/// the <see cref="ModelStore.Handle"/> that represents this environment's claim on each one, and
	/// this environment's OWN registrations in ITS OWN <see cref="DiligentBatchRenderer"/> - a material
	/// set (<see cref="ModelStore.AcquireMaterialSet"/>) plus the resulting MeshId/MaterialId/BatchId
	/// maps.
	///
	/// A <see cref="Resident"/>'s store <see cref="Resident.StoreHandle"/> is held for as long as the
	/// LOCAL reference count is non-zero, AND kept a while longer even at zero (the store's own
	/// <see cref="ModelStore.UnloadAfterSeconds"/> hysteresis - not duplicated here) - so a
	/// <see cref="Release"/> immediately followed by a fresh <see cref="Acquire"/> of the same path (the
	/// camera oscillating at the edge of <see cref="StreamRadius"/>) is nearly free: the model is still
	/// resident in the store, and this environment's Mesh/Material/Batch registrations were never torn
	/// down, so nothing needs re-registering. Registrations are torn down ONLY in reaction to
	/// <see cref="ModelStore.BeforeModelEvicted"/> - i.e. only when the model ACTUALLY leaves the GPU
	/// (see <see cref="OnStoreModelEvicted"/>), which for a normal idle-out only happens once every
	/// handle across every environment has let go.
	///
	/// Кадровый привод - <see cref="ModelStreamingSystem"/> в SystemRoot окружения: она каждый кадр
	/// читает позицию камеры из ECS-стора и зовёт <see cref="Tick"/> (priority bookkeeping only now -
	/// см. class-doc <see cref="ModelStore.Tick"/> про то, кто на самом деле шагает загрузку/стриминг
	/// текстур один раз за кадр). Потребители (превью, префабы) берут модель через
	/// <see cref="Acquire"/>/<see cref="Release"/> и следят за <see cref="Resident.Ready"/>.
	/// </summary>
	public sealed class ModelStreamer
	{
		/// <summary>Локальное (для ЭТОГО окружения) состояние одного файла модели: регистрации в ЕГО
		/// батч-рендерере (MeshIds/MaterialIds/BatchCache) плюс ссылка на разделяемый
		/// <see cref="ModelLoader"/>, которым владеет <see cref="ModelStore"/>. Переживает
		/// Release/Acquire без переrегистрации, пока <see cref="ModelStore"/> не выселит саму модель -
		/// см. class-doc.</summary>
		public sealed class Resident
		{
			public readonly string Path;
			public ModelLoader? Model;
			public readonly Dictionary<int, MeshId> MeshIds = new();
			public readonly Dictionary<int, MaterialId> MaterialIds = new();
			public readonly Dictionary<(int, int), BatchId> BatchCache = new();
			public string? Error;

			/// <summary>Мировая точка, по которой считается приоритет загрузки (расстояние до камеры).
			/// Потребитель обновляет её при движении своей сущности.</summary>
			public Vector3 Anchor;

			internal int RefCount;

			/// <summary>Заявка ЭТОГО окружения на модель в столе - живёт, пока RefCount &gt; 0 (см.
			/// <see cref="EnsureStoreHandle"/>/<see cref="Release"/>); null, если сейчас нет активной
			/// заявки (либо ещё не потребовалась, либо путь оказался с ошибкой - см. Error).</summary>
			internal ModelStore.Handle? StoreHandle;

			public bool Ready => Model != null;
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

		// Скретч выселения абандонированных/ошибочных записей - без аллокаций на кадр.
		private readonly List<Resident> _evictScratch = new();

		/// <summary>Гистерезис выгрузки: уже резидентная модель отпускается только за
		/// <see cref="StreamRadius"/> * этот множитель - иначе на самой границе радиуса она бы
		/// грузилась и выгружалась каждый шаг камеры.</summary>
		public const float StreamOutHysteresis = 1.15f;

		/// <summary>Радиус стриминга от камеры (мировые единицы): дальше него потребители через
		/// <see cref="ShouldBeResident"/> отпускают модель, ближе - берут. Бесконечность = грузить
		/// всё независимо от камеры (камера тогда влияет только на ПОРЯДОК загрузки, через приоритет
		/// в столе).</summary>
		public float StreamRadius { get; set; } = float.PositiveInfinity;

		/// <summary>Модель этого окружения догрузилась и зарегистрирована в его батч-рендерере -
		/// можно инстанцировать.</summary>
		public event Action<Resident>? ModelReady;

		/// <summary>Стол СЕЙЧАС снимет регистрации батч-рендерера этого окружения для ОДНОЙ конкретной
		/// модели (см. <see cref="ModelStore.BeforeModelEvicted"/>): подписчик обязан снять свои
		/// инстанс-сущности, ссылающиеся ИМЕННО на этот <see cref="Resident"/> (сравнение по ссылке -
		/// см. <see cref="Resident"/>), пока его BatchId-ы ещё валидны. Другие резиденты этим событием
		/// не затронуты - в отличие от прежней версии (полный сброс на любое частичное выселение), см.
		/// задачу про сужение ResidencyResetting.</summary>
		public event Action<Resident>? ResidencyResetting;

		/// <summary>Сброс завершён для этой модели - подписчик может инстанцировать её заново (или
		/// признать её окончательно ушедшей).</summary>
		public event Action<Resident>? ResidencyReset;

		public ModelStreamer(ModelViewportEnvironment env, ModelStore store, IGraphicsApi graphicsApi,
			Func<ModelLoadOptions> optionsFactory)
		{
			_env = env;
			_store = store;
			_graphicsApi = graphicsApi;
			_optionsFactory = optionsFactory;

			// Живут всю сессию редактора вместе с этим ModelStreamer-ом (он не пересоздаётся при
			// RecreateEnvironment - меняется только _env) - отписка не требуется.
			_store.ModelReady += OnStoreModelReady;
			_store.BeforeModelEvicted += OnStoreModelEvicted;
		}

		/// <summary>Резидентный кеш на чтение - вьюпортам для обхода загруженных моделей
		/// (материалы, probe-GI и т.п.). Не мутировать.</summary>
		public IReadOnlyDictionary<string, Resident> Models => _models;

		/// <summary>Есть ли незавершённая работа (модель запрошена, но стол её ещё не выдал ни
		/// готовой, ни ошибкой).</summary>
		public bool HasPendingLoads
		{
			get
			{
				foreach (var resident in _models.Values)
				{
					if (resident.Model == null && resident.Error == null)
					{
						return true;
					}
				}

				return false;
			}
		}

		/// <summary>Позиция камеры последнего Tick - от неё считаются приоритеты и радиус стриминга.</summary>
		public Vector3 CameraPosition => _cameraPos;

		/// <summary>Решение стриминга для точки: держать ли модель с якорем <paramref name="anchor"/>
		/// резидентной при текущей камере. С гистерезисом - см. <see cref="StreamOutHysteresis"/>.</summary>
		public bool ShouldBeResident(Vector3 anchor, bool currentlyResident)
		{
			if (float.IsPositiveInfinity(StreamRadius))
			{
				return true;
			}

			var distance = Vector3.Distance(anchor, _cameraPos);
			return distance <= (currentlyResident ? StreamRadius * StreamOutHysteresis : StreamRadius);
		}

		/// <summary>Берёт ссылку на модель файла (загрузка/финализация/стриминг текстур - целиком в
		/// <see cref="ModelStore"/>, по приоритету камеры). Каждому Acquire обязан соответствовать
		/// <see cref="Release"/>.</summary>
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

		/// <summary>Отпускает ссылку. НЕ трогает регистрации/модель немедленно - см. class-doc: модель
		/// (и это окружение регистрация) остаётся валидной, пока стол сам не решит её выселить
		/// (<see cref="OnStoreModelEvicted"/>), что обычно происходит намного позже (гистерезис стола,
		/// либо модель ещё нужна другому окружению).</summary>
		public void Release(Resident resident)
		{
			resident.RefCount = Math.Max(0, resident.RefCount - 1);

			if (resident.RefCount == 0 && resident.StoreHandle != null)
			{
				_store.Release(resident.StoreHandle);
				resident.StoreHandle = null;
			}
		}

		/// <summary>
		/// Кадровый шаг (главный поток, под GPU-локом): обновляет приоритет загрузки/апгрейда текстур
		/// этого окружения для стола (расстояние до камеры) и забывает локально абандонированные/
		/// ошибочные записи. Саму загрузку/финализацию/стриминг текстур шагает
		/// <see cref="ModelStore.Tick"/> - ОДИН раз за кадр на весь процесс (см. EditorManager), а не
		/// отсюда, иначе бюджет "одна финализация за кадр" считался бы отдельно на каждое окружение.
		/// </summary>
		public void Tick(float deltaTime, Vector3 cameraPos)
		{
			_cameraPos = cameraPos;

			foreach (var resident in _models.Values)
			{
				if (resident.StoreHandle != null)
				{
					_store.SetPriority(resident.StoreHandle, DistanceToCamera(resident));
				}
			}

			EvictAbandoned();
		}

		private float DistanceToCamera(Resident resident) => Vector3.Distance(resident.Anchor, _cameraPos);

		/// <summary>Заводит заявку этого окружения на модель в столе, если её ещё нет и путь не помечен
		/// окончательно ошибочным (см. Resident.Error - тот же приём, что был в столе/старом стримере:
		/// однажды распознанный неподдерживаемый формат больше не переспрашивается).</summary>
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
				_store.Release(handle); // путь безнадёжен - незачем держать заявку на мёртвую запись
				return;
			}

			resident.StoreHandle = handle;
		}

		/// <summary>Модель СТОЛА догрузилась (в общем-то любая, для любого окружения процесса) -
		/// проверяем, не наша ли это заявка, и если да - регистрируем в СВОЁМ батч-рендерере.</summary>
		private void OnStoreModelReady(ModelLoader model)
		{
			foreach (var resident in _models.Values)
			{
				if (resident.Model != null || resident.StoreHandle == null)
				{
					continue;
				}

				if (!_store.TryGetReady(resident.StoreHandle, out var readyModel) ||
					!ReferenceEquals(readyModel, model))
				{
					continue;
				}

				try
				{
					RegisterResident(resident, readyModel);
					resident.Model = readyModel;
					ModelReady?.Invoke(resident);
				}
				catch (Exception ex)
				{
					resident.Error = ex.Message;
					EditorConsoleLog.Add(LogLevel.Error,
						$"Model streaming: failed to register '{resident.Path}': {ex.Message}");
				}

				return; // пути уникальные ключи - максимум один резидент может ссылаться на эту модель
			}
		}

		/// <summary>Стол СЕЙЧАС выселит эту модель (см. <see cref="ModelStore.BeforeModelEvicted"/>) -
		/// если это НАША заявка (сравнение по ссылке), снимаем регистрации ИМЕННО этого резидента в
		/// своём батч-рендерере. Другие резиденты не трогаются - партиционное выселение, а не сброс
		/// всего окружения (см. class-doc про сужение прежнего ResidencyResetting).</summary>
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
				// Либо это окружение никогда не регистрировало эту модель, либо уже само сняло
				// регистрацию раньше (Release уронил RefCount до нуля и отпустил handle стола ДО того,
				// как стол реально дошёл до выселения - см. Release/EnsureStoreHandle).
				return;
			}

			ResidencyResetting?.Invoke(found);

			_env.BatchRenderer.UnregisterModel(found.BatchCache.Values, found.MaterialIds.Values, found.MeshIds.Values);
			found.Model = null;
			found.MeshIds.Clear();
			found.MaterialIds.Clear();
			found.BatchCache.Clear();
			found.StoreHandle = null;

			_env.Pipeline.InvalidateGraph();
			ResidencyReset?.Invoke(found);
		}

		private void RegisterResident(Resident resident, ModelLoader model)
		{
			var materials = _store.AcquireMaterialSet(resident.StoreHandle!);
			ModelViewportGeometry.RegisterModelResources(_env.BatchRenderer, model, resident.MeshIds, resident.MaterialIds,
				_graphicsApi, _env.SceneCopyTarget, _env.EnvironmentMap, materials);
		}

		/// <summary>Записи без ссылок, которые так и не дожили до готовности (заявка снята Release-ом
		/// ДО того, как стол успел её выдать) или окончательно ошибочные (Resident.Error) - забываем,
		/// иначе словарь копит мусор по путям, мимо которых камера просто проехала (тот же приём, что
		/// раньше делал стол/стример при отмене фоновой загрузки).</summary>
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

		/// <summary>
		/// Полная очистка ЭТОГО окружения: сбрасывает батч-рендерер целиком (дешевле, чем гонять
		/// UnregisterModel по каждой когда-либо зарегистрированной записи - см. тот же приём в
		/// DiligentBatchRenderer.ResetRegistrations) и отпускает заявки стола на все резиденты. Модели
		/// сами по себе НЕ освобождаются здесь - это теперь дело стола (Release просто снимает заявку
		/// этого окружения; резидентность в других окружениях/гистерезис стола решают, когда модель
		/// реально уйдёт с GPU). Вызывающий обязан снять СВОИ инстанс-сущности (и записи, ссылающиеся
		/// на резидентов) ДО этого вызова - см. PrefabSceneViewport.ClearScene/ModelPreviewViewport.LoadModel.
		/// </summary>
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
				// Кадры с ресурсами старых регистраций могут быть в полёте - без ожидания GPU сброс
				// батч-рендерера роняет драйвер (та же дисциплина, что была в прежнем ClearAll).
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
				resident.MeshIds.Clear();
				resident.MaterialIds.Clear();
				resident.BatchCache.Clear();
			}

			_models.Clear();

			if (anyRegistered)
			{
				_env.Pipeline.InvalidateGraph();
			}
		}

		/// <summary>Переезд в пересозданное окружение. Вызывающий уже дождался GPU и освободил старое
		/// окружение целиком (его батч-рендерер умер вместе с регистрациями - сбрасывать нечего).
		/// <paramref name="dropModels"/> - выбросить и сами заявки стола (например, анизотропия
		/// печётся в сэмплеры при загрузке - кеш непригоден, опции загрузки другие, значит и запись в
		/// столе другая); иначе резидентные модели просто перерегистрируются в новый батч-рендерер по
		/// уже готовым данным (без перечитывания с диска - ModelLoader тот же самый, только
		/// материалы этого окружения строятся заново, см. <see cref="ModelStore.AcquireMaterialSet"/>),
		/// а незавершённые загрузки продолжаются в столе как ни в чём не бывало.</summary>
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
				// Записи о сущностях старого стора забыты вызывающим - ссылки начнутся заново с
				// первого Acquire по новому окружению.
				resident.RefCount = 0;
				resident.MeshIds.Clear();
				resident.MaterialIds.Clear();
				resident.BatchCache.Clear();

				if (resident.Model == null)
				{
					continue;
				}

				try
				{
					// Материалы старого окружения умерли вместе с его батч-рендерером - этот резидент
					// уже когда-то брал набор у стола (см. ModelStore.AcquireMaterialSet), поэтому
					// повторный запрос гарантированно вернёт СВЕЖИЙ дополнительный набор, а не украдёт
					// первичный у кого-то другого.
					RegisterResident(resident, resident.Model);
				}
				catch (Exception ex)
				{
					resident.Error = ex.Message;
					resident.Model = null;
					EditorConsoleLog.Add(LogLevel.Error,
						$"Model streaming: failed to re-register '{resident.Path}' after environment recreate: {ex.Message}");
				}
			}
		}
	}

	/// <summary>
	/// ECS-привод стриминга: каждый кадр берёт позицию камеры окружения из стора (первая сущность с
	/// <see cref="CameraComponent"/>) и шагает <see cref="ModelStreamer.Tick"/> (приоритеты для стола -
	/// см. его class-doc, сама загрузка теперь шагается централизованно из EditorManager). Добавляется
	/// в SystemRoot окружения ПОСЛЕДНЕЙ (после GpuInstanceBufferSystem/культинга): готовая модель
	/// инстанцируется следующим кадром, зато финализация/регистрация не вклинивается между записью
	/// инстанс-буферов и раскладкой батчей текущего кадра.
	/// </summary>
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
