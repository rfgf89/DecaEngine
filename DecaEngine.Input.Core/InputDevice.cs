using DecaEngine.Core;

public abstract class InputDevice : IInputDevice
{
	public uint deviceId { get; set; }

	// Несколько подписчиков (например, ImGuiRender и FlyCameraSystem) могут слушать одно и то же
	// событие (скажем, MouseEvent.RightButton). Раньше здесь был Dictionary<Enum, InputAction>,
	// и повторный AddListener на тот же ключ молча ЗАМЕНЯЛ предыдущего подписчика - из-за этого,
	// например, правая кнопка мыши доставалась только одной системе, а остальные (в т.ч. ImGui,
	// а значит и контекстные меню) её вообще не получали. Список подписчиков на событие устраняет
	// эту проблему.
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