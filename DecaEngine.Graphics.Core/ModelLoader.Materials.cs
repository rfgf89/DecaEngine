using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using SharpGLTF.Schema2;
using DecaEngine.Core;
using DecaEngine.Graphics.Assets;
using System.Runtime.InteropServices;
using Diligent;
using MeshOptimizer;
using StbImageSharp;
using UnsafeCollections.Collections.Unsafe;
using DecaEngine.Animation;

namespace DecaEngine.Graphics;

/// <summary>Материалы и шейдеры: кейворды, прекомпиляция вариантов, дополнительные наборы материалов. Часть <see cref="ModelLoader"/> - файл на фазу; состояние,
/// точки входа загрузки и Release живут в основном файле.</summary>
public partial class ModelLoader
{
	/// <summary>Shader-кейворды материала по сырому <see cref="PreparedMaterial"/> - единственный
	/// источник истины и для финализации (локальный BuildMaterialKeywords внутри
	/// <see cref="BuildFromPreparedIncremental"/>), и для фоновой прекомпиляции
	/// (<see cref="PrecompileShaderVariants"/>): разойдись наборы, прекомпиляция грела бы не те
	/// варианты, и финализация снова компилировала бы синхронно на GPU-потоке.
	/// pm == null - встроенный дефолтный материал (без текстур/расширений).</summary>
	private static List<string> BuildKeywordsFromPrepared(ModelLoadOptions options, PreparedMaterial pm)
	{
		var keywords = new List<string>();

		if (options.PreviewLightingFeatures)
		{
			keywords.Add("FEATURE_NORMAL_MAPS");
			keywords.Add("FEATURE_OCCLUSION");
			keywords.Add("FEATURE_SHADOWS");
		}

		// Теневые лучи по TLAS - вариант компилируется DXC/SM6.5 (см. DiligentShader) и требует
		// привязанного TLAS; включается только на устройстве с inline-трассировкой.
		if (options.RtShadows)
		{
			keywords.Add("FEATURE_RT_SHADOWS");
		}

		// Тонкий G-buffer отражений вторым/третьим MRT-слотом (см. ModelLoadOptions.ReflectionGbuffer).
		if (options.ReflectionGbuffer)
		{
			keywords.Add("FEATURE_REFLECTION_GBUFFER");
		}

		if (pm == null)
		{
			return keywords;
		}

		if (pm.BaseColorTexture != null)
		{
			keywords.Add("HAS_BASECOLOR_TEXTURE");
		}
		if (pm.MetallicRoughnessTexture != null)
		{
			keywords.Add("HAS_MR_TEXTURE");
		}
		if (pm.AlphaCutoff > 0f)
		{
			keywords.Add("MATERIAL_ALPHA_CLIP");
		}
		if (pm.TransmissionFactor > 0f)
		{
			keywords.Add("MATERIAL_TRANSMISSION");
			if (pm.Dispersion > 0f)
			{
				keywords.Add("MATERIAL_DISPERSION");
			}
		}
		if (pm.SheenColorFactor != Vector3.Zero)
		{
			keywords.Add("MATERIAL_SHEEN");
		}

		return keywords;
	}

