namespace DecaEngine.Core;

/// <summary>
/// Per-pass debug statistics collected while executing a <see cref="IRenderGraph"/>.
/// Only ever populated in DEBUG builds (see <see cref="IRenderGraph.DebugSnapshot"/>).
/// </summary>
public sealed class PassDebugInfo
{
	public string Name = "";
	public int Id;
	public double CpuMs;
	public double GpuMs;
	public int DrawCalls;
	public int DispatchCalls;
	public int TransitionCount;
	public long TriangleCount;
	public string[] ReadResources = System.Array.Empty<string>();
	public string[] WriteResources = System.Array.Empty<string>();
}

/// <summary>
/// Lifetime/allocation info for a single pinned render-graph resource (texture or buffer).
/// </summary>
public sealed class ResourceDebugInfo
{
	public string Name = "";
	public bool IsBuffer;
	public int FirstPassIndex;
	public int LastPassIndex;
	public ulong SizeInBytes;
	public int RefCount;
}

/// <summary>
/// Full snapshot of one executed frame of a render graph. Cheap to allocate (only in DEBUG),
/// meant to be read by editor/debug UI (e.g. RenderGraphDebugWindow).
/// </summary>
public sealed class RenderGraphDebugSnapshot
{
	public double TotalCpuMs;
	public double TotalGpuMs;
	public ulong TotalResourceMemoryBytes;
	public PassDebugInfo[] Passes = System.Array.Empty<PassDebugInfo>();
	public ResourceDebugInfo[] Resources = System.Array.Empty<ResourceDebugInfo>();
	public int[] TopologicalOrder = System.Array.Empty<int>();
}

/// <summary>
/// Small fixed-size ring buffer of recent frame snapshots, used to draw a frame-time history graph.
/// </summary>
public sealed class RenderGraphDebugHistory
{
	private readonly RenderGraphDebugSnapshot[] _buffer;
	private int _head;
	private int _count;

	public RenderGraphDebugHistory(int capacity)
	{
		_buffer = new RenderGraphDebugSnapshot[capacity];
	}

	public int Count => _count;
	public int Capacity => _buffer.Length;

	public void Push(RenderGraphDebugSnapshot snapshot)
	{
		_buffer[_head] = snapshot;
		_head = (_head + 1) % _buffer.Length;
		if (_count < _buffer.Length)
		{
			_count++;
		}
	}

	/// <summary>Returns snapshots ordered oldest-first.</summary>
	public void CopyTo(List<RenderGraphDebugSnapshot> destination)
	{
		destination.Clear();
		int start = _count < _buffer.Length ? 0 : _head;
		for (int i = 0; i < _count; i++)
		{
			var snap = _buffer[(start + i) % _buffer.Length];
			if (snap != null)
			{
				destination.Add(snap);
			}
		}
	}
}

