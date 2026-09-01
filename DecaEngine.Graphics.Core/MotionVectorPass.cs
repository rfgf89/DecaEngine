using System.Numerics;
using System.Runtime.InteropServices;
using DecaEngine.Graphics;
using DecaEngine.Core;

namespace DecaEngine.Graphics;

/// <summary>Owns the GPU resources for the screen-space motion vector buffer: один фуллскрин-материал,
/// который читает глубину кадра и пишет в свой RG16F-таргет смещение каждого пикселя к его положению
/// в ПРЕДЫДУЩЕМ кадре (см. MotionVectorPS.hlsl). Создаётся лениво <see cref="GraphicsPipelineSimple"/>
/// при первом включении <see cref="PipelineFeatures.MotionVectors"/> - тот же паттерн владения, что у
/// <see cref="FogPassResources"/>.
///
/// ЭТО СТУПЕНЬ 1. Векторы восстанавливаются из одной лишь глубины, то есть содержат движение КАМЕРЫ и
/// ничего больше: всё, что двигалось само (анимация, скиннинг, едущие объекты), для апскейлера
/// выглядит неподвижным и потому тянет за собой шлейф истории. Честные пер-инстансные векторы требуют
/// второй матрицы в <see cref="DecaEngine.Graphics.Diligent.GPURenderInstance"/> и второго RT-слота в
/// UnlitInstancedPS.hlsl - и когда они появятся, этот пасс останется фолбэком для геометрии, которая
/// не идёт через батч-рендерер.
///
/// Смысл ступени в том, что она даёт ПРОВЕРЯЕМУЮ статику: на неподвижной сцене результат обязан быть
/// точным, поэтому джиттер, расщепление разрешений и интеграцию самого апскейлера можно отлаживать,
/// зная, что источник векторов не врёт.</summary>
public sealed unsafe class MotionVectorPassResources : IReleaseObject
{
	internal IMaterialObject Material { get; }

	/// <summary>RG16F-таргет с векторами. Единицы - UV (0..1 по экрану), направление - ИЗ текущего
	/// кадра в прошлый (prevUV = curUV + motion), см. MotionVectorPS.hlsl.</summary>
	public IRenderTarget MotionTarget { get; }

	// Кбуфер со СВОЕЙ unmanaged-памятью, заливаемый командой UpdateBuffer из пасса, - ровно как в
	// FogPassResources и по той же причине: матрица репроджекции меняется КАЖДЫЙ кадр, а
	// IMaterialObject.SetConstant попутно переустанавливает переменную в SRB, что роняет
	// Vulkan-валидацию, пока предыдущий кадр ещё в полёте.
	private readonly IBufferHandle _constantBuffer;
	private readonly MotionVectorConstantsData* _constants;

	/// <summary>viewProj предыдущего кадра. Защёлкивается раз в кадр в <see cref="UpdateFromView"/>.</summary>
	private Matrix4x4 _prevViewProj;

	/// <summary>Был ли хоть один защёлкнутый кадр. До него репроджекция - единичная матрица, что даёт
	/// РОВНО нулевые векторы: первый кадр истории не имеет, и любое ненулевое смещение увело бы
	/// апскейлер в неинициализированный буфер.</summary>
	private bool _hasPrev;

