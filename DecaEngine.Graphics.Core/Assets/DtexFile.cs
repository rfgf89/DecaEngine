using System.Buffers.Binary;
using DecaEngine.Graphics.Core;

namespace DecaEngine.Graphics.Assets;

/// <summary>
/// Контейнер запечённой текстуры ассет-пайплайна: заголовок фиксированного размера, таблица длин
/// мип-уровней и сами уровни подряд, без какой-либо упаковки поверх.
///
/// Отсутствие внешнего сжатия (Deflate/Zstd) - осознанный выбор, а не недоделка. Смысл кеша в том,
/// чтобы путь «диск -&gt; VRAM» не требовал НИКАКОЙ обработки: BC-блоки читаются с диска ровно в том
/// виде, в каком уедут в текстуру, поэтому загрузка модели упирается только в скорость носителя, а
/// не в CPU. Deflate поверх BC даёт единицы процентов (блоки уже энтропийно плотные) и вернул бы в
/// критический путь распаковку, ради устранения которой всё и затевалось.
///
/// Сэмплер (wrap/filter) здесь НЕ хранится: он свойство слота материала в glTF, а не картинки, и
/// один и тот же .dtex законно шарится слотами с разными сэмплерами. Живёт в записи материала
/// cooked-модели (см. <see cref="CookedModelFile"/>).
/// </summary>
public static class DtexFile
{
	/// <summary>"DTEX" little-endian.</summary>
	private const uint Magic = 0x58455444;

	private const int Version = 1;

	/// <summary>Magic + версия + формат + ширина + высота + число мипов = 6 * 4 байта.</summary>
	private const int HeaderBytes = 24;

	public const string Extension = ".dtex";

	public readonly record struct Header(TextureObjectFormat Format, int Width, int Height, int MipCount);

	/// <summary>Запечённая текстура целиком: заголовок + данные по уровням (0 = полный размер).</summary>
	public sealed class Payload
	{
		public required TextureObjectFormat Format { get; init; }
		public required int Width { get; init; }
		public required int Height { get; init; }
		public required byte[][] Mips { get; init; }

		public long TotalBytes
		{
			get
			{
				long total = 0;
				foreach (var mip in Mips)
				{
					total += mip.LongLength;
				}

				return total;
			}
		}
	}

	/// <summary>
	/// Пишет .dtex атомарно: сначала во временный файл рядом, потом Move поверх цели. Бейк идёт в
	/// фоновых потоках и может быть убит закрытием редактора в любой момент - без атомарной подмены
	/// на диске остался бы обрезанный файл, который выглядит валидным по имени (ключ кеша совпадает)
	/// и при следующем запуске поехал бы в текстуру мусором.
	/// </summary>
	public static void Write(string path, Payload payload)
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
			{
				Span<byte> header = stackalloc byte[HeaderBytes];
				BinaryPrimitives.WriteUInt32LittleEndian(header[..4], Magic);
				BinaryPrimitives.WriteInt32LittleEndian(header.Slice(4, 4), Version);
				BinaryPrimitives.WriteInt32LittleEndian(header.Slice(8, 4), (int)payload.Format);
				BinaryPrimitives.WriteInt32LittleEndian(header.Slice(12, 4), payload.Width);
				BinaryPrimitives.WriteInt32LittleEndian(header.Slice(16, 4), payload.Height);
				BinaryPrimitives.WriteInt32LittleEndian(header.Slice(20, 4), payload.Mips.Length);
				stream.Write(header);

				Span<byte> lengthField = stackalloc byte[4];
				foreach (var mip in payload.Mips)
				{
					BinaryPrimitives.WriteInt32LittleEndian(lengthField, mip.Length);
					stream.Write(lengthField);
				}

				foreach (var mip in payload.Mips)
				{
					stream.Write(mip);
				}
			}

