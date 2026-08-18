using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using DecaEngine.Core;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Assets;
using DecaEngine.Graphics.Core;
using UnsafeCollections.Collections.Unsafe;

namespace DecaEngine.Editor.ECS
{
	/// <summary>
	/// Device-level, refcounted store of loaded models: today every viewport (Scene View, Model
	/// Preview, the icon baker - see <see cref="DecaEngine.Editor.ECS.ModelStreamer"/>/
	/// <see cref="DecaEngine.Editor.ModelIconBaker"/>) parses the same .gltf, decodes and uploads the
	/// same textures/geometry SEPARATELY, once per environment. <see cref="ModelStore"/> is the single
	/// point that owns <see cref="ModelLoader"/> instances (geometry, textures, CPU-side material data -
	/// all SHAREABLE, see class-doc below) for the whole editor process: one <see cref="IGraphicsApi"/>
	/// device, one store, keyed by (absolute path, <see cref="ModelLoadOptions.Signature"/>).
	///
	/// What IS shared across every acquirer of the same key: <see cref="IMeshObject"/> geometry,
	/// <see cref="IGpuTexture"/>s, samplers, shaders (already deduped device-wide by
	/// <see cref="IGraphicsApi.CreateSharedShader"/>), and all CPU-side parsed data (instances, bounds,
	/// <see cref="MaterialPbrFactors"/>, MeshHasUv). What is NOT shared: <see cref="IMaterialObject"/> -
	/// registering a material into a <see cref="DecaEngine.Graphics.Diligent.DiligentBatchRenderer"/> MUTATES it (rebinds
	/// View/Light/GPURenderInstances/cluster buffers to THAT renderer's constant buffers), and PSOs bake
	/// per-environment SampleCount/RenderTargetFormats. Each acquirer that wants to render the model
	/// builds its OWN material set via <see cref="ModelLoader.BuildAdditionalMaterialSet"/> (the FIRST
	/// acquirer may just use <see cref="ModelLoader.materialObjects"/>, built as a side effect of the
	/// load itself) and registers THAT into its own batch renderer - see
	/// <see cref="DecaEngine.Editor.ModelViewportGeometry.RegisterModelResources"/>'s
	/// <c>materials</c> parameter.
	///
	/// Options MUST match exactly (<see cref="ModelLoadOptions.Signature"/>) for two acquirers to share
	/// one entry: anisotropy/MipLodBias/MaxTextureSize/etc. are baked into immutable samplers and the
	/// texture decoder at LOAD time, so mismatched options are not interchangeable and get their own,
	/// separate <see cref="ModelLoader"/>.
	///
	/// Refcounting: <see cref="Acquire"/> returns a <see cref="Handle"/> that the caller must eventually
	/// pass to <see cref="Release"/> - exactly once. Multiple handles for the same key share one
	/// underlying entry/<see cref="ModelLoader"/>; the entry stays resident while any handle references
	/// it, plus <see cref="UnloadAfterSeconds"/> after the last one lets go (hysteresis against a
	/// consumer that acquires/releases the same model every frame).
	///
	/// Release protocol (preserved from <see cref="DecaEngine.Editor.ECS.ModelStreamer"/>, see its class-doc):
	/// wait for GPU / ensure no frozen command references the resources -&gt; drop instances -&gt;
	/// unregister from batch renderer -&gt; <see cref="ModelLoader.Release"/> -&gt;
	/// Pipeline.InvalidateGraph. The first three steps are ENVIRONMENT-specific (which batch renderer,
	/// which graph) and stay the caller's job: <see cref="BeforeModelEvicted"/> fires - synchronously,
	/// before the model's GPU resources are torn down - so every subscriber gets a chance to unregister
	/// ITS OWN registrations of that <see cref="ModelLoader"/> (compare by reference) before this store
	/// proceeds. The store itself only does the last two steps, and even <see cref="ModelLoader.Release"/>
	/// is deferred a few ticks (<see cref="RetireTicks"/>) - the engine has no in-flight-frame fence, so
	/// a barrier-free eviction (see <see cref="EvictIdle"/>) must assume the GPU can still be reading the
	/// model's buffers/textures for a few more frames after the CPU-side unregistration above.
	/// </summary>
	public sealed class ModelStore
	{
		/// <summary>Caller-held reference to one (path, options) entry. Acquire/Release must be paired 1:1;
		/// multiple handles for the same key are independent refcount units sharing one <see cref="Entry"/>.</summary>
		public sealed class Handle
		{
			internal readonly Entry Entry;
			internal bool Released;

			/// <summary>Priority hint for load/finalize/texture-upgrade ordering - LOWER loads first (e.g.
			/// distance to the requester's camera). An entry's effective priority is the MINIMUM across all
			/// its live handles, so the most urgent requester wins regardless of who else also holds it.
			/// Update via <see cref="ModelStore.SetPriority"/> as the requester's own priority changes
			/// (camera movement, etc.) - a stale value only affects load ORDER, never correctness.</summary>
			internal float Priority;

			public string Path => Entry.Path;
			public ModelLoadOptions Options => Entry.Options;
			public bool Ready => Entry.Model != null;
			public bool Failed => Entry.Error != null;
			public string? Error => Entry.Error;
			public ModelLoader? Model => Entry.Model;

			internal Handle(Entry entry, float priority)
			{
				Entry = entry;
				Priority = priority;
			}
		}

		internal sealed class Entry
		{
			public readonly string Path;
			public readonly ModelLoadOptions Options;
			public readonly string Key;
			public ModelLoader? Model;
			public string? Error;

			/// <summary>Permanently latched by the FIRST <see cref="AcquireMaterialSet"/> call on this
			/// entry (never reset while the entry lives - a NEW entry after reload starts fresh): decides
			/// whether that call (and every call after it, from any handle) gets the model's own
			/// <see cref="ModelLoader.materialObjects"/> or a freshly built <see cref="ModelLoader.BuildAdditionalMaterialSet"/>
			/// set. See <see cref="AcquireMaterialSet"/>.</summary>
			internal bool PrimaryMaterialSetTaken;

			internal readonly List<Handle> Handles = new();
			internal float IdleSeconds;
			internal ModelLoader.ModelLoadRequest? Request;
			internal CancellationTokenSource? Cts;

			/// <summary>See <see cref="DecaEngine.Editor.ECS.ModelStreamer.Resident.Finalizing"/>: FinalizeChunk
			/// already created part of the GPU resources - abandoning mid-finalize leaks them (no rollback),
			/// so an in-progress finalization is always driven to completion even for a now-unreferenced
			/// entry; eviction happens the normal way afterwards.</summary>
			internal bool Finalizing;

			/// <summary>Латч "модель можно показывать": КАЖДАЯ её стрим-текстура дошла минимум до
			/// <see cref="ModelStore.ShowTextureSize"/> (или ждать дальше бессмысленно - см.
			/// <see cref="ModelStore.TextureWaitTimeoutSeconds"/>/бюджет). До него потребители модель НЕ
			/// показывают (см. <see cref="ModelStore.ModelTexturesReady"/> и
			/// <see cref="DecaEngine.Editor.ECS.ModelStreamer.Resident.Ready"/>): иначе она появляется в
			/// кадре с 1x1-филлерами в слотах - то самое "мигание" текстур. ПОЛНОГО качества латч не
			/// ждёт: дальше оно догоняет ступенями фоном (см. <see cref="TryStartTextureUpgrade"/>).
			/// Латчится один раз на запись; после выселения запись создаётся заново с чистого листа.</summary>
			internal bool TexturesReady;

