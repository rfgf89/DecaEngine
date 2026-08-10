using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Graphics.Diligent;
using DecaEngine.Sdl;

namespace DecaEngine.Editor;

/// <summary>
/// ВРЕМЕННЫЙ отладочный CLI-режим: `DecaEngine.Editor.exe --preview-loop &lt;model.gltf&gt;
/// [frames] [backend]`. В отличие от <see cref="PreviewProbe"/> (который вручную собирает
/// оффскрин-сцену и рендерит один кадр), гоняет РЕАЛЬНЫЙ <see cref="ModelPreviewViewport"/> -
/// тот же путь, что и настоящий редактор (LoadModel -> PollPendingLoad -> Update() каждый кадр
/// в цикле) - чтобы поймать баг, который не воспроизводится в PreviewProbe (см. NRE в
/// DiligentCommandBuffer.Execute при работе полного редактора).
/// </summary>
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

		var viewport = new ModelPreviewViewport(api, settings);
		viewport.LoadModel(modelPath);

		float time = 0f;
		const float dt = 1f / 60f;
		for (int i = 0; i < frames; i++)
		{
			// LoadModel грузит в фоновом Task.Run - реальные кадры редактора идут с реальным
			// интервалом (в отличие от тайтового цикла), так что тут нужна настоящая задержка,
			// иначе PollPendingLoad ни разу не увидит PrepareTask завершённым.
			Thread.Sleep(16);
			viewport.Update(dt, time);
			time += dt;

			if (i == frames / 2)
			{
				Console.WriteLine($"[loop] frame {i}: HasModel={viewport.HasModel}, LoadError={viewport.LoadError}");

				// На половине прогона переключаем технику AO живьём - тот же путь, что и OK в
				// окне настроек (RecreateEnvironment), см. ModelPreviewViewport.OnGraphicsSettingsChanged.
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
