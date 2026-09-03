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

/// <summary>CPU phase: parses glTF into a PreparedModel - meshes, LODs, skeleton, clips, materials.</summary>
public static partial class ModelImporter
{
	internal static PreparedModel PrepareModel(string modelPath, ModelLoadOptions options,
		IProgress<float> progress, CancellationToken cancellationToken)
	{
		// On a cache hit nothing below runs: parse, decode, meshopt and LOD are pure functions of
		// the source plus options, and together they are almost all of the load time.
		var cache = options.Cache;
		if (cache != null)
		{
			var modelKey = AssetCache.ModelKey(modelPath, options.CookSignature());
			var cooked = CookedModelFile.TryRead(cache.ModelPath(modelKey));

			if (cooked != null && ModelAssetBaker.AllTexturesPresent(cooked, cache))
			{
				progress?.Report(1f);
				return cooked;
			}

			// Miss: the load does NOT wait for the bake, so enabling the pipeline can never make
			// the first open of a model slower.
			AssetBakeQueue.Enqueue(modelPath, options, modelKey);
		}

		var swPhase = System.Diagnostics.Stopwatch.StartNew();
		var model = LoadModelRoot(modelPath, options, out var externalImagePaths);
		cancellationToken.ThrowIfCancellationRequested();

		var prepared = new PreparedModel();
		prepared.MsParse = swPhase.ElapsedMilliseconds;
		swPhase.Restart();

		// Deduplicated so a shared image (e.g. one ORM texture used by both MetallicRoughness and
		// Occlusion) is decoded once; decode is the most expensive CPU phase of the load.
		var usedImages = new List<SharpGLTF.Schema2.Image>();
		{
			var seenImages = new HashSet<SharpGLTF.Schema2.Image>();
			void AddImage(SharpGLTF.Schema2.Texture texture)
			{
				if (texture?.PrimaryImage != null && seenImages.Add(texture.PrimaryImage))
				{
					usedImages.Add(texture.PrimaryImage);
				}
			}

			foreach (var logicalMaterial in model.LogicalMaterials)
			{
				if (logicalMaterial == null)
				{
					continue;
				}

				AddImage(logicalMaterial.GetDiffuseTexture());
				AddImage(logicalMaterial.FindChannel("MetallicRoughness")?.Texture);
				AddImage(logicalMaterial.FindChannel("VolumeThickness")?.Texture);
				AddImage(logicalMaterial.FindChannel("Occlusion")?.Texture);
				AddImage(logicalMaterial.FindChannel("Normal")?.Texture);
			}
		}

		// Streaming: this phase decodes NOTHING. Materials get 1x1 fillers, and shader keywords
		// stay the same (they follow texture presence in glTF, not pixels), so PSOs are untouched.
		// External images are re-decoded from their PATH; only embedded ones keep their bytes.
		int decodeMaxSize = options.MaxTextureSize;
		Dictionary<SharpGLTF.Schema2.Image, TextureStreamSource> streamSources = null;
		if (options.StreamTextures)
		{
			decodeMaxSize = 0;
			streamSources = new Dictionary<SharpGLTF.Schema2.Image, TextureStreamSource>();
			foreach (var image in usedImages)
			{
				streamSources[image] = CreateStreamSource(image, externalImagePaths);
			}
		}

		var decodedImages = new Dictionary<SharpGLTF.Schema2.Image, (byte[] Pixels, int Width, int Height)>();
		if (usedImages.Count > 0 && !options.StreamTextures)
		{
			var decodedResults = new (byte[] Pixels, int Width, int Height)[usedImages.Count];
			int imagesDone = 0;

			// Capped for MEMORY, not CPU: stb decodes at full resolution before the downscale, so
			// each thread peaks at a full RGBA copy (64 MB for 4K). One thread per core would mean
			// 1-2 GB of intermediates alone; decode is memory-bound, so four keep most of the win.
			var decodeOptions = new ParallelOptions
			{
				CancellationToken = cancellationToken,
				MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount),
			};

			Parallel.For(0, usedImages.Count, decodeOptions, i =>
			{
				decodedResults[i] = DecodeImagePixels(usedImages[i], decodeMaxSize);
				progress?.Report(0.05f + 0.30f * (Interlocked.Increment(ref imagesDone) / (float)usedImages.Count));
			});

			for (int i = 0; i < usedImages.Count; i++)
			{
				decodedImages[usedImages[i]] = decodedResults[i];
				prepared.DecodedBytes += decodedResults[i].Pixels?.LongLength ?? 0;
			}

			prepared.DecodedImages = usedImages.Count;
		}

