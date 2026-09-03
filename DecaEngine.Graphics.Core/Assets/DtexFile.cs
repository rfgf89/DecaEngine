using System.Buffers.Binary;

namespace DecaEngine.Graphics.Assets;

// Deliberately uncompressed: BC blocks go disk -> VRAM untouched, so loading is I/O bound.
// The sampler is not stored here - it belongs to the glTF material slot, not the image.
/// <summary>Baked texture container: fixed header, mip length table, then the mip levels back to back.</summary>
public static class DtexFile
{
	// "DTEX" little-endian.
	private const uint Magic = 0x58455444;

	private const int Version = 1;

	// Magic + version + format + width + height + mip count = 6 * 4 bytes.
	private const int HeaderBytes = 24;

	public const string Extension = ".dtex";

	public readonly record struct Header(TextureObjectFormat Format, int Width, int Height, int MipCount);

	/// <summary>A whole baked texture: header plus per-level data, level 0 being full size.</summary>
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

	/// <summary>Writes a .dtex atomically (temp file then Move): a bake killed mid-write must not leave
	/// a truncated file whose name still matches the cache key.</summary>
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

	/// <summary>Reads a whole .dtex; null on a missing or invalid file, which callers treat as a cache miss.</summary>
	public static Payload? TryRead(string path) => TryReadFromLevel(path, 0);

	/// <summary>Reads the mip chain tail from <paramref name="firstLevel"/> down, giving a smaller
	/// texture with a full chain under it - one seek, one read, no decode. This is the streaming path.</summary>
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

				// Level length is derivable from format and size: a mismatch means a corrupt table.
				int width = Math.Max(1, header.Width >> i);
				int height = Math.Max(1, header.Height >> i);
				if (lengths[i] != TextureFormatLayout.LevelBytes(header.Format, width, height))
				{
					return null;
				}
			}

			// Skipped levels are seeked over, never read: that is why the length table is separate.
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

	/// <summary>Index of the first mip level whose longer side is at most <paramref name="size"/>.</summary>
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

	/// <summary>Reads the header only, without touching the mip data.</summary>
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

	/// <summary>Wraps .dtex data for a single graphics-API texture upload.</summary>
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
		}
		catch (UnauthorizedAccessException)
		{
		}
	}
}
