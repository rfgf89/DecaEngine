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

/// <summary>GPU phase: incremental finalization of a PreparedModel into buffers, textures, materials.</summary>
public partial class ModelLoader
{
	// Yields an estimate of bytes uploaded per step; Diligent frees upload-heap pages only on
	// Present, so finalizing a whole model in one frame blows host-visible memory past 2.5 GB.
	// result is only valid once MoveNext returned false.
	private static IEnumerator<long> BuildFromPreparedIncremental(IGraphicsApi graphicsApi, ModelLoadOptions options,
		PreparedModel prepared, ModelLoader result)
	{
		var (vsFactoryPath, vsFileName) = options.VertexShader.ToShaderFactoryParts();
		var (psFactoryPath, psFileName) = options.PixelShader.ToShaderFactoryParts();
		// FEATURE_RT_SHADOWS on the VS too: it switches the compiler to DXC/SM6.5, and D3D12
		// forbids mixing DXBC and DXIL in one PSO.
		var vsKeywords = options.RtShadows ? new[] { "FEATURE_RT_SHADOWS" } : null;
		var modelShaderVs = graphicsApi.CreateSharedShader("Model Vertex Shader", vsFactoryPath, vsFileName,
			ShaderObjectType.Vertex, keywords: vsKeywords);
		result._ownedShaders.Add(modelShaderVs);

		// Pixel variants by shader keyword: material-static effects are compiled out, not branched.
		var pixelShaderVariants = new Dictionary<string, IShaderObject>();

		IShaderObject GetPixelShaderVariant(List<string> keywords)
		{
			keywords.Sort(StringComparer.Ordinal);
			string cacheKey = string.Join(";", keywords);

			if (!pixelShaderVariants.TryGetValue(cacheKey, out var shader))
			{
				var swShader = System.Diagnostics.Stopwatch.StartNew();
				shader = graphicsApi.CreateSharedShader(
					cacheKey.Length == 0 ? "Model Pixel Shader" : $"Model Pixel Shader [{cacheKey}]",
					psFactoryPath, psFileName, ShaderObjectType.Pixel, "Main", keywords.ToArray());
				pixelShaderVariants[cacheKey] = shader;
				result._ownedShaders.Add(shader);

				result._shaderMs += swShader.ElapsedMilliseconds;
				result._shaderVariants++;
			}

			return shader;
		}

		// pm == null is the built-in default material (no textures, no extensions).
		List<string> BuildMaterialKeywords(PreparedMaterial pm) => BuildKeywordsFromPrepared(options, pm);

		var defaultMaterial = graphicsApi.CreateMaterial("Default Material");

		// Shaders are shared and this object is handed to several logical indices, so Release
		// runs once per index - it must not touch shaders.
		defaultMaterial.OwnsShaders = false;
		defaultMaterial.SetShader(GetPixelShaderVariant(BuildMaterialKeywords(null)), modelShaderVs);

		// 1x1 white filler: the PS references texture slots statically, and an unbound descriptor
		// is undefined behavior on Vulkan (VUID-vkCmdDrawIndexedIndirect-None-08114).
		Texture fallbackTexture = null;
		ISamplerObject fallbackSampler = null;

		// Separate normal-map filler: (128,128,255) unpacks to (0,0,1), white would tilt the normal.
		Texture flatNormalTexture = null;

		void EnsureFallbackTextures()
		{
			if (fallbackTexture == null)
			{
				fallbackTexture = new Texture("Model Fallback White", new CpuTextureData
				{
					Name = "Model Fallback White",
					DecodedPixels = new byte[] { 255, 255, 255, 255 },
					DecodedWidth = 1,
					DecodedHeight = 1,
				});
				fallbackTexture.Upload(graphicsApi, true);
				result._ownedTextures.Add(fallbackTexture.GpuHandle);

				fallbackSampler = graphicsApi.CreateSampler(
					name: "Model Fallback Sampler",
					filter: TextureFilter.Point,
					address: TextureAddress.Wrap,
					comparisonFunction: CompFunction.Always,
					border: Vector4.Zero);
			}

			if (flatNormalTexture == null)
			{
				flatNormalTexture = new Texture("Model Fallback Flat Normal", new CpuTextureData
				{
					Name = "Model Fallback Flat Normal",
					DecodedPixels = new byte[] { 128, 128, 255, 255 },
					DecodedWidth = 1,
					DecodedHeight = 1,
				});
				flatNormalTexture.Upload(graphicsApi, true);
				result._ownedTextures.Add(flatNormalTexture.GpuHandle);
			}
		}

		void BindFallbackTexture(IMaterialObject material, string slot)
		{
			if (fallbackTexture == null)
			{
				fallbackTexture = new Texture("Model Fallback White", new CpuTextureData
				{
					Name = "Model Fallback White",
					DecodedPixels = new byte[] { 255, 255, 255, 255 },
					DecodedWidth = 1,
					DecodedHeight = 1,
				});
				fallbackTexture.Upload(graphicsApi, true);
				result._ownedTextures.Add(fallbackTexture.GpuHandle);

				fallbackSampler = graphicsApi.CreateSampler(
					name: "Model Fallback Sampler",
					filter: TextureFilter.Point,
					address: TextureAddress.Wrap,
					comparisonFunction: CompFunction.Always,
					border: Vector4.Zero);
			}

			material.SetTexture(slot, fallbackTexture.GpuHandle);
			material.SetImmutableSampler(slot, fallbackSampler);
		}

		void BindFlatNormalFallback(IMaterialObject material)
		{
			// The white filler is what creates the shared sampler.
			if (fallbackSampler == null)
			{
				BindFallbackTexture(material, "_NormalTex");
			}

			if (flatNormalTexture == null)
			{
				flatNormalTexture = new Texture("Model Fallback Flat Normal", new CpuTextureData
				{
					Name = "Model Fallback Flat Normal",
					DecodedPixels = new byte[] { 128, 128, 255, 255 },
					DecodedWidth = 1,
					DecodedHeight = 1,
				});
				flatNormalTexture.Upload(graphicsApi, true);
				result._ownedTextures.Add(flatNormalTexture.GpuHandle);
			}

			material.SetTexture("_NormalTex", flatNormalTexture.GpuHandle);
			material.SetImmutableSampler("_NormalTex", fallbackSampler);
		}

		BindFallbackTexture(defaultMaterial, "_MainTex");
		BindFallbackTexture(defaultMaterial, "_OcclusionTex");
		BindFlatNormalFallback(defaultMaterial);

		result.FallbackWhiteTexture = fallbackTexture.GpuHandle;
		result.FallbackSampler = fallbackSampler;
		result.FallbackFlatNormalTexture = flatNormalTexture.GpuHandle;

		result.materialObjects.Add(-1, defaultMaterial);

		// The built-in default material is not a glTF material, so the spec's metallic=1 default
		// would make it preview as a dark mirror in Lighting mode - neutral dielectric gray instead.
		var defaultPbr = new MaterialPbrFactors
		{
			BaseColorFactor = Vector4.One,
			MetallicFactor = 0f,
			RoughnessFactor = 0.6f,
			HasBaseColorTexture = false,
			AlphaCutoff = 0f,
			Ior = 1.5f,
			VolumeAttenuation = new Vector4(1f, 1f, 1f, 0f),
			NormalScale = 1f,
			OcclusionStrength = 1f,
			SpecularColorFactor = Vector4.One
		};
		result.MaterialPbr[-1] = defaultPbr;

		yield return 4096;

		static long EstimateMaterialBytes(PreparedMaterial pm)
		{
			if (pm == null)
			{
				return 4096;
			}

			long bytes = 4096;
			bytes += SlotBytes(pm.BaseColorTexture);
			bytes += SlotBytes(pm.MetallicRoughnessTexture);
			bytes += SlotBytes(pm.NormalTexture);
			bytes += SlotBytes(pm.OcclusionTexture);
			bytes += SlotBytes(pm.EmissiveTexture);
			bytes += pm.TransmissionFactor > 0f ? SlotBytes(pm.ThicknessTexture) : 0;
			return bytes;

			// Baked slots carry no pixels but still cost upload time: BC7/BC5 is a byte per texel,
			// plus a third for the mip tail.
			static long SlotBytes(PreparedTexture texture)
			{
				if (texture == null)
				{
					return 0;
				}

				if (texture.Pixels != null)
				{
					return texture.Pixels.Length;
				}

				return texture.CacheKey != null
					? (long)texture.Width * texture.Height * 4 / 3
					: 0;
			}
		}

		// KHR_materials_volume: thickness is in mesh-local units and the spec scales it by the node
		// scale. Thickness is per-material, scale per-instance - take the first instance's scale.
		var materialScales = new Dictionary<int, float>();
		foreach (var instance in prepared.Instances)
		{
			var s = instance.transform.scale;
			materialScales.TryAdd(instance.materialId, (s.X + s.Y + s.Z) / 3f);
		}

		// One source image is shared by several slots/materials (typical ORM): stream it once.
		var streamEntries = new Dictionary<TextureStreamSource, StreamedTexture>();

		var assetCache = options.Cache;

		// Shared by cache key: without this map one .dtex would be uploaded once per reference,
		// so the cache would save load time while inflating VRAM.
		var bakedTextures = new Dictionary<string, IGpuTexture>(StringComparer.Ordinal);

		// Same deduplication for baked streaming entries.
		var bakedStreamEntries = new Dictionary<string, StreamedTexture>(StringComparer.Ordinal);

		// null means the .dtex is gone (cache wiped mid-load).
		IGpuTexture GetOrCreateBakedTexture(string cacheKey, string slot)
		{
			if (bakedTextures.TryGetValue(cacheKey, out var existing))
			{
				return existing;
			}

			if (assetCache == null)
			{
				return null;
			}

			var payload = DtexFile.TryRead(assetCache.TexturePath(cacheKey));
			if (payload == null)
			{
				return null;
			}

			var swBaked = System.Diagnostics.Stopwatch.StartNew();
			var texture = new Texture(slot, payload.ToCpuTextureData(slot));
			texture.Upload(graphicsApi, true);
			result._textureMs += swBaked.ElapsedMilliseconds;
			result._textureCount++;

			result._ownedTextures.Add(texture.GpuHandle);
			bakedTextures[cacheKey] = texture.GpuHandle;
			return texture.GpuHandle;
		}

		// Returns null when the slot got a filler instead of a real texture.
		BaseColorBinding BindPreparedTexture(IMaterialObject materialObj, string slot, PreparedTexture preparedTexture)
		{
			if (preparedTexture == null)
			{
				BindFallbackTexture(materialObj, slot);
				return null;
			}

			// Streaming: no pixels yet, so the slot takes the 1x1 filler and ModelStreamer delivers
			// the first level. Shader keywords are unchanged, so upgrades never touch the PSO.
			if (preparedTexture.StreamSource != null)
			{
				if (!streamEntries.TryGetValue(preparedTexture.StreamSource, out var streamEntry))
				{
					streamEntry = new StreamedTexture
					{
						FilePath = preparedTexture.StreamSource.FilePath,
						EncodedPixels = preparedTexture.StreamSource.EncodedBytes,
						CurrentSize = 0,
						TargetSize = options.MaxTextureSize,
						Texture = null,
						AddressMode = preparedTexture.AddressMode,
						FilterMode = preparedTexture.FilterMode,
					};

					streamEntries[preparedTexture.StreamSource] = streamEntry;
					result.StreamedTextures.Add(streamEntry);
				}

				// The authored sampler is set up front: samplers bake into the PSO layout and
				// cannot be swapped on upgrade.
				EnsureFallbackTextures();
				materialObj.SetTexture(slot, slot == "_NormalTex"
					? flatNormalTexture.GpuHandle
					: fallbackTexture.GpuHandle);

				var streamFilter = preparedTexture.FilterMode == TextureFilter.Linear && options.AnisotropicFiltering
					? TextureFilter.Anisotropic
					: preparedTexture.FilterMode;

				var streamSampler = graphicsApi.CreateSampler(
					name: slot + "_Sampler",
					filter: streamFilter,
					address: preparedTexture.AddressMode,
					comparisonFunction: CompFunction.Always,
					border: Vector4.Zero,
					mipLodBias: options.MipLodBias);

				// Dynamic sampler (on the texture view), not immutable: SetTexture re-attaches it
				// to the new view on hot swap.
				materialObj.SetSampler(slot + "_sampler", streamSampler);
				result._samplerCount++;

				streamEntry.Bindings.Add((materialObj, slot));

				return new BaseColorBinding
				{
					Texture = slot == "_NormalTex" ? flatNormalTexture.GpuHandle : fallbackTexture.GpuHandle,
					Sampler = streamSampler,
					Stream = streamEntry,
				};
			}

			// Baked: the mip chain is on disk ready to upload - no decode, no GPU GenerateMips.
			if (preparedTexture.CacheKey != null)
			{
				var bakedFilter = preparedTexture.FilterMode == TextureFilter.Linear && options.AnisotropicFiltering
					? TextureFilter.Anisotropic
					: preparedTexture.FilterMode;

				var bakedSampler = graphicsApi.CreateSampler(
					name: slot + "_Sampler",
					filter: bakedFilter,
					address: preparedTexture.AddressMode,
					comparisonFunction: CompFunction.Always,
					border: Vector4.Zero,
					mipLodBias: options.MipLodBias);

				result._samplerCount++;

				// Streaming on top of the cache: levels arrive as mip-chain tails straight from
				// the .dtex, so top levels are never read until quality reaches them.
				if (options.StreamTextures && assetCache != null)
				{
					if (!bakedStreamEntries.TryGetValue(preparedTexture.CacheKey, out var bakedStream))
					{
						bakedStream = new StreamedTexture
						{
							DtexPath = assetCache.TexturePath(preparedTexture.CacheKey),
							DtexWidth = preparedTexture.Width,
							DtexHeight = preparedTexture.Height,
							IsBlockCompressed = true,
							CurrentSize = 0,

							// Quality ceiling is the .dtex's own top level, not the import limit:
							// otherwise a small source would read as permanently under-loaded.
							TargetSize = Math.Max(preparedTexture.Width, preparedTexture.Height),
							Texture = null,
							AddressMode = preparedTexture.AddressMode,
							FilterMode = preparedTexture.FilterMode,
						};

						bakedStreamEntries[preparedTexture.CacheKey] = bakedStream;
						result.StreamedTextures.Add(bakedStream);
					}

					EnsureFallbackTextures();
					var filler = slot == "_NormalTex" ? flatNormalTexture.GpuHandle : fallbackTexture.GpuHandle;

					materialObj.SetTexture(slot, filler);
					materialObj.SetSampler(slot + "_sampler", bakedSampler);
					bakedStream.Bindings.Add((materialObj, slot));

					return new BaseColorBinding
					{
						Texture = filler,
						Sampler = bakedSampler,
						Stream = bakedStream,
					};
				}

				var bakedTexture = GetOrCreateBakedTexture(preparedTexture.CacheKey, slot);
				if (bakedTexture == null)
				{
					// .dtex vanished mid-load: a cooked model has no pixels to fall back on, so the
					// slot takes a filler and the next load re-bakes it.
					BindFallbackTexture(materialObj, slot);
					return null;
				}

				materialObj.SetTexture(slot, bakedTexture);
				materialObj.SetSampler(slot + "_sampler", bakedSampler);

				return new BaseColorBinding
				{
					Texture = bakedTexture,
					Sampler = bakedSampler,
				};
			}

			IGpuTexture gpuTexture;
			{
				var cpuData = new CpuTextureData
				{
					Name = slot,
					DecodedPixels = preparedTexture.Pixels,
					DecodedWidth = preparedTexture.Width,
					DecodedHeight = preparedTexture.Height,
				};

				var texture = new Texture(cpuData.Name, cpuData);

				var swUpload = System.Diagnostics.Stopwatch.StartNew();
				texture.Upload(graphicsApi, true);
				result._textureMs += swUpload.ElapsedMilliseconds;
				result._textureCount++;

				gpuTexture = texture.GpuHandle;
				result._ownedTextures.Add(gpuTexture);
			}

			// Linear upgrades to anisotropic; an authored point filter is preserved.
			var filterMode = preparedTexture.FilterMode == TextureFilter.Linear && options.AnisotropicFiltering
				? TextureFilter.Anisotropic
				: preparedTexture.FilterMode;

			var swSampler = System.Diagnostics.Stopwatch.StartNew();
			var samplerObject = graphicsApi.CreateSampler(
				name: slot + "_Sampler",
				filter: filterMode,
				address: preparedTexture.AddressMode,
				comparisonFunction: CompFunction.Always,
				border: Vector4.Zero,
				mipLodBias: options.MipLodBias
			);
			result._samplerMs += swSampler.ElapsedMilliseconds;
			result._samplerCount++;

			materialObj.SetTexture(slot, gpuTexture);

			// Dynamic, not SetImmutableSampler: for batch materials Diligent silently substitutes
			// its default linear-wrap sampler, killing anisotropy and mip bias.
			materialObj.SetSampler(slot + "_sampler", samplerObject);

			return new BaseColorBinding { Texture = gpuTexture, Sampler = samplerObject, Stream = null };
		}

		// Sole writer of result.MaterialTextureBindings; filler bindings are not recorded.
		void TrackBinding(int materialKey, string slot, BaseColorBinding binding)
		{
			if (binding == null)
			{
				return;
			}

			if (!result.MaterialTextureBindings.TryGetValue(materialKey, out var slots))
			{
				slots = new Dictionary<string, BaseColorBinding>();
				result.MaterialTextureBindings[materialKey] = slots;
			}

			slots[slot] = binding;
		}

		// vs is a parameter rather than a second SetShader call: SetShader releases the previously
		// set shaders, and those are shared between materials - a double free.
		IMaterialObject BuildMaterialObject(PreparedMaterial pm, string name, IShaderObject vs, int materialKey,
			out BaseColorBinding baseColor)
		{
			var swCreate = System.Diagnostics.Stopwatch.StartNew();
			var materialObj = graphicsApi.CreateMaterial(name);

			// Shaders are shared across the model's materials; releasing them here double-frees.
			materialObj.OwnsShaders = false;
			result._matCreateMs += swCreate.ElapsedMilliseconds;

			var swSetShader = System.Diagnostics.Stopwatch.StartNew();
			materialObj.SetShader(GetPixelShaderVariant(BuildMaterialKeywords(pm)), vs);
			result._matShaderMs += swSetShader.ElapsedMilliseconds;

			baseColor = BindPreparedTexture(materialObj, "_MainTex", pm.BaseColorTexture);
			TrackBinding(materialKey, "_MainTex", baseColor);

			// Slot exists only under HAS_MR_TEXTURE, so binding a fallback would leave a sampler
			// with no shader resource.
			if (pm.MetallicRoughnessTexture != null)
			{
				TrackBinding(materialKey, "_MetallicRoughnessTex",
					BindPreparedTexture(materialObj, "_MetallicRoughnessTex", pm.MetallicRoughnessTexture));
			}

			// Slot exists only under MATERIAL_TRANSMISSION.
			if (pm.TransmissionFactor > 0f)
			{
				TrackBinding(materialKey, "_ThicknessTex",
					BindPreparedTexture(materialObj, "_ThicknessTex", pm.ThicknessTexture));
			}

			if (pm.NormalTexture != null)
			{
				TrackBinding(materialKey, "_NormalTex",
					BindPreparedTexture(materialObj, "_NormalTex", pm.NormalTexture));
			}
			else
			{
				BindFlatNormalFallback(materialObj);
			}

			// White filler (R=1) reads as "nothing occluded", so no has-flag is needed.
			TrackBinding(materialKey, "_OcclusionTex",
				BindPreparedTexture(materialObj, "_OcclusionTex", pm.OcclusionTexture));

			// Slot exists only under HAS_EMISSIVE_TEXTURE.
			if (pm.EmissiveTexture != null)
			{
				TrackBinding(materialKey, "_EmissiveTex",
					BindPreparedTexture(materialObj, "_EmissiveTex", pm.EmissiveTexture));
			}

			return materialObj;
		}

		// scaleKey is the key instances reference the material by, since materialScales is per-instance.
		MaterialPbrFactors BuildFactors(PreparedMaterial pm, int scaleKey)
		{
			var averageBaseColor = ModelImporter.ComputeAverageBaseColor(pm);
			return new MaterialPbrFactors
			{
			BaseColorFactor = pm.BaseColorFactor,
			AverageBaseColor = new Vector3(averageBaseColor.X, averageBaseColor.Y, averageBaseColor.Z),
			AverageAlpha = averageBaseColor.W,
			MetallicFactor = pm.MetallicFactor,
			RoughnessFactor = pm.RoughnessFactor,
			HasBaseColorTexture = pm.BaseColorTexture != null,
			HasMetallicRoughnessTexture = pm.MetallicRoughnessTexture != null,
			NormalScale = pm.NormalScale,
			OcclusionStrength = pm.OcclusionStrength,
			OcclusionUvSet = pm.OcclusionUvSet,
			UvTransform = pm.UvTransform,
			UvOffset = pm.UvOffset,
			HasUvTransform = pm.HasUvTransform,
			AlphaCutoff = pm.AlphaCutoff,
			AlphaMode = pm.AlphaMode,
			SoftAlphaFraction = pm.SoftAlphaFraction,
			TransmissionFactor = pm.TransmissionFactor,
			Ior = pm.Ior,
			Dispersion = pm.Dispersion,
			SheenColorRoughness = new Vector4(pm.SheenColorFactor, pm.SheenRoughnessFactor),
			SpecularColorFactor = new Vector4(pm.SpecularColorFactor, pm.SpecularFactor),
			EmissiveFactor = pm.EmissiveFactor,
			HasEmissiveTexture = pm.EmissiveTexture != null,
			VolumeAttenuation = ScaleVolumeAttenuation(pm, materialScales, scaleKey),
			ThicknessWorld = pm.ThicknessFactor *
				(materialScales.TryGetValue(scaleKey, out var nodeScale) && nodeScale > 0f ? nodeScale : 1f)
			};
		}

		foreach (var preparedMaterial in prepared.Materials)
		{
			if (preparedMaterial.IsNull)
			{
				result.materialObjects.Add(preparedMaterial.LogicalIndex, defaultMaterial);
				result.MaterialPbr[preparedMaterial.LogicalIndex] = defaultPbr;
				continue;
			}

			var swMat = System.Diagnostics.Stopwatch.StartNew();
			var builtMaterial = BuildMaterialObject(preparedMaterial, preparedMaterial.Name, modelShaderVs,
				preparedMaterial.LogicalIndex, out var builtBaseColor);
			result._materialMs += swMat.ElapsedMilliseconds;
			result._materialCount++;

			if (builtBaseColor != null)
			{
				result.MaterialBaseColor[preparedMaterial.LogicalIndex] = builtBaseColor;
			}

			result.materialObjects.Add(preparedMaterial.LogicalIndex, builtMaterial);
			result.MaterialPbr[preparedMaterial.LogicalIndex] =
				BuildFactors(preparedMaterial, preparedMaterial.LogicalIndex);

			yield return EstimateMaterialBytes(preparedMaterial);
		}

		// Clone materials for non-triangle topologies: same shading, but a separate material object
		// so RegisterModelResources can give it a PSO with the right PrimitiveTopology.
		IShaderObject pointShaderVs = null;

		foreach (var (synthKey, clone) in prepared.TopologyMaterialClones)
		{
			PreparedMaterial source = null;
			if (clone.SourceMaterial >= 0)
			{
				source = prepared.Materials.Find(m => m.LogicalIndex == clone.SourceMaterial && !m.IsNull);
			}

			// A POINT_LIST PSO must write builtin PointSize from the VS
			// (VUID-VkGraphicsPipelineCreateInfo-topology-08773), hence the *PointVS variant.
			var cloneVs = modelShaderVs;
			if (clone.Topology == MeshTopologyPoints)
			{
				if (pointShaderVs == null && vsFileName == "UnlitInstancedVS.hlsl")
				{
					// Same keywords as the main VS, for DXC parity with the RT pixel variant.
					pointShaderVs = graphicsApi.CreateSharedShader("Model Point Vertex Shader", vsFactoryPath,
						"UnlitInstancedPointVS.hlsl", ShaderObjectType.Vertex, keywords: vsKeywords);
					result._ownedShaders.Add(pointShaderVs);
				}

				cloneVs = pointShaderVs ?? modelShaderVs;
			}

			IMaterialObject materialObj;
			MaterialPbrFactors factors;
			if (source == null)
			{
				materialObj = graphicsApi.CreateMaterial($"Default Material (topology {clone.Topology})");

				// Shared shaders: ModelLoader.Release frees them, once each.
				materialObj.OwnsShaders = false;
				materialObj.SetShader(GetPixelShaderVariant(BuildMaterialKeywords(null)), cloneVs);
				BindFallbackTexture(materialObj, "_MainTex");
				BindFallbackTexture(materialObj, "_OcclusionTex");
				BindFlatNormalFallback(materialObj);
				factors = defaultPbr;
			}
			else
			{
				materialObj = BuildMaterialObject(source, $"{source.Name} (topology {clone.Topology})", cloneVs,
					synthKey, out var cloneBaseColor);
				factors = BuildFactors(source, synthKey);

				if (cloneBaseColor != null)
				{
					result.MaterialBaseColor[synthKey] = cloneBaseColor;
				}
			}

			factors.Topology = clone.Topology;
			result.materialObjects.Add(synthKey, materialObj);
			result.MaterialPbr[synthKey] = factors;

			yield return EstimateMaterialBytes(source);
		}

		foreach (var preparedMesh in prepared.Meshes)
		{
			var swMesh = System.Diagnostics.Stopwatch.StartNew();
			var meshObj = graphicsApi.CreateMesh(preparedMesh.Name);
			meshObj.SetVertices(preparedMesh.Vertices);
			meshObj.SetIndices(preparedMesh.Indices);
			result._meshMs += swMesh.ElapsedMilliseconds;
			result._meshCount++;
			meshObj.SetBounds(preparedMesh.BoundsCenter, preparedMesh.BoundsRadius);

			if (preparedMesh.LodLevels != null)
			{
				UploadLodGroup(meshObj, preparedMesh.LodLevels);
			}

			result.Meshes.Add(meshObj);
			result.MeshHasUv.Add(preparedMesh.HasUv);
			result.MeshSkin.Add(preparedMesh.SkinVertices);

			yield return (long)preparedMesh.Vertices.Length * VertexSizeBytes + (long)preparedMesh.Indices.Length * sizeof(uint);
		}

		result.Skeleton = prepared.Skeleton;
		result.Animations.AddRange(prepared.Animations);
		result.instances.AddRange(prepared.Instances);

		// Must run while base-color CPU pixels are still alive - finalization frees them.
		ModelImporter.ComputeTriangleAlbedoFromTextures(result, prepared);
	}

}
