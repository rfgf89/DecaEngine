using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;

namespace DecaEngine.Editor
{
	/// <summary>
	/// Ð”Ð¸ÑÐºÐ¾Ð²Ñ‹Ð¹ ÐºÐµÑˆ BVH Ð¿Ñ€Ð¾Ð±: Ñ„Ð°Ð¹Ð» &lt;Ð¼Ð¾Ð´ÐµÐ»ÑŒ&gt;.bhv.bin Ñ€ÑÐ´Ð¾Ð¼ Ñ ÑÐ°Ð¼Ð¾Ð¹ Ð¼Ð¾Ð´ÐµÐ»ÑŒÑŽ. Ð¡Ð±Ð¾Ñ€ÐºÐ° BVH - Ñ‡Ð¸ÑÑ‚Ñ‹Ð¹
	/// CPU Ð¿Ð¾ Ð²ÑÐµÐ¼ Ñ‚Ñ€ÐµÑƒÐ³Ð¾Ð»ÑŒÐ½Ð¸ÐºÐ°Ð¼ ÑÑ†ÐµÐ½Ñ‹, Ð¸ Ð½Ð° Ð°ÑÑÐµÑ‚Ðµ ÑƒÑ€Ð¾Ð²Ð½Ñ Sponza (~7.7 Ð¼Ð»Ð½ Ñ‚Ñ€ÐµÑƒÐ³Ð¾Ð»ÑŒÐ½Ð¸ÐºÐ¾Ð²) Ð¾Ð½Ð° ÑÑ‚Ð¾Ð¸Ñ‚
	/// Ð”Ð•Ð¡Ð¯Ð¢ÐšÐ˜ Ð¡Ð•ÐšÐ£ÐÐ” Ð¿Ñ€Ð¸ ÐºÐ°Ð¶Ð´Ð¾Ð¼ Ð¾Ñ‚ÐºÑ€Ñ‹Ñ‚Ð¸Ð¸ Ð¼Ð¾Ð´ÐµÐ»Ð¸. Ð“ÐµÐ¾Ð¼ÐµÑ‚Ñ€Ð¸Ñ Ð¿Ñ€Ð¸ ÑÑ‚Ð¾Ð¼ Ð½Ðµ Ð¼ÐµÐ½ÑÐµÑ‚ÑÑ, Ñ‚Ð°Ðº Ñ‡Ñ‚Ð¾ Ð²Ñ‚Ð¾Ñ€Ð¾Ð¹ Ð¸
	/// Ð¿Ð¾ÑÐ»ÐµÐ´ÑƒÑŽÑ‰Ð¸Ðµ Ñ€Ð°Ð·Ñ‹ ÐµÑ‘ Ð¼Ð¾Ð¶Ð½Ð¾ Ð¿Ñ€Ð¾ÑÑ‚Ð¾ Ð¿Ñ€Ð¾Ñ‡Ð¸Ñ‚Ð°Ñ‚ÑŒ Ñ Ð´Ð¸ÑÐºÐ°.
	///
	/// Ð’Ð°Ð»Ð¸Ð´Ð½Ð¾ÑÑ‚ÑŒ: Ñ„Ð°Ð¹Ð» Ð¿Ñ€Ð¸Ð²ÑÐ·Ð°Ð½ Ðº (Ð²ÐµÑ€ÑÐ¸Ñ Ñ„Ð¾Ñ€Ð¼Ð°Ñ‚Ð°, Ñ€Ð°Ð·Ð¼ÐµÑ€ Ð¸ Ð²Ñ€ÐµÐ¼Ñ Ð¼Ð¾Ð´Ð¸Ñ„Ð¸ÐºÐ°Ñ†Ð¸Ð¸ Ñ„Ð°Ð¹Ð»Ð° Ð¼Ð¾Ð´ÐµÐ»Ð¸). Ð›ÑŽÐ±Ð¾Ðµ
	/// Ñ€Ð°ÑÑ…Ð¾Ð¶Ð´ÐµÐ½Ð¸Ðµ - ÐºÐµÑˆ Ð¸Ð³Ð½Ð¾Ñ€Ð¸Ñ€ÑƒÐµÑ‚ÑÑ Ð¸ Ð¿ÐµÑ€ÐµÑÑ‚Ñ€Ð°Ð¸Ð²Ð°ÐµÑ‚ÑÑ; Ð¿Ð¾Ñ€Ñ‡ÐµÐ½Ñ‹Ð¹/Ð¾Ð±Ñ€ÐµÐ·Ð°Ð½Ð½Ñ‹Ð¹ Ñ„Ð°Ð¹Ð» Ñ‚Ð¾Ð¶Ðµ, Ð¿Ð¾ÑÑ‚Ð¾Ð¼Ñƒ
	/// Ñ‡Ñ‚ÐµÐ½Ð¸Ðµ Ð¾Ð±Ñ‘Ñ€Ð½ÑƒÑ‚Ð¾ Ð² try (ÐºÐµÑˆ - Ð¾Ð¿Ñ‚Ð¸Ð¼Ð¸Ð·Ð°Ñ†Ð¸Ñ, Ð° Ð½Ðµ Ð¸ÑÑ‚Ð¾Ñ‡Ð½Ð¸Ðº Ð¿Ñ€Ð°Ð²Ð´Ñ‹).
	/// </summary>
	public static class ProbeGiBvhCache
	{
		private const uint Magic = 0x48564842; // "BHVH"

