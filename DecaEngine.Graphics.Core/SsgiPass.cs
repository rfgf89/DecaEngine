using System.Numerics;
using DecaEngine.Graphics;
using DecaEngine.Core;

namespace DecaEngine.Graphics;

/// <summary>Owns the GPU resources for the SSGI (screen-space global illumination) post-process:
/// the GI render target plus the two fullscreen materials (depth + scene copy -&gt; one-bounce
/// light estimate, then additive composite back into the color target). Created once by
/// <see cref="GraphicsPipelineSimple"/> when SSGI is enabled - <see cref="SsgiPass"/> itself is
/// rebuilt every frame like the other render-graph passes, but only ever references the resources
/// held here. Same ownership pattern as <see cref="SsaoPassResources"/>.</summary>
public sealed class SsgiPassResources : IReleaseObject
{
	public IRenderTarget GiTarget { get; }
	internal IMaterialObject GiMaterial { get; }
	internal IMaterialObject CompositeMaterial { get; }

	/// <param name="colorFormat">Формат цветового таргета (RGBA16F в HDR-режиме превью): его берут и
	/// PSO композита, и сам GI-таргет - bounce собирается из линейного HDR-кадра, и в RGBA8 яркие
	/// участки клипались бы в единицу ещё до композита.</param>
	public SsgiPassResources(IGraphicsApi graphicsApi, IBatchRenderer batchRenderer, string colorTargetName,
		uint width, uint height, IGpuTexture depthTarget, IGpuTexture sceneCopyTarget,
		TextureObjectFormat colorFormat = TextureObjectFormat.R8G8B8A8UNorm)
	{
		GiTarget = graphicsApi.CreateRenderTarget(new TextureInfo
		{
			name = colorTargetName + " SSGI",
			width = width,
			height = height,
			format = colorFormat,
		});

		// У КАЖДОГО материала свой экземпляр VS - см. комментарий в SsaoPassResources (двойной
		// Release шареного шейдера при пересоздании окружения).
		var giVs = graphicsApi.CreateShader("SSGI Fullscreen VS", "EditorAssets/shader", "SkyBackgroundVS.hlsl", ShaderObjectType.Vertex);
		var compositeVs = graphicsApi.CreateShader("SSGI Composite Fullscreen VS", "EditorAssets/shader", "SkyBackgroundVS.hlsl", ShaderObjectType.Vertex);

		// PSO без депта: GI и композит - фуллскрин-треугольники по готовой глубине.
		var postProcessState = graphicsApi.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "SSGI PostProcess PSO",
			RenderTargetFormats = [colorFormat],
			DepthStencilFormat = TextureObjectFormat.Unknown,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = false },
			InputLayout = [],
		});

		var giPs = graphicsApi.CreateShader("SSGI PS", "EditorAssets/shader",
			"SsgiPS.hlsl", ShaderObjectType.Pixel);
		GiMaterial = graphicsApi.CreateMaterial("SSGI Material");
		GiMaterial.SetShader(giVs, giPs);
		GiMaterial.SetState(postProcessState);
		batchRenderer.BindViewConstants(GiMaterial);
		GiMaterial.SetTexture("_DepthTex", depthTarget);
		// Источник bounce-света - копия кадра (Load по пикселю, сэмплер не нужен).
		GiMaterial.SetTexture("_SceneTex", sceneCopyTarget);

		// Композит билатеральный (размытие bounce по глубине, см. SsgiCompositeCommon.hlsl),
		// поэтому у него та же пара обёрток под депт, что у самой оценки GI.
		var compositePs = graphicsApi.CreateShader("SSGI Composite PS", "EditorAssets/shader",
			"SsgiCompositePS.hlsl", ShaderObjectType.Pixel);
		CompositeMaterial = graphicsApi.CreateMaterial("SSGI Composite Material");
		CompositeMaterial.SetShader(compositeVs, compositePs);
		CompositeMaterial.SetState(postProcessState);
		batchRenderer.BindViewConstants(CompositeMaterial);

		var postProcessSampler = graphicsApi.CreateSampler(
			name: "SSGI Sampler",
			filter: TextureFilter.Linear,
			address: TextureAddress.Clamp,
			comparisonFunction: CompFunction.Always,
			border: Vector4.Zero);
		CompositeMaterial.SetTexture("_SceneTex", sceneCopyTarget);
		CompositeMaterial.SetImmutableSampler("_SceneTex", postProcessSampler);
		CompositeMaterial.SetTexture("_GiTex", GiTarget);
		CompositeMaterial.SetImmutableSampler("_GiTex", postProcessSampler);
		CompositeMaterial.SetTexture("_DepthTex", depthTarget);

		// Иначе cbuffer остался бы с мусором до первого пуша (кадрирование случается только
		// после загрузки модели, а GI-пасс рисует с первого кадра) - см. SsaoPassResources.
		SetWorldRange(0f);
		SetCompositeParams(DefaultBlurRadius, false);
	}

	/// <summary>Дефолты ручек SSGI - ими же инициализируется кбуфер до первого пуша из окна
	/// Graphics и они же служат стартовыми значениями в <see cref="EditorSettings"/>-аналогах.</summary>
	public const float DefaultIntensity = 1.0f;
	public const int DefaultSampleCount = 16;
	public const float DefaultMaxLuminance = 4f;
	public const float DefaultSaturation = 0.8f;
	public const int DefaultBlurRadius = 2;

	/// <summary>Потолки, зашитые в шейдеры (GiMaxTaps в SsgiCommon.hlsl, GiMaxBlurRadius в
	/// SsgiCompositeCommon.hlsl): границы циклов там статические, и значение выше просто
	/// клампится - выносим их сюда, чтобы UI не предлагал недостижимое.</summary>
	public const int MaxSampleCount = 32;
	public const int MaxBlurRadius = 3;

	/// <summary>Layout of the "GiConstants" cbuffer в SsgiCommon.hlsl - 32 байта
	/// (SetConstant грузит размер структуры, округлённый вверх до 16).</summary>
	private struct GiConstantsData
	{
		public float WorldRange;
		public float Intensity;
		public float SampleCount;
		public float MaxLuminance;
		public float Saturation;
		public float Pad0, Pad1, Pad2;
	}

	/// <summary>Layout of the "GiComposite" cbuffer в SsgiCompositeCommon.hlsl - ровно 16 байт.</summary>
	private struct GiCompositeData
	{
		public float BlurRadius;
		public float DebugView;
		public float Pad0, Pad1;
	}

	private float _worldRange;
	private float _intensity = DefaultIntensity;
	private float _sampleCount = DefaultSampleCount;
	private float _maxLuminance = DefaultMaxLuminance;
	private float _saturation = DefaultSaturation;

	/// <summary>Мировой радиус сбора GI. Пушится после кадрирования модели как доля её
	/// габаритного радиуса (см. ModelPreviewViewport.FrameAll) - шире AO-шного, bounce тянется
	/// дальше контактной тени. 0 = легаси-поведение (радиус в долях экрана, falloff в долях
	/// глубины точки - см. SsgiCommon.hlsl).</summary>
	public void SetWorldRange(float worldRange)
	{
		_worldRange = worldRange;
		PushGiConstants();
	}

	/// <summary>Живые ручки оценки GI - окно Graphics (см. GraphicsSettingsWindow).
	/// Радиус сохраняется прежним.</summary>
	/// <param name="maxLuminance">Потолок яркости ОДНОГО тапа (firefly clamp): в HDR-кадре
	/// солнечное пятно рядом с тенью даёт тап в десятки единиц, и один такой из восьми превращал
	/// пасс в цветной снег. 0 = без ограничения.</param>
	public void SetParams(float intensity, int sampleCount, float maxLuminance, float saturation)
	{
		_intensity = intensity;
		_sampleCount = Math.Clamp(sampleCount, 4, MaxSampleCount);
		_maxLuminance = maxLuminance;
		_saturation = saturation;
		PushGiConstants();
	}

	/// <summary>Живые ручки композита: радиус билатерального размытия bounce и отладочный вид
	/// (вывести один только GI вместо кадра) - см. SsgiCompositeCommon.hlsl.</summary>
	public void SetCompositeParams(int blurRadius, bool debugView)
	{
		var data = new GiCompositeData
		{
			BlurRadius = Math.Clamp(blurRadius, 0, MaxBlurRadius),
			DebugView = debugView ? 1f : 0f,
		};
		CompositeMaterial.SetConstant("GiComposite", ref data);
	}

	private void PushGiConstants()
	{
		var data = new GiConstantsData
		{
			WorldRange = _worldRange,
			Intensity = _intensity,
			SampleCount = _sampleCount,
			MaxLuminance = _maxLuminance,
			Saturation = _saturation,
		};
		GiMaterial.SetConstant("GiConstants", ref data);
	}

	/// <summary>Перепривязывает ресайзабельные таргеты ПОСЛЕ Resize - Resize пересоздаёт нативные
	/// текстуры, и SRB иначе держали бы уничтоженные (см. ModelPreviewViewport.ResizeTargets).</summary>
	public void RebindTargets(IGpuTexture depthTarget, IGpuTexture sceneCopyTarget)
	{
		GiMaterial.SetTexture("_DepthTex", depthTarget);
		GiMaterial.SetTexture("_SceneTex", sceneCopyTarget);
		CompositeMaterial.SetTexture("_SceneTex", sceneCopyTarget);
		CompositeMaterial.SetTexture("_GiTex", GiTarget);
		CompositeMaterial.SetTexture("_DepthTex", depthTarget);
	}

	public void Release()
	{
		GiTarget.Release();
		GiMaterial.Release();
		CompositeMaterial.Release();
	}
}

