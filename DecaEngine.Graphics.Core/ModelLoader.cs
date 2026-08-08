using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using SharpGLTF.Schema2;
using DecaEngine.Core;
using DecaEngine.Graphics.Core;
using System.Runtime.InteropServices;
using Diligent;
using MeshOptimizer;
using StbImageSharp;
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

	// --- Pure CPU-array variants below, operating on plain managed arrays instead of an already-created
	// IMeshObject's native buffers. Used by ModelLoader's background load pipeline (see
	// ModelLoader.PrepareModel) so mesh optimization/LOD generation - all pure CPU meshoptimizer work -
	// can run off the main/GPU thread, before any GPU resource is created for the mesh. ---

	/// <summary>
	/// Fills in <see cref="Vertex.Tangent"/> for every vertex from the triangle's positions/UVs (the
	/// standard per-triangle tangent formula, accumulated per vertex across its adjacent triangles, then
	/// Gram-Schmidt orthogonalized against <see cref="Vertex.Normal"/> and normalized). Mutates
	/// <paramref name="vertices"/> in place. No-op (leaves whatever Tangent already holds) for a
	/// non-triangle index list; a triangle with a degenerate UV mapping (zero UV area) simply doesn't
	/// contribute to its vertices' accumulated tangent, and a vertex left with a near-zero accumulated
	/// tangent (e.g. no real UVs at all) falls back to an arbitrary vector perpendicular to its normal.
	/// </summary>
	public static void GenerateTangents(Vertex[] vertices, uint[] indices)
	{
		if (indices.Length == 0 || indices.Length % 3 != 0)
		{
			return;
		}

		var accumulated = new Vector3[vertices.Length];

		for (int i = 0; i < indices.Length; i += 3)
		{
			uint i0 = indices[i], i1 = indices[i + 1], i2 = indices[i + 2];

			var edge1 = vertices[i1].Position - vertices[i0].Position;
			var edge2 = vertices[i2].Position - vertices[i0].Position;
			var duv1 = vertices[i1].TexCoord - vertices[i0].TexCoord;
			var duv2 = vertices[i2].TexCoord - vertices[i0].TexCoord;

			float det = duv1.X * duv2.Y - duv2.X * duv1.Y;
			if (MathF.Abs(det) < 1e-12f)
			{
				// Degenerate (or absent) UV mapping for this triangle - it can't define a tangent
				// direction, so it simply doesn't contribute to its vertices' accumulated tangent.
				continue;
			}

			var tangent = (edge1 * duv2.Y - edge2 * duv1.Y) * (1f / det);

			accumulated[i0] += tangent;
			accumulated[i1] += tangent;
			accumulated[i2] += tangent;
		}

		for (int i = 0; i < vertices.Length; i++)
		{
			var normal = vertices[i].Normal;
			var tangent = accumulated[i];

			// Gram-Schmidt: remove whatever component of the accumulated tangent already points along
			// the normal, so the result is a valid tangent-plane direction even after averaging
			// contributions from triangles that aren't perfectly coplanar.
			tangent -= normal * Vector3.Dot(normal, tangent);

			vertices[i].Tangent = tangent.LengthSquared() > 1e-12f
				? Vector3.Normalize(tangent)
				: ArbitraryTangent(normal);
		}
	}

	private static Vector3 ArbitraryTangent(Vector3 normal)
	{
		var up = MathF.Abs(normal.Y) < 0.999f ? Vector3.UnitY : Vector3.UnitX;
		return Vector3.Normalize(Vector3.Cross(up, normal));
	}

	public static unsafe (Vector3 center, float radius) ComputeBoundsData(Vertex[] vertices)
	{
		if (vertices.Length == 0)
		{
			return (Vector3.Zero, 0f);
		}

		var positions = new Vector3[vertices.Length];
		for (int i = 0; i < positions.Length; i++)
		{
			positions[i] = vertices[i].Position;
		}

		fixed (Vector3* ptr = positions)
		{
			var bound = Meshopt.ComputeSphereBounds((float*)ptr, (UIntPtr)positions.Length, (UIntPtr)sizeof(Vector3), null, UIntPtr.Zero);
			return (new Vector3(bound.center[0], bound.center[1], bound.center[2]), bound.radius);
		}
	}

	public static unsafe (Vertex[] vertices, uint[] indices) OptimizeMeshData(Vertex[] vertices, uint[] indices)
	{
		if (indices.Length == 0 || indices.Length % 3 != 0)
		{
			return (vertices, indices);
		}

		int vertexCount = vertices.Length;
		var remap = new uint[vertexCount];
		var workingVertices = (Vertex[])vertices.Clone();
		var workingIndices = (uint[])indices.Clone();

		var uniqueVertexCount = (int)Meshopt.GenerateVertexRemap(remap, workingIndices, workingVertices);

		var remappedVertices = new Vertex[vertexCount];
		Meshopt.RemapVertexBuffer(remappedVertices, workingVertices, remap);
		Meshopt.RemapIndexBuffer(workingIndices, workingIndices, remap);

		Meshopt.OptimizeVertexCache(workingIndices, workingIndices, (UIntPtr)uniqueVertexCount);

		var finalVertices = new Span<Vertex>(remappedVertices, 0, uniqueVertexCount);
		Meshopt.OptimizeVertexFetch(MemoryMarshal.Cast<Vertex, uint>(finalVertices), workingIndices, finalVertices);

		var finalPositions = new Vector3[uniqueVertexCount];
		for (int i = 0; i < finalPositions.Length; i++)
		{
			finalPositions[i] = finalVertices[i].Position;
		}

		var finalIndices = new uint[indices.Length];
		fixed (Vector3* posPtr = finalPositions)
		fixed (uint* indicesPtr = workingIndices)
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

		return (finalVertices.ToArray(), finalIndices);
	}

	public static unsafe (Vertex[] vertices, uint[] indices, LodLevel[] lodLevels) GenerateLodGroupData(
		Vertex[] baseVertices, uint[] baseIndices, float[] levels)
	{
		List<Vertex[]> allVertices = new() { baseVertices };
		List<uint[]> allIndices = new() { baseIndices };
		List<LodLevel> lodLevelsList = new();

		lodLevelsList.Add(new LodLevel { error = 0, firstIndex = 0, indexCount = baseIndices.Length, vertexOffset = 0 });

		if (baseIndices.Length == 0 || levels.Length <= 0)
		{
			return (baseVertices, baseIndices, lodLevelsList.ToArray());
		}

		int baseVertexCount = baseVertices.Length;
		int currentVertexOffset = baseVertexCount;
		int currentIndexOffset = baseIndices.Length;

		var positions = new Vector3[baseVertexCount];
		var texCoords = new Vector2[baseVertexCount];
		for (int i = 0; i < baseVertexCount; i++)
		{
			positions[i] = baseVertices[i].Position;
			texCoords[i] = baseVertices[i].TexCoord;
		}

		float scale;
		fixed (Vector3* p = positions)
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
				fixed (float* attrPtr = attributeWeights)
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

		var combinedVertices = new Vertex[currentVertexOffset];
		var combinedIndices = new uint[currentIndexOffset];

		int vOffset = 0, iOffset = 0;
		for (int i = 0; i < allVertices.Count; i++)
		{
			Array.Copy(allVertices[i], 0, combinedVertices, vOffset, allVertices[i].Length);
			Array.Copy(allIndices[i], 0, combinedIndices, iOffset, allIndices[i].Length);
			vOffset += allVertices[i].Length;
			iOffset += allIndices[i].Length;
		}

		return (combinedVertices, combinedIndices, lodLevelsList.ToArray());
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

	/// <summary>
	/// Precomputed per-vertex tangent (see <see cref="MeshUtility.GenerateTangents"/>), Gram-Schmidt
	/// orthogonalized against Normal and normalized. No handedness/bitangent-sign component - this
	/// engine has no normal-mapped materials yet, so it's currently consumed only by the Model Preview
	/// Channel debug view (see DecaEngine.Editor's UnlitInstancedPS.hlsl PreviewChannel == Tangent),
	/// which only needs a direction to visualize.
	/// </summary>
	public Vector3 Tangent;
}

