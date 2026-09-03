
namespace DecaEngine.Graphics.Assets;

/// <summary>Material slot of a texture; drives automatic compression format choice.</summary>
public enum TextureSlotKind
{
	/// <summary>Base color: sRGB data plus alpha for cutout/blend.</summary>
	BaseColor,

	/// <summary>glTF metallicRoughness: G = roughness, B = metallic, linear.</summary>
	MetallicRoughness,

	/// <summary>Tangent-space normal map, linear.</summary>
	Normal,

	/// <summary>Ambient occlusion, linear.</summary>
	Occlusion,

	/// <summary>KHR_materials_volume thickness in channel G, linear.</summary>
	Thickness,

	/// <summary>Emissive: sRGB data like base color, decoded to linear in the shader.</summary>
	Emissive,
}

/// <summary>Bake time versus quality; the managed BC7 encoder is much slower at the top level.</summary>
public enum TextureBakeQuality
{
	Fast,
	Balanced,
	Best,
}

/// <summary>
/// How the asset pipeline bakes one texture. Every field takes part in the cache key: any change
/// must force a rebake, otherwise a stale file survives under a name that promises new settings.
/// </summary>
public readonly record struct TextureImportSettings
{
	public required TextureObjectFormat Format { get; init; }

	/// <summary>Max side in texels; larger images are box-downscaled by 2. 0 means no limit.</summary>
	public required int MaxSize { get; init; }

	public required TextureBakeQuality Quality { get; init; }

	/// <summary>Renormalize after each halving; normal maps only, or far mips flatten the relief.</summary>
	public bool RenormalizeMips { get; init; }

	/// <summary>Default settings for a texture slot: BC7 for color, BC5 for normals.</summary>
	public static TextureImportSettings AutoFor(TextureSlotKind kind, int maxSize, TextureBakeQuality quality)
	{
		var format = kind switch
		{
			TextureSlotKind.Normal => TextureObjectFormat.BC5UNorm,
			_ => TextureObjectFormat.BC7UNorm,
		};

		return new TextureImportSettings
		{
			Format = format,
			MaxSize = maxSize,
			Quality = quality,
			RenormalizeMips = kind == TextureSlotKind.Normal,
		};
	}

	/// <summary>Stable cache key string; changes with every field.</summary>
	public string CacheKey() =>
		$"{(int)Format}-{MaxSize}-{(int)Quality}-{(RenormalizeMips ? 1 : 0)}";
}
