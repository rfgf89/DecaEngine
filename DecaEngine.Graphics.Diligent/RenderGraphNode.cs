namespace DecaEngine.Graphics.Diligent;

public class RenderGraphNode<TView, TViewDesc, TDesc, T>
	where TViewDesc : struct
	where TView : SharpGen.Runtime.DisposeBase
	where T : SharpGen.Runtime.DisposeBase
{
	public TView[] BindWrittenTargets { get; private set; } = [];
	public TView[] BindReadTargets { get; private set; } = [];

	public List<TViewDesc> ReadViewsDesc { get; } = new(32);
	public List<TViewDesc> WriteViewsDesc { get; } = new(32);

	private T[] RenderTargets { get; set; } = [];
	public List<TDesc> RenderTargetsDesc { get; } = new(32);

	private readonly Dictionary<string, int> _lastUsage = new(32);

	private CounterList renderTargetCount = new();
	private CounterList readTargetViewCount = new();
	private CounterList writeTargetViewCount = new();

	private List<int> readRenderTargetIndex = new(32);
	private List<int> writeRenderTargetIndex = new(32);

	private int _passIndex;

	private readonly Func<List<TDesc>, TViewDesc, int> findTargetDescIndex;
	private readonly Func<TViewDesc, string> getViewDescName;
	private readonly Func<TDesc, string> getTargetDescName;
	private readonly Func<TDesc, T> createTarget;
	private readonly Func<T, TViewDesc, TView> createView;
	private readonly Func<TDesc, TDesc, bool> descEquals;
	private readonly Func<TViewDesc, TViewDesc, bool> viewDescEquals;

	/// <summary>Одна физическая аллокация: нативный ресурс, дескриптор, под который он создан, и все
	/// вьюхи, когда-либо на него сделанные. Живёт ДОЛЬШЕ одной компиляции графа - см. <see cref="_pool"/>.</summary>
	private sealed class Entry
	{
		public TDesc Desc;
		public T Target;

		/// <summary>false - ресурс создан ВНЕ графа и лишь объявлен ему для учёта зависимостей и
		/// времён жизни (см. <see cref="RegisterExternal"/>): граф его не создаёт, не пулит и не
		/// освобождает.</summary>
		public bool Owned = true;

		public readonly List<KeyValuePair<TViewDesc, TView>> Views = new(4);

		public void DisposeAll()
		{
			if (!Owned)
			{
				return;
			}

			foreach (var pair in Views)
			{
				pair.Value?.Dispose();
			}

			Views.Clear();
			Target?.Dispose();
			Target = null;
		}
	}

	/// <summary>Внешние ресурсы, объявленные графу на текущей компиляции - см. <see cref="Entry.Owned"/>.
	/// Чистится вместе с остальным в <see cref="Clean"/>: объявления живут ровно одну компиляцию.</summary>
	private readonly Dictionary<string, T> _external = new(16);

	/// <summary>Объявляет графу уже существующий ресурс: он попадёт в зависимости и в отладочную
	/// раскладку времён жизни, но остаётся во владении того, кто его создал.</summary>
	public void RegisterExternal(string name, T target)
	{
		_external[name] = target;
	}

	/// <summary>Ресурсы, отданные ТЕКУЩЕЙ компиляции (по имени пина).</summary>
	private readonly Dictionary<string, Entry> _live = new(32);

	/// <summary>Ресурсы прошлых компиляций, ещё не востребованные текущей. Именно они делают
	/// пересборку графа дешёвой: тумблер фичи или смена сцены пересобирают СПИСОК пассов, но не
	/// пересоздают ни текстур, ни вьюх - пин с тем же именем и тем же дескриптором забирает
	/// готовый ресурс отсюда. Пин с тем же именем, но ДРУГИМ дескриптором (ресайз вьюпорта, смена
	/// формата) освобождает старый и создаёт новый, поэтому пул не растёт от ресайзов.</summary>
	private readonly Dictionary<string, Entry> _pool = new(32);

	/// <summary>Entry для каждого элемента <see cref="RenderTargetsDesc"/> - заполняется в
	/// <see cref="Allocate"/> параллельно с <see cref="RenderTargets"/>.</summary>
	private Entry[] _entries = [];

#if DEBUG
	private readonly Func<TDesc, ulong> getTargetDescSizeInBytes;
	private readonly Dictionary<string, int> _firstUsage = new(32);
	// Separate from _lastUsage: PostSetup() clears _lastUsage right after SetupPass finishes (before
	// Compile() even returns), so by the time the debug window reads it in Execute() it's always
	// empty and every resource's LastPassIndex silently defaults to 0. This copy is only ever
	// cleared in Clean(), so it survives until ExportLifetimes() actually needs it.
	private readonly Dictionary<string, int> _debugLastUsage = new(32);
#endif

	public RenderGraphNode(
		Func<List<TDesc>, TViewDesc, int> findTargetDescIndex,
		Func<TViewDesc, string> getViewDescName,
		Func<TDesc, string> getTargetDescName,
		Func<TDesc, T> createTarget,
		Func<T, TViewDesc, TView> createView,
		Func<TDesc, TDesc, bool> descEquals,
		Func<TViewDesc, TViewDesc, bool> viewDescEquals
#if DEBUG
		, Func<TDesc, ulong> getTargetDescSizeInBytes = null
#endif
		)
	{
		this.findTargetDescIndex = findTargetDescIndex;
		this.getViewDescName = getViewDescName;
		this.getTargetDescName = getTargetDescName;
		this.createTarget = createTarget;
		this.createView = createView;
		this.descEquals = descEquals;
		this.viewDescEquals = viewDescEquals;
#if DEBUG
		this.getTargetDescSizeInBytes = getTargetDescSizeInBytes ?? (_ => 0UL);
#endif
	}

	/// <param name="recycle">true - нативные ресурсы уходят в пул и переживут пересборку графа
	/// (см. <see cref="_pool"/>); false - освобождаются вместе с пулом (полный снос графа).
	/// Вызывающий обязан гарантировать, что кадры со старыми ресурсами уже не в полёте.</param>
	public void Clean(bool recycle = false)
	{
		foreach (var entry in _live.Values)
		{
			if (!entry.Owned)
			{
				// Объявленный внешний ресурс - не наш: ни в пул, ни на освобождение.
				continue;
			}

			if (recycle)
			{
				// Имя уникально в пределах компиляции (PinTexture дедуплицирует), так что затирания
				// живого пулового ресурса здесь быть не может.
				_pool[getTargetDescName(entry.Desc)] = entry;
			}
			else
			{
				entry.DisposeAll();
			}
		}

		_live.Clear();
		_external.Clear();

		if (!recycle)
		{
			foreach (var entry in _pool.Values)
			{
				entry.DisposeAll();
			}

			_pool.Clear();
		}

		BindWrittenTargets = [];
		BindReadTargets = [];
		RenderTargets = [];
		_entries = [];

		renderTargetCount.Clean();
		readTargetViewCount.Clean();
		writeTargetViewCount.Clean();

		readRenderTargetIndex.Clear();
		writeRenderTargetIndex.Clear();
		ReadViewsDesc.Clear();
		WriteViewsDesc.Clear();
		RenderTargetsDesc.Clear();
		_lastUsage.Clear();
		_passIndex = 0;
#if DEBUG
		_firstUsage.Clear();
		_debugLastUsage.Clear();
#endif
	}

	/// <summary>Освобождает всё, что лежит в пуле и не востребовано текущей компиляцией - точка, где
	/// реально возвращается VRAM выключенных фич. Живые ресурсы не трогает.</summary>
	public void TrimPool()
	{
		foreach (var entry in _pool.Values)
		{
			entry.DisposeAll();
		}

		_pool.Clear();
	}

	public void PostSetup()
	{
		for (var i = 0; i < ReadViewsDesc.Count; i++)
		{
			var find = findTargetDescIndex.Invoke(RenderTargetsDesc, ReadViewsDesc[i]);
			if (find != -1)
			{
				readRenderTargetIndex.Add(find);
			}
			else
			{
				throw new InvalidOperationException(
					$"Render graph resource '{getViewDescName(ReadViewsDesc[i])}' was read but never pinned.");
			}
		}

		for (var i = 0; i < WriteViewsDesc.Count; i++)
		{
			var find = findTargetDescIndex.Invoke(RenderTargetsDesc, WriteViewsDesc[i]);
			if (find != -1)
			{
				writeRenderTargetIndex.Add(find);
			}
			else
			{
				throw new InvalidOperationException(
					$"Render graph resource '{getViewDescName(WriteViewsDesc[i])}' was written but never pinned.");
			}
		}

		_lastUsage.Clear();

		RenderTargets = new T[RenderTargetsDesc.Count];
		_entries = new Entry[RenderTargetsDesc.Count];
		BindWrittenTargets = new TView[WriteViewsDesc.Count];
		BindReadTargets = new TView[ReadViewsDesc.Count];
	}

	public void SetupPass(int pass)
	{
		renderTargetCount.Add(RenderTargetsDesc);
		readTargetViewCount.Add(ReadViewsDesc);
		writeTargetViewCount.Add(WriteViewsDesc);

		for (int i = readTargetViewCount.pos[pass]; i < readTargetViewCount.counter[pass]; i++)
		{
			var name = getViewDescName(ReadViewsDesc[i]);
			_lastUsage[name] = pass;
#if DEBUG
			if (!_firstUsage.ContainsKey(name)) _firstUsage[name] = pass;
			_debugLastUsage[name] = pass;
#endif
		}

		for (int i = writeTargetViewCount.pos[pass]; i < writeTargetViewCount.counter[pass]; i++)
		{
			var name = getViewDescName(WriteViewsDesc[i]);
			_lastUsage[name] = pass;
#if DEBUG
			if (!_firstUsage.ContainsKey(name)) _firstUsage[name] = pass;
			_debugLastUsage[name] = pass;
#endif
		}

		_passIndex = pass;
	}

	public bool DependenceCheck(List<int> passAdjacencyList, int stIndex, int enIndex)
	{
		bool Conflicts(
			List<TViewDesc> first,
			int firstStart,
			int firstEnd,
			List<TViewDesc> second,
			int secondStart,
			int secondEnd)
		{
			for (int i = firstStart; i < firstEnd; i++)
			{
				for (int j = secondStart; j < secondEnd; j++)
				{
					if (getViewDescName(first[i]) == getViewDescName(second[j]))
					{
						return true;
					}
				}
			}

			return false;
		}

		var earlierReadStart = readTargetViewCount.pos[stIndex];
		var earlierReadEnd = readTargetViewCount.counter[stIndex];
		var earlierWriteStart = writeTargetViewCount.pos[stIndex];
		var earlierWriteEnd = writeTargetViewCount.counter[stIndex];
		var laterReadStart = readTargetViewCount.pos[enIndex];
		var laterReadEnd = readTargetViewCount.counter[enIndex];
		var laterWriteStart = writeTargetViewCount.pos[enIndex];
		var laterWriteEnd = writeTargetViewCount.counter[enIndex];

		var depends =
			Conflicts(WriteViewsDesc, earlierWriteStart, earlierWriteEnd,
				ReadViewsDesc, laterReadStart, laterReadEnd) ||
			Conflicts(WriteViewsDesc, earlierWriteStart, earlierWriteEnd,
				WriteViewsDesc, laterWriteStart, laterWriteEnd) ||
			Conflicts(ReadViewsDesc, earlierReadStart, earlierReadEnd,
				WriteViewsDesc, laterWriteStart, laterWriteEnd);

		if (depends && !passAdjacencyList.Contains(enIndex))
		{
			passAdjacencyList.Add(enIndex);
		}

		return depends;
	}

	/// <summary>
	/// Returns the native resource (texture/buffer) allocated for the pinned resource named <paramref name="name"/>.
	/// Must be called after <see cref="Allocate"/> has run for the pass that owns it (i.e. from within a
	/// pass's Execute), otherwise the resource may not have been created yet.
	/// </summary>
	public T GetTarget(string name)
	{
		var index = RenderTargetsDesc.FindIndex(desc => getTargetDescName(desc) == name);
		if (index < 0)
		{
			throw new InvalidOperationException($"Render graph resource '{name}' was never pinned.");
		}

		var target = RenderTargets[index];
		if (target == null)
		{
			throw new InvalidOperationException(
				$"Render graph resource '{name}' has not been allocated yet. It must be requested from within a pass's Execute.");
		}

		return target;
	}

	public void Allocate(int pass)
	{
		if (RenderTargetsDesc.Count != 0)
		{
			for (int i = renderTargetCount.pos[pass]; i < renderTargetCount.counter[pass]; i++)
			{
				if (RenderTargets[i] == null)
				{
					_entries[i] = AcquireEntry(RenderTargetsDesc[i]);
					RenderTargets[i] = _entries[i].Target;
				}
			}
		}

		if (WriteViewsDesc.Count != 0)
		{
			for (int i = writeTargetViewCount.pos[pass]; i < writeTargetViewCount.counter[pass]; i++)
			{
				BindWrittenTargets[i] ??= AcquireView(_entries[writeRenderTargetIndex[i]], WriteViewsDesc[i]);
			}
		}

		if (ReadViewsDesc.Count != 0)
		{
			for (int i = readTargetViewCount.pos[pass]; i < readTargetViewCount.counter[pass]; i++)
			{
				BindReadTargets[i] ??= AcquireView(_entries[readRenderTargetIndex[i]], ReadViewsDesc[i]);
			}
		}
	}

	/// <summary>Ресурс под дескриптор: из пула, если там лежит одноимённый и СОВМЕСТИМЫЙ (иначе
	/// пуловый освобождается - см. <see cref="_pool"/>), иначе создаётся заново.</summary>
	private Entry AcquireEntry(TDesc desc)
	{
		var name = getTargetDescName(desc);

		if (_live.TryGetValue(name, out var live))
		{
			return live;
		}

		if (_external.TryGetValue(name, out var external))
		{
			var imported = new Entry { Desc = desc, Target = external, Owned = false };
			_live[name] = imported;
			return imported;
		}

		if (_pool.Remove(name, out var pooled))
		{
			if (descEquals(pooled.Desc, desc))
			{
				_live[name] = pooled;
				return pooled;
			}

			pooled.DisposeAll();
		}

		var entry = new Entry { Desc = desc, Target = createTarget(desc) };
		_live[name] = entry;
		return entry;
	}

	/// <summary>Вьюха на ресурс: переиспользуется, пока жив сам ресурс, - иначе каждая пересборка
	/// графа создавала бы RTV/DSV/SRV заново поверх тех же текстур.</summary>
	private TView AcquireView(Entry entry, TViewDesc viewDesc)
	{
		if (!entry.Owned)
		{
			// Внешний ресурс объявлен графу только ради зависимостей: свои вьюхи ему делает владелец,
			// а угадывать по формату, какая нужна графу, значило бы создавать заведомо лишние.
			return null;
		}

		for (int i = 0; i < entry.Views.Count; i++)
		{
			if (viewDescEquals(entry.Views[i].Key, viewDesc))
			{
				return entry.Views[i].Value;
			}
		}

		var view = createView(entry.Target, viewDesc);
		entry.Views.Add(new KeyValuePair<TViewDesc, TView>(viewDesc, view));
		return view;
	}

	public void Release(int pass)
	{
		// Frozen command buffers keep native resource views alive between frames.
		// Resources are released together by Clean() when the graph is recompiled.
	}

#if DEBUG
	/// <summary>Debug-only: names of resources read by the given pass.</summary>
	public string[] GetPassReadNames(int pass)
	{
		var start = readTargetViewCount.pos[pass];
		var end = readTargetViewCount.counter[pass];
		var result = new string[end - start];
		for (int i = start; i < end; i++)
		{
			result[i - start] = getViewDescName(ReadViewsDesc[i]);
		}

		return result;
	}

	/// <summary>Debug-only: names of resources written by the given pass.</summary>
	public string[] GetPassWriteNames(int pass)
	{
		var start = writeTargetViewCount.pos[pass];
		var end = writeTargetViewCount.counter[pass];
		var result = new string[end - start];
		for (int i = start; i < end; i++)
		{
			result[i - start] = getViewDescName(WriteViewsDesc[i]);
		}

		return result;
	}

	/// <summary>Debug-only: сколько нативных ресурсов сейчас лежит в пуле (выключенные фичи, старые
	/// размеры до <see cref="TrimPool"/>) - показывается в окне отладки графа.</summary>
	public int PooledCount => _pool.Count;

	/// <summary>Debug-only: суммарный вес пула - см. <see cref="PooledCount"/>.</summary>
	public ulong PooledSizeInBytes
	{
		get
		{
			ulong total = 0;
			foreach (var entry in _pool.Values)
			{
				total += getTargetDescSizeInBytes(entry.Desc);
			}

			return total;
		}
	}

	/// <summary>
	/// Debug-only export of resource lifetime/allocation info for every pinned resource in this
	/// container. Must be called after <see cref="SetupPass"/> has run for all passes (i.e. after
	/// <see cref="DecaEngine.Graphics.Diligent.DiligentRenderGraph.Compile"/>).
	/// </summary>
	public IEnumerable<DecaEngine.Graphics.ResourceDebugInfo> ExportLifetimes(bool isBuffer)
	{
		foreach (var desc in RenderTargetsDesc)
		{
			var name = getTargetDescName(desc);
			_firstUsage.TryGetValue(name, out var first);
			_debugLastUsage.TryGetValue(name, out var last);

			int refCount = 0;
			foreach (var view in ReadViewsDesc) { if (getViewDescName(view) == name) refCount++; }
			foreach (var view in WriteViewsDesc) { if (getViewDescName(view) == name) refCount++; }

			yield return new DecaEngine.Graphics.ResourceDebugInfo
			{
				Name = name,
				IsBuffer = isBuffer,
				FirstPassIndex = first,
				LastPassIndex = last,
				SizeInBytes = getTargetDescSizeInBytes(desc),
				RefCount = refCount,
			};
		}
	}
#endif
}
