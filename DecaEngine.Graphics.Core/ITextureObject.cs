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
		D32FloatS8X24UInt = 6
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
	}

	public interface IGpuTexture : IReleaseObject
	{
		public string Name { get; }
		public TextureInfo Info { get; }
	}
}