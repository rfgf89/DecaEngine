using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using DecaEngine.Core;
using DecaEngine.Core.Diagnostics;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
using DecaEngine.Graphics.ProbeGi;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using Hexa.NET.ImGui;
using DecaEngine.Scene;
using DecaEngine.Animation;

namespace DecaEngine.Editor
{
	/// <summary>
	/// ????????? (????????????? ?? ??????? ????? / Game View) ????????? ??????-?????: ????
	/// EntityStore, DiligentBatchRenderer, GraphicsPipeline ? off-screen color/depth render-???????.
	/// ???????????? <see cref="InspectorWindow"/> ??? 3D-?????? .gltf/.glb ???????, ????????? ?
	/// <see cref="AssetBrowserWindow"/> - ?????? ??????????? ????? <see cref="ModelLoader"/> ? ????
	/// EntityStore (????? ?? ???????????? ?? ? ??????? ??????, ?? ? EntityStore-?? ????????
	/// Inspector-?), ? ?????? ???? ?????????? ? ??????????? offscreen-????????, ??????? ?????
	/// ???????????? ????? ImGui.Image (?????????? ????, ??? <see cref="GameViewWindow"/>
	/// ?????????? ??????? ????? ????? ???? ??????????? IRenderHandle).
	/// </summary>
	public partial class ModelPreviewViewport
	{
		/// <summary>Sub-mesh view mode, selectable from the Inspector while a single sub-mesh is isolated
		/// (see <see cref="InspectorWindow.RenderModelPreview"/>). Irrelevant for the whole-model view,
		/// which is always rendered in Lighting/PBR (see <see cref="ApplyPreviewSettingsToMaterials"/>).
		/// Orthogonal to <see cref="WireframeEnabled"/> - the wireframe overlay can be toggled on top of
		/// either mode.</summary>
		public enum SubMeshPreviewMode
		{
			Highlight,
			Channel,
			Lighting,
		}

		/// <summary>Debug channel visualized in <see cref="SubMeshPreviewMode.Channel"/>.</summary>
		public enum PreviewChannel
		{
			Normal,
			Uv,
			Tangent,
		}

		private const uint InitialWidth = 256;
		private const uint InitialHeight = 256;
		private const float CameraFovDegrees = ModelViewportEnvironment.CameraFovDegrees;

		/// <summary>
		/// How long the requested ImGui image size must stay unchanged before the off-screen targets
		/// are actually resized - resizing recreates GPU resources (see <see cref="ResizeTargets"/>),
		/// so applying it every frame while the user is still dragging the window edge would mean a
		/// GPU stall (<see cref="Diligent.IDeviceContext.WaitForIdle"/>) on every single frame of the
		/// drag instead of once after they let go.
		/// </summary>
		private const float ResizeSettleSeconds = 0.3f;

		private readonly IGraphicsApi _graphicsApi;
		private readonly EditorSettings _editorSettings;
		private readonly ModelStore _modelStore;
		private readonly SharedViewportResources _sharedResources;
		private ModelViewportEnvironment _env;

		/// <summary>Есть ли у объёмного света каскадные тени - без них god rays невозможны
		/// (см. VolumetricLightPassResources.ShadowsAvailable). Читается окном Graphics, чтобы
		/// предупредить человека, а не оставлять его крутить мёртвый ползунок.</summary>
		public bool VolumetricShadowsAvailable => _env?.VolumetricShadowsAvailable ?? false;

		/// <summary>Текущее оффскрин-окружение - для отладочных инструментов (дамп shadow map
		/// каскадов в окне Graphics). Пересоздаётся при смене env-level настроек - не кэшировать.</summary>
		public ModelViewportEnvironment Environment => _env;

		// Конфигурация, с которой создано ТЕКУЩЕЕ окружение (env-level опции пекутся в его
		// таргеты/пассы/PSO): диф с настройками в OnGraphicsSettingsChanged решает, нужно ли
		// пересоздание (см. RecreateEnvironment).
		private bool _appliedSsao;
		private AmbientOcclusionMode _appliedAoMode;
		private bool _appliedSsgi;
		private bool _appliedSky;
		private string _appliedHdrPath = "";
		private bool _appliedAniso;

		// Потолок стороны текстуры печётся в декодер загрузчика (см. BuildLoadOptions), то есть
		// живёт в уже залитых на GPU текстурах: применить его можно только перечитыванием модели.
		// Раньше он молча ждал следующей загрузки - смена ручки не давала НИЧЕГО до ручного
		// переоткрытия ассета; теперь он в дифе перезагрузки, наравне с анизотропией.
		private int _appliedMaxTextureSize;

		// Авто-экспозиция - опция уровня создания окружения: с ней конвейер превью становится HDR
		// (линейный RGBA16F-кадр + отдельный TonemapPass), а формат таргета печётся в PSO.
		private bool _appliedEyeAdaptation;

