using System.Numerics;
using DecaEngine.Core;
using Hexa.NET.ImGui;

public enum FontType
{
	Regular,
	Heading,
	MaterialSymbols,
	Default = Regular,
}

public abstract unsafe class ImGuiRender
{
	public readonly List<ImGuiWindow> windows = new();

	private Vector2 _windowSize;
	private Vector2 _windowScale;
	private bool _frameBegin;
	private int _nextTexId = 1;
	public IGraphicsApi GraphicsApi { get; private set; }

	protected Dictionary<FontType, ImFontPtr> _fonts = new();

	public ImGuiRender(IGraphicsApi graphicsApi)
	{
		this.GraphicsApi = graphicsApi;
	}

	public void AddFont(FontType type, ImFont* font)
	{
		_fonts[type] = font;
	}

	public void PushFont(FontType type, float size)
	{
		ImGui.PushFont(_fonts[type], size);
	}

	public void PopFont()
	{
		ImGui.PopFont();
	}

	public static void InitializeImGui(ImGuiConfigFlags configFlags)
	{
		ImGui.SetCurrentContext(ImGui.CreateContext());
		var io = ImGui.GetIO();

		io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset | ImGuiBackendFlags.RendererHasTextures;
		io.ConfigFlags |= configFlags;
		io.ConfigWindowsMoveFromTitleBarOnly = true;
		io.ConfigDockingWithShift = false;
		io.ConfigDockingAlwaysTabBar = false;
		io.ConfigDpiScaleFonts = true;
		io.ConfigDpiScaleViewports = true;

		//io.Fonts.Flags |= ImFontAtlasFlags.NoBakedLines;
	}

	public Dictionary<Enum, Func<ImGuiWindow>> windowGetters = new();

	public void AddWindowGetter(Enum window, Func<ImGuiWindow> imGuiWindowGetter)
	{
		windowGetters.Add(window, imGuiWindowGetter);
	}

	public ImGuiWindow InstanceWindowGetter(Enum windowType)
	{
		var window = windowGetters[windowType].Invoke();
		return window;
	}

	public void AddWindow(ImGuiWindow imGuiWindow)
	{
		if (!windows.Contains(imGuiWindow))
		{
			windows.Add(imGuiWindow);
		}
	}

	public void RemoveWindow(ImGuiWindow imGuiWindow)
	{
		windows.Remove(imGuiWindow);
	}

	public virtual void Initialize(DevicePull devicePull)
	{
		CreateDeviceResources();
		SetPerFrameImGuiData();
		ImGui.NewFrame();
		_frameBegin = true;

		InitializeImGuiInput(devicePull);
	}

	public virtual void SetPerFrameImGuiData(float deltaSeconds = 1f / 60f)
	{
		ImGuiIOPtr io = ImGui.GetIO();
		io.DisplaySize = _windowSize;
		io.DisplayFramebufferScale = _windowScale;
		io.DeltaTime = deltaSeconds;
	}

	public void AfterLayout()
	{
		ImGui.Render();
		var drawData = ImGui.GetDrawData();
		ProcessTextureUpdates(drawData);
		RenderImDrawData(drawData);
	}

	public void BeforeLayout(double deltaSeconds)
	{
		var delta = MathF.Max((float)deltaSeconds, 0.0001f);
		ImGui.Render();

		SetPerFrameImGuiData(delta);

		ImGui.NewFrame();
	}

	public void RenderWindows()
	{
		if (_frameBegin)
		{
			for (var i = 0; i < windows.Count; i++)
			{
				var imGuiWindow = windows[i];
				imGuiWindow.FirstFrame(1);
			}
		}

		for (var i = 0; i < windows.Count; i++)
		{
			var imGuiWindow = windows[i];
			imGuiWindow.Render(1);
		}

		if (_frameBegin)
		{
			for (var i = 0; i < windows.Count; i++)
			{
				var imGuiWindow = windows[i];
				imGuiWindow.EndFirstFrame(1);
			}

			_frameBegin = false;
		}
	}

	public void SetupWindow(Vector2 size, Vector2 scale)
	{
		_windowSize = size;
		_windowScale = scale;
	}

	private void UpdateTexture(ImTextureDataPtr textureData)
	{
		switch (textureData.Status)
		{
			case ImTextureStatus.WantCreate:
				CreateTexture(textureData);
				break;

			case ImTextureStatus.WantUpdates:
				UpdateTextureData(textureData);
				break;

			case ImTextureStatus.WantDestroy:
				DestroyTexture(textureData);
				break;

			case ImTextureStatus.Ok:
				// Nothing to do
				break;
		}
	}

	private unsafe void ProcessTextureUpdates(ImDrawDataPtr drawData)
	{
		if (drawData.Textures.Data == null)
		{
			return;
		}

		for (int i = 0; i < drawData.Textures.Size; i++)
		{
			ImTextureDataPtr textureData = drawData.Textures.Data[i];
			UpdateTexture(textureData);
		}
	}

	public abstract void BindBackTarget(IRenderHandle? renderHandle);
	public abstract void BindRenderTarget(ImTextureID textureId, IRenderHandle renderHandle);
	public abstract unsafe ImTextureRef GetNewTexture();
	public abstract void GarbageTexture(ImTextureRef textureRef);
	protected abstract void CreateDeviceResources();
	protected abstract void RenderImDrawData(ImDrawDataPtr drawData);
	protected abstract void CreateTexture(ImTextureDataPtr textureData);
	protected abstract void UpdateTextureData(ImTextureDataPtr textureData);
	protected abstract void DestroyTexture(ImTextureDataPtr textureData);
	protected abstract void ReleaseDeviceResources();

