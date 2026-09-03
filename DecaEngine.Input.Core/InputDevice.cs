using DecaEngine.Core;

public abstract class InputDevice : IInputDevice
{
	public uint deviceId { get; set; }

	// A list per event: several systems legitimately listen to the same event.
	protected readonly Dictionary<Enum, List<InputAction>> _actions = new ();

	public void AddListener(Enum actionEvent, InputAction inputAction)
	{
		if (!_actions.TryGetValue(actionEvent, out var list))
		{
			list = new List<InputAction>();
			_actions[actionEvent] = list;
		}

		if (!list.Contains(inputAction))
		{
			list.Add(inputAction);
		}
	}

	public void RemoveListener(Enum actionEvent, InputAction inputAction)
	{
		if (_actions.TryGetValue(actionEvent, out var list))
		{
			list.Remove(inputAction);
		}
	}

	public enum MouseEvent
	{
		LeftButton,
		RightButton,
		MiddleButton,
		WheelUp,
		WheelDown,
		WheelDelta,
		Position,
		PositionDelta
	}

	public enum KeyboardEvent
	{
		KeyUp,
		KeyDown,
	}

	public virtual void StartTextInput(IWindowHandle windowHandle)
	{

	}

	public virtual void StopTextInput(IWindowHandle windowHandle)
	{

	}
}