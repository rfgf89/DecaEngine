using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Core.Assets;
using DecaEngine.Editor.ECS;
using Friflo.Engine.ECS;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Core;
using DecaEngine.Graphics.Diligent;
using DecaEngine.Sdl;
using Friflo.Engine.ECS.Systems;

namespace DecaEngine.Editor;

/// <summary>
/// Отладочный CLI-рендер превью модели без UI: `DecaEngine.Editor.exe --preview-probe
/// &lt;model.gltf&gt; &lt;outDir&gt;`. Поднимает ту же оффскрин-среду, что и
/// <see cref="ModelPreviewViewport"/>/<see cref="ModelIconBaker"/>, рендерит модель в режимах
/// Lighting/Highlight/Channel(Normal) и пишет PNG + числовую статистику яркости в консоль -
/// чтобы багрепорты вида "света нет" можно было воспроизводить и диагностировать без ручных
/// кликов по редактору.
/// </summary>
public static class PreviewProbe
{
	public static void Run(string[] args)
	{
		var modelPath = args.Length > 1 ? args[1] : "EditorAssets/models/Sponza.gltf";
		var outDir = args.Length > 2 ? args[2] : "preview-probe";
		int subMesh = args.Length > 3 && int.TryParse(args[3], out var parsed) ? parsed : -1;
		float zoom = args.Length > 4 && float.TryParse(args[4],
			System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedZoom)
			? parsedZoom : 1f;
		float yaw = args.Length > 5 && float.TryParse(args[5],
			System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedYaw)
			? parsedYaw : -0.6f;
		Directory.CreateDirectory(outDir);

		Console.WriteLine($"[probe] model: {modelPath}");
		Console.WriteLine($"[probe] out:   {Path.GetFullPath(outDir)}");

		var window = new SdlWindowHandle();
		window.Initialize("Preview Probe", 0, new Vector2(320, 240));

		var api = new DiligentGraphicsApi(window);
		// Бэкенд по умолчанию Vulkan, но редактор работает на D3D12 (см. EditorManager) - для
		// проверки именно редакторского пути: DECA_PROBE_BACKEND=d3d12.
		var backend = Environment.GetEnvironmentVariable("DECA_PROBE_BACKEND")?.ToLowerInvariant() switch
		{
			"d3d12" => GraphicsBackend.D3D12,
			"d3d11" => GraphicsBackend.D3D11,
			_ => GraphicsBackend.Vulkan,
		};
		Console.WriteLine($"[probe] backend: {backend}");
		api.Initialize(backend);

		// Тумблеры фич Lighting-режима (см. PreviewFeatureFlags): DECA_PROBE_FEATURES=0 - всё
		// выключено, =1 - только нормал-мапы и т.д. По умолчанию All - как в редакторе.
		ProbeFeatureFlags = int.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_FEATURES"), out var flags)
			? flags
			: (int)PreviewFeatureFlags.All;
		Console.WriteLine($"[probe] features: {(PreviewFeatureFlags)ProbeFeatureFlags}");

		// DECA_PROBE_HDR=<путь к .hdr> - IBL из файла вместо процедурного неба.
		// DECA_PROBE_MSAA=1 - выключить MSAA (по умолчанию 4x, как в редакторе).
		uint msaa = uint.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_MSAA"), out var parsedMsaa)
			? Math.Max(1u, parsedMsaa)
			: 4u;
		Console.WriteLine($"[probe] msaa: {msaa}x");

		// DECA_PROBE_SSAO=0 - выключить SSAO-пасс (по умолчанию включён, как в редакторе).
		bool ssao = Environment.GetEnvironmentVariable("DECA_PROBE_SSAO") != "0";
		// DECA_PROBE_AO=GTAO - техника GTAO вместо классического SSAO (см. AmbientOcclusionMode).
		var aoMode = string.Equals(Environment.GetEnvironmentVariable("DECA_PROBE_AO"), "GTAO",
			StringComparison.OrdinalIgnoreCase) ? AmbientOcclusionMode.Gtao : AmbientOcclusionMode.Ssao;
		// DECA_PROBE_SHADOWS=0 - выключить тени мирового света.
		bool shadows = Environment.GetEnvironmentVariable("DECA_PROBE_SHADOWS") != "0";
		// DECA_PROBE_SSGI=0 - выключить SSGI-пасс (по умолчанию включён, как в редакторе).
		bool ssgi = Environment.GetEnvironmentVariable("DECA_PROBE_SSGI") != "0";
		Console.WriteLine($"[probe] ssao: {ssao} ({aoMode}), ssgi: {ssgi}, shadows: {shadows}");

		var env = new ModelViewportEnvironment(api, 512, 512, "Probe Color", "Probe Depth", skyBackground: true,
			environmentHdrPath: Environment.GetEnvironmentVariable("DECA_PROBE_HDR"),
			msaaSamples: msaa, ssao: ssao, shadows: shadows, aoMode: aoMode, ssgi: ssgi);

		// A/B анизотропии: DECA_PROBE_ANISO=0 выключает (тумблер уровня загрузки, см. ModelLoadOptions).
		bool anisotropic = Environment.GetEnvironmentVariable("DECA_PROBE_ANISO") != "0";

		var model = ModelLoader.Load(api, modelPath, new ModelLoadOptions
		{
			AnisotropicFiltering = anisotropic,
			VertexShader = new EditorRef("shader/UnlitInstancedVS.hlsl"),
			PixelShader = new EditorRef("shader/UnlitInstancedPS.hlsl"),
			OptimizeMesh = false,
			GenerateLods = false
		});

		Console.WriteLine($"[probe] meshes={model.Meshes.Count} materials={model.materialObjects.Count} instances={model.instances.Count}");

		var meshIdMap = new Dictionary<int, MeshId>();
		var materialIdMap = new Dictionary<int, MaterialId>();
		var batchCache = new Dictionary<(int, int), BatchId>();
		ModelViewportGeometry.RegisterModelResources(env.BatchRenderer, model, meshIdMap, materialIdMap,
			api, env.SceneCopyTarget, env.EnvironmentMap);

		int created = 0;
		foreach (var instance in model.instances)
		{
			if (subMesh >= 0 && instance.meshId != subMesh)
			{
				continue;
			}

			var entity = ModelViewportGeometry.CreateInstanceEntity(env.Store, env.ResourceManager,
				env.BatchRenderer, meshIdMap, materialIdMap, batchCache,
				instance.meshId, instance.materialId, instance.transform);
			if (entity != null)
			{
				created++;
			}
		}
		Console.WriteLine($"[probe] entities created: {created} (subMesh={subMesh})");

		var (min, max) = subMesh >= 0
			? ModelViewportGeometry.ComputeSubMeshBounds(model, subMesh)
			: model.ComputeBounds();
		var target = (min + max) * 0.5f;
		var radius = MathF.Max(0.05f, (max - min).Length() * 0.5f);
		var distance = ModelViewportGeometry.ComputeFramingDistance(radius, ModelViewportEnvironment.CameraFovDegrees) * zoom;
		var eye = ModelViewportGeometry.ComputeOrbitEye(target, distance, yaw, 0.35f);
		env.SetCameraTransform(eye, target);

		if (env.ShadowSettings != null)
		{
			env.ShadowSettings.BoundsCenter = target;
			env.ShadowSettings.BoundsRadius = radius;
		}

		// Тот же мировой радиус AO, что пушит редакторский вьюпорт (см. ModelPreviewViewport.FrameAll).
		// DECA_PROBE_AO_RANGE=<доля радиуса баундов> - переопределить, 0 - легаси-режим (радиус в
		// долях экрана) для A/B.
		float aoRangeFraction = float.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_AO_RANGE"),
			System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedAoRange)
			? parsedAoRange
			: ModelViewportEnvironment.AoRangeOfBoundsRadius;
		env.SetAoWorldRange(radius * aoRangeFraction);
		env.SetGiWorldRange(radius * ModelViewportEnvironment.GiRangeOfBoundsRadius);
		Console.WriteLine($"[probe] eye={eye} target={target} distance={distance}");

