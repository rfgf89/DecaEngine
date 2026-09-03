using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using DecaEngine.Core;
using DecaEngine.Animation;

namespace DecaEngine.Graphics.Assets;

/// <summary>Serialized result of ModelImporter.PrepareModel: a cooked model on disk.</summary>
// Vertices, indices, LODs and instances are written raw; struct layout is part of the format.
public static class CookedModelFile
{
	// "DMDL" little-endian.
	private const uint Magic = 0x4C444D44;

	// Bump on ANY layout change, including Vertex/LodLevel/InstanceData fields: raw blocks shift.
	public const int FormatVersion = 8;

	public const string Extension = ".dmdl";

	// Atomic write (temp + Move): a truncated .dmdl is indistinguishable from a complete one.
	internal static void Write(string path, PreparedModel prepared)
	{
		var directory = Path.GetDirectoryName(path);
		if (!string.IsNullOrEmpty(directory))
		{
			Directory.CreateDirectory(directory);
		}

		var tempPath = path + ".tmp" + Environment.CurrentManagedThreadId.ToString();

		try
		{
			using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
			using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
			{
				writer.Write(Magic);
				writer.Write(FormatVersion);

				WriteMeshes(writer, prepared);
				WriteMaterials(writer, prepared);
				WriteBlittable(writer, CollectionsMarshal.AsSpan(prepared.Instances));
				WriteTopologyClones(writer, prepared);

				// Must run here: triangle attributes need texture pixels, which are gone after read.
				ModelImporter.EnsureTriangleAttributes(prepared);
				WriteTriangleAttributes(writer, prepared);

				WriteSkeleton(writer, prepared);
				WriteAnimations(writer, prepared);
			}

			File.Move(tempPath, path, overwrite: true);
		}
		catch
		{
			TryDelete(tempPath);
			throw;
		}
	}

	// null = missing, wrong format version or corrupt; the caller treats it as a cache miss.
	internal static PreparedModel? TryRead(string path)
	{
		try
		{
			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
			using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

			if (reader.ReadUInt32() != Magic || reader.ReadInt32() != FormatVersion)
			{
				return null;
			}

			var prepared = new PreparedModel();
			ReadMeshes(reader, prepared);
			ReadMaterials(reader, prepared);
			prepared.Instances.AddRange(ReadBlittable<InstanceData>(reader));
			ReadTopologyClones(reader, prepared);
			ReadTriangleAttributes(reader, prepared);
			ReadSkeleton(reader, prepared);
			ReadAnimations(reader, prepared);

			return prepared;
		}
		catch (EndOfStreamException)
		{
			return null;
		}
		catch (IOException)
		{
			return null;
		}
		catch (UnauthorizedAccessException)
		{
			return null;
		}
	}

	private static void WriteMeshes(BinaryWriter writer, PreparedModel prepared)
	{
		writer.Write(prepared.Meshes.Count);

		foreach (var mesh in prepared.Meshes)
		{
			writer.Write(mesh.Name ?? string.Empty);
			writer.Write(mesh.Topology);
			writer.Write(mesh.HasUv);
			WriteVector3(writer, mesh.BoundsCenter);
			writer.Write(mesh.BoundsRadius);

			WriteBlittable<Vertex>(writer, mesh.Vertices);
			WriteBlittable<uint>(writer, mesh.Indices);
			WriteBlittable<LodLevel>(writer, mesh.LodLevels);

			// Empty skin block means a static mesh.
			WriteBlittable<SkinVertex>(writer, mesh.SkinVertices);
		}
	}

	private static void ReadMeshes(BinaryReader reader, PreparedModel prepared)
	{
		int count = reader.ReadInt32();
		ThrowIfImplausibleCount(count);

		for (int i = 0; i < count; i++)
		{
			var mesh = new PreparedMesh
			{
				Name = reader.ReadString(),
				Topology = reader.ReadInt32(),
				HasUv = reader.ReadBoolean(),
			};

			mesh.BoundsCenter = ReadVector3(reader);
			mesh.BoundsRadius = reader.ReadSingle();

			mesh.Vertices = ReadBlittable<Vertex>(reader);
			mesh.Indices = ReadBlittable<uint>(reader);

			// Empty array and null are distinct states: null means the LOD group was never built.
			var lods = ReadBlittable<LodLevel>(reader);
			mesh.LodLevels = lods.Length > 0 ? lods : null;

			var skin = ReadBlittable<SkinVertex>(reader);
			mesh.SkinVertices = skin.Length > 0 ? skin : null;

			prepared.Meshes.Add(mesh);
		}
	}

