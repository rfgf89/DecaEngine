namespace DecaEngine.Graphics.Diligent;

public class RenderGraphNode<TView, TViewDesc, TDesc, T>
	where TViewDesc : struct
	where TView : SharpGen.Runtime.DisposeBase
	where T : SharpGen.Runtime.DisposeBase
{
	public TView[] BindWrittenTargets { get; private set; }
	public TView[] BindReadTargets { get; private set; }

	public List<TViewDesc> ReadViewsDesc { get; } = new(32);
	public List<TViewDesc> WriteViewsDesc { get; } = new(32);

	private T[] RenderTargets { get; set; }
	public List<TDesc> RenderTargetsDesc { get; } = new(32);

	private readonly Dictionary<string, int> _lastUsage = new(32);

	private CounterList renderTargetCount = new();
	private CounterList readTargetViewCount = new();
	private CounterList writeTargetViewCount = new();

	private List<int> readRenderTargetIndex = new(32);
	private List<int> writeRenderTargetIndex = new(32);

	private List<int>[] readRenderTargetReleaseIndex;

	private int _passIndex;

	private readonly Func<List<TDesc>, TViewDesc, int> findTargetDescIndex;
	private readonly Func<TViewDesc, string> getViewDescName;
	private readonly Func<TDesc, string> getTargetDescName;
	private readonly Func<TDesc, T> createTarget;
	private readonly Func<T, TViewDesc, TView> createView;

	public RenderGraphNode(
		Func<List<TDesc>, TViewDesc, int> findTargetDescIndex,
		Func<TViewDesc, string> getViewDescName,
		Func<TDesc, string> getTargetDescName,
		Func<TDesc, T> createTarget,
		Func<T, TViewDesc, TView> createView)
	{
		this.findTargetDescIndex = findTargetDescIndex;
		this.getViewDescName = getViewDescName;
		this.getTargetDescName = getTargetDescName;
		this.createTarget = createTarget;
		this.createView = createView;
	}

	public void Clean()
	{
		renderTargetCount.Clean();
		readTargetViewCount.Clean();
		writeTargetViewCount.Clean();

		readRenderTargetIndex.Clear();
		writeRenderTargetIndex.Clear();
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
		}

		for (var i = 0; i < WriteViewsDesc.Count; i++)
		{
			var find = findTargetDescIndex.Invoke(RenderTargetsDesc, WriteViewsDesc[i]);
			if (find != -1)
			{
				writeRenderTargetIndex.Add(find);
			}
		}

		readRenderTargetReleaseIndex = new List<int>[_passIndex + 1];
		for (int i = 0; i < RenderTargetsDesc.Count; i++)
		{
			if (_lastUsage.TryGetValue(getTargetDescName(RenderTargetsDesc[i]), out var lastUsage))
			{
				readRenderTargetReleaseIndex[lastUsage] ??= new();
				readRenderTargetReleaseIndex[lastUsage].Add(i);
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
			_lastUsage[getViewDescName(ReadViewsDesc[i])] = pass;
		}

		for (int i = writeTargetViewCount.pos[pass]; i < writeTargetViewCount.counter[pass]; i++)
		{
			_lastUsage[getViewDescName(WriteViewsDesc[i])] = pass;
		}

		_passIndex = pass;
	}

	public bool DependenceCheck(List<int> passAdjacencyList, int stIndex, int enIndex)
	{
		bool depends = false;

		for (int r = readTargetViewCount.pos[enIndex]; r < readTargetViewCount.counter[enIndex]; r++)
		{
			var readDesc = ReadViewsDesc[r];

			for (int w = writeTargetViewCount.pos[stIndex]; w < writeTargetViewCount.counter[stIndex]; w++)
			{
				var writeDesc = WriteViewsDesc[w];
				if (getViewDescName(readDesc) == getViewDescName(writeDesc))
				{
					passAdjacencyList.Add(enIndex);
					return true;
				}
			}

			if (depends)
			{
				return true;
			}
		}

		return depends;
	}

	public void Allocate(int pass)
	{
		if (RenderTargetsDesc.Count != 0)
		{
			for (int i = renderTargetCount.pos[pass]; i < renderTargetCount.counter[pass]; i++)
			{
				RenderTargets[i] = createTarget(RenderTargetsDesc[i]);
			}
		}

		if (WriteViewsDesc.Count != 0)
		{
			for (int i = writeTargetViewCount.pos[pass]; i < writeTargetViewCount.counter[pass]; i++)
			{
				BindWrittenTargets[i] = createView(RenderTargets[writeRenderTargetIndex[i]], WriteViewsDesc[i]);
			}
		}

		if (ReadViewsDesc.Count != 0)
		{
			for (int i = readTargetViewCount.pos[pass]; i < readTargetViewCount.counter[pass]; i++)
			{
				BindReadTargets[i] = createView(RenderTargets[readRenderTargetIndex[i]], ReadViewsDesc[i]);
			}
		}
	}

	public void Release(int pass)
	{
		if (readRenderTargetReleaseIndex.Length > pass)
		{
			for (var i = 0; i < readRenderTargetReleaseIndex[pass].Count; i++)
			{
				RenderTargets[readRenderTargetReleaseIndex[pass][i]].Dispose();
				RenderTargets[readRenderTargetReleaseIndex[pass][i]] = null;
			}
		}

		if (BindWrittenTargets.Length > 0)
		{
			for (int i = writeTargetViewCount.pos[pass]; i < writeTargetViewCount.counter[pass]; i++)
			{
				BindWrittenTargets[i]?.Dispose();
			}
		}

		if (BindReadTargets.Length > 0)
		{
			for (int i = readTargetViewCount.pos[pass]; i < readTargetViewCount.counter[pass]; i++)
			{
				BindReadTargets[i]?.Dispose();
			}
		}
	}
}