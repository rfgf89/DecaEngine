using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using DecaEngine.Core;
using DecaEngine.Graphics.Core;

namespace DecaEngine.Graphics.Assets;

/// <summary>
/// Контейнер запечённой модели: результат ФОНОВОЙ фазы загрузки (<c>ModelLoader.PrepareModel</c>)
/// целиком, сериализованный на диск. Хранит уже подготовленную геометрию - переведённую в
/// левостороннюю систему, с посчитанными нормалями и тангентами, прогнанную через meshopt и с
/// готовыми LOD-уровнями, - плюс материалы со ссылками на .dtex-текстуры и список инстансов.
///
/// Смысл ровно в том, чтобы при попадании в кеш glTF не открывался вообще. Разбор документа,
/// декод картинок, оптимизация вершин и упрощение под LOD - это и есть почти всё время загрузки, и
/// все они зависят только от исходника и опций, то есть считать их заново при каждом открытии
/// сцены незачем. Остаётся чтение линейного файла и заливка в GPU.
///
/// Вершины, индексы, LOD-уровни и инстансы пишутся ПОБАЙТОВО как есть: это blittable-структуры
/// (<see cref="Vertex"/>, <see cref="LodLevel"/>, <see cref="InstanceData"/> - только float/int),
/// и гонять их через пополевой BinaryWriter значило бы тратить на разбор ровно то время, ради
/// экономии которого файл и существует. Раскладка структур - часть формата, поэтому любое их
/// изменение обязано бампать <see cref="FormatVersion"/>.
/// </summary>
public static class CookedModelFile
{
	/// <summary>"DMDL" little-endian.</summary>
	private const uint Magic = 0x4C444D44;

	/// <summary>Версия формата. Бампать при ЛЮБОМ изменении раскладки, включая поля
	/// <see cref="Vertex"/>/<see cref="LodLevel"/>/<see cref="InstanceData"/>: они пишутся сырыми
	/// байтами, и добавленное поле молча сдвинет всю геометрию.</summary>
	// 2: добавлено PreparedMaterial.AverageBaseColorRgba - без него cooked-материалы приходили со
	// средней альфой = фактору (1), и отбор «дырявой» геометрии (листва) молча выключался.
	// 3: раскладка та же, но версия бампнута НАМЕРЕННО - ModelAssetBaker не проставлял слотам
	// CacheKey, поэтому все .dmdl версии 2 и ниже записаны БЕЗ единой ссылки на текстуру и дают
	// белую сцену. Отличить такой файл от честного (у модели и правда нет текстур) по содержимому
	// нельзя, поэтому старые версии просто объявляются промахом.
	// 4: добавлен PreparedMaterial.AlphaMode - без него MASK и BLEND схлопывались в один AlphaCutoff,
	// и BLEND-накладки (декали грязи Intel Sponza) нечем было исключить из кастеров тени.
	public const int FormatVersion = 4;

	public const string Extension = ".dmdl";

