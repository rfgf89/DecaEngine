using System;
using System.Numerics;
using SharpGLTF.Schema2;
using DecaEngine.Core;
using DecaEngine.Graphics.Core;
using System.Runtime.InteropServices;
using Diligent;
using MeshOptimizer;
using UnsafeCollections.Collections.Unsafe;

namespace DecaEngine.Graphics;

public static class MeshUtility
{
	public static unsafe void RecalculateBounds(this IMeshObject mesh)
	{
		if (mesh.IndexCount == 0)
		{
			return;
		}

		var finalPositions = new Vector3[UnsafeArray.GetLength(mesh.VertexData)];
		for (int i = 0; i < finalPositions.Length; i++)
		{
			finalPositions[i] = UnsafeArray.Get<Vertex>(mesh.VertexData, i).Position;
		}

		fixed (Vector3* ptr = finalPositions)
		{
			var bound = Meshopt.ComputeSphereBounds((float*)ptr, (UIntPtr)finalPositions.Length, (UIntPtr)sizeof(Vector3), null, UIntPtr.Zero);

			var center = new Vector3(bound.center[0], bound.center[1], bound.center[2]);
			var radius = bound.radius;

			mesh.SetBounds(center, radius);
		}
	}

	public static unsafe void OptimizeMesh(this IMeshObject mesh)
	{
		if (mesh.IndexCount == 0 || mesh.IndexCount % 3 != 0)
		{
			return;
		}

		int vertexCount = UnsafeArray.GetLength(mesh.VertexData);
		Span<uint> remap = new uint[vertexCount];

		var vertices = new Span<Vertex>(UnsafeArray.GetPtr<Vertex>(mesh.VertexData, 0), vertexCount);
		var indices = new Span<uint>(UnsafeArray.GetPtr<uint>(mesh.IndexData, 0), mesh.IndexCount);

		var uniqueVertexCount = (int)Meshopt.GenerateVertexRemap(remap, indices, vertices);

		Meshopt.RemapVertexBuffer(vertices, vertices, remap);
		Meshopt.RemapIndexBuffer(indices, indices, remap);

		Meshopt.OptimizeVertexCache(indices, indices, (UIntPtr)uniqueVertexCount);
		Meshopt.OptimizeVertexFetch(MemoryMarshal.Cast<Vertex, uint>(vertices), indices, vertices);

		Span<Vector3> finalPositions = new Vector3[uniqueVertexCount];
		var finalIndices = new uint[mesh.IndexCount];

		for (int i = 0; i < finalPositions.Length; i++)
		{
			finalPositions[i] = vertices[i].Position;
		}

		fixed (Vector3* posPtr = finalPositions)
		fixed (uint* indicesPtr = indices)
		fixed (uint* ptr = finalIndices)
		{
			Meshopt.OptimizeOverdraw(ptr,
				indicesPtr,
				(UIntPtr)finalIndices.Length,
				(float*)posPtr,
				(UIntPtr)finalPositions.Length,
				(UIntPtr)Marshal.SizeOf<Vector3>(),
				1.05f);
		}

		mesh.SetIndices(finalIndices);
		mesh.SetVertices(vertices.Slice(0, uniqueVertexCount).ToArray());
	}

