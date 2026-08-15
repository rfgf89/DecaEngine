using System;
using System.Diagnostics;
using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Graphics.Core;
using DecaEngine.Graphics.Diligent;
using DecaEngine.Sdl;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics;
using Engine.ImGui.Core;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using Hexa.NET.ImGui;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace DecaEngine.Editor;

public enum WindowType
{
	GameView, Inspector, Hierarchy, Console, AssetBrowser, Project, SceneView, Settings, Graphics,
#if DEBUG
	/// <summary>Окно отладки рендер-графа (см. <see cref="RenderGraphDebugWindow"/>) - только в DEBUG,
	/// потому что снимки графа в Release не собираются вовсе.</summary>
	RenderGraph,
#endif
}

public class EditorManager : TimeLoopCore
{
	private IGraphicsApi _graphicsApi;
	private IGraphicsPipeline _pipeline;
	private IWindowHandle _windowHandle;
	private DevicePull _devicePull;
	private IInputEventPull _inputEventPull;
	private ImGuiManager _imGuiManager;
	private DockLayout _dockLayout;
	private DiligentBatchRenderer _batchRenderer;
	private IRenderHandle _renderHandle;
	private DebugWindow _debugWindow;
	private MenuBarWindow _menuBarWindow;

	// --- ECS ---
	private EntityStore _ecsWorld;
	private SystemRoot _root;
	private RenderResourceManager _renderResourceManager;
	private ProjectSession _projectSession;
	private EditorSettings _editorSettings;
	private ModelPreviewViewport _modelPreviewViewport;
	private PrefabSceneViewport _prefabSceneViewport;
	private InspectorWindow _inspectorWindow;
	private ModelIconCache _modelIconCache;
	private ModelIconBaker _modelIconBaker;
	// --- /ECS ---

	public void Initialize()
	{
		_windowHandle = new SdlWindowHandle();
		_devicePull = new SdlDevicePull();
		_inputEventPull = new SdlEventPull(_windowHandle, _devicePull as SdlDevicePull);
		_graphicsApi = new DiligentGraphicsApi(_windowHandle);
		_imGuiManager = new ImGuiManager(_graphicsApi, _windowHandle, _inputEventPull, _devicePull);

		_dockLayout = new DockLayout("Dock Layout");
		_dockLayout.AddDockLayoutElement(new DockLayoutElement { name = "Project", imGuiDir = ImGuiDir.Left, ratio = 0.20f });
		_dockLayout.AddDockLayoutElement(new DockLayoutElement { name = "Asset Browser", imGuiDir = ImGuiDir.Right, ratio = 0.25f });
		_dockLayout.AddDockLayoutElement(new DockLayoutElement { name = "Console", imGuiDir = ImGuiDir.Down, ratio = 0.30f });
		_dockLayout.AddDockLayoutElement(new DockLayoutElement { name = "Hierarchy", imGuiDir = ImGuiDir.Down, ratio = 0.1f, });
		_dockLayout.AddDockLayoutElement(new DockLayoutElement { name = "Inspector", imGuiDir = ImGuiDir.Right, ratio = 0.25f });
		_dockLayout.AddDockLayoutElement(new DockLayoutElement { name = "Game View", imGuiDir = ImGuiDir.Down, ratio = 0.2f });
		_dockLayout.AddDockLayoutElement(new DockLayoutElement { name = "Scene View", imGuiDir = ImGuiDir.Right, ratio = 0.4f });

		_graphicsApi.OnCreateSetupInfo += setupInfo =>
		{
			setupInfo.contextCount = 4;
			if (setupInfo.backend == GraphicsBackend.D3D12) { setupInfo.dynamicHeapPageSize = 26 << 20; }
			else if (setupInfo.backend == GraphicsBackend.Vulkan) { setupInfo.dynamicHeapSize = 500000 << 20; }
		};
		
		this.Run();
	}

	private void OnWindowHandleResize() { }

