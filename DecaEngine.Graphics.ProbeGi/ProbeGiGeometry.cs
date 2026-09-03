using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
using UnsafeCollections.Collections.Native;
using UnsafeCollections.Collections.Unsafe;

namespace DecaEngine.Graphics.ProbeGi;


/// <summary>BVH node laid out for a StructuredBuffer; must match BvhNode in SceneTrace.hlsl
/// byte for byte, hence the explicit padding around float3 fields.</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct BvhNodeGpu
{
	public Vector3 BoundsMin;

	/// <summary>&lt; 0 marks a leaf sliced by Start/Count; otherwise the left child index, with
	/// the right child in Start.</summary>
	public int Left;

	public Vector3 BoundsMax;
	public int Start;
	public int Count;
	public int Pad0, Pad1, Pad2;
}

/// <summary>Scene triangle for a StructuredBuffer; mirrors BvhTriangle in SceneTrace.hlsl.</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct BvhTriangleGpu
{
	public Vector3 A;

	/// <summary>UV of vertex A, two halves packed into float bits. Only filled for object-space
	/// geometry on the hardware path; the software path leaves zeros.</summary>
	public float UvA;
	public Vector3 E1;

	/// <summary>UV of vertex A+E1, packed as <see cref="UvA"/>.</summary>
	public float UvB;
	public Vector3 E2;

	/// <summary>UV of vertex A+E2, packed as <see cref="UvA"/>.</summary>
	public float UvC;

	/// <summary>Linear albedo for the bounce, returned directly by the GPU trace.</summary>
	public Vector3 Albedo;

	/// <summary>Metalness at the UV centroid; the software path leaves zero.</summary>
	public float Metalness;

	/// <summary>Vertex normals (A, A+E1, A+E2) as octahedral half pairs, in OBJECT space;
	/// transformed to world by the same edge matrix. Hardware path only.</summary>
	public float NormalA;
	public float NormalB;
	public float NormalC;

	/// <summary>Roughness at the UV centroid; the software path leaves zero.</summary>
	public float Roughness;

	/// <summary>Packs a UV pair as two halves in float bits. Half precision only holds up near
	/// zero, so the caller must fold the wrap first (subtract the triangle's common floor).</summary>
	public static float PackUv(Vector2 uv)
	{
		uint bits = System.BitConverter.HalfToUInt16Bits((Half)uv.X)
			| ((uint)System.BitConverter.HalfToUInt16Bits((Half)uv.Y) << 16);
		return System.BitConverter.UInt32BitsToSingle(bits);
	}

	/// <summary>Octahedral-encodes a unit normal; mirrors SceneUnpackOctNormal in
	/// SceneTrace.hlsl.</summary>
	public static float PackOctNormal(Vector3 n)
	{
		float sum = MathF.Abs(n.X) + MathF.Abs(n.Y) + MathF.Abs(n.Z);
		if (sum < 1e-12f)
		{
			return PackUv(Vector2.Zero);
		}

		var p = new Vector2(n.X / sum, n.Y / sum);
		if (n.Z < 0f)
		{
			p = new Vector2(
				(1f - MathF.Abs(p.Y)) * (p.X >= 0f ? 1f : -1f),
				(1f - MathF.Abs(p.X)) * (p.Y >= 0f ? 1f : -1f));
		}

		return PackUv(p);
	}
}

/// <summary>Scene instance for hardware tracing; mirrors the part of SceneInstance the shader
/// sees. SourceInstance indexes the source model's instance list (numbering here is separate,
/// since glass, foliage and degenerate meshes are skipped). World pose is
/// LocalTransform times the scene entry's world matrix. TextureIndex is -1 when the material
/// has no base colour texture, and BaseColorFactor is applied by the shader after sampling.</summary>
public readonly record struct ProbeGeometryInstance(int MeshSlot, int SourceInstance, Vector3 Albedo,
	Matrix4x4 Transform, int SourceModel = 0, Matrix4x4 LocalTransform = default,
	int TextureIndex = -1, Vector3 BaseColorFactor = default);

/// <summary>Scene geometry for hardware tracing: triangles in OBJECT space, one copy per mesh,
/// plus an instance table with matrices. Unlike the software path's world-space soup, geometry
/// here is pose-independent, so moving the world only costs a TLAS rebuild.</summary>
public sealed class ProbeInstancedGeometry
{
	/// <summary>Triangles of all meshes back to back, in object space. The albedo field is left
	/// unset: albedo is a property of the instance, so the shader reads it from
	/// <see cref="Instances"/>.</summary>
	public required BvhTriangleGpu[] Triangles { get; init; }

	/// <summary>Per-mesh slice of <see cref="Triangles"/>; also the base for
	/// CommittedPrimitiveIndex.</summary>
	public required (int First, int Count)[] Meshes { get; init; }

	/// <summary>Instances in TLAS order: the index here is InstanceID() in the shader.</summary>
	public required ProbeGeometryInstance[] Instances { get; init; }

	/// <summary>Unique base colour textures as (model index, materialId) keys rather than GPU
	/// objects, so they survive the on-disk BVH cache.</summary>
	public required (int Model, int Material)[] HitTextureKeys { get; init; }

	/// <summary>Cap on unique hit textures; materials past it fall back to per-triangle
	/// albedo (TextureIndex = -1).</summary>
	public const int MaxHitTextures = DecaEngine.Graphics.SsrPassResources.MaxHitTextures;

	public int TriangleCount => Triangles.Length;
}