			/// <summary>Сколько секунд эта запись уже ждёт готовности текстур (см. <see cref="TexturesReady"/>) -
			/// страховка от вечно невидимой модели.</summary>
			internal float TextureWaitSeconds;

			/// <summary>Сколько текстур модели провалили декод. Модель при этом показывается БЕЛОЙ (в
			/// слотах остаются 1x1-филлеры), и без сводки это выглядит как «стриминг не догрузил», хотя
			/// декодер просто не понял формат файла.</summary>
			internal int TextureDecodeFailures;

			public bool Ready => Model != null;
			public bool Failed => Error != null;
			public int RefCount => Handles.Count;

			internal float BestPriority
			{
				get
				{
					var best = float.MaxValue;
					foreach (var h in Handles)
					{
						if (h.Priority < best)
						{
							best = h.Priority;
						}
					}

					return best;
				}
			}

			internal Entry(string path, ModelLoadOptions options, string key)
			{
				Path = path;
				Options = options;
				Key = key;
			}
		}

		private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
		private readonly IGraphicsApi _graphicsApi;

		// Скретчи Tick-а - без аллокаций на кадр (см. тот же приём в ModelStreamer).
		private readonly List<Entry> _startScratch = new();
		private readonly List<Entry> _evictScratch = new();

		/// <summary>Фоновый декод ОДНОЙ текстуры сразу всей лестницей качества (см.
		/// <see cref="ModelLoader.DecodeEncodedImageLadder"/>): файл читается и декодируется ровно один
		/// раз за всю жизнь текстуры, а не по разу на ступень.</summary>
		private sealed class TextureUpgradeJob
		{
			public required Entry Entry;
			public required ModelLoader Model;
			public required ModelLoader.StreamedTexture Stream;
			public required int RequestedSize;
			public required System.Threading.Tasks.Task<List<ModelLoader.StreamedTextureLevel>> DecodeTask;
		}

		/// <summary>Декодированные, но ещё не залитые ступени одной текстуры - по возрастанию размера.
		/// Заливаются по одной за тик (см. <see cref="UploadPendingLevels"/>): первая (самая мелкая)
		/// открывает показ модели, остальные догоняют качество уже в кадре.</summary>
		private sealed class PendingTextureLevels
		{
			public required Entry Entry;
			public required ModelLoader Model;
			public required ModelLoader.StreamedTexture Stream;
			public required List<ModelLoader.StreamedTextureLevel> Levels;
			public required int RequestedSize;
			public int Next;
		}

		/// <summary>Фоновые декоды, идущие ПРЯМО СЕЙЧАС (до <see cref="MaxConcurrentTextureDecodes"/>
		/// штук) - чистый CPU в пуле потоков.</summary>
		private readonly List<TextureUpgradeJob> _textureJobs = new();

		/// <summary>Готовые ступени, ждущие заливки на GPU. Их суммарный вес (<see cref="_pendingLevelBytes"/>)
		/// и притормаживает новые декоды - см. <see cref="PendingDecodeBytesBudget"/>.</summary>
		private readonly List<PendingTextureLevels> _pendingLevels = new();

		private long _pendingLevelBytes;

		/// <summary>Апгрейды упёрлись в <see cref="TextureMemoryBudgetBytes"/> - ждать готовности текстур
		/// дальше бессмысленно, ждущие записи объявляются готовыми как есть (лучше показать модель в
		/// текущем качестве, чем не показать вовсе).</summary>
		private bool _upgradesStalled;

		/// <summary>Заменённые апгрейдами GPU-текстуры, ждущие отложенного Release - см. class-doc:
		/// движок не ждёт GPU на партиционном выселении, поэтому старая текстура должна пережить ещё
		/// несколько тиков после того, как все SRB перестали на неё ссылаться.</summary>
		private readonly List<(IGpuTexture Texture, int TicksLeft)> _retiredTextures = new();

		/// <summary>Модели, выселенные <see cref="EvictIdle"/> и ждущие отложенного
		/// <see cref="ModelLoader.Release"/> - тем же приёмом, что и <see cref="_retiredTextures"/>.</summary>
		private readonly List<(ModelLoader Model, int TicksLeft)> _retiredModels = new();

		private const int RetireTicks = 8;

		public int TextureStepFactor { get; set; } = 4;

		/// <summary>Первая ступень качества - её же ждёт показ модели (см. <see cref="ShowTextureSize"/>):
		/// маленький декод с диска стоит копейки и приезжает за считанные кадры.</summary>
		public int InitialTextureSize { get; set; } = 64;

		/// <summary>Минимальная сторона текстур, при которой модель уже можно показывать (см.
		/// <see cref="Entry.TexturesReady"/>): ждать ПОЛНОГО качества незачем - модель появляется на
		/// первой ступени, а дальше качество догоняет фоном ступенями. Ждать нужно ровно того, чтобы в
		/// слотах не остались 1x1-филлеры: именно белые филлеры и выглядели как мигание текстур при
		/// появлении модели, а не переход 64 -&gt; 256 -&gt; 1024.</summary>
		public int ShowTextureSize { get; set; } = 64;

		/// <summary>Потолок ВРЕМЕНИ на заливки текстур за тик для УЖЕ показанной модели, мс. Именно
		/// время, а не число заливок: стоимость одной заливки зависит от размера ступени на два порядка
		/// (64px против 2048px), поэтому счётчик "N штук за тик" либо режет пропускную способность на
		/// мелких ступенях, либо пропускает рывок на крупных. Ограничение по времени даёт ровно то, что
		/// нужно - предсказуемую долю кадра.</summary>
		public float TextureUploadMillisecondsPerTick { get; set; } = 1.5f;

		/// <summary>То же для тиков, в которых есть модели, ЖДУЩИЕ показа: их текстуры - не косметика, а
		/// задержка появления модели, и рывок здесь не виден, потому что самой модели в кадре ещё нет.
		/// Отсюда и куда более щедрый бюджет.</summary>
		public float PendingTextureUploadMillisecondsPerTick { get; set; } = 6f;

		/// <summary>Сколько фоновых декодов может идти одновременно. Декод - чистый CPU в пуле, и именно
		/// он определяет и задержку появления модели, и скорость догрузки качества: у ассетов с 4K-PNG
		/// (десятки мегабайт на файл, разжатие только в полном разрешении) это на порядок дороже всего
		/// остального в стриминге. По умолчанию - почти все ядра, оставляя пару главному потоку и
		/// пулу.</summary>
		public int MaxConcurrentTextureDecodes { get; set; } = Math.Max(2, Environment.ProcessorCount - 2);

		/// <summary>Потолок CPU-памяти под декодированные, но ещё не залитые ступени (см.
		/// <see cref="_pendingLevels"/>). Декоды кратно быстрее заливок, поэтому без него лестницы всех
		/// текстур модели скопились бы в куче разом. Держать надо с запасом на число параллельных
		/// декодов: одна лестница 4K-текстуры с потолком 2048 - это ~22 МБ, и слишком тесный потолок
		/// просто не даёт декодам стартовать (они ждут заливок), обнуляя параллелизм.</summary>
		public long PendingDecodeBytesBudget { get; set; } = 512L << 20;

		/// <summary>Потолок ожидания текстур для ещё не показанной модели: за это время она объявляется
		/// готовой в том качестве, до которого успела дойти. Страховка от вечно невидимой модели, если
		/// апгрейды застопорились (декод падает, исходник недоступен и т.п.).</summary>
		public float TextureWaitTimeoutSeconds { get; set; } = 8f;

