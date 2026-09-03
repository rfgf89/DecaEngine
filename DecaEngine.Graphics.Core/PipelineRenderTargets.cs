
using DecaEngine.Core;

namespace DecaEngine.Graphics;

/// <summary>Owns the off-screen color/depth/scene-copy targets a <see cref="GraphicsPipelineSimple"/>
/// renders into - the off-screen equivalent of a swap chain's back buffer.</summary>
// No MSAA path: antialiasing is left to the temporal upscalers (TemporalUpscalePass).
public sealed class PipelineRenderTargets : IReleaseObject
{
	public IRenderTarget ColorTarget { get; }
	public IRenderTarget DepthTarget { get; }

	/// <summary>Linear RGBA16F scene target, non-null only in HDR mode; ColorTarget stays
	/// display-space RGBA8 because ImGui and preview readback sample it.</summary>
	public IRenderTarget? HdrColorTarget { get; }

	/// <summary>Format geometry actually renders into; post-pass PSOs must match it.</summary>
	public TextureObjectFormat RenderColorFormat { get; }

	/// <summary>Target geometry renders into: the HDR target when HDR, else the display color.</summary>
	public IRenderTarget RenderColorTarget => HdrColorTarget ?? ColorTarget;

	/// <summary>Sampleable copy of the scene after opaque draws, used as the refraction source.</summary>
	public IRenderTarget SceneCopyTarget { get; }

	/// <summary>Reflection G-buffer, non-null in HDR mode: world shading normal (xyz) plus
	/// perceptual roughness (w), written from the second MRT slot of opaque draws.</summary>
	public IRenderTarget? NormalRoughnessTarget { get; }

	/// <summary>Third MRT slot: the full env-specular multiplier (rgb) so SSR can replace the
	/// prefiltered environment instead of adding on top; w = 1 marks lit-path pixels.</summary>
	public IRenderTarget? EnvFactorTarget { get; }

	public PipelineRenderTargets(IGraphicsApi api, string colorTargetName, string depthTargetName,
		uint width, uint height, bool hdr = false)
	{
		RenderColorFormat = hdr ? TextureObjectFormat.R16G16B16A16Float : TextureObjectFormat.R8G8B8A8UNorm;

		ColorTarget = api.CreateRenderTarget(new TextureInfo
		{
			name = colorTargetName,
			width = width,
			height = height,
			format = TextureObjectFormat.R8G8B8A8UNorm,
		});

		DepthTarget = api.CreateRenderTarget(new TextureInfo
		{
			name = depthTargetName,
			width = width,
			height = height,
			format = TextureObjectFormat.D32Float,
		});

		// Format and size must match the source: CopyTexture does not convert.
		SceneCopyTarget = api.CreateRenderTarget(new TextureInfo
		{
			name = colorTargetName + " Scene Copy",
			width = width,
			height = height,
			format = RenderColorFormat,
		});

		if (hdr)
		{
			HdrColorTarget = api.CreateRenderTarget(new TextureInfo
			{
				name = colorTargetName + " HDR",
				width = width,
				height = height,
				format = RenderColorFormat,
			});
		}

		// Unconditional in HDR mode: MRT formats are baked into every geometry PSO, so gating this
		// on the reflections toggle would force a full environment rebuild.
		if (hdr)
		{
			NormalRoughnessTarget = api.CreateRenderTarget(new TextureInfo
			{
				name = colorTargetName + " NormalRough",
				width = width,
				height = height,
				format = TextureObjectFormat.R16G16B16A16Float,
			});

			EnvFactorTarget = api.CreateRenderTarget(new TextureInfo
			{
				name = colorTargetName + " EnvFactor",
				width = width,
				height = height,
				format = TextureObjectFormat.R16G16B16A16Float,
			});
		}
	}

	public void Release()
	{
		ColorTarget.Release();
		DepthTarget.Release();
		SceneCopyTarget.Release();
		HdrColorTarget?.Release();
		NormalRoughnessTarget?.Release();
		EnvFactorTarget?.Release();
	}
}
