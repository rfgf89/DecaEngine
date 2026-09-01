using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using DecaEngine.Core;
using Diligent;
using DiligentEngineNET.Samples.Utils;
using Hexa.NET.ImGui;
using SDL;
using Version = Diligent.Version;
using DecaEngine.Graphics;

namespace DecaEngine;

public abstract unsafe class Application(GraphicsBackend graphicsBackend)
{
	private SDL_Window* _window;
	private IEngineFactory? _engineFactory;
	private IRenderDevice? _renderDevice;
	private IDeviceContext? _immediateContext;
	private IDeviceContext[] _deferredDevices = [];
	private ISwapChain? _swapChain;

	private readonly List<Profiler> _profilers = new();

	protected IEngineFactory EngineFactory => _engineFactory ?? throw new NullReferenceException();
	protected IRenderDevice Device => _renderDevice ?? throw new NullReferenceException();
	protected IDeviceContext ImmediateContext => _immediateContext ?? throw new NullReferenceException();
	protected IDeviceContext[] DeferredContexts => _deferredDevices ?? [];
	protected ISwapChain SwapChain => _swapChain ?? throw new NullReferenceException();

	private DevicePull devicePull = new SdlDevicePull();

	public Size WindowSize
	{
		get
		{
			var x = 0;
			var y = 0;
			SDL3.SDL_GetWindowSize(_window, &x, &y);
			return new Size(x, y);
		}
	}

	public void Setup()
	{
		SetupSDL();
		SetupDiligentEngine();
		OnSetup();
	}

	public void Run()
	{
		var sdlPollProfiler = new Profiler("SDL_PollEvent");
		var updateProfiler = new Profiler("Update");

		_profilers.Add(sdlPollProfiler);
		_profilers.Add(updateProfiler);

		var stop = false;

		Console.CancelKeyPress += (_, _) => stop = true;

		var stopWatch = Stopwatch.StartNew();
		var prevElapsed = 0L;
		var ratio = 1 / 1000.0;

		while (!stop)
		{
			var elapsed = stopWatch.ElapsedMilliseconds;
			var dt = (elapsed - prevElapsed) * ratio;
			prevElapsed = elapsed;

			sdlPollProfiler.Begin();
			SDL_Event evt;
			while (SDL3.SDL_PollEvent(&evt))
			{
				if (devicePull is IPerformSdlEvent sdlEvent)
				{
					sdlEvent.PerformSdlEvent(evt);
				}

				if (evt.type == (uint)SDL_EventType.SDL_EVENT_QUIT)
				{
					stop = true;
				}
				else if (evt.type == (uint)SDL_EventType.SDL_EVENT_WINDOW_RESIZED)
				{
					var windowSize = WindowSize;
					SwapChain.Resize((uint)windowSize.Width, (uint)windowSize.Height, SurfaceTransform.Identity);
				}
				else if (evt.type == (uint)SDL_EventType.SDL_EVENT_KEY_DOWN)
				{
					if (evt.key.key == SDL_Keycode.SDLK_ESCAPE)
					{
						stop = true;
					}
				}
			}

			sdlPollProfiler.End();

			updateProfiler.Begin();
			OnUpdate(dt);
			OnPresent();
			updateProfiler.End();
		}

		stopWatch.Stop();

		OnExit();
		ReleaseDiligentObjects();

		SDL3.SDL_DestroyWindow(_window);
		SDL3.SDL_Quit();

		Console.WriteLine(".:: Profiler Timers ::.");
		foreach (var profiler in _profilers)
			Console.WriteLine(profiler);
	}

	private void SetupSDL()
	{
		if (!SDL3.SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO))
			throw new Exception("Failed to initialize SDL");

