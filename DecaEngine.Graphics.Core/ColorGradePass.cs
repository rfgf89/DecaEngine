using System.Numerics;
using System.Runtime.InteropServices;
using DecaEngine.Graphics;
using DecaEngine.Core;

namespace DecaEngine.Graphics;

/// <summary>Owns the GPU resources for the final colour grading + vignette pass (см.
/// ColorGradePS.hlsl): one fullscreen material plus its own display-space copy of the frame.
///
/// СВОЯ копия, а не общий <see cref="PipelineRenderTargets.SceneCopyTarget"/>: тот в HDR-режиме
/// RGBA16F (его читает рефракция), а грейдинг работает по ОТОБРАЖАЕМОМУ RGBA8-кадру, и CopyTexture
/// между разными форматами не годится. Зато благодаря этому пасс совершенно одинаков в обоих
/// конвейерах - ColorTarget всегда RGBA8 display-space.</summary>
public sealed unsafe class ColorGradePassResources : IReleaseObject
{
	internal IMaterialObject Material { get; }

	private readonly IRenderTarget _copy;
	private readonly IBufferHandle _constantBuffer;
	private readonly ColorGradeConstantsData* _constants;

	/// <summary>Копия кадра, из которой читает пасс - её наполняет <see cref="ColorGradePass"/>.</summary>
	internal IRenderTarget Copy => _copy;

	public ColorGradePassResources(IGraphicsApi graphicsApi, IBatchRenderer batchRenderer,
		string colorTargetName, uint width, uint height)
	{
		// Формат жёстко RGBA8: грейдинг всегда идёт по отображаемому кадру (см. док класса).
		_copy = graphicsApi.CreateRenderTarget(new TextureInfo
		{
			name = colorTargetName + " Grade Copy",
			width = width,
			height = height,
			format = TextureObjectFormat.R8G8B8A8UNorm,
		});

		// Свой экземпляр VS - см. комментарий в SsaoPassResources (шареный шейдер освобождался бы
		// дважды при пересоздании окружения).
		var vs = graphicsApi.CreateShader("Color Grade VS", "EditorAssets/shader", "SkyBackgroundVS.hlsl",
			ShaderObjectType.Vertex);
		var ps = graphicsApi.CreateShader("Color Grade PS", "EditorAssets/shader", "ColorGradePS.hlsl",
			ShaderObjectType.Pixel);

		var state = graphicsApi.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "Color Grade PSO",
			RenderTargetFormats = [TextureObjectFormat.R8G8B8A8UNorm],
			DepthStencilFormat = TextureObjectFormat.Unknown,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = false },
			InputLayout = [],
		});

		var sampler = graphicsApi.CreateSampler(
			name: "Color Grade Sampler",
			filter: TextureFilter.Linear,
			address: TextureAddress.Clamp,
			comparisonFunction: CompFunction.Always,
			border: Vector4.Zero);

		Material = graphicsApi.CreateMaterial("Color Grade Material");
		Material.SetShader(vs, ps);
		Material.SetState(state);
		batchRenderer.BindViewConstants(Material);
		Material.SetTexture("_SceneTex", _copy);
		Material.SetImmutableSampler("_SceneTex", sampler);

		// dynamic = false: динамические буферы Diligent обновляет через Map, а нам нужен именно
		// UpdateBuffer из командного буфера (USAGE_DEFAULT) - см. EyeAdaptationPassResources.
		_constantBuffer = graphicsApi.CreateBuffer(new BufferInfo
		{
			name = "GradeConstants",
			dynamic = false,
			type = BufferHandleType.Constant,
			access = HandleAccess.Pixel,
			sizeInBytes = (uint)sizeof(ColorGradeConstantsData),
		});

		Material.SetBuffer("GradeConstants", _constantBuffer, HandleAccess.Pixel);
		_constants = (ColorGradeConstantsData*)NativeMemory.AllocZeroed(
			1, (nuint)sizeof(ColorGradeConstantsData));

		// Дефолты - до первого пуша из окна Graphics (та же причина, что в SsaoPassResources).
		SetGrade(DefaultSaturation, DefaultContrast, DefaultGamma, DefaultTemperature, DefaultTint);
		SetTints(Vector3.Zero, Vector3.One);
		SetVignette(DefaultVignetteIntensity, DefaultVignetteRadius, DefaultVignetteSmoothness,
			DefaultVignetteRoundness);
		Resize(width, height);
	}

	/// <summary>Нейтральные дефолты: с ними пасс не меняет кадр ВООБЩЕ. Это сознательно - грейдинг
	/// включается галкой, и включение не должно само по себе перекрашивать сцену; художник добавляет
	/// коррекцию сам, начиная с нуля.</summary>
	public const float DefaultSaturation = 1f;
	public const float DefaultContrast = 1f;
	public const float DefaultGamma = 1f;
	public const float DefaultTemperature = 0f;
	public const float DefaultTint = 0f;
	public const float DefaultVignetteIntensity = 0f;
	public const float DefaultVignetteRadius = 0.75f;
	public const float DefaultVignetteSmoothness = 0.45f;
	public const float DefaultVignetteRoundness = 1f;

	/// <summary>Layout кбуфера "GradeConstants" в ColorGradePS.hlsl - пять float4 (80 байт).</summary>
	private struct ColorGradeConstantsData
	{
		/// <summary>x - насыщенность, y - контраст, z - гамма, w - температура.</summary>
		public Vector4 Params;

		/// <summary>x - оттенок, y - сила виньетки, z - радиус, w - мягкость края.</summary>
		public Vector4 Params2;

		/// <summary>xyz - тонировка теней (аддитивная), w - вытянутость виньетки к формату.</summary>
		public Vector4 ShadowTint;

		/// <summary>xyz - тонировка светов (мультипликативная), w - резерв.</summary>
		public Vector4 HighlightTint;

		/// <summary>xy - размер таргета, zw - 1/xy.</summary>
		public Vector4 Target;
	}

	/// <summary>Основные ручки коррекции. Все живые - пасс перечитывает кбуфер каждым реплеем.</summary>
	public void SetGrade(float saturation, float contrast, float gamma, float temperature, float tint)
	{
		_constants->Params = new Vector4(MathF.Max(saturation, 0f), MathF.Max(contrast, 0f),
			MathF.Max(gamma, 1e-3f), Math.Clamp(temperature, -1f, 1f));
		_constants->Params2.X = Math.Clamp(tint, -1f, 1f);
	}

	/// <summary>Тонировка теней (аддитивная, нейтраль - чёрный) и светов (мультипликативная,
	/// нейтраль - белый). Это lift/gain, разнесённые по способу применения: аддитив поднимает
	/// именно чёрное, множитель - белое.</summary>
	public void SetTints(Vector3 shadows, Vector3 highlights)
	{
		_constants->ShadowTint = new Vector4(shadows, _constants->ShadowTint.W);
		_constants->HighlightTint = new Vector4(highlights, 0f);
	}

	/// <summary>Виньетка: сила, радиус чистой зоны, мягкость края и «круглость» (1 - круг с учётом
	/// формата кадра, 0 - овал по всему кадру).</summary>
	public void SetVignette(float intensity, float radius, float smoothness, float roundness)
	{
		_constants->Params2.Y = Math.Clamp(intensity, 0f, 1f);
		_constants->Params2.Z = MathF.Max(radius, 1e-3f);
		_constants->Params2.W = MathF.Max(smoothness, 1e-3f);
		_constants->ShadowTint.W = Math.Clamp(roundness, 0f, 1f);
	}

	/// <summary>Ресайзит копию кадра и обновляет размеры в кбуфере - зовётся при ресайзе вьюпорта
	/// (см. ModelPreviewViewport.ResizeTargets).</summary>
	public void Resize(uint width, uint height)
	{
		_copy.Resize(new Vector2(width, height));
		_constants->Target = new Vector4(width, height, 1f / MathF.Max(width, 1f), 1f / MathF.Max(height, 1f));
	}

	/// <summary>Перепривязывает копию после её Resize - Resize пересоздаёт нативную текстуру, и SRB
	/// иначе держал бы уничтоженную.</summary>
	public void RebindTargets(uint width, uint height)
	{
		Resize(width, height);
		Material.SetTexture("_SceneTex", _copy);
	}

	internal void WriteConstants(ICommandBuffer cmd) => cmd.UpdateBuffer(_constantBuffer, 0, _constants);

	public void Release()
	{
		Material.Release();
		_copy.Release();
		_constantBuffer.Release();
		NativeMemory.Free(_constants);
	}
}

