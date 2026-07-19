using DecaEngine.Core;
using DecaEngine.Sdl;
using SDL;

public class KeyboardSdlDevice : InputDevice, IPerformSdlEvent
{
	private readonly Dictionary<SDL_EventType, Action<SDL_Event>> _events;

	public override unsafe void StartTextInput(IWindowHandle windowHandle)
	{
		if (windowHandle is not SdlWindowHandle sdlWindowOutput)
		{
			return;
		}

		base.StartTextInput(sdlWindowOutput);
		SDL3.SDL_StartTextInput(sdlWindowOutput.Window);
	}

	public override unsafe void StopTextInput(IWindowHandle windowHandle)
	{
		if (windowHandle is not SdlWindowHandle sdlWindowOutput)
		{
			return;
		}

		base.StopTextInput(sdlWindowOutput);
		SDL3.SDL_StopTextInput(sdlWindowOutput.Window);
	}

	public KeyboardSdlDevice()
	{
		_events = new Dictionary<SDL_EventType, Action<SDL_Event>>
		{
			{ SDL_EventType.SDL_EVENT_TEXT_INPUT, TextInput },
			{ SDL_EventType.SDL_EVENT_KEY_DOWN, KeyDown },
			{ SDL_EventType.SDL_EVENT_KEY_UP, KeyUp }
		};
	}

	public void PerformSdlEvent(SDL_Event sdlEvent)
	{
		if (_events.TryGetValue(sdlEvent.Type, out var action))
		{
			action(sdlEvent);
		}
	}

	private void TextInput(SDL_Event obj)
	{
		var str = obj.text.GetText();

		if (str == null)
		{
			return;
		}

		if (_actions.TryGetValue(KeyboardKeys.Keys.LastKeyChar, out var actionChar))
		{
			actionChar.Perform(str);
		}
	}

	private void KeyUp(SDL_Event obj)
	{
		var sdlKeyName = SDL3.SDL_GetKeyName(obj.key.key);

		if (sdlKeyName == null)
		{
			return;
		}

		if (KeyboardKeys.KeyNameDictionary.TryGetValue(sdlKeyName, out KeyboardKeys.Keys keyEnumValue))
		{
			if (_actions.TryGetValue(KeyboardKeys.Keys.All, out var actionAll))
			{
				actionAll.Released(keyEnumValue);
			}

			if (_actions.TryGetValue(keyEnumValue, out var action))
			{
				action.Released(0f);
			}
		}
	}

	private void KeyDown(SDL_Event obj)
	{
		var sdlKeyName = SDL3.SDL_GetKeyName(obj.key.key);
		if (sdlKeyName == null)
		{
			return;
		}

		if (KeyboardKeys.KeyNameDictionary.TryGetValue(sdlKeyName, out KeyboardKeys.Keys keyEnumValue))
		{
			if (_actions.TryGetValue(KeyboardKeys.Keys.All, out var actionAll))
			{
				actionAll.Pressed(keyEnumValue);
			}

			if (_actions.TryGetValue(keyEnumValue, out var action))
			{
				action.Pressed(1f);
			}
		}
	}
}