		_window = SDL3.SDL_CreateWindow("Diligent Engine .NET - Samples - " + graphicsBackend, 1920, 1080, SDL_WindowFlags.SDL_WINDOW_RESIZABLE);
		SDL3.SDL_ShowWindow(_window);
		SDL3.SDL_SetWindowSurfaceVSync(_window, 0);
	}

	private void SetupDiligentEngine()
	{
		switch (graphicsBackend)
		{
			case GraphicsBackend.D3D11:
				SetupD3D11();
				break;
			case GraphicsBackend.D3D12:
				SetupD3D12();
				break;
			case GraphicsBackend.Vulkan:
				SetupVulkan();
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(graphicsBackend), graphicsBackend, null);
		}

		void SetupD3D11()
		{
			var engineFactory = Native.GetEngineFactoryD3D11();
			if (engineFactory is null)
				throw new NullReferenceException($"Failed to get {nameof(IEngineFactoryD3D11)}");
			engineFactory.SetMessageCallback(OnMessageCallback);

			var adapter = FindBestAdapter(engineFactory);
			var createInfo = new EngineD3D11CreateInfo
			{
				EnableValidation = true,
				GraphicsAPIVersion = new Version(11, 0),
				AdapterId = (uint)adapter
			};
			OnSetupEngineCreateInfo(createInfo);

			engineFactory.CreateDeviceAndContextsD3D11(createInfo, out var renderDevice, out var deviceContexts);
			_engineFactory = engineFactory;
			_immediateContext = deviceContexts[0];
			_deferredDevices = deviceContexts.Skip(1).ToArray();
			_renderDevice = renderDevice;

			var wndSize = WindowSize;
			var swapChainDesc = new SwapChainDesc
			{
				Width = (uint)wndSize.Width,
				Height = (uint)wndSize.Height,
				BufferCount = 2,
				Usage = SwapChainUsageFlags.RenderTarget,
				IsPrimary = true,
				ColorBufferFormat = TextureFormat.RGBA8_UNorm,
				DepthBufferFormat = TextureFormat.D16_UNorm
			};

			OnSetupSwapChainDesc(swapChainDesc);
			var swapChain = engineFactory.CreateSwapChainD3D11(renderDevice, deviceContexts[0], swapChainDesc,
				new FullScreenModeDesc(), GetNativeWindowHandle());

			_swapChain = swapChain;
		}

		void SetupD3D12()
		{
			var engineFactory = Native.GetEngineFactoryD3D12();
			if (engineFactory is null)
				throw new NullReferenceException($"Failed to get {nameof(IEngineFactoryD3D12)}");
			engineFactory.SetMessageCallback(OnMessageCallback);
			engineFactory.LoadD3D12("d3d12.dll");

			var adapter = FindBestAdapter(engineFactory);

			var createInfo = new EngineD3D12CreateInfo
			{
				EnableValidation = true,
				AdapterId = (uint)adapter
			};

			OnSetupEngineCreateInfo(createInfo);
			engineFactory.CreateDeviceAndContextsD3D12(createInfo, out var renderDevice, out var deviceContexts);
			_engineFactory = engineFactory;
			_immediateContext = deviceContexts[0];
			_deferredDevices = deviceContexts.Skip(1).ToArray();
			_renderDevice = renderDevice;

			var wndSize = WindowSize;
			var swapChainDesc = new SwapChainDesc
			{
				Width = (uint)wndSize.Width,
				Height = (uint)wndSize.Height,
				BufferCount = 2,
				Usage = SwapChainUsageFlags.RenderTarget,
				IsPrimary = true,
				ColorBufferFormat = TextureFormat.RGBA8_UNorm,
				DepthBufferFormat = TextureFormat.D16_UNorm
			};
			OnSetupSwapChainDesc(swapChainDesc);
			var swapChain = engineFactory.CreateSwapChainD3D12(renderDevice, deviceContexts[0], swapChainDesc,
				new FullScreenModeDesc(), GetNativeWindowHandle());

			_swapChain = swapChain;
		}

		void SetupVulkan()
		{
			var engineFactory = Native.GetEngineFactoryVk();
			if (engineFactory is null)
				throw new NullReferenceException($"Failed to get {nameof(IEngineFactoryVk)}");
			engineFactory.SetMessageCallback(OnMessageCallback);

			var adapter = FindBestAdapter(engineFactory);
			var createInfo = new EngineVkCreateInfo
			{
				EnableValidation = true,
				AdapterId = (uint)adapter
			};
			OnSetupEngineCreateInfo(createInfo);

			engineFactory.CreateDeviceAndContextsVk(createInfo, out var renderDevice, out var deviceContexts);
			_engineFactory = engineFactory;
			_immediateContext = deviceContexts[0];
			_deferredDevices = deviceContexts.Skip(1).ToArray();
			_renderDevice = renderDevice;

			var wndSize = WindowSize;
			var swapChainDesc = new SwapChainDesc
			{
				Width = (uint)wndSize.Width,
				Height = (uint)wndSize.Height,
				BufferCount = 2,
				Usage = SwapChainUsageFlags.RenderTarget,
				IsPrimary = true,
				ColorBufferFormat = TextureFormat.RGBA8_UNorm,
				DepthBufferFormat = TextureFormat.D16_UNorm
			};
			OnSetupSwapChainDesc(swapChainDesc);
			var swapChain = engineFactory.CreateSwapChainVk(renderDevice, deviceContexts[0], swapChainDesc, GetNativeWindowHandle());
			_swapChain = swapChain;
		}
	}

	private int FindBestAdapter(IEngineFactory factory)
	{
		var adapters = factory.EnumerateAdapters(new Version(11, 1)).ToList();
		GraphicsAdapterInfo suitableAdapter = default;
		int adapterIdx = 0;

		for (int i = 0; i < adapters.Count; i++)
		{
			if (adapters[i].Type == AdapterType.Discrete)
			{
				adapterIdx = i;
				suitableAdapter = adapters[i];
				break;
			}
		}

		if (suitableAdapter.DeviceId != 0)
			return adapterIdx;

		for (int i = 0; i < adapters.Count; i++)
		{
			if (adapters[i].Type == AdapterType.Integrated)
			{
				adapterIdx = i;
				suitableAdapter = adapters[i];
				break;
			}
		}

		if (suitableAdapter.DeviceId != 0) return adapterIdx;

		suitableAdapter = adapters.FirstOrDefault();
		return suitableAdapter.DeviceId != 0 ? 0 : throw new NullReferenceException("There's no graphics adapter available.");
	}

	private void ReleaseDiligentObjects()
	{
		_swapChain?.Dispose();
		foreach (var deviceContext in _deferredDevices)
		{
			deviceContext.Dispose();
		}
		_immediateContext?.Dispose();
		_renderDevice?.Dispose();
	}

	private Win32NativeWindow GetNativeWindowHandle()
	{
		var props = SDL3.SDL_GetWindowProperties(_window);

		if (OperatingSystem.IsWindows())
			return new Win32NativeWindow() { Wnd = SDL3.SDL_GetPointerProperty(props, SDL3.SDL_PROP_WINDOW_WIN32_HWND_POINTER, IntPtr.Zero) };
		if (OperatingSystem.IsLinux())

			/*return WindowHandle.CreateLinuxWindow(new LinuxWindowHandle
			{
				Window_id_ = (uint)SDL3
					.SDL_GetPointerProperty(props, SDL3.SDL_PROP_WINDOW_X11_WINDOW_NUMBER, IntPtr.Zero).ToInt32(),
				display_ = SDL3.SDL_GetPointerProperty(props, SDL3.SDL_PROP_WINDOW_X11_WINDOW_NUMBER, IntPtr.Zero)
			});*/

			return new Diligent.Win32NativeWindow{Wnd = IntPtr.Size};

		throw new PlatformNotSupportedException();
	}

	protected virtual void OnSetupEngineCreateInfo(EngineCreateInfo createInfo)
	{
	}

	protected virtual void OnSetupSwapChainDesc(SwapChainDesc swapChainDesc)
	{
	}

	protected abstract void OnSetup();
	protected abstract void OnUpdate(double dt);
	protected abstract void OnPresent();
	protected abstract void OnExit();

	private static void OnMessageCallback(DebugMessageSeverity severity, string message, string function, string file,
		int line)
	{
		Console.WriteLine($"[{severity}] {message} ({function}): {file}, {line}");
	}

	/*private IntPtr _fontAtlasID = (IntPtr)1;
	public void InitializeGUI()
	{
		ImGui.SetCurrentContext(ImGui.CreateContext());

		var io = ImGui.GetIO();

		io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset | ImGuiBackendFlags.RendererHasTextures;
		io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard | ImGuiConfigFlags.DockingEnable;
		io.Fonts.Flags |= ImFontAtlasFlags.NoBakedLines;

		CreateDeviceResources(_renderDevice);
		SetPerFrameImGuiData(1f / 60f);
		ImGui.NewFrame();

		_frameBegin = true;

		InitializeImGuiInput(devicePull);
	}*/

	/*private void RecreateFontDeviceTexture()
	{
		using var stream = File.OpenRead(Path.Combine(Environment.CurrentDirectory, "proggyfonts/ProggyOriginal", "ProggyClean.ttf"));
		byte[] dataByte = new byte[stream.Length];
		var data = stream.Read(dataByte);

		var io = ImGui.GetIO();
		var font = io.Fonts.AddFontFromFileTTF(
			Path.Combine(Environment.CurrentDirectory, "proggyfonts/ProggyOriginal", "ProggyClean.ttf"), 12);

		//io.Fonts.Fonts[0] = font;
		//io.Fonts.Build();
		//ImGuiNative.ImFontAtlas_AddFontDefault(io.Fonts.NativePtr, null);
		IntPtr pixels;
		int width, height, bytesPerPixel;

		//io.Fonts.GetTexDataAsRGBA32(out pixels, out width, out height, out bytesPerPixel);

		var textureDesc = new TextureDesc()
		{
			Name = "FontTextureSampler",
			Width = (uint)width,
			Height = (uint)height,
			Type = ResourceDimension.Tex2d,
			BindFlags = BindFlags.ShaderResource,
			Usage = Usage.Immutable,
			Format = TextureFormat.RGBA8_UNorm_sRGB,
		};

		_fontTexture = Device.CreateTexture(textureDesc, new TextureData()
		{
			SubResources =
			[
				new TextureSubResData()
				{
					Data = pixels,
					Stride = (ulong)(textureDesc.Width * bytesPerPixel),
				}
			]
		});

		_fontAtlasID = _fontTexture.NativePointer;

		//io.Fonts.SetTexID(_fontAtlasID);
		//io.Fonts.ClearTexData();
		//io.Fonts.Build();
		_fontTextureView = _fontTexture.GetDefaultView(TextureViewType.ShaderResource);
		var texx = io.Fonts;
		//Span<byte> sp= new Span<byte>(pixels.ToPointer(), width * height * bytesPerPixel);

		//MappedTextureSubresource mapPtr1 = ImmediateContext.MapTextureSubresource(_fontTexture, 0, 0,
		//	MapType.Write, MapFlags.Discard, new Box(){MaxX = (uint)width, MaxY = (uint)height});

		//Unsafe.CopyBlock(mapPtr1.Data.ToPointer(), pixels.ToPointer(), (uint)(width * height * bytesPerPixel));
		//ImmediateContext.UnmapTextureSubresource(_fontTexture, 0, 0);
	}*/

	/*private void UpdateImGuiInput()
	{
		ImGuiIOPtr io = ImGui.GetIO();

		io.AddMousePosEvent(snapshot.MousePosition.X, snapshot.MousePosition.Y);
		io.AddMouseButtonEvent(0, snapshot.IsMouseDown(MouseButton.Left));
		io.AddMouseButtonEvent(1, snapshot.IsMouseDown(MouseButton.Right));
		io.AddMouseButtonEvent(2, snapshot.IsMouseDown(MouseButton.Middle));
		io.AddMouseButtonEvent(3, snapshot.IsMouseDown(MouseButton.Button1));
		io.AddMouseButtonEvent(4, snapshot.IsMouseDown(MouseButton.Button2));
		io.AddMouseWheelEvent(0f, snapshot.WheelDelta);
		for (int i = 0; i < snapshot.KeyCharPresses.Count; i++)
		{
			io.AddInputCharacter(snapshot.KeyCharPresses[i]);
		}

		for (int i = 0; i < snapshot.KeyEvents.Count; i++)
		{
			KeyEvent keyEvent = snapshot.KeyEvents[i];
			if (TryMapKey(keyEvent.Key, out ImGuiKey imguikey))
			{
				io.AddKeyEvent(imguikey, keyEvent.Down);
			}
		}
	}*/
}