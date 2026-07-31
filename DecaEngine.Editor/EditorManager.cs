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
	GameView, Inspector, Hierarchy, Console, AssetBrowser, Project
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
#if DEBUG
	private RenderGraphDebugWindow _renderGraphDebugWindow;
#endif

	// --- ECS ---
	private EntityStore _ecsWorld;
	private SystemRoot _root;
	private RenderResourceManager _renderResourceManager;
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

		_graphicsApi.OnCreateSetupInfo += setupInfo =>
		{
			setupInfo.contextCount = 4;
			if (setupInfo.backend == GraphicsBackend.D3D12) { setupInfo.dynamicHeapPageSize = 26 << 20; }
			else if (setupInfo.backend == GraphicsBackend.Vulkan) { setupInfo.dynamicHeapSize = 26 << 20; }
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
			_graphicsApi.Initialize(GraphicsBackend.Vulkan);

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
			_scene = new Scene(_graphicsApi);

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
				new GpuInstanceBufferSystem(),
				new CullingAndRenderSystem(_renderResourceManager, _graphicsApi, _pipeline),
				new FlyCameraSystem([ent], _devicePull)
			};
			_root.AddStore(_ecsWorld);

			CreateTestSceneEntities();
			
			new MenuBarWindow("Menu Bar", _dockLayout, _imGuiManager.ImGuiRender).Show();
			new GameViewWindow("Game View", _renderHandle, _imGuiManager.ImGuiRender).Show();
			new InspectorWindow("Inspector", _imGuiManager.ImGuiRender).Show();
			new HierarchyWindow("Hierarchy", _imGuiManager.ImGuiRender).Show();
			new ConsoleWindow("Console", _imGuiManager.ImGuiRender).Show();
			new AssetBrowserWindow("Asset Browser", _imGuiManager.ImGuiRender).Show();
			new ProjectWindow("Project", _imGuiManager.ImGuiRender).Show();
			_debugWindow = new DebugWindow("Debug", _ecsWorld, _batchRenderer, _imGuiManager.ImGuiRender);
			_debugWindow.Show();
#if DEBUG
			_renderGraphDebugWindow = new RenderGraphDebugWindow("Render Graph Debugger", _pipeline, _imGuiManager.ImGuiRender);
			_renderGraphDebugWindow.Show();
#endif
		}
	}

	private Scene _scene;

	private void CreateTestSceneEntities()
	{
		var baseMaterialState = _batchRenderer.GetBaseState();
		var materialIdMap = new Dictionary<int, MaterialId>();
		for (int i = 0; i < _scene.materialObjects.Count; i++)
		{
			var kvp = _scene.materialObjects.GetAt(i);
			var materialObj = kvp.Value;
			materialObj.SetState(baseMaterialState);

			var matId = _batchRenderer.Register(materialObj);
			materialIdMap.Add(kvp.Key, matId);
		}

		var meshIdMap = new Dictionary<int, MeshId>();
		for (int i = 0; i < _scene.Meshes.Count; i++)
		{
			var meshObj = _scene.Meshes[i];
			var meshId = _batchRenderer.Register(meshObj);
			meshIdMap.Add(i, meshId);
		}

		var batchCache = new Dictionary<(int, int), BatchId>();

		for (int i = 0; i < _scene.instances.Count; i++)
		{
			var instance = _scene.instances[i];

			if (!meshIdMap.TryGetValue(instance.meshId, out var mId))
			{
				continue;
			}

			if (!materialIdMap.TryGetValue(instance.materialId, out var matId))
			{
				matId = materialIdMap[-1];
			}

			if (!batchCache.TryGetValue((instance.meshId, instance.materialId), out var batchId))
			{
				batchId = _batchRenderer.CreateBatch(mId, matId);
				batchCache.Add((instance.meshId, instance.materialId), batchId);
			}

			var entity = _ecsWorld.CreateEntity(
				new Position(instance.transform.position.X, instance.transform.position.Y, instance.transform.position.Z),
				new Scale3(instance.transform.scale.X, instance.transform.scale.Y, instance.transform.scale.Z),
				new Rotation(instance.transform.rotation.X, instance.transform.rotation.Y, instance.transform.rotation.Z, instance.transform.rotation.W),
				Tags.Get<GpuUpdateTag>()
			);

			_renderResourceManager.RegisterRenderable(entity, batchId);
		}

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

		StyleEditorManager.SetDarkThemeColors(_windowHandle.GetScale());
		
		_root.Update(new UpdateTick(deltaTime, time));
		_pipeline.Execute();
		
		_imGuiManager.BeforeLayout(deltaTime);
		
		_debugWindow.FramePerSecond = framePerSecond;

		_debugWindow.Render(0);
#if DEBUG
		_renderGraphDebugWindow.Render(0);
#endif
		_imGuiManager.AfterLayout();
		_graphicsApi.Present();

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
		_imGuiManager.Release();
		_windowHandle.OnWindowResize -= OnWindowHandleResize;
		_windowHandle.Release();
		_graphicsApi.Release();
	}
}