using System.Numerics;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Core;
using DecaEngine.Graphics.Diligent;

namespace DecaEngine.Core;

/// <summary>Набор фич конвейера превью, которые можно менять НА ЖИВОМ конвейере - см.
/// <see cref="GraphicsPipelineSimple.SetFeatures"/>. Всё, что здесь перечислено, переключается без
/// пересоздания окружения и без перезагрузки сцены: ресурсы фичи создаются лениво при первом
/// включении и дальше живут в конвейере, а граф пересобирается заново (дёшево - нативные текстуры
/// переиспользуются из пула графа, см. <see cref="IRenderGraph.ResetPasses"/>).
///
/// Чего здесь НЕТ и почему: MSAA. Число сэмплов пекётся в PSO ВСЕЙ геометрии (см.
/// <see cref="DiligentBatchRenderer"/>), а не только в пост-обработку, поэтому остаётся опцией
/// уровня создания окружения. Авто-экспозиция здесь есть, но она не структурная - см.
/// <see cref="EyeAdaptation"/>.</summary>
public struct PipelineFeatures : IEquatable<PipelineFeatures>
{
	/// <summary>Тени от мирового ключевого света: <see cref="ShadowPass"/> в графе.</summary>
	public bool Shadows;

	/// <summary>Энвайронмент фоном кадра (см. <see cref="SkyPassResources"/>) - рисуется инлайн
	/// внутри <see cref="ForwardPass"/>.</summary>
	public bool SkyBackground;

	/// <summary>Экранное контактное затемнение - инлайн внутри <see cref="ForwardPass"/>.</summary>
	public bool Ssao;

	/// <summary>Техника AO при включённом <see cref="Ssao"/>. Смена пересоздаёт материалы AO
	/// (в них запечён шейдер), но не окружение.</summary>
	public AmbientOcclusionMode AoMode;

	public bool Ssgi;

	/// <summary>Экспонировать кадр по ЗАМЕРЕННОЙ яркости, а не по ручной экспокоррекции. Не
	/// структурная фича: цепочка замера в HDR-конвейере есть всегда (она копеечная - пять дроу
	/// 64x64 и мельче), а тумблер лишь переключает тонемап между измеренной и ручной экспозицией,
	/// а туман/блум/god rays - между относительными и абсолютными единицами. Поэтому её смена не
	/// требует даже пересборки графа.</summary>
	public bool EyeAdaptation;

	public bool Fog;
	public bool Volumetric;
	public bool Bloom;
	public bool ColorGrade;

	/// <summary>Экранные векторы движения (<see cref="MotionVectorPass"/>) - вход временнЫх техник
	/// (апскейлеры DLSS/FSR, TAA). Сами по себе кадр не меняют: пасс лишь заполняет свой буфер, и
	/// пока его никто не читает, фича стоит одного фуллскрин-дроу.
	///
	/// Несовместима с MSAA и потому молча не создаётся при <c>msaaSamples &gt; 1</c> (см.
	/// <see cref="GraphicsPipelineSimple.EnsureResources"/>): апскейлер обязан получать
    /// одно-сэмпловый кадр, он сам себе антиалиасинг.</summary>
	public bool MotionVectors;

	/// <summary>Темпоральный апскейл (<see cref="TemporalUpscalePass"/>): сцена в рендер-разрешении +
	/// джиттер + векторы движения собираются в display-кадр аккумуляцией истории. Требует
	/// <see cref="MotionVectors"/> (иначе ресурсы не создаются) и включает джиттер сам - без него
	/// аккумулировать нечего. Это встроенный управляемый бэкенд слота апскейлера; FSR/DLSS встанут
	/// на его место тем же контрактом входов (HDR-кадр, векторы, джиттер, пара разрешений).</summary>
	public bool TemporalUpscale;

	public bool Equals(PipelineFeatures other)
	{
		return Shadows == other.Shadows &&
		       SkyBackground == other.SkyBackground &&
		       Ssao == other.Ssao &&
		       AoMode == other.AoMode &&
		       Ssgi == other.Ssgi &&
		       EyeAdaptation == other.EyeAdaptation &&
		       Fog == other.Fog &&
		       Volumetric == other.Volumetric &&
		       Bloom == other.Bloom &&
		       ColorGrade == other.ColorGrade &&
		       MotionVectors == other.MotionVectors &&
		       TemporalUpscale == other.TemporalUpscale;
	}

	/// <summary>Отличаются ли наборы чем-то, кроме <see cref="EyeAdaptation"/> - то есть нужна ли
	/// пересборка графа (см. <see cref="GraphicsPipelineSimple.SetFeatures"/>).</summary>
	public bool StructurallyEquals(PipelineFeatures other)
	{
		var a = this;
		var b = other;
		a.EyeAdaptation = false;
		b.EyeAdaptation = false;
		return a.Equals(b);
	}

	public override bool Equals(object? obj) => obj is PipelineFeatures other && Equals(other);

	public override int GetHashCode()
	{
		var hash = new HashCode();
		hash.Add(Shadows);
		hash.Add(SkyBackground);
		hash.Add(Ssao);
		hash.Add((int)AoMode);
		hash.Add(Ssgi);
		hash.Add(EyeAdaptation);
		hash.Add(Fog);
		hash.Add(Volumetric);
		hash.Add(Bloom);
		hash.Add(ColorGrade);
		hash.Add(MotionVectors);
		hash.Add(TemporalUpscale);
		return hash.ToHashCode();
	}
}

/// <summary>
/// <see cref="IGraphicsPipeline"/> without <see cref="ShadowPass"/> - for off-screen consumers
/// (see <see cref="DecaEngine.Editor.ModelViewportEnvironment"/>) that only ever draw unlit
/// geometry through <see cref="SimpleCullingAndRenderSystem"/> and never need shadow-cascade
/// culling/rendering. The <see cref="DirectionalLightCascadeData"/> passed to
/// <see cref="SignalGraph"/> is ignored - callers no longer need to feed it an empty
/// <see cref="DirectionalLightCascadeData"/> just to keep <see cref="ShadowPass"/> a no-op.
///
/// В офскрин-режиме конвейер ВСЕГДА HDR: геометрия и вся пост-обработка живут в линейном RGBA16F
/// (<see cref="PipelineRenderTargets.HdrColorTarget"/>), а отображаемый RGBA8-таргет пишет
/// <see cref="TonemapPass"/> в самом конце. Раньше это включалось вместе с авто-экспозицией и,
/// поскольку формат таргета пекётся в PSO геометрии, требовало пересоздания всего окружения; теперь
/// формат один и тот же при любом наборе фич - см. <see cref="PipelineFeatures"/>.
/// </summary>
public class GraphicsPipelineSimple : IGraphicsPipeline
{
	private readonly IGraphicsApi _api;
	private readonly IBatchRenderer _batchRenderer;
	private readonly IRenderGraph _renderGraph;
	private readonly PipelineRenderTargets? _targets;
	private readonly string? _colorTargetName;
	private readonly IGpuTexture? _environmentMap;
	private readonly uint _msaaSamples;
	private readonly Vector4 _clearColor;

	private PipelineFeatures _features;

	// Ресурсы фич: создаются ЛЕНИВО при первом включении и переживают выключение - повторное
	// включение бесплатно (см. EnsureResources). VRAM возвращается только по явному
	// ReleaseDisabledResources.
	private SkyPassResources? _skyResources;
	private SsaoPassResources? _ssaoResources;
	private SsgiPassResources? _ssgiResources;
	private FogPassResources? _fogResources;
	private VolumetricLightPassResources? _volumetricResources;
	private BloomPassResources? _bloomResources;
	private ColorGradePassResources? _gradeResources;
	private EyeAdaptationPassResources? _eyeAdaptationResources;
	private TonemapPassResources? _tonemapResources;
	private MotionVectorPassResources? _motionVectorResources;
	private MotionVectorDebugPassResources? _motionVectorDebugResources;
	private TemporalUpscalePassResources? _temporalUpscaleResources;