	private void InitializeImGuiInput(DevicePull devicePull)
	{
		devicePull.OnDeviceConnected += deviceType =>
		{
			var mouseDevice = devicePull.GetFirstInputDevice(deviceType);

			if (deviceType == DevicePull.DeviceType.Keyboard)
			{
				var textInputAction = new InputAction();
				var buttonAction = new InputAction();

				buttonAction.onPressed += OnPressed;
				buttonAction.onReleased += OnReleased;

				textInputAction.onPerform += OnPerform;

				mouseDevice.AddListener(KeyboardKeys.Keys.All, buttonAction);
				mouseDevice.AddListener(KeyboardKeys.Keys.LastKeyChar, textInputAction);

				void OnReleased()
				{
					KeyboardKeys.Keys keys = buttonAction.Read<KeyboardKeys.Keys>();

					if (!KeyboardImGuiKeys.KeyToImGuiKey.TryGetValue(keys, out ImGuiKey key))
					{
						return;
					}

					ImGuiIOPtr io = ImGui.GetIO();
					io.AddKeyEvent(key, false);
				}

				void OnPressed()
				{
					KeyboardKeys.Keys keys = buttonAction.Read<KeyboardKeys.Keys>();

					if (!KeyboardImGuiKeys.KeyToImGuiKey.TryGetValue(keys, out ImGuiKey key))
					{
						return;
					}

					ImGuiIOPtr io = ImGui.GetIO();
					io.AddKeyEvent(key, true);
				}

				void OnPerform()
				{
					string keys = textInputAction.Read<string>();

					ImGuiIOPtr io = ImGui.GetIO();
					io.AddInputCharacter(keys[^1]);
				}
			}

			if (deviceType == DevicePull.DeviceType.Mouse)
			{
				var positionDeltaAction = new InputAction();
				var positionAction = new InputAction();

				var leftButtonAction = new InputAction();
				var rightButtonAction = new InputAction();
				var middleButtonAction = new InputAction();

				var upMouseWheelButtonAction = new InputAction();
				var downMouseWheelButtonAction = new InputAction();

				positionAction.onPerform += OnMousePosition;
				positionDeltaAction.onPerform += OnPositionDelta;

				leftButtonAction.onPressed += OnLeftButtonDown;
				leftButtonAction.onReleased += OnLeftButtonUp;
				rightButtonAction.onPressed += OnRightButtonDown;
				rightButtonAction.onReleased += OnRightButtonUp;
				middleButtonAction.onPressed += OnMiddleButtonDown;
				middleButtonAction.onReleased += OnMiddleButtonUp;

				upMouseWheelButtonAction.onPressed += OnX1ButtonDown;
				upMouseWheelButtonAction.onReleased += OnX1ButtonUp;
				downMouseWheelButtonAction.onPressed += OnX2ButtonDown;
				downMouseWheelButtonAction.onReleased += OnX2ButtonUp;

				mouseDevice.AddListener(InputDevice.MouseEvent.WheelDelta, positionDeltaAction);
				mouseDevice.AddListener(InputDevice.MouseEvent.Position, positionAction);

				mouseDevice.AddListener(InputDevice.MouseEvent.LeftButton, leftButtonAction);
				mouseDevice.AddListener(InputDevice.MouseEvent.RightButton, rightButtonAction);
				mouseDevice.AddListener(InputDevice.MouseEvent.MiddleButton, middleButtonAction);

				mouseDevice.AddListener(InputDevice.MouseEvent.WheelUp, upMouseWheelButtonAction);
				mouseDevice.AddListener(InputDevice.MouseEvent.WheelDown, downMouseWheelButtonAction);

				void OnMousePosition()
				{
					ImGuiIOPtr io = ImGui.GetIO();
					var mousePosition = positionAction.Read<Vector2>();
					io.AddMousePosEvent(mousePosition.X, mousePosition.Y);
				}

				void OnPositionDelta()
				{
					ImGuiIOPtr io = ImGui.GetIO();
					Vector2 positionDelta = positionDeltaAction.Read<Vector2>();
					io.AddMouseWheelEvent(positionDelta.X, positionDelta.Y);
				}
			}
		};

		void OnLeftButtonDown()
		{
			ImGuiIOPtr io = ImGui.GetIO();
			io.AddMouseButtonEvent(0, true);
		}

		void OnLeftButtonUp()
		{
			ImGuiIOPtr io = ImGui.GetIO();
			io.AddMouseButtonEvent(0, false);
		}

		void OnRightButtonDown()
		{
			ImGuiIOPtr io = ImGui.GetIO();
			io.AddMouseButtonEvent(1, true);
		}

		void OnRightButtonUp()
		{
			ImGuiIOPtr io = ImGui.GetIO();
			io.AddMouseButtonEvent(1, false);
		}

		void OnMiddleButtonDown()
		{
			ImGuiIOPtr io = ImGui.GetIO();
			io.AddMouseButtonEvent(2, true);
		}

		void OnMiddleButtonUp()
		{
			ImGuiIOPtr io = ImGui.GetIO();
			io.AddMouseButtonEvent(2, false);
		}

		void OnX1ButtonDown()
		{
			ImGuiIOPtr io = ImGui.GetIO();
			io.AddMouseButtonEvent(3, true);
		}

		void OnX1ButtonUp()
		{
			ImGuiIOPtr io = ImGui.GetIO();
			io.AddMouseButtonEvent(3, false);
		}

		void OnX2ButtonDown()
		{
			ImGuiIOPtr io = ImGui.GetIO();
			io.AddMouseButtonEvent(4, true);
		}

		void OnX2ButtonUp()
		{
			ImGuiIOPtr io = ImGui.GetIO();
			io.AddMouseButtonEvent(4, false);
		}
	}
}