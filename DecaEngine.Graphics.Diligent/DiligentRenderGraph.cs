using System.Diagnostics;
using DecaEngine.Core;
using Diligent;

namespace DecaEngine.Graphics.Diligent;

public sealed class DiligentRenderGraph : IRenderGraph
{
	private class PassData(int id, IRenderGraphPass pass)
	{
		public IRenderGraphPass Pass { get; } = pass;
		public int Id { get; } = id;
	}

	private readonly List<DiligentRenderGraphContext> _graphContext = new();

	// Recording contexts left over from the previous graph build.
	private readonly List<DiligentRenderGraphContext> _contextPool = new();

	private readonly List<PassData> _passes = new();

	// Indices into _passes for the enabled passes; everything downstream numbers passes by
	// position in THIS list, so a disabled pass simply does not exist for the graph.
	private readonly List<int> _activePasses = new();

	// Local indices (positions in _activePasses) in execution order.
	private readonly List<int> _topologicallySortedPasses = new();
	private readonly List<List<int>> _adjacencyLists = new();

	private readonly DiligentGraphicsApi _api;
	private readonly DiligentRenderGraphBuilder _builder;
	private bool _isCompiled = false;

#if DEBUG
	private readonly Stopwatch _passStopwatch = new();
	private readonly RenderGraphDebugHistory _debugHistory = new(240); // ~4s of history at 60fps
	public RenderGraphDebugSnapshot DebugSnapshot { get; private set; }
	public RenderGraphDebugHistory DebugHistory => _debugHistory;
#else
	public RenderGraphDebugSnapshot DebugSnapshot => null;
	public RenderGraphDebugHistory DebugHistory => null;
#endif

	public DiligentRenderGraph(DiligentGraphicsApi api)
	{
		_api = api;
		_builder = new DiligentRenderGraphBuilder(_api);
	}

	public void AddPass(IRenderGraphPass pass)
	{
		ArgumentNullException.ThrowIfNull(pass);
		_passes.Add(new PassData(_passes.Count, pass));

		// Contexts are pooled across rebuilds: each holds a command buffer of hundreds of entries.
		DiligentRenderGraphContext context;
		if (_contextPool.Count > 0)
		{
			context = _contextPool[^1];
			_contextPool.RemoveAt(_contextPool.Count - 1);
		}
		else
		{
			context = new DiligentRenderGraphContext();
		}

		_graphContext.Add(context);
		_isCompiled = false;
	}

	public void Invalidate()
	{
		_isCompiled = false;
	}

	public void Compile()
	{
		_isCompiled = false;

		// Resources go back to the builder pool rather than being destroyed; only Release frees them.
		_builder.Clean(recycle: true);

		_activePasses.Clear();
		for (var index = 0; index < _passes.Count; index++)
		{
			if (_passes[index].Pass.Enabled)
			{
				_activePasses.Add(index);
			}
		}

		for (var local = 0; local < _activePasses.Count; local++)
		{
			_passes[_activePasses[local]].Pass.SetupPassData(_builder);
			_builder.SetupPass(local);
		}

		_builder.PostSetup();

		BuildAdjacencyLists();
		TopologicalSort();

		for (int local = 0; local < _activePasses.Count; local++)
		{
			_builder.Allocate(local);
		}

		foreach (var local in _topologicallySortedPasses)
		{
			var graphContext = _graphContext[_activePasses[local]];
			graphContext.BeginRecording(_api, _api.ImmediateContext, _builder);
			_passes[_activePasses[local]].Pass.WriteCommands(graphContext);
			graphContext.Freeze();
		}

		_isCompiled = true;
	}

	private void BuildAdjacencyLists()
	{
		_adjacencyLists.Clear();
		for (int i = 0; i < _activePasses.Count; i++)
		{
			_adjacencyLists.Add(new List<int>());
		}

		for (int i = 0; i < _activePasses.Count; i++)
		{
			var passAdjacencyList = _adjacencyLists[i];

			for (int j = i + 1; j < _activePasses.Count; j++)
			{
				_builder.renderContainer.DependenceCheck(passAdjacencyList, i, j);
				_builder.bufferContainer.DependenceCheck(passAdjacencyList, i, j);
			}
		}
	}

