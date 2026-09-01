using System.Numerics;
using DecaEngine.Graphics;
using DecaEngine.Core;

namespace DecaEngine.Graphics;

/// <summary>Owns the GPU resources for the final HDR -&gt; display conversion: exposure from
/// <see cref="EyeAdaptationPassResources"/>, the PBR Neutral curve and the manual sRGB encode
/// (см. TonemapPS.hlsl), из линейного <see cref="PipelineRenderTargets.HdrColorTarget"/> в
/// отображаемый <see cref="PipelineRenderTargets.ColorTarget"/>. Создаётся один раз
/// <see cref="GraphicsPipelineSimple"/> вместе с HDR-конвейером - тот же паттерн владения, что у
/// <see cref="SsaoPassResources"/>. В LDR-режиме пасса нет вовсе, и те же две операции делает сам
/// UnlitInstancedPS.hlsl.</summary>
public sealed class TonemapPassResources : IReleaseObject
{
	internal IMaterialObject Material { get; }

	public TonemapPassResources(IGraphicsApi graphicsApi, IBatchRenderer batchRenderer, IGpuTexture hdrColorTarget,
		IGpuTexture adaptationTarget)
	{
		var vs = graphicsApi.CreateShader("Tonemap Fullscreen VS", "EditorAssets/shader", "SkyBackgroundVS.hlsl", ShaderObjectType.Vertex);
		var ps = graphicsApi.CreateShader("Tonemap PS", "EditorAssets/shader", "TonemapPS.hlsl", ShaderObjectType.Pixel);

		// Таргет тонемапа - ОТОБРАЖАЕМЫЙ RGBA8 (его сэмплируют ImGui и readback превью-пробы), без
		// депта и без MSAA: кадр к этому моменту уже отрезолвлен (см. ForwardPass).
		var state = graphicsApi.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "Tonemap PSO",
			RenderTargetFormats = [TextureObjectFormat.R8G8B8A8UNorm],
			DepthStencilFormat = TextureObjectFormat.Unknown,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = false },
			InputLayout = [],
		});

		// Тонемап - точка апскейла конвейера: при масштабе рендера меньше 1 HDR-кадр меньше
		// отображаемого таргета, и билинейный сэмпл по UV поднимает его до display-разрешения
		// (см. GraphicsPipelineSimple.SetRenderScale). При 1:1 UV попадает ровно в центры текселей,
		// и фильтр вырождается в точное чтение - отдельная ветка не нужна.
		var sampler = graphicsApi.CreateSampler(
			name: "Tonemap Scene Sampler",
			filter: TextureFilter.Linear,
			address: TextureAddress.Clamp,
			comparisonFunction: CompFunction.Always,
			border: Vector4.Zero);

		Material = graphicsApi.CreateMaterial("Tonemap Material");
		Material.SetShader(vs, ps);
		Material.SetState(state);
		batchRenderer.BindViewConstants(Material);
		Material.SetTexture("_SceneTex", hdrColorTarget);
		Material.SetImmutableSampler("_SceneTex", sampler);
		Material.SetTexture("_AdaptTex", adaptationTarget);

		// Иначе cbuffer остался бы с мусором до первого пуша - пасс рисует с первого кадра
		// (см. SsaoPassResources).
		SetParams(0.18f, 0f);
	}

	/// <summary>Layout кбуфера "TonemapConstants" в TonemapPS.hlsl - ровно 16 байт.</summary>
	private struct TonemapConstantsData
	{
		public float Key;
		public float ExposureCompensation;

		/// <summary>&gt;0.5 - копировать кадр как есть (см. <see cref="SetPassthrough"/>).</summary>
		public float Passthrough;

		/// <summary>Режим кривой (см. Tonemap.hlsl): 0 - PBR Neutral, 1 - ACES, 2 - AgX.</summary>
		public float Curve;

		/// <summary>&gt;0.5 - экспозиция по замеренной яркости, иначе ручная (см.
		/// <see cref="SetAutoExposure"/>).</summary>
		public float AutoExposure;

		/// <summary>&gt;0.5 - альфа кадра форсируется в 1 (см. <see cref="SetForceOpaque"/>).</summary>
		public float ForceOpaque;

		public float Pad2;
		public float Pad3;
	}

	private float _key = 0.18f;
	private float _exposureCompensation;
	private bool _passthrough;
	private int _curve;
	private bool _autoExposure = true;
	private bool _forceOpaque;

	/// <summary>Форсировать альфу кадра в 1: нативный апскейлер (FSR) альфу не переносит - его
	/// выход приходит с alpha 0, и композит превью по альфе выкидывал бы весь кадр. Живая ручка,
	/// ставится конвейером вместе с выбором входа тонемапа (см. GraphicsPipelineSimple.RebuildGraph).
	/// Цена: в FSR-режиме превью теряет прозрачный фон - подложка ImGui закрыта кадром целиком.</summary>
	public void SetForceOpaque(bool forceOpaque)
	{
		_forceOpaque = forceOpaque;
		PushConstants();
	}

	/// <summary>Экспонировать кадр по ЗАМЕРЕННОЙ яркости (<see cref="EyeAdaptationPassResources"/>) или
	/// по одной лишь экспокоррекции. Живая ручка - именно ею и работает тумблер авто-экспозиции, см.
	/// <see cref="PipelineFeatures.EyeAdaptation"/>: цепочка замера остаётся в графе в любом случае,
	/// меняется только то, смотрит ли на неё тонемап.</summary>
	public void SetAutoExposure(bool autoExposure)
	{
		_autoExposure = autoExposure;
		PushConstants();
	}

	/// <summary>Те же key value и экспокоррекция, что уходят в <see cref="EyeAdaptationPassResources.SetParams"/>:
	/// адаптация меряет яркость, а приводит к ней кадр уже тонемап.</summary>
	public void SetParams(float key, float exposureCompensation)
	{
		_key = key;
		_exposureCompensation = exposureCompensation;
		PushConstants();
	}

	/// <summary>Отладочные режимы превью (каналы нормалей/UV, AO debug view, probe debug) пишут в
	/// кадр УЖЕ отображаемые значения - экспозиция и кривая исказили бы ровно то, что смотрят как
	/// есть. Живая ручка (см. ModelPreviewViewport.ApplyGraphicsSettings).</summary>
	public void SetPassthrough(bool passthrough)
	{
		_passthrough = passthrough;
		PushConstants();
	}

	/// <summary>Кривая тонмапа (см. Tonemap.hlsl): 0 - PBR Neutral, 1 - ACES, 2 - AgX. Живая ручка -
	/// выбор рантаймный, вариантов шейдера под кривые нет (пересборка всех PSO превью на каждое
	/// движение выпадающего списка того не стоит).</summary>
	public void SetCurve(int curve)
	{
		_curve = curve;
		PushConstants();
	}

	private void PushConstants()
	{
		var data = new TonemapConstantsData
		{
			Key = _key,
			ExposureCompensation = _exposureCompensation,
			Passthrough = _passthrough ? 1f : 0f,
			Curve = _curve,
			AutoExposure = _autoExposure ? 1f : 0f,
			ForceOpaque = _forceOpaque ? 1f : 0f,
		};
		Material.SetConstant("TonemapConstants", ref data, HandleAccess.Pixel);
	}

	/// <summary>Перепривязывает HDR-кадр ПОСЛЕ его Resize - Resize пересоздаёт нативную текстуру, и
	/// SRB иначе держал бы уничтоженную (см. ModelPreviewViewport.ResizeTargets). 1x1-таргет
	/// адаптации не ресайзится.</summary>
	public void RebindTargets(IGpuTexture hdrColorTarget)
	{
		Material.SetTexture("_SceneTex", hdrColorTarget);
	}

	public void Release()
	{
		Material.Release();
	}
}

