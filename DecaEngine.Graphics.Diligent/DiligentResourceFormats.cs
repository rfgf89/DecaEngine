using Diligent;

namespace DecaEngine.Graphics.Diligent;

/// <summary>
/// Centralizes the mapping between engine-level resource descriptions (<see cref="TextureObjectFormat"/>,
/// <see cref="BufferHandleType"/>/<see cref="HandleAccess"/>) and native Diligent resource descriptions.
/// Used by both the eagerly-created resource wrappers (<see cref="DiligentRenderTarget"/>,
/// <see cref="DiligentBufferHandle"/>) and the render graph (<see cref="DiligentRenderGraphBuilder"/>)
/// so the two code paths can never drift apart.
/// </summary>
public static class DiligentResourceFormats
{
	/// <summary>
	/// The single canonical mapping from the engine-level <see cref="TextureObjectFormat"/> to the
	/// native Diligent <see cref="TextureFormat"/>. Used both for texture creation and for PSO
	/// RTV/DSV format declarations, so the two can never disagree on what a given format means.
	/// </summary>
	public static TextureFormat ToNativeFormat(TextureObjectFormat format)
	{
		return format switch
		{
			TextureObjectFormat.R8G8B8A8UNorm => TextureFormat.RGBA8_UNorm,
			TextureObjectFormat.R16G16B16A16Float => TextureFormat.RGBA16_Float,
			TextureObjectFormat.R32G32B32A32Float => TextureFormat.RGBA32_Float,
			TextureObjectFormat.R16G16Float => TextureFormat.RG16_Float,
			TextureObjectFormat.R32Float => TextureFormat.R32_Float,
			TextureObjectFormat.D32Float => TextureFormat.D32_Float,
			TextureObjectFormat.D24UNormS8UInt => TextureFormat.D24_UNorm_S8_UInt,
			TextureObjectFormat.D32FloatS8X24UInt => TextureFormat.D32_Float_S8X24_UInt,
			// Блочно-сжатые форматы ассет-пайплайна. Все *_UNorm, а не *_UNorm_sRGB: шейдер
			// материалов сам разворачивает базовый цвет в линейное пространство (см. комментарий
			// у TextureObjectFormat.BC1UNorm).
			TextureObjectFormat.BC1UNorm => TextureFormat.BC1_UNorm,
			TextureObjectFormat.BC3UNorm => TextureFormat.BC3_UNorm,
			TextureObjectFormat.BC4UNorm => TextureFormat.BC4_UNorm,
			TextureObjectFormat.BC5UNorm => TextureFormat.BC5_UNorm,
			TextureObjectFormat.BC7UNorm => TextureFormat.BC7_UNorm,
			_ => TextureFormat.Unknown
		};
	}

	/// <summary>
	/// The reverse of <see cref="ToNativeFormat"/>, used to expose native formats (e.g. the swap chain's)
	/// back to engine code as a <see cref="TextureObjectFormat"/>.
	/// </summary>
	public static TextureObjectFormat ToTextureObjectFormat(TextureFormat format)
	{
		return format switch
		{
			TextureFormat.RGBA8_UNorm => TextureObjectFormat.R8G8B8A8UNorm,
			TextureFormat.RGBA16_Float => TextureObjectFormat.R16G16B16A16Float,
			TextureFormat.RGBA32_Float => TextureObjectFormat.R32G32B32A32Float,
			TextureFormat.RG16_Float => TextureObjectFormat.R16G16Float,
			TextureFormat.R32_Float => TextureObjectFormat.R32Float,
			TextureFormat.D32_Float => TextureObjectFormat.D32Float,
			TextureFormat.D24_UNorm_S8_UInt => TextureObjectFormat.D24UNormS8UInt,
			TextureFormat.D32_Float_S8X24_UInt => TextureObjectFormat.D32FloatS8X24UInt,
			TextureFormat.BC1_UNorm => TextureObjectFormat.BC1UNorm,
			TextureFormat.BC3_UNorm => TextureObjectFormat.BC3UNorm,
			TextureFormat.BC4_UNorm => TextureObjectFormat.BC4UNorm,
			TextureFormat.BC5_UNorm => TextureObjectFormat.BC5UNorm,
			TextureFormat.BC7_UNorm => TextureObjectFormat.BC7UNorm,
			_ => TextureObjectFormat.Unknown
		};
	}

