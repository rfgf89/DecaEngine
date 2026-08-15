using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using DecaEngine.Core;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Core;
using DecaEngine.Graphics.Diligent;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;

namespace DecaEngine.Editor.ECS
{
	/// <summary>
	/// Стриминг моделей (<see cref="ModelLoader"/>) для одного офскрин-окружения
	/// (<see cref="ModelViewportEnvironment"/>): резидентный кеш по пути файла со счётчиком ссылок,
	/// очередь фоновых загрузок с приоритетом ПО РАССТОЯНИЮ ДО КАМЕРЫ и покадровая бюджетная
	/// финализация GPU-ресурсов (см. ModelLoader.FinalizeChunk - дисциплина upload-хипа).
	///
	/// Владеет жизненным циклом ModelLoader-ов целиком: регистрирует готовую модель в батч-рендерере
	/// окружения, а при выселении/очистке освобождает её по обязательному протоколу этого движка -
	/// дождаться GPU -> сбросить регистрации -> Release -> пересобрать граф (см. комментарии в
	/// ModelPreviewViewport.PopulateFromScene: мега-буферы батч-рендерера пересоздаются целиком, а
	/// замороженные команды графа держат ссылки на старые буферы).
	///
	/// Кадровый привод - <see cref="ModelStreamingSystem"/> в SystemRoot окружения: она каждый кадр
	/// читает позицию камеры из ECS-стора и зовёт <see cref="Tick"/>. Потребители (превью, префабы)
	/// берут модель через <see cref="Acquire"/>/<see cref="Release"/> и следят за
	/// <see cref="Resident.Ready"/>.
	/// </summary>
	public sealed class ModelStreamer
	{
		/// <summary>Резидентная (или грузящаяся) модель одного файла. Те же поля-словари, что были у
		/// локальных кешей вьюпортов (MeshIds/MaterialIds/BatchCache), - регистрации в батч-рендерере
		/// ТЕКУЩЕГО окружения; при любом сбросе регистраций пересоздаются стримером.</summary>
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
			internal float IdleSeconds;
			internal ModelLoader.ModelLoadRequest? Request;
			internal CancellationTokenSource? Cts;

			/// <summary>FinalizeChunk уже создал часть GPU-ресурсов - отмена на этом этапе бросает их
			/// недостроенными (у ModelLoadRequest нет отката), поэтому начатую финализацию доводим до
			/// конца даже для уже никому не нужной модели, а выселяем её обычным путём.</summary>
			internal bool Finalizing;

			public bool Ready => Model != null;
			public bool Failed => Error != null;

			internal Resident(string path)
			{
				Path = path;
			}
		}

		private readonly Dictionary<string, Resident> _models = new(StringComparer.OrdinalIgnoreCase);
		private readonly IGraphicsApi _graphicsApi;
		private readonly Func<ModelLoadOptions> _optionsFactory;
		private ModelViewportEnvironment _env;
		private Vector3 _cameraPos;

		// Скретчи Tick-а - без аллокаций на кадр.
		private readonly List<Resident> _startScratch = new();
		private readonly List<Resident> _evictScratch = new();

		/// <summary>Фоновый ре-декод одной ступени качества текстуры (см. ModelLoader.StreamedTextures).
		/// Одна задача за раз: декод идёт в полном разрешении файла (пик ~64 МБ на 4K), а заливка -
		/// одна текстура за Tick, чтобы не раздувать upload-хип.</summary>
		private sealed class TextureUpgradeJob
		{
			public required Resident Resident;
			public required ModelLoader Model;
			public required ModelLoader.StreamedTexture Entry;
			public required int RequestedSize;
			public required Task<(byte[] Pixels, int Width, int Height)> DecodeTask;
		}

		private TextureUpgradeJob? _textureJob;

		/// <summary>Заменённые апгрейдами GPU-текстуры, ждущие отложенного Release: у движка нет
		/// fence кадров в полёте, а WaitForIdle на каждую замену стопорил бы рендер - вместо этого
		/// текстура низкой ступени живёт ещё несколько тиков после того, как все SRB перестали на
		/// неё ссылаться.</summary>
		private readonly List<(IGpuTexture Texture, int TicksLeft)> _retiredTextures = new();
		private const int RetireTicks = 8;

