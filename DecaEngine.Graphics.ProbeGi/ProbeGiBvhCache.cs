using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using DecaEngine.Core.Diagnostics;

namespace DecaEngine.Graphics.ProbeGi
{
	/// <summary>On-disk BVH cache keyed by format version plus model file size and timestamp.</summary>
	public static class ProbeGiBvhCache
	{
		private const uint Magic = 0x48564842; // "BHVH"

		// Bump on any layout change below: an old file would be read as garbage geometry.
		private const int Version = 6;

		private static void WriteArray<T>(Stream stream, BinaryWriter writer, T[] values) where T : unmanaged
		{
			writer.Write(values.Length);
			if (values.Length > 0)
			{
				stream.Write(MemoryMarshal.AsBytes(values.AsSpan()));
			}
		}

		// ReadExactly, not Read: a short read would look like a valid but corrupt tree.
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

		/// <summary>Cache path for a model: the model path plus a .bhv.bin suffix.</summary>
		public static string GetCachePath(string modelPath) => modelPath + ".bhv.bin";

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

			// Write-then-rename: an interrupted write would otherwise leave a truncated cache file.
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
					WriteArray(stream, writer, data.HitTextureKeys);
				}

				File.Move(tempPath, cachePath, overwrite: true);
			}
			catch (Exception ex)
			{
				EngineLog.Add(LogLevel.Warning, $"Probe BVH cache: failed to write '{cachePath}': {ex.Message}");

				try
				{
					if (File.Exists(tempPath))
					{
						File.Delete(tempPath);
					}
				}
				catch (Exception)
				{
				}
			}
		}

		/// <summary>Reads the cache if present and current; null means the BVH must be rebuilt.</summary>
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
				var hitTextureKeys = ReadArray<(int Model, int Material)>(stream, reader);

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
					HitTextureKeys = hitTextureKeys,
				};
			}
			catch (Exception ex)
			{
				EngineLog.Add(LogLevel.Warning, $"Probe BVH cache: failed to read '{cachePath}': {ex.Message}");
				return null;
			}
		}

	}
}
