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

		/// <summary>MSAA sample count (0/1 = none); multisampled targets need ResolveTexture first.</summary>
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

		/// <summary>Motion vectors; signed format is required, UNorm would clip half the directions.</summary>
		R16G16Float = 7,

		/// <summary>Typed depth copy for native upscalers: Diligent's own depth is R32_TYPELESS.</summary>
		R32Float = 8,

		// No *_SRGB variants on purpose: the material shader linearizes base color itself
		// (pow(texel.rgb, 2.2) in UnlitInstancedPS.hlsl), an sRGB view would apply it twice.

		/// <summary>BC1 (DXT1), 4 bits/texel, RGB without alpha; manual selection only.</summary>
		BC1UNorm = 9,

		/// <summary>BC3 (DXT5), 8 bits/texel, RGB + full alpha; manual selection only.</summary>
		BC3UNorm = 10,

		/// <summary>BC4, 4 bits/texel, single channel (R) for masks.</summary>
		BC4UNorm = 11,

		/// <summary>BC5, 8 bits/texel, two channels (RG); normal maps rebuild Z from XY in the shader.</summary>
		BC5UNorm = 12,

		/// <summary>BC7, 8 bits/texel, high quality RGB(A); default for color and ORM.</summary>
		BC7UNorm = 13
	}

	/// <summary>Block size and mip layout of <see cref="TextureObjectFormat"/>, shared by the asset
	/// loader and the backend.</summary>
	public static class TextureFormatLayout
	{
		/// <summary>True for BC* formats: data is stored in 4x4 texel blocks, not scanlines.</summary>
		public static bool IsBlockCompressed(TextureObjectFormat format) => format
			is TextureObjectFormat.BC1UNorm
			or TextureObjectFormat.BC3UNorm
			or TextureObjectFormat.BC4UNorm
			or TextureObjectFormat.BC5UNorm
			or TextureObjectFormat.BC7UNorm;

		/// <summary>Bytes per 4x4 block for block formats; 0 otherwise.</summary>
		public static int BlockBytes(TextureObjectFormat format) => format switch
		{
			TextureObjectFormat.BC1UNorm or TextureObjectFormat.BC4UNorm => 8,
			TextureObjectFormat.BC3UNorm or TextureObjectFormat.BC5UNorm or TextureObjectFormat.BC7UNorm => 16,
			_ => 0
		};

		/// <summary>Bytes per texel for non-block formats; 0 for block formats.</summary>
		public static int BytesPerPixel(TextureObjectFormat format) => format switch
		{
			TextureObjectFormat.R8G8B8A8UNorm => 4,
			TextureObjectFormat.R16G16B16A16Float => 8,
			TextureObjectFormat.R32G32B32A32Float => 16,
			TextureObjectFormat.R16G16Float => 4,
			TextureObjectFormat.R32Float => 4,
			_ => 0
		};

		/// <summary>Row pitch of a mip level; for block formats a row is a row of 4x4 blocks.</summary>
		public static int RowPitch(TextureObjectFormat format, int width)
		{
			if (IsBlockCompressed(format))
			{
				return ((width + 3) / 4) * BlockBytes(format);
			}

			return width * BytesPerPixel(format);
		}

		/// <summary>Total size of a mip level in bytes.</summary>
		public static int LevelBytes(TextureObjectFormat format, int width, int height)
		{
			if (IsBlockCompressed(format))
			{
				return ((width + 3) / 4) * ((height + 3) / 4) * BlockBytes(format);
			}

			return width * height * BytesPerPixel(format);
		}

		/// <summary>Number of levels in a full mip chain down to 1x1.</summary>
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

		public Image Image { get; set; }

		/// <summary>Pre-decoded RGBA8 pixels for <see cref="Image"/>; null if not decoded yet.</summary>
		public byte[] DecodedPixels { get; set; }
		public int DecodedWidth { get; set; }
		public int DecodedHeight { get; set; }

		/// <summary>Generate the full mip chain on the GPU at creation; ignored when
		/// <see cref="CompressedMips"/> is set, BC data cannot be filtered on the GPU.</summary>
		public bool GenerateMips { get; set; } = true;

		/// <summary>Baked mip chain in <see cref="CompressedFormat"/>, one level per element (0 = full
		/// size). When set, <see cref="DecodedPixels"/> and <see cref="Image"/> are ignored.</summary>
		public byte[][] CompressedMips { get; set; }

		/// <summary>Format of the data in <see cref="CompressedMips"/>.</summary>
		public TextureObjectFormat CompressedFormat { get; set; } = TextureObjectFormat.Unknown;

		/// <summary>Size of level 0 of <see cref="CompressedMips"/>.</summary>
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