	/// <summary>Нативный бэкенд слота апскейлера (FSR) - см. <see cref="SetNativeUpscaler"/>.
	/// Когда задан и фича включена, встаёт в граф ВМЕСТО встроенного TAAU. Конвейер владеет им
	/// (Release в обоих релиз-путях).</summary>
	private INativeUpscalerBackend? _nativeUpscaler;

	/// <summary>Техника AO, под которую собраны текущие <see cref="_ssaoResources"/> - в их материалы
	/// запечён шейдер, так что смена техники требует пересоздания (но не окружения).</summary>
	private AmbientOcclusionMode _ssaoBuiltMode;

	/// <summary>Были ли тени доступны, когда собирались <see cref="_volumetricResources"/> - его
	/// материал берёт каскадный shadow map через IBatchRenderer.BindShadowResources.</summary>
	private bool _volumetricBuiltWithShadows;

	// Последние данные, с которыми собирался граф: SetFeatures пересобирает его сам, не дожидаясь
	// следующего SignalGraph (тот случается только при смене числа камер).
	private DirectionalLightCascadeData _lastCascadeData;
	private RenderCamerasData _lastCameras;
	private bool _hasGraphData;

	private Ref<Vector2> _viewPortRef;

	/// <summary>Вьюпорт СЦЕНЫ - для пассов, рисующих в рендер-разрешение (геометрия, векторы, AO/GI,
	/// туман, объёмник, блум). При <see cref="_renderScale"/> == 1 это та же нативная память, что у
	/// <see cref="_viewPortRef"/>; при масштабе - уменьшенный размер. Пассы отображаемого разрешения
	/// (тонемап, грейд, отладка векторов, оверлеи) остаются на <see cref="_viewPortRef"/>: тонемап и
	/// есть точка апскейла (см. TonemapPS.hlsl).</summary>
	private Ref<Vector2> _renderViewPortRef;

	/// <summary>Доля отображаемого разрешения, в которую рисуется сцена, - см. <see cref="SetRenderScale"/>.</summary>
	private float _renderScale = 1f;

	// Темпоральный джиттер - см. SetTemporalJitter. Пара запомненных матриц отличает "Execute без
	// нового Update" (в viewData всё ещё лежит НАША джиттернутая матрица - её надо сперва откатить,
	// иначе смещения складывались бы) от "Update переписал viewData" (лежит свежая чистая - откатывать
	// нечего). Сравнение точное: Update пересчитывает view*proj из тех же чисел бит-в-бит.
	private bool _jitterEnabled;
	private uint _jitterFrameIndex;
	private Vector2 _jitterPixels;
	private Matrix4x4 _jitteredViewProj;
	private Matrix4x4 _unjitteredViewProj;

	// Выключенные пассы - ПО ИМЕНИ и с жизнью дольше графа: пересборка графа создаёт объекты пассов
	// заново (каждый со своим Enabled=true по умолчанию), поэтому флаги хранятся здесь и
	// переприкладываются после сборки. Иначе тумблер сбрасывался бы сам собой при любом изменении сцены.
	private readonly Dictionary<string, bool> _passEnabled = new();

	/// <summary>Non-null only in off-screen mode (<paramref name="colorTargetName"/> given to the
	/// constructor) - the pipeline owns and creates these itself, the same way a swap chain would own
	/// the back buffer, so off-screen consumers (see <see cref="DecaEngine.Editor.ModelViewportEnvironment"/>)
	/// resize/bind them through here rather than creating their own.</summary>
	public PipelineRenderTargets? Targets => _targets;

	/// <summary>Текущий набор фич - см. <see cref="SetFeatures"/>.</summary>
	public PipelineFeatures Features => _features;

	/// <summary>HDR-конвейер (линейный кадр + отдельный тонемап) - всегда в офскрин-режиме и никогда
	/// на swap chain.</summary>
	public bool HdrPipeline => _targets?.HdrColorTarget is not null;

	/// <summary>Экспонируется ли кадр по замеренной яркости - см. <see cref="PipelineFeatures.EyeAdaptation"/>.</summary>
	public bool AutoExposure => _features.EyeAdaptation && HdrPipeline;

	/// <summary>Non-null only when a sky background was enabled (see <see cref="SkyPassResources"/>).
	/// Exposed so the preview viewport can push the environment yaw (light-rotation slider) into the
	/// sky shader - см. <see cref="SkyPassResources.SetEnvironmentYaw"/>.</summary>
	public SkyPassResources? SkyResources => _skyResources;

	/// <summary>Non-null once SSAO has been enabled at least once (ресурсы переживают выключение -
	/// см. <see cref="SetFeatures"/>). Живые ручки можно пушить и в выключенном состоянии: они лягут
	/// в кбуферы и оживут вместе с пассом.</summary>
	public SsaoPassResources? SsaoResources => _ssaoResources;

	/// <summary>См. <see cref="SsaoResources"/>.</summary>
	public SsgiPassResources? SsgiResources => _ssgiResources;

	/// <summary>См. <see cref="SsaoResources"/>. Через неё вьюпорт пушит живые ручки дымки,
	/// направление солнца и покадровый базис камеры.</summary>
	public FogPassResources? FogResources => _fogResources;

	/// <summary>См. <see cref="SsaoResources"/>. Через неё вьюпорт пушит живые ручки god rays,
	/// направление солнца и покадровый базис камеры.</summary>
	public VolumetricLightPassResources? VolumetricResources => _volumetricResources;

	/// <summary>См. <see cref="SsaoResources"/>.</summary>
	public BloomPassResources? BloomResources => _bloomResources;

	/// <summary>См. <see cref="SsaoResources"/>. Работает в ОБОИХ конвейерах: ColorTarget всегда
	/// RGBA8 display-space.</summary>
	public ColorGradePassResources? ColorGradeResources => _gradeResources;

	/// <summary>Non-null в офскрин-режиме ВСЕГДА: цепочка замера яркости - часть HDR-конвейера, а не
	/// отдельная фича (см. <see cref="PipelineFeatures.EyeAdaptation"/>).</summary>
	public EyeAdaptationPassResources? EyeAdaptationResources => _eyeAdaptationResources;

	/// <summary>Non-null once motion vectors have been enabled at least once на конвейере БЕЗ MSAA
	/// (см. <see cref="PipelineFeatures.MotionVectors"/>). Отсюда апскейлер возьмёт свой входной
	/// буфер, а отладочный вид - картинку векторов.</summary>
	public MotionVectorPassResources? MotionVectorResources => _motionVectorResources;

	/// <summary>Отладочная заливка кадра векторами движения - создаётся вместе с
	/// <see cref="MotionVectorResources"/> и потому non-null ровно тогда же. Сам показ включается её
	/// живой ручкой <see cref="MotionVectorDebugPassResources.SetDebugView"/>, а не набором фич:
	/// пересобирать граф на дебаг-галку незачем.</summary>
	public MotionVectorDebugPassResources? MotionVectorDebugResources => _motionVectorDebugResources;

	/// <summary>Non-null после первого включения <see cref="PipelineFeatures.TemporalUpscale"/> -
	/// и только на конвейере, где есть векторы движения (см. EnsureResources).</summary>
	public TemporalUpscalePassResources? TemporalUpscaleResources => _temporalUpscaleResources;

	/// <summary>Отдаёт конвейеру нативный бэкенд апскейла (или null - вернуться на TAAU) и
	/// пересобирает граф. Конвейер забирает владение: Release бэкенда - на нём. Вызывающий обязан
	/// сперва дождаться GPU, если конвейер уже рисовал (замена входа тонемапа на живом материале -
	/// см. needsWait в SetFeatures).</summary>
	public void SetNativeUpscaler(INativeUpscalerBackend? backend)
	{
		if (ReferenceEquals(_nativeUpscaler, backend))
		{
			return;
		}

		_nativeUpscaler?.Release();
		_nativeUpscaler = backend;

		if (_hasGraphData)
		{
			RebuildGraph();
		}
	}

