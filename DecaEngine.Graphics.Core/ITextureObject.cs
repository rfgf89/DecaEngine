using System;
using DecaEngine.Core;
using SharpGLTF.Schema2; // For Image

namespace DecaEngine.Graphics.Core
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
		R32Float = 8
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
		/// пропускаются автоматически.</summary>
		public bool GenerateMips { get; set; } = true;
	}

	public interface IGpuTexture : IReleaseObject
	{
		public string Name { get; }
		public TextureInfo Info { get; }
	}
}