		prepared.MsDecode = swPhase.ElapsedMilliseconds;
		swPhase.Restart();

		int materialCount = Math.Max(1, model.LogicalMaterials.Count);

		for (var index = 0; index < model.LogicalMaterials.Count; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var logicalMaterial = model.LogicalMaterials[index];

			if (logicalMaterial == null)
			{
				prepared.Materials.Add(new PreparedMaterial { LogicalIndex = index, IsNull = true });
				progress?.Report(0.35f + 0.05f * ((index + 1) / (float)materialCount));
				continue;
			}

			var preparedMaterial = new PreparedMaterial
			{
				LogicalIndex = index,
				Name = logicalMaterial.Name ?? $"Material_{index}"
			};

			var baseColorTexture = logicalMaterial.GetDiffuseTexture();
			if (baseColorTexture?.PrimaryImage != null)
			{
				preparedMaterial.BaseColorTexture = DecodeTexture(baseColorTexture, decodeMaxSize, decodedImages, streamSources, externalImagePaths);
			}

			// Field initializers already hold the glTF spec defaults, so only authored values read.
			var baseColorChannel = logicalMaterial.FindChannel("BaseColor");
			if (baseColorChannel.HasValue)
			{
				foreach (var parameter in baseColorChannel.Value.Parameters)
				{
					if (parameter.Name == "RGBA" && parameter.Value is Vector4 rgba)
					{
						preparedMaterial.BaseColorFactor = rgba;
					}
				}
			}

			preparedMaterial.AlphaCutoff = logicalMaterial.Alpha switch
			{
				AlphaMode.MASK => logicalMaterial.AlphaCutoff,
				AlphaMode.BLEND => 0.5f,
				_ => 0f,
			};

			// Kept as its own field: the cutoff above cannot express the mode.
			preparedMaterial.AlphaMode = logicalMaterial.Alpha switch
			{
				AlphaMode.MASK => MaterialAlphaMode.Mask,
				AlphaMode.BLEND => MaterialAlphaMode.Blend,
				_ => MaterialAlphaMode.Opaque,
			};

			bool metallicAuthored = false;
			bool roughnessAuthored = false;

			var metallicRoughnessChannel = logicalMaterial.FindChannel("MetallicRoughness");
			if (metallicRoughnessChannel.HasValue)
			{
				var channel = metallicRoughnessChannel.Value;

				foreach (var parameter in channel.Parameters)
				{
					if (parameter.Name == "MetallicFactor")
					{
						preparedMaterial.MetallicFactor = Convert.ToSingle(parameter.Value);
						metallicAuthored = !parameter.IsDefault;
					}
					else if (parameter.Name == "RoughnessFactor")
					{
						preparedMaterial.RoughnessFactor = Convert.ToSingle(parameter.Value);
						roughnessAuthored = !parameter.IsDefault;
					}
				}

				// Channel convention: G = roughness, B = metallic.
				var mrTexture = channel.Texture;
				if (mrTexture?.PrimaryImage != null)
				{
					preparedMaterial.MetallicRoughnessTexture = DecodeTexture(mrTexture, decodeMaxSize, decodedImages, streamSources, externalImagePaths);
				}
			}

			// KHR_materials_ior / KHR_materials_dispersion: default IOR 1.5, dispersion 0 = off.
			preparedMaterial.Ior = logicalMaterial.IndexOfRefraction;
			preparedMaterial.Dispersion = logicalMaterial.Dispersion;

			// KHR_materials_transmission: scalar factor only - the preview has no real refraction.
			var transmissionChannel = logicalMaterial.FindChannel("Transmission");
			if (transmissionChannel.HasValue)
			{
				foreach (var parameter in transmissionChannel.Value.Parameters)
				{
					if (parameter.Name == "TransmissionFactor")
					{
						preparedMaterial.TransmissionFactor = Convert.ToSingle(parameter.Value);
					}
				}
			}

			// Matched by value TYPE, not name: SharpGLTF's parameter key names are internal.
			var sheenColorChannel = logicalMaterial.FindChannel("SheenColor");
			if (sheenColorChannel.HasValue)
			{
				foreach (var parameter in sheenColorChannel.Value.Parameters)
				{
					if (parameter.Value is Vector3 sheenRgb)
					{
						preparedMaterial.SheenColorFactor = sheenRgb;
					}
				}
			}

			var sheenRoughnessChannel = logicalMaterial.FindChannel("SheenRoughness");
			if (sheenRoughnessChannel.HasValue)
			{
				foreach (var parameter in sheenRoughnessChannel.Value.Parameters)
				{
					if (parameter.Value is float || parameter.Value is double)
					{
						preparedMaterial.SheenRoughnessFactor = Convert.ToSingle(parameter.Value);
					}
				}
			}

			// specularColorFactor may exceed 1; per spec the shader clamps after multiplying by F0.
			var specularColorChannel = logicalMaterial.FindChannel("SpecularColor");
			if (specularColorChannel.HasValue)
			{
				foreach (var parameter in specularColorChannel.Value.Parameters)
				{
					if (parameter.Value is Vector3 specularRgb)
					{
						preparedMaterial.SpecularColorFactor = specularRgb;
					}
				}
			}

			var specularFactorChannel = logicalMaterial.FindChannel("SpecularFactor");
			if (specularFactorChannel.HasValue)
			{
				foreach (var parameter in specularFactorChannel.Value.Parameters)
				{
					if (parameter.Value is float || parameter.Value is double)
					{
						preparedMaterial.SpecularFactor = Convert.ToSingle(parameter.Value);
					}
				}
			}

			// KHR_materials_volume: Beer-Lambert. Packed for the shader as float4(rgb, exponent),
			// where exponent = thickness / attenuationDistance.
			float volumeThickness = 0f;
			float attenuationDistance = 0f;
			var attenuationColor = Vector3.One;

			var thicknessChannel = logicalMaterial.FindChannel("VolumeThickness");
			if (thicknessChannel.HasValue)
			{
				foreach (var parameter in thicknessChannel.Value.Parameters)
				{
					if (parameter.Name == "ThicknessFactor")
					{
						volumeThickness = Convert.ToSingle(parameter.Value);
						preparedMaterial.ThicknessFactor = volumeThickness;
					}
				}

				// Thickness texture is the G channel per spec, multiplying the factor.
				var thicknessTexture = thicknessChannel.Value.Texture;
				if (thicknessTexture?.PrimaryImage != null)
				{
					preparedMaterial.ThicknessTexture = DecodeTexture(thicknessTexture, decodeMaxSize, decodedImages, streamSources, externalImagePaths);
				}
			}

			var attenuationChannel = logicalMaterial.FindChannel("VolumeAttenuation");
			if (attenuationChannel.HasValue)
			{
				foreach (var parameter in attenuationChannel.Value.Parameters)
				{
					if (parameter.Name == "RGB" && parameter.Value is Vector3 rgb)
					{
						attenuationColor = rgb;
					}
					else if (parameter.Name == "AttenuationDistance")
					{
						attenuationDistance = Convert.ToSingle(parameter.Value);
					}
				}
			}

			preparedMaterial.VolumeAttenuation = volumeThickness > 0f && attenuationDistance > 0f
				? new Vector4(attenuationColor, volumeThickness / attenuationDistance)
				: new Vector4(1f, 1f, 1f, 0f);

			// Baked AO is the R channel per spec; it attenuates ambient/env only, never direct light.
			var occlusionChannel = logicalMaterial.FindChannel("Occlusion");
			if (occlusionChannel.HasValue)
			{
				foreach (var parameter in occlusionChannel.Value.Parameters)
				{
					if (parameter.Name == "OcclusionStrength")
					{
						preparedMaterial.OcclusionStrength = Convert.ToSingle(parameter.Value);
					}
				}

				// AO is often baked against UV1; sets above 1 are not stored per vertex, so clamp.
				preparedMaterial.OcclusionUvSet = Math.Clamp(occlusionChannel.Value.TextureCoordinate, 0, 1);

				var occlusionTexture = occlusionChannel.Value.Texture;
				if (occlusionTexture?.PrimaryImage != null)
				{
					preparedMaterial.OcclusionTexture = DecodeTexture(occlusionTexture, decodeMaxSize, decodedImages, streamSources, externalImagePaths);
				}
			}

			// Tangent-space normal map: linear, never sRGB-decoded.
			var normalChannel = logicalMaterial.FindChannel("Normal");
			if (normalChannel.HasValue)
			{
				foreach (var parameter in normalChannel.Value.Parameters)
				{
					if (parameter.Name == "NormalScale")
					{
						preparedMaterial.NormalScale = Convert.ToSingle(parameter.Value);
					}
				}

				var normalTexture = normalChannel.Value.Texture;
				if (normalTexture?.PrimaryImage != null)
				{
					preparedMaterial.NormalTexture = DecodeTexture(normalTexture, decodeMaxSize, decodedImages, streamSources, externalImagePaths);
				}
			}

			// emissiveFactor and KHR_materials_emissive_strength collapse into one linear RGB here.
			var emissiveChannel = logicalMaterial.FindChannel("Emissive");
			if (emissiveChannel.HasValue)
			{
				var emissiveRgb = Vector3.Zero;
				float emissiveStrength = 1f;
				foreach (var parameter in emissiveChannel.Value.Parameters)
				{
					if (parameter.Value is Vector3 emissive)
					{
						emissiveRgb = emissive;
					}
					else if (parameter.Name == "EmissiveStrength")
					{
						emissiveStrength = Convert.ToSingle(parameter.Value);
					}
				}

				preparedMaterial.EmissiveFactor = emissiveRgb * emissiveStrength;

				// Per spec the factor multiplies the texture, so a zero factor makes it pointless.
				var emissiveTexture = preparedMaterial.EmissiveFactor != Vector3.Zero
					? emissiveChannel.Value.Texture
					: null;
				if (emissiveTexture?.PrimaryImage != null)
				{
					preparedMaterial.EmissiveTexture = DecodeTexture(emissiveTexture, decodeMaxSize, decodedImages, streamSources, externalImagePaths);
				}
			}

			// KHR_texture_transform, one per material: baked to a 2x2 matrix plus offset following
			// the spec's M = Translation * Rotation * Scale.
			foreach (var channelName in new[] { "BaseColor", "Normal", "MetallicRoughness" })
			{
				var transform = logicalMaterial.FindChannel(channelName)?.TextureTransform;
				if (transform == null)
				{
					continue;
				}

				float sin = MathF.Sin(transform.Rotation);
				float cos = MathF.Cos(transform.Rotation);
				preparedMaterial.UvTransform = new Vector4(
					cos * transform.Scale.X, -sin * transform.Scale.Y,
					sin * transform.Scale.X, cos * transform.Scale.Y);
				preparedMaterial.UvOffset = transform.Offset;
				preparedMaterial.HasUvTransform = true;
				break;
			}

			// Deliberate deviation: the spec defaults (metallic 1, roughness 1) render as unlit, so
			// fall back to a neutral dielectric - but only if NEITHER factor was authored, since
			// SharpGLTF's IsDefault means "equals the default", not "absent from the JSON".
			if (preparedMaterial.MetallicRoughnessTexture == null && !metallicAuthored && !roughnessAuthored)
			{
				preparedMaterial.MetallicFactor = 0f;
				preparedMaterial.RoughnessFactor = 0.6f;
			}

			prepared.Materials.Add(preparedMaterial);
			progress?.Report(0.35f + 0.05f * ((index + 1) / (float)materialCount));
		}