public class ModelLoader
{
	public List<InstanceData> instances = new();

	public List<IMeshObject> Meshes = new();

	/// <summary>
	/// Parallel to <see cref="Meshes"/>: whether the glTF primitive that became Meshes[i] had a real
	/// TEXCOORD_0 accessor, as opposed to synthesized all-zero UVs (see PrepareModel). Used to gate the
	/// Tangent channel option in <see cref="DecaEngine.Editor.ModelPreviewViewport"/>'s Channel debug
	/// view - a derivative-based tangent computed from degenerate (0,0) UVs is meaningless.
	/// </summary>
	public List<bool> MeshHasUv = new();

	public OrderedDictionary<int, IMaterialObject> materialObjects = new();

	/// <summary>
	/// ????????? ????? (world-space) AABB ???? ?????, ????????? bounding-????? (<see
	/// cref="IMeshObject.Center"/>/<see cref="IMeshObject.Radius"/>, ??. <see
	/// cref="MeshUtility.RecalculateBounds"/>) ??????? <see cref="InstanceData"/>, ??????????????????
	/// ??? <see cref="Transform"/>. ?????? ????? ?????? ??????? ?? ?????????? ???????? (??????????
	/// glTF-????/?????) - ??????? ???????????? bound ??????? ???? ?????????? ????????????, ?????
	/// ?????????? bounds ???? ?????????/???????? ?????, ????? ????? ?????? ????? ???????? ??????
	/// ?????? ??? ???????? ?????? ??????. ?????????? (Vector3.Zero, Vector3.Zero), ???? ? ????? ???
	/// ?? ?????? ????????? ????????.
	/// </summary>
	public (Vector3 min, Vector3 max) ComputeBounds()
	{
		var min = new Vector3(float.MaxValue);
		var max = new Vector3(float.MinValue);
		var any = false;

		foreach (var instance in instances)
		{
			if (instance.meshId < 0 || instance.meshId >= Meshes.Count)
			{
				continue;
			}

			var mesh = Meshes[instance.meshId];
			var t = instance.transform;

			// ??????? ?????? ????????????????? ???????: Scale -> Rotate -> Translate
			var matrix = Matrix4x4.CreateScale(t.scale.X, t.scale.Y, t.scale.Z) *
						 Matrix4x4.CreateFromQuaternion(t.rotation) *
						 Matrix4x4.CreateTranslation(t.position);

			// ?????????????? ????????? ????? ? world-space
			var worldCenter = Vector3.Transform(mesh.Center, matrix);

			// ??? ??????? ?????????? ???????????? ????????? scale (?????????????? ??????)
			var worldRadius = mesh.Radius * MathF.Max(MathF.Abs(t.scale.X),
				MathF.Max(MathF.Abs(t.scale.Y), MathF.Abs(t.scale.Z)));

			// ????????? - ?????????? ???????? ? NaN ??? Infinity ? bounds
			if (float.IsNaN(worldCenter.X) || float.IsNaN(worldCenter.Y) || float.IsNaN(worldCenter.Z) ||
			    float.IsNaN(worldRadius) || float.IsInfinity(worldCenter.X) || float.IsInfinity(worldCenter.Y) ||
			    float.IsInfinity(worldCenter.Z) || float.IsInfinity(worldRadius) || worldRadius <= 0)
			{
				continue;
			}

			var extent = new Vector3(worldRadius);

			min = Vector3.Min(min, worldCenter - extent);
			max = Vector3.Max(max, worldCenter + extent);
			any = true;
		}

		return any ? (min, max) : (Vector3.Zero, Vector3.Zero);
	}

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

