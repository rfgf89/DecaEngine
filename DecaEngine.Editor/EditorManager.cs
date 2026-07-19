using System;
using System.Diagnostics;
using System.Numerics;
using DecaEngine.Core;
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
	private IGraphicsPipeline _graphicsPipeline;
	private IWindowHandle _windowHandle;
	private DevicePull _devicePull;
	private IInputEventPull _inputEventPull;
	private ImGuiRender _imGuiRender;
	private DockLayout _dockLayout;
	private IRenderGraph _renderGraph;
	private DiligentBatchRenderer _batchRenderer;
	private IRenderHandle _renderHandle;

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
		_graphicsPipeline = new DiligentGraphicsPipeline(_windowHandle);
		_imGuiRender = new ImGuiDiligentRender(_graphicsPipeline as DiligentGraphicsPipeline);

		_dockLayout = new DockLayout("Dock Layout");
		_dockLayout.AddDockLayoutElement(new DockLayoutElement { name = "Project", imGuiDir = ImGuiDir.Left, ratio = 0.20f });
		_dockLayout.AddDockLayoutElement(new DockLayoutElement { name = "Asset Browser", imGuiDir = ImGuiDir.Right, ratio = 0.25f });
		_dockLayout.AddDockLayoutElement(new DockLayoutElement { name = "Console", imGuiDir = ImGuiDir.Down, ratio = 0.30f });
		_dockLayout.AddDockLayoutElement(new DockLayoutElement { name = "Hierarchy", imGuiDir = ImGuiDir.Down, ratio = 0.1f, });
		_dockLayout.AddDockLayoutElement(new DockLayoutElement { name = "Inspector", imGuiDir = ImGuiDir.Right, ratio = 0.25f });
		_dockLayout.AddDockLayoutElement(new DockLayoutElement { name = "Game View", imGuiDir = ImGuiDir.Down, ratio = 0.2f });

		new MenuBarWindow("Menu Bar", _dockLayout, _imGuiRender).Show();

		_graphicsPipeline.OnCreateSetupInfo += setupInfo =>
		{
			setupInfo.contextCount = 4;
			if (setupInfo.backend == GraphicsBackend.D3D12) { setupInfo.dynamicHeapPageSize = 26 << 20; }
			else if (setupInfo.backend == GraphicsBackend.Vulkan) { setupInfo.dynamicHeapSize = 26 << 20; }
		};
		//var tt = new MsBuildAssemblyCollector();
		//tt.Collect("C:\\Users\\rfgf89\\Desktop\\NewProject33\\NewProject33.csproj");
		//var tt = CsprojOutputResolver.GetBuildOutputs("C:\\Users\\rfgf89\\Desktop\\NewProject33\\NewProject33.csproj");

		//new Thread(() =>
		//{
		//	EditorBuilder.ExecuteCommand($"dotnet watch run --no-hot-reload --project \"C:\\Users\\rfgf89\\Desktop\\NewProject33\\NewProject33.sln\"");
		//}).Start();

		//var assembly = new AssemblyApp(typeof(IEngineRun).GetTypeInfo().Assembly.Location);
		//assembly.LoadFromPath();
		//assembly.Run();

		/*assembly.Load($$"""
		                using System;
		                using DecaEngine.Core;

		                namespace AppCore;

		                class Program
		                {
		                	private static IEngineRun _engineRun = new LoopCore().GetRun();

		                	private static void Main(string[] args)
		                	{
		                		_engineRun.Run();
		                	}

		                	public static void Play()
		                	{
		                		_engineRun.Play();
		                	}

		                	public static void Pause()
		                	{
		                		_engineRun.Pause();
		                	}

		                	public static void Quit()
		                	{
		                		_engineRun.Quit();
		                	}
		                }
		                """);*/

		this.Run();
	}

	private ImGuiWindow ProjectCreateInstance() => new ProjectWindow("Project", _imGuiRender);
	private ImGuiWindow AssetBrowserCreateInstance() => new AssetBrowserWindow("Asset Browser", _imGuiRender);
	private ImGuiWindow ConsoleCreateInstance() => new ConsoleWindow("Console", _imGuiRender);
	private ImGuiWindow HierarchyCreateInstance() => new HierarchyWindow("Hierarchy", _imGuiRender);
	private ImGuiWindow InspectorCreateInstance() => new InspectorWindow("Inspector", _imGuiRender);
	private ImGuiWindow GameViewCreateInstance() => new GameViewWindow("Game View", _renderHandle, _imGuiRender);

	private void OnWindowHandleResize() { }

	private void OnSurfaceResize(Vector2 surface)
	{
		_imGuiRender.SetupWindow(surface, Vector2.One);
		_windowHandle.Size = surface;
	}

	protected override void OnStart()
	{
		unsafe
		{
			_windowHandle.Initialize("DecaEngine Editor", 0, new Vector2(1920, 1080));
			_windowHandle.LoadAndSetIcon(Path.Combine(Environment.CurrentDirectory, "EditorAssets/Icons", "download (6).jpg"));
			_graphicsPipeline.Initialize(GraphicsBackend.Vulkan);

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

			OnSurfaceResize(_windowHandle.Size);
			_imGuiRender.Initialize(_devicePull);

			_inputEventPull.OnSurfaceResize += OnSurfaceResize;
			_windowHandle.OnWindowResize += OnWindowHandleResize;

			_renderHandle = new DiligentRenderHandle((_graphicsPipeline as DiligentGraphicsPipeline).Device);

			_renderHandle.Alloc(new RenderTargetInfo
			{
				width = (uint)_windowHandle.Size.X,
				height = (uint)_windowHandle.Size.Y,
				textureFormat = RenderTargetInfo.Format.R8G8B8A8_UNORM,
				name = "Game Main Render Target"
			});

			GameViewCreateInstance().Show();
			InspectorCreateInstance().Show();
			HierarchyCreateInstance().Show();
			ConsoleCreateInstance().Show();
			AssetBrowserCreateInstance().Show();
			ProjectCreateInstance().Show();

			var dilPipe = _graphicsPipeline as DiligentGraphicsPipeline;
			_batchRenderer = new DiligentBatchRenderer(dilPipe);
			
			_ecsWorld = new EntityStore();
			_scene = new Scene(_graphicsPipeline);

			_renderResourceManager = new RenderResourceManager( _scene.instances.Count,
				 _scene.Meshes.Count * _scene.materialObjects.Count, _ecsWorld, _batchRenderer);

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
				new CullingAndRenderSystem(_batchRenderer, _renderResourceManager, _graphicsPipeline as DiligentGraphicsPipeline),
				new FlyCameraSystem([ent], _devicePull)
			};
			_root.AddStore(_ecsWorld);

			CreateTestSceneEntities();
		}
	}

	private Scene _scene;

	private void CreateTestSceneEntities()
	{
		var materialIdMap = new Dictionary<int, MaterialId>();
		for (int i = 0; i < _scene.materialObjects.Count; i++)
		{
			var kvp = _scene.materialObjects.GetAt(i);
			var materialObj = kvp.Value;

			if (materialObj is DiligentMaterial dilMat)
			{
				dilMat.SetBasePipelineState(_batchRenderer.GetBaseState());
			}

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

	private bool _isBatchDebug;
    private Vector3 _tempEuler = Vector3.Zero;
    private int _lastLightEntityId = -1;

	protected override unsafe void OnUpdate(float deltaTime)
	{
		if (_inputEventPull.PullEvent())
		{
			Quit();
		}

		StyleEditorManager.SetDarkThemeColors(_windowHandle.GetScale());
		_graphicsPipeline.SetBackBufferTarget(new Vector4(0.1f, 0.1f, 0.1f,1));
		
		_root.Update(new UpdateTick(deltaTime, time));
		
		_imGuiRender.BeforeLayout(deltaTime);
		
		ImGui.Begin("Drawing");

		ImGui.Text("Fps : " + framePerSecond);

		ImGui.Checkbox("Debug Batch Renderer", ref _isBatchDebug);

		var cameras = _ecsWorld.Query<CameraComponent>();
		cameras.ForEachEntity((ref CameraComponent camera, Entity entity) =>
		{
			bool frustumCulling = (camera.data.cullFlags & CullFlags.Frustum) != 0;
			if (ImGui.Checkbox("Frustum Culling", ref frustumCulling) && frustumCulling)
			{
				camera.data.cullFlags |= CullFlags.Frustum;
			}

			bool lodSelection = (camera.data.cullFlags & CullFlags.Lod) != 0;
			if (ImGui.Checkbox("LOD Selection", ref lodSelection) && lodSelection)
			{
				camera.data.cullFlags |= CullFlags.Lod;
			}
		});

		if (_isBatchDebug)
		{
			var info = _batchRenderer.ReadInfo();
			ImGui.Text($"Total Batches: {_batchRenderer.GetBatches().Count}, Draw Calls: {info.pipelineStateCount}");

            // Debug Light Data
            if (ImGui.TreeNode("Light Debug"))
            {
                var lights = _ecsWorld.Query<LightComponent, SunComponent>();
                lights.ForEachEntity((ref LightComponent light, ref SunComponent sun, Entity lightEntity) =>
                {
                    if (_lastLightEntityId != lightEntity.Id)
                    {
                        _lastLightEntityId = lightEntity.Id;
                        _tempEuler = ToEulerAngles(lightEntity.Rotation.value) * (180f / (float)Math.PI);
                    }

                    ImGui.Text($"Entity: {lightEntity.Id}");
                    
                    var pos = lightEntity.Position.value;
                    if (ImGui.DragFloat3("Position", ref pos))
                        lightEntity.Position = new Position(pos.X, pos.Y, pos.Z);

                    if (ImGui.DragFloat3("Rotation (Euler)", ref _tempEuler))
                    {
                        var rad = _tempEuler * ((float)Math.PI / 180f);
                        lightEntity.Rotation = new Rotation { value = Quaternion.CreateFromYawPitchRoll(rad.Y, rad.X, rad.Z) };
                    }
                    
                    ImGui.DragFloat("Intensity", ref light.Intensity, 0.05f, 0f, 100f);
                    ImGui.ColorEdit3("Color", ref light.Color);
                    ImGui.DragFloat("Shadow Strength", ref light.ShadowStrength, 0.001f, 0f, 10f);

                    var lightDirection = Vector3.Transform(Vector3.UnitZ, lightEntity.Rotation.value);
                    ImGui.Text($"Direction: {lightDirection}");
                });
                ImGui.TreePop();
            }

			var indirectArgs = _batchRenderer.ReadIndirectArgsForDebugging();
			var drawRanges = _batchRenderer.GetDebugDrawRanges();

			long totalVisibleIndices = 0;

			if (ImGui.TreeNode("Material Draw Ranges"))
			{
				foreach (var kvp in drawRanges)
				{
					var materialId = kvp.Key;
					var range = kvp.Value;
					ImGui.Text($"Material {materialId}: {range.DrawCount} batches, starting at index {range.FirstDrawIndex}");
				}
				ImGui.TreePop();
			}

			if (ImGui.TreeNode("Batch Details"))
			{
				var batches = _batchRenderer.GetBatches();
				int count = Math.Min(indirectArgs.Length, batches.Count);

				if (count > 0)
				{
					if (ImGui.BeginTable("BatchTable", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY))
					{
						ImGui.TableSetupColumn("Batch");
						ImGui.TableSetupColumn("Mesh ID");
						ImGui.TableSetupColumn("Material ID");
						ImGui.TableSetupColumn("Visible Instances");
						ImGui.TableHeadersRow();

						var clipper = new ImGuiListClipper();
						clipper.Begin(count);

						while (clipper.Step())
						{
							for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
							{
								var batchInfo = batches[i];
								var args = indirectArgs[i];

								ImGui.TableNextRow();
								ImGui.TableSetColumnIndex(0);
								ImGui.Text($"{i}");
								ImGui.TableSetColumnIndex(1);
								ImGui.Text($"{batchInfo.Value.mesh.meshId}");
								ImGui.TableSetColumnIndex(2);
								ImGui.Text($"{batchInfo.Value.material.materialId}");
								ImGui.TableSetColumnIndex(3);
								ImGui.Text($"{args.NumInstances}");
							}
						}
						ImGui.EndTable();
					}
				}
				ImGui.TreePop();
			}

			if (indirectArgs.Length > 0)
			{
				for (int i = 0; i < indirectArgs.Length; i++)
				{
					var args = indirectArgs[i];
					totalVisibleIndices += (long)args.NumInstances * args.NumIndices;
				}
			}
			
			ImGui.Text($"Visible Triangles: {totalVisibleIndices / 3f / 1000000f:F2}m");
		}

		ImGui.End();

		_imGuiRender.AfterLayout();
		_graphicsPipeline.Present();

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

    private Vector3 ToEulerAngles(Quaternion q)
    {
        Vector3 angles = new();

        // pitch (x-axis rotation)
        double sinp = 2 * (q.W * q.Y - q.Z * q.X);
        if (Math.Abs(sinp) >= 1)
            angles.X = (float)Math.CopySign(Math.PI / 2, sinp); 
        else
            angles.X = (float)Math.Asin(sinp);

        // yaw (y-axis rotation)
        double sinr_cosp = 2 * (q.W * q.X + q.Y * q.Z);
        double cosr_cosp = 1 - 2 * (q.X * q.X + q.Y * q.Y);
        angles.Y = (float)Math.Atan2(sinr_cosp, cosr_cosp);

        // roll (z-axis rotation)
        double siny_cosp = 2 * (q.W * q.Z + q.X * q.Y);
        double cosy_cosp = 1 - 2 * (q.Y * q.Y + q.Z * q.Z);
        angles.Z = (float)Math.Atan2(siny_cosp, cosy_cosp);

        return angles;
    }

	private float framePerSecond;
	private ulong numFramesRendered;
	private ulong lastNumFramesRendered;
	private float timeFps;
	private float time;

	protected override void OnQuit()
	{
		_inputEventPull.OnSurfaceResize -= OnSurfaceResize;
		_windowHandle.OnWindowResize -= OnWindowHandleResize;
		_windowHandle.Release();
		_graphicsPipeline.Release();
	}
}