		/// <summary>Целевая сторона, если <see cref="ModelLoadOptions.MaxTextureSize"/> = 0 (без лимита).</summary>
		private const int DefaultTextureTargetSize = 4096;

		/// <summary>Ниже этой стороны потолок качества не опускается: смысла нет - модель в таком
		/// качестве уже неотличима от размытого пятна, а память экономится копеечная.</summary>
		private const int MinQualityCeiling = 256;

		/// <summary>Потолок качества текущего тика (см. <see cref="ComputeQualityCeiling"/>).</summary>
		private int _qualityCeiling = DefaultTextureTargetSize;

		/// <summary>Потолок памяти под стримленные текстуры суммарно по ВСЕМ резидентным моделям
		/// (см. тот же тумблер в ModelStreamer - здесь он общий на весь процесс, а не на одно
		/// окружение, ровно потому что модели теперь резидентны РАЗ, а не на каждое окружение).</summary>
		public long TextureMemoryBudgetBytes { get; set; } = 1024L << 20;

		private long _textureBytes;
		private bool _budgetReported;

		public int MaxConcurrentLoads { get; set; } = 2;

		/// <summary>Сколько секунд запись с нулём живых Handle остаётся резидентной, прежде чем быть
		/// выселенной с GPU - буфер против дребезга Acquire/Release в одном и том же кадре.</summary>
		public float UnloadAfterSeconds { get; set; } = 4f;

		/// <summary>Модель догрузилась и её ПЕРВЫЙ материал набор (<see cref="ModelLoader.materialObjects"/>)
		/// готов - можно строить дополнительные наборы (<see cref="ModelLoader.BuildAdditionalMaterialSet"/>)
		/// и регистрировать инстансы. Не сообщает КАКОЙ handle стал готов (их может быть несколько на
		/// одну запись) - подписчик сам решает, относится ли это к его пути/handle-у.</summary>
		public event Action<ModelLoader>? ModelReady;

		/// <summary>Текстуры модели доведены до целевого качества (или ждать дальше бессмысленно - см.
		/// <see cref="Entry.TexturesReady"/>): ТОЛЬКО С ЭТОГО МОМЕНТА модель имеет смысл показывать -
		/// до него её материалы стоят на 1x1-филлерах/низких ступенях, и появление в кадре выглядит как
		/// мигание текстур. Всегда приходит ПОСЛЕ <see cref="ModelReady"/> для той же модели (регистрация
		/// в батч-рендерере нужна раньше - именно её материалы принимают горячие замены).</summary>
		public event Action<ModelLoader>? ModelTexturesReady;

		/// <summary>Запись сейчас будет выселена с GPU (см. class-doc про протокол Release): подписчик
		/// обязан СИНХРОННО, до возврата из обработчика, снять все свои регистрации (инстансы, батчи,
		/// материалы) этой конкретной модели (сравнение по ссылке) в СВОЁМ батч-рендерере и вызвать
		/// Pipeline.InvalidateGraph на своих окружениях - store дальше сам сделает
		/// <see cref="ModelLoader.Release"/> (отложенно, см. <see cref="RetireTicks"/>).</summary>
		public event Action<ModelLoader>? BeforeModelEvicted;

		public ModelStore(IGraphicsApi graphicsApi)
		{
			_graphicsApi = graphicsApi;
		}

		/// <summary>Диагностика - сколько (path, options) записей сейчас в столе (резидентных или ещё
		/// грузящихся).</summary>
		public int EntryCount => _entries.Count;

		private static string NormalizePath(string path)
		{
			if (!Path.IsPathRooted(path))
			{
				path = Path.Combine(Environment.CurrentDirectory, path);
			}

			return Path.GetFullPath(path);
		}

		private static string MakeKey(string normalizedPath, ModelLoadOptions options) =>
			normalizedPath + "" + options.Signature();

		/// <summary>Берёт ссылку на модель файла с данными опциями загрузки (загрузка стартует из Tick).
		/// Каждому Acquire обязан соответствовать РОВНО ОДИН <see cref="Release"/> с ВОЗВРАЩЁННЫМ handle.
		/// <paramref name="priority"/> - см. <see cref="Handle.Priority"/>.</summary>
		public Handle Acquire(string path, ModelLoadOptions options, float priority = 0f)
		{
			var normalizedPath = NormalizePath(path);
			var key = MakeKey(normalizedPath, options);

			if (!_entries.TryGetValue(key, out var entry))
			{
				entry = new Entry(normalizedPath, options, key);
				_entries[key] = entry;

				var extension = Path.GetExtension(normalizedPath);
				if (!string.Equals(extension, ".gltf", StringComparison.OrdinalIgnoreCase) &&
					!string.Equals(extension, ".glb", StringComparison.OrdinalIgnoreCase))
				{
					entry.Error = $"Unsupported model format: {extension}";
					EditorConsoleLog.Add(LogLevel.Warning, $"Model store: {entry.Error} ('{normalizedPath}')");
				}
			}

			var handle = new Handle(entry, priority);
			entry.Handles.Add(handle);
			entry.IdleSeconds = 0f;
			return handle;
		}

		/// <summary>Отпускает handle. Модель НЕ выгружается немедленно - см. <see cref="UnloadAfterSeconds"/>.
		/// Идемпотентно повторному вызову на уже освобождённом handle - no-op.</summary>
		public void Release(Handle handle)
		{
			if (handle.Released)
			{
				return;
			}

			handle.Released = true;
			handle.Entry.Handles.Remove(handle);
			handle.Entry.IdleSeconds = 0f;
		}

		public void SetPriority(Handle handle, float priority) => handle.Priority = priority;

		public bool TryGetReady(Handle handle, out ModelLoader model)
		{
			model = handle.Model!;
			return model != null;
		}

		/// <summary>Текстуры модели этого handle доведены до целевого качества - см.
		/// <see cref="Entry.TexturesReady"/>/<see cref="ModelTexturesReady"/>. false для ещё не
		/// загруженной модели.</summary>
		public bool AreTexturesReady(Handle handle) => handle.Entry.TexturesReady;

		/// <summary>Однострочный срез состояния стриминга - для отладочных прогонов (см. FullLoopProbe).
		/// Нужен потому, что снаружи видно только результат («часть текстур белая»), а причин у него
		/// несколько и они не различимы: не стартовал декод, ждёт заливки, упёрлись в бюджет памяти,
		/// запись выселена.</summary>
		public string DescribeStreamingState()
		{
			var entries = 0;
			var streams = 0;
			var withSource = 0;
			var atZero = 0;
			var atTarget = 0;

			foreach (var entry in _entries.Values)
			{
				entries++;
				if (entry.Model == null)
				{
					continue;
				}

				foreach (var stream in entry.Model.StreamedTextures)
				{
					streams++;
					if (stream.CurrentSize <= 0)
					{
						atZero++;
					}

					// Сравнение с потолком, а не с сырым MaxTextureSize: под бюджетом памяти "готово" -
					// это дойти до потолка, и иначе дамп показывал бы atTarget=0 у полностью догруженной
					// модели.
					if (stream.CurrentSize >= TargetSizeFor(stream))
					{
						atTarget++;
					}

					if (stream.HasSource)
					{
						withSource++;
					}
				}
			}

			return $"entries={entries}, streams={streams}, ceiling={_qualityCeiling}, withSource={withSource}, atZero={atZero}, " +
				$"atTarget={atTarget}, decodeJobs={_textureJobs.Count}, queuedLevels={_pendingLevels.Count}, " +
				$"pendingLevelMB={_pendingLevelBytes >> 20}, textureMB={_textureBytes >> 20}/{TextureMemoryBudgetBytes >> 20}, " +
				$"stalled={_upgradesStalled}";
		}