	/// <summary>Компилирует шейдер-варианты модели ЕЩЁ В ФОНОВОЙ фазе загрузки (см.
	/// ModelLoadRequest): наборы кейвордов известны сразу после парса материалов, а создание
	/// ресурсов у IRenderDevice, в отличие от контекстов, потокобезопасно. Без этого компиляция
	/// происходила лениво - из DiligentMaterial.SetShader во время финализации, то есть синхронно
	/// на GPU-потоке: секунды фриза на КАЖДЫЙ ещё не виденный вариант UnlitInstancedPS (12+ с у
	/// Sponza при холодном кеше байткода, см. DiligentShaderBytecodeCache). Здесь же варианты
	/// компилируются параллельно, пока грузятся текстуры, и финализации остаётся готовый
	/// нативный объект из общего кэша (CreateSharedShader выдаёт ТОТ ЖЕ экземпляр - ключ кэша
	/// совпадает с ключом, который потом соберёт GetPixelShaderVariant).
	///
	/// Материалы-клоны не-треугольных топологий не греются: их вершинный шейдер зависит от
	/// топологии (см. BuildTopologyClones), встречаются они редко и компилируются по-старому.</summary>
	private static void PrecompileShaderVariants(IGraphicsApi graphicsApi, ModelLoadOptions options,
		PreparedModel prepared, CancellationToken cancellationToken)
	{
		var (vsFactoryPath, vsFileName) = options.VertexShader.ToShaderFactoryParts();
		var (psFactoryPath, psFileName) = options.PixelShader.ToShaderFactoryParts();

		var shaders = new List<IShaderObject>
		{
			// Кейворды вершинника - как в финализации (DXC-паритет RT-варианта, см. там же).
			graphicsApi.CreateSharedShader("Model Vertex Shader", vsFactoryPath, vsFileName,
				ShaderObjectType.Vertex,
				keywords: options.RtShadows ? new[] { "FEATURE_RT_SHADOWS" } : null)
		};

		var seenVariants = new HashSet<string>(StringComparer.Ordinal);
		void AddVariant(PreparedMaterial pm)
		{
			var keywords = BuildKeywordsFromPrepared(options, pm);
			keywords.Sort(StringComparer.Ordinal);
			var cacheKey = string.Join(";", keywords);
			if (!seenVariants.Add(cacheKey))
			{
				return;
			}

			shaders.Add(graphicsApi.CreateSharedShader(
				cacheKey.Length == 0 ? "Model Pixel Shader" : $"Model Pixel Shader [{cacheKey}]",
				psFactoryPath, psFileName, ShaderObjectType.Pixel, "Main", keywords.ToArray()));
		}

		// null-материалы модели получают встроенный дефолтный (см. BuildFromPreparedIncremental) -
		// его вариант нужен всегда.
		AddVariant(null);
		foreach (var pm in prepared.Materials)
		{
			if (!pm.IsNull)
			{
				AddVariant(pm);
			}
		}

		// Параллельно: вариантов единицы, но холодный стоит секунды - последовательный прогрев
		// растягивал бы фоновую фазу почти на их сумму. Compile идемпотентен и сам держит замок
		// экземпляра, отмена проверяется на входе в каждый элемент.
		Parallel.ForEach(shaders, new ParallelOptions { CancellationToken = cancellationToken },
			shader => shader.Compile());
	}

	/// <summary>Те же shader-кейворды, что и <see cref="BuildKeywordsFromPrepared"/>, но выведенные
	/// из уже посчитанных <see cref="MaterialPbrFactors"/> вместо сырого <see cref="PreparedMaterial"/>
	/// (которого больше нет - PrepareModel-данные живут только до конца ПЕРВОЙ финализации, см.
	/// ModelLoadRequest.FinalizeChunk).
	/// pbr == null - встроенный дефолтный материал (материал-клон без источника), как и pm == null там.</summary>
	private static List<string> BuildKeywordsFromFactors(ModelLoadOptions options, MaterialPbrFactors? pbr)
	{
		var keywords = new List<string>();

		if (options.PreviewLightingFeatures)
		{
			keywords.Add("FEATURE_NORMAL_MAPS");
			keywords.Add("FEATURE_OCCLUSION");
			keywords.Add("FEATURE_SHADOWS");
		}

		// Зеркало BuildKeywordsFromPrepared - наборы обязаны совпадать (см. комментарий там).
		if (options.RtShadows)
		{
			keywords.Add("FEATURE_RT_SHADOWS");
		}

		if (options.ReflectionGbuffer)
		{
			keywords.Add("FEATURE_REFLECTION_GBUFFER");
		}

		if (pbr == null)
		{
			return keywords;
		}

		var f = pbr.Value;
		if (f.HasBaseColorTexture)
		{
			keywords.Add("HAS_BASECOLOR_TEXTURE");
		}
		if (f.HasMetallicRoughnessTexture)
		{
			keywords.Add("HAS_MR_TEXTURE");
		}
		if (f.AlphaCutoff > 0f)
		{
			keywords.Add("MATERIAL_ALPHA_CLIP");
		}
		if (f.TransmissionFactor > 0f)
		{
			keywords.Add("MATERIAL_TRANSMISSION");
			if (f.Dispersion > 0f)
			{
				keywords.Add("MATERIAL_DISPERSION");
			}
		}
		if (new Vector3(f.SheenColorRoughness.X, f.SheenColorRoughness.Y, f.SheenColorRoughness.Z) != Vector3.Zero)
		{
			keywords.Add("MATERIAL_SHEEN");
		}

		return keywords;
	}