	public static unsafe void GenerateLodGroup(this IMeshObject mesh, float[] levels)
	{
		if (mesh.VertexData == null || mesh.IndexData == null || mesh.IndexCount == 0 || levels.Length <= 0)
		{
			return;
		}
		
		int baseVertexCount = UnsafeArray.GetLength(mesh.VertexData);
		var baseVertices = new ReadOnlySpan<Vertex>(UnsafeArray.GetPtr<Vertex>(mesh.VertexData, 0), baseVertexCount);
		var baseIndices = new ReadOnlySpan<uint>(UnsafeArray.GetPtr<uint>(mesh.IndexData, 0), mesh.IndexCount);

		List<Vertex[]> allVertices = new() { baseVertices.ToArray() };
		List<uint[]> allIndices = new() { baseIndices.ToArray() };
		List<LodLevel> lodLevelsList = new();


		lodLevelsList.Add(new LodLevel { error = 0, firstIndex = 0, indexCount = baseIndices.Length, vertexOffset = 0 });

		int currentVertexOffset = baseVertexCount;
		int currentIndexOffset = baseIndices.Length;

		Span<Vector3> positions = new Vector3[baseVertexCount];
		Span<Vector2> texCoords = new Vector2[baseVertexCount];
		for (int i = 0; i < baseVertexCount; i++)
		{
			positions[i] = baseVertices[i].Position;
			texCoords[i] = baseVertices[i].TexCoord;
		}

		float scale;
		fixed(Vector3* p = positions)
		{
			scale = Meshopt.SimplifyScale((float*)p, (UIntPtr)positions.Length, (UIntPtr)sizeof(Vector3));
		}

		float lodError = 0.0f;

		for (int level = 1; level <= levels.Length; level++)
		{
			float ratio = levels[level - 1];
			var targetIndexCount = (UIntPtr)((float)baseIndices.Length * ratio);
			targetIndexCount = (targetIndexCount / 3) * 3;
			if (targetIndexCount < 3) break;

			var lodIndices = new uint[baseIndices.Length];
			float resultError = lodError * scale;
			
			UIntPtr simplifiedIndexCount;
			fixed (uint* sourceIndPtr = baseIndices)
			fixed (uint* indPtr = lodIndices)
			fixed (Vector3* posPtr = positions)
			fixed (Vector2* texPtr = texCoords)
			{
				var attributeWeights = new[] { 1f, 1f };
				fixed(float* attrPtr = attributeWeights)
				{
					const float maxError = 1e-1f;

					simplifiedIndexCount = Meshopt.SimplifyWithAttributes(
						indPtr, sourceIndPtr, (UIntPtr)baseIndices.Length,
						(float*)posPtr, (UIntPtr)baseVertexCount, (UIntPtr)sizeof(Vector3),
						(float*)texPtr, (UIntPtr)sizeof(Vector2),
						attrPtr, (UIntPtr)attributeWeights.Length,
						null, targetIndexCount, maxError,
						SimplificationOptions.SimplifyLockBorder, &resultError);
				}
			}

			if (simplifiedIndexCount == 0 || (int)simplifiedIndexCount >= allIndices[^1].Length) continue;

			var finalLodIndices = new Span<uint>(lodIndices, 0, (int)simplifiedIndexCount);
			var remap = new uint[baseVertexCount];
			var uniqueLodVertexCount = (int)Meshopt.GenerateVertexRemap(remap, finalLodIndices, baseVertices);

			var lodVerticesResult = new Vertex[uniqueLodVertexCount];
			var remappedLodIndices = new uint[simplifiedIndexCount];

			Meshopt.RemapVertexBuffer(lodVerticesResult, baseVertices, remap);
			Meshopt.RemapIndexBuffer(remappedLodIndices, finalLodIndices, remap);

			lodLevelsList.Add(new LodLevel 
			{ 
				error = resultError, 
				firstIndex = currentIndexOffset, 
				indexCount = (int)simplifiedIndexCount, 
				vertexOffset = currentVertexOffset 
			});

			allVertices.Add(lodVerticesResult);
			allIndices.Add(remappedLodIndices);

			currentVertexOffset += uniqueLodVertexCount;
			currentIndexOffset += (int)simplifiedIndexCount;

			lodError = Math.Max(lodError * 1.5f, resultError);
		}

		UnsafeArray* finalVertexData = UnsafeArray.Allocate<Vertex>(currentVertexOffset);
		UnsafeArray* finalIndexData = UnsafeArray.Allocate<uint>(currentIndexOffset);

		int vPtr = 0, iPtr = 0;
		for(int i = 0; i < allVertices.Count; i++)
		{
			fixed(Vertex* p = allVertices[i]) finalVertexData->CopyFrom<Vertex>(p, vPtr, 0, allVertices[i].Length);
			fixed(uint* p = allIndices[i]) finalIndexData->CopyFrom<uint>(p, iPtr, 0, allIndices[i].Length);
			vPtr += allVertices[i].Length;
			iPtr += allIndices[i].Length;
		}

		UnsafeArray* lodsNative = UnsafeArray.Allocate<LodLevel>(lodLevelsList.Count);
		for(int i = 0; i < lodLevelsList.Count; i++)
		{
			UnsafeArray.Set(lodsNative, i, lodLevelsList[i]);
		}

		mesh.SetVertices(finalVertexData);
		mesh.SetIndices(finalIndexData);
		mesh.SetLodGroup(lodsNative);
	}
}

