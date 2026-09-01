using System.Numerics;
using System.Runtime.InteropServices;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Core;

namespace DecaEngine.Core;

/// <summary>Owns the GPU resources for the volumetric-light post-process: one fullscreen material
/// that raymarches the view ray through a participating medium, sampling the cascaded shadow map at
/// every step, and writes the frame back with in-scattered light added and transmittance applied
/// (см. VolumetricCommon.hlsl). Created once by <see cref="GraphicsPipelineSimple"/> when the effect
/// is enabled - тот же паттерн владения, что у <see cref="FogPassResources"/>: сам
/// <see cref="VolumetricLightPass"/> пересобирается каждый кадр, ресурсы живут здесь и принимают
/// живые пуши ручек.
///
/// Своего рендер-таргета нет (как и у тумана): пасс не оценивает величину, которую потом надо
/// размывать, а сразу пишет готовый кадр - источником служит копия сцены, которую он снимает сам
/// (см. <see cref="VolumetricLightPass.WriteCommands"/>).</summary>
public sealed unsafe class VolumetricLightPassResources : IReleaseObject
{
	internal IMaterialObject Material { get; }

	// Кбуфер "VolumetricConstants" со СВОЕЙ unmanaged-памятью, заливаемый командой UpdateBuffer из
	// пасса, - ровно как в FogPassResources и по той же причине: базис камеры меняется КАЖДЫЙ КАДР,
	// а IMaterialObject.SetConstant попутно переустанавливает переменную в SRB; обновление
	// дескриптор-сета, пока предыдущий кадр ещё в полёте, роняет валидацию Vulkan.
	private readonly IBufferHandle _constantBuffer;
	private readonly VolumetricConstantsData* _constants;