	/// <summary>
	/// Builds an ADDITIONAL, independent set of <see cref="IMaterialObject"/>s for an already-loaded
	/// <paramref name="model"/> - for a second (or Nth) viewport/environment that needs its OWN
	/// materials to register into its OWN batch renderer (see <see cref="DiligentBatchRenderer.Register"/>:
	/// registering one material object into a second batch renderer silently steals it from the first -
	/// and PSOs additionally bake per-environment SampleCount/RenderTargetFormats at registration time,
	/// see DiligentBatchRenderer ~930-954).
	///
	/// Does NOT touch the GPU beyond creating small material/PSO objects: shaders come from the
	/// device-wide shared cache (<see cref="IGraphicsApi.CreateSharedShader"/> - calling it again with
	/// the same keys is a cache hit, no recompilation), and textures/samplers are the SAME already-
	/// uploaded GPU objects <paramref name="model"/> owns (see <see cref="MaterialTextureBindings"/>,
	/// <see cref="FallbackWhiteTexture"/> et al.) - nothing is re-decoded or re-uploaded.
	///
	/// A material bound to a texture that is still mid-<see cref="ModelLoadOptions.StreamTextures"/>
	/// picks up whatever quality is CURRENT on the shared <see cref="StreamedTexture"/> entry (not the
	/// stale filler captured when the first set was built - see <see cref="StreamedTexture.Texture"/>),
	/// and registers itself into that entry's <see cref="StreamedTexture.Bindings"/> so future quality
	/// upgrades hot-swap THIS set's SRBs too, exactly like the first one (see
	/// DecaEngine.Editor.ECS.ModelStreamer.PumpTextureUpgrades / ModelStore's equivalent pump).
	///
	/// <paramref name="options"/> MUST have the same <see cref="ModelLoadOptions.Signature"/> the model
	/// was originally loaded with - anisotropy/mip bias/keyword toggles are read here again rather than
	/// re-derived from <paramref name="model"/>, and a mismatch would silently desync the second set
	/// from what its textures/samplers actually are.
	/// </summary>
	public static OrderedDictionary<int, IMaterialObject> BuildAdditionalMaterialSet(IGraphicsApi graphicsApi,
		ModelLoadOptions options, ModelLoader model)
	{
		var (vsFactoryPath, vsFileName) = options.VertexShader.ToShaderFactoryParts();
		var (psFactoryPath, psFileName) = options.PixelShader.ToShaderFactoryParts();

		// Кейворды вершинника - как в финализации (DXC-паритет RT-варианта, см. там же).
		var modelShaderVs = graphicsApi.CreateSharedShader("Model Vertex Shader", vsFactoryPath, vsFileName,
			ShaderObjectType.Vertex,
			keywords: options.RtShadows ? new[] { "FEATURE_RT_SHADOWS" } : null);

		var pixelShaderVariants = new Dictionary<string, IShaderObject>();
		IShaderObject GetPixelShaderVariant(List<string> keywords)
		{
			keywords.Sort(StringComparer.Ordinal);
			var cacheKey = string.Join(";", keywords);
			if (!pixelShaderVariants.TryGetValue(cacheKey, out var shader))
			{
				shader = graphicsApi.CreateSharedShader(
					cacheKey.Length == 0 ? "Model Pixel Shader" : $"Model Pixel Shader [{cacheKey}]",
					psFactoryPath, psFileName, ShaderObjectType.Pixel, "Main", keywords.ToArray());
				pixelShaderVariants[cacheKey] = shader;
			}

			return shader;
		}

		IShaderObject pointShaderVs = null;

		// Биндит один слот из уже загруженных ресурсов модели: реальная привязка (см.
		// MaterialTextureBindings) - тем же СЭМПЛЕРОМ (сэмплеры шарятся между окружениями, см. class-doc
		// у ModelStore) и АКТУАЛЬНОЙ текстурой стрим-записи, если она есть; иначе - тот же общий филлер,
		// каким пользуется первый набор (fallbackTexture параметр).
		void BindShared(IMaterialObject materialObj, string slot, Dictionary<string, BaseColorBinding> slots,
			IGpuTexture fallbackTexture)
		{
			if (slots != null && slots.TryGetValue(slot, out var binding))
			{
				var currentTexture = binding.Stream?.Texture ?? binding.Texture;
				materialObj.SetTexture(slot, currentTexture);
				materialObj.SetSampler(slot + "_sampler", binding.Sampler);
				binding.Stream?.Bindings.Add((materialObj, slot));
				return;
			}

			if (fallbackTexture == null)
			{
				return;
			}

			materialObj.SetTexture(slot, fallbackTexture);
			materialObj.SetImmutableSampler(slot, model.FallbackSampler);
		}

		var result = new OrderedDictionary<int, IMaterialObject>();

		for (int i = 0; i < model.materialObjects.Count; i++)
		{
			var kvp = model.materialObjects.GetAt(i);
			var key = kvp.Key;
			model.MaterialPbr.TryGetValue(key, out var pbr);

			var vs = modelShaderVs;
			if (pbr.Topology == MeshTopologyPoints)
			{
				// PSO с POINT_LIST обязан писать builtin PointSize из VS (см. тот же выбор в
				// BuildFromPreparedIncremental) - тот же именной вариант вершинного шейдера.
				if (pointShaderVs == null && vsFileName == "UnlitInstancedVS.hlsl")
				{
					pointShaderVs = graphicsApi.CreateSharedShader("Model Point Vertex Shader", vsFactoryPath,
						"UnlitInstancedPointVS.hlsl", ShaderObjectType.Vertex);
				}

				vs = pointShaderVs ?? modelShaderVs;
			}

			var materialObj = graphicsApi.CreateMaterial($"Model Material {key} (env clone)");

			// Как и у первого набора: шейдеры - шарёные device-кэшем объекты, Release на них - no-op
			// (см. DiligentShader.IsShared), поэтому этому набору не нужен собственный список owned-
			// шейдеров - освобождать здесь нечего.
			materialObj.OwnsShaders = false;
			materialObj.SetShader(GetPixelShaderVariant(BuildKeywordsFromFactors(options, pbr)), vs);

			model.MaterialTextureBindings.TryGetValue(key, out var slots);

			BindShared(materialObj, "_MainTex", slots, model.FallbackWhiteTexture);
			if (pbr.HasMetallicRoughnessTexture)
			{
				BindShared(materialObj, "_MetallicRoughnessTex", slots, null);
			}
			if (pbr.TransmissionFactor > 0f)
			{
				BindShared(materialObj, "_ThicknessTex", slots, model.FallbackWhiteTexture);
			}
			BindShared(materialObj, "_NormalTex", slots, model.FallbackFlatNormalTexture);
			BindShared(materialObj, "_OcclusionTex", slots, model.FallbackWhiteTexture);

			result.Add(key, materialObj);
		}

		return result;
	}

