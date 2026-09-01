using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Core.Diagnostics;
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

		// DECA_LOOP_LIGHTS=N - засеять главную сцену N точечными светами по кольцу и одним спотом:
		// структурная проверка всего кластерного пути (сбор и по-типовый кулинг в
		// CullingAndRenderSystem, компьют-кластеризация LightClusterCS, чтение кластеров в PS) под
		// живой валидацией бэкенда. Картинку не проверяет - геометрии в главной сцене нет.
		if (int.TryParse(Environment.GetEnvironmentVariable("DECA_LOOP_LIGHTS"), out var lightCount) && lightCount > 0)
		{
			for (int li = 0; li < lightCount; li++)
			{
				float angle = li * MathF.Tau / lightCount;
				store.CreateEntity(
					new Position(MathF.Cos(angle) * 3f, 1f, MathF.Sin(angle) * 3f),
					new LightComponent
					{
						Type = LightType.Point,
						Color = new Vector3(1f, 0.8f, 0.6f),
						Intensity = 5f,
						Range = 4f,
						// Часть светов - с тенями: прогоняет раздачу слайсов (бюджет меньше, чем
						// просят) и запись/сэмплинг shadow map punctual-светов.
						ShadowStrength = li % 3 == 0 ? 1f : 0f,
					});
			}

			var spotDown = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f);
			store.CreateEntity(
				new Position(0, 3f, 0),
				new Rotation(spotDown.X, spotDown.Y, spotDown.Z, spotDown.W),
				new LightComponent
				{
					Type = LightType.Spot,
					Color = new Vector3(0.5f, 0.7f, 1f),
					Intensity = 8f,
					Range = 10f,
					SpotAngle = 45f,
					ShadowStrength = 1f,
				});

			Console.WriteLine($"[full] seeded {lightCount} point lights + 1 spot");
		}

		var root = new SystemRoot { new CullingAndRenderSystem(resourceManager, api, pipeline) };
		root.AddStore(store);

		var settings = new EditorSettings
		{
			PreviewSsao = true,
			PreviewAoMode = Environment.GetEnvironmentVariable("DECA_LOOP_AO") == "GTAO"
				? AmbientOcclusionMode.Gtao
				: AmbientOcclusionMode.Ssao,

			// DECA_PROBE_TEXSIZE=<n> - тот же потолок текстур, что и у --preview-probe. Ручка обязана
			// быть общей у обоих пробников: потолок входит в подпись cooked-модели (см.
			// ModelLoadOptions.CookSignature), и разойдись они - один пробник пёк бы кеш, которым
			// второй никогда не воспользуется, молча уходя на путь без кеша.
			PreviewMaxTextureSize = int.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_TEXSIZE"), out var texSize)
				? Math.Clamp(texSize, 128, 8192)
				: 2048,
		};

		var modelStore = new ModelStore(api);

		// DECA_LOOP_TEXBUDGET_MB=N - зажать бюджет текстурной памяти до N МБ и отпустить его на
		// середине прогона. Проверка того, что упор в бюджет ставит догрузку качества на ПАУЗУ, а не
		// хоронит её: разжатая лестница остаётся в очереди, и после освобождения памяти качество
		// обязано доехать до потолка. Раньше отказ бюджета выбрасывал очередь вместе с исходником -
		// текстура навсегда оставалась на том качестве, до которого успела дойти.
		var squeezeBudgetMb = int.TryParse(Environment.GetEnvironmentVariable("DECA_LOOP_TEXBUDGET_MB"), out var mb) && mb > 0
			? mb
			: 0;
		var budgetReleaseFrame = frames / 2;

		if (squeezeBudgetMb > 0)
		{
			modelStore.TextureMemoryBudgetBytes = (long)squeezeBudgetMb << 20;
			Console.WriteLine($"[full] texture memory budget squeezed to {squeezeBudgetMb} MB until frame {budgetReleaseFrame}");
		}

		// Диагностика бесшовного появления модели: показ ждёт готовности текстур (см.
		// ModelStore.ModelTexturesReady), поэтому кадр, на котором модель попала в сцену (HasModel),
		// обязан быть НЕ РАНЬШЕ кадра готовности текстур, а сами текстуры на нём - в целевом размере.
		// Если бы показ шёл по одной лишь финализации, разрыв между этими кадрами и был бы тем самым
		// "миганием" текстур.
		var frame = 0;
		var finalizedFrame = -1;
		var texturesFrame = -1;
		var visibleFrame = -1;
		var streamedFrame = -1;

		DecaEngine.Graphics.ModelLoader? streamedModel = null;

		modelStore.ModelReady += model =>
		{
			streamedModel = model;
			finalizedFrame = frame;
			Console.WriteLine($"[full] frame {frame}: model finalized, {model.StreamedTextures.Count} streamed texture(s)");
		};

		modelStore.ModelTexturesReady += model =>
		{
			texturesFrame = frame;

			var min = int.MaxValue;
			var max = 0;
			foreach (var stream in model.StreamedTextures)
			{
				min = Math.Min(min, stream.CurrentSize);
				max = Math.Max(max, stream.CurrentSize);
			}

			Console.WriteLine($"[full] frame {frame}: textures ready, size min={(min == int.MaxValue ? 0 : min)} max={max}");
		};

		var viewport = new ModelPreviewViewport(api, settings, modelStore);
		viewport.LoadModel(modelPath);

		// Стоимость ModelStore.Tick на ГЛАВНОМ потоке по кадрам: декод ступеней живёт в пуле, но
		// заливка на GPU (CreateTexture с мип-цепочкой) - здесь, и именно она способна дать рывок
		// кадра во время догрузки качества.
		var tickMs = new List<double>(frames);
		var clock = System.Diagnostics.Stopwatch.StartNew();

		float time = 0f;
		const float dt = 1f / 60f;
		for (int i = 0; i < frames; i++)
		{
			frame = i;

			if (squeezeBudgetMb > 0 && i == budgetReleaseFrame)
			{
				modelStore.TextureMemoryBudgetBytes = 1024L << 20;
				Console.WriteLine($"[full] frame {i}: texture memory budget released");
			}

			// LoadModel грузит в фоновом Task.Run - реальные кадры редактора идут с реальным
			// интервалом, иначе PollPendingLoad ни разу не увидит PrepareTask завершённым.
			Thread.Sleep(16);

			// Тот же порядок, что EditorManager.OnUpdate: столу ОДИН тик на весь процесс (загрузка/
			// финализация/стриминг текстур теперь там - см. ModelStore class-doc), затем preview ПЕРЕД
			// главным пайплайном (см. комментарий там про rebind swap-chain backbuffer), затем ECS
			// root, затем pipeline.Execute() (обязан быть ПОСЛЕДНИМ Execute() кадра), затем Present().
			var tickStart = clock.Elapsed.TotalMilliseconds;
			modelStore.Tick(dt);
			tickMs.Add(clock.Elapsed.TotalMilliseconds - tickStart);

			viewport.Update(dt, time);
			root.Update(new UpdateTick(dt, time));
			pipeline.Execute();
			api.Present();

			time += dt;

			// Кадр, на котором стриминг закончился ПОЛНОСТЬЮ (у всех текстур исчерпан исходник) - метрика
			// пропускной способности декода/заливки, которую момент показа модели не отражает вовсе:
			// показ открывает первая ступень, а качество доезжает намного позже.
			if (streamedFrame < 0 && streamedModel != null && streamedModel.StreamedTextures.Count > 0)
			{
				var done = true;
				foreach (var stream in streamedModel.StreamedTextures)
				{
					if (stream.HasSource)
					{
						done = false;
						break;
					}
				}

				if (done)
				{
					streamedFrame = i;
					Console.WriteLine($"[full] frame {i}: texture streaming complete");
				}
			}

			if (visibleFrame < 0 && viewport.HasModel)
			{
				visibleFrame = i;
				Console.WriteLine($"[full] frame {i}: model became visible " +
					$"(finalized at {finalizedFrame}, textures ready at {texturesFrame})");
			}

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

		Console.WriteLine($"[full] done: {frames} frames, HasModel={viewport.HasModel}, LoadError={viewport.LoadError}, " +
			$"finalized={finalizedFrame}, texturesReady={texturesFrame}, visible={visibleFrame}, " +
			$"streamingComplete={streamedFrame}");

		// Качество в КОНЦЕ прогона: показ открывается на первой (мелкой) ступени, поэтому отдельно
		// нужно видеть, что лестница потом действительно доехала до потолка, а не застряла на ней.
		if (streamedModel != null && streamedModel.StreamedTextures.Count > 0)
		{
			var finalMin = int.MaxValue;
			var finalMax = 0;
			var pending = 0;
			foreach (var stream in streamedModel.StreamedTextures)
			{
				finalMin = Math.Min(finalMin, stream.CurrentSize);
				finalMax = Math.Max(finalMax, stream.CurrentSize);
				if (stream.HasSource)
				{
					pending++;
				}
			}

			Console.WriteLine($"[full] final texture quality: min={finalMin} max={finalMax}, " +
				$"still streaming {pending}/{streamedModel.StreamedTextures.Count}");
		}

		Console.WriteLine($"[full] store state: {modelStore.DescribeStreamingState()}");

		// Предупреждения/ошибки стола (сбойный декод, упёршийся бюджет) уходят в ImGui-консоль
		// редактора и в stdout НЕ попадают - без этой выжимки прогон выглядит успешным даже когда все
		// текстуры до одной провалили декод.
		var warnings = new Dictionary<string, int>();
		foreach (var entry in EngineLog.Snapshot())
		{
			if (entry.Level == LogLevel.Warning || entry.Level == LogLevel.Error)
			{
				// Схлопываем по первым словам: сообщения различаются путём/причиной, а интересно
				// именно КАКИХ и СКОЛЬКО, а не 76 почти одинаковых строк.
				var key = entry.Message.Length > 90 ? entry.Message[..90] : entry.Message;
				warnings[key] = warnings.TryGetValue(key, out var count) ? count + 1 : 1;
			}
		}

		foreach (var (message, count) in warnings)
		{
			Console.WriteLine($"[full] log x{count}: {message}");
		}

		// Рывки стриминга: интересна не средняя стоимость тика, а хвост - один 10-миллисекундный
		// кадр посреди догрузки качества виден как дёрганье.
		if (tickMs.Count > 0)
		{
			var sorted = tickMs.ToArray();
			Array.Sort(sorted);

			// Отдельно - хвост ПОСЛЕ появления модели: до этого момента рывки в кадре не видны
			// (модели в нём ещё нет), и они мешали бы читать метрику.
			var afterVisible = new List<double>();
			for (int i = Math.Max(0, visibleFrame); i < tickMs.Count; i++)
			{
				afterVisible.Add(tickMs[i]);
			}

			var visibleSorted = afterVisible.ToArray();
			Array.Sort(visibleSorted);

			Console.WriteLine($"[full] store tick ms: p50={sorted[sorted.Length / 2]:0.00} " +
				$"p99={sorted[(int)(sorted.Length * 0.99)]:0.00} max={sorted[^1]:0.00}");

			if (visibleSorted.Length > 0)
			{
				Console.WriteLine($"[full] store tick ms after visible: p50={visibleSorted[visibleSorted.Length / 2]:0.00} " +
					$"p99={visibleSorted[(int)(visibleSorted.Length * 0.99)]:0.00} max={visibleSorted[^1]:0.00}");
			}
		}
	}
}