	/// <param name="shadowsAvailable">В конвейере есть теневой пасс. Когда его нет, содержимое
	/// shadow map не определено (ShadowRenderer создаётся всегда, но никто в него не рисует), и
	/// выборка дала бы случайные столбы - поэтому сила тени принудительно зажимается в ноль
	/// (см. <see cref="SetParams"/>). Эффект при этом не выключается: остаётся однородный объёмный
	/// туман без god rays, что и есть корректное поведение сцены без теней.</param>
	/// <param name="adaptationTarget">1x1-таргет авто-экспозиции (см. EyeAdaptationPassResources).
	/// null в LDR-конвейере - тогда цвета рассеяния трактуются как абсолютные, а в слот _AdaptTex
	/// всё равно привязывается плейсхолдер: слот объявлен в шейдере безусловно, и пустой дескриптор
	/// роняет валидацию Vulkan (VUID-08114).</param>
	public VolumetricLightPassResources(IGraphicsApi graphicsApi, IBatchRenderer batchRenderer,
		IGpuTexture depthTarget, IGpuTexture sceneCopyTarget,
		TextureObjectFormat colorFormat, IGpuTexture? adaptationTarget, bool shadowsAvailable)
	{
		ShadowsAvailable = shadowsAvailable;

		// Свой экземпляр VS - см. комментарий в SsaoPassResources (шареный шейдер освобождался бы
		// дважды при пересоздании окружения).
		var vs = graphicsApi.CreateShader("Volumetric Fullscreen VS", "EditorAssets/shader",
			"SkyBackgroundVS.hlsl", ShaderObjectType.Vertex);
		var ps = graphicsApi.CreateShader("Volumetric PS", "EditorAssets/shader",
			"VolumetricPS.hlsl", ShaderObjectType.Pixel);

		// Без депта: марш идёт по готовому кадру и глубине.
		var state = graphicsApi.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "Volumetric PSO",
			RenderTargetFormats = [colorFormat],
			DepthStencilFormat = TextureObjectFormat.Unknown,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = false },
			InputLayout = [],
		});

		Material = graphicsApi.CreateMaterial("Volumetric Material");
		Material.SetShader(vs, ps);
		Material.SetState(state);
		batchRenderer.BindViewConstants(Material);

		// Кбуфер Light (матрицы каскадов) + сам массив shadow map. Register() сюда не годится: он
		// тянет инстанс-буферы и прочую машинерию батч-материала, которой фуллскрин-квад не нужна.
		batchRenderer.BindShadowResources(Material);

		var sampler = graphicsApi.CreateSampler(
			name: "Volumetric Sampler",
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
			name = "VolumetricConstants",
			dynamic = false,
			type = BufferHandleType.Constant,
			access = HandleAccess.Pixel,
			sizeInBytes = (uint)sizeof(VolumetricConstantsData),
		});

		Material.SetBuffer("VolumetricConstants", _constantBuffer, HandleAccess.Pixel);

		_constants = (VolumetricConstantsData*)NativeMemory.AllocZeroed(1,
			(nuint)sizeof(VolumetricConstantsData));

		// Дефолты - до первого пуша из окна Graphics: пасс рисует с первого кадра, и кбуфер иначе
		// остался бы с мусором (та же причина, что в FogPassResources). Базис камеры без пуша -
		// смотрящий вдоль +Z, чтобы первый кадр не выдал NaN на нулевом луче.
		SetParams(DefaultDensity, DefaultHeightFalloff, DefaultHeightRef, DefaultStartDistance,
			DefaultMaxDistance, DefaultSteps, DefaultMaxOpacity, DefaultShadowStrength);
		SetScattering(DefaultScattering, DefaultExtinction, DefaultAnisotropy);
		SetColors(DefaultSunColor, DefaultSunIntensity, DefaultAmbientColor, DefaultAmbientIntensity,
			DefaultAmbientShadowFloor);
		SetSun(new Vector3(0f, 1f, 0f));
		SetCamera(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
		SetExposure(adaptationTarget is not null, 0.18f);
		SetPunctualScatter(1f);
	}

	/// <summary>Есть ли в конвейере теневой пасс - см. одноимённый параметр конструктора. Без него
	/// сила тени зажата в ноль, и god rays физически невозможны (остаётся ровный объёмный туман).
	/// Читается окном настроек, чтобы объяснить это человеку, а не оставлять его крутить мёртвый
	/// ползунок.</summary>
	public bool ShadowsAvailable { get; }

	/// <summary>Дефолты ручек объёмного света - ими же инициализируется кбуфер до первого пуша и они
	/// же служат стартовыми значениями в <see cref="DecaEngine.Editor.EditorSettings"/>.
	///
	/// Плотность на порядок ниже, чем у аналитического тумана (<see cref="FogPassResources.DefaultDensity"/>):
	/// там она отвечает за потерю контраста на сотнях единиц, здесь - за ВИДИМОЕ свечение среды, и
	/// на тех же значениях кадр превращался бы в молоко. Дальность марша (120) вместо тумановских
	/// 500 - по той же причине, по которой марш вообще ограничивают: шагов фиксированное число, и
	/// растянув их на 500 единиц, получаем шаг в полтора метра и рваные столбы.</summary>
	public const float DefaultDensity = 0.01f;
	public const float DefaultHeightFalloff = 0.05f;
	public const float DefaultHeightRef = 0f;
	public const float DefaultStartDistance = 0.5f;
	public const float DefaultMaxDistance = 120f;
	public const int DefaultSteps = 48;
	public const float DefaultMaxOpacity = 0.9f;
	public const float DefaultShadowStrength = 1f;

	/// <summary>Рассеяние и экстинкция разведены намеренно: дефолт даёт среду, которая заметно
	/// светится (столбы читаются), но кадр за собой почти не мутит - экстинкция вчетверо ниже
	/// рассеяния. Физически такого вещества не бывает, но запрос «столбы есть, а даль не в молоке»
	/// - самый частый, и разделение ручек единственный способ его выполнить.</summary>
	public const float DefaultScattering = 1f;
	public const float DefaultExtinction = 0.15f;

	/// <summary>Прямое рассеяние: реальная дымка и пыль рассеивают вперёд (g ~ 0.6..0.85), поэтому
	/// столбы вспыхивают именно при взгляде против солнца. Изотропная среда (0) выглядит как ровный
	/// туман без «луча».</summary>
	public const float DefaultAnisotropy = 0.7f;

	/// <summary>Тёплый солнечный луч и холодное небесное подсвечивание - та же пара, что у
	/// аналитического тумана, и по той же причине (см. FogPassResources.DefaultColor). Поканально,
	/// а не Vector3: эти же значения - дефолты свойств EditorSettings, а те сериализуются в JSON
	/// скалярами и требуют именно const.</summary>
	public const float DefaultSunColorR = 1.00f, DefaultSunColorG = 0.90f, DefaultSunColorB = 0.72f;
	public const float DefaultAmbientColorR = 0.30f, DefaultAmbientColorG = 0.38f, DefaultAmbientColorB = 0.52f;

	/// <summary>Сила солнечного рассеяния - это и есть god rays. Небесная доля вшестеро слабее: её
	/// задача не светить, а не дать среде в тени стать угольно-чёрной (иначе столбы читаются как
	/// вырезанные ножницами).
	///
	/// Замерено на интерьере Sponza: прежние 0.35 при неглушёной небесной доле заливали кадр
	/// молочной пеленой целиком - крытая аркада в двух метрах от камеры получала ровно столько же
	/// свечения, сколько залитый солнцем двор. Флор глушения ниже лечит причину, а эта величина
	/// убирает оставшийся запас.</summary>
	public const float DefaultSunIntensity = 1.2f;
	public const float DefaultAmbientIntensity = 0.2f;

	/// <summary>Во сколько раз небесная доля слабее в затенённом объёме. 0.15 - глубокий интерьер
	/// практически без небесного свечения, при том что тень одиночной колонны во дворе им ещё
	/// подсвечена. См. подробный разбор в VolumetricCommon.hlsl.</summary>
	public const float DefaultAmbientShadowFloor = 0.15f;

	public static Vector3 DefaultSunColor => new(DefaultSunColorR, DefaultSunColorG, DefaultSunColorB);
	public static Vector3 DefaultAmbientColor => new(DefaultAmbientColorR, DefaultAmbientColorG, DefaultAmbientColorB);

	/// <summary>Layout кбуфера "VolumetricConstants" в VolumetricCommon.hlsl - девять float4
	/// (144 байта). Каждая строка ровно 16 байт: трёхкомпонентный вектор по невыровненному смещению
	/// SPIR-V отвергает целиком (см. историю в SsaoCommon.hlsl), поэтому раскладка описана
	/// Vector4-строками, а не Vector3 + скаляр.</summary>
	private struct VolumetricConstantsData
	{
		/// <summary>x - плотность, y - спад по высоте, z - опорная высота, w - начало марша.</summary>
		public Vector4 Params;

		/// <summary>x - дальность марша, y - число шагов, z - рассеяние, w - анизотропия.</summary>
		public Vector4 March;

		/// <summary>xyz - цвет солнечного рассеяния, w - его сила.</summary>
		public Vector4 SunColor;

		/// <summary>xyz - цвет небесного рассеяния, w - его сила.</summary>
		public Vector4 AmbientColor;

		/// <summary>xyz - направление НА солнце в мире, w - сила тени.</summary>
		public Vector4 Sun;

		/// <summary>xyz - мировой right камеры, w - потолок непрозрачности.</summary>
		public Vector4 Right;

		/// <summary>xyz - мировой up камеры, w - экстинкция.</summary>
		public Vector4 Up;

		/// <summary>xyz - мировой forward камеры, w - флор глушения небесной доли затенением.</summary>
		public Vector4 Forward;

		/// <summary>x - цвета заданы относительно экспозиции, y - key value, z - множитель
		/// рассеяния punctual-светов (см. <see cref="SetPunctualScatter"/>), w - резерв.</summary>
		public Vector4 Exposure;
	}

	/// <summary>Геометрия среды и параметры марша. <paramref name="steps"/> - прямая ручка «цена
	/// против гладкости»: яркость эффекта от неё НЕ зависит (см. аналитическое интегрирование
	/// отрезка в VolumetricCommon.hlsl), меняется только чёткость границ столбов.</summary>
	public void SetParams(float density, float heightFalloff, float heightRef, float startDistance,
		float maxDistance, int steps, float maxOpacity, float shadowStrength)
	{
		_constants->Params = new Vector4(MathF.Max(density, 0f), MathF.Max(heightFalloff, 0f),
			heightRef, MathF.Max(startDistance, 0f));
		_constants->March.X = MathF.Max(maxDistance, 1f);
		_constants->March.Y = Math.Clamp(steps, 4, 256);
		_constants->Right.W = Math.Clamp(maxOpacity, 0f, 1f);

		// Без теневого пасса содержимое shadow map не определено - см. параметр shadowsAvailable
		// конструктора. Зажимаем ЗДЕСЬ, а не в вызывающем: иначе о запрете пришлось бы помнить
		// каждому вьюпорту.
		_constants->Sun.W = ShadowsAvailable ? Math.Clamp(shadowStrength, 0f, 1f) : 0f;
	}

	/// <summary>Оптика среды: сколько света она рассеивает, сколько гасит и насколько направленно
	/// (см. дефолты - разведение рассеяния и экстинкции здесь намеренное).</summary>
	public void SetScattering(float scattering, float extinction, float anisotropy)
	{
		_constants->March.Z = MathF.Max(scattering, 0f);
		_constants->Up.W = MathF.Max(extinction, 1e-4f);
		_constants->March.W = Math.Clamp(anisotropy, -0.95f, 0.95f);
	}

	/// <summary>Цвета и силы солнечного и небесного рассеяния - линейные.
	/// <paramref name="ambientShadowFloor"/> - во сколько раз небесная доля слабее в затенённом
	/// объёме; см. подробный разбор в VolumetricCommon.hlsl, без этого глушения эффект вырождается
	/// в молочную пелену по всему кадру.</summary>
	public void SetColors(Vector3 sunColor, float sunIntensity, Vector3 ambientColor,
		float ambientIntensity, float ambientShadowFloor)
	{
		_constants->SunColor = new Vector4(sunColor, MathF.Max(sunIntensity, 0f));
		_constants->AmbientColor = new Vector4(ambientColor, MathF.Max(ambientIntensity, 0f));
		_constants->Forward.W = Math.Clamp(ambientShadowFloor, 0f, 1f);
	}

	/// <summary>Направление НА солнце в мире. Приходит из того же источника, что и свет сцены
	/// (см. ModelViewportEnvironment.ShadowSettings.LightDirection, указывающий ОТ солнца).</summary>
	public void SetSun(Vector3 sunDirection)
	{
		var dir = sunDirection.LengthSquared() > 1e-8f ? Vector3.Normalize(sunDirection) : Vector3.UnitY;
		_constants->Sun = new Vector4(dir, _constants->Sun.W);
	}

	/// <summary>Привязка яркости рассеяния к авто-экспозиции - см. <see cref="FogPassResources.SetExposure"/>:
	/// та же механика, тот же обязательный к совпадению <paramref name="key"/>.</summary>
	public void SetExposure(bool exposureRelative, float key)
	{
		_constants->Exposure = new Vector4(exposureRelative ? 1f : 0f, MathF.Max(key, 1e-4f),
			_constants->Exposure.Z, 0f);
	}

	/// <summary>Множитель рассеяния punctual-светов средой (0 - лампы среду не подсвечивают,
	/// 1 - физическая доля). В отличие от цветов солнца/неба это не источник, а доля: сами света
	/// приходят из пула кадра в сценовых линейных единицах.</summary>
	public void SetPunctualScatter(float intensity)
	{
		_constants->Exposure.Z = MathF.Max(intensity, 0f);
	}

	/// <summary>Переключает ТОЛЬКО режим, сохраняя key - см.
	/// <see cref="FogPassResources.SetExposureRelative"/>.</summary>
	public void SetExposureRelative(bool exposureRelative)
	{
		_constants->Exposure.X = exposureRelative ? 1f : 0f;
	}

	/// <summary>Мировой базис камеры - ЕДИНИЧНЫЕ векторы, посчитанные вызывающим прямо из eye/target
	/// (см. ModelViewportEnvironment.SetCameraTransform), а не разбором матрицы вида: соглашение о
	/// строках/столбцах легко перепутать, а ошибка в нём даёт объём, «приклеенный» к экрану.
	/// Пушится каждый кадр - оттого кбуфер и заливается командой (см. <see cref="_constants"/>).</summary>
	public void SetCamera(Vector3 right, Vector3 up, Vector3 forward)
	{
		_constants->Right = new Vector4(right, _constants->Right.W);
		_constants->Up = new Vector4(up, _constants->Up.W);
		_constants->Forward = new Vector4(forward, _constants->Forward.W);
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
	/// без пересборки графа (см. FogPassResources - тот же приём).</summary>
	internal void WriteConstants(ICommandBuffer cmd) => cmd.UpdateBuffer(_constantBuffer, 0, _constants);

	public void Release()
	{
		Material.Release();
		_constantBuffer.Release();
		NativeMemory.Free(_constants);
	}
}

