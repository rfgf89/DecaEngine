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
				// Degenerate (or absent) UV mapping for this triangle - it can't define a tangent
				// direction, so it simply doesn't contribute to its vertices' accumulated tangent.
				continue;
			}

			var tangent = (edge1 * duv2.Y - edge2 * duv1.Y) * (1f / det);
			// Битангент (направление роста V) копится отдельно ради знака w ниже: у зеркальных
			// UV-развёрток он смотрит ПРОТИВ cross(N, T), и без знака нормал-мапа применяется с
			// перевёрнутым Y (инвертированный рельеф).
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

			// Gram-Schmidt: remove whatever component of the accumulated tangent already points along
			// the normal, so the result is a valid tangent-plane direction even after averaging
			// contributions from triangles that aren't perfectly coplanar.
			tangent -= normal * Vector3.Dot(normal, tangent);

			// Знак битангента в пространстве движка: куда реально растёт V относительно cross(N, T).
			// Вычислен из уже зеркалированной геометрии, так что никаких поправок на смену
			// ориентации (в отличие от авторского glTF w) не требует.
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
	/// Per-vertex tangent, xyz = направление роста U на поверхности (мировое после VS), w = знак
	/// битангента (±1): B = cross(N, T) * w в шейдере (см. UnlitInstancedPS.hlsl,
	/// FEATURE_NORMAL_MAPS). Источник - авторский glTF TANGENT, когда он есть (знак w инвертируется
	/// при зеркалировании Z: зеркало меняет ориентацию базиса), иначе - <see
	/// cref="MeshUtility.GenerateTangents"/>, вычисляющий и направление, и знак прямо в
	/// пространстве движка. Без верного w зеркальные UV-развёртки (атласы, симметричные модели)
	/// получают перевёрнутый Y нормал-мапы - рельеф инвертируется.
	/// </summary>
	public Vector4 Tangent;

	/// <summary>glTF COLOR_0 (линейный, по спеке умножается на base color); белый (1,1,1,1) для
	/// мешей без атрибута - ноль по умолчанию структуры красил бы всё в чёрное.</summary>
	public Vector4 Color;

	/// <summary>glTF TEXCOORD_1 - второй UV-канал; по спеке им чаще всего пользуется
	/// occlusionTexture с AO-картой, запечённой под отдельную уникальную развёртку (см.
	/// <see cref="MaterialPbrFactors.OcclusionUvSet"/>). Ноль для мешей без атрибута.</summary>
	public Vector2 TexCoord1;
}

/// <summary>
/// glTF PBR metallic-roughness scalars of one material (see <see cref="ModelLoader.MaterialPbr"/>).
/// Defaults follow the glTF spec (baseColor white, metallic 1, roughness 1) unless the material
/// explicitly authored other values.
/// </summary>
public struct MaterialPbrFactors
{
	public Vector4 BaseColorFactor;
	public float MetallicFactor;
	public float RoughnessFactor;

	/// <summary>Среднее ЛИНЕЙНОЕ альбедо материала (среднее по base color текстуре × фактор; без
	/// текстуры - просто фактор). Цвет отскока для CPU-бейка probe-GI (см. DecaEngine.Editor.ProbeGiBaker):
	/// трассировщику нужен один цвет на материал, а не сэмплинг текстур.</summary>
	public Vector3 AverageBaseColor;

	/// <summary>Средняя альфа base color текстуры × фактор (1 без текстуры). Probe-GI бейкер по
	/// ней отличает «дырявые» материалы (листва: альфа мала - не блокируют лучи) от сплошных,
	/// даже если экспортер пометил их MASK/BLEND (камень: альфа ~1).</summary>
	public float AverageAlpha;

	/// <summary>Whether the material has a base-color texture bound as _MainTex - lets a shader
	/// decide between sampling it and using <see cref="BaseColorFactor"/> alone (an unbound
	/// _MainTex cannot be detected from HLSL).</summary>
	public bool HasBaseColorTexture;

	/// <summary>Whether the material has a metallic-roughness texture bound as _MetallicRoughnessTex
	/// (glTF packing: G = roughness, B = metallic; <see cref="MetallicFactor"/>/<see cref="RoughnessFactor"/>
	/// are multipliers over it). Same unbound-texture rationale as <see cref="HasBaseColorTexture"/>.</summary>
	public bool HasMetallicRoughnessTexture;

	/// <summary>KHR_materials_transmission scalar factor (0 = opaque, 1 = fully transmissive glass);
	/// the preview approximates it without a refraction pass - see UnlitInstancedPS.hlsl.</summary>
	public float TransmissionFactor;

	/// <summary>KHR_materials_ior (glTF default 1.5 - every construction site must set it, this is a
	/// struct so an unset field is 0, which would zero out fresnel F0 in the shader).</summary>
	public float Ior;

	/// <summary>KHR_materials_dispersion strength (20 / Abbe number, 0 = off); the preview fakes it
	/// with per-channel refraction of the backdrop - see UnlitInstancedPS.hlsl.</summary>
	public float Dispersion;

	/// <summary>KHR_materials_volume, precomputed for the shader: rgb = attenuationColor, w =
	/// thicknessFactor / attenuationDistance (Beer-Lambert exponent; 0 = no volume attenuation).
	/// Учитывает масштаб узла - см. ScaleVolumeAttenuation.</summary>
	public Vector4 VolumeAttenuation;

	/// <summary>Толщина стекла в МИРОВЫХ единицах (thicknessFactor × масштаб узла) - геометрическая
	/// длина преломлённого луча внутри объёма для расчёта смещения рефракции в шейдере.</summary>
	public float ThicknessWorld;

	/// <summary>Код топологии примитивов этого материала (MeshTopology*-константы ModelLoader) -
	/// не-треугольным нужен PSO с соответствующей PrimitiveTopology, см. RegisterModelResources.</summary>
	public int Topology;

	/// <summary>glTF normalScale (множитель xy-каналов нормал-мапы; 1 = как есть). Материалы без
	/// нормал-мапы получают "плоский" филлер (0,0,1), так что отдельный has-флаг не нужен.</summary>
	public float NormalScale;

	/// <summary>glTF occlusionStrength (вес запечённого AO из _OcclusionTex; 1 = как в текстуре).
	/// Материалы без AO-текстуры получают белый филлер (R=1 = не заслонено).</summary>
	public float OcclusionStrength;

	/// <summary>Alpha-clip threshold for the shader: pixels whose base-color alpha falls below it
	/// are discarded; 0 disables clipping. glTF alphaMode MASK maps to the authored alphaCutoff,
	/// BLEND to 0.5 (the preview has no blending, so mostly-transparent texels vanish and
	/// mostly-opaque ones render solid - e.g. Intel Sponza's dirt_decal overlays at alpha 0.35
	/// would otherwise cover the walls as fully opaque grime), OPAQUE to 0.</summary>
	public float AlphaCutoff;

	/// <summary>KHR_texture_transform, предвычисленная в 2x2-матрицу UV (row-major: u' = X*u + Y*v,
	/// v' = Z*u + W*v, затем + <see cref="UvOffset"/>). Одна на материал - берётся с baseColor-канала
	/// (фоллбек normal/MR, каналы одного материала практически всегда делят одну трансформацию).
	/// Валидна только при <see cref="HasUvTransform"/>.</summary>
	public Vector4 UvTransform;

	/// <summary>KHR_texture_transform offset - слагаемое после матрицы <see cref="UvTransform"/>.</summary>
	public Vector2 UvOffset;

	/// <summary>Есть ли у материала KHR_texture_transform. Шейдер применяет матрицу только по этому
	/// флагу, так что зануленный по умолчанию cbuffer (сцены вне превью его не заполняют) остаётся
	/// тождественным преобразованием.</summary>
	public bool HasUvTransform;

	/// <summary>Индекс UV-канала occlusionTexture (glTF texCoord; поддержаны 0 и 1, см.
	/// <see cref="Vertex.TexCoord1"/>) - AO часто запечён под уникальную развёртку второго канала.</summary>
	public int OcclusionUvSet;

	/// <summary>KHR_materials_sheen, упакованный для шейдера: rgb = sheenColorFactor (линейный;
	/// ноль = расширение не авторское, велюровый лоб выключен), w = sheenRoughnessFactor.
	/// Даёт ткани "световой ворс" - ретрорефлективный ободок Charlie-лоба (велюр/бархат).</summary>
	public Vector4 SheenColorRoughness;

	/// <summary>KHR_materials_specular, упакованный для шейдера: rgb = specularColorFactor
	/// (может быть &gt;1 - по спеке умножается на F0 от IOR и клампится к 1), w = specularFactor
	/// (вес всего диэлектрического спекуляра). (1,1,1,1) = расширение не авторское, тождественно;
	/// это struct - каждая точка конструирования обязана заполнить его, иначе нулевой w глушит
	/// спекуляр в чёрный (та же ловушка, что у <see cref="Ior"/>).</summary>
	public Vector4 SpecularColorFactor;
}

public class ModelLoader
{
	public List<InstanceData> instances = new();

	/// <summary>Разбивка времени загрузки и объём декодированных текстур - диагностика.
	/// Фазы стоят очень по-разному на разных ассетах, и «очевидный» виновник обычно не тот:
	/// без этих чисел оптимизация загрузки - гадание.</summary>
	public readonly record struct LoadTimings(long ParseMs, long DecodeMs, long MaterialsMs, long MeshesMs,
		long FinalizeMs, int DecodedImages, long DecodedBytes, int ShaderVariants, long ShaderMs,
		int TextureUploads, long TextureMs, int MeshUploads, long MeshMs, int Samplers, long SamplerMs, int MaterialsBuilt, long MaterialBuildMs, long MatCreateMs, long MatShaderMs);

	public LoadTimings Timings { get; internal set; }

	// Компиляция вариантов пиксельного шейдера внутри финализации - накапливается там же
	// (см. BuildFromPreparedIncremental.GetPixelShaderVariant).
	internal long _shaderMs;
	internal int _shaderVariants;
	internal long _textureMs;
	internal int _textureCount;
	internal long _meshMs;
	internal int _meshCount;
	internal long _samplerMs;
	internal int _samplerCount;
	internal long _materialMs;
	internal int _materialCount;
	internal long _matCreateMs;
	internal long _matShaderMs;