	/// <summary>Текущий нативный бэкенд (null = встроенный TAAU).</summary>
	public INativeUpscalerBackend? NativeUpscaler => _nativeUpscaler;

	/// <summary>Non-null ровно тогда же, когда <see cref="EyeAdaptationResources"/>: финальный
	/// тонемап существует только в HDR-конвейере (на swap chain его делает сам UnlitInstancedPS.hlsl).</summary>
	public TonemapPassResources? TonemapResources => _tonemapResources;

	/// <summary>Суб-пиксельный джиттер проекции (подготовка к темпоральному апскейлеру) - живая ручка
	/// без пересборки графа: смещение вбивается в viewProj нулевой камеры прямо в нативной памяти
	/// <see cref="RenderCamerasData.viewData"/> перед каждым Execute, а заморожённый SetupViewData
	/// перечитывает её на каждом реплее (см. <see cref="ApplyTemporalJitter"/>).
	///
	/// БЕЗ темпоральной аккумуляции картинка от него дрожит на суб-пиксель - это ожидаемо: ручка
	/// существует, чтобы отладить сам джиттер и его НЕпротекание в векторы движения до того, как
	/// появится потребитель (TAA/апскейлер), который это дрожание соберёт в детализацию.</summary>
	public void SetTemporalJitter(bool enabled)
	{
		_jitterEnabled = enabled;
	}

	/// <summary>Смещение текущего кадра в ПИКСЕЛЯХ (y - вниз, как идут строки кадра), диапазон
	/// [-0.5..0.5). Именно это значение апскейлер обязан получать как jitter offset. Vector2.Zero,
	/// пока джиттер выключен.</summary>
	public Vector2 CurrentJitterPixels => _jitterPixels;

	/// <summary>Инлайн-оверлей поверх геометрии (см. ForwardPass): дебаг-вид проб и подобное.
	/// Читается при ЗАПИСИ команд графа, так что после смены значения вызывающий обязан позвать
	/// <see cref="InvalidateGraph"/> - иначе заморожённые команды продолжат играть старое.</summary>
	public Action<ICommandBuffer>? InlineOverlay { get; set; }

	/// <summary>Оверлей ОТДЕЛЬНЫМ пассом в самом конце кадра - после ForwardPass (включая резолв
	/// MSAA) и всей пост-обработки, то есть поверх готового отображаемого ColorTarget: контур
	/// выделения Scene View и подобное (см. <see cref="PostOverlayPass"/>). Хук сам привязывает свои
	/// таргеты и вьюпорт. Читается при ЗАПИСИ команд графа - после смены значения вызывающий обязан
	/// позвать <see cref="InvalidateGraph"/>, как и с <see cref="InlineOverlay"/>.</summary>
	public Action<ICommandBuffer>? PostOverlay { get; set; }

	public GraphicsPipelineSimple(IGraphicsApi api, IBatchRenderer batchRenderer, string? debugName = null)
		: this(api, batchRenderer, null, null, 0, 0, new Vector4(0.1f, 0.1f, 0.1f, 1f), debugName: debugName)
	{
	}

	/// <param name="colorTargetName">Non-null selects off-screen mode: the pipeline creates and owns
	/// its own color/depth/scene-copy(/MSAA) targets instead of drawing to the swap chain's back
	/// buffer (see <see cref="DecaEngine.Editor.ModelViewportEnvironment"/>). Null draws straight to
	/// the back buffer and every other creation parameter below is ignored.</param>
	/// <param name="debugName">Имя конвейера в окне отладки рендер-графа (см.
	/// <see cref="GraphicsPipelineRegistry"/>). Null - имя выводится из <paramref name="colorTargetName"/>.</param>
	public GraphicsPipelineSimple(IGraphicsApi api, IBatchRenderer batchRenderer, string? colorTargetName,
		string? depthTargetName, uint width, uint height, Vector4 clearColor, uint msaaSamples = 1,
		bool skyBackground = false, IGpuTexture? environmentMap = null, bool ssao = false, bool enableShadowPass = false,
		AmbientOcclusionMode aoMode = AmbientOcclusionMode.Ssao, bool ssgi = false, bool eyeAdaptation = false,
		bool fog = false, bool bloom = false, bool colorGrade = false, bool volumetric = false,
		bool motionVectors = false, bool temporalUpscale = false, string? debugName = null)
	{
		_api = api;
		_batchRenderer = batchRenderer;
		_clearColor = clearColor;
		_colorTargetName = colorTargetName;
		_environmentMap = environmentMap;
		_msaaSamples = Math.Max(1u, msaaSamples);
		_renderGraph = _api.CreateRenderGraph();

		_features = new PipelineFeatures
		{
			Shadows = enableShadowPass,
			SkyBackground = skyBackground,
			Ssao = ssao,
			AoMode = aoMode,
			Ssgi = ssgi,
			EyeAdaptation = eyeAdaptation,
			Fog = fog,
			Volumetric = volumetric,
			Bloom = bloom,
			ColorGrade = colorGrade,
			MotionVectors = motionVectors,
			TemporalUpscale = temporalUpscale,
		};

		if (colorTargetName is not null)
		{
			// hdr: true БЕЗУСЛОВНО - см. комментарий класса: формат цветового таргета перестал
			// зависеть от набора фич, и тумблеры больше не тянут за собой пересборку PSO геометрии.
			_targets = new PipelineRenderTargets(api, colorTargetName, depthTargetName!, width, height,
				_msaaSamples, hdr: true);

			_viewPortRef = new Ref<Vector2>(new Vector2(width, height));

			// Свой экземпляр: масштаб рендера меняет только его, отображаемый вьюпорт не трогая.
			_renderViewPortRef = new Ref<Vector2>(new Vector2(width, height));
		}
		else
		{
			_viewPortRef = new Ref<Vector2>(_api.WindowHandle.Size);

			// Swap chain масштаба не имеет - оба вьюпорта НАРОЧНО одна и та же нативная память,
			// OnViewportChange обновляет обе разом.
			_renderViewPortRef = _viewPortRef;
			_api.WindowHandle.OnWindowResize += OnViewportChange;
		}

		EnsureResources();

		// Конвейер сам встаёт в реестр - окно отладки рендер-графа набирает свой список именно так
		// (см. GraphicsPipelineRegistry). Реестр держит слабую ссылку и жизнь конвейера не продлевает.
		GraphicsPipelineRegistry.Register(this, debugName ?? DefaultDebugName(colorTargetName));
	}

	/// <summary>Имя конвейера для окна отладки, когда явного не дали: цветовой таргет офскрин-режима
	/// (см. <see cref="PipelineRenderTargets"/>) уже назван по потребителю ("Model Preview Color",
	/// "Prefab Scene Color", ...), так что достаточно отбросить у него хвост " Color".</summary>
	private static string DefaultDebugName(string? colorTargetName)
	{
		if (string.IsNullOrWhiteSpace(colorTargetName))
		{
			return "Simple (swap chain)";
		}

		var name = colorTargetName!.Trim();
		return name.EndsWith(" Color", StringComparison.OrdinalIgnoreCase) ? name[..^" Color".Length] : name;
	}

	/// <summary>Формат таргета, в который рисует геометрия и пост-обработка.</summary>
	private TextureObjectFormat RenderColorFormat =>
		_targets?.RenderColorFormat ?? TextureObjectFormat.R8G8B8A8UNorm;