	private static void WriteMaterials(BinaryWriter writer, PreparedModel prepared)
	{
		writer.Write(prepared.Materials.Count);

		foreach (var material in prepared.Materials)
		{
			writer.Write(material.LogicalIndex);
			writer.Write(material.IsNull);

			if (material.IsNull)
			{
				continue;
			}

			writer.Write(material.Name ?? string.Empty);

			WriteTexture(writer, material.BaseColorTexture);
			WriteTexture(writer, material.MetallicRoughnessTexture);
			WriteTexture(writer, material.NormalTexture);
			WriteTexture(writer, material.OcclusionTexture);
			WriteTexture(writer, material.ThicknessTexture);

			writer.Write(material.NormalScale);
			writer.Write(material.OcclusionStrength);
			writer.Write(material.OcclusionUvSet);

			WriteVector4(writer, material.UvTransform);
			WriteVector2(writer, material.UvOffset);
			writer.Write(material.HasUvTransform);

			WriteVector4(writer, material.BaseColorFactor);
			writer.Write(material.MetallicFactor);
			writer.Write(material.RoughnessFactor);
			writer.Write(material.AlphaCutoff);
			writer.Write((byte)material.AlphaMode);
			writer.Write(material.TransmissionFactor);
			writer.Write(material.Ior);
			writer.Write(material.Dispersion);
			WriteVector4(writer, material.VolumeAttenuation);
			writer.Write(material.ThicknessFactor);

			WriteVector3(writer, material.SheenColorFactor);
			writer.Write(material.SheenRoughnessFactor);

			WriteVector3(writer, material.SpecularColorFactor);
			writer.Write(material.SpecularFactor);

			WriteTexture(writer, material.EmissiveTexture);
			WriteVector3(writer, material.EmissiveFactor);

			// Average base color and alpha hardness come from pixels, which only exist while baking.
			ModelImporter.EnsureAverageBaseColor(material);
			WriteVector4(writer, material.AverageBaseColorRgba.Value);
			writer.Write(material.SoftAlphaFraction);
		}
	}

	private static void ReadMaterials(BinaryReader reader, PreparedModel prepared)
	{
		int count = reader.ReadInt32();
		ThrowIfImplausibleCount(count);

		for (int i = 0; i < count; i++)
		{
			var material = new PreparedMaterial
			{
				LogicalIndex = reader.ReadInt32(),
				IsNull = reader.ReadBoolean(),
			};

			if (material.IsNull)
			{
				prepared.Materials.Add(material);
				continue;
			}

			material.Name = reader.ReadString();

			material.BaseColorTexture = ReadTexture(reader);
			material.MetallicRoughnessTexture = ReadTexture(reader);
			material.NormalTexture = ReadTexture(reader);
			material.OcclusionTexture = ReadTexture(reader);
			material.ThicknessTexture = ReadTexture(reader);

			material.NormalScale = reader.ReadSingle();
			material.OcclusionStrength = reader.ReadSingle();
			material.OcclusionUvSet = reader.ReadInt32();

			material.UvTransform = ReadVector4(reader);
			material.UvOffset = ReadVector2(reader);
			material.HasUvTransform = reader.ReadBoolean();

			material.BaseColorFactor = ReadVector4(reader);
			material.MetallicFactor = reader.ReadSingle();
			material.RoughnessFactor = reader.ReadSingle();
			material.AlphaCutoff = reader.ReadSingle();
			material.AlphaMode = (MaterialAlphaMode)reader.ReadByte();
			material.TransmissionFactor = reader.ReadSingle();
			material.Ior = reader.ReadSingle();
			material.Dispersion = reader.ReadSingle();
			material.VolumeAttenuation = ReadVector4(reader);
			material.ThicknessFactor = reader.ReadSingle();

			material.SheenColorFactor = ReadVector3(reader);
			material.SheenRoughnessFactor = reader.ReadSingle();

			material.SpecularColorFactor = ReadVector3(reader);
			material.SpecularFactor = reader.ReadSingle();

			material.EmissiveTexture = ReadTexture(reader);
			material.EmissiveFactor = ReadVector3(reader);

			material.AverageBaseColorRgba = ReadVector4(reader);
			material.SoftAlphaFraction = reader.ReadSingle();

			prepared.Materials.Add(material);
		}
	}

