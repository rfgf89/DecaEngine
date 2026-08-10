using System.Numerics;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Core;

namespace DecaEngine.Core;

/// <summary>Техника экранной амбиентной окклюзии AO-пасса превью. Выбирает пиксельный шейдер в
/// <see cref="SsaoPassResources"/> (SsaoCommon.hlsl против GtaoCommon.hlsl) - весь остальной
/// конвейер (таргет, композит, инлайн-дроу в <see cref="ForwardPass"/>) общий для обеих техник.</summary>
public enum AmbientOcclusionMode
{
	/// <summary>Классический спиральный SSAO - счёт заслоняющих тапов (SsaoCommon.hlsl).</summary>
	Ssao,

	/// <summary>Ground Truth AO - горизонты по срезам + аналитический интеграл косинус-взвешенной
	/// видимости (GtaoCommon.hlsl). Меньше серого налёта на плоскостях, чуть дороже.</summary>
	Gtao,
}

/// <summary>Owns the GPU resources for the SSAO post-process: the AO render target plus the two
/// fullscreen materials (depth -&gt; occlusion estimate, then multiplicative composite back into the
/// color target). Created once by <see cref="GraphicsPipelineSimple"/> when SSAO is enabled; drawn
/// inline by <see cref="ForwardPass"/> between the opaque and transmissive draws - see
/// <see cref="WriteInlineCommands"/>.</summary>
public sealed class SsaoPassResources : IReleaseObject
{
	public IRenderTarget AoTarget { get; }
	internal IMaterialObject AoMaterial { get; }
	internal IMaterialObject CompositeMaterial { get; }

