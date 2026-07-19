using DecaEngine.Core;

namespace DecaEngine.Graphics.Diligent;

public class RenderGraphGlobalNode<TDesc, T>
{
	public List<GraphId> ReadDesc { get; } = new(32);

	public T[] RenderTargets { get; private set; }
	public List<TDesc> RenderTargetsDesc { get; } = new(32);

	public CounterList renderTargetCount = new();
	private List<int> readRenderTargetIndex = new(32);

	private readonly Func<List<TDesc>, GraphId, int> findTargetDescIndex;
	private readonly Func<TDesc, T> createTarget;

	public RenderGraphGlobalNode(Func<TDesc, T> createTarget, Func<List<TDesc>, GraphId, int> findTargetDescIndex)
	{
		this.createTarget = createTarget;
		this.findTargetDescIndex = findTargetDescIndex;
	}

	public void Clean()
	{
		renderTargetCount.Clean();
	}

	public void PostSetup()
	{
		RenderTargets = new T[RenderTargetsDesc.Count];

		for (var i = 0; i < ReadDesc.Count; i++)
		{
			var find = findTargetDescIndex.Invoke(RenderTargetsDesc, ReadDesc[i]);
			if (find != -1)
			{
				readRenderTargetIndex.Add(find);
			}
		}
	}

	public void SetupPass()
	{
		renderTargetCount.Add(RenderTargetsDesc);
	}

	public void AllocateGlobal(int pass)
	{
		if (RenderTargetsDesc.Count == 0)
		{
			return;
		}

		for (int i = renderTargetCount.pos[pass]; i < renderTargetCount.counter[pass]; i++)
		{
			RenderTargets[i] = createTarget(RenderTargetsDesc[i]);
		}
	}
}