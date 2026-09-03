using DecaEngine.Core.Assets;
using DecaEngine.Graphics.Assets;

namespace DecaEngine.Graphics;

/// <summary>
/// Controls how <see cref="ModelLoader"/> builds materials/meshes for a loaded glTF scene: which
/// shaders to use, and whether to run mesh optimization / LOD generation. Lets a lightweight editor
/// preview (see DecaEngine.Editor.ModelPreviewViewport) skip the same per-primitive optimization work
/// the main scene wants, instead of both paths paying for it unconditionally.
/// </summary>
public readonly struct ModelLoadOptions
{
	public static readonly float[] DefaultLodRatios = [0.5f, 0.25f, 0.1f, 0.05f, 0.0025f];

	public required EditorRef VertexShader { get; init; }
	public required EditorRef PixelShader { get; init; }
	public bool OptimizeMesh { get; init; }
	public bool GenerateLods { get; init; }

	/// <summary>8x anisotropic filtering; load-time: samplers are immutable, applies on next load.</summary>
	public bool AnisotropicFiltering { get; init; } = true;

	/// <summary>Mip LOD bias for model samplers (log2 of render scale under temporal upscale); load-time.</summary>
	public float MipLodBias { get; init; } = 0f;

	/// <summary>Compile Lighting-preview feature keywords into pixel variants; live toggles then
	/// work as bits inside the compiled feature.</summary>
	public bool PreviewLightingFeatures { get; init; } = true;

	/// <summary>Compile FEATURE_RT_SHADOWS variants (DXC/SM6.5, inline RayQuery); only valid on
	/// devices with inline ray tracing, and the caller MUST bind a TLAS to materials or resource
	/// commit hits an empty descriptor. Load-time toggle.</summary>
	public bool RtShadows { get; init; }

	/// <summary>Compile FEATURE_REFLECTION_GBUFFER pixel variants; must match whether the batch
	/// renderer was built with MRT reflection slots (in practice: MSAA off). Load-time toggle.</summary>
	public bool ReflectionGbuffer { get; init; }

	public float[] LodRatios { get; init; } = DefaultLodRatios;

	/// <summary>Max material texture side in pixels; larger ones are box-downscaled on decode
	/// (uncompressed RGBA8 + mips is ~5.3 B/px, a 4K texture is ~89 MB). 0 = no limit.</summary>
	public int MaxTextureSize { get; init; } = 2048;

	/// <summary>Texture streaming: load builds materials with 1x1 fillers and keeps encoded
	/// sources for background decode with hot swap, prioritized by camera distance.</summary>
	public bool StreamTextures { get; init; }

	/// <summary>First-decode texture side under StreamTextures; quality steps up to MaxTextureSize.</summary>
	public int StreamInitialTextureSize { get; init; } = 64;

	/// <summary>Asset-pipeline disk cache root; null/empty disables the pipeline. On a hit the
	/// glTF is not parsed at all (.dmdl geometry + .dtex BC textures); a miss loads normally and
	/// queues a background bake.</summary>
	public string CacheDirectory { get; init; } = AssetCache.DefaultRoot;

	/// <summary>BC encode quality for bakes; the managed encoder makes max-quality BC7 minutes
	/// slower on large scenes.</summary>
	public TextureBakeQuality BakeQuality { get; init; } = TextureBakeQuality.Balanced;

	public ModelLoadOptions()
	{
	}

	/// <summary>Asset cache for this load, or null when CacheDirectory is unset.</summary>
	public AssetCache Cache => string.IsNullOrEmpty(CacheDirectory) ? null : new AssetCache(CacheDirectory);

	/// <summary>
	/// Stable key capturing every field that changes the SHARED (device-level) load output - the
	/// geometry/textures/material CPU-data a <see cref="DecaEngine.Editor.ECS.ModelStore"/> entry
	/// produces for a given file path. Two loads of the same path with equal <see cref="Signature"/>
	/// are safe to share as ONE ModelLoader; anisotropy/MipLodBias/MaxTextureSize/etc. are baked into
	/// immutable samplers and the texture decoder (see the field docs above), so a mismatch on any of
	/// them means the models are NOT interchangeable and must load (and stay) separate.
	/// </summary>
	public string Signature()
	{
		var ratios = LodRatios ?? DefaultLodRatios;
		var ratioParts = new string[ratios.Length];
		for (int i = 0; i < ratios.Length; i++)
		{
			ratioParts[i] = ratios[i].ToString("R");
		}

		return string.Join('|',
			VertexShader.Path, PixelShader.Path,
			OptimizeMesh.ToString(), GenerateLods.ToString(), AnisotropicFiltering.ToString(),
			MipLodBias.ToString("R"), PreviewLightingFeatures.ToString(), RtShadows.ToString(),
			ReflectionGbuffer.ToString(),
			string.Join(',', ratioParts),
			MaxTextureSize.ToString(), StreamTextures.ToString(), StreamInitialTextureSize.ToString(),
			BakeQuality.ToString(), CacheDirectory ?? string.Empty);
	}

	/// <summary>Signature of only the fields that affect cooked-model CONTENT - the cache key.
	/// Deliberately separate from Signature(): keying the cache on shader/sampler/streaming
	/// fields would rebake byte-identical .dmdl files per preview-shader combination.</summary>
	public string CookSignature()
	{
		var ratios = LodRatios ?? DefaultLodRatios;
		var ratioParts = new string[ratios.Length];
		for (int i = 0; i < ratios.Length; i++)
		{
			ratioParts[i] = ratios[i].ToString("R");
		}

		return string.Join('|',
			OptimizeMesh.ToString(), GenerateLods.ToString(), string.Join(',', ratioParts),
			MaxTextureSize.ToString(), BakeQuality.ToString());
	}
}
