using DecaEngine.Graphics.Core;

namespace DecaEngine.Core;

/// <summary>Owns the off-screen color/depth/scene-copy(/MSAA) targets a <see cref="GraphicsPipelineSimple"/>
/// renders into - the off-screen equivalent of a swap chain's back buffer, created once by the
/// pipeline (see <see cref="SsaoPassResources"/>, <see cref="SkyPassResources"/> for the same pattern
/// applied to individual passes).</summary>
public sealed class PipelineRenderTargets : IReleaseObject
{
	public IRenderTarget ColorTarget { get; }
	public IRenderTarget DepthTarget { get; }

	/// <summary>Сэмплируемая копия <see cref="ColorTarget"/> после opaque-дроу - источник рефракции
	/// для transmissive-материалов (см. ForwardPass / UnlitInstancedPS.hlsl).</summary>
	public IRenderTarget SceneCopyTarget { get; }

	/// <summary>Мультисемпловая пара таргетов, non-null только когда конструктору передан
	/// <c>msaaSamples</c> &gt; 1 - геометрия рисуется в них и резолвится в <see cref="ColorTarget"/>
	/// (см. ForwardPass).</summary>
	public IRenderTarget? MsaaColorTarget { get; }
	public IRenderTarget? MsaaDepthTarget { get; }

	public PipelineRenderTargets(IGraphicsApi api, string colorTargetName, string depthTargetName,
		uint width, uint height, uint msaaSamples)
	{
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

		// Снимок opaque-сцены для refraction-пасса (см. ForwardPass): transmissive-материалы
		// сэмплируют его как "_SceneColor" - то, что реально находится за стеклом. Формат и
		// размер обязаны совпадать с ColorTarget (CopyTexture не конвертирует).
		SceneCopyTarget = api.CreateRenderTarget(new TextureInfo
		{
			name = colorTargetName + " Scene Copy",
			width = width,
			height = height,
			format = TextureObjectFormat.R8G8B8A8UNorm,
		});

		if (msaaSamples > 1)
		{
			MsaaColorTarget = api.CreateRenderTarget(new TextureInfo
			{
				name = colorTargetName + " MSAA",
				width = width,
				height = height,
				format = TextureObjectFormat.R8G8B8A8UNorm,
				sampleCount = msaaSamples,
			});

			MsaaDepthTarget = api.CreateRenderTarget(new TextureInfo
			{
				name = depthTargetName + " MSAA",
				width = width,
				height = height,
				format = TextureObjectFormat.D32Float,
				sampleCount = msaaSamples,
			});
		}
	}

	public void Release()
	{
		ColorTarget.Release();
		DepthTarget.Release();
		SceneCopyTarget.Release();
		MsaaColorTarget?.Release();
		MsaaDepthTarget?.Release();
	}
}
