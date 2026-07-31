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
	private readonly List<PassData> _passes = new();
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
		_graphContext.Add(new DiligentRenderGraphContext());
		_isCompiled = false;
	}

	public void Compile()
	{
		_isCompiled = false;
		_builder.Clean();
		
		for (var index = 0; index < _passes.Count; index++)
		{
			var passData = _passes[index];
			passData.Pass.SetupPassData(_builder);
			_builder.SetupPass(passData.Id);
		}

		_builder.PostSetup();

		BuildAdjacencyLists();
		TopologicalSort();

		for (int passId = 0; passId < _passes.Count; passId++)
		{
			_builder.Allocate(passId);
		}

		foreach (var passId in _topologicallySortedPasses)
		{
			var graphContext = _graphContext[passId];
			graphContext.BeginRecording(_api, _api.ImmediateContext, _builder);
			_passes[passId].Pass.WriteCommands(graphContext);
			graphContext.Freeze();
		}

		_isCompiled = true;
	}

	private void BuildAdjacencyLists()
	{
		_adjacencyLists.Clear();
		for (int i = 0; i < _passes.Count; i++)
		{
			_adjacencyLists.Add(new List<int>());
		}

		for (int i = 0; i < _passes.Count; i++)
		{
			var passAdjacencyList = _adjacencyLists[i];

			for (int j = i + 1; j < _passes.Count; j++)
			{
				_builder.renderContainer.DependenceCheck(passAdjacencyList, i, j);
				_builder.bufferContainer.DependenceCheck(passAdjacencyList, i, j);
			}
		}
	}

	private void TopologicalSort()
	{
		_topologicallySortedPasses.Clear();
		var incomingEdges = new int[_passes.Count];

		for (int i = 0; i < _passes.Count; i++)
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

		if (_topologicallySortedPasses.Count != _passes.Count)
		{
			throw new InvalidOperationException("The render graph contains a dependency cycle.");
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

		foreach (var passId in _topologicallySortedPasses)
		{
			_passes[passId].Pass.EarlyCommands();

#if DEBUG
			_passStopwatch.Restart();
#endif
			_graphContext[passId].Execute(immediateContext);
#if DEBUG
			_passStopwatch.Stop();

			var (drawCalls, dispatchCalls, transitionCount, triangles) = _graphContext[passId].GetDebugStats();
			var (reads, writes) = _builder.GetPassResourceNames(passId);
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
			TopologicalOrder = _topologicallySortedPasses.ToArray(),
		};
		_debugHistory.Push(DebugSnapshot);
#endif
	}

	public void Release()
	{
		_builder.Clean();
		foreach (var pass in _passes)
		{
			if (pass.Pass is IDisposable disposable)
			{
				disposable.Dispose();
			}
		}

		_passes.Clear();
		_graphContext.Clear();
		_topologicallySortedPasses.Clear();
		_adjacencyLists.Clear();
		_isCompiled = false;
#if DEBUG
		DebugSnapshot = null;
#endif
	}
}