	/// <summary>Освобождает GPU-ресурсы модели: меши (вершинные/индексные буферы плюс их CPU-копии
	/// в неуправляемой памяти) и материалы (PSO, SRB, кбуферы, шейдеры, текстуры).
	///
	/// Раньше этого не было вовсе - <see cref="ModelLoader"/> не был освобождаемым, и каждая
	/// открытая модель оставляла на GPU весь свой footprint навсегда.
	///
	/// Про шейдеры отдельно, потому что рядом в коде есть предупреждение о двойном освобождении:
	/// один вершинный шейдер и горстка вариантов пиксельного ШАРЯТСЯ между материалами модели, и
	/// <see cref="IMaterialObject.Release"/> освобождает их у каждого. Здесь это безопасно по двум
	/// причинам: DiligentShader.Release нуллит нативный объект и повторный вызов на нём - no-op, а
	/// кэш вариантов локален для ОДНОЙ загрузки (см. BuildFromPreparedIncremental), так что чужой
	/// модели эти шейдеры не принадлежат. Опасен был другой сценарий - освобождение шейдера, пока им
	/// пользуется ЖИВОЙ материал; здесь же умирает весь набор разом.
	///
	/// Вызывающий обязан сперва снять все инстансы со сцены и дождаться GPU: на буферы модели
	/// ссылаются записанные команды рендер-графа (см. ModelPreviewViewport.PopulateFromScene).</summary>
	/// <summary>Шейдеры, созданные загрузкой этой модели: один вершинный, варианты пиксельного и
	/// (при не-треугольных топологиях) точечный вершинный. ШАРЯТСЯ между материалами модели, поэтому
	/// материалы их не освобождают (OwnsShaders=false) - освобождает их отсюда, по одному разу.</summary>
	internal readonly List<IShaderObject> _ownedShaders = new();

	public void Release()
	{
		foreach (var mesh in Meshes)
		{
			mesh?.Release();
		}

		Meshes.Clear();
		MeshHasUv.Clear();

		// По РАЗЛИЧНЫМ объектам, а не по значениям словаря: дефолтный материал раздаётся ВСЕМ
		// null-материалам модели, то есть лежит в materialObjects под несколькими ключами. Простой
		// проход по Values освободил бы его столько же раз, а повторный Dispose нативного SRB/PSO -
		// это обращение к освобождённой памяти, ровно как с шарёными шейдерами.
		var releasedMaterials = new HashSet<IMaterialObject>(ReferenceEqualityComparer.Instance
			as IEqualityComparer<IMaterialObject>);
		foreach (var material in materialObjects.Values)
		{
			if (material != null && releasedMaterials.Add(material))
			{
				material.Release();
			}
		}

		materialObjects.Clear();

		// Текстуры - ПОСЛЕ материалов: их SRB держали вьюхи текстур; сами материалы текстур не
		// освобождают (см. DiligentMaterial.Release), владение здесь.
		foreach (var texture in _ownedTextures)
		{
			texture?.Release();
		}

		_ownedTextures.Clear();

		foreach (var streamed in StreamedTextures)
		{
			streamed.Texture?.Release();
			streamed.Texture = null;
			streamed.EncodedPixels = null;
			streamed.Bindings.Clear();
		}

		StreamedTextures.Clear();

		// ПОСЛЕ материалов: они держат нативные шейдеры, и освобождать шейдер, пока жив
		// использующий его материал, - та же ошибка, только с другой стороны.
		foreach (var shader in _ownedShaders)
		{
			shader?.Release();
		}

		_ownedShaders.Clear();
		instances.Clear();
	}

	public List<IMeshObject> Meshes = new();

	/// <summary>
	/// Parallel to <see cref="Meshes"/>: whether the glTF primitive that became Meshes[i] had a real
	/// TEXCOORD_0 accessor, as opposed to synthesized all-zero UVs (see PrepareModel). Used to gate the
	/// Tangent channel option in <see cref="DecaEngine.Editor.ModelPreviewViewport"/>'s Channel debug
	/// view - a derivative-based tangent computed from degenerate (0,0) UVs is meaningless.
	/// </summary>
	public List<bool> MeshHasUv = new();

	public OrderedDictionary<int, IMaterialObject> materialObjects = new();

	/// <summary>Одна стримимая текстура модели (см. <see cref="ModelLoadOptions.StreamTextures"/>):
	/// текущая GPU-текстура (первая ступень - низкое качество), сжатый исходник для ре-декода и все
	/// слоты материалов, куда она привязана (один image часто шарится каналами/материалами - ORM).
	/// Апгрейды делает DecaEngine.Editor.ECS.ModelStreamer: фоновый декод следующей ступени ->
	/// CreateTexture -> SetTexture по всем привязкам (SRB живого материала обновляется на месте, см.
	/// DiligentMaterial.SetTexture - тот же приём, что у ProbeGiTextures.Bind) -> отложенный Release
	/// старой. По достижении целевого/нативного размера <see cref="EncodedPixels"/> обнуляется -
	/// CPU-данные освобождаются.</summary>
	public sealed class StreamedTexture
	{
		/// <summary>Путь к внешнему файлу картинки (ре-декод читает его с диска в фоне) - null для
		/// встроенных, у них исходник лежит в <see cref="EncodedPixels"/>.</summary>
		public string FilePath;

		/// <summary>Сжатый исходник встроенной картинки (.glb / data-URI).</summary>
		public byte[] EncodedPixels;

		/// <summary>Есть ли ещё откуда декодировать (иначе стриминг этой текстуры окончен).</summary>
		public bool HasSource => !Completed && (FilePath != null || EncodedPixels != null);

		/// <summary>Читает сжатый исходник. Дисковый I/O - звать только из фонового потока.</summary>
		public byte[] ReadEncoded() => EncodedPixels ?? (FilePath != null ? File.ReadAllBytes(FilePath) : null);

		/// <summary>Освобождает CPU-данные исходника (стриминг завершён).</summary>
		public void ReleaseCpuData()
		{
			Completed = true;
			EncodedPixels = null;
			FilePath = null;
		}

		/// <summary>Бо́льшая сторона текущего GPU-декода (0 = ещё 1x1-филлер).</summary>
		public int CurrentSize;

		/// <summary>Целевая сторона (<see cref="ModelLoadOptions.MaxTextureSize"/>; 0 = нативное
		/// разрешение файла).</summary>
		public int TargetSize;

		public bool Completed;

		/// <summary>Авторские настройки сэмплера glTF - апгрейд создаёт текстуру, сэмплер уже
		/// привязан к слоту как immutable и не меняется.</summary>
		public TextureAddress AddressMode;
		public TextureFilter FilterMode;

		/// <summary>Текущая GPU-текстура (шарится всеми привязками); null = слот ещё на 1x1-филлере.
		/// Финальную освобождает <see cref="ModelLoader.Release"/>, промежуточные - стример
		/// отложенной очередью.</summary>
		public IGpuTexture Texture;

		public readonly List<(IMaterialObject Material, string Slot)> Bindings = new();
	}

	/// <summary>Что привязано в слот _MainTex материала - текстура, её (immutable) сэмплер и запись
	/// стриминга, если текстура приезжает ступенями.
	///
	/// Существует ради ТЕНЕВОГО материала с альфа-тестом (см. ShadowRenderer.RegisterAlphaTestedMaterial):
	/// теневой пасс - отдельный PSO со своим SRB, и чтобы вырезать листву по альфе, ему нужна ровно
	/// та же текстура и тот же сэмплер, что и экранному материалу. Пересоздавать их для тени
	/// значило бы удвоить память на каждую крону.</summary>
	public sealed class BaseColorBinding
	{
		public IGpuTexture Texture;
		public ISamplerObject Sampler;

		/// <summary>Запись стриминга (null - текстура загружена целиком): подписавшись на неё,
		/// теневой материал получает те же ступени качества, что и экранный.</summary>
		public StreamedTexture Stream;
	}

	/// <summary>Привязки _MainTex по ключу материала (тому же, что у <see cref="materialObjects"/> и
	/// <see cref="MaterialPbr"/>). Материалы без базовой текстуры сюда не попадают.</summary>
	public readonly Dictionary<int, BaseColorBinding> MaterialBaseColor = new();

	/// <summary>Обобщение <see cref="MaterialBaseColor"/> на ВСЕ слоты (не только _MainTex): по ключу
	/// материала (как у <see cref="materialObjects"/>/<see cref="MaterialPbr"/>) - словарь "имя слота
	/// -&gt; привязка" для каждого слота, куда был привязан РЕАЛЬНЫЙ (не филлер) ресурс. Слоты, ушедшие
	/// на филлер (нет текстуры в glTF), сюда не попадают - их состояние детерминированно выводится из
	/// <see cref="MaterialPbr"/> (Has*Texture/TransmissionFactor).
	///
	/// Существует ради <see cref="BuildAdditionalMaterialSet"/>: второй (третий, ...) набор материалов
	/// для ДРУГОГО окружения строится из уже загруженных GPU-текстур этой модели без повторной
	/// декодировки/заливки - см. класс-комментарий у <see cref="DecaEngine.Editor.ECS.ModelStore"/> о
	/// том, почему материалы (в отличие от текстур/мешей) НЕ шарятся между окружениями напрямую.</summary>
	public readonly Dictionary<int, Dictionary<string, BaseColorBinding>> MaterialTextureBindings = new();

	/// <summary>Общие 1x1-филлеры модели (белый и плоская нормаль) плюс их сэмплер - см. локальные
	/// EnsureFallbackTextures/BindFallbackTexture/BindFlatNormalFallback в
	/// <see cref="BuildFromPreparedIncremental"/>, которые их лениво создают и заполняют эти поля.
	/// Освобождаются как обычные <see cref="_ownedTextures"/> в <see cref="Release"/>; хранятся здесь
	/// отдельно только чтобы <see cref="BuildAdditionalMaterialSet"/> могло переиспользовать те же
	/// объекты вместо создания собственных копий на каждое окружение.</summary>
	internal IGpuTexture FallbackWhiteTexture;
	internal ISamplerObject FallbackSampler;
	internal IGpuTexture FallbackFlatNormalTexture;