		// Туман - тоже опция УРОВНЯ СОЗДАНИЯ: пассу нужны депт и scene-copy, он создаётся
		// вместе с конвейером (см. GraphicsPipelineSimple), так что галка требует пересоздания окружения.
		private bool _appliedFog;

		// Объёмный свет - тоже уровня создания: пассу нужны депт, scene-copy и shadow map
		// (см. VolumetricLightPass), он создаётся вместе с конвейером.
		private bool _appliedVolumetric;

		// Блум - тоже уровня создания: он владеет своей цепочкой таргетов (см. BloomPassResources).
		private bool _appliedBloom;

		// Грейдинг - тоже уровня создания: пасс владеет своей копией кадра.
		private bool _appliedColorGrade;

		// Векторы движения - пасс владеет своим RG16F-буфером (см. MotionVectorPassResources).
		private bool _appliedMotionVectors;

		// Последний ImGuiRender из Render() - RecreateEnvironment должен отвязать ImGui-биндинг
		// старого таргета до его освобождения (см. ResizeTargets - тот же порядок).
		private ImGuiRender? _lastImGuiRender;

		// Заявка на пересоздание окружения из OnGraphicsSettingsChanged; исполняется в начале
		// Update() - до записи кадра, когда старые биндинги ещё нигде не задействованы.
		private bool _pendingEnvironmentRecreate;

		private readonly List<Entity> _instanceEntities = new();

		private string? _loadedPath;
		private int _loadedSubMesh = -1;
		private string? _loadError;
		private string? _loadingPath;
		private int _loadingSubMesh = -1;

		// --- Активность вьюпорта -------------------------------------------------------------------
		// Модель редактора грузится РОВНО В ОДНОМ месте: либо здесь (Inspector в режиме Model), либо в
		// PrefabSceneViewport (открыт префаб), но никогда в обоих сразу - иначе одна и та же модель
		// держит два набора материалов/инстансов и два полных кадра офскрин-конвейера. Переключает
		// EditorManager.OnUpdate по режиму Inspector-а (см. SetActive); неактивное превью отдаёт
		// модель с GPU и не пишет кадр вовсе (его никто не видит: RenderModelPreview зовётся только в
		// режиме Model). По умолчанию активно - пробы (PreviewLoopProbe/FullLoopProbe) гоняют вьюпорт
		// без EditorManager-а и SetActive не зовут.
		private bool _active = true;
		private bool _activeRequested = true;

		/// <summary>Путь/сабмеш, снятые уходом в паузу, - возвращаются загрузкой при активации, если к
		/// тому моменту не запрошена другая модель (см. <see cref="ApplyPendingActiveChange"/>).</summary>
		private string? _suspendedPath;
		private int _suspendedSubMesh = -1;

		/// <summary>Идёт возврат сохранённой моделью после паузы - гасит диагностику «FULL reload» в
		/// <see cref="LoadModel"/>: резидента здесь нет по построению.</summary>
		private bool _restoringAfterResume;

		/// <summary>Стриминг модели превью (см. <see cref="ModelStreamer"/>): фоновая загрузка,
		/// покадровая финализация и владение жизненным циклом ModelLoader-а. Превью - эксклюзивный
		/// потребитель: перед загрузкой нового файла предыдущий ПОЛНОСТЬЮ очищается (ClearAll),
		/// поэтому резидентна всегда максимум одна модель. Кадровый шаг - ModelStreamingSystem в
		/// SystemRoot окружения.</summary>
		private readonly ModelStreamer _streamer;

		/// <summary>Ссылка стримера на грузящуюся/загруженную модель текущего выбора; null - ничего
		/// не запрошено. Готовность опрашивает PollPendingLoad.</summary>
		private ModelStreamer.Resident? _streamingModel;

		// Радиус, посчитанный последним FrameAll (см. его комментарий) - PollPendingLoad пушит AO
		// world-range из него сам, ПОСЛЕ своего Flush()+WaitForIdle() барьера.
		private float _framedRadius;

		// Резидентная модель: тот же .gltf/.glb, что уже полностью распарсен и зарегистрирован в
		// _env.BatchRenderer с предыдущего LoadModel - переключение сабмеша той же модели (см.
		// LoadModel) должно просто перенаселить сцену данными, уже сидящими в памяти/на GPU, а не
		// заново читать файл с диска и гонять прогресс-бар (см. ModelIconBaker, тот же приём).
		private string? _residentPath;
		private ModelLoader? _residentModel;

		// --- Probe GI (DDGI-lite, см. ProbeGi.cs) ---------------------------------------------------
		// ПРОГРЕССИВНЫЙ CPU-бейк сетки irradiance-проб + sky visibility. Сессия живёт вместе с
		// резидентной моделью, а фоновая задача крутит по одному раунду (RaysPerRound лучей на пробу,
		// единицы миллисекунд) за раз: поле показывается уже после первого раунда и уточняется
		// дальше, вместо секундного «бейка одним куском» с дебаунсом. Поворот света накопленное не
		// выбрасывает (см. ProbeGiBakeSession.SetLighting) - пересоздание сессии нужно только при
		// смене модели или параметров сетки/качества.
		private ProbeGiBaker? _probeBaker;

