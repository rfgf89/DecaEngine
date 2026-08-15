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

	// Single pending command written by Play/Pause/Quit (from any thread) and consumed
	// (read-and-cleared) once per loop iteration. The latest command wins; unlike the previous
	// rotating 3-slot queue a command can never be silently dropped because the slot cursor moved
	// independently of the writers.
	private long _pendingCommand;

	private long _currentState;

	public void Run()
	{
		Console.CancelKeyPress += (_, _) => Quit();

		Play();
		OnStart(ref _currentState);

		while (_currentState != QuitState)
		{
			var command = Interlocked.Exchange(ref _pendingCommand, NoneState);
			if (command != NoneState)
			{
				_currentState = command;
			}

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
		Interlocked.Exchange(ref _pendingCommand, PlayingState);
	}

	public void Pause()
	{
		Interlocked.Exchange(ref _pendingCommand, PausedState);
	}

	public void Quit()
	{
		Interlocked.Exchange(ref _pendingCommand, QuitState);
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