	public SsaoPassResources(IGraphicsApi graphicsApi, IBatchRenderer batchRenderer, string colorTargetName,
		uint width, uint height, IGpuTexture depthTarget, IGpuTexture sceneCopyTarget, uint msaaSamples,
		AmbientOcclusionMode aoMode = AmbientOcclusionMode.Ssao)
	{
		var msaaDepth = msaaSamples > 1;
		AoTarget = graphicsApi.CreateRenderTarget(new TextureInfo
		{
			name = colorTargetName + " SSAO",
			width = width,
			height = height,
			format = TextureObjectFormat.R8G8B8A8UNorm,
		});

		// У КАЖДОГО материала свой экземпляр VS: DiligentMaterial.Release() освобождает свои
		// шейдеры, и шареный между материалами экземпляр при пересоздании окружения
		// освобождался бы дважды (AV в ComObject.Release - см. историю с RecreateEnvironment).
		var aoVs = graphicsApi.CreateShader("SSAO Fullscreen VS", "EditorAssets/shader", "SkyBackgroundVS.hlsl", ShaderObjectType.Vertex);
		var compositeVs = graphicsApi.CreateShader("SSAO Composite Fullscreen VS", "EditorAssets/shader", "SkyBackgroundVS.hlsl", ShaderObjectType.Vertex);

		// PSO без депта и без MSAA: AO и композит всегда считаются в 1x (MSAA-депт читается
		// через Texture2DMS.Load, см. SsaoMsaaPS.hlsl).
		var postProcessState = graphicsApi.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "SSAO PostProcess PSO",
			RenderTargetFormats = [TextureObjectFormat.R8G8B8A8UNorm],
			DepthStencilFormat = TextureObjectFormat.Unknown,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = false },
			InputLayout = [],
		});

		// Техника (SSAO/GTAO) - это только другой пиксельный шейдер оценки AO: оба пишут grayscale
		// в тот же таргет и композитятся тем же SsaoCompositePS.hlsl.
		var aoShaderFile = aoMode == AmbientOcclusionMode.Gtao
			? (msaaDepth ? "GtaoMsaaPS.hlsl" : "GtaoPS.hlsl")
			: (msaaDepth ? "SsaoMsaaPS.hlsl" : "SsaoPS.hlsl");
		var aoPs = graphicsApi.CreateShader("SSAO PS", "EditorAssets/shader", aoShaderFile, ShaderObjectType.Pixel);
		AoMaterial = graphicsApi.CreateMaterial("SSAO Material");
		AoMaterial.SetShader(aoVs, aoPs);
		AoMaterial.SetState(postProcessState);
		batchRenderer.BindViewConstants(AoMaterial);
		AoMaterial.SetTexture("_DepthTex", depthTarget);

		// Композит рисует ВНУТРИ ForwardPass в текущий render-таргет геометрии (при MSAA -
		// мультисемпловый), поэтому его PSO обязан совпадать по SampleCount; фуллскрин-треугольник
		// пишет во все сэмплы одно значение. AO-оценка выше остаётся 1x (AoTarget не MSAA).
		var compositeState = graphicsApi.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "SSAO Composite PSO",
			RenderTargetFormats = [TextureObjectFormat.R8G8B8A8UNorm],
			DepthStencilFormat = TextureObjectFormat.Unknown,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = false },
			InputLayout = [],
			SampleCount = msaaSamples,
		});

		var compositePs = graphicsApi.CreateShader("SSAO Composite PS", "EditorAssets/shader", "SsaoCompositePS.hlsl", ShaderObjectType.Pixel);
		CompositeMaterial = graphicsApi.CreateMaterial("SSAO Composite Material");
		CompositeMaterial.SetShader(compositeVs, compositePs);
		CompositeMaterial.SetState(compositeState);
		batchRenderer.BindViewConstants(CompositeMaterial);

		var postProcessSampler = graphicsApi.CreateSampler(
			name: "SSAO Sampler",
			filter: TextureFilter.Linear,
			address: TextureAddress.Clamp,
			comparisonFunction: CompFunction.Always,
			border: Vector4.Zero);
		CompositeMaterial.SetTexture("_SceneTex", sceneCopyTarget);
		CompositeMaterial.SetImmutableSampler("_SceneTex", postProcessSampler);
		CompositeMaterial.SetTexture("_AoTex", AoTarget);
		CompositeMaterial.SetImmutableSampler("_AoTex", postProcessSampler);

		// Иначе cbuffer остался бы с мусором до первого пуша (кадрирование случается только после
		// загрузки модели, а AO-пасс рисует с первого кадра).
		SetWorldRange(0f);
	}

	/// <summary>Layout of the "AoConstants" cbuffer в SsaoCommon.hlsl/GtaoCommon.hlsl - ровно 16
	/// байт (SetConstant грузит размер структуры, округлённый вверх до 16).</summary>
	private struct AoConstantsData
	{
		public float WorldRange;
		public float Pad0, Pad1, Pad2;
	}

	/// <summary>Мировой радиус влияния AO. Пушится после кадрирования модели как доля её
	/// габаритного радиуса (см. ModelPreviewViewport.FrameAll) - с ним контактная тень не
	/// схлопывается при приближении камеры. 0 = легаси-поведение (радиус в долях экрана,
	/// falloff в долях глубины точки - см. SsaoCommon.hlsl/GtaoCommon.hlsl).</summary>
	public void SetWorldRange(float worldRange)
	{
		var data = new AoConstantsData { WorldRange = worldRange };
		AoMaterial.SetConstant("AoConstants", ref data);
	}

	/// <summary>Перепривязывает ресайзабельные таргеты ПОСЛЕ Resize - Resize пересоздаёт нативные
	/// текстуры, и SRB иначе держали бы уничтоженные (см. ModelPreviewViewport.ResizeTargets).</summary>
	public void RebindTargets(IGpuTexture depthTarget, IGpuTexture sceneCopyTarget)
	{
		AoMaterial.SetTexture("_DepthTex", depthTarget);
		CompositeMaterial.SetTexture("_SceneTex", sceneCopyTarget);
		CompositeMaterial.SetTexture("_AoTex", AoTarget);
	}

	/// <summary>AO-оценка + мультипликативный композит, инлайн внутри <see cref="ForwardPass"/> -
	/// МЕЖДУ opaque-дроу и transmissive-дроу, а не поверх готового кадра. Так стекло преломляет уже
	/// затенённый фон (после композита ForwardPass пере-снимает scene copy), но само НЕ глушится AO:
	/// экранная окклюзия аппроксимирует заслонённость рассеянного амбиента у поверхности, а свет от
	/// transmissive-поверхности - преломлённый фон плюс френель, к которым она неприменима.
	/// Требования к вызывающему: opaque уже отрисован, снапшот сцены уже скопирован/отрезолвлен в
	/// sceneCopyTarget (композит читает его как _SceneTex), render-таргеты отвязаны.</summary>
	internal void WriteInlineCommands(ICommandBuffer cmd, IGpuTexture renderColor, IGpuTexture renderDepth,
		Ref<Vector2> viewPortRef)
	{
		// Именно DepthRead, а не ShaderResource: SRV депт-текстуры на Vulkan биндится с лейаутом
		// DEPTH_STENCIL_READ_ONLY_OPTIMAL (VUID-VkDescriptorImageInfo-imageLayout-00344). Обратно в
		// DepthWrite депт вернёт SetRenderTarget transmissive-дроу (см. DiligentCommandBuffer).
		cmd.TransitionResource(renderDepth, ResourceState.DepthRead);

		cmd.SetRenderTarget(AoTarget, null);
		cmd.SetViewport(viewPortRef);
		cmd.SetPipelineState(AoMaterial);
		cmd.CommitShaderResources(AoMaterial);
		cmd.Draw(3);

		cmd.TransitionResource(AoTarget, ResourceState.ShaderResource);

		// Композит пишет в render-таргет геометрии (при MSAA - мультисемпловый, см. SampleCount
		// его PSO) без депта - фуллскрин-треугольнику депт-тест не нужен.
		cmd.SetRenderTarget(renderColor, null);
		cmd.SetViewport(viewPortRef);
		cmd.SetPipelineState(CompositeMaterial);
		cmd.CommitShaderResources(CompositeMaterial);
		cmd.Draw(3);
	}

	public void Release()
	{
		AoTarget.Release();
		AoMaterial.Release();
		CompositeMaterial.Release();
	}
}