		/// <summary>Ð’ÐµÑ€ÑÐ¸Ñ Ñ„Ð¾Ñ€Ð¼Ð°Ñ‚Ð° - Ð¿Ð¾Ð´Ð½Ð¸Ð¼Ð°Ñ‚ÑŒ Ð¿Ñ€Ð¸ Ð»ÑŽÐ±Ð¾Ð¼ Ð¸Ð·Ð¼ÐµÐ½ÐµÐ½Ð¸Ð¸ Ñ€Ð°ÑÐºÐ»Ð°Ð´ÐºÐ¸ Ð¿Ð¾Ð»ÐµÐ¹ Ð½Ð¸Ð¶Ðµ, Ð¸Ð½Ð°Ñ‡Ðµ ÑÑ‚Ð°Ñ€Ñ‹Ð¹
		/// Ñ„Ð°Ð¹Ð» Ð¿Ñ€Ð¾Ñ‡Ð¸Ñ‚Ð°ÐµÑ‚ÑÑ ÐºÐ°Ðº Ð¼ÑƒÑÐ¾Ñ€Ð½Ð°Ñ Ð³ÐµÐ¾Ð¼ÐµÑ‚Ñ€Ð¸Ñ. v2: Ð¼Ð°ÑÑÐ¸Ð²Ñ‹ Ð¿Ð¸ÑˆÑƒÑ‚ÑÑ Ð‘Ð›ÐžÐšÐÐœÐ˜ (Ð¿Ð¾Ð±Ð°Ð¹Ñ‚Ð¾Ð²Ñ‹Ð¹ Ð¾Ð±Ñ€Ð°Ð·
		/// ÑÑ‚Ñ€ÑƒÐºÑ‚ÑƒÑ€), Ð° Ð½Ðµ Ð¿Ð¾ Ð¾Ð´Ð½Ð¾Ð¼Ñƒ float - Ð½Ð° Ð´ÐµÑ€ÐµÐ²Ðµ Sponza ÑÑ‚Ð¾ 884 ÐœÐ‘, Ð¸ Ð¿Ð¾ÑÐ»ÐµÐ¼ÐµÐ½Ñ‚Ð½Ñ‹Ð¹ BinaryWriter
		/// Ñ‡Ð¸Ñ‚Ð°Ð» Ð¸Ñ… Ð¿Ð¾Ñ‡Ñ‚Ð¸ Ð¿ÑÑ‚ÑŒ ÑÐµÐºÑƒÐ½Ð´, ÑÑŠÐµÐ´Ð°Ñ Ð²ÐµÑÑŒ ÑÐ¼Ñ‹ÑÐ» ÐºÐµÑˆÐ°.</summary>
		private const int Version = 2;

		/// <summary>Ð‘Ð»Ð¾Ñ‡Ð½Ð°Ñ Ð·Ð°Ð¿Ð¸ÑÑŒ Ð¼Ð°ÑÑÐ¸Ð²Ð° blittable-ÑÑ‚Ñ€ÑƒÐºÑ‚ÑƒÑ€: Ð¾Ð´Ð¸Ð½ Write Ð²Ð¼ÐµÑÑ‚Ð¾ N*Ð¿Ð¾Ð»ÐµÐ¹.</summary>
		private static void WriteArray<T>(Stream stream, BinaryWriter writer, T[] values) where T : unmanaged
		{
			writer.Write(values.Length);
			if (values.Length > 0)
			{
				stream.Write(MemoryMarshal.AsBytes(values.AsSpan()));
			}
		}

