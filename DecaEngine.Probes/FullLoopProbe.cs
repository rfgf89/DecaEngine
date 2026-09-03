using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Core.Diagnostics;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics.Diligent;
using DecaEngine.Sdl;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using DecaEngine.Scene;
using DecaEngine.Editor;
using DecaEngine.Graphics;

namespace DecaEngine.Probes;

/// <summary>Debug CLI: `--full-loop &lt;model.gltf&gt; [frames] [backend]`.</summary>
// Runs both editor render graphs in EditorManager's order; backbuffer bugs need the main
// pipeline present, so PreviewLoopProbe (preview only) cannot reproduce them.
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

		// DECA_LOOP_LIGHTS=N: seed N point lights plus a spot to exercise the clustered path.
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
						// Only some lights cast: exercises slice hand-out under an undersized budget.
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

			// DECA_PROBE_TEXSIZE must match --preview-probe: it feeds ModelLoadOptions.CookSignature,
			// so a divergent cap silently invalidates the other probe's cooked cache.
			PreviewMaxTextureSize = int.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_TEXSIZE"), out var texSize)
				? Math.Clamp(texSize, 128, 8192)
				: 2048,
		};

		var modelStore = new ModelStore(api);

		// DECA_LOOP_TEXBUDGET_MB=N: squeeze the texture budget, release it mid-run, and check that
		// hitting the budget pauses quality upload rather than dropping the queue.
		var squeezeBudgetMb = int.TryParse(Environment.GetEnvironmentVariable("DECA_LOOP_TEXBUDGET_MB"), out var mb) && mb > 0
			? mb
			: 0;
		var budgetReleaseFrame = frames / 2;

		if (squeezeBudgetMb > 0)
		{
			modelStore.TextureMemoryBudgetBytes = (long)squeezeBudgetMb << 20;
			Console.WriteLine($"[full] texture memory budget squeezed to {squeezeBudgetMb} MB until frame {budgetReleaseFrame}");
		}

		// Pop-in diagnostics: the visible frame must not precede the textures-ready frame.
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

		// Main-thread cost of ModelStore.Tick: mip-chain uploads run here and can spike a frame.
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

			// Real frame interval: LoadModel runs on a background task PollPendingLoad must observe.
			Thread.Sleep(16);

			// Order matches EditorManager.OnUpdate: one store tick, preview before the main
			// pipeline, then ECS root, then pipeline.Execute (last Execute of the frame), Present.
			var tickStart = clock.Elapsed.TotalMilliseconds;
			modelStore.Tick(dt);
			tickMs.Add(clock.Elapsed.TotalMilliseconds - tickStart);

			viewport.Update(dt, time);
			root.Update(new UpdateTick(dt, time));
			pipeline.Execute();
			api.Present();

			time += dt;

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

			// DECA_LOOP_RESIZE=1: drive the real window resize path every 50 frames, hunting a
			// swap-chain resize racing Execute().
			if (Environment.GetEnvironmentVariable("DECA_LOOP_RESIZE") == "1" && i > 0 && i % 50 == 0)
			{
				var newSize = (i / 50) % 2 == 0 ? new Vector2(1000, 600) : new Vector2(1280, 720);
				Console.WriteLine($"[full] frame {i}: resizing window -> {newSize}");
				window.Size = newSize;
			}

			// DECA_LOOP_TOGGLE=1: only toggles once the model is resident, otherwise it cancels the
			// still-running background load. Alternates AO technique and the SSAO on/off path.
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

		// Store warnings go to the editor's ImGui console, never stdout: without this digest a run
		// where every texture failed to decode still looks successful.
		var warnings = new Dictionary<string, int>();
		foreach (var entry in EngineLog.Snapshot())
		{
			if (entry.Level == LogLevel.Warning || entry.Level == LogLevel.Error)
			{
				// Collapsed by prefix: messages differ only by path, and the count is what matters.
				var key = entry.Message.Length > 90 ? entry.Message[..90] : entry.Message;
				warnings[key] = warnings.TryGetValue(key, out var count) ? count + 1 : 1;
			}
		}

		foreach (var (message, count) in warnings)
		{
			Console.WriteLine($"[full] log x{count}: {message}");
		}

		// The tail matters, not the mean: a single 10 ms tick mid-stream reads as a hitch.
		if (tickMs.Count > 0)
		{
			var sorted = tickMs.ToArray();
			Array.Sort(sorted);

			// Hitches before the model is visible are invisible on screen, so split the tail.
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
