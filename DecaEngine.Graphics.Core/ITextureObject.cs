using System;
using DecaEngine.Core;
using SharpGLTF.Schema2; // For Image

namespace DecaEngine.Graphics
{
	public struct TextureInfo
	{
		public string name;
		public uint width;
		public uint height;
		public uint depth;
		public uint mipLevels;
		public uint arraySize;

		public TextureType type;
		public TextureObjectFormat format;
		public HandleAccess access;
		public bool dynamic;

		/// <summary>MSAA sample count (0/1 = без мультисемплинга). Мультисемпловый таргет нельзя
		/// сэмплировать обычным шейдером - перед использованием он резолвится в одиночный
		/// (см. ICommandBuffer.ResolveTexture).</summary>
		public uint sampleCount;
	}

	public enum TextureType : int
	{
		Texture1D = 0,
		Texture2D = 1,
		Texture3D = 2,
		TextureCube = 3,
		Texture1DArray = 4,
		Texture2DArray = 5
	}

	public enum TextureObjectFormat : int
	{
		Unknown = 0,
		R8G8B8A8UNorm = 1,
		R16G16B16A16Float = 2,
		R32G32B32A32Float = 3,
		D32Float = 4,
		D24UNormS8UInt = 5,
		D32FloatS8X24UInt = 6,

		/// <summary>Двухканальный полуплавающий таргет - экранные ВЕКТОРЫ, а не цвет: motion vectors
		/// (см. <see cref="DecaEngine.Core.MotionVectorPassResources"/>). Знаковый формат обязателен -
		/// смещение к прошлому кадру бывает любого знака, а UNorm срезал бы половину направлений.</summary>
		R16G16Float = 7,

		/// <summary>Одноканальный float32 - ТИПИЗИРОВАННАЯ копия глубины для нативных апскейлеров:
		/// сам депт Diligent создаёт как R32_TYPELESS, и внешний рантайм, строящий SRV по дескриптору
		/// ресурса, читал бы его нулями (см. FsrUpscalerBackend).</summary>
		R32Float = 8,

		// Блочно-сжатые форматы ассет-пайплайна (см. DecaEngine.Graphics.Assets.TextureBaker). Пекутся
		// оффлайн в редакторе и грузятся с диска готовой мип-цепочкой, минуя и CPU-декод, и генерацию
		// мипов на GPU. sRGB-вариантов тут НЕТ намеренно: шейдер материалов разворачивает базовый цвет
		// в линейное пространство вручную (pow(texel.rgb, 2.2) в UnlitInstancedPS.hlsl), так что
		// аппаратный *_SRGB-view применил бы ту же кривую второй раз и вымыл бы все текстуры.

		/// <summary>BC1 (DXT1), 4 бита/тексель - RGB без альфы. Вчетверо меньше RGBA8, но заметно
		/// грубее BC7 на градиентах; в авто-выборе не используется, доступен ручной настройкой.</summary>
		BC1UNorm = 9,

		/// <summary>BC3 (DXT5), 8 бит/тексель - RGB + полная альфа. Дешевле BC7 по времени
		/// кодирования, хуже по качеству; ручная настройка.</summary>
		BC3UNorm = 10,

		/// <summary>BC4, 4 бита/тексель - ОДИН канал (R). Для однокональных масок: толщина
		/// KHR_materials_volume и т.п.</summary>
		BC4UNorm = 11,

		/// <summary>BC5, 8 бит/тексель - ДВА канала (RG) с точностью заметно выше, чем у любого
		/// трёхканального BC на тех же битах. Штатный формат карт нормалей: Z не хранится, а
		/// восстанавливается в шейдере из XY (тангенциальные нормали всегда имеют z &gt; 0).</summary>
		BC5UNorm = 12,

		/// <summary>BC7, 8 бит/тексель - RGB(A) высокого качества. Дефолт для всех цветовых и
		/// ORM-каналов: вдвое меньше RGBA8 и практически неотличим от него на глаз.</summary>
		BC7UNorm = 13
	}

	/// <summary>Свойства <see cref="TextureObjectFormat"/>, нужные и загрузчику ассетов, и бэкенду:
	/// размер блока в байтах и раскладка мип-уровня. Живут здесь, а не в бэкенде, потому что
	/// .dtex-контейнер считает те же смещения на CPU при бейке и чтении.</summary>
	public static class TextureFormatLayout
	{
		/// <summary>true для BC*-форматов: данные хранятся блоками 4x4 текселя, а не построчно.</summary>
		public static bool IsBlockCompressed(TextureObjectFormat format) => format
			is TextureObjectFormat.BC1UNorm
			or TextureObjectFormat.BC3UNorm
			or TextureObjectFormat.BC4UNorm
			or TextureObjectFormat.BC5UNorm
			or TextureObjectFormat.BC7UNorm;