	/// <summary>
	/// Path to a lightweight placeholder model bundled at EditorAssets/models/result.gltf, used when
	/// no other model has been selected (the full Sponza.gltf scene this once stood in for isn't
	/// shipped in this repo - large external asset).
	/// </summary>
	public const string DefaultModelPath = "EditorAssets/models/result.gltf";

	private ModelLoader()
	{
	}

	/// <summary>
	/// Kicks off loading a .gltf/.glb file from <paramref name="modelPath"/> (absolute or relative to
	/// <see cref="Environment.CurrentDirectory"/>) in the background: file I/O, glTF parsing, texture
	/// decoding and mesh optimization/LOD generation (all pure-CPU work) run on a thread-pool thread via
	/// <see cref="Task.Run(Action)"/>. GPU resource creation (shaders/materials/textures/meshes) cannot
	/// safely happen off the main thread (Diligent's immediate device context isn't thread-safe - see
	/// DiligentGraphicsApi.CreateTexture), so it's deferred to <see cref="ModelLoadRequest.FinalizeOnMainThread"/>,
	/// which the caller must invoke from the same thread that owns <paramref name="graphicsApi"/> once
	/// the request is ready. <paramref name="progress"/>, if given, receives 0..1 completion updates from
	/// the background thread. Used both by the main editor scene (<see
	/// cref="DecaEngine.Editor.EditorManager"/>) and <see cref="DecaEngine.Editor.ModelPreviewViewport"/>'s
	/// lightweight Asset Browser preview.
	/// </summary>
	public static ModelLoadRequest BeginLoadAsync(IGraphicsApi graphicsApi, string modelPath, ModelLoadOptions options,
		IProgress<float> progress = null, CancellationToken cancellationToken = default)
	{
		if (!Path.IsPathRooted(modelPath))
		{
			modelPath = Path.Combine(Environment.CurrentDirectory, modelPath);
		}

		if (!File.Exists(modelPath))
		{
			throw new FileNotFoundException(
				$"Model scene not found: '{modelPath}'.",
				modelPath);
		}

		return new ModelLoadRequest(graphicsApi, modelPath, options, progress, cancellationToken);
	}