	public MotionVectorPassResources(IGraphicsApi graphicsApi, IBatchRenderer batchRenderer,
		string colorTargetName, IGpuTexture depthTarget, uint width, uint height)
	{
		// Свой экземпляр VS - см. комментарий в FogPassResources (шареный шейдер освобождался бы
		// дважды при пересоздании окружения).
		var vs = graphicsApi.CreateShader("Motion Vector Fullscreen VS", "EditorAssets/shader",
			"SkyBackgroundVS.hlsl", ShaderObjectType.Vertex);
		var ps = graphicsApi.CreateShader("Motion Vector PS", "EditorAssets/shader",
			"MotionVectorPS.hlsl", ShaderObjectType.Pixel);

		MotionTarget = graphicsApi.CreateRenderTarget(new TextureInfo
		{
			name = colorTargetName + " Motion Vectors",
			width = width,
			height = height,
			format = TextureObjectFormat.R16G16Float,
		});

		var state = graphicsApi.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "Motion Vector PSO",
			RenderTargetFormats = [TextureObjectFormat.R16G16Float],
			DepthStencilFormat = TextureObjectFormat.Unknown,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = false },
			InputLayout = [],
		});

		Material = graphicsApi.CreateMaterial("Motion Vector Material");
		Material.SetShader(vs, ps);
		Material.SetState(state);
		batchRenderer.BindViewConstants(Material);

		// Сэмплера нет намеренно: глубина читается только через Load по целочисленному пикселю
		// (см. MotionVectorPS.hlsl), а билинейная фильтрация здесь была бы прямо вредна - среднее
		// между двумя глубинами не лежит ни на одной из поверхностей.
		Material.SetTexture("_DepthTex", depthTarget);

		// dynamic = false: динамические буферы Diligent обновляет через Map, а нам нужен именно
		// UpdateBuffer из командного буфера (USAGE_DEFAULT).
		_constantBuffer = graphicsApi.CreateBuffer(new BufferInfo
		{
			name = "MotionVectorConstants",
			dynamic = false,
			type = BufferHandleType.Constant,
			access = HandleAccess.Pixel,
			sizeInBytes = (uint)sizeof(MotionVectorConstantsData),
		});

		Material.SetBuffer("MotionVectorConstants", _constantBuffer, HandleAccess.Pixel);

		_constants = (MotionVectorConstantsData*)NativeMemory.AllocZeroed(1,
			(nuint)sizeof(MotionVectorConstantsData));

		// До первой защёлки - единичная матрица (нулевые векторы), а не нули: нулевая матрица дала бы
		// prevClip.w == 0 и увела бы шейдер в ветку отсечки на всём кадре.
		_constants->Reprojection = Matrix4x4.Identity;
	}

	/// <summary>Layout кбуфера "MotionVectorConstants" в MotionVectorPS.hlsl - одна матрица (64 байта).
	/// Раскладка совпадает с <see cref="DecaEngine.Graphics.Diligent.ViewData"/>: шейдеры компилируются
	/// с PackMatrixRowMajor (см. DiligentShader), поэтому System.Numerics-матрица уезжает в кбуфер как
	/// есть, без транспонирования.</summary>
	private struct MotionVectorConstantsData
	{
		public Matrix4x4 Reprojection;
	}

	/// <summary>Защёлкивает кадр: считает матрицу репроджекции из viewProj ТЕКУЩЕГО кадра и
	/// запомненного прошлого, после чего запоминает текущий. Зовётся РОВНО ОДИН РАЗ за кадр, из
	/// <see cref="GraphicsPipelineSimple.Execute"/>.
	///
	/// Почему не из <see cref="MotionVectorPass.WriteCommands"/>: команды графа замораживаются и
	/// проигрываются много кадров подряд (см. IRenderGraph), так что посчитанная там матрица
	/// застыла бы вместе с ними. Живым остаётся только содержимое кбуфера - его UpdateBuffer
	/// перечитывает эту память на КАЖДОМ реплее.</summary>
	public void UpdateFromView(in Matrix4x4 viewProj)
	{
		if (!_hasPrev)
		{
			_constants->Reprojection = Matrix4x4.Identity;
			_prevViewProj = viewProj;
			_hasPrev = true;
			return;
		}

		// Порядок множителей - строчный (v * M), как во всём движке: сперва развернуть пиксель
		// текущего кадра обратно в мир, потом спроецировать его прошлой камерой.
		if (Matrix4x4.Invert(viewProj, out var invViewProj))
		{
			_constants->Reprojection = invViewProj * _prevViewProj;
		}
		else
		{
			// Вырожденная камера (нулевой fov, схлопнутый вьюпорт при сворачивании окна). Кадр без
			// векторов честнее кадра с мусорными: единичная матрица даёт нули.
			_constants->Reprojection = Matrix4x4.Identity;
		}

		_prevViewProj = viewProj;
	}

	/// <summary>Сбрасывает историю - следующий кадр выйдет с нулевыми векторами. Нужен там, где
	/// непрерывность кадра рвётся не движением камеры: телепорт, смена сцены, ресайз. Без сброса
	/// апскейлер получил бы огромное смещение на скачке и размазал бы первый кадр.</summary>
	public void ResetHistory()
	{
		_hasPrev = false;
		_constants->Reprojection = Matrix4x4.Identity;
	}

	/// <summary>Ресайзит таргет векторов под новое разрешение кадра (см.
	/// ModelPreviewViewport.ResizeTargets). Историю сбрасывает: содержимое таргета после ресайза
	/// не определено, да и прошлый кадр относился к другому вьюпорту.</summary>
	public void Resize(uint width, uint height)
	{
		MotionTarget.Resize(new Vector2(width, height));
		ResetHistory();
	}

	/// <summary>Перепривязывает депт ПОСЛЕ его Resize - Resize пересоздаёт нативную текстуру, и SRB
	/// иначе держал бы уничтоженную (см. ModelPreviewViewport.ResizeTargets).</summary>
	public void RebindTargets(IGpuTexture depthTarget, uint width, uint height)
	{
		Material.SetTexture("_DepthTex", depthTarget);
		Resize(width, height);
	}

	/// <summary>Заливает кбуфер в командный буфер - зовётся пассом перед дроу. Команда перечитывает
	/// CPU-память при КАЖДОМ реплее заморожённого буфера, поэтому покадровая матрица доезжает без
	/// пересборки графа (см. FogPassResources - тот же приём).</summary>
	internal void WriteConstants(ICommandBuffer cmd) => cmd.UpdateBuffer(_constantBuffer, 0, _constants);

	public void Release()
	{
		Material.Release();
		MotionTarget.Release();
		_constantBuffer.Release();
		NativeMemory.Free(_constants);
	}
}

