using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using DecaEngine.Core;
using DecaEngine.Graphics;
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

		private sealed class TextureUpgradeJob
		{
			public required Entry Entry;
			public required ModelLoader Model;
			public required ModelLoader.StreamedTexture Stream;
			public required int RequestedSize;
			public required System.Threading.Tasks.Task<(byte[] Pixels, int Width, int Height)> DecodeTask;
		}

		private TextureUpgradeJob? _textureJob;

		/// <summary>Заменённые апгрейдами GPU-текстуры, ждущие отложенного Release - см. class-doc:
		/// движок не ждёт GPU на партиционном выселении, поэтому старая текстура должна пережить ещё
		/// несколько тиков после того, как все SRB перестали на неё ссылаться.</summary>
		private readonly List<(IGpuTexture Texture, int TicksLeft)> _retiredTextures = new();

		/// <summary>Модели, выселенные <see cref="EvictIdle"/> и ждущие отложенного
		/// <see cref="ModelLoader.Release"/> - тем же приёмом, что и <see cref="_retiredTextures"/>.</summary>
		private readonly List<(ModelLoader Model, int TicksLeft)> _retiredModels = new();

		private const int RetireTicks = 8;

		public int TextureStepFactor { get; set; } = 4;
		public int InitialTextureSize { get; set; } = 64;

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

				var alive = _entries.TryGetValue(job.Entry.Key, out var current) &&
					ReferenceEquals(current, job.Entry) && ReferenceEquals(job.Entry.Model, job.Model);

				if (!job.DecodeTask.IsCompletedSuccessfully)
				{
					if (alive)
					{
						job.Stream.ReleaseCpuData();
						EditorConsoleLog.Add(LogLevel.Warning,
							$"Model store: texture upgrade decode failed for '{job.Entry.Path}': " +
							$"{job.DecodeTask.Exception?.GetBaseException().Message}");
					}

					return;
				}

				if (!alive)
				{
					return;
				}

				var (pixels, width, height) = job.DecodeTask.Result;
				var entry = job.Stream;

				if (pixels == null || Math.Max(width, height) <= entry.CurrentSize)
				{
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
						$"Model store: texture upgrade upload failed for '{job.Entry.Path}': {ex.Message}");
				}

				return; // одна заливка за Tick
			}

			Entry? bestEntry = null;
			ModelLoader.StreamedTexture? bestStream = null;
			var bestPriority = float.MaxValue;
			var bestSize = int.MaxValue;

			foreach (var entry in _entries.Values)
			{
				if (entry.Model == null || entry.RefCount <= 0)
				{
					continue;
				}

				var priority = entry.BestPriority;

				foreach (var stream in entry.Model.StreamedTextures)
				{
					if (!stream.HasSource)
					{
						continue;
					}

					var target = stream.TargetSize > 0 ? stream.TargetSize : 4096;
					if (stream.CurrentSize >= target)
					{
						continue;
					}

					if (priority < bestPriority || (priority == bestPriority && stream.CurrentSize < bestSize))
					{
						bestEntry = entry;
						bestStream = stream;
						bestPriority = priority;
						bestSize = stream.CurrentSize;
					}
				}
			}

			if (bestStream == null)
			{
				return;
			}

			var targetSize = bestStream.TargetSize > 0 ? bestStream.TargetSize : 4096;
			var nextSize = bestStream.CurrentSize <= 0
				? Math.Max(16, InitialTextureSize)
				: bestStream.CurrentSize * Math.Max(2, TextureStepFactor);
			nextSize = Math.Min(nextSize, targetSize);

			if (nextSize <= bestStream.CurrentSize)
			{
				return;
			}

			var delta = EstimateTextureBytes(nextSize) - EstimateTextureBytes(bestStream.CurrentSize);
			if (_textureBytes + delta > TextureMemoryBudgetBytes)
			{
				if (!_budgetReported)
				{
					_budgetReported = true;
					EditorConsoleLog.Add(LogLevel.Info,
						$"Model store: texture memory budget reached ({_textureBytes >> 20} MB) - quality upgrades paused.");
				}

				return;
			}

			var source = bestStream;
			var requestedSize = nextSize;
			_textureJob = new TextureUpgradeJob
			{
				Entry = bestEntry!,
				Model = bestEntry!.Model!,
				Stream = bestStream,
				RequestedSize = requestedSize,
				DecodeTask = System.Threading.Tasks.Task.Run(() =>
				{
					var encoded = source.ReadEncoded();
					return encoded == null
						? ((byte[])null!, 0, 0)
						: ModelLoader.DecodeEncodedImage(encoded, requestedSize);
				}),
			};
		}

		private static long EstimateTextureBytes(int size)
		{
			if (size <= 0)
			{
				return 0;
			}

			return (long)size * size * 4L * 4L / 3L;
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
					_textureBytes += EstimateTextureBytes(stream.CurrentSize);
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

			_textureJob = null;
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
