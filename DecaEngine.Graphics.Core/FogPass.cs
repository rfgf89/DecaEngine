using System.Numerics;
using System.Runtime.InteropServices;
using DecaEngine.Graphics;
using DecaEngine.Core;

namespace DecaEngine.Graphics;

/// <summary>Owns the GPU resources for the atmospheric fog post-process: one fullscreen material
/// that reads scene depth plus a copy of the frame and writes the frame back with exponential
/// height fog and sun inscattering applied (см. FogCommon.hlsl). Created once by
/// <see cref="GraphicsPipelineSimple"/> when fog is enabled - тот же паттерн владения, что у
/// <see cref="SsgiPassResources"/>: сам <see cref="FogPass"/> пересобирается каждый кадр, ресурсы
/// живут здесь и принимают живые пуши ручек.
///
/// Своего рендер-таргета у тумана нет (в отличие от SSAO/SSGI): он не оценивает величину, которую
/// потом надо размывать, а сразу пишет готовый кадр - источником служит копия сцены, которую пасс
/// делает сам (см. <see cref="FogPass.WriteCommands"/>).</summary>
public sealed unsafe class FogPassResources : IReleaseObject
{
	internal IMaterialObject Material { get; }

	// Кбуфер "FogConstants" со СВОЕЙ unmanaged-памятью, заливаемый командой UpdateBuffer из пасса, -
	// ровно как в EyeAdaptationPassResources и по той же причине. Базис камеры меняется КАЖДЫЙ КАДР
	// (орбита мыши), а IMaterialObject.SetConstant попутно переустанавливает переменную в SRB;
	// обновление дескриптор-сета, пока предыдущий кадр ещё в полёте, роняет Vulkan-валидацию
	// ("bound VkDescriptorSet was destroyed or updated"). Ручкам, которые человек двигает раз в
	// сессию, это сходит с рук; покадровому базису - нет.
	private readonly IBufferHandle _constantBuffer;
	private readonly FogConstantsData* _constants;

