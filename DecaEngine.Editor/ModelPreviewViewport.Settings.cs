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
	/// <summary>Применение окна Graphics к превью: фичи конвейера, живые ручки, пересоздание окружения. Часть <see cref="ModelPreviewViewport"/> - файл на тему;
	/// состояние, конструктор и кадровые Update/Render живут в основном файле.</summary>
	public partial class ModelPreviewViewport
	{
		/// <summary>Режим RT-теней запрошен и устройство его умеет. Уровня ЗАГРУЗКИ (кейворд в
		/// вариантах шейдера, см. ModelLoadOptions.RtShadows) - смена гоняется через пересоздание
		/// окружения (см. needsRecreate).</summary>
		private bool RtShadowsEnabled() =>
			_editorSettings.ShadowFilterMode == 4 && RayTracingSupported;
		private bool _appliedRtShadows;

		// GPU-путь один раз сорвался - больше не пробуем до перезапуска редактора. Иначе каждая
		// пересборка сессии повторяла бы отказ, а если он роняет драйвер - в цикле.
		private bool _probeGpuDisabled;

		// Параметры, под которыми заведена ТЕКУЩАЯ сессия: диф с настройками в ApplyGraphicsSettings
		// решает, нужно ли её пересоздавать. Интенсивность солнца сюда НЕ входит - она подтягивается
		// в живую сессию каждым раундом, как и поворот света (live-ручки шейдера тем более: они
		// пушатся кбуфером и бейка не касаются).
		private (bool On, float Sky, int Rays, int Bounces, float BounceSaturation,
			float Density, int MaxProbes, bool HardwareTrace, int VisRes) _probeSessionOptions;

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
			PushSsrEnvironment();
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
			ViewportSettingsPush.Upscaler(_env, _editorSettings);
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

		/// <summary>Обработчик "OK" окна настроек: env-level опции (скай/HDR/анизотропия)
		/// при изменении применяются пересозданием окружения с перезагрузкой текущей модели,
		/// live-биты - как обычно. Ничего не изменилось - ничего и не пересоздаётся.</summary>
		private void OnGraphicsSettingsChanged()
		{
			// Пересоздания окружения требуют только те опции, что запечены НЕ в конвейер:
			// HDRI энвайронмента (пересчёт IBL),
			// анизотропия (в сэмплеры материалов) и потолок текстур (в декодер загрузчика). Всё
			// остальное - фичи конвейера, применяются на живом окружении без единой перезагрузки
			// модели (см. GraphicsPipelineSimple.SetFeatures). Ровно этот список и стоит за кнопкой
			// "Применить" в окне Graphics - см. GraphicsSettingsWindow.DrawApplyBar.
			bool needsRecreate =
				_appliedHdrPath != (ResolveEnvironmentHdrPath(_editorSettings.PreviewEnvironmentHdr) ?? "") ||
				_appliedAniso != _editorSettings.PreviewAnisotropicFiltering ||
				_appliedMaxTextureSize != ClampedMaxTextureSize() ||
				// RT-тени - кейворд в вариантах шейдера модели (см. ModelLoadOptions.RtShadows):
				// пересечение границы «Ray-traced» перечитывает модель, остальные режимы живые.
				_appliedRtShadows != RtShadowsEnabled();

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
			// ДО SetFeatures: предикат RT-фолбэка смотрит на живой accel (см. EnsureSsrOwnRayScene).
			EnsureSsrOwnRayScene();

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
				// SSR тянет векторы за собой - см. CreateEnvironment.
				MotionVectors = _appliedMotionVectors || _editorSettings.PreviewSsr,
				TemporalUpscale = _appliedMotionVectors && _editorSettings.TemporalUpscale,
				Ssr = _editorSettings.PreviewSsr,
				SsrRayTraced = SsrRayTracedEnabled(),
				SsrHitTextures = _editorSettings.SsrHitTextures,
			});

			// RT-вариант трейса обязан получить TLAS ДО первого кадра (см. SsrPassResources.SetRayScene);
			// probe-поле для света RT-хитов - по той же причине здесь же.
			UpdateSsrRayScene();
			_env.SetSsrProbeField(_probeTextures);

			// Смена RT-фолбэка пересоздала SSR-ресурсы - живые ручки откатились в дефолты.
			ApplySsrSettings();
		}


		/// <summary>Зеркало PrefabSceneViewport.SsrRayTracedBlockReason - для строки статуса окна
		/// Graphics (см. комментарий там: тихий даунгрейд неотличим от поломки).</summary>
		public string? SsrRayTracedBlockReason
		{
			get
			{
				if (_graphicsApi.RayTracing < RayTracingSupport.Inline)
				{
					return "нет inline-трассировки (нужен D3D12)";
				}
				if (_probeAccel == null && _ssrOwnAccel == null)
				{
					return _residentModel == null
						? "в превью не открыта модель (RT-фолбэк превью ждёт её; Scene View - независим)"
						: "accel модели ещё не собран (модель грузится)";
				}
				if (_env.Pipeline.SsrResources is not { RayTraced: true })
				{
					return "ресурсы SSR ещё не пересобраны под RT-вариант";
				}
				return null;
			}
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
			_rtShadowScene?.Release();
			_rtShadowScene = null;
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
		/// в окне настроек; рестарт-левел опции (скай/HDR) считываются конструктором.</summary>
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
		private void ApplyBloomSettings() => ViewportSettingsPush.Bloom(_env, _editorSettings);


		private void ApplyColorGradeSettings() => ViewportSettingsPush.ColorGrade(_env, _editorSettings);

		private void ApplyFogSettings() => ViewportSettingsPush.Fog(_env, _editorSettings);

		private void ApplyVolumetricSettings() => ViewportSettingsPush.Volumetric(_env, _editorSettings);
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
		/// <summary>Зеркало PrefabSceneViewport.PushSsrEnvironment: покадровые данные SSR (yaw
		/// env-карты и солнце RT-фолбэка).</summary>
		private void PushSsrEnvironment()
		{
			var shadowSettings = _env.ShadowSettings;
			if (shadowSettings == null)
			{
				return;
			}

			// Те же константы света, что у PrefabSceneViewport.PushSsrEnvironment - см. комментарий там.
			_env.SetSsrEnvironment(shadowSettings.EnvYawRadians,
				-Vector3.Normalize(shadowSettings.LightDirection),
				new Vector3(1f, 0.97f, 0.9f), 0.55f);
		}

		/// <summary>Пуш живых ручек SSR - вместе с SSGI (см. ApplyGiSettings ниже).</summary>
		private void ApplySsrSettings()
		{
			_env.SetSsrParams(
				Math.Clamp(_editorSettings.SsrIntensity, 0f, 4f),
				Math.Clamp(_editorSettings.SsrMaxRoughness, 0.05f, 1f),
				Math.Clamp(_editorSettings.SsrThickness, 0.01f, 5f),
				Math.Clamp(_editorSettings.SsrMaxDistance, 1f, 500f),
				Math.Clamp(_editorSettings.SsrHistoryWeight, 0f, 0.97f),
				Math.Clamp(_editorSettings.SsrRaysPerPixel, 1, 4),
				_editorSettings.SsrDebugView,
				Math.Clamp(_editorSettings.SsrRtBounces, 1, 4),
				Math.Clamp(_editorSettings.SsrTraceMode, 0, 1));
			PushSsrEnvironment();
		}

		private void ApplyGiSettings(bool pushRange)
		{
			ApplySsrSettings();
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

	}
}