/// <summary>
/// Render-graph pass that gathers one bounce of indirect light from the already-rendered frame
/// (screen-space GI / color bleeding) and composites it additively back into the color target.
/// Runs after <see cref="ForwardPass"/> (which draws SSAO inline between its opaque and
/// transmissive draws - see <see cref="SsaoPassResources.WriteInlineCommands"/>), so the frame it
/// samples already contains direct lighting and contact shadows. No-op
/// (not added to the graph at all) when SSGI is disabled - see <see cref="GraphicsPipelineSimple"/>.
/// </summary>
public sealed class SsgiPass : RenderGraphPass<SsgiPass.PassData>
{
	public override string Name => "SSGI Pass";

	private readonly SsgiPassResources _resources;
	private readonly IGpuTexture _colorTarget;
	private readonly IGpuTexture _sceneCopy;
	private readonly IGpuTexture _renderDepth;
	private readonly Ref<Vector2> _viewPortRef;

	public struct PassData
	{
	}

	public SsgiPass(SsgiPassResources resources, IGpuTexture colorTarget, IGpuTexture sceneCopy,
		IGpuTexture renderDepth, Ref<Vector2> viewPortRef)
	{
		_resources = resources;
		_colorTarget = colorTarget;
		_sceneCopy = sceneCopy;
		_renderDepth = renderDepth;
		_viewPortRef = viewPortRef;
	}

