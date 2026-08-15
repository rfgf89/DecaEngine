using System;
using System.Numerics;
using System.Runtime.InteropServices;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Core;

namespace DecaEngine.Core;

/// <summary>Ресурсы встроенного темпорального апскейлера (TAAU, см. TemporalUpscalePS.hlsl) -
/// первый бэкенд слота апскейлера конвейера. Владеет display-размерными <see cref="OutputTarget"/>
/// (его читает тонемап вместо HDR-кадра) и <see cref="_historyTarget"/> (копия результата для
/// следующего кадра). Создаётся лениво <see cref="GraphicsPipelineSimple"/> при включении
/// <see cref="PipelineFeatures.TemporalUpscale"/> - и только вместе с ресурсами векторов движения:
/// без них аккумулятору нечем репроецировать историю.
///
/// Контракт входов - ровно тот, что у FSR/DLSS: HDR-кадр сцены в рендер-разрешении, буфер векторов,
/// джиттер текущего кадра и пара разрешений. Когда появится нативный бэкенд, он встанет на это же
/// место графа с этими же входами, а этот класс останется программным фолбэком.</summary>
public sealed unsafe class TemporalUpscalePassResources : IReleaseObject
{
	/// <summary>Дефолтный вес текущего кадра в экспоненциальном аккумуляторе. 0.1 - классика TAA:
	/// история сходится за ~2 прохода 16-фазного джиттера, а шлейфы (то, что не отсёк clamp)
	/// гаснут за ~10 кадров. Живая ручка - см. <see cref="SetBlendAlpha"/>.</summary>
	public const float DefaultBlendAlpha = 0.1f;

	private float _blendAlpha = DefaultBlendAlpha;

	/// <summary>Вес текущего кадра (0.02..0.5): меньше - стабильнее и резче на статике, но дольше
	/// сходится и заметнее шлейфы; больше - отзывчивее, но дрожание джиттера глушится слабее.
	/// Живая ручка: значение уезжает в кбуфер очередным SetFrameParams (каждый Execute).</summary>
	public void SetBlendAlpha(float alpha)
	{
		_blendAlpha = Math.Clamp(alpha, 0.02f, 0.5f);
	}

	internal IMaterialObject Material { get; }

	/// <summary>Display-разрешение, RGBA16F - аккумулированный кадр, вход тонемапа при включённом
	/// апскейле.</summary>
	public IRenderTarget OutputTarget { get; }

	private readonly IRenderTarget _historyTarget;
	internal IRenderTarget HistoryTarget => _historyTarget;

	// Кбуфер со своей unmanaged-памятью + UpdateBuffer из пасса - тот же приём и та же причина,
	// что у MotionVectorPassResources: джиттер меняется каждый кадр, а SetConstant трогал бы SRB
	// под кадром в полёте.
	private readonly IBufferHandle _constantBuffer;
	private readonly TemporalUpscaleConstantsData* _constants;

	/// <summary>Есть ли в <see cref="_historyTarget"/> хоть один осмысленный кадр. До первого -
	/// шейдер берёт текущий кадр целиком (см. TuFrame.w), иначе аккумулятор смешивал бы с
	/// неинициализированной памятью.</summary>
	private bool _hasHistory;

