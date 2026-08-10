using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics.Diligent;
using DecaEngine.Sdl;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;

namespace DecaEngine.Editor;

/// <summary>
/// ВРЕМЕННЫЙ отладочный CLI-режим: `DecaEngine.Editor.exe --full-loop &lt;model.gltf&gt; [frames]
/// [backend]`. В отличие от <see cref="PreviewLoopProbe"/> (который гоняет ТОЛЬКО
/// <see cref="ModelPreviewViewport"/> изолированно), собирает ОБА рендер-графа редактора - главный
/// back-buffer <see cref="GraphicsPipeline"/> (как в <see cref="EditorManager"/>) И offscreen-превью
/// - в том же порядке (preview.Update -> root.Update -> pipeline.Execute -> Present), что и
/// EditorManager.OnUpdate. Нужен, потому что NRE в DiligentCommandBuffer.Execute при
/// SetBackBufferTarget (см. баг "переключение SSAO->GTAO роняет NRE") бьёт по ГЛАВНОМУ пайплайну и
/// не воспроизводится, если превью гонять в одиночку - PreviewLoopProbe его не ловит.
/// </summary>
public static class FullLoopProbe
{
	public static void Run(string[] args)
	{
		var modelPath = args.Length > 1 ? args[1] : "EditorAssets/models/Sponza.gltf";
		int frames = args.Length > 2 && int.TryParse(args[2], out var parsedFrames) ? parsedFrames : 300;
		var backend = (args.Length > 3 ? args[3] : "d3d12").ToLowerInvariant() switch
		{
			"vulkan" => GraphicsBackend.Vulkan,
			"d3d11" => GraphicsBackend.D3D11,
			_ => GraphicsBackend.D3D12,
		};

		Console.WriteLine($"[full] model: {modelPath}, frames: {frames}, backend: {backend}");

		var window = new SdlWindowHandle();
		window.Initialize("Full Loop Probe", 0, new Vector2(1280, 720));

		var api = new DiligentGraphicsApi(window);
		api.Initialize(backend);

		var batchRenderer = new DiligentBatchRenderer(api);
		var pipeline = new GraphicsPipeline(api, batchRenderer);

		var store = new EntityStore();
		var resourceManager = new RenderResourceManager(2, 2, store, batchRenderer);

		var cameraComponent = new CameraComponent(new CameraData(90f, 0.1f, 1000f, new Vector4(0, 0f, 1280f, 720f)));
		cameraComponent.data.cullFlags = CullFlags.All;
		store.CreateEntity(
			new Position(0, 0, -4.0f),
			new Rotation(),
			new Scale3(1, 1, 1),
			cameraComponent);

		var root = new SystemRoot { new CullingAndRenderSystem(resourceManager, api, pipeline) };
		root.AddStore(store);

		var settings = new EditorSettings
		{
			PreviewSsao = true,
			PreviewAoMode = Environment.GetEnvironmentVariable("DECA_LOOP_AO") == "GTAO"
				? AmbientOcclusionMode.Gtao
				: AmbientOcclusionMode.Ssao,
		};

		var viewport = new ModelPreviewViewport(api, settings);
		viewport.LoadModel(modelPath);

		float time = 0f;
		const float dt = 1f / 60f;
		for (int i = 0; i < frames; i++)
		{
			// LoadModel грузит в фоновом Task.Run - реальные кадры редактора идут с реальным
			// интервалом, иначе PollPendingLoad ни разу не увидит PrepareTask завершённым.
			Thread.Sleep(16);

			// Тот же порядок, что EditorManager.OnUpdate: preview ПЕРЕД главным пайплайном (см.
			// комментарий там про rebind swap-chain backbuffer), затем ECS root, затем
			// pipeline.Execute() (обязан быть ПОСЛЕДНИМ Execute() кадра), затем Present().
			viewport.Update(dt, time);
			root.Update(new UpdateTick(dt, time));
			pipeline.Execute();
			api.Present();

			time += dt;

			// DECA_LOOP_RESIZE=1 - дёргать РЕАЛЬНЫЙ путь ресайза окна (WindowHandle.Size сеттер ->
			// OnWindowResize -> DiligentGraphicsApi.OnWindowHandleResize -> SwapChain.Resize) каждые
			// 50 кадров, независимо от AO - проверка гипотезы: NRE на SetBackBufferTarget
			// (GetCurrentBackBufferRTV() == null) может быть гонкой ресайза свопчейна с Execute(),
			// а не самим переключением AO технику - AO лишь совпало по времени у пользователя.
			if (Environment.GetEnvironmentVariable("DECA_LOOP_RESIZE") == "1" && i > 0 && i % 50 == 0)
			{
				var newSize = (i / 50) % 2 == 0 ? new Vector2(1000, 600) : new Vector2(1280, 720);
				Console.WriteLine($"[full] frame {i}: resizing window -> {newSize}");
				window.Size = newSize;
			}

			// DECA_LOOP_TOGGLE=1 - переключать раз в TOGGLE_INTERVAL кадров, но только когда модель
			// уже резидентна (HasModel) - иначе тоггл отменяет ещё не завершившуюся фоновую загрузку
			// (Sponza в Debug-сборке парсится секунды 3-4) и она никогда не долетает до финала, что
			// выглядит как зависание, но им не является (просто цикл тестов быстрее реальной загрузки).
			// Чередует AO technique (Ssao/Gtao) И вкл/выкл SSAO целиком (RecreateEnvironment
			// создаёт/уничтожает _ssaoResources) - разные code path, оба стоит простучать.
			const int ToggleInterval = 400;
			if (Environment.GetEnvironmentVariable("DECA_LOOP_TOGGLE") == "1" && i > 0 && i % ToggleInterval == 0 && viewport.HasModel)
			{
				if ((i / ToggleInterval) % 3 == 0)
				{
					settings.PreviewSsao = !settings.PreviewSsao;
					Console.WriteLine($"[full] frame {i}: toggling SSAO -> {settings.PreviewSsao}");
				}
				else
				{
					settings.PreviewAoMode = settings.PreviewAoMode == AmbientOcclusionMode.Gtao
						? AmbientOcclusionMode.Ssao
						: AmbientOcclusionMode.Gtao;
					Console.WriteLine($"[full] frame {i}: toggling AO mode -> {settings.PreviewAoMode}");
				}
				SettingsWindow.RaisePreviewGraphicsAppliedForTest();
				Console.WriteLine($"[full] frame {i}: toggled ok, HasModel={viewport.HasModel}, LoadError={viewport.LoadError}");
			}
		}

		Console.WriteLine($"[full] done: {frames} frames, HasModel={viewport.HasModel}, LoadError={viewport.LoadError}");
	}
}
