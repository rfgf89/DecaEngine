using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using DecaEngine.Core;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Core;
using DecaEngine.Graphics.Diligent;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using Hexa.NET.ImGui;

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
	public class ModelPreviewViewport
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
		private uint _appliedMsaa;
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

		/// <summary>Дополнительные МЕЛКИЕ каскады probe-GI (см. SampleProbeGi в UnlitInstancedPS):
		/// сессия + атласы + GPU-раунд на каскад. Каскад i покрывает бокс в 2^i раз меньше базового
		/// вокруг центра сцены той же плотностью, то есть ячейкой в 2^i раз мельче. Базовый объём
		/// остаётся в старых полях (_probeSession и далее) - он гарантия покрытия, каскады только
		/// уточняют. Существуют ТОЛЬКО в GPU-пути реального времени: CPU-фолбэк остаётся
		/// однообъёмным, каскады - фича динамики, а не запечки.</summary>
		private readonly List<(ProbeGiBakeSession Session, ProbeGiTextures Textures, ProbeRoundGpu Gpu,
			Vector3 Center)> _probeCascades = new();

		/// <summary>Дебаг-вид проб (шарики, см. ProbeDebugOverlay) - по оверлею на ОБЪЁМ: базовый
		/// плюс каскады. Каждый помнит атласы, под которые создан, - их пересоздание (новая сессия,
		/// смена сетки, перецентровка каскада) пересобирает весь набор.</summary>
		private readonly List<(ProbeDebugOverlay Overlay, ProbeGiTextures Textures)> _probeDebugOverlays = new();
		private bool _probeDebugFailed;

		/// <summary>Умеет ли устройство inline-трассировку - по этому флагу окно Graphics гасит
		/// галочку аппаратного ускорения.</summary>
		public bool RayTracingSupported => _graphicsApi.RayTracing >= RayTracingSupport.Inline;

		// GPU-путь один раз сорвался - больше не пробуем до перезапуска редактора. Иначе каждая
		// пересборка сессии повторяла бы отказ, а если он роняет драйвер - в цикле.
		private bool _probeGpuDisabled;

		// Параметры, под которыми заведена ТЕКУЩАЯ сессия: диф с настройками в ApplyGraphicsSettings
		// решает, нужно ли её пересоздавать. Интенсивность солнца сюда НЕ входит - она подтягивается
		// в живую сессию каждым раундом, как и поворот света (live-ручки шейдера тем более: они
		// пушатся кбуфером и бейка не касаются).
		private (bool On, float Sky, int Rays, int Bounces, float BounceSaturation,
			float Density, int MaxProbes, bool HardwareTrace, int Cascades, int VisRes) _probeSessionOptions;

		// Снимок live-ручек шейдера: Update пушит кбуфер при любом их изменении напрямую, не
		// полагаясь на событие PreviewGraphicsApplied - слайдеры окна Graphics обязаны работать,
		// даже если событийную проводку кто-то сломает.
		private (float ShadowFloor, float SkyFloor, float SpecFloor, float Sun, float Boost, float Bias, bool On, bool Debug) _lastLiveProbeParams;
		private readonly System.Diagnostics.Stopwatch _probeRoundSw = new();
		private long _probeRoundMs;
		private string _probeStatus = "нет проб";

		/// <summary>Статус probe-GI для окна Graphics (см. GraphicsSettingsWindow): пока поле не
		/// сошлось, показывает номер раунда - бейк больше не «чёрный ящик на секунду».</summary>
		public string ProbeGiStatus
		{
			get
			{
				if (!_editorSettings.PreviewProbeGi)
				{
					return "выключен";
				}

				var session = _probeSession;
				if (session == null)
				{
					return _probeStatus;
				}

				var grid = $"{session.CountX}x{session.CountY}x{session.CountZ} проб";

				// Каскады иначе невидимы снаружи: без этой строки не отличить «каскады работают» от
				// «тихо не создались». Ячейка - главное, ради чего они есть.
				if (_probeCascades.Count > 0)
				{
					float Cell(ProbeGiBakeSession s) => MathF.Min(s.Cell.X, MathF.Min(s.Cell.Y, s.Cell.Z));
					grid += $" + каскады: яч. {Cell(session):F2}";
					foreach (var cascade in _probeCascades)
					{
						grid += $"/{Cell(cascade.Session):F2}";
					}
				}

				// Какой путь трассировки ЖИВОЙ. Снаружи это было невидимо: галка в окне Graphics
				// показывает ЖЕЛАНИЕ, а путь выбирается при подъёме сессии и законно может с ним не
				// совпадать: устройство не умеет inline-трассировки либо сессию ещё не перезавели.
				// Без этой строки одно от другого отличалось только под профайлером.
				grid += _probeGpu == null ? ", GPU-путь не поднялся"
					: _probeGpu.Hardware ? ", трассировка аппаратная"
					: ", трассировка программная";

				if (session.Realtime)
				{
					// Номер раунда в реальном времени ничего не значит - он растёт вечно. Значение
					// имеет только темп: он и есть время отклика поля на изменение света.
					return $"{grid}, реальное время ({_probeRoundMs} мс/раунд)";
				}

				return session.Converged
					? $"{grid}, готово ({_probeRoundMs} мс/раунд)"
					: $"{grid}, раунд {session.Round}/{session.TargetRounds}";
			}
		}

		/// <summary>Дебаунс ПЕРЕСОЗДАНИЯ сессии проб: ползунки качества/плотности окна Graphics шлют
		/// изменение каждый кадр драга, а новая сессия выбрасывает всё накопленное поле. Поворот
		/// света через этот дебаунс больше не ходит - он применяется к живой сессии сразу.</summary>
		private const float ProbeRebakeDebounceSeconds = 0.25f;
		private readonly Dictionary<int, MeshId> _meshIdMap = new();
		private readonly Dictionary<int, MaterialId> _materialIdMap = new();
		private readonly Dictionary<(int, int), BatchId> _batchCache = new();

		// Wireframe overlay toggle (see WireframeEnabled/SetWireframeEnabled): a second material
		// (wireframe-filled PSO, see DiligentBatchRenderer.GetWireframeState) drawing the exact same
		// geometry as the currently isolated sub-mesh's instances, added/removed on top of
		// _instanceEntities independently of SubMeshPreviewMode - the batch renderer has no notion of
		// "redraw this batch again with a different PSO", so a second material/batch/instance set is how
		// a second draw pass over the same geometry happens here (see
		// ModelViewportGeometry.CreateInstanceEntity for the pattern this mirrors).
		private IMaterialObject? _wireframeMaterial;
		private MaterialId? _wireframeMaterialId;
		private readonly Dictionary<int, BatchId> _wireframeBatchCache = new();
		private readonly List<Entity> _wireframeEntities = new();
		private bool _wireframeEnabled;

		private SubMeshPreviewMode _viewMode = SubMeshPreviewMode.Highlight;
		private PreviewChannel _previewChannel = PreviewChannel.Normal;

		/// <summary>Глобальные тумблеры фич Lighting-превью (см. <see cref="PreviewFeatureFlags"/>) -
		/// задел под настройки графики. Меняются через <see cref="SetFeatureFlags"/>.</summary>
		private PreviewFeatureFlags _featureFlags = PreviewFeatureFlags.All;

		/// <summary>Текущие тумблеры фич - см. <see cref="SetFeatureFlags"/>.</summary>
		public PreviewFeatureFlags FeatureFlags => _featureFlags;

		/// <summary>Включает/выключает фичи Lighting-превью (нормал-мапы, AO и т.д.) - применяется к
		/// текущей резидентной модели немедленно.</summary>
		public void SetFeatureFlags(PreviewFeatureFlags flags)
		{
			if (_featureFlags == flags)
			{
				return;
			}

			_featureFlags = flags;
			ApplyPreviewSettingsToMaterials();
		}

		/// <summary>Смещения ползунков света в градусах ОТ базового положения солнца энвайронмента
		/// (яв вокруг Y / высота над горизонтом, см. <see cref="SetLightRotation"/>). Хранятся здесь,
		/// а не в ShadowSettings, чтобы переживать пересоздание окружения (см.
		/// <see cref="RecreateEnvironment"/>).</summary>
		private float _lightYawOffsetDegrees;
		private float _lightElevationOffsetDegrees;

		/// <summary>Абсолютная высота солнца клампится в эти пределы: у горизонта/зенита ортокамера
		/// каскада вырождается (см. BuildLightData - up-вектор, растянутая проекция).</summary>
		private const float LightElevationMinDegrees = -85f;
		private const float LightElevationMaxDegrees = 85f;

		/// <summary>Текущие смещения ползунков света - см. <see cref="SetLightRotation"/>.</summary>
		public float LightYawDegrees => _lightYawOffsetDegrees;
		public float LightElevationDegrees => _lightElevationOffsetDegrees;

		/// <summary>Поворачивает мировой ключевой свет («солнце» энвайронмента): яв вокруг Y + высота
		/// над горизонтом, оба - смещения от базового положения солнца. Применяется live: направление
		/// читается системой рендера каждый кадр (см. SimpleCullingAndRenderSystem.BuildLightData), а
		/// поворот по яву дополнительно уходит в шейдеры неба/IBL (см. <see cref="ApplyLightRotation"/>),
		/// чтобы фон и отражения вращались вместе со светом.</summary>
		public void SetLightRotation(float yawOffsetDegrees, float elevationOffsetDegrees)
		{
			_lightYawOffsetDegrees = yawOffsetDegrees;
			_lightElevationOffsetDegrees = elevationOffsetDegrees;
			ApplyLightRotation();
		}

		/// <summary>Применяет текущие смещения ползунков к окружению: направление света/теней
		/// (ShadowSettings), поворот фонового неба (SkyPassResources) и IBL-отражений материалов
		/// (PreviewSettings-кбуфер, см. <see cref="ApplyPreviewSettingsToMaterials"/>). Высота на
		/// equirect-карту не переносится - вращать панораму дёшево только вокруг Y.</summary>
		private void ApplyLightRotation()
		{
			var shadowSettings = _env.ShadowSettings;
			if (shadowSettings == null)
			{
				return;
			}

			shadowSettings.SetAngles(
				shadowSettings.BaseYawDegrees + _lightYawOffsetDegrees,
				Math.Clamp(shadowSettings.BaseElevationDegrees + _lightElevationOffsetDegrees,
					LightElevationMinDegrees, LightElevationMaxDegrees));

			_env.Pipeline.SkyResources?.SetEnvironmentYaw(shadowSettings.EnvYawRadians);
			ApplyPreviewSettingsToMaterials();

			// Пробы под новое направление солнца ничего перезапускать не требуют: PollProbeBake
			// подтягивает свет в живую сессию каждым раундом, и поле само перетекает к новому
			// решению за единицы раундов, сохранив накопленную геометрию (см.
			// ProbeGiBakeSession.SetLighting). Прежний код здесь ставил дебаунс полного ребейка.
		}

		private Vector3 _orbitTarget = Vector3.Zero;
		private float _yaw = -0.6f;
		private float _pitch = 0.35f;
		private float _distance = 4f;
		private bool _orbiting;
		private bool _panning;

		private ImTextureRef _textureRef;
		private bool _textureBound;

		private Vector2 _pendingSize;
		private float _resizeIdleSeconds;

		/// <summary>Масштаб рендера, увиденный последним TrackAndApplyResize, - смена сбрасывает
		/// дебаунс-таймер, как смена размера окна (см. там же).</summary>
		private float _pendingRenderScale = 1f;

		/// <summary>Заявка на применение бэкенда апскейлера и его настроек - исполняется в начале
		/// Update (см. ApplyPendingUpscalerSettings): смена бэкенда ждёт GPU и создаёт NGX-фичу,
		/// посреди ImGui-кадра это роняло редактор.</summary>
		private bool _pendingUpscalerApply;

		/// <summary>Отложенное применение бэкенда апскейлера (см. <see cref="_pendingUpscalerApply"/>).
		/// Индекс комбо DLSS переводится в NVSDK_NGX_PerfQuality_Value: {Perf, Balanced, Quality,
		/// DLAA} = {0, 1, 2, 5}.</summary>
		private void ApplyPendingUpscalerSettings()
		{
			if (!_pendingUpscalerApply || _env is null)
			{
				return;
			}

			_pendingUpscalerApply = false;
			_env.SetUpscalerBackend(_editorSettings.TemporalUpscale && _editorSettings.PreviewMotionVectors
				? Math.Clamp(_editorSettings.UpscalerBackend, 0, 2)
				: 0);
			_env.SetUpscalerTuning(
				Math.Clamp(_editorSettings.TaauBlendAlpha, 0.02f, 0.5f),
				Math.Clamp(_editorSettings.FsrSharpness, 0f, 1f),
				new[] { 0, 1, 2, 5 }[Math.Clamp(_editorSettings.DlssQuality, 0, 3)],
				// Индекс комбо провайдера FSR -> мажор ветки: {Авто, FSR 2, FSR 3.1} = {0, 2, 3}.
				new[] { 0, 2, 3 }[Math.Clamp(_editorSettings.FsrProvider, 0, 2)]);
		}

		/// <summary>?????????? ???? ? ????????? ??????? ??????????? ??????, ???? null.</summary>
		public string? LoadedPath => _loadedPath;

		/// <summary>????????? ?? ?????? ????????? ??????? ????????, ???? null ???? ??? ??????.</summary>
		public string? LoadError => _loadError;

		public bool HasModel => _instanceEntities.Count > 0;

		/// <summary>True while a single sub-mesh (rather than the whole model) is isolated - only then
		/// is <see cref="ViewMode"/>/<see cref="Channel"/> meaningful (see <see cref="InspectorWindow"/>).</summary>
		public bool IsSubMeshView => _loadedSubMesh >= 0;

		/// <summary>Current sub-mesh view mode - see <see cref="SetSubMeshViewMode"/>.</summary>
		public SubMeshPreviewMode ViewMode => _viewMode;

		/// <summary>Whether the wireframe overlay is currently on - see <see cref="SetWireframeEnabled"/>.
		/// Orthogonal to <see cref="ViewMode"/>: can be toggled on top of either Highlight or Channel.</summary>
		public bool WireframeEnabled => _wireframeEnabled;

		/// <summary>Current Channel-mode debug channel - see <see cref="SetPreviewChannel"/>.</summary>
		public PreviewChannel Channel => _previewChannel;

		/// <summary>Whether the currently isolated sub-mesh has real UV data, i.e. whether
		/// <see cref="PreviewChannel.Tangent"/> (derived from UV derivatives) is meaningful for it.</summary>
		public bool CurrentSubMeshHasUv =>
			_loadedSubMesh >= 0 && _residentModel != null &&
			_loadedSubMesh < _residentModel.MeshHasUv.Count && _residentModel.MeshHasUv[_loadedSubMesh];

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
			_appliedMsaa = (uint)Math.Clamp(_editorSettings.PreviewMsaaSamples, 1, 8);
			_appliedSky = _editorSettings.PreviewSkyBackground;
			_appliedHdrPath = ResolveEnvironmentHdrPath(_editorSettings.PreviewEnvironmentHdr) ?? "";
			_appliedAniso = _editorSettings.PreviewAnisotropicFiltering;
			_appliedMaxTextureSize = ClampedMaxTextureSize();
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
				msaaSamples: _appliedMsaa,
				ssao: _appliedSsao,
				shadows: true,
				aoMode: _appliedAoMode,
				ssgi: _appliedSsgi,
				eyeAdaptation: _appliedEyeAdaptation,
				fog: _appliedFog,
				bloom: _appliedBloom,
				colorGrade: _appliedColorGrade,
				volumetric: _appliedVolumetric,
				motionVectors: _appliedMotionVectors,
				temporalUpscale: _appliedMotionVectors && _editorSettings.TemporalUpscale,
				upscalerBackend: _appliedMotionVectors && _editorSettings.TemporalUpscale
					? Math.Clamp(_editorSettings.UpscalerBackend, 0, 2)
					: 0);

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

		/// <summary>Обработчик "OK" окна настроек: env-level опции (SSAO/MSAA/скай/HDR/анизотропия)
		/// при изменении применяются пересозданием окружения с перезагрузкой текущей модели,
		/// live-биты - как обычно. Ничего не изменилось - ничего и не пересоздаётся.</summary>
		private void OnGraphicsSettingsChanged()
		{
			// Пересоздания окружения требуют только те опции, что запечены НЕ в конвейер:
			// MSAA (число сэмплов - в PSO всей геометрии), HDRI энвайронмента (пересчёт IBL),
			// анизотропия (в сэмплеры материалов) и потолок текстур (в декодер загрузчика). Всё
			// остальное - фичи конвейера, применяются на живом окружении без единой перезагрузки
			// модели (см. GraphicsPipelineSimple.SetFeatures). Ровно этот список и стоит за кнопкой
			// "Применить" в окне Graphics - см. GraphicsSettingsWindow.DrawApplyBar.
			bool needsRecreate =
				_appliedMsaa != (uint)Math.Clamp(_editorSettings.PreviewMsaaSamples, 1, 8) ||
				_appliedHdrPath != (ResolveEnvironmentHdrPath(_editorSettings.PreviewEnvironmentHdr) ?? "") ||
				_appliedAniso != _editorSettings.PreviewAnisotropicFiltering ||
				_appliedMaxTextureSize != ClampedMaxTextureSize();

			// Пересоздание ОТКЛАДЫВАЕТСЯ до начала следующего Update: "OK" настроек срабатывает
			// посреди ImGui-кадра, когда превью-картинка со старым биндингом уже может лежать в
			// draw list-е - освобождение таргета здесь обратилось бы к нему из ImGui-рендера.
			_pendingEnvironmentRecreate |= needsRecreate;

			if (!needsRecreate)
			{
				// При пересоздании фичи всё равно перечитает CreateEnvironment - применять их
				// дважды незачем.
				ApplyPipelineFeatures();
			}

			ApplyGraphicsSettings();
		}

		/// <summary>Применяет фичи конвейера к ЖИВОМУ окружению - см.
		/// <see cref="GraphicsPipelineSimple.SetFeatures"/>: ресурсы включённой впервые фичи
		/// создаются на месте, выключенной - остаются лежать до следующего включения, граф
		/// пересобирается (дёшево). Сцена, батч-рендерер и загруженная модель не трогаются.</summary>
		private void ApplyPipelineFeatures()
		{
			_appliedSsao = _editorSettings.PreviewSsao;
			_appliedAoMode = _editorSettings.PreviewAoMode;
			_appliedSsgi = _editorSettings.PreviewSsgi;
			_appliedSky = _editorSettings.PreviewSkyBackground;
			_appliedEyeAdaptation = _editorSettings.PreviewEyeAdaptation;
			_appliedFog = _editorSettings.PreviewFog;
			_appliedVolumetric = _editorSettings.PreviewVolumetric;
			_appliedBloom = _editorSettings.PreviewBloom;
			_appliedColorGrade = _editorSettings.PreviewColorGrade;
			_appliedMotionVectors = _editorSettings.PreviewMotionVectors;

			_env.SetFeatures(new PipelineFeatures
			{
				SkyBackground = _appliedSky,
				Ssao = _appliedSsao,
				AoMode = _appliedAoMode,
				Ssgi = _appliedSsgi,
				EyeAdaptation = _appliedEyeAdaptation,
				Fog = _appliedFog,
				Volumetric = _appliedVolumetric,
				Bloom = _appliedBloom,
				ColorGrade = _appliedColorGrade,
				MotionVectors = _appliedMotionVectors,
				TemporalUpscale = _appliedMotionVectors && _editorSettings.TemporalUpscale,
			});
		}

		/// <summary>Пересоздаёт превью-окружение под новые env-level опции и перезагружает текущую
		/// модель. Порядок обязателен: дождаться GPU -> отвязать ImGui-биндинг таргета -> освободить
		/// окружение -> создать новое -> сбросить резидентный кеш (он ссылался на старый батч-рендерер)
		/// -> перезагрузить модель с диска.</summary>
		private void RecreateEnvironment()
		{
			var reloadPath = _loadedPath ?? _loadingPath;
			var reloadSubMesh = _loadedPath != null ? _loadedSubMesh : _loadingSubMesh;

			CancelPendingLoad();

			// Кадры с ресурсами старого окружения могут быть в полёте - без ожидания GPU
			// освобождение роняет драйвер (та же дисциплина, что в ResizeTargets).
			_env.DilApi.ImmediateContext.Flush();
			_env.DilApi.ImmediateContext.WaitForIdle();

			// Атласы проб принадлежат этому вьюпорту, а не окружению - освобождаем сами (за барьером
			// выше); новое окружение перепечёт их при перезагрузке модели ниже.
			ResetProbeGi();

			if (_textureBound && _lastImGuiRender != null)
			{
				_lastImGuiRender.ReleaseRenderTargetBinding(_textureRef.GetTexID());
				_textureBound = false;
			}

			_env.Release();

			// Резидентный кеш и вся геометрия жили в старом батч-рендерере/EntityStore - обнуляем
			// ссылки, новое окружение наполнится перезагрузкой модели.
			_instanceEntities.Clear();
			_wireframeEntities.Clear();
			_wireframeMaterial = null;
			_wireframeMaterialId = null;
			_wireframeBatchCache.Clear();

			// Окружение (вместе с батч-рендерером и стором) освобождается целиком - ссылки
			// отладочного оверлея BVH умирают вместе с ним.
			ReleaseBvhDebugResources();
			_batchCache.Clear();
			_meshIdMap.Clear();
			_materialIdMap.Clear();
			_residentModel = null;
			_residentPath = null;
			_streamingModel = null;
			_loadedPath = null;
			_loadedSubMesh = -1;
			_loadError = null;

			_env = CreateEnvironment();
			_env.Root.Add(new ModelStreamingSystem(_streamer));

			// Модели стримера жили под старые сэмплеры/шейдеры - выбрасываются (dropModels), барьер
			// GPU уже был выше; перезагрузка ниже стартует с чистого листа.
			_streamer.MigrateEnvironment(_env, dropModels: true);
			ApplyLightRotation();

			if (reloadPath != null)
			{
				LoadModel(reloadPath, reloadSubMesh);
			}
		}

		/// <summary>Применяет live-настройки графики превью из <see cref="EditorSettings"/> (см.
		/// SettingsWindow): биты фич и рантайм-тумблер теней. Вызывается при создании и после "OK"
		/// в окне настроек; рестарт-левел опции (MSAA/SSAO/скай/HDR) считываются конструктором.</summary>
		public void ApplyGraphicsSettings()
		{
			var flags = PreviewFeatureFlags.None;
			if (_editorSettings.PreviewNormalMaps)
			{
				flags |= PreviewFeatureFlags.NormalMaps;
			}
			if (_editorSettings.PreviewBakedOcclusion)
			{
				flags |= PreviewFeatureFlags.Occlusion;
			}
			if (_editorSettings.PreviewShadows)
			{
				flags |= PreviewFeatureFlags.Shadows;
			}

			// Не из настроек напрямую, а от РЕАЛЬНО созданного окружения: тумблер авто-экспозиции -
			// рестарт-левел, и до пересоздания шейдер обязан продолжать писать display-space.
			if (_env.HdrOutput)
			{
				flags |= PreviewFeatureFlags.HdrOutput;
			}

			SetFeatureFlags(flags);

			if (_env.ShadowSettings != null)
			{
				_env.ShadowSettings.Enabled = _editorSettings.PreviewShadows;
			}

			// Live-ручки probe-GI/солнца (флоры глушения, буст, интенсивность) уходят кбуфером
			// сразу, без ребейка.
			ApplyPreviewSettingsToMaterials();

			// Ручки AO (сила/предел/радиус) - в кбуфер AO-пасса, тоже живьём.
			_env.SetAoStrength(Math.Clamp(_editorSettings.AoStrength, 0.1f, 4f),
				Math.Clamp(_editorSettings.AoFloor, 0f, 1f));
			_env.SetAoDebugView(_editorSettings.AoDebugView);

			// Отладочный вид векторов движения - тоже живая ручка кбуфера, и, в отличие от самой галки
			// векторов, он не пересобирает граф (см. MotionVectorDebugPassResources).
			_env.SetMotionVectorDebug(_editorSettings.MotionVectorDebugView,
				Math.Clamp(_editorSettings.MotionVectorDebugRange, 0.25f, 256f));
			_env.SetTemporalJitter(_editorSettings.TemporalJitter);

			// Бэкенд апскейлера и его настройки НЕ применяются здесь: применение настроек срабатывает
			// посреди ImGui-кадра, а смена бэкенда/качества DLSS ждёт GPU и пишет init-команды NGX -
			// делать это на полусобранном кадре нельзя (ровно тот класс бага, что был у render scale).
			// Отложено до начала Update - см. ApplyPendingUpscalerSettings.
			_pendingUpscalerApply = true;

			// Масштаб рендера здесь НЕ применяется - только в TrackAndApplyResize (см. его
			// комментарий): применение настроек срабатывает посреди ImGui-кадра, когда картинка
			// превью со старым биндингом уже может лежать в draw list-е, и синхронный ResizeTargets
			// отсюда ломал кадр - ровно та же причина, по которой откладывается пересоздание
			// окружения (_pendingEnvironmentRecreate). TrackAndApplyResize перечитывает настройку
			// сам, каждый кадр - это же чинит и потерю масштаба при пересоздании окружения.

			// Ручки авто-экспозиции - тоже живьём (сам тумблер рестарт-левел, см. CreateEnvironment).
			// Границы измеренной яркости держим упорядоченными: перевёрнутый диапазон дал бы clamp в
			// нижнюю границу и намертво зафиксировал экспозицию.
			var eaMin = Math.Clamp(_editorSettings.EyeAdaptationMinLuminance, 0.0001f, 100f);
			var eaMax = Math.Max(Math.Clamp(_editorSettings.EyeAdaptationMaxLuminance, 0.0001f, 100f), eaMin);
			_env.SetEyeAdaptationParams(
				Math.Clamp(_editorSettings.EyeAdaptationKey, 0.01f, 2f),
				eaMin,
				eaMax,
				Math.Clamp(_editorSettings.EyeAdaptationExposureCompensation, -8f, 8f));
			_env.SetEyeAdaptationSpeed(
				Math.Clamp(_editorSettings.EyeAdaptationSpeedUp, 0.05f, 20f),
				Math.Clamp(_editorSettings.EyeAdaptationSpeedDown, 0.05f, 20f));

			// Мировая ручка работает и до кадрирования модели; доля баундов - только после него
			// (_framedRadius == 0 означало бы нулевой радиус, то есть AO без эффекта).
			if (_framedRadius > 0f || _editorSettings.AoRadiusWorld > 0f)
			{
				_env.SetAoWorldRange(AoWorldRange());
			}

			// Ручки SSGI (интенсивность/тапы/клампы/размытие) - тоже живьём; радиус, как и у AO,
			// имеет смысл только когда есть от чего его считать.
			ApplyGiSettings(pushRange: _framedRadius > 0f || _editorSettings.SsgiRadiusWorld > 0f);
			ApplyFogSettings();
			ApplyVolumetricSettings();
			ApplyBloomSettings();
			ApplyColorGradeSettings();
			_env.SetToneCurve(_editorSettings.ToneCurve);

			// Параметры сетки/качества сменились (окно Graphics) - заводим сессию заново, с
			// дебаунсом: слайдер шлёт изменение каждый тик драга.
			var wanted = (_editorSettings.PreviewProbeGi,
				_editorSettings.ProbeGiSkyIntensity,
				_editorSettings.ProbeGiRaysPerProbe,
				_editorSettings.ProbeGiBounces,
				_editorSettings.ProbeGiBounceSaturation,
				_editorSettings.ProbeGiGridDensity,
				_editorSettings.ProbeGiMaxProbes,
				// Число каскадов - раскладка, а не live-ручка.
				//
				// АППАРАТНАЯ ТРАССИРОВКА тоже здесь, и это не украшение: путь трассировки выбирается
				// ОДИН РАЗ, когда поднимается GPU-комплект сессии (кейворд шейдера плюс структуры
				// ускорения, см. TryBeginProbeGpu). Без этого слота галка в окне Graphics меняла только
				// EditorSettings и не трогала живую сессию вовсе: пробы продолжали ехать на том пути,
				// с которым сессию завели (по умолчанию - программном), и включение аппаратной не давало
				// РОВНО НИЧЕГО до следующего ребейка по другой ручке.
				_editorSettings.ProbeGiHardwareRayTracing,
				_editorSettings.ProbeGiCascades,
				// Сторона окто-карты видимости - раскладка атласов, применяется только пересозданием
				// сессии (см. ProbeGiBakeResult.VisRes).
				_editorSettings.ProbeGiVisRes);
			if (wanted.Item1 && wanted != _probeSessionOptions)
			{
				RequestProbeSession();
			}
		}


		/// <summary>Пуш живых ручек тумана (no-op когда он выключен - см. ModelViewportEnvironment.SetFogParams).
		/// Направление солнца сюда НЕ входит: оно пушится покадрово вместе с базисом камеры
		/// (см. SetCameraTransform), иначе в Scene View подсветка отставала бы от гизмо солнца.</summary>
		/// <summary>Пуш живых ручек блума (no-op когда он выключен - см.
		/// ModelViewportEnvironment.SetBloomParams).</summary>
		private void ApplyBloomSettings()
		{
			_env.SetBloomParams(
				Math.Max(_editorSettings.BloomThreshold, 0f),
				Math.Max(_editorSettings.BloomKnee, 0.0001f),
				Math.Max(_editorSettings.BloomRadius, 0f),
				Math.Max(_editorSettings.BloomIntensity, 0f));
		}


		/// <summary>Пуш живых ручек цветокоррекции и виньетки (no-op когда грейдинг выключен - см.
		/// ModelViewportEnvironment.SetColorGrade).</summary>
		private void ApplyColorGradeSettings()
		{
			_env.SetColorGrade(
				Math.Max(_editorSettings.GradeSaturation, 0f),
				Math.Max(_editorSettings.GradeContrast, 0f),
				Math.Max(_editorSettings.GradeGamma, 0.001f),
				Math.Clamp(_editorSettings.GradeTemperature, -1f, 1f),
				Math.Clamp(_editorSettings.GradeTint, -1f, 1f),
				new Vector3(_editorSettings.GradeShadowR, _editorSettings.GradeShadowG, _editorSettings.GradeShadowB),
				new Vector3(_editorSettings.GradeHighlightR, _editorSettings.GradeHighlightG, _editorSettings.GradeHighlightB));

			_env.SetVignette(
				Math.Clamp(_editorSettings.VignetteIntensity, 0f, 1f),
				Math.Max(_editorSettings.VignetteRadius, 0.001f),
				Math.Max(_editorSettings.VignetteSmoothness, 0.001f),
				Math.Clamp(_editorSettings.VignetteRoundness, 0f, 1f));
		}

		private void ApplyFogSettings()
		{
			_env.SetFogParams(
				Math.Max(_editorSettings.FogDensity, 0f),
				Math.Max(_editorSettings.FogHeightFalloff, 0f),
				_editorSettings.FogHeightRef,
				Math.Max(_editorSettings.FogStartDistance, 0f),
				Math.Max(_editorSettings.FogMaxDistance, 1f),
				Math.Clamp(_editorSettings.FogMaxOpacity, 0f, 1f));

			_env.SetFogColors(
				new Vector3(_editorSettings.FogColorR, _editorSettings.FogColorG, _editorSettings.FogColorB),
				new Vector3(_editorSettings.FogSunColorR, _editorSettings.FogSunColorG, _editorSettings.FogSunColorB),
				Math.Clamp(_editorSettings.FogSunStrength, 0f, 1f),
				Math.Max(_editorSettings.FogSunSharpness, 0.001f));
		}

		/// <summary>Пуш живых ручек объёмного света (no-op когда он выключен - см.
		/// ModelViewportEnvironment.SetVolumetricParams). Направление солнца сюда, как и у тумана,
		/// НЕ входит: оно пушится покадрово вместе с базисом камеры (см. SetCameraTransform).</summary>
		private void ApplyVolumetricSettings()
		{
			_env.SetVolumetricParams(
				Math.Max(_editorSettings.VolumetricDensity, 0f),
				Math.Max(_editorSettings.VolumetricHeightFalloff, 0f),
				_editorSettings.VolumetricHeightRef,
				Math.Max(_editorSettings.VolumetricStartDistance, 0f),
				Math.Max(_editorSettings.VolumetricMaxDistance, 1f),
				Math.Clamp(_editorSettings.VolumetricSteps, 4, 256),
				Math.Clamp(_editorSettings.VolumetricMaxOpacity, 0f, 1f),
				Math.Clamp(_editorSettings.VolumetricShadowStrength, 0f, 1f));

			_env.SetVolumetricScattering(
				Math.Max(_editorSettings.VolumetricScattering, 0f),
				Math.Max(_editorSettings.VolumetricExtinction, 1e-4f),
				Math.Clamp(_editorSettings.VolumetricAnisotropy, -0.95f, 0.95f));

			_env.SetVolumetricColors(
				new Vector3(_editorSettings.VolumetricSunColorR, _editorSettings.VolumetricSunColorG,
					_editorSettings.VolumetricSunColorB),
				Math.Max(_editorSettings.VolumetricSunIntensity, 0f),
				new Vector3(_editorSettings.VolumetricAmbientColorR, _editorSettings.VolumetricAmbientColorG,
					_editorSettings.VolumetricAmbientColorB),
				Math.Max(_editorSettings.VolumetricAmbientIntensity, 0f),
				Math.Clamp(_editorSettings.VolumetricAmbientShadowFloor, 0f, 1f));
		}
		/// <summary>Мировой радиус влияния AO для текущей модели: явная ручка "AO radius (world)",
		/// если она задана, иначе доля габаритного радиуса (см. EditorSettings.AoRadiusWorld -
		/// на сценах-уровнях доля от баундов даёт метры, и контактная тень расползается в пятно).</summary>
		private float AoWorldRange()
		{
			var world = _editorSettings.AoRadiusWorld;
			return world > 0f
				? Math.Clamp(world, 0.01f, 1000f)
				: _framedRadius * Math.Clamp(_editorSettings.AoRadiusFraction, 0.01f, 1f);
		}

		/// <summary>Мировой радиус сбора SSGI - та же логика, что у <see cref="AoWorldRange"/>:
		/// явная ручка, если задана, иначе доля габаритного радиуса модели.</summary>
		private float GiWorldRange()
		{
			var world = _editorSettings.SsgiRadiusWorld;
			return world > 0f
				? Math.Clamp(world, 0.01f, 1000f)
				: _framedRadius * Math.Clamp(_editorSettings.SsgiRadiusFraction, 0.01f, 2f);
		}

		/// <summary>Пуш живых ручек SSGI в кбуферы пасса (no-op при выключенном SSGI - см.
		/// ModelViewportEnvironment.SetGiParams). Радиус пушится отдельным флагом: до кадрирования
		/// модели доля баундов даёт ноль, то есть GI без эффекта.</summary>
		private void ApplyGiSettings(bool pushRange)
		{
			_env.SetGiParams(
				Math.Clamp(_editorSettings.SsgiIntensity, 0f, 4f),
				Math.Clamp(_editorSettings.SsgiSamples, 4, SsgiPassResources.MaxSampleCount),
				Math.Max(0f, _editorSettings.SsgiMaxLuminance),
				Math.Clamp(_editorSettings.SsgiSaturation, 0f, 1f));
			_env.SetGiCompositeParams(
				Math.Clamp(_editorSettings.SsgiBlurRadius, 0, SsgiPassResources.MaxBlurRadius),
				_editorSettings.SsgiDebugView);

			if (pushRange)
			{
				_env.SetGiWorldRange(GiWorldRange());
			}
		}

		/// <summary>Принудительный ребейк probe-GI (кнопка Rebake в окне Graphics) - заводит сессию
		/// заново, с нуля.</summary>
		public void RequestProbeRebake()
		{
			RequestProbeSession(delaySeconds: 0f);
		}

		/// <summary>Просит пересоздать сессию probe-GI (новая модель либо изменившиеся параметры
		/// сетки/качества). Дебаунс - потому что новая сессия выбрасывает всё накопленное поле, а
		/// ползунки окна Graphics шлют изменение каждый кадр драга.</summary>
		private void RequestProbeSession(float delaySeconds = ProbeRebakeDebounceSeconds)
		{
			if (_residentModel != null && _editorSettings.PreviewProbeGi)
			{
				_probeSessionDelay = delaySeconds;
			}
		}

		/// <summary>Резолвит путь HDR-окружения из настроек: абсолютный - как есть, относительный -
		/// от "EditorAssets/", пусто/не найден - null (процедурное небо).</summary>
		private static string ResolveEnvironmentHdrPath(string configured)
		{
			if (string.IsNullOrWhiteSpace(configured))
			{
				return null;
			}

			if (File.Exists(configured))
			{
				return configured;
			}

			var relative = Path.Combine("EditorAssets", configured);
			return File.Exists(relative) ? relative : configured;
		}

		/// <summary>Switches the sub-mesh view mode (see <see cref="InspectorWindow"/>'s View Mode combo).
		/// No-op outside sub-mesh view. Independent of <see cref="WireframeEnabled"/> - the wireframe
		/// overlay, if on, stays on across a mode switch.</summary>
		public void SetSubMeshViewMode(SubMeshPreviewMode mode)
		{
			if (_viewMode == mode || !IsSubMeshView)
			{
				return;
			}

			_viewMode = mode;
			ApplyPreviewSettingsToMaterials();
		}

		/// <summary>Toggles the wireframe overlay (see <see cref="InspectorWindow"/>'s Wireframe checkbox) -
		/// orthogonal to <see cref="SetSubMeshViewMode"/>, so it can be combined with either Highlight or
		/// Channel. No-op outside sub-mesh view.</summary>
		public void SetWireframeEnabled(bool enabled)
		{
			if (_wireframeEnabled == enabled || !IsSubMeshView)
			{
				return;
			}

			_wireframeEnabled = enabled;

			if (enabled)
			{
				PopulateWireframeOverlay();
			}
			else
			{
				ClearWireframeOverlay();
			}
		}

		/// <summary>Switches the Channel-mode debug channel (see <see cref="InspectorWindow"/>'s Channel
		/// combo). Only has a visible effect while <see cref="ViewMode"/> is <see cref="SubMeshPreviewMode.Channel"/>.</summary>
		public void SetPreviewChannel(PreviewChannel channel)
		{
			if (_previewChannel == channel)
			{
				return;
			}

			_previewChannel = channel;
			ApplyPreviewSettingsToMaterials();
		}

		/// <summary>
		/// Pushes the current view mode/channel to every material of the resident model via
		/// <see cref="IMaterialObject.SetConstant{T}"/> (see UnlitInstancedPS.hlsl's PreviewSettings
		/// cbuffer). The whole-model view (<see cref="IsSubMeshView"/> false) always maps to Lighting
		/// (Mode 3) regardless of <see cref="ViewMode"/> - that combo is only shown/meaningful for
		/// sub-mesh view (see <see cref="InspectorWindow.RenderModelPreview"/>).
		/// </summary>
		private void ApplyPreviewSettingsToMaterials()
		{
			// The whole-model view always renders in Lighting (PBR) mode; the View Mode combo only
			// exists for an isolated sub-mesh (see InspectorWindow.RenderModelPreview).
			int mode = !IsSubMeshView ? 3 : _viewMode switch
			{
				SubMeshPreviewMode.Channel => 2,
				SubMeshPreviewMode.Lighting => 3,
				_ => 1,
			};

			// Отладочные виды (каналы/подсветка сабмеша, AO debug, probe debug) пишут в кадр УЖЕ
			// отображаемые значения - HDR-конвейер обязан прокинуть их мимо экспозиции и кривой, иначе
			// художник смотрел бы не на то, что шейдер посчитал (см. TonemapPassResources.SetPassthrough).
			// Пушится ДО выхода по отсутствию модели: пустое превью тоже рисует кадр.
			_env.SetTonemapPassthrough(mode != 3 || _editorSettings.AoDebugView
				|| (_editorSettings.ProbeGiDebugView && !IsSubMeshView));

			if (_residentModel == null)
			{
				return;
			}

			var data = new PreviewSettingsData
			{
				// Кривая действует только в LDR - в HDR её применяет TonemapPass (см. Tonemap.hlsl).
				ToneCurve = _editorSettings.ToneCurve,
				Mode = mode,
				Channel = (int)_previewChannel,
				EnvYawRadians = _env.ShadowSettings?.EnvYawRadians ?? 0f,
			};

			// Параметры сетки проб (нули = probe-GI выключен, Origin.w - тумблер в шейдере): атласы
			// уже привязаны к материалам в PollProbeBake, здесь только кбуфер. Выключение тумблера
			// в окне Graphics просто перестаёт слать сетку - атласы остаются привязанными, но
			// мёртвая ветка шейдера их не читает.
			if (_probeTextures != null && _editorSettings.PreviewProbeGi)
			{
				ProbeGiViewportShared.PushGrid(ref data, _probeTextures, _probeCascades,
					_editorSettings.ProbeGiNormalBias);
			}

			// Live-ручки probe-GI/солнца (см. кбуфер ProbeGiParams в UnlitInstancedPS.hlsl).
			data.ProbeGiParams = new Vector4(
				Math.Clamp(_editorSettings.ProbeGiShadowFloor, 0f, 1f),
				Math.Clamp(_editorSettings.ProbeGiSpecularFloor, 0f, 1f),
				Math.Clamp(_editorSettings.ProbeGiSunIntensity, 0.1f, 16f),
				Math.Clamp(_editorSettings.ProbeGiAmbientBoost, 0.1f, 128f));
			// y - сторона окто-карты видимости: шейдер раскладывает по ней тайл пробы в атласе, и
			// значение обязано быть тем же, под которое собрана сессия (см. ProbeGiBakeResult.VisRes).
			data.ProbeGiParams2 = new Vector4(
				Math.Clamp(_editorSettings.ProbeGiSkyShadowFloor, 0.01f, 1f),
				ProbeGiBakeResult.VisRes, 0f, 0f);

			// Отладочный вид probe-GI (см. канал 9 в шейдере) - только для целого вида модели:
			// в сабмеш-режиме каналом управляет Inspector-ов Channel-комбо.
			if (_editorSettings.ProbeGiDebugView && !IsSubMeshView)
			{
				data.Channel = 9;
			}

			// Расстановка проб (канал 10) старше вида поля: если попросили оба, показываем более
			// частный - где стоят пробы.
			if (_editorSettings.ProbeGiDebugProbes && !IsSubMeshView)
			{
				data.Channel = 10;
			}

			// Unlike Mode/Channel, the PBR factors are per material (glTF metallic/roughness/baseColor,
			// see ModelLoader.MaterialPbr), so the constant push has to walk key-value pairs rather than
			// blast one shared struct at every material.
			for (int i = 0; i < _residentModel.materialObjects.Count; i++)
			{
				var kvp = _residentModel.materialObjects.GetAt(i);

				if (!_residentModel.MaterialPbr.TryGetValue(kvp.Key, out var pbr))
				{
					pbr = new MaterialPbrFactors
					{
						BaseColorFactor = Vector4.One,
						MetallicFactor = 0f,
						RoughnessFactor = 0.6f,
						HasBaseColorTexture = false,
						Ior = 1.5f,
						VolumeAttenuation = new Vector4(1f, 1f, 1f, 0f),
						NormalScale = 1f,
						OcclusionStrength = 1f,
						SpecularColorFactor = Vector4.One
					};
				}

				data.Metallic = pbr.MetallicFactor;
				data.Roughness = pbr.RoughnessFactor;
				data.BaseColor = pbr.BaseColorFactor;
				data.HasBaseColorTexture = pbr.HasBaseColorTexture ? 1 : 0;
				data.AlphaCutoff = pbr.AlphaCutoff;
				data.HasMetallicRoughnessTexture = pbr.HasMetallicRoughnessTexture ? 1 : 0;
				data.Transmission = pbr.TransmissionFactor;
				data.Dispersion = pbr.Dispersion;
				data.Ior = pbr.Ior;
				data.VolumeAttenuation = pbr.VolumeAttenuation;
				data.ThicknessWorld = pbr.ThicknessWorld;
				data.FeatureFlags = (int)_featureFlags;
				data.NormalScale = pbr.NormalScale;
				data.OcclusionStrength = pbr.OcclusionStrength;
				data.UvOffset = pbr.UvOffset;
				data.UvTransform = pbr.UvTransform;
				data.UvHasTransform = pbr.HasUvTransform ? 1 : 0;
				data.OcclusionUvSet = pbr.OcclusionUvSet;
				data.SheenColorRoughness = pbr.SheenColorRoughness;
				data.SpecularColorFactor = pbr.SpecularColorFactor;

				kvp.Value.SetConstant("PreviewSettings", ref data, HandleAccess.Pixel);
			}
		}

		// --- Отладочный вид BVH проб ----------------------------------------------------------------

		/// <summary>Сущности каркасных боксов узлов BVH (см. <see cref="PopulateBvhDebugOverlay"/>).</summary>
		private readonly List<Entity> _bvhDebugEntities = new();

		/// <summary>Единичный куб для отладочных боксов: рисуется тем же wireframe-материалом, что и
		/// оверлей сабмеша, поэтому каркас получается из обычной треугольной геометрии. Один меш на
		/// весь оверлей - боксы различаются только Position/Scale3 инстанса.</summary>
		private MeshId? _bvhDebugMeshId;
		private BatchId? _bvhDebugBatchId;

		/// <summary>Снимок настроек, под которым построен текущий оверлей: боксы перестраиваются
		/// только при реальном изменении (спуск по дереву - не бесплатная операция).</summary>
		private (bool On, int Depth, bool Leaves, object? Baker) _bvhDebugState;

		/// <summary>Держит отладочный оверлей BVH в согласии с настройками и текущим деревом.
		/// Зовётся каждый кадр из Update - вся работа только на изменение.</summary>
		private void PollBvhDebugOverlay()
		{
			var wanted = (_editorSettings.ProbeGiBvhDebug && _probeBaker != null,
				Math.Clamp(_editorSettings.ProbeGiBvhDebugDepth, 0, 24),
				_editorSettings.ProbeGiBvhDebugLeaves,
				(object?)_probeBaker);

			if (wanted == _bvhDebugState)
			{
				return;
			}

			_bvhDebugState = wanted;
			ClearBvhDebugOverlay();

			if (wanted.Item1)
			{
				PopulateBvhDebugOverlay(wanted.Item2, wanted.Item3);
			}
		}

		/// <summary>
		/// Создаёт по каркасному боксу на узел BVH (см. <see cref="ProbeGiBaker.CollectDebugBoxes"/>).
		/// Инстансы обычные - те же Position/Rotation/Scale3 + регистрация в RenderResourceManager,
		/// что и у геометрии модели, так что культинг и инстансинг работают как есть.
		/// </summary>
		private void PopulateBvhDebugOverlay(int maxDepth, bool leavesOnly)
		{
			var baker = _probeBaker;
			if (baker == null || !baker.HasGeometry)
			{
				return;
			}

			var boxes = baker.CollectDebugBoxes(maxDepth, leavesOnly);
			if (boxes.Count == 0)
			{
				return;
			}

			// Верхний предел - защита от «показать все листья Sponza» (сотни тысяч боксов кладут и
			// инстанс-буферы, и глаз): режем и честно говорим об этом в консоль.
			const int MaxBoxes = 20000;
			int drawn = Math.Min(boxes.Count, MaxBoxes);

			try
			{
				EnsureWireframeMaterial();
				EnsureBvhDebugMesh();

				if (_bvhDebugMeshId == null || _wireframeMaterialId == null)
				{
					return;
				}

				_bvhDebugBatchId ??= _env.BatchRenderer.CreateBatch(_bvhDebugMeshId.Value, _wireframeMaterialId.Value);

				for (int i = 0; i < drawn; i++)
				{
					var (min, max, _) = boxes[i];
					var center = (min + max) * 0.5f;
					var size = Vector3.Max(max - min, new Vector3(1e-4f));

					var entity = _env.Store.CreateEntity(
						new Position(center.X, center.Y, center.Z),
						new Scale3(size.X, size.Y, size.Z),
						new Rotation(0f, 0f, 0f, 1f),
						Tags.Get<GpuUpdateTag>());

					_env.ResourceManager.RegisterRenderable(entity, _bvhDebugBatchId.Value);
					_bvhDebugEntities.Add(entity);
				}

				// Тот же барьер + пересборка графа, что у остальных оверлеев: команды ForwardPass
				// заморожены, а мы только что добавили батч/инстансы (см. ClearWireframeOverlay).
				_env.DilApi.ImmediateContext.Flush();
				_env.DilApi.ImmediateContext.WaitForIdle();
				_env.Pipeline.InvalidateGraph();

				var stats = baker.GetStats();
				EditorConsoleLog.Add(LogLevel.Info,
					$"Probe BVH debug: {drawn} of {boxes.Count} boxes ({(leavesOnly ? "leaves" : $"depth <= {maxDepth}")}), " +
					$"tree: {stats.Nodes} nodes / {stats.Leaves} leaves / depth {stats.MaxDepth} / " +
					$"{stats.AvgLeafTriangles:F1} tris per leaf" +
					(drawn < boxes.Count ? " - list truncated" : ""));
			}
			catch (Exception ex)
			{
				EditorConsoleLog.Add(LogLevel.Error, $"Probe BVH debug: failed to build overlay: {ex.Message}");
				ClearBvhDebugOverlay();
			}
		}

		/// <summary>Единичный куб (центр в нуле, сторона 1) - геометрия отладочного бокса.</summary>
		private void EnsureBvhDebugMesh()
		{
			if (_bvhDebugMeshId != null)
			{
				return;
			}

			var corners = new Vector3[8];
			for (int i = 0; i < 8; i++)
			{
				corners[i] = new Vector3(
					(i & 1) == 0 ? -0.5f : 0.5f,
					(i & 2) == 0 ? -0.5f : 0.5f,
					(i & 4) == 0 ? -0.5f : 0.5f);
			}

			var vertices = new Vertex[8];
			for (int i = 0; i < 8; i++)
			{
				vertices[i] = new Vertex
				{
					Position = corners[i],
					Normal = Vector3.Normalize(corners[i]),
					Tangent = new Vector4(1f, 0f, 0f, 1f),
					Color = Vector4.One,
				};
			}

			// Треугольники куба: в wireframe-состоянии PSO (см. GetWireframeState) они и дают каркас.
			var indices = new uint[]
			{
				0, 2, 1, 1, 2, 3, // -Z
				4, 5, 6, 5, 7, 6, // +Z
				0, 1, 4, 1, 5, 4, // -Y
				2, 6, 3, 3, 6, 7, // +Y
				0, 4, 2, 2, 4, 6, // -X
				1, 3, 5, 3, 7, 5, // +X
			};

			var mesh = _graphicsApi.CreateMesh("BVH Debug Box");
			mesh.SetVertices(vertices);
			mesh.SetIndices(indices);
			mesh.RecalculateBounds();

			_bvhDebugMesh = mesh;
			_bvhDebugMeshId = _env.BatchRenderer.Register(mesh);
		}

		private IMeshObject? _bvhDebugMesh;

		/// <summary>Выбрасывает GPU-сторону отладочного оверлея: зовётся там, где сбрасываются
		/// регистрации батч-рендерера (смена модели, пересоздание окружения) - MeshId/BatchId после
		/// сброса выдаются заново с нуля, и старые указывали бы в чужую геометрию. Вызывающий уже
		/// дождался GPU.
		///
		/// ВНИМАНИЕ: сущности оверлея обязан снять вызывающий - ДО сброса регистраций, пока их
		/// BatchId ещё валиден (см. ClearBvhDebugOverlay). Просто забыть список здесь нельзя: живые
		/// сущности остаются в сторе, их BatchRenderInfo указывает на батч с уже переиспользованным
		/// индексом, и вместо боксов дерева сцена получает копии геометрии НОВОЙ модели, расставленные
		/// по позициям старых боксов.</summary>
		private void ReleaseBvhDebugResources()
		{
			if (_bvhDebugEntities.Count > 0)
			{
				// Страховка на случай нового пути, забывшего снять сущности: без Unregister слоты
				// инстансов утекут, но мусорной геометрии в кадре не будет.
				foreach (var entity in _bvhDebugEntities)
				{
					if (!entity.IsNull)
					{
						entity.DeleteEntity();
					}
				}

				_bvhDebugEntities.Clear();
			}

			_bvhDebugMeshId = null;
			_bvhDebugBatchId = null;
			_bvhDebugState = default;

			_bvhDebugMesh?.Release();
			_bvhDebugMesh = null;
		}

		private void ClearBvhDebugOverlay()
		{
			if (_bvhDebugEntities.Count == 0)
			{
				return;
			}

			foreach (var entity in _bvhDebugEntities)
			{
				_env.ResourceManager.UnregisterRenderable(entity);
				entity.DeleteEntity();
			}

			_bvhDebugEntities.Clear();

			// Без пересборки графа снятые инстансы продолжали бы рисоваться (та же причина, что в
			// ClearWireframeOverlay).
			_env.DilApi.ImmediateContext.Flush();
			_env.DilApi.ImmediateContext.WaitForIdle();
			_env.Pipeline.InvalidateGraph();
		}

		/// <summary>Lazily creates the shared wireframe-overlay material/PSO (see
		/// <see cref="DiligentBatchRenderer.GetWireframeState"/>) - one instance shared by every mesh this
		/// viewport ever draws in wireframe, since it needs no per-material texture/state beyond a flat
		/// color (see WireframeOverlayPS.hlsl).</summary>
		private void EnsureWireframeMaterial()
		{
			if (_wireframeMaterialId != null)
			{
				return;
			}

			var vs = _graphicsApi.CreateShader("Wireframe Overlay VS", "EditorAssets/shader", "UnlitInstancedVS.hlsl", ShaderObjectType.Vertex);
			var ps = _graphicsApi.CreateShader("Wireframe Overlay PS", "EditorAssets/shader", "WireframeOverlayPS.hlsl", ShaderObjectType.Pixel);

			_wireframeMaterial = _graphicsApi.CreateMaterial("Wireframe Overlay Material");
			_wireframeMaterial.SetShader(vs, ps);
			_wireframeMaterial.SetState(_env.BatchRenderer.GetWireframeState());

			_wireframeMaterialId = _env.BatchRenderer.Register(_wireframeMaterial);
		}

		/// <summary>
		/// Adds one wireframe instance per instance of the currently isolated sub-mesh, reusing (and
		/// lazily creating) one wireframe batch per mesh - mirrors <see cref="ModelViewportGeometry.CreateInstanceEntity"/>
		/// but against <see cref="_wireframeMaterialId"/> instead of the sub-mesh's real material, since
		/// the wireframe overlay is the same flat color regardless of which glTF material the geometry uses.
		/// </summary>
		private void PopulateWireframeOverlay()
		{
			if (_residentModel == null || !IsSubMeshView)
			{
				return;
			}

			EnsureWireframeMaterial();

			foreach (var instance in _residentModel.instances)
			{
				if (instance.meshId != _loadedSubMesh)
				{
					continue;
				}

				if (!_meshIdMap.TryGetValue(instance.meshId, out var meshId))
				{
					continue;
				}

				if (!_wireframeBatchCache.TryGetValue(instance.meshId, out var batchId))
				{
					batchId = _env.BatchRenderer.CreateBatch(meshId, _wireframeMaterialId!.Value);
					_wireframeBatchCache[instance.meshId] = batchId;
				}

				var t = instance.transform;
				var entity = _env.Store.CreateEntity(
					new Position(t.position.X, t.position.Y, t.position.Z),
					new Scale3(t.scale.X, t.scale.Y, t.scale.Z),
					new Rotation(t.rotation.X, t.rotation.Y, t.rotation.Z, t.rotation.W),
					Tags.Get<GpuUpdateTag>());

				_env.ResourceManager.RegisterRenderable(entity, batchId);
				_wireframeEntities.Add(entity);
			}

			// See the matching Flush/WaitForIdle/InvalidateGraph comments in LoadModel/PollPendingLoad -
			// a new wireframe batch/material may have just been registered, which the render graph's
			// frozen ForwardPass commands need to be recompiled to pick up.
			_env.DilApi.ImmediateContext.Flush();
			_env.DilApi.ImmediateContext.WaitForIdle();
			_env.Pipeline.InvalidateGraph();
		}

		/// <summary>Resets the sub-mesh view mode/channel/wireframe toggle to their defaults (Highlight/
		/// Normal/off) whenever a genuinely new model or sub-mesh selection is about to be populated - a
		/// "Channel: Tangent" or wireframe choice made for one sub-mesh shouldn't silently carry over to
		/// an unrelated one (e.g. with different/no UV data). Wireframe overlay entities themselves are
		/// cleared by <see cref="ClearInstances"/>, called by both call sites right before this.</summary>
		private void ResetPreviewModeForNewSelection()
		{
			_viewMode = SubMeshPreviewMode.Highlight;
			_previewChannel = PreviewChannel.Normal;
			_wireframeEnabled = false;
		}

		private void ClearWireframeOverlay()
		{
			if (_wireframeEntities.Count == 0)
			{
				return;
			}

			foreach (var entity in _wireframeEntities)
			{
				_env.ResourceManager.UnregisterRenderable(entity);
				entity.DeleteEntity();
			}
			_wireframeEntities.Clear();

			// Without this, the wireframe overlay never actually disappears: ForwardPass's commands are
			// only (re-)recorded when the render graph recompiles (see DiligentRenderGraph.Compile/
			// Execute), and DiligentBatchRenderer.CheckAndReallocateBuffers - which re-uploads the CPU-side
			// instance array picking up the now-freed slots as holes for the culling compute shader to
			// skip - only runs from inside that recording (ForwardPass.WriteCommands). The compute dispatch
			// itself IS replayed every frame, but against whatever GPU instance buffer content existed at
			// the last compile, so it would keep "seeing" and drawing the unregistered wireframe instances
			// until something forces a recompile. Same Flush/WaitForIdle/InvalidateGraph triple as
			// LoadModel/PollPendingLoad/PopulateWireframeOverlay above, for consistency.
			_env.DilApi.ImmediateContext.Flush();
			_env.DilApi.ImmediateContext.WaitForIdle();
			_env.Pipeline.InvalidateGraph();
		}

		/// <summary>
		/// ????????? .gltf/.glb ?????? ?? ?????????? ???? ? ??????????? EntityStore ????? ??????.
		/// ?? ?????? ??????, ???? ???? ????????? ? ??? ???????????. ?????? ???????? (????? ????,
		/// ?? ?????? ? ?.?.) ?? ????????? ?????? - ??. <see cref="LoadError"/>.
		/// </summary>
		public void LoadModel(string modelPath, int subMeshIndex = -1)
		{
			// Ключ загрузки - пара (путь, сабмеш): та же модель с другим выбранным сабмешем
			// должна перезагрузиться (точнее, перенаселить сцену только этим сабмешем).
			if ((string.Equals(_loadedPath, modelPath, StringComparison.OrdinalIgnoreCase) && _loadedSubMesh == subMeshIndex) ||
			    (string.Equals(_loadingPath, modelPath, StringComparison.OrdinalIgnoreCase) && _loadingSubMesh == subMeshIndex))
			{
				return;
			}

			// Та же модель, что уже резидентна с предыдущего вызова (просто другой сабмеш выбран) -
			// файл уже распарсен и его меши/материалы уже зарегистрированы в _env.BatchRenderer, так
			// что достаточно перенаселить сцену, без диска, фоновой задачи и лоадер-хендла.
			if (_residentModel != null && string.Equals(_residentPath, modelPath, StringComparison.OrdinalIgnoreCase))
			{
				CancelPendingLoad();
				ClearInstances();

				try
				{
					ResetPreviewModeForNewSelection();
					PopulateFromScene(_residentModel, subMeshIndex);

					// See the matching comment in PollPendingLoad below - new batches were just
					// registered for this sub-mesh selection and the render graph must be recompiled
					// to pick them up.
					_env.DilApi.ImmediateContext.Flush();
					_env.DilApi.ImmediateContext.WaitForIdle();
					_env.Pipeline.InvalidateGraph();

					// AO/GI world-range (см. FrameAll) - только теперь, после барьера выше, той же
					// причине, что и в PollPendingLoad.
					_env.SetAoWorldRange(AoWorldRange());
					_env.SetAoStrength(Math.Clamp(_editorSettings.AoStrength, 0.1f, 4f),
						Math.Clamp(_editorSettings.AoFloor, 0f, 1f));
					_env.SetAoDebugView(_editorSettings.AoDebugView);
					ApplyGiSettings(pushRange: true);

					_loadedPath = modelPath;
					_loadedSubMesh = subMeshIndex;
					_loadError = null;
					ApplyPreviewSettingsToMaterials();
				}
				catch (Exception ex)
				{
					_loadedPath = null;
					_loadError = ex.Message;
					EditorConsoleLog.Add(LogLevel.Error, $"Model preview: failed to switch sub-mesh for '{modelPath}': {ex.Message}");
				}

				return;
			}

			CancelPendingLoad();

			EditorConsoleLog.Add(LogLevel.Warning,
				$"Model preview: FULL reload for '{modelPath}' subMesh={subMeshIndex} " +
				$"(resident was '{_residentPath}', model={(_residentModel is null ? "null" : "loaded")}) - " +
				"resident path did not match, re-parsing from disk instead of reusing it.");

			// «Сначала очистить предыдущее, потом грузить новое»: инстансы и резидентная модель
			// снимаются НЕМЕДЛЕННО, а не при готовности новой, - контент прошлого выбора не висит на
			// GPU всё время фоновой загрузки. ClearAll стримера выполняет обязательный протокол
			// освобождения (барьер GPU -> сброс регистраций батч-рендерера -> Release модели ->
			// пересборка графа - см. комментарии в PopulateFromScene про мега-буферы и замороженные
			// команды).
			ClearInstances();

			// Фоновая сборка BVH под пробы могла ещё читать геометрию старой модели, а ClearAll ниже
			// её освободит - ждём ДО освобождения (ResetProbeGi зовётся уже после и на этом месте
			// был бы поздно).
			WaitProbeBakerTask();

			// Ссылка на резидентную модель прошлого выбора - отпустить до ClearAll.
			if (_streamingModel != null)
			{
				_streamer.Release(_streamingModel);
				_streamingModel = null;
			}

			_streamer.ClearAll();
			_residentModel = null;
			_residentPath = null;
			_meshIdMap.Clear();
			_materialIdMap.Clear();
			_batchCache.Clear();
			// Wireframe-материал регистрировался в батч-рендерере - его регистрация умерла со
			// сбросом в ClearAll; сам объект освобождаем (GPU уже дождались там же) и пересоздадим
			// лениво в EnsureWireframeMaterial.
			_wireframeMaterial?.Release();
			_wireframeMaterial = null;
			_wireframeMaterialId = null;
			_wireframeBatchCache.Clear();

			// Куб отладочного BVH был зарегистрирован в ТОМ ЖЕ батч-рендерере, чьи регистрации
			// только что сброшены: его MeshId/BatchId стали недействительны, а сам меш надо
			// освободить (его GPU-буферы больше никому не принадлежат).
			ReleaseBvhDebugResources();

			_loadedPath = null;
			_loadedSubMesh = -1;

			// Пробы ссылались на материалы/BVH только что освобождённой модели - сброс за барьером,
			// который ClearAll уже сделал.
			ResetProbeGi();

			// Сама загрузка стартует из ModelStreamingSystem (в SystemRoot окружения) ближайшим
			// кадром; готовность опрашивает PollPendingLoad. Ошибки (файл пропал, битый glTF)
			// приходят через Resident.Failed тем же путём.
			_streamingModel = _streamer.Acquire(modelPath, _orbitTarget);
			_loadingPath = modelPath;
			_loadingSubMesh = subMeshIndex;
			_loadError = null;
		}

		/// <summary>Опции загрузки для стримера - прежние опции прямого BeginLoadAsync. Фабрика, а не
		/// снимок: MaxTextureSize/анизотропию пользователь меняет между загрузками. Кламп
		/// MaxTextureSize - это ПИКОВАЯ память загрузки: все текстуры модели декодируются разом и
		/// лежат несжатыми до самой заливки (см. EditorSettings.PreviewMaxTextureSize).</summary>
		private ModelLoadOptions BuildLoadOptions() => new()
		{
			VertexShader = _editorSettings.DefaultVertexShader,
			PixelShader = _editorSettings.DefaultPixelShader,
			OptimizeMesh = false,
			GenerateLods = false,
			AnisotropicFiltering = _editorSettings.PreviewAnisotropicFiltering,
			// log2 масштаба рендера: при апскейле мипы обязаны выбираться под ПОЛНОЕ разрешение,
			// иначе аккумулятору нечего восстанавливать (см. ModelLoadOptions.MipLodBias). Уровня
			// загрузки, как анизотропия: смена масштаба подхватится следующей загрузкой модели.
			MipLodBias = MathF.Log2(Math.Clamp(_editorSettings.RenderScale, 0.25f, 1f)),
			MaxTextureSize = ClampedMaxTextureSize(),
			// Меш появляется почти сразу с текстурами первой ступени (64px), дальше стример
			// поднимает качество ступенями по приоритету от камеры (см. ModelStreamer).
			StreamTextures = true
		};

		/// <summary>Потолок текстуры в том виде, в каком он уходит в загрузчик. Отдельным методом,
		/// потому что то же значение сравнивает диф перезагрузки: сравнивать сырую настройку с
		/// заклампленной - значит вечно видеть расхождение на значениях вне [128, 8192].</summary>
		private int ClampedMaxTextureSize() => Math.Clamp(_editorSettings.PreviewMaxTextureSize, 128, 8192);

		/// <summary>
		/// Cancels and releases the in-flight background load, if any - the background Task.Run in
		/// ModelLoader.PrepareModel checks the token between phases, so this actually stops it from
		/// continuing to burn CPU decoding textures for a model/sub-mesh selection the user has already
		/// moved on from, instead of just forgetting the reference and letting it run to completion
		/// unobserved.
		/// </summary>
		private void CancelPendingLoad()
		{
			// Отпускаем ссылку ТОЛЬКО если загрузка ещё шла: после готовности _streamingModel - это
			// ссылка, удерживающая РЕЗИДЕНТНУЮ модель от выселения стримером (переключение сабмеша
			// зовёт CancelPendingLoad и не должно её терять). Стример сам отменит фоновую задачу
			// (CancellationToken проверяется между фазами PrepareModel - декод реально остановится)
			// ближайшим Tick-ом, увидев ноль ссылок; хендл статуса закрывается там же.
			if (_loadingPath != null && _streamingModel != null)
			{
				_streamer.Release(_streamingModel);
				_streamingModel = null;
			}

			_loadingPath = null;
			_loadingSubMesh = -1;
		}

		/// <summary>Опрос стриминга текущего выбора. Саму загрузку (фоновый Prepare, покадровую
		/// финализацию порциями - дисциплину upload-хипа, регистрацию в батч-рендерере) ведёт
		/// <see cref="ModelStreamer"/> из ModelStreamingSystem; здесь - только реакция на готовность:
		/// перенос словарей регистраций, население сцены и пост-обвязка (AO/GI/материалы).</summary>
		private void PollPendingLoad()
		{
			if (_streamingModel == null || _loadingPath == null)
			{
				return;
			}

			var state = _streamingModel;

			if (state.Failed)
			{
				var failedPath = _loadingPath;
				var message = state.Error!;
				CancelPendingLoad();
				_loadedPath = null;
				_loadError = message;
				EditorConsoleLog.Add(LogLevel.Error, $"Model preview: failed to load '{failedPath}': {message}");
				return;
			}

			if (!state.Ready)
			{
				return;
			}

			var modelPath = _loadingPath;
			var subMeshIndex = _loadingSubMesh;
			_loadingPath = null;
			_loadingSubMesh = -1;

			ClearInstances();

			try
			{
				ResetPreviewModeForNewSelection();

				// Модель уже зарегистрирована стримером - переносим его словари регистраций в поля
				// вьюпорта (ими пользуются PopulateFromScene/wireframe) и объявляем модель резидентной
				// ДО населения: PopulateFromScene по ReferenceEquals поймёт, что регистрировать заново
				// нечего.
				_residentModel = state.Model;
				_residentPath = modelPath;
				_meshIdMap.Clear();
				_materialIdMap.Clear();
				_batchCache.Clear();
				foreach (var kvp in state.MeshIds)
				{
					_meshIdMap[kvp.Key] = kvp.Value;
				}
				foreach (var kvp in state.MaterialIds)
				{
					_materialIdMap[kvp.Key] = kvp.Value;
				}

				PopulateFromScene(state.Model!, subMeshIndex);

				// New batches were just registered in _batchRenderer for this model/sub-mesh, but
				// the render graph's ForwardPass commands are frozen after the first Compile() and
				// merely replayed on every Execute() (see IRenderGraph.Invalidate) - without this,
				// switching model/sub-mesh keeps drawing whatever batch set existed when the graph
				// was first compiled instead of the newly loaded one.
				//
				// Recompiling disposes and recreates every native resource the graph pinned (e.g.
				// ShadowPass's shadow maps) - same hazard as ResizeTargets below: with no
				// frame-in-flight fence in this engine, disposing GPU resources the previous frame's
				// (still in-flight) commands might reference races the GPU and can crash the driver.
				// Flush()+WaitForIdle() must run first, exactly as ResizeTargets does.
				_env.DilApi.ImmediateContext.Flush();
				_env.DilApi.ImmediateContext.WaitForIdle();
				_env.Pipeline.InvalidateGraph();

				// AO/GI world-range (см. FrameAll) - только теперь, после барьера выше: SetConstant
				// трогает ImmediateContext и метит AoMaterial dirty (пересборка PSO на следующий
				// draw), это небезопасно, пока предыдущий кадр ещё может быть в полёте.
				_env.SetAoWorldRange(AoWorldRange());
				_env.SetAoStrength(Math.Clamp(_editorSettings.AoStrength, 0.1f, 4f),
					Math.Clamp(_editorSettings.AoFloor, 0f, 1f));
				_env.SetAoDebugView(_editorSettings.AoDebugView);
				ApplyGiSettings(pushRange: true);

				_loadedPath = modelPath;
				_loadedSubMesh = subMeshIndex;
				_loadError = null;
				ApplyPreviewSettingsToMaterials();

				// Probe-GI пересчитывается под новую модель: сброс безопасен - барьер выше уже
				// дождался GPU, а раунды пойдут из PollProbeBake.
				ResetProbeGi();
				RequestProbeSession(delaySeconds: 0f);
			}
			catch (Exception ex)
			{
				_loadedPath = null;
				_loadError = ex.Message;
				EditorConsoleLog.Add(LogLevel.Error, $"Model preview: failed to load '{modelPath}': {ex.Message}");
			}
		}

		/// <summary>
		/// subMeshIndex &gt;= 0 - показываем только инстансы этого сабмеша, иначе всю модель. Сабмеш
		/// без единого инстанса (неиспользуемый меш в glTF) остаётся пустым - HasModel вернёт false
		/// и Render покажет "No model loaded" вместо синтетического инстанса.
		/// </summary>
		private void PopulateFromScene(ModelLoader modelLoader, int subMeshIndex = -1)
		{
			// Жизненный цикл модели теперь у ModelStreamer: регистрацию ресурсов он делает при
			// готовности загрузки, освобождение предыдущей модели - в LoadModel через ClearAll
			// (барьер GPU -> сброс регистраций -> Release -> пересборка графа; прежде этот протокол
			// жил здесь). Сюда модель приходит уже резидентной: PollPendingLoad переносит словари
			// регистраций и выставляет _residentModel ДО вызова, сабмеш-путь LoadModel передаёт
			// _residentModel сам. Чужая модель - нарушение жизненного цикла стриминга.
			if (!ReferenceEquals(modelLoader, _residentModel))
			{
				EditorConsoleLog.Add(LogLevel.Error,
					"Model preview: PopulateFromScene called with a non-resident model - streaming lifecycle violated, skipping.");
				return;
			}

			var instances = new List<InstanceData>(modelLoader.instances.Count);
			foreach (var candidate in modelLoader.instances)
			{
				if (subMeshIndex < 0 || candidate.meshId == subMeshIndex)
				{
					instances.Add(candidate);
				}
			}

			for (int i = 0; i < instances.Count; i++)
			{
				var instance = instances[i];
				var entity = ModelViewportGeometry.CreateInstanceEntity(_env.Store, _env.ResourceManager,
					_env.BatchRenderer, _meshIdMap, _materialIdMap, _batchCache,
					instance.meshId, instance.materialId, instance.transform);
				if (entity != null)
				{
					_instanceEntities.Add(entity.Value);
				}
			}

			// Framing must be based on the actual MESH geometry bounds of ALL sub-meshes/instances
			// (Scene.ComputeBounds, using Scene.Meshes[i].Center/Radius, computed by
			// MeshUtility.RecalculateBounds when the model was loaded, see Scene.cs) - a model
			// almost always consists of multiple sub-meshes/nodes, so a single mesh's bound is not
			// enough on its own; a mesh whose geometry is offset from its local origin (very common
			// for glTF nodes) would otherwise make the orbit target sit next to the model instead of
			// at its actual visual center, so the camera would circle some empty point beside it
			// rather than fully around it.

			// Для одиночного сабмеша считаем bounds только по ЕГО инстансам (аналог
			// ModelLoader.ComputeBounds, но с фильтром) - иначе камера кадрировала бы всю модель,
			// а маленький сабмеш где-нибудь с краю был бы едва различим.
			Vector3 boundsMin, boundsMax;
			if (subMeshIndex < 0)
			{
				(boundsMin, boundsMax) = modelLoader.ComputeBounds();
			}
			else
			{
				(boundsMin, boundsMax) = ModelViewportGeometry.ComputeSubMeshBounds(modelLoader, subMeshIndex);
			}

			// ??????????? ??? ??????? ??????? ? ???????
			EditorConsoleLog.Add(LogLevel.Info,
				$"Model preview bounds: min={boundsMin}, max={boundsMax}, instances={_instanceEntities.Count}");

			FrameAll(boundsMin, boundsMax);
		}


		private void ClearInstances()
		{
			foreach (var entity in _instanceEntities)
			{
				_env.ResourceManager.UnregisterRenderable(entity);
				entity.DeleteEntity();
			}
			_instanceEntities.Clear();
			ClearWireframeOverlay();

			// И боксы отладочного BVH: они такие же инстансы батч-рендерера, и снимать их надо
			// ЗДЕСЬ - то есть до любого сброса регистраций, пока их BatchId валиден. Иначе после
			// смены модели они продолжают рисоваться, подхватив геометрию новой модели.
			ClearBvhDebugOverlay();
			_bvhDebugState = default;
		}

		/// <summary>Цвет/интенсивность солнца для бейка проб: тот же keyIntensity, что у
		/// аналитического мирового света (ProbeGiParams.z в UnlitInstancedPS.hlsl) - иначе баунс не
		/// сойдётся по яркости с прямым светом: ярче - тень заливается эмбиентом, тусклее - отскок
		/// проваливается.</summary>
		private Vector3 ProbeSunColor() =>
			new Vector3(1f, 0.98f, 0.92f) * Math.Clamp(_editorSettings.ProbeGiSunIntensity, 0.1f, 16f);

		/// <summary>Заводит сессию прогрессивного бейка probe-GI по резидентной модели. BVH строится в
		/// ФОНЕ (см. _probeBakerTask): на сцене уровня Sponza это ~7.7 млн треугольников и
		/// ДЕСЯТКИ СЕКУНД - именно он держал редактор в "Not Responding" ПОСЛЕ того, как модель уже
		/// загрузилась. Сама сессия - только раскладка сетки и аллокации: лучей тут не пускается ни
		/// одного, трассировка идёт раундами из PollProbeBake. Требует мировой свет (ShadowSettings) -
		/// без него направление солнца неизвестно, остаёмся на старом константном ambient-е.</summary>
		private void BeginProbeSession()
		{
			if (_residentModel == null || _env.ShadowSettings == null || !_editorSettings.PreviewProbeGi)
			{
				return;
			}

			if (_probeBaker == null)
			{
				// Задача уже считает - дождёмся её из PollProbeBake, сессия заведётся следующим тиком.
				if (_probeBakerTask != null)
				{
					return;
				}

				var model = _residentModel;
				var modelPath = _residentPath;
				_probeBakerModel = model;
				_probeBakerSw = System.Diagnostics.Stopwatch.StartNew();

				// Чистый CPU по CPU-копиям вершин модели: пока модель жива, читать их из другого
				// потока безопасно. Освобождение модели обязано сперва дождаться этой задачи -
				// см. WaitProbeBakerTask (её зовут все точки, ведущие к ModelLoader.Release).
				//
				// LoadOrBuild сперва пробует кеш <модель>.bhv.bin рядом с файлом: сборка дерева не
				// зависит от настроек и на тяжёлом ассете стоит десятки секунд, а геометрия между
				// запусками не меняется.
				_probeBakerTask = Task.Run(() =>
				{
					var baker = ProbeGiBaker.LoadOrBuild(model, modelPath, out var fromCache);
					_probeBakerFromCache = fromCache;
					return baker;
				});

				return;
			}

			if (!_probeBaker.HasGeometry)
			{
				return;
			}

			var options = BuildProbeOptions();

			_probeSessionOptions = (true, options.SkyIntensity, options.RaysPerProbe, options.Bounces,
				options.BounceSaturation, options.GridDensity, options.MaxProbes,
				_editorSettings.ProbeGiHardwareRayTracing, _editorSettings.ProbeGiCascades,
				_editorSettings.ProbeGiVisRes);

			// Сессия пересоздаётся - GPU-путь прошлой сессии и его атласы обязаны уйти ДО того, как
			// появятся новые. BeginProbeSession зовётся на каждое изменение настроек (с дебаунсом),
			// так что без этого каждая правка ползунка утекала бы полным комплектом буферов, а
			// устаревшие SRB продолжали бы держать ресурсы - до рассинхрона и падения драйвера.
			ReleaseProbeGpuAndAtlases();

			// LightDirection указывает ОТ солнца (направление света), бейкер ждёт направление НА солнце.
			_probeSession = _probeBaker.BeginBake(_probeBoundsMin, _probeBoundsMax,
				Vector3.Normalize(-_env.ShadowSettings.LightDirection), ProbeSunColor(),
				_env.ShadowSettings.EnvYawRadians, _env.EnvironmentRadiance, options);

			// GPU - единственный привод раундов: CPU-раунд остался только эталоном сверки в CLI
			// (см. SceneTraceVerifier). Не поднялся - пробы выключены, о чём скажет статус; молча
			// уползать на путь в тысячу раз медленнее и без динамики было бы хуже отказа.
			if (!_probeGpuDisabled)
			{
				TryBeginProbeGpu(_probeSession);
			}
		}

		/// <summary>Настройки бейка из окна Graphics - общие для базового объёма и каскадов
		/// (каскад отличается только баундами, см. TryBeginProbeGpu).</summary>
		private ProbeGiBakeOptions BuildProbeOptions() =>
			ProbeGiViewportShared.BuildOptions(_editorSettings);

		/// <summary>Поднимает GPU-путь раунда для сессии: выгружает BVH в буферы, заводит атласы с
		/// UAV и привязывает их к материалам. Любая осечка (шейдер не собрался, формат не тянет UAV)
		/// - это откат на CPU-путь, а не падение редактора: probe GI не та фича, ради которой стоит
		/// ронять превью.</summary>
		private void TryBeginProbeGpu(ProbeGiBakeSession session)
		{
			try
			{
				// Захват поверхностей нужен уже при создании буферов - на CPU его строит первый
				// раунд, которого здесь не будет.
				// Только запечке - в реальном времени кэш не читается, а захват стопорит кадры.
				if (!_editorSettings.ProbeGiRealtime)
				{
					_probeBaker!.EnsureSurfaceCache(session);
				}

				// Атласы под запись из шейдера. Создаются здесь, а не после первого раунда: их
				// содержимое теперь появляется на GPU, забирать с CPU нечего.
				_probeTextures = new ProbeGiTextures(_graphicsApi, session.Result,
					$"_probeGi{_probeTextureGeneration++}", gpuWritable: true);
				_probeTextures.Bind(_residentModel!);
				ApplyPreviewSettingsToMaterials();

				// Аппаратная трассировка только если её и просят, и устройство её умеет. Структуры
				// строятся из мировых треугольников бейкера (см. ProbeSceneAccel).
				bool hardware = _editorSettings.ProbeGiHardwareRayTracing && RayTracingSupported;
				if (!hardware && _probeAccel != null)
				{
					// Аппаратную трассировку выключили - структуры больше не нужны.
					_probeAccel.Dispose();
					_probeAccel = null;
				}

				// Строятся ОДИН РАЗ на модель: геометрия от настроек probe-GI не зависит (см. поле).
				// Движение объектов их тоже не пересоздаёт - на него отвечает пересборка TLAS в
				// PollProbeAccel.
				_probeAccel ??= hardware
					? new ProbeSceneAccel(_env.DilApi, _probeBaker.InstancedGeometry)
					: null;

				_probeInstancePoses.Clear();

				// Конвейеры компилируются один раз и переживают пересоздание сессии. Пересобираем,
				// если сменилось устройство (иначе PSO остались бы от мёртвого девайса) или режим
				// трассировки - он задан кейвордом на этапе компиляции.
				if (_probePipelines != null
					&& (!ReferenceEquals(_probePipelinesApi, _env.DilApi) || _probePipelines.Hardware != hardware))
				{
					_probePipelines.Dispose();
					_probePipelines = null;
				}

				if (_probePipelines == null)
				{
					_probePipelines = new ProbeRoundPipelines(_env.DilApi, hardware);
					_probePipelinesApi = _env.DilApi;
				}

				_probeGpu = new ProbeRoundGpu(_env.DilApi, _probePipelines, session, _probeBaker,
					_probeTextures, _env.EnvironmentMap, _env.ShadowSettings!.EnvYawRadians, _probeAccel);

				// Мелкие каскады - только в реальном времени: запечке нужен один сходящийся объём.
				// Центрируются на точке интереса камеры и дальше следуют за ней (см.
				// PollProbeCascadeRecenter).
				int cascades = _editorSettings.ProbeGiRealtime
					? Math.Clamp(_editorSettings.ProbeGiCascades, 1, 3)
					: 1;
				for (int i = 1; i < cascades; i++)
				{
					_probeCascades.Add(CreateProbeCascade(i, _orbitTarget));
				}

				if (_probeCascades.Count > 0)
				{
					ApplyPreviewSettingsToMaterials();
				}
			}
			catch (Exception ex)
			{
				EditorConsoleLog.Add(LogLevel.Warning,
					$"Probe GI: GPU path unavailable, falling back to CPU: {ex.Message}");
				_probeGpuDisabled = true;
				ReleaseProbeGpu();
			}
		}

		/// <summary>Половина бокса каскада i: базовые баунды, делённые на 2^i, - тот же бюджет проб
		/// на меньший объём и есть выигрыш плотности.</summary>
		private Vector3 CascadeHalfExtent(int index) =>
			ProbeGiViewportShared.CascadeHalfExtent(_probeBoundsMin, _probeBoundsMax, index);

		private Vector3 ClampCascadeCenter(Vector3 target, Vector3 half) =>
			ProbeGiViewportShared.ClampCascadeCenter(_probeBoundsMin, _probeBoundsMax, target, half);

		/// <summary>Создаёт каскад i вокруг точки интереса: сессия + атласы (слоты _Ci) + GPU-раунд.
		/// Тот же бюджет и плотность на бокс в 2^i раз меньше = ячейка в 2^i раз мельче.</summary>
		private (ProbeGiBakeSession, ProbeGiTextures, ProbeRoundGpu, Vector3) CreateProbeCascade(
			int index, Vector3 target) =>
			ProbeGiViewportShared.CreateCascade(_probeBaker!, _probePipelines!, _probeAccel, _env,
				_graphicsApi, _editorSettings, new[] { _residentModel! },
				$"_probeGiC{index}_{_probeTextureGeneration++}", index, target,
				_probeBoundsMin, _probeBoundsMax, ProbeSunColor());

		/// <summary>Ведёт каскады за точкой интереса камеры - это и делает их каскадами, а не просто
		/// вложенными сетками. Каскад пересоздаётся вокруг новой точки, когда та ушла от его центра
		/// дальше четверти бокса (гистерезис: мелкие движения камеры не трогают ничего).
		///
		/// Пересоздание - это ХОЛОДНЫЙ СТАРТ каскада, а не тороидальный скролл с переносом
		/// накопленного: провал прикрыт фолбэком выборки на крупный объём (см. SampleProbeGi), и за
		/// разгонные раунды (~доли секунды) мелкий каскад дотекает заново. Скролл со сдвигом
		/// индирекции - следующий шаг, если холодный старт окажется виден глазом; не больше одного
		/// каскада за кадр, чтобы не собирать все пересоздания в один хитч.</summary>
		/// <summary>Дебаунс перецентровки каскадов: пересоздавать их можно только когда камера
		/// УСПОКОИЛАСЬ.
		///
		/// Перецентровка - это не сдвиг, а полное пересоздание объёма: Dispose GPU-раунда, Release
		/// атласов, Flush + WaitForIdle (полный стоп GPU на потоке рендера) и новая сессия с
		/// обнулённым полем. За один драг средней кнопки (пан камеры двигает _orbitTarget) порог
		/// смещения пересекается многократно, и всё это происходило по нескольку раз подряд - отсюда
		/// и рывки, и «все пробы перестраиваются» на ровном месте.
		///
		/// Правильное лечение - тороидальная прокрутка объёма вместо пересоздания, но это отдельная
		/// крупная работа; дебаунс убирает повторные пересоздания за копейки и той же механикой, что
		/// уже применена к пересозданию сессии (см. ProbeRebakeDebounceSeconds).</summary>
		private const float CascadeRecenterIdleSeconds = 0.35f;
		private Vector3 _cascadeRecenterTarget;
		private float _cascadeRecenterIdle;

		private void PollProbeCascadeRecenter(float deltaTime)
		{
			if (_probeCascades.Count == 0 || _probeBaker == null || _env.ShadowSettings == null
				|| _residentModel == null || _probePipelines == null)
			{
				return;
			}

			// Камера ещё движется - ждём. Сравнение с ПРЕДЫДУЩИМ кадром, а не с центром каскада:
			// нас интересует именно «мышь отпустили», а не «уехали далеко».
			if (_orbitTarget != _cascadeRecenterTarget)
			{
				_cascadeRecenterTarget = _orbitTarget;
				_cascadeRecenterIdle = 0f;
				return;
			}

			_cascadeRecenterIdle += deltaTime;
			if (_cascadeRecenterIdle < CascadeRecenterIdleSeconds)
			{
				return;
			}

			for (int j = 0; j < _probeCascades.Count; j++)
			{
				int index = j + 1;
				var half = CascadeHalfExtent(index);
				var desired = ClampCascadeCenter(_orbitTarget, half);
				if (!ProbeGiViewportShared.NeedsRecenter(_probeCascades[j].Center, desired, half))
				{
					continue;
				}

				try
				{
					// Старый каскад держат и замороженные команды графа (оверлей), и SRB материалов -
					// порядок тот же, что при любом освобождении атласов: оверлей, барьер,
					// плейсхолдеры, и только потом ресурсы.
					ReleaseProbeDebugOverlay();
					_env.DilApi.ImmediateContext.Flush();
					_env.DilApi.ImmediateContext.WaitForIdle();
					RebindCascadePlaceholders($"_C{index}");
					_probeCascades[j].Gpu.Dispose();
					_probeCascades[j].Textures.Release();

					_probeCascades[j] = CreateProbeCascade(index, _orbitTarget);
					ApplyPreviewSettingsToMaterials();
				}
				catch (Exception ex)
				{
					// Каскад умер - выкидываем его целиком: выборка сама провалится на следующий
					// объём, а бесконечно повторять отказ каждый кадр движения нельзя.
					EditorConsoleLog.Add(LogLevel.Error,
						$"Probe GI: cascade {index} recenter failed, dropping it: {ex.Message}");
					RebindCascadePlaceholders($"_C{index}");
					_probeCascades.RemoveAt(j);
					ApplyPreviewSettingsToMaterials();
				}

				break;
			}
		}

		/// <summary>Освобождает GPU-путь вместе с атласами, за барьером. Ресурсы мог читать ещё
		/// находящийся в полёте кадр - тот же Flush+WaitForIdle, что при любом освобождении
		/// GPU-ресурсов здесь (см. PollPendingLoad).</summary>
		private void ReleaseProbeGpuAndAtlases()
		{
			if (_probeGpu == null && _probeTextures == null)
			{
				return;
			}

			// Первым: замороженные команды графа рисуют оверлей по этим атласам - освободить их под
			// живыми командами значит оставить дроу с мёртвыми дескрипторами.
			ReleaseProbeDebugOverlay();

			_env.DilApi.ImmediateContext.Flush();
			_env.DilApi.ImmediateContext.WaitForIdle();

			ReleaseProbeGpu();
			_probeTextures?.Release();
			_probeTextures = null;
		}

		/// <summary>Освобождает GPU-путь раунда (вместе с каскадами - они существуют только в нём),
		/// но НЕ структуры ускорения: те живут по модели и переживают пересоздание сессии (см.
		/// _probeAccel). Звать РАНЬШЕ освобождения атласов базового объёма: раунды держат на них
		/// представления, и обратный порядок роняет драйвер.</summary>
		private void ReleaseProbeGpu()
		{
			_probeGpu?.Dispose();
			_probeGpu = null;

			if (_probeCascades.Count == 0)
			{
				return;
			}

			// Атласы каскадов - только после возврата ПЛЕЙСХОЛДЕРОВ в материалы и барьера: слоты
			// _C1/_C2 привязаны статически, и освобождение под живыми привязками оставило бы
			// материалам мёртвые дескрипторы, даже при Origin.w = 0 (мёртвая ветка не спасает от
			// валидации привязок). Кбуфер занулит PushProbeGridConstants следующим кадром.
			if (_residentModel != null)
			{
				for (int i = 1; i <= 2; i++)
				{
					RebindCascadePlaceholders($"_C{i}");
				}
			}

			_env.DilApi.ImmediateContext.Flush();
			_env.DilApi.ImmediateContext.WaitForIdle();

			foreach (var cascade in _probeCascades)
			{
				cascade.Gpu.Dispose();
				cascade.Textures.Release();
			}

			_probeCascades.Clear();
		}

		/// <summary>Возвращает слотам каскада безопасную текстуру-плейсхолдер (та же env-карта, что
		/// при создании окружения, см. ModelViewportEnvironment) перед освобождением его атласов.</summary>
		private void RebindCascadePlaceholders(string suffix)
		{
			ProbeGiViewportShared.RebindCascadePlaceholders(_residentModel!, _env.EnvironmentMap, suffix);
		}

		/// <summary>Покадровый привод probe-GI: забирает завершившийся раунд в атласы, тикает дебаунс
		/// пересоздания сессии и запускает следующий раунд. Раунды идут строго по одному - сессия не
		/// потокобезопасна, и всё, что её трогает (снимок, обновление света), делается здесь, пока
		/// фоновая задача не бежит.</summary>
		private void PollProbeBake(float deltaTime)
		{
			if (_probeRoundTask != null)
			{
				if (!_probeRoundTask.IsCompleted)
				{
					return;
				}

				var finished = _probeRoundTask;
				_probeRoundTask = null;
				_probeRoundMs = _probeRoundSw.ElapsedMilliseconds;

				if (finished.IsCompletedSuccessfully)
				{
					UploadProbeSnapshot();
				}
				else
				{
					// Раунд упал - сессия могла остаться в полуобновлённом состоянии, дальше её не
					// крутим. Уже показанные пробы остаются на экране.
					_probeSession = null;
					_probeStatus = "ошибка бейка";
					EditorConsoleLog.Add(LogLevel.Error, "Probe GI: bake round failed: " +
						(finished.Exception?.GetBaseException().Message ?? "Unknown error"));
				}
			}

			// Фоновая сборка BVH завершилась - принимаем её и заводим сессию (BeginProbeSession
			// увидит готовый _probeBaker и пойдёт дальше по обычному пути).
			if (_probeBakerTask != null && _probeBakerTask.IsCompleted)
			{
				var task = _probeBakerTask;
				var builtFor = _probeBakerModel;
				_probeBakerTask = null;
				_probeBakerModel = null;

				if (!task.IsCompletedSuccessfully)
				{
					EditorConsoleLog.Add(LogLevel.Error,
						$"Probe GI: failed to build BVH: {task.Exception?.GetBaseException().Message}");
				}
				else if (ReferenceEquals(builtFor, _residentModel))
				{
					_probeBaker = task.Result;
					(_probeBoundsMin, _probeBoundsMax) = _residentModel!.ComputeBounds();

					var stats = _probeBaker.GetStats();
					EditorConsoleLog.Add(LogLevel.Info,
						$"Probe BVH {(_probeBakerFromCache ? "loaded from cache" : "built")} in " +
						$"{_probeBakerSw?.ElapsedMilliseconds ?? 0} ms: {stats.Triangles} tris, {stats.Nodes} nodes, " +
						$"{stats.Leaves} leaves, depth {stats.MaxDepth}, avg {stats.AvgLeafTriangles:F1} tris/leaf" +
						(_probeBakerFromCache ? "" : $" -> cached as '{System.IO.Path.GetFileName(ProbeGiBvhCache.GetCachePath(_residentPath ?? ""))}'"));

					_probeBakerSw = null;
					BeginProbeSession();
				}
				// Иначе модель успели сменить - результат относится к уже мёртвой геометрии.
			}

			if (_probeSessionDelay >= 0f)
			{
				_probeSessionDelay -= deltaTime;
				if (_probeSessionDelay < 0f)
				{
					BeginProbeSession();
				}
			}

			var session = _probeSession;
			var baker = _probeBaker;
			if (session == null || baker == null || !_editorSettings.PreviewProbeGi)
			{
				return;
			}

			// Режим подтягивается в живую сессию, как и свет: это пол веса раунда, а не параметр
			// раскладки, - пересоздавать сессию (и выбрасывать поле) ради галочки незачем.
			session.Realtime = _editorSettings.ProbeGiRealtime;
			session.RealtimeRaysPerRound = Math.Clamp(_editorSettings.ProbeGiRealtimeRays, 8, 1024);
			session.RealtimeBlend = Math.Clamp(_editorSettings.ProbeGiRealtimeBlend, 0.01f, 0.5f);
			session.RealtimeMaxStep = Math.Clamp(_editorSettings.ProbeGiRealtimeMaxStep, 0f, 0.2f);
			session.RealtimeGamma = Math.Clamp(_editorSettings.ProbeGiRealtimeGamma, 1f, 8f);
			session.RealtimeRelocation = Math.Clamp(_editorSettings.ProbeGiRealtimeRelocation, 0f, 0.45f);

			PollProbeAccel(session);

			// Свет подтягивается перед каждым раундом: повернули солнце или подвинули его
			// интенсивность - сессия откатит сходимость и дотечёт до нового решения сама, не
			// выбрасывая ни геометрию, ни поле как стартовое приближение.
			if (_env.ShadowSettings != null)
			{
				session.SetLighting(Vector3.Normalize(-_env.ShadowSettings.LightDirection),
					ProbeSunColor(), _env.ShadowSettings.EnvYawRadians, _env.EnvironmentRadiance);
			}

			if (session.Converged)
			{
				return;
			}

			_probeRoundSw.Restart();

			if (_probeGpu != null)
			{
				// Предыдущий РАУНД ещё считается - ждём. Забор стоит на раунде, а не на порции:
				// иначе каждая порция ждала бы завершения предыдущей, и при 1.7 мс счёта мы теряли
				// бы целый кадр на ожидание (см. ProbeRoundGpu.SignalRound).
				if (!_probeGpu.IsReady)
				{
					return;
				}

				// GPU-раунд - это запись команд, а не работа: в фоновый Task его уносить незачем
				// (и нельзя - контекст принадлежит потоку рендера). Атласы шейдер пишет сам, так
				// что ни снимка, ни заливки после раунда не нужно.
				try
				{
					// Порции и раунды - общим циклом (см. ProbeGiViewportShared.DriveChunks): бюджет
					// тратится целиком, переходя границы раундов, а не обрывается на первом же.
					int volumeCount = 1 + _probeCascades.Count;
					ProbeGiViewportShared.DriveChunks(_probeGpu, session, baker,
						Math.Max(ProbeChunksPerFrame,
							_probeGpu.ChunksPerFrame(session.RaysPerRound, volumeCount)));
					_probeRoundMs = _probeRoundSw.ElapsedMilliseconds;

					// Мелкие каскады - общим приводом (см. ProbeGiViewportShared.DriveVolume), каждый
					// со своим забором и тем же бюджетом порций.
					if (_env.ShadowSettings != null)
					{
						var sunDir = Vector3.Normalize(-_env.ShadowSettings.LightDirection);
						foreach (var cascade in _probeCascades)
						{
							ProbeGiViewportShared.DriveVolume(cascade.Gpu, cascade.Session, baker,
								_editorSettings, sunDir, ProbeSunColor(),
								_env.ShadowSettings.EnvYawRadians, _env.EnvironmentRadiance,
								// ЧИСЛО ОБЪЁМОВ, а не бюджет порций: здесь стоял ProbeChunksPerFrame (8), и
								// каждый каскад получал восьмую часть лучевого бюджета вместо своей доли
								// (см. ProbeRoundGpu.ChunksPerFrame) - на двух каскадах это вчетверо медленнее чем надо.
								volumeCount);
						}
					}
				}
				catch (Exception ex)
				{
					EditorConsoleLog.Add(LogLevel.Error,
						$"Probe GI: GPU round failed, probes disabled: {ex.Message}");
					_probeGpuDisabled = true;
					ReleaseProbeGpu();
				}
			}

			// CPU-привода больше нет: раунды крутит только GPU, CPU-раунд остался эталоном сверки
			// в CLI. _probeGpu == null здесь значит «GPU-путь не поднялся» - пробы стоят.
		}

		/// <summary>Ведёт структуры ускорения за движущейся сценой: если хоть один инстанс сменил
		/// позу, TLAS пересобирается под новые матрицы. Это и есть весь механизм динамической
		/// геометрии - BLAS-ы не трогаются, они в объектном пространстве (см. ProbeSceneAccel), а
		/// поле само перетечёт к новому решению за единицы раундов, потому что в реальном времени
		/// оно ни к чему не сходится (см. ProbeGiBakeOptions.Realtime).
		///
		/// Позы читаются из модели, а не из ECS: бейкер собирал геометрию по ней же, поэтому
		/// нумерация инстансов совпадает гарантированно (см. ProbeGeometryInstance.SourceInstance).
		/// Сцена стоит на месте - не делается вообще ничего, включая аллокации.</summary>
		private void PollProbeAccel(ProbeGiBakeSession session)
		{
			var accel = _probeAccel;
			var baker = _probeBaker;
			if (accel == null || baker == null || _residentModel == null || _probeAccelFrozen)
			{
				return;
			}

			// Только на границе раунда: пересборка под уже выпущенными порциями записала бы в поле
			// шов из половины старой сцены и половины новой (см. ProbeRoundGpu.AtRoundStart).
			if (_probeGpu is { AtRoundStart: false })
			{
				return;
			}

			var instances = baker.InstancedGeometry.Instances;
			bool moved = _probeInstancePoses.Count != instances.Length;
			if (moved)
			{
				_probeInstancePoses.Clear();
				for (int i = 0; i < instances.Length; i++)
				{
					_probeInstancePoses.Add(instances[i].Transform);
				}
			}

			var modelInstances = _residentModel.instances;
			for (int i = 0; i < instances.Length; i++)
			{
				int source = instances[i].SourceInstance;
				var pose = source >= 0 && source < modelInstances.Count
					? ProbeGiBaker.InstanceMatrix(modelInstances[source].transform)
					: instances[i].Transform;

				if (_probeInstancePoses[i] != pose)
				{
					_probeInstancePoses[i] = pose;
					moved = true;
				}
			}

			if (!moved)
			{
				return;
			}

			try
			{
				accel.Rebuild(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_probeInstancePoses));

				// Движение объекта БОЛЬШЕ НЕ трогает ни релокацию, ни вес раунда. Здесь стоял
				// ReopenRelocation(), и он делал две ГЛОБАЛЬНЫЕ вещи на каждый кадр драга гизмо:
				// открывал окно релокации у ВСЕЙ сетки и откатывал Round, отчего вес раунда
				// прыгал с MinBlend (~0.05) до 0.5 - вдесятеро, тоже у всей сетки разом. Пока объект
				// тащат, это повторялось каждый кадр, то есть всё время драга поле шло ПРАКТИЧЕСКИ
				// НЕФИЛЬТРОВАННЫМ (видимое кипение), а сон проб был выключен целиком
				// (условие сна требует RelocationRoundsLeft == 0, см. ProbeRoundGpu).
				//
				// Majercik 2021 §5 прямо запрещает двигать пробы вокруг динамики («this causes
				// instability; a stable result is preferable to an unstable result with lower average
				// error») - пробы двигаются только на инициализации (это делает конструктор
				// сессии). Пробу, которую накрыло движущимся объектом, ловят backface-эвристики
				// (§4.1): она просто молчит, пока её накрывают. А за самим светом поле следит
				// и без отката: в реальном времени alpha - это экспоненциальное среднее с постоянной
				// MinBlend, оно отслеживает изменения непрерывно - в этом весь смысл режима.
				//
				// Локальное пробуждение только ближних проб (§6.3: расширенный AABB динамического
				// объекта -> Newly Awake) требует пер-пробных состояний, которых здесь пока нет;
				// глобальный откат был негодным приближением: расшатывал 100% сетки ради единиц
				// процентов проб возле объекта.
				//
				// Запечке же откат нужен, и только он: она останавливается по Converged, и без сброса
				// поле навсегда осталось бы с объектом в старой позе (см. InvalidateGeometry).
				session.InvalidateGeometry();
			}
			catch (Exception ex)
			{
				// Структуры НЕ освобождаются: их держат уже записанные привязки GPU-раунда, и
				// освобождение здесь оставило бы шейдеру висячий дескриптор. Сцена для трассировки
				// просто замирает в последней удавшейся позе - пробы остаются валидными, теряется
				// только слежение за движением.
				_probeAccelFrozen = true;
				EditorConsoleLog.Add(LogLevel.Error,
					$"Probe GI: TLAS rebuild failed, scene frozen for tracing: {ex.Message}");
			}
		}

		/// <summary>Ведёт дебаг-вид проб за галочкой и жизнью атласов. Создание/снятие пересобирает
		/// рендер-граф (команды заморожены, см. GraphicsPipelineSimple.InlineOverlay), поэтому
		/// сравнение «хочу/есть» обязано быть дешёвым - оно и есть пара сравнений ссылок.</summary>
		private void PollProbeDebugOverlay() =>
			ProbeGiViewportShared.PollOverlays(_probeDebugOverlays,
				_editorSettings.PreviewProbeGi && _editorSettings.ProbeGiShowProbes,
				ref _probeDebugFailed, _env, _graphicsApi, _probeSession, _probeTextures,
				_probeCascades);

		private void ReleaseProbeDebugOverlay() =>
			ProbeGiViewportShared.ReleaseOverlays(_probeDebugOverlays, _env);

		/// <summary>Забирает снимок текущего поля сессии в GPU-атласы: создаёт их один раз на сетку и
		/// дальше обновляет НА МЕСТЕ (см. ProbeGiTextures.Update) - пересоздавать пять текстур,
		/// переприязывать их к материалам и ставить Flush+WaitForIdle каждый раунд было бы дороже
		/// самого раунда.</summary>
		private void UploadProbeSnapshot()
		{
			var session = _probeSession;
			// Модель выгрузили/окружение пересоздали, пока раунд шёл (см. ResetProbeGi) - результат
			// уже не про текущую сцену.
			if (session == null || _probeBaker == null || _residentModel == null)
			{
				return;
			}

			try
			{
				var snapshot = _probeBaker.Snapshot(session);

				// Сетка сменилась (новая сессия) - атласы под неё не подходят. Старые мог читать ещё
				// находящийся в полёте кадр, поэтому тот же Flush+WaitForIdle барьер, что при любом
				// освобождении GPU-ресурсов здесь (см. PollPendingLoad).
				if (_probeTextures != null && !_probeTextures.Matches(snapshot))
				{
					// Оверлей держит эти атласы в замороженных командах графа - снять до Release.
					ReleaseProbeDebugOverlay();
					_env.DilApi.ImmediateContext.Flush();
					_env.DilApi.ImmediateContext.WaitForIdle();
					_probeTextures.Release();
					_probeTextures = null;
				}

				if (_probeTextures == null)
				{
					_probeTextures = new ProbeGiTextures(_graphicsApi, snapshot,
						$"_probeGi{_probeTextureGeneration++}");
					_probeTextures.Bind(_residentModel);
					ApplyPreviewSettingsToMaterials();
				}
				else
				{
					_probeTextures.Update(snapshot);
				}
			}
			catch (Exception ex)
			{
				_probeTextures = null;
				EditorConsoleLog.Add(LogLevel.Error, $"Probe GI: failed to upload atlases: {ex.Message}");
			}
		}

		/// <summary>Сбрасывает состояние probe-GI при смене модели/пересоздании окружения. Звать
		/// ПОСЛЕ Flush+WaitForIdle (обе точки вызова уже за барьером) - освобождает GPU-атласы.
		/// Недосчитавшийся фоновый раунд не отменяется, а осиротевает вместе со своей сессией:
		/// чистый CPU-таск, его результат просто некому забрать.</summary>
		/// <summary>Дожидается фоновой сборки BVH. ОБЯЗАТЕЛЬНО перед любым освобождением модели:
		/// задача читает CPU-копии вершин в неуправляемой памяти (IMeshObject), и ModelLoader.Release
		/// из-под неё - это чтение освобождённой памяти, а не безобидная гонка.</summary>
		private void WaitProbeBakerTask()
		{
			if (_probeBakerTask == null)
			{
				return;
			}

			try
			{
				_probeBakerTask.Wait();
			}
			catch (Exception)
			{
				// Ошибка сборки уже будет доложена в PollProbeBake - здесь важен только сам факт,
				// что поток закончил читать геометрию.
			}

			_probeBakerTask = null;
			_probeBakerModel = null;
		}

		private void ResetProbeGi()
		{
			// До всего остального: фоновая сборка BVH ещё может читать геометрию модели, которую
			// вызывающий вот-вот освободит.
			WaitProbeBakerTask();

			// Первым: оверлей держит атласы в материале, а его снятие включает барьер Flush+WaitForIdle.
			ReleaseProbeDebugOverlay();

			// Раньше атласов: GPU-объект держит на них представления (см. ReleaseProbeGpu).
			ReleaseProbeGpu();

			// Структуры ускорения привязаны к геометрии модели - уходят вместе с бейкером.
			_probeAccel?.Dispose();
			_probeAccel = null;
			_probeBaker = null;
			_probeSession = null;
			_probeRoundTask = null;
			_probeSessionDelay = -1f;
			_probeSessionOptions = default;
			_probeStatus = "нет проб";

			if (_probeTextures != null)
			{
				_probeTextures.Release();
				_probeTextures = null;
			}
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

		/// <summary>
		/// ?????????? ??????????? (?????????) ECS/render-graph ?????? ?? ???? ????. ??????
		/// ?????????? ??? ? ???? ????????? (??. EditorManager.OnUpdate) ??? ??? ?? GPU-?????, ??? ?
		/// ???????? ?????, ????????? ?????????? ????? IGraphicsApi/??????????.
		/// </summary>
		public void Update(float deltaTime, float time)
		{
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
			PollProbeCascadeRecenter(deltaTime);
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
				EditorConsoleLog.Add(LogLevel.Error, $"Model preview: render failed for '{_loadedPath}': {ex.Message}");
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

			// Сценовые таргеты (депт/HDR/scene-copy/MSAA/AO/GI) - в РЕНДЕР-размере: при масштабе
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
			_env.MsaaColorTarget?.Resize(sceneSize);
			_env.MsaaDepthTarget?.Resize(sceneSize);
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