	public TemporalUpscalePassResources(IGraphicsApi graphicsApi, IBatchRenderer batchRenderer,
		string colorTargetName, IGpuTexture sceneHdrTarget, IGpuTexture motionTarget,
		uint renderWidth, uint renderHeight, uint displayWidth, uint displayHeight)
	{
		// Свой экземпляр VS - см. комментарий в FogPassResources (шареный шейдер освобождался бы
		// дважды при пересоздании окружения).
		var vs = graphicsApi.CreateShader("Temporal Upscale VS", "EditorAssets/shader",
			"SkyBackgroundVS.hlsl", ShaderObjectType.Vertex);
		var ps = graphicsApi.CreateShader("Temporal Upscale PS", "EditorAssets/shader",
			"TemporalUpscalePS.hlsl", ShaderObjectType.Pixel);

		OutputTarget = graphicsApi.CreateRenderTarget(new TextureInfo
		{
			name = colorTargetName + " Upscaled",
			width = displayWidth,
			height = displayHeight,
			format = TextureObjectFormat.R16G16B16A16Float,
		});

		_historyTarget = graphicsApi.CreateRenderTarget(new TextureInfo
		{
			name = colorTargetName + " Upscale History",
			width = displayWidth,
			height = displayHeight,
			format = TextureObjectFormat.R16G16B16A16Float,
		});

		var state = graphicsApi.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "Temporal Upscale PSO",
			RenderTargetFormats = [TextureObjectFormat.R16G16B16A16Float],
			DepthStencilFormat = TextureObjectFormat.Unknown,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = false },
			InputLayout = [],
		});

		var linearClamp = graphicsApi.CreateSampler(
			name: "Temporal Upscale Sampler",
			filter: TextureFilter.Linear,
			address: TextureAddress.Clamp,
			comparisonFunction: CompFunction.Always,
			border: Vector4.Zero);

		Material = graphicsApi.CreateMaterial("Temporal Upscale Material");
		Material.SetShader(vs, ps);
		Material.SetState(state);
		batchRenderer.BindViewConstants(Material);
		Material.SetTexture("_SceneTex", sceneHdrTarget);
		Material.SetImmutableSampler("_SceneTex", linearClamp);
		Material.SetTexture("_HistoryTex", _historyTarget);
		Material.SetImmutableSampler("_HistoryTex", linearClamp);

		// Векторы - только Load ближайшего пикселя, сэмплер не нужен (см. TemporalUpscalePS.hlsl).
		Material.SetTexture("_MotionTex", motionTarget);

		_constantBuffer = graphicsApi.CreateBuffer(new BufferInfo
		{
			name = "TemporalUpscaleConstants",
			dynamic = false,
			type = BufferHandleType.Constant,
			access = HandleAccess.Pixel,
			sizeInBytes = (uint)sizeof(TemporalUpscaleConstantsData),
		});

		Material.SetBuffer("TemporalUpscaleConstants", _constantBuffer, HandleAccess.Pixel);
		_constants = (TemporalUpscaleConstantsData*)NativeMemory.AllocZeroed(
			1, (nuint)sizeof(TemporalUpscaleConstantsData));

		SetSizes(renderWidth, renderHeight);
		SetFrameParams(Vector2.Zero);
	}

	/// <summary>Layout кбуфера "TemporalUpscaleConstants" в TemporalUpscalePS.hlsl - два float4.</summary>
	private struct TemporalUpscaleConstantsData
	{
		/// <summary>xy - рендер-размер, zw - 1/рендер-размер.</summary>
		public Vector4 Render;

		/// <summary>xy - джиттер кадра в рендер-пикселях (y вниз), z - alpha смешивания, w - есть
		/// ли история.</summary>
		public Vector4 Frame;
	}

	private void SetSizes(uint renderWidth, uint renderHeight)
	{
		_constants->Render = new Vector4(renderWidth, renderHeight,
			1f / Math.Max(1u, renderWidth), 1f / Math.Max(1u, renderHeight));
	}

	/// <summary>Покадровые параметры - зовётся из <see cref="GraphicsPipelineSimple.Execute"/> ПОСЛЕ
	/// наложения джиттера: шейдер обязан вычесть смещение ИМЕННО этого кадра. Здесь же взводится
	/// флаг истории: к следующему Execute в history-таргете уже лежит результат текущего.</summary>
	public void SetFrameParams(Vector2 jitterPixels)
	{
		_constants->Frame = new Vector4(jitterPixels.X, jitterPixels.Y, _blendAlpha, _hasHistory ? 1f : 0f);
		_hasHistory = true;
	}

	/// <summary>Сбрасывает аккумулятор - следующий кадр выйдет без истории. Обязателен там же, где
	/// рвётся история векторов (ресайз, смена сцены): старый кадр после ресайза - другой вьюпорт,
	/// и смешивание с ним дало бы мусор на весь период схождения.</summary>
	public void ResetHistory()
	{
		_hasHistory = false;
	}

	/// <summary>Ресайз display-таргетов и новый рендер-размер; история при этом рвётся.</summary>
	public void Resize(uint renderWidth, uint renderHeight, uint displayWidth, uint displayHeight)
	{
		OutputTarget.Resize(new Vector2(displayWidth, displayHeight));
		_historyTarget.Resize(new Vector2(displayWidth, displayHeight));
		SetSizes(renderWidth, renderHeight);
		ResetHistory();
	}

	/// <summary>Перепривязка ПОСЛЕ Resize входов - те пересоздают нативные текстуры, и SRB иначе
	/// держал бы уничтоженные (см. ModelPreviewViewport.ResizeTargets). History перепривязывается
	/// внутри собственного Resize по той же причине.</summary>
	public void RebindTargets(IGpuTexture sceneHdrTarget, IGpuTexture motionTarget,
		uint renderWidth, uint renderHeight, uint displayWidth, uint displayHeight)
	{
		Material.SetTexture("_SceneTex", sceneHdrTarget);
		Material.SetTexture("_MotionTex", motionTarget);
		Resize(renderWidth, renderHeight, displayWidth, displayHeight);
		Material.SetTexture("_HistoryTex", _historyTarget);
	}

	internal void WriteConstants(ICommandBuffer cmd) => cmd.UpdateBuffer(_constantBuffer, 0, _constants);

	public void Release()
	{
		Material.Release();
		OutputTarget.Release();
		_historyTarget.Release();
		_constantBuffer.Release();
		NativeMemory.Free(_constants);
	}
}