		float time = 0f;

		// NB: прежние стадии lighting_glass/debug_transmission (принудительный transmission в
		// рантайме) удалены: с shader keywords код стекла существует только в вариантах материалов,
		// у которых KHR_materials_transmission авторский - рантайм-форс на непрозрачном материале
		// теперь по определению no-op. Стекло проверяется на ассетах с настоящим transmission
		// (DragonDispersion/ABeautifulGame).
		foreach (var (mode, channel, name) in new[] { (3, 0, "lighting"), (3, 0, "lighting_flat"), (3, 8, "debug_ambient"), (3, 7, "debug_direct"), (3, 6, "debug_envspec"), (1, 0, "highlight"), (2, 0, "channel_normal") })
		{
			// "lighting_flat": тот же Lighting-режим, но с принудительно белым нетекстурированным
			// диэлектриком - чистый отклик источников света без влияния альбедо/MR-текстур, чтобы
			// отличать "свет не работает" от "тёмные текстуры съедают контраст".
			PushPreviewSettings(model, mode, channel, forceFlatWhite: name != "lighting");

			env.DilApi.ImmediateContext.Flush();
			env.DilApi.ImmediateContext.WaitForIdle();
			env.Pipeline.InvalidateGraph();

			for (int frame = 0; frame < 3; frame++)
			{
				time += 1f / 60f;
				env.Root.Update(new UpdateTick(1f / 60f, time));
				env.Pipeline.Execute();
			}

			env.DilApi.ImmediateContext.Flush();
			env.DilApi.ImmediateContext.WaitForIdle();

			var pixels = DiligentTextureReadback.ReadRgba8(env.DilApi, (DiligentRenderTarget)env.ColorTarget,
				out var width, out var height);

			ReportStats(name, pixels, width, height);

			// Композит поверх того же светлого градиента, что подкладывает ModelPreviewViewport.Render -
			// сам таргет очищается с alpha 0, а смотреть надо именно то, что видит пользователь.
			CompositeOverBackdrop(pixels, width, height);

			var pngPath = Path.Combine(outDir, $"probe_{name}.png");
			PngWriter.Write(pngPath, pixels, width, height);
		}

