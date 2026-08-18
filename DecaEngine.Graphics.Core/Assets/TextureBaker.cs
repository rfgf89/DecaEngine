using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using CommunityToolkit.HighPerformance;
using DecaEngine.Graphics.Core;

namespace DecaEngine.Graphics.Assets;

/// <summary>
/// Оффлайновая часть ассет-пайплайна: RGBA8 -&gt; мип-цепочка -&gt; BC-блоки -&gt; <see cref="DtexFile"/>.
/// Работает чистым CPU без единого обращения к графическому API, поэтому вызывается из фоновых
/// потоков загрузки и из бейкера редактора одинаково.
/// </summary>
public static class TextureBaker
{
	/// <summary>
	/// Бампается при ЛЮБОМ изменении того, что баркер кладёт в .dtex: смена фильтра уменьшения,
	/// перенормировки, версии кодировщика, авто-выбора формата. Входит в ключ кеша, поэтому старые
	/// файлы перебейкиваются автоматически.
	///
	/// Без этого счётчика единственной проверкой протухания осталось бы время изменения ИСХОДНИКА -
	/// а он при правке кода пайплайна не меняется, и весь уже накопленный кеш (у крупной сцены это
	/// гигабайты) навсегда застревал бы в старом виде. Ровно на эти грабли уже наступал кеш иконок,
	/// см. ModelIconCache.BakeVersion.
	/// </summary>
	public const int PipelineVersion = 1;

	/// <summary>
	/// Кодирует готовые RGBA8-пиксели в блочно-сжатую мип-цепочку.
	/// </summary>
	/// <param name="rgba">Пиксели верхнего уровня, 4 байта на тексель.</param>
	/// <param name="maxParallelism">Ограничение внутреннего параллелизма кодировщика. Вызывающий,
	/// который сам разложил картинки по потокам, обязан ставить 1 - иначе потоки перемножаются и
	/// машина уходит в переподписку.</param>
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

				// Мипы строим сами - тем же боксом 2x2, каким их всегда делал рантайм-путь (генерация
				// на GPU при создании текстуры, см. DiligentGraphicsApi.CreateTexture). Совпадение
				// фильтров тут принципиально: кешированная и некешированная загрузка одной модели
				// обязаны давать неотличимую картинку, иначе «включил кеш - поплыли дальние планы»
				// станет отдельным классом багов, который невозможно свести к чему-то одному.
				GenerateMipMaps = false,
			},
			Options =
			{
				// Кодировщик распараллеливает БЛОКИ внутри одной картинки. Вызывающий, который сам
				// разложил картинки по потокам, обязан это выключить: иначе потоки перемножаются и
				// машина уходит в переподписку, где на переключениях контекста теряется больше, чем
				// выигрывается на параллелизме.
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

		// Размеры берём от ФАКТИЧЕСКОГО нулевого уровня, а не пересчитываем кламп заново: повтор
		// одной и той же арифметики в двух местах - классический источник рассинхрона заголовка с
		// данными, а DtexFile.TryRead как раз сверяет длины уровней с размерами и отверг бы такой
		// файл как битый.
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

		// GenerateMipMaps выключен, поэтому массив всегда ровно из одного уровня.
		var encoded = encoder.EncodeToRawBytes(new ReadOnlyMemory2D<ColorRgba32>(colors, height, width));
		return encoded[0];
	}

	/// <summary>Ужимает до <paramref name="maxSize"/> последовательными делениями пополам. Отдаёт
	/// исходный буфер без копии, если ужимать нечего.</summary>
	private static (byte[] Pixels, int Width, int Height) ClampToMaxSize(byte[] rgba, int width, int height, int maxSize)
	{
		while (maxSize > 0 && (width > maxSize || height > maxSize) && (width > 1 || height > 1))
		{
			(rgba, width, height) = DownscaleHalf(rgba, width, height);
		}

		return (rgba, width, height);
	}

	/// <summary>Бокс-фильтр 2x2. Намеренно работает прямо в хранимом пространстве, БЕЗ разворота
	/// sRGB в линейное: ровно так же мипы генерирует GPU для RGBA8_UNORM-текстуры, а расхождение
	/// кешированного и некешированного путей дороже теоретически более правильного усреднения.</summary>
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

	/// <summary>Возвращает усреднённым тангенциальным нормалям единичную длину (см.
	/// <see cref="TextureImportSettings.RenormalizeMips"/>). Работает на месте.</summary>
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
				// Выродившийся тексель (чёрный пиксель в карте нормалей) - плоская нормаль.
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