public struct Transform
{
	public Vector3 position;
	public Quaternion rotation;
	public Vector3 scale;
}

public struct InstanceData
{
	public Transform transform;
	public int meshId;
	public int materialId;
}

public struct Vertex
{
	public Vector3 Position;
	public Vector2 TexCoord;
	public Vector3 Normal;
}

public class Scene
{
	public List<InstanceData> instances = new();

	public List<IMeshObject> Meshes = new();
	public OrderedDictionary<int, IMaterialObject> materialObjects = new();

	private static TextureAddress ToAddressMode(TextureWrapMode wrapMode)
	{
		return wrapMode switch
		{
			TextureWrapMode.CLAMP_TO_EDGE => TextureAddress.Clamp,
			TextureWrapMode.MIRRORED_REPEAT => TextureAddress.Mirror,
			TextureWrapMode.REPEAT => TextureAddress.Wrap,
			_ => TextureAddress.Wrap
		};
	}

	private static TextureFilter ToFilter(TextureMipMapFilter minFilter, TextureInterpolationFilter magFilter)
	{
		if (magFilter == TextureInterpolationFilter.LINEAR)
		{
			return minFilter switch
			{
				TextureMipMapFilter.LINEAR => TextureFilter.Linear,
				TextureMipMapFilter.NEAREST => TextureFilter.Point,
				TextureMipMapFilter.LINEAR_MIPMAP_LINEAR => TextureFilter.Linear,
				TextureMipMapFilter.LINEAR_MIPMAP_NEAREST => TextureFilter.Linear,
				TextureMipMapFilter.NEAREST_MIPMAP_LINEAR => TextureFilter.Point,
				TextureMipMapFilter.NEAREST_MIPMAP_NEAREST => TextureFilter.Point,
				_ => TextureFilter.Linear
			};
		}
		else // magFilter is NEAREST
		{
			return minFilter switch
			{
				TextureMipMapFilter.LINEAR => TextureFilter.Linear,
				TextureMipMapFilter.NEAREST => TextureFilter.Point,
				TextureMipMapFilter.LINEAR_MIPMAP_LINEAR => TextureFilter.Linear,
				TextureMipMapFilter.LINEAR_MIPMAP_NEAREST => TextureFilter.Linear,
				TextureMipMapFilter.NEAREST_MIPMAP_LINEAR => TextureFilter.Point,
				TextureMipMapFilter.NEAREST_MIPMAP_NEAREST => TextureFilter.Point,
				_ => TextureFilter.Point
			};
		}
	}

