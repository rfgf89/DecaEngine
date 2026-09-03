using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using DecaEngine.Animation;
using MeshOptimizer;
using UnsafeCollections.Collections.Unsafe;

namespace DecaEngine.Graphics;

/// <summary>Geometry processing on top of meshopt: vertex dedup, reorder optimization and LOD generation.</summary>
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

	// CPU-array variants below run off the GPU thread, before any GPU resource exists.

	/// <summary>Fills <see cref="Vertex.Tangent"/> in place from triangle positions/UVs; degenerate UVs fall back to an arbitrary perpendicular.</summary>
	public static void GenerateTangents(Vertex[] vertices, uint[] indices)
	{
		if (indices.Length == 0 || indices.Length % 3 != 0)
		{
			return;
		}

		var accumulated = new Vector3[vertices.Length];
		var accumulatedBitangent = new Vector3[vertices.Length];

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
				continue;
			}

			var tangent = (edge1 * duv2.Y - edge2 * duv1.Y) * (1f / det);
			// Bitangent tracked separately for the w sign: mirrored UVs point against cross(N, T).
			var bitangent = (edge2 * duv1.X - edge1 * duv2.X) * (1f / det);

			accumulated[i0] += tangent;
			accumulated[i1] += tangent;
			accumulated[i2] += tangent;
			accumulatedBitangent[i0] += bitangent;
			accumulatedBitangent[i1] += bitangent;
			accumulatedBitangent[i2] += bitangent;
		}

		for (int i = 0; i < vertices.Length; i++)
		{
			var normal = vertices[i].Normal;
			var tangent = accumulated[i];

			// Gram-Schmidt against the normal so averaged non-coplanar contributions stay tangent-plane.
			tangent -= normal * Vector3.Dot(normal, tangent);

			// w sign computed from already-mirrored geometry, so no glTF handedness correction needed.
			float w = Vector3.Dot(Vector3.Cross(normal, Vector3.Normalize(
				tangent.LengthSquared() > 1e-12f ? tangent : ArbitraryTangent(normal))),
				accumulatedBitangent[i]) < 0f ? -1f : 1f;

			vertices[i].Tangent = tangent.LengthSquared() > 1e-12f
				? new Vector4(Vector3.Normalize(tangent), w)
				: new Vector4(ArbitraryTangent(normal), 1f);
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

	public static unsafe (T[] vertices, uint[] indices) OptimizeMeshData<T>(T[] vertices, uint[] indices)
		where T : unmanaged, IMeshVertex
	{
		if (indices.Length == 0 || indices.Length % 3 != 0)
		{
			return (vertices, indices);
		}

		int vertexCount = vertices.Length;
		var remap = new uint[vertexCount];
		var workingVertices = (T[])vertices.Clone();
		var workingIndices = (uint[])indices.Clone();

		var uniqueVertexCount = (int)Meshopt.GenerateVertexRemap(remap, workingIndices, workingVertices);

		var remappedVertices = new T[vertexCount];
		Meshopt.RemapVertexBuffer(remappedVertices, workingVertices, remap);
		Meshopt.RemapIndexBuffer(workingIndices, workingIndices, remap);

		Meshopt.OptimizeVertexCache(workingIndices, workingIndices, (UIntPtr)uniqueVertexCount);

		var finalVertices = new Span<T>(remappedVertices, 0, uniqueVertexCount);
		Meshopt.OptimizeVertexFetch(MemoryMarshal.Cast<T, uint>(finalVertices), workingIndices, finalVertices);

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

	public static unsafe (T[] vertices, uint[] indices, LodLevel[] lodLevels) GenerateLodGroupData<T>(
		T[] baseVertices, uint[] baseIndices, float[] levels)
		where T : unmanaged, IMeshVertex
	{
		List<T[]> allVertices = new() { baseVertices };
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

			var lodVerticesResult = new T[uniqueLodVertexCount];
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

		var combinedVertices = new T[currentVertexOffset];
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

	/// <summary>Interleaves geometry and skin streams into one vertex for a meshopt pass.</summary>
	public static SkinnedVertex[] PackSkinned(Vertex[] vertices, SkinVertex[] skin)
	{
		var packed = new SkinnedVertex[vertices.Length];
		for (int i = 0; i < packed.Length; i++)
		{
			packed[i] = new SkinnedVertex
			{
				Geometry = vertices[i],
				// If the skin stream is shorter, missing vertices are pinned to the root joint.
				Skin = i < skin.Length ? skin[i] : new SkinVertex { W0 = (ushort)SkinVertex.WeightScale },
			};
		}

		return packed;
	}

	/// <summary>Inverse of <see cref="PackSkinned"/>: splits streams back apart after meshopt.</summary>
	public static (Vertex[] Vertices, SkinVertex[] Skin) UnpackSkinned(SkinnedVertex[] packed)
	{
		var vertices = new Vertex[packed.Length];
		var skin = new SkinVertex[packed.Length];

		for (int i = 0; i < packed.Length; i++)
		{
			vertices[i] = packed[i].Geometry;
			skin[i] = packed[i].Skin;
		}

		return (vertices, skin);
	}
}