		public bool TryGetError(Handle handle, out string error)
		{
			error = handle.Error!;
			return error != null;
		}

		/// <summary>
		/// Hands the caller ONE independent set of <see cref="IMaterialObject"/>s for a ready model
		/// (<see cref="Handle.Ready"/> must be true) to register into ITS OWN batch renderer - see the
		/// class-doc invariant: materials cannot be shared across environments. The FIRST call ever made
		/// on this handle's entry (by this handle or any other acquirer of the same key - whichever gets
		/// there first) gets the model's own <see cref="ModelLoader.materialObjects"/>, built as a side
		/// effect of the load; every call after that, from any handle, gets a fresh
		/// <see cref="ModelLoader.BuildAdditionalMaterialSet"/> set instead. The decision is latched on
		/// the ENTRY (<see cref="Entry.PrimaryMaterialSetTaken"/>), not the handle, so it is safe (and
		/// expected - see <see cref="DecaEngine.Editor.ECS.ModelStreamer.MigrateEnvironment"/>) to call
		/// this again later on the SAME handle after its previous set was abandoned (e.g. the environment
		/// it was registered into was recreated): the second call always gets a fresh additional set,
		/// never steals the primary one back.
		/// </summary>
		public OrderedDictionary<int, IMaterialObject> AcquireMaterialSet(Handle handle)
		{
			var entry = handle.Entry;
			var model = entry.Model;
			if (model == null)
			{
				throw new InvalidOperationException(
					"Model store: AcquireMaterialSet called before the model finished loading.");
			}

			if (!entry.PrimaryMaterialSetTaken)
			{
				entry.PrimaryMaterialSetTaken = true;
				return model.materialObjects;
			}

			return ModelLoader.BuildAdditionalMaterialSet(_graphicsApi, entry.Options, model);
		}

		/// <summary>
		/// Кадровый шаг (главный/GPU поток, под GPU-локом - как и <see cref="DecaEngine.Editor.ECS.ModelStreamer.Tick"/>):
		/// запуск очередных загрузок по приоритету, опрос фоновых Prepare-задач, финализация ОДНОЙ
		/// модели за кадр, прогрессивный стриминг текстур (ОДНА заливка за тик - на ВСЕ резидентные
		/// модели процесса разом, а не на окружение, см. class-doc) и выселение простаивающих записей.
		/// </summary>
		public void Tick(float deltaTime)
		{
			StartQueuedLoads();
			PollLoads();
			PumpTextureUpgrades();
			AnnounceTextureReadiness(deltaTime);
			AgeRetiredTextures();
			AgeRetiredModels();
			EvictIdle(deltaTime);
		}

		private void StartQueuedLoads()
		{
			var inFlight = 0;
			_startScratch.Clear();
			foreach (var entry in _entries.Values)
			{
				if (entry.Request != null)
				{
					inFlight++;
				}
				else if (entry.Model == null && entry.Error == null && entry.RefCount > 0)
				{
					_startScratch.Add(entry);
				}
			}

			if (_startScratch.Count == 0 || inFlight >= MaxConcurrentLoads)
			{
				return;
			}

			_startScratch.Sort((a, b) => a.BestPriority.CompareTo(b.BestPriority));

			foreach (var entry in _startScratch)
			{
				if (inFlight >= MaxConcurrentLoads)
				{
					break;
				}

				try
				{
					entry.Cts = new CancellationTokenSource();
					entry.Request = ModelLoader.BeginLoadAsync(_graphicsApi, entry.Path, entry.Options,
						cancellationToken: entry.Cts.Token);
					inFlight++;
				}
				catch (Exception ex)
				{
					entry.Error = ex.Message;
					FinishRequest(entry);
					EditorConsoleLog.Add(LogLevel.Error, $"Model store: failed to load '{entry.Path}': {ex.Message}");
				}
			}
		}