	/// <param name="adaptationTarget">1x1-таргет авто-экспозиции (см. EyeAdaptationPassResources).
	/// null в LDR-конвейере - тогда цвет дымки трактуется как абсолютный, а в слот _AdaptTex всё
	/// равно привязывается плейсхолдер: слот объявлен в шейдере безусловно, и пустой дескриптор
	/// роняет валидацию Vulkan (VUID-08114).</param>
	public FogPassResources(IGraphicsApi graphicsApi, IBatchRenderer batchRenderer, IGpuTexture depthTarget,
		IGpuTexture sceneCopyTarget, TextureObjectFormat colorFormat,
		IGpuTexture? adaptationTarget)
	{
		// Свой экземпляр VS - см. комментарий в SsaoPassResources (шареный шейдер освобождался бы
		// дважды при пересоздании окружения).
		var vs = graphicsApi.CreateShader("Fog Fullscreen VS", "EditorAssets/shader", "SkyBackgroundVS.hlsl",
			ShaderObjectType.Vertex);
		var ps = graphicsApi.CreateShader("Fog PS", "EditorAssets/shader",
			"FogPS.hlsl", ShaderObjectType.Pixel);

		// Без депта: туман считается по готовому кадру и глубине.
		var state = graphicsApi.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "Fog PSO",
			RenderTargetFormats = [colorFormat],
			DepthStencilFormat = TextureObjectFormat.Unknown,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = false },
			InputLayout = [],
		});

		Material = graphicsApi.CreateMaterial("Fog Material");
		Material.SetShader(vs, ps);
		Material.SetState(state);
		batchRenderer.BindViewConstants(Material);

		var sampler = graphicsApi.CreateSampler(
			name: "Fog Sampler",
			filter: TextureFilter.Linear,
			address: TextureAddress.Clamp,
			comparisonFunction: CompFunction.Always,
			border: Vector4.Zero);

		Material.SetTexture("_SceneTex", sceneCopyTarget);
		Material.SetImmutableSampler("_SceneTex", sampler);
		Material.SetTexture("_DepthTex", depthTarget);
		Material.SetTexture("_AdaptTex", adaptationTarget ?? sceneCopyTarget);

		// dynamic = false: динамические буферы Diligent обновляет через Map, а нам нужен именно
		// UpdateBuffer из командного буфера (USAGE_DEFAULT).
		_constantBuffer = graphicsApi.CreateBuffer(new BufferInfo
		{
			name = "FogConstants",
			dynamic = false,
			type = BufferHandleType.Constant,
			access = HandleAccess.Pixel,
			sizeInBytes = (uint)sizeof(FogConstantsData),
		});

		Material.SetBuffer("FogConstants", _constantBuffer, HandleAccess.Pixel);

		_constants = (FogConstantsData*)NativeMemory.AllocZeroed(1, (nuint)sizeof(FogConstantsData));

		// Дефолты - до первого пуша из окна Graphics: пасс рисует с первого кадра, и кбуфер иначе
		// остался бы с мусором (та же причина, что в SsaoPassResources). Базис камеры без пуша -
		// смотрящий вдоль +Z, чтобы первый кадр не выдал NaN на нулевом луче.
		SetParams(DefaultDensity, DefaultHeightFalloff, DefaultHeightRef, DefaultStartDistance,
			DefaultMaxDistance, DefaultMaxOpacity);
		SetColors(DefaultColor, DefaultSunColor, DefaultSunStrength, DefaultSunSharpness);
		SetSun(new Vector3(0f, 1f, 0f));
		SetCamera(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
		SetExposure(adaptationTarget is not null, 0.18f);
	}

	/// <summary>Дефолты ручек тумана - ими же инициализируется кбуфер до первого пуша и они же
	/// служат стартовыми значениями в <see cref="DecaEngine.Editor.EditorSettings"/>.
	///
	/// Плотность подобрана «на глаз художника», а не физически: 0.012 на единицу мира даёт
	/// заметную воздушную перспективу на сцене масштаба Sponza (десятки единиц) и почти незаметную
	/// на превью одиночной модели (единицы) - то есть ручку не приходится крутить при каждой смене
	/// содержимого.</summary>
	public const float DefaultDensity = 0.012f;
	public const float DefaultHeightFalloff = 0.05f;
	public const float DefaultHeightRef = 0f;
	public const float DefaultStartDistance = 1f;
	public const float DefaultMaxDistance = 500f;
	public const float DefaultMaxOpacity = 0.9f;
	public const float DefaultSunStrength = 0.6f;
	public const float DefaultSunSharpness = 8f;

	/// <summary>Холодная сизая дымка и тёплая подсветка - классическая пара воздушной перспективы:
	/// тень среды уходит в синеву неба, просвет в сторону солнца теплеет. Поканально, а не
	/// Vector3: эти же значения - дефолты свойств EditorSettings, а те сериализуются в JSON скалярами
	/// и требуют именно const, а не static readonly.</summary>
	public const float DefaultColorR = 0.42f, DefaultColorG = 0.50f, DefaultColorB = 0.62f;
	public const float DefaultSunColorR = 1.00f, DefaultSunColorG = 0.82f, DefaultSunColorB = 0.60f;

	public static Vector3 DefaultColor => new(DefaultColorR, DefaultColorG, DefaultColorB);
	public static Vector3 DefaultSunColor => new(DefaultSunColorR, DefaultSunColorG, DefaultSunColorB);

	/// <summary>Layout кбуфера "FogConstants" в FogCommon.hlsl - семь float4 (112 байт). Каждая
	/// строка ровно 16 байт: трёхкомпонентный вектор по невыровненному смещению SPIR-V отвергает
	/// целиком (см. историю в SsaoCommon.hlsl), поэтому раскладка описана Vector4-строками, а не
	/// Vector3 + скаляр.</summary>
	private struct FogConstantsData
	{
		/// <summary>x - плотность, y - спад по высоте, z - опорная высота, w - ближняя отсечка.</summary>
		public Vector4 Params;

		/// <summary>xyz - цвет среды, w - потолок непрозрачности.</summary>
		public Vector4 Color;

		/// <summary>xyz - цвет подсветки солнцем, w - её сила.</summary>
		public Vector4 SunColor;

		/// <summary>xyz - направление НА солнце в мире, w - резкость солнечного пятна.</summary>
		public Vector4 Sun;

		/// <summary>xyz - мировой right камеры, w - предельная дальность тумана.</summary>
		public Vector4 Right;

		/// <summary>xyz - мировой up камеры, w - резерв.</summary>
		public Vector4 Up;

		/// <summary>xyz - мировой forward камеры, w - резерв.</summary>
		public Vector4 Forward;

		/// <summary>x - цвет задан относительно экспозиции, y - key value, zw - резерв.</summary>
		public Vector4 Exposure;
	}

	/// <summary>Привязка яркости дымки к авто-экспозиции. Включена ровно тогда, когда жив
	/// HDR-конвейер: цвет тумана задаётся в ОТОБРАЖАЕМЫХ единицах и домножается в шейдере на
	/// adapted/key, поэтому дымка выглядит одинаково независимо от абсолютной яркости сцены.
	///
	/// <paramref name="key"/> обязан совпадать с тем, что уходит в
	/// <see cref="EyeAdaptationPassResources.SetParams"/> и <see cref="TonemapPassResources.SetParams"/>,
	/// иначе туман разъедется с кадром по яркости.</summary>
	public void SetExposure(bool exposureRelative, float key)
	{
		_constants->Exposure = new Vector4(exposureRelative ? 1f : 0f, MathF.Max(key, 1e-4f), 0f, 0f);
	}

	/// <summary>Переключает ТОЛЬКО режим (относительно экспозиции против абсолютных единиц),
	/// сохраняя key. Тумблер авто-экспозиции живой - см. <see cref="PipelineFeatures.EyeAdaptation"/>.</summary>
	public void SetExposureRelative(bool exposureRelative)
	{
		_constants->Exposure.X = exposureRelative ? 1f : 0f;
	}

	/// <summary>Геометрия среды: плотность, высотный профиль, ближняя отсечка и предельная
	/// дальность (её же получает небо - см. FogCommon.hlsl).</summary>
	public void SetParams(float density, float heightFalloff, float heightRef, float startDistance,
		float maxDistance, float maxOpacity)
	{
		_constants->Params = new Vector4(MathF.Max(density, 0f), MathF.Max(heightFalloff, 0f),
			heightRef, MathF.Max(startDistance, 0f));
		_constants->Right.W = MathF.Max(maxDistance, 1f);
		_constants->Color.W = Math.Clamp(maxOpacity, 0f, 1f);
	}

	/// <summary>Цвета среды и её солнечной подсветки - линейные.</summary>
	public void SetColors(Vector3 color, Vector3 sunColor, float sunStrength, float sunSharpness)
	{
		_constants->Color = new Vector4(color, _constants->Color.W);
		_constants->SunColor = new Vector4(sunColor, Math.Clamp(sunStrength, 0f, 1f));
		_constants->Sun.W = MathF.Max(sunSharpness, 0.001f);
	}

	/// <summary>Направление НА солнце в мире. Приходит из того же источника, что и свет сцены
	/// (см. ModelViewportEnvironment.ShadowSettings.LightDirection, указывающий ОТ солнца).</summary>
	public void SetSun(Vector3 sunDirection)
	{
		var dir = sunDirection.LengthSquared() > 1e-8f ? Vector3.Normalize(sunDirection) : Vector3.UnitY;
		_constants->Sun = new Vector4(dir, _constants->Sun.W);
	}

	/// <summary>Мировой базис камеры - ЕДИНИЧНЫЕ векторы, посчитанные вызывающим прямо из eye/target
	/// (см. ModelViewportEnvironment.SetCameraTransform), а не разбором матрицы вида: соглашение о
	/// строках/столбцах легко перепутать, а ошибка в нём даёт туман, «приклеенный» к экрану.
	/// Пушится каждый кадр - оттого кбуфер и заливается командой (см. <see cref="_constants"/>).</summary>
	public void SetCamera(Vector3 right, Vector3 up, Vector3 forward)
	{
		_constants->Right = new Vector4(right, _constants->Right.W);
		_constants->Up = new Vector4(up, 0f);
		_constants->Forward = new Vector4(forward, 0f);
	}

	/// <summary>Перепривязывает депт и копию кадра ПОСЛЕ их Resize - Resize пересоздаёт нативные
	/// текстуры, и SRB иначе держал бы уничтоженные (см. ModelPreviewViewport.ResizeTargets).</summary>
	public void RebindTargets(IGpuTexture depthTarget, IGpuTexture sceneCopyTarget)
	{
		Material.SetTexture("_DepthTex", depthTarget);
		Material.SetTexture("_SceneTex", sceneCopyTarget);
	}

	/// <summary>Заливает кбуфер в командный буфер - зовётся пассом перед дроу. Команда перечитывает
	/// CPU-память при КАЖДОМ реплее заморожённого буфера, поэтому покадровый базис камеры доезжает
	/// без пересборки графа (см. EyeAdaptationPassResources - тот же приём).</summary>
	internal void WriteConstants(ICommandBuffer cmd) => cmd.UpdateBuffer(_constantBuffer, 0, _constants);

	public void Release()
	{
		Material.Release();
		_constantBuffer.Release();
		NativeMemory.Free(_constants);
	}
}