			File.Move(tempPath, path, overwrite: true);
		}
		catch
		{
			TryDelete(tempPath);
			throw;
		}
	}

	/// <summary>
	/// Читает .dtex целиком. Возвращает null, если файла нет или он не проходит проверку заголовка -
	/// вызывающий трактует это как промах кеша и перебейкивает, вместо того чтобы падать. Битый кеш
	/// (обрезанная запись, файл от старой версии формата) обязан лечиться сам: чинить его вручную
	/// пользователь всё равно не станет, а «редактор не запускается, сотрите папку» - плохой контракт.
	/// </summary>
	public static Payload? TryRead(string path) => TryReadFromLevel(path, 0);

	/// <summary>
	/// Читает ХВОСТ мип-цепочки начиная с уровня <paramref name="firstLevel"/> (0 = весь файл).
	/// Возвращает payload, чей нулевой уровень - это <paramref name="firstLevel"/> исходного файла,
	/// то есть готовую текстуру уменьшенного размера с полной цепочкой под ней.
	///
	/// Это и есть механика постепенной подгрузки. Уровни лежат в файле от большого к малому, поэтому
	/// «показать текстуру в 64px» - это ОДИН seek и ОДНО чтение до конца файла, а самые тяжёлые
	/// уровни (нулевой занимает три четверти файла) не читаются вовсе, пока качество до них не
	/// дошло. Ни декода, ни пересжатия на этом пути нет ни на одной ступени - в отличие от
	/// стриминга из PNG, где каждая ступень требует полного разжатия исходника.
	/// </summary>
	public static Payload? TryReadFromLevel(string path, int firstLevel)
	{
		try
		{
			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

			if (!TryReadHeader(stream, out var header))
			{
				return null;
			}

			if (firstLevel < 0 || firstLevel >= header.MipCount)
			{
				return null;
			}

			var lengths = new int[header.MipCount];
			Span<byte> lengthField = stackalloc byte[4];
			for (int i = 0; i < header.MipCount; i++)
			{
				stream.ReadExactly(lengthField);
				lengths[i] = BinaryPrimitives.ReadInt32LittleEndian(lengthField);

				// Длина каждого уровня выводима из формата и размеров - сверяем и отвергаем файл при
				// расхождении. Иначе повреждённая таблица длин заставила бы аллоцировать мусорный
				// размер и залить в текстуру не те байты (артефакты вместо честного промаха кеша).
				int width = Math.Max(1, header.Width >> i);
				int height = Math.Max(1, header.Height >> i);
				if (lengths[i] != TextureFormatLayout.LevelBytes(header.Format, width, height))
				{
					return null;
				}
			}

			// Пропускаемые уровни именно ПЕРЕПРЫГИВАЮТСЯ по смещению, а не вычитываются в никуда:
			// ради этого таблица длин и лежит в заголовке отдельно от данных.
			long skipBytes = 0;
			for (int i = 0; i < firstLevel; i++)
			{
				skipBytes += lengths[i];
			}

			stream.Seek(skipBytes, SeekOrigin.Current);

			var mips = new byte[header.MipCount - firstLevel][];
			for (int i = 0; i < mips.Length; i++)
			{
				mips[i] = new byte[lengths[firstLevel + i]];
				stream.ReadExactly(mips[i]);
			}

			return new Payload
			{
				Format = header.Format,
				Width = Math.Max(1, header.Width >> firstLevel),
				Height = Math.Max(1, header.Height >> firstLevel),
				Mips = mips,
			};
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

	/// <summary>
	/// Индекс уровня, чья бо́льшая сторона не превышает <paramref name="size"/>, для цепочки с верхним
	/// уровнем <paramref name="topWidth"/>x<paramref name="topHeight"/>. Ноль для size &gt;= верхнего
	/// уровня, последний уровень - для слишком мелких запросов.
	/// </summary>
	public static int LevelForSize(int topWidth, int topHeight, int size)
	{
		int level = 0;
		int width = topWidth;
		int height = topHeight;

		while (Math.Max(width, height) > size && (width > 1 || height > 1))
		{
			width = Math.Max(1, width >> 1);
			height = Math.Max(1, height >> 1);
			level++;
		}

		return level;
	}

	/// <summary>Читает только заголовок - для диагностики и для решений «стоит ли грузить целиком»
	/// без чтения мегабайт данных.</summary>
	public static bool TryReadHeader(string path, out Header header)
	{
		header = default;

		try
		{
			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
			return TryReadHeader(stream, out header);
		}
		catch (IOException)
		{
			return false;
		}
		catch (UnauthorizedAccessException)
		{
			return false;
		}
	}

	private static bool TryReadHeader(Stream stream, out Header header)
	{
		header = default;

		Span<byte> buffer = stackalloc byte[HeaderBytes];
		if (stream.ReadAtLeast(buffer, HeaderBytes, throwOnEndOfStream: false) < HeaderBytes)
		{
			return false;
		}

		if (BinaryPrimitives.ReadUInt32LittleEndian(buffer[..4]) != Magic)
		{
			return false;
		}

		if (BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(4, 4)) != Version)
		{
			return false;
		}

		var format = (TextureObjectFormat)BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(8, 4));
		int width = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(12, 4));
		int height = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(16, 4));
		int mipCount = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(20, 4));

		if (!TextureFormatLayout.IsBlockCompressed(format) || width <= 0 || height <= 0)
		{
			return false;
		}

		if (mipCount <= 0 || mipCount > TextureFormatLayout.FullMipCount(width, height))
		{
			return false;
		}

		header = new Header(format, width, height, mipCount);
		return true;
	}

	/// <summary>Готовит данные .dtex к заливке в текстуру одним вызовом графического API.</summary>
	public static CpuTextureData ToCpuTextureData(this Payload payload, string name) => new()
	{
		Name = name,
		CompressedMips = payload.Mips,
		CompressedFormat = payload.Format,
		CompressedWidth = payload.Width,
		CompressedHeight = payload.Height,
		GenerateMips = false,
	};

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
