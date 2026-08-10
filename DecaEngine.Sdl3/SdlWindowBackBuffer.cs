using System.Drawing;
using System.Numerics;
using DecaEngine.Core;
using SDL;
using StbImageSharp;

namespace DecaEngine.Sdl;

public class SdlWindowHandle : IWindowHandle
{
	public IntPtr Handle { get; private set; }

	public Vector2 Size
	{
		get;
		set
		{
			field = value;
			OnWindowResize?.Invoke();
		}
	}

	public event Action OnWindowResize;

	public bool IsMinimized { get; set; }

	public unsafe SDL_Window* Window { get; private set; }

	public unsafe void Initialize(string title, int vsync, Vector2 size)
	{
		if (!SDL3.SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO))
		{
			throw new Exception("Failed to initialize SDL");
		}

		Window = SDL3.SDL_CreateWindow(title, (int)size.X, (int)size.Y, SDL_WindowFlags.SDL_WINDOW_RESIZABLE);
		SDL3.SDL_ShowWindow(Window);
		SDL3.SDL_SetWindowSurfaceVSync(Window, vsync);
		Handle = GetNativeWindowHandle();
		Size = size;
	}

	public unsafe void LoadAndSetIcon(string path)
	{
		using var stream = File.OpenRead(path);
		var image = ImageResult.FromStream(stream);

		ImageToSdlSurface(image.Width, image.Height, image.Data.AsSpan());
	}

	public unsafe void SetTitle(string title)
	{
		SDL3.SDL_SetWindowTitle(Window, title);
	}

	private unsafe void ImageToSdlSurface(int width, int height, Span<byte> data)
	{
		fixed (void* dataPtr = data)
		{
			SDL_Surface* surface = SDL3.SDL_CreateSurfaceFrom(width, height, SDL_PixelFormat.SDL_PIXELFORMAT_RGB24, new IntPtr(dataPtr), width * 3);

			SDL3.SDL_SetWindowIcon(Window, surface);
			SDL3.SDL_DestroySurface(surface);
		}
	}

	public unsafe void Release()
	{
		SDL3.SDL_DestroyWindow(Window);
		SDL3.SDL_Quit();
	}

	public unsafe float GetScale()
	{
		var rect = new SDL_Rect();

		if (SDL3.SDL_GetDisplayBounds(SDL3.SDL_GetDisplayForWindow(Window), &rect))
		{
			return (rect.h / 1080f);
		}

		return 1f;
	}

	private unsafe IntPtr GetNativeWindowHandle()
	{
		var props = SDL3.SDL_GetWindowProperties(Window);

		if (OperatingSystem.IsWindows())
		{
			return SDL3.SDL_GetPointerProperty(props, SDL3.SDL_PROP_WINDOW_WIN32_HWND_POINTER, IntPtr.Zero);
		}

		if (OperatingSystem.IsLinux())

			/*return WindowHandle.CreateLinuxWindow(new LinuxWindowHandle
			{
				Window_id_ = (uint)SDL3
					.SDL_GetPointerProperty(props, SDL3.SDL_PROP_WINDOW_X11_WINDOW_NUMBER, IntPtr.Zero).ToInt32(),
				display_ = SDL3.SDL_GetPointerProperty(props, SDL3.SDL_PROP_WINDOW_X11_WINDOW_NUMBER, IntPtr.Zero)
			});*/

			return IntPtr.Size;

		throw new PlatformNotSupportedException();
	}
}