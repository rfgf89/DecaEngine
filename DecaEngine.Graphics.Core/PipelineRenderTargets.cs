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

	/// <summary>Non-null только в HDR-режиме (<c>hdr: true</c>): линейный RGBA16F-таргет, в который
	/// рисуется вся геометрия и пост-обработка, тогда как <see cref="ColorTarget"/> остаётся
	/// отображаемым RGBA8 и получает кадр уже после тонемапа (см. <see cref="TonemapPassResources"/>).
	/// Отдельный таргет, а не смена формата <see cref="ColorTarget"/>, потому что его сэмплируют
	/// ImGui и readback превью-пробы - они ждут display-space RGBA8.</summary>
	public IRenderTarget? HdrColorTarget { get; }

	/// <summary>Формат таргета, в который реально рисует геометрия (и с которым обязаны совпадать
	/// PSO пост-пассов): RGBA16F в HDR-режиме, иначе RGBA8.</summary>
	public TextureObjectFormat RenderColorFormat { get; }

	/// <summary>Таргет геометрии: HDR-таргет, если конвейер HDR, иначе отображаемый цветовой.
	/// Именно его отдают <see cref="ForwardPass"/>/<see cref="SsgiPass"/>.</summary>
	public IRenderTarget RenderColorTarget => HdrColorTarget ?? ColorTarget;

	/// <summary>Сэмплируемая копия <see cref="ColorTarget"/> после opaque-дроу - источник рефракции
	/// для transmissive-материалов (см. ForwardPass / UnlitInstancedPS.hlsl).</summary>
	public IRenderTarget SceneCopyTarget { get; }

	/// <summary>Мультисемпловая пара таргетов, non-null только когда конструктору передан
	/// <c>msaaSamples</c> &gt; 1 - геометрия рисуется в них и резолвится в <see cref="ColorTarget"/>
	/// (см. ForwardPass).</summary>
	public IRenderTarget? MsaaColorTarget { get; }
	public IRenderTarget? MsaaDepthTarget { get; }

	/// <param name="hdr">Включает HDR-конвейер: добавляется линейный <see cref="HdrColorTarget"/>, а
	/// scene-copy и MSAA-цвет создаются в том же RGBA16F (копия/резолв формат не конвертируют, а
	/// рефракция обязана читать линейную сцену). Нужен для авто-экспозиции - см.
	/// <see cref="EyeAdaptationPassResources"/>.</param>
	public PipelineRenderTargets(IGraphicsApi api, string colorTargetName, string depthTargetName,
		uint width, uint height, uint msaaSamples, bool hdr = false)
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

		// Снимок opaque-сцены для refraction-пасса (см. ForwardPass): transmissive-материалы
		// сэмплируют его как "_SceneColor" - то, что реально находится за стеклом. Формат и
		// размер обязаны совпадать с ColorTarget (CopyTexture не конвертирует).
		SceneCopyTarget = api.CreateRenderTarget(new TextureInfo
		{
			name = colorTargetName + " Scene Copy",
			width = width,
			height = height,
			format = RenderColorFormat,
		});

		// Линейный кадр HDR-конвейера: вся геометрия, небо, AO/GI-композиты и refraction-снимок
		// живут здесь, а ColorTarget получает результат TonemapPass-а (экспозиция + кривая + sRGB).
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

		if (msaaSamples > 1)
		{
			MsaaColorTarget = api.CreateRenderTarget(new TextureInfo
			{
				name = colorTargetName + " MSAA",
				width = width,
				height = height,
				format = RenderColorFormat,
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
		HdrColorTarget?.Release();
		MsaaColorTarget?.Release();
		MsaaDepthTarget?.Release();
	}
}