		/// <summary>Множитель ступени качества текстур: 64 -> 256 -> 1024 -> целевой размер.</summary>
		public int TextureStepFactor { get; set; } = 4;

		/// <summary>Первая (и минимальная) ступень качества.</summary>
		public int InitialTextureSize { get; set; } = 64;

		/// <summary>Дистанция, ближе которой объекту достаётся полное качество текстур; дальше
		/// потолок падает вдвое на каждое удвоение расстояния (см. TargetSizeForDistance).</summary>
		public float FullQualityDistance { get; set; } = 12f;

		/// <summary>Потолок памяти под стримленные текстуры. Достигнут - апгрейды останавливаются:
		/// сцена уровня Sponza (сотни текстур) на полном качестве весит десятки гигабайт RGBA с
		/// мипами, и без потолка редактор просто уходил в своп.</summary>
		public long TextureMemoryBudgetBytes { get; set; } = 1024L << 20;

		private long _textureBytes;
		private bool _budgetReported;

		/// <summary>Сколько фоновых Prepare-задач держать в полёте одновременно. Каждая - это
		/// параллельный декод текстур с пиковыми полноразмерными RGBA-копиями (см.
		/// ModelLoader.PrepareModel), так что больше двух заметно раздувает пиковую память.</summary>
		public int MaxConcurrentLoads { get; set; } = 2;

		/// <summary>Сколько секунд модель с нулём ссылок остаётся резидентной, прежде чем быть
		/// выселенной с GPU. Буфер против дребезга: камера у границы радиуса стриминга то отпускает,
		/// то тут же снова берёт одну и ту же модель.</summary>
		public float UnloadAfterSeconds { get; set; } = 4f;

		/// <summary>Радиус стриминга от камеры (мировые единицы): дальше него потребители через
		/// <see cref="ShouldBeResident"/> отпускают модель, ближе - берут. Бесконечность = грузить
		/// всё независимо от камеры (камера тогда влияет только на ПОРЯДОК загрузки).</summary>
		public float StreamRadius { get; set; } = float.PositiveInfinity;

		/// <summary>Гистерезис выгрузки: уже резидентная модель отпускается только за
		/// <see cref="StreamRadius"/> * этот множитель - иначе на самой границе радиуса она бы
		/// грузилась и выгружалась каждый шаг камеры.</summary>
		public const float StreamOutHysteresis = 1.15f;

		/// <summary>Модель догрузилась и зарегистрирована в батч-рендерере - можно инстанцировать.</summary>
		public event Action<Resident>? ModelReady;

		/// <summary>Сейчас будут сброшены ВСЕ регистрации батч-рендерера (выселение/очистка):
		/// подписчик обязан снять свои инстанс-сущности (UnregisterRenderable + DeleteEntity),
		/// пока BatchId-ы ещё валидны.</summary>
		public event Action? ResidencyResetting;

		/// <summary>Сброс завершён, выжившие модели перерегистрированы (их словари Id перезаполнены) -
		/// подписчик может инстанцировать заново.</summary>
		public event Action? ResidencyReset;

		public ModelStreamer(ModelViewportEnvironment env, IGraphicsApi graphicsApi,
			Func<ModelLoadOptions> optionsFactory)
		{
			_env = env;
			_graphicsApi = graphicsApi;
			_optionsFactory = optionsFactory;
		}

		/// <summary>Резидентный кеш на чтение - вьюпортам для обхода загруженных моделей
		/// (материалы, probe-GI и т.п.). Не мутировать.</summary>
		public IReadOnlyDictionary<string, Resident> Models => _models;

