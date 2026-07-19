using System.Numerics;
using Friflo.Engine.ECS;

namespace DecaEngine.Core;

public class LoopCore : IEngineRun
{
	public enum State : long
	{
		None = 0L,
		Playing = 1L,
		Paused = 2L,
		Quit = 3L
	}

	private const long NoneState = 0L;
	private const long PlayingState = 1L;
	private const long PausedState = 2L;
	private const long QuitState = 3L;

	private long statePos;
	private readonly long[] _processQueue = new long[3];

	private long _currentState;

	public void Run()
	{
		Console.CancelKeyPress += (_, _) => Quit();

		Play();
		OnStart(ref _currentState);

		while (_currentState != QuitState)
		{
			var lockReadState = Interlocked.Read(ref statePos);
			var state = Interlocked.Read(ref _processQueue[lockReadState]);

			if (state != NoneState)
			{
				_currentState = state;
				Interlocked.Exchange(ref _processQueue[lockReadState], NoneState);
			}

			long statePlus = (lockReadState + 1) % 3;
			Interlocked.Exchange(ref statePos, statePlus);

			if (_currentState == PausedState)
			{
				Thread.Sleep(100);
			}
			else
			{
				OnProcess(ref _currentState);
			}
		}

		OnQuit(ref _currentState);
	}

	public void Play()
	{
		long stPos = Interlocked.Read(ref statePos);
		Interlocked.Exchange(ref _processQueue[stPos], PlayingState);
	}

	public void Pause()
	{
		long stPos = Interlocked.Read(ref statePos);
		Interlocked.Exchange(ref _processQueue[stPos], PausedState);
	}

	public void Quit()
	{
		long stPos = Interlocked.Read(ref statePos);
		Interlocked.Exchange(ref _processQueue[stPos], QuitState);
	}

	public IEngineRun GetRun()
	{
		return new LoopCore();
	}

	protected virtual void OnStart(ref long state)
	{

	}

	protected virtual void OnProcess(ref long state)
	{

	}

	protected virtual void OnQuit(ref long state)
	{

	}
}