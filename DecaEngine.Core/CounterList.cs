namespace DecaEngine.Graphics.Diligent;

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

		_lastIndex = list.Count;
	}
}