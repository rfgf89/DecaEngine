using System.Numerics;
using SDL;

public class MouseSdlDevice : InputDevice, IPerformSdlEvent
{
	private readonly Dictionary<SDL_EventType, Action<SDL_Event>> _events;

	public MouseSdlDevice()
	{
		_events = new Dictionary<SDL_EventType, Action<SDL_Event>>
		{
			{ SDL_EventType.SDL_EVENT_MOUSE_MOTION, MouseMotion },
			{ SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP, MouseButtonUp },
			{ SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN, MouseButtonDown },
			{ SDL_EventType.SDL_EVENT_MOUSE_WHEEL, MouseButtonWheel },
		};
	}

	public void PerformSdlEvent(SDL_Event sdlEvent)
	{
		if (_events.TryGetValue(sdlEvent.Type, out var action))
		{
			action(sdlEvent);
		}
	}

	private void MouseMotion(SDL_Event sdlEvent)
	{
		if (_actions.TryGetValue(MouseEvent.Position, out var mousePosition))
		{
			mousePosition.Perform(new Vector2(sdlEvent.motion.x, sdlEvent.motion.y));
		}

		if (_actions.TryGetValue(MouseEvent.PositionDelta, out var mouseDeltaPosition))
		{
			mouseDeltaPosition.Perform(new Vector2(sdlEvent.motion.xrel, sdlEvent.motion.yrel));
		}
	}

	private void MouseButtonDown(SDL_Event sdlEvent)
	{
		switch (sdlEvent.button.Button)
		{
			case SDLButton.SDL_BUTTON_LEFT when _actions.TryGetValue(MouseEvent.LeftButton, out var buttonLeft):
				buttonLeft.Pressed(1f);
				break;
			case SDLButton.SDL_BUTTON_RIGHT when _actions.TryGetValue(MouseEvent.RightButton, out var buttonRight):
				buttonRight.Pressed(1f);
				break;
			case SDLButton.SDL_BUTTON_MIDDLE
				when _actions.TryGetValue(MouseEvent.MiddleButton, out var buttonMiddle):
				buttonMiddle.Pressed(1f);
				break;
			case SDLButton.SDL_BUTTON_X1
				when _actions.TryGetValue(MouseEvent.WheelUp, out var buttonWheelUp):
				buttonWheelUp.Pressed(1f);
				break;
			case SDLButton.SDL_BUTTON_X2
				when _actions.TryGetValue(MouseEvent.WheelDown, out var buttonWheelDown):
				buttonWheelDown.Pressed(1f);
				break;
			default:
				break;
		}
	}

	private void MouseButtonUp(SDL_Event sdlEvent)
	{
		switch (sdlEvent.button.Button)
		{
			case SDLButton.SDL_BUTTON_LEFT
				when _actions.TryGetValue(MouseEvent.LeftButton, out var buttonLeft):
				buttonLeft.Released(0f);
				break;
			case SDLButton.SDL_BUTTON_RIGHT
				when _actions.TryGetValue(MouseEvent.RightButton, out var buttonRight):
				buttonRight.Released(0f);
				break;
			case SDLButton.SDL_BUTTON_MIDDLE
				when _actions.TryGetValue(MouseEvent.MiddleButton, out var buttonMiddle):
				buttonMiddle.Released(0f);
				break;
			case SDLButton.SDL_BUTTON_X1
				when _actions.TryGetValue(MouseEvent.WheelUp, out var buttonWheelUp):
				buttonWheelUp.Released(0f);
				break;
			case SDLButton.SDL_BUTTON_X2
				when _actions.TryGetValue(MouseEvent.WheelDown, out var buttonWheelDown):
				buttonWheelDown.Released(0f);
				break;
			default:
				break;
		}
	}

	private void MouseButtonWheel(SDL_Event sdlEvent)
	{
		if (_actions.TryGetValue(MouseEvent.WheelDelta, out var mouseWheelDelta))
		{
			mouseWheelDelta.Perform(new Vector2(sdlEvent.wheel.x, sdlEvent.wheel.y));
		}
	}
}