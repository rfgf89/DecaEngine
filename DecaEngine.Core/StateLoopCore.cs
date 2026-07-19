namespace DecaEngine.Core;

public abstract class StateLoopCore : LoopCore
{
	protected override void OnStart(ref long state)
	{
		base.OnStart(ref state);

		var statEnum = (State)state;
		Start(ref statEnum);
		state = (long)statEnum;
	}

	protected override void OnProcess(ref long state)
	{
		base.OnProcess(ref state);

		var statEnum = (State)state;
		OnProcess(ref statEnum);
		state = (long)statEnum;
	}

	protected override void OnQuit(ref long state)
	{
		var statEnum = (State)state;
		OnQuit(ref statEnum);
		state = (long)statEnum;

		base.OnQuit(ref state);
	}

	protected abstract void Start(ref State state);

	protected abstract void OnProcess(ref State state);

	protected abstract void OnQuit(ref State state);
}