using System;
using System.Numerics;
using System.Runtime.InteropServices;
using DecaEngine.Graphics;
using DecaEngine.Core;

namespace DecaEngine.Graphics;

/// <summary>Ресурсы отладочной визуализации буфера векторов движения (см. MotionVectorDebugPS.hlsl).
/// Живут ровно столько же, сколько <see cref="MotionVectorPassResources"/>, и создаются вместе с ними:
/// сам по себе буфер векторов кадр не меняет, и пока апскейлера нет, это ЕДИНСТВЕННЫЙ способ увидеть,
/// что пасс вообще посчитал.
///
/// Тумблер живёт в кбуфере, а не в <see cref="PipelineFeatures"/>, и выключенный пасс просто
/// дискардит весь экран. Так галка не пересобирает граф и срабатывает в тот же кадр, а платим мы за
/// это одним фуллскрин-дроу без единой записи в таргет.</summary>
public sealed unsafe class MotionVectorDebugPassResources : IReleaseObject
{
	/// <summary>Во сколько ПИКСЕЛЕЙ смещения упирается шкала. Порядка десятка - это заметное, но не
	/// рывковое движение камеры на 60 fps; за пределами диапазона шейдер гасит синий канал, так что
	/// слишком мелкое значение видно сразу (см. MotionVectorDebugPS.hlsl).</summary>
	public const float DefaultRangePixels = 16f;

	internal IMaterialObject Material { get; }

	private readonly IBufferHandle _constantBuffer;
	private readonly MotionVectorDebugConstantsData* _constants;

	public MotionVectorDebugPassResources(IGraphicsApi graphicsApi, IBatchRenderer batchRenderer,
		IGpuTexture motionTarget, uint renderWidth, uint renderHeight, uint displayWidth, uint displayHeight)
	{
		// Свой экземпляр VS - см. комментарий в ColorGradePassResources (шареный шейдер освобождался
		// бы дважды при пересоздании окружения).
		var vs = graphicsApi.CreateShader("Motion Vector Debug VS", "EditorAssets/shader",
			"SkyBackgroundVS.hlsl", ShaderObjectType.Vertex);
		var ps = graphicsApi.CreateShader("Motion Vector Debug PS", "EditorAssets/shader",
			"MotionVectorDebugPS.hlsl", ShaderObjectType.Pixel);

		// Формат жёстко RGBA8: пасс пишет в ОТОБРАЖАЕМЫЙ таргет, после тонемапа - см. док шейдера.
		var state = graphicsApi.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "Motion Vector Debug PSO",
			RenderTargetFormats = [TextureObjectFormat.R8G8B8A8UNorm],
			DepthStencilFormat = TextureObjectFormat.Unknown,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = false },
			InputLayout = [],
		});

		Material = graphicsApi.CreateMaterial("Motion Vector Debug Material");
		Material.SetShader(vs, ps);
		Material.SetState(state);
		batchRenderer.BindViewConstants(Material);

		// Сэмплера нет: буфер векторов читается Load'ом пиксель-в-пиксель, фильтрация усреднила бы
		// векторы поперёк силуэтов и нарисовала бы движение там, где его нет.
		Material.SetTexture("_MotionTex", motionTarget);

		// dynamic = false: нужен именно UpdateBuffer из командного буфера (USAGE_DEFAULT) - см.
		// MotionVectorPassResources.
		_constantBuffer = graphicsApi.CreateBuffer(new BufferInfo
		{
			name = "MotionVectorDebugConstants",
			dynamic = false,
			type = BufferHandleType.Constant,
			access = HandleAccess.Pixel,
			sizeInBytes = (uint)sizeof(MotionVectorDebugConstantsData),
		});

		Material.SetBuffer("MotionVectorDebugConstants", _constantBuffer, HandleAccess.Pixel);
		_constants = (MotionVectorDebugConstantsData*)NativeMemory.AllocZeroed(
			1, (nuint)sizeof(MotionVectorDebugConstantsData));

		SetDebugView(false, DefaultRangePixels);
		Resize(renderWidth, renderHeight, displayWidth, displayHeight);
	}

	/// <summary>Layout кбуфера "MotionVectorDebugConstants" в MotionVectorDebugPS.hlsl - два float4.</summary>
	private struct MotionVectorDebugConstantsData
	{
		/// <summary>xy - размер БУФЕРА ВЕКТОРОВ в пикселях (рендер-разрешение), z - 1/диапазон,
		/// w - включённость.</summary>
		public Vector4 Params;

		/// <summary>xy - отношение рендер-размера к отображаемому по осям: пасс рисует в display-кадр,
		/// а Load из буфера векторов требует рендер-координату (см. MotionVectorDebugPS.hlsl).</summary>
		public Vector4 Params2;
	}

	/// <summary>Живая ручка: пасс перечитывает кбуфер каждым реплеем, так что ни пересборки графа, ни
	/// ожидания GPU переключение не требует.</summary>
	public void SetDebugView(bool enabled, float rangePixels)
	{
		// Диапазон уходит в шейдер уже перевёрнутым - деление на ноль при range == 0 иначе выдало бы
		// inf и покрасило бы кадр в чистые углы шкалы вместо честного «диапазон слишком мал».
		_constants->Params.Z = 1f / MathF.Max(rangePixels, 1e-3f);
		_constants->Params.W = enabled ? 1f : 0f;
	}

	/// <summary>Признаёт новые размеры: рендер-размер переводит вектор из долей экрана в пиксели
	/// (шкала в пикселях РЕНДЕР-разрешения - именно их потребляет апскейлер), а отношение размеров
	/// отображает display-пиксель пасса в координату буфера векторов.</summary>
	public void Resize(uint renderWidth, uint renderHeight, uint displayWidth, uint displayHeight)
	{
		_constants->Params.X = renderWidth;
		_constants->Params.Y = renderHeight;
		_constants->Params2.X = renderWidth / (float)Math.Max(1u, displayWidth);
		_constants->Params2.Y = renderHeight / (float)Math.Max(1u, displayHeight);
	}

	/// <summary>Перепривязывает буфер векторов ПОСЛЕ его Resize - Resize пересоздаёт нативную
	/// текстуру, и SRB иначе держал бы уничтоженную (см. ModelPreviewViewport.ResizeTargets).</summary>
	public void RebindTargets(IGpuTexture motionTarget, uint renderWidth, uint renderHeight,
		uint displayWidth, uint displayHeight)
	{
		Material.SetTexture("_MotionTex", motionTarget);
		Resize(renderWidth, renderHeight, displayWidth, displayHeight);
	}

	internal void WriteConstants(ICommandBuffer cmd) => cmd.UpdateBuffer(_constantBuffer, 0, _constants);

	public void Release()
	{
		Material.Release();
		_constantBuffer.Release();
		NativeMemory.Free(_constants);
	}
}

