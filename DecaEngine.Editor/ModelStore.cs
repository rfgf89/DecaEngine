using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using DecaEngine.Core;
using DecaEngine.Core.Diagnostics;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Assets;
using UnsafeCollections.Collections.Unsafe;

namespace DecaEngine.Editor.ECS
{
	/// <summary>
	/// Refcounted, device-wide store of loaded models keyed by (absolute path, options signature).
	/// Geometry, textures, samplers, shaders and CPU-side parsed data are shared across acquirers;
	/// IMaterialObject sets are NOT - registering into a batch renderer mutates them, so each
	/// acquirer builds its own set via <see cref="AcquireMaterialSet"/>. Eviction is barrier-free:
	/// there is no in-flight-frame fence, so GPU releases are deferred by <see cref="RetireTicks"/>
	/// and <see cref="BeforeModelEvicted"/> subscribers must unregister synchronously.
	/// </summary>
	public sealed class ModelStore
	{
		/// <summary>Caller-held reference to one (path, options) entry; Acquire/Release must pair 1:1.</summary>
		public sealed class Handle
		{
			internal readonly Entry Entry;
			internal bool Released;

			/// <summary>Load-order hint, LOWER loads first; entry priority is the minimum over its handles.</summary>
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

			/// <summary>Latched by the first AcquireMaterialSet call; later calls always build fresh sets.</summary>
			internal bool PrimaryMaterialSetTaken;

			internal readonly List<Handle> Handles = new();
			internal float IdleSeconds;
			internal ModelLoader.ModelLoadRequest? Request;
			internal CancellationTokenSource? Cts;

			/// <summary>FinalizeChunk has created partial GPU resources; abandoning mid-finalize leaks them, so it always runs to completion.</summary>
			internal bool Finalizing;

			/// <summary>Latched once every streamed texture reached ShowTextureSize (or waiting is pointless); consumers must not show the model before this or it appears with 1x1 fillers.</summary>
			internal bool TexturesReady;

			/// <summary>Seconds spent waiting for TexturesReady; guards against a forever-invisible model.</summary>
			internal float TextureWaitSeconds;

			/// <summary>Count of failed texture decodes; without a summary the white model looks like a streaming stall.</summary>
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

		// Tick scratch lists - no per-frame allocations.
		private readonly List<Entry> _startScratch = new();
		private readonly List<Entry> _evictScratch = new();

		/// <summary>One background decode produces the whole quality ladder; the file is read and decoded exactly once per texture lifetime.</summary>
		private sealed class TextureUpgradeJob
		{
			public required Entry Entry;
			public required ModelLoader Model;
			public required ModelLoader.StreamedTexture Stream;
			public required int RequestedSize;
			public required System.Threading.Tasks.Task<List<ModelLoader.StreamedTextureLevel>> DecodeTask;
		}

		/// <summary>Decoded but not yet uploaded levels of one texture, ascending by size; uploaded one per tick.</summary>
		private sealed class PendingTextureLevels
		{
			public required Entry Entry;
			public required ModelLoader Model;
			public required ModelLoader.StreamedTexture Stream;
			public required List<ModelLoader.StreamedTextureLevel> Levels;
			public required int RequestedSize;
			public int Next;
		}

		/// <summary>Background decodes currently running (up to MaxConcurrentTextureDecodes), pure CPU on the thread pool.</summary>
		private readonly List<TextureUpgradeJob> _textureJobs = new();

		/// <summary>Decoded levels awaiting GPU upload; their total bytes throttle new decodes (see PendingDecodeBytesBudget).</summary>
		private readonly List<PendingTextureLevels> _pendingLevels = new();

		private long _pendingLevelBytes;

		/// <summary>Upgrades hit TextureMemoryBudgetBytes: pending entries are declared ready as-is rather than waiting forever.</summary>
		private bool _upgradesStalled;

		/// <summary>Replaced GPU textures awaiting deferred Release: the GPU may still read them for a few frames (no fence).</summary>
		private readonly List<(IGpuTexture Texture, int TicksLeft)> _retiredTextures = new();

		/// <summary>Evicted models awaiting deferred ModelLoader.Release, same reason as _retiredTextures.</summary>
		private readonly List<(ModelLoader Model, int TicksLeft)> _retiredModels = new();

		private const int RetireTicks = 8;

		public int TextureStepFactor { get; set; } = 4;

		/// <summary>First quality step; showing the model waits for it, and a tiny decode arrives in a few frames.</summary>
		public int InitialTextureSize { get; set; } = 64;

