using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using DecaEngine.Core.Diagnostics;

namespace DecaEngine.Editor
{
	/// <summary>
	/// Дисковый кеш BVH проб: файл &lt;модель&gt;.bhv.bin рядом с самой моделью. Сборка BVH - чистый
	/// CPU по всем треугольникам сцены, и на ассете уровня Sponza (~7.7 млн треугольников) она стоит
	/// ДЕСЯТКИ СЕКУНД при каждом открытии модели. Геометрия при этом не меняется, так что второй и
	/// последующие разы её можно просто прочитать с диска.
	///
	/// Валидность: файл привязан к (версия формата, размер и время модификации файла модели). Любое
	/// расхождение - кеш игнорируется и перестраивается; порченый/обрезанный файл тоже, поэтому
	/// чтение обёрнуто в try (кеш - оптимизация, а не источник правды).
	/// </summary>
	public static class ProbeGiBvhCache
	{
		private const uint Magic = 0x48564842; // "BHVH"

		/// <summary>Версия формата - поднимать при любом изменении раскладки полей ниже, иначе старый
		/// файл прочитается как мусорная геометрия. v2: массивы пишутся БЛОКАМИ (побайтовый образ
		/// структур), а не по одному float - на дереве Sponza это 884 МБ, и поэлементный BinaryWriter
		/// читал их почти пять секунд, съедая весь смысл кеша.</summary>
		private const int Version = 6; // v6: шероховатость треугольника (бывший Pad4); v5: вершинные окто-нормали (80 байт); v4: металличность; v3: UV/TextureIndex/HitTextureKeys

		/// <summary>Блочная запись массива blittable-структур: один Write вместо N*полей.</summary>
		private static void WriteArray<T>(Stream stream, BinaryWriter writer, T[] values) where T : unmanaged
		{
			writer.Write(values.Length);
			if (values.Length > 0)
			{
				stream.Write(MemoryMarshal.AsBytes(values.AsSpan()));
			}
		}

		/// <summary>Блочное чтение массива blittable-структур. ReadExactly - потому что Read с
		/// большого файла вправе вернуть меньше запрошенного, и частично заполненный массив выглядел
		/// бы как валидное (но битое) дерево.</summary>
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

		/// <summary>Путь кеша для модели: рядом с ней, суффиксом (Sponza.gltf -&gt; Sponza.gltf.bhv.bin).</summary>
		public static string GetCachePath(string modelPath) => modelPath + ".bhv.bin";

		/// <summary>Отпечаток исходника - по нему кеш признаётся годным.</summary>
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

			// Пишем во временный файл и переименовываем: прерванная запись (закрыли редактор на
			// середине) иначе оставила бы обрезанный файл, который следующий запуск считал бы своим.
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
					// Уборка мусора - не повод шуметь ещё раз.
				}
			}
		}

		/// <summary>Читает кеш, если он есть и совпадает по отпечатку. null - строить заново.</summary>
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
