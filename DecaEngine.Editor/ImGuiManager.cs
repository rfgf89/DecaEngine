using System;
using System.Diagnostics;
using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Graphics.Core;
using DecaEngine.Graphics.Diligent;
using DecaEngine.Sdl;
using Engine.ImGui.Core;
using Hexa.NET.ImGui;

namespace DecaEngine.Editor
{
	public class ImGuiManager
	{
		private IGraphicsApi _graphicsApi;
		private IWindowHandle _windowHandle;
		private IInputEventPull _inputEventPull;
		private ImGuiRender _imGuiRender;
		private DevicePull _devicePull;

		public ImGuiRender ImGuiRender => _imGuiRender;

		public ImGuiManager(IGraphicsApi graphicsApi, IWindowHandle windowHandle, IInputEventPull inputEventPull, DevicePull devicePull)
		{
			_graphicsApi = graphicsApi;
			_windowHandle = windowHandle;
			_inputEventPull = inputEventPull;
			_devicePull = devicePull;
		}

		public void Initialize()
		{
			_imGuiRender = new ImGuiDiligentRender(_graphicsApi as DiligentGraphicsApi);
			
			ImGuiRender.InitializeImGui(ImGuiConfigFlags.NavEnableKeyboard |
			                            ImGuiConfigFlags.NavEnableGamepad |
			                            ImGuiConfigFlags.DockingEnable |
			                            ImGuiConfigFlags.ViewportsEnable);

			var io = ImGui.GetIO();
			io.ConfigViewportsNoAutoMerge = false;
			io.ConfigViewportsNoTaskBarIcon = false;
			io.ConfigDragClickToInputText = true;
			io.ConfigDebugIsDebuggerPresent = Debugger.IsAttached;
			io.ConfigErrorRecoveryEnableDebugLog = true;
			io.ConfigErrorRecovery = true;
			io.ConfigErrorRecoveryEnableAssert = false;
			io.ConfigDpiScaleFonts = true;
			io.ConfigDpiScaleViewports = true;
			io.WantSaveIniSettings = false;

			unsafe
			{
				uint* glyphRanges = stackalloc uint[]
				{
					(uint)0xe005, (uint)0xe684,
					(uint)0xF000, (uint)0xF8FF,
					(uint)0 // null terminator
				};

				uint* glyphMaterialRanges = stackalloc uint[]
				{
					0xe003, 0xF8FF,
					0 // null terminator
				};

				var config = ImGui.ImFontConfig();
				config.FontDataOwnedByAtlas = true;
				config.GlyphRanges = glyphRanges;

				var configMaterial = ImGui.ImFontConfig();
				configMaterial.FontDataOwnedByAtlas = true;
				configMaterial.GlyphRanges = glyphMaterialRanges;
				configMaterial.MergeMode = true;
				configMaterial.GlyphOffset.Y = 5f;

				var regularFont = io.Fonts.AddFontFromFileTTF(Path.Combine(Environment.CurrentDirectory, "EditorAssets/Inter/Inter_24pt-Medium.ttf"), 24f, config);
				var headingFont = io.Fonts.AddFontFromFileTTF(Path.Combine(Environment.CurrentDirectory, "EditorAssets/proggyfonts/ProggyVector/ProggyVector-Dotted.ttf"), 20f, config);
				var materialFont = io.Fonts.AddFontFromFileTTF(Path.Combine(Environment.CurrentDirectory, "EditorAssets/MaterialIcons-Regular.ttf"), 24f, configMaterial);

				_imGuiRender.AddFont(FontType.Regular, regularFont);
				_imGuiRender.AddFont(FontType.Heading, headingFont);
				_imGuiRender.AddFont(FontType.MaterialSymbols, materialFont);

				io.FontDefault = regularFont;
			}

			OnSurfaceResize(_windowHandle.Size);
			_imGuiRender.Initialize(_devicePull);

			_inputEventPull.OnSurfaceResize += OnSurfaceResize;
		}

		private void OnSurfaceResize(Vector2 surface)
		{
			_imGuiRender.SetupWindow(surface, Vector2.One);
			_windowHandle.Size = surface;
		}

		public void BeforeLayout(float deltaTime)
		{
			_imGuiRender.BeforeLayout(deltaTime);
		}

		public void AfterLayout()
		{
			_imGuiRender.AfterLayout();
		}

		public void Release()
		{
			_inputEventPull.OnSurfaceResize -= OnSurfaceResize;
		}
	}
}