/// <summary>
/// Render-graph pass that applies atmospheric fog (aerial perspective) to the finished frame.
///
/// Порядок в кадре не случаен: ПОСЛЕ <see cref="SsgiPass"/> - туман обязан лечь и на непрямой
/// свет, иначе дальний bounce остался бы неразмытым и выдал бы плоскость, - и ДО
/// <see cref="EyeAdaptationPass"/>, потому что рассеянный средой свет входит в яркость кадра
/// наравне с остальным и обязан участвовать в экспозиции.
/// </summary>
public sealed class FogPass : RenderGraphPass<FogPass.PassData>
{
	public override string Name => "Fog Pass";

	private readonly FogPassResources _resources;
	private readonly IGpuTexture _colorTarget;
	private readonly IGpuTexture _sceneCopy;
	private readonly IGpuTexture _renderDepth;
	private readonly Ref<Vector2> _viewPortRef;

	public struct PassData
	{
	}

	public FogPass(FogPassResources resources, IGpuTexture colorTarget, IGpuTexture sceneCopy,
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
		var color = builder.ImportTexture(_colorTarget);
		builder.ReadTarget(color);
		builder.WriteTarget(color);

		var sceneCopy = builder.ImportTexture(_sceneCopy);
		builder.WriteTarget(sceneCopy);
		builder.ReadTarget(sceneCopy);

		builder.ReadTarget(builder.ImportTexture(_renderDepth));
		return default;
	}

	public override void WriteCommands(in PassData value, in IRenderGraphContext context)
	{
		var cmd = context.cmd;

		_resources.WriteConstants(cmd);

		// Именно DepthRead, а не ShaderResource - см. комментарий в SsaoPassResources
		// (лейаут DEPTH_STENCIL_READ_ONLY_OPTIMAL на Vulkan).
		cmd.TransitionResource(_renderDepth, ResourceState.DepthRead);

		// Читать и писать один таргет нельзя - берём копию кадра, как это делает SSGI. Копия
		// снимается ЗДЕСЬ, а не переиспользуется от SSGI: между ними тот успел дописать в кадр
		// свой bounce, и старый снимок вернул бы кадр без него.
		cmd.SetRenderTarget(null, null);
		cmd.CopyTexture(_colorTarget, _sceneCopy);
		cmd.TransitionResource(_sceneCopy, ResourceState.ShaderResource);

		cmd.SetRenderTarget(_colorTarget, null);
		cmd.SetViewport(_viewPortRef);
		cmd.SetPipelineState(_resources.Material);
		cmd.CommitShaderResources(_resources.Material);
		cmd.Draw(3);
	}
}