	/// <summary>
	/// Synchronously loads and finalizes a model - equivalent to <see cref="BeginLoadAsync"/> followed by
	/// blocking until ready and calling <see cref="ModelLoadRequest.FinalizeOnMainThread"/>. Blocks the
	/// calling thread for the entire load; prefer <see cref="BeginLoadAsync"/> in the editor so the UI
	/// stays responsive and a progress indicator can be shown.
	/// </summary>
	public static ModelLoader Load(IGraphicsApi graphicsApi, string modelPath, ModelLoadOptions options)
	{
		var request = BeginLoadAsync(graphicsApi, modelPath, options);
		request.PrepareTask.GetAwaiter().GetResult();
		return request.FinalizeOnMainThread();
	}

	private static PreparedModel PrepareModel(string modelPath, ModelLoadOptions options,
		IProgress<float> progress, CancellationToken cancellationToken)
	{
		var model = ModelRoot.Load(modelPath);
		cancellationToken.ThrowIfCancellationRequested();

		var prepared = new PreparedModel();

		// Weight the two big background phases (materials/textures, meshes) roughly by count so the
		// progress bar moves at a believable pace instead of jumping straight to 50%.
		int materialCount = Math.Max(1, model.LogicalMaterials.Count);
		int primitiveCount = Math.Max(1, model.LogicalMeshes.Sum(m => m.Primitives.Count));

		for (var index = 0; index < model.LogicalMaterials.Count; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var logicalMaterial = model.LogicalMaterials[index];

			if (logicalMaterial == null)
			{
				prepared.Materials.Add(new PreparedMaterial { LogicalIndex = index, IsNull = true });
				progress?.Report(0.05f + 0.35f * ((index + 1) / (float)materialCount));
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
				var encodedBytes = baseColorTexture.PrimaryImage.Content.Content.ToArray();
				var decoded = ImageResult.FromMemory(encodedBytes, ColorComponents.RedGreenBlueAlpha);

				var sampler = baseColorTexture.Sampler;
				preparedMaterial.BaseColorTexture = new PreparedTexture
				{
					Pixels = decoded.Data,
					Width = decoded.Width,
					Height = decoded.Height,
					AddressMode = ToAddressMode(sampler.WrapS),
					FilterMode = ToFilter(sampler.MinFilter, sampler.MagFilter),
				};
			}

			prepared.Materials.Add(preparedMaterial);
			progress?.Report(0.05f + 0.35f * ((index + 1) / (float)materialCount));
		}

		var primitiveToMeshIdMap = new Dictionary<MeshPrimitive, int>();
		int primitivesDone = 0;

		foreach (var logicalMesh in model.LogicalMeshes)
		{
			var baseMeshName = logicalMesh.Name ?? $"Mesh_{logicalMesh.LogicalIndex}";

			for (var primitiveIndex = 0; primitiveIndex < logicalMesh.Primitives.Count; primitiveIndex++)
			{
				var primitive = logicalMesh.Primitives[primitiveIndex];
				cancellationToken.ThrowIfCancellationRequested();

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

				var sourceIndices = indices.ToArray();
				var (boundsCenter, boundsRadius) = MeshUtility.ComputeBoundsData(sourceVertices);

				var finalVertices = sourceVertices;
				var finalIndices = sourceIndices;
				LodLevel[] lodLevels = null;

				if (primitive.DrawPrimitiveType == PrimitiveType.TRIANGLES)
				{
					// Must run before Optimize/GenerateLods reorder/remap vertices - it needs the
					// pristine per-triangle winding to compute per-triangle tangents, but the resulting
					// per-vertex Tangent then rides along automatically through any later remap (it's
					// just another Vertex field, opaque to Meshopt's vertex-remap/simplify passes).
					MeshUtility.GenerateTangents(sourceVertices, sourceIndices);

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

				// A glTF logical mesh with multiple primitives (e.g. one node using several materials)
				// becomes multiple sub-meshes here, one per primitive - without a per-primitive suffix
				// they'd all inherit the same logicalMesh.Name and be indistinguishable in the sub-mesh
				// list (same label for every entry, even though each is a distinct piece of geometry).
				var meshName = logicalMesh.Primitives.Count > 1 ? $"{baseMeshName}.{primitiveIndex}" : baseMeshName;

				var meshId = prepared.Meshes.Count;
				prepared.Meshes.Add(new PreparedMesh
				{
					Name = meshName,
					Vertices = finalVertices,
					Indices = finalIndices,
					LodLevels = lodLevels,
					BoundsCenter = boundsCenter,
					BoundsRadius = boundsRadius,
					HasUv = uvsAccessor != null,
				});
				primitiveToMeshIdMap[primitive] = meshId;

				primitivesDone++;
				progress?.Report(0.4f + 0.55f * (primitivesDone / (float)primitiveCount));
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
		return prepared;
	}

	private static ModelLoader BuildFromPrepared(IGraphicsApi graphicsApi, ModelLoadOptions options, PreparedModel prepared)
	{
		var result = new ModelLoader();

		var (vsFactoryPath, vsFileName) = options.VertexShader.ToShaderFactoryParts();
		var (psFactoryPath, psFileName) = options.PixelShader.ToShaderFactoryParts();
		var modelShaderVs = graphicsApi.CreateShader("Model Vertex Shader", vsFactoryPath, vsFileName, ShaderObjectType.Vertex);
		var modelShaderPs = graphicsApi.CreateShader("Model Pixel Shader", psFactoryPath, psFileName, ShaderObjectType.Pixel);

		var defaultMaterial = graphicsApi.CreateMaterial("Default Material");
		defaultMaterial.SetShader(modelShaderPs, modelShaderVs);

		result.materialObjects.Add(-1, defaultMaterial);

		foreach (var preparedMaterial in prepared.Materials)
		{
			if (preparedMaterial.IsNull)
			{
				result.materialObjects.Add(preparedMaterial.LogicalIndex, defaultMaterial);
				continue;
			}

			var materialObj = graphicsApi.CreateMaterial(preparedMaterial.Name);
			materialObj.SetShader(modelShaderPs, modelShaderVs);

			var preparedTexture = preparedMaterial.BaseColorTexture;
			if (preparedTexture != null)
			{
				var cpuData = new CpuTextureData
				{
					Name = "_MainTex",
					DecodedPixels = preparedTexture.Pixels,
					DecodedWidth = preparedTexture.Width,
					DecodedHeight = preparedTexture.Height,
				};

				var texture = new Core.Texture(cpuData.Name, cpuData);
				texture.Upload(graphicsApi, true);

				var samplerObject = graphicsApi.CreateSampler(
					name: "_MainTex_Sampler",
					filter: preparedTexture.FilterMode,
					address: preparedTexture.AddressMode,
					comparisonFunction: CompFunction.Always,
					border: Vector4.Zero
				);

				materialObj.SetTexture("_MainTex", texture.GpuHandle);
				materialObj.SetImmutableSampler("_MainTex", samplerObject);
			}

			result.materialObjects.Add(preparedMaterial.LogicalIndex, materialObj);
		}

		foreach (var preparedMesh in prepared.Meshes)
		{
			var meshObj = graphicsApi.CreateMesh(preparedMesh.Name);
			meshObj.SetVertices(preparedMesh.Vertices);
			meshObj.SetIndices(preparedMesh.Indices);
			meshObj.SetBounds(preparedMesh.BoundsCenter, preparedMesh.BoundsRadius);

			if (preparedMesh.LodLevels != null)
			{
				unsafe
				{
					var lodsNative = UnsafeArray.Allocate<LodLevel>(preparedMesh.LodLevels.Length);
					for (int i = 0; i < preparedMesh.LodLevels.Length; i++)
					{
						UnsafeArray.Set(lodsNative, i, preparedMesh.LodLevels[i]);
					}
					meshObj.SetLodGroup(lodsNative);
				}
			}

			result.Meshes.Add(meshObj);
			result.MeshHasUv.Add(preparedMesh.HasUv);
		}

		result.instances.AddRange(prepared.Instances);

		return result;
	}

	private sealed class PreparedTexture
	{
		public byte[] Pixels;
		public int Width;
		public int Height;
		public TextureAddress AddressMode;
		public TextureFilter FilterMode;
	}

	private sealed class PreparedMaterial
	{
		public int LogicalIndex;
		public bool IsNull;
		public string Name;
		public PreparedTexture BaseColorTexture;
	}

	private sealed class PreparedMesh
	{
		public string Name;
		public Vertex[] Vertices;
		public uint[] Indices;
		public LodLevel[] LodLevels;
		public Vector3 BoundsCenter;
		public float BoundsRadius;
		public bool HasUv;
	}

	private sealed class PreparedModel
	{
		public List<PreparedMaterial> Materials = new();
		public List<PreparedMesh> Meshes = new();
		public List<InstanceData> Instances = new();
	}

	/// <summary>
	/// Handle to an in-flight background <see cref="ModelLoader"/> load (see <see cref="BeginLoadAsync"/>).
	/// Poll <see cref="PrepareTask"/>/<see cref="Progress"/> from the render loop and, once the task
	/// completes successfully, call <see cref="FinalizeOnMainThread"/> on the graphics thread to create
	/// the actual GPU resources and obtain the ready <see cref="ModelLoader"/>.
	/// </summary>
	public sealed class ModelLoadRequest
	{
		private readonly IGraphicsApi _graphicsApi;
		private readonly ModelLoadOptions _options;
		private readonly ProgressTracker _progressTracker = new();

		public string ModelPath { get; }
		public Task PrepareTask { get; }
		public float Progress => _progressTracker.Value;

		private PreparedModel _prepared;

		internal ModelLoadRequest(IGraphicsApi graphicsApi, string modelPath, ModelLoadOptions options,
			IProgress<float> externalProgress, CancellationToken cancellationToken)
		{
			_graphicsApi = graphicsApi;
			_options = options;
			ModelPath = modelPath;

			var combinedProgress = new Progress<float>(p =>
			{
				_progressTracker.Value = p;
				externalProgress?.Report(p);
			});

			PrepareTask = Task.Run(() =>
			{
				_prepared = PrepareModel(modelPath, options, combinedProgress, cancellationToken);
			}, cancellationToken);
		}

		/// <summary>
		/// Creates the GPU resources (shaders/materials/textures/meshes) for a completed background load
		/// and returns the ready <see cref="ModelLoader"/>. Must be called on the thread that owns the
		/// <see cref="IGraphicsApi"/> passed to <see cref="BeginLoadAsync"/> (i.e. the main/render
		/// thread), only after <see cref="PrepareTask"/> has completed successfully.
		/// </summary>
		public ModelLoader FinalizeOnMainThread()
		{
			if (!PrepareTask.IsCompletedSuccessfully)
			{
				throw new InvalidOperationException(
					"FinalizeOnMainThread called before the background load finished successfully.");
			}

			return BuildFromPrepared(_graphicsApi, _options, _prepared);
		}

		private sealed class ProgressTracker
		{
			private float _value;
			public float Value
			{
				get => Volatile.Read(ref _value);
				set => Volatile.Write(ref _value, value);
			}
		}
	}
}