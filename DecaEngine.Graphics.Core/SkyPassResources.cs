using System.Numerics;
using DecaEngine.Graphics;
using DecaEngine.Core;

namespace DecaEngine.Graphics;

/// <summary>Owns the GPU resources for the procedural sky background (fullscreen equirect-sample
/// material). Created once by <see cref="GraphicsPipelineSimple"/> when a sky background is enabled -
/// unlike <see cref="SsaoPassResources"/> it draws inline inside <see cref="ForwardPass"/>'s per-view
/// loop rather than as its own render-graph pass, since it shares that pass's already-bound render
/// target instead of needing one of its own.</summary>
public sealed class SkyPassResources : IReleaseObject
{
	private readonly IMaterialObject _material;

	/// <param name="colorFormat">Формат цветового таргета, в который небо рисуется инлайн внутри
	/// <see cref="ForwardPass"/> - в HDR-режиме превью это RGBA16F, и PSO обязан совпадать с
	/// привязанным таргетом (см. <see cref="PipelineRenderTargets.RenderColorFormat"/>).</param>
	public SkyPassResources(IGraphicsApi graphicsApi, IBatchRenderer batchRenderer, IGpuTexture environmentMap,
		TextureObjectFormat colorFormat = TextureObjectFormat.R8G8B8A8UNorm)
	{
		var skyVs = graphicsApi.CreateShader("Sky Background VS", "EditorAssets/shader", "SkyBackgroundVS.hlsl", ShaderObjectType.Vertex);
		var skyPs = graphicsApi.CreateShader("Sky Background PS", "EditorAssets/shader", "SkyBackgroundPS.hlsl", ShaderObjectType.Pixel);

		_material = graphicsApi.CreateMaterial("Sky Background Material");
		_material.SetShader(skyVs, skyPs);
		_material.SetState(graphicsApi.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "Sky Background PSO",
			RenderTargetFormats = [colorFormat],
			DepthStencilFormat = TextureObjectFormat.D32Float,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = false },
			InputLayout = [],
		}));

		batchRenderer.BindViewConstants(_material);

		var skySampler = graphicsApi.CreateSampler(
			name: "_EnvMap_Sampler",
			filter: TextureFilter.Linear,
			address: TextureAddress.Wrap,
			comparisonFunction: CompFunction.Always,
			border: Vector4.Zero);
		_material.SetTexture("_EnvMap", environmentMap);
		_material.SetImmutableSampler("_EnvMap", skySampler);

		// SkySettings обязан быть привязан до первого Draw: без этого дескриптор пустой
		// (VUID-vkCmdDraw-None-08114) и шейдер читает мусор, пока пользователь не тронет ползунок
		// света (единственный другой вызов SetEnvironmentYaw - ApplyLightRotation).
		_hdrOutput = colorFormat != TextureObjectFormat.R8G8B8A8UNorm;
		SetEnvironmentYaw(0f);
	}

	/// <summary>Layout of the SkySettings cbuffer in SkyBackgroundPS.hlsl - см.
	/// <see cref="SetEnvironmentYaw"/>.</summary>
	private struct SkySettingsData
	{
		public float EnvYawRadians;

		/// <summary>>0.5 - писать линейную яркость вместо display-энкода (HDR-конвейер).</summary>
		public float HdrOutput;

		public float Pad1, Pad2;
	}

	private bool _hdrOutput;
	private float _envYawRadians;

	/// <summary>Пользовательский поворот энвайронмента вокруг Y в радианах (ползунок света в
	/// превью): сдвигает equirect-U фонового неба тем же значением, что уходит в PbrEnvYaw
	/// материалов модели (см. UnlitInstancedPS.hlsl), - фон и отражения вращаются синхронно.</summary>
	public void SetEnvironmentYaw(float radians)
	{
		_envYawRadians = radians;
		PushSettings();
	}

	/// <summary>Писать ли небу ЛИНЕЙНУЮ яркость. Обычно совпадает с HDR-режимом конвейера, но
	/// сбрасывается на время passthrough-режимов тонемапа (отладочные каналы/AO debug): там кадр
	/// копируется в отображаемый таргет как есть, и линейное небо ушло бы туда без энкода - заметно
	/// темнее, чем то же небо в LDR-конвейере. См. <see cref="TonemapPassResources.SetPassthrough"/>.</summary>
	public void SetHdrOutput(bool hdrOutput)
	{
		_hdrOutput = hdrOutput;
		PushSettings();
	}

	private void PushSettings()
	{
		var data = new SkySettingsData { EnvYawRadians = _envYawRadians, HdrOutput = _hdrOutput ? 1f : 0f };
		_material.SetConstant("SkySettings", ref data, HandleAccess.Pixel);
	}

	/// <summary>Фон-энвайронмент: фуллскрин-треугольник без вершинного буфера, рисуется ДО геометрии
	/// с выключенным depth-тестом - геометрия его перекроет. Заодно наполняет scene copy настоящим
	/// небом, так что рефракция стекла на фоне показывает окружение, а не аналитический
	/// градиент-фолбэк. Вызывающий обязан уже забиндить целевой render target (см. ForwardPass).</summary>
	public void Draw(ICommandBuffer cmd)
	{
		cmd.SetPipelineState(_material);
		cmd.CommitShaderResources(_material);
		cmd.Draw(3);
	}

	public void Release()
	{
		_material.Release();
	}
}