		/// <summary>Minimum texture side at which the model may be shown; quality catches up in the background afterwards.</summary>
		public int ShowTextureSize { get; set; } = 64;

		/// <summary>Per-tick texture upload budget in ms for already-shown models. Time, not count: upload cost varies two orders of magnitude with level size.</summary>
		public float TextureUploadMillisecondsPerTick { get; set; } = 1.5f;

		/// <summary>Same budget while any model is still waiting to be shown - much more generous, since its textures gate visibility.</summary>
		public float PendingTextureUploadMillisecondsPerTick { get; set; } = 6f;

		/// <summary>Concurrent background decodes; decode is the dominant streaming cost (4K PNGs only decompress at full resolution).</summary>
		public int MaxConcurrentTextureDecodes { get; set; } = Math.Max(2, Environment.ProcessorCount - 2);

		/// <summary>CPU-memory cap for decoded-but-unuploaded levels. Too tight a cap stalls decodes entirely (one 4K ladder is ~22 MB).</summary>
		public long PendingDecodeBytesBudget { get; set; } = 512L << 20;

		/// <summary>Max wait for texture readiness of a not-yet-shown model before declaring it ready at current quality.</summary>
		public float TextureWaitTimeoutSeconds { get; set; } = 8f;

		/// <summary>Target side when ModelLoadOptions.MaxTextureSize is 0 (unlimited).</summary>
		private const int DefaultTextureTargetSize = 4096;

		/// <summary>The quality ceiling never drops below this side; savings below it are negligible.</summary>
		private const int MinQualityCeiling = 256;

		/// <summary>This tick's quality ceiling (see ComputeQualityCeiling).</summary>
		private int _qualityCeiling = DefaultTextureTargetSize;

		/// <summary>Memory cap for streamed textures across ALL resident models - process-wide, since models are resident once, not per environment.</summary>
		public long TextureMemoryBudgetBytes { get; set; } = 1024L << 20;

		private long _textureBytes;
		private bool _budgetReported;

		public int MaxConcurrentLoads { get; set; } = 2;

		/// <summary>Seconds a zero-refcount entry stays resident before GPU eviction; hysteresis against per-frame Acquire/Release churn.</summary>
		public float UnloadAfterSeconds { get; set; } = 4f;

		/// <summary>Model finished loading and its primary material set is ready. Does not say WHICH handle; subscribers filter by path/handle themselves.</summary>
		public event Action<ModelLoader>? ModelReady;

		/// <summary>Model textures reached show quality; only from this point should the model be displayed. Always fires AFTER ModelReady for the same model.</summary>
		public event Action<ModelLoader>? ModelTexturesReady;

		/// <summary>Entry is about to be evicted; subscribers MUST synchronously unregister their registrations of this model (compare by reference) before returning.</summary>
		public event Action<ModelLoader>? BeforeModelEvicted;

		public ModelStore(IGraphicsApi graphicsApi)
		{
			_graphicsApi = graphicsApi;
		}

		/// <summary>Number of (path, options) entries currently resident or loading.</summary>
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
			normalizedPath + "" + options.Signature();

		/// <summary>Acquires a reference to the model (load starts from Tick); each Acquire needs exactly one Release with the returned handle.</summary>
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
					EngineLog.Add(LogLevel.Warning, $"Model store: {entry.Error} ('{normalizedPath}')");
				}
			}

