using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using CommunityToolkit.HighPerformance;

namespace DecaEngine.Graphics.Assets;

/// <summary>Offline asset step: RGBA8 -&gt; mip chain -&gt; BC blocks -&gt; <see cref="DtexFile"/>.</summary>
// Pure CPU, no graphics API calls, so background loader threads and the editor baker share it.
public static class TextureBaker
{
	/// <summary>Bump on ANY change to what the baker writes; it is part of the cache key.</summary>
	public const int PipelineVersion = 1;

	/// <summary>Encodes RGBA8 pixels (4 bytes per texel) into a block-compressed mip chain.</summary>
	// maxParallelism must be 1 when the caller already spreads images across threads.
	public static DtexFile.Payload Bake(byte[] rgba, int width, int height, TextureImportSettings settings,
		int maxParallelism = 0)
	{
		ArgumentNullException.ThrowIfNull(rgba);

		if (width <= 0 || height <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(width), $"Texture size must be positive, got {width}x{height}.");
		}

		if (rgba.Length < (long)width * height * 4)
		{
			throw new ArgumentException(
				$"Pixel buffer holds {rgba.Length} bytes, need {(long)width * height * 4} for {width}x{height} RGBA8.",
				nameof(rgba));
		}

		if (!TextureFormatLayout.IsBlockCompressed(settings.Format))
		{
			throw new ArgumentException($"{settings.Format} is not a block-compressed format.", nameof(settings));
		}

		var (pixels, levelWidth, levelHeight) = ClampToMaxSize(rgba, width, height, settings.MaxSize);
		int topWidth = levelWidth;
		int topHeight = levelHeight;

		var encoder = new BcEncoder
		{
			OutputOptions =
			{
				Format = ToCompressionFormat(settings.Format),
				Quality = ToCompressionQuality(settings.Quality),

				// Mips built here with the same 2x2 box the GPU path uses; cached and uncached loads
				// of one model must look identical.
				GenerateMipMaps = false,
			},
			Options =
			{
				// The encoder parallelizes blocks within one image; nesting that under per-image
				// threading oversubscribes the machine.
				IsParallel = maxParallelism != 1,
			},
		};

		int mipCount = TextureFormatLayout.FullMipCount(levelWidth, levelHeight);
		var mips = new byte[mipCount][];

		for (int level = 0; level < mipCount; level++)
		{
			if (level > 0)
			{
				(pixels, levelWidth, levelHeight) = DownscaleHalf(pixels, levelWidth, levelHeight);

				if (settings.RenormalizeMips)
				{
					Renormalize(pixels);
				}
			}

			mips[level] = EncodeLevel(encoder, pixels, levelWidth, levelHeight);
		}

		// Size taken from the ACTUAL level 0, never recomputed: DtexFile.TryRead cross-checks it.
		return new DtexFile.Payload
		{
			Format = settings.Format,
			Width = topWidth,
			Height = topHeight,
			Mips = mips,
		};
	}

	private static byte[] EncodeLevel(BcEncoder encoder, byte[] rgba, int width, int height)
	{
		var colors = new ColorRgba32[width * height];
		for (int i = 0; i < colors.Length; i++)
		{
			int src = i * 4;
			colors[i] = new ColorRgba32(rgba[src], rgba[src + 1], rgba[src + 2], rgba[src + 3]);
		}

		// GenerateMipMaps is off, so the result always holds exactly one level.
		var encoded = encoder.EncodeToRawBytes(new ReadOnlyMemory2D<ColorRgba32>(colors, height, width));
		return encoded[0];
	}

	// Returns the source buffer uncopied when nothing needs shrinking.
	private static (byte[] Pixels, int Width, int Height) ClampToMaxSize(byte[] rgba, int width, int height, int maxSize)
	{
		while (maxSize > 0 && (width > maxSize || height > maxSize) && (width > 1 || height > 1))
		{
			(rgba, width, height) = DownscaleHalf(rgba, width, height);
		}

		return (rgba, width, height);
	}

	// 2x2 box filter in STORAGE space, no sRGB decode: matches how the GPU mips RGBA8_UNORM.
	private static (byte[] Pixels, int Width, int Height) DownscaleHalf(byte[] pixels, int width, int height)
	{
		int newWidth = Math.Max(1, width >> 1);
		int newHeight = Math.Max(1, height >> 1);
		var result = new byte[newWidth * newHeight * 4];

		for (int y = 0; y < newHeight; y++)
		{
			int y0 = Math.Min(y * 2, height - 1);
			int y1 = Math.Min(y0 + 1, height - 1);

			for (int x = 0; x < newWidth; x++)
			{
				int x0 = Math.Min(x * 2, width - 1);
				int x1 = Math.Min(x0 + 1, width - 1);

				int i00 = (y0 * width + x0) * 4;
				int i01 = (y0 * width + x1) * 4;
				int i10 = (y1 * width + x0) * 4;
				int i11 = (y1 * width + x1) * 4;
				int dst = (y * newWidth + x) * 4;

				for (int c = 0; c < 4; c++)
				{
					result[dst + c] = (byte)((pixels[i00 + c] + pixels[i01 + c] + pixels[i10 + c] + pixels[i11 + c] + 2) / 4);
				}
			}
		}

		return (result, newWidth, newHeight);
	}

	// Restores unit length to averaged tangent-space normals, in place.
	private static void Renormalize(byte[] rgba)
	{
		for (int i = 0; i < rgba.Length; i += 4)
		{
			float x = rgba[i] / 255f * 2f - 1f;
			float y = rgba[i + 1] / 255f * 2f - 1f;
			float z = rgba[i + 2] / 255f * 2f - 1f;

			float length = MathF.Sqrt(x * x + y * y + z * z);
			if (length < 1e-6f)
			{
				// Degenerate texel (a black pixel in a normal map) becomes a flat normal.
				rgba[i] = 128;
				rgba[i + 1] = 128;
				rgba[i + 2] = 255;
				continue;
			}

			x /= length;
			y /= length;
			z /= length;

			rgba[i] = ToUNorm(x);
			rgba[i + 1] = ToUNorm(y);
			rgba[i + 2] = ToUNorm(z);
		}
	}

	private static byte ToUNorm(float value) =>
		(byte)Math.Clamp(MathF.Round((value * 0.5f + 0.5f) * 255f), 0f, 255f);

	private static CompressionFormat ToCompressionFormat(TextureObjectFormat format) => format switch
	{
		TextureObjectFormat.BC1UNorm => CompressionFormat.Bc1,
		TextureObjectFormat.BC3UNorm => CompressionFormat.Bc3,
		TextureObjectFormat.BC4UNorm => CompressionFormat.Bc4,
		TextureObjectFormat.BC5UNorm => CompressionFormat.Bc5,
		TextureObjectFormat.BC7UNorm => CompressionFormat.Bc7,
		_ => throw new ArgumentOutOfRangeException(nameof(format), format, "Not a supported block-compressed format.")
	};

	private static CompressionQuality ToCompressionQuality(TextureBakeQuality quality) => quality switch
	{
		TextureBakeQuality.Fast => CompressionQuality.Fast,
		TextureBakeQuality.Best => CompressionQuality.BestQuality,
		_ => CompressionQuality.Balanced,
	};
}