	public unsafe Scene(IGraphicsApi graphicsApi)
	{
		var model = ModelRoot.Load(Path.Combine(Environment.CurrentDirectory, "EditorAssets/models/Sponza/Sponza.gltf"));
		
		var cubeShaderPs = graphicsApi.CreateShader("Cube Shader Ps", "EditorAssets/shader", "CubeInstancePS.hlsl", ShaderObjectType.Pixel);
		var cubeShaderVs = graphicsApi.CreateShader("Cube Shader Vs", "EditorAssets/shader", "CubeInstanceVS.hlsl", ShaderObjectType.Vertex);

		var defaultMaterial = graphicsApi.CreateMaterial("Default Material");
		defaultMaterial.SetShader(cubeShaderPs, cubeShaderVs);
		
		materialObjects.Add(-1, defaultMaterial);

		for (var index = 0; index < model.LogicalMaterials.Count; index++)
		{
			var logicalMaterial = model.LogicalMaterials[index];

			if (logicalMaterial == null)
			{
				materialObjects.Add(index, defaultMaterial);
				continue;
			}

			var materialObj = graphicsApi.CreateMaterial(logicalMaterial.Name ?? $"Material_{index}");
			materialObj.SetShader(cubeShaderPs, cubeShaderVs);

			var baseColorTexture = logicalMaterial.GetDiffuseTexture();
			if (baseColorTexture != null)
			{
				var cpuData = new CpuTextureData()
				{
					Name = "_MainTex",
					Image = baseColorTexture.PrimaryImage,
				};

				var texture = new Core.Texture(cpuData.Name, cpuData);
				texture.Upload(graphicsApi, true);

				var sampler = baseColorTexture.Sampler;
				var addressMode = ToAddressMode(sampler.WrapS);
				var filterMode = ToFilter(sampler.MinFilter, sampler.MagFilter);

				var samplerObject = graphicsApi.CreateSampler(
					name: "_MainTex_Sampler",
					filter: filterMode,
					address: addressMode,
					comparisonFunction: CompFunction.Always,
					border: Vector4.Zero
				);

				materialObj.SetTexture("_MainTex", texture.GpuHandle);
				materialObj.SetImmutableSampler("_MainTex", samplerObject);
			}

			materialObjects.Add(index, materialObj);
		}

		var primitiveToMeshIdMap = new Dictionary<MeshPrimitive, int>();

		foreach (var logicalMesh in model.LogicalMeshes)
		{
			foreach (var primitive in logicalMesh.Primitives)
			{
				var positionsAccessor = primitive.GetVertexAccessor("POSITION");
				var uvsAccessor = primitive.GetVertexAccessor("TEXCOORD_0");
                var normalsAccessor = primitive.GetVertexAccessor("NORMAL");
				var indexAccessor = primitive.GetIndexAccessor();

				if (positionsAccessor == null || indexAccessor == null)
				{
					continue;
				}

				var positions = positionsAccessor.AsVector3Array();
				if (positions.Count == 0)
				{
					continue;
				}
				
				var uvs = uvsAccessor?.AsVector2Array();
                var normals = normalsAccessor?.AsVector3Array();
				var indices = indexAccessor.AsIndicesArray();

				var sourceVertices = new Vertex[positions.Count];
				for (int i = 0; i < positions.Count; i++)
				{
					var uv = uvs != null && i < uvs.Count ? uvs[i] : Vector2.Zero;
					var normal = normals != null && i < normals.Count ? normals[i] : Vector3.UnitY;
					sourceVertices[i] = new Vertex
					{
						Position = positions[i],
						TexCoord = new Vector2(uv.X, uv.Y),
						Normal = normal
					};
				}

				var meshObj = graphicsApi.CreateMesh(logicalMesh.Name ?? $"Mesh_{logicalMesh.LogicalIndex}");
				meshObj.SetVertices(sourceVertices.ToArray());
				meshObj.SetIndices(indices.ToArray());
				meshObj.RecalculateBounds();

				if (primitive.DrawPrimitiveType == PrimitiveType.TRIANGLES)
				{
					meshObj.OptimizeMesh();
					meshObj.GenerateLodGroup([0.5f, 0.25f, 0.1f, 0.05f, 0.0025f]);
				}

				var meshId = Meshes.Count;
				Meshes.Add(meshObj);
				primitiveToMeshIdMap[primitive] = meshId;
			}
		}

		foreach (var node in model.LogicalNodes)
		{
			if (node.Mesh == null)
			{
				continue;
			}

			Matrix4x4.Decompose(node.WorldMatrix, out var scale, out var rotation, out var translation);

			foreach (var primitive in node.Mesh.Primitives)
			{
				if (primitiveToMeshIdMap.TryGetValue(primitive, out int meshId))
				{
					var material = primitive.Material;
					int materialId = material?.LogicalIndex ?? -1;
					
					var transform = new Transform
					{
						position = translation,
						rotation = rotation,
						scale = scale
					};

					instances.Add(new InstanceData
					{
						transform = transform,
						meshId = meshId,
						materialId = materialId
					});
				}
			}
		}
	}
}