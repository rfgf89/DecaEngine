using DecaEngine.Core;

public abstract class InputDevice : IInputDevice
{
	public uint deviceId { get; set; }
	protected readonly Dictionary<Enum, InputAction> _actions = new ();

	public void AddListener(Enum actionEvent, InputAction inputAction)
	{
		_actions[actionEvent] = inputAction;
	}

	public void RemoveListener(Enum actionEvent, InputAction inputAction)
	{
		_actions.Remove(actionEvent);
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