		// DECA_PROBE_FRAMES=<N> - длительный прогон: N кадров подряд без ресайза, лог каждые 100.
		// Репро накопительного зависания "AO включён при старте -> модель загружена -> фриз через
		// ~30 секунд" (~1800 кадров при 60fps): если виснет исчерпание пула (дескрипторы/upload heap),
		// последний залогированный номер кадра покажет скорость утечки, а связка с DECA_PROBE_SSAO=0
		// подтвердит AO-специфичность.
		int longRunFrames = int.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_FRAMES"), out var parsedFrames)
			? parsedFrames
			: 0;
		if (longRunFrames > 0)
		{
			Console.WriteLine($"[probe] long run: {longRunFrames} frames...");
			for (int frame = 0; frame < longRunFrames; frame++)
			{
				time += 1f / 60f;
				env.Root.Update(new UpdateTick(1f / 60f, time));
				env.Pipeline.Execute();

				// Как в EditorManager.OnUpdate: Present двигает внутренний кадровый цикл Diligent
				// (рециклинг динамических пулов/дескрипторов) - без него длинный прогон исчерпал бы
				// пулы даже там, где редакторский путь здоров.
				api.Present();

				if (frame % 100 == 0)
				{
					// Idle-чек раз в сотню кадров: если GPU уже подвис, WaitForIdle не вернётся, и
					// последняя строка лога назовёт диапазон кадров, где это произошло.
					env.DilApi.ImmediateContext.Flush();
					env.DilApi.ImmediateContext.WaitForIdle();
					Console.WriteLine($"[probe] long run frame {frame} ok ({GC.GetTotalMemory(false) / (1024 * 1024)} MB managed)");
				}
			}

			env.DilApi.ImmediateContext.Flush();
			env.DilApi.ImmediateContext.WaitForIdle();
			Console.WriteLine("[probe] long run complete");
		}

