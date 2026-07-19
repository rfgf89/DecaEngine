using System.Diagnostics;

namespace DecaEngine.Core;

public abstract class TimeLoopCore : LoopCore
{
	private Stopwatch _stopwatch;
	private long _prevElapsed;
	private float _ratio;

	private long _elapsed;

	protected override void OnStart(ref long state)
	{
		base.OnStart(ref state);
		_stopwatch = Stopwatch.StartNew();
		_prevElapsed = 0L;
		_ratio = 1f / 1000.0f;

		OnStart();
	}

	protected override void OnProcess(ref long state)
	{
		base.OnProcess(ref state);

		_elapsed = _stopwatch.ElapsedMilliseconds;
		var deltaTime = (_elapsed - _prevElapsed) * _ratio;
		_prevElapsed = _elapsed;

		OnUpdate(deltaTime);
	}

	protected override void OnQuit(ref long state)
	{
		_stopwatch.Stop();

		OnQuit();
		base.OnQuit(ref state);
	}

	protected abstract void OnStart();

	protected abstract void OnUpdate(float deltaTime);

	protected abstract void OnQuit();
}