	/// <summary>Текущий размер офскрин-таргетов. Ресурсы фичи, включённой уже после ресайза вьюпорта,
	/// обязаны создаваться под ФАКТИЧЕСКИЙ размер, а не под тот, что был у конструктора.</summary>
	private (uint Width, uint Height) TargetSize
	{
		get
		{
			var size = _targets!.ColorTarget.Size;
			return (Math.Max(1u, (uint)size.X), Math.Max(1u, (uint)size.Y));
		}
	}

	/// <summary>Фактический размер СЦЕНОВЫХ таргетов - по депту, а не по формуле от масштаба:
	/// ресайзит таргеты вызывающий (см. <see cref="SetRenderScale"/>), и между сменой масштаба и
	/// ресайзом правду знает только сам таргет.</summary>
	private (uint Width, uint Height) RenderTargetSize
	{
		get
		{
			var size = _targets!.DepthTarget.Size;
			return (Math.Max(1u, (uint)size.X), Math.Max(1u, (uint)size.Y));
		}
	}

	/// <summary>Досоздаёт ресурсы, которых требует текущий <see cref="_features"/>, и пересобирает те,
	/// у которых изменилось что-то запечённое (техника AO, наличие теней у god rays). Уже созданные
	/// ресурсы выключенных фич НЕ трогает - в этом весь смысл: повторное включение бесплатно.</summary>
	private void EnsureResources()
	{
		if (_features.SkyBackground && _skyResources is null && _environmentMap is not null)
		{
			_skyResources = new SkyPassResources(_api, _batchRenderer, _environmentMap, _msaaSamples,
				RenderColorFormat);
		}

		if (_targets is null)
		{
			// Swap chain: своих таргетов у конвейера нет, а вся пост-обработка требует хотя бы копии
			// кадра и депта - остаётся один ForwardPass (плюс небо инлайн).
			return;
		}

		// Сценовые ресурсы (AO/GI/блум/векторы) - в РЕНДЕР-разрешении: они работают по глубине и
		// HDR-кадру, а те при масштабе меньше отображаемого. Display-размер остаётся только у
		// грейда и отладки векторов - они живут после точки апскейла (тонемапа).
		var (width, height) = RenderTargetSize;
		var (displayWidth, displayHeight) = TargetSize;
		var renderDepth = _targets.MsaaDepthTarget ?? _targets.DepthTarget;

		// HDR-хребет конвейера: замер яркости и тонемап есть ВСЕГДА, тумблер авто-экспозиции лишь
		// переключает тонемап между измеренной и ручной экспозицией (см. PipelineFeatures.EyeAdaptation).
		// Создаётся ПЕРВЫМ: туман, блум и god rays берут его 1x1-таргет.
		if (_eyeAdaptationResources is null)
		{
			_eyeAdaptationResources = new EyeAdaptationPassResources(_api, _batchRenderer, _colorTargetName!,
				_targets.HdrColorTarget!);
			_tonemapResources = new TonemapPassResources(_api, _batchRenderer, _targets.HdrColorTarget!,
				_eyeAdaptationResources.AdaptationTarget);
		}

		if (_features.Ssao && _ssaoResources is not null && _ssaoBuiltMode != _features.AoMode)
		{
			// В материалы AO запечён шейдер техники - сменить её на живых ресурсах нельзя.
			_ssaoResources.Release();
			_ssaoResources = null;
		}

		if (_features.Ssao && _ssaoResources is null)
		{
			_ssaoResources = new SsaoPassResources(_api, _batchRenderer, _colorTargetName!, width, height,
				renderDepth, _targets.SceneCopyTarget, _msaaSamples, _features.AoMode, RenderColorFormat);
			_ssaoBuiltMode = _features.AoMode;
		}

		if (_features.Ssgi && _ssgiResources is null)
		{
			_ssgiResources = new SsgiPassResources(_api, _batchRenderer, _colorTargetName!, width, height,
				renderDepth, _targets.SceneCopyTarget, _targets.MsaaDepthTarget is not null, RenderColorFormat);
		}

		if (_features.Fog && _fogResources is null)
		{
			_fogResources = new FogPassResources(_api, _batchRenderer, renderDepth, _targets.SceneCopyTarget,
				_targets.MsaaDepthTarget is not null, RenderColorFormat,
				_eyeAdaptationResources!.AdaptationTarget);
		}

		if (_features.Volumetric && _volumetricResources is not null &&
		    _volumetricBuiltWithShadows != _features.Shadows)
		{
			// Материал god rays собран под наличие/отсутствие каскадного shadow map.
			_volumetricResources.Release();
			_volumetricResources = null;
		}

		if (_features.Volumetric && _volumetricResources is null)
		{
			_volumetricResources = new VolumetricLightPassResources(_api, _batchRenderer, renderDepth,
				_targets.SceneCopyTarget, _targets.MsaaDepthTarget is not null, RenderColorFormat,
				_eyeAdaptationResources!.AdaptationTarget, _features.Shadows);
			_volumetricBuiltWithShadows = _features.Shadows;
		}

		if (_features.Bloom && _bloomResources is null)
		{
			_bloomResources = new BloomPassResources(_api, _batchRenderer, _colorTargetName!, width, height,
				_targets.SceneCopyTarget, RenderColorFormat, _eyeAdaptationResources!.AdaptationTarget);
		}

		if (_features.ColorGrade && _gradeResources is null)
		{
			_gradeResources = new ColorGradePassResources(_api, _batchRenderer, _colorTargetName!,
				displayWidth, displayHeight);
		}

		// Векторы движения читают ОДНО-СЭМПЛОВЫЙ депт: при MSAA геометрия рисует в MsaaDepthTarget, и
		// DepthTarget остаётся неразрешённым, а Texture2DMS.Load потребовал бы отдельного варианта
		// шейдера, как у тумана. Заводить его незачем - фича существует ради апскейлеров, а те с MSAA
		// взаимоисключающи (см. PipelineFeatures.MotionVectors), поэтому при MSAA ресурсы просто не
		// создаются и пасс не встаёт в граф.
		if (_features.MotionVectors && _motionVectorResources is null && _targets.MsaaDepthTarget is null)
		{
			_motionVectorResources = new MotionVectorPassResources(_api, _batchRenderer, _colorTargetName!,
				_targets.DepthTarget, width, height);

			// Отладочный вид заводится сразу вместе с буфером, а не по своей галке: без него векторы
			// вообще нечем посмотреть (кадр они не меняют), а стоит он одного материала и одного
			// кбуфера - дешевле, чем отдельная ветка ленивого создания. Ему нужны ОБА размера: буфер
			// векторов живёт в рендер-разрешении, а рисует пасс в отображаемый кадр.
			_motionVectorDebugResources = new MotionVectorDebugPassResources(_api, _batchRenderer,
				_motionVectorResources.MotionTarget, width, height, displayWidth, displayHeight);
		}

		// Слот апскейлера - строго ПОСЛЕ блока векторов: без их буфера аккумулятору нечем
		// репроецировать историю, поэтому при MSAA (векторы не создаются) фича молча не срабатывает -
		// ровно как сами векторы, и окно Graphics предупреждает об этом тем же способом.
		if (_features.TemporalUpscale && _temporalUpscaleResources is null &&
		    _motionVectorResources is not null)
		{
			_temporalUpscaleResources = new TemporalUpscalePassResources(_api, _batchRenderer,
				_colorTargetName!, _targets.HdrColorTarget!, _motionVectorResources.MotionTarget,
				width, height, displayWidth, displayHeight);
		}

		ApplyExposureMode();
	}

	/// <summary>Раздаёт всем, кто считает яркость, текущий режим экспозиции: измеренная против
	/// ручной. Единственное, что делает тумблер авто-экспозиции, - см.
	/// <see cref="PipelineFeatures.EyeAdaptation"/>.</summary>
	private void ApplyExposureMode()
	{
		var auto = AutoExposure;
		_tonemapResources?.SetAutoExposure(auto);
		_fogResources?.SetExposureRelative(auto);
		_bloomResources?.SetExposureRelative(auto);
		_volumetricResources?.SetExposureRelative(auto);
	}