		// DECA_PROBE_RESIZE=1 - воспроизведение ресайз-пути редактора (точное зеркало
		// ModelPreviewViewport.ResizeTargets, кроме ImGui-биндинга - его в headless нет): в редакторе
		// связка AO + ресайз окна превью намертво вешает GPU. Здесь тот же сценарий воспроизводится
		// без UI, так что его можно гонять под D3D12 debug layer / Vulkan validation и в связке с
		// DECA_PROBE_SSAO/DECA_PROBE_AO/DECA_PROBE_MSAA для локализации.
		if (Environment.GetEnvironmentVariable("DECA_PROBE_RESIZE") == "1")
		{
			var newSize = new Vector2(768, 640);
			Console.WriteLine($"[probe] resize -> {newSize.X}x{newSize.Y}");

			env.DilApi.ImmediateContext.Flush();
			env.DilApi.ImmediateContext.WaitForIdle();

			env.ColorTarget.Resize(newSize);
			env.DepthTarget.Resize(newSize);
			env.SceneCopyTarget.Resize(newSize);
			env.MsaaColorTarget?.Resize(newSize);
			env.MsaaDepthTarget?.Resize(newSize);
			env.AoTarget?.Resize(newSize);
			env.GiTarget?.Resize(newSize);
			env.RebindPostProcessTargets();
			for (int i = 0; i < model.materialObjects.Count; i++)
			{
				model.materialObjects.GetAt(i).Value.SetTexture("_SceneColor", env.SceneCopyTarget);
			}

			env.Pipeline.SetOffscreenViewportSize(newSize);

			ref var cameraComponent = ref env.CameraEntity.GetComponent<CameraComponent>();
			cameraComponent.data.viewport = new Vector4(0, 0, newSize.X, newSize.Y);
			cameraComponent.data.aspect = newSize.X / newSize.Y;
			cameraComponent.RecalculateProjection();

			for (int frame = 0; frame < 3; frame++)
			{
				time += 1f / 60f;
				Console.WriteLine($"[probe] resize frame {frame}...");
				env.Root.Update(new UpdateTick(1f / 60f, time));
				env.Pipeline.Execute();
				env.DilApi.ImmediateContext.Flush();
				env.DilApi.ImmediateContext.WaitForIdle();
				Console.WriteLine($"[probe] resize frame {frame} ok");
			}

			var resizedPixels = DiligentTextureReadback.ReadRgba8(env.DilApi, (DiligentRenderTarget)env.ColorTarget,
				out var resizedWidth, out var resizedHeight);
			ReportStats("resized", resizedPixels, resizedWidth, resizedHeight);
			CompositeOverBackdrop(resizedPixels, resizedWidth, resizedHeight);
			PngWriter.Write(Path.Combine(outDir, "probe_resized.png"), resizedPixels, resizedWidth, resizedHeight);
		}