		/// <summary>Фоновая сборка BVH под пробы (см. BeginProbeSession): на тяжёлой сцене это
		/// десятки секунд чистого CPU, и на потоке рендера она вешала редактор целиком уже ПОСЛЕ
		/// того, как модель показалась.</summary>
		private Task<ProbeGiBaker>? _probeBakerTask;

		/// <summary>Модель, по которой считается <see cref="_probeBakerTask"/>: результат для чужой
		/// (успели переключить) выбрасывается, а её освобождение обязано задачу дождаться.</summary>
		private ModelLoader? _probeBakerModel;

		/// <summary>Время сборки/чтения BVH и откуда он взялся - отладочная строка в консоли: без
		/// неё «почему пробы появились только через полминуты» не диагностируется.</summary>
		private System.Diagnostics.Stopwatch? _probeBakerSw;
		private volatile bool _probeBakerFromCache;

		private ProbeGiBakeSession? _probeSession;
		private Task? _probeRoundTask;
		private ProbeGiTextures? _probeTextures;
		private Vector3 _probeBoundsMin, _probeBoundsMax;
		private float _probeSessionDelay = -1f;  // секунды до пересоздания сессии; <0 = не запрошено
		private int _probeTextureGeneration;     // суффикс имён GPU-текстур (имена в API уникальны)

		// GPU-путь раунда (см. ProbeRoundGpu). Живёт вместе с сессией и сам владеет своими буферами
		// (BVH, поле, кэш); атласы в этом режиме заводятся с UAV и пишутся шейдером напрямую, минуя
		// упаковку на CPU. null = крутим раунды на CPU, как раньше.
		private ProbeRoundGpu? _probeGpu;

		// Конвейеры GPU-раунда живут дольше сессии и модели: их компиляция стоит ~650 мс, а сессия
		// пересоздаётся на каждое изменение настроек. Пока компиляция сидела в конструкторе
		// ProbeRoundGpu, каждая правка ползунка означала полусекундный стопор на потоке рендера, и
		// кадровый цикл swap chain этого не переживал.
		/// <summary>Сколько порций GPU-раунда выпускать за кадр. Порция стоит ~2 мс счёта, так что
		/// восемь укладываются в кадр с запасом; ограничение нужно лишь чтобы на плотной сетке один
		/// кадр не утащил раунд целиком и не подвесил презентацию.</summary>
		private const int ProbeChunksPerFrame = 8;

		private ProbeRoundPipelines? _probePipelines;
		private DiligentGraphicsApi? _probePipelinesApi;

		/// <summary>Аппаратные структуры ускорения под GPU-раунд (см. ProbeSceneAccel). null, если
		/// аппаратная трассировка выключена или недоступна.
		///
		/// Живут по МОДЕЛИ, а не по сессии: BLAS строится из геометрии, а её изменение настроек
		/// probe-GI не трогает. Пересоздание на каждую правку ползунка добавляло за цикл десятки
		/// мегабайт (BLAS плюс scratch на сотни тысяч треугольников) и упиралось в исчерпание
		/// ресурсов - на третьей запечке не создавалась уже текстура 80x80.</summary>
		private ProbeSceneAccel? _probeAccel;

		/// <summary>Позы инстансов, под которыми собран текущий TLAS - по ним PollProbeAccel решает,
		/// шевелилась ли сцена. Порядок - порядок ProbeInstancedGeometry.Instances.</summary>
		private readonly List<Matrix4x4> _probeInstancePoses = new();

		// Пересборка TLAS однажды сорвалась - больше не пробуем (иначе отказ повторялся бы каждый
		// кадр движения). Сцена для трассировки замирает, пробы продолжают считаться.
		private bool _probeAccelFrozen;

		/// <summary>Дебаг-вид проб (шарики, см. ProbeDebugOverlay). Помнит атласы, под которые
		/// создан, - их пересоздание (новая сессия, смена сетки) пересобирает набор.</summary>
		private readonly List<(ProbeDebugOverlay Overlay, ProbeGiTextures Textures)> _probeDebugOverlays = new();
		private bool _probeDebugFailed;

		/// <summary>Умеет ли устройство inline-трассировку - по этому флагу окно Graphics гасит
		/// галочку аппаратного ускорения.</summary>
		public bool RayTracingSupported => _graphicsApi.RayTracing >= RayTracingSupport.Inline;

		/// <summary>TLAS для RT-теней (режим «Ray-traced» комбо Shadow filtering, см.
		/// FEATURE_RT_SHADOWS в UnlitInstancedPS.hlsl). Отдельный от <see cref="_probeAccel"/>:
		/// тот живёт от бейкера проб и только при аппаратном GPU-пути GI, а теневым лучам TLAS
		/// нужен независимо от проб. Строится из GPU-мешей резидентной модели
		/// (DiligentRayTracingScene), в превью модель статична - пересборка только на смену
		/// модели/сабмеша.</summary>
		private DiligentRayTracingScene? _rtShadowScene;