		/// <summary>Ð‘Ð»Ð¾Ñ‡Ð½Ð¾Ðµ Ñ‡Ñ‚ÐµÐ½Ð¸Ðµ Ð¼Ð°ÑÑÐ¸Ð²Ð° blittable-ÑÑ‚Ñ€ÑƒÐºÑ‚ÑƒÑ€. ReadExactly - Ð¿Ð¾Ñ‚Ð¾Ð¼Ñƒ Ñ‡Ñ‚Ð¾ Read Ñ
		/// Ð±Ð¾Ð»ÑŒÑˆÐ¾Ð³Ð¾ Ñ„Ð°Ð¹Ð»Ð° Ð²Ð¿Ñ€Ð°Ð²Ðµ Ð²ÐµÑ€Ð½ÑƒÑ‚ÑŒ Ð¼ÐµÐ½ÑŒÑˆÐµ Ð·Ð°Ð¿Ñ€Ð¾ÑˆÐµÐ½Ð½Ð¾Ð³Ð¾, Ð¸ Ñ‡Ð°ÑÑ‚Ð¸Ñ‡Ð½Ð¾ Ð·Ð°Ð¿Ð¾Ð»Ð½ÐµÐ½Ð½Ñ‹Ð¹ Ð¼Ð°ÑÑÐ¸Ð² Ð²Ñ‹Ð³Ð»ÑÐ´ÐµÐ»
		/// Ð±Ñ‹ ÐºÐ°Ðº Ð²Ð°Ð»Ð¸Ð´Ð½Ð¾Ðµ (Ð½Ð¾ Ð±Ð¸Ñ‚Ð¾Ðµ) Ð´ÐµÑ€ÐµÐ²Ð¾.</summary>
		private static T[] ReadArray<T>(Stream stream, BinaryReader reader) where T : unmanaged
		{
			var count = reader.ReadInt32();
			if (count < 0)
			{
				throw new InvalidDataException($"Negative array length {count} in BVH cache.");
			}

			var values = new T[count];
			if (count > 0)
			{
				stream.ReadExactly(MemoryMarshal.AsBytes(values.AsSpan()));
			}

			return values;
		}

		/// <summary>ÐŸÑƒÑ‚ÑŒ ÐºÐµÑˆÐ° Ð´Ð»Ñ Ð¼Ð¾Ð´ÐµÐ»Ð¸: Ñ€ÑÐ´Ð¾Ð¼ Ñ Ð½ÐµÐ¹, ÑÑƒÑ„Ñ„Ð¸ÐºÑÐ¾Ð¼ (Sponza.gltf -&gt; Sponza.gltf.bhv.bin).</summary>
		public static string GetCachePath(string modelPath) => modelPath + ".bhv.bin";

