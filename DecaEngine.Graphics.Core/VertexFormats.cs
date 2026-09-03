using System.Numerics;
using System.Runtime.InteropServices;
using DecaEngine.Animation;
using DecaEngine.Core;

namespace DecaEngine.Graphics;

public struct InstanceData
{
	public Transform transform;
	public int meshId;
	public int materialId;
}

// Skinned vertices must go through meshopt whole: its passes reorder and drop vertices without
// always returning a remap, so a parallel skin stream would drift out of sync with the geometry.
/// <summary>What meshopt passes need from a vertex: position to simplify by and UV as a metric attribute.</summary>
public interface IMeshVertex
{
	Vector3 Position { get; }
	Vector2 TexCoord { get; }
}

/// <summary>Geometry plus skinning as one blittable block, used only for meshopt; .dmdl and the GPU
/// take it as two separate streams.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SkinnedVertex : IMeshVertex
{
	public Vertex Geometry;
	public SkinVertex Skin;

	public readonly Vector3 Position => Geometry.Position;
	public readonly Vector2 TexCoord => Geometry.TexCoord;
}

public struct Vertex : IMeshVertex
{
	public Vector3 Position;
	public Vector2 TexCoord;
	public Vector3 Normal;

	readonly Vector3 IMeshVertex.Position => Position;
	readonly Vector2 IMeshVertex.TexCoord => TexCoord;

	/// <summary>Tangent: xyz = U direction on the surface, w = bitangent sign (B = cross(N, T) * w).
	/// The sign flips when Z is mirrored; a wrong w inverts normal-map Y on mirrored UVs.</summary>
	public Vector4 Tangent;

	/// <summary>glTF COLOR_0 (linear, multiplies base color); white for meshes without the attribute.</summary>
	public Vector4 Color;

	/// <summary>glTF TEXCOORD_1, the second UV channel, mostly used by occlusionTexture; zero if absent.</summary>
	public Vector2 TexCoord1;
}