		prepared.MsMaterials = swPhase.ElapsedMilliseconds;
		swPhase.Restart();

		var primitiveToMeshIdMap = new Dictionary<MeshPrimitive, int>();
		var meshWork = new List<MeshWorkItem>();

		// Must precede the primitive walk: the skin stream remaps local skin indices to joints.
		prepared.Skeleton = SkinningImport.BuildSkeleton(model, out var nodeToJoint);
		prepared.Animations.AddRange(SkinningImport.BuildAnimations(model, prepared.Skeleton, nodeToJoint));

		// Skins hang off NODES, not primitives. A primitive shared by two differently-skinned nodes
		// resolves to the first: glTF allows it, real assets don't, and per-skin copies are costly.
		var primitiveToSkin = new Dictionary<MeshPrimitive, Skin>();
		foreach (var node in model.LogicalNodes)
		{
			if (node.Mesh == null || node.Skin == null)
			{
				continue;
			}

			foreach (var primitive in node.Mesh.Primitives)
			{
				primitiveToSkin.TryAdd(primitive, node.Skin);
			}
		}

		foreach (var logicalMesh in model.LogicalMeshes)
		{
			var baseMeshName = logicalMesh.Name ?? $"Mesh_{logicalMesh.LogicalIndex}";

			for (var primitiveIndex = 0; primitiveIndex < logicalMesh.Primitives.Count; primitiveIndex++)
			{
				var primitive = logicalMesh.Primitives[primitiveIndex];
				cancellationToken.ThrowIfCancellationRequested();

				var positionsAccessor = primitive.GetVertexAccessor("POSITION");
				var uvsAccessor = primitive.GetVertexAccessor("TEXCOORD_0");
				var uvs1Accessor = primitive.GetVertexAccessor("TEXCOORD_1");
				var normalsAccessor = primitive.GetVertexAccessor("NORMAL");
				var tangentsAccessor = primitive.GetVertexAccessor("TANGENT");
				var colorsAccessor = primitive.GetVertexAccessor("COLOR_0");
				var indexAccessor = primitive.GetIndexAccessor();

				if (positionsAccessor == null)
				{
					continue;
				}

				// Points/lines get material clones carrying a PSO for their topology, since the
				// batch renderer groups draws by material.
				int topology = primitive.DrawPrimitiveType switch
				{
					PrimitiveType.TRIANGLES => ModelLoader.MeshTopologyTriangles,
					PrimitiveType.LINES => ModelLoader.MeshTopologyLineList,
					PrimitiveType.LINE_STRIP => ModelLoader.MeshTopologyLineStrip,
					PrimitiveType.LINE_LOOP => ModelLoader.MeshTopologyLineStrip,
					PrimitiveType.POINTS => ModelLoader.MeshTopologyPoints,
					_ => -1,
				};
				if (topology < 0)
				{
					// TRIANGLE_STRIP/FAN are unsupported.
					continue;
				}

				var positions = positionsAccessor.AsVector3Array();
				if (positions.Count == 0)
				{
					continue;
				}

				var uvs = uvsAccessor?.AsVector2Array();
				var uvs1 = uvs1Accessor?.AsVector2Array();
				var normals = normalsAccessor?.AsVector3Array();
				var tangents = tangentsAccessor?.AsVector4Array();
				var colors = colorsAccessor?.AsColorArray();
				var indices = indexAccessor?.AsIndicesArray();

				// glTF is right-handed, the engine left-handed: Z is mirrored here and triangle
				// winding is flipped below to keep front faces facing front.
				var sourceVertices = new Vertex[positions.Count];
				for (int i = 0; i < positions.Count; i++)
				{
					var uv = uvs != null && i < uvs.Count ? uvs[i] : Vector2.Zero;
					var uv1 = uvs1 != null && i < uvs1.Count ? uvs1[i] : Vector2.Zero;
					var normal = normals != null && i < normals.Count ? normals[i] : Vector3.UnitY;
					var color = colors != null && i < colors.Count ? colors[i] : Vector4.One;

					// TANGENT is vec4 with w = bitangent sign; w is INVERTED alongside the Z mirror
					// because mirroring flips basis handedness (det = -1).
					var tangent = tangents != null && i < tangents.Count
						? new Vector4(tangents[i].X, tangents[i].Y, -tangents[i].Z, -tangents[i].W)
						: new Vector4(1f, 0f, 0f, 1f);

					sourceVertices[i] = new Vertex
					{
						Position = new Vector3(positions[i].X, positions[i].Y, -positions[i].Z),
						TexCoord = new Vector2(uv.X, uv.Y),
						TexCoord1 = new Vector2(uv1.X, uv1.Y),
						Normal = new Vector3(normal.X, normal.Y, -normal.Z),
						Tangent = tangent,
						Color = color
					};
				}

				// The batch renderer only issues DrawIndexedIndirect, so synthesize 0..N-1.
				uint[] sourceIndices;
				if (indices != null)
				{
					sourceIndices = indices.ToArray();
				}
				else
				{
					sourceIndices = new uint[positions.Count];
					for (uint i = 0; i < sourceIndices.Length; i++)
					{
						sourceIndices[i] = i;
					}
				}

				// One sub-mesh per primitive; the suffix keeps them distinguishable by name.
				var meshName = logicalMesh.Primitives.Count > 1 ? $"{baseMeshName}.{primitiveIndex}" : baseMeshName;

				// This loop only reads SharpGLTF (not thread-safe); the heavy work runs in parallel
				// below. A primitive's meshId is its work-item index.
				primitiveToMeshIdMap[primitive] = meshWork.Count;
				meshWork.Add(new MeshWorkItem
				{
					Name = meshName,
					SourceVertices = sourceVertices,
					SourceIndices = sourceIndices,
					Topology = topology,
					HasUv = uvsAccessor != null,
					HasNormals = normalsAccessor != null,
					HasTangents = tangents != null,
					// Read HERE, not in the parallel phase: SharpGLTF is not thread-safe.
					SourceSkin = primitiveToSkin.TryGetValue(primitive, out var primitiveSkin)
						? SkinningImport.ReadSkinVertices(primitive, primitiveSkin, nodeToJoint, sourceVertices.Length)
						: null,
				});
			}
		}