/// <summary>
/// Render-graph pass that fills the screen-space motion vector buffer from scene depth.
///
/// Место в кадре: сразу ПОСЛЕ <see cref="ForwardPass"/> и до всей пост-обработки. Пассу нужна только
/// готовая глубина, а его результат - вход апскейлера, который встанет между сценой и блумом с
/// тонемапом (см. GraphicsPipelineSimple.RebuildGraph). Стоять раньше он не может (глубины ещё нет),
/// а позже незачем - пост-обработка глубину не трогает.
///
/// Своей очистки таргета нет и не нужно: фуллскрин-треугольник покрывает каждый пиксель кадра.
/// </summary>
public sealed class MotionVectorPass : RenderGraphPass<MotionVectorPass.PassData>
{
	public override string Name => "Motion Vector Pass";

	private readonly MotionVectorPassResources _resources;
	private readonly IGpuTexture _depthTarget;
	private readonly Ref<Vector2> _viewPortRef;

	public struct PassData
	{
	}

	public MotionVectorPass(MotionVectorPassResources resources, IGpuTexture depthTarget,
		Ref<Vector2> viewPortRef)
	{
		_resources = resources;
		_depthTarget = depthTarget;
		_viewPortRef = viewPortRef;
	}

	/// <summary>Объявляет графу таргеты пасса - см. <see cref="ForwardPass.Setup"/>.</summary>
	public override PassData Setup(IRenderGraphBuilder builder)
	{
		builder.ReadTarget(builder.ImportTexture(_depthTarget));
		builder.WriteTarget(builder.ImportTexture(_resources.MotionTarget));
		return default;
	}

	public override void WriteCommands(in PassData value, in IRenderGraphContext context)
	{
		var cmd = context.cmd;

		_resources.WriteConstants(cmd);

		// Именно DepthRead, а не ShaderResource - см. комментарий в SsaoPassResources
		// (лейаут DEPTH_STENCIL_READ_ONLY_OPTIMAL на Vulkan).
		cmd.SetRenderTarget(null, null);
		cmd.TransitionResource(_depthTarget, ResourceState.DepthRead);

		cmd.SetRenderTarget(_resources.MotionTarget, null);
		cmd.SetViewport(_viewPortRef);
		cmd.SetPipelineState(_resources.Material);
		cmd.CommitShaderResources(_resources.Material);
		cmd.Draw(3);
	}
}
