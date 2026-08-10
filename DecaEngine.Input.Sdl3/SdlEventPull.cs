using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Sdl;
using SDL;

public unsafe class SdlEventPull : IInputEventPull
{
	private readonly IWindowHandle _windowHandle;
	private readonly SdlDevicePull _devicePull;
	private readonly SdlWindowHandle _windowOutput;

	public event Action<Vector2> OnSurfaceResize;

	public Vector2 WindowSize
	{
		get
		{
			var x = 0;
			var y = 0;
			SDL3.SDL_GetWindowSize(_windowOutput.Window, &x, &y);
			return new Vector2(x, y);
		}
	}

	public SdlEventPull(IWindowHandle windowHandle, SdlDevicePull devicePull)
	{
		_windowHandle = windowHandle;
		_devicePull = devicePull;
		_windowOutput = (_windowHandle as SdlWindowHandle);
	}

	public bool PullEvent()
	{
		SDL_Event evt;
		SDL3.SDL_StartTextInput(_windowOutput.Window);

		while (SDL3.SDL_PollEvent(&evt))
		{
			switch (evt.type)
			{
				case (uint)SDL_EventType.SDL_EVENT_QUIT:
				{
					return true;
				}
				case (uint)SDL_EventType.SDL_EVENT_WINDOW_RESIZED:
				{
					var windowSize = WindowSize;
					OnSurfaceResize?.Invoke(windowSize);
					break;
				}
				case (uint)SDL_EventType.SDL_EVENT_WINDOW_MINIMIZED:
				{
					_windowHandle.IsMinimized = true;
					break;
				}
				case (uint)SDL_EventType.SDL_EVENT_WINDOW_RESTORED:
				case (uint)SDL_EventType.SDL_EVENT_WINDOW_MAXIMIZED:
				case (uint)SDL_EventType.SDL_EVENT_WINDOW_SHOWN:
				{
					_windowHandle.IsMinimized = false;
					break;
				}
				case (uint)SDL_EventType.SDL_EVENT_KEY_DOWN:
				{
					if (evt.key.key == SDL_Keycode.SDLK_ESCAPE)
					{
						return true;
					}
					break;
				}
			}

			if (_devicePull is IPerformSdlEvent sdlEvent)
			{
				sdlEvent.PerformSdlEvent(evt);
			}
		}

		SDL3.SDL_StopTextInput(_windowOutput.Window);

		return false;
	}
}