		Console.WriteLine("[probe] done");
		Environment.Exit(0);
	}

	/// <summary>Зеркало <see cref="ModelPreviewViewport.ApplyPreviewSettingsToMaterials"/>: Mode/Channel
	/// общие, PBR-факторы - свои на материал (см. ModelLoader.MaterialPbr).</summary>
	private static int ProbeFeatureFlags = (int)PreviewFeatureFlags.All;

	private static void PushPreviewSettings(ModelLoader model, int mode, int channel,
		bool forceFlatWhite = false)
	{
		var data = new PreviewSettingsData { Mode = mode, Channel = channel };

		for (int i = 0; i < model.materialObjects.Count; i++)
		{
			var kvp = model.materialObjects.GetAt(i);

			if (forceFlatWhite || !model.MaterialPbr.TryGetValue(kvp.Key, out var pbr))
			{
				pbr = new MaterialPbrFactors
				{
					BaseColorFactor = new Vector4(0.85f, 0.95f, 0.9f, 1f),
					MetallicFactor = 0f,
					RoughnessFactor = 0.6f,
					HasBaseColorTexture = false,
					Ior = 1.5f,
					VolumeAttenuation = new Vector4(1f, 1f, 1f, 0f),
					NormalScale = 1f,
					OcclusionStrength = 1f,
					SpecularColorFactor = Vector4.One
				};
			}

			data.Metallic = pbr.MetallicFactor;
			data.Roughness = pbr.RoughnessFactor;
			data.BaseColor = pbr.BaseColorFactor;
			data.HasBaseColorTexture = pbr.HasBaseColorTexture ? 1 : 0;
			data.AlphaCutoff = pbr.AlphaCutoff;
			data.HasMetallicRoughnessTexture = pbr.HasMetallicRoughnessTexture ? 1 : 0;
			data.Transmission = pbr.TransmissionFactor;
			data.Dispersion = pbr.Dispersion;
			data.Ior = pbr.Ior;
			data.VolumeAttenuation = pbr.VolumeAttenuation;
			data.ThicknessWorld = pbr.ThicknessWorld;
			data.FeatureFlags = ProbeFeatureFlags;
			data.NormalScale = pbr.NormalScale;
			data.OcclusionStrength = pbr.OcclusionStrength;
			data.UvOffset = pbr.UvOffset;
			data.UvTransform = pbr.UvTransform;
			data.UvHasTransform = pbr.HasUvTransform ? 1 : 0;
			data.OcclusionUvSet = pbr.OcclusionUvSet;
			data.SheenColorRoughness = pbr.SheenColorRoughness;
			data.SpecularColorFactor = pbr.SpecularColorFactor;

			kvp.Value.SetConstant("PreviewSettings", ref data, HandleAccess.Pixel);
		}
	}

	private static void CompositeOverBackdrop(byte[] rgba, int width, int height)
	{
		var top = new Vector3(0.55f, 0.55f, 0.55f);
		var bottom = new Vector3(0.26f, 0.26f, 0.26f);

		for (int y = 0; y < height; y++)
		{
			var bg = Vector3.Lerp(top, bottom, y / (float)Math.Max(1, height - 1)) * 255f;

			for (int x = 0; x < width; x++)
			{
				int idx = (y * width + x) * 4;
				float a = rgba[idx + 3] / 255f;

				rgba[idx + 0] = (byte)(rgba[idx + 0] * a + bg.X * (1f - a));
				rgba[idx + 1] = (byte)(rgba[idx + 1] * a + bg.Y * (1f - a));
				rgba[idx + 2] = (byte)(rgba[idx + 2] * a + bg.Z * (1f - a));
				rgba[idx + 3] = 255;
			}
		}
	}

	/// <summary>Минимальная числовая сводка по картинке (не по фону), чтобы "света нет" было видно
	/// прямо в консоли: средняя/максимальная яркость и разброс. Фон отсекается по нулевой альфе -
	/// таргет очищается с alpha 0 (см. ModelViewportEnvironment), геометрия пишет alpha 1.</summary>
	private static void ReportStats(string name, byte[] rgba, int width, int height)
	{
		long count = 0;
		double sum = 0;
		double chromaSum = 0;
		int chromaMax = 0;
		byte minL = 255, maxL = 0;

		for (int i = 0; i < width * height; i++)
		{
			byte r = rgba[i * 4 + 0], g = rgba[i * 4 + 1], b = rgba[i * 4 + 2];

			if (rgba[i * 4 + 3] == 0)
			{
				continue;
			}

			byte l = (byte)((r * 299 + g * 587 + b * 114) / 1000);
			minL = Math.Min(minL, l);
			maxL = Math.Max(maxL, l);
			sum += l;

			// Разброс каналов: на нейтральном сером фоне и бесцветном стекле любой ненулевой
			// chroma - это цветная кайма дисперсии (см. PbrDispersion в UnlitInstancedPS.hlsl).
			int chroma = Math.Max(Math.Abs(r - g), Math.Max(Math.Abs(g - b), Math.Abs(r - b)));
			chromaSum += chroma;
			chromaMax = Math.Max(chromaMax, chroma);

			count++;
		}

		Console.WriteLine(count == 0
			? $"[probe] {name}: EMPTY - only background pixels"
			: $"[probe] {name}: pixels={count} lum avg={sum / count:F1} min={minL} max={maxL} chroma avg={chromaSum / count:F1} max={chromaMax}");
	}
}