/// <summary>
/// Render-graph pass that converts the linear HDR frame into the displayable color target: exposure
/// from the auto-adaptation value measured by <see cref="EyeAdaptationPass"/>, then the PBR Neutral
/// curve and the sRGB encode. Последний пасс кадра - после него ColorTarget уже в display-space,
/// каким его ждут ImGui и readback превью-пробы.
/// </summary>
public sealed class TonemapPass : RenderGraphPass<TonemapPass.PassData>
{
	public override string Name => "Tonemap Pass";

	private readonly TonemapPassResources _resources;
	private readonly IGpuTexture _hdrColorTarget;
	private readonly IGpuTexture _colorTarget;
	private readonly Ref<Vector2> _viewPortRef;

	public struct PassData
	{
	}

	public TonemapPass(TonemapPassResources resources, IGpuTexture hdrColorTarget, IGpuTexture colorTarget,
		Ref<Vector2> viewPortRef)
	{
		_resources = resources;
		_hdrColorTarget = hdrColorTarget;
		_colorTarget = colorTarget;
		_viewPortRef = viewPortRef;
	}

	/// <summary>Объявляет графу таргеты пасса - см. <see cref="ForwardPass.Setup"/>.</summary>
	public override PassData Setup(IRenderGraphBuilder builder)
	{
		builder.ReadTarget(builder.ImportTexture(_hdrColorTarget));
		builder.WriteTarget(builder.ImportTexture(_colorTarget));
		return default;
	}

	public override void WriteCommands(in PassData value, in IRenderGraphContext context)
	{
		var cmd = context.cmd;

		cmd.SetRenderTarget(null, null);
		cmd.TransitionResource(_hdrColorTarget, ResourceState.ShaderResource);

		cmd.SetRenderTarget(_colorTarget, null);
		cmd.SetViewport(_viewPortRef);
		cmd.SetPipelineState(_resources.Material);
		cmd.CommitShaderResources(_resources.Material);
		cmd.Draw(3);
	}
}