		var preparedMeshes = new PreparedMesh[meshWork.Count];
		int primitivesDone = 0;
		Parallel.For(0, meshWork.Count, new ParallelOptions { CancellationToken = cancellationToken }, workIndex =>
		{
			var work = meshWork[workIndex];
			var sourceVertices = work.SourceVertices;
			var sourceIndices = work.SourceIndices;
			var sourceSkin = work.SourceSkin;

			if (work.Topology == ModelLoader.MeshTopologyTriangles)
			{
				for (int t = 0; t + 2 < sourceIndices.Length; t += 3)
				{
					(sourceIndices[t + 1], sourceIndices[t + 2]) = (sourceIndices[t + 2], sourceIndices[t + 1]);
				}

				// No NORMAL accessor means FLAT (per-face) shading per the glTF spec, so vertices
				// are unwelded per triangle.
				if (!work.HasNormals)
				{
					var flatVertices = new Vertex[sourceIndices.Length];
					// The skin must be unwelded WITH the geometry: indices are rewritten to 0..N-1.
					var flatSkin = sourceSkin != null ? new SkinVertex[sourceIndices.Length] : null;

					for (int t = 0; t + 2 < sourceIndices.Length; t += 3)
					{
						if (flatSkin != null)
						{
							flatSkin[t] = sourceSkin[sourceIndices[t]];
							flatSkin[t + 1] = sourceSkin[sourceIndices[t + 1]];
							flatSkin[t + 2] = sourceSkin[sourceIndices[t + 2]];
						}

						var v0 = sourceVertices[sourceIndices[t]];
						var v1 = sourceVertices[sourceIndices[t + 1]];
						var v2 = sourceVertices[sourceIndices[t + 2]];

						var faceNormal = Vector3.Cross(v2.Position - v0.Position, v1.Position - v0.Position);
						faceNormal = faceNormal.LengthSquared() > 1e-16f
							? Vector3.Normalize(faceNormal)
							: Vector3.UnitY;

						v0.Normal = faceNormal;
						v1.Normal = faceNormal;
						v2.Normal = faceNormal;

						flatVertices[t] = v0;
						flatVertices[t + 1] = v1;
						flatVertices[t + 2] = v2;
						sourceIndices[t] = (uint)t;
						sourceIndices[t + 1] = (uint)(t + 1);
						sourceIndices[t + 2] = (uint)(t + 2);
					}

					sourceVertices = flatVertices;
					sourceSkin = flatSkin;
				}
			}
			var (boundsCenter, boundsRadius) = MeshUtility.ComputeBoundsData(sourceVertices);

			var finalVertices = sourceVertices;
			var finalIndices = sourceIndices;
			var finalSkin = sourceSkin;
			LodLevel[] lodLevels = null;

			if (work.Topology == ModelLoader.MeshTopologyTriangles)
			{
				// Must run before Optimize/GenerateLods remap vertices: it needs pristine
				// per-triangle winding. Fallback only - authored tangents match the normal-map bake.
				if (!work.HasTangents)
				{
					MeshUtility.GenerateTangents(sourceVertices, sourceIndices);
				}

				if (finalSkin == null)
				{
					if (options.OptimizeMesh)
					{
						(finalVertices, finalIndices) = MeshUtility.OptimizeMeshData(finalVertices, finalIndices);
					}

					if (options.GenerateLods)
					{
						(finalVertices, finalIndices, lodLevels) =
							MeshUtility.GenerateLodGroupData(finalVertices, finalIndices, options.LodRatios);
					}
				}
				else
				{
					// Skinned meshes go through the same passes with skin data PACKED into the
					// vertex: meshopt reorders and welds without exposing a full remap table.
					var packed = MeshUtility.PackSkinned(finalVertices, finalSkin);

					if (options.OptimizeMesh)
					{
						(packed, finalIndices) = MeshUtility.OptimizeMeshData(packed, finalIndices);
					}

					if (options.GenerateLods)
					{
						(packed, finalIndices, lodLevels) =
							MeshUtility.GenerateLodGroupData(packed, finalIndices, options.LodRatios);
					}

					(finalVertices, finalSkin) = MeshUtility.UnpackSkinned(packed);
				}
			}

			preparedMeshes[workIndex] = new PreparedMesh
			{
				Name = work.Name,
				Vertices = finalVertices,
				Indices = finalIndices,
				SkinVertices = finalSkin,
				LodLevels = lodLevels,
				BoundsCenter = boundsCenter,
				BoundsRadius = boundsRadius,
				HasUv = work.HasUv,
				Topology = work.Topology,
			};

			progress?.Report(0.4f + 0.55f * (Interlocked.Increment(ref primitivesDone) / (float)meshWork.Count));
		});