	private static void WriteTexture(BinaryWriter writer, PreparedTexture texture)
	{
		if (texture?.CacheKey == null)
		{
			// No texture or a failed bake: both end up with a filler, so they need no distinction.
			writer.Write(false);
			return;
		}

		writer.Write(true);
		writer.Write(texture.CacheKey);
		writer.Write((int)texture.AddressMode);
		writer.Write((int)texture.FilterMode);

		// Baked mip 0 size: incremental finalization budgets frames by material bytes.
		writer.Write(texture.Width);
		writer.Write(texture.Height);
	}

	private static PreparedTexture? ReadTexture(BinaryReader reader)
	{
		if (!reader.ReadBoolean())
		{
			return null;
		}

		return new PreparedTexture
		{
			CacheKey = reader.ReadString(),
			AddressMode = (TextureAddress)reader.ReadInt32(),
			FilterMode = (TextureFilter)reader.ReadInt32(),
			Width = reader.ReadInt32(),
			Height = reader.ReadInt32(),
		};
	}

	private static void WriteTopologyClones(BinaryWriter writer, PreparedModel prepared)
	{
		writer.Write(prepared.TopologyMaterialClones.Count);

		foreach (var (key, value) in prepared.TopologyMaterialClones)
		{
			writer.Write(key);
			writer.Write(value.SourceMaterial);
			writer.Write(value.Topology);
		}
	}

	private static void ReadTopologyClones(BinaryReader reader, PreparedModel prepared)
	{
		int count = reader.ReadInt32();
		ThrowIfImplausibleCount(count);

		for (int i = 0; i < count; i++)
		{
			int key = reader.ReadInt32();
			int sourceMaterial = reader.ReadInt32();
			int topology = reader.ReadInt32();
			prepared.TopologyMaterialClones[key] = (sourceMaterial, topology);
		}
	}

	// Per-triangle material attributes: meshId -> 5 bytes per triangle, written raw.
	private static void WriteTriangleAttributes(BinaryWriter writer, PreparedModel prepared)
	{
		writer.Write(prepared.TriangleAttributes.Count);

		foreach (var (meshId, packed) in prepared.TriangleAttributes)
		{
			writer.Write(meshId);
			writer.Write(packed.Length);
			writer.Write(packed);
		}
	}

	private static void ReadTriangleAttributes(BinaryReader reader, PreparedModel prepared)
	{
		int count = reader.ReadInt32();
		ThrowIfImplausibleCount(count);

		for (int i = 0; i < count; i++)
		{
			int meshId = reader.ReadInt32();
			int length = reader.ReadInt32();
			ThrowIfImplausibleCount(length);
			prepared.TriangleAttributes[meshId] = reader.ReadBytes(length);
		}
	}

	// Joint names as strings, the rest as raw blocks; a zero-length block means a static model.
	private static void WriteSkeleton(BinaryWriter writer, PreparedModel prepared)
	{
		var skeleton = prepared.Skeleton;
		if (skeleton == null)
		{
			writer.Write(0);
			return;
		}

		writer.Write(skeleton.JointCount);
		foreach (var name in skeleton.JointNames)
		{
			writer.Write(name ?? string.Empty);
		}

		WriteBlittable(writer, skeleton.Parents);
		WriteBlittable(writer, skeleton.BindLocals);
		WriteBlittable(writer, skeleton.InverseBind);
	}

	private static void ReadSkeleton(BinaryReader reader, PreparedModel prepared)
	{
		int jointCount = reader.ReadInt32();
		ThrowIfImplausibleCount(jointCount);

		if (jointCount == 0)
		{
			return;
		}

		var skeleton = new PreparedSkeleton { JointNames = new string[jointCount] };
		for (int i = 0; i < jointCount; i++)
		{
			skeleton.JointNames[i] = reader.ReadString();
		}

		skeleton.Parents = ReadBlittable<int>(reader);
		skeleton.BindLocals = ReadBlittable<Transform>(reader);
		skeleton.InverseBind = ReadBlittable<Matrix4x4>(reader);

		// A length mismatch means a corrupt file; otherwise it faults far from the real cause.
		if (skeleton.Parents.Length != jointCount ||
			skeleton.BindLocals.Length != jointCount ||
			skeleton.InverseBind.Length != jointCount)
		{
			throw new InvalidDataException("Cooked model skeleton block is inconsistent.");
		}

		prepared.Skeleton = skeleton;
	}