/// <summary>
/// Render-graph pass that applies colour grading and a vignette to the finished displayable frame.
///
/// Последний ХУДОЖЕСТВЕННЫЙ пасс кадра: после тонемапа (грейдинг определён в отображаемом
/// пространстве, см. ColorGradePS.hlsl), но ДО <see cref="PostOverlayPass"/> - контур выделения,
/// гизмо и прочий интерфейс коррекция трогать не должна, иначе выделение меняло бы цвет вместе с
/// настроением сцены.
/// </summary>
public sealed class ColorGradePass : RenderGraphPass<ColorGradePass.PassData>
{
	public override string Name => "Color Grade Pass";

	private readonly ColorGradePassResources _resources;
	private readonly IGpuTexture _colorTarget;
	private readonly Ref<Vector2> _viewPortRef;

	public struct PassData
	{
	}

	public ColorGradePass(ColorGradePassResources resources, IGpuTexture colorTarget, Ref<Vector2> viewPortRef)
	{
		_resources = resources;
		_colorTarget = colorTarget;
		_viewPortRef = viewPortRef;
	}

	/// <summary>Объявляет графу таргеты пасса - см. <see cref="ForwardPass.Setup"/>.</summary>
	public override PassData Setup(IRenderGraphBuilder builder)
	{
		var color = builder.ImportTexture(_colorTarget);
		builder.ReadTarget(color);
		builder.WriteTarget(color);

		var copy = builder.ImportTexture(_resources.Copy);
		builder.WriteTarget(copy);
		builder.ReadTarget(copy);

		return default;
	}

	public override void WriteCommands(in PassData value, in IRenderGraphContext context)
	{
		var cmd = context.cmd;

		_resources.WriteConstants(cmd);

		// Читать и писать один таргет нельзя - берём копию, как это делают туман и блум.
		cmd.SetRenderTarget(null, null);
		cmd.CopyTexture(_colorTarget, _resources.Copy);
		cmd.TransitionResource(_resources.Copy, ResourceState.ShaderResource);

		cmd.SetRenderTarget(_colorTarget, null);
		cmd.SetViewport(_viewPortRef);
		cmd.SetPipelineState(_resources.Material);
		cmd.CommitShaderResources(_resources.Material);
		cmd.Draw(3);
	}
}