		prepared.Meshes.AddRange(preparedMeshes);

		// One mesh under several nodes with the SAME world matrix is baked only once.
		var bakedMeshCache = new Dictionary<(int MeshId, Matrix4x4 World), int>();

		foreach (var node in model.LogicalNodes)
		{
			if (node.Mesh == null)
			{
				continue;
			}

			// A deep hierarchy can produce shear, which TRS cannot express; Decompose then returns
			// false and GARBAGE in its out params, so fall back to baking the matrix into vertices.
			bool trsValid = Matrix4x4.Decompose(node.WorldMatrix, out var scale, out var rotation, out var translation);

			// Same RH->LH conversion as the vertices: conjugating by diag(1,1,-1) turns the
			// quaternion into (-x,-y,z,w).
			translation.Z = -translation.Z;
			rotation = new Quaternion(-rotation.X, -rotation.Y, rotation.Z, rotation.W);

			foreach (var primitive in node.Mesh.Primitives)
			{
				if (primitiveToMeshIdMap.TryGetValue(primitive, out int meshId))
				{
					// Per glTF spec the node transform of a skinned mesh is IGNORED - the joints
					// carry it, so baking WorldMatrix here would apply it twice.
					if (prepared.Meshes[meshId].SkinVertices != null)
					{
						prepared.Instances.Add(new InstanceData
						{
							transform = new Transform
							{
								position = Vector3.Zero,
								rotation = Quaternion.Identity,
								scale = Vector3.One,
							},
							meshId = meshId,
							materialId = primitive.Material?.LogicalIndex ?? -1,
						});
						continue;
					}

					if (!trsValid)
					{
						var cacheKey = (meshId, node.WorldMatrix);
						if (!bakedMeshCache.TryGetValue(cacheKey, out int bakedId))
						{
							bakedId = BakeMeshWithMatrix(prepared, meshId, node.WorldMatrix);
							bakedMeshCache[cacheKey] = bakedId;
						}
						meshId = bakedId;
						translation = Vector3.Zero;
						rotation = Quaternion.Identity;
						scale = Vector3.One;
					}

					var material = primitive.Material;
					int materialId = material?.LogicalIndex ?? -1;

					// Non-triangle topology points at a material clone carrying a matching PSO.
					int topology = prepared.Meshes[meshId].Topology;
					if (topology != ModelLoader.MeshTopologyTriangles)
					{
						int synthKey = ModelLoader.MakeTopologyMaterialKey(topology, materialId);
						prepared.TopologyMaterialClones[synthKey] = (materialId, topology);
						materialId = synthKey;
					}

					prepared.Instances.Add(new InstanceData
					{
						transform = new Transform { position = translation, rotation = rotation, scale = scale },
						meshId = meshId,
						materialId = materialId
					});
				}
			}
		}

		progress?.Report(1f);
		prepared.MsMeshes = swPhase.ElapsedMilliseconds;
		return prepared;
	}


	// Cache-bypassing entry point for the background baker; the caller clears CacheDirectory so
	// this cannot recurse.
	internal static PreparedModel PrepareForBake(string modelPath, ModelLoadOptions options,
		CancellationToken cancellationToken) => PrepareModel(modelPath, options, null, cancellationToken);
}
