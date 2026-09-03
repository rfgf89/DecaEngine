using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DecaEngine.Core;

namespace DecaEngine.Animation;

/// <summary>Reads and writes the avatar next to the model: <c>Fox.glb</c> -> <c>Fox.avatar.json</c>.
/// Kept out of the .dmdl cook: authored data must survive cache invalidation. Slot -> bone name map
/// rather than an array so adding a slot keeps old files readable.</summary>
public static class HumanoidAvatarAsset
{
	private static readonly JsonSerializerOptions Options = new()
	{
		WriteIndented = true,
	};

	/// <summary>Avatar path for a model path.</summary>
	public static string PathFor(string modelPath) =>
		Path.ChangeExtension(modelPath, ".avatar.json");

	public static bool Exists(string modelPath) =>
		!string.IsNullOrEmpty(modelPath) && File.Exists(PathFor(modelPath));

	// Arrays rather than x/y/z objects: the file is hand-edited, "p":[0,0.12,0] stays on one line.
	private sealed class ReferenceBone
	{
		public float[]? P { get; set; }
		public float[]? R { get; set; }
		public float[]? S { get; set; }
	}

	private sealed class AvatarFile
	{
		public Dictionary<string, string>? Bones { get; set; }
		public Dictionary<string, ReferenceBone>? Reference { get; set; }
	}

	public static void Save(HumanoidAvatar avatar, string modelPath)
	{
		var file = new AvatarFile { Bones = new Dictionary<string, string>() };

		foreach (var info in HumanoidBones.All)
		{
			// Unassigned slots stay out of the file: it is meant to be read by a human.
			if (avatar.IsAssigned(info.Bone))
			{
				file.Bones[info.Bone.ToString()] = avatar[info.Bone];
			}
		}

		if (avatar.HasReferencePose)
		{
			file.Reference = new Dictionary<string, ReferenceBone>(StringComparer.Ordinal);

			foreach (var pair in avatar.ReferenceLocals)
			{
				var local = pair.Value;
				file.Reference[pair.Key] = new ReferenceBone
				{
					P = [local.position.X, local.position.Y, local.position.Z],
					R = [local.rotation.X, local.rotation.Y, local.rotation.Z, local.rotation.W],
					S = [local.scale.X, local.scale.Y, local.scale.Z],
				};
			}
		}

		string path = PathFor(modelPath);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, JsonSerializer.Serialize(file, Options));
	}

	/// <summary>Reads a model's avatar; null when missing or unreadable. Unknown slots are skipped so
	/// files written by a newer editor still load.</summary>
	public static HumanoidAvatar? Load(string modelPath)
	{
		string path = PathFor(modelPath);

		if (!File.Exists(path))
		{
			return null;
		}

		try
		{
			string text = File.ReadAllText(path);
			var avatar = new HumanoidAvatar();

			var file = JsonSerializer.Deserialize<AvatarFile>(text);

			// Legacy format: a flat slot -> bone map with no wrapper object.
			var bones = file?.Bones;
			if (bones == null)
			{
				bones = JsonSerializer.Deserialize<Dictionary<string, string>>(text);
			}

			if (bones == null)
			{
				return null;
			}

			foreach (var pair in bones)
			{
				if (Enum.TryParse<HumanoidBone>(pair.Key, out var bone) && bone < HumanoidBone.Count)
				{
					avatar[bone] = pair.Value;
				}
			}

			if (file?.Reference != null)
			{
				foreach (var pair in file.Reference)
				{
					var value = pair.Value;
					if (value?.P is not { Length: 3 } p || value.R is not { Length: 4 } r ||
						value.S is not { Length: 3 } s)
					{
						// Skip truncated entries: a zero scale would collapse the whole subtree.
						continue;
					}

					avatar.ReferenceLocals[pair.Key] = new Transform
					{
						position = new System.Numerics.Vector3(p[0], p[1], p[2]),
						rotation = new System.Numerics.Quaternion(r[0], r[1], r[2], r[3]),
						scale = new System.Numerics.Vector3(s[0], s[1], s[2]),
					};
				}
			}

			return avatar;
		}
		catch (Exception)
		{
			// A corrupt or foreign json means "no avatar"; the model must still open.
			return null;
		}
	}
}
