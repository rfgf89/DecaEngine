using System.Buffers.Binary;
using System.IO.Compression;

namespace DecaEngine.Editor;

/// <summary>
/// Минимальный PNG-энкодер (8-bit RGBA, без интерлейса) для сохранения превью-иконок ассетов
/// (см. <see cref="ModelIconBaker"/>). Свой, а не библиотечный, потому что StbImageSharp
/// (уже в зависимостях) умеет только декодировать - а тянуть отдельный пакет ради записи
/// маленьких 128x128 иконок не хочется. Сжатие - стандартный zlib через
/// <see cref="ZLibStream"/> (PNG IDAT - это ровно zlib-поток отфильтрованных строк).
/// </summary>
public static class PngWriter
{
	private static readonly uint[] CrcTable = BuildCrcTable();

	public static void Write(string path, byte[] rgba, int width, int height)
	{
		using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);

		// PNG signature
		stream.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

		// IHDR: width, height, bit depth 8, color type 6 (RGBA), compression 0, filter 0, interlace 0
		Span<byte> ihdr = stackalloc byte[13];
		BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
		BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..], (uint)height);
		ihdr[8] = 8;
		ihdr[9] = 6;
		ihdr[10] = 0;
		ihdr[11] = 0;
		ihdr[12] = 0;
		WriteChunk(stream, "IHDR", ihdr.ToArray());

		// IDAT: zlib-поток строк, каждая строка предваряется байтом фильтра 0 (None).
		var rowBytes = width * 4;
		using (var idatBuffer = new MemoryStream())
		{
			using (var zlib = new ZLibStream(idatBuffer, CompressionLevel.Fastest, leaveOpen: true))
			{
				for (int y = 0; y < height; y++)
				{
					zlib.WriteByte(0);
					zlib.Write(rgba, y * rowBytes, rowBytes);
				}
			}

			WriteChunk(stream, "IDAT", idatBuffer.ToArray());
		}

		WriteChunk(stream, "IEND", Array.Empty<byte>());
	}

	private static void WriteChunk(Stream stream, string type, byte[] data)
	{
		Span<byte> lengthBytes = stackalloc byte[4];
		BinaryPrimitives.WriteUInt32BigEndian(lengthBytes, (uint)data.Length);
		stream.Write(lengthBytes);

		var typeBytes = new byte[] { (byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3] };
		stream.Write(typeBytes);
		stream.Write(data);

		var crc = UpdateCrc(0xFFFFFFFF, typeBytes);
		crc = UpdateCrc(crc, data);

		Span<byte> crcBytes = stackalloc byte[4];
		BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc ^ 0xFFFFFFFF);
		stream.Write(crcBytes);
	}

	private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
	{
		foreach (var b in data)
		{
			crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
		}

		return crc;
	}

	private static uint[] BuildCrcTable()
	{
		var table = new uint[256];
		for (uint n = 0; n < 256; n++)
		{
			var c = n;
			for (int k = 0; k < 8; k++)
			{
				c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
			}

			table[n] = c;
		}

		return table;
	}
}
