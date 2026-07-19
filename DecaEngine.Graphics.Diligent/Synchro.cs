namespace DecaEngine.Graphics.Diligent;

public class Synchro
{
	private readonly CountdownEvent _countdown;

	private ManualResetEventSlim[] _events =
	[
		new(false),
		new(false),
	];

	private readonly object _sync = new();
	private volatile int _nextEvtIdx;

	public Synchro(int threadsCount, CancellationToken token)
	{
		_countdown = new CountdownEvent(threadsCount);
		_countdown.Reset();
	}

	public void ResetCount(int count)
	{
		_countdown.Reset(count);
	}

	public void MarkThreadAsReady()
	{
		_countdown.Signal();
	}

	public void Wait()
	{
		int nextEvtIdx;
		lock (_sync)
			nextEvtIdx = _nextEvtIdx;

		_events[nextEvtIdx].Wait(1000, CancellationToken.None);
	}

	public void WaitSignal()
	{
		MarkThreadAsReady();
		Wait();
	}

	public void Signal()
	{
		//WaitForThreads();
		lock (_sync)
		{
			var currEvt = _events[_nextEvtIdx];
			// ReSharper disable once NonAtomicCompoundOperator
			_nextEvtIdx++;
			// ReSharper disable once NonAtomicCompoundOperator
			_nextEvtIdx %= _events.Length;

			_countdown.Reset();
			_events[_nextEvtIdx].Reset();
			currEvt.Set();
		}
	}

	public void WaitForThreads()
	{
		_countdown.Wait();
	}
}