			var handle = new Handle(entry, priority);
			entry.Handles.Add(handle);
			entry.IdleSeconds = 0f;
			return handle;
		}

		/// <summary>Releases the handle; the model is not unloaded immediately (see UnloadAfterSeconds). Idempotent.</summary>
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

		/// <summary>True once this handle's model textures reached show quality; false for a not-yet-loaded model.</summary>
		public bool AreTexturesReady(Handle handle) => handle.Entry.TexturesReady;

		/// <summary>One-line streaming state snapshot for debug probes; distinguishes the several indistinguishable causes of white textures.</summary>
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

					// Compare against the ceiling, not raw MaxTextureSize: under a memory budget
					// "done" means reaching the ceiling.
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
		/// to register into its own batch renderer - materials cannot be shared across environments.
		/// The first call on an entry gets the model's own materialObjects; every later call gets a
		/// fresh BuildAdditionalMaterialSet set. Latched on the ENTRY, so re-calling on the same
		/// handle after abandoning its set always yields a fresh additional set.
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
		/// Per-frame step (main/GPU thread, under the GPU lock): starts queued loads by priority,
		/// polls background prepare tasks, finalizes ONE model per frame, streams texture quality
		/// and evicts idle entries.
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
					EngineLog.Add(LogLevel.Error, $"Model store: failed to load '{entry.Path}': {ex.Message}");
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

				// All handles released before the load finished - cancel the background decode.
				// An in-progress finalization is never abandoned (see Entry.Finalizing).
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
					EngineLog.Add(LogLevel.Error, $"Model store: failed to load '{entry.Path}': {entry.Error}");
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
				EngineLog.Add(LogLevel.Error, $"Model store: failed to finalize '{best.Path}': {ex.Message}");
				return;
			}

			if (model == null)
			{
				return; // frame budget exhausted - continues next frame
			}

			FinishRequest(best);
			best.Model = model;
			best.IdleSeconds = 0f;

			var t = model.Timings;
			EngineLog.Add(LogLevel.Info,
				$"Model load '{Path.GetFileName(best.Path)}': " +
				$"parse {t.ParseMs} ms, materials {t.MaterialsMs} ms, meshes {t.MeshesMs} ms, " +
				$"finalize {t.FinalizeMs} ms (shaders {t.ShaderMs} ms / {t.ShaderVariants} variants, " +
				$"PSO+material {t.MaterialBuildMs} ms / {t.MaterialsBuilt}, " +
				$"mesh upload {t.MeshMs} ms / {t.MeshUploads}, samplers {t.SamplerMs} ms / {t.Samplers})");

			ModelReady?.Invoke(model);
		}

		/// <summary>
		/// Progressive texture quality streaming: background decode, one upload/hot-swap per tick,
		/// one queue for the whole process (which also fixes the race of two environments upgrading
		/// the same shared StreamedTexture). Hot swaps fan out to all live material sets via
		/// StreamedTexture.Bindings. There is no distance-based quality cap here - the store does
		/// not know per-environment cameras; upgrades go to MaxTextureSize ordered by BestPriority.
		/// Entries not yet TexturesReady are served out of order: their first level gates visibility.
		/// </summary>
		private void PumpTextureUpgrades()
		{
			_qualityCeiling = ComputeQualityCeiling();

			// Reset BEFORE the whole pump, not inside StartTextureUpgrades: both uploads and decode
			// starts set the flag, and AnnounceTextureReadiness reads it AFTER the pump. Resetting
			// at the end would erase a stall that happened during upload.
			_upgradesStalled = false;

			CollectTextureDecodes();
			UploadPendingLevels();
			StartTextureUpgrades();
		}

		/// <summary>Collects finished background decodes; moving levels to the upload queue is just reference shuffling and costs no tick budget.</summary>
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

					// Log details only for the FIRST failed texture; the cause is usually shared
					// (unsupported format) and AnnounceTextureReadiness prints the per-model summary.
					if (job.Entry.TextureDecodeFailures++ == 0)
					{
						EngineLog.Add(LogLevel.Warning,
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
				// Nothing to decode (source missing/empty) - otherwise the texture would count as
				// "not ready" forever and block showing the model.
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

		/// <summary>Uploads ready levels and hot-swaps textures until the per-tick TIME budget runs out; a count-based budget either starves small levels or hitches on large ones.</summary>
		private void UploadPendingLevels()
		{
			if (_pendingLevels.Count == 0)
			{
				return;
			}

			// Generous budget kicks in exactly while some model still waits to be shown: it is not
			// in frame yet, so uploads cannot cause a visible hitch.
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

			// Two passes; the order IS the model-appearance latency. First: textures still on 1x1
			// fillers (below ShowTextureSize) - while any exists the model is hidden entirely.
			// Second pass is cosmetic quality catch-up.
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

					// Paused means the MEMORY budget, not the time budget: the level stays queued
					// and the tick moves on to uploads that can proceed.
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
			/// <summary>Level uploaded, more remain in the queue.</summary>
			Uploaded,

			/// <summary>Texture memory budget exceeded. The queue is kept: dropping it would freeze the texture at current quality forever (no new decode starts).</summary>
			Paused,

			/// <summary>Ladder fully uploaded - streaming of this texture is done.</summary>
			Finished,

			/// <summary>Upload failed - nothing left to retry with.</summary>
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

				// Check BEFORE dequeuing the level: on refusal it must stay in place.
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
					// The level knows what it is: decoded RGBA8 (GPU builds mips) or a baked BC
					// tail from .dtex (mips included). See ModelLoader.StreamedTextureLevel.
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
					EngineLog.Add(LogLevel.Warning,
						$"Model store: texture upgrade upload failed for '{queued.Entry.Path}': {ex.Message}");
					return LevelUpload.Failed;
				}

				ConsumeLevel(queued);
				return queued.Next >= queued.Levels.Count ? LevelUpload.Finished : LevelUpload.Uploaded;
			}

			return LevelUpload.Finished;
		}

		/// <summary>Dequeues a level and releases its array immediately; keeping tens of MB in CPU memory until the ladder finishes is wasteful.</summary>
		private void ConsumeLevel(PendingTextureLevels queued)
		{
			var index = queued.Next++;

			_pendingLevelBytes -= queued.Levels[index]?.ByteLength ?? 0;

			queued.Levels[index] = null!;
		}

		/// <summary>Drops the whole level queue and closes streaming for the texture: the ladder is decoded once and for all, so the source is never needed again.</summary>
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
			EngineLog.Add(LogLevel.Info,
				$"Model store: texture memory budget reached ({_textureBytes >> 20} MB) - quality upgrades paused.");
		}

		/// <summary>Tops background decodes up to MaxConcurrentTextureDecodes; decode speed gates how fast a hidden model reaches TexturesReady.</summary>
		private void StartTextureUpgrades()
		{
			// Hold decodes while decoded levels await upload: the whole ladder sits in CPU memory
			// until uploaded, and uploads are far slower than decodes.
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

				// A model nobody can show yet outranks any quality upgrade of a visible one: its
				// textures gate visibility, they are not cosmetic.
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

			// The first level must fit the budget or the model never appears; later levels are
			// checked one by one at upload, so a full budget truncates the ladder, not cancels it.
			var firstSize = Math.Min(Math.Max(16, InitialTextureSize), targetSize);
			var delta = EstimateTextureBytes(firstSize, bestStream.IsBlockCompressed) -
				EstimateTextureBytes(bestStream.CurrentSize, bestStream.IsBlockCompressed);
			if (_textureBytes + delta > TextureMemoryBudgetBytes)
			{
				_upgradesStalled = true;
				ReportTextureBudget();
				return false;
			}

			// ONE decode per texture lifetime: stb only decompresses at full resolution, so a
			// "64px" step costs the same as full quality and per-step decodes would mean N full
			// decompressions of one file. All steps come from one downscale chain.
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

			var decoded = ModelImporter.DecodeEncodedImageLadder(encoded, targetSize, firstSize, stepFactor);
			var levels = new List<ModelLoader.StreamedTextureLevel>(decoded.Count);

			foreach (var (pixels, width, height) in decoded)
			{
				levels.Add(ModelLoader.StreamedTextureLevel.FromDecodedPixels(pixels, width, height));
			}

			return levels;
		}

		/// <summary>
		/// Quality ladder straight from a baked .dtex - no decode, one disk read. Mips are stored
		/// large-to-small, so "texture at size S" is the tail of the chain: only the needed tail is
		/// read, and every step is a sub-array of that one read (zero extra I/O and CPU per step).
		/// </summary>
		private static List<ModelLoader.StreamedTextureLevel> BuildBakedLadder(
			ModelLoader.StreamedTexture stream, int targetSize, int firstSize, int stepFactor)
		{
			var levels = new List<ModelLoader.StreamedTextureLevel>();

			// The target-quality level is the TOPMOST (smallest index) needed; the tail starts there.
			int topLevel = DtexFile.LevelForSize(stream.DtexWidth, stream.DtexHeight, targetSize);

			var payload = DtexFile.TryReadFromLevel(stream.DtexPath, topLevel);
			if (payload == null)
			{
				return levels;
			}

			// Steps go bottom-up in quality, i.e. from the last tail levels toward the first.
			int firstIndex = DtexFile.LevelForSize(payload.Width, payload.Height, firstSize);
			firstIndex = Math.Min(firstIndex, payload.Mips.Length - 1);

			// The step is a SIDE multiplier (TextureStepFactor) while levels are powers of two -
			// convert one into the other.
			int step = Math.Max(1, (int)Math.Round(Math.Log2(stepFactor)));

			for (int index = firstIndex; ; index = Math.Max(0, index - step))
			{
				levels.Add(ModelLoader.StreamedTextureLevel.FromCompressed(
					payload.Format,
					payload.Mips[index..],
					Math.Max(1, payload.Width >> index),
					Math.Max(1, payload.Height >> index)));

				// Level 0 is the target quality; the ladder always ends there regardless of step.
				if (index == 0)
				{
					break;
				}
			}

			return levels;
		}

		/// <summary>True if the texture is being decoded OR its levels await upload; re-decoding while a ladder is pending would be a wasted full decompression.</summary>
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

		/// <summary>Quality cap for this texture, respecting the global memory budget (see ComputeQualityCeiling).</summary>
		private int TargetSizeFor(ModelLoader.StreamedTexture stream) =>
			Math.Max(EffectiveShowSize(stream), Math.Min(EffectiveTargetSize(stream), _qualityCeiling));

		/// <summary>
		/// Largest side such that ALL resident textures fit TextureMemoryBudgetBytes, not just the
		/// first arrivals. Without it streaming degrades into a race: the first textures take the
		/// whole budget at max size and the rest stay on 1x1 fillers - a half-white model. Even
		/// quality for everyone is the only sensible outcome here.
		/// </summary>
		private int ComputeQualityCeiling()
		{
			// Compressed and uncompressed are counted separately: they differ 4x, and a shared
			// counter would under-cap a scene of baked textures by the same factor.
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

		/// <summary>Side at which a texture is presentable (see ShowTextureSize); capped at the source's own target for small sources.</summary>
		private int EffectiveShowSize(ModelLoader.StreamedTexture stream) =>
			Math.Min(Math.Max(16, ShowTextureSize), EffectiveTargetSize(stream));

		/// <summary>
		/// Latches Entry.TexturesReady and fires ModelTexturesReady - the moment the model can be
		/// shown without texture popping. Ready = every stream either exhausted its source or
		/// reached EffectiveShowSize (the FIRST step, not full quality). Two escapes from a
		/// forever-invisible model: a stalled memory budget and TextureWaitTimeoutSeconds.
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
						EngineLog.Add(LogLevel.Info,
							$"Model store: showing '{Path.GetFileName(entry.Path)}' at current texture quality - " +
							"upgrades are paused by the memory budget.");
					}
					else if (entry.TextureWaitSeconds >= TextureWaitTimeoutSeconds)
					{
						EngineLog.Add(LogLevel.Warning,
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

				// The key fact for a white model: it is not a streaming stall but unreadable
				// texture formats. The decoder reads PNG/JPG only; DDS/KTX2 are not opened at all.
				if (entry.TextureDecodeFailures > 0)
				{
					EngineLog.Add(LogLevel.Warning,
						$"Model store: {entry.TextureDecodeFailures} of {entry.Model.StreamedTextures.Count} textures " +
						$"failed to decode for '{Path.GetFileName(entry.Path)}' - those materials stay untextured. " +
						"Supported source formats are PNG and JPEG (DDS/KTX2 are not decoded).");
				}
			}

			if (announced == null)
			{
				return;
			}

			// Invoked OUTSIDE the _entries loop: a subscriber showing the model may Acquire new
			// entries, mutating the dictionary under foreach.
			foreach (var entry in announced)
			{
				if (entry.Model != null)
				{
					ModelTexturesReady?.Invoke(entry.Model);
				}
			}
		}

		/// <summary>
		/// VRAM estimate for a texture of the given side with a full mip chain (hence the 4/3).
		/// Block-compressed (BC7/BC5: 1 byte/texel vs 4 for RGBA8) must be counted separately or
		/// the budget would see 4x the real usage; BC1/BC4 are smaller still but overestimating
		/// them is safer than underestimating.
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

			// Unreferenced failed entries are forgotten too.
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
					// Synchronous: subscribers must unregister BEFORE the model goes into deferred
					// Release - see class doc and BeforeModelEvicted.
					BeforeModelEvicted?.Invoke(entry.Model);
					_retiredModels.Add((entry.Model, RetireTicks));
				}

				entry.Model = null;
			}

			RecomputeTextureBytes();
		}

		/// <summary>
		/// Full shutdown (editor close): cancels background loads and releases ALL resident models
		/// immediately, barriering on the GPU once - unlike EvictIdle, which never waits. The
		/// caller must unregister everything from all its batch renderers BEFORE calling.
		/// </summary>
		public void Shutdown()
		{
			foreach (var entry in _entries.Values)
			{
				if (entry.Request != null)
				{
					if (entry.Finalizing)
					{
						EngineLog.Add(LogLevel.Warning,
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