	// One track per joint, channels independent; key times and values go as separate raw blocks.
	private static void WriteAnimations(BinaryWriter writer, PreparedModel prepared)
	{
		writer.Write(prepared.Animations.Count);

		foreach (var clip in prepared.Animations)
		{
			writer.Write(clip.Name ?? string.Empty);
			writer.Write(clip.Duration);
			writer.Write(clip.Tracks.Length);

			foreach (var track in clip.Tracks)
			{
				WriteBlittable(writer, track.TranslationTimes);
				WriteBlittable(writer, track.Translations);
				WriteBlittable(writer, track.RotationTimes);
				WriteBlittable(writer, track.Rotations);
				WriteBlittable(writer, track.ScaleTimes);
				WriteBlittable(writer, track.Scales);
			}
		}
	}

	private static void ReadAnimations(BinaryReader reader, PreparedModel prepared)
	{
		int clipCount = reader.ReadInt32();
		ThrowIfImplausibleCount(clipCount);

		for (int i = 0; i < clipCount; i++)
		{
			var clip = new PreparedAnimation
			{
				Name = reader.ReadString(),
				Duration = reader.ReadSingle(),
			};

			int trackCount = reader.ReadInt32();
			ThrowIfImplausibleCount(trackCount);

			clip.Tracks = new JointTrack[trackCount];
			for (int t = 0; t < trackCount; t++)
			{
				clip.Tracks[t] = new JointTrack
				{
					TranslationTimes = ReadBlittable<float>(reader),
					Translations = ReadBlittable<Vector3>(reader),
					RotationTimes = ReadBlittable<float>(reader),
					Rotations = ReadBlittable<Quaternion>(reader),
					ScaleTimes = ReadBlittable<float>(reader),
					Scales = ReadBlittable<Vector3>(reader),
				};
			}

			prepared.Animations.Add(clip);
		}
	}

	private static void WriteBlittable<T>(BinaryWriter writer, ReadOnlySpan<T> items) where T : unmanaged
	{
		writer.Write(items.Length);
		writer.Write(MemoryMarshal.AsBytes(items));
	}

	private static void WriteBlittable<T>(BinaryWriter writer, T[]? items) where T : unmanaged =>
		WriteBlittable(writer, items is null ? ReadOnlySpan<T>.Empty : items.AsSpan());

	private static T[] ReadBlittable<T>(BinaryReader reader) where T : unmanaged
	{
		int count = reader.ReadInt32();
		ThrowIfImplausibleCount(count);

		var items = new T[count];
		if (count == 0)
		{
			return items;
		}

		// ReadExactly, not Read: a short read would silently zero the tail of the geometry.
		reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(items.AsSpan()));
		return items;
	}

	// Rejects garbage lengths BEFORE allocating: a corrupt file would otherwise OOM instead of miss.
	private static void ThrowIfImplausibleCount(int count)
	{
		const int limit = 64 * 1024 * 1024;
		if (count < 0 || count > limit)
		{
			throw new InvalidDataException($"Cooked model declares an implausible element count ({count}).");
		}
	}

	private static void WriteVector2(BinaryWriter writer, Vector2 value)
	{
		writer.Write(value.X);
		writer.Write(value.Y);
	}

	private static void WriteVector3(BinaryWriter writer, Vector3 value)
	{
		writer.Write(value.X);
		writer.Write(value.Y);
		writer.Write(value.Z);
	}

	private static void WriteVector4(BinaryWriter writer, Vector4 value)
	{
		writer.Write(value.X);
		writer.Write(value.Y);
		writer.Write(value.Z);
		writer.Write(value.W);
	}

	private static Vector2 ReadVector2(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle());

	private static Vector3 ReadVector3(BinaryReader reader) =>
		new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

	private static Vector4 ReadVector4(BinaryReader reader) =>
		new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

	private static void TryDelete(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (IOException)
		{
			// Cleanup only; the original write error matters more and propagates.
		}
		catch (UnauthorizedAccessException)
		{
		}
	}
}