		public ModelPreviewViewport(IGraphicsApi graphicsApi, EditorSettings editorSettings, ModelStore modelStore,
			SharedViewportResources sharedResources = null)
		{
			_graphicsApi = graphicsApi;
			_editorSettings = editorSettings;
			_modelStore = modelStore;

			// CLI-гарнессы (FullLoopProbe/PreviewLoopProbe) конструируют этот вьюпорт изолированно и не
			// делят контейнер ни с чем - им годится собственный, локальный (см. class-doc
			// SharedViewportResources: "или per CLI-harness").
			_sharedResources = sharedResources ?? new SharedViewportResources(graphicsApi);

			_env = CreateEnvironment();

			_streamer = new ModelStreamer(_env, _modelStore, _graphicsApi, BuildLoadOptions);
			_env.Root.Add(new ModelStreamingSystem(_streamer));

			ApplyGraphicsSettings();

			// Настройки из окна Settings (см. SettingsWindow.PreviewGraphicsApplied): вьюпорт
			// один и живёт всю сессию редактора, отписка не требуется.
			SettingsWindow.PreviewGraphicsApplied += OnGraphicsSettingsChanged;
		}

		/// <summary>Создаёт превью-окружение по текущим настройкам и запоминает применённую
		/// env-level конфигурацию (для дифа в <see cref="OnGraphicsSettingsChanged"/>). Тени
		/// создаются всегда: их пасс дёшев и no-op-ится live через ShadowSettings.Enabled.</summary>
		private ModelViewportEnvironment CreateEnvironment()
		{
			_appliedSsao = _editorSettings.PreviewSsao;
			_appliedAoMode = _editorSettings.PreviewAoMode;
			_appliedSsgi = _editorSettings.PreviewSsgi;
			_appliedSky = _editorSettings.PreviewSkyBackground;
			_appliedHdrPath = ResolveEnvironmentHdrPath(_editorSettings.PreviewEnvironmentHdr) ?? "";
			_appliedAniso = _editorSettings.PreviewAnisotropicFiltering;
			_appliedMaxTextureSize = ClampedMaxTextureSize();
			_appliedRtShadows = RtShadowsEnabled();
			_appliedEyeAdaptation = _editorSettings.PreviewEyeAdaptation;
			_appliedFog = _editorSettings.PreviewFog;
			_appliedVolumetric = _editorSettings.PreviewVolumetric;
			_appliedBloom = _editorSettings.PreviewBloom;
			_appliedColorGrade = _editorSettings.PreviewColorGrade;
			_appliedMotionVectors = _editorSettings.PreviewMotionVectors;

			var env = new ModelViewportEnvironment(_graphicsApi, InitialWidth, InitialHeight,
				"Model Preview Color", "Model Preview Depth", _sharedResources,
				skyBackground: _appliedSky,
				environmentHdrPath: _appliedHdrPath.Length > 0 ? _appliedHdrPath : null,
				ssao: _appliedSsao,
				shadows: true,
				aoMode: _appliedAoMode,
				ssgi: _appliedSsgi,
				eyeAdaptation: _appliedEyeAdaptation,
				fog: _appliedFog,
				bloom: _appliedBloom,
				colorGrade: _appliedColorGrade,
				volumetric: _appliedVolumetric,
				// SSR тянет векторы за собой (репроекция истории) - как TemporalUpscale.
				motionVectors: _appliedMotionVectors || _editorSettings.PreviewSsr,
				temporalUpscale: _appliedMotionVectors && _editorSettings.TemporalUpscale,
				upscalerBackend: _appliedMotionVectors && _editorSettings.TemporalUpscale
					? Math.Clamp(_editorSettings.UpscalerBackend, 0, 2)
					: 0,
				ssr: _editorSettings.PreviewSsr,
				// RT-фолбэк догоняет ApplyPipelineFeatures - accel проб в момент создания окружения
				// ещё не существует (см. PrefabSceneViewport.CreateEnvironment, та же причина).
				ssrRayTraced: false);

			// Полный набор каскадов, как у Scene View: один орто-каскад на все баунды (прежний
			// дефолт) на сцене-уровне (Sponza) даёт мыльную тень - вся карта растянута на габарит.
			// Выставляется ДО первого кадра: SimpleCullingAndRenderSystem замораживает под это
			// ёмкость DirectionalLightCascadeData (см. CascadeCount там).
			if (env.ShadowSettings != null)
			{
				env.ShadowSettings.CascadeCount = ShadowRenderer.MaxCascades;
			}

			return env;
		}

