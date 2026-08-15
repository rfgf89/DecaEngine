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
		// DECA_PROBE_EXPOSURE=1 - авто-экспозиция, а с ней и весь HDR-конвейер (линейный кадр +
		// отдельный TonemapPass, см. EyeAdaptationPass). По умолчанию выключена, как в редакторе.
		bool eyeAdaptation = Environment.GetEnvironmentVariable("DECA_PROBE_EXPOSURE") == "1";
		Console.WriteLine($"[probe] ssao: {ssao} ({aoMode}), ssgi: {ssgi}, shadows: {shadows}, exposure: {eyeAdaptation}");

		// DECA_PROBE_MAINCASCADES=1 - каскады строит ОСНОВНОЙ CullingAndRenderSystem через
		// солнце-сущность (путь Scene View, см. PrefabSceneViewport), а не SimpleCulling. Единственный
		// headless-способ проверить теневую математику главного пайплайна (UpdateCascades).
		bool mainCascades = Environment.GetEnvironmentVariable("DECA_PROBE_MAINCASCADES") == "1";
		if (mainCascades)
		{
			Console.WriteLine("[probe] shadow cascades: main pipeline (CullingAndRenderSystem)");
		}

		// Гарнесс изолирован (свой процесс, один запуск) - собственный контейнер, а не общий с
		// редактором (см. class-doc SharedViewportResources: "или per CLI-harness").
		var sharedResources = new SharedViewportResources(api);

		var env = new ModelViewportEnvironment(api, 512, 512, "Probe Color", "Probe Depth", sharedResources,
			skyBackground: true,
			environmentHdrPath: Environment.GetEnvironmentVariable("DECA_PROBE_HDR"),
			msaaSamples: msaa, ssao: ssao, shadows: shadows, aoMode: aoMode, ssgi: ssgi,
			eyeAdaptation: eyeAdaptation, mainCascades: mainCascades,
			// DECA_PROBE_FOG=1 - атмосферный туман в гарнессе (см. FogPass): выключен по умолчанию,
			// чтобы не сдвигать сравнительную метрику яркости ([probe] lighting: lum avg).
			fog: Environment.GetEnvironmentVariable("DECA_PROBE_FOG") == "1",
			// DECA_PROBE_BLOOM=1 - блум в гарнессе (см. BloomPass). Выключен по умолчанию по той же
			// причине, что и туман: он сдвигает сравнительную метрику яркости.
			bloom: Environment.GetEnvironmentVariable("DECA_PROBE_BLOOM") == "1",
			// DECA_PROBE_GRADE=1 - пасс цветокоррекции (см. ColorGradePass). С нейтральными
			// дефолтами он кадр не меняет, так что метрику яркости не сдвигает.
			colorGrade: Environment.GetEnvironmentVariable("DECA_PROBE_GRADE") == "1",
			// DECA_PROBE_VOLUMETRIC=1 - объёмный свет / god rays (см. VolumetricLightPass). Выключен
			// по умолчанию по той же причине, что туман и блум: он сдвигает сравнительную метрику
			// яркости.
			volumetric: Environment.GetEnvironmentVariable("DECA_PROBE_VOLUMETRIC") == "1",
			// DECA_PROBE_MOTION=1 - буфер векторов движения (см. MotionVectorPass). Кадр он не меняет
			// вообще, так что метрику яркости не сдвигает; включать имеет смысл вместе с
			// DECA_PROBE_MOTIONDEBUG=1, иначе смотреть будет не на что.
			//
			// Гарнесс - ЕДИНСТВЕННЫЙ способ проверить векторы численно: камера здесь неподвижна, а
			// значит отладочный кадр обязан быть РОВНО серым (0.5, 0.5, 0.5). Любое отклонение - это
			// либо ошибка в матрице репроджекции, либо разъезд viewProj на кадр.
			motionVectors: Environment.GetEnvironmentVariable("DECA_PROBE_MOTION") == "1",
			// DECA_PROBE_TAAU=1 - темпоральный апскейл (требует DECA_PROBE_MOTION=1, включает
			// джиттер сам - см. GraphicsPipelineSimple.ApplyTemporalJitter). Вместе с
			// DECA_PROBE_RENDERSCALE и DECA_PROBE_SHARPNESS даёт числовой тест восстановления
			// детализации: TAAU на полу-разрешении обязан быть резче билинейного апскейла.
			temporalUpscale: Environment.GetEnvironmentVariable("DECA_PROBE_TAAU") == "1",
			// DECA_PROBE_FSR=1 / DECA_PROBE_DLSS=1 - нативный бэкенд вместо встроенного TAAU
			// (требует DECA_PROBE_TAAU=1 и D3D12; шим и нативная DLL должны лежать рядом с
			// экзешником, иначе молча остаётся TAAU; DLSS - только NVIDIA RTX).
			upscalerBackend: Environment.GetEnvironmentVariable("DECA_PROBE_DLSS") == "1" ? 2
				: Environment.GetEnvironmentVariable("DECA_PROBE_FSR") == "1" ? 1 : 0);

		env.SetMotionVectorDebug(Environment.GetEnvironmentVariable("DECA_PROBE_MOTIONDEBUG") == "1",
			float.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_MOTIONRANGE"),
				System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
				out var motionRange) && motionRange > 0f
				? motionRange
				: MotionVectorDebugPassResources.DefaultRangePixels);

		// Ручки объёмного света для A/B. Нужны ровно затем, зачем DECA_PROBE_FLICKER*: без них
		// гарнесс рисует с дефолтами, а проверить главное свойство эффекта - что столбы идут ИМЕННО
		// от теней, а не от однородной дымки, - можно только сравнив кадры при силе тени 0 и 1.
		if (env.Pipeline.VolumetricResources is { } probeVolumetric)
		{
			float VolKnob(string name, float fallback) =>
				float.TryParse(Environment.GetEnvironmentVariable(name),
					System.Globalization.NumberStyles.Float,
					System.Globalization.CultureInfo.InvariantCulture, out var parsed)
					? parsed
					: fallback;

			var volDensity = VolKnob("DECA_PROBE_VOLDENSITY", VolumetricLightPassResources.DefaultDensity);
			var volShadow = VolKnob("DECA_PROBE_VOLSHADOW", VolumetricLightPassResources.DefaultShadowStrength);
			var volSun = VolKnob("DECA_PROBE_VOLSUN", VolumetricLightPassResources.DefaultSunIntensity);

			probeVolumetric.SetParams(volDensity, VolumetricLightPassResources.DefaultHeightFalloff,
				VolumetricLightPassResources.DefaultHeightRef,
				VolKnob("DECA_PROBE_VOLSTART", VolumetricLightPassResources.DefaultStartDistance),
				VolKnob("DECA_PROBE_VOLMAXDIST", VolumetricLightPassResources.DefaultMaxDistance),
				VolumetricLightPassResources.DefaultSteps,
				VolKnob("DECA_PROBE_VOLOPACITY", VolumetricLightPassResources.DefaultMaxOpacity), volShadow);
			probeVolumetric.SetScattering(
				VolKnob("DECA_PROBE_VOLSCATTER", VolumetricLightPassResources.DefaultScattering),
				VolKnob("DECA_PROBE_VOLEXT", VolumetricLightPassResources.DefaultExtinction),
				VolumetricLightPassResources.DefaultAnisotropy);
			probeVolumetric.SetColors(VolumetricLightPassResources.DefaultSunColor, volSun,
				VolumetricLightPassResources.DefaultAmbientColor,
				VolKnob("DECA_PROBE_VOLAMBIENT", VolumetricLightPassResources.DefaultAmbientIntensity),
				VolKnob("DECA_PROBE_VOLSKYFLOOR", VolumetricLightPassResources.DefaultAmbientShadowFloor));

			Console.WriteLine($"[probe] volumetric: density={volDensity} sun={volSun} shadow={volShadow} " +
				$"(shadows available: {probeVolumetric.ShadowsAvailable})");
		}

		// Бит HDR - от РЕАЛЬНО созданного окружения, как в редакторе (см.
		// ModelPreviewViewport.ApplyGraphicsSettings): без него шейдер модели тонемапил бы кадр
		// дважды - у себя и в TonemapPass.
		if (env.HdrOutput)
		{
			ProbeFeatureFlags |= (int)PreviewFeatureFlags.HdrOutput;
		}

		// A/B анизотропии: DECA_PROBE_ANISO=0 выключает (тумблер уровня загрузки, см. ModelLoadOptions).
		bool anisotropic = Environment.GetEnvironmentVariable("DECA_PROBE_ANISO") != "0";

		// Mip bias под масштаб рендера - как во вьюпорте (см. ModelPreviewViewport.BuildLoadOptions):
		// сэмплеры пекутся при загрузке, поэтому bias считается из того же DECA_PROBE_RENDERSCALE,
		// который позже отресайзит таргеты. DECA_PROBE_MIPBIAS=0 - выключить для A/B.
		float probeMipBias = float.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_RENDERSCALE"),
			System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
			out var probeRsForBias) && probeRsForBias > 0f && probeRsForBias < 1f
			? MathF.Log2(probeRsForBias)
			: 0f;

		// Явный override (в т.ч. 0 - выключить, экстремумы - диагностика тракта: +4 обязан заметно
		// замылить кадр, и если НЕ мылит - bias не доезжает до GPU).
		if (float.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_MIPBIAS"),
			    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
			    out var mipBiasOverride))
		{
			probeMipBias = mipBiasOverride;
		}

		if (probeMipBias != 0f)
		{
			Console.WriteLine($"[probe] mip bias: {probeMipBias:F2}");
		}

		var model = ModelLoader.Load(api, modelPath, new ModelLoadOptions
		{
			AnisotropicFiltering = anisotropic,
			MipLodBias = probeMipBias,
			VertexShader = new EditorRef("shader/UnlitInstancedVS.hlsl"),
			PixelShader = new EditorRef("shader/UnlitInstancedPS.hlsl"),
			OptimizeMesh = false,
			GenerateLods = false,
			// DECA_PROBE_TEXSIZE=<n> - потолок стороны текстуры (зеркало ручки превью, см.
			// EditorSettings.PreviewMaxTextureSize). Здесь он нужен ровно для того, чтобы ПРОВЕРИТЬ
			// замером, действительно ли пик памяти загрузки масштабируется от него: загрузчик
			// декодирует все текстуры разом, и если гипотеза верна, шаг вниз обязан резать пик вчетверо.
			MaxTextureSize = int.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_TEXSIZE"), out var texSize)
				? Math.Clamp(texSize, 128, 8192)
				: 2048,
			// DECA_PROBE_STREAM=1 - режим редактора: текстуры в фоновой фазе не декодируются вовсе,
			// материалы строятся с 1x1-филлерами, а качество приезжает ступенями из ModelStreamer
			// (см. ModelLoadOptions.StreamTextures). Здесь нужен, чтобы ЗАМЕРИТЬ разницу фаз: в
			// обычном режиме декод - самая дорогая фаза загрузки с отрывом.
			StreamTextures = Environment.GetEnvironmentVariable("DECA_PROBE_STREAM") == "1"
		});

		Console.WriteLine($"[probe] meshes={model.Meshes.Count} materials={model.materialObjects.Count} instances={model.instances.Count}");

		// Разбивка загрузки по фазам (см. ModelLoader.LoadTimings): без неё оптимизировать
		// загрузку приходится вслепую.
		var loadTimings = model.Timings;
		Console.WriteLine($"[probe] load: parse {loadTimings.ParseMs} ms, decode {loadTimings.DecodeMs} ms, " +
			$"materials {loadTimings.MaterialsMs} ms, meshes {loadTimings.MeshesMs} ms, finalize {loadTimings.FinalizeMs} ms; " +
			$"{loadTimings.DecodedImages} images -> {loadTimings.DecodedBytes / (1024 * 1024)} MB decoded");
		Console.WriteLine($"[probe] load shaders: {loadTimings.ShaderVariants} pixel variants, " +
			$"{loadTimings.ShaderMs} ms (внутри finalize)");
		Console.WriteLine($"[probe] load textures: {loadTimings.TextureUploads} uploads, " +
			$"{loadTimings.TextureMs} ms (внутри finalize)");
		Console.WriteLine($"[probe] load pso: {DecaEngine.Graphics.Diligent.DiligentPsoManager.DiagCreateCount} created, " +
			$"{DecaEngine.Graphics.Diligent.DiligentPsoManager.DiagCreateMs} ms (с начала процесса)");
		Console.WriteLine($"[probe] load meshes-gpu: {loadTimings.MeshUploads} meshes, " +
			$"{loadTimings.MeshMs} ms (внутри finalize)");
		Console.WriteLine($"[probe] load samplers: {loadTimings.Samplers} created, " +
			$"{loadTimings.SamplerMs} ms (внутри finalize)");
		Console.WriteLine($"[probe] load materials-gpu: {loadTimings.MaterialsBuilt} built, " +
			$"{loadTimings.MaterialBuildMs} ms (внутри finalize): " +
			$"CreateMaterial {loadTimings.MatCreateMs} ms, SetShader {loadTimings.MatShaderMs} ms");
		// DECA_PROBE_VISRES=<8..24> - сторона окто-карты видимости (ручка «Visibility res»).
		// Нужна для A/B: качество теста Чебышёва зависит и от неё, и от числа лучей на пробу.
		if (int.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_VISRES"), out var visResOverride))
		{
			ProbeGiBakeResult.VisRes = Math.Clamp(visResOverride,
				ProbeGiBakeResult.MinVisRes, ProbeGiBakeResult.MaxVisRes);
			Console.WriteLine($"[probe] visibility res: {ProbeGiBakeResult.VisRes}");
		}

		// DECA_PROBE_BVHVERIFY=1 - СВЕРКА кеша с прямой сборкой: строим дерево обоими путями и
		// сравниваем экспорт для GPU (узлы + треугольники + порядок) поле в поле. Кеш - это
		// сериализация структуры, от которой зависит вся трассировка проб; расхождение здесь
		// означало бы тихо испорченный GI на всех последующих запусках.
		if (Environment.GetEnvironmentVariable("DECA_PROBE_BVHVERIFY") == "1")
		{
			var swBuilt = System.Diagnostics.Stopwatch.StartNew();
			var direct = new ProbeGiBaker(model);
			long builtMs = swBuilt.ElapsedMilliseconds;

			ProbeGiBvhCache.Write(modelPath, direct.ExportCache());

			var swCached = System.Diagnostics.Stopwatch.StartNew();
			var fromCache = ProbeGiBaker.LoadOrBuild(model, modelPath, out var usedCache);
			long cacheMs = swCached.ElapsedMilliseconds;

			var a = direct.ExportCache();
			var b = fromCache.ExportCache();

			bool ok = usedCache
				&& a.NodeCount == b.NodeCount
				&& a.SceneEpsilon == b.SceneEpsilon
				&& a.RayTMax == b.RayTMax
				&& a.Triangles.Length == b.Triangles.Length
				&& a.Nodes.Length == b.Nodes.Length
				&& a.Order.Length == b.Order.Length
				&& a.ObjectTriangles.Length == b.ObjectTriangles.Length
				&& a.MeshSlots.Length == b.MeshSlots.Length
				&& a.Instances.Length == b.Instances.Length;

			int mismatches = 0;
			if (ok)
			{
				for (int i = 0; i < a.Triangles.Length && mismatches < 8; i++)
				{
					var x = a.Triangles[i];
					var y = b.Triangles[i];
					if (x.A != y.A || x.E1 != y.E1 || x.E2 != y.E2 || x.Albedo != y.Albedo)
					{
						Console.WriteLine($"[probe] bvh verify: tri[{i}] differs");
						mismatches++;
					}
				}

				for (int i = 0; i < a.Nodes.Length && mismatches < 8; i++)
				{
					var x = a.Nodes[i];
					var y = b.Nodes[i];
					if (x.Min != y.Min || x.Max != y.Max || x.Left != y.Left || x.Start != y.Start || x.Count != y.Count)
					{
						Console.WriteLine($"[probe] bvh verify: node[{i}] differs");
						mismatches++;
					}
				}

				for (int i = 0; i < a.Order.Length && mismatches < 8; i++)
				{
					if (a.Order[i] != b.Order[i])
					{
						Console.WriteLine($"[probe] bvh verify: order[{i}] differs");
						mismatches++;
					}
				}

				for (int i = 0; i < a.Instances.Length && mismatches < 8; i++)
				{
					if (!a.Instances[i].Equals(b.Instances[i]))
					{
						Console.WriteLine($"[probe] bvh verify: instance[{i}] differs");
						mismatches++;
					}
				}
			}

			var sa = direct.GetStats();
			var sb = fromCache.GetStats();
			bool statsMatch = sa.Equals(sb);

			// Отладочные боксы: число узлов на уровне должно расти вдвое (медианный сплит), корень -
			// ровно один и обязан накрывать баунды всей сцены, а листьев - столько же, сколько
			// насчитала статистика. Иначе оверлей показывал бы не то дерево, по которому идут лучи.
			for (int depth = 0; depth <= 4; depth++)
			{
				var boxes = fromCache.CollectDebugBoxes(depth, leavesOnly: false);
				Console.WriteLine($"[probe] bvh boxes: depth<={depth} -> {boxes.Count} boxes " +
					$"(expected <= {(1 << (depth + 1)) - 1})");
			}

			var leafBoxes = fromCache.CollectDebugBoxes(64, leavesOnly: true);
			var rootBox = fromCache.CollectDebugBoxes(0, leavesOnly: false);
			Console.WriteLine($"[probe] bvh boxes: leaves -> {leafBoxes.Count} (stats says {sb.Leaves}), " +
				$"root {(rootBox.Count == 1 ? $"min={rootBox[0].Min} max={rootBox[0].Max}" : "MISSING")}, " +
				$"stats bounds min={sb.Min} max={sb.Max}");

			Console.WriteLine($"[probe] bvh verify: {(ok && mismatches == 0 && statsMatch ? "MATCH" : "MISMATCH")} " +
				$"(usedCache={usedCache}, built {builtMs} ms vs cache {cacheMs} ms, " +
				$"tris {a.Triangles.Length}/{b.Triangles.Length}, nodes {a.NodeCount}/{b.NodeCount}, " +
				$"stats {(statsMatch ? "equal" : $"{sa} != {sb}")}, field diffs {mismatches})");
		}

		// DECA_PROBE_BVHCACHE=1 - проверка дискового кеша BVH (<модель>.bhv.bin): первый прогон
		// строит дерево и пишет файл, второй обязан прочитать его и уложиться в доли секунды.
		if (Environment.GetEnvironmentVariable("DECA_PROBE_BVHCACHE") == "1")
		{
			var swBvh = System.Diagnostics.Stopwatch.StartNew();
			var bvhBaker = ProbeGiBaker.LoadOrBuild(model, modelPath, out var bvhFromCache);
			var bvhStats = bvhBaker.GetStats();
			Console.WriteLine($"[probe] bvh: {(bvhFromCache ? "CACHE" : "BUILT")} in {swBvh.ElapsedMilliseconds} ms, " +
				$"{bvhStats.Triangles} tris, {bvhStats.Nodes} nodes, {bvhStats.Leaves} leaves, " +
				$"depth {bvhStats.MaxDepth}, {bvhStats.AvgLeafTriangles:F2} tris/leaf, " +
				$"file '{Path.GetFileName(ProbeGiBvhCache.GetCachePath(modelPath))}'");
		}

		// Стрим-записи текстур: без валидного источника (путь к файлу или встроенные байты) апгрейд
		// качества в редакторе молча не состоится - слоты навсегда останутся на 1x1-филлере.
		if (model.StreamedTextures.Count > 0)
		{
			int withPath = 0, withBytes = 0;
			foreach (var entry in model.StreamedTextures)
			{
				if (entry.FilePath != null) withPath++;
				else if (entry.EncodedPixels != null) withBytes++;
			}

			Console.WriteLine($"[probe] stream textures: {model.StreamedTextures.Count} entries " +
				$"({withPath} by file path, {withBytes} embedded), bindings=" +
				$"{model.StreamedTextures.Sum(e => e.Bindings.Count)}");
		}

		Console.WriteLine($"[probe] load compile: {DecaEngine.Graphics.Diligent.DiligentShader.DiagCompileCalls} calls, " +
			$"{DecaEngine.Graphics.Diligent.DiligentShader.DiagCompileActual} РЕАЛЬНЫХ, " +
			$"{DecaEngine.Graphics.Diligent.DiligentShader.DiagCompileMs} ms");

		// DECA_PROBE_RELOAD=<путь> - загрузить ВТОРУЮ модель после освобождения первой, ровно как
		// это делает редактор при смене модели в превью (ModelPreviewViewport.PopulateFromScene:
		// ResetRegistrations + ModelLoader.Release + регистрация новой).
		//
		// Это репро для целого класса падений: ресурсы модели ШАРЯТСЯ (один вершинный шейдер и
		// варианты пиксельного - между всеми её материалами; дефолтный материал - между всеми
		// null-материалами), и наивное освобождение "по всем значениям словаря" уводит нативные
		// счётчики ссылок в минус - 0xC0000005 в Diligent.ComObject.Release.
		var reloadPath = Environment.GetEnvironmentVariable("DECA_PROBE_RELOAD");
		if (!string.IsNullOrEmpty(reloadPath))
		{
			Console.WriteLine($"[probe] reload test: releasing '{modelPath}', loading '{reloadPath}'");
			api.ImmediateContext.Flush();
			api.ImmediateContext.WaitForIdle();
			env.BatchRenderer.ResetRegistrations();
			model.Release();
			Console.WriteLine("[probe] reload test: first model released OK");

			model = ModelLoader.Load(api, reloadPath, new ModelLoadOptions
			{
				AnisotropicFiltering = anisotropic,
				VertexShader = new EditorRef("shader/UnlitInstancedVS.hlsl"),
				PixelShader = new EditorRef("shader/UnlitInstancedPS.hlsl"),
				OptimizeMesh = false,
				GenerateLods = false
			});

			Console.WriteLine($"[probe] reload test: second model loaded OK " +
				$"(meshes={model.Meshes.Count} materials={model.materialObjects.Count})");
		}

		var meshIdMap = new Dictionary<int, MeshId>();
		var materialIdMap = new Dictionary<int, MaterialId>();
		var batchCache = new Dictionary<(int, int), BatchId>();
		ModelViewportGeometry.RegisterModelResources(env.BatchRenderer, model, meshIdMap, materialIdMap,
			sharedResources.EnvMapSampler, env.SceneCopyTarget, env.EnvironmentMap,
			sceneCopySampler: sharedResources.SceneColorSampler);

		// Сколько материалов пишут тень с альфа-тестом (листва). Ноль на сцене с деревом означает,
		// что критерий отбора её не признал, - и тогда никакие god rays сквозь крону не пойдут по
		// причине, не имеющей к объёмному свету никакого отношения (см. ShadowMaskedPS.hlsl).
		Console.WriteLine($"[probe] alpha-tested shadows: {env.BatchRenderer.WorldShadowRenderer.AlphaTestedMaterialCount} materials");

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

		// DECA_PROBE_EYE / DECA_PROBE_TARGET = "x,y,z" - явная камера вместо орбитальной, чтобы
		// воспроизводить интерьерные ракурсы редактора (орбита вокруг центра баундов не может
		// встать внутри двора). Печатаются в [probe] eye=... - значения можно снять из редактора.
		static Vector3? ParseVec(string? s)
		{
			if (string.IsNullOrWhiteSpace(s)) return null;
			var parts = s.Split(',');
			if (parts.Length != 3) return null;
			return float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x)
				&& float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y)
				&& float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var z)
				? new Vector3(x, y, z) : null;
		}

		if (ParseVec(Environment.GetEnvironmentVariable("DECA_PROBE_EYE")) is { } eyeOverride)
		{
			eye = eyeOverride;
		}
		if (ParseVec(Environment.GetEnvironmentVariable("DECA_PROBE_TARGET")) is { } targetOverride)
		{
			target = targetOverride;
		}

		env.SetCameraTransform(eye, target);

		// DECA_PROBE_SPOT=1 - завести один punctual spot-свет в env.Store (тот же ECS-стор, который
		// читает SimpleCullingAndRenderSystem - см. её punctualLightsQuery) и репро баг-репорта
		// "спот светит геометрию, но тень сквозь объект не ложится". Позиция/цель/угол/тень все
		// управляются переменными окружения, чтобы гонять и параметры пользователя (SpotAngle=127.7
		// и т.д.), и здравый контроль (SpotAngle=45) без пересборки:
		//   DECA_PROBE_SPOT_POS="x,y,z"      - позиция света (по умолчанию eye + up*1.5, над камерой)
		//   DECA_PROBE_SPOT_TARGET="x,y,z"   - точка, куда светит конус (по умолчанию model target)
		//   DECA_PROBE_SPOT_ANGLE=<deg>      - SpotAngle, ПОЛНЫЙ внешний угол конуса (default 45)
		//   DECA_PROBE_SPOT_INNER=<deg>      - InnerSpotAngle, ПОЛНЫЙ внутренний угол (default 0 = авто 80%)
		//   DECA_PROBE_SPOT_RANGE=<float>    - Range (default 5)
		//   DECA_PROBE_SPOT_INTENSITY=<float>- Intensity (default 8)
		//   DECA_PROBE_SPOT_COLOR="r,g,b"    - Color (default 3.45,3.6,4.05 - как у пользователя)
		//   DECA_PROBE_SPOT_SHADOW=<float>   - ShadowStrength ДО клампа (default 5.85, как у пользователя;
		//                                       TryBuildPunctualLight клампит его к [0,1] сам)
		if (Environment.GetEnvironmentVariable("DECA_PROBE_SPOT") == "1")
		{
			var spotPos = ParseVec(Environment.GetEnvironmentVariable("DECA_PROBE_SPOT_POS"))
				?? eye + Vector3.UnitY * 1.5f;
			var spotTarget = ParseVec(Environment.GetEnvironmentVariable("DECA_PROBE_SPOT_TARGET")) ?? target;
			var spotColor = ParseVec(Environment.GetEnvironmentVariable("DECA_PROBE_SPOT_COLOR"))
				?? new Vector3(3.45f, 3.6f, 4.05f);

			float spotAngle = EnvFloat("DECA_PROBE_SPOT_ANGLE", 45f);
			float spotInner = EnvFloat("DECA_PROBE_SPOT_INNER", 0f);
			float spotRange = EnvFloat("DECA_PROBE_SPOT_RANGE", 5f);
			float spotIntensity = EnvFloat("DECA_PROBE_SPOT_INTENSITY", 8f);
			float spotShadow = EnvFloat("DECA_PROBE_SPOT_SHADOW", 5.85f);

			var spotDir = Vector3.Normalize(spotTarget - spotPos);
			var spotUp = MathF.Abs(spotDir.Y) > 0.95f ? Vector3.UnitX : Vector3.UnitY;
			// Локальный +Z энтити должен смотреть вдоль spotDir - та же конвенция, что
			// PunctualShadowScheduler.AddSlice/LightCulling.TryBuildPunctualLight (dir = Transform(UnitZ, rot)).
			var spotView = Matrix4x4.CreateLookAtLeftHanded(Vector3.Zero, spotDir, spotUp);
			var spotRot = Quaternion.CreateFromRotationMatrix(Matrix4x4.Transpose(spotView));

			env.Store.CreateEntity(
				new Position(spotPos.X, spotPos.Y, spotPos.Z),
				new Rotation { value = spotRot },
				new LightComponent
				{
					Type = LightType.Spot,
					Color = spotColor,
					Intensity = spotIntensity,
					Range = spotRange,
					SpotAngle = spotAngle,
					InnerSpotAngle = spotInner,
					ShadowStrength = spotShadow,
				});

			Console.WriteLine($"[probe] spot light: pos={spotPos} target={spotTarget} dir={spotDir} " +
				$"angle={spotAngle} inner={spotInner} range={spotRange} intensity={spotIntensity} " +
				$"shadow={spotShadow} (clamped to 1 downstream)");

			// DECA_PROBE_SPOT_DECOYS=<n> - засеять n СПОТ-светов (по одному слайсу каждый - точечный
			// взял бы 6 и рассадка вышла бы иначе) с ShadowStrength=1 БЛИЖЕ к камере, чем наш спот, -
			// проверка гипотезы "бюджет теневых слайсов (LightClusters.MaxShadowSlices=16) исчерпан
			// ближними светами раньше, чем очередь дойдёт до этого спота" (PunctualShadowScheduler.
			// BuildShadowSlices раздаёт слайсы по возрастанию дистанции до камеры; не влезший свет
			// получает ShadowParams.x = -1 и светит БЕЗ тени - ровно симптом баг-репорта: геометрия
			// освещена, но тень отсутствует целиком). Декои сидят прямо на луче камера->спот, ближе
			// камеры, чтобы гарантированно попасть в раскладку раньше цели.
			if (int.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_SPOT_DECOYS"), out var decoyCount) && decoyCount > 0)
			{
				for (int i = 0; i < decoyCount; i++)
				{
					var decoyPos = eye + (spotPos - eye) * (0.01f + 0.001f * i);
					env.Store.CreateEntity(
						new Position(decoyPos.X, decoyPos.Y, decoyPos.Z),
						new Rotation(),
						new LightComponent
						{
							Type = LightType.Spot,
							Color = new Vector3(1f, 1f, 1f),
							Intensity = 0.001f, // почти невидим - нужен только для бюджета слайсов, не для картинки
							Range = 1f,
							SpotAngle = 30f,
							ShadowStrength = 1f,
						});
				}
				Console.WriteLine($"[probe] spot decoys: {decoyCount} shadow-casting SPOT lights (1 slice each) seeded " +
					"closer to camera (budget-exhaustion test)");
			}
		}

		// DECA_PROBE_POINT=1 - завести один punctual POINT-свет (6 теневых слайсов, куб. грани -
		// см. PunctualShadowScheduler.FaceDirs/UnlitInstancedPS.hlsl:983-998) вместо спота: спот
		// покрывает только ОДИН слайс/одну проекцию, а баг-репорт пользователя - именно точечный
		// свет, чей путь выбора грани по доминирующей оси до сих пор ничем не проверялся.
		//   DECA_PROBE_POINT_POS="x,y,z"      - позиция света (default eye + up*1.5)
		//   DECA_PROBE_POINT_RANGE=<float>    - Range (default 6.4, как у пользователя)
		//   DECA_PROBE_POINT_INTENSITY=<float>- Intensity (default 8)
		//   DECA_POINT_COLOR / DECA_PROBE_POINT_COLOR="r,g,b" - Color (default 3.45,3.6,4.05)
		//   DECA_PROBE_POINT_SHADOW=<float>   - ShadowStrength ДО клампа (default 5.85, как у
		//                                        пользователя; TryBuildPunctualLight клампит к [0,1])
		if (Environment.GetEnvironmentVariable("DECA_PROBE_POINT") == "1")
		{
			var pointPos = ParseVec(Environment.GetEnvironmentVariable("DECA_PROBE_POINT_POS"))
				?? eye + Vector3.UnitY * 1.5f;
			var pointColor = ParseVec(Environment.GetEnvironmentVariable("DECA_PROBE_POINT_COLOR"))
				?? new Vector3(3.45f, 3.6f, 4.05f);

			float pointRange = EnvFloat("DECA_PROBE_POINT_RANGE", 6.4f);
			float pointIntensity = EnvFloat("DECA_PROBE_POINT_INTENSITY", 8f);
			float pointShadow = EnvFloat("DECA_PROBE_POINT_SHADOW", 5.85f);

			env.Store.CreateEntity(
				new Position(pointPos.X, pointPos.Y, pointPos.Z),
				new Rotation(),
				new LightComponent
				{
					Type = LightType.Point,
					Color = pointColor,
					Intensity = pointIntensity,
					Range = pointRange,
					ShadowStrength = pointShadow,
				});

			Console.WriteLine($"[probe] point light: pos={pointPos} range={pointRange} " +
				$"intensity={pointIntensity} shadow={pointShadow} (clamped to 1 downstream)");
		}

		// DECA_PROBE_RENDERSCALE=<0.25..1> - масштаб рендера сцены (см.
		// GraphicsPipelineSimple.SetRenderScale): сценовые таргеты уменьшаются, ColorTarget остаётся
		// display, кадр поднимает тонемап. Зеркало ресайз-пути вьюпорта, как DECA_PROBE_RESIZE.
		// Камера НЕ трогается: подмену viewport.zw на рендер-размер в View-кбуфере конвейер делает
		// сам (LatchRenderResolution).
		if (float.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_RENDERSCALE"),
			    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
			    out var renderScale)
		    && renderScale > 0f && renderScale < 1f)
		{
			env.Pipeline.SetRenderScale(renderScale);
			var displaySize = env.ColorTarget.Size;
			var sceneSize = env.Pipeline.SceneSizeFor(displaySize);
			Console.WriteLine($"[probe] render scale: {env.Pipeline.RenderScale:F2} -> " +
				$"scene {sceneSize.X}x{sceneSize.Y}, display {displaySize.X}x{displaySize.Y}");

			env.DilApi.ImmediateContext.Flush();
			env.DilApi.ImmediateContext.WaitForIdle();

			env.DepthTarget.Resize(sceneSize);
			env.SceneCopyTarget.Resize(sceneSize);
			env.MsaaColorTarget?.Resize(sceneSize);
			env.MsaaDepthTarget?.Resize(sceneSize);
			env.AoTarget?.Resize(sceneSize);
			env.GiTarget?.Resize(sceneSize);
			env.HdrColorTarget?.Resize(sceneSize);
			env.RebindPostProcessTargets();
			for (int i = 0; i < model.materialObjects.Count; i++)
			{
				model.materialObjects.GetAt(i).Value.SetTexture("_SceneColor", env.SceneCopyTarget);
			}

			env.Pipeline.SetOffscreenViewportSize(displaySize);
		}

		if (env.ShadowSettings != null)
		{
			env.ShadowSettings.BoundsCenter = target;
			env.ShadowSettings.BoundsRadius = radius;

			// DECA_PROBE_CASCADES_SM=<1..4> - число теневых каскадов (зеркало настройки вьюпорта;
			// не путать с DECA_PROBE_CASCADES - каскадами probe-GI). Должно быть выставлено ДО
			// первого кадра: SimpleCullingAndRenderSystem замораживает под него ёмкость
			// DirectionalLightCascadeData.
			if (int.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_CASCADES_SM"), out var smCascades))
			{
				env.ShadowSettings.CascadeCount = Math.Clamp(smCascades, 1, ShadowRenderer.MaxCascades);
				Console.WriteLine($"[probe] shadow cascades: {env.ShadowSettings.CascadeCount}");
			}

			// DECA_PROBE_LIGHT="yawOffset,elevOffset" (градусы) - смещения от базового солнца
			// энвайронмента, зеркало ползунков Light Yaw/Height редактора (см.
			// ModelPreviewViewport.SetLightRotation). Применяется ДО бейка проб.
			var lightRaw = Environment.GetEnvironmentVariable("DECA_PROBE_LIGHT");
			if (!string.IsNullOrWhiteSpace(lightRaw))
			{
				var lp = lightRaw.Split(',');
				if (lp.Length == 2
					&& float.TryParse(lp[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var yawOff)
					&& float.TryParse(lp[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var elevOff))
				{
					env.ShadowSettings.SetAngles(
						env.ShadowSettings.BaseYawDegrees + yawOff,
						Math.Clamp(env.ShadowSettings.BaseElevationDegrees + elevOff, -85f, 85f));
					EnvYaw = env.ShadowSettings.EnvYawRadians;
					env.Pipeline.SkyResources?.SetEnvironmentYaw(EnvYaw);
					Console.WriteLine($"[probe] light offsets: yaw {yawOff} elev {elevOff} -> dir {env.ShadowSettings.LightDirection}");
				}
			}
		}

		// Тот же мировой радиус AO, что пушит редакторский вьюпорт (см. ModelPreviewViewport.FrameAll).
		// DECA_PROBE_AO_RANGE=<доля радиуса баундов> - переопределить, 0 - легаси-режим (радиус в
		// долях экрана) для A/B.
		float aoRangeFraction = float.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_AO_RANGE"),
			System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedAoRange)
			? parsedAoRange
			: ModelViewportEnvironment.AoRangeOfBoundsRadius;
		// DECA_PROBE_AO_RANGE_WORLD=<мировые единицы> - радиус напрямую, минуя долю баундов
		// (зеркало ручки "AO radius (world)", см. EditorSettings.AoRadiusWorld).
		float aoRangeWorld = float.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_AO_RANGE_WORLD"),
			System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedAoWorld)
			? parsedAoWorld
			: 0f;
		env.SetAoWorldRange(aoRangeWorld > 0f ? aoRangeWorld : radius * aoRangeFraction);
		// DECA_PROBE_AO_DEBUG=1 - отладочный вид AO (чекбокс "AO debug view" окна Graphics):
		// композит выводит видимость в grayscale, PNG становится картой AO для A/B техник.
		env.SetAoDebugView(Environment.GetEnvironmentVariable("DECA_PROBE_AO_DEBUG") == "1");
		// Ручки SSGI - зеркало секции SSGI окна Graphics (см. EditorSettings.Ssgi*), чтобы шум
		// экранного отскока можно было A/B-ить в headless без пересборки:
		// DECA_PROBE_SSGI_RANGE_WORLD=<мировые единицы> (0 - доля баундов, как в редакторе),
		// DECA_PROBE_SSGI_PARAMS="интенсивность,тапы,firefly clamp,насыщенность",
		// DECA_PROBE_SSGI_BLUR=<радиус билатерального размытия>, DECA_PROBE_SSGI_DEBUG=1 - вывести
		// один только bounce.
		static float EnvFloat(string name, float fallback) =>
			float.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

		float giRangeWorld = EnvFloat("DECA_PROBE_SSGI_RANGE_WORLD", 0f);
		env.SetGiWorldRange(giRangeWorld > 0f
			? giRangeWorld
			: radius * ModelViewportEnvironment.GiRangeOfBoundsRadius);

		var ssgiParams = (Environment.GetEnvironmentVariable("DECA_PROBE_SSGI_PARAMS") ?? "").Split(',');
		if (ssgiParams.Length == 4
			&& float.TryParse(ssgiParams[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var giIntensity)
			&& int.TryParse(ssgiParams[1], out var giSamples)
			&& float.TryParse(ssgiParams[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var giMaxLum)
			&& float.TryParse(ssgiParams[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var giSat))
		{
			env.SetGiParams(giIntensity, giSamples, giMaxLum, giSat);
			Console.WriteLine($"[probe] ssgi params: intensity={giIntensity} samples={giSamples} clamp={giMaxLum} sat={giSat}");
		}

		env.SetGiCompositeParams(
			(int)EnvFloat("DECA_PROBE_SSGI_BLUR", SsgiPassResources.DefaultBlurRadius),
			Environment.GetEnvironmentVariable("DECA_PROBE_SSGI_DEBUG") == "1");

		// В mainCascades-режиме дистанции каскадов синхронизируются с камерой так же, как это
		// каждый кадр делает Scene View (см. PrefabSceneViewport.SyncSunEntity): по диапазону, где
		// реально лежит геометрия. Без этого солнце-сущность остаётся со статичными дефолтами
		// [0.01..300], и при орбитальной камере снаружи ближние каскады закономерно пусты.
		if (mainCascades && !env.SunEntity.IsNull && env.ShadowSettings != null)
		{
			float syncSceneRadius = env.ShadowSettings.BoundsRadius * 1.15f;
			float distanceToScene = Vector3.Distance(eye, env.ShadowSettings.BoundsCenter);
			float rangeStart = MathF.Max(distanceToScene - syncSceneRadius, 0.01f);
			float rangeSpan = MathF.Max(distanceToScene + syncSceneRadius - rangeStart, syncSceneRadius * 0.1f);

			ref var cascaded = ref env.SunEntity.GetComponent<CascadedShadowComponent>();
			cascaded.CascadeDistances[0] = rangeStart;
			cascaded.CascadeDistances[1] = rangeStart + rangeSpan * 0.055f;
			cascaded.CascadeDistances[2] = rangeStart + rangeSpan * 0.144f;
			cascaded.CascadeDistances[3] = rangeStart + rangeSpan * 0.38f;
			cascaded.CascadeDistances[4] = rangeStart + rangeSpan;
			Console.WriteLine($"[probe] cascade distances: [{cascaded.CascadeDistances[0]:F1}, " +
				$"{cascaded.CascadeDistances[1]:F1}, {cascaded.CascadeDistances[2]:F1}, " +
				$"{cascaded.CascadeDistances[3]:F1}, {cascaded.CascadeDistances[4]:F1}]");
		}

		Console.WriteLine($"[probe] eye={eye} target={target} distance={distance}");

		var giParamsRaw = Environment.GetEnvironmentVariable("DECA_PROBE_GIPARAMS");
		if (!string.IsNullOrWhiteSpace(giParamsRaw))
		{
			var parts = giParamsRaw.Split(',');
			if (parts.Length == 4
				&& float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gx)
				&& float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gy)
				&& float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gz)
				&& float.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gw))
			{
				GiParamsOverride = new Vector4(gx, gy, gz, gw);
				Console.WriteLine($"[probe] gi params override: {GiParamsOverride}");
			}
		}

		if (float.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_GIPARAMS2"),
			System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var skyFloor))
		{
			SkyShadowFloorOverride = skyFloor;
			Console.WriteLine($"[probe] sky shadow floor override: {skyFloor}");
		}

		// Аппаратная трассировка запрашивается опционально (см. DiligentGraphicsApi.Initialize) -
		// печатаем, что устройство реально дало: от этого зависит, какой путь возьмёт динамический
		// GI (аппаратный, compute-обход своего BVH или нынешний CPU).
		Console.WriteLine($"[probe] ray tracing: {api.RayTracing}");

		// Смоук-проверка структур ускорения: строим BLAS на каждый меш и TLAS по инстансам той же
		// модели. Здесь она под валидацией бэкенда, то есть ловит неверные флаги буферов, шаги и
		// матрицы до того, как на них поедет динамический GI.
		if (api.RayTracing != RayTracingSupport.None && api is DiligentGraphicsApi dilApi)
		{
			var rtSw = System.Diagnostics.Stopwatch.StartNew();
			var rtScene = new DiligentRayTracingScene(dilApi);
			try
			{
				var rtInstances = new List<DiligentRayTracingScene.Instance>();
				foreach (var instance in model.instances)
				{
					if (instance.meshId < 0 || instance.meshId >= model.Meshes.Count ||
						model.Meshes[instance.meshId] is not DiligentMesh mesh ||
						mesh.IndexCount < 3 || mesh.VertexBuffer == null || mesh.IndexBuffer == null)
					{
						continue;
					}

					var t = instance.transform;
					rtInstances.Add(new DiligentRayTracingScene.Instance(mesh,
						Matrix4x4.CreateScale(t.scale.X, t.scale.Y, t.scale.Z) *
						Matrix4x4.CreateFromQuaternion(t.rotation) *
						Matrix4x4.CreateTranslation(t.position),
						(uint)rtInstances.Count));
				}

				rtScene.Rebuild(rtInstances);
				long buildMs = rtSw.ElapsedMilliseconds;

				// Пересборка TLAS без трогания BLAS - ровно то, что динамический мир делает каждый
				// кадр; её цена и есть главное число этой проверки.
				rtSw.Restart();
				rtScene.Rebuild(rtInstances);
				dilApi.ImmediateContext.Flush();
				dilApi.ImmediateContext.WaitForIdle();

				Console.WriteLine($"[probe] accel structs: {rtScene.InstanceCount} instances, " +
					$"full build {buildMs} ms, tlas rebuild {rtSw.ElapsedMilliseconds} ms");
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[probe] accel structs FAILED: {ex.Message}");
			}
			finally
			{
				rtScene.Release();
			}
		}

		// DECA_PROBE_PROBEGI=0 - выключить probe-GI (DDGI-lite, см. ProbeGi.cs). По умолчанию
		// печётся синхронно перед рендером - тот же путь, что фоновый бейк редактора (см.
		// ModelPreviewViewport.StartProbeBake), но headless-у ждать нечего. Сетка всегда по баундам
		// ВСЕЙ модели, даже при изоляции сабмеша - трассировщик видит всю геометрию.
		if (Environment.GetEnvironmentVariable("DECA_PROBE_PROBEGI") != "0" && env.ShadowSettings != null)
		{
			var sw = System.Diagnostics.Stopwatch.StartNew();

			// DECA_PROBE_BVHFROMCACHE=1 - взять BVH из <модель>.bhv.bin (как это делает редактор).
			// Нужно для A/B: поле проб, посчитанное по кешированному дереву, обязано совпасть с
			// полем по дереву, собранному прямо сейчас, иначе кеш меняет освещение.
			ProbeGiBaker baker;
			bool bakerFromCache = false;
			if (Environment.GetEnvironmentVariable("DECA_PROBE_BVHFROMCACHE") == "1")
			{
				baker = ProbeGiBaker.LoadOrBuild(model, modelPath, out bakerFromCache);
			}
			else
			{
				baker = new ProbeGiBaker(model);
			}

			Console.WriteLine($"[probe] probe-gi bvh: {baker.TriangleCount} triangles ({sw.ElapsedMilliseconds} ms" +
				$"{(bakerFromCache ? ", from cache" : "")})");
			if (baker.HasGeometry)
			{
				var (fullMin, fullMax) = model.ComputeBounds();

				// DECA_PROBE_FLICKER=<лучей за раунд> - замерить мерцание поля в режиме реального
				// времени (см. SceneTraceVerifier.MeasureFlicker). Разделяет две причины, которые на
				// глаз выглядят одинаково: дисперсию оценки и раскачку петли мультибаунса.
				if (env.ShadowSettings != null &&
					int.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_FLICKER"),
						out var flickerRays) && flickerRays > 0)
				{
					var flicker = SceneTraceVerifier.MeasureFlicker(env.DilApi, baker,
						fullMin, fullMax,
						Vector3.Normalize(-env.ShadowSettings.LightDirection),
						new Vector3(1f, 0.98f, 0.92f) * 2f,
						raysPerRound: flickerRays, settleRounds: 40, measureRounds: 16,
						// DECA_PROBE_FLICKERCLAMP=<потолок яркости луча>, 0 = без ограничения.
						maxRayLuminance: float.TryParse(
							Environment.GetEnvironmentVariable("DECA_PROBE_FLICKERCLAMP"),
							System.Globalization.NumberStyles.Float,
							System.Globalization.CultureInfo.InvariantCulture, out var clamp)
							? clamp
							: 0f,
						// DECA_PROBE_FLICKERBLEND=<альфа>, 0 = дефолтная.
						blend: float.TryParse(
							Environment.GetEnvironmentVariable("DECA_PROBE_FLICKERBLEND"),
							System.Globalization.NumberStyles.Float,
							System.Globalization.CultureInfo.InvariantCulture, out var blendOverride)
							? blendOverride
							: 0f,
						// DECA_PROBE_FLICKERSKY=<яркость неба>, DECA_PROBE_FLICKERGRID=<плотность
						// сетки>: воспроизведение настроек редактора - контраст неба и размер ячейки
						// влияют на разброс пробы сильнее, чем что-либо ещё.
						skyIntensity: float.TryParse(
							Environment.GetEnvironmentVariable("DECA_PROBE_FLICKERSKY"),
							System.Globalization.NumberStyles.Float,
							System.Globalization.CultureInfo.InvariantCulture, out var flickerSky)
							? flickerSky
							: 1f,
						gridDensity: float.TryParse(
							Environment.GetEnvironmentVariable("DECA_PROBE_FLICKERGRID"),
							System.Globalization.NumberStyles.Float,
							System.Globalization.CultureInfo.InvariantCulture, out var flickerGrid)
							? flickerGrid
							: 0f,
						// DECA_PROBE_FLICKERSTEP=<предел изменения за раунд>, 0 = выключить.
						maxStep: float.TryParse(
							Environment.GetEnvironmentVariable("DECA_PROBE_FLICKERSTEP"),
							System.Globalization.NumberStyles.Float,
							System.Globalization.CultureInfo.InvariantCulture, out var flickerStep)
							? flickerStep
							: -1f,
						// DECA_PROBE_FLICKERRELOC=<предел релокации в долях ячейки>, 0 = выключить.
						relocation: float.TryParse(
							Environment.GetEnvironmentVariable("DECA_PROBE_FLICKERRELOC"),
							System.Globalization.NumberStyles.Float,
							System.Globalization.CultureInfo.InvariantCulture, out var flickerReloc)
							? flickerReloc
							: -1f,
						// DECA_PROBE_FLICKERGAMMA=<гамма накопления>, 1 = линейно (выключить).
						gamma: float.TryParse(
							Environment.GetEnvironmentVariable("DECA_PROBE_FLICKERGAMMA"),
							System.Globalization.NumberStyles.Float,
							System.Globalization.CultureInfo.InvariantCulture, out var flickerGamma)
							? flickerGamma
							: -1f,
						hardware: api.RayTracing >= RayTracingSupport.Inline,
						environmentMap: env.EnvironmentMap,
						skyRadiance: env.EnvironmentRadiance,
						envYaw: env.ShadowSettings.EnvYawRadians);
					Console.WriteLine($"[probe] flicker: {flicker.Rays} rays/round, " +
						$"alpha {flicker.Alpha:F3}, global swing {flicker.GlobalSwing:P1}, " +
						$"per-probe p50 {flicker.P50:P1} p90 {flicker.P90:P1} p99 {flicker.P99:P1} " +
						$"max {flicker.MaxRelativeDelta:P0}, above 10% {flicker.ShareAbove10:P1} " +
						$"(mean lum {flicker.MeanLuminanceAvg:F4})");
				}

				// DECA_PROBE_TRACETEST=1 - сверить compute-обход BVH с CPU-эталоном (см.
				// SceneTraceVerifier). Отдельным тумблером: стоит компиляции шейдера и синхронной
				// вычитки с GPU, в обычном прогоне не нужно.
				if (Environment.GetEnvironmentVariable("DECA_PROBE_TRACETEST") == "1")
				{
					try
					{
						var report = SceneTraceVerifier.Run(env.DilApi, baker, fullMin, fullMax);
						Console.WriteLine($"[probe] trace verify: {report.RayCount} rays, " +
							$"{report.Mismatches} mismatches, worst rel error {report.WorstRelativeError:E2}, " +
							$"hits cpu={report.CpuHits} gpu={report.GpuHits}, " +
							$"nodes seen {report.ShaderNodeCount}/{report.UploadedNodeCount}");

						// Сверка целого раунда обновления проб - уровнем выше, чем отдельные лучи:
						// проверяет сборку SH, теневые лучи, счётчики и смешивание раундов.
						//
						// DECA_PROBE_VERIFYROUNDS=<n> - сколько раундов прогнать. Четыре по умолчанию
						// СМЕШИВАНИЕ НЕ ПРОВЕРЯЮТ: первые раунды разгонные (alpha = 1, предыдущее
						// поле не участвует вовсе, см. ProbeGiBaker.BootstrapRounds), так что баги
						// накопления начинают быть видны только дальше.
						int verifyRounds = int.TryParse(
							Environment.GetEnvironmentVariable("DECA_PROBE_VERIFYROUNDS"), out var vr) && vr > 0
							? vr
							: 4;
						var roundReport = SceneTraceVerifier.VerifyRound(env.DilApi, baker,
							fullMin, fullMax,
							Vector3.Normalize(-env.ShadowSettings.LightDirection),
							new Vector3(1f, 0.98f, 0.92f) * 2f,
							rounds: verifyRounds,
							environmentMap: env.EnvironmentMap,
							skyRadiance: env.EnvironmentRadiance,
							envYaw: env.ShadowSettings.EnvYawRadians);
						Console.WriteLine($"[probe] round verify: {roundReport.Probes} probes x " +
							$"{roundReport.Rounds} rounds, {roundReport.Mismatches}/" +
							$"{roundReport.SignificantProbes} significant mismatches, " +
							$"worst rel {roundReport.WorstRelativeError:E2}, " +
							$"worst abs {roundReport.WorstAbsoluteError:E2} vs mean |L0| " +
							$"{roundReport.MeanMagnitude:E2}, " +
							$"mean lum cpu={roundReport.CpuMeanLuminance:F4} gpu={roundReport.GpuMeanLuminance:F4}");
						Console.WriteLine($"[probe] round cost: cpu {roundReport.CpuMsPerRound:F2} ms, " +
							$"gpu {roundReport.GpuMsPerRound:F2} ms " +
							$"({roundReport.CpuMsPerRound / Math.Max(roundReport.GpuMsPerRound, 1e-3):F1}x)");
					}
					catch (Exception ex)
					{
						Console.WriteLine($"[probe] trace verify FAILED: {ex.Message}");
					}
				}
				// DECA_PROBE_GISAT=<0..1> - насыщенность цветного отскока (зеркало ручки
				// "Bounce saturation" окна Graphics) для A/B цветного бличинга без пересборки.
				var bakeOptions = new ProbeGiBakeOptions();
				if (float.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_GISAT"),
					System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
					out var bounceSat))
				{
					bakeOptions.BounceSaturation = bounceSat;
					Console.WriteLine($"[probe] bounce saturation: {bounceSat}");
				}

				// DECA_PROBE_GIMAX=<int> - бюджет проб (зеркало комбо "Max probes" окна Graphics).
				// На разреженной сетке им же проверяется раскладка кирпичей по уровням: чем плотнее
				// сетка, тем мельче кирпич относительно деталей сцены и тем есть что укрупнять.
				if (int.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_GIMAX"),
					out var maxProbes))
				{
					bakeOptions.MaxProbes = maxProbes;
					Console.WriteLine($"[probe] max probes: {maxProbes}");
				}

				// DECA_PROBE_GIDENSITY=<float> - плотность сетки (ячеек на габарит). Бюджет только
				// ограничивает её сверху, поэтому мельчить сетку надо именно этой ручкой.
				if (float.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_GIDENSITY"),
					System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
					out var gridDensity))
				{
					bakeOptions.GridDensity = gridDensity;
					Console.WriteLine($"[probe] grid density: {gridDensity}");
				}

				// DECA_PROBE_SURFCACHE=0 - собирать отскок по-старому, из поля проб, а не из кэша
				// радианса на поверхностях (см. SurfaceCache). Для A/B детализации баунса.
				if (Environment.GetEnvironmentVariable("DECA_PROBE_SURFCACHE") == "0")
				{
					bakeOptions.SurfaceCache = false;
					Console.WriteLine("[probe] surface cache: off");
				}

				// Сессия заводится явно (а не одним вызовом Bake) только чтобы отчитаться по кэшу
				// поверхностей до прогона - дальше тот же прогон до сходимости.
				var session = baker.BeginBake(fullMin, fullMax,
					Vector3.Normalize(-env.ShadowSettings.LightDirection),
					new Vector3(1f, 0.98f, 0.92f) * 2f,
					env.ShadowSettings.EnvYawRadians, env.EnvironmentRadiance, bakeOptions);
				// DECA_PROBE_GIGPU=1 - крутить раунды в compute вместо CPU, тем же путём, что и
				// редактор (см. ModelPreviewViewport.TryBeginProbeGpu). Атласы в этом режиме пишет
				// сам шейдер, поэтому ни Snapshot, ни заливки после прогона не требуется.
				ProbeRoundGpu? gpuRound = null;
				ProbeRoundPipelines? gpuPipelines = null;
				ProbeSceneAccel? gpuAccel = null;
				if (Environment.GetEnvironmentVariable("DECA_PROBE_GIGPU") == "1")
				{
					baker.EnsureSurfaceCache(session);
					ProbeTextures = new ProbeGiTextures(api, session.Result, "_probeGiCli",
						gpuWritable: true);
					ProbeTextures.Bind(model);
					// DECA_PROBE_GPUSTRESS=<n> - n раз пересоздать GPU-путь перед реальным прогоном.
					// Воспроизводит то, что делает редактор при каждом изменении настроек: без
					// освобождения предыдущего комплекта это утекало десятками мегабайт за правку
					// ползунка и заканчивалось рассинхроном и падением драйвера.
					// Конвейеры компилируются один раз и переживают пересоздание пути - ровно так же,
					// как в редакторе (см. ProbeRoundPipelines).
					// DECA_PROBE_GIHW=1 - аппаратная трассировка (RayQuery по BLAS/TLAS) вместо
					// программного обхода BVH. Требует inline-поддержки устройства.
					bool wantHardware = Environment.GetEnvironmentVariable("DECA_PROBE_GIHW") == "1";
					bool hardware = wantHardware && api.RayTracing >= RayTracingSupport.Inline;
					if (wantHardware && !hardware)
					{
						Console.WriteLine("[probe] hardware ray tracing requested but unsupported - software BVH");
					}

					if (hardware)
					{
						gpuAccel = new ProbeSceneAccel(env.DilApi, baker.InstancedGeometry);
						Console.WriteLine($"[probe] accel build: {gpuAccel.BuildMs} ms " +
							$"({gpuAccel.MeshCount} mesh BLAS over " +
							$"{baker.InstancedGeometry.TriangleCount} object-space triangles, " +
							$"{gpuAccel.InstanceCount} TLAS instances)");

						// Пересборка TLAS - цена движения объекта; в редакторе она платится каждый
						// раз, когда инстанс сменил позу (см. ModelPreviewViewport.PollProbeAccel),
						// поэтому меряем её здесь же, на тех же самых позах.
						var poses = new Matrix4x4[gpuAccel.InstanceCount];
						for (int i = 0; i < poses.Length; i++)
						{
							poses[i] = baker.InstancedGeometry.Instances[i].Transform;
						}

						gpuAccel.Rebuild(poses);
						env.DilApi.ImmediateContext.Flush();
						env.DilApi.ImmediateContext.WaitForIdle();
						Console.WriteLine($"[probe] accel tlas rebuild: {gpuAccel.RebuildMs} ms");
					}

					var swPipelines = System.Diagnostics.Stopwatch.StartNew();
					gpuPipelines = new ProbeRoundPipelines(env.DilApi, hardware);
					Console.WriteLine($"[probe] gpu pipelines compiled: {swPipelines.ElapsedMilliseconds} ms " +
						$"(один раз на устройство, трассировка: {(hardware ? "аппаратная" : "программная")})");

					if (int.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_GPUSTRESS"),
						out var stress) && stress > 0)
					{
						// Пересоздаётся ВЕСЬ комплект, как при повторной запечке в редакторе: атласы,
						// структуры ускорения, конвейеры и сам раунд. Прежний вариант гонял только
						// ProbeRoundGpu и потому не ловил утечки соседних объектов.
						for (int i = 0; i < stress; i++)
						{
							// Пересоздаётся ровно то, что пересоздаёт редактор при смене настроек:
							// атласы и сам раунд. Структуры ускорения и конвейеры к сессии не
							// привязаны и переживают цикл (см. ModelPreviewViewport._probeAccel) -
							// пересоздание BLAS на каждую правку ползунка и было тем, что упиралось
							// в исчерпание ресурсов на третьей запечке.
							var cycleAtlases = new ProbeGiTextures(env.DilApi, session.Result,
								$"_probeGiStress{i}", gpuWritable: true);

							var probe = new ProbeRoundGpu(env.DilApi, gpuPipelines, session, baker,
								cycleAtlases, env.EnvironmentMap, env.ShadowSettings.EnvYawRadians,
								gpuAccel);
							while (!probe.RunRound(session, baker,
								ProbeGiBaker.RoundRayDirections(session.RaysPerRound, session.Sequence),
								ProbeGiBaker.RoundBlendWeight(session)))
							{
							}

							env.DilApi.ImmediateContext.Flush();
							env.DilApi.ImmediateContext.WaitForIdle();

							// Порядок обязателен: раунд держит привязки на атласы.
							probe.Dispose();
							cycleAtlases.Release();

							// Diligent освобождает ресурсы ОТЛОЖЕННО, по завершении кадра: без
							// Present очередь освобождения не разбирается, дескрипторы не
							// перерабатываются, и цикл упирается в исчерпание там, где редактор
							// (который презентует каждый кадр) прошёл бы спокойно.
							api.Present();
						}

						Console.WriteLine($"[probe] gpu stress: {stress} recreate cycles survived");
					}

					// Подъём GPU-пути идёт СИНХРОННО на потоке рендера (в редакторе - прямо в
					// Update): захват поверхностей, выгрузка BVH в массивы на десятки мегабайт,
					// создание буферов и компиляция шейдеров. Меряем отдельно - длинный стопор
					// между кадрами ломает кадровый цикл swap chain, а это ровно то сообщение
					// "Timeout elapsed while waiting for the frame waitable object", которое
					// предшествовало падению редактора.
					var swGpuSetup = System.Diagnostics.Stopwatch.StartNew();
					gpuRound = new ProbeRoundGpu(env.DilApi, gpuPipelines, session, baker,
						ProbeTextures, env.EnvironmentMap, env.ShadowSettings.EnvYawRadians, gpuAccel);
					var t = gpuRound.SetupTiming;
					Console.WriteLine($"[probe] gpu path setup: {swGpuSetup.ElapsedMilliseconds} ms " +
						$"(surface capture {t.SurfaceCapture}, bvh export {t.BvhExport}, " +
						$"buffers {t.Buffers}, shaders {t.Shaders})");

					// Отдаём длинному прогону ниже - он гоняет раунды по одному на кадр, как редактор.
					_gpuRoundLongRun = gpuRound;
					_gpuSessionLongRun = session;
					_gpuBakerLongRun = baker;
					Console.WriteLine("[probe] probe-gi rounds: GPU (compute)");

					// DECA_PROBE_CASCADES=<n> (2..3) - мелкие каскады, как во вьюпорте: бокс в 2^i
					// раз меньше вокруг центра той же плотностью, атласы в слоты _C1/_C2. Раунды
					// докручиваются до сходимости здесь же - смоуку нужен готовый результат в кадре.
					if (int.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_CASCADES"),
							out var cascadeCount) && cascadeCount > 1)
					{
						var cascadeCenter = (fullMin + fullMax) * 0.5f;
						var cascadeHalf = (fullMax - fullMin) * 0.5f;
						for (int c = 1; c < Math.Min(cascadeCount, 3); c++)
						{
							var half = cascadeHalf / (1 << c);
							var cascadeSession = baker.BeginBake(cascadeCenter - half,
								cascadeCenter + half,
								Vector3.Normalize(-env.ShadowSettings.LightDirection),
								new Vector3(1f, 0.98f, 0.92f) * 2f,
								env.ShadowSettings.EnvYawRadians, env.EnvironmentRadiance,
								bakeOptions);
							baker.EnsureSurfaceCache(cascadeSession);

							var cascadeAtlases = new ProbeGiTextures(api, cascadeSession.Result,
								$"_probeGiCliC{c}", gpuWritable: true);
							cascadeAtlases.Bind(model, $"_C{c}");
							CascadeTextures.Add(cascadeAtlases);

							var cascadeGpu = new ProbeRoundGpu(env.DilApi, gpuPipelines!,
								cascadeSession, baker, cascadeAtlases, env.EnvironmentMap,
								env.ShadowSettings.EnvYawRadians, gpuAccel);
							while (!cascadeSession.NoGeometry
								&& cascadeSession.Round < cascadeSession.TargetRounds)
							{
								if (cascadeGpu.RunRound(cascadeSession, baker,
										ProbeGiBaker.RoundRayDirections(cascadeSession.RaysPerRound,
											cascadeSession.Sequence),
										ProbeGiBaker.RoundBlendWeight(cascadeSession)))
								{
									cascadeSession.AdvanceRound();
								}
							}

							env.DilApi.ImmediateContext.Flush();
							env.DilApi.ImmediateContext.WaitForIdle();
							Console.WriteLine($"[probe] cascade {c}: " +
								$"{cascadeSession.CountX}x{cascadeSession.CountY}x{cascadeSession.CountZ} " +
								$"virtual grid, {cascadeSession.ProbeCount} probes, cell " +
								$"{cascadeSession.Cell.X:F2}");
							// Раунд держит привязки на атласы - жить им до конца прогона, Dispose
							// только раунду.
							cascadeGpu.Dispose();
						}
					}

					// DECA_PROBE_DEBUGOVERLAY=1 - смоук дебаг-вида проб (см. ProbeDebugOverlay):
					// компиляция его шейдеров и PSO плюс отрисовка в кадры прогона. Единственный
					// headless-способ поймать ошибку шейдера/раскладки до запуска редактора.
					if (Environment.GetEnvironmentVariable("DECA_PROBE_DEBUGOVERLAY") == "1")
					{
						var overlay = new ProbeDebugOverlay(env.DilApi, api, env.BatchRenderer,
							session, ProbeTextures!, env.MsaaSamples,
							env.Pipeline.Targets?.RenderColorFormat
								?? TextureObjectFormat.R8G8B8A8UNorm);
						env.Pipeline.InlineOverlay = overlay.Draw;
						env.Pipeline.InvalidateGraph();
						Console.WriteLine(
							$"[probe] debug overlay: {session.ProbeCount} probes, shaders compiled");
					}
				}

				// Чистое время счёта до сходимости, БЕЗ покадровой выдачи: порции идут одна за другой,
				// как быстро успевает GPU. Это нижняя граница - в редакторе к ней добавляется
				// растягивание по кадрам, и разница между этими двумя числами показывает, что
				// упирается в счёт, а что в темп выдачи.
				var swConverge = System.Diagnostics.Stopwatch.StartNew();
				int chunkCount = 0;

				// Условие явное, а не Converged: в режиме реального времени сходимости нет по
				// определению (см. ProbeGiBakeOptions.Realtime), и headless-прогон, которому нечего
				// показывать между раундами, повис бы здесь навсегда.
				while (!session.NoGeometry && session.Round < session.TargetRounds)
				{
					if (gpuRound != null)
					{
						// Раунд идёт порциями - headless-у ждать между кадрами нечего, докручиваем сразу.
						while (!gpuRound.RunRound(session, baker,
							ProbeGiBaker.RoundRayDirections(session.RaysPerRound, session.Sequence),
							ProbeGiBaker.RoundBlendWeight(session)))
						{
							chunkCount++;
						}

						chunkCount++;
						session.AdvanceRound();
					}
					else
					{
						baker.RunRound(session);
					}
				}

				if (gpuRound != null)
				{
					env.DilApi.ImmediateContext.Flush();
					env.DilApi.ImmediateContext.WaitForIdle();
					Console.WriteLine($"[probe] gpu converge: {swConverge.ElapsedMilliseconds} ms, " +
						$"{session.TargetRounds} rounds, {chunkCount} dispatches " +
						$"({swConverge.Elapsed.TotalMilliseconds / Math.Max(chunkCount, 1):F2} ms/dispatch)");
				}

				// Кэш строится первым раундом (см. ProbeGiBakeSession.WantsSurfaceCache), поэтому
				// отчитываемся о нём после прогона.
				if (session.Surface != null)
				{
					var surface = session.Surface;
					long grid = (long)surface.CountX * surface.CountY * surface.CountZ;
					Console.WriteLine($"[probe] surface cache: {surface.VoxelCount} voxels over {grid} cells " +
						$"({100.0 * surface.VoxelCount / Math.Max(grid, 1):F1}% filled), " +
						$"voxel {surface.Voxel.X:F2}");
				}

				// В GPU-режиме атласы уже созданы и заполнены шейдером - Snapshot нужен только
				// как источник цифр для отчёта ниже.
				var bake = baker.Snapshot(session);
				if (gpuRound == null)
				{
					ProbeTextures = new ProbeGiTextures(api, bake, "_probeGiCli");
					ProbeTextures.Bind(model);
				}
				// Кирпичи - мера разреженности: сколько ячеек сетки реально запечено против того,
				// сколько заняла бы плотная сетка того же шага (см. ProbeGiBaker.ClassifyBricks).
				int brickGrid = bake.BrickCountX * bake.BrickCountY * bake.BrickCountZ;
				int probesPerBrick = ProbeGiBaker.BrickProbes * ProbeGiBaker.BrickProbes * ProbeGiBaker.BrickProbes;
				Console.WriteLine($"[probe] probe-gi: {bake.CountX}x{bake.CountY}x{bake.CountZ} virtual grid, " +
					$"{bake.BrickTotal} bricks over {brickGrid} cells, " +
					$"levels [{string.Join('/', bake.BricksPerLevel)}], " +
					$"{bake.BrickTotal * probesPerBrick} probes baked in {sw.ElapsedMilliseconds} ms");

				// Санити-статистика испечённого поля прямо из атласов (альфы Sh0/Sh1/Sh2 = sky
				// visibility / валидность / солнечная доля) - чтобы отличать баги бейка от багов
				// GPU-пути (привязка/сэмплинг/кбуфер).
				static (float avg, float max) HalfAlphaStats(byte[] atlas)
				{
					float sum = 0f, max = 0f;
					int count = atlas.Length / 8;
					for (int i = 0; i < count; i++)
					{
						float v = (float)BitConverter.UInt16BitsToHalf(
							(ushort)(atlas[i * 8 + 6] | (atlas[i * 8 + 7] << 8)));
						sum += v;
						max = MathF.Max(max, v);
					}
					return (count > 0 ? sum / count : 0f, max);
				}

				var skyStats = HalfAlphaStats(bake.Sh0);
				var validityStats = HalfAlphaStats(bake.Sh1);
				var sunStats = HalfAlphaStats(bake.Sh2);
				Console.WriteLine($"[probe] probe-gi field: skyVis avg={skyStats.avg:F3} max={skyStats.max:F3}, " +
					$"validity avg={validityStats.avg:F3}, sunFrac avg={sunStats.avg:F3} max={sunStats.max:F3}");
			}
			else
			{
				Console.WriteLine("[probe] probe-gi: no geometry to trace, skipped");
			}
		}

		float time = 0f;

		// NB: прежние стадии lighting_glass/debug_transmission (принудительный transmission в
		// рантайме) удалены: с shader keywords код стекла существует только в вариантах материалов,
		// у которых KHR_materials_transmission авторский - рантайм-форс на непрозрачном материале
		// теперь по определению no-op. Стекло проверяется на ассетах с настоящим transmission
		// (DragonDispersion/ABeautifulGame).
		foreach (var (mode, channel, name) in new[] { (3, 0, "lighting"), (3, 0, "lighting_flat"), (3, 8, "debug_ambient"), (3, 7, "debug_direct"), (3, 6, "debug_envspec"), (3, 9, "debug_probes"), (1, 0, "highlight"), (2, 0, "channel_normal") })
		{
			// "lighting_flat": тот же Lighting-режим, но с принудительно белым нетекстурированным
			// диэлектриком - чистый отклик источников света без влияния альбедо/MR-текстур, чтобы
			// отличать "свет не работает" от "тёмные текстуры съедают контраст".
			PushPreviewSettings(model, mode, channel, forceFlatWhite: name != "lighting");

			// Отладочные стадии пишут уже отображаемые значения - тонемап их обязан пропустить как
			// есть (зеркало ModelPreviewViewport.ApplyPreviewSettingsToMaterials).
			env.SetTonemapPassthrough(mode != 3 || channel != 0
				|| Environment.GetEnvironmentVariable("DECA_PROBE_AO_DEBUG") == "1");

			env.DilApi.ImmediateContext.Flush();
			env.DilApi.ImmediateContext.WaitForIdle();
			env.Pipeline.InvalidateGraph();

			for (int frame = 0; frame < 3; frame++)
			{
				time += 1f / 60f;
				env.SetEyeAdaptationDeltaTime(1f / 60f);
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

		// DECA_PROBE_PUNCTUALDEBUG=1 - диагностический канал 11 (см. UnlitInstancedPS.hlsl):
		// визуализация punctual-теневого сэмплинга per-pixel - магента (ветка не выполнилась вовсе:
		// нет назначенного слайса или атен. вклад <= 0), циан (точка приёмника вне UV слайса),
		// оранжевый (UV в допуске, но за дальней плоскостью слайса), градация серого (реальный
		// shadowLit сэмплера: 0 чёрный = окклюдер найден, 1 белый = свет). Числовая раскладка долей
		// пикселей по категориям даёт то, что просто картинка не покажет однозначно: сколько экрана
		// вообще ДОШЛО до сэмплера и с каким результатом.
		if (Environment.GetEnvironmentVariable("DECA_PROBE_PUNCTUALDEBUG") == "1")
		{
			PushPreviewSettings(model, 3, 11, forceFlatWhite: false);
			env.SetTonemapPassthrough(true);
			env.DilApi.ImmediateContext.Flush();
			env.DilApi.ImmediateContext.WaitForIdle();
			env.Pipeline.InvalidateGraph();

			for (int frame = 0; frame < 3; frame++)
			{
				time += 1f / 60f;
				env.SetEyeAdaptationDeltaTime(1f / 60f);
				env.Root.Update(new UpdateTick(1f / 60f, time));
				env.Pipeline.Execute();
			}

			env.DilApi.ImmediateContext.Flush();
			env.DilApi.ImmediateContext.WaitForIdle();

			var dbgPixels = DiligentTextureReadback.ReadRgba8(env.DilApi, (DiligentRenderTarget)env.ColorTarget,
				out var dbgW, out var dbgH);

			// Раскладка цветов - зеркало кодирования канала 11 в UnlitInstancedPS.hlsl:
			// магента (1,0,1) - ветка не выполнилась; циан (0,1,1) - UV вне [0,1]; оранжевый
			// (1,0.5,0) - UV в допуске, но ndc.z >= 1.0 (за дальней плоскостью); иначе - серое
			// (r=g=b) с реальным shadowLit сэмплера.
			long noBranch = 0, uvOut = 0, zOut = 0, sampled = 0, litSum = 0;
			for (int i = 0; i < dbgPixels.Length; i += 4)
			{
				byte r = dbgPixels[i], g = dbgPixels[i + 1], b = dbgPixels[i + 2];
				if (r > 200 && g < 50 && b > 200) noBranch++;
				else if (r < 50 && g > 200 && b > 200) uvOut++;
				else if (r > 200 && g is > 100 and < 180 && b < 50) zOut++;
				else { sampled++; litSum += r; }
			}

			long totalPx = dbgW * (long)dbgH;
			Console.WriteLine($"[probe] punctual shadow debug (channel 11): no-branch={100.0 * noBranch / totalPx:F1}% " +
				$"uv-out={100.0 * uvOut / totalPx:F1}% z-out(far plane)={100.0 * zOut / totalPx:F1}% " +
				$"sampled={100.0 * sampled / totalPx:F1}% " +
				$"(sampled avg shadowLit={(sampled > 0 ? litSum / (double)sampled / 255.0 : -1):F3}, " +
				"0=занавешено окклюдером, 1=не найден)");

			PngWriter.Write(Path.Combine(outDir, "probe_punctualdebug.png"), dbgPixels, dbgW, dbgH);

			// Пересъёмка debug_direct СРАЗУ вслед, тем же временем/состоянием, что и канал 11 выше -
			// сверка "точки, которые канал 11 считает shadowLit=0 (чёрные), действительно тёмные и в
			// direct" или расхождение между двумя кадрами говорит о нестабильности per-frame раскладки
			// теневых слайсов (PunctualShadowScheduler читает бюджет заново каждый кадр).
			PushPreviewSettings(model, 3, 7, forceFlatWhite: false);
			env.Pipeline.InvalidateGraph();
			for (int frame = 0; frame < 3; frame++)
			{
				time += 1f / 60f;
				env.SetEyeAdaptationDeltaTime(1f / 60f);
				env.Root.Update(new UpdateTick(1f / 60f, time));
				env.Pipeline.Execute();
			}
			env.DilApi.ImmediateContext.Flush();
			env.DilApi.ImmediateContext.WaitForIdle();
			var recheckPixels = DiligentTextureReadback.ReadRgba8(env.DilApi, (DiligentRenderTarget)env.ColorTarget,
				out var rw, out var rh);
			ReportStats("debug_direct_recheck", recheckPixels, rw, rh);
			PngWriter.Write(Path.Combine(outDir, "probe_debug_direct_recheck.png"), recheckPixels, rw, rh);

			// Канал 12 (см. UnlitInstancedPS.hlsl) - какой ИНДЕКС слайса шейдер РЕАЛЬНО выбрал за
			// пиксель, закодирован как r=g=b=slice*16 (магента = ветка не выполнилась). Отвечает на
			// вопрос, который канал 11 сам по себе не решает: белый/"лит" пиксель канала 11 может
			// значить и "слайс верный, окклюдера в кадре нет", и "слайс вообще не тот - сэмплится
			// пустая/чужая карта". Сверяется с DECA_PROBE_SHADOWDUMP=3 (какие слайсы РЕАЛЬНО содержат
			// геометрию) - расхождение "слайс без геометрии в дампе, но часто встречается тут" прямо
			// указывает на несовпадение записи/сэмплинга индекса.
			PushPreviewSettings(model, 3, 12, forceFlatWhite: false);
			env.SetTonemapPassthrough(true);
			env.Pipeline.InvalidateGraph();
			for (int frame = 0; frame < 3; frame++)
			{
				time += 1f / 60f;
				env.SetEyeAdaptationDeltaTime(1f / 60f);
				env.Root.Update(new UpdateTick(1f / 60f, time));
				env.Pipeline.Execute();
			}
			env.DilApi.ImmediateContext.Flush();
			env.DilApi.ImmediateContext.WaitForIdle();
			var slicePixels = DiligentTextureReadback.ReadRgba8(env.DilApi, (DiligentRenderTarget)env.ColorTarget,
				out var sw, out var sh);

			var sliceHistogram = new long[16];
			long sliceNoBranch = 0;
			for (int i = 0; i < slicePixels.Length; i += 4)
			{
				byte r = slicePixels[i], g = slicePixels[i + 1], b = slicePixels[i + 2];
				if (r > 200 && g < 50 && b > 200) { sliceNoBranch++; continue; }
				int slice = (int)Math.Round(r / 16.0);
				if (slice is >= 0 and < 16) sliceHistogram[slice]++;
			}
			long sliceTotalPx = sw * (long)sh;
			var histStr = string.Join(", ", sliceHistogram.Select((count, idx) => $"{idx}:{100.0 * count / sliceTotalPx:F1}%")
				.Where((_, idx) => sliceHistogram[idx] > 0));
			Console.WriteLine($"[probe] punctual shadow slice index (channel 12): no-branch={100.0 * sliceNoBranch / sliceTotalPx:F1}% " +
				$"per-slice pixel share: {(histStr.Length > 0 ? histStr : "(none sampled)")}");
			PngWriter.Write(Path.Combine(outDir, "probe_punctualslice.png"), slicePixels, sw, sh);

			// Канал 13 - punctual.ShadowParams.x (БАЗОВЫЙ слайс, назначенный LightCulling, ДО
			// смещения грани куба) - та же кодировка/декод, что канал 12. Изолирует источник
			// расхождения: если БАЗА уже неверна - баг в LightCulling/assignments, если база верна
			// (0), а итоговый слайс (канал 12) - нет, баг в выборе грани точечного света в PS.
			PushPreviewSettings(model, 3, 13, forceFlatWhite: false);
			env.SetTonemapPassthrough(true);
			env.Pipeline.InvalidateGraph();
			for (int frame = 0; frame < 3; frame++)
			{
				time += 1f / 60f;
				env.SetEyeAdaptationDeltaTime(1f / 60f);
				env.Root.Update(new UpdateTick(1f / 60f, time));
				env.Pipeline.Execute();
			}
			env.DilApi.ImmediateContext.Flush();
			env.DilApi.ImmediateContext.WaitForIdle();
			var basePixels = DiligentTextureReadback.ReadRgba8(env.DilApi, (DiligentRenderTarget)env.ColorTarget,
				out var bw, out var bh);

			var baseHistogram = new long[16];
			long baseNoBranch = 0;
			for (int i = 0; i < basePixels.Length; i += 4)
			{
				byte r = basePixels[i], g = basePixels[i + 1], b = basePixels[i + 2];
				if (r > 200 && g < 50 && b > 200) { baseNoBranch++; continue; }
				int slice = (int)Math.Round(r / 16.0);
				if (slice is >= 0 and < 16) baseHistogram[slice]++;
			}
			long baseTotalPx = bw * (long)bh;
			var baseHistStr = string.Join(", ", baseHistogram.Select((count, idx) => $"{idx}:{100.0 * count / baseTotalPx:F1}%")
				.Where((_, idx) => baseHistogram[idx] > 0));
			Console.WriteLine($"[probe] punctual shadow BASE slice, pre-face-offset (channel 13): no-branch={100.0 * baseNoBranch / baseTotalPx:F1}% " +
				$"per-slice pixel share: {(baseHistStr.Length > 0 ? baseHistStr : "(none sampled)")}");
			PngWriter.Write(Path.Combine(outDir, "probe_punctualbase.png"), basePixels, bw, bh);

			// Канал 14 - сырое ClusterCounts[clusterIdx] (до клампа CLUSTER_MAX_LIGHTS), та же
			// кодировка. При единственном свете в сцене ОБЯЗАН быть 0 (ветка не вошла, кластер без
			// punctual-светов) либо 1 (наш единственный свет) ПОВСЮДУ, где punctualCount > 0.
			PushPreviewSettings(model, 3, 14, forceFlatWhite: false);
			env.SetTonemapPassthrough(true);
			env.Pipeline.InvalidateGraph();
			for (int frame = 0; frame < 3; frame++)
			{
				time += 1f / 60f;
				env.SetEyeAdaptationDeltaTime(1f / 60f);
				env.Root.Update(new UpdateTick(1f / 60f, time));
				env.Pipeline.Execute();
			}
			env.DilApi.ImmediateContext.Flush();
			env.DilApi.ImmediateContext.WaitForIdle();
			var countPixels = DiligentTextureReadback.ReadRgba8(env.DilApi, (DiligentRenderTarget)env.ColorTarget,
				out var cw, out var ch);

			var countHistogram = new long[16];
			long countNoBranch = 0;
			for (int i = 0; i < countPixels.Length; i += 4)
			{
				byte r = countPixels[i], g = countPixels[i + 1], b = countPixels[i + 2];
				if (r > 200 && g < 50 && b > 200) { countNoBranch++; continue; }
				int cnt = (int)Math.Round(r / 16.0);
				if (cnt is >= 0 and < 16) countHistogram[cnt]++;
			}
			long countTotalPx = cw * (long)ch;
			var countHistStr = string.Join(", ", countHistogram.Select((count, idx) => $"{idx}:{100.0 * count / countTotalPx:F1}%")
				.Where((_, idx) => countHistogram[idx] > 0));
			Console.WriteLine($"[probe] punctual cluster raw light count (channel 14): no-branch(punctualCount==0)={100.0 * countNoBranch / countTotalPx:F1}% " +
				$"per-count pixel share: {(countHistStr.Length > 0 ? countHistStr : "(none)")}");
			PngWriter.Write(Path.Combine(outDir, "probe_punctualcount.png"), countPixels, cw, ch);

			env.SetTonemapPassthrough(false);
		}

		// DECA_PROBE_SHARPNESS=1 - числовой замер детализации кадра: 48 кадров неподвижной камеры
		// (темпоральному аккумулятору надо сойтись - 16-фазный джиттер плюс запас на вес 0.1), затем
		// средний градиент яркости по кадру. Сам по себе ни о чём - смысл в СРАВНЕНИИ прогонов:
		//   полное разрешение          -> эталон;
		//   RENDERSCALE=0.5            -> билинейный апскейл, градиент проседает;
		//   RENDERSCALE=0.5 + TAAU     -> обязан вернуть заметную часть просадки.
		// Если TAAU не резче билинейного - он не собирает джиттер (мёртвая история, неверный знак
		// анти-джиттера или вектора).
		if (Environment.GetEnvironmentVariable("DECA_PROBE_SHARPNESS") == "1")
		{
			// Вернуть Lighting-режим: цикл шотов выше заканчивается на channel_normal - отладочном
			// виде, который ТЕКСТУР НЕ СЭМПЛИРУЕТ, и замер на нём слеп к mip bias/анизотропии/деталям.
			// (Найдено больно: три конфигурации сэмплеров дали бит-в-бит одинаковые кадры.)
			PushPreviewSettings(model, 3, 0, forceFlatWhite: false);
			env.SetTonemapPassthrough(false);
			env.DilApi.ImmediateContext.Flush();
			env.DilApi.ImmediateContext.WaitForIdle();
			env.Pipeline.InvalidateGraph();

			// DECA_PROBE_SHARPMOVE=1 - первые 40 кадров камера МЕДЛЕННО орбитит (0.15 град/кадр),
			// последние 8 - неподвижна (аккумулятору дать стабилизироваться перед замером).
			// Диагностика дегенерации темпоральных эвристик на ИДЕАЛЬНОЙ статике (locks/luma
			// instability FSR 3.1.x): игровой сцены без движения не бывает, а пробник неподвижен.
			var sharpMove = Environment.GetEnvironmentVariable("DECA_PROBE_SHARPMOVE") == "1";

			for (int frame = 0; frame < 48; frame++)
			{
				if (sharpMove && frame < 40)
				{
					var orbitYaw = 0.15f * MathF.PI / 180f * frame;
					env.SetCameraTransform(eye,
						eye + Vector3.Transform(target - eye, Matrix4x4.CreateRotationY(orbitYaw)));
				}

				time += 1f / 60f;
				env.SetEyeAdaptationDeltaTime(1f / 60f);
				env.Root.Update(new UpdateTick(1f / 60f, time));
				env.Pipeline.Execute();
			}

			env.DilApi.ImmediateContext.Flush();
			env.DilApi.ImmediateContext.WaitForIdle();

			var sharpPixels = DiligentTextureReadback.ReadRgba8(env.DilApi,
				(DiligentRenderTarget)env.ColorTarget, out var sw, out var sh);

			double gradSum = 0;
			long gradCount = 0;
			for (int y = 1; y < sh - 1; y++)
			{
				for (int x = 1; x < sw - 1; x++)
				{
					int i = (y * sw + x) * 4;
					float lum = 0.299f * sharpPixels[i] + 0.587f * sharpPixels[i + 1] + 0.114f * sharpPixels[i + 2];
					int ir = i + 4;
					int id = i + sw * 4;
					float lumR = 0.299f * sharpPixels[ir] + 0.587f * sharpPixels[ir + 1] + 0.114f * sharpPixels[ir + 2];
					float lumD = 0.299f * sharpPixels[id] + 0.587f * sharpPixels[id + 1] + 0.114f * sharpPixels[id + 2];
					gradSum += MathF.Abs(lumR - lum) + MathF.Abs(lumD - lum);
					gradCount++;
				}
			}

			Console.WriteLine($"[probe] sharpness: mean |grad|={gradSum / Math.Max(1, gradCount):F3} " +
				$"({sw}x{sh}, 48 кадров сведения)");
			PngWriter.Write(Path.Combine(outDir, "probe_sharpness.png"), sharpPixels, sw, sh);
		}

		// DECA_PROBE_MOTIONSHIFT=<градусы> - ПОЛОЖИТЕЛЬНЫЙ контроль векторов движения (см.
		// MotionVectorPass). Ровно серый кадр на неподвижной камере доказывает мало: такой же дал бы и
		// вовсе не привязанный буфер. Здесь камера доворачивается на заданный угол, и проверка идёт по
		// САМОМУ ОПРЕДЕЛЕНИЮ вектора (prevUV = curUV + motion): кадр A, пересобранный по векторам,
		// обязан совпасть с кадром B заметно лучше, чем несдвинутый кадр A. Такой тест ловит разом и
		// масштаб, и знак, и переворот Y - и не требует ни одной формулы про матрицы проекции, то есть
		// не может ошибиться теми же соглашениями, что и проверяемый код.
		if (Environment.GetEnvironmentVariable("DECA_PROBE_MOTION") == "1"
		    && float.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_MOTIONSHIFT"),
			    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
			    out var motionYawDeg)
		    && MathF.Abs(motionYawDeg) > 1e-4f)
		{
			float motionRangePx = float.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_MOTIONRANGE"),
				System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
				out var parsedRange) && parsedRange > 0f
				? parsedRange
				: MotionVectorDebugPassResources.DefaultRangePixels;

			// Камера B - поворот ВЗГЛЯДА вокруг вертикали, без переноса. Чистое вращение сдвигает весь
			// кадр примерно одинаково, включая небо, поэтому тест не зависит ни от глубины сцены, ни от
			// того, куда именно смотрит камера при данном кадрировании.
			var targetB = eye + Vector3.Transform(target - eye,
				Matrix4x4.CreateRotationY(motionYawDeg * MathF.PI / 180f));

			void RenderFrames(int count)
			{
				for (int frame = 0; frame < count; frame++)
				{
					time += 1f / 60f;
					env.SetEyeAdaptationDeltaTime(1f / 60f);
					env.Root.Update(new UpdateTick(1f / 60f, time));
					env.Pipeline.Execute();
				}

				env.DilApi.ImmediateContext.Flush();
				env.DilApi.ImmediateContext.WaitForIdle();
			}

			// Порядок A -> B -> A -> B. Последний шаг ПОВТОРЯЕТ ту же пару кадров, что и второй, а
			// значит и ту же матрицу репроджекции - только теперь отладочный пасс рисует её в кадр.
			// Иначе включение отладки стоило бы лишнего Execute, а тот защёлкнул бы B поверх B и
			// обнулил бы ровно те векторы, которые мы собрались измерять.
			env.SetMotionVectorDebug(false, motionRangePx);

			env.SetCameraTransform(eye, target);
			RenderFrames(3);

			env.SetCameraTransform(eye, targetB);
			RenderFrames(1);
			var imageB = DiligentTextureReadback.ReadRgba8(env.DilApi,
				(DiligentRenderTarget)env.ColorTarget, out var mw, out var mh);

			env.SetCameraTransform(eye, target);
			RenderFrames(1);
			var imageA = DiligentTextureReadback.ReadRgba8(env.DilApi,
				(DiligentRenderTarget)env.ColorTarget, out _, out _);

			env.SetMotionVectorDebug(true, motionRangePx);
			env.SetCameraTransform(eye, targetB);
			RenderFrames(1);
			var motionRgba = DiligentTextureReadback.ReadRgba8(env.DilApi,
				(DiligentRenderTarget)env.ColorTarget, out _, out _);
			env.SetMotionVectorDebug(false, motionRangePx);

			static float Lum(byte[] px, int i) => 0.299f * px[i] + 0.587f * px[i + 1] + 0.114f * px[i + 2];

			double naiveSad = 0, warpedSad = 0, magnitudeSum = 0;
			int used = 0, clipped = 0;

			// Поля кадра выкидываем: у края сдвиг уводит выборку за границу, и там сравнивать нечего -
			// ровно та же дисокклюзия, с которой в бою разбирается сам апскейлер.
			const int margin = 24;
			for (int y = margin; y < mh - margin; y++)
			{
				for (int x = margin; x < mw - margin; x++)
				{
					int i = (y * mw + x) * 4;

					// Синий канал гасится шейдером ровно там, где вектор ушёл за диапазон шкалы: такие
					// пиксели декодировались бы обрезанными и занижали бы сдвиг.
					if (motionRgba[i + 2] < 64)
					{
						clipped++;
						continue;
					}

					// Шкала отладочного вида - в пикселях РЕНДЕР-разрешения (см. MotionVectorDebugPS),
					// а warp идёт по display-кадрам: при масштабе рендера домножаем на отношение
					// размеров (1 при выключенном масштабе).
					float mx = (motionRgba[i] / 255f * 2f - 1f) * motionRangePx
						* (mw / env.DepthTarget.Size.X);
					float my = (motionRgba[i + 1] / 255f * 2f - 1f) * motionRangePx
						* (mh / env.DepthTarget.Size.Y);

					int sx = (int)MathF.Round(x + mx);
					int sy = (int)MathF.Round(y + my);
					if (sx < 0 || sy < 0 || sx >= mw || sy >= mh)
					{
						continue;
					}

					float lumB = Lum(imageB, i);
					naiveSad += MathF.Abs(lumB - Lum(imageA, i));
					warpedSad += MathF.Abs(lumB - Lum(imageA, (sy * mw + sx) * 4));
					magnitudeSum += MathF.Sqrt(mx * mx + my * my);
					used++;
				}
			}

			PngWriter.Write(Path.Combine(outDir, "probe_motion_debug.png"), motionRgba, mw, mh);

			if (used > 0)
			{
				Console.WriteLine($"[probe] motion shift: yaw={motionYawDeg:F2} deg, range={motionRangePx:F1} px, " +
					$"sampled={used}, clipped={clipped}, |motion| avg={magnitudeSum / used:F2} px");
				Console.WriteLine($"[probe] motion warp: SAD naive={naiveSad / used:F2} -> warped={warpedSad / used:F2} " +
					$"({(naiveSad > 0 ? (1.0 - warpedSad / naiveSad) * 100.0 : 0.0):F1}% лучше; " +
					"отрицательное значение = вектор указывает НЕ ТУДА)");
			}
			else
			{
				Console.WriteLine("[probe] motion shift: не набралось ни одного пригодного пикселя " +
					$"(clipped={clipped}) - подними DECA_PROBE_MOTIONRANGE");
			}
		}

		// DECA_PROBE_JITTER=1 - тройная проверка суб-пиксельного джиттера проекции (см.
		// GraphicsPipelineSimple.ApplyTemporalJitter):
		//   1) отрицательный контроль: без джиттера два подряд кадра неподвижной камеры обязаны
		//      совпасть БИТ-В-БИТ (детерминизм рендера - предпосылка теста 2);
		//   2) с джиттером те же два кадра обязаны РАЗОЙТИСЬ: смещение проекции реально доехало до
		//      GPU, фазы Halton меняются от кадра к кадру;
		//   3) при включённых векторах движения (DECA_PROBE_MOTION=1) отладочный кадр векторов под
		//      джиттером обязан остаться РОВНО серым: векторы защёлкиваются по матрице ДО джиттера,
		//      и протечка дрожания в них - ровно та ошибка, из-за которой апскейлер видел бы
		//      суб-пиксельное "движение" на неподвижной сцене и никогда не сходился бы в резкость.
		// Тест 3 осмыслен только потому, что живость тракта векторов уже доказана положительным
		// контролем DECA_PROBE_MOTIONSHIFT: серый кадр от неподключённого буфера здесь исключён.
		if (Environment.GetEnvironmentVariable("DECA_PROBE_JITTER") == "1")
		{
			void RenderOne()
			{
				time += 1f / 60f;
				env.SetEyeAdaptationDeltaTime(1f / 60f);
				env.Root.Update(new UpdateTick(1f / 60f, time));
				env.Pipeline.Execute();
				env.DilApi.ImmediateContext.Flush();
				env.DilApi.ImmediateContext.WaitForIdle();
			}

			double Sad(byte[] a, byte[] b)
			{
				double sum = 0;
				for (int i = 0; i < a.Length; i++)
				{
					sum += Math.Abs(a[i] - b[i]);
				}

				return sum / a.Length;
			}

			env.SetCameraTransform(eye, target);
			env.SetTemporalJitter(false);
			RenderOne();
			var still0 = DiligentTextureReadback.ReadRgba8(env.DilApi,
				(DiligentRenderTarget)env.ColorTarget, out var jw, out var jh);
			RenderOne();
			var still1 = DiligentTextureReadback.ReadRgba8(env.DilApi,
				(DiligentRenderTarget)env.ColorTarget, out _, out _);

			env.SetTemporalJitter(true);
			RenderOne();
			var jit0 = DiligentTextureReadback.ReadRgba8(env.DilApi,
				(DiligentRenderTarget)env.ColorTarget, out _, out _);
			RenderOne();
			var jit1 = DiligentTextureReadback.ReadRgba8(env.DilApi,
				(DiligentRenderTarget)env.ColorTarget, out _, out _);

			Console.WriteLine($"[probe] jitter off: frame delta SAD={Sad(still0, still1):F4} " +
				"(обязан быть ровно 0: рендер детерминирован)");
			Console.WriteLine($"[probe] jitter on:  frame delta SAD={Sad(jit0, jit1):F4} " +
				"(обязан быть > 0: смещение проекции доехало до GPU)");

			if (Environment.GetEnvironmentVariable("DECA_PROBE_MOTION") == "1")
			{
				// Мелкий диапазон шкалы - нарочно: протечка джиттера это доли пикселя, и на дефолтных
				// 16 px она пряталась бы внутри одной градации 8-битного отладочного кадра. На 2 px
				// каждая 1/127 шкалы = 0.016 px - утечку в четверть пикселя видно как ~16 градаций.
				const float leakRangePx = 2f;
				env.SetMotionVectorDebug(true, leakRangePx);
				RenderOne();
				var motionRgba = DiligentTextureReadback.ReadRgba8(env.DilApi,
					(DiligentRenderTarget)env.ColorTarget, out _, out _);
				env.SetMotionVectorDebug(false, leakRangePx);

				int maxDev = 0;
				for (int i = 0; i < motionRgba.Length; i += 4)
				{
					maxDev = Math.Max(maxDev,
						Math.Max(Math.Abs(motionRgba[i] - 127), Math.Abs(motionRgba[i + 1] - 127)));
				}

				Console.WriteLine($"[probe] jitter motion leak: max |dev from 127|={maxDev} " +
					$"({maxDev * leakRangePx / 127f:F3} px; 0-1 = чисто, больше = джиттер протёк в векторы)");
			}

			env.SetTemporalJitter(Environment.GetEnvironmentVariable("DECA_PROBE_JITTERKEEP") == "1");
		}

		// DECA_PROBE_SHADOWDUMP=1 - вычитать слайсы shadow map каскадов (D32 массив) и сохранить их
		// PNG + статистику глубины в консоль. Два PNG на каскад: raw (глубина как есть, 0..1) и norm
		// (растянутая по факт. диапазону непустых пикселей) - raw показывает, ПОЧЕМУ буфер "весь
		// белый" (узкий диапазон у дальнего конца), norm - что геометрия в слайсе вообще есть.
		var shadowDumpMode = Environment.GetEnvironmentVariable("DECA_PROBE_SHADOWDUMP");
		if (shadows && (shadowDumpMode == "1" || shadowDumpMode == "2"))
		{
			env.DilApi.ImmediateContext.Flush();
			env.DilApi.ImmediateContext.WaitForIdle();

			var shadowTarget = (DiligentRenderTarget)env.BatchRenderer.WorldShadowRenderer.ShadowMapsTarget;

			// Режим 2 - диагностика тракта чтения: каждый слайс принудительно чистится СВОИМ
			// значением глубины ((i+1)*0.11) мимо рендера. Если ридбек вернёт четыре разных
			// значения - чтение исправно и дубли слайсов надо искать в записи (DSV per-slice);
			// если парами/одинаково - сломан сам ридбек массива на этом бэкенде.
			if (shadowDumpMode == "2")
			{
				var ctx = env.DilApi.ImmediateContext;
				for (uint slice = 0; slice < ShadowRenderer.MaxCascades; slice++)
				{
					var dsv = shadowTarget.GetView(global::Diligent.TextureViewType.DepthStencil, slice);
					ctx.SetRenderTargets([], dsv, global::Diligent.ResourceStateTransitionMode.Transition);
					ctx.ClearDepthStencil(dsv, global::Diligent.ClearDepthStencilFlags.Depth,
						(slice + 1) * 0.11f, 0, global::Diligent.ResourceStateTransitionMode.Transition);
				}

				ctx.Flush();
				ctx.WaitForIdle();
				Console.WriteLine("[probe] shadow dump: DIAGNOSTIC clears (slice i -> (i+1)*0.11)");
			}
			var slices = DiligentTextureReadback.ReadFloatSlices(env.DilApi, shadowTarget,
				out var smWidth, out var smHeight);

			// PNG пишем даунсемплом (шаг 4: 4096 -> 1024) - для картинки хватает, а полноразмерные
			// float-статистики всё равно считаются по каждому пикселю.
			const int step = 4;
			int outW = smWidth / step, outH = smHeight / step;

			for (int slice = 0; slice < slices.Length; slice++)
			{
				var data = slices[slice];
				float depthMin = float.MaxValue, depthMax = float.MinValue;
				double sum = 0;
				long clearCount = 0;
				foreach (var v in data)
				{
					if (v >= 1.0f) { clearCount++; continue; }
					depthMin = Math.Min(depthMin, v);
					depthMax = Math.Max(depthMax, v);
					sum += v;
				}

				long geomCount = data.LongLength - clearCount;
				float avg = geomCount > 0 ? (float)(sum / geomCount) : 0f;
				if (geomCount == 0) { depthMin = 1f; depthMax = 1f; }

				Console.WriteLine($"[probe] shadow slice {slice}: geometry {100.0 * geomCount / data.LongLength:F1}% " +
					$"of texels, depth min={depthMin:F4} max={depthMax:F4} avg={avg:F4}, clear(1.0)={100.0 * clearCount / data.LongLength:F1}%");

				var raw = new byte[outW * outH * 4];
				var norm = new byte[outW * outH * 4];
				float range = MathF.Max(depthMax - depthMin, 1e-6f);
				for (int y = 0; y < outH; y++)
				{
					for (int x = 0; x < outW; x++)
					{
						float v = data[(y * step) * smWidth + x * step];
						byte rawB = (byte)Math.Clamp((int)(v * 255f), 0, 255);
						// В norm пустота (clear 1.0) остаётся белой, геометрия растягивается на 0..0.9.
						byte normB = v >= 1.0f ? (byte)255 : (byte)Math.Clamp((int)((v - depthMin) / range * 230f), 0, 230);
						int o = (y * outW + x) * 4;
						raw[o] = raw[o + 1] = raw[o + 2] = rawB;
						raw[o + 3] = 255;
						norm[o] = norm[o + 1] = norm[o + 2] = normB;
						norm[o + 3] = 255;
					}
				}

				PngWriter.Write(Path.Combine(outDir, $"shadow_c{slice}_raw.png"), raw, outW, outH);
				PngWriter.Write(Path.Combine(outDir, $"shadow_c{slice}_norm.png"), norm, outW, outH);
			}
		}

		// DECA_PROBE_SHADOWDUMP=3 - punctual-аналог дампа выше: слайсы PunctualShadowMaps (D32
		// массив, 1024^2, до LightClusters.MaxShadowSlices слайсов, см. PunctualShadowScheduler/
		// ShadowRenderer). Та же пара PNG (raw/norm) + статистика глубины на слайс - нужна отдельно
		// от каскадного дампа: разное разрешение (1024 не 4096) и текстура (PunctualShadowMaps не
		// ShadowMaps), а до первого punctual-дроу массив вообще смотрит на 1x1 заглушку
		// (см. ShadowRenderer.PunctualShadowMapsTarget) - тогда ридбек честно вернёт один слайс 1x1.
		if (shadows && shadowDumpMode == "3")
		{
			env.DilApi.ImmediateContext.Flush();
			env.DilApi.ImmediateContext.WaitForIdle();

			var punctualTarget = (DiligentRenderTarget)env.BatchRenderer.WorldShadowRenderer.PunctualShadowMapsTarget;
			var pSlices = DiligentTextureReadback.ReadFloatSlices(env.DilApi, punctualTarget,
				out var pWidth, out var pHeight);

			Console.WriteLine($"[probe] punctual shadow maps: {pSlices.Length} slice(s), {pWidth}x{pHeight} " +
				$"(1x1 = ещё не заведён реальный массив, см. EnsurePunctualShadowMaps)");

			const int pStep = 4;
			int pOutW = Math.Max(1, pWidth / pStep), pOutH = Math.Max(1, pHeight / pStep);

			for (int slice = 0; slice < pSlices.Length; slice++)
			{
				var data = pSlices[slice];
				float depthMin = float.MaxValue, depthMax = float.MinValue;
				double sum = 0;
				long clearCount = 0;
				foreach (var v in data)
				{
					if (v >= 1.0f) { clearCount++; continue; }
					depthMin = Math.Min(depthMin, v);
					depthMax = Math.Max(depthMax, v);
					sum += v;
				}

				long geomCount = data.LongLength - clearCount;
				float avg = geomCount > 0 ? (float)(sum / geomCount) : 0f;
				if (geomCount == 0) { depthMin = 1f; depthMax = 1f; }

				Console.WriteLine($"[probe] punctual shadow slice {slice}: geometry {100.0 * geomCount / data.LongLength:F1}% " +
					$"of texels, depth min={depthMin:F4} max={depthMax:F4} avg={avg:F4}, clear(1.0)={100.0 * clearCount / data.LongLength:F1}%");

				if (pWidth <= 1)
				{
					continue;
				}

				var raw = new byte[pOutW * pOutH * 4];
				var norm = new byte[pOutW * pOutH * 4];
				float range = MathF.Max(depthMax - depthMin, 1e-6f);
				for (int y = 0; y < pOutH; y++)
				{
					for (int x = 0; x < pOutW; x++)
					{
						float v = data[(y * pStep) * pWidth + x * pStep];
						byte rawB = (byte)Math.Clamp((int)(v * 255f), 0, 255);
						byte normB = v >= 1.0f ? (byte)255 : (byte)Math.Clamp((int)((v - depthMin) / range * 230f), 0, 230);
						int o = (y * pOutW + x) * 4;
						raw[o] = raw[o + 1] = raw[o + 2] = rawB;
						raw[o + 3] = 255;
						norm[o] = norm[o + 1] = norm[o + 2] = normB;
						norm[o + 3] = 255;
					}
				}

				PngWriter.Write(Path.Combine(outDir, $"shadow_p{slice}_raw.png"), raw, pOutW, pOutH);
				PngWriter.Write(Path.Combine(outDir, $"shadow_p{slice}_norm.png"), norm, pOutW, pOutH);
			}
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
				// Зеркало редакторного привода (ModelPreviewViewport.PollProbeBake): по раунду на
				// кадр, с дросселем по забору. Именно этот режим ронял редактор на Sponza - прежний
				// прогон гнал раунды пачкой до сходимости и такого не воспроизводил. Сходимость тут
				// нарочно НЕ проверяется: это худший случай, когда освещение всё время «меняется»
				// и раунды идут без остановки.
				if (_gpuRoundLongRun != null && _gpuSessionLongRun != null && _gpuBakerLongRun != null)
				{
					// DECA_PROBE_GPUNOFENCE=1 - выпускать раунды НЕ дожидаясь предыдущего. Так
					// выглядел код до дросселирования; переключатель оставлен, чтобы отказ можно
					// было воспроизвести, а не только рассуждать о нём.
					if (_gpuRoundLongRun.IsReady ||
						Environment.GetEnvironmentVariable("DECA_PROBE_GPUNOFENCE") == "1")
					{
						// Тем же общим циклом, что и вьюпорты (ProbeGiViewportShared.DriveChunks) -
						// иначе гарнесс меряет не тот привод, который работает в редакторе.
						_gpuRoundsRun += ProbeGiViewportShared.DriveChunks(_gpuRoundLongRun,
							_gpuSessionLongRun, _gpuBakerLongRun,
							_gpuRoundLongRun.ChunksPerFrame(_gpuSessionLongRun.RaysPerRound),
							stopOnConverged: false);
					}
					else
					{
						_gpuRoundsSkipped++;
					}
				}

				time += 1f / 60f;
				env.SetEyeAdaptationDeltaTime(1f / 60f);
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
			Console.WriteLine("[probe] long run complete" +
				(_gpuRoundLongRun != null
					? $"; gpu rounds run {_gpuRoundsRun}, skipped by fence {_gpuRoundsSkipped}"
					: string.Empty));
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
			env.HdrColorTarget?.Resize(newSize);
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
				env.SetEyeAdaptationDeltaTime(1f / 60f);
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

	/// <summary>Атласы probe-GI текущего прогона (null = выключен) - PushPreviewSettings пушит их
	/// параметры сетки в кбуфер каждого материала (см. ModelPreviewViewport.PollProbeBake).</summary>
	private static ProbeGiTextures? ProbeTextures;

	/// <summary>Атласы мелких каскадов (DECA_PROBE_CASCADES, зеркало
	/// ModelPreviewViewport._probeCascades) - индекс 0 уходит в слоты _C1, 1 - в _C2.</summary>
	private static readonly List<ProbeGiTextures> CascadeTextures = new();

	// GPU-путь для длинного прогона (DECA_PROBE_FRAMES): раунд на кадр, как в редакторе.
	private static ProbeRoundGpu? _gpuRoundLongRun;
	private static ProbeGiBakeSession? _gpuSessionLongRun;
	private static ProbeGiBaker? _gpuBakerLongRun;
	private static int _gpuRoundsRun, _gpuRoundsSkipped;

	/// <summary>Переопределение ProbeGiParams из DECA_PROBE_GIPARAMS (см. PushPreviewSettings).</summary>
	private static Vector4? GiParamsOverride;

	/// <summary>Переопределение флора небесной доли из DECA_PROBE_GIPARAMS2.</summary>
	private static float? SkyShadowFloorOverride;

	/// <summary>Поворот энвайронмента при DECA_PROBE_LIGHT (радианы) - зеркало
	/// PreviewShadowSettings.EnvYawRadians, уходит в кбуфер как в редакторе.</summary>
	private static float EnvYaw;

	private static void PushPreviewSettings(ModelLoader model, int mode, int channel,
		bool forceFlatWhite = false)
	{
		// ToneCurve заполняется и ЗДЕСЬ тоже: CLI пушит этот кбуфер независимо от вьюпорта, и поле,
		// добавленное только там, приезжало бы сюда нулём (см. PreviewSettingsData.ToneCurve).
		var data = new PreviewSettingsData
		{
			Mode = mode,
			Channel = channel,
			EnvYawRadians = EnvYaw,
			ToneCurve = int.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_CURVE"), out var curve)
				? curve
				: 0,
		};

		if (ProbeTextures != null)
		{
			data.ProbeGridOrigin = ProbeTextures.GridOrigin;
			data.ProbeGridCell = ProbeTextures.GridCell;
			data.ProbeGridCounts = ProbeTextures.GridCounts;
			data.ProbeGridBricks = ProbeTextures.GridBricks;
		}

		// Каскады (DECA_PROBE_CASCADES > 1) - как во вьюпорте: нули = каскада нет.
		if (CascadeTextures.Count > 0)
		{
			var t = CascadeTextures[0];
			data.ProbeGridOrigin1 = t.GridOrigin;
			data.ProbeGridCell1 = t.GridCell;
			data.ProbeGridCounts1 = t.GridCounts;
			data.ProbeGridBricks1 = t.GridBricks;
		}

		if (CascadeTextures.Count > 1)
		{
			var t = CascadeTextures[1];
			data.ProbeGridOrigin2 = t.GridOrigin;
			data.ProbeGridCell2 = t.GridCell;
			data.ProbeGridCounts2 = t.GridCounts;
			data.ProbeGridBricks2 = t.GridBricks;
		}

		// Дефолты ручек probe-GI/солнца - те же, что в EditorSettings (см. кбуфер ProbeGiParams);
		// нули дали бы жёсткие флоры глушения вместо редакторских 0.3/0.2.
		// DECA_PROBE_GIPARAMS="x,y,z,w" - переопределение (флор тени, флор спекуляра,
		// интенсивность солнца, буст эмбиента) для A/B без пересборки.
		data.ProbeGiParams = GiParamsOverride ?? new Vector4(0.3f, 0.2f, 2f, 1f);
		// DECA_PROBE_GIPARAMS2=<x> - флор глушения небесной доли эмбиента тенью (дефолт 1).
		// y - сторона окто-карты видимости (см. ProbeGiBakeResult.VisRes). Заполняется и здесь:
		// CLI собирает PreviewSettings САМ, и забытое поле означает молча иначе выбранный тайл.
		data.ProbeGiParams2 = new Vector4(SkyShadowFloorOverride ?? 1f, ProbeGiBakeResult.VisRes, 0f, 0f);

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
