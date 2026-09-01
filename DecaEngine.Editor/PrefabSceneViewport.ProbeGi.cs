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
	/// <summary>Probe GI сцены: сессия бейка, GPU-путь, позы инстансов, снапшоты и текстуры. Часть <see cref="PrefabSceneViewport"/> - файл на тему;
	/// состояние, конструктор и кадровые Update/Render живут в основном файле.</summary>
	public partial class PrefabSceneViewport
	{
		/// <summary>Модели, по которым считается <see cref="_probeBakerTask"/>: их освобождение
		/// обязано задачу дождаться (она читает CPU-копии вершин в неуправляемой памяти).</summary>
		private List<(ModelLoader Model, Matrix4x4 World)>? _probeBakerModels;

		/// <summary>Снимок сцены (модель + мировая матрица записи), под который собран ЖИВОЙ
		/// <see cref="_probeBaker"/>. По нему BeginProbeSession решает, устарело ли дерево: BVH
		/// мировой и позам соответствует намертво.</summary>
		private List<(ModelLoader Model, Matrix4x4 World)>? _probeBakerBuiltFor;

		/// <summary>Записи сцены в порядке инстансов строящегося дерева. Переезжают в
		/// <see cref="_probeSceneRecords"/> ТОЛЬКО вместе с готовым бейкером: пара «бейкер + записи»
		/// сопоставляется по индексу, и рассинхрон уводит в TLAS чужие позы.</summary>
		private List<RenderedModel>? _probeBakerRecords;

		/// <summary>Совпадает ли СОСТАВ сцены (набор моделей в том же порядке). Изменился - дерево
		/// пересобирается при любом режиме трассировки: в нём просто нет геометрии новой записи.</summary>
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

		/// <summary>Совпадают ли ПОЗЫ записей. Матрицы сравниваются точным равенством намеренно:
		/// любое отличие означает, что мировые треугольники дерева уже не там, где геометрия.</summary>
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

		// --- Динамический GPU-путь сцены (реальное время, см. ModelPreviewViewport) ---------------
		// Движение сущности при живом GPU-пути НЕ пересоздаёт сессию: позы уезжают в TLAS, поле
		// перетекает само. Прежний путь на любое движение перепекал BVH на главном потоке (десятки
		// миллисекунд - тот самый лаг гизмо) и выбрасывал накопленное поле.
		private ProbeRoundPipelines? _scenePipelines;
		private ProbeSceneAccel? _sceneAccel;
		private ProbeRoundGpu? _sceneGpu;
		private bool _sceneGpuDisabled;

		// --- Собственный accel SSR (RT-фолбэк отражений БЕЗ probe GI) --------------------------
		// Когда accel проб недоступен (probe GI выключен/программный/ещё не собрался), SSR строит
		// геометрию сам: тем же конструктором ProbeGiBaker (сбор треугольников), но без сессии и
		// бейка. Предпочтение всегда у _sceneAccel - его позы живые (PollSceneProbePoses); свой
		// пересобирается по смене состава/поз сцены с дебаунсом (пересборка BLAS всей сцены дорогая).
		private ProbeSceneAccel? _ssrOwnAccel;
		private List<(ModelLoader Model, Matrix4x4 World)>? _ssrOwnBuiltFor;
		private float _ssrOwnRebuildDelay = -1f;

		// Наборы текстур RT-хитов (текстурное альбедо отражений, см. SsrHitTextures) - по одному
		// на каждый accel: живут и умирают вместе с ним, привязка выбирает набор того accel-а,
		// который реально ушёл в SetRayScene.
		private SsrHitTextures? _sceneAccelHitTextures;
		private SsrHitTextures? _ssrOwnHitTextures;

		/// <summary>Записи сцены в порядке списка моделей, отданного бейкеру: по
		/// ProbeGeometryInstance.SourceModel отсюда берётся ЖИВАЯ мировая матрица записи
		/// (RenderedModel - класс, LastWorld обновляется гизмо).</summary>
		private readonly List<RenderedModel> _probeSceneRecords = new();
		private readonly List<Matrix4x4> _probeScenePoses = new();
		private bool _sceneTlasDirty;

		/// <summary>Дебаг-вид проб сцены - общий жизненный цикл с превью (см.
		/// ProbeGiViewportShared.PollOverlays).</summary>
		private readonly List<(ProbeDebugOverlay Overlay, ProbeGiTextures Textures)> _sceneDebugOverlays = new();
		private bool _sceneDebugFailed;

		// --- Выделение (см. SyncSelectionHighlight / SelectionOutlineOverlay) --------------------
		private SelectionOutlineOverlay? _selectionOverlay;
		private int _highlightedId = -1;
		private bool _structuralDirtySelection;
		private readonly List<Vector3> _selectionPositions = new();
		private readonly List<uint> _selectionIndices = new();

		/// <summary>Результат клика по вьюпорту (см. <see cref="Render"/>): Clicked = был клик по
		/// сцене (не по гизмо), Entity = сущность префаба под курсором, null = клик в пустоту.</summary>
		public struct PickResult
		{
			public bool Clicked;
			public Entity? Entity;
		}

		private ShadingMode _shading = ShadingMode.Lighting;
		private PreviewFeatureFlags _featureFlags = PreviewFeatureFlags.All;

		private float _lightYawOffsetDegrees;
		private float _lightElevationOffsetDegrees;

		// Полёт/орбита/пан/фокус - см. SceneCamera. Заменяет прежние _orbitTarget/_yaw/_pitch/_distance/
		// _orbiting/_panning; ModelPreviewViewport оставлен на старой орбитальной камере намеренно -
		// там она правильная (см. задачу).
		private readonly SceneCamera _camera;
		private bool _framePending = true;

		private ImTextureRef _textureRef;
		private bool _textureBound;
		private ImGuiRender? _lastImGuiRender;
		private Vector2 _pendingSize;
		private float _resizeIdleSeconds;

		/// <summary>Масштаб рендера, увиденный последним TrackAndApplyResize, - смена сбрасывает
		/// дебаунс-таймер, как смена размера окна (см. ModelPreviewViewport).</summary>
		private float _pendingRenderScale = 1f;

		/// <summary>См. ModelPreviewViewport._pendingUpscalerApply - та же отложка.</summary>
		private bool _pendingUpscalerApply;

		/// <summary>Просит пересоздать сессию бейка (изменилась геометрия/позы сцены). Дебаунс -
		/// драг гизмо шлёт изменение каждый кадр, а новая сессия выбрасывает накопленное поле.</summary>
		private void RequestProbeSession(float delaySeconds)
		{
			if (ProbesEnabled && HasContent)
			{
				_probeSessionDelay = delaySeconds;
			}
		}

		/// <summary>Заводит сессию бейка по ВСЕЙ сцене: мульти-модельный BVH из (модель, мировая
		/// матрица записи) - см. новый конструктор ProbeGiBaker. Бейкер пересобирается на каждую
		/// сессию: в отличие от превью, геометрия сцены меняется, и BVH обязан ей соответствовать
		/// (десятки миллисекунд на главном потоке - за дебаунсом).</summary>
		private void BeginProbeSession()
		{
			if (!ProbesEnabled || _env.ShadowSettings == null || !TryComputeSceneBounds(out var min, out var max))
			{
				return;
			}

			// Список записей собирается ЛОКАЛЬНО и переносится в поле только вместе с бейкером, под
			// который он собран. Иначе, пока фоновая сборка считается, _probeSceneRecords уже новый,
			// а _probeBaker ещё старый - и PollSceneProbePoses, сопоставляющий их ПО ИНДЕКСУ
			// (ProbeGeometryInstance.SourceModel), утаскивает в TLAS матрицы от чужих записей.
			// Геометрия трассировки разъезжается с видимой, и пробы начинают ловить свет и тень из
			// ниоткуда - те самые веера и ромбы в отладочных видах.
			var sceneModels = new List<(ModelLoader Model, Matrix4x4 World)>();
			var sceneRecords = new List<RenderedModel>();
			foreach (var record in _rendered.Values)
			{
				if (record.Instantiated && !string.IsNullOrEmpty(record.ResolvedPath) &&
					_models.TryGetValue(record.ResolvedPath, out var state) && state.Model != null)
				{
					sceneModels.Add((state.Model, record.LastWorld));
					// Индекс здесь = SourceModel инстансов бейкера - по нему слежение за позами
					// достаёт живую LastWorld записи (см. PollSceneProbePoses).
					sceneRecords.Add(record);
				}
			}

			if (sceneModels.Count == 0)
			{
				return;
			}

			// Сборка BVH - в ФОНЕ: на сцене уровня Sponza это миллионы треугольников и десятки
			// секунд чистого CPU; на потоке рендера она вешала редактор целиком уже после того, как
			// модели показались (см. ModelPreviewViewport.BeginProbeSession - та же схема).
			//
			// Когда дерево устарело. Состав сцены - всегда: в старом BVH просто нет геометрии новой
			// записи. Позы - только на ПРОГРАММНОЙ трассировке, где лучи ходят по мировому BVH и он
			// приколочен к позам намертво; при аппаратной позы живут в TLAS и обновляются без
			// ребейка (см. PollSceneProbePoses - ради этого его и делали, ребейк на каждое движение
			// был тем самым лагом гизмо).
			//
			// Без этой сверки дерево после первой сборки оставалось бы под стартовые позы навсегда,
			// и подвинутый объект продолжал бы светить и затенять со старого места.
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

				// Записи поедут в поле вместе с готовым бейкером (см. PollProbeBake) - до тех пор
				// живой бейкер и _probeSceneRecords обязаны оставаться согласованной парой.
				_probeBakerRecords = sceneRecords;
				_probeBakerTask = Task.Run(() => new ProbeGiBaker(models));
				return;
			}

			if (!_probeBaker.HasGeometry)
			{
				return;
			}

			// Дерево актуально: состав тот же, значит порядок записей совпадает с порядком инстансов
			// живого бейкера - список можно принять.
			_probeSceneRecords.Clear();
			_probeSceneRecords.AddRange(sceneRecords);

			_probeSceneBoundsMin = min;
			_probeSceneBoundsMax = max;

			// LightDirection указывает ОТ солнца, бейкер ждёт направление НА солнце.
			_probeSession = _probeBaker.BeginBake(min, max,
				Vector3.Normalize(-_env.ShadowSettings.LightDirection), ProbeSunColor(),
				_env.ShadowSettings.EnvYawRadians, _env.EnvironmentRadiance, BuildSceneProbeOptions());

			TryBeginSceneProbeGpu();
		}

		/// <summary>Настройки бейка сцены.</summary>
		private ProbeGiBakeOptions BuildSceneProbeOptions() =>
			ProbeGiViewportShared.BuildOptions(_editorSettings);

		private Vector3 _probeSceneBoundsMin, _probeSceneBoundsMax;

		/// <summary>Поднимает GPU-путь сцены: атласы под запись из шейдера, аппаратные структуры,
		/// compute-раунды. Только в реальном времени - ради него всё и делается: движение сущностей
		/// перестаёт перепекать сессию (см. PollSceneProbePoses). При любой осечке молча остаёмся на
		/// CPU-пути - он рабочий, просто статический.</summary>
		private void TryBeginSceneProbeGpu()
		{
			var session = _probeSession;
			var baker = _probeBaker;
			if (session == null || baker == null || _sceneGpuDisabled)
			{
				return;
			}

			// Сессия пересоздана (структурное изменение сцены) - прежний GPU-комплект обязан уйти
			// ДО нового: он привязан к старой сессии, и без освобождения каждая пересборка текла бы
			// полным набором буферов. Оверлей - первым: замороженные команды графа держат атласы.
			ReleaseSceneProbeDebugOverlay();
			ReleaseSceneProbeGpu();
			if (_probeTextures != null)
			{
				// РАНЬШЕ освобождения: SRB SSR-трейса держит SH-атласы (свет RT-хитов).
				_env.SetSsrProbeField(null);
				_env.DilApi.ImmediateContext.Flush();
				_env.DilApi.ImmediateContext.WaitForIdle();
				_probeTextures.Release();
				_probeTextures = null;
			}

			try
			{
				// Кэш поверхностей - только запечке: в реальном времени он не читается (этап 3), а
				// его захват - сотни миллисекунд НА ПОТОКЕ РЕНДЕРА (стопор кадров, таймауты
				// кадрового объекта при создании сессии большой сцены).
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

				// Набор текстур RT-хитов - вместе с accel-ом: индексы в его таблице инстансов
				// указывают именно в этот набор. Модели - _probeBakerBuiltFor: снимок, под который
				// собран ЖИВОЙ бейкер (его порядок = индексы моделей в HitTextureKeys).
				// НЕ _probeBakerModels - это модели ЗАДАЧИ, и PollProbeBake обнуляет его при её
				// завершении, то есть здесь он всегда null, набор молча не строился, и bindless
				// уходил на плейсхолдеры («каша» вместо текстур в отражениях).
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

				// RT-фолбэк SSR питается этим же accel-ом: он мог только что появиться (фича ждала
				// его) или пересоздаться (дескриптор протух) - фичи и привязка обновляются здесь же.
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

		/// <summary>Ведёт дебаг-вид проб сцены за галочкой Probe spheres и жизнью атласов - зеркало
		/// ModelPreviewViewport.PollProbeDebugOverlay для единственного объёма сцены.</summary>
		private void PollSceneProbeDebugOverlay() =>
			ProbeGiViewportShared.PollOverlays(_sceneDebugOverlays,
				ProbesEnabled && _editorSettings.ProbeGiShowProbes && _sceneGpu != null,
				ref _sceneDebugFailed, _env, _graphicsApi, _probeSession, _probeTextures);

		private void ReleaseSceneProbeDebugOverlay() =>
			ProbeGiViewportShared.ReleaseOverlays(_sceneDebugOverlays, _env);

		/// <summary>Освобождает GPU-путь сцены за барьером (конвейеры переживают - их компиляция
		/// дорогая, а от сессии они не зависят).</summary>
		private void ReleaseSceneProbeGpu()
		{
			if (_sceneGpu == null && _sceneAccel == null)
			{
				return;
			}

			// Трейс не должен держать view умирающего атласа текстур хитов (та же дисциплина,
			// что у probe-атласов выше).
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

			// RT-вариант SSR-трейса держал дескриптор на только что уничтоженный TLAS - откат на
			// экранный вариант (SsrRayTracedEnabled без accel-а даёт false, ресурсы пересоберутся).
			if (hadAccel && _editorSettings.SsrRayTraced)
			{
				ApplyPipelineFeatures();
			}
		}

		/// <summary>Ведёт TLAS за позами сущностей - сердце динамики сцены: гизмо двигает запись,
		/// TLAS пересобирается из живых LastWorld, поле перетекает само. Сессия при этом НЕ
		/// пересоздаётся - ни ребейка BVH на главном потоке, ни потери накопленного.</summary>
		private void PollSceneProbePoses()
		{
			var session = _probeSession;
			var baker = _probeBaker;
			if (!_sceneTlasDirty || session == null || baker == null
				|| _sceneGpu == null || _sceneAccel == null)
			{
				return;
			}

			// Только на границе раунда - иначе половина проб отследит старую сцену, половина новую
			// (см. ProbeRoundGpu.AtRoundStart).
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

				// Движение объекта БОЛЬШЕ НЕ трогает ни релокацию, ни вес раунда. Здесь стоял
				// ReopenRelocation(), и он делал две ГЛОБАЛЬНЫЕ вещи на каждый кадр драга гизмо:
				// открывал окно релокации у ВСЕЙ сетки и откатывал Round, отчего вес раунда
				// прыгал с MinBlend (~0.05) до 0.5 - вдесятеро, тоже у всей сетки разом. Пока объект
				// тащат, это повторялось каждый кадр, то есть всё время драга поле шло ПРАКТИЧЕСКИ
				// НЕФИЛЬТРОВАННЫМ (видимое кипение), а сон проб был выключен целиком
				// (условие сна требует RelocationRoundsLeft == 0, см. ProbeRoundGpu).
				//
				// Majercik 2021 §5 прямо запрещает двигать пробы вокруг динамики («this causes
				// instability; a stable result is preferable to an unstable result with lower average
				// error») - пробы двигаются только на инициализации (это делает конструктор
				// сессии). Пробу, которую накрыло движущимся объектом, ловят backface-эвристики
				// (§4.1): она просто молчит, пока её накрывают. А за самим светом поле следит
				// и без отката: в реальном времени alpha - это экспоненциальное среднее с постоянной
				// MinBlend, оно отслеживает изменения непрерывно - в этом весь смысл режима.
				//
				// Локальное пробуждение только ближних проб (§6.3: расширенный AABB динамического
				// объекта -> Newly Awake) требует пер-пробных состояний, которых здесь пока нет;
				// глобальный откат был негодным приближением: расшатывал 100% сетки ради единиц
				// процентов проб возле объекта.
				//
				// Запечке же откат нужен, и только он: она останавливается по Converged, и без сброса
				// поле навсегда осталось бы с объектом в старой позе (см. InvalidateGeometry).
				session.InvalidateGeometry();
			}
			catch (Exception ex)
			{
				EngineLog.Add(LogLevel.Error,
					$"Scene probe GI: TLAS rebuild failed, scene frozen for tracing: {ex.Message}");
				_sceneGpuDisabled = true;
			}
		}

		/// <summary>Покадровый привод бейка: забирает завершившийся CPU-раунд в атласы, тикает
		/// дебаунс пересоздания сессии, запускает следующий раунд. Раунды строго по одному - сессия
		/// не потокобезопасна, и всё, что её трогает (свет), делается пока фоновая задача не бежит.</summary>
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

			// Фоновая сборка BVH завершилась - принимаем результат и заводим сессию.
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

					// Снимок поз, под который собрано дерево: по нему BeginProbeSession поймёт, что
					// сцену снова подвинули и BVH пора пересобирать.
					_probeBakerBuiltFor = builtFor;

					// Записи - СТРОГО вместе с бейкером: их порядок и есть SourceModel его инстансов,
					// по нему PollSceneProbePoses тянет живые позы в TLAS.
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

			// Живые ручки реального времени и свет - перед каждым раундом (см. ModelPreviewViewport).
			session.Realtime = _editorSettings.ProbeGiRealtime && _sceneGpu != null;
			session.RealtimeRaysPerRound = Math.Clamp(_editorSettings.ProbeGiRealtimeRays, 8, 1024);
			session.RealtimeBlend = Math.Clamp(_editorSettings.ProbeGiRealtimeBlend, 0.01f, 0.5f);
			session.RealtimeMaxStep = Math.Clamp(_editorSettings.ProbeGiRealtimeMaxStep, 0f, 0.2f);
			session.RealtimeGamma = Math.Clamp(_editorSettings.ProbeGiRealtimeGamma, 1f, 8f);
			// Порог сходимости - без этой строки ползунок «Порог сходимости» действовал только при
			// пересоздании сессии, хотя помечен Live (значение из BuildOptionsCore застывало).
			session.VariabilityThreshold = MathF.Max(_editorSettings.ProbeGiVariabilityThreshold, 0f);
			// Релокация проб - такая же live-ручка, как соседние, но её здесь не было: в Scene View
			// ручка «Relocation» окна Graphics не делала НИЧЕГО, а пробы, замурованные в стенах,
			// оставались замурованными (в превью слот есть, см. ModelPreviewViewport.PollProbeBake).
			session.RealtimeRelocation = Math.Clamp(_editorSettings.ProbeGiRealtimeRelocation, 0f, 0.45f);

			PollSceneProbePoses();

			// Свет подтягивается перед каждым раундом: поворот солнца откатывает сходимость, и поле
			// само перетекает к новому решению, не выбрасывая накопленное (см.
			// ProbeGiBakeSession.SetLighting).
			if (_env.ShadowSettings != null)
			{
				session.SetLighting(Vector3.Normalize(-_env.ShadowSettings.LightDirection),
					ProbeSunColor(), _env.ShadowSettings.EnvYawRadians, _env.EnvironmentRadiance);
			}

			// Punctual-света сцены - в бейк той же механикой: движение/правка лампы реально меняет
			// список (сравнение внутри SetPunctualLights) и откатывает вес раунда. Зеркала уже несут
			// МИРОВЫЕ Position/Rotation (см. SyncEntity).
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

			// GPU-раунды: запись команд на потоке рендера, атласы пишет шейдер - ни фоновой задачи,
			// ни снимка (см. ModelPreviewViewport.PollProbeBake, тот же привод).
			if (_sceneGpu != null)
			{
				if (!_sceneGpu.IsReady)
				{
					return;
				}

				try
				{
					// Общий цикл порций (см. ProbeGiViewportShared.DriveChunks): бюджет тратится
					// целиком, переходя границы раундов. Объём один - бюджет лучей кадра весь его.
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

			// CPU-привода больше нет (см. ModelPreviewViewport.PollProbeBake): не поднялся GPU -
			// пробы сцены стоят, консоль объяснила почему.
		}

		/// <summary>Снимок поля сессии в GPU-атласы: создаются один раз на сетку и обновляются НА
		/// МЕСТЕ (см. ProbeGiTextures.Update); смена сетки пересоздаёт их за GPU-барьером.</summary>
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

		/// <summary>Привязывает атласы проб к материалам ВСЕХ загруженных моделей (в отличие от
		/// превью с одной резидентной) - см. ProbeGiTextures.Bind.</summary>
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
					_probeTextures.Bind(state.Model);
				}
			}
		}

		/// <summary>Сброс probe-GI (смена сцены/пересоздание окружения/выключение галочки). Звать ЗА
		/// GPU-барьером - освобождает атласы. Недосчитавшийся фоновый раунд осиротевает вместе со
		/// своей сессией: чистый CPU-таск, его результат просто некому забрать.</summary>
		/// <summary>Дожидается фоновой сборки BVH. ОБЯЗАТЕЛЬНО перед освобождением любой модели
		/// сцены: задача читает CPU-копии вершин в неуправляемой памяти, и Release из-под неё - это
		/// обращение к освобождённой памяти.</summary>
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
				// Причина уже будет доложена из PollProbeBake - здесь нужен только факт завершения.
			}

			_probeBakerTask = null;
			_probeBakerModels = null;
			_probeBakerRecords = null;
		}

		private void ResetProbeGi()
		{
			// Дерево уходит вместе с бейкером - снимок поз, под который оно собрано, тоже
			// недействителен (иначе следующая сборка сочла бы сцену неизменной и не состоялась).
			_probeBakerBuiltFor = null;
			ResetProbeGiCore();
		}

		private void ResetProbeGiCore()
		{
			// До всего: фоновая сборка BVH ещё может читать геометрию моделей, которые вызывающий
			// вот-вот освободит.
			WaitProbeBakerTask();

			// Оверлей первым (держит атласы в замороженных командах), затем GPU-объект.
			ReleaseSceneProbeDebugOverlay();
			ReleaseSceneProbeGpu();
			_sceneTlasDirty = false;
			_probeSceneRecords.Clear();

			_probeBaker = null;
			_probeSession = null;
			_probeRoundTask = null;
			_probeSessionDelay = -1f;

			if (_probeTextures != null)
			{
				_probeTextures.Release();
				_probeTextures = null;
			}
		}

		// --- Баунды/тени/пост-процесс ---------------------------------------------------------------

	}
}
