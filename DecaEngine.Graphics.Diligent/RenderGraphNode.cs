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

	// One physical allocation; outlives a single graph compilation via _pool.
	private sealed class Entry
	{
		public TDesc Desc;
		public T Target;

		// false: declared from outside, so the graph never creates, pools or releases it.
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

	// Externally owned resources; declarations live for exactly one compilation.
	private readonly Dictionary<string, T> _external = new(16);

	/// <summary>Declares an existing resource to the graph without transferring ownership.</summary>
	public void RegisterExternal(string name, T target)
	{
		_external[name] = target;
	}

	private readonly Dictionary<string, Entry> _live = new(32);

	// Resources from earlier compilations, keyed by pin name: a pin with a matching descriptor
	// reclaims one instead of recreating it; a mismatching descriptor frees it, so no growth.
	private readonly Dictionary<string, Entry> _pool = new(32);

	private Entry[] _entries = [];

#if DEBUG
	private readonly Func<TDesc, ulong> getTargetDescSizeInBytes;
	private readonly Dictionary<string, int> _firstUsage = new(32);
	// Separate from _lastUsage, which PostSetup() clears before ExportLifetimes() can read it.
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

	/// <summary>Releases the compilation; caller must ensure no frame using these resources is in flight.</summary>
	public void Clean(bool recycle = false)
	{
		foreach (var entry in _live.Values)
		{
			if (!entry.Owned)
			{
				continue;
			}

			if (recycle)
			{
				// Names are unique within a compilation (PinTexture dedups), so no pool entry is lost.
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

	/// <summary>Frees pooled resources not claimed by the current compilation; live ones are kept.</summary>
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

	/// <summary>Native resource for a pinned name; only valid from within a pass's Execute.</summary>
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

	// Views are cached per resource; otherwise every recompile would recreate RTV/DSV/SRVs.
	private TView AcquireView(Entry entry, TViewDesc viewDesc)
	{
		if (!entry.Owned)
		{
			// External resources are declared for dependencies only; their owner makes the views.
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
		// Frozen command buffers hold the views across frames; Clean() releases them on recompile.
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

	/// <summary>Debug-only: number of native resources currently held in the pool.</summary>
	public int PooledCount => _pool.Count;

	/// <summary>Debug-only: total size of the pooled resources.</summary>
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

	/// <summary>Debug-only lifetime info; valid only after SetupPass has run for every pass.</summary>
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