/// <summary>
/// Render-graph pass that paints the motion vector buffer over the displayed frame.
///
/// Место в кадре: ПОСЛЕ тонемапа и грейдинга, но ДО оверлеев - как и грейдинг, пасс работает по
/// отображаемому RGBA8-кадру (см. MotionVectorDebugPS.hlsl), а гизмо и контур выделения обязаны
/// остаться видны поверх отладки, иначе в этом режиме нельзя даже выбрать объект.
///
/// Копии кадра, в отличие от <see cref="ColorGradePass"/>, не нужно: пасс кадр не читает, а
/// полностью замещает.
/// </summary>
public sealed class MotionVectorDebugPass : RenderGraphPass<MotionVectorDebugPass.PassData>
{
	public override string Name => "Motion Vector Debug Pass";

	private readonly MotionVectorDebugPassResources _resources;
	private readonly IGpuTexture _motionTarget;
	private readonly IGpuTexture _colorTarget;
	private readonly Ref<Vector2> _viewPortRef;

	public struct PassData
	{
	}

	public MotionVectorDebugPass(MotionVectorDebugPassResources resources, IGpuTexture motionTarget,
		IGpuTexture colorTarget, Ref<Vector2> viewPortRef)
	{
		_resources = resources;
		_motionTarget = motionTarget;
		_colorTarget = colorTarget;
		_viewPortRef = viewPortRef;
	}

	/// <summary>Объявляет графу таргеты пасса - см. <see cref="ForwardPass.Setup"/>.</summary>
	public override PassData Setup(IRenderGraphBuilder builder)
	{
		builder.ReadTarget(builder.ImportTexture(_motionTarget));
		builder.WriteTarget(builder.ImportTexture(_colorTarget));
		return default;
	}

	public override void WriteCommands(in PassData value, in IRenderGraphContext context)
	{
		var cmd = context.cmd;

		_resources.WriteConstants(cmd);

		cmd.SetRenderTarget(null, null);
		cmd.TransitionResource(_motionTarget, ResourceState.ShaderResource);

		cmd.SetRenderTarget(_colorTarget, null);
		cmd.SetViewport(_viewPortRef);
		cmd.SetPipelineState(_resources.Material);
		cmd.CommitShaderResources(_resources.Material);
		cmd.Draw(3);
	}
}