		/// <summary>Байт на блок 4x4 для блочных форматов; 0 для остальных.</summary>
		public static int BlockBytes(TextureObjectFormat format) => format switch
		{
			TextureObjectFormat.BC1UNorm or TextureObjectFormat.BC4UNorm => 8,
			TextureObjectFormat.BC3UNorm or TextureObjectFormat.BC5UNorm or TextureObjectFormat.BC7UNorm => 16,
			_ => 0
		};

		/// <summary>Байт на тексель для НЕблочных форматов; 0 для блочных.</summary>
		public static int BytesPerPixel(TextureObjectFormat format) => format switch
		{
			TextureObjectFormat.R8G8B8A8UNorm => 4,
			TextureObjectFormat.R16G16B16A16Float => 8,
			TextureObjectFormat.R32G32B32A32Float => 16,
			TextureObjectFormat.R16G16Float => 4,
			TextureObjectFormat.R32Float => 4,
			_ => 0
		};

		/// <summary>Длина одной строки данных мип-уровня в байтах. Для блочных форматов строка - это
		/// РЯД БЛОКОВ (4 текселя по высоте), поэтому округление вверх до кратного 4 обязательно:
		/// мип 3x3 всё равно занимает один полный блок.</summary>
		public static int RowPitch(TextureObjectFormat format, int width)
		{
			if (IsBlockCompressed(format))
			{
				return ((width + 3) / 4) * BlockBytes(format);
			}

			return width * BytesPerPixel(format);
		}

		/// <summary>Полный размер мип-уровня в байтах.</summary>
		public static int LevelBytes(TextureObjectFormat format, int width, int height)
		{
			if (IsBlockCompressed(format))
			{
				return ((width + 3) / 4) * ((height + 3) / 4) * BlockBytes(format);
			}

			return width * height * BytesPerPixel(format);
		}

		/// <summary>Число уровней полной мип-цепочки до 1x1.</summary>
		public static int FullMipCount(int width, int height)
		{
			int levels = 1;
			while (width > 1 || height > 1)
			{
				width = Math.Max(1, width >> 1);
				height = Math.Max(1, height >> 1);
				levels++;
			}

			return levels;
		}
	}

	public class CpuTextureData
	{
		public string Name { get; set; }
		public TextureInfo Info { get; set; }

		// For now, holding the SharpGLTF Image.
		// Ideally, this would be a raw byte array (byte[]) or Memory<byte>
		// after decoding, to decouple from GLTF entirely.
		public Image Image { get; set; }

		/// <summary>
		/// Already-decoded RGBA8 pixels for <see cref="Image"/>, if decoding was done ahead of time
		/// (e.g. on a background thread by <see cref="DecaEngine.Graphics.ModelLoader"/>) so that
		/// IGraphicsApi.CreateTexture, which must run on the GPU/main thread, can skip the decode step.
		/// Null if the implementation should decode <see cref="Image"/> itself.
		/// </summary>
		public byte[] DecodedPixels { get; set; }
		public int DecodedWidth { get; set; }
		public int DecodedHeight { get; set; }

		/// <summary>Сгенерировать полную мип-цепочку на GPU при создании (см.
		/// DiligentGraphicsApi.CreateTexture). Без мипов любая минификация (доска под острым
		/// углом) шумит и мылится, а анизотропная фильтрация не работает вовсе. 1x1-филлеры
		/// пропускаются автоматически. Игнорируется для <see cref="CompressedMips"/>: там цепочка
		/// уже забейкана оффлайн, а BC-формат на GPU не отфильтруешь.</summary>
		public bool GenerateMips { get; set; } = true;

		/// <summary>Готовая мип-цепочка в блочно-сжатом формате <see cref="CompressedFormat"/>, уровень
		/// на элемент (0 = полный размер). Приходит прямо из .dtex-кеша ассет-пайплайна, то есть путь
		/// «диск -&gt; VRAM» не проходит ни через декод PNG, ни через RGBA8-буфер, ни через генерацию
		/// мипов. Когда не null, <see cref="DecodedPixels"/>/<see cref="Image"/> не используются.</summary>
		public byte[][] CompressedMips { get; set; }

		/// <summary>Формат данных в <see cref="CompressedMips"/>.</summary>
		public TextureObjectFormat CompressedFormat { get; set; } = TextureObjectFormat.Unknown;

		/// <summary>Размеры нулевого уровня <see cref="CompressedMips"/>.</summary>
		public int CompressedWidth { get; set; }
		public int CompressedHeight { get; set; }

		public bool IsCompressed => CompressedMips is { Length: > 0 };
	}

	public interface IGpuTexture : IReleaseObject
	{
		public string Name { get; }
		public TextureInfo Info { get; }
	}
}