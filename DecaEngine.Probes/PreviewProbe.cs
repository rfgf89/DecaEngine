using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Core.Assets;
using DecaEngine.Editor.ECS;
using Friflo.Engine.ECS;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
using DecaEngine.Graphics.ProbeGi;
using DecaEngine.Sdl;
using Friflo.Engine.ECS.Systems;
using DecaEngine.Scene;
using DecaEngine.Animation;
using DecaEngine.Editor;

namespace DecaEngine.Probes;

/// <summary>Headless CLI preview render (`--preview-probe &lt;model&gt; &lt;outDir&gt;`): same offscreen environment as the editor preview, writes PNGs plus numeric luminance stats.</summary>
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
		// DECA_PROBE_BACKEND=d3d12|d3d11 - the editor runs D3D12; default here is Vulkan.
		var backend = Environment.GetEnvironmentVariable("DECA_PROBE_BACKEND")?.ToLowerInvariant() switch
		{
			"d3d12" => GraphicsBackend.D3D12,
			"d3d11" => GraphicsBackend.D3D11,
			_ => GraphicsBackend.Vulkan,
		};
		Console.WriteLine($"[probe] backend: {backend}");
		api.Initialize(backend);

		// DECA_PROBE_FEATURES=<bits> - Lighting feature toggles (PreviewFeatureFlags); default All.
		ProbeFeatureFlags = int.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_FEATURES"), out var flags)
			? flags
			: (int)PreviewFeatureFlags.All;
		Console.WriteLine($"[probe] features: {(PreviewFeatureFlags)ProbeFeatureFlags}");

		// DECA_PROBE_HDR=<path.hdr> - file IBL instead of the procedural sky.
		// DECA_PROBE_SSAO=0 - disable the SSAO pass (on by default, as in editor).
		bool ssao = Environment.GetEnvironmentVariable("DECA_PROBE_SSAO") != "0";
		// DECA_PROBE_AO=GTAO - use GTAO instead of classic SSAO.
		var aoMode = string.Equals(Environment.GetEnvironmentVariable("DECA_PROBE_AO"), "GTAO",
			StringComparison.OrdinalIgnoreCase) ? AmbientOcclusionMode.Gtao : AmbientOcclusionMode.Ssao;
		// DECA_PROBE_SHADOWS=0 - disable world light shadows.
		bool shadows = Environment.GetEnvironmentVariable("DECA_PROBE_SHADOWS") != "0";
		// DECA_PROBE_SSGI=0 - disable the SSGI pass (on by default, as in editor).
		bool ssgi = Environment.GetEnvironmentVariable("DECA_PROBE_SSGI") != "0";
		// DECA_PROBE_EXPOSURE=1 - auto exposure + full HDR chain (off by default, as in editor).
		bool eyeAdaptation = Environment.GetEnvironmentVariable("DECA_PROBE_EXPOSURE") == "1";
		Console.WriteLine($"[probe] ssao: {ssao} ({aoMode}), ssgi: {ssgi}, shadows: {shadows}, exposure: {eyeAdaptation}");

		// DECA_PROBE_MAINCASCADES=1 - main-pipeline cascades; only headless UpdateCascades test.
		bool mainCascades = Environment.GetEnvironmentVariable("DECA_PROBE_MAINCASCADES") == "1";
		if (mainCascades)
		{
			Console.WriteLine("[probe] shadow cascades: main pipeline (CullingAndRenderSystem)");
		}

		// Harness owns its SharedViewportResources: separate process, never shared with the editor.
		var sharedResources = new SharedViewportResources(api);

		var env = new ModelViewportEnvironment(api, 512, 512, "Probe Color", "Probe Depth", sharedResources,
			skyBackground: true,
			environmentHdrPath: Environment.GetEnvironmentVariable("DECA_PROBE_HDR"),
			ssao: ssao, shadows: shadows, aoMode: aoMode, ssgi: ssgi,
			eyeAdaptation: eyeAdaptation, mainCascades: mainCascades,
			// DECA_PROBE_FOG=1 - fog pass; off by default to keep the luminance metric comparable.
			fog: Environment.GetEnvironmentVariable("DECA_PROBE_FOG") == "1",
			// DECA_PROBE_BLOOM=1 - bloom; off by default, it shifts the luminance metric.
			bloom: Environment.GetEnvironmentVariable("DECA_PROBE_BLOOM") == "1",
			// DECA_PROBE_GRADE=1 - color grade pass (neutral defaults leave the frame unchanged).
			colorGrade: Environment.GetEnvironmentVariable("DECA_PROBE_GRADE") == "1",
			// DECA_PROBE_VOLUMETRIC=1 - volumetric light; off by default, shifts the luminance metric.
			volumetric: Environment.GetEnvironmentVariable("DECA_PROBE_VOLUMETRIC") == "1",
			// DECA_PROBE_MOTION=1 - motion vector buffer; static camera must yield exact grey (0.5).
			motionVectors: Environment.GetEnvironmentVariable("DECA_PROBE_MOTION") == "1",
			// DECA_PROBE_TAAU=1 - temporal upscale (needs DECA_PROBE_MOTION=1; enables jitter itself).
			temporalUpscale: Environment.GetEnvironmentVariable("DECA_PROBE_TAAU") == "1",
			// DECA_PROBE_FSR=1 / DECA_PROBE_DLSS=1 - native upscaler (needs TAAU=1 + D3D12; a
			// missing shim DLL silently keeps TAAU; DLSS is NVIDIA RTX only).
			upscalerBackend: Environment.GetEnvironmentVariable("DECA_PROBE_DLSS") == "1" ? 2
				: Environment.GetEnvironmentVariable("DECA_PROBE_FSR") == "1" ? 1 : 0);

		env.SetMotionVectorDebug(Environment.GetEnvironmentVariable("DECA_PROBE_MOTIONDEBUG") == "1",
			float.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_MOTIONRANGE"),
				System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
				out var motionRange) && motionRange > 0f
				? motionRange
				: MotionVectorDebugPassResources.DefaultRangePixels);

		// DECA_PROBE_VOL* knobs - volumetric A/B: shadow strength 0 vs 1 proves beams come from shadows.
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

		// HDR flag must match the actually created environment, or the model shader tonemaps twice.
		if (env.HdrOutput)
		{
			ProbeFeatureFlags |= (int)PreviewFeatureFlags.HdrOutput;
		}

		// DECA_PROBE_ANISO=0 - disable anisotropic filtering (load-time toggle).
		bool anisotropic = Environment.GetEnvironmentVariable("DECA_PROBE_ANISO") != "0";

		// Samplers bake at load time, so mip bias derives from the same DECA_PROBE_RENDERSCALE.
		float probeMipBias = float.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_RENDERSCALE"),
			System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
			out var probeRsForBias) && probeRsForBias > 0f && probeRsForBias < 1f
			? MathF.Log2(probeRsForBias)
			: 0f;

		// DECA_PROBE_MIPBIAS=<f> - explicit override (0 disables; +4 must visibly blur, else bias is lost).
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
			// DECA_PROBE_TEXSIZE=<n> - texture side cap (mirrors PreviewMaxTextureSize); for peak-memory A/B.
			MaxTextureSize = int.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_TEXSIZE"), out var texSize)
				? Math.Clamp(texSize, 128, 8192)
				: 2048,
			// DECA_PROBE_STREAM=1 - editor streaming mode: 1x1 fillers, quality arrives via ModelStreamer.
			StreamTextures = Environment.GetEnvironmentVariable("DECA_PROBE_STREAM") == "1"
		});

		Console.WriteLine($"[probe] meshes={model.Meshes.Count} materials={model.materialObjects.Count} instances={model.instances.Count}");

		var loadTimings = model.Timings;
		Console.WriteLine($"[probe] load: parse {loadTimings.ParseMs} ms, decode {loadTimings.DecodeMs} ms, " +
			$"materials {loadTimings.MaterialsMs} ms, meshes {loadTimings.MeshesMs} ms, finalize {loadTimings.FinalizeMs} ms; " +
			$"{loadTimings.DecodedImages} images -> {loadTimings.DecodedBytes / (1024 * 1024)} MB decoded");
		Console.WriteLine($"[probe] load shaders: {loadTimings.ShaderVariants} pixel variants, " +
			$"{loadTimings.ShaderMs} ms (inside finalize)");
		Console.WriteLine($"[probe] load textures: {loadTimings.TextureUploads} uploads, " +
			$"{loadTimings.TextureMs} ms (inside finalize)");
		Console.WriteLine($"[probe] load pso: {DecaEngine.Graphics.Diligent.DiligentPsoManager.DiagCreateCount} created, " +
			$"{DecaEngine.Graphics.Diligent.DiligentPsoManager.DiagCreateMs} ms (since process start)");
		Console.WriteLine($"[probe] load meshes-gpu: {loadTimings.MeshUploads} meshes, " +
			$"{loadTimings.MeshMs} ms (inside finalize)");
		Console.WriteLine($"[probe] load samplers: {loadTimings.Samplers} created, " +
			$"{loadTimings.SamplerMs} ms (inside finalize)");
		Console.WriteLine($"[probe] load materials-gpu: {loadTimings.MaterialsBuilt} built, " +
			$"{loadTimings.MaterialBuildMs} ms (inside finalize): " +
			$"CreateMaterial {loadTimings.MatCreateMs} ms, SetShader {loadTimings.MatShaderMs} ms");
		// DECA_PROBE_ASSETCACHE=1 (+DECA_ASSET_CACHE) - wait for the background bake, reload the
		// model from cache and verify baked textures/geometry actually match the glTF parse.
		if (Environment.GetEnvironmentVariable("DECA_PROBE_ASSETCACHE") == "1")
		{
			var cacheRoot = DecaEngine.Graphics.Assets.AssetCache.DefaultRoot;
			if (string.IsNullOrEmpty(cacheRoot))
			{
				Console.WriteLine("[probe] asset cache: DECA_ASSET_CACHE not set - check skipped");
			}
			else
			{
				Console.WriteLine($"[probe] asset cache: waiting for the background bake in {cacheRoot}");

				var swBake = System.Diagnostics.Stopwatch.StartNew();
				bool baked = DecaEngine.Graphics.Assets.AssetBakeQueue.WaitForIdle(TimeSpan.FromMinutes(10));
				Console.WriteLine($"[probe] asset cache: bake {(baked ? "finished" : "TIMED OUT")} in {swBake.ElapsedMilliseconds} ms");

				long dtexBytes = 0;
				int dtexCount = 0;
				var textureDirectory = Path.Combine(cacheRoot, "textures");
				if (Directory.Exists(textureDirectory))
				{
					foreach (var file in Directory.EnumerateFiles(textureDirectory, "*.dtex"))
					{
						dtexCount++;
						dtexBytes += new FileInfo(file).Length;
					}
				}

				Console.WriteLine($"[probe] asset cache: {dtexCount} .dtex, {dtexBytes / (1024 * 1024)} MB");

				var swSecond = System.Diagnostics.Stopwatch.StartNew();
				var cachedModel = ModelLoader.Load(api, modelPath, new ModelLoadOptions
				{
					AnisotropicFiltering = anisotropic,
					MipLodBias = probeMipBias,
					VertexShader = new EditorRef("shader/UnlitInstancedVS.hlsl"),
					PixelShader = new EditorRef("shader/UnlitInstancedPS.hlsl"),
					OptimizeMesh = false,
					GenerateLods = false,
					MaxTextureSize = int.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_TEXSIZE"), out var cachedTexSize)
						? Math.Clamp(cachedTexSize, 128, 8192)
						: 2048,
				});
				long secondMs = swSecond.ElapsedMilliseconds;

				var cachedTimings = cachedModel.Timings;
				Console.WriteLine($"[probe] asset cache: second load {secondMs} ms " +
					$"(parse {cachedTimings.ParseMs}, decode {cachedTimings.DecodeMs}, " +
					$"materials {cachedTimings.MaterialsMs}, meshes {cachedTimings.MeshesMs}, " +
					$"finalize {cachedTimings.FinalizeMs}); textures {cachedTimings.TextureUploads} uploads, " +
					$"{cachedTimings.TextureMs} ms");

				// Cooked model must match the parse exactly; drift shows as mesh holes, not exceptions.
				bool sameShape = cachedModel.Meshes.Count == model.Meshes.Count &&
					cachedModel.materialObjects.Count == model.materialObjects.Count &&
					cachedModel.instances.Count == model.instances.Count;

				Console.WriteLine($"[probe] asset cache: geometry match {(sameShape ? "OK" : "MISMATCH")} - " +
					$"meshes {cachedModel.Meshes.Count}/{model.Meshes.Count}, " +
					$"materials {cachedModel.materialObjects.Count}/{model.materialObjects.Count}, " +
					$"instances {cachedModel.instances.Count}/{model.instances.Count}");

				var boundsA = model.ComputeBounds();
				var boundsB = cachedModel.ComputeBounds();
				bool sameBounds = Vector3.Distance(boundsA.min, boundsB.min) < 1e-3f &&
					Vector3.Distance(boundsA.max, boundsB.max) < 1e-3f;

				Console.WriteLine($"[probe] asset cache: bounds match {(sameBounds ? "OK" : "MISMATCH")} - " +
					$"{boundsA.min}..{boundsA.max} vs {boundsB.min}..{boundsB.max}");

				cachedModel.Release();
			}
		}

		// DECA_PROBE_VISRES=<8..24> - visibility octahedral map side ("Visibility res" knob), for A/B.
		if (int.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_VISRES"), out var visResOverride))
		{
			ProbeGiBakeResult.VisRes = Math.Clamp(visResOverride,
				ProbeGiBakeResult.MinVisRes, ProbeGiBakeResult.MaxVisRes);
			Console.WriteLine($"[probe] visibility res: {ProbeGiBakeResult.VisRes}");
		}

		// DECA_PROBE_BVHVERIFY=1 - compare cached BVH vs direct build field-by-field; cache drift
		// would mean silently broken GI on every later run.
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

			// Node count per level must double (median split); root covers scene bounds; leaves match stats.
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

		// DECA_PROBE_BVHCACHE=1 - disk BVH cache check: second run must read <model>.bhv.bin fast.
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

		// Stream entries without a valid source never upgrade past the 1x1 filler.
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
			$"{DecaEngine.Graphics.Diligent.DiligentShader.DiagCompileActual} ACTUAL, " +
			$"{DecaEngine.Graphics.Diligent.DiligentShader.DiagCompileMs} ms");

		// DECA_PROBE_RELOAD=<path> - release the first model and load a second (editor model-switch path).
		// Repro for shared-resource over-release: naive per-dictionary-value release drives native
		// refcounts negative (0xC0000005 in Diligent.ComObject.Release).
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

		// Empty map on a static model is normal: CreateInstanceEntity just makes a regular instance.
		var skinBaseMap = new Dictionary<int, int>();
		ModelViewportGeometry.RegisterModelResources(env.BatchRenderer, model, meshIdMap, materialIdMap,
			sharedResources.EnvMapSampler, env.SceneCopyTarget, env.EnvironmentMap,
			sceneCopySampler: sharedResources.SceneColorSampler, skinBaseMap: skinBaseMap);

		// Zero alpha-tested shadow materials on a foliage scene means the selection criterion failed.
		Console.WriteLine($"[probe] alpha-tested shadows: {env.BatchRenderer.WorldShadowRenderer.AlphaTestedMaterialCount} materials, " +
			$"non-casters: {env.BatchRenderer.WorldShadowRenderer.NonCastingMaterialCount}");

		foreach (var kvp in model.MaterialPbr)
		{
			if (kvp.Value.AlphaCutoff > 0f)
			{
				Console.WriteLine($"[probe] transparent material {kvp.Key}: mode={kvp.Value.AlphaMode} " +
					$"cutoff={kvp.Value.AlphaCutoff:F2} avgAlpha={kvp.Value.AverageAlpha:F3} " +
					$"soft={kvp.Value.SoftAlphaFraction:F3}");
			}
		}

		// Palettes bind to ONE entity after the loop: the callback fires inside CreateInstanceEntity
		// (entity not yet created) and all meshes of a character share one skeleton.
		var skinnedPalettes = new List<int>();
		Entity? animatedEntity = null;

		int created = 0;
		foreach (var instance in model.instances)
		{
			if (subMesh >= 0 && instance.meshId != subMesh)
			{
				continue;
			}

			int palettesBefore = skinnedPalettes.Count;
			var entity = ModelViewportGeometry.CreateInstanceEntity(env.Store, env.ResourceManager,
				env.BatchRenderer, meshIdMap, materialIdMap, batchCache,
				instance.meshId, instance.materialId, instance.transform,
				model, skinBaseMap, skinnedPalettes.Add);
			if (entity != null)
			{
				created++;
				if (skinnedPalettes.Count > palettesBefore)
				{
					animatedEntity ??= entity;
				}
			}
		}
		Console.WriteLine($"[probe] entities created: {created} (subMesh={subMesh})");

		// Animation runs through components + AnimationDriver, the same path as Scene View.
		//   DECA_PROBE_ANIMCLIP=<idx>  - clip index (without it the character stays in bind pose)
		//   DECA_PROBE_ANIMTIME=<sec>  - time within the clip
		//   DECA_PROBE_ANIMFRAMES=<n>  - run n frames at 1/60 s through the full pipeline
		//   DECA_PROBE_PHYSICS=1 - standalone physics world test (PhysicsProbe)
		//   DECA_PROBE_PROC=1    - procedural layer test on the model rig (ProceduralProbe)
		if (Environment.GetEnvironmentVariable("DECA_PROBE_PHYSICS") == "1")
		{
			PhysicsProbe.Run();
		}

		if (Environment.GetEnvironmentVariable("DECA_PROBE_PROC") == "1")
		{
			ProceduralProbe.Run(model);
		}

		//   DECA_PROBE_HUMANOID=1 - avatar auto-mapping test (HumanoidProbe)
		if (Environment.GetEnvironmentVariable("DECA_PROBE_HUMANOID") == "1")
		{
			HumanoidProbe.Run(model);
		}

		//   DECA_PROBE_ANIMREPORT=1 - numeric per-clip report (AnimationReportProbe)
		if (Environment.GetEnvironmentVariable("DECA_PROBE_ANIMREPORT") == "1")
		{
			AnimationReportProbe.ModelPathHint = modelPath;
			AnimationReportProbe.Run(model);
		}

		//   DECA_PROBE_GAMEPLAY=1 - gameplay scene scripts test (GameplayProbe)
		if (Environment.GetEnvironmentVariable("DECA_PROBE_GAMEPLAY") == "1")
		{
			GameplayProbe.Run();
		}

		//   DECA_PROBE_SCENE=1 - headless demo scene with physics (ScenePhysicsProbe; needs api)
		if (Environment.GetEnvironmentVariable("DECA_PROBE_SCENE") == "1")
		{
			ScenePhysicsProbe.Run(api, env.BatchRenderer.Skinning);
		}

		//   DECA_PROBE_CHARACTER=1 - full character on plane + wall (CharacterPlaneProbe)
		if (Environment.GetEnvironmentVariable("DECA_PROBE_CHARACTER") == "1")
		{
			CharacterPlaneProbe.Run(env.BatchRenderer.Skinning, model, modelPath);
		}

		var animationDriver = new AnimationDriver(env.BatchRenderer.Skinning);
		if (animatedEntity != null)
		{
			foreach (int palette in skinnedPalettes)
			{
				animationDriver.AddInstance(animatedEntity.Value.Id, model, palette);
			}

			string clipName = int.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_ANIMCLIP"),
					out int clipIndex)
				&& clipIndex >= 0 && clipIndex < model.Animations.Count
					? model.Animations[clipIndex].Name
					: string.Empty;

			float animTime = float.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_ANIMTIME"),
				System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out var parsedAnimTime)
				? parsedAnimTime
				: 0f;

			// Playing=false: time is set directly; Advance would shift it and break reproducibility.
			animatedEntity.Value.AddComponent(new Animator
			{
				ClipName = clipName,
				Time = animTime,
				Playing = false,
				Loop = true,
				Speed = 1f,
			});

			// Identity transform: model space == world space here (probe has no physics).
			animationDriver.Update(animatedEntity.Value, Matrix4x4.Identity, 0f);
			env.BatchRenderer.ExecuteSkinning();

			Console.WriteLine($"[probe] skinning: {animationDriver.CharacterCount} characters via components, " +
				$"clip {(clipName.Length > 0 ? clipName : "none (bind pose)")}, t={animTime:0.###}s");

			// DECA_PROBE_DEBUGDRAW=1 - skeleton overlay; probe_lighting.png must differ from a run without it.
			if (Environment.GetEnvironmentVariable("DECA_PROBE_DEBUGDRAW") == "1")
			{
				var debugDraw = new DebugDraw { Enabled = true };

				animationDriver.Debug = debugDraw;
				animationDriver.DebugOptions = new AnimationDebugOptions
				{
					Skeleton = true,
					JointAxes = true,
					OnTop = true,
				};

				// Extra zero-dt step: pose is already computed, this only records the debug lines.
				debugDraw.Clear();
				animationDriver.Update(animatedEntity.Value, Matrix4x4.Identity, 0f);

				var debugOverlay = new DebugLineOverlay(env.DilApi, api, env.BatchRenderer,
					env.Pipeline.Targets?.RenderColorFormat ?? TextureObjectFormat.R8G8B8A8UNorm);

				debugOverlay.Upload(debugDraw);
				env.Pipeline.DebugOverlay = debugOverlay.Draw;
				env.Pipeline.InvalidateGraph();

				Console.WriteLine($"[probe] debug draw: {debugDraw.TotalCount} vertices " +
					$"({debugDraw.DepthTestedCount} depth-tested, {debugDraw.OnTopCount} on top), " +
					$"capacity {debugOverlay.DepthTestedCapacity}/{debugOverlay.OnTopCapacity}" +
					(debugDraw.Overflowed ? ", HIT THE CEILING" : ""));
			}

			// Full pipeline per frame: exercises palette updates, skinning dispatch and buffer growth.
			if (int.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_ANIMFRAMES"), out int animFrames) &&
				animFrames > 0)
			{
				Console.WriteLine($"[probe] anim-loop: {animFrames} frames of 1/60 s");

				var animEntity = animatedEntity.Value;
				ref var loopAnimator = ref animEntity.GetComponent<Animator>();
				loopAnimator.Playing = true;

				float loopTime = 0f;
				for (int frame = 0; frame < animFrames; frame++)
				{
					// Skinning strictly BEFORE Root.Update: growth recreates the mega vertex buffer,
					// which would free it under already-recorded frame commands.
					animationDriver.Update(animEntity, Matrix4x4.Identity, 1f / 60f);
					env.BatchRenderer.ExecuteSkinning();

					loopTime += 1f / 60f;
					env.SetEyeAdaptationDeltaTime(1f / 60f);
					env.Root.Update(new UpdateTick(1f / 60f, loopTime));
					env.Pipeline.Execute();

					if (frame % 15 == 0)
					{
						Console.WriteLine($"[probe] anim-loop: frame {frame}, " +
							$"t={animEntity.GetComponent<Animator>().Time:0.###}s, " +
							$"{env.BatchRenderer.DiagCounters}");
					}
				}

				env.DilApi.ImmediateContext.Flush();
				env.DilApi.ImmediateContext.WaitForIdle();
				Console.WriteLine($"[probe] anim-loop: {animFrames} frames completed without a crash");
			}
		}

		// DECA_PROBE_MODEL2=<path> - load a SECOND model alongside the first at a given world TRS.
		//   DECA_PROBE_MODEL2_POS="x,y,z"   - world position (default 0,0,0)
		//   DECA_PROBE_MODEL2_ROT="x,y,z,w" - world rotation quaternion (default identity)
		//   DECA_PROBE_MODEL2_SCALE="x,y,z" - world scale (default 1,1,1)
		var model2Path = Environment.GetEnvironmentVariable("DECA_PROBE_MODEL2");
		if (!string.IsNullOrEmpty(model2Path))
		{
			var model2Pos = ParseVec(Environment.GetEnvironmentVariable("DECA_PROBE_MODEL2_POS")) ?? Vector3.Zero;
			var model2ScaleV = ParseVec(Environment.GetEnvironmentVariable("DECA_PROBE_MODEL2_SCALE")) ?? Vector3.One;
			var model2Rot = Quaternion.Identity;
			var rotStr = Environment.GetEnvironmentVariable("DECA_PROBE_MODEL2_ROT");
			if (!string.IsNullOrWhiteSpace(rotStr))
			{
				var rp = rotStr.Split(',');
				if (rp.Length == 4
					&& float.TryParse(rp[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rx)
					&& float.TryParse(rp[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ry)
					&& float.TryParse(rp[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rz)
					&& float.TryParse(rp[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rw))
				{
					model2Rot = new Quaternion(rx, ry, rz, rw);
				}
			}

			Console.WriteLine($"[probe] model2: {model2Path} pos={model2Pos} rot={model2Rot} scale={model2ScaleV}");

			var model2 = ModelLoader.Load(api, model2Path, new ModelLoadOptions
			{
				AnisotropicFiltering = anisotropic,
				VertexShader = new EditorRef("shader/UnlitInstancedVS.hlsl"),
				PixelShader = new EditorRef("shader/UnlitInstancedPS.hlsl"),
				OptimizeMesh = false,
				GenerateLods = false
			});

			var meshIdMap2 = new Dictionary<int, MeshId>();
			var materialIdMap2 = new Dictionary<int, MaterialId>();
			var batchCache2 = new Dictionary<(int, int), BatchId>();
			ModelViewportGeometry.RegisterModelResources(env.BatchRenderer, model2, meshIdMap2, materialIdMap2,
				sharedResources.EnvMapSampler, env.SceneCopyTarget, env.EnvironmentMap,
				sceneCopySampler: sharedResources.SceneColorSampler);

			// Local * world: same composition order as TransformSystem/GpuInstanceBufferSystem.
			var model2World = Matrix4x4.CreateScale(model2ScaleV) * Matrix4x4.CreateFromQuaternion(model2Rot)
				* Matrix4x4.CreateTranslation(model2Pos);

			int created2 = 0;
			foreach (var instance in model2.instances)
			{
				var localTrs = Matrix4x4.CreateScale(instance.transform.scale)
					* Matrix4x4.CreateFromQuaternion(instance.transform.rotation)
					* Matrix4x4.CreateTranslation(instance.transform.position);
				var worldTrs = localTrs * model2World;
				if (!Matrix4x4.Decompose(worldTrs, out var wScale, out var wRot, out var wPos))
				{
					continue;
				}

				var worldTransform = new DecaEngine.Core.Transform
				{
					position = wPos,
					rotation = wRot,
					scale = wScale,
				};

				var entity2 = ModelViewportGeometry.CreateInstanceEntity(env.Store, env.ResourceManager,
					env.BatchRenderer, meshIdMap2, materialIdMap2, batchCache2,
					instance.meshId, instance.materialId, worldTransform);
				if (entity2 != null)
				{
					created2++;
				}
			}
			Console.WriteLine($"[probe] model2 entities created: {created2}");
		}

		var (min, max) = subMesh >= 0
			? ModelViewportGeometry.ComputeSubMeshBounds(model, subMesh)
			: model.ComputeBounds();
		var target = (min + max) * 0.5f;
		var radius = MathF.Max(0.05f, (max - min).Length() * 0.5f);
		// Bounds always printed: DECA_PROBE_EYE/TARGET cannot be picked blind.
		Console.WriteLine($"[probe] bounds: min={min} max={max} size={max - min}");
		var distance = ModelViewportGeometry.ComputeFramingDistance(radius, ModelViewportEnvironment.CameraFovDegrees) * zoom;
		var eye = ModelViewportGeometry.ComputeOrbitEye(target, distance, yaw, 0.35f);

		// DECA_PROBE_EYE / DECA_PROBE_TARGET = "x,y,z" - explicit camera (orbit cannot reach interiors).
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

		// DECA_PROBE_SPOT=1 - spawn one punctual spot light in env.Store:
		//   DECA_PROBE_SPOT_POS="x,y,z"       - light position (default eye + up*1.5)
		//   DECA_PROBE_SPOT_TARGET="x,y,z"    - cone aim point (default model target)
		//   DECA_PROBE_SPOT_ANGLE=<deg>       - SpotAngle, FULL outer cone angle (default 45)
		//   DECA_PROBE_SPOT_INNER=<deg>       - InnerSpotAngle, FULL inner angle (0 = auto 80%)
		//   DECA_PROBE_SPOT_RANGE=<float>     - Range (default 5)
		//   DECA_PROBE_SPOT_INTENSITY=<float> - Intensity (default 8)
		//   DECA_PROBE_SPOT_COLOR="r,g,b"     - Color (default 3.45,3.6,4.05)
		//   DECA_PROBE_SPOT_SHADOW=<float>    - ShadowStrength pre-clamp (clamped to [0,1] downstream)
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
			// Entity local +Z must point along spotDir - same convention as PunctualShadowScheduler/LightCulling.
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

			// DECA_PROBE_SPOT_DECOYS=<n> - n closer shadow-casting spots exhaust MaxShadowSlices=16;
			// a light past the budget gets ShadowParams.x=-1 and shines with no shadow.
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
							Intensity = 0.001f, // nearly invisible: exists only to consume a shadow slice
							Range = 1f,
							SpotAngle = 30f,
							ShadowStrength = 1f,
						});
				}
				Console.WriteLine($"[probe] spot decoys: {decoyCount} shadow-casting SPOT lights (1 slice each) seeded " +
					"closer to camera (budget-exhaustion test)");
			}
		}

		// DECA_PROBE_POINT=1 - spawn one punctual POINT light (6 shadow slices, cube faces):
		//   DECA_PROBE_POINT_POS="x,y,z"       - position (default eye + up*1.5)
		//   DECA_PROBE_POINT_RANGE=<float>     - Range (default 6.4)
		//   DECA_PROBE_POINT_INTENSITY=<float> - Intensity (default 8)
		//   DECA_PROBE_POINT_COLOR="r,g,b"     - Color (default 3.45,3.6,4.05)
		//   DECA_PROBE_POINT_SHADOW=<float>    - ShadowStrength pre-clamp (clamped to [0,1] downstream)
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

		// DECA_PROBE_RENDERSCALE=<0.25..1> - scene render scale; camera untouched, the pipeline
		// latches viewport.zw itself (LatchRenderResolution).
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

			// DECA_PROBE_CASCADES_SM=<1..4> - shadow cascade count; must be set BEFORE the first
			// frame (DirectionalLightCascadeData capacity is frozen then).
			if (int.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_CASCADES_SM"), out var smCascades))
			{
				env.ShadowSettings.CascadeCount = Math.Clamp(smCascades, 1, ShadowRenderer.MaxCascades);
				Console.WriteLine($"[probe] shadow cascades: {env.ShadowSettings.CascadeCount}");
			}

			// DECA_PROBE_LIGHT="yawOff,elevOff" (degrees) - sun offsets; applied BEFORE the probe bake.
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

		// DECA_PROBE_AO_RANGE=<fraction of bounds radius> - override; 0 = legacy screen-space mode.
		float aoRangeFraction = float.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_AO_RANGE"),
			System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedAoRange)
			? parsedAoRange
			: ModelViewportEnvironment.AoRangeOfBoundsRadius;
		// DECA_PROBE_AO_RANGE_WORLD=<world units> - AO radius directly, bypassing the bounds fraction.
		float aoRangeWorld = float.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_AO_RANGE_WORLD"),
			System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedAoWorld)
			? parsedAoWorld
			: 0f;
		env.SetAoWorldRange(aoRangeWorld > 0f ? aoRangeWorld : radius * aoRangeFraction);
		// DECA_PROBE_AO_DEBUG=1 - AO debug view: the PNG becomes a grayscale AO map.
		env.SetAoDebugView(Environment.GetEnvironmentVariable("DECA_PROBE_AO_DEBUG") == "1");
		// DECA_PROBE_SSGI_RANGE_WORLD=<world units> - SSGI range (0 = bounds fraction, as in editor).
		// DECA_PROBE_SSGI_PARAMS="intensity,taps,firefly clamp,saturation"
		// DECA_PROBE_SSGI_BLUR=<bilateral blur radius>; DECA_PROBE_SSGI_DEBUG=1 - bounce only.
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

		// mainCascades: sync cascade distances to the camera like Scene View does; the sun entity
		// defaults [0.01..300] otherwise leave the near cascades empty.
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

		// The ray tracing level the device actually granted decides the dynamic GI path.
		Console.WriteLine($"[probe] ray tracing: {api.RayTracing}");

		// BLAS/TLAS smoke build under backend validation: catches bad buffer flags/strides/matrices early.
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

				// TLAS rebuild without touching BLAS is the per-frame cost of a dynamic world.
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

		// DECA_PROBE_PROBEGI=0 - disable probe-GI (DDGI-lite). Grid always spans FULL model bounds,
		// even with submesh isolation.
		if (Environment.GetEnvironmentVariable("DECA_PROBE_PROBEGI") != "0" && env.ShadowSettings != null)
		{
			var sw = System.Diagnostics.Stopwatch.StartNew();

			// DECA_PROBE_BVHFROMCACHE=1 - take BVH from <model>.bhv.bin; probe field must match direct build.
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

				// DECA_PROBE_FLICKER=<rays/round> - measure realtime field flicker; separates
				// estimator variance from multibounce loop swing.
				if (env.ShadowSettings != null &&
					int.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_FLICKER"),
						out var flickerRays) && flickerRays > 0)
				{
					var flicker = SceneTraceVerifier.MeasureFlicker(env.DilApi, baker,
						fullMin, fullMax,
						Vector3.Normalize(-env.ShadowSettings.LightDirection),
						new Vector3(1f, 0.98f, 0.92f) * 2f,
						raysPerRound: flickerRays, settleRounds: 40, measureRounds: 16,
						// DECA_PROBE_FLICKERCLAMP=<ray luminance cap>, 0 = uncapped.
						maxRayLuminance: float.TryParse(
							Environment.GetEnvironmentVariable("DECA_PROBE_FLICKERCLAMP"),
							System.Globalization.NumberStyles.Float,
							System.Globalization.CultureInfo.InvariantCulture, out var clamp)
							? clamp
							: 0f,
						// DECA_PROBE_FLICKERBLEND=<alpha>, 0 = default.
						blend: float.TryParse(
							Environment.GetEnvironmentVariable("DECA_PROBE_FLICKERBLEND"),
							System.Globalization.NumberStyles.Float,
							System.Globalization.CultureInfo.InvariantCulture, out var blendOverride)
							? blendOverride
							: 0f,
						// DECA_PROBE_FLICKERSKY=<sky intensity>, DECA_PROBE_FLICKERGRID=<grid density>.
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
						// DECA_PROBE_FLICKERSTEP=<max change per round>, 0 = off.
						maxStep: float.TryParse(
							Environment.GetEnvironmentVariable("DECA_PROBE_FLICKERSTEP"),
							System.Globalization.NumberStyles.Float,
							System.Globalization.CultureInfo.InvariantCulture, out var flickerStep)
							? flickerStep
							: -1f,
						// DECA_PROBE_FLICKERRELOC=<relocation cap, cell fractions>, 0 = off.
						relocation: float.TryParse(
							Environment.GetEnvironmentVariable("DECA_PROBE_FLICKERRELOC"),
							System.Globalization.NumberStyles.Float,
							System.Globalization.CultureInfo.InvariantCulture, out var flickerReloc)
							? flickerReloc
							: -1f,
						// DECA_PROBE_FLICKERGAMMA=<accumulation gamma>, 1 = linear (off).
						gamma: float.TryParse(
							Environment.GetEnvironmentVariable("DECA_PROBE_FLICKERGAMMA"),
							System.Globalization.NumberStyles.Float,
							System.Globalization.CultureInfo.InvariantCulture, out var flickerGamma)
							? flickerGamma
							: -1f,
						// DECA_PROBE_FLICKERVAR=<variability threshold>, 0 = off (default: skipped
						// rounds would dilute the flicker metric with zero deltas).
						variabilityThreshold: float.TryParse(
							Environment.GetEnvironmentVariable("DECA_PROBE_FLICKERVAR"),
							System.Globalization.NumberStyles.Float,
							System.Globalization.CultureInfo.InvariantCulture, out var flickerVar)
							? flickerVar
							: 0f,
						hardware: api.RayTracing >= RayTracingSupport.Inline,
						environmentMap: env.EnvironmentMap,
						skyRadiance: env.EnvironmentRadiance,
						envYaw: env.ShadowSettings.EnvYawRadians);
					Console.WriteLine($"[probe] flicker: {flicker.Rays} rays/round, " +
						$"alpha {flicker.Alpha:F3}, global swing {flicker.GlobalSwing:P1}, " +
						$"per-probe p50 {flicker.P50:P1} p90 {flicker.P90:P1} p99 {flicker.P99:P1} " +
						$"max {flicker.MaxRelativeDelta:P0}, above 10% {flicker.ShareAbove10:P1} " +
						$"(mean lum {flicker.MeanLuminanceAvg:F4}), " +
						$"variability {flicker.Variability:F4}, " +
						$"skipped rounds {flicker.SkippedRoundShare:P0}");
				}

				// DECA_PROBE_TRACETEST=1 - compare compute BVH traversal against the CPU reference (costly).
				if (Environment.GetEnvironmentVariable("DECA_PROBE_TRACETEST") == "1")
				{
					try
					{
						var report = SceneTraceVerifier.Run(env.DilApi, baker, fullMin, fullMax);
						Console.WriteLine($"[probe] trace verify: {report.RayCount} rays, " +
							$"{report.Mismatches} mismatches, worst rel error {report.WorstRelativeError:E2}, " +
							$"hits cpu={report.CpuHits} gpu={report.GpuHits}, " +
							$"nodes seen {report.ShaderNodeCount}/{report.UploadedNodeCount}");

						// DECA_PROBE_VERIFYROUNDS=<n> - rounds to verify; the default 4 do NOT test
						// blending (bootstrap rounds use alpha=1, see ProbeGiBaker.BootstrapRounds).
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
				// DECA_PROBE_GISAT=<0..1> - bounce saturation (mirror of the "Bounce saturation" knob).
				var bakeOptions = new ProbeGiBakeOptions();
				if (float.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_GISAT"),
					System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
					out var bounceSat))
				{
					bakeOptions.BounceSaturation = bounceSat;
					Console.WriteLine($"[probe] bounce saturation: {bounceSat}");
				}

				// DECA_PROBE_GIMAX=<int> - probe budget (mirror of the "Max probes" combo).
				if (int.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_GIMAX"),
					out var maxProbes))
				{
					bakeOptions.MaxProbes = maxProbes;
					Console.WriteLine($"[probe] max probes: {maxProbes}");
				}

				// DECA_PROBE_GIDENSITY=<float> - grid density (cells per extent); the budget only caps it.
				if (float.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_GIDENSITY"),
					System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
					out var gridDensity))
				{
					bakeOptions.GridDensity = gridDensity;
					Console.WriteLine($"[probe] grid density: {gridDensity}");
				}

				// DECA_PROBE_SURFCACHE=0 - bounce from the probe field instead of the surface cache (A/B).
				if (Environment.GetEnvironmentVariable("DECA_PROBE_SURFCACHE") == "0")
				{
					bakeOptions.SurfaceCache = false;
					Console.WriteLine("[probe] surface cache: off");
				}

				// Session opened explicitly only to report the surface cache before the run.
				var session = baker.BeginBake(fullMin, fullMax,
					Vector3.Normalize(-env.ShadowSettings.LightDirection),
					new Vector3(1f, 0.98f, 0.92f) * 2f,
					env.ShadowSettings.EnvYawRadians, env.EnvironmentRadiance, bakeOptions);
				// DECA_PROBE_GIGPU=1 - run rounds in compute like the editor; the shader writes the
				// atlases itself, so no Snapshot/upload is needed afterwards.
				ProbeRoundGpu? gpuRound = null;
				ProbeRoundPipelines? gpuPipelines = null;
				ProbeSceneAccel? gpuAccel = null;
				if (Environment.GetEnvironmentVariable("DECA_PROBE_GIGPU") == "1")
				{
					baker.EnsureSurfaceCache(session);
					ProbeTextures = new ProbeGiTextures(api, session.Result, "_probeGiCli",
						gpuWritable: true);
					ProbeTextures.Bind(model);
					// DECA_PROBE_GPUSTRESS=<n> - recreate the GPU path n times (editor settings-change leak repro).
					// DECA_PROBE_GIHW=1 - hardware ray tracing (RayQuery); needs inline device support.
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

						// TLAS rebuild = per-move cost in the editor; measured on identical poses.
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
						$"(once per device, tracing: {(hardware ? "hardware" : "software")})");

					if (int.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_GPUSTRESS"),
						out var stress) && stress > 0)
					{
						for (int i = 0; i < stress; i++)
						{
							// Only atlases + round are per-session; accel structures and pipelines
							// survive the cycle (per-tweak BLAS rebuilds exhausted GPU resources).
							var cycleAtlases = new ProbeGiTextures(env.DilApi, session.Result,
								$"_probeGiStress{i}", gpuWritable: true);

							var probe = new ProbeRoundGpu(env.DilApi, gpuPipelines, session, baker,
								cycleAtlases, env.EnvironmentMap, env.ShadowSettings.EnvYawRadians,
								gpuAccel);
							while (!probe.RunRound(session, baker,
								ProbeGiBaker.RoundRayDirections(session),
								ProbeGiBaker.RoundBlendWeight(session)))
							{
							}

							env.DilApi.ImmediateContext.Flush();
							env.DilApi.ImmediateContext.WaitForIdle();

							// Order matters: the round holds bindings on the atlases.
							probe.Dispose();
							cycleAtlases.Release();

							// Diligent frees resources deferred, per frame: without Present the
							// release queue never drains and the loop exhausts descriptors.
							api.Present();
						}

						Console.WriteLine($"[probe] gpu stress: {stress} recreate cycles survived");
					}

					// GPU path setup runs synchronously on the render thread; timed separately
					// because a long stall between frames breaks the swap chain frame cycle.
					var swGpuSetup = System.Diagnostics.Stopwatch.StartNew();
					gpuRound = new ProbeRoundGpu(env.DilApi, gpuPipelines, session, baker,
						ProbeTextures, env.EnvironmentMap, env.ShadowSettings.EnvYawRadians, gpuAccel);
					var t = gpuRound.SetupTiming;
					Console.WriteLine($"[probe] gpu path setup: {swGpuSetup.ElapsedMilliseconds} ms " +
						$"(surface capture {t.SurfaceCapture}, bvh export {t.BvhExport}, " +
						$"buffers {t.Buffers}, shaders {t.Shaders})");

					// Handed to the long run below: one round per frame, as the editor does.
					_gpuRoundLongRun = gpuRound;
					_gpuSessionLongRun = session;
					_gpuBakerLongRun = baker;
					Console.WriteLine("[probe] probe-gi rounds: GPU (compute)");

					// DECA_PROBE_DEBUGOVERLAY=1 - probe debug view smoke: compiles its shaders/PSO
					// and draws into the run, the only headless way to catch a shader error early.
					if (Environment.GetEnvironmentVariable("DECA_PROBE_DEBUGOVERLAY") == "1")
					{
						var overlay = new ProbeDebugOverlay(env.DilApi, api, env.BatchRenderer,
							session, ProbeTextures!,
							env.Pipeline.Targets?.RenderColorFormat
								?? TextureObjectFormat.R8G8B8A8UNorm);
						env.Pipeline.InlineOverlay = overlay.Draw;
						env.Pipeline.InvalidateGraph();
						Console.WriteLine(
							$"[probe] debug overlay: {session.ProbeCount} probes, shaders compiled");
					}
				}

				// Pure convergence time with no per-frame pacing: a lower bound for the editor.
				var swConverge = System.Diagnostics.Stopwatch.StartNew();
				int chunkCount = 0;

				// Explicit condition, not Converged: realtime never converges and would hang here.
				while (!session.NoGeometry && session.Round < session.TargetRounds)
				{
					if (gpuRound != null)
					{
						while (!gpuRound.RunRound(session, baker,
							ProbeGiBaker.RoundRayDirections(session),
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

				// The cache is built by the first round, so it is reported after the run.
				if (session.Surface != null)
				{
					var surface = session.Surface;
					long grid = (long)surface.CountX * surface.CountY * surface.CountZ;
					Console.WriteLine($"[probe] surface cache: {surface.VoxelCount} voxels over {grid} cells " +
						$"({100.0 * surface.VoxelCount / Math.Max(grid, 1):F1}% filled), " +
						$"voxel {surface.Voxel.X:F2}");
				}

				// On the GPU path the atlases are already filled; Snapshot only feeds the report.
				var bake = baker.Snapshot(session);
				if (gpuRound == null)
				{
					ProbeTextures = new ProbeGiTextures(api, bake, "_probeGiCli");
					ProbeTextures.Bind(model);
				}
				Console.WriteLine($"[probe] probe-gi: {bake.CountX}x{bake.CountY}x{bake.CountZ} dense grid, " +
					$"{bake.ProbeCount} probes, atlas {bake.ShWidth}x{bake.ShHeight}, " +
					$"baked in {sw.ElapsedMilliseconds} ms");

				// Sh0/Sh1/Sh2 alphas hold sky visibility, validity and sun fraction.
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

		foreach (var (mode, channel, name) in new[] { (3, 0, "lighting"), (3, 0, "lighting_flat"), (3, 8, "debug_ambient"), (3, 7, "debug_direct"), (3, 6, "debug_envspec"), (3, 9, "debug_probes"), (1, 0, "highlight"), (2, 0, "channel_normal") })
		{
			// "lighting_flat": same lighting mode on a forced white untextured dielectric.
			PushPreviewSettings(model, mode, channel, forceFlatWhite: name != "lighting");

			// Debug stages write display-ready values, so tonemapping must pass them through.
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

			// Composite over the same backdrop as ModelPreviewViewport.Render: target clears alpha 0.
			CompositeOverBackdrop(pixels, width, height);

			var pngPath = Path.Combine(outDir, $"probe_{name}.png");
			PngWriter.Write(pngPath, pixels, width, height);
		}

		// DECA_PROBE_PUNCTUALDEBUG=1 - channel 11: per-pixel punctual shadow sampling breakdown.
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

			// Color decode mirrors channel 11 in UnlitInstancedPS.hlsl: magenta = branch not taken,
			// cyan = UV outside [0,1], orange = past the far plane, grey = sampled shadowLit.
			// Background (alpha < 128) must be excluded: those pixels carry the clear color.
			long noBranch = 0, uvOut = 0, zOut = 0, sampled = 0, litSum = 0, geomPx = 0;
			for (int i = 0; i < dbgPixels.Length; i += 4)
			{
				if (dbgPixels[i + 3] < 128) continue;
				geomPx++;
				byte r = dbgPixels[i], g = dbgPixels[i + 1], b = dbgPixels[i + 2];
				if (r > 200 && g < 50 && b > 200) noBranch++;
				else if (r < 50 && g > 200 && b > 200) uvOut++;
				else if (r > 200 && g is > 100 and < 180 && b < 50) zOut++;
				else { sampled++; litSum += r; }
			}

			long totalPx = Math.Max(geomPx, 1);
			Console.WriteLine($"[probe] punctual debug coverage: geometry={geomPx} px of {dbgW * (long)dbgH} " +
				"(fractions below are of geometry, not of the frame)");
			Console.WriteLine($"[probe] punctual shadow debug (channel 11): no-branch={100.0 * noBranch / totalPx:F1}% " +
				$"uv-out={100.0 * uvOut / totalPx:F1}% z-out(far plane)={100.0 * zOut / totalPx:F1}% " +
				$"sampled={100.0 * sampled / totalPx:F1}% " +
				$"(sampled avg shadowLit={(sampled > 0 ? litSum / (double)sampled / 255.0 : -1):F3}, " +
				"0=covered by an occluder, 1=none found)");

			PngWriter.Write(Path.Combine(outDir, "probe_punctualdebug.png"), dbgPixels, dbgW, dbgH);

			// Recapture debug_direct at the same time/state as channel 11 above, to cross-check it.
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

			// Channel 12: shadow slice index the shader actually picked, encoded r=g=b=slice*16.
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

			// Channel 13: base slice from LightCulling before the cube face offset, encoded as ch12.
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

			// Channel 14: raw ClusterCounts[clusterIdx] before the CLUSTER_MAX_LIGHTS clamp.
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

			// Channel 15: how far shadowUv falls outside [0,1] at channel 11's cyan pixels.
			PushPreviewSettings(model, 3, 15, forceFlatWhite: false);
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
			var excessPixels = DiligentTextureReadback.ReadRgba8(env.DilApi, (DiligentRenderTarget)env.ColorTarget,
				out var ew, out var eh);

			long excessCount = 0, excessMarginal = 0;
			byte excessMaxByte = 0;
			double excessSum = 0;
			for (int i = 0; i < excessPixels.Length; i += 4)
			{
				byte r = excessPixels[i];
				if (r == 0) continue;
				excessCount++;
				excessSum += r / 255.0 * 2.0;
				if (r > excessMaxByte) excessMaxByte = r;
				// "marginal" = under 0.05 past the edge, what the 91.8 deg face overlap absorbs.
				if (r / 255.0 * 2.0 < 0.05) excessMarginal++;
			}
			double excessMax = excessMaxByte / 255.0 * 2.0;
			Console.WriteLine($"[probe] punctual shadowUv excess magnitude (channel 15): " +
				$"cyan-with-data={excessCount} px, avg excess={(excessCount > 0 ? excessSum / excessCount : 0):F4}, " +
				$"max excess={excessMax:F4}, marginal(<0.05)={(excessCount > 0 ? 100.0 * excessMarginal / excessCount : 0):F1}% of cyan px");
			PngWriter.Write(Path.Combine(outDir, "probe_punctualexcess.png"), excessPixels, ew, eh);

			// Channel 16: slice index as in ch12/13, but only at channel 11's cyan pixels.
			PushPreviewSettings(model, 3, 16, forceFlatWhite: false);
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
			var cyanSlicePixels = DiligentTextureReadback.ReadRgba8(env.DilApi, (DiligentRenderTarget)env.ColorTarget,
				out var csw, out var csh);

			var cyanSliceHistogram = new long[16];
			long cyanSliceNoBranch = 0, cyanSliceBlack = 0;
			for (int i = 0; i < cyanSlicePixels.Length; i += 4)
			{
				byte r = cyanSlicePixels[i], g = cyanSlicePixels[i + 1], b = cyanSlicePixels[i + 2];
				if (r == 0 && g == 0 && b == 0) { cyanSliceBlack++; continue; }
				if (r > 200 && g < 50 && b > 200) { cyanSliceNoBranch++; continue; }
				int slice = (int)Math.Round(r / 16.0);
				if (slice is >= 0 and < 16) cyanSliceHistogram[slice]++;
			}
			long cyanSliceTotalPx = csw * (long)csh;
			var cyanSliceHistStr = string.Join(", ", cyanSliceHistogram.Select((count, idx) => $"{idx}:{100.0 * count / cyanSliceTotalPx:F2}%")
				.Where((_, idx) => cyanSliceHistogram[idx] > 0));
			Console.WriteLine($"[probe] slice index AT cyan pixels only (channel 16): non-cyan(black)={100.0 * cyanSliceBlack / cyanSliceTotalPx:F1}% " +
				$"per-slice pixel share among cyan: {(cyanSliceHistStr.Length > 0 ? cyanSliceHistStr : "(none)")}");
			PngWriter.Write(Path.Combine(outDir, "probe_punctualcyanslice.png"), cyanSlicePixels, csw, csh);

			// Channel 17: raw shadowUv.xy at cyan pixels, r=(u/8+0.5), g=(v/8+0.5), black = not cyan.
			PushPreviewSettings(model, 3, 17, forceFlatWhite: false);
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
			var uvPixels = DiligentTextureReadback.ReadRgba8(env.DilApi, (DiligentRenderTarget)env.ColorTarget,
				out var uvw, out var uvh);

			double uMin = double.MaxValue, uMax = double.MinValue, uSum = 0;
			double vMin = double.MaxValue, vMax = double.MinValue, vSum = 0;
			long uvCount = 0;
			for (int i = 0; i < uvPixels.Length; i += 4)
			{
				byte r = uvPixels[i], g = uvPixels[i + 1];
				if (r == 0 && g == 0) continue;
				double u = (r / 255.0 - 0.5) * 8.0;
				double v = (g / 255.0 - 0.5) * 8.0;
				uMin = Math.Min(uMin, u); uMax = Math.Max(uMax, u); uSum += u;
				vMin = Math.Min(vMin, v); vMax = Math.Max(vMax, v); vSum += v;
				uvCount++;
			}
			Console.WriteLine($"[probe] raw shadowUv at cyan pixels (channel 17): n={uvCount} " +
				$"u: min={uMin:F3} max={uMax:F3} avg={(uvCount > 0 ? uSum / uvCount : 0):F3}, " +
				$"v: min={vMin:F3} max={vMax:F3} avg={(uvCount > 0 ? vSum / uvCount : 0):F3}");
			PngWriter.Write(Path.Combine(outDir, "probe_punctualuv.png"), uvPixels, uvw, uvh);

			// Channel 18: toFrag at cyan pixels, r/g/b=(toFrag.xyz/16+0.5), for CPU face recompute.
			PushPreviewSettings(model, 3, 18, forceFlatWhite: false);
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
			var toFragPixels = DiligentTextureReadback.ReadRgba8(env.DilApi, (DiligentRenderTarget)env.ColorTarget,
				out var tfw, out var tfh);

			var expectedFaceHistogram = new long[6];
			long toFragCount = 0;
			for (int i = 0; i < toFragPixels.Length; i += 4)
			{
				byte r = toFragPixels[i], g = toFragPixels[i + 1], b = toFragPixels[i + 2];
				if (r == 0 && g == 0 && b == 0) continue;
				float tx = (r / 255f - 0.5f) * 16f;
				float ty = (g / 255f - 0.5f) * 16f;
				float tz = (b / 255f - 0.5f) * 16f;
				float ax = MathF.Abs(tx), ay = MathF.Abs(ty), az = MathF.Abs(tz);
				int face = ax >= ay && ax >= az ? (tx > 0 ? 0 : 1)
					: ay >= az ? (ty > 0 ? 2 : 3)
					: (tz > 0 ? 4 : 5);
				expectedFaceHistogram[face]++;
				toFragCount++;
			}
			var expectedFaceStr = string.Join(", ", expectedFaceHistogram.Select((count, idx) => $"{idx}:{100.0 * count / Math.Max(1, toFragCount):F1}%")
				.Where((_, idx) => expectedFaceHistogram[idx] > 0));
			Console.WriteLine($"[probe] expected face from CPU-recomputed toFrag at cyan pixels (channel 18): n={toFragCount} " +
				$"per-face share: {(expectedFaceStr.Length > 0 ? expectedFaceStr : "(none)")}");
			PngWriter.Write(Path.Combine(outDir, "probe_punctualtofrag.png"), toFragPixels, tfw, tfh);

			env.SetTonemapPassthrough(false);
		}

		// DECA_PROBE_CHANNELS=<comma-separated> - capture arbitrary UnlitInstancedPS debug channels,
		// one PNG each plus a 12-bin color histogram. Background (alpha < 128) is excluded.
		var channelList = Environment.GetEnvironmentVariable("DECA_PROBE_CHANNELS");
		if (!string.IsNullOrWhiteSpace(channelList))
		{
			var channels = channelList
				.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Select(s => int.TryParse(s, out var n) ? n : -1)
				.Where(n => n >= 0)
				.ToArray();

			env.SetTonemapPassthrough(true);

			foreach (var channel in channels)
			{
				PushPreviewSettings(model, 3, channel, forceFlatWhite: false);
				env.Pipeline.InvalidateGraph();
				env.DilApi.ImmediateContext.Flush();
				env.DilApi.ImmediateContext.WaitForIdle();

				for (int frame = 0; frame < 3; frame++)
				{
					time += 1f / 60f;
					env.SetEyeAdaptationDeltaTime(1f / 60f);
					env.Root.Update(new UpdateTick(1f / 60f, time));
					env.Pipeline.Execute();
				}

				env.DilApi.ImmediateContext.Flush();
				env.DilApi.ImmediateContext.WaitForIdle();

				var chPixels = DiligentTextureReadback.ReadRgba8(env.DilApi,
					(DiligentRenderTarget)env.ColorTarget, out var chW, out var chH);
				PngWriter.Write(Path.Combine(outDir, $"probe_channel{channel}.png"), chPixels, chW, chH);

				var bins = new Dictionary<string, long>();
				long chGeom = 0;
				for (int i = 0; i < chPixels.Length; i += 4)
				{
					if (chPixels[i + 3] < 128) continue;
					chGeom++;
					bins[ColorBin(chPixels[i], chPixels[i + 1], chPixels[i + 2])] =
						bins.GetValueOrDefault(ColorBin(chPixels[i], chPixels[i + 1], chPixels[i + 2])) + 1;
				}

				var top = bins.OrderByDescending(kv => kv.Value).Take(6)
					.Select(kv => $"{kv.Key} {100.0 * kv.Value / Math.Max(1, chGeom):F1}%");
				Console.WriteLine($"[probe] channel {channel}: geometry={chGeom} px of {chW * (long)chH}, " +
					$"colors: {string.Join(", ", top)}");
			}

			env.SetTonemapPassthrough(false);
		}

		// DECA_PROBE_SHARPNESS=1 - mean luminance gradient after 48 static frames; only meaningful
		// when compared across runs (full res vs RENDERSCALE=0.5 vs RENDERSCALE=0.5 + TAAU).
		if (Environment.GetEnvironmentVariable("DECA_PROBE_SHARPNESS") == "1")
		{
			// Back to Lighting mode: the shot loop ends on channel_normal, which samples no textures.
			PushPreviewSettings(model, 3, 0, forceFlatWhite: false);
			env.SetTonemapPassthrough(false);
			env.DilApi.ImmediateContext.Flush();
			env.DilApi.ImmediateContext.WaitForIdle();
			env.Pipeline.InvalidateGraph();

			// DECA_PROBE_SHARPMOVE=1 - orbit slowly for 40 frames, then hold 8: temporal heuristics
			// degenerate on perfectly static scenes, which no real game frame ever is.
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
				$"({sw}x{sh}, 48 convergence frames)");
			PngWriter.Write(Path.Combine(outDir, "probe_sharpness.png"), sharpPixels, sw, sh);
		}

		// DECA_PROBE_MOTIONSHIFT=<degrees> - positive control for motion vectors: rotate the camera,
		// then check that frame A warped by prevUV = curUV + motion matches frame B.
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

			// Camera B rotates the gaze only: pure rotation shifts the whole frame, sky included,
			// so the test does not depend on scene depth.
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

			// Order A -> B -> A -> B: the last step repeats the same frame pair, hence the same
			// reprojection matrix; an extra Execute would latch B over B and zero the vectors.
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

			// Frame margins dropped: near the edge the shift samples out of bounds (disocclusion).
			const int margin = 24;
			for (int y = margin; y < mh - margin; y++)
			{
				for (int x = margin; x < mw - margin; x++)
				{
					int i = (y * mw + x) * 4;

					// The shader zeroes blue where the vector left the scale range: decoding those
					// pixels would clip and understate the shift.
					if (motionRgba[i + 2] < 64)
					{
						clipped++;
						continue;
					}

					// Debug view scale is in RENDER-resolution pixels while the warp is in display
					// pixels, so scale by the size ratio.
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
					$"({(naiveSad > 0 ? (1.0 - warpedSad / naiveSad) * 100.0 : 0.0):F1}% better; " +
					"a negative value = the vector points THE WRONG WAY)");
			}
			else
			{
				Console.WriteLine("[probe] motion shift: not a single usable pixel was collected " +
					$"(clipped={clipped}) - raise DECA_PROBE_MOTIONRANGE");
			}
		}

		// DECA_PROBE_JITTER=1 - three checks on sub-pixel projection jitter: two static frames must
		// match bit-for-bit without jitter, must differ with it, and the motion vector debug frame
		// must stay exactly grey (vectors latch the pre-jitter matrix).
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
				"(must be exactly 0: rendering is deterministic)");
			Console.WriteLine($"[probe] jitter on:  frame delta SAD={Sad(jit0, jit1):F4} " +
				"(must be > 0: the projection offset reached the GPU)");

			if (Environment.GetEnvironmentVariable("DECA_PROBE_MOTION") == "1")
			{
				// Small scale range on purpose: a sub-pixel leak would hide inside one 8-bit step
				// at the default 16 px.
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
					$"({maxDev * leakRangePx / 127f:F3} px; 0-1 = clean, more = jitter leaked into the vectors)");
			}

			env.SetTemporalJitter(Environment.GetEnvironmentVariable("DECA_PROBE_JITTERKEEP") == "1");
		}

		// DECA_PROBE_SHADOWDUMP=1 - dump cascade shadow map slices as PNG plus depth stats. Two PNGs
		// per cascade: raw depth (0..1) and one stretched over the non-empty range.
		var shadowDumpMode = Environment.GetEnvironmentVariable("DECA_PROBE_SHADOWDUMP");
		if (shadows && (shadowDumpMode == "1" || shadowDumpMode == "2"))
		{
			env.DilApi.ImmediateContext.Flush();
			env.DilApi.ImmediateContext.WaitForIdle();

			var shadowTarget = (DiligentRenderTarget)env.BatchRenderer.WorldShadowRenderer.ShadowMapsTarget;

			// Mode 2 tests the readback path: each slice is cleared to its own depth, bypassing
			// rendering. Distinct values back means readback is fine and writes are at fault.
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

			// PNGs are downsampled; the float stats below still run over every texel.
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
						// In norm, cleared texels (1.0) stay white and geometry stretches over 0..0.9.
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

		// DECA_PROBE_SHADOWDUMP=3 - same dump for PunctualShadowMaps; before the first punctual draw
		// the array is still a 1x1 placeholder, so the readback honestly returns one 1x1 slice.
		if (shadows && shadowDumpMode == "3")
		{
			env.DilApi.ImmediateContext.Flush();
			env.DilApi.ImmediateContext.WaitForIdle();

			var punctualTarget = (DiligentRenderTarget)env.BatchRenderer.WorldShadowRenderer.PunctualShadowMapsTarget;
			var pSlices = DiligentTextureReadback.ReadFloatSlices(env.DilApi, punctualTarget,
				out var pWidth, out var pHeight);

			Console.WriteLine($"[probe] punctual shadow maps: {pSlices.Length} slice(s), {pWidth}x{pHeight} " +
				$"(1x1 = the real array has not been created yet, see EnsurePunctualShadowMaps)");

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

		// DECA_PROBE_FRAMES=<N> - long run of N frames without resize, logging every 100; repro for
		// cumulative pool exhaustion (descriptors / upload heap).
		int longRunFrames = int.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_FRAMES"), out var parsedFrames)
			? parsedFrames
			: 0;
		if (longRunFrames > 0)
		{
			Console.WriteLine($"[probe] long run: {longRunFrames} frames...");
			// Wall clock is the only handle on pixel-shader cost (probe-GI sampling); coarse, so
			// compare only the same run before and after a change.
			var swLongRun = System.Diagnostics.Stopwatch.StartNew();
			for (int frame = 0; frame < longRunFrames; frame++)
			{
				// Mirrors the editor driver: one round per frame, fence-throttled. Convergence is
				// deliberately not checked - worst case, rounds never stop.
				if (_gpuRoundLongRun != null && _gpuSessionLongRun != null && _gpuBakerLongRun != null)
				{
					// DECA_PROBE_GPUNOFENCE=1 - issue rounds without waiting for the previous one.
					if (_gpuRoundLongRun.IsReady ||
						Environment.GetEnvironmentVariable("DECA_PROBE_GPUNOFENCE") == "1")
					{
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

				// Present drives Diligent's internal frame cycle: without it the dynamic pools and
				// descriptor heaps never recycle.
				api.Present();

				if (frame % 100 == 0)
				{
					// Idle check every hundred frames: if the GPU hung, WaitForIdle never returns and
					// the last log line names the frame range.
					env.DilApi.ImmediateContext.Flush();
					env.DilApi.ImmediateContext.WaitForIdle();
					Console.WriteLine($"[probe] long run frame {frame} ok ({GC.GetTotalMemory(false) / (1024 * 1024)} MB managed)");
				}
			}

			env.DilApi.ImmediateContext.Flush();
			env.DilApi.ImmediateContext.WaitForIdle();
			double frameMs = swLongRun.Elapsed.TotalMilliseconds / Math.Max(longRunFrames, 1);
			Console.WriteLine($"[probe] long run complete in {swLongRun.ElapsedMilliseconds} ms " +
				$"({frameMs:F2} ms/frame)" +
				(_gpuRoundLongRun != null
					? $"; gpu rounds run {_gpuRoundsRun}, skipped by fence {_gpuRoundsSkipped}"
					: string.Empty));
		}

		// DECA_PROBE_RESIZE=1 - mirror of ModelPreviewViewport.ResizeTargets minus the ImGui binding,
		// so the AO + resize GPU hang can be reproduced under the debug layer without UI.
		if (Environment.GetEnvironmentVariable("DECA_PROBE_RESIZE") == "1")
		{
			var newSize = new Vector2(768, 640);
			Console.WriteLine($"[probe] resize -> {newSize.X}x{newSize.Y}");

			env.DilApi.ImmediateContext.Flush();
			env.DilApi.ImmediateContext.WaitForIdle();

			env.ColorTarget.Resize(newSize);
			env.DepthTarget.Resize(newSize);
			env.SceneCopyTarget.Resize(newSize);
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

		// Counted after all frames: PSOs are created lazily in the first SetPipelineState.
		Console.WriteLine($"[probe] final compile: {DecaEngine.Graphics.Diligent.DiligentShader.DiagCompileCalls} calls, " +
			$"{DecaEngine.Graphics.Diligent.DiligentShader.DiagCompileActual} ACTUAL, " +
			$"{DecaEngine.Graphics.Diligent.DiligentShader.DiagCompileMs} ms");

		var psoByName = DecaEngine.Graphics.Diligent.DiligentPsoManager.DiagByName;
		Console.WriteLine($"[probe] final pso: {DecaEngine.Graphics.Diligent.DiligentPsoManager.DiagCreateCount} created, " +
			$"{DecaEngine.Graphics.Diligent.DiligentPsoManager.DiagCreateMs} ms, " +
			$"{psoByName.Count} UNIQUE names, " +
			$"{DecaEngine.Graphics.Diligent.DiligentPsoManager.DiagSharedHits} reuses");

		// DECA_PSO_DIAG=1 - per-name breakdown of PSO creation.
		if (Environment.GetEnvironmentVariable("DECA_PSO_DIAG") == "1")
		{
			foreach (var entry in psoByName.OrderByDescending(e => e.Value.Ms))
			{
				Console.WriteLine($"[probe]   pso x{entry.Value.Count} {entry.Value.Ms,5} ms  {entry.Key}");
			}
		}

		Console.WriteLine("[probe] done");
		Environment.Exit(0);
	}

	// Coarse hue bin, not exact RGB: ramp channels would degenerate into thousands of 0.1% bins.
	private static string ColorBin(byte r, byte g, byte b)
	{
		if (r > 200 && g < 60 && b > 200) return "magenta(no-branch)";
		if (r < 40 && g < 40 && b < 40) return "black";
		if (r > 215 && g > 215 && b > 215) return "white";

		int max = Math.Max(r, Math.Max(g, b));
		int min = Math.Min(r, Math.Min(g, b));
		if (max - min < 30) return $"grey~{max / 32 * 32}";

		string hue =
			  r == max && g >= b + 60 ? "yellow"
			: r == max && b >= g + 60 ? "magenta-ish"
			: r == max ? "red"
			: g == max && b >= r + 60 ? "cyan"
			: g == max ? "green"
			: b == max && r >= g + 60 ? "violet"
			: "blue";
		string level = max > 200 ? "bright" : max > 110 ? "mid" : "dim";
		return $"{level}-{hue}";
	}

	private static int ProbeFeatureFlags = (int)PreviewFeatureFlags.All;

	// Probe-GI atlases of the current run; null means disabled.
	private static ProbeGiTextures? ProbeTextures;


	// GPU path for the long run (DECA_PROBE_FRAMES): one round per frame, as the editor does.
	private static ProbeRoundGpu? _gpuRoundLongRun;
	private static ProbeGiBakeSession? _gpuSessionLongRun;
	private static ProbeGiBaker? _gpuBakerLongRun;
	private static int _gpuRoundsRun, _gpuRoundsSkipped;

	private static Vector4? GiParamsOverride;

	private static float? SkyShadowFloorOverride;

	// Environment yaw in radians, mirroring PreviewShadowSettings.EnvYawRadians.
	private static float EnvYaw;

	private static void PushPreviewSettings(ModelLoader model, int mode, int channel,
		bool forceFlatWhite = false)
	{
		// The CLI fills this cbuffer independently of the viewport: every field must be set here too.
		var data = new PreviewSettingsData
		{
			Mode = mode,
			Channel = channel,
			EnvYawRadians = EnvYaw,
			ToneCurve = int.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_CURVE"), out var curve)
				? curve
				: 0,
		};

		// Grid pushed through the same helper as the viewports, so no field can drift.
		if (ProbeTextures != null)
		{
			ProbeGiViewportShared.PushGrid(ref data, ProbeTextures,
				// Must match the EditorSettings bias defaults or the luminance metric is incomparable.
				normalBias: 0.3f, viewBias: 1f);
		}

		// Defaults mirror EditorSettings; zeros would mean hard occlusion floors instead of 0.3/0.2.
		// DECA_PROBE_GIPARAMS="x,y,z,w" - shadow floor, specular floor, sun intensity, ambient boost.
		data.ProbeGiParams = GiParamsOverride ?? new Vector4(0.3f, 0.2f, 2f, 1f);
		// DECA_PROBE_GIPARAMS2=<x> - sky ambient shadow floor; y = visibility octa-map side.
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
			data.Emissive = pbr.EmissiveFactor;
			data.AlphaBlend = pbr.IsSoftBlend ? 1 : 0;

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

	// Luminance summary over geometry only; background is cut by alpha 0 from the target clear.
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

			// On a neutral backdrop with colorless glass, any nonzero chroma is a dispersion fringe.
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
