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
	/// <summary>Probe GI превью: сессия бейка, GPU-путь, ускоряющая структура, снапшоты. Часть <see cref="ModelPreviewViewport"/> - файл на тему;
	/// состояние, конструктор и кадровые Update/Render живут в основном файле.</summary>
	public partial class ModelPreviewViewport
	{
		/// <summary>Принудительный ребейк probe-GI (кнопка Rebake в окне Graphics) - заводит сессию
		/// заново, с нуля.</summary>
		public void RequestProbeRebake()
		{
			RequestProbeSession(delaySeconds: 0f);
		}

		/// <summary>Просит пересоздать сессию probe-GI (новая модель либо изменившиеся параметры
		/// сетки/качества). Дебаунс - потому что новая сессия выбрасывает всё накопленное поле, а
		/// ползунки окна Graphics шлют изменение каждый кадр драга.</summary>
		private void RequestProbeSession(float delaySeconds = ProbeRebakeDebounceSeconds)
		{
			if (_residentModel != null && _editorSettings.PreviewProbeGi)
			{
				_probeSessionDelay = delaySeconds;
			}
		}

		/// <summary>Заводит сессию прогрессивного бейка probe-GI по резидентной модели. BVH строится в
		/// ФОНЕ (см. _probeBakerTask): на сцене уровня Sponza это ~7.7 млн треугольников и
		/// ДЕСЯТКИ СЕКУНД - именно он держал редактор в "Not Responding" ПОСЛЕ того, как модель уже
		/// загрузилась. Сама сессия - только раскладка сетки и аллокации: лучей тут не пускается ни
		/// одного, трассировка идёт раундами из PollProbeBake. Требует мировой свет (ShadowSettings) -
		/// без него направление солнца неизвестно, остаёмся на старом константном ambient-е.</summary>
		private void BeginProbeSession()
		{
			if (_residentModel == null || _env.ShadowSettings == null || !_editorSettings.PreviewProbeGi)
			{
				return;
			}

			if (_probeBaker == null)
			{
				// Задача уже считает - дождёмся её из PollProbeBake, сессия заведётся следующим тиком.
				if (_probeBakerTask != null)
				{
					return;
				}

				var model = _residentModel;
				var modelPath = _residentPath;
				_probeBakerModel = model;
				_probeBakerSw = System.Diagnostics.Stopwatch.StartNew();

				// Чистый CPU по CPU-копиям вершин модели: пока модель жива, читать их из другого
				// потока безопасно. Освобождение модели обязано сперва дождаться этой задачи -
				// см. WaitProbeBakerTask (её зовут все точки, ведущие к ModelLoader.Release).
				//
				// LoadOrBuild сперва пробует кеш <модель>.bhv.bin рядом с файлом: сборка дерева не
				// зависит от настроек и на тяжёлом ассете стоит десятки секунд, а геометрия между
				// запусками не меняется.
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

			// Сессия пересоздаётся - GPU-путь прошлой сессии и его атласы обязаны уйти ДО того, как
			// появятся новые. BeginProbeSession зовётся на каждое изменение настроек (с дебаунсом),
			// так что без этого каждая правка ползунка утекала бы полным комплектом буферов, а
			// устаревшие SRB продолжали бы держать ресурсы - до рассинхрона и падения драйвера.
			ReleaseProbeGpuAndAtlases();

			// LightDirection указывает ОТ солнца (направление света), бейкер ждёт направление НА солнце.
			_probeSession = _probeBaker.BeginBake(_probeBoundsMin, _probeBoundsMax,
				Vector3.Normalize(-_env.ShadowSettings.LightDirection), ProbeSunColor(),
				_env.ShadowSettings.EnvYawRadians, _env.EnvironmentRadiance, options);

			// GPU - единственный привод раундов: CPU-раунд остался только эталоном сверки в CLI
			// (см. SceneTraceVerifier). Не поднялся - пробы выключены, о чём скажет статус; молча
			// уползать на путь в тысячу раз медленнее и без динамики было бы хуже отказа.
			if (!_probeGpuDisabled)
			{
				TryBeginProbeGpu(_probeSession);
			}
		}

		/// <summary>Настройки бейка из окна Graphics - общие для базового объёма и каскадов
		/// (каскад отличается только баундами, см. TryBeginProbeGpu).</summary>
		private ProbeGiBakeOptions BuildProbeOptions() =>
			ProbeGiViewportShared.BuildOptions(_editorSettings);

		/// <summary>Поднимает GPU-путь раунда для сессии: выгружает BVH в буферы, заводит атласы с
		/// UAV и привязывает их к материалам. Любая осечка (шейдер не собрался, формат не тянет UAV)
		/// - это откат на CPU-путь, а не падение редактора: probe GI не та фича, ради которой стоит
		/// ронять превью.</summary>
		private void TryBeginProbeGpu(ProbeGiBakeSession session)
		{
			try
			{
				// Захват поверхностей нужен уже при создании буферов - на CPU его строит первый
				// раунд, которого здесь не будет.
				// Только запечке - в реальном времени кэш не читается, а захват стопорит кадры.
				if (!_editorSettings.ProbeGiRealtime)
				{
					_probeBaker!.EnsureSurfaceCache(session);
				}

				// Атласы под запись из шейдера. Создаются здесь, а не после первого раунда: их
				// содержимое теперь появляется на GPU, забирать с CPU нечего.
				_probeTextures = new ProbeGiTextures(_graphicsApi, session.Result,
					$"_probeGi{_probeTextureGeneration++}", gpuWritable: true);
				_probeTextures.Bind(_residentModel!);
				ApplyPreviewSettingsToMaterials();

				// Аппаратная трассировка только если её и просят, и устройство её умеет. Структуры
				// строятся из мировых треугольников бейкера (см. ProbeSceneAccel).
				bool hardware = _editorSettings.ProbeGiHardwareRayTracing && RayTracingSupported;
				if (!hardware && _probeAccel != null)
				{
					// Аппаратную трассировку выключили - структуры больше не нужны.
					_env.Pipeline.SsrResources?.SetHitTextures(null, null);
					_probeAccel.Dispose();
					_probeAccel = null;
					_probeAccelHitTextures?.Dispose();
					_probeAccelHitTextures = null;
				}

				// Строятся ОДИН РАЗ на модель: геометрия от настроек probe-GI не зависит (см. поле).
				// Движение объектов их тоже не пересоздаёт - на него отвечает пересборка TLAS в
				// PollProbeAccel.
				if (_probeAccel == null && hardware)
				{
					_probeAccel = new ProbeSceneAccel(_env.DilApi, _probeBaker.InstancedGeometry);

					// Набор текстур RT-хитов - вместе с accel-ом: индексы в его таблице инстансов
					// указывают именно в этот набор (модель та же, под которую собран бейкер;
					// ключи переживают дисковый кеш BVH).
					_probeAccelHitTextures?.Dispose();
					_probeAccelHitTextures = _residentModel != null
						? SsrHitTextures.Build(_graphicsApi, _probeBaker.InstancedGeometry,
							new[] { _residentModel })
						: null;
				}

				_probeInstancePoses.Clear();

				// Конвейеры компилируются один раз и переживают пересоздание сессии. Пересобираем,
				// если сменилось устройство (иначе PSO остались бы от мёртвого девайса) или режим
				// трассировки - он задан кейвордом на этапе компиляции.
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

				// RT-фолбэк SSR питается этим же accel-ом (см. PrefabSceneViewport - та же связка).
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

		/// <summary>Освобождает GPU-путь вместе с атласами, за барьером. Ресурсы мог читать ещё
		/// находящийся в полёте кадр - тот же Flush+WaitForIdle, что при любом освобождении
		/// GPU-ресурсов здесь (см. PollPendingLoad).</summary>
		private void ReleaseProbeGpuAndAtlases()
		{
			if (_probeGpu == null && _probeTextures == null)
			{
				return;
			}

			// Первым: замороженные команды графа рисуют оверлей по этим атласам - освободить их под
			// живыми командами значит оставить дроу с мёртвыми дескрипторами. SSR-трейс тоже держит
			// SH-атласы (свет RT-хитов) - возвращаем его слоты на плейсхолдер.
			ReleaseProbeDebugOverlay();
			_env.SetSsrProbeField(null);

			_env.DilApi.ImmediateContext.Flush();
			_env.DilApi.ImmediateContext.WaitForIdle();

			ReleaseProbeGpu();
			_probeTextures?.Release();
			_probeTextures = null;
		}

		/// <summary>Освобождает GPU-путь раунда, но НЕ структуры ускорения: те живут по модели и
		/// переживают пересоздание сессии (см. _probeAccel). Звать РАНЬШЕ освобождения атласов:
		/// раунды держат на них представления, и обратный порядок роняет драйвер.</summary>
		private void ReleaseProbeGpu()
		{
			_probeGpu?.Dispose();
			_probeGpu = null;
		}

		/// <summary>Покадровый привод probe-GI: забирает завершившийся раунд в атласы, тикает дебаунс
		/// пересоздания сессии и запускает следующий раунд. Раунды идут строго по одному - сессия не
		/// потокобезопасна, и всё, что её трогает (снимок, обновление света), делается здесь, пока
		/// фоновая задача не бежит.</summary>
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
					// Раунд упал - сессия могла остаться в полуобновлённом состоянии, дальше её не
					// крутим. Уже показанные пробы остаются на экране.
					_probeSession = null;
					_probeStatus = "ошибка бейка";
					EngineLog.Add(LogLevel.Error, "Probe GI: bake round failed: " +
						(finished.Exception?.GetBaseException().Message ?? "Unknown error"));
				}
			}

			// Фоновая сборка BVH завершилась - принимаем её и заводим сессию (BeginProbeSession
			// увидит готовый _probeBaker и пойдёт дальше по обычному пути).
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
				// Иначе модель успели сменить - результат относится к уже мёртвой геометрии.
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

			// Режим подтягивается в живую сессию, как и свет: это пол веса раунда, а не параметр
			// раскладки, - пересоздавать сессию (и выбрасывать поле) ради галочки незачем.
			session.Realtime = _editorSettings.ProbeGiRealtime;
			session.RealtimeRaysPerRound = Math.Clamp(_editorSettings.ProbeGiRealtimeRays, 8, 1024);
			session.RealtimeBlend = Math.Clamp(_editorSettings.ProbeGiRealtimeBlend, 0.01f, 0.5f);
			session.RealtimeMaxStep = Math.Clamp(_editorSettings.ProbeGiRealtimeMaxStep, 0f, 0.2f);
			session.RealtimeGamma = Math.Clamp(_editorSettings.ProbeGiRealtimeGamma, 1f, 8f);
			session.RealtimeRelocation = Math.Clamp(_editorSettings.ProbeGiRealtimeRelocation, 0f, 0.45f);
			// Порог сходимости - та же живая ручка, что и соседние (см. PrefabSceneViewport: без
			// этой строки ползунок действовал только при пересоздании сессии).
			session.VariabilityThreshold = MathF.Max(_editorSettings.ProbeGiVariabilityThreshold, 0f);

			PollProbeAccel(session);

			// Свет подтягивается перед каждым раундом: повернули солнце или подвинули его
			// интенсивность - сессия откатит сходимость и дотечёт до нового решения сама, не
			// выбрасывая ни геометрию, ни поле как стартовое приближение.
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
				// Предыдущий РАУНД ещё считается - ждём. Забор стоит на раунде, а не на порции:
				// иначе каждая порция ждала бы завершения предыдущей, и при 1.7 мс счёта мы теряли
				// бы целый кадр на ожидание (см. ProbeRoundGpu.SignalRound).
				if (!_probeGpu.IsReady)
				{
					return;
				}

				// GPU-раунд - это запись команд, а не работа: в фоновый Task его уносить незачем
				// (и нельзя - контекст принадлежит потоку рендера). Атласы шейдер пишет сам, так
				// что ни снимка, ни заливки после раунда не нужно.
				try
				{
					// Порции и раунды - общим циклом (см. ProbeGiViewportShared.DriveChunks): бюджет
					// тратится целиком, переходя границы раундов, а не обрывается на первом же.
					// Объём один - весь лучевой бюджет кадра его.
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

			// CPU-привода больше нет: раунды крутит только GPU, CPU-раунд остался эталоном сверки
			// в CLI. _probeGpu == null здесь значит «GPU-путь не поднялся» - пробы стоят.
		}

		/// <summary>Ведёт структуры ускорения за движущейся сценой: если хоть один инстанс сменил
		/// позу, TLAS пересобирается под новые матрицы. Это и есть весь механизм динамической
		/// геометрии - BLAS-ы не трогаются, они в объектном пространстве (см. ProbeSceneAccel), а
		/// поле само перетечёт к новому решению за единицы раундов, потому что в реальном времени
		/// оно ни к чему не сходится (см. ProbeGiBakeOptions.Realtime).
		///
		/// Позы читаются из модели, а не из ECS: бейкер собирал геометрию по ней же, поэтому
		/// нумерация инстансов совпадает гарантированно (см. ProbeGeometryInstance.SourceInstance).
		/// Сцена стоит на месте - не делается вообще ничего, включая аллокации.</summary>
		private void PollProbeAccel(ProbeGiBakeSession session)
		{
			var accel = _probeAccel;
			var baker = _probeBaker;
			if (accel == null || baker == null || _residentModel == null || _probeAccelFrozen)
			{
				return;
			}

			// Только на границе раунда: пересборка под уже выпущенными порциями записала бы в поле
			// шов из половины старой сцены и половины новой (см. ProbeRoundGpu.AtRoundStart).
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
				// Структуры НЕ освобождаются: их держат уже записанные привязки GPU-раунда, и
				// освобождение здесь оставило бы шейдеру висячий дескриптор. Сцена для трассировки
				// просто замирает в последней удавшейся позе - пробы остаются валидными, теряется
				// только слежение за движением.
				_probeAccelFrozen = true;
				EngineLog.Add(LogLevel.Error,
					$"Probe GI: TLAS rebuild failed, scene frozen for tracing: {ex.Message}");
			}
		}

		/// <summary>Ведёт дебаг-вид проб за галочкой и жизнью атласов. Создание/снятие пересобирает
		/// рендер-граф (команды заморожены, см. GraphicsPipelineSimple.InlineOverlay), поэтому
		/// сравнение «хочу/есть» обязано быть дешёвым - оно и есть пара сравнений ссылок.</summary>
		private void PollProbeDebugOverlay() =>
			ProbeGiViewportShared.PollOverlays(_probeDebugOverlays,
				_editorSettings.PreviewProbeGi && _editorSettings.ProbeGiShowProbes,
				ref _probeDebugFailed, _env, _graphicsApi, _probeSession, _probeTextures);

		private void ReleaseProbeDebugOverlay() =>
			ProbeGiViewportShared.ReleaseOverlays(_probeDebugOverlays, _env);

		/// <summary>Забирает снимок текущего поля сессии в GPU-атласы: создаёт их один раз на сетку и
		/// дальше обновляет НА МЕСТЕ (см. ProbeGiTextures.Update) - пересоздавать пять текстур,
		/// переприязывать их к материалам и ставить Flush+WaitForIdle каждый раунд было бы дороже
		/// самого раунда.</summary>
		private void UploadProbeSnapshot()
		{
			var session = _probeSession;
			// Модель выгрузили/окружение пересоздали, пока раунд шёл (см. ResetProbeGi) - результат
			// уже не про текущую сцену.
			if (session == null || _probeBaker == null || _residentModel == null)
			{
				return;
			}

			try
			{
				var snapshot = _probeBaker.Snapshot(session);

				// Сетка сменилась (новая сессия) - атласы под неё не подходят. Старые мог читать ещё
				// находящийся в полёте кадр, поэтому тот же Flush+WaitForIdle барьер, что при любом
				// освобождении GPU-ресурсов здесь (см. PollPendingLoad).
				if (_probeTextures != null && !_probeTextures.Matches(snapshot))
				{
					// Оверлей держит эти атласы в замороженных командах графа - снять до Release.
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
					_probeTextures.Bind(_residentModel);
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

		/// <summary>Сбрасывает состояние probe-GI при смене модели/пересоздании окружения. Звать
		/// ПОСЛЕ Flush+WaitForIdle (обе точки вызова уже за барьером) - освобождает GPU-атласы.
		/// Недосчитавшийся фоновый раунд не отменяется, а осиротевает вместе со своей сессией:
		/// чистый CPU-таск, его результат просто некому забрать.</summary>
		/// <summary>Дожидается фоновой сборки BVH. ОБЯЗАТЕЛЬНО перед любым освобождением модели:
		/// задача читает CPU-копии вершин в неуправляемой памяти (IMeshObject), и ModelLoader.Release
		/// из-под неё - это чтение освобождённой памяти, а не безобидная гонка.</summary>
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
				// Ошибка сборки уже будет доложена в PollProbeBake - здесь важен только сам факт,
				// что поток закончил читать геометрию.
			}

			_probeBakerTask = null;
			_probeBakerModel = null;
		}

		private void ResetProbeGi()
		{
			// До всего остального: фоновая сборка BVH ещё может читать геометрию модели, которую
			// вызывающий вот-вот освободит.
			WaitProbeBakerTask();

			// Первым: оверлей держит атласы в материале, а его снятие включает барьер Flush+WaitForIdle.
			ReleaseProbeDebugOverlay();

			// Раньше атласов: GPU-объект держит на них представления (см. ReleaseProbeGpu).
			ReleaseProbeGpu();

			// Структуры ускорения привязаны к геометрии модели - уходят вместе с бейкером.
			bool hadProbeAccel = _probeAccel != null;
			if (hadProbeAccel)
			{
				// Трейс не должен держать view умирающего атласа текстур хитов.
				_env.Pipeline.SsrResources?.SetHitTextures(null, null);
			}
			_probeAccel?.Dispose();
			_probeAccel = null;
			_probeAccelHitTextures?.Dispose();
			_probeAccelHitTextures = null;

			// RT-вариант SSR-трейса держал дескриптор на уничтоженный TLAS - откат на экранный
			// вариант (SsrRayTracedEnabled без accel-а даёт false).
			if (hadProbeAccel && _editorSettings.SsrRayTraced)
			{
				ApplyPipelineFeatures();
			}
			_probeBaker = null;
			_probeSession = null;
			_probeRoundTask = null;
			_probeSessionDelay = -1f;
			_probeSessionOptions = default;
			_probeStatus = "нет проб";

			if (_probeTextures != null)
			{
				_probeTextures.Release();
				_probeTextures = null;
			}
		}

	}
}
