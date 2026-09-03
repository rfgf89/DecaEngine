namespace DecaEngine.Graphics.Assets;

/// <summary>Background asset baking: one queue, one low-priority worker per process.</summary>
// Exactly one thread: a bake decodes every texture and BC-encodes it, so two in parallel would
// double peak memory and steal the cores the queue exists to protect.
public static class AssetBakeQueue
{
	private sealed record Job(string ModelPath, ModelLoadOptions Options, string ModelKey);

	private static readonly Lock Gate = new();
	private static readonly Queue<Job> Pending = new();

	// Keys queued or handled this session; keeps repeated opens from re-queuing a running bake.
	private static readonly HashSet<string> Seen = new(StringComparer.Ordinal);

	private static readonly CancellationTokenSource ShutdownSource = new();
	private static Thread _worker;
	private static bool _draining;

	/// <summary>Number of jobs still waiting, for the editor indicator.</summary>
	public static int PendingCount
	{
		get
		{
			lock (Gate)
			{
				return Pending.Count + (_draining ? 1 : 0);
			}
		}
	}

	/// <summary>Raised when a bake fails; loading itself is unaffected.</summary>
	public static event Action<string, Exception> BakeFailed;

	internal static void Enqueue(string modelPath, ModelLoadOptions options, string modelKey)
	{
		lock (Gate)
		{
			if (ShutdownSource.IsCancellationRequested || !Seen.Add(modelKey))
			{
				return;
			}

			Pending.Enqueue(new Job(modelPath, options, modelKey));

			if (_worker == null)
			{
				_worker = new Thread(Run)
				{
					IsBackground = true,
					Name = "DecaEngine asset bake",

					// Deliberately below normal: the result is needed next session, not now.
					Priority = ThreadPriority.BelowNormal,
				};

				_worker.Start();
			}
		}
	}

	/// <summary>Stops the queue; anything already on disk stays valid (atomic writes).</summary>
	public static void Stop() => ShutdownSource.Cancel();

	/// <summary>Blocks until the queue drains; for probes and batch prewarm, never for UI.</summary>
	public static bool WaitForIdle(TimeSpan timeout)
	{
		var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

		while (PendingCount > 0)
		{
			if (Environment.TickCount64 >= deadline || ShutdownSource.IsCancellationRequested)
			{
				return false;
			}

			Thread.Sleep(50);
		}

		return true;
	}

	private static void Run()
	{
		var token = ShutdownSource.Token;

		while (!token.IsCancellationRequested)
		{
			Job job;

			lock (Gate)
			{
				if (Pending.Count == 0)
				{
					_worker = null;
					_draining = false;
					return;
				}

				job = Pending.Dequeue();
				_draining = true;
			}

			try
			{
				Bake(job, token);
			}
			catch (OperationCanceledException)
			{
				return;
			}
			catch (Exception ex)
			{
				// One bad model must not stop the queue.
				BakeFailed?.Invoke(job.ModelPath, ex);
			}
			finally
			{
				lock (Gate)
				{
					_draining = false;
				}
			}
		}
	}

	private static void Bake(Job job, CancellationToken token)
	{
		var cache = job.Options.Cache;
		if (cache == null)
		{
			return;
		}

		// The model is prepared from scratch on purpose: the in-flight PreparedModel belongs to
		// the render thread, which frees its pixels. Streaming is off (a bake needs pixels) and
		// CacheDirectory is cleared so preparation does not recurse into the cache.
		var bakeOptions = job.Options with { StreamTextures = false, CacheDirectory = null };

		var prepared = ModelImporter.PrepareForBake(job.ModelPath, bakeOptions, token);
		token.ThrowIfCancellationRequested();

		ModelAssetBaker.BakeTextures(prepared, cache, job.Options, token);
		token.ThrowIfCancellationRequested();

		CookedModelFile.Write(cache.ModelPath(job.ModelKey), prepared);
	}
}
