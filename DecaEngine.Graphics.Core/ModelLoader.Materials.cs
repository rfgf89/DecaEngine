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

/// <summary>Materials and shaders: keywords, variant precompilation, additional material sets.</summary>
public partial class ModelLoader
{
	// Single source of truth for both finalization and background precompilation: if the two sets
	// diverge, precompilation warms the wrong variants. pm == null is the built-in default material.
	private static List<string> BuildKeywordsFromPrepared(ModelLoadOptions options, PreparedMaterial pm)
	{
		var keywords = new List<string>();

		if (options.PreviewLightingFeatures)
		{
			keywords.Add("FEATURE_NORMAL_MAPS");
			keywords.Add("FEATURE_OCCLUSION");
			keywords.Add("FEATURE_SHADOWS");
		}

		// TLAS shadow rays: this variant needs DXC/SM6.5 and a bound TLAS, so inline RT only.
		if (options.RtShadows)
		{
			keywords.Add("FEATURE_RT_SHADOWS");
		}

		// Thin reflection G-buffer in the second and third MRT slots.
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
		if (pm.EmissiveTexture != null)
		{
			keywords.Add("HAS_EMISSIVE_TEXTURE");
		}

		return keywords;
	}

	// Compiles shader variants during the BACKGROUND load phase: IRenderDevice resource creation is
	// thread-safe, unlike contexts. Otherwise finalization compiles them synchronously on the GPU
	// thread, freezing seconds per unseen variant. Non-triangle topology clones are not warmed.
	private static void PrecompileShaderVariants(IGraphicsApi graphicsApi, ModelLoadOptions options,
		PreparedModel prepared, CancellationToken cancellationToken)
	{
		var (vsFactoryPath, vsFileName) = options.VertexShader.ToShaderFactoryParts();
		var (psFactoryPath, psFileName) = options.PixelShader.ToShaderFactoryParts();

		var shaders = new List<IShaderObject>
		{
			// VS keywords must match finalization exactly (DXC parity for the RT variant).
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

		// Null materials fall back to the built-in default, so its variant is always needed.
		AddVariant(null);
		foreach (var pm in prepared.Materials)
		{
			if (!pm.IsNull)
			{
				AddVariant(pm);
			}
		}

		// Compile is idempotent and locks per instance, so parallel warming is safe.
		Parallel.ForEach(shaders, new ParallelOptions { CancellationToken = cancellationToken },
			shader => shader.Compile());
	}

	// Same keywords as BuildKeywordsFromPrepared but derived from MaterialPbrFactors, because the
	// PreparedMaterial data only lives until the FIRST finalization. pbr == null is the default.
	private static List<string> BuildKeywordsFromFactors(ModelLoadOptions options, MaterialPbrFactors? pbr)
	{
		var keywords = new List<string>();

		if (options.PreviewLightingFeatures)
		{
			keywords.Add("FEATURE_NORMAL_MAPS");
			keywords.Add("FEATURE_OCCLUSION");
			keywords.Add("FEATURE_SHADOWS");
		}

		// Mirrors BuildKeywordsFromPrepared: the two sets must stay identical.
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
		if (f.HasEmissiveTexture)
		{
			keywords.Add("HAS_EMISSIVE_TEXTURE");
		}

		return keywords;
	}

	/// <summary>Builds an ADDITIONAL, independent material set for an already-loaded model, so a second
	/// viewport can register its own materials: registering one material into a second batch renderer
	/// silently steals it from the first. Shaders and textures are shared, nothing is re-uploaded.
	/// <paramref name="options"/> MUST carry the Signature the model was originally loaded with.</summary>
	public static OrderedDictionary<int, IMaterialObject> BuildAdditionalMaterialSet(IGraphicsApi graphicsApi,
		ModelLoadOptions options, ModelLoader model)
	{
		var (vsFactoryPath, vsFileName) = options.VertexShader.ToShaderFactoryParts();
		var (psFactoryPath, psFileName) = options.PixelShader.ToShaderFactoryParts();

		// VS keywords must match finalization exactly (DXC parity for the RT variant).
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

		// Binds one slot from the model's already-loaded resources, using the shared sampler and the
		// CURRENT streamed texture if one exists, otherwise the same filler the first set uses.
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
				// A POINT_LIST PSO must write the builtin PointSize from the VS.
				if (pointShaderVs == null && vsFileName == "UnlitInstancedVS.hlsl")
				{
					pointShaderVs = graphicsApi.CreateSharedShader("Model Point Vertex Shader", vsFactoryPath,
						"UnlitInstancedPointVS.hlsl", ShaderObjectType.Vertex);
				}

				vs = pointShaderVs ?? modelShaderVs;
			}

			var materialObj = graphicsApi.CreateMaterial($"Model Material {key} (env clone)");

			// Shaders come from the device-wide cache, so Release on them is a no-op.
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
			if (pbr.HasEmissiveTexture)
			{
				BindShared(materialObj, "_EmissiveTex", slots, null);
			}

			result.Add(key, materialObj);
		}

		return result;
	}

	private static readonly int VertexSizeBytes = System.Runtime.CompilerServices.Unsafe.SizeOf<Vertex>();

	// Split out of the iterator body: unsafe blocks are illegal there, and SetLodGroup needs the
	// LOD table in unmanaged memory.
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

	// Scales the precomputed Beer-Lambert exponent (w) by the instance node's scale.
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
