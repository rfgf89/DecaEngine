using System.Diagnostics;

namespace DecaEngine.Core;

public abstract class TimeLoopCore : LoopCore
{
	/// <summary>Upper bound for a single frame's delta time in seconds. A debugger breakpoint or an
	/// OS hitch otherwise produces a huge dt that explodes the simulation.</summary>
	private const float MaxDeltaTime = 0.25f;

	/// <summary>Cap of fixed steps executed per frame; the remaining accumulator is dropped when the
	/// cap is hit so catch-up work cannot snowball frame over frame (spiral of death).</summary>
	private const int MaxFixedStepsPerFrame = 8;

	private Stopwatch _stopwatch;
	private long _prevElapsedTicks;
	private double _fixedAccumulator;
	private float _fixedTimeStep = 1f / 60f;

	/// <summary>Fixed simulation step in seconds passed to <see cref="OnFixedUpdate"/>. Default 1/60.</summary>
	public float FixedTimeStep
	{
		get => _fixedTimeStep;
		set => _fixedTimeStep = value > 0.0001f ? value : 0.0001f;
	}

	protected override void OnStart(ref long state)
	{
		base.OnStart(ref state);
		_stopwatch = Stopwatch.StartNew();
		_prevElapsedTicks = 0L;
		_fixedAccumulator = 0.0;

		OnStart();
	}

	protected override void OnProcess(ref long state)
	{
		base.OnProcess(ref state);

		// ElapsedTicks / Frequency: ElapsedMilliseconds quantizes to whole milliseconds, which makes
		// dt alternate between 0 and 1 ms at high frame rates.
		long elapsedTicks = _stopwatch.ElapsedTicks;
		var deltaTime = (float)((elapsedTicks - _prevElapsedTicks) / (double)Stopwatch.Frequency);
		_prevElapsedTicks = elapsedTicks;

		if (deltaTime > MaxDeltaTime)
		{
			deltaTime = MaxDeltaTime;
		}
		else if (deltaTime < 0f)
		{
			deltaTime = 0f;
		}

		_fixedAccumulator += deltaTime;

		var step = _fixedTimeStep;
		int steps = 0;
		while (_fixedAccumulator >= step && steps < MaxFixedStepsPerFrame)
		{
			OnFixedUpdate(step);
			_fixedAccumulator -= step;
			steps++;
		}

		if (_fixedAccumulator >= step)
		{
			// Hit the per-frame cap: drop the remainder instead of carrying the debt forward.
			_fixedAccumulator = 0.0;
		}

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

	/// <summary>Called 0..N times per frame with a constant <see cref="FixedTimeStep"/> before the
	/// variable-rate <see cref="OnUpdate"/> of the same frame. Default implementation does nothing.</summary>
	protected virtual void OnFixedUpdate(float fixedDeltaTime)
	{
	}

	protected abstract void OnQuit();
}