	private void TopologicalSort()
	{
		_topologicallySortedPasses.Clear();
		var incomingEdges = new int[_activePasses.Count];

		for (int i = 0; i < _activePasses.Count; i++)
		{
			foreach (var dependentPass in _adjacencyLists[i])
			{
				incomingEdges[dependentPass]++;
			}
		}

		var ready = new PriorityQueue<int, int>();
		for (int i = 0; i < incomingEdges.Length; i++)
		{
			if (incomingEdges[i] == 0)
			{
				ready.Enqueue(i, i);
			}
		}

		while (ready.TryDequeue(out var passId, out _))
		{
			_topologicallySortedPasses.Add(passId);
			foreach (var dependentPass in _adjacencyLists[passId])
			{
				incomingEdges[dependentPass]--;
				if (incomingEdges[dependentPass] == 0)
				{
					ready.Enqueue(dependentPass, dependentPass);
				}
			}
		}

		if (_topologicallySortedPasses.Count != _activePasses.Count)
		{
			throw new InvalidOperationException("The render graph contains a dependency cycle.");
		}
	}

	// By name, not index: pass order depends on which pipeline features are enabled.
	public bool SetPassEnabled(string name, bool enabled)
	{
		bool found = false;
		foreach (var passData in _passes)
		{
			if (passData.Pass.Name == name)
			{
				if (passData.Pass.Enabled != enabled)
				{
					passData.Pass.Enabled = enabled;
					_isCompiled = false;
				}

				found = true;
			}
		}

		return found;
	}

	public IReadOnlyList<string> PassNames
	{
		get
		{
			var names = new string[_passes.Count];
			for (int i = 0; i < _passes.Count; i++)
			{
				names[i] = _passes[i].Pass.Name;
			}

			return names;
		}
	}

	public void Execute()
	{
		if (!_isCompiled)
		{
			Compile();
		}

		var immediateContext = _api.ImmediateContext;

#if DEBUG
		var passInfos = new PassDebugInfo[_topologicallySortedPasses.Count];
		double totalCpu = 0;
		int debugIdx = 0;
#endif

		foreach (var local in _topologicallySortedPasses)
		{
			var passId = _activePasses[local];
			_passes[passId].Pass.EarlyCommands();

#if DEBUG
			_passStopwatch.Restart();
#endif
			_graphContext[passId].Execute(immediateContext);
#if DEBUG
			_passStopwatch.Stop();

			var (drawCalls, dispatchCalls, transitionCount, triangles) = _graphContext[passId].GetDebugStats();
			var (reads, writes) = _builder.GetPassResourceNames(local);
			var cpuMs = _passStopwatch.Elapsed.TotalMilliseconds;

			passInfos[debugIdx++] = new PassDebugInfo
			{
				Id = passId,
				Name = _passes[passId].Pass.Name,
				CpuMs = cpuMs,
				DrawCalls = drawCalls,
				DispatchCalls = dispatchCalls,
				TransitionCount = transitionCount,
				TriangleCount = triangles,
				ReadResources = reads,
				WriteResources = writes,
			};
			totalCpu += cpuMs;
#endif
		}

#if DEBUG
		var resources = _builder.ExportResourceDebugInfo();
		ulong totalMemory = 0;
		foreach (var r in resources) totalMemory += r.SizeInBytes;

		DebugSnapshot = new RenderGraphDebugSnapshot
		{
			TotalCpuMs = totalCpu,
			TotalResourceMemoryBytes = totalMemory,
			Passes = passInfos,
			Resources = resources.ToArray(),
			// Reported in AddPass numbering, matching PassNames, not the local one.
			TopologicalOrder = _topologicallySortedPasses.Select(local => _activePasses[local]).ToArray(),
		};
		_debugHistory.Push(DebugSnapshot);
#endif
	}

	public void ResetPasses()
	{
		ClearPasses(recycleResources: true);
	}

	public void Release()
	{
		ClearPasses(recycleResources: false);
	}

	public void TrimResourcePool()
	{
		_builder.TrimPool();
	}

	private void ClearPasses(bool recycleResources)
	{
		_builder.Clean(recycleResources);
		foreach (var pass in _passes)
		{
			if (pass.Pass is IDisposable disposable)
			{
				disposable.Dispose();
			}
		}

		_passes.Clear();

		if (recycleResources)
		{
			_contextPool.AddRange(_graphContext);
		}
		else
		{
			_contextPool.Clear();
		}

		_graphContext.Clear();
		_activePasses.Clear();
		_topologicallySortedPasses.Clear();
		_adjacencyLists.Clear();
		_isCompiled = false;
#if DEBUG
		DebugSnapshot = null;
#endif
	}
}