		private void FrameAll(Vector3 min, Vector3 max)
		{
			// ????????? bounds - ???? ??? ???????? NaN ??? Infinity, ?????????? ?????????? ????????
			if (float.IsNaN(min.X) || float.IsNaN(min.Y) || float.IsNaN(min.Z) ||
			    float.IsNaN(max.X) || float.IsNaN(max.Y) || float.IsNaN(max.Z) ||
			    float.IsInfinity(min.X) || float.IsInfinity(min.Y) || float.IsInfinity(min.Z) ||
			    float.IsInfinity(max.X) || float.IsInfinity(max.Y) || float.IsInfinity(max.Z))
			{
				// Если bounds некорректны, используем значения по умолчанию
				_orbitTarget = Vector3.Zero;
				_distance = 4f;
				_yaw = -0.6f;
				_pitch = 0.35f;
				_framedRadius = 0f;
				return;
			}

			_orbitTarget = (min + max) * 0.5f;

			// Half-diagonal of the (mesh-bounds-based, see PopulateFromScene) AABB, used as a
			// bounding-sphere radius around _orbitTarget - simple and good enough for auto-framing.
			var radius = MathF.Max(0.05f, (max - min).Length() * 0.5f);

			// Те же баунды питают ортокамеру мирового света (см.
			// SimpleCullingAndRenderSystem.BuildLightData) - тени пересчитаются со следующего кадра.
			if (_env.ShadowSettings != null)
			{
				_env.ShadowSettings.BoundsCenter = _orbitTarget;
				_env.ShadowSettings.BoundsRadius = radius;
			}

			// Радиус AO в мировых единицах от габаритов модели (см. SsaoPassResources.SetWorldRange):
			// с экранным радиусом контактная тень под нависающей геометрией (корона ферзя и т.п.)
			// схлопывалась при приближении камеры - нависание выпадало из радиуса поиска. НЕ пушим
			// его отсюда - FrameAll выполняется из PopulateFromScene ДО Flush()+WaitForIdle() в
			// PollPendingLoad, а SetConstant трогает GPU-буфер и помечает AoMaterial dirty (следующий
			// draw пересоберёт его PSO) на ImmediateContext, которым в этот момент может ещё
			// пользоваться предыдущий, ещё не дождавшийся кадр - гонка с рендером основной сцены (см.
			// PollPendingLoad, который пушит его сам, уже после барьера).
			_framedRadius = radius;

			// Distance at which a sphere of this radius exactly fills the vertical FOV, plus a
			// small margin so the model isn't touching the viewport edges.
			_distance = ModelViewportGeometry.ComputeFramingDistance(radius, CameraFovDegrees);

			_yaw = -0.6f;
			_pitch = 0.35f;
		}

		/// <summary>Показывается ли превью прямо сейчас (Inspector в режиме Model). В паузе модель с
		/// GPU снята и кадр не пишется - см. <see cref="SetActive"/>.</summary>
		public bool IsActive => _activeRequested;

		/// <summary>
		/// Включает/ставит на паузу превью модели. Заявка исполняется в начале ближайшего
		/// <see cref="Update"/> (там мы под GPU-локом редактора): пауза отдаёт модель с GPU целиком,
		/// активация грузит обратно ту же, если за время паузы не выбрали другую. Зовёт
		/// EditorManager.OnUpdate по режиму Inspector-а, чтобы модель редактора была загружена ровно в
		/// одном месте - здесь ИЛИ в <see cref="PrefabSceneViewport"/>.
		/// </summary>
		public void SetActive(bool active)
		{
			_activeRequested = active;
		}

		private void ApplyPendingActiveChange()
		{
			_active = _activeRequested;

			if (!_active)
			{
				// Что показывали (или как раз грузили) - вернём при активации.
				_suspendedPath = _loadedPath ?? _loadingPath;
				_suspendedSubMesh = _loadedPath != null ? _loadedSubMesh : _loadingSubMesh;

				CancelPendingLoad();
				UnloadResidentModel();
				return;
			}

			var restorePath = _suspendedPath;
			var restoreSubMesh = _suspendedSubMesh;
			_suspendedPath = null;
			_suspendedSubMesh = -1;

			// Заявка, пришедшая ПОКА мы стояли на паузе (Asset Browser -> InspectorWindow.ShowModel
			// зовёт LoadModel сразу, а активность переключается лишь следующим кадром), главнее
			// сохранённого выбора - иначе клик по новой модели откатывался бы на старую.
			if (restorePath != null && _loadedPath == null && _loadingPath == null)
			{
				_restoringAfterResume = true;
				try
				{
					LoadModel(restorePath, restoreSubMesh);
				}
				finally
				{
					_restoringAfterResume = false;
				}
			}
		}