	/// <summary>Пишет cooked-модель атомарно (временный файл + Move) - по той же причине, что и
	/// <see cref="DtexFile.Write"/>: бейк идёт в фоне и переживает закрытие редактора не всегда, а
	/// обрезанный .dmdl по имени неотличим от целого.</summary>
	internal static void Write(string path, ModelLoader.PreparedModel prepared)
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
			}

			File.Move(tempPath, path, overwrite: true);
		}
		catch
		{
			TryDelete(tempPath);
			throw;
		}
	}

	/// <summary>Читает cooked-модель. null - файла нет, он от другой версии формата или повреждён;
	/// вызывающий трактует это как промах кеша и печёт заново (см. <see cref="DtexFile.TryRead"/>
	/// о том, почему битый кеш обязан лечиться сам).</summary>
	internal static ModelLoader.PreparedModel? TryRead(string path)
	{
		try
		{
			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
			using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

			if (reader.ReadUInt32() != Magic || reader.ReadInt32() != FormatVersion)
			{
				return null;
			}

			var prepared = new ModelLoader.PreparedModel();
			ReadMeshes(reader, prepared);
			ReadMaterials(reader, prepared);
			prepared.Instances.AddRange(ReadBlittable<InstanceData>(reader));
			ReadTopologyClones(reader, prepared);

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

	private static void WriteMeshes(BinaryWriter writer, ModelLoader.PreparedModel prepared)
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
		}
	}

	private static void ReadMeshes(BinaryReader reader, ModelLoader.PreparedModel prepared)
	{
		int count = reader.ReadInt32();
		ThrowIfImplausibleCount(count);

		for (int i = 0; i < count; i++)
		{
			var mesh = new ModelLoader.PreparedMesh
			{
				Name = reader.ReadString(),
				Topology = reader.ReadInt32(),
				HasUv = reader.ReadBoolean(),
			};

			mesh.BoundsCenter = ReadVector3(reader);
			mesh.BoundsRadius = reader.ReadSingle();

			mesh.Vertices = ReadBlittable<Vertex>(reader);
			mesh.Indices = ReadBlittable<uint>(reader);

			// Пустой массив LOD-уровней и его отсутствие - разные состояния (см.
			// ModelLoader.UploadLodGroup): null означает «группа не строилась».
			var lods = ReadBlittable<LodLevel>(reader);
			mesh.LodLevels = lods.Length > 0 ? lods : null;

			prepared.Meshes.Add(mesh);
		}
	}

	private static void WriteMaterials(BinaryWriter writer, ModelLoader.PreparedModel prepared)
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

			// Среднее base color считается по ПИКСЕЛЯМ, которых в cooked-модели нет, - значит оно
			// обязано лежать в файле. Ensure здесь, а не на вызывающей стороне: на этом шаге пиксели
			// ещё живы (печка держит PreparedModel целиком), а после чтения из кеша их уже не будет.
			ModelLoader.EnsureAverageBaseColor(material);
			WriteVector4(writer, material.AverageBaseColorRgba.Value);

			// Бинарность альфы - тоже по пикселям и тоже обязана лежать в файле (см.
			// PreparedMaterial.SoftAlphaFraction). Ensure выше считает её вместе со средним.
			writer.Write(material.SoftAlphaFraction);
		}
	}

	private static void ReadMaterials(BinaryReader reader, ModelLoader.PreparedModel prepared)
	{
		int count = reader.ReadInt32();
		ThrowIfImplausibleCount(count);

		for (int i = 0; i < count; i++)
		{
			var material = new ModelLoader.PreparedMaterial
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
			material.AverageBaseColorRgba = ReadVector4(reader);
			material.SoftAlphaFraction = reader.ReadSingle();

			prepared.Materials.Add(material);
		}
	}

	private static void WriteTexture(BinaryWriter writer, ModelLoader.PreparedTexture texture)
	{
		if (texture?.CacheKey == null)
		{
			// Слот без текстуры ИЛИ текстура, которую не удалось запечь: в обоих случаях материал
			// получит филлер, и различать их в cooked-виде незачем.
			writer.Write(false);
			return;
		}

		writer.Write(true);
		writer.Write(texture.CacheKey);
		writer.Write((int)texture.AddressMode);
		writer.Write((int)texture.FilterMode);

		// Размеры ЗАПЕЧЁННОГО уровня 0. Пикселей в cooked-модели нет, а инкрементальная финализация
		// нарезает работу по кадрам, оценивая материалы в байтах (см. ModelLoader.EstimateMaterialBytes):
		// без размеров все запечённые слоты весили бы ноль, и вся сцена финализировалась бы одним
		// куском в одном кадре - ровно тот хитч, ради устранения которого нарезка и заведена.
		writer.Write(texture.Width);
		writer.Write(texture.Height);
	}

	private static ModelLoader.PreparedTexture? ReadTexture(BinaryReader reader)
	{
		if (!reader.ReadBoolean())
		{
			return null;
		}

		return new ModelLoader.PreparedTexture
		{
			CacheKey = reader.ReadString(),
			AddressMode = (TextureAddress)reader.ReadInt32(),
			FilterMode = (TextureFilter)reader.ReadInt32(),
			Width = reader.ReadInt32(),
			Height = reader.ReadInt32(),
		};
	}

	private static void WriteTopologyClones(BinaryWriter writer, ModelLoader.PreparedModel prepared)
	{
		writer.Write(prepared.TopologyMaterialClones.Count);

		foreach (var (key, value) in prepared.TopologyMaterialClones)
		{
			writer.Write(key);
			writer.Write(value.SourceMaterial);
			writer.Write(value.Topology);
		}
	}

	private static void ReadTopologyClones(BinaryReader reader, ModelLoader.PreparedModel prepared)
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

		// ReadExactly, а не Read: короткое чтение здесь молча оставило бы хвост геометрии нулями -
		// меш без видимой ошибки схлопнулся бы в точку. Исключение ловит TryRead и трактует как
		// промах кеша.
		reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(items.AsSpan()));
		return items;
	}

	/// <summary>Отсекает мусорные длины ДО аллокации. Без этой проверки повреждённый файл (или
	/// файл от другого формата, прошедший magic по случайности) заказывал бы массив на пару
	/// миллиардов элементов, и вместо честного промаха кеша редактор получал бы OOM.</summary>
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
			// Уборка мусора; исходная ошибка записи важнее и пробрасывается выше.
		}
		catch (UnauthorizedAccessException)
		{
		}
	}
}