	/// <summary>Меняет набор фич на ЖИВОМ конвейере: ресурсы новых фич создаются лениво, ресурсы
	/// выключенных остаются лежать (повторное включение бесплатно), граф пересобирается. Ни
	/// окружение, ни батч-рендерер, ни сцена при этом не пересоздаются - в этом вся суть.
	///
	/// Смена ОДНОЙ ЛИШЬ авто-экспозиции не пересобирает даже граф: она живая (см.
	/// <see cref="PipelineFeatures.EyeAdaptation"/>).
	///
	/// Ждёт GPU перед освобождением чего бы то ни было (смена техники AO, наличия теней у god rays),
	/// так что вызывать можно из любой точки кадра, где нет незакрытой записи команд.</summary>
	public void SetFeatures(in PipelineFeatures features)
	{
		if (_features.Equals(features))
		{
			return;
		}

		var structural = !_features.StructurallyEquals(features);
		var needsWait =
			(features.Ssao && _ssaoResources is not null && _ssaoBuiltMode != features.AoMode) ||
			(features.Volumetric && _volumetricResources is not null &&
			 _volumetricBuiltWithShadows != features.Shadows) ||
			// Смена АКТИВНОСТИ апскейла переключает вход тонемапа (HDR-кадр <-> выход апскейлера)
			// на живом материале - менять SRB, пока предыдущий кадр в полёте, нельзя (см.
			// EyeAdaptationPass про ту же Vulkan-валидацию). Активность зависит от ОБЕИХ фич:
			// тумблер векторов при включённом апскейле тоже её щёлкает.
			(features.TemporalUpscale && features.MotionVectors) !=
			(_features.TemporalUpscale && _features.MotionVectors);

		if (needsWait)
		{
			_api.WaitForGpuIdle();
		}

		_features = features;
		EnsureResources();

		if (structural)
		{
			RebuildGraph();
		}
	}

	/// <summary>Освобождает ресурсы ВЫКЛЮЧЕННЫХ фич и пул ресурсов графа - точка, где реально
	/// возвращается VRAM. Отдельно от <see cref="SetFeatures"/> намеренно: держать выключенную фичу в
	/// памяти дёшево по времени и дорого по VRAM, и выбор этого размена - за вызывающим.</summary>
	public void ReleaseDisabledResources()
	{
		_api.WaitForGpuIdle();

		if (!_features.SkyBackground)
		{
			_skyResources?.Release();
			_skyResources = null;
		}

		if (!_features.Ssao)
		{
			_ssaoResources?.Release();
			_ssaoResources = null;
		}

		if (!_features.Ssgi)
		{
			_ssgiResources?.Release();
			_ssgiResources = null;
		}

		if (!_features.Fog)
		{
			_fogResources?.Release();
			_fogResources = null;
		}

		if (!_features.Volumetric)
		{
			_volumetricResources?.Release();
			_volumetricResources = null;
		}

		if (!_features.Bloom)
		{
			_bloomResources?.Release();
			_bloomResources = null;
		}

		if (!_features.ColorGrade)
		{
			_gradeResources?.Release();
			_gradeResources = null;
		}

		if (!_features.MotionVectors)
		{
			_motionVectorResources?.Release();
			_motionVectorResources = null;
			_motionVectorDebugResources?.Release();
			_motionVectorDebugResources = null;
		}

		// Также при выключенных ВЕКТОРАХ: без их буфера апскейлер держал бы SRB на освобождённую
		// текстуру (создание гейтится на векторы - см. EnsureResources, симметрично и освобождение).
		if (!_features.TemporalUpscale || !_features.MotionVectors)
		{
			_temporalUpscaleResources?.Release();
			_temporalUpscaleResources = null;
		}

		// Ресурсы могли быть привязаны в заморожённых командах - граф пересобираем, а его
		// собственный пул чистим следом.
		RebuildGraph();
		_renderGraph.TrimResourcePool();
	}

	/// <summary>Перепривязывает материалы пост-обработки к текущим depth/scene-copy/HDR таргетам
	/// ПОСЛЕ их Resize (см. ModelPreviewViewport.ResizeTargets) - no-op для ещё не созданных.</summary>
	public void RebindSsaoTargets()
	{
		if (_targets is null)
		{
			return;
		}

		// Сценовые ресурсы - в рендер-разрешении, display остаётся у грейда и отладки векторов
		// (см. EnsureResources).
		var (renderWidth, renderHeight) = RenderTargetSize;
		var (displayWidth, displayHeight) = TargetSize;

		var renderDepth = _targets.MsaaDepthTarget ?? _targets.DepthTarget;
		_ssaoResources?.RebindTargets(renderDepth, _targets.SceneCopyTarget);
		_ssgiResources?.RebindTargets(renderDepth, _targets.SceneCopyTarget);
		_fogResources?.RebindTargets(renderDepth, _targets.SceneCopyTarget);
		_volumetricResources?.RebindTargets(renderDepth, _targets.SceneCopyTarget);
		_bloomResources?.RebindTargets(_targets.SceneCopyTarget, renderWidth, renderHeight);
		_gradeResources?.RebindTargets(displayWidth, displayHeight);
		_eyeAdaptationResources?.RebindTargets(_targets.HdrColorTarget!);

		// Одно-сэмпловый депт, а не renderDepth: см. EnsureResources - при MSAA этих ресурсов
		// попросту нет.
		_motionVectorResources?.RebindTargets(_targets.DepthTarget, renderWidth, renderHeight);

		// Строго ПОСЛЕ ребинда самих векторов: их RebindTargets ресайзит MotionTarget, пересоздавая
		// нативную текстуру, и привяжись дебаг (и апскейлер - он тоже читает буфер векторов) раньше -
		// они держали бы уничтоженную.
		if (_motionVectorResources is not null)
		{
			_motionVectorDebugResources?.RebindTargets(_motionVectorResources.MotionTarget,
				renderWidth, renderHeight, displayWidth, displayHeight);
			_temporalUpscaleResources?.RebindTargets(_targets.HdrColorTarget!,
				_motionVectorResources.MotionTarget, renderWidth, renderHeight,
				displayWidth, displayHeight);
			_nativeUpscaler?.Resize(_targets.HdrColorTarget!, _targets.DepthTarget,
				_motionVectorResources.MotionTarget, renderWidth, renderHeight,
				displayWidth, displayHeight);
		}

		// Тонемап - ПОСЛЕДНИМ: при включённом апскейле его вход - OutputTarget апскейлера, а тот
		// только что пересоздан его же Resize выше.
		var nativeActive = _features.TemporalUpscale && _features.MotionVectors &&
			_nativeUpscaler is not null && _motionVectorResources is not null;
		var upscaleActive = !nativeActive && _features.TemporalUpscale && _features.MotionVectors &&
			_temporalUpscaleResources is not null && _motionVectorResources is not null;
		_tonemapResources?.RebindTargets(nativeActive
			? (IGpuTexture)_nativeUpscaler!.OutputTarget
			: upscaleActive
				? (IGpuTexture)_temporalUpscaleResources!.OutputTarget
				: _targets.HdrColorTarget!);
	}

	public void OnViewportChange()
	{
		_viewPortRef.Set(_api.WindowHandle.Size);
	}

	/// <summary>See <see cref="GraphicsPipeline.SetOffscreenViewportSize"/>. Обновляет ОБА вьюпорта:
	/// отображаемый - переданным размером, сценовый - им же, умноженным на <see cref="RenderScale"/>.</summary>
	public void SetOffscreenViewportSize(Vector2 size)
	{
		var renderSize = SceneSizeFor(size);
		var change = _viewPortRef.Value != size || _renderViewPortRef.Value != renderSize;
		_viewPortRef.Set(size);
		_renderViewPortRef.Set(renderSize);
		if (change)
		{
			_renderGraph.Invalidate();
		}
	}