		/// <summary>
		/// ?????????? ??????????? (?????????) ECS/render-graph ?????? ?? ???? ????. ??????
		/// ?????????? ??? ? ???? ????????? (??. EditorManager.OnUpdate) ??? ??? ?? GPU-?????, ??? ?
		/// ???????? ?????, ????????? ?????????? ????? IGraphicsApi/??????????.
		/// </summary>
		public void Update(float deltaTime, float time)
		{
			// Переход активности исполняется ЗДЕСЬ, а не в SetActive: внутри выгрузка с барьером
			// GPU (см. UnloadResidentModel), а под локом редактора мы только в Update.
			if (_activeRequested != _active)
			{
				ApplyPendingActiveChange();
			}

			// Пауза: модель уже отдана, кадр не пишем - его всё равно некому показать (см. _active).
			if (!_active)
			{
				return;
			}

			// Собственный accel SSR (RT-фолбэк без probe GI) - опрос покадровый, см. метод.
			PollSsrOwnRayScene(deltaTime);

			if (_pendingEnvironmentRecreate)
			{
				_pendingEnvironmentRecreate = false;
				RecreateEnvironment();
			}

			// После возможного пересоздания окружения и ДО записи кадра - безопасная точка для
			// смены бэкенда апскейлера (GPU-барьер + init-команды NGX).
			ApplyPendingUpscalerSettings();

			PollPendingLoad();
			PollProbeBake(deltaTime);
			PollBvhDebugOverlay();
			PollProbeDebugOverlay();

			// Live-ручки probe-GI/солнца из окна Graphics - пуш по факту изменения значения
			// (дёшево: сравнение кортежа), в начале Update, до записи кадра.
			var liveProbeParams = (_editorSettings.ProbeGiShadowFloor, _editorSettings.ProbeGiSkyShadowFloor,
				_editorSettings.ProbeGiSpecularFloor, _editorSettings.ProbeGiSunIntensity,
				_editorSettings.ProbeGiAmbientBoost, _editorSettings.ProbeGiNormalBias,
				_editorSettings.PreviewProbeGi, _editorSettings.ProbeGiDebugView);
			if (liveProbeParams != _lastLiveProbeParams)
			{
				_lastLiveProbeParams = liveProbeParams;
				ApplyPreviewSettingsToMaterials();
			}

			// Шаг времени временной адаптации - каждый кадр, до записи: заморожённый командный буфер
			// графа берёт его из кбуфера (см. EyeAdaptationPassResources.SetDeltaTime). Кламп сверху -
			// защита от «прыжка» экспозиции после долгих пауз редактора (загрузка модели, бейк проб).
			_env.SetEyeAdaptationDeltaTime(Math.Min(deltaTime, 0.1f));

			try
			{
				var eye = ModelViewportGeometry.ComputeOrbitEye(_orbitTarget, _distance, _yaw, _pitch);
				_env.SetCameraTransform(eye, _orbitTarget);

				// Кадр исполняется ВСЕГДА, даже без единой модели - пустая сцена показывает небо
				// окружения (батч-рендерер безопасен при нуле инстансов), а загрузку первой модели
				// ведёт ModelStreamingSystem ВНУТРИ Root.Update (курица и яйцо иначе не разрешается:
				// стример шагает по позиции камерной сущности стора, которую SetCameraTransform выше
				// только что обновил). Раньше эта ветка при отсутствии модели вызывала
				// _streamer.Tick(...) напрямую и выходила без записи кадра - теперь это делает сама
				// система внутри Root, а кадр (пустое небо) пишется как обычно (см. PrefabSceneViewport.Update
				// - тот же приём).
				_env.Root.Update(new UpdateTick(deltaTime, time));
				_env.Pipeline.Execute();
			}
			catch (Exception ex)
			{
				// This runs every frame while a model is loaded (unlike the one-time load path in
				// PollPendingLoad, which already has its own try/catch) - EditorManager.OnUpdate calls
				// this BEFORE the main scene's _pipeline.Execute()/Present() inside the same GPU lock
				// (see the ordering comment there), so an exception escaping here would skip Present()
				// for this frame and, since the model stays loaded, do so again on every frame after -
				// i.e. the editor would appear to freeze/stop presenting entirely instead of just
				// losing this one preview.
				_loadError = ex.Message;
				EngineLog.Add(LogLevel.Error, $"Model preview: render failed for '{_loadedPath}': {ex.Message}");
				ClearInstances();
			}
		}