	private static readonly int VertexSizeBytes = System.Runtime.CompilerServices.Unsafe.SizeOf<Vertex>();

	/// <summary>Вынесено из <see cref="BuildFromPreparedIncremental"/>: unsafe-блок в теле итератора
	/// недопустим, а нативная копия LOD-таблицы обязана жить в неуправляемой памяти для SetLodGroup.</summary>
	private static void UploadLodGroup(IMeshObject meshObj, LodLevel[] lodLevels)
	{
		unsafe
		{
			var lodsNative = UnsafeArray.Allocate<LodLevel>(lodLevels.Length);
			for (int i = 0; i < lodLevels.Length; i++)
			{
				UnsafeArray.Set(lodsNative, i, lodLevels[i]);
			}
			meshObj.SetLodGroup(lodsNative);
		}
	}

	/// <summary>Домножает предвычисленную экспоненту Beer-Lambert (w) на масштаб узла-инстанса -
	/// см. комментарий у materialScales в <see cref="BuildFromPreparedIncremental"/>.</summary>
	private static Vector4 ScaleVolumeAttenuation(PreparedMaterial material, Dictionary<int, float> materialScales, int scaleKey)
	{
		var volume = material.VolumeAttenuation;
		if (volume.W > 0f && materialScales.TryGetValue(scaleKey, out var scale) && scale > 0f)
		{
			volume.W *= scale;
		}

		return volume;
	}

}