	protected override void OnStart()
	{
		unsafe
		{
			_windowHandle.Initialize("DecaEngine Editor", 0, new Vector2(1920, 1080));
			_windowHandle.LoadAndSetIcon(Path.Combine(Environment.CurrentDirectory, "EditorAssets/Icons", "download (6).jpg"));
			_graphicsApi.Initialize(GraphicsBackend.D3D12);

			_imGuiManager.Initialize();

			_windowHandle.OnWindowResize += OnWindowHandleResize;

			_renderHandle = new DiligentRenderHandle((_graphicsApi as DiligentGraphicsApi).Device);

			_renderHandle.Alloc(new TextureInfo
			{
				width = (uint)_windowHandle.Size.X,
				height = (uint)_windowHandle.Size.Y,
				format = TextureObjectFormat.R8G8B8A8UNorm,
				name = "Game Main Render Target"
			});

			var dilPipe = _graphicsApi as DiligentGraphicsApi;
			_batchRenderer = new DiligentBatchRenderer(dilPipe);
			_pipeline = new GraphicsPipeline(dilPipe, _batchRenderer);

			_ecsWorld = new EntityStore();

			_editorSettings = EditorSettings.Load();

			// VSync из настроек применяется сразу на старте: окно Graphics Settings синхронизирует
			// его только на первом своём кадре, а до открытия окна редактор шёл бы с дефолтом api.
			// Переменная окружения DECA_VSYNC приоритетнее сохранённой настройки.
			if (Environment.GetEnvironmentVariable("DECA_VSYNC") is null)
			{
				_graphicsApi.PresentInterval = _editorSettings.VSync ? 1 : 0;
			}

			EditorAssetDatabase.Rescan();

			_renderResourceManager = new RenderResourceManager(2, 2, _ecsWorld, _batchRenderer);

			var cameraComponent = new CameraComponent(new CameraData(90f, 0.1f, 1000f, new Vector4(0, 0f, 1920f, 1080f)));
			cameraComponent.data.cullFlags = CullFlags.All;

			var ent = _ecsWorld.CreateEntity(
				new Position(0, 0, -4.0f),
				new Rotation() { value = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.6f) },
				new Scale3(1, 1, 1),
				cameraComponent
			);

			_root = new SystemRoot()
			{
				// Before GpuInstanceBufferSystem: it produces WorldMatrix/WorldTransformDirtyTag
				// that the instance-buffer upload consumes the same frame.
				new DecaEngine.Core.Entities.TransformSystem(),
				new GpuInstanceBufferSystem(),
				new CullingAndRenderSystem(_renderResourceManager, _graphicsApi, _pipeline),
				new FlyCameraSystem([ent], _devicePull)
			};
			_root.AddStore(_ecsWorld);

		CreateSceneSunEntity();

		_projectSession = new ProjectSession(_graphicsApi, _renderHandle, _ecsWorld, _root);
		_imGuiManager.ImGuiRender.UiScaleMultiplier = _editorSettings.UiScaleMultiplier;
		EditorPalette.ApplyFrom(_editorSettings);

			_menuBarWindow = new MenuBarWindow("Menu Bar", _dockLayout, _windowHandle, _projectSession, _editorSettings, _imGuiManager.ImGuiRender);
			_menuBarWindow.Show();

			_imGuiManager.ImGuiRender.AddWindowGetter(WindowType.GameView, () => new GameViewWindow("Game View", _renderHandle, _projectSession, _imGuiManager.ImGuiRender));

		var projectWindow = new ProjectWindow("Project", _projectSession, _imGuiManager.ImGuiRender);
		new GameViewWindow("Game View", _renderHandle, _projectSession, _imGuiManager.ImGuiRender).Show();
		_modelPreviewViewport = new ModelPreviewViewport(_graphicsApi, _editorSettings);
		_modelIconCache = new ModelIconCache(_graphicsApi, _imGuiManager.ImGuiRender);
		_modelIconBaker = new ModelIconBaker(_graphicsApi, _editorSettings, _modelIconCache);
		var inspectorWindow = new InspectorWindow("Inspector", _modelPreviewViewport, _imGuiManager.ImGuiRender);
		_inspectorWindow = inspectorWindow;
		inspectorWindow.Show();

		// GPU-вьюпорт окна Scene View (рендер префаба по AssetRef-ам, см. PrefabSceneViewport) -
		// один на редактор, окна Scene View его разделяют (как Inspector разделяет превью моделей).
		_prefabSceneViewport = new PrefabSceneViewport(_graphicsApi, _editorSettings, _projectSession);

		// ВРЕМЕННО: DECA_AUTOLOAD_MODEL=<путь> грузит модель в превью сразу при старте - для
		// автоматизированного репро багов загрузки/AO без ручных кликов по Asset Browser-у.
		var autoLoadModel = Environment.GetEnvironmentVariable("DECA_AUTOLOAD_MODEL");
		if (!string.IsNullOrEmpty(autoLoadModel))
		{
			_modelPreviewViewport.LoadModel(autoLoadModel);
		}
		_imGuiManager.ImGuiRender.AddWindowGetter(WindowType.SceneView, () => new SceneViewWindow("Scene View", inspectorWindow, _prefabSceneViewport, _imGuiManager.ImGuiRender));
		new SceneViewWindow("Scene View", inspectorWindow, _prefabSceneViewport, _imGuiManager.ImGuiRender).Show();
		new HierarchyWindow("Hierarchy", _imGuiManager.ImGuiRender).Show();
			new ConsoleWindow("Console", _imGuiManager.ImGuiRender).Show();

			_imGuiManager.ImGuiRender.AddWindowGetter(WindowType.Settings,
				() => new SettingsWindow("Settings", _editorSettings, _imGuiManager.ImGuiRender));
			// Окно Graphics (все настройки превью-графики + probe-GI, см. GraphicsSettingsWindow) -
			// открывается из меню Window; ссылка на вьюпорт нужна для статуса/ребейка проб.
			_imGuiManager.ImGuiRender.AddWindowGetter(WindowType.Graphics,
				() => new GraphicsSettingsWindow("Graphics", _editorSettings, _modelPreviewViewport, _prefabSceneViewport, _imGuiManager.ImGuiRender));
			new AssetBrowserWindow("Asset Browser", _projectSession, inspectorWindow, _modelIconCache, _modelIconBaker, _imGuiManager.ImGuiRender).Show();
			projectWindow.Show();
			//_debugWindow = new DebugWindow("Debug", _ecsWorld, _batchRenderer, _imGuiManager.ImGuiRender);
			//_debugWindow.ForceShow();

#if DEBUG
			// Окно отладки рендер-графа открывается из меню Window и САМО находит все живые конвейеры
			// через GraphicsPipelineRegistry - здесь передаётся лишь тот, на котором оно откроется
			// по умолчанию (основная сцена).
			_imGuiManager.ImGuiRender.AddWindowGetter(WindowType.RenderGraph,
				() => new RenderGraphDebugWindow("Render Graph Debugger", _imGuiManager.ImGuiRender, _pipeline));
#endif
		}
	}

	/// <summary>Солнце главной сцены: направленный свет с каскадными тенями.
	///
	/// Раньше это было хвостом CreateTestSceneEntities - функции, которая грузила с диска
	/// EditorAssets/models/result.gltf (6.8 МБ, с оптимизацией мешей и генерацией LOD-ов) и населяла
	/// ею сцену на КАЖДОМ запуске редактора, независимо от того, открывают проект или нет. Демо-сцена
	/// удалена целиком; солнце осталось, потому что оно не тестовое - без него у главной сцены нет ни
	/// направленного света, ни каскадов теней.</summary>
	private void CreateSceneSunEntity()
	{
        _ecsWorld.CreateEntity(
            new Position(0, 50, 0),
            new Rotation { value = Quaternion.CreateFromYawPitchRoll(0, 45, 0) },
            new LightComponent
            {
                Type = LightType.Directional,
                Color = Vector3.One,
                Intensity = 1.0f,
                ShadowStrength = 0.8f
            },
            new SunComponent(),
			new CascadedShadowComponent()
			{
				CascadeDistances = [0.01f, 10f, 30f, 100f, 300f]
			}
        );
	}
	
	protected override unsafe void OnUpdate(float deltaTime)
	{
		if (_inputEventPull.PullEvent())
		{
			Quit();
		}

		/*if (_windowHandle.IsMinimized)
		{
			// The DXGI/D3D12 swap chain's frame-latency waitable object never gets signaled while
			// the window is minimized (DWM stops presenting it), so calling Present() here spams
			// "Timeout elapsed while waiting for the frame waitable object" until the window returns.
			Thread.Sleep(50);
			return;
		}*/

		StyleEditorManager.SetDarkThemeColors(_windowHandle.GetScale() * _imGuiManager.ImGuiRender.UiScaleMultiplier);

		lock (GameHostBridge.GpuSync)
		{
			// Must run before the main scene render below: each offscreen Pipeline.Execute() here
			// rebinds the device's active render target to its own preview/icon-bake color+depth
			// texture (see ForwardPass.WriteCommands). _pipeline.Execute() unconditionally rebinds
			// the swap-chain backbuffer, so it must be the LAST Execute() this frame - otherwise
			// ImGui (and Present) would draw into whatever offscreen target was bound last instead
			// of the backbuffer, making the whole editor appear to go blank.
			_modelPreviewViewport.Update(deltaTime, time);
			_prefabSceneViewport.Update(deltaTime, time, _inspectorWindow.Root, _inspectorWindow.PrefabPath,
				_inspectorWindow.Selected);
			_modelIconBaker.Update(deltaTime, time);
			_inspectorWindow.UpdatePlayMode(deltaTime, time);

			_root.Update(new UpdateTick(deltaTime, time));
			_pipeline.Execute();

			_imGuiManager.BeforeLayout(deltaTime);
			_imGuiManager.ImGuiRender.RenderWindows();
			EditorLoadingStatus.Render(_imGuiManager.ImGuiRender.UiScaleMultiplier);
			//_debugWindow.FramePerSecond = framePerSecond;
			//_debugWindow.Render(0);
			_imGuiManager.AfterLayout();
			_graphicsApi.Present();
		}

		++numFramesRendered;
		timeFps += deltaTime;
		time += deltaTime;
		if (timeFps > 1.0)
		{
			framePerSecond = (numFramesRendered - lastNumFramesRendered);
			lastNumFramesRendered = numFramesRendered;
			timeFps = 0.0f;
		}
	}

	private float framePerSecond;
	private ulong numFramesRendered;
	private ulong lastNumFramesRendered;
	private float timeFps;
	private float time;

	protected override void OnQuit()
	{
		GameHostBridge.Reset();
		_imGuiManager.Release();
		_windowHandle.OnWindowResize -= OnWindowHandleResize;
		_windowHandle.Release();
		_graphicsApi.Release();
	}
}