	/// <summary>
	/// Maps a <see cref="TextureObjectFormat"/> to its native Diligent texture format and the
	/// "eager" bind flags a persistent, always-sampleable render target would use (i.e. including
	/// <see cref="BindFlags.ShaderResource"/> for color targets). Callers that need to defer the
	/// <see cref="BindFlags.ShaderResource"/> flag until it is actually required (e.g. the render
	/// graph, which only knows after all passes were set up) should mask it off for non-depth formats.
	/// </summary>
	public static (TextureFormat Format, BindFlags BindFlags) ToRenderTargetFormat(TextureObjectFormat format)
	{
		// No explicit format was requested: default to a standard color render target, same as
		// callers relied on before TextureObjectFormat.Unknown existed as a valid input here.
		if (format == TextureObjectFormat.Unknown)
		{
			format = TextureObjectFormat.R8G8B8A8UNorm;
		}

		var isDepth = format is TextureObjectFormat.D32Float
			or TextureObjectFormat.D24UNormS8UInt
			or TextureObjectFormat.D32FloatS8X24UInt;

		var bindFlags = isDepth
			? BindFlags.DepthStencil | BindFlags.ShaderResource
			: BindFlags.RenderTarget | BindFlags.ShaderResource;

		return (ToNativeFormat(format), bindFlags);
	}

	/// <summary>Как <see cref="ToRenderTargetFormat"/>, но с UAV-бинд-флагом по
	/// <see cref="HandleAccess.Compute"/> в <see cref="TextureInfo.access"/>: выход нативного
	/// апскейлера (FSR) пишется compute-шейдером через UAV (см. DiligentRenderTarget).</summary>
	public static (TextureFormat Format, BindFlags BindFlags) ToRenderTargetFormat(TextureObjectFormat format,
		HandleAccess access)
	{
		var (dilFormat, bindFlags) = ToRenderTargetFormat(format);
		if ((access & HandleAccess.Compute) != 0 && (bindFlags & BindFlags.DepthStencil) == 0)
		{
			bindFlags |= BindFlags.UnorderedAccess;
		}

		return (dilFormat, bindFlags);
	}

	/// <summary>
	/// Computes the native <see cref="BindFlags"/> for a buffer given its <see cref="BufferHandleType"/> and
	/// the <see cref="HandleAccess"/> it will be used with. Shared by <see cref="DiligentBufferHandle"/> and
	/// the render graph's buffer pinning so both paths agree on how a given <see cref="BufferInfo"/> is bound.
	/// </summary>
	public static BindFlags ToBufferBindFlags(BufferHandleType type, HandleAccess access)
	{
		var bindFlags = type switch
		{
			BufferHandleType.Constant => BindFlags.UniformBuffer,
			BufferHandleType.Vertex => BindFlags.VertexBuffer,
			BufferHandleType.Index => BindFlags.IndexBuffer,
			BufferHandleType.IndirectArgs => BindFlags.IndirectDrawArgs,
			_ => BindFlags.None
		};

		if (type != BufferHandleType.Constant)
		{
			if ((access & HandleAccess.Compute) != 0)
			{
				bindFlags |= BindFlags.UnorderedAccess;
			}

			if ((access & (HandleAccess.Vertex | HandleAccess.Pixel)) != 0)
			{
				bindFlags |= BindFlags.ShaderResource;
			}
		}

		return bindFlags;
	}

#if DEBUG
	/// <summary>
	/// Rough bytes-per-pixel estimate for a native <see cref="TextureFormat"/>, used only for the
	/// debug render-graph VRAM footprint display (<see cref="DiligentRenderGraphBuilder"/>). Not
	/// meant to be byte-exact for block-compressed formats.
	/// </summary>
	public static int GetApproxBytesPerPixel(TextureFormat format)
	{
		return format switch
		{
			TextureFormat.RGBA32_Float or TextureFormat.RGBA32_UInt or TextureFormat.RGBA32_SInt => 16,
			TextureFormat.RGBA16_Float or TextureFormat.RGBA16_UNorm or TextureFormat.RGBA16_UInt
				or TextureFormat.RGBA16_SInt or TextureFormat.RGBA16_SNorm
				or TextureFormat.D32_Float_S8X24_UInt => 8,
			TextureFormat.RGBA8_UNorm or TextureFormat.RGBA8_UNorm_sRGB or TextureFormat.RGBA8_UInt
				or TextureFormat.RGBA8_SInt or TextureFormat.RGBA8_SNorm
				or TextureFormat.D32_Float or TextureFormat.D24_UNorm_S8_UInt
				or TextureFormat.RGB10A2_UNorm or TextureFormat.RGB10A2_UInt
				or TextureFormat.R11G11B10_Float or TextureFormat.RG16_Float => 4,
			TextureFormat.RG8_UNorm or TextureFormat.RG8_UInt or TextureFormat.RG8_SInt or TextureFormat.RG8_SNorm
				or TextureFormat.R16_Float or TextureFormat.R16_UNorm or TextureFormat.R16_UInt
				or TextureFormat.R16_SInt or TextureFormat.R16_SNorm or TextureFormat.D16_UNorm => 2,
			TextureFormat.R8_UNorm or TextureFormat.R8_UInt or TextureFormat.R8_SInt or TextureFormat.R8_SNorm => 1,
			_ => 4, // reasonable default for anything unhandled (e.g. block-compressed, approx.)
		};
	}
#endif
}
