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
		Func<T, TViewDesc, TView> createView
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
#if DEBUG
		this.getTargetDescSizeInBytes = getTargetDescSizeInBytes ?? (_ => 0UL);
#endif
	}

	public void Clean()
	{
		foreach (var view in BindWrittenTargets)
		{
			view?.Dispose();
		}

		foreach (var view in BindReadTargets)
		{
			view?.Dispose();
		}

		foreach (var target in RenderTargets)
		{
			target?.Dispose();
		}

		BindWrittenTargets = [];
		BindReadTargets = [];
		RenderTargets = [];

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
				RenderTargets[i] ??= createTarget(RenderTargetsDesc[i]);
			}
		}

		if (WriteViewsDesc.Count != 0)
		{
			for (int i = writeTargetViewCount.pos[pass]; i < writeTargetViewCount.counter[pass]; i++)
			{
				BindWrittenTargets[i] ??= createView(RenderTargets[writeRenderTargetIndex[i]], WriteViewsDesc[i]);
			}
		}

		if (ReadViewsDesc.Count != 0)
		{
			for (int i = readTargetViewCount.pos[pass]; i < readTargetViewCount.counter[pass]; i++)
			{
				BindReadTargets[i] ??= createView(RenderTargets[readRenderTargetIndex[i]], ReadViewsDesc[i]);
			}
		}
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

	/// <summary>
	/// Debug-only export of resource lifetime/allocation info for every pinned resource in this
	/// container. Must be called after <see cref="SetupPass"/> has run for all passes (i.e. after
	/// <see cref="DecaEngine.Graphics.Diligent.DiligentRenderGraph.Compile"/>).
	/// </summary>
	public IEnumerable<DecaEngine.Core.ResourceDebugInfo> ExportLifetimes(bool isBuffer)
	{
		foreach (var desc in RenderTargetsDesc)
		{
			var name = getTargetDescName(desc);
			_firstUsage.TryGetValue(name, out var first);
			_debugLastUsage.TryGetValue(name, out var last);

			int refCount = 0;
			foreach (var view in ReadViewsDesc) { if (getViewDescName(view) == name) refCount++; }
			foreach (var view in WriteViewsDesc) { if (getViewDescName(view) == name) refCount++; }

			yield return new DecaEngine.Core.ResourceDebugInfo
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