	/// <summary>Текущая доля отображаемого разрешения, в которую рисуется сцена (1 = масштаба нет).</summary>
	public float RenderScale => _renderScale;

	/// <summary>Размер, в котором при текущем масштабе живут СЦЕНОВЫЕ таргеты (депт, HDR-кадр,
	/// scene-copy, MSAA, векторы, AO/GI) для данного отображаемого размера. Отображаемый ColorTarget
	/// масштабом не трогается - апскейл делает тонемап (см. TonemapPS.hlsl).</summary>
	public Vector2 SceneSizeFor(Vector2 displaySize) => _renderScale >= 1f
		? displaySize
		: new Vector2(MathF.Max(1f, MathF.Round(displaySize.X * _renderScale)),
			MathF.Max(1f, MathF.Round(displaySize.Y * _renderScale)));

	/// <summary>Меняет масштаб рендера сцены (0.25..1). Только запоминает значение и возвращает
	/// true при смене: РЕСАЙЗ сценовых таргетов - забота вызывающего, у которого есть весь ресайз-путь
	/// (GPU-барьер, перепривязка _SceneColor к резидентным материалам, ImGui-биндинги - см.
	/// ModelPreviewViewport.ResizeTargets). Пока вызывающий не отресайзил, конвейер продолжает рисовать
	/// в старом размере - вьюпорты обновятся вместе с ресайзом через SetOffscreenViewportSize.</summary>
	public bool SetRenderScale(float scale)
	{
		scale = Math.Clamp(scale, 0.25f, 1f);
		if (_targets is null || _renderScale == scale)
		{
			return false;
		}

		_renderScale = scale;
		return true;
	}

	/// <summary>Имена пассов текущего графа - для отладочного списка в UI.</summary>
	public IReadOnlyList<string> PassNames => _renderGraph.PassNames;

	/// <summary>Включает/выключает пасс по имени - отладочный тумблер окна графа. Безопасен для
	/// любого пасса: выключенный исключается из графа целиком, вместе со своими переходами состояний
	/// (см. IRenderGraphPass.Enabled). Запомненное значение переживает пересборку графа.
	///
	/// Для фич конвейера пользуйтесь <see cref="SetFeatures"/>: там выключение ещё и снимает работу
	/// по подготовке ресурсов, а не только сам пасс.</summary>
	public void SetPassEnabled(string name, bool enabled)
	{
		_passEnabled[name] = enabled;
		_renderGraph.SetPassEnabled(name, enabled);
	}

	/// <summary>Выключен ли пасс (по запомненному флагу, а не по состоянию графа).</summary>
	public bool IsPassEnabled(string name) => !_passEnabled.TryGetValue(name, out var enabled) || enabled;

	/// <summary>See <see cref="GraphicsPipeline.InvalidateGraph"/>.</summary>
	public void InvalidateGraph()
	{
		_renderGraph.Invalidate();
	}

	public void Initialize()
	{
	}

	public void SignalGraph(DirectionalLightCascadeData renderScene, RenderCamerasData renderViews)
	{
		_lastCascadeData = renderScene;
		_lastCameras = renderViews;
		_hasGraphData = true;
		RebuildGraph();
	}