/// <summary>
/// Слот апскейлера рендер-графа: собирает display-кадр из рендер-разрешения по истории (см.
/// TemporalUpscalePS.hlsl).
///
/// Место в кадре: ПОСЛЕ всей сценовой пост-обработки (туман/god rays/блум уже вложены в HDR-кадр -
/// апскейлер должен видеть то же, что увидел бы глаз) и ПЕРЕД тонемапом, который при включённом
/// апскейле читает <see cref="TemporalUpscalePassResources.OutputTarget"/> вместо HDR-кадра и
/// работает 1:1. Аккумулировать надо ЛИНЕЙНЫЙ свет: смешивание после кривой тонемапа занижало бы
/// яркие субпиксельные детали.
///
/// В конце пасса результат копируется в history-таргет - источник репроджекции следующего кадра.
/// Копия вместо пинг-понга нарочно: тонемап держит ОДИН фиксированный вход, и пинг-понг требовал
/// бы либо два его материала (как у адаптации), либо перепривязку SRB каждый кадр.
/// </summary>
public sealed class TemporalUpscalePass : RenderGraphPass<TemporalUpscalePass.PassData>
{
	public override string Name => "Temporal Upscale Pass";

	private readonly TemporalUpscalePassResources _resources;
	private readonly IGpuTexture _sceneHdrTarget;
	private readonly IGpuTexture _motionTarget;
	private readonly Ref<Vector2> _viewPortRef;

	public struct PassData
	{
	}

	public TemporalUpscalePass(TemporalUpscalePassResources resources, IGpuTexture sceneHdrTarget,
		IGpuTexture motionTarget, Ref<Vector2> viewPortRef)
	{
		_resources = resources;
		_sceneHdrTarget = sceneHdrTarget;
		_motionTarget = motionTarget;
		_viewPortRef = viewPortRef;
	}

	/// <summary>Объявляет графу таргеты пасса - см. <see cref="ForwardPass.Setup"/>. History и
	/// читается (репроджекция), и пишется (копия результата) - тот же паттерн, что у scene-copy.</summary>
	public override PassData Setup(IRenderGraphBuilder builder)
	{
		builder.ReadTarget(builder.ImportTexture(_sceneHdrTarget));
		builder.ReadTarget(builder.ImportTexture(_motionTarget));
		var history = builder.ImportTexture(_resources.HistoryTarget);
		builder.ReadTarget(history);
		builder.WriteTarget(history);
		builder.WriteTarget(builder.ImportTexture(_resources.OutputTarget));
		return default;
	}

	public override void WriteCommands(in PassData value, in IRenderGraphContext context)
	{
		var cmd = context.cmd;

		_resources.WriteConstants(cmd);

		cmd.SetRenderTarget(null, null);
		cmd.TransitionResource(_sceneHdrTarget, ResourceState.ShaderResource);
		cmd.TransitionResource(_motionTarget, ResourceState.ShaderResource);
		cmd.TransitionResource(_resources.HistoryTarget, ResourceState.ShaderResource);

		// Вьюпорт - ОТОБРАЖАЕМЫЙ: пасс и есть переход рендер-разрешения в display.
		cmd.SetRenderTarget(_resources.OutputTarget, null);
		cmd.SetViewport(_viewPortRef);
		cmd.SetPipelineState(_resources.Material);
		cmd.CommitShaderResources(_resources.Material);
		cmd.Draw(3);

		// Результат - в историю следующего кадра (см. ForwardPass: тот же приём со scene-copy).
		cmd.SetRenderTarget(null, null);
		cmd.CopyTexture(_resources.OutputTarget, _resources.HistoryTarget);
		cmd.TransitionResource(_resources.HistoryTarget, ResourceState.ShaderResource);
	}
}
