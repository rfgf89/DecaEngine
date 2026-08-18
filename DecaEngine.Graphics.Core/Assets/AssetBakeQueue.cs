namespace DecaEngine.Graphics.Assets;

/// <summary>
/// Фоновая печка ассетов: одна очередь, один рабочий поток пониженного приоритета на весь процесс.
///
/// Ставится в очередь ПРОМАХ кеша. Сама загрузка при этом идёт обычным путём и ничего не ждёт -
/// в этом вся идея: включение пайплайна не имеет права сделать первое открытие модели медленнее,
/// чем оно было без него. Печка догоняет в фоне, и уже следующее открытие идёт из кеша.
///
/// Поток ровно один, и это не экономия на спичках. Бейк - это полный декод всех картинок модели
/// плюс BC-кодирование, то есть и сотни мегабайт пиковой памяти, и все ядра под нагрузкой. Две
/// такие задачи параллельно (а в редакторе легко ткнуть подряд в пять моделей в браузере ассетов)
/// удваивают пик памяти и отбирают у рендера ровно то время, ради которого фоновая печка и
/// затевалась.
/// </summary>
public static class AssetBakeQueue
{
	private sealed record Job(string ModelPath, ModelLoadOptions Options, string ModelKey);

	private static readonly Lock Gate = new();
	private static readonly Queue<Job> Pending = new();

	/// <summary>Ключи, уже поставленные в очередь или обработанные за эту сессию. Держит очередь от
	/// повторов: браузер ассетов переоткрывает одну и ту же модель десятки раз за сессию, и без
	/// этого фильтра каждая попытка ставила бы ещё одну задачу на уже идущий бейк.</summary>
	private static readonly HashSet<string> Seen = new(StringComparer.Ordinal);

	private static readonly CancellationTokenSource ShutdownSource = new();
	private static Thread _worker;
	private static bool _draining;

	/// <summary>Сколько задач ждёт очереди - для индикатора в редакторе.</summary>
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

	/// <summary>Сообщения об ошибках бейка. Бейк фоновый и НЕ обязан валить загрузку - модель
	/// прекрасно грузится и без кеша, - но и молчать о том, что кеш никогда не наполнится, нельзя.</summary>
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

					// Ниже нормального намеренно: печка соревнуется за ядра с потоком рендера и с
					// фоновыми загрузками других моделей, а её результат нужен не сейчас, а в
					// следующей сессии. Проигрывать эту гонку - правильное поведение.
					Priority = ThreadPriority.BelowNormal,
				};

				_worker.Start();
			}
		}
	}

	/// <summary>Останавливает печку. Текущая задача дорабатывает до ближайшей точки отмены; всё
	/// записанное на диск остаётся валидным (контейнеры пишутся атомарно, см. DtexFile.Write).</summary>
	public static void Stop() => ShutdownSource.Cancel();

	/// <summary>
	/// Блокирует вызывающего, пока очередь не опустеет (или не выйдет <paramref name="timeout"/>).
	/// Возвращает true, если печка успела всё.
	///
	/// Для UI это НЕ путь - там очередь и нужна затем, чтобы никто её не ждал. Нужно пробникам и
	/// пакетному прогреву кеша («запечь весь проект»), где смысл запуска ровно в том, чтобы дождаться
	/// результата.
	/// </summary>
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
				// Провал бейка одной модели не должен ни валить редактор, ни останавливать очередь:
				// битый или экзотический glTF - это норма жизни, а остальные модели ни при чём.
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

		// Модель готовится ЗАНОВО, а не переиспользуется из уже идущей загрузки, и это осознанно.
		// Тот PreparedModel уезжает на поток рендера, где его пиксели заливаются в текстуры и тут же
		// освобождаются, - трогать его отсюда значило бы гонку на каждом буфере. Повторная подготовка
		// стоит одного лишнего разбора на модель ЗА ВСЮ ЕЁ ЖИЗНЬ, зато не имеет разделяемого
		// состояния вовсе.
		//
		// Стриминг для бейка выключается: он существует ради быстрого первого кадра и намеренно НЕ
		// декодирует картинки, а печь нечего без пикселей. CacheDirectory снимается, чтобы подготовка
		// не ушла в кеш рекурсивно.
		var bakeOptions = job.Options with { StreamTextures = false, CacheDirectory = null };

		var prepared = ModelLoader.PrepareForBake(job.ModelPath, bakeOptions, token);
		token.ThrowIfCancellationRequested();

		ModelAssetBaker.BakeTextures(prepared, cache, job.Options, token);
		token.ThrowIfCancellationRequested();

		CookedModelFile.Write(cache.ModelPath(job.ModelKey), prepared);
	}
}