	/// <summary>Пересобирает СПИСОК пассов под текущий набор фич. Нативные ресурсы при этом
	/// переиспользуются - см. <see cref="IRenderGraph.ResetPasses"/>, поэтому вызов дешёвый и
	/// уместен на каждый тумблер.</summary>
	private void RebuildGraph()
	{
		if (!_hasGraphData)
		{
			return;
		}

		_renderGraph.ResetPasses();

		if (_features.Shadows)
		{
			_renderGraph.AddPass(new ShadowPass(_batchRenderer, _lastCascadeData));
		}

		// AO рисуется инлайн внутри ForwardPass - между opaque- и transmissive-дроу, чтобы стекло
		// преломляло затенённый фон, но само экранным AO не глушилось (см.
		// SsaoPassResources.WriteInlineCommands).
		// В HDR-конвейере вся геометрия и пост-обработка живут в линейном HDR-таргете, а
		// ColorTarget получает кадр только от TonemapPass в самом конце.
		var renderColor = _targets?.RenderColorTarget;

		// Сценовые пассы (отсюда и до блума включительно) рисуют в РЕНДЕР-вьюпорт: при масштабе
		// меньше 1 их таргеты уменьшены, а в отображаемое разрешение кадр поднимает тонемап.
		_renderGraph.AddPass(new ForwardPass(_batchRenderer, _lastCameras, _renderViewPortRef, renderColor,
			_targets?.DepthTarget, _clearColor, _targets?.SceneCopyTarget,
			_features.SkyBackground ? _skyResources : null,
			_targets?.MsaaColorTarget, _targets?.MsaaDepthTarget,
			_features.Ssao ? _ssaoResources : null,
			() => InlineOverlay));

		// Векторы движения - сразу за геометрией: пассу нужна только готовая глубина, и до всей
		// пост-обработки он успевает заполнить буфер, который дальше заберёт апскейлер. Кадр он не
		// трогает, так что на порядок остальных пассов не влияет.
		if (_features.MotionVectors && _motionVectorResources is not null)
		{
			_renderGraph.AddPass(new MotionVectorPass(_motionVectorResources, _targets!.DepthTarget,
				_renderViewPortRef));
		}

		// SSGI собирает bounce из уже отрисованного кадра, поэтому идёт последним - после AO
		// в источнике света уже есть контактные тени, и bounce их корректно учитывает.
		if (_features.Ssgi && _ssgiResources is not null)
		{
			var renderDepth = _targets!.MsaaDepthTarget ?? _targets.DepthTarget;
			_renderGraph.AddPass(new SsgiPass(_ssgiResources, renderColor!, _targets.SceneCopyTarget, renderDepth, _renderViewPortRef));
		}

		// Замер яркости - по ГОТОВОМУ линейному кадру (со всем непрямым светом), тонемап - следом:
		// экспозиция этого же кадра, кривая и sRGB-энкод в отображаемый ColorTarget.
		//
		// Пасс стоит в графе ВСЕГДА, а не только при включённой авто-экспозиции: он копеечный (пять
		// дроу 64x64 и мельче), зато его отсутствие оставляло бы 1x1-таргеты адаптации ни разу не
		// записанными - на Vulkan это UNDEFINED-лейаут под SRV тонемапа. См. PipelineFeatures.EyeAdaptation.
		if (_eyeAdaptationResources is not null)
		{
			_renderGraph.AddPass(new EyeAdaptationPass(_eyeAdaptationResources, _targets!.HdrColorTarget!));
		}

		// Туман - ПОСЛЕ SSGI (дымка обязана лечь и на непрямой свет), но и ПОСЛЕ ЗАМЕРА ЯРКОСТИ,
		// хотя физически рассеянный средой свет входит в экспозицию наравне с остальным.
		//
		// Причина практическая и замеренная: яркость дымки привязана к адаптации (иначе ручка цвета
		// бессмысленна - см. FogPassResources.SetExposure), и стой пасс ДО замера, туман раздувал бы
		// ровно ту величину, на которую сам же и множится. На интерьере Sponza эта петля давала
		// среднюю яркость кадра 104 против 36 без тумана - дымка съедала кадр целиком. Ценой решения
		// сильный туман больше не заставляет камеру прижиматься; для инструмента предпросмотра
		// предсказуемость важнее физической честности.
		//
		// Тонемап при этом остаётся ПОСЛЕДНИМ: туман кладётся в ЛИНЕЙНЫЙ кадр, до кривой.
		//
		// Объёмный свет (god rays) идёт ПЕРЕД туманом: столбы обязаны затуманиваться дальней дымкой
		// наравне с геометрией, иначе на затуманенном фоне они читаются как наклейка. Причина
		// «после замера яркости» у него ровно та же, что у тумана, - см. абзац выше.
		if (_features.Volumetric && _volumetricResources is not null)
		{
			var volumetricDepth = _targets!.MsaaDepthTarget ?? _targets.DepthTarget;
			_renderGraph.AddPass(new VolumetricLightPass(_volumetricResources, _batchRenderer,
				renderColor!, _targets.SceneCopyTarget, volumetricDepth, _renderViewPortRef));
		}

		if (_features.Fog && _fogResources is not null)
		{
			var renderDepth = _targets!.MsaaDepthTarget ?? _targets.DepthTarget;
			_renderGraph.AddPass(new FogPass(_fogResources, renderColor!, _targets.SceneCopyTarget,
				renderDepth, _renderViewPortRef));
		}

		// Блум - ПОСЛЕ тумана (рассеяние в оптике происходит уже со светом, дошедшим до объектива,
		// включая свечение самой дымки) и, как и туман, ПОСЛЕ замера яркости: его порог привязан к
		// адаптации, и стой пасс до замера, ореол раздувал бы ровно ту величину, на которую сам же
		// и опирается. До тонемапа - складывать свет можно только в линейном пространстве.
		if (_features.Bloom && _bloomResources is not null)
		{
			_renderGraph.AddPass(new BloomPass(_bloomResources, renderColor!, _targets!.SceneCopyTarget,
				_renderViewPortRef));
		}

		// Слот апскейлера - ПОСЛЕДНИМ в рендер-разрешении: туман, god rays и блум уже вложены в
		// HDR-кадр (апскейлер обязан видеть то же, что увидел бы глаз), а аккумулировать надо
		// ЛИНЕЙНЫЙ свет - смешивание после кривой тонемапа занижало бы яркие субпиксельные детали.
		// Тонемап при включённом апскейле читает его выход и работает 1:1.
		// Нативный бэкенд (FSR) занимает слот вместо встроенного TAAU, когда задан. Гейт и на САМУ
		// ФИЧУ векторов, не только на ресурсы: ресурсы переживают выключение (ленивая схема), и без
		// этой проверки снятая галка векторов оставляла бы апскейлер в графе на протухшем буфере -
		// пасс векторов из графа уже ушёл, буфер замер с последним кадром.
		var nativeUpscaleActive = _features.TemporalUpscale && _features.MotionVectors &&
			_nativeUpscaler is not null && _motionVectorResources is not null;
		var upscaleActive = !nativeUpscaleActive &&
			_features.TemporalUpscale && _features.MotionVectors &&
			_temporalUpscaleResources is not null && _motionVectorResources is not null;

		if (nativeUpscaleActive)
		{
			_renderGraph.AddPass(new NativeUpscalePass(_nativeUpscaler!, _targets!.HdrColorTarget!,
				_targets.DepthTarget, _motionVectorResources!.MotionTarget));
		}
		else if (upscaleActive)
		{
			_renderGraph.AddPass(new TemporalUpscalePass(_temporalUpscaleResources!,
				_targets!.HdrColorTarget!, _motionVectorResources!.MotionTarget, _viewPortRef));
		}

		if (_tonemapResources is not null && Environment.GetEnvironmentVariable("DECA_NO_EA_PASS") != "1")
		{
			// Вход тонемапа зависит от тумблера апскейла - перепривязка здесь же, где строится граф:
			// SetFeatures при смене тумблера уже дождался GPU (см. needsWait).
			var tonemapSource = nativeUpscaleActive
				? (IGpuTexture)_nativeUpscaler!.OutputTarget
				: upscaleActive
					? (IGpuTexture)_temporalUpscaleResources!.OutputTarget
					: _targets!.HdrColorTarget!;
			_tonemapResources.RebindTargets(tonemapSource);
			_tonemapResources.SetForceOpaque(nativeUpscaleActive);
			_renderGraph.AddPass(new TonemapPass(_tonemapResources, tonemapSource,
				_targets!.ColorTarget, _viewPortRef));
		}

		// Грейдинг и виньетка - ПОСЛЕ тонемапа (их шкалы определены в отображаемом пространстве) и
		// ДО оверлеев: контур выделения и гизмо - интерфейс, художественная коррекция их трогать не
		// должна. См. ColorGradePass.
		if (_features.ColorGrade && _gradeResources is not null)
		{
			_renderGraph.AddPass(new ColorGradePass(_gradeResources, _targets!.ColorTarget, _viewPortRef));
		}

		// Отладочный вид векторов - ПОСЛЕ тонемапа и грейдинга (он работает по отображаемому кадру, см.
		// MotionVectorDebugPS.hlsl), но ДО оверлеев: гизмо и контур выделения обязаны остаться видны,
		// иначе в этом режиме нельзя выбрать объект.
		//
		// В графе он стоит ВСЕГДА, когда есть буфер векторов, а показ включается ручкой в его кбуфере:
		// выключенный пасс дискардит весь экран, зато галка не пересобирает граф (см.
		// MotionVectorDebugPassResources).
		if (_features.MotionVectors && _motionVectorResources is not null &&
		    _motionVectorDebugResources is not null)
		{
			_renderGraph.AddPass(new MotionVectorDebugPass(_motionVectorDebugResources,
				_motionVectorResources.MotionTarget, _targets!.ColorTarget, _viewPortRef));
		}

		// Последним - оверлей отдельным пассом поверх готового кадра (см. PostOverlay): пасс
		// добавляется всегда, при null-хуке он не пишет ни одной команды.
		_renderGraph.AddPass(new PostOverlayPass(() => PostOverlay));

		// Пассы созданы заново и пришли с Enabled=true - возвращаем запомненные тумблеры.
		foreach (var (name, enabled) in _passEnabled)
		{
			_renderGraph.SetPassEnabled(name, enabled);
		}
	}

	public void Execute()
	{
		// Порядок жёсткий: сперва ОТКАТИТЬ джиттер прошлого кадра (если Execute идёт без нового
		// Update, в viewData всё ещё наша матрица), затем защёлкнуть векторы движения по ЧИСТОЙ
		// матрице - апскейлеры ждут векторы без джиттера, суб-пиксельное дрожание они вычитают сами
		// по переданному jitter offset, - и только потом вбить смещение нового кадра.
		LatchRenderResolution();
		UnwindTemporalJitter();
		LatchMotionVectors();
		ApplyTemporalJitter();

		// Апскейлеру - джиттер ИМЕННО этого кадра (потому после ApplyTemporalJitter): шейдер вычитает
		// его из выборки текущего кадра, см. TemporalUpscalePS.hlsl.
		if (_features.TemporalUpscale && _features.MotionVectors)
		{
			if (_nativeUpscaler is not null)
			{
				_nativeUpscaler.SetFrameParams(_jitterPixels);
			}
			else
			{
				_temporalUpscaleResources?.SetFrameParams(_jitterPixels);
			}
		}

		_renderGraph.Execute();
	}

	/// <summary>При масштабе рендера подменяет в viewData нулевой камеры размер вьюпорта на
	/// РЕНДЕР-разрешение: шейдеры сцены строят экранные UV и пиксельные шаги как viewport.zw из
	/// View-кбуфера (см. FogCommon/GtaoCommon/SsaoCommon), а камера хранит отображаемый размер.
	/// Тот же приём живой нативной памяти, что у джиттера, и идемпотентен: Update переписывает
	/// viewData каждый кадр, повторный Execute перезапишет то же значение.</summary>
	private void LatchRenderResolution()
	{
		if (_renderScale >= 1f || _targets is null || !_hasGraphData ||
		    !_lastCameras.IsCreated || _lastCameras.Length == 0)
		{
			return;
		}

		var size = _renderViewPortRef.Value;
		ref var viewData = ref _lastCameras.viewData.GetRef(0, false);
		viewData.viewport = new Vector4(viewData.viewport.X, viewData.viewport.Y, size.X, size.Y);
	}

