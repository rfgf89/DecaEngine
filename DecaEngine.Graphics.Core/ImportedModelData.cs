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

// Data model of the CPU import phase: what PrepareModel pulls out of glTF, consumed by both the
// loader's GPU finalization and the asset bakery (CookedModelFile/ModelAssetBaker).

// Compressed source of one glTF image, shared by every PreparedTexture that references it.
internal sealed class TextureStreamSource
{
	// External image file; preferred, since nothing is held in memory.
	public string FilePath;

	// Embedded bytes (.glb / data URI) when there is no file on disk.
	public byte[] EncodedBytes;
}

internal sealed class PreparedTexture
{
	public byte[] Pixels;
	public int Width;
	public int Height;
	public TextureAddress AddressMode;
	public TextureFilter FilterMode;

	// null = streaming off (plain full-size decode).
	public TextureStreamSource StreamSource;

	// Baked BC texture key; when set, Pixels is absent and the slot uploads straight from .dtex.
	public string CacheKey;

	// Bake phase only: the cache key is hashed from this image's compressed bytes. Never in .dmdl.
	public SharpGLTF.Schema2.Image SourceImage;
}

internal sealed class PreparedMaterial
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

	// glTF texCoord set of the occlusion channel (0/1).
	public int OcclusionUvSet;
	public PreparedTexture ThicknessTexture;

	// sRGB like base color; decoded only when EmissiveFactor is non-zero, since it multiplies.
	public PreparedTexture EmissiveTexture;

	// Linear emission: glTF emissiveFactor x KHR_materials_emissive_strength, folded on import.
	public Vector3 EmissiveFactor;

	// KHR_texture_transform.
	public Vector4 UvTransform;
	public Vector2 UvOffset;
	public bool HasUvTransform;

	// glTF spec defaults - overwritten in PrepareModel only when the material authored them.
	public Vector4 BaseColorFactor = Vector4.One;
	public float MetallicFactor = 1f;
	public float RoughnessFactor = 1f;
	public float AlphaCutoff;
	public MaterialAlphaMode AlphaMode;

	// Computed from pixels, so it must be stored in .dmdl. -1 = not computed yet.
	public float SoftAlphaFraction = -1f;
	public float TransmissionFactor;
	public float Ior = 1.5f;
	public float Dispersion;
	public Vector4 VolumeAttenuation = new(1f, 1f, 1f, 0f);
	public float ThicknessFactor;

	// KHR_materials_sheen (zero color = off; spec default roughness is 0).
	public Vector3 SheenColorFactor;
	public float SheenRoughnessFactor;

	// KHR_materials_specular (spec defaults: white color, weight 1 = identity).
	public Vector3 SpecularColorFactor = Vector3.One;
	public float SpecularFactor = 1f;

	// rgb = linear albedo, w = mean alpha. From pixels, so it must be stored in .dmdl.
	// null = not computed yet.
	public Vector4? AverageBaseColorRgba;
}

// One glTF primitive, gathered serially (SharpGLTF reads are not thread-safe) for parallel CPU
// processing. Index in the work-item list is the future meshId.
internal sealed class MeshWorkItem
{
	public string Name;
	public Vertex[] SourceVertices;
	public uint[] SourceIndices;
	public int Topology;
	public bool HasUv;
	public bool HasNormals;
	public bool HasTangents;

	// null for static geometry.
	public SkinVertex[] SourceSkin;
}

internal sealed class PreparedMesh
{
	public string Name;
	public Vertex[] Vertices;
	public uint[] Indices;
	public LodLevel[] LodLevels;

	// Parallel to Vertices; null means a static mesh drawn without compute skinning.
	public SkinVertex[] SkinVertices;
	public Vector3 BoundsCenter;
	public float BoundsRadius;
	public bool HasUv;

	// MeshTopology* constant.
	public int Topology;
}

internal sealed class PreparedModel
{
	public List<PreparedMaterial> Materials = new();
	public List<PreparedMesh> Meshes = new();
	public List<InstanceData> Instances = new();

	// null for a static model. One per model even when it has several skins.
	public PreparedSkeleton Skeleton;

	// Clips resolved against Skeleton's joints; empty when no clip touches the skeleton.
	public List<PreparedAnimation> Animations = new();

	// Clone materials for non-triangle topologies: synthetic key -> (glTF material, topology).
	public Dictionary<int, (int SourceMaterial, int Topology)> TopologyMaterialClones = new();

	// Background phase timings, ms.
	public long MsParse, MsDecode, MsMaterials, MsMeshes;

	// Unique images decoded and their uncompressed size: the peak load memory contributor.
	public int DecodedImages;
	public long DecodedBytes;

	// meshId -> 5 bytes per triangle: sRGB albedo + metallic + roughness. From pixels, so it must
	// be stored in .dmdl. Empty = not computed yet.
	public Dictionary<int, byte[]> TriangleAttributes = new();
}