		/// <summary>ÐžÑ‚Ð¿ÐµÑ‡Ð°Ñ‚Ð¾Ðº Ð¸ÑÑ…Ð¾Ð´Ð½Ð¸ÐºÐ° - Ð¿Ð¾ Ð½ÐµÐ¼Ñƒ ÐºÐµÑˆ Ð¿Ñ€Ð¸Ð·Ð½Ð°Ñ‘Ñ‚ÑÑ Ð³Ð¾Ð´Ð½Ñ‹Ð¼.</summary>
		private static bool TryGetStamp(string modelPath, out long length, out long modifiedTicks)
		{
			length = 0;
			modifiedTicks = 0;

			try
			{
				var info = new FileInfo(modelPath);
				if (!info.Exists)
				{
					return false;
				}

				length = info.Length;
				modifiedTicks = info.LastWriteTimeUtc.Ticks;
				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}

		public static void Write(string modelPath, ProbeGiBaker.BvhCacheData data)
		{
			if (!TryGetStamp(modelPath, out var length, out var modifiedTicks))
			{
				return;
			}

			var cachePath = GetCachePath(modelPath);

			// ÐŸÐ¸ÑˆÐµÐ¼ Ð²Ð¾ Ð²Ñ€ÐµÐ¼ÐµÐ½Ð½Ñ‹Ð¹ Ñ„Ð°Ð¹Ð» Ð¸ Ð¿ÐµÑ€ÐµÐ¸Ð¼ÐµÐ½Ð¾Ð²Ñ‹Ð²Ð°ÐµÐ¼: Ð¿Ñ€ÐµÑ€Ð²Ð°Ð½Ð½Ð°Ñ Ð·Ð°Ð¿Ð¸ÑÑŒ (Ð·Ð°ÐºÑ€Ñ‹Ð»Ð¸ Ñ€ÐµÐ´Ð°ÐºÑ‚Ð¾Ñ€ Ð½Ð°
			// ÑÐµÑ€ÐµÐ´Ð¸Ð½Ðµ) Ð¸Ð½Ð°Ñ‡Ðµ Ð¾ÑÑ‚Ð°Ð²Ð¸Ð»Ð° Ð±Ñ‹ Ð¾Ð±Ñ€ÐµÐ·Ð°Ð½Ð½Ñ‹Ð¹ Ñ„Ð°Ð¹Ð», ÐºÐ¾Ñ‚Ð¾Ñ€Ñ‹Ð¹ ÑÐ»ÐµÐ´ÑƒÑŽÑ‰Ð¸Ð¹ Ð·Ð°Ð¿ÑƒÑÐº ÑÑ‡Ð¸Ñ‚Ð°Ð» Ð±Ñ‹ ÑÐ²Ð¾Ð¸Ð¼.
			var tempPath = cachePath + ".tmp";

			try
			{
				using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
				using (var writer = new BinaryWriter(stream))
				{
					writer.Write(Magic);
					writer.Write(Version);
					writer.Write(length);
					writer.Write(modifiedTicks);

					writer.Write(data.SceneEpsilon);
					writer.Write(data.RayTMax);
					writer.Write(data.NodeCount);

					WriteArray(stream, writer, data.Triangles);
					WriteArray(stream, writer, data.Nodes);
					WriteArray(stream, writer, data.Order);
					WriteArray(stream, writer, data.ObjectTriangles);
					WriteArray(stream, writer, data.MeshSlots);
					WriteArray(stream, writer, data.Instances);
				}

				File.Move(tempPath, cachePath, overwrite: true);
			}
			catch (Exception ex)
			{
				EditorConsoleLog.Add(LogLevel.Warning, $"Probe BVH cache: failed to write '{cachePath}': {ex.Message}");

				try
				{
					if (File.Exists(tempPath))
					{
						File.Delete(tempPath);
					}
				}
				catch (Exception)
				{
					// Ð£Ð±Ð¾Ñ€ÐºÐ° Ð¼ÑƒÑÐ¾Ñ€Ð° - Ð½Ðµ Ð¿Ð¾Ð²Ð¾Ð´ ÑˆÑƒÐ¼ÐµÑ‚ÑŒ ÐµÑ‰Ñ‘ Ñ€Ð°Ð·.
				}
			}
		}

		/// <summary>Ð§Ð¸Ñ‚Ð°ÐµÑ‚ ÐºÐµÑˆ, ÐµÑÐ»Ð¸ Ð¾Ð½ ÐµÑÑ‚ÑŒ Ð¸ ÑÐ¾Ð²Ð¿Ð°Ð´Ð°ÐµÑ‚ Ð¿Ð¾ Ð¾Ñ‚Ð¿ÐµÑ‡Ð°Ñ‚ÐºÑƒ. null - ÑÑ‚Ñ€Ð¾Ð¸Ñ‚ÑŒ Ð·Ð°Ð½Ð¾Ð²Ð¾.</summary>
		public static ProbeGiBaker.BvhCacheData? TryRead(string modelPath)
		{
			var cachePath = GetCachePath(modelPath);

			if (!File.Exists(cachePath) || !TryGetStamp(modelPath, out var length, out var modifiedTicks))
			{
				return null;
			}

			try
			{
				using var stream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
				using var reader = new BinaryReader(stream);

				if (reader.ReadUInt32() != Magic || reader.ReadInt32() != Version ||
					reader.ReadInt64() != length || reader.ReadInt64() != modifiedTicks)
				{
					return null;
				}

				var sceneEpsilon = reader.ReadSingle();
				var rayTMax = reader.ReadSingle();
				var nodeCount = reader.ReadInt32();

				var triangles = ReadArray<ProbeGiBaker.CachedTri>(stream, reader);
				var nodes = ReadArray<ProbeGiBaker.CachedNode>(stream, reader);
				var order = ReadArray<int>(stream, reader);
				var objectTriangles = ReadArray<BvhTriangleGpu>(stream, reader);
				var meshSlots = ReadArray<(int First, int Count)>(stream, reader);
				var instances = ReadArray<ProbeGeometryInstance>(stream, reader);

				return new ProbeGiBaker.BvhCacheData
				{
					Triangles = triangles,
					Nodes = nodes,
					Order = order,
					NodeCount = nodeCount,
					SceneEpsilon = sceneEpsilon,
					RayTMax = rayTMax,
					ObjectTriangles = objectTriangles,
					MeshSlots = meshSlots,
					Instances = instances,
				};
			}
			catch (Exception ex)
			{
				EditorConsoleLog.Add(LogLevel.Warning, $"Probe BVH cache: failed to read '{cachePath}': {ex.Message}");
				return null;
			}
		}

	}
}