	/// <summary>Возвращает в viewData нулевой камеры матрицу без джиттера, если там всё ещё лежит
	/// наша джиттернутая (Execute без Update между ними). См. комментарий у полей джиттера.</summary>
	private void UnwindTemporalJitter()
	{
		if (!_hasGraphData || !_lastCameras.IsCreated || _lastCameras.Length == 0)
		{
			return;
		}

		ref var viewData = ref _lastCameras.viewData.GetRef(0, false);
		if (viewData.viewProj == _jitteredViewProj)
		{
			viewData.viewProj = _unjitteredViewProj;
		}
	}

	/// <summary>Вбивает суб-пиксельное смещение кадра в живую viewProj нулевой камеры. Halton(2,3) -
	/// стандартная низкорасходящаяся последовательность фаз джиттера; сдвиг - перенос в clip space
	/// ПОСЛЕ проекции (строчная конвенция: постумножение, x' = x + jx*w), так что деление на w
	/// раскладывает его ровно в константный суб-пиксельный сдвиг всего кадра.
	///
	/// Только нулевая камера - по той же причине, что и в <see cref="LatchMotionVectors"/>. Матрицы
	/// вида, куллинга и каскадов не трогаются: фрустум от суб-пикселя не меняется, а тени джиттерить
	/// прямо вредно - шимеринг краёв теней апскейлер собрать не сможет.</summary>
	private void ApplyTemporalJitter()
	{
		_jitterPixels = Vector2.Zero;

		// Активный апскейлер включает джиттер сам: без него аккумулятору нечего собирать -
		// каждый кадр приносил бы одни и те же сэмплы, и детализация не восстанавливалась бы.
		// Гейт на фичу векторов - тот же, что у слота в RebuildGraph: без них апскейл неактивен,
		// и дрожать кадру незачем.
		var jitterActive = _jitterEnabled ||
			(_features.TemporalUpscale && _features.MotionVectors &&
			 (_temporalUpscaleResources is not null || _nativeUpscaler is not null));

		if (!jitterActive || !_hasGraphData || !_lastCameras.IsCreated || _lastCameras.Length == 0)
		{
			return;
		}

		// РЕНДЕР-вьюпорт: джиттер - доли пикселя того разрешения, в котором растеризуется сцена.
		var size = _renderViewPortRef.Value;
		if (size.X < 1f || size.Y < 1f)
		{
			return;
		}

		// 16 фаз: хватает и TAA, и апскейлеру до 2x (FSR берёт 8*(scale^2)). Halton начинается с
		// индекса 1 - нулевой дал бы (0,0) и выпадал бы из равномерности покрытия пикселя.
		uint phase = _jitterFrameIndex++ % 16 + 1;
		_jitterPixels = new Vector2(Halton(phase, 2) - 0.5f, Halton(phase, 3) - 0.5f);

		// Пиксели -> NDC: пиксель по x это 2/width; y в NDC растёт вверх, а строки кадра идут вниз.
		var ndc = new Vector2(_jitterPixels.X * 2f / size.X, -_jitterPixels.Y * 2f / size.Y);

		ref var viewData = ref _lastCameras.viewData.GetRef(0, false);
		_unjitteredViewProj = viewData.viewProj;
		viewData.viewProj = _unjitteredViewProj * Matrix4x4.CreateTranslation(ndc.X, ndc.Y, 0f);
		_jitteredViewProj = viewData.viewProj;
	}

	private static float Halton(uint index, uint radix)
	{
		float result = 0f;
		float fraction = 1f / radix;
		while (index > 0)
		{
			result += index % radix * fraction;
			index /= radix;
			fraction /= radix;
		}

		return result;
	}

	/// <summary>Защёлкивает матрицу репроджекции ровно один раз за кадр - ЗДЕСЬ, а не в пассе:
	/// команды графа замораживаются и проигрываются много кадров подряд, так что посчитанное внутри
	/// <see cref="MotionVectorPass.WriteCommands"/> застыло бы вместе с ними (см.
	/// <see cref="MotionVectorPassResources.UpdateFromView"/>).
	///
	/// viewProj берётся из той же живой нативной памяти, по которой рисует <see cref="ForwardPass"/>
	/// (<see cref="RenderCamerasData.viewData"/>), - иначе векторы считались бы по камере, отличной
	/// от той, которой отрисован кадр, и разъезд был бы ровно в один кадр: самый неприятный вид
	/// ошибки, потому что на неподвижной камере он невидим.</summary>
	private void LatchMotionVectors()
	{
		if (_motionVectorResources is null || !_hasGraphData)
		{
			return;
		}

		// Камера НУЛЕВАЯ: буфер векторов один на кадр, а несколько камер (сплит-скрин) потребовали бы
		// своего буфера каждой. Ступень 1 этого не покрывает - см. комментарий класса
		// MotionVectorPassResources.
		if (!_lastCameras.IsCreated || _lastCameras.Length == 0)
		{
			// Кадр без камеры (сцена ещё не собрана): историю рвём, иначе следующий живой кадр
			// репроецировался бы по матрице, от которой его отделяет неизвестно сколько времени.
			_motionVectorResources.ResetHistory();
			return;
		}

		_motionVectorResources.UpdateFromView(_lastCameras.viewData.GetRef(0, false).viewProj);
	}

	/// <summary>Освобождает рендер-граф (заморожённые команды и пины ресурсов) - для пересоздания
	/// превью-окружения на лету (см. ModelPreviewViewport.RecreateEnvironment). Вызывающий обязан
	/// сперва дождаться GPU (Flush + WaitForIdle).</summary>
	public void Release()
	{
		// Первым делом - вон из реестра, чтобы окно отладки не успело выбрать освобождаемый конвейер
		// (см. GraphicsPipelineRegistry).
		GraphicsPipelineRegistry.Unregister(this);

		_renderGraph.Release();
		_ssaoResources?.Release();
		_ssgiResources?.Release();
		_fogResources?.Release();
		_volumetricResources?.Release();
		_bloomResources?.Release();
		_gradeResources?.Release();
		_tonemapResources?.Release();
		_eyeAdaptationResources?.Release();
		_motionVectorResources?.Release();
		_motionVectorDebugResources?.Release();
		_temporalUpscaleResources?.Release();
		_nativeUpscaler?.Release();
		_skyResources?.Release();
		_targets?.Release();
	}

	public RenderGraphDebugSnapshot DebugSnapshot => _renderGraph.DebugSnapshot;
	public RenderGraphDebugHistory DebugHistory => _renderGraph.DebugHistory;
}

/// <summary>Финальный пасс-хук <see cref="GraphicsPipelineSimple.PostOverlay"/>: исполняет
/// пользовательский оверлей ОТДЕЛЬНЫМ пассом рендер-графа после всей пост-обработки, поверх уже
/// готового отображаемого кадра (контур выделения Scene View и подобное). Хук читается при записи
/// команд (тот же паттерн, что у InlineOverlay в ForwardPass) - смена значения требует
/// <see cref="GraphicsPipelineSimple.InvalidateGraph"/>; null-хук не пишет команд вовсе.</summary>
public sealed class PostOverlayPass : RenderGraphPass<PostOverlayPass.PassData>
{
	public override string Name => "Post Overlay Pass";

	private readonly Func<Action<ICommandBuffer>?> _overlay;

	public struct PassData
	{
	}

	public PostOverlayPass(Func<Action<ICommandBuffer>?> overlay)
	{
		_overlay = overlay;
	}

	public override PassData Setup(IRenderGraphBuilder builder)
	{
		return default;
	}

	public override void WriteCommands(in PassData value, in IRenderGraphContext context)
	{
		_overlay()?.Invoke(context.cmd);
	}
}