		private void PollLoads()
		{
			Entry? best = null;
			var bestPriority = float.MaxValue;
			List<Entry>? abandoned = null;

			foreach (var entry in _entries.Values)
			{
				if (entry.Request == null)
				{
					continue;
				}

				// Запись разлюбили, пока грузилась (все Release прежде готовности) - глушим фоновый
				// декод. Начатую финализацию не бросаем (см. Entry.Finalizing).
				if (entry.RefCount <= 0 && !entry.Finalizing)
				{
					entry.Cts?.Cancel();
					FinishRequest(entry);
					(abandoned ??= new List<Entry>()).Add(entry);
					continue;
				}

				if (!entry.Request.PrepareTask.IsCompleted)
				{
					continue;
				}

				if (!entry.Request.PrepareTask.IsCompletedSuccessfully)
				{
					entry.Error = entry.Request.PrepareTask.Exception?.GetBaseException().Message ?? "Unknown error";
					FinishRequest(entry);
					EditorConsoleLog.Add(LogLevel.Error, $"Model store: failed to load '{entry.Path}': {entry.Error}");
					continue;
				}

				var priority = entry.Finalizing ? float.MinValue : entry.BestPriority;
				if (priority < bestPriority)
				{
					bestPriority = priority;
					best = entry;
				}
			}

			if (abandoned != null)
			{
				foreach (var entry in abandoned)
				{
					_entries.Remove(entry.Key);
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
				EditorConsoleLog.Add(LogLevel.Error, $"Model store: failed to finalize '{best.Path}': {ex.Message}");
				return;
			}

			if (model == null)
			{
				return; // порция кадра исчерпана - продолжение следующим кадром
			}

			FinishRequest(best);
			best.Model = model;
			best.IdleSeconds = 0f;

			var t = model.Timings;
			EditorConsoleLog.Add(LogLevel.Info,
				$"Model load '{Path.GetFileName(best.Path)}': " +
				$"parse {t.ParseMs} ms, materials {t.MaterialsMs} ms, meshes {t.MeshesMs} ms, " +
				$"finalize {t.FinalizeMs} ms (shaders {t.ShaderMs} ms / {t.ShaderVariants} variants, " +
				$"PSO+material {t.MaterialBuildMs} ms / {t.MaterialsBuilt}, " +
				$"mesh upload {t.MeshMs} ms / {t.MeshUploads}, samplers {t.SamplerMs} ms / {t.Samplers})");

			ModelReady?.Invoke(model);
		}

		/// <summary>
		/// Прогрессивный стриминг качества текстур - см. <see cref="DecaEngine.Editor.ECS.ModelStreamer.PumpTextureUpgrades"/>,
		/// та же механика (декод в фоне, заливка/горячая замена здесь по одной за тик), но на уровне
		/// ВСЕГО стола: одна очередь на процесс вместо одной на окружение, что попутно чинит гонку двух
		/// окружений, апгрейдящих одну и ту же (теперь РАЗДЕЛЯЕМУЮ) StreamedTexture независимо. Горячая
		/// замена сама фанаутится на все живые наборы материалов - см. <see cref="ModelLoader.StreamedTexture.Bindings"/>,
		/// куда каждый набор (первый и любой из <see cref="ModelLoader.BuildAdditionalMaterialSet"/>)
		/// дописывает свою привязку при постройке.
		///
		/// В отличие от ModelStreamer здесь НЕТ дистанционного потолка качества (TargetSizeForDistance):
		/// стол не знает камер отдельных окружений - апгрейд всегда идёт до <see cref="ModelLoadOptions.MaxTextureSize"/>,
		/// упорядоченный по <see cref="Entry.BestPriority"/> (приоритет ближайшего интересанта).
		///
		/// Записи, ещё не дошедшие до <see cref="Entry.TexturesReady"/> (модель загружена, но потребители
		/// её НЕ показывают - см. <see cref="ModelTexturesReady"/>), обслуживаются ВНЕ ОЧЕРЕДИ: их первая
		/// ступень - это не косметика, а условие появления модели в кадре. Ступень при этом обычная,
		/// маленькая (<see cref="InitialTextureSize"/>) - показ не ждёт полного качества.
		/// </summary>
		private void PumpTextureUpgrades()
		{
			_qualityCeiling = ComputeQualityCeiling();

			// Флаг сбрасывается ДО всей прокачки, а не внутри StartTextureUpgrades: его выставляет и
			// заливка (UploadNextLevel), и старт декода, а читает - AnnounceTextureReadiness уже ПОСЛЕ
			// прокачки. Сброс в конце обнулял бы отказ, случившийся на заливке, и страховка "показать в
			// текущем качестве при упёршемся бюджете" не срабатывала бы вовсе.
			_upgradesStalled = false;

			CollectTextureDecodes();
			UploadPendingLevels();
			StartTextureUpgrades();
		}

		/// <summary>Забирает результаты завершившихся фоновых декодов. Декод отдаёт СРАЗУ ВСЮ лестницу
		/// качества (см. <see cref="ModelLoader.DecodeEncodedImageLadder"/>), поэтому здесь остаётся
		/// только переложить готовые ступени в очередь заливки - это перекладывание ссылок, бюджета тика
		/// оно не тратит (его тратит <see cref="UploadPendingLevels"/>).</summary>
		private void CollectTextureDecodes()
		{
			for (int i = 0; i < _textureJobs.Count; )
			{
				var job = _textureJobs[i];
				if (!job.DecodeTask.IsCompleted)
				{
					i++;
					continue;
				}

				_textureJobs.RemoveAt(i);
				EnqueueDecodedLevels(job);
			}
		}

		private void EnqueueDecodedLevels(TextureUpgradeJob job)
		{
			var alive = _entries.TryGetValue(job.Entry.Key, out var current) &&
				ReferenceEquals(current, job.Entry) && ReferenceEquals(job.Entry.Model, job.Model);

			if (!job.DecodeTask.IsCompletedSuccessfully)
			{
				if (alive)
				{
					job.Stream.ReleaseCpuData();

					// Подробности - только про ПЕРВУЮ сбойную текстуру модели: причина у всех обычно
					// одна (неподдерживаемый формат), и 76 почти одинаковых строк лишь топят её в
					// консоли. Сводку по модели даёт AnnounceTextureReadiness.
					if (job.Entry.TextureDecodeFailures++ == 0)
					{
						EditorConsoleLog.Add(LogLevel.Warning,
							$"Model store: texture decode failed for '{job.Entry.Path}': " +
							$"{job.DecodeTask.Exception?.GetBaseException().Message}");
					}
				}

				return;
			}

			var levels = job.DecodeTask.Result;
			if (!alive)
			{
				return;
			}

			if (levels == null || levels.Count == 0)
			{
				// Декодировать было нечего (исходник пропал/пуст) - иначе текстура вечно считалась бы
				// "ещё не готовой" и держала бы показ модели.
				job.Stream.ReleaseCpuData();
				return;
			}

			foreach (var level in levels)
			{
				_pendingLevelBytes += level.ByteLength;
			}

			_pendingLevels.Add(new PendingTextureLevels
			{
				Entry = job.Entry,
				Model = job.Model,
				Stream = job.Stream,
				Levels = levels,
				RequestedSize = job.RequestedSize,
			});
		}

		/// <summary>Заливает готовые ступени на GPU и горячо подменяет текстуры во всех живых привязках,
		/// пока не выйдет бюджет ВРЕМЕНИ тика (<see cref="TextureUploadMillisecondsPerTick"/>, а пока
		/// есть ждущие показа модели - <see cref="PendingTextureUploadMillisecondsPerTick"/>). Раньше
		/// здесь стоял счётчик "N заливок за тик"; на ассете из 82 текстур 4K это выливалось в сотни
		/// тиков ожидания при том, что сама заливка занимала доли миллисекунды - бюджет простаивал.</summary>
		private void UploadPendingLevels()
		{
			if (_pendingLevels.Count == 0)
			{
				return;
			}

			// Бюджет тика - по времени. Щедрый режим включается ровно тогда, когда хоть одна модель ещё
			// ждёт показа: пока её нет в кадре, заливки не могут дать видимого рывка.
			var anyPending = false;
			foreach (var queued in _pendingLevels)
			{
				if (!queued.Entry.TexturesReady)
				{
					anyPending = true;
					break;
				}
			}

			var budgetMs = anyPending
				? Math.Max(TextureUploadMillisecondsPerTick, PendingTextureUploadMillisecondsPerTick)
				: TextureUploadMillisecondsPerTick;

			var clock = System.Diagnostics.Stopwatch.StartNew();

			// Два прохода, и порядок здесь - это прямо задержка появления модели. Сначала текстуры, у
			// которых в слоте ещё 1x1-филлер (ниже ShowTextureSize): пока хоть одна такая есть, модель
			// не показывается вовсе, и потратить тик на 512 -&gt; 1024 у соседней текстуры значит отложить
			// показ ВСЕЙ модели ради качества, которого никто пока не видит. Второй проход - уже
			// косметика: догон качества сверху.
			for (int pass = 0; pass < 2; pass++)
			{
				for (int i = 0; i < _pendingLevels.Count; )
				{
					if (clock.Elapsed.TotalMilliseconds >= budgetMs)
					{
						return;
					}

					var queued = _pendingLevels[i];

					var alive = _entries.TryGetValue(queued.Entry.Key, out var current) &&
						ReferenceEquals(current, queued.Entry) && ReferenceEquals(queued.Entry.Model, queued.Model);

					if (!alive)
					{
						DropPendingLevels(i);
						continue;
					}

					var blocksShow = !queued.Entry.TexturesReady &&
						queued.Stream.CurrentSize < EffectiveShowSize(queued.Stream);

					if (blocksShow != (pass == 0))
					{
						i++;
						continue;
					}

					var result = UploadNextLevel(queued);

					// Пауза - это упор в бюджет ПАМЯТИ, а не в бюджет времени: ступень осталась в очереди
					// и дождётся освобождения памяти, а тик идёт дальше к тем, кто залиться может.
					if (result == LevelUpload.Paused)
					{
						i++;
						continue;
					}

					if (result != LevelUpload.Uploaded)
					{
						DropPendingLevels(i);
						continue;
					}

					i++;
				}
			}
		}

		private enum LevelUpload
		{
			/// <summary>Ступень залита, в очереди есть ещё.</summary>
			Uploaded,

			/// <summary>Не хватает бюджета текстурной памяти. Очередь НЕ выбрасывается: исходник уже
			/// разжат, повторить заливку позже стоит копейки, а выбросить - значит заморозить текстуру
			/// на текущем качестве навсегда (декод больше не начнётся, см. IsUpgradeInFlight/HasSource).</summary>
			Paused,

			/// <summary>Лестница пройдена до конца - стриминг этой текстуры окончен.</summary>
			Finished,

			/// <summary>Заливка сорвалась - повторять нечем.</summary>
			Failed,
		}

		private LevelUpload UploadNextLevel(PendingTextureLevels queued)
		{
			var stream = queued.Stream;

			while (queued.Next < queued.Levels.Count)
			{
				var level = queued.Levels[queued.Next];
				var size = level?.Size ?? 0;

				if (level == null || size <= stream.CurrentSize)
				{
					ConsumeLevel(queued);
					continue;
				}

				// Проверка ДО изъятия ступени из очереди: при отказе она должна остаться на месте.
				var delta = EstimateTextureBytes(size, stream.IsBlockCompressed) -
					EstimateTextureBytes(stream.CurrentSize, stream.IsBlockCompressed);
				if (_textureBytes + delta > TextureMemoryBudgetBytes)
				{
					_upgradesStalled = true;
					ReportTextureBudget();
					return LevelUpload.Paused;
				}

				try
				{
					// Ступень сама знает, чем она является: RGBA8 из декода (мипы достроит GPU) или
					// готовый BC-хвост из .dtex (мипы уже в ней). См. ModelLoader.StreamedTextureLevel.
					var gpuTexture = _graphicsApi.CreateTexture(
						level.ToCpuTextureData($"Stream {level.Width}x{level.Height}"));

					foreach (var (material, slot) in stream.Bindings)
					{
						material.SetTexture(slot, gpuTexture);
					}

					if (stream.Texture != null)
					{
						_retiredTextures.Add((stream.Texture, RetireTicks));
						_textureBytes -= EstimateTextureBytes(stream.CurrentSize, stream.IsBlockCompressed);
					}

					stream.Texture = gpuTexture;
					stream.CurrentSize = size;
					_textureBytes += EstimateTextureBytes(stream.CurrentSize, stream.IsBlockCompressed);
				}
				catch (Exception ex)
				{
					ConsumeLevel(queued);
					EditorConsoleLog.Add(LogLevel.Warning,
						$"Model store: texture upgrade upload failed for '{queued.Entry.Path}': {ex.Message}");
					return LevelUpload.Failed;
				}

				ConsumeLevel(queued);
				return queued.Next >= queued.Levels.Count ? LevelUpload.Finished : LevelUpload.Uploaded;
			}

			return LevelUpload.Finished;
		}

		/// <summary>Снимает ступень с очереди и сразу отпускает её массив: до заливки остатка лестницы
		/// может пройти много тиков, и держать уже ненужный уровень в CPU-памяти незачем.</summary>
		private void ConsumeLevel(PendingTextureLevels queued)
		{
			var index = queued.Next++;

			_pendingLevelBytes -= queued.Levels[index]?.ByteLength ?? 0;

			// Ссылка на данные ступени рвётся сразу, а не по завершении всей лестницы: у Sponza это
			// десятки мегабайт, которые иначе досидели бы в куче до последней ступени.
			queued.Levels[index] = null!;
		}

		/// <summary>Выбрасывает очередь ступеней целиком (залита последняя, запись умерла или заливка
		/// сорвалась) и закрывает стриминг текстуры: лестница декодируется РАЗ И НАВСЕГДА, так что после
		/// неё исходник больше не нужен ни при каком исходе.</summary>
		private void DropPendingLevels(int index)
		{
			var queued = _pendingLevels[index];

			for (int i = queued.Next; i < queued.Levels.Count; i++)
			{
				_pendingLevelBytes -= queued.Levels[i]?.ByteLength ?? 0;
			}

			queued.Levels.Clear();
			_pendingLevels.RemoveAt(index);
			queued.Stream.ReleaseCpuData();
		}

		private void ReportTextureBudget()
		{
			if (_budgetReported)
			{
				return;
			}

			_budgetReported = true;
			EditorConsoleLog.Add(LogLevel.Info,
				$"Model store: texture memory budget reached ({_textureBytes >> 20} MB) - quality upgrades paused.");
		}

		/// <summary>Доводит число фоновых декодов до <see cref="MaxConcurrentTextureDecodes"/> - декод
		/// это чистый CPU в пуле, и именно он определяет, как быстро ещё не показанная модель доедет
		/// до <see cref="Entry.TexturesReady"/>.</summary>
		private void StartTextureUpgrades()
		{
			// Декоды придерживаются, пока уже готовые ступени не залиты: лестница целиком лежит в
			// CPU-памяти до заливки, и без этого потолка 69 текстур Sponza разом дали бы сотни мегабайт
			// мусора (заливка идёт единицами за тик и заведомо медленнее декодов).
			if (_pendingLevelBytes >= PendingDecodeBytesBudget)
			{
				return;
			}

			var maxJobs = Math.Max(1, MaxConcurrentTextureDecodes);
			while (_textureJobs.Count < maxJobs && TryStartTextureUpgrade())
			{
			}
		}

		private bool TryStartTextureUpgrade()
		{
			Entry? bestEntry = null;
			ModelLoader.StreamedTexture? bestStream = null;
			var bestPending = false;
			var bestPriority = float.MaxValue;
			var bestSize = int.MaxValue;

			foreach (var entry in _entries.Values)
			{
				if (entry.Model == null || entry.RefCount <= 0)
				{
					continue;
				}

				var priority = entry.BestPriority;

				// Модель, которую ещё никто не показывает, важнее любого повышения качества уже видимой:
				// её текстуры - условие появления в кадре вообще, а не косметика.
				var pending = !entry.TexturesReady;

				foreach (var stream in entry.Model.StreamedTextures)
				{
					if (!stream.HasSource || stream.CurrentSize >= TargetSizeFor(stream) ||
						IsUpgradeInFlight(stream))
					{
						continue;
					}

					var better = bestStream == null ||
						(pending != bestPending
							? pending
							: priority != bestPriority
								? priority < bestPriority
								: stream.CurrentSize < bestSize);

					if (!better)
					{
						continue;
					}

					bestEntry = entry;
					bestStream = stream;
					bestPending = pending;
					bestPriority = priority;
					bestSize = stream.CurrentSize;
				}
			}

			if (bestStream == null)
			{
				return false;
			}

			var targetSize = TargetSizeFor(bestStream);

			// Первая ступень бюджету обязана поместиться - иначе модель вообще не появится; остальные
			// проверяются по одной при заливке (см. UploadNextLevel), поэтому упёршийся бюджет обрывает
			// лестницу, а не отменяет её целиком.
			var firstSize = Math.Min(Math.Max(16, InitialTextureSize), targetSize);
			var delta = EstimateTextureBytes(firstSize, bestStream.IsBlockCompressed) -
				EstimateTextureBytes(bestStream.CurrentSize, bestStream.IsBlockCompressed);
			if (_textureBytes + delta > TextureMemoryBudgetBytes)
			{
				_upgradesStalled = true;
				ReportTextureBudget();
				return false;
			}

			// ОДИН декод на всю жизнь текстуры: stb умеет разжимать только в полном разрешении, поэтому
			// ступень "64px" стоит ровно столько же, сколько полная, и лестница из отдельных декодов
			// означала бы N полных разжатий одного файла (это и делало появление модели МЕДЛЕННЕЕ, чем
			// загрузка сразу в целевом качестве). Все ступени снимаются с одной цепочки даунскейлов и
			// заливаются по одной - см. ModelLoader.DecodeEncodedImageLadder и UploadPendingLevels.
			var source = bestStream;
			var stepFactor = Math.Max(2, TextureStepFactor);
			_textureJobs.Add(new TextureUpgradeJob
			{
				Entry = bestEntry!,
				Model = bestEntry!.Model!,
				Stream = bestStream,
				RequestedSize = targetSize,
				DecodeTask = System.Threading.Tasks.Task.Run(() => source.DtexPath != null
					? BuildBakedLadder(source, targetSize, firstSize, stepFactor)
					: BuildDecodedLadder(source, targetSize, firstSize, stepFactor)),
			});

			return true;
		}

		private static List<ModelLoader.StreamedTextureLevel> BuildDecodedLadder(
			ModelLoader.StreamedTexture stream, int targetSize, int firstSize, int stepFactor)
		{
			var encoded = stream.ReadEncoded();
			if (encoded == null)
			{
				return new List<ModelLoader.StreamedTextureLevel>();
			}

			var decoded = ModelLoader.DecodeEncodedImageLadder(encoded, targetSize, firstSize, stepFactor);
			var levels = new List<ModelLoader.StreamedTextureLevel>(decoded.Count);

			foreach (var (pixels, width, height) in decoded)
			{
				levels.Add(ModelLoader.StreamedTextureLevel.FromDecodedPixels(pixels, width, height));
			}

			return levels;
		}

		/// <summary>
		/// Лестница качества прямо из запечённой .dtex - без декода вообще, одним чтением с диска.
		///
		/// Мип-уровни лежат в файле от большого к малому, поэтому «текстура в размере S» - это хвост
		/// цепочки от соответствующего уровня до конца файла. Отсюда две вещи, которых стриминг из
		/// PNG дать не может. Во-первых, читается РОВНО столько, сколько нужно целевому качеству: при
		/// потолке 512 у .dtex с верхним уровнем 2048 нулевой уровень (три четверти файла) не
		/// касается диска вовсе. Во-вторых, все ступени - это подмассивы ОДНОГО прочитанного хвоста,
		/// то есть каждая следующая ступень стоит ноль I/O и ноль CPU: ступень 64 -&gt; 256 -&gt; 1024 не
		/// перечитывает и не пережимает ничего.
		/// </summary>
		private static List<ModelLoader.StreamedTextureLevel> BuildBakedLadder(
			ModelLoader.StreamedTexture stream, int targetSize, int firstSize, int stepFactor)
		{
			var levels = new List<ModelLoader.StreamedTextureLevel>();

			// Уровень целевого качества - самый ВЕРХНИЙ (наименьший индекс) из нужных, с него и
			// начинается хвост.
			int topLevel = DtexFile.LevelForSize(stream.DtexWidth, stream.DtexHeight, targetSize);

			var payload = DtexFile.TryReadFromLevel(stream.DtexPath, topLevel);
			if (payload == null)
			{
				return levels;
			}

			// Ступени идут снизу вверх по качеству, то есть от последних уровней хвоста к первому.
			int firstIndex = DtexFile.LevelForSize(payload.Width, payload.Height, firstSize);
			firstIndex = Math.Min(firstIndex, payload.Mips.Length - 1);

			// Шаг качества задан множителем СТОРОНЫ (TextureStepFactor), а уровни идут степенями
			// двойки - переводим одно в другое.
			int step = Math.Max(1, (int)Math.Round(Math.Log2(stepFactor)));

			for (int index = firstIndex; ; index = Math.Max(0, index - step))
			{
				levels.Add(ModelLoader.StreamedTextureLevel.FromCompressed(
					payload.Format,
					payload.Mips[index..],
					Math.Max(1, payload.Width >> index),
					Math.Max(1, payload.Height >> index)));

				// Нулевой уровень - целевое качество; на нём лестница заканчивается всегда, каким бы
				// шаг ни был.
				if (index == 0)
				{
					break;
				}
			}

			return levels;
		}

		/// <summary>Текстура уже обслуживается - декодируется в пуле ИЛИ её ступени ждут заливки. Второе
		/// не менее важно: лестница декодируется один раз целиком, и повторный декод той же текстуры,
		/// пока предыдущий не долит свои ступени, был бы полным разжатием файла впустую.</summary>
		private bool IsUpgradeInFlight(ModelLoader.StreamedTexture stream)
		{
			foreach (var job in _textureJobs)
			{
				if (ReferenceEquals(job.Stream, stream))
				{
					return true;
				}
			}

			foreach (var queued in _pendingLevels)
			{
				if (ReferenceEquals(queued.Stream, stream))
				{
					return true;
				}
			}

			return false;
		}

		private static int EffectiveTargetSize(ModelLoader.StreamedTexture stream) =>
			stream.TargetSize > 0 ? stream.TargetSize : DefaultTextureTargetSize;

		/// <summary>Потолок качества этой текстуры с учётом общего бюджета памяти - см.
		/// <see cref="ComputeQualityCeiling"/>.</summary>
		private int TargetSizeFor(ModelLoader.StreamedTexture stream) =>
			Math.Max(EffectiveShowSize(stream), Math.Min(EffectiveTargetSize(stream), _qualityCeiling));

		/// <summary>
		/// Наибольшая сторона, до которой можно поднимать качество, чтобы в <see cref="TextureMemoryBudgetBytes"/>
		/// поместились ВСЕ текстуры резидентных моделей, а не только те, что успели первыми.
		///
		/// Без этого потолка стриминг вырождается в гонку: у ассета из 82 текстур 4K первые 48 доезжают
		/// до 2048 и выбирают бюджет до байта, а оставшиеся 34 остаются на 1x1-филлерах - модель
		/// наполовину белая. Ровное качество на всех (пусть и ниже максимума) в этой ситуации - не
		/// компромисс, а единственный осмысленный исход: 82 текстуры по 1024 занимают 459 МБ и влезают
		/// целиком, тогда как по 2048 их нужно 1.8 ГБ.
		/// </summary>
		private int ComputeQualityCeiling()
		{
			// Сжатые и несжатые считаются отдельно: они отличаются вчетверо, и общий счётчик занижал
			// бы потолок сцене из запечённых текстур ровно во столько же раз.
			var plainStreams = 0;
			var blockStreams = 0;

			foreach (var entry in _entries.Values)
			{
				if (entry.Model == null || entry.RefCount <= 0)
				{
					continue;
				}

				foreach (var stream in entry.Model.StreamedTextures)
				{
					if (stream.IsBlockCompressed)
					{
						blockStreams++;
					}
					else
					{
						plainStreams++;
					}
				}
			}

			if (plainStreams + blockStreams <= 0)
			{
				return DefaultTextureTargetSize;
			}

			var size = DefaultTextureTargetSize;
			while (size > MinQualityCeiling &&
				plainStreams * EstimateTextureBytes(size, false) +
				blockStreams * EstimateTextureBytes(size, true) > TextureMemoryBudgetBytes)
			{
				size >>= 1;
			}

			return size;
		}

		/// <summary>Сторона, начиная с которой текстуру не стыдно показать - см.
		/// <see cref="ShowTextureSize"/>. Для маленьких исходников это их собственный потолок.</summary>
		private int EffectiveShowSize(ModelLoader.StreamedTexture stream) =>
			Math.Min(Math.Max(16, ShowTextureSize), EffectiveTargetSize(stream));

		/// <summary>
		/// Латчит <see cref="Entry.TexturesReady"/> и шлёт <see cref="ModelTexturesReady"/> - момент, с
		/// которого модель можно показывать без мигания текстур. Готова = у каждой стрим-текстуры либо
		/// исчерпан исходник (<see cref="ModelLoader.StreamedTexture.HasSource"/>: дошли до нативного
		/// разрешения или декод не удался), либо достигнута <see cref="EffectiveShowSize"/> - ПЕРВАЯ
		/// ступень, а не полное качество: дальше оно догоняет фоном, уже в кадре.
		///
		/// Две страховки от вечно невидимой модели: упёршийся бюджет текстурной памяти
		/// (<see cref="_upgradesStalled"/>) и потолок ожидания (<see cref="TextureWaitTimeoutSeconds"/>) -
		/// в обоих случаях модель объявляется готовой в том качестве, до которого дошла.
		/// </summary>
		private void AnnounceTextureReadiness(float deltaTime)
		{
			List<Entry>? announced = null;

			foreach (var entry in _entries.Values)
			{
				if (entry.TexturesReady || entry.Model == null)
				{
					continue;
				}

				entry.TextureWaitSeconds += deltaTime;

				var settled = true;
				foreach (var stream in entry.Model.StreamedTextures)
				{
					if (stream.HasSource && stream.CurrentSize < EffectiveShowSize(stream))
					{
						settled = false;
						break;
					}
				}

				if (!settled)
				{
					if (_upgradesStalled)
					{
						EditorConsoleLog.Add(LogLevel.Info,
							$"Model store: showing '{Path.GetFileName(entry.Path)}' at current texture quality - " +
							"upgrades are paused by the memory budget.");
					}
					else if (entry.TextureWaitSeconds >= TextureWaitTimeoutSeconds)
					{
						EditorConsoleLog.Add(LogLevel.Warning,
							$"Model store: texture streaming for '{Path.GetFileName(entry.Path)}' did not settle in " +
							$"{TextureWaitTimeoutSeconds:0.#} s - showing it at current quality.");
					}
					else
					{
						continue;
					}
				}

				entry.TexturesReady = true;
				(announced ??= new List<Entry>()).Add(entry);

				// Главное, что должен увидеть пользователь, когда модель вышла белой: это не «стриминг
				// не догрузил», а нечитаемый формат текстур. Декодер понимает PNG/JPG; DDS/KTX2 (обычная
				// упаковка ассетов из движковых сэмплов) он не открывает вовсе.
				if (entry.TextureDecodeFailures > 0)
				{
					EditorConsoleLog.Add(LogLevel.Warning,
						$"Model store: {entry.TextureDecodeFailures} of {entry.Model.StreamedTextures.Count} textures " +
						$"failed to decode for '{Path.GetFileName(entry.Path)}' - those materials stay untextured. " +
						"Supported source formats are PNG and JPEG (DDS/KTX2 are not decoded).");
				}
			}

			if (announced == null)
			{
				return;
			}

			// Событие зовётся ВНЕ обхода _entries: подписчик показывает модель, а это может привести к
			// новым Acquire (например, соседние записи сцены) - мутации словаря под foreach.
			foreach (var entry in announced)
			{
				if (entry.Model != null)
				{
					ModelTexturesReady?.Invoke(entry.Model);
				}
			}
		}

		/// <summary>
		/// Оценка VRAM под текстуру со стороной <paramref name="size"/> и полной мип-цепочкой
		/// (отсюда множитель 4/3).
		///
		/// Блочно-сжатые (BC7/BC5 - байт на тексель против четырёх у RGBA8) обязаны считаться
		/// отдельно: иначе бюджет текстурной памяти видел бы вчетверо больше, чем занято на самом
		/// деле, и упирался бы в потолок на сцене, которая помещается с запасом, - то есть кеш
		/// ассетов экономил бы VRAM, а стример продолжал бы вести себя так, будто экономии нет.
		/// BC1/BC4 занимают ещё вдвое меньше, но в авто-выборе не участвуют, и завышение для них
		/// безопаснее занижения.
		/// </summary>
		private static long EstimateTextureBytes(int size, bool blockCompressed)
		{
			if (size <= 0)
			{
				return 0;
			}

			return (long)size * size * (blockCompressed ? 1L : 4L) * 4L / 3L;
		}

		private void RecomputeTextureBytes()
		{
			_textureBytes = 0;
			foreach (var entry in _entries.Values)
			{
				if (entry.Model == null)
				{
					continue;
				}

				foreach (var stream in entry.Model.StreamedTextures)
				{
					_textureBytes += EstimateTextureBytes(stream.CurrentSize, stream.IsBlockCompressed);
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

		private void AgeRetiredModels()
		{
			for (int i = _retiredModels.Count - 1; i >= 0; i--)
			{
				var (model, ticksLeft) = _retiredModels[i];
				if (ticksLeft <= 1)
				{
					model.Release();
					_retiredModels.RemoveAt(i);
				}
				else
				{
					_retiredModels[i] = (model, ticksLeft - 1);
				}
			}
		}

		private void EvictIdle(float deltaTime)
		{
			_evictScratch.Clear();
			foreach (var entry in _entries.Values)
			{
				if (entry.RefCount > 0 || !entry.Ready)
				{
					if (entry.RefCount > 0)
					{
						entry.IdleSeconds = 0f;
					}
					continue;
				}

				entry.IdleSeconds += deltaTime;
				if (entry.IdleSeconds >= UnloadAfterSeconds)
				{
					_evictScratch.Add(entry);
				}
			}

			// Ошибочные записи без ссылок тоже забываем.
			foreach (var entry in _entries.Values)
			{
				if (entry.RefCount <= 0 && entry.Failed && entry.Model == null && entry.Request == null &&
					!_evictScratch.Contains(entry))
				{
					_evictScratch.Add(entry);
				}
			}

			if (_evictScratch.Count == 0)
			{
				return;
			}

			foreach (var entry in _evictScratch)
			{
				_entries.Remove(entry.Key);

				if (entry.Model != null)
				{
					// Синхронно: подписчики обязаны снять свои регистрации ДО того, как эта модель
					// уйдёт в отложенный Release - см. class-doc и BeforeModelEvicted.
					BeforeModelEvicted?.Invoke(entry.Model);
					_retiredModels.Add((entry.Model, RetireTicks));
				}

				entry.Model = null;
			}

			RecomputeTextureBytes();
		}

		/// <summary>
		/// Полная остановка стола (закрытие редактора и т.п.): отменяет фоновые загрузки и освобождает
		/// ВСЕ резидентные модели немедленно, барьерясь на GPU один раз в начале - в отличие от
		/// <see cref="EvictIdle"/>, которая НЕ ждёт GPU (см. class-doc). Вызывающий обязан снять ВСЕ
		/// регистрации во ВСЕХ своих батч-рендерерах ДО вызова (тот же протокол, что и per-entry
		/// <see cref="BeforeModelEvicted"/>, но для абсолютно всех записей разом).
		/// </summary>
		public void Shutdown()
		{
			foreach (var entry in _entries.Values)
			{
				if (entry.Request != null)
				{
					if (entry.Finalizing)
					{
						EditorConsoleLog.Add(LogLevel.Warning,
							$"Model store: shutdown cancelled mid-finalize load of '{entry.Path}' - partial GPU resources leaked.");
					}

					entry.Cts?.Cancel();
					FinishRequest(entry);
				}
			}

			_textureJobs.Clear();
			_pendingLevels.Clear();
			_pendingLevelBytes = 0;
			_graphicsApi.WaitForGpuIdle();

			foreach (var (texture, _) in _retiredTextures)
			{
				texture.Release();
			}

			_retiredTextures.Clear();

			foreach (var (model, _) in _retiredModels)
			{
				model.Release();
			}

			_retiredModels.Clear();

			foreach (var entry in _entries.Values)
			{
				if (entry.Model != null)
				{
					BeforeModelEvicted?.Invoke(entry.Model);
					entry.Model.Release();
					entry.Model = null;
				}
			}

			_entries.Clear();
			_textureBytes = 0;
			_budgetReported = false;
		}

		private static void FinishRequest(Entry entry)
		{
			if (entry.Cts != null)
			{
				entry.Cts.Dispose();
				entry.Cts = null;
			}

			entry.Request = null;
			entry.Finalizing = false;
		}
	}
}