	/// <summary>Стримимые текстуры модели; пусто без <see cref="ModelLoadOptions.StreamTextures"/>.</summary>
	public readonly List<StreamedTexture> StreamedTextures = new();

	/// <summary>Не-стримимые GPU-текстуры загрузки (полноразмерные + 1x1-филлеры): материалы текстур
	/// не освобождают (см. DiligentMaterial.Release), владение и Release - здесь. Раньше они не
	/// хранились нигде и утекали навсегда.</summary>
	internal readonly List<IGpuTexture> _ownedTextures = new();

	// Коды топологии меша (MaterialPbrFactors.Topology / PreparedMesh.Topology): точечные и
	// линейные glTF-примитивы рисуются клоном материала с PSO соответствующей топологии.
	public const int MeshTopologyTriangles = 0;
	public const int MeshTopologyLineList = 1;
	public const int MeshTopologyLineStrip = 2;
	public const int MeshTopologyPoints = 3;

	/// <summary>Синтетический ключ материала-клона для не-треугольной топологии - не пересекается с
	/// логическими индексами glTF-материалов (-1..N).</summary>
	public static int MakeTopologyMaterialKey(int topology, int materialIndex) => 10000 * topology + materialIndex + 1;

	/// <summary>
	/// PBR metallic-roughness factors per material, keyed like <see cref="materialObjects"/> (glTF
	/// logical material index, plus -1 for the built-in default material). Consumed by the editor's
	/// Model Preview Lighting mode (see DecaEngine.Editor.ModelPreviewViewport / UnlitInstancedPS.hlsl
	/// PreviewMode == 3) - the engine has no PBR scene shading yet, so nothing else reads these.
	/// </summary>
	public Dictionary<int, MaterialPbrFactors> MaterialPbr = new();

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
		// Строгая валидация SharpGLTF на больших сценах заметно небесплатна; TryFix заодно чинит
		// мелкие огрехи экспортёров вместо жёсткого отказа.
		var swPhase = System.Diagnostics.Stopwatch.StartNew();
		var model = LoadModelRoot(modelPath, options, out var externalImagePaths);
		cancellationToken.ThrowIfCancellationRequested();

		var prepared = new PreparedModel();
		prepared.MsParse = swPhase.ElapsedMilliseconds;
		swPhase.Restart();

		// Картинки, на которые реально ссылаются декодируемые ниже каналы материалов. Декод (PNG/JPG +
		// даунскейл) - самая дорогая CPU-фаза загрузки: параллелится по уникальным image, материалы
		// ниже берут готовые пиксели из кэша. Кэш заодно убирает повторный декод одного image,
		// разделяемого несколькими материалами/каналами (типовая ORM-текстура: у MetallicRoughness и
		// Occlusion один и тот же image - раньше он декодировался дважды).
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

		// Стриминг текстур: в фоновой фазе НЕ ДЕКОДИРУЕТСЯ НИ ОДНА картинка. Декод (PNG/JPG +
		// даунскейл) - самая дорогая CPU-фаза загрузки и главный вкладчик в пиковую память, и именно
		// он раньше держал сцену пустой всё время загрузки. Материалы строятся сразу с 1x1-филлерами
		// (кейворды шейдера при этом ТЕ ЖЕ - они ставятся по наличию текстуры в glTF, а не по
		// наличию пикселей, так что апгрейд не трогает PSO), геометрия появляется почти сразу, а
		// пиксели приезжают ступенями из ModelStreamer.
		//
		// Источник ре-декода - ПУТЬ к файлу картинки, если она внешняя (типовая .gltf-сцена вроде
		// Sponza: папка с PNG рядом), и только для встроенных (.glb / data-URI) копируются байты.
		// Иначе сотни 4K-исходников Sponza жили бы в managed-памяти всё время сессии.
		int decodeMaxSize = options.MaxTextureSize;
		Dictionary<SharpGLTF.Schema2.Image, TextureStreamSource> streamSources = null;
		if (options.StreamTextures)
		{
			decodeMaxSize = 0; // ничего не декодируем в этой фазе
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

			// Параллелизм ОГРАНИЧЕН, и это не про загрузку CPU, а про ПАМЯТЬ. Декод идёт в полном
			// разрешении файла и только потом ужимается до MaxTextureSize (stb иначе не умеет), то
			// есть каждый поток держит в пике полноразмерную RGBA-копию: для 4K это 64 МБ. Без
			// ограничения Parallel.For берёт по потоку на ядро, и на 16-32-поточной машине это
			// 1-2 ГБ ОДНИХ ТОЛЬКО промежуточных буферов - поверх того, что уже накоплено
			// декодированным (см. ниже: decodedResults держит ВСЕ картинки до конца фазы).
			//
			// Четыре потока сохраняют почти всю выгоду распараллеливания (декод упирается в память,
			// а не в ALU) и срезают этот пик до сотен мегабайт.
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

		// Weight the big background phases (texture decode above, then materials and meshes) roughly
		// by count so the progress bar moves at a believable pace instead of jumping straight to 50%.
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

			// PBR metallic-roughness scalars for the editor's Lighting preview (see MaterialPbr).
			// PreparedMaterial's field initializers already hold the glTF spec defaults (white/1/1),
			// so only explicitly-authored channel parameters are read here.
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

				// Game-ready assets typically keep the factors at 1 and put the real per-texel values
				// into the metallic-roughness texture (G = roughness, B = metallic) - without sampling
				// it the preview would treat everything as polished-then-fully-rough metal.
				var mrTexture = channel.Texture;
				if (mrTexture?.PrimaryImage != null)
				{
					preparedMaterial.MetallicRoughnessTexture = DecodeTexture(mrTexture, decodeMaxSize, decodedImages, streamSources, externalImagePaths);
				}
			}

			// KHR_materials_ior / KHR_materials_dispersion - SharpGLTF мапит их прямо в свойства
			// материала (IndexOfRefraction по умолчанию 1.5, Dispersion 0 = выключена).
			preparedMaterial.Ior = logicalMaterial.IndexOfRefraction;
			preparedMaterial.Dispersion = logicalMaterial.Dispersion;

			// KHR_materials_transmission: только скалярный factor - текстуру трансмиссии превью не
			// сэмплирует, а полноценной рефракции у него нет (см. UnlitInstancedPS.hlsl, там
			// аппроксимация "фон сквозь тонированное стекло").
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

			// KHR_materials_sheen: велюровый "световой ворс" (Charlie-лоб в шейдере). Цвет и своя
			// шероховатость - двумя каналами SharpGLTF. Параметры матчатся по ТИПУ значения (в каждом
			// канале ровно один нетекстурный параметр) - имена ключей у SharpGLTF внутренние.
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

			// KHR_materials_specular: перекраска/ослабление диэлектрического F0 (сатин и прочие ткани
			// с цветным бликом). specularColorFactor может быть >1 (ChairDamaskPurplegold: [1,0.25,2]) -
			// кламп произойдёт в шейдере ПОСЛЕ умножения на F0 от IOR, как велит спека.
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

			// KHR_materials_volume: Beer-Lambert затухание сквозь толщу стекла. Толщину берём только
			// фактором (thicknessTexture не сэмплируется), показатель степени thickness/attenuationDistance
			// предвычисляем здесь - шейдеру нужен один float4 (rgb цвет, w показатель).
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

				// Толщина в текстуре (G-канал по спеке) - множитель поверх factor-а; без неё
				// плотное стекло глушит просвет равномерно, и тонкие детали (гребни, шипы)
				// теряют характерную "светящуюся" прозрачность.
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

			// Запечённый ambient occlusion (R-канал по спеке, часто общая ORM-текстура с MR) +
			// occlusionStrength. Глушит ambient/env-термы в порах и складках - без него фигуры
			// выглядят "пластиково чистыми". Прямой свет по спеке AO не трогает.
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

				// AO часто запечён под уникальную развёртку ВТОРОГО UV-канала (texCoord 1, см.
				// ChairDamaskPurplegold) - сэмпл по UV0 кладёт затемнения в случайные места.
				// Каналы выше 1 в вершине не хранятся - клампятся в TEXCOORD_1.
				preparedMaterial.OcclusionUvSet = Math.Clamp(occlusionChannel.Value.TextureCoordinate, 0, 1);

