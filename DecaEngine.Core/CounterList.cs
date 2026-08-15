namespace DecaEngine.Graphics.Diligent;

/// <summary>
/// Per-pass [start, end) ranges over a single CUMULATIVE backing list. <see cref="Add{T}"/> must be
/// called once per pass with the SAME list, which only ever grows between calls (cleared together
/// with this via <see cref="Clean"/> - see RenderGraphNode/RenderGraphGlobalNode). For pass i the
/// items added during that pass are <c>list[pos[i]] .. list[counter[i] - 1]</c>.
/// </summary>
public class CounterList
{
	public List<int> pos = new(32);
	public List<int> counter = new(32);
	private int _lastIndex = 0;

	public void Clean()
	{
		pos.Clear();
		counter.Clear();
		_lastIndex = 0;
	}

	public void Add<T>(List<T> list)
	{
		counter.Add(list.Count);
		pos.Add(_lastIndex);

		// list is cumulative, so its current Count IS the end index of this pass and therefore the
		// start index of the next one. Do NOT turn this into "+=": that only holds for per-pass
		// lists, which is not how any call site uses this type.
		_lastIndex = list.Count;
	}
}