		/// <summary>Есть ли незавершённая работа (фоновые Prepare или недофинализированные модели).</summary>
		public bool HasPendingLoads
		{
			get
			{
				foreach (var resident in _models.Values)
				{
					if (resident.Request != null)
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

		/// <summary>Берёт ссылку на модель файла (загрузка стартует из Tick по приоритету камеры).
		/// Каждому Acquire обязан соответствовать <see cref="Release"/>.</summary>
		public Resident Acquire(string path, Vector3 anchor)
		{
			if (!_models.TryGetValue(path, out var resident))
			{
				resident = new Resident(path) { Anchor = anchor };
				_models[path] = resident;

				var extension = System.IO.Path.GetExtension(path);
				if (!string.Equals(extension, ".gltf", StringComparison.OrdinalIgnoreCase) &&
					!string.Equals(extension, ".glb", StringComparison.OrdinalIgnoreCase))
				{
					resident.Error = $"Unsupported model format: {extension}";
					EditorConsoleLog.Add(LogLevel.Warning, $"Model streaming: {resident.Error} ('{path}')");
				}
			}

			resident.RefCount++;
			resident.IdleSeconds = 0f;
			resident.Anchor = anchor;
			return resident;
		}

		/// <summary>Отпускает ссылку. Модель НЕ выгружается немедленно - остаётся резидентной ещё
		/// <see cref="UnloadAfterSeconds"/> (кеш против дребезга), незапущенная загрузка снимается с
		/// очереди, а запущенная отменяется следующим Tick-ом.</summary>
		public void Release(Resident resident)
		{
			resident.RefCount = Math.Max(0, resident.RefCount - 1);
		}

		/// <summary>
		/// Кадровый шаг (главный поток, под GPU-локом): запуск очередных загрузок ближайших к камере
		/// моделей, опрос фоновых задач, финализация ОДНОЙ модели за кадр (ближайшей из готовых) и
		/// выселение простаивающих. Зовётся <see cref="ModelStreamingSystem"/> из SystemRoot окружения.
		/// </summary>
		public void Tick(float deltaTime, Vector3 cameraPos)
		{
			_cameraPos = cameraPos;

			StartQueuedLoads();
			PollLoads();
			PumpTextureUpgrades();
			AgeRetiredTextures();
			EvictIdle(deltaTime);
		}

		private float DistanceToCamera(Resident resident) => Vector3.Distance(resident.Anchor, _cameraPos);

		private void StartQueuedLoads()
		{
			var inFlight = 0;
			_startScratch.Clear();
			foreach (var resident in _models.Values)
			{
				if (resident.Request != null)
				{
					inFlight++;
				}
				else if (resident.Model == null && resident.Error == null && resident.RefCount > 0)
				{
					_startScratch.Add(resident);
				}
			}

			if (_startScratch.Count == 0 || inFlight >= MaxConcurrentLoads)
			{
				return;
			}

			// Ближайшие к камере - первыми: это и есть "стриминг от камеры", а не FIFO по порядку
			// обхода префаба.
			_startScratch.Sort((a, b) => DistanceToCamera(a).CompareTo(DistanceToCamera(b)));

			foreach (var resident in _startScratch)
			{
				if (inFlight >= MaxConcurrentLoads)
				{
					break;
				}

				try
				{
					resident.Cts = new CancellationTokenSource();
					resident.Request = ModelLoader.BeginLoadAsync(_graphicsApi, resident.Path, _optionsFactory(),
						cancellationToken: resident.Cts.Token);
					inFlight++;
				}
				catch (Exception ex)
				{
					resident.Error = ex.Message;
					FinishRequest(resident);
					EditorConsoleLog.Add(LogLevel.Error,
						$"Model streaming: failed to load '{resident.Path}': {ex.Message}");
				}
			}
		}

		private void PollLoads()
		{
			// Кандидат на финализацию этого кадра - ближайшая к камере модель с готовым Prepare.
			// Финализируем ОДНУ за кадр: FinalizeChunk и так порционный, но несколько моделей разом
			// суммировали бы свои бюджеты в один upload-хип.
			Resident? best = null;
			var bestDistance = float.MaxValue;
			List<Resident>? abandoned = null;

			foreach (var resident in _models.Values)
			{
				if (resident.Request == null)
				{
					continue;
				}

				// Модель разлюбили, пока она грузилась (камера ушла / префаб сменился до ClearAll) -
				// глушим фоновый декод. Начатую финализацию не бросаем (см. Resident.Finalizing).
				// Удаление из словаря - после цикла: словарь нельзя мутировать во время обхода.
				if (resident.RefCount <= 0 && !resident.Finalizing)
				{
					resident.Cts?.Cancel();
					FinishRequest(resident);
					(abandoned ??= new List<Resident>()).Add(resident);
					continue;
				}

				if (!resident.Request.PrepareTask.IsCompleted)
				{
					continue;
				}

				if (!resident.Request.PrepareTask.IsCompletedSuccessfully)
				{
					resident.Error = resident.Request.PrepareTask.Exception?.GetBaseException().Message
						?? "Unknown error";
					FinishRequest(resident);
					EditorConsoleLog.Add(LogLevel.Error,
						$"Model streaming: failed to load '{resident.Path}': {resident.Error}");
					continue;
				}

				var distance = DistanceToCamera(resident);
				// Начатая финализация всегда важнее новых: пока она не закончена, её недостроенные
				// GPU-ресурсы висят без владельца.
				if (resident.Finalizing)
				{
					distance = -1f;
				}

				if (distance < bestDistance)
				{
					bestDistance = distance;
					best = resident;
				}
			}

			if (abandoned != null)
			{
				foreach (var resident in abandoned)
				{
					_models.Remove(resident.Path);
				}
			}

			if (best == null)
			{
				return;
			}

			ModelLoader? model;
			try
			{
				best.Finalizing = true;
				model = best.Request!.FinalizeChunk();
			}
			catch (Exception ex)
			{
				best.Error = ex.Message;
				FinishRequest(best);
				EditorConsoleLog.Add(LogLevel.Error,
					$"Model streaming: failed to finalize '{best.Path}': {ex.Message}");
				return;
			}

			if (model == null)
			{
				return; // порция кадра исчерпана - продолжение следующим кадром
			}

			FinishRequest(best);

			try
			{
				RegisterResident(best, model);
				best.Model = model;
				best.IdleSeconds = 0f;

				// Разбивка фаз - без неё «долго грузится» не диагностируется: стоимость фаз на
				// разных ассетах расходится на порядки, и виновник обычно не тот, на кого думаешь.
				var t = model.Timings;
				EditorConsoleLog.Add(LogLevel.Info,
					$"Model load '{System.IO.Path.GetFileName(best.Path)}': " +
					$"parse {t.ParseMs} ms, materials {t.MaterialsMs} ms, meshes {t.MeshesMs} ms, " +
					$"finalize {t.FinalizeMs} ms (shaders {t.ShaderMs} ms / {t.ShaderVariants} variants, " +
					$"PSO+material {t.MaterialBuildMs} ms / {t.MaterialsBuilt}, " +
					$"mesh upload {t.MeshMs} ms / {t.MeshUploads}, samplers {t.SamplerMs} ms / {t.Samplers}); " +
					$"shader compiler total {DecaEngine.Graphics.Diligent.DiligentShader.DiagCompileMs} ms " +
					$"({DecaEngine.Graphics.Diligent.DiligentShader.DiagCompileActual} real / " +
					$"{DecaEngine.Graphics.Diligent.DiligentShader.DiagCompileCalls} calls)");

				ModelReady?.Invoke(best);
			}
			catch (Exception ex)
			{
				best.Error = ex.Message;
				EditorConsoleLog.Add(LogLevel.Error,
					$"Model streaming: failed to register '{best.Path}': {ex.Message}");
			}
		}

		/// <summary>
		/// Прогрессивный стриминг качества текстур: ступени 64 -> 256 -> 1024 -> целевой размер (см.
		/// ModelLoader.StreamedTextures). Декод следующей ступени - асинхронно на пуле потоков;
		/// заливка и горячая замена (SetTexture обновляет SRB живого материала на месте) - здесь, на
		/// GPU-потоке, максимум одна текстура за Tick. Порядок - по расстоянию модели до камеры,
		/// внутри модели - от самой низкокачественной. Заменённая текстура уходит в отложенную
		/// очередь Release (_retiredTextures), CPU-пиксели декода живут только до заливки, а по
		/// достижении целевого/нативного размера освобождается и сжатый исходник.
		/// </summary>
		private void PumpTextureUpgrades()
		{
			if (_textureJob != null)
			{
				var job = _textureJob;
				if (!job.DecodeTask.IsCompleted)
				{
					return;
				}

				_textureJob = null;

				// Модель могла быть выгружена/заменена, пока шёл декод, - результат просто
				// выбрасывается (GPU не трогали, буферы заберёт GC).
				var alive = _models.TryGetValue(job.Resident.Path, out var current) &&
					ReferenceEquals(current, job.Resident) &&
					ReferenceEquals(job.Resident.Model, job.Model);

				if (!job.DecodeTask.IsCompletedSuccessfully)
				{
					if (alive)
					{
						job.Entry.ReleaseCpuData();
						EditorConsoleLog.Add(LogLevel.Warning,
							$"Model streaming: texture upgrade decode failed for '{job.Resident.Path}': " +
							$"{job.DecodeTask.Exception?.GetBaseException().Message}");
					}

					return;
				}

				if (!alive)
				{
					return;
				}

				var (pixels, width, height) = job.DecodeTask.Result;
				var entry = job.Entry;

				if (pixels == null || Math.Max(width, height) <= entry.CurrentSize)
				{
					// Декод не стал крупнее текущего - нативное разрешение достигнуто.
					entry.ReleaseCpuData();
					return;
				}

				try
				{
					var gpuTexture = _graphicsApi.CreateTexture(new CpuTextureData
					{
						Name = $"Stream {width}x{height}",
						DecodedPixels = pixels,
						DecodedWidth = width,
						DecodedHeight = height,
					});

					// Горячая замена: SetTexture обновляет SRB живого материала на месте (см.
					// DiligentMaterial.SetTexture) - ни PSO, ни граф не трогаются.
					foreach (var (material, slot) in entry.Bindings)
					{
						material.SetTexture(slot, gpuTexture);
					}

					if (entry.Texture != null)
					{
						_retiredTextures.Add((entry.Texture, RetireTicks));
						_textureBytes -= EstimateTextureBytes(entry.CurrentSize);
					}

					entry.Texture = gpuTexture;
					entry.CurrentSize = Math.Max(width, height);
					_textureBytes += EstimateTextureBytes(entry.CurrentSize);

					// Целевая ступень или нативное разрешение (декод меньше запрошенного) - конец
					// стриминга этой текстуры, CPU-данные (путь/сжатый исходник) освобождаются.
					if ((entry.TargetSize > 0 && entry.CurrentSize >= entry.TargetSize) ||
						(width < job.RequestedSize && height < job.RequestedSize))
					{
						entry.ReleaseCpuData();
					}
				}
				catch (Exception ex)
				{
					entry.ReleaseCpuData();
					EditorConsoleLog.Add(LogLevel.Warning,
						$"Model streaming: texture upgrade upload failed for '{job.Resident.Path}': {ex.Message}");
				}

				return; // одна заливка за Tick
			}

			// Следующий апгрейд: ближайшая к камере модель, в ней - текстура самой низкой ступени.
			Resident? bestResident = null;
			ModelLoader.StreamedTexture? bestEntry = null;
			var bestTarget = 0;
			var bestDistance = float.MaxValue;
			var bestSize = int.MaxValue;

			foreach (var resident in _models.Values)
			{
				if (resident.Model == null || resident.RefCount <= 0)
				{
					continue;
				}

				var distance = DistanceToCamera(resident);

				foreach (var entry in resident.Model.StreamedTextures)
				{
					if (!entry.HasSource)
					{
						continue;
					}

					// Потолок качества ПО РАССТОЯНИЮ: дальнему объекту полноразмерная текстура
					// физически не видна, а стоит она столько же. Без этого Sponza тянула все свои
					// сотни текстур до 2048 - те самые ~15+ ГБ RGBA с мипами, на которых редактор и
					// вставал.
					var target = TargetSizeForDistance(entry.TargetSize, distance);
					if (entry.CurrentSize >= target)
					{
						continue;
					}

					if (distance < bestDistance || (distance == bestDistance && entry.CurrentSize < bestSize))
					{
						bestResident = resident;
						bestEntry = entry;
						bestTarget = target;
						bestDistance = distance;
						bestSize = entry.CurrentSize;
					}
				}
			}

			if (bestEntry == null)
			{
				return;
			}

			var nextSize = bestEntry.CurrentSize <= 0
				? Math.Max(16, InitialTextureSize)
				: bestEntry.CurrentSize * Math.Max(2, TextureStepFactor);
			nextSize = Math.Min(nextSize, bestTarget);

			if (nextSize <= bestEntry.CurrentSize)
			{
				return;
			}

			// Бюджет ПАМЯТИ текстур: следующая ступень заменит текущую, поэтому считаем дельту.
			// Исчерпан - апгрейды просто останавливаются (картинка остаётся на достигнутом
			// качестве), вместо того чтобы утащить процесс в своп.
			var delta = EstimateTextureBytes(nextSize) - EstimateTextureBytes(bestEntry.CurrentSize);
			if (_textureBytes + delta > TextureMemoryBudgetBytes)
			{
				if (!_budgetReported)
				{
					_budgetReported = true;
					EditorConsoleLog.Add(LogLevel.Info,
						$"Model streaming: texture memory budget reached " +
						$"({_textureBytes >> 20} MB) - quality upgrades paused.");
				}

				return;
			}

			var source = bestEntry;
			var requestedSize = nextSize;
			_textureJob = new TextureUpgradeJob
			{
				Resident = bestResident!,
				Model = bestResident!.Model!,
				Entry = bestEntry,
				RequestedSize = requestedSize,
				// Чтение файла с диска и декод - ЦЕЛИКОМ в фоне; главный поток только заливает
				// готовые пиксели. Одна задача за раз: декод идёт в полном разрешении файла (пик
				// ~64 МБ на 4K-исходнике), и параллельные задачи складывали бы эти пики.
				DecodeTask = Task.Run(() =>
				{
					var encoded = source.ReadEncoded();
					return encoded == null
						? ((byte[])null, 0, 0)
						: ModelLoader.DecodeEncodedImage(encoded, requestedSize);
				}),
			};
		}

		/// <summary>Потолок качества текстуры для объекта на данном расстоянии: вплотную - целевой
		/// размер, дальше - вдвое меньше на каждое удвоение дистанции сверх <see cref="FullQualityDistance"/>.
		/// Нижняя граница - первая ступень.</summary>
		private int TargetSizeForDistance(int targetSize, float distance)
		{
			var maxSize = targetSize > 0 ? targetSize : 4096;
			if (distance <= FullQualityDistance)
			{
				return maxSize;
			}

			var size = maxSize;
			var reference = FullQualityDistance;
			while (size > InitialTextureSize && distance > reference * 2f)
			{
				size /= 2;
				reference *= 2f;
			}

			return Math.Max(InitialTextureSize, size);
		}

		/// <summary>Байты GPU-текстуры стороны <paramref name="size"/>: RGBA8 + полная мип-цепочка
		/// (~4/3 от нулевого мипа).</summary>
		private static long EstimateTextureBytes(int size)
		{
			if (size <= 0)
			{
				return 0;
			}

			return (long)size * size * 4L * 4L / 3L;
		}

		/// <summary>Пересчитывает учёт текстурной памяти по живым моделям - зовётся после любого
		/// выселения/очистки, где стримленные текстуры освобождены вместе с моделью.</summary>
		private void RecomputeTextureBytes()
		{
			_textureBytes = 0;
			foreach (var resident in _models.Values)
			{
				if (resident.Model == null)
				{
					continue;
				}

				foreach (var entry in resident.Model.StreamedTextures)
				{
					_textureBytes += EstimateTextureBytes(entry.CurrentSize);
				}
			}

			_budgetReported = false;
		}

		private void AgeRetiredTextures()
		{
			for (int i = _retiredTextures.Count - 1; i >= 0; i--)
			{
				var (texture, ticksLeft) = _retiredTextures[i];
				if (ticksLeft <= 1)
				{
					texture.Release();
					_retiredTextures.RemoveAt(i);
				}
				else
				{
					_retiredTextures[i] = (texture, ticksLeft - 1);
				}
			}
		}

		/// <summary>Немедленный Release всей отложенной очереди - только из точек, где GPU уже
		/// дождались (ClearAll/ResetResidency/MigrateEnvironment).</summary>
		private void FlushRetiredTextures()
		{
			foreach (var (texture, _) in _retiredTextures)
			{
				texture.Release();
			}

			_retiredTextures.Clear();
			_budgetReported = false;
		}

		private void EvictIdle(float deltaTime)
		{
			_evictScratch.Clear();
			foreach (var resident in _models.Values)
			{
				if (resident.RefCount > 0 || !resident.Ready)
				{
					if (resident.RefCount > 0)
					{
						resident.IdleSeconds = 0f;
					}
					continue;
				}

				resident.IdleSeconds += deltaTime;
				if (resident.IdleSeconds >= UnloadAfterSeconds)
				{
					_evictScratch.Add(resident);
				}
			}

			// Ошибочные записи без ссылок тоже забываем - иначе словарь копит мусор по битым путям.
			// (Без GPU-стороны им сброс регистраций не нужен.)
			foreach (var resident in _models.Values)
			{
				if (resident.RefCount <= 0 && resident.Failed && resident.Model == null && resident.Request == null)
				{
					_evictScratch.Add(resident);
				}
			}

			if (_evictScratch.Count == 0)
			{
				return;
			}

			var needsGpuReset = false;
			foreach (var resident in _evictScratch)
			{
				if (resident.Model != null)
				{
					needsGpuReset = true;
				}
			}

			if (!needsGpuReset)
			{
				foreach (var resident in _evictScratch)
				{
					_models.Remove(resident.Path);
				}
				return;
			}

			ResetResidency(_evictScratch);
		}

		/// <summary>Выселение с GPU: снять инстансы (событие) -> дождаться GPU -> сбросить регистрации
		/// -> освободить выселяемые модели -> перерегистрировать выживших -> пересобрать граф ->
		/// событие на переинстанцирование. Мега-буферы батч-рендерера не умеют частичного удаления
		/// (пересоздаются целиком), поэтому выселение даже одной модели - полный сброс.</summary>
		private void ResetResidency(List<Resident> evicted)
		{
			ResidencyResetting?.Invoke();

			_env.DilApi.ImmediateContext.Flush();
			_env.DilApi.ImmediateContext.WaitForIdle();
			_env.BatchRenderer.ResetRegistrations();

			// Апгрейд-очередь текстур: GPU уже дождались - отложенные Release можно исполнить сразу;
			// незавершённый декод сам обнаружит смерть модели (alive-проверка в PumpTextureUpgrades).
			FlushRetiredTextures();

			foreach (var resident in evicted)
			{
				_models.Remove(resident.Path);
				resident.Model?.Release();
				resident.Model = null;
				resident.MeshIds.Clear();
				resident.MaterialIds.Clear();
				resident.BatchCache.Clear();
			}

			foreach (var resident in _models.Values)
			{
				resident.MeshIds.Clear();
				resident.MaterialIds.Clear();
				resident.BatchCache.Clear();

				if (resident.Model == null)
				{
					continue;
				}

				try
				{
					RegisterResident(resident, resident.Model);
				}
				catch (Exception ex)
				{
					resident.Error = ex.Message;
					resident.Model = null;
					EditorConsoleLog.Add(LogLevel.Error,
						$"Model streaming: failed to re-register '{resident.Path}': {ex.Message}");
				}
			}

			RecomputeTextureBytes();
			_env.Pipeline.InvalidateGraph();
			ResidencyReset?.Invoke();
		}

		/// <summary>
		/// Полная очистка: отменяет все загрузки и освобождает ВСЕ резидентные модели (барьер GPU +
		/// сброс регистраций + Release + пересборка графа). Это тот самый порядок "сначала очистить
		/// предыдущее, потом грузить новое": вызывается при смене модели превью и при открытии другого
		/// префаба, ДО Acquire новой сцены.
		/// </summary>
		public void ClearAll()
		{
			foreach (var resident in _models.Values)
			{
				if (resident.Request != null)
				{
					if (resident.Finalizing)
					{
						// Недостроенные GPU-ресурсы прерванной финализации откатить нечем - принимаем
						// редкую ограниченную утечку (см. комментарий в ModelPreviewViewport.PollPendingLoad).
						EditorConsoleLog.Add(LogLevel.Warning,
							$"Model streaming: load of '{resident.Path}' cancelled mid-finalize - partial GPU resources leaked.");
					}

					resident.Cts?.Cancel();
					FinishRequest(resident);
				}
			}

			// Незавершённый фоновый декод апгрейда бросаем - его alive-проверка не пройдёт.
			_textureJob = null;

			var anyGpu = _retiredTextures.Count > 0;
			foreach (var resident in _models.Values)
			{
				if (resident.Model != null)
				{
					anyGpu = true;
					break;
				}
			}

			if (!anyGpu)
			{
				_models.Clear();
				return;
			}

			ResidencyResetting?.Invoke();

			_env.DilApi.ImmediateContext.Flush();
			_env.DilApi.ImmediateContext.WaitForIdle();
			_env.BatchRenderer.ResetRegistrations();

			FlushRetiredTextures();

			foreach (var resident in _models.Values)
			{
				resident.Model?.Release();
				resident.Model = null;
			}

			_models.Clear();
			_textureBytes = 0;
			_budgetReported = false;
			_env.Pipeline.InvalidateGraph();
			ResidencyReset?.Invoke();
		}

		/// <summary>Переезд в пересозданное окружение. Вызывающий уже дождался GPU и освободил старое
		/// окружение целиком (его батч-рендерер умер вместе с регистрациями - сбрасывать нечего).
		/// <paramref name="dropModels"/> - выбросить и сами модели (например, анизотропия печётся в
		/// сэмплеры при загрузке - кеш непригоден); иначе резидентные модели перерегистрируются в
		/// новый батч-рендерер без перечитывания с диска, а незавершённые загрузки продолжаются.</summary>
		public void MigrateEnvironment(ModelViewportEnvironment newEnv, bool dropModels)
		{
			_env = newEnv;

			if (dropModels)
			{
				_textureJob = null;
				FlushRetiredTextures(); // барьер уже был - перед освобождением старого окружения

				foreach (var resident in _models.Values)
				{
					resident.Cts?.Cancel();
					FinishRequest(resident);
					// Барьер уже был (перед освобождением старого окружения) - Release безопасен.
					resident.Model?.Release();
					resident.Model = null;
				}

				_models.Clear();
				_textureBytes = 0;
				return;
			}

			foreach (var resident in _models.Values)
			{
				// Записи о сущностях старого стора забыты вызывающим - ссылки начнутся заново с
				// первого Acquire по новому окружению.
				resident.RefCount = 0;
				resident.IdleSeconds = 0f;
				resident.MeshIds.Clear();
				resident.MaterialIds.Clear();
				resident.BatchCache.Clear();

				if (resident.Model == null)
				{
					continue;
				}

				try
				{
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

		private void RegisterResident(Resident resident, ModelLoader model)
		{
			ModelViewportGeometry.RegisterModelResources(_env.BatchRenderer, model,
				resident.MeshIds, resident.MaterialIds, _graphicsApi, _env.SceneCopyTarget, _env.EnvironmentMap);
		}

		private static void FinishRequest(Resident resident)
		{
			if (resident.Cts != null)
			{
				resident.Cts.Dispose();
				resident.Cts = null;
			}

			resident.Request = null;
			resident.Finalizing = false;
		}
	}

	/// <summary>
	/// ECS-привод стриминга: каждый кадр берёт позицию камеры окружения из стора (первая сущность с
	/// <see cref="CameraComponent"/>) и шагает <see cref="ModelStreamer.Tick"/>. Добавляется в
	/// SystemRoot окружения ПОСЛЕДНЕЙ (после GpuInstanceBufferSystem/культинга): готовая модель
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