	/// <summary>Объявляет графу таргеты пасса - см. <see cref="ForwardPass.Setup"/>.</summary>
	public override PassData Setup(IRenderGraphBuilder builder)
	{
		// Кадр и читается (источник копии и bounce), и пишется (композит).
		var color = builder.ImportTexture(_colorTarget);
		builder.ReadTarget(color);
		builder.WriteTarget(color);

		// Копия кадра переснимается ЭТИМ пассом - для него она и вход, и выход.
		var sceneCopy = builder.ImportTexture(_sceneCopy);
		builder.WriteTarget(sceneCopy);
		builder.ReadTarget(sceneCopy);

		builder.ReadTarget(builder.ImportTexture(_renderDepth));

		var gi = builder.ImportTexture(_resources.GiTarget);
		builder.WriteTarget(gi);
		builder.ReadTarget(gi);

		return default;
	}

	public override void WriteCommands(in PassData value, in IRenderGraphContext context)
	{
		var cmd = context.cmd;

		// Именно DepthRead, а не ShaderResource - см. комментарий в SsaoPassResources.
		// WriteInlineCommands (лейаут DEPTH_STENCIL_READ_ONLY_OPTIMAL на Vulkan).
		cmd.TransitionResource(_renderDepth, ResourceState.DepthRead);

		// В отличие от AO, копия кадра нужна ДО оценочного пасса: GI-шейдер читает цвет
		// сцены как источник bounce-света. ForwardPass успел дорисовать transmissive поверх
		// AO-композита - копия перезатирает refraction-снимок уже финальным кадром.
		cmd.SetRenderTarget(null, null);
		cmd.CopyTexture(_colorTarget, _sceneCopy);
		cmd.TransitionResource(_sceneCopy, ResourceState.ShaderResource);

		cmd.SetRenderTarget(_resources.GiTarget, null);
		cmd.SetViewport(_viewPortRef);
		cmd.SetPipelineState(_resources.GiMaterial);
		cmd.CommitShaderResources(_resources.GiMaterial);
		cmd.Draw(3);

		cmd.SetRenderTarget(null, null);
		cmd.TransitionResource(_resources.GiTarget, ResourceState.ShaderResource);

		cmd.SetRenderTarget(_colorTarget, null);
		cmd.SetViewport(_viewPortRef);
		cmd.SetPipelineState(_resources.CompositeMaterial);
		cmd.CommitShaderResources(_resources.CompositeMaterial);
		cmd.Draw(3);
	}
}
