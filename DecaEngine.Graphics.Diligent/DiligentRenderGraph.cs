using DecaEngine.Core;
using Diligent;

namespace DecaEngine.Graphics.Diligent;

public class DiligentRenderGraph : IRenderGraph
{
	private class PassData(int id, IRenderGraphPass pass)
	{
		public IRenderGraphPass Pass { get; } = pass;
		public int Id { get; } = id;
	}

	private class DependencyLevel()
	{
		public List<PassData> Passes { get; } = new();
	}

	private readonly List<DiligentRenderGraphContext> _graphContext = new();
	private readonly List<PassData> _passes = new();
	private readonly List<int> _topologicallySortedPasses = new();
	private readonly List<List<int>> _adjacencyLists = new();
	private readonly List<DependencyLevel> _dependencyLevels = new();

	private readonly DiligentGraphicsPipeline _pipeline;
	private readonly DiligentRenderGraphBuilder _builder;
	private bool _isCompiled = false;

	public DiligentRenderGraph(DiligentGraphicsPipeline pipeline)
	{
		_pipeline = pipeline;
		_builder = new DiligentRenderGraphBuilder(_pipeline);
	}

	public void AddPass(IRenderGraphPass pass)
	{
		_passes.Add(new PassData(_passes.Count, pass));
		_graphContext.Add(new DiligentRenderGraphContext());
		_isCompiled = false;
	}

	public void Compile()
	{
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
		BuildDependencyLevels();

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
				if (_builder.renderContainer.DependenceCheck(passAdjacencyList, i, j))
				{
					break;
				}

				if (_builder.bufferContainer.DependenceCheck(passAdjacencyList, i, j))
				{
					break;
				}
			}
		}
	}

	private void TopologicalSort()
	{
		_topologicallySortedPasses.Clear();
		bool[] visited = new bool[_passes.Count];

		for (int i = 0; i < _passes.Count; i++)
		{
			if (!visited[i])
			{
				DepthFirstSearch(i, visited, _topologicallySortedPasses);
			}
		}

		_topologicallySortedPasses.Reverse();
	}

	private void DepthFirstSearch(int i, bool[] visited, List<int> sorted)
	{
		visited[i] = true;
		foreach (var j in _adjacencyLists[i])
		{
			if (!visited[j])
			{
				DepthFirstSearch(j, visited, sorted);
			}
		}
		sorted.Add(i);
	}

	private void BuildDependencyLevels()
	{
		_dependencyLevels.Clear();
		if (_passes.Count == 0)
		{
			return;
		}

		int[] distances = new int[_passes.Count];

		for (int u = 0; u < _topologicallySortedPasses.Count; u++)
		{
			int i = _topologicallySortedPasses[u];
			foreach (int v in _adjacencyLists[i])
			{
				if (distances[v] < distances[i] + 1)
				{
					distances[v] = distances[i] + 1;
				}
			}
		}

		int maxLevel = distances.Length > 0 ? distances.Max() + 1 : 0;
		for (int i = 0; i < maxLevel; i++)
		{
			_dependencyLevels.Add(new DependencyLevel());
		}

		for (int i = 0; i < _passes.Count; i++)
		{
			int level = distances[i];
			_dependencyLevels[level].Passes.Add(_passes[i]);
		}
	}

	public void Execute()
	{
		if (!_isCompiled)
		{
			Compile();
		}

		var immediateContext = _pipeline.ImmediateContext;
		var deferredContexts = _pipeline.DeferredContexts;

		for (var index = 0; index < _dependencyLevels.Count; index++)
		{
			var level = _dependencyLevels[index];

			var activePasses = level.Passes;
			if (activePasses.Count == 0)
			{
				continue;
			}

			for (int i = 0; i < activePasses.Count; i++)
			{
				_builder.Allocate(activePasses[i].Id);
			}

			var passDataImmediate = activePasses[0];
			_graphContext[0].Initialize(0, _pipeline, immediateContext, _builder);

			passDataImmediate.Pass.Execute(_graphContext[0]);

			if (activePasses.Count > 1)
			{
				var deferredContextQueue = new System.Collections.Concurrent.BlockingCollection<IDeviceContext>();
				foreach (var ctx in deferredContexts)
				{
					deferredContextQueue.Add(ctx);
				}

				var commandListsToExecute = new System.Collections.Concurrent.ConcurrentBag<ICommandList>();
				var deferredTasks = new Task[activePasses.Count - 1];

				for (int i = 1; i < activePasses.Count; i++)
				{
					int j = i; // local copy for closure

					deferredTasks[i - 1] = Task.Run(() =>
					{
						var deferredCtx = deferredContextQueue.Take();
						try
						{
							deferredCtx.Begin(0);
							_graphContext[j].Initialize(j, _pipeline, deferredCtx, _builder);

							activePasses[j].Pass.Execute(_graphContext[j]);

							var cmdList = deferredCtx.FinishCommandList();

							deferredCtx.FinishFrame();
							if (cmdList != null)
							{
								commandListsToExecute.Add(cmdList);
							}
						}
						finally
						{
							deferredContextQueue.Add(deferredCtx);
						}
					});
				}

				Task.WaitAll(deferredTasks);

				var cmds = commandListsToExecute.ToArray();
				if (cmds.Length > 0)
				{
					immediateContext.ExecuteCommandLists(cmds);
					foreach (var cmd in cmds)
					{
						cmd.Dispose();
					}
				}
			}

			for (int i = 0; i < level.Passes.Count; i++)
			{
				_builder.Release(level.Passes[i].Id);
			}
		}

		foreach (var deferredCtx in deferredContexts)
		{
			deferredCtx.FinishFrame();
		}
	}
}