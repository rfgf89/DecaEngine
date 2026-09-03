using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics.Diligent;
using DecaEngine.Sdl;
using DecaEngine.Editor;
using DecaEngine.Graphics;

namespace DecaEngine.Probes;

/// <summary>CLI probe: `DecaEngine.Editor.exe --preview-loop &lt;model.gltf&gt; [frames] [backend]`.
/// Drives the real <see cref="ModelPreviewViewport"/> frame loop, unlike the single-frame PreviewProbe.</summary>
public static class PreviewLoopProbe
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

		Console.WriteLine($"[loop] model: {modelPath}, frames: {frames}, backend: {backend}");

		var window = new SdlWindowHandle();
		window.Initialize("Preview Loop Probe", 0, new Vector2(320, 240));

		var api = new DiligentGraphicsApi(window);
		api.Initialize(backend);

		var settings = new EditorSettings
		{
			PreviewSsao = true,
			PreviewAoMode = Environment.GetEnvironmentVariable("DECA_LOOP_AO") == "GTAO"
				? AmbientOcclusionMode.Gtao
				: AmbientOcclusionMode.Ssao,
		};

		var modelStore = new ModelStore(api);
		var viewport = new ModelPreviewViewport(api, settings, modelStore);
		viewport.LoadModel(modelPath);

		float time = 0f;
		const float dt = 1f / 60f;
		for (int i = 0; i < frames; i++)
		{
			// Real delay required: LoadModel runs in a background Task, and ModelStore.Tick must
			// pump it or the model never finishes loading.
			Thread.Sleep(16);
			modelStore.Tick(dt);
			viewport.Update(dt, time);
			time += dt;

			if (i == frames / 2)
			{
				Console.WriteLine($"[loop] frame {i}: HasModel={viewport.HasModel}, LoadError={viewport.LoadError}");

				// Mid-run live AO toggle exercises the same path as OK in the settings window.
				if (Environment.GetEnvironmentVariable("DECA_LOOP_TOGGLE") == "1")
				{
					settings.PreviewAoMode = settings.PreviewAoMode == AmbientOcclusionMode.Gtao
						? AmbientOcclusionMode.Ssao
						: AmbientOcclusionMode.Gtao;
					SettingsWindow.RaisePreviewGraphicsAppliedForTest();
				}
			}
		}

		Console.WriteLine($"[loop] done: {frames} frames, HasModel={viewport.HasModel}, LoadError={viewport.LoadError}");
	}
}