				var occlusionTexture = occlusionChannel.Value.Texture;
				if (occlusionTexture?.PrimaryImage != null)
				{
					preparedMaterial.OcclusionTexture = DecodeTexture(occlusionTexture, decodeMaxSize, decodedImages, streamSources, externalImagePaths);
				}
			}

			// Нормал-мапа (tangent-space, линейная - без sRGB-декода) + normalScale. Без неё весь
			// авторский микрорельеф (кладка, резьба, прожилки) теряется - поверхность шейдится
			// только геометрической нормалью.
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

			// KHR_texture_transform: смещение/масштаб/поворот UV, заданные материалом (Khronos-семпл
			// ChairDamaskPurplegold: scale 3x3 + rotation 0.1 на дереве/ткани - без учёта текстуры
			// тайлятся втрое крупнее и без поворота волокон). Одна трансформация на материал: с
			// baseColor-канала, фоллбек normal/MR. Предвычисляется в 2x2-матрицу + offset по формуле
			// спеки M = Translation * Rotation * Scale.
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

			// Preview-friendly fallback: a material with neither a metallic-roughness texture nor
			// authored factors lands on the glTF spec defaults (metallic 1, roughness 1), i.e. a metal
			// with no diffuse and a lobe-less specular - it renders as if unlit (ambient only). A
			// neutral dielectric reads far closer to what the author meant.
			//
			// ВАЖНО: только когда НЕ авторский НИ ОДИН фактор. IsDefault у SharpGLTF означает
			// "значение равно дефолту", а не "не записан в JSON" - материал с явным metallic=1 +
			// roughness=0 (зеркало, см. PrimitiveModeNormalsTest) выглядит как "metallic не авторский",
			// но авторский roughness выдаёт осознанный metal-workflow, и глушить его в диэлектрик нельзя.
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

				// Топология примитива (см. MeshTopology*-константы): точки/линии рисуются клонами
				// материала с PSO соответствующей топологии (см. BuildFromPrepared /
				// ModelViewportGeometry.RegisterModelResources) - батч-рендерер группирует дроу по
				// материалу, так что отдельный материал на топологию не требует его переделки.
				int topology = primitive.DrawPrimitiveType switch
				{
					PrimitiveType.TRIANGLES => MeshTopologyTriangles,
					PrimitiveType.LINES => MeshTopologyLineList,
					PrimitiveType.LINE_STRIP => MeshTopologyLineStrip,
					PrimitiveType.LINE_LOOP => MeshTopologyLineStrip,
					PrimitiveType.POINTS => MeshTopologyPoints,
					_ => -1,
				};
				if (topology < 0)
				{
					// TRIANGLE_STRIP/FAN не поддержаны - раньше такие примитивы рисовались как
					// triangle list (мусор), теперь честно пропускаются.
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

				// glTF - правосторонняя система (+Z на зрителя), движок - левосторонняя: без
				// зеркалирования Z вся геометрия рендерится отражённой (текст задом наперёд, см.
				// PrimitiveModeNormalsTest). Вместе с инверсией Z у треугольников меняется winding -
				// он разворачивается ниже, чтобы фронт-фейсы остались фронт-фейсами.
				var sourceVertices = new Vertex[positions.Count];
				for (int i = 0; i < positions.Count; i++)
				{
					var uv = uvs != null && i < uvs.Count ? uvs[i] : Vector2.Zero;
					var uv1 = uvs1 != null && i < uvs1.Count ? uvs1[i] : Vector2.Zero;
					var normal = normals != null && i < normals.Count ? normals[i] : Vector3.UnitY;
					var color = colors != null && i < colors.Count ? colors[i] : Vector4.One;

					// Авторский glTF TANGENT (vec4, w = знак битангента). Направление зеркалируется
					// по Z вместе с позициями/нормалями, а w ИНВЕРТИРУЕТСЯ: зеркало меняет
					// ориентацию базиса (det = -1), и cross(N, T) в пространстве движка смотрит
					// против зеркалированного битангента. Без авторских тангентов w временно 1 -
					// GenerateTangents ниже перезапишет и направление, и знак.
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

				// Точки/линии в glTF почти всегда неиндексированные (см. PrimitiveModeNormalsTest) -
				// батч-рендерер рисует только DrawIndexedIndirect, поэтому синтезируем 0..N-1.
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

				// A glTF logical mesh with multiple primitives (e.g. one node using several materials)
				// becomes multiple sub-meshes here, one per primitive - without a per-primitive suffix
				// they'd all inherit the same logicalMesh.Name and be indistinguishable in the sub-mesh
				// list (same label for every entry, even though each is a distinct piece of geometry).
				var meshName = logicalMesh.Primitives.Count > 1 ? $"{baseMeshName}.{primitiveIndex}" : baseMeshName;

				// Тяжёлая чистая CPU-обработка (winding/нормали/тангенты/meshopt/LOD) вынесена в
				// параллельную фазу ниже - здесь только чтение SharpGLTF (не потокобезопасно) и
				// сбор сырья по примитивам. meshId примитива = индекс work-item-а.
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
				});
			}
		}

		// Обработка примитивов независима и не трогает SharpGLTF - параллелится целиком.
		var preparedMeshes = new PreparedMesh[meshWork.Count];
		int primitivesDone = 0;
		Parallel.For(0, meshWork.Count, new ParallelOptions { CancellationToken = cancellationToken }, workIndex =>
		{
			var work = meshWork[workIndex];
			var sourceVertices = work.SourceVertices;
			var sourceIndices = work.SourceIndices;

			if (work.Topology == MeshTopologyTriangles)
			{
				for (int t = 0; t + 2 < sourceIndices.Length; t += 3)
				{
					(sourceIndices[t + 1], sourceIndices[t + 2]) = (sourceIndices[t + 2], sourceIndices[t + 1]);
				}

				// Примитив без NORMAL-аксессора: по спеке glTF шейдится FLAT (per-face). Вершины
				// развариваются по треугольникам, каждая получает нормаль своей грани - ровно
				// гранёный "диско-шар" эталонного вьювера. Усреднение по вершинам (прошлая
				// версия) давало гладкую сферу, но швы дублированных вершин расходились
				// полосами в отражениях.
				if (!work.HasNormals)
				{
					var flatVertices = new Vertex[sourceIndices.Length];
					for (int t = 0; t + 2 < sourceIndices.Length; t += 3)
					{
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
				}
			}
			var (boundsCenter, boundsRadius) = MeshUtility.ComputeBoundsData(sourceVertices);

			var finalVertices = sourceVertices;
			var finalIndices = sourceIndices;
			LodLevel[] lodLevels = null;

			if (work.Topology == MeshTopologyTriangles)
			{
				// Must run before Optimize/GenerateLods reorder/remap vertices - it needs the
				// pristine per-triangle winding to compute per-triangle tangents, but the resulting
				// per-vertex Tangent then rides along automatically through any later remap (it's
				// just another Vertex field, opaque to Meshopt's vertex-remap/simplify passes).
				// Только фоллбек: авторский glTF TANGENT (уже в вершинах, со знаком w) точнее
				// генерации - он согласован с запечкой нормал-мапы (MikkTSpace и пр.).
				if (!work.HasTangents)
				{
					MeshUtility.GenerateTangents(sourceVertices, sourceIndices);
				}

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

			preparedMeshes[workIndex] = new PreparedMesh
			{
				Name = work.Name,
				Vertices = finalVertices,
				Indices = finalIndices,
				LodLevels = lodLevels,
				BoundsCenter = boundsCenter,
				BoundsRadius = boundsRadius,
				HasUv = work.HasUv,
				Topology = work.Topology,
			};

			progress?.Report(0.4f + 0.55f * (Interlocked.Increment(ref primitivesDone) / (float)meshWork.Count));
		});

		prepared.Meshes.AddRange(preparedMeshes);

		// Кэш запечённых мешей для нераскладываемых матриц (см. ниже): один меш под несколькими
		// узлами с ОДИНАКОВОЙ мировой матрицей пекётся однажды.
		var bakedMeshCache = new Dictionary<(int MeshId, Matrix4x4 World), int>();

		foreach (var node in model.LogicalNodes)
		{
			if (node.Mesh == null)
			{
				continue;
			}

			// Decompose ПРОВЕРЯЕТСЯ: мировая матрица глубокой иерархии (родительский поворот поверх
			// неравномерного масштаба - Intel Sponza) содержит shear, в TRS не представимый.
			// Decompose тогда возвращает false и МУСОР в out-параметрах - геометрия таких узлов
			// съезжала и перекашивалась. Фоллбек - запечь матрицу прямо в вершины (см. BakeMeshWithMatrix).
			bool trsValid = Matrix4x4.Decompose(node.WorldMatrix, out var scale, out var rotation, out var translation);

			// Та же RH->LH конвертация, что и для вершин выше: зеркалим Z трансляции, а поворот
			// сопрягаем отражением M*R*M (M = diag(1,1,-1)), что для кватерниона даёт (-x,-y,z,w).
			translation.Z = -translation.Z;
			rotation = new Quaternion(-rotation.X, -rotation.Y, rotation.Z, rotation.W);

			foreach (var primitive in node.Mesh.Primitives)
			{
				if (primitiveToMeshIdMap.TryGetValue(primitive, out int meshId))
				{
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

					// Не-треугольная топология: инстанс ссылается на материал-клон с подходящим PSO
					// (создаётся в BuildFromPrepared по этому реестру).
					int topology = prepared.Meshes[meshId].Topology;
					if (topology != MeshTopologyTriangles)
					{
						int synthKey = MakeTopologyMaterialKey(topology, materialId);
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

	/// <summary>Пошаговое создание GPU-ресурсов готовой <see cref="PreparedModel"/>: итератор
	/// возвращает ОЦЕНКУ байт, залитых в GPU на очередном шаге (текстуры материала / вершины+индексы
	/// меша). Diligent освобождает страницы upload-хипа только на FinishFrame (Present), поэтому
	/// финализация всей модели одним кадром раздувала host-visible память до гигабайт («Space in
	/// dynamic heap is almost exhausted», peak 2.5+ GB). Вызывающий (<see
	/// cref="ModelLoadRequest.FinalizeChunk"/>) двигает итератор, пока не выберет байтовый бюджет
	/// кадра, и продолжает на следующем кадре - <paramref name="result"/> наполняется по мере
	/// движения и валиден только после того, как MoveNext вернул false.</summary>
	private static IEnumerator<long> BuildFromPreparedIncremental(IGraphicsApi graphicsApi, ModelLoadOptions options,
		PreparedModel prepared, ModelLoader result)
	{
		var (vsFactoryPath, vsFileName) = options.VertexShader.ToShaderFactoryParts();
		var (psFactoryPath, psFileName) = options.PixelShader.ToShaderFactoryParts();
		// Шейдеры модели берутся из ОБЩЕГО кэша бэкенда: варианты у разных моделей практически
		// всегда одни и те же, а компиляция идёт синхронно на потоке рендера (см. CreateSharedShader).
		// Материалы модели помечены OwnsShaders=false, так что шарёный экземпляр никто не убьёт.
		var modelShaderVs = graphicsApi.CreateSharedShader("Model Vertex Shader", vsFactoryPath, vsFileName,
			ShaderObjectType.Vertex);
		result._ownedShaders.Add(modelShaderVs);

		// Пиксельные ВАРИАНТЫ по shader keywords (см. шапку UnlitInstancedPS.hlsl): эффекты,
		// статически известные по материалу (текстуры, transmission, dispersion, alpha clip),
		// вырезаются из кода компиляцией вместо рантайм-веток по cbuffer-флагам. Кэш - материалы
		// с одинаковым набором ключей делят один скомпилированный шейдер.
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

		// pm == null - встроенный дефолтный материал (без текстур/расширений).
		List<string> BuildMaterialKeywords(PreparedMaterial pm)
		{
			var keywords = new List<string>();

			if (options.PreviewLightingFeatures)
			{
				keywords.Add("FEATURE_NORMAL_MAPS");
				keywords.Add("FEATURE_OCCLUSION");
				keywords.Add("FEATURE_SHADOWS");
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

		var defaultMaterial = graphicsApi.CreateMaterial("Default Material");

		// Шейдеры шареные - см. IMaterialObject.OwnsShaders. Этот материал вдобавок раздаётся
		// НЕСКОЛЬКИМ логическим индексам (все null-материалы модели ссылаются на один объект),
		// так что его Release зовётся из ModelLoader.Release столько же раз - ещё одна причина не
		// давать ему трогать шейдеры.
		defaultMaterial.OwnsShaders = false;
		defaultMaterial.SetShader(GetPixelShaderVariant(BuildMaterialKeywords(null)), modelShaderVs);

		// Белый 1x1-филлер для _MainTex/_MetallicRoughnessTex у материалов без соответствующей
		// текстуры: пиксельный шейдер статически ссылается на оба слота (ветвление по
		// PbrHas*Texture - динамическое), поэтому непривязанный дескриптор - это undefined
		// behavior на Vulkan (validation VUID-vkCmdDrawIndexedIndirect-None-08114), а не
		// безобидный «нулевой» сэмпл. Один общий на модель, создаётся лениво.
		Core.Texture fallbackTexture = null;
		ISamplerObject fallbackSampler = null;

		// Отдельный филлер для _NormalTex: белый пиксель распаковался бы в наклонённую нормаль
		// (1,1,1)->(1,1,1), а "плоский" (128,128,255) -> (0,0,1) оставляет геометрическую.
		Core.Texture flatNormalTexture = null;

		// Создаёт (лениво) оба 1x1-филлера, не привязывая их ни к какому слоту: стриминг ставит их
		// сам, со СВОИМ (авторским) сэмплером - см. BindPreparedTexture.
		void EnsureFallbackTextures()
		{
			if (fallbackTexture == null)
			{
				fallbackTexture = new Core.Texture("Model Fallback White", new CpuTextureData
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
				flatNormalTexture = new Core.Texture("Model Fallback Flat Normal", new CpuTextureData
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
				fallbackTexture = new Core.Texture("Model Fallback White", new CpuTextureData
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
			// Белый филлер создаёт общий сэмплер - гарантируем его наличие.
			if (fallbackSampler == null)
			{
				BindFallbackTexture(material, "_NormalTex");
			}

			if (flatNormalTexture == null)
			{
				flatNormalTexture = new Core.Texture("Model Fallback Flat Normal", new CpuTextureData
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

		// Все три филлера гарантированно созданы к этой точке (вызовы выше) - публикуем их на модели
		// для BuildAdditionalMaterialSet (см. поле-комментарии у FallbackWhiteTexture/FallbackSampler/
		// FallbackFlatNormalTexture).
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

		// Шейдеры + дефолтный материал с 1x1-филлерами - копейки, но это удобная точка отсечки
		// перед первым «тяжёлым» материалом.
		yield return 4096;

		// Оценка залитых в GPU байт при материализации материала: сумма несжатых RGBA-пикселей
		// его текстур (каждый Bind* делает отдельный Upload, так что считаем по слотам).
		static long EstimateMaterialBytes(PreparedMaterial pm)
		{
			if (pm == null)
			{
				return 4096;
			}

			// В режиме стриминга Pixels у всех каналов null (заливки на этой фазе нет вовсе) - оценка
			// честно выходит в «почти ноль», и финализация материалов не тратит кадровый бюджет.
			long bytes = 4096;
			bytes += pm.BaseColorTexture?.Pixels?.Length ?? 0;
			bytes += pm.MetallicRoughnessTexture?.Pixels?.Length ?? 0;
			bytes += pm.NormalTexture?.Pixels?.Length ?? 0;
			bytes += pm.OcclusionTexture?.Pixels?.Length ?? 0;
			bytes += pm.TransmissionFactor > 0f ? pm.ThicknessTexture?.Pixels?.Length ?? 0 : 0;
			return bytes;
		}

		// KHR_materials_volume: толщина задана в ЛОКАЛЬНЫХ координатах меша и по спеке умножается
		// на масштаб узла (у Khronos-семплов DragonAttenuation/DragonDispersion узел дракона имеет
		// scale 0.25 - без учёта масштаба экспонента Beer-Lambert завышается в 4 раза, и янтарное
		// стекло глушится в тёмно-красное, а слегка голубоватое - в тёмно-синее). Толщина -
		// per-material, масштаб - per-instance; для превью берём масштаб первого инстанса,
		// использующего материал (модели с volume-стеклом практически всегда один узел на меш).
		var materialScales = new Dictionary<int, float>();
		foreach (var instance in prepared.Instances)
		{
			var s = instance.transform.scale;
			materialScales.TryAdd(instance.materialId, (s.X + s.Y + s.Z) / 3f);
		}

		// Реестр стрим-текстур по исходнику: один image шарится несколькими слотами/материалами
		// (типовая ORM-текстура), апгрейд декодируется один раз и раскладывается по всем привязкам.
		var streamEntries = new Dictionary<TextureStreamSource, StreamedTexture>();

		// Возвращает привязку (текстура + сэмплер + запись стриминга) - её переиспользует теневой
		// материал с альфа-тестом (см. ModelLoader.MaterialBaseColor). null - слот получил филлер.
		BaseColorBinding BindPreparedTexture(IMaterialObject materialObj, string slot, PreparedTexture preparedTexture)
		{
			if (preparedTexture == null)
			{
				// Белый филлер (для _ThicknessTex G=1 -> толщина остаётся чистым factor-ом).
				BindFallbackTexture(materialObj, slot);
				return null;
			}

			// Режим стриминга: пикселей ещё нет вовсе - слот получает общий 1x1-филлер (белый, для
			// _NormalTex - плоская нормаль), а первая ступень приедет из ModelStreamer. Заливать
			// здесь нечего, поэтому финализация материалов стоит копейки и геометрия появляется
			// почти сразу. Кейворды шейдера при этом ТЕ ЖЕ (ставятся по наличию текстуры в glTF),
			// так что апгрейд не трогает PSO.
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

				// Текстура-филлер - общая 1x1 (белая; для нормалей плоская), а вот СЭМПЛЕР ставится
				// сразу авторский: он immutable и печётся в layout PSO, то есть подменить его при
				// апгрейде уже нельзя - фоллбечный Point/Wrap остался бы с текстурой навсегда.
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

				// Динамический сэмплер (на texture view), а не immutable, - как в прямом пути ниже:
				// immutable для батч-материалов был мёртв из-за PSO-кэша (см. там же), а стримингу
				// динамический ещё и роднее - при горячей замене текстуры SetTexture сам перевесит
				// его на новый view (см. DiligentMaterial.SetTexture).
				materialObj.SetSampler(slot + "_sampler", streamSampler);
				result._samplerCount++;

				streamEntry.Bindings.Add((materialObj, slot));

				// Текстура здесь - общий 1x1-филлер; теневому материалу важна не она, а ЗАПИСЬ
				// стриминга: он подпишется на неё и получит те же ступени качества.
				return new BaseColorBinding
				{
					Texture = slot == "_NormalTex" ? flatNormalTexture.GpuHandle : fallbackTexture.GpuHandle,
					Sampler = streamSampler,
					Stream = streamEntry,
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

				var texture = new Core.Texture(cpuData.Name, cpuData);

				// Замер отдельно от остальной финализации: она оказалась 80% времени загрузки и при этом
				// почти не зависит от ОБЪЁМА текстур - значит цена не в байтах, а в вызовах, и надо
				// знать, в каких именно.
				var swUpload = System.Diagnostics.Stopwatch.StartNew();
				texture.Upload(graphicsApi, true);
				result._textureMs += swUpload.ElapsedMilliseconds;
				result._textureCount++;

				gpuTexture = texture.GpuHandle;
				result._ownedTextures.Add(gpuTexture);
			}

			// Линейные текстуры апгрейдятся до анизотропных (тумблер в ModelLoadOptions) - без
			// этого доска/пол мылятся под острым углом; авторский point-фильтр сохраняется.
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

			// ДИНАМИЧЕСКАЯ привязка (сэмплер вешается на texture view), а не SetImmutableSampler:
			// immutable-путь для батч-материалов молча не срабатывает - Diligent подставляет дефолтный
			// сэмплер (linear wrap), и все ручки (анизотропия, mip bias) оказываются мёртвыми.
			// Замерено пробником: кадры с ANISO=0/1 и MIPBIAS=+4 были БИТ-В-БИТ одинаковыми.
			materialObj.SetSampler(slot + "_sampler", samplerObject);

			return new BaseColorBinding { Texture = gpuTexture, Sampler = samplerObject, Stream = null };
		}

		// Записывает РЕАЛЬНУЮ (не филлер) привязку слота в result.MaterialTextureBindings под ключом
		// материала - см. поле-комментарий. Единственный писатель этого словаря.
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

		// vs передаётся параметром (а не правится повторным SetShader): DiligentMaterial.SetShader
		// release-ит ранее установленные шейдеры, а они шарятся между материалами - повторный вызов
		// на живом наборе роняет процесс двойным освобождением.
		//
		// materialKey - ключ, под которым будет зарегистрирован ИТОГОВЫЙ материал в
		// result.materialObjects (обычный логический индекс или синтетический ключ клона топологии,
		// см. MakeTopologyMaterialKey) - нужен только чтобы разложить реальные привязки текстур в
		// result.MaterialTextureBindings (см. TrackBinding) для BuildAdditionalMaterialSet.
		IMaterialObject BuildMaterialObject(PreparedMaterial pm, string name, IShaderObject vs, int materialKey,
			out BaseColorBinding baseColor)
		{
			var swCreate = System.Diagnostics.Stopwatch.StartNew();
			var materialObj = graphicsApi.CreateMaterial(name);

			// Шейдеры ШАРЕНЫЕ между материалами модели (вариантный кэш + один VS): освобождать их
			// материалу нельзя - это декремент чужого счётчика ссылок и падение на следующем
			// материале. См. IMaterialObject.OwnsShaders и ModelLoader.Release.
			materialObj.OwnsShaders = false;
			result._matCreateMs += swCreate.ElapsedMilliseconds;

			var swSetShader = System.Diagnostics.Stopwatch.StartNew();
			materialObj.SetShader(GetPixelShaderVariant(BuildMaterialKeywords(pm)), vs);
			result._matShaderMs += swSetShader.ElapsedMilliseconds;

			baseColor = BindPreparedTexture(materialObj, "_MainTex", pm.BaseColorTexture);
			TrackBinding(materialKey, "_MainTex", baseColor);

			// Слот объявлен в шейдере только под HAS_MR_TEXTURE (см. UnlitInstancedPS.hlsl) - этот
			// кейворд ставится только когда у материала реально есть MR-текстура, так что фоллбек
			// тут не нужен и не должен биндиться (иначе immutable sampler без ресурса в шейдере).
			if (pm.MetallicRoughnessTexture != null)
			{
				TrackBinding(materialKey, "_MetallicRoughnessTex",
					BindPreparedTexture(materialObj, "_MetallicRoughnessTex", pm.MetallicRoughnessTexture));
			}

			// Слот объявлен в шейдере только под MATERIAL_TRANSMISSION (см. UnlitInstancedPS.hlsl) -
			// у остальных материалов кейворд выключен, и биндить нечего.
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

			// Белый филлер (R=1) = "ничего не заслонено" - has-флаг не нужен.
			TrackBinding(materialKey, "_OcclusionTex",
				BindPreparedTexture(materialObj, "_OcclusionTex", pm.OcclusionTexture));

			return materialObj;
		}

		// scaleKey - ключ, под которым ИНСТАНСЫ ссылаются на материал (для клонов топологий это
		// синтетический ключ, см. MakeTopologyMaterialKey), т.к. materialScales собран по инстансам.
		MaterialPbrFactors BuildFactors(PreparedMaterial pm, int scaleKey)
		{
			var averageBaseColor = ComputeAverageBaseColor(pm);
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
			TransmissionFactor = pm.TransmissionFactor,
			Ior = pm.Ior,
			Dispersion = pm.Dispersion,
			SheenColorRoughness = new Vector4(pm.SheenColorFactor, pm.SheenRoughnessFactor),
			SpecularColorFactor = new Vector4(pm.SpecularColorFactor, pm.SpecularFactor),
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

		// Материалы-клоны под не-треугольные топологии (см. PrepareModel): тот же шейдинг и
		// текстуры, но отдельный объект материала - RegisterModelResources назначит ему PSO с
		// нужной PrimitiveTopology, а батч-рендерер и так группирует индирект-дроу по материалу,
		// так что смешение топологий в одной модели больше ничего не требует.
		IShaderObject pointShaderVs = null;

		foreach (var (synthKey, clone) in prepared.TopologyMaterialClones)
		{
			PreparedMaterial source = null;
			if (clone.SourceMaterial >= 0)
			{
				source = prepared.Materials.Find(m => m.LogicalIndex == clone.SourceMaterial && !m.IsNull);
			}

			// PSO с POINT_LIST обязан писать builtin PointSize из VS (Vulkan
			// VUID-VkGraphicsPipelineCreateInfo-topology-08773) - точечным клонам достаётся
			// *PointVS-вариант, лежащий рядом со штатным (конвенция имени; для нестандартного VS
			// из опций остаётся обычный - валидация ругнётся, но на большинстве драйверов рендер
			// работает).
			var cloneVs = modelShaderVs;
			if (clone.Topology == MeshTopologyPoints)
			{
				if (pointShaderVs == null && vsFileName == "UnlitInstancedVS.hlsl")
				{
					// Добавляется в _ownedShaders сразу после создания - см. ниже.
					pointShaderVs = graphicsApi.CreateSharedShader("Model Point Vertex Shader", vsFactoryPath,
						"UnlitInstancedPointVS.hlsl", ShaderObjectType.Vertex);
					result._ownedShaders.Add(pointShaderVs);
				}

				cloneVs = pointShaderVs ?? modelShaderVs;
			}

			IMaterialObject materialObj;
			MaterialPbrFactors factors;
			if (source == null)
			{
				materialObj = graphicsApi.CreateMaterial($"Default Material (topology {clone.Topology})");

				// Шейдеры здесь ШАРЕНЫЕ (вариантный кэш + один VS на модель) - освобождает их
				// ModelLoader.Release, по разу на каждый. См. IMaterialObject.OwnsShaders.
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

			yield return (long)preparedMesh.Vertices.Length * VertexSizeBytes + (long)preparedMesh.Indices.Length * sizeof(uint);
		}

		result.instances.AddRange(prepared.Instances);
	}

	/// <summary>Те же shader-кейворды, что строит локальный BuildMaterialKeywords внутри
	/// <see cref="BuildFromPreparedIncremental"/>, но выведенные из уже посчитанных
	/// <see cref="MaterialPbrFactors"/> вместо сырого <see cref="PreparedMaterial"/> (которого больше
	/// нет - PrepareModel-данные живут только до конца ПЕРВОЙ финализации, см. ModelLoadRequest.FinalizeChunk).
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

		var modelShaderVs = graphicsApi.CreateSharedShader("Model Vertex Shader", vsFactoryPath, vsFileName,
			ShaderObjectType.Vertex);

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

	/// <summary>Фоллбек для узлов, чья мировая матрица не раскладывается в TRS (shear от родительского
	/// поворота поверх неравномерного масштаба - Matrix4x4.Decompose возвращает false): матрица
	/// запекается прямо в копию вершин, инстанс получает identity-трансформ. Матрица приходит в
	/// RH-конвенции glTF и переводится в LH движка сопряжением M*W*M (M = diag(1,1,-1)) - вершины
	/// исходного меша уже отзеркалены по Z при чтении атрибутов.</summary>
	private static int BakeMeshWithMatrix(PreparedModel prepared, int meshId, Matrix4x4 worldRh)
	{
		var source = prepared.Meshes[meshId];
		var mirrorZ = Matrix4x4.CreateScale(1f, 1f, -1f);
		var world = mirrorZ * worldRh * mirrorZ;

		// Нормали - через inverse-transpose: под неравномерным масштабом/сдвигом прямое умножение
		// уводит их с перпендикуляра к поверхности.
		Matrix4x4.Invert(world, out var inverse);
		var normalMatrix = Matrix4x4.Transpose(inverse);

		var vertices = new Vertex[source.Vertices.Length];
		var min = new Vector3(float.MaxValue);
		var max = new Vector3(float.MinValue);
		for (int i = 0; i < vertices.Length; i++)
		{
			var vertex = source.Vertices[i];
			vertex.Position = Vector3.Transform(vertex.Position, world);
			vertex.Normal = SafeNormalize(Vector3.TransformNormal(vertex.Normal, normalMatrix));
			var tangent = SafeNormalize(Vector3.TransformNormal(
				new Vector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z), world));
			vertex.Tangent = new Vector4(tangent, vertex.Tangent.W);
			vertices[i] = vertex;
			min = Vector3.Min(min, vertex.Position);
			max = Vector3.Max(max, vertex.Position);
		}

		// Зеркалящая матрица (отрицательный детерминант) обращает обход треугольников - без
		// инверсии индексов culling выворачивает геометрию наизнанку. Свап покрывает и LOD-ы:
		// их LodLevel-ы - диапазоны в этом же индекс-буфере. Знак битангента флипается по той же
		// причине, что при базовом Z-зеркалировании (см. Vertex.Tangent).
		var indices = source.Indices;
		if (world.GetDeterminant() < 0f && source.Topology == MeshTopologyTriangles)
		{
			indices = (uint[])indices.Clone();
			for (int i = 0; i + 2 < indices.Length; i += 3)
			{
				(indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
			}
			for (int i = 0; i < vertices.Length; i++)
			{
				vertices[i].Tangent.W = -vertices[i].Tangent.W;
			}
		}

		prepared.Meshes.Add(new PreparedMesh
		{
			Name = source.Name + " (baked transform)",
			Vertices = vertices,
			Indices = indices,
			LodLevels = source.LodLevels,
			BoundsCenter = (min + max) * 0.5f,
			BoundsRadius = MathF.Max(0.0001f, (max - min).Length() * 0.5f),
			HasUv = source.HasUv,
			Topology = source.Topology,
		});
		return prepared.Meshes.Count - 1;
	}

	private static Vector3 SafeNormalize(Vector3 v)
	{
		return v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : v;
	}

	/// <summary>Собирает PreparedTexture из заранее декодированных пикселей image (параллельный декод
	/// в начале PrepareModel; кэш заодно дедуплицирует image, разделяемый несколькими
	/// материалами/каналами - пиксельный массив шарится, дальше он только читается) + настроек
	/// сэмплера. Сэмплер в glTF опционален (нет - значит wrap + linear по спеке): WaterBottle и
	/// другие Khronos-семплы без явных сэмплеров роняли загрузку NRE.</summary>
	private static PreparedTexture DecodeTexture(SharpGLTF.Schema2.Texture texture, int maxSize,
		Dictionary<SharpGLTF.Schema2.Image, (byte[] Pixels, int Width, int Height)> decodedImages,
		Dictionary<SharpGLTF.Schema2.Image, TextureStreamSource> streamSources,
		Dictionary<int, string> externalImagePaths = null)
	{
		var sampler = texture.Sampler;
		var prepared = new PreparedTexture
		{
			AddressMode = sampler != null ? ToAddressMode(sampler.WrapS) : TextureAddress.Wrap,
			FilterMode = sampler != null ? ToFilter(sampler.MinFilter, sampler.MagFilter) : TextureFilter.Linear,
		};

		if (streamSources != null)
		{
			// Стриминг: пикселей на этой фазе нет вовсе - слот получит 1x1-филлер, а первая ступень
			// приедет из ModelStreamer. Страховка на канал, не учтённый пре-сбором usedImages.
			if (!streamSources.TryGetValue(texture.PrimaryImage, out var streamSource))
			{
				streamSource = CreateStreamSource(texture.PrimaryImage, externalImagePaths);
				streamSources[texture.PrimaryImage] = streamSource;
			}

			prepared.StreamSource = streamSource;
			return prepared;
		}

		if (!decodedImages.TryGetValue(texture.PrimaryImage, out var decoded))
		{
			// Страховка: канал, не учтённый пре-сбором usedImages, декодируется на месте.
			decoded = DecodeImagePixels(texture.PrimaryImage, maxSize);
			decodedImages[texture.PrimaryImage] = decoded;
		}

		prepared.Pixels = decoded.Pixels;
		prepared.Width = decoded.Width;
		prepared.Height = decoded.Height;
		return prepared;
	}

	/// <summary>Источник ре-декодов для стриминга: путь к ВНЕШНЕМУ файлу картинки, если он известен
	/// (типовая .gltf-сцена - папка с PNG рядом), иначе копия встроенных байт (.glb / data-URI).
	/// Путь предпочтительнее ровно по памяти: у Sponza сотни 4K-исходников, и держать их все в
	/// managed-куче всю сессию - гигабайты на ровном месте.</summary>
	private static TextureStreamSource CreateStreamSource(SharpGLTF.Schema2.Image image,
		Dictionary<int, string> externalImagePaths)
	{
		// Внешний файл, чьё чтение мы подменили заглушкой при парсинге (см. LoadModelRoot): в
		// памяти его нет вовсе, читаем с диска в момент апгрейда.
		if (externalImagePaths != null && externalImagePaths.TryGetValue(image.LogicalIndex, out var path))
		{
			return new TextureStreamSource { FilePath = path };
		}

		var sourcePath = image.Content.SourcePath;
		if (!string.IsNullOrEmpty(sourcePath) && File.Exists(sourcePath))
		{
			return new TextureStreamSource { FilePath = sourcePath };
		}

		// Встроенная картинка (.glb / data-URI / bufferView) - её байты и так уже в памяти модели.
		return new TextureStreamSource { EncodedBytes = image.Content.Content.ToArray() };
	}

	/// <summary>Минимальный валидный PNG 1x1 - заглушка вместо реального содержимого внешних
	/// картинок при стриминге (см. <see cref="LoadModelRoot"/>).</summary>
	private static readonly byte[] StubPng = Convert.FromBase64String(
		"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

	/// <summary>
	/// Парсит glTF. В обычном режиме - как раньше. В режиме стриминга внешние файлы картинок НЕ
	/// ЧИТАЮТСЯ ВОВСЕ: их содержимое подменяется 1x1-заглушкой, а на выход отдаётся карта
	/// «логический индекс image -> путь к файлу», по которой стример читает нужную картинку с диска
	/// в момент, когда она реально понадобилась материалу.
	///
	/// Это и была главная причина «сцена пустая, редактор висит две минуты»: SharpGLTF грузит
	/// содержимое КАЖДОГО image при разборе документа, то есть Sponza затягивала в managed-кучу все
	/// свои сотни мегабайт (а с Intel-версией - гигабайты) PNG ещё до того, как появлялась хоть
	/// одна вершина, - и всё это до единого байта тут же становилось мусором, потому что декод
	/// текстур в этой фазе уже не делается.
	/// </summary>
	private static ModelRoot LoadModelRoot(string modelPath, ModelLoadOptions options,
		out Dictionary<int, string> externalImagePaths)
	{
		externalImagePaths = null;

		var settings = new ReadSettings { Validation = SharpGLTF.Validation.ValidationMode.TryFix };

		// Только для текстового .gltf: у .glb картинки лежат внутри самого файла, подменять нечего.
		if (!options.StreamTextures ||
			!string.Equals(Path.GetExtension(modelPath), ".gltf", StringComparison.OrdinalIgnoreCase))
		{
			return ModelRoot.Load(modelPath, settings);
		}

		// URI картинок берём из JSON напрямую: порядок элементов "images" совпадает с
		// ModelRoot.LogicalImages, а разбирать их через SharpGLTF мы как раз и не хотим.
		var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(modelPath)) ?? Environment.CurrentDirectory;
		var pathsByIndex = new Dictionary<int, string>();
		var stubbedUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		try
		{
			using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(modelPath));
			if (json.RootElement.TryGetProperty("images", out var images) &&
				images.ValueKind == System.Text.Json.JsonValueKind.Array)
			{
				int index = 0;
				foreach (var image in images.EnumerateArray())
				{
					if (image.TryGetProperty("uri", out var uriElement) &&
						uriElement.GetString() is { Length: > 0 } uri &&
						!uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
					{
						var relative = Uri.UnescapeDataString(uri).Replace('/', Path.DirectorySeparatorChar);
						var fullPath = Path.Combine(baseDirectory, relative);
						if (File.Exists(fullPath))
						{
							pathsByIndex[index] = fullPath;
							stubbedUris.Add(uri);
							stubbedUris.Add(Uri.UnescapeDataString(uri));
						}
					}

					index++;
				}
			}
		}
		catch (Exception)
		{
			// Не разобрали JSON сами - просто грузим обычным путём (медленно, но верно).
			return ModelRoot.Load(modelPath, settings);
		}

		if (pathsByIndex.Count == 0)
		{
			return ModelRoot.Load(modelPath, settings);
		}

		var context = ReadContext
			.Create(uri =>
			{
				if (stubbedUris.Contains(uri))
				{
					return new ArraySegment<byte>(StubPng);
				}

				var candidate = Path.Combine(baseDirectory, Uri.UnescapeDataString(uri)
					.Replace('/', Path.DirectorySeparatorChar));
				if (!File.Exists(candidate))
				{
					candidate = Path.Combine(baseDirectory, uri);
				}

				return new ArraySegment<byte>(File.ReadAllBytes(candidate));
			})
			.WithSettingsFrom(settings);

		externalImagePaths = pathsByIndex;
		return context.ReadSchema2(Path.GetFileName(modelPath));
	}

	/// <summary>Декодирование картинки (PNG/JPG) + даунскейл до <paramref name="maxSize"/> (см.
	/// ModelLoadOptions.MaxTextureSize). Чистый CPU без разделяемого состояния - зовётся из
	/// Parallel.For в PrepareModel.</summary>
	private static (byte[] Pixels, int Width, int Height) DecodeImagePixels(SharpGLTF.Schema2.Image image, int maxSize)
		=> DecodeEncodedImage(image.Content.Content.ToArray(), maxSize);

	/// <summary>Декод сжатой картинки (PNG/JPG) с даунскейлом до <paramref name="maxSize"/> (0 = без
	/// лимита). Публичный - им же фоновые апгрейды стрим-текстур ре-декодируют сохранённые исходники
	/// (см. <see cref="StreamedTextures"/>). Чистый CPU без разделяемого состояния - безопасен из
	/// любого потока; учти, что декод идёт в ПОЛНОМ разрешении файла и только потом ужимается (stb
	/// иначе не умеет) - пиковая память по одной задаче на 4K-исходнике ~64 МБ.</summary>
	public static (byte[] Pixels, int Width, int Height) DecodeEncodedImage(byte[] encodedBytes, int maxSize)
	{
		var decoded = ImageResult.FromMemory(encodedBytes, ColorComponents.RedGreenBlueAlpha);

		var pixels = decoded.Data;
		int width = decoded.Width;
		int height = decoded.Height;
		while (maxSize > 0 && (width > maxSize || height > maxSize))
		{
			(pixels, width, height) = DownscaleHalf(pixels, width, height);
		}

		return (pixels, width, height);
	}

	/// <summary>Бокс-фильтр 2x2 в один шаг вдвое - то же усреднение, что GPU GenerateMips, поэтому
	/// картинка после даунскейла совпадает с тем, что сэмплер и так показал бы на этом мипе.
	/// Нечётные размеры клампятся к краю (последние строка/столбец усредняются сами с собой).</summary>
	private static (byte[] pixels, int width, int height) DownscaleHalf(byte[] pixels, int width, int height)
	{
		int newWidth = Math.Max(1, width / 2);
		int newHeight = Math.Max(1, height / 2);
		var result = new byte[newWidth * newHeight * 4];

		for (int y = 0; y < newHeight; y++)
		{
			int srcY0 = Math.Min(height - 1, y * 2);
			int srcY1 = Math.Min(height - 1, y * 2 + 1);
			for (int x = 0; x < newWidth; x++)
			{
				int srcX0 = Math.Min(width - 1, x * 2);
				int srcX1 = Math.Min(width - 1, x * 2 + 1);
				int p00 = (srcY0 * width + srcX0) * 4;
				int p01 = (srcY0 * width + srcX1) * 4;
				int p10 = (srcY1 * width + srcX0) * 4;
				int p11 = (srcY1 * width + srcX1) * 4;
				int dst = (y * newWidth + x) * 4;
				for (int c = 0; c < 4; c++)
				{
					result[dst + c] = (byte)((pixels[p00 + c] + pixels[p01 + c] + pixels[p10 + c] + pixels[p11 + c] + 2) >> 2);
				}
			}
		}

		return (result, newWidth, newHeight);
	}

	/// <summary>Среднее линейное альбедо материала для <see cref="MaterialPbrFactors.AverageBaseColor"/>:
	/// разреженное среднее по base color текстуре (sRGB → linear), умноженное на линейный фактор.
	/// Без текстуры - просто фактор. Альфа (линейная, без sRGB) уходит в
	/// <see cref="MaterialPbrFactors.AverageAlpha"/> - по ней probe-GI бейкер отличает реально
	/// «дырявые» материалы (листва/трава/решётки, средняя альфа мала) от сплошных, которые
	/// экспортер зачем-то пометил MASK/BLEND (камень с альфой ~1) - см. ProbeGiBaker.</summary>
	private static Vector4 ComputeAverageBaseColor(PreparedMaterial pm)
	{
		var factor = new Vector3(pm.BaseColorFactor.X, pm.BaseColorFactor.Y, pm.BaseColorFactor.Z);
		var texture = pm.BaseColorTexture;
		if (texture?.Pixels == null || texture.Width <= 0 || texture.Height <= 0)
		{
			return new Vector4(factor, pm.BaseColorFactor.W);
		}

		// Каждый ~16-й пиксель: среднему хватает, а гигантские атласы не тормозят загрузку.
		int pixelCount = texture.Width * texture.Height;
		int stride = Math.Max(1, pixelCount / 4096);
		var sum = Vector3.Zero;
		float alphaSum = 0f;
		int count = 0;
		for (int i = 0; i < pixelCount; i += stride)
		{
			int idx = i * 4;
			if (idx + 3 >= texture.Pixels.Length)
			{
				break;
			}

			// sRGB → linear тем же pow(2.2), что и шейдер (см. UnlitInstancedPS.hlsl).
			sum += new Vector3(
				MathF.Pow(texture.Pixels[idx] / 255f, 2.2f),
				MathF.Pow(texture.Pixels[idx + 1] / 255f, 2.2f),
				MathF.Pow(texture.Pixels[idx + 2] / 255f, 2.2f));
			alphaSum += texture.Pixels[idx + 3] / 255f;
			count++;
		}

		return count > 0
			? new Vector4(sum / count * factor, alphaSum / count * pm.BaseColorFactor.W)
			: new Vector4(factor, pm.BaseColorFactor.W);
	}

	/// <summary>Сжатый исходник одной glTF-картинки для стриминга качества - один на image, шарится
	/// всеми PreparedTexture его каналов/материалов; в финализации по нему группируются привязки в
	/// один <see cref="StreamedTexture"/>.</summary>
	private sealed class TextureStreamSource
	{
		/// <summary>Внешний файл картинки (предпочтительно - ничего не держим в памяти).</summary>
		public string FilePath;

		/// <summary>Встроенные байты (.glb / data-URI), когда файла на диске нет.</summary>
		public byte[] EncodedBytes;
	}

	private sealed class PreparedTexture
	{
		public byte[] Pixels;
		public int Width;
		public int Height;
		public TextureAddress AddressMode;
		public TextureFilter FilterMode;

		/// <summary>null = стриминг выключен (обычный полноразмерный декод).</summary>
		public TextureStreamSource StreamSource;
	}

	private sealed class PreparedMaterial
	{
		public int LogicalIndex;
		public bool IsNull;
		public string Name;
		public PreparedTexture BaseColorTexture;
		public PreparedTexture MetallicRoughnessTexture;
		public PreparedTexture NormalTexture;
		public float NormalScale = 1f;
		public PreparedTexture OcclusionTexture;
		public float OcclusionStrength = 1f;

		/// <summary>glTF texCoord occlusion-канала (0/1, см. MaterialPbrFactors.OcclusionUvSet).</summary>
		public int OcclusionUvSet;
		public PreparedTexture ThicknessTexture;

		// KHR_texture_transform (см. MaterialPbrFactors.UvTransform/UvOffset/HasUvTransform).
		public Vector4 UvTransform;
		public Vector2 UvOffset;
		public bool HasUvTransform;

		// glTF spec defaults - overwritten in PrepareModel only when the material authored them.
		public Vector4 BaseColorFactor = Vector4.One;
		public float MetallicFactor = 1f;
		public float RoughnessFactor = 1f;
		public float AlphaCutoff;
		public float TransmissionFactor;
		public float Ior = 1.5f;
		public float Dispersion;
		public Vector4 VolumeAttenuation = new(1f, 1f, 1f, 0f);
		public float ThicknessFactor;

		// KHR_materials_sheen (нулевой цвет = выключено; roughness-дефолт спеки 0).
		public Vector3 SheenColorFactor;
		public float SheenRoughnessFactor;

		// KHR_materials_specular (дефолты спеки: белый цвет, вес 1 = тождественно).
		public Vector3 SpecularColorFactor = Vector3.One;
		public float SpecularFactor = 1f;
	}

	/// <summary>Сырьё одного glTF-примитива, собранное последовательной фазой PrepareModel (чтение
	/// SharpGLTF не потокобезопасно) для параллельной CPU-обработки (winding/нормали/тангенты/
	/// meshopt/LOD). Индекс в списке work-item-ов = будущий meshId.</summary>
	private sealed class MeshWorkItem
	{
		public string Name;
		public Vertex[] SourceVertices;
		public uint[] SourceIndices;
		public int Topology;
		public bool HasUv;
		public bool HasNormals;
		public bool HasTangents;
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

		/// <summary>Код топологии (MeshTopology*-константы).</summary>
		public int Topology;
	}

	private sealed class PreparedModel
	{
		public List<PreparedMaterial> Materials = new();
		public List<PreparedMesh> Meshes = new();
		public List<InstanceData> Instances = new();

		/// <summary>Реестр материалов-клонов для не-треугольных топологий: синтетический ключ ->
		/// (исходный glTF-материал, код топологии). Заполняется в PrepareModel, материализуется в
		/// BuildFromPrepared.</summary>
		public Dictionary<int, (int SourceMaterial, int Topology)> TopologyMaterialClones = new();

		/// <summary>Тайминги фоновых фаз, мс - для диагностики (см. ModelLoader.Timings). Без них
		/// оптимизация загрузки превращается в гадание: фазы стоят очень по-разному на разных
		/// ассетах, и «очевидный» виновник обычно не тот.</summary>
		public long MsParse, MsDecode, MsMaterials, MsMeshes;

		/// <summary>Сколько уникальных картинок декодировано и сколько мегабайт они заняли
		/// несжатыми - главный вкладчик в пиковую память загрузки.</summary>
		public int DecodedImages;
		public long DecodedBytes;
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

		// Состояние пошаговой финализации (см. FinalizeChunk): наполовину построенный ModelLoader и
		// текущая позиция итератора BuildFromPreparedIncremental. Живёт между кадрами.
		private ModelLoader _finalizing;
		private long _finalizeMs;
		private IEnumerator<long> _finalizeSteps;

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

		/// <summary>Дефолтный байтовый бюджет одного вызова <see cref="FinalizeChunk"/>. Подобран под
		/// дефолтный dynamicHeapSize Diligent-а: страницы upload-хипа возвращаются в пул только на
		/// FinishFrame (Present), поэтому бюджет кадра и определяет пиковый расход host-visible
		/// памяти при загрузке (у Sponza одним махом выходило 2.5+ GB и «Space in dynamic heap is
		/// almost exhausted» с принудительным idle GPU).</summary>
		public const long DefaultFinalizeBudgetBytes = 96L << 20;

		/// <summary>
		/// Creates the GPU resources (shaders/materials/textures/meshes) for a completed background load
		/// and returns the ready <see cref="ModelLoader"/>. Must be called on the thread that owns the
		/// <see cref="IGraphicsApi"/> passed to <see cref="BeginLoadAsync"/> (i.e. the main/render
		/// thread), only after <see cref="PrepareTask"/> has completed successfully. Заливает ВСЮ
		/// модель одним вызовом - в интерактивном рендер-лупе предпочитайте покадровый
		/// <see cref="FinalizeChunk"/>, иначе upload-хип раздувается на весь размер модели.
		/// </summary>
		public ModelLoader FinalizeOnMainThread() => FinalizeChunk(long.MaxValue, long.MaxValue);

		/// <summary>
		/// Покадровая версия <see cref="FinalizeOnMainThread"/>: создаёт GPU-ресурсы, пока суммарная
		/// оценка залитых байт не превысит <paramref name="budgetBytes"/>, и возвращает null - «зайди
		/// на следующем кадре» (между вызовами обязан пройти Present, освобождающий страницы
		/// upload-хипа). Когда всё создано - возвращает готовый <see cref="ModelLoader"/>. Те же
		/// требования, что у FinalizeOnMainThread: главный поток, PrepareTask завершился успешно.
		/// Внимание: бросить запрос между вызовами (не дойдя до результата) - значит утечь уже
		/// созданными GPU-ресурсами: у ModelLoader нет Release, недостроенный экземпляр никому не
		/// возвращается.
		/// </summary>
		public ModelLoader FinalizeChunk(long budgetBytes = DefaultFinalizeBudgetBytes,
			long budgetMs = FinalizeBudgetMs)
		{
			if (!PrepareTask.IsCompletedSuccessfully)
			{
				throw new InvalidOperationException(
					"FinalizeChunk called before the background load finished successfully.");
			}

			if (_prepared == null)
			{
				throw new InvalidOperationException("FinalizeChunk called after finalization already completed.");
			}

			if (_finalizeSteps == null)
			{
				_finalizing = new ModelLoader();
				_finalizeSteps = BuildFromPreparedIncremental(_graphicsApi, _options, _prepared, _finalizing);
			}

			// Финализация размазана по кадрам, поэтому её время копится по кусочкам - иначе цифра
			// показывала бы длину последнего чанка, а не стоимость фазы.
			var swFinalize = System.Diagnostics.Stopwatch.StartNew();

			long uploadedBytes = 0;
			while (uploadedBytes < budgetBytes)
			{
				if (!_finalizeSteps.MoveNext())
				{
					var ready = _finalizing;
					_finalizeMs += swFinalize.ElapsedMilliseconds;
					ready.Timings = new LoadTimings(_prepared.MsParse, _prepared.MsDecode,
						_prepared.MsMaterials, _prepared.MsMeshes, _finalizeMs,
						_prepared.DecodedImages, _prepared.DecodedBytes,
						ready._shaderVariants, ready._shaderMs, ready._textureCount, ready._textureMs,
						ready._meshCount, ready._meshMs, ready._samplerCount, ready._samplerMs, ready._materialCount, ready._materialMs, ready._matCreateMs, ready._matShaderMs);

					_finalizeSteps.Dispose();
					_finalizeSteps = null;
					_finalizing = null;
					_prepared = null;

					// Кэш PSO - на диск ровно здесь: загрузка только что создала все конвейеры модели,
					// а следующий запуск иначе скомпилирует их заново (см. IGraphicsApi.SavePipelineCache).
					_graphicsApi.SavePipelineCache();
					return ready;
				}

				uploadedBytes += _finalizeSteps.Current;

				// Бюджет ВРЕМЕНИ, а не только байт. Байтовый бюджет считает ЗАЛИВКУ, а самое дорогое
				// в финализации байт не заливает вовсе: компиляция вариантов пиксельного шейдера
				// (секунда на вариант) и создание материалов. В режиме стриминга текстур оценка
				// материала - жалкие 4 КБ, так что все 50+ материалов Sponza со всеми компиляциями
				// проходили за ОДИН вызов, то есть за один кадр: окно редактора висело "Not
				// Responding" на всё время загрузки.
				if (swFinalize.ElapsedMilliseconds >= budgetMs)
				{
					break;
				}
			}

			_finalizeMs += swFinalize.ElapsedMilliseconds;

			return null;
		}

		/// <summary>Сколько миллисекунд одному вызову <see cref="FinalizeChunk"/> позволено занимать
		/// поток рендера. Один шаг итератора прервать нельзя (компиляция шейдера идёт целиком), так
		/// что реальный кадр может выйти длиннее - но следующий шаг уже уедет в следующий кадр, и UI
		/// остаётся живым.</summary>
		public const long FinalizeBudgetMs = 8;

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