/// <summary>
/// Render-graph pass that adds shadowed single scattering - god rays and volumetric fog - to the
/// finished frame by raymarching the view ray against the cascaded shadow map.
///
/// Порядок в кадре: ПОСЛЕ <see cref="SsgiPass"/> и замера яркости, но ДО <see cref="FogPass"/>.
/// Каждое «после» здесь по своей причине:
///
///  * после SSGI - в кадре уже весь непрямой свет, и столбы ложатся поверх готовой картинки;
///  * после замера яркости - ровно та же петля обратной связи, что у тумана (см. подробный разбор
///    в GraphicsPipelineSimple.SignalGraph): яркость рассеяния привязана к адаптации, и стой пасс
///    до замера, объём раздувал бы величину, на которую сам же и множится;
///  * до тумана - аналитическая дымка обязана лечь и на столбы: она ближе к камере, чем дальний
///    конец марша, и не затуманенный столб на затуманенном фоне читается как наклейка.
/// </summary>
public sealed class VolumetricLightPass : RenderGraphPass<VolumetricLightPass.PassData>
{
	public override string Name => "Volumetric Light Pass";

	private readonly VolumetricLightPassResources _resources;
	private readonly IBatchRenderer _batchRenderer;
	private readonly IGpuTexture _colorTarget;
	private readonly IGpuTexture _sceneCopy;
	private readonly IGpuTexture _renderDepth;
	private readonly Ref<Vector2> _viewPortRef;

	public struct PassData
	{
	}

	public VolumetricLightPass(VolumetricLightPassResources resources, IBatchRenderer batchRenderer,
		IGpuTexture colorTarget, IGpuTexture sceneCopy, IGpuTexture renderDepth, Ref<Vector2> viewPortRef)
	{
		_resources = resources;
		_batchRenderer = batchRenderer;
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

		// Shadow map - тоже DepthRead. Формально его уже перевёл ForwardPass, но полагаться на это
		// нельзя: пассы включаются и выключаются по имени (см. GraphicsPipelineSimple.SetPassEnabled),
		// а выключенный пасс не делает и своих переходов состояний.
		_batchRenderer.TransitionShadowMapsForRead(cmd);

		// Читать и писать один таргет нельзя - берём копию кадра. Копия снимается ЗДЕСЬ, а не
		// переиспользуется от SSGI: между ними тот успел дописать в кадр свой bounce, и старый
		// снимок вернул бы кадр без него.
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