		/// <summary>
		/// ?????? ImGui.Image ?????? ? ???????????? orbit/pan/zoom ???? ???? ??? ??? (??????????
		/// <see cref="PrefabSceneViewport"/>). ?????? ?????????? ?? ??????????? ImGui-????, ???????
		/// ??? ?????????? (??. InspectorWindow.RenderModelPreview).
		/// </summary>
		public void Render(ImGuiRender imGuiRender, Vector2 size)
		{
			_lastImGuiRender = imGuiRender;

			if (size.X <= 1f || size.Y <= 1f)
			{
				return;
			}

			if (!_textureBound)
			{
				// Bind immediately, not just allocate the ImGui texture id - otherwise the very first
				// ~ResizeSettleSeconds of the viewport's life (see TrackAndApplyResize) show an unbound
				// image (nothing drawn) instead of the empty-scene sky, because BindRenderTarget below
				// used to run ONLY on a settled resize. Same fix as PrefabSceneViewport.Render.
				_textureRef = imGuiRender.GetNewTexture();
				imGuiRender.BindRenderTarget(_textureRef.GetTexID(), _env.ColorTarget);
				_textureBound = true;
			}

			bool resized = TrackAndApplyResize(imGuiRender, size);

			if (resized)
			{
				// Resizing recreates the underlying GPU texture (see DiligentRenderTarget.Resize), so
				// the shader resource binding ImGui captured at bind time now points at a disposed
				// texture - rebind onto the same ImTextureID rather than allocating a new one each time,
				// which would otherwise leak an entry in ImGuiDiligentRender's texture table per resize.
				imGuiRender.BindRenderTarget(_textureRef.GetTexID(), _env.ColorTarget);
			}

			var cursor = ImGui.GetCursorScreenPos();

			// Вертикальный градиент-подложка в духе glTF Sample Viewer: сам оффскрин-таргет
			// очищается с alpha 0 (см. ModelViewportEnvironment), так что фон картинки прозрачен и
			// ImGui-блендинг кладёт модель поверх этого прямоугольника. Цвета - строго нейтральные
			// (R=G=B): тонированные значения здесь выходили на экран с перекошенным оттенком
			// (тёплый низ вместо холодного - похоже на R/B-swap в цветовом пути ImGui-бэкенда),
			// а нейтральному серому перестановка каналов безразлична. Должны совпадать с backdrop
			// в UnlitInstancedPS.hlsl (просвет стекла) и PreviewProbe.CompositeOverBackdrop.
			var backdropDrawList = ImGui.GetWindowDrawList();
			uint backdropTop = ImGui.GetColorU32(new Vector4(0.55f, 0.55f, 0.55f, 1f));
			uint backdropBottom = ImGui.GetColorU32(new Vector4(0.26f, 0.26f, 0.26f, 1f));
			backdropDrawList.AddRectFilledMultiColor(cursor, cursor + size,
				backdropTop, backdropTop, backdropBottom, backdropBottom);

			ImGui.Image(_textureRef, size);

			bool hovered = ImGui.IsItemHovered();
			HandleCameraInput(hovered);

			if (!HasModel)
			{
				var drawList = ImGui.GetWindowDrawList();
				var text = _loadError ?? "No model loaded";
				var textSize = ImGui.CalcTextSize(text);
				var textPos = cursor + (size - textSize) * 0.5f;
				drawList.AddText(textPos, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.6f)), text);
			}
		}

		/// <summary>
		/// Debounces <see cref="ResizeTargets"/>: only applies once the requested ImGui image size has
		/// stayed unchanged for <see cref="ResizeSettleSeconds"/>, i.e. once the user has finished
		/// resizing the window/panel rather than on every frame while they're still dragging it.
		///
		/// Сюда же сведён и МАСШТАБ РЕНДЕРА: это единственная точка кадра, где ресайз таргетов
		/// безопасен (картинка превью ещё не добавлена в draw list ImGui, а вызывающий после
		/// ресайза сам перевешивает биндинг - см. Render). Настройка перечитывается каждый кадр,
		/// а не пушится событием: так расхождение сценовых таргетов с масштабом чинится само -
		/// и после драга слайдера (с тем же дебаунсом, что у ресайза окна), и после пересоздания
		/// окружения, у которого масштаб сбрасывается в 1.</summary>
		private bool TrackAndApplyResize(ImGuiRender imGuiRender, Vector2 imGuiSize)
		{
			var width = (uint)Math.Max(1, MathF.Round(imGuiSize.X));
			var height = (uint)Math.Max(1, MathF.Round(imGuiSize.Y));
			var requestedSize = new Vector2(width, height);

			var scale = Math.Clamp(_editorSettings.RenderScale, 0.25f, 1f);
			_env.SetRenderScale(scale);

			if (requestedSize != _pendingSize || scale != _pendingRenderScale)
			{
				_pendingSize = requestedSize;
				_pendingRenderScale = scale;
				_resizeIdleSeconds = 0f;
				return false;
			}

			// Сценовые таргеты сверяются по ФАКТУ (депт против SceneSizeFor), а не по «изменилась ли
			// настройка»: факт переживает пересоздание окружения и любые пропущенные события.
			if (requestedSize == _env.ColorTarget.Size &&
			    _env.Pipeline.SceneSizeFor(requestedSize) == _env.DepthTarget.Size)
			{
				return false;
			}

			_resizeIdleSeconds += ImGui.GetIO().DeltaTime;
			if (_resizeIdleSeconds < ResizeSettleSeconds)
			{
				return false;
			}

			return ResizeTargets(imGuiRender, requestedSize);
		}

		/// <summary>
		/// Resizes the off-screen color/depth targets and camera viewport to match the given size so
		/// the preview renders at native resolution instead of a fixed one.
		/// </summary>
		private bool ResizeTargets(ImGuiRender imGuiRender, Vector2 newSize)
		{
			var width = (uint)newSize.X;
			var height = (uint)newSize.Y;

			// Resize disposes and recreates the underlying GPU texture (see
			// DiligentRenderTarget.Resize) - without waiting for any in-flight GPU work that still
			// reads/writes the old texture (this engine currently has no frame-in-flight fence, see
			// DiligentGraphicsApi.Present) to finish first, disposing it here races the GPU and can
			// crash the driver with an access violation. Flush() must precede WaitForIdle(): otherwise
			// commands recorded on the immediate context but not yet submitted are still pending when
			// WaitForIdle() returns (see the same Flush()+WaitForIdle() pairing in
			// DiligentGraphicsUtility's buffer readback), so the old texture could still be disposed out
			// from under work the GPU hasn't actually started yet.
			_env.DilApi.ImmediateContext.Flush();
			_env.DilApi.ImmediateContext.WaitForIdle();

			// Must happen before Resize() disposes the color target's texture/views below: the cached
			// ImGui shader-resource binding for this texture id holds a reference to a view of the
			// CURRENT (about to be stale) texture, and releasing that binding after the view is gone
			// crashes instead of cleanly releasing it (see ImGuiRender.ReleaseRenderTargetBinding).
			imGuiRender.ReleaseRenderTargetBinding(_textureRef.GetTexID());

			// Сценовые таргеты (депт/HDR/scene-copy/AO/GI) - в РЕНДЕР-размере: при масштабе
			// рендера меньше 1 сцена рисуется в уменьшенные, а до отображаемого её поднимает тонемап
			// (см. GraphicsPipelineSimple.SetRenderScale). ColorTarget всегда display - его сэмплирует
			// ImGui.
			var sceneSize = _env.Pipeline.SceneSizeFor(newSize);

			_env.ColorTarget.Resize(newSize);
			_env.DepthTarget.Resize(sceneSize);

			// Снимок сцены обязан совпадать по размеру с таргетом геометрии (CopyTexture копирует 1:1),
			// а после Resize это уже ДРУГАЯ нативная текстура - резидентным материалам нужно перепривязать
			// _SceneColor, иначе они продолжат сэмплировать уничтоженную (см. RegisterModelResources).
			_env.SceneCopyTarget.Resize(sceneSize);

			// G-buffer отражений живёт в рендер-размере вместе с дептом (его читают SSR-пассы).
			_env.Pipeline.Targets?.NormalRoughnessTarget?.Resize(sceneSize);
			_env.Pipeline.Targets?.EnvFactorTarget?.Resize(sceneSize);
			_env.AoTarget?.Resize(sceneSize);
			_env.GiTarget?.Resize(sceneSize);

			// HDR-кадр - размером со сцену, как и остальные; таргеты цепочки замера яркости
			// фиксированного размера и ресайза не требуют (см. EyeAdaptationPass).
			_env.HdrColorTarget?.Resize(sceneSize);

			_env.RebindPostProcessTargets();
			if (_residentModel != null)
			{
				foreach (var material in _residentModel.materialObjects.Values)
				{
					material.SetTexture("_SceneColor", _env.SceneCopyTarget);
				}
			}

			// Must happen immediately after Resize(), before any code below that could throw (e.g.
			// GetComponent/RecalculateProjection) - Resize() already disposed the old GPU
			// texture/views, so if Invalidate() were skipped the render graph would keep replaying
			// its frozen command buffer, which still references those disposed views, on every
			// subsequent frame until something else happens to invalidate it.
			_env.Pipeline.SetOffscreenViewportSize(newSize);

			ref var cameraComponent = ref _env.CameraEntity.GetComponent<CameraComponent>();
			cameraComponent.data.viewport = new Vector4(0, 0, width, height);
			cameraComponent.data.aspect = width / (float)height;
			cameraComponent.RecalculateProjection();

			return true;
		}

		private void HandleCameraInput(bool hovered)
		{
			var io = ImGui.GetIO();

			if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
			{
				_orbiting = true;
			}
			if (_orbiting && ImGui.IsMouseReleased(ImGuiMouseButton.Right))
			{
				_orbiting = false;
			}

			if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Middle))
			{
				_panning = true;
			}
			if (_panning && ImGui.IsMouseReleased(ImGuiMouseButton.Middle))
			{
				_panning = false;
			}

			if (_orbiting)
			{
				var delta = io.MouseDelta;
				_yaw -= delta.X * 0.01f;
				_pitch = Math.Clamp(_pitch - delta.Y * 0.01f, -1.5f, 1.5f);
			}
			else if (_panning)
			{
				var delta = io.MouseDelta;
				var right = new Vector3(MathF.Cos(_yaw), 0f, -MathF.Sin(_yaw));
				var panScale = MathF.Max(0.01f, _distance * 0.001f);
				_orbitTarget -= right * delta.X * panScale;
				_orbitTarget += Vector3.UnitY * delta.Y * panScale;
			}

			if (hovered && io.MouseWheel != 0f)
			{
				_distance = Math.Clamp(_distance + io.MouseWheel * _distance * 0.1f, 0.2f, 1500f);
			}
		}
	}
}



