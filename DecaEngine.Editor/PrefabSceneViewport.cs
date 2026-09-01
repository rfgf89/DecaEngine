using System;
using System.Collections.Generic;
using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Core.Diagnostics;
using DecaEngine.Core.Assets;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
using DecaEngine.Graphics.ProbeGi;
using Engine.ImGui.Core;
using System.Threading.Tasks;
using Friflo.Engine.ECS;
using Hexa.NET.ImGui;
using Hexa.NET.ImGuizmo;
using UnsafeCollections.Collections.Unsafe;
using DecaEngine.Scene;
using DecaEngine.Animation;

namespace DecaEngine.Editor
{
	/// <summary>
	/// GPU-вьюпорт окна Scene View: рендерит сущности редактируемого префаба (см.
	/// <see cref="InspectorWindow"/>) через собственное офскрин-окружение
	/// <see cref="ModelViewportEnvironment"/> (GraphicsPipelineSimple - тот же конвейер, что у
	/// превью моделей). Сущности с компонентом <see cref="ModelRenderer"/> грузят свои .gltf/.glb
	/// по <see cref="AssetRef"/> (пути относительно "Assets" проекта), инстансы моделей следуют за
	/// TRS-иерархией префаба live, выделенная сущность манипулируется гизмо ImGuizmo поверх кадра.
	/// Режимы шейдинга (Lighting/Textured/каналы отладки) и вращение мирового света - те же ручки,
	/// что у превью модели (см. <see cref="ModelPreviewViewport"/>).
	/// </summary>
	public partial class PrefabSceneViewport
	{
		/// <summary>Режим шейдинга кадра - маппится в Mode/Channel кбуфера PreviewSettings
		/// (см. UnlitInstancedPS.hlsl): Lighting = PBR, Textured = плоские текстуры,
		/// Normal/Uv/Tangent = отладочные каналы.</summary>
		public enum ShadingMode
		{
			Lighting,
			Textured,
			Normal,
			Uv,
			Tangent,
			// Mode 3 (Lighting) + Channel 11 - визуализация сэмплинга теней punctual-светов
			// (см. UnlitInstancedPS.hlsl PreviewChannel == 11 и DECA_PROBE_PUNCTUALDEBUG в
			// PreviewProbe.cs): магента - ветка сэмплинга не выполнилась (нет назначенного слайса
			// или точка вне радиуса света), оранжевый - точка приёмника за дальней плоскостью слайса,
			// голубой - точка приёмника вне квадрата UV слайса, градация серого - реальный
			// shadowLit сэмплера (чёрный = в тени/окклюдер найден, белый = свет/не найден).
			PunctualShadowDebug,

			// Кластеризация punctual-светов (LightClusterCS.hlsl) - три вида на ОТДЕЛЬНЫХ пунктах меню,
			// а не через env-переменную: вопрос "почему свет не светит" начинается именно с них, а
			// каналы теней выше о кластерах не говорят ничего. Ожидаемый вид каждого - в легенде
			// ClusterLegend ниже и в комментариях каналов 20/21/14 в UnlitInstancedPS.hlsl.
			ClusterDepthSlices,  // канал 20 - срез глубины фроксела цветом
			ClusterScreenTiles,  // канал 21 - тайл фроксела по экрану
			ClusterLightCount,   // канал 14 - число светов в кластере пикселя

			// Проецируемая глубина света на поверхность - обе глубины, которые сравнивает теневой
			// сэмплер, в мировых единицах вдоль оси слайса (каналы 22..24 в UnlitInstancedPS.hlsl).
			LightDepthReceiver,  // канал 22 - глубина приёмника (этой поверхности) от света
			LightDepthOccluder,  // канал 23 - глубина окклюдера, записанная в слайсе по тому же UV
			LightDepthGap,       // канал 24 - их зазор в единицах применённого байаса

			// Каскадные тени СОЛНЦА (канал 28) - отдельно от punctual-каналов выше: у них общий
			// только сэмплер, а вопросы разные. Тон = номер каскада, яркость = множитель тени.
			SunShadowCascades,
		}

		/// <summary>Легенда кластерных режимов для тултипа меню - здесь, а не в SceneViewWindow, чтобы
		/// текст жил рядом с перечислением каналов, которые он описывает.</summary>
		public static string ClusterLegend(ShadingMode mode) => mode switch
		{
			ShadingMode.ClusterDepthSlices =>
				"Cluster Depth Slices (channel 20) - froxel depth slice, one color per slice.\n" +
				"Expected: bands run with DEPTH, not across the screen - a floor receding from the\n" +
				"camera gets bands across the view direction, packing tighter with distance; a wall\n" +
				"facing the camera is ONE flat color. Color must change as the camera moves forward\n" +
				"and stay put as it rotates in place.\n" +
				"Whole frame one color = depth slices are degenerate; vertical/horizontal screen\n" +
				"bands = screen x/y leaked into the slice. Magenta = grid undefined.",
			ShadingMode.ClusterScreenTiles =>
				"Cluster Screen Tiles (channel 21) - froxel screen tile, checkerboarded.\n" +
				"Expected: an even 16x8 grid over the WHOLE frame (red = tile x, green = tile y).\n" +
				"Fewer cells squeezed into a corner = SV_Position and viewport.zw are in different\n" +
				"resolutions (render scale); grid drifting on resize = camera viewport lags the target.",
			ShadingMode.ClusterLightCount =>
				"Cluster Light Count (channel 14) - lights in this pixel's cluster, raw (before the\n" +
				"32-per-cluster clamp).\n" +
				"black - 0 lights: the cluster is empty\n" +
				"blue -> cyan -> green -> yellow -> red - 1 .. 32 lights, ascending\n" +
				"white - MORE than 32: the cluster overflows and the tail of its lights is dropped\n" +
				"magenta - the cluster branch never ran (the camera has no punctual lights)",
			ShadingMode.LightDepthReceiver or ShadingMode.LightDepthOccluder =>
				"Light Depth - Receiver (ch 22) / Occluder (ch 23): the two depths the shadow sampler\n" +
				"compares, in WORLD units along the slice axis, on a shared ramp\n" +
				"(black at the light -> blue -> cyan -> green -> yellow -> red at the slice far plane).\n" +
				"Receiver = how far THIS surface is from the light. Occluder = what the slice actually\n" +
				"stores at the same UV, i.e. how far the light got in that direction.\n" +
				"Expected: wherever the surface is NOT shadowed, the two views must MATCH - flip\n" +
				"between them and look for differences. A difference means either a real occluder in\n" +
				"front (legit shadow, localized) or the WRONG slice being sampled (mismatch is then\n" +
				"wholesale and patternless). Magenta = shadow sampling never ran here.",
			ShadingMode.LightDepthGap =>
				"Light Depth Gap (channel 24) - (receiver - occluder) measured in units of the bias\n" +
				"actually applied at that pixel, which is the sampler's verdict itself.\n" +
				"green - gap within the bias: the surface is its own occluder, pixel lit (normal)\n" +
				"red   - gap larger than the bias: a real occluder in front, pixel shadowed\n" +
				"blue  - NEGATIVE gap (receiver closer to the light than anything stored): normal only\n" +
				"        where no caster was drawn; a solid blue field means an empty or wrong slice\n" +
				"Brightness is |gap|/bias capped at 4. A thin red rim along contacts over a green field\n" +
				"is the healthy picture; a wide red band trailing an object = bias too large\n" +
				"(peter-panning); ragged red speckle over a lit plane = bias too small (acne).",
			ShadingMode.SunShadowCascades =>
				"Sun Shadow Cascades (channel 28) - the sun's cascaded shadow and WHICH cascade it\n" +
				"came from, in one image. Hue = cascade, brightness = shadow factor.\n" +
				"magenta - no world light (shadows off, or LightDirection is empty)\n" +
				"BLACK   - no cascade was picked at all; the point is declared lit. On geometry that\n" +
				"          should be inside the cascade volumes this IS the gap\n" +
				"red / green / blue / yellow - cascade 0 / 1 / 2 / 3\n" +
				"full hue = lit, darkening toward black = shadowed\n" +
				"\n" +
				"A shadow from a real occluder follows a silhouette and does NOT change hue across\n" +
				"its own edge. A cascade switch IS a hue change - if the darkness starts exactly on\n" +
				"one, the cascade fit is at fault, not the occluder. Acne is fine speckle WITHIN a\n" +
				"single hue, following the shadow map's texel grid.",
			_ =>
				"Punctual Shadow Debug legend:\n" +
				"magenta - shadow sampling branch didn't run (light has no assigned shadow slice,\n" +
				"          or the point is outside the light's radius)\n" +
				"orange  - receiver point is beyond the shadow slice's far plane\n" +
				"cyan    - receiver point is outside the shadow slice's UV square\n" +
				"grey    - actual sampled shadow result (black = shadowed, white = lit)\n" +
				"\n" +
				"DECA_PUNCTUAL_CHANNEL=N switches THIS mode to any other temporary channel\n" +
				"(15 UV excess, 16 slice of cyan pixels, 17 raw UV, 18 toFrag, 19 cube face).",
		};

		/// <summary>Какой именно диагностический канал UnlitInstancedPS показывает режим
		/// <see cref="ShadingMode.PunctualShadowDebug"/>. По умолчанию 11 (сводная легенда сэмплинга),
		/// DECA_PUNCTUAL_CHANNEL=N переключает на любой другой временный канал без правки кода и
		/// пересборки UI: 15 - величина выхода UV за диапазон, 16 - слайс у циан-пикселей, 17 - сырой
		/// UV, 18 - toFrag, 19 - ВЫБРАННАЯ ГРАНЬ КУБА своим цветом. Перебирать их через меню незачем -
		/// они временные и живут ровно до починки punctual-теней.
		///
		/// Отдельно от теней - диагностика КЛАСТЕРИЗАЦИИ (LightClusterCS): 20 - срез глубины
		/// фроксел-сетки цветом (аналог "Display depth Slices" из статьи aortiz, по которой сделан
		/// компьют), 21 - тайл сетки по экрану шахматкой, 14 - число светов в кластере пикселя.
		/// Каналы теней о кластерах не говорят ничего, так что вопрос "почему свет не светит"
		/// начинается с 20/21/14, а не с 11.</summary>
		private static readonly int PunctualDebugChannel =
			// System.Environment полным именем: у класса есть собственное свойство Environment
			// (вьюпортное окружение рендера), и короткое имя разрешается в него.
			int.TryParse(System.Environment.GetEnvironmentVariable("DECA_PUNCTUAL_CHANNEL"), out var ch) && ch > 0
				? ch
				: 11;

		private const uint InitialWidth = 256;
		private const uint InitialHeight = 256;
		private const float CameraFovDegrees = ModelViewportEnvironment.CameraFovDegrees;
		private const float CameraNear = 0.05f;
		private const float CameraFar = 2000f;

		/// <summary>См. ModelPreviewViewport.ResizeSettleSeconds - ресайз таргетов только после того,
		/// как пользователь отпустил край окна.</summary>
		private const float ResizeSettleSeconds = 0.3f;

		/// <summary>Кламп высоты солнца - у горизонта/зенита ортокамера каскада теней вырождается
		/// (см. ModelPreviewViewport.LightElevationMinDegrees).</summary>
		private const float LightElevationMinDegrees = -85f;
		private const float LightElevationMaxDegrees = 85f;

		/// <summary>Отображение одной сущности префаба с ModelRenderer: какие env-сущности созданы
		/// под её инстансы и под какой мировой матрицей они стоят. Resident - ссылка стримера на
		/// модель файла (см. <see cref="ModelStreamer.Acquire"/>); берётся/отпускается по радиусу
		/// стриминга от камеры.</summary>
		private sealed class RenderedModel
		{
			/// <summary>Сущность ПРЕФАБА, которой принадлежит запись (ключ <see cref="_rendered"/>).
			/// Продублирован в саму запись, потому что она передаётся вглубь инстанцирования, где
			/// ключа словаря уже нет, а компоненты анимации висят именно на этой сущности.</summary>
			public int EntityId;

			public string AssetPath = "";
			public string? ResolvedPath;
			public ModelStreamer.Resident? Resident;
			public readonly List<Entity> EnvEntities = new();
			public readonly List<int> InstanceIndices = new();
			public Matrix4x4 LastWorld = Matrix4x4.Identity;
			public bool Instantiated;
		}

		private readonly IGraphicsApi _graphicsApi;
		private readonly EditorSettings _editorSettings;
		private readonly ProjectSession _projectSession;
		private readonly ModelStore _modelStore;
		private readonly SharedViewportResources _sharedResources;
		private ModelViewportEnvironment _env;

		/// <summary>Есть ли у объёмного света каскадные тени - см. одноимённое свойство
		/// <see cref="ModelPreviewViewport.VolumetricShadowsAvailable"/>.</summary>
		public bool VolumetricShadowsAvailable => _env?.VolumetricShadowsAvailable ?? false;

		/// <summary>Текущее окружение сцены - для отладочных инструментов (дамп shadow map каскадов
		/// в окне Graphics). Пересоздаётся при смене env-level настроек - не кэшировать.</summary>
		public ModelViewportEnvironment Environment => _env;

		// Конфигурация, с которой создано ТЕКУЩЕЕ окружение (env-level опции пекутся в его
		// таргеты/пассы/PSO): диф с настройками в OnGraphicsSettingsChanged решает, нужно ли
		// пересоздание (см. RecreateEnvironment - та же схема, что у ModelPreviewViewport).
		private bool _appliedSsao;
		private AmbientOcclusionMode _appliedAoMode;
		private bool _appliedSsgi;
		private bool _appliedSky;
		private string _appliedHdrPath = "";
		private bool _appliedAniso;

		/// <summary>Потолок стороны текстуры, под которым перечитаны резидентные модели. Как и
		/// анизотропия, он печётся при ЗАГРУЗКЕ (текстура ужимается до заливки), поэтому его смена
		/// требует не пересоздания окружения, а перечитывания моделей с диска.</summary>
		private int _appliedMaxTextureSize;

		private bool _appliedSceneHdr;

		// Туман - опция УРОВНЯ СОЗДАНИЯ окружения: пассу нужны депт и scene-copy, поэтому он
		// создаётся вместе с конвейером (см. GraphicsPipelineSimple), а галка требует пересоздания.
		private bool _appliedFog;

		// Объёмный свет - тоже уровня создания окружения: пассу нужны депт, scene-copy и shadow map
		// (см. VolumetricLightPass), он создаётся вместе с конвейером.
		private bool _appliedVolumetric;

		// Блум - тоже уровня создания: он владеет своей цепочкой таргетов (см. BloomPassResources).
		private bool _appliedBloom;

		// Грейдинг - тоже уровня создания: пасс владеет своей копией кадра.
		private bool _appliedColorGrade;

		// Векторы движения - пасс владеет своим RG16F-буфером (см. MotionVectorPassResources).
		private bool _appliedMotionVectors;
		private bool _pendingEnvironmentRecreate;

		/// <summary>Стриминг моделей сцены: резидентный кеш, очередь загрузок с приоритетом по
		/// расстоянию до камеры и выселение простаивающих (см. <see cref="ModelStreamer"/>). Кадровый
		/// шаг делает <see cref="ModelStreamingSystem"/> в SystemRoot окружения.</summary>
		private readonly ModelStreamer _streamer;

		/// <summary>Резидентный кеш стримера на чтение - прежний словарь _models (материалы, probe-GI
		/// и прочие обходы загруженных моделей продолжают работать без изменений).</summary>
		private IReadOnlyDictionary<string, ModelStreamer.Resident> _models => _streamer.Models;

		// --- Активность вьюпорта -------------------------------------------------------------------
		// Модель редактора грузится РОВНО В ОДНОМ месте: либо здесь (открыт префаб), либо в
		// ModelPreviewViewport (Inspector в режиме Model), но никогда в обоих сразу - иначе одна и та
		// же модель держит два набора материалов/инстансов. Переключает EditorManager.OnUpdate по
		// режиму Inspector-а (см. SetActive). В отличие от превью, кадр здесь пишется и в паузе:
		// окно Scene View живёт отдельно от Inspector-а и видно всегда, поэтому на паузе оно
		// показывает пустое небо окружения с подсказкой (см. SceneViewWindow), а не подвисший
		// последний кадр.
		private bool _active = true;

		private readonly Dictionary<int, RenderedModel> _rendered = new();

		/// <summary>Анимация скиннед-моделей сцены: держит позы персонажей и читает их компоненты
		/// (<see cref="ECS.Animator"/> и прочие) каждый кадр. Ключ - сущность ПРЕФАБА: именно на ней
		/// висят компоненты, а не на env-сущностях инстансов.</summary>
		private AnimationDriver? _animation;

		/// <summary>Драйвер создаётся лениво - вместе с первой скиннед-моделью сцены. В сценах без
		/// персонажей его нет вовсе, и кадровый шаг анимации не стоит ничего.</summary>
		private AnimationDriver EnsureAnimation() => _animation ??= new AnimationDriver(_env.BatchRenderer.Skinning);

		// --- Физика сцены и дебаг-вид (см. ScenePhysics / DebugDraw / DebugLineOverlay) ----------

		/// <summary>Мир физики сцены. Заводится ЛЕНИВО - только когда в сцене есть персонаж с
		/// компонентом, которому физика нужна (foot IK или рэгдолл): построение статики - это BVH по
		/// всем треугольникам сцены, и платить за него в сцене без персонажей незачем.</summary>
		private ScenePhysics? _physics;

		/// <summary>Тела персонажей, которых ведут геймплейные скрипты. В отличие от рэгдоллов, их
		/// заводит не анимация, а скрипт, и живут они ровно столько, сколько идёт игра.</summary>
		private readonly CharacterMotionDriver _motion = new();

		/// <summary>Ввод игрока, собранный в <see cref="Render"/> этого кадра. Поле, а не прямая
		/// запись в привод: ввод собирается при отрисовке, а потребляется в PollScenePhysics, и
		/// между ними он должен где-то пережить границу кадра. Потребление ОБНУЛЯЕТ поле - скрытый
		/// вьюпорт перестаёт слать последнее зажатое направление вечно.</summary>
		private PlayerInput _playerInput;

		/// <summary>Идёт ли Play Mode (см. <see cref="InspectorWindow.IsPlaying"/>). Ставится
		/// EditorManager'ом каждый кадр.
		///
		/// ВРЕМЕННАЯ ПРОВОДКА. Сейчас сцену ведут двое: вьюпорт (физика, анимация - всегда) и
		/// Play-Mode-системы инспектора (только по кнопке), и этот флаг - единственное, что их
		/// связывает. Вопрос «кто ведёт сцену» решается отдельно и целиком; до тех пор флаг нужен,
		/// чтобы персонаж под физикой не бегал по сцене, которую в этот момент редактируют.</summary>
		public bool IsPlaying { get; set; }

		/// <summary>Шло ли Play в прошлом кадре - чтобы поймать МОМЕНТ остановки. Состояние, которое
		/// живёт сбоку от ECS, снимком Play Mode не откатывается, и снимать его надо на самом
		/// переходе.</summary>
		private bool _wasPlaying;

		/// <summary>Статику надо пересобрать: сцена изменилась структурно или кто-то поехал. Флаг
		/// свой, а не общий с обводкой выделения: та потребляет свои флаги в тот же кадр, и деление
		/// одного на двоих означало бы, что успевший первым гасит его для второго.</summary>
		private bool _physicsStaticsDirty = true;

		/// <summary>Скретч мировой геометрии под статику физики - тот же приём, что у обводки
		/// выделения: списки переиспользуются, чтобы пересборка не аллоцировала сцену целиком.</summary>
		private readonly List<Vector3> _physicsPositions = new();
		private readonly List<uint> _physicsIndices = new();

		/// <summary>Приёмник дебаг-линий кадра. Живёт всегда (он ничего не стоит выключенным), а
		/// GPU-оверлей под ним - только пока дебаг включён.</summary>
		private readonly DebugDraw _debugDraw = new();
		private DebugLineOverlay? _debugLineOverlay;

		/// <summary>Создание оверлея провалилось (не собрались шейдеры) - больше не пробовать.
		/// Тот же приём, что у дебаг-вида проб: попытка на каждом кадре означала бы поток одинаковых
		/// ошибок в консоли и компиляцию шейдера в каждом кадре.</summary>
		private bool _debugOverlayFailed;

		/// <summary>Кадр сцены упал с исключением - вьюпорт остановлен до перезагрузки префаба.
		/// Иначе устойчивая ошибка означает снос и пересборку сцены на КАЖДОМ кадре: поток
		/// одинаковых сообщений в консоли и мигающий мусор во вьюпорте вместо диагноза.</summary>
		private bool _renderFailed;

		/// <summary>Подробности падения уже напечатаны. Флаг НЕ сбрасывается перезагрузкой префаба:
		/// повторять один и тот же стек по кругу незачем, а перезагрузка при устойчивой ошибке -
		/// самое частое действие пользователя.</summary>
		private bool _renderFailureLogged;

		/// <summary>
		/// Печатает падение кадра сцены ПОСТРОЧНО.
		///
		/// Именно построчно: консоль редактора показывает запись одной строкой без переноса, и
		/// многострочный <c>ex.ToString()</c> обрезается на первом же кадре стека - то есть ровно на
		/// том месте, ради которого его и печатают. Кадры стека уходят отдельными записями и видны
		/// целиком.
		/// </summary>
		private void LogRenderFailure(Exception ex)
		{
			if (_renderFailureLogged)
			{
				return;
			}

			_renderFailureLogged = true;

			EngineLog.Add(LogLevel.Error,
				$"Prefab scene: render failed: {ex.GetType().Name}: {ex.Message}");

			for (var inner = ex; inner != null; inner = inner.InnerException)
			{
				if (!ReferenceEquals(inner, ex))
				{
					EngineLog.Add(LogLevel.Error,
						$"  ---> {inner.GetType().Name}: {inner.Message}");
				}

				// Потолок в 20 кадров: интересна вершина стека, а хвост - это цикл редактора, один
				// и тот же у любого падения.
				var frames = (inner.StackTrace ?? string.Empty)
					.Split('\n', StringSplitOptions.RemoveEmptyEntries);

				for (int i = 0; i < frames.Length && i < 20; i++)
				{
					EngineLog.Add(LogLevel.Error, "  " + frames[i].TrimEnd('\r').Trim());
				}
			}
		}

		private readonly List<AnimationDriver.CharacterInfo> _debugCharacters = new();

		/// <summary>Сводка для окна дебага. Читается ИЗ окна каждый кадр - поэтому это снимок
		/// прошлого кадра, а не живые ссылки на состояние вьюпорта.</summary>
		public IReadOnlyList<AnimationDriver.CharacterInfo> DebugCharacters => _debugCharacters;

		/// <summary>Мир физики сцены для окна дебага; null - физики в сцене нет (см. <see cref="_physics"/>).</summary>
		public ScenePhysics? DebugPhysics => _physics;

		/// <summary>Сколько вершин дебаг-линий ушло на GPU в последнем кадре и уперлись ли в потолок -
		/// окно дебага показывает это честно, чтобы «показано не всё» не выглядело как «больше
		/// ничего нет».</summary>
		public (int Vertices, bool Overflowed) DebugLineStats => (_debugDraw.TotalCount, _debugDraw.Overflowed);

		/// <summary>
		/// Кадровый шаг анимации: читает компоненты сущностей префаба, считает позы и диспетчеризует
		/// GPU-скиннинг. Зовётся ДО исполнения графа - и тени, и forward, и трассировка читают уже
		/// деформированную геометрию.
		/// </summary>
		// Счётчик кадров для покадровой диагностики (DECA_ANIM_DIAG=1).
		private int _animDiagFrame;

		/// <summary>Режим RT-теней запрошен и устройство умеет inline-трассировку (см.
		/// ModelLoadOptions.RtShadows - тумблер уровня загрузки).</summary>
		private bool RtShadowsEnabled() =>
			_editorSettings.ShadowFilterMode == 4 && _graphicsApi.RayTracing >= RayTracingSupport.Inline;
		private readonly HashSet<int> _visitedLightsThisSync = new();

		private EntityStore? _lastStore;
		private string? _currentPrefabPath;

		// Сущности двигались в этом кадре - после Root.Update (он перепишет CPU-массивы инстансов)
		// их нужно перезалить на GPU (см. Update).
		private bool _transformsDirty;

		// --- Probe GI сцены (упрощённый CPU-путь превью, см. ProbeGi.cs / ModelPreviewViewport) --
		// Прогрессивный бейк сетки irradiance-проб по ВСЕЙ сцене (мульти-модельный ProbeGiBaker):
		// включается той же галочкой HDR+GI, что и экспозиция. Изменение сцены (структура/позы)
		// пересоздаёт сессию с дебаунсом; поворот солнца подтягивается в живую сессию без ребейка.
		private ProbeGiBaker? _probeBaker;

		/// <summary>Фоновая сборка BVH сцены (см. BeginProbeSession): десятки секунд чистого CPU на
		/// тяжёлой сцене - на потоке рендера это был полный стопор редактора.</summary>
		private Task<ProbeGiBaker>? _probeBakerTask;

		public PrefabSceneViewport(IGraphicsApi graphicsApi, EditorSettings editorSettings, ProjectSession projectSession,
			ModelStore modelStore, SharedViewportResources sharedResources)
		{
			_graphicsApi = graphicsApi;
			_editorSettings = editorSettings;
			_projectSession = projectSession;
			_modelStore = modelStore;
			_sharedResources = sharedResources;

			_camera = new SceneCamera(_editorSettings.SceneCameraSpeed);

			_env = CreateEnvironment();

			_streamer = new ModelStreamer(_env, _modelStore, _graphicsApi, BuildLoadOptions);
			_streamer.ModelReady += OnStreamedModelReady;
			_streamer.ResidencyResetting += OnStreamerResidencyResetting;
			_streamer.ResidencyReset += OnStreamerResidencyReset;
			_env.Root.Add(new ModelStreamingSystem(_streamer));

			ApplyGraphicsSettings();

			// "OK" окна Settings: live-биты применяются сразу, изменившиеся env-level опции
			// (скай/HDR-карта/анизотропия) - пересозданием окружения в начале следующего
			// Update (см. OnGraphicsSettingsChanged). Вьюпорт один и живёт всю сессию редактора -
			// отписка не требуется.
			SettingsWindow.PreviewGraphicsApplied += OnGraphicsSettingsChanged;
		}

		/// <summary>Создаёт окружение сцены по текущим настройкам и запоминает применённую env-level
		/// конфигурацию (для дифа в <see cref="OnGraphicsSettingsChanged"/>). HDR-конвейер
		/// (авто-экспозиция + тонемап) - по отдельной галочке тулбара Scene View
		/// (<see cref="EditorSettings.SceneViewHdr"/>).</summary>
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
			_appliedSceneHdr = _editorSettings.SceneViewHdr;
			_appliedFog = _editorSettings.PreviewFog;
			_appliedVolumetric = _editorSettings.PreviewVolumetric;
			_appliedBloom = _editorSettings.PreviewBloom;
			_appliedColorGrade = _editorSettings.PreviewColorGrade;
			_appliedMotionVectors = _editorSettings.PreviewMotionVectors;

			// mainCascades: каскады строит ОСНОВНОЙ CullingAndRenderSystem (тот же точный камерный
			// CSM, что в GameView) через солнце-сущность в сторе окружения - Update синхронизирует
			// её с ShadowSettings и пушит дистанции каскадов (см. SyncSunEntity).
			return new ModelViewportEnvironment(_graphicsApi, InitialWidth, InitialHeight,
				"Prefab Scene Color", "Prefab Scene Depth", _sharedResources,
				skyBackground: _appliedSky,
				environmentHdrPath: _appliedHdrPath.Length > 0 ? _appliedHdrPath : null,
				ssao: _appliedSsao,
				shadows: true,
				aoMode: _appliedAoMode,
				ssgi: _appliedSsgi,
				eyeAdaptation: _appliedSceneHdr,
				mainCascades: true,
				fog: _appliedFog,
				bloom: _appliedBloom,
				colorGrade: _appliedColorGrade,
				volumetric: _appliedVolumetric,
				// SSR требует буфера векторов (репроекция истории) - включённые отражения тянут
				// векторы за собой, как TemporalUpscale.
				motionVectors: _appliedMotionVectors || _editorSettings.PreviewSsr,
				temporalUpscale: _appliedMotionVectors && _editorSettings.TemporalUpscale,
				ssr: _editorSettings.PreviewSsr,
				// RT-фолбэк догоняет ApplyPipelineFeatures: TLAS сцены (ProbeSceneAccel) в момент
				// создания окружения ещё не существует, а трейс-материал RT-варианта без него
				// коммитился бы с пустым дескриптором.
				ssrRayTraced: false,
				upscalerBackend: _appliedMotionVectors && _editorSettings.TemporalUpscale
					? Math.Clamp(_editorSettings.UpscalerBackend, 0, 2)
					: 0);
		}

		/// <summary>Синхронизирует солнце-сущность окружения с <see cref="PreviewShadowSettings"/>
		/// (единый источник правды для слайдеров света, неба/IBL и probe-GI): поворот сущности -
		/// чтобы +Z смотрел по направлению света, дистанции каскадов - по диапазону, где реально
		/// лежит геометрия (орбитальная камера может стоять и вплотную, и очень далеко - абсолютные
		/// метры основного пайплайна тут не работают). Звать каждый кадр ДО Root.Update.</summary>
		private void SyncSunEntity()
		{
			var shadowSettings = _env.ShadowSettings;
			var sun = _env.SunEntity;
			if (shadowSettings == null || sun.IsNull)
			{
				return;
			}

			var travel = Vector3.Normalize(shadowSettings.LightDirection);
			var up = MathF.Abs(travel.Y) > 0.95f ? Vector3.UnitX : Vector3.UnitY;
			var view = Matrix4x4.CreateLookAtLeftHanded(Vector3.Zero, travel, up);
			sun.Rotation = new Rotation { value = Quaternion.CreateFromRotationMatrix(Matrix4x4.Transpose(view)) };

			// Угловой размер диска - ширина полутени PCSS (ползунок «Sun angular size» окна Graphics):
			// сущность солнца заводит окружение с нулём, а ноль в шейдере значит «дефолт», и ручка
			// без этой строки не делала бы ничего.
			sun.GetComponent<LightComponent>().SunAngularSize =
				Math.Clamp(_editorSettings.SunAngularSize, 0.05f, 15f);

			if (shadowSettings.BoundsRadius <= 0f)
			{
				return;
			}

			float sceneRadius = shadowSettings.BoundsRadius * 1.15f;
			float distanceToScene = Vector3.Distance(_lastEye, shadowSettings.BoundsCenter);
			float rangeStart = MathF.Max(distanceToScene - sceneRadius, 0.01f);
			float rangeSpan = MathF.Max(distanceToScene + sceneRadius - rangeStart, sceneRadius * 0.1f);

			// Прогрессия ~2.6x (0.38^k) внутри диапазона - ближний к геометрии срез самый плотный.
			ref var cascaded = ref sun.GetComponent<CascadedShadowComponent>();
			var distances = cascaded.CascadeDistances;
			distances[0] = rangeStart;
			distances[1] = rangeStart + rangeSpan * 0.055f;
			distances[2] = rangeStart + rangeSpan * 0.144f;
			distances[3] = rangeStart + rangeSpan * 0.38f;
			distances[4] = rangeStart + rangeSpan;
		}

		/// <summary>Текущее состояние галочки HDR тулбара Scene View.</summary>
		public bool HdrEnabled => _editorSettings.SceneViewHdr;

		/// <summary>Галочка HDR тулбара Scene View: включает авто-экспозицию. Живая - HDR-конвейер в
		/// офскрин-окружении есть всегда, галочка лишь выбирает, экспонировать кадр по замеренной
		/// яркости или по ручной экспокоррекции (см. PipelineFeatures.EyeAdaptation). Раньше требовала
		/// пересоздания окружения: под неё менялся формат цветового таргета, а с ним - PSO геометрии.</summary>
		public void SetHdrEnabled(bool enabled)
		{
			if (_editorSettings.SceneViewHdr == enabled)
			{
				return;
			}

			_editorSettings.SceneViewHdr = enabled;
			ApplyPipelineFeatures();
		}

		/// <summary>Переключает режим шейдинга - пушится в кбуферы материалов всех загруженных
		/// моделей немедленно (см. ApplyMaterialSettings).</summary>
		public void SetShading(ShadingMode shading)
		{
			if (_shading == shading)
			{
				return;
			}

			_shading = shading;
			ApplyMaterialSettings();
		}

		/// <summary>Поворачивает мировой ключевой свет: яв вокруг Y + высота над горизонтом,
		/// смещения от базового положения солнца энвайронмента - зеркало
		/// <see cref="ModelPreviewViewport.SetLightRotation"/>.</summary>
		public void SetLightRotation(float yawOffsetDegrees, float elevationOffsetDegrees)
		{
			_lightYawOffsetDegrees = yawOffsetDegrees;
			_lightElevationOffsetDegrees = elevationOffsetDegrees;
			ApplyLightRotation();
		}

		/// <summary>Кадрирует камеру по баундам текущей сцены; если моделей ещё нет (грузятся) -
		/// откладывает кадрирование до первого появления геометрии.</summary>
		public void FrameAll()
		{
			if (!TryComputeSceneBounds(out var min, out var max))
			{
				_camera.ResetToDefaults();
				_framePending = true;
				return;
			}

			_framePending = false;
			var center = (min + max) * 0.5f;
			var radius = MathF.Max(0.05f, (max - min).Length() * 0.5f);
			_camera.Frame(center, radius, CameraFovDegrees, resetAngle: true);
			RequestMotionVectorHistoryReset();
		}

		/// <summary>Кадрирует камеру на сущность (её баунды + баунды детей): F в Scene View. Направление
		/// взгляда СОХРАНЯЕТСЯ (resetAngle: false) - в отличие от Frame All, это не «дай мне красивый
		/// обзорный ракурс», а «подъедь поближе к тому, на что я уже смотрю». Пусто выделение - тот же
		/// Frame All по всей сцене (см. задачу).</summary>
		public void FrameSelection(Entity? selected)
		{
			if (!selected.HasValue || selected.Value.IsNull)
			{
				FrameAll();
				return;
			}

			if (!TryComputeEntityBounds(selected.Value, out var min, out var max))
			{
				FrameAll();
				return;
			}

			var center = (min + max) * 0.5f;
			var radius = MathF.Max(0.05f, (max - min).Length() * 0.5f);
			_camera.Frame(center, radius, CameraFovDegrees, resetAngle: false);
			RequestMotionVectorHistoryReset();
		}

		/// <summary>Сбрасывает историю векторов движения - камера телепортировалась (F-фокус/Frame All),
		/// и без сброса TAA/апскейлер поймал бы один кадр огромного смещения и размазал бы его (см.
		/// MotionVectorPassResources.ResetHistory). No-op, если векторы движения выключены.</summary>
		private void RequestMotionVectorHistoryReset()
		{
			_env.Pipeline.MotionVectorResources?.ResetHistory();
		}

		/// <summary>Рисует ли вьюпорт сцену прямо сейчас. На паузе (Inspector показывает превью
		/// модели) префаб снят со сцены и в кадре только небо окружения - см. <see cref="SetActive"/>.</summary>
		public bool IsActive => _active;

		/// <summary>
		/// Включает/ставит на паузу сцену префаба. Зовёт EditorManager.OnUpdate по режиму Inspector-а,
		/// чтобы модель редактора была загружена ровно в одном месте - здесь ИЛИ в
		/// <see cref="ModelPreviewViewport"/>. Само снятие сцены с GPU делает ближайший
		/// <see cref="Update"/> (там мы под GPU-локом редактора), ровно тем же путём, что и закрытие
		/// префаба.
		/// </summary>
		public void SetActive(bool active)
		{
			_active = active;
		}

		/// <summary>
		/// Покадровый привод: синхронизирует env-сцену с префабом (загрузки по AssetRef, трансформы,
		/// удаления), двигает камеру и исполняет офскрин-конвейер. Звать из EditorManager.OnUpdate
		/// ДО рендера основной сцены, под тем же GPU-локом (см. порядок у ModelPreviewViewport).
		/// </summary>
		public void Update(float deltaTime, float time, Entity? root, string? prefabPath, Entity? selected = null)
		{
			_currentPrefabPath = prefabPath;

			// Пауза (Inspector показывает превью модели) неотличима здесь от закрытого префаба: сцена
			// и её резидентные модели снимаются тем же ClearScene ниже, кадр пишется пустым. При
			// возврате в активное состояние _lastStore уже сброшен - SyncScene инстанцирует сцену
			// заново и стример перезапросит модели.

			// Заявка на пересоздание окружения (смена env-level настроек графики/галочки HDR) -
			// исполняется здесь, до записи кадра: старые биндинги ещё нигде не задействованы.
			if (_pendingEnvironmentRecreate)
			{
				_pendingEnvironmentRecreate = false;
				RecreateEnvironment();
			}

			// После возможного пересоздания окружения и ДО записи кадра - безопасная точка для
			// смены бэкенда апскейлера (GPU-барьер + init-команды NGX), см. ModelPreviewViewport.
			ApplyPendingUpscalerSettings();

			bool hasRoot = _active && root.HasValue && !root.Value.IsNull;
			if (!hasRoot)
			{
				// Префаб закрыт: снимаем сцену И резидентные модели с загрузками - стримеру без
				// Root.Update некому шагать, брошенные фоновые задачи иначе повисли бы навсегда.
				if (_rendered.Count > 0 || _models.Count > 0)
				{
					ClearScene();
				}
				_lastStore = null;

				// Кадр НЕ прерывается здесь (в отличие от прежней версии): вьюпорт обязан показывать
				// лит-небо окружения ДО того, как открыт хоть один префаб (см. задачу про мгновенный
				// пустой вьюпорт) - см. общий Execute ниже, тот же приём, что и с открытым, но пустым
				// префабом.
			}
			else
			{
				// Reload/смена префаба пересоздаёт EntityStore - все закешированные entity id мертвы.
				var store = root.Value.Store;
				if (!ReferenceEquals(store, _lastStore))
				{
					ClearScene();
					_lastStore = store;
					_framePending = true;

					// Перезагрузка префаба - это и есть «попробовать ещё раз»: сцена собирается с
					// нуля, и держать вьюпорт остановленным после неё незачем.
					_renderFailed = false;
				}

				// Устойчивая ошибка отрисовки останавливает вьюпорт до перезагрузки префаба (см.
				// catch ниже): иначе сцена рушится и собирается заново каждый кадр.
				if (_renderFailed)
				{
					return;
				}

				// Загрузки опрашивает ModelStreamingSystem внутри _env.Root.Update ниже (приоритет по
				// камере, финализация порциями); догрузившаяся модель инстанцируется СЛЕДУЮЩИМ SyncScene.
				SyncScene(root.Value);
				SyncSelectionHighlight(selected);
				PollProbeBake(deltaTime);
				PollSceneProbeDebugOverlay();
			}

			// Собственный accel SSR - и при закрытом префабе тоже (там он освобождается вместе с
			// опустевшей сценой).
			PollSsrOwnRayScene(deltaTime);

			try
			{
				_lastEye = _camera.Eye;
				_env.SetCameraTransform(_camera.Eye, _camera.Target);

				// Солнце-сущность (направление света + дистанции каскадов) - ДО Root.Update, где
				// CullingAndRenderSystem раскладывает каскады.
				SyncSunEntity();

				// Шаг времени временной адаптации экспозиции - каждый кадр, до записи (no-op без
				// HDR). Кламп сверху - защита от «прыжка» после долгих пауз редактора.
				_env.SetEyeAdaptationDeltaTime(Math.Min(deltaTime, 0.1f));

				// Анимация - строго ДО Root.Update: внутри него CullingAndRenderSystem ЗАПИСЫВАЕТ
				// команды кадра, а скиннинг может дозалить (и на пути роста - пересоздать)
				// мега-буфер вершин. Вызов после записи освобождал бы буфер под уже записанными
				// командами - ровно это роняло кадр в DrawIndexedIndirect при появлении скиннед-
				// модели в сцене (см. DiligentBatchRenderer.ExecuteSkinning).
				// Кадр дебага открывается ДО первой стадии, которая в него пишет: список линий - это
				// кадр целиком, а не накопитель.
				BeginDebugFrame();

				// Физика - ДО анимации: луч foot IK обязан щупать мир в том состоянии, в котором
				// кадр будет нарисован, а рэгдолл читает позу из уже проинтегрированных тел.
				PollScenePhysics(deltaTime);

				// Анимация тоже идёт ТОЛЬКО в Play, и остановка сделана НУЛЕВЫМ ШАГОМ, а не пропуском
				// вызова. Разница существенная: с нулевым шагом поза считается целиком (клип
				// семплируется, foot IK и spring bones применяются) - просто время не двигается.
				// Пропустив вызов, мы оставили бы персонажа с палитрой прошлого кадра, а свежую
				// скиннед-модель - вовсе без неё, то есть схлопнутой в точку.
				UpdateAnimation(IsPlaying ? deltaTime : 0f);

				// Сброс на выходе из Play: время клипа и состояние цикла падения лежат в компонентах и
				// откатываются снимком (см. InspectorWindow.Stop), а переход позы при подъёме живёт
				// сбоку, в драйвере, и его надо снять руками - иначе персонаж остался бы навсегда в
				// промежуточной позе того подъёма, на котором нажали Stop.
				if (_wasPlaying && !IsPlaying)
				{
					_animation?.EndPlay();
				}

				_wasPlaying = IsPlaying;

				_env.Root.Update(new UpdateTick(deltaTime, time));

				// Замороженный граф сам инстанс-буферы не перезальёт: их аплоад живёт в
				// CheckAndReallocateBuffers, который вызывается только из записи команд
				// (ForwardPass.WriteCommands). GpuInstanceBufferSystem выше уже переписал CPU-массивы
				// под новые позы - перезаливаем на GPU напрямую. Пересборка графа не нужна: движение
				// не меняет ни числа инстансов, ни ёмкостей буферов, так что замороженные команды
				// продолжают ссылаться на те же (перезалитые) буферы.
				if (_transformsDirty)
				{
					_transformsDirty = false;
					_env.BatchRenderer.MarkInstancesContentDirty();
					_env.BatchRenderer.CheckAndReallocateBuffers();
				}

				// Дебаг-линии дорисовываются и заливаются ПОСЛЕ всех, кто в них пишет, и до графа:
				// заливка идёт немедленным контекстом, а сам дроу - командой внутри ForwardPass.
				EndDebugFrame();

				// Кадр исполняется ВСЕГДА, даже без открытого префаба (см. hasRoot выше), - пустая
				// сцена показывает небо окружения (батч-рендерер безопасен при нуле инстансов:
				// ExecuteComputeCulling/ExecuteDrawBatching выходят сразу), и назначенная сетка просто
				// появляется в ней, как только пользователь открывает префаб.
				_env.Pipeline.Execute();
			}
			catch (Exception ex)
			{
				// См. комментарий в ModelPreviewViewport.Update: исключение отсюда сорвало бы Present
				// всего редактора - теряем только кадр этого вьюпорта.
				//
				LogRenderFailure(ex);

				// Вьюпорт останавливается до перезагрузки префаба: сцена сносится и собирается
				// заново на каждом кадре, поэтому устойчивая ошибка превращалась в поток одинаковых
				// строк и в мигающий мусор во вьюпорте. Остановленная сцена честнее.
				_renderFailed = true;
				ClearScene();
			}
		}

		/// <summary>
		/// Рисует кадр сцены как ImGui.Image, обрабатывает ввод камеры (см. SceneCamera) и гизмо
		/// выделенной сущности поверх кадра. Возвращает true, если трансформ выделенной сущности
		/// изменён гизмо.
		/// </summary>
		public bool Render(ImGuiRender imGuiRender, Entity root, Entity? selected, Vector2 size, out PickResult pick)
		{
			_lastImGuiRender = imGuiRender;
			pick = default;

			if (size.X <= 1f || size.Y <= 1f)
			{
				return false;
			}

			if (!_textureBound)
			{
				_textureRef = imGuiRender.GetNewTexture();
				imGuiRender.BindRenderTarget(_textureRef.GetTexID(), _env.ColorTarget);
				_textureBound = true;
			}

			if (TrackAndApplyResize(imGuiRender, size))
			{
				imGuiRender.BindRenderTarget(_textureRef.GetTexID(), _env.ColorTarget);
			}

			var cursor = ImGui.GetCursorScreenPos();

			// ВАЖНО: место в layout резервирует Dummy, а картинка рисуется напрямую в drawlist -
			// НЕ ImGui.Image. Image регистрируется как hoverable item и «отравляет» hover, из-за
			// чего ImGuizmo (его CanActivate требует !IsAnyItemHovered() && !IsAnyItemActive())
			// считает себя перекрытым: гизмо рисуется, но на драг не реагирует. Тот же приём был
			// в прежней версии этого вьюпорта.
			ImGui.Dummy(size);
			bool hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows)
				&& ImGui.IsMouseHoveringRect(cursor, cursor + size);

			// Нейтральный градиент-подложка (см. ModelPreviewViewport.Render): офскрин-таргет
			// очищается с alpha 0, ImGui-блендинг кладёт геометрию поверх этого прямоугольника.
			var drawList = ImGui.GetWindowDrawList();
			uint backdropTop = ImGui.GetColorU32(new Vector4(0.55f, 0.55f, 0.55f, 1f));
			uint backdropBottom = ImGui.GetColorU32(new Vector4(0.26f, 0.26f, 0.26f, 1f));
			drawList.AddRectFilledMultiColor(cursor, cursor + size, backdropTop, backdropTop, backdropBottom, backdropBottom);
			drawList.AddImage(_textureRef, cursor, cursor + size);

			_camera.HandleInput(hovered, ImGui.GetIO().DeltaTime);

			// Колесо при зажатой RMB (см. SceneCamera.HandleInput) меняет базовую скорость полёта -
			// персистим в EditorSettings ТОЛЬКО когда она реально изменилась, а не каждый кадр: запись
			// в EditorSettings тут в памяти дёшева, но незачем дёргать её без нужды на каждый Render.
			if (_camera.FlySpeed != _editorSettings.SceneCameraSpeed)
			{
				_editorSettings.SceneCameraSpeed = _camera.FlySpeed;
			}

			// F - кадрирование на выделение (см. FrameSelection); не ворует хоткей у текстовых полей
			// (см. задачу) и молчит, пока курсор не над вьюпортом - иначе F, набранный в любом текстовом
			// поле редактора, дёргал бы камеру сцены просто потому, что окно Scene View где-то открыто.
			if (hovered && !ImGui.GetIO().WantTextInput && ImGui.IsKeyPressed(ImGuiKey.F))
			{
				FrameSelection(selected);
			}

			// Управление персонажем игрока (см. PlayerMoveComponent): WASD/стрелки в Play, Shift - бег.
			// Правила те же, что у F: курсор над вьюпортом и текст не занят. ПКМ отдаёт WASD полёту
			// камеры (SceneCamera) - у него приоритет. Направление переводится в мир ЗДЕСЬ: «W - от
			// камеры» знает только тот, у кого есть камера, приводу приходит уже мировой вектор.
			if (IsPlaying && hovered && !ImGui.GetIO().WantTextInput &&
				!ImGui.IsMouseDown(ImGuiMouseButton.Right))
			{
				var forward = _camera.Forward;
				forward.Y = 0f;

				// Камера, глядящая отвесно вниз, «от камеры» не определяет - берётся мировой +Z,
				// чтобы управление не отключалось молча (произвольная, но стабильная привязка).
				forward = forward.LengthSquared() > 1e-6f ? Vector3.Normalize(forward) : Vector3.UnitZ;
				var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));

				var move = Vector3.Zero;
				if (ImGui.IsKeyDown(ImGuiKey.W) || ImGui.IsKeyDown(ImGuiKey.UpArrow)) move += forward;
				if (ImGui.IsKeyDown(ImGuiKey.S) || ImGui.IsKeyDown(ImGuiKey.DownArrow)) move -= forward;
				if (ImGui.IsKeyDown(ImGuiKey.D) || ImGui.IsKeyDown(ImGuiKey.RightArrow)) move += right;
				if (ImGui.IsKeyDown(ImGuiKey.A) || ImGui.IsKeyDown(ImGuiKey.LeftArrow)) move -= right;

				_playerInput = new PlayerInput
				{
					MoveWorld = move.LengthSquared() > 0f ? Vector3.Normalize(move) : Vector3.Zero,
					Run = ImGui.IsKeyDown(ImGuiKey.LeftShift) || ImGui.IsKeyDown(ImGuiKey.RightShift),
					// Именно IsKeyPressed: фронт, а не удержание - зажатый Space не автоскок.
					Jump = ImGui.IsKeyPressed(ImGuiKey.Space, false),
				};
			}

			// Сцена (небо окружения) рендерится и без единого объекта - текст поверх только про
			// реальные события: идущие загрузки или ошибки.
			var status = CollectStatusText();
			if (status != null)
			{
				var textSize = ImGui.CalcTextSize(status);
				drawList.AddText(cursor + (size - textSize) * 0.5f, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.6f)), status);
			}

			// Подписи дебага (имена костей) - до гизмо: гизмо должно рисоваться ПОВЕРХ текста, иначе
			// подпись кости перекрывает ручку, за которую тянут.
			DrawDebugLabels(drawList, cursor, size);

			bool gizmoChanged = RenderGizmo(drawList, cursor, size, selected);

			// Пикинг ЛКМ - после гизмо, когда его состояние за этот кадр актуально: клик по самому
			// гизмо (или во время манипуляции) выделение не трогает. Alt+ЛКМ - орбита камеры (см.
			// SceneCamera.HandleInput), не пикинг: без этой проверки орбита кликом по объекту ещё и
			// меняла бы выделение.
			bool gizmoBusy = selected.HasValue && !selected.Value.IsNull &&
				(ImGuizmo.IsUsing() || ImGuizmo.IsOver());
			bool altDown = ImGui.IsKeyDown(ImGuiKey.LeftAlt);
			if (hovered && !altDown && !gizmoBusy && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
			{
				pick = new PickResult
				{
					Clicked = true,
					Entity = Pick(cursor, size, ImGui.GetMousePos()),
				};
			}

			return gizmoChanged;
		}

		/// <summary>
		/// Экранные подписи дебага (имена костей, см. <see cref="DebugDraw.Label"/>) поверх картинки
		/// вьюпорта. Текст рисует ImGui, а не оверлей линий: у движка нет ни шрифта, ни разметки
		/// текста в 3D, а имена костей нужны читаемыми - то есть постоянного размера на экране и не
		/// перекошенными перспективой.
		///
		/// Проекция берётся ТЕМИ ЖЕ view/proj, под которыми камера рендерила кадр (см.
		/// <see cref="RenderGizmo"/>), иначе подпись поедет относительно кости на краях кадра, где
		/// расхождение проекций как раз и заметно.
		/// </summary>
		private void DrawDebugLabels(ImDrawListPtr drawList, Vector2 cursor, Vector2 size)
		{
			var labels = _debugDraw.Labels;
			if (labels.Count == 0 || size.X < 1f || size.Y < 1f)
			{
				return;
			}

			var view = Matrix4x4.CreateLookAtLeftHanded(_lastEye, _camera.Target, Vector3.UnitY);
			var projection = Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(
				CameraFovDegrees * (MathF.PI / 180f), size.X / size.Y, CameraNear, CameraFar);

			var viewProjection = view * projection;

			for (int i = 0; i < labels.Count; i++)
			{
				var label = labels[i];
				var clip = Vector4.Transform(new Vector4(label.Position, 1f), viewProjection);

				// За камерой и ровно в её плоскости - деление на w дало бы «отражённую» точку, то
				// есть подпись кости, которая за спиной, приехала бы в кадр.
				if (clip.W <= 1e-4f)
				{
					continue;
				}

				var ndc = new Vector2(clip.X / clip.W, clip.Y / clip.W);
				if (ndc.X < -1.2f || ndc.X > 1.2f || ndc.Y < -1.2f || ndc.Y > 1.2f)
				{
					continue;
				}

				var screen = cursor + new Vector2(
					(ndc.X * 0.5f + 0.5f) * size.X,
					(0.5f - ndc.Y * 0.5f) * size.Y);

				// Тень на пиксель ниже-правее: подписи ложатся и на светлую, и на тёмную геометрию, и
				// без неё половина имён нечитаема ровно там, где на них смотрят.
				drawList.AddText(screen + new Vector2(1f, 1f), ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.75f)),
					label.Text);
				drawList.AddText(screen, ImGui.GetColorU32(label.Color), label.Text);
			}
		}

		/// <summary>Гизмо манипуляции выделенной сущностью - теми же view/proj, под которыми камера
		/// рендерила кадр (левосторонний lookAt + LH-перспектива без reversed-Z: экранная проекция
		/// та же, что у MakePerspectiveReversedZ, а глубина ImGuizmo нужна лишь монотонной).</summary>
		private bool RenderGizmo(ImDrawListPtr drawList, Vector2 cursor, Vector2 size, Entity? selected)
		{
			if (!selected.HasValue || selected.Value.IsNull)
			{
				return false;
			}

			var view = Matrix4x4.CreateLookAtLeftHanded(_lastEye, _camera.Target, Vector3.UnitY);
			var proj = Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(
				CameraFovDegrees * (MathF.PI / 180f), size.X / size.Y, CameraNear, CameraFar);

			ImGuizmo.SetImGuiContext(ImGui.GetCurrentContext());
			ImGuizmo.SetOrthographic(false);
			ImGuizmo.BeginFrame();
			ImGuizmo.SetDrawlist(drawList);
			ImGuizmo.SetRect(cursor.X, cursor.Y, size.X, size.Y);

			var world = ComputeWorldMatrix(selected.Value);
			if (!ImGuizmo.Manipulate(ref view, ref proj, Operation, ImGuizmoMode.Local, ref world))
			{
				return false;
			}

			ApplyWorldMatrix(selected.Value, world);
			return true;
		}

		private string? CollectStatusText()
		{
			int loading = 0;
			int failed = 0;
			foreach (var state in _models.Values)
			{
				if (state.Failed)
				{
					failed++;
				}
				else if (!state.Ready)
				{
					loading++;
				}
			}

			if (loading > 0)
			{
				return $"Loading models... ({loading})";
			}
			if (failed > 0)
			{
				return $"Model load failed ({failed}) - see Console";
			}
			return null;
		}

		// --- Синхронизация префаб -> env-сцена ------------------------------------------------------

		private static string? ResolveEnvironmentHdrPath(string configured)
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

		// --- Камера/ресайз --------------------------------------------------------------------------
		// Ввод камеры - см. SceneCamera.HandleInput (вызывается из Render); здесь остался только ресайз.

		/// <summary>Дебаунс ресайза таргетов - см. ModelPreviewViewport.TrackAndApplyResize.</summary>
		private bool TrackAndApplyResize(ImGuiRender imGuiRender, Vector2 imGuiSize)
		{
			var width = (uint)Math.Max(1, MathF.Round(imGuiSize.X));
			var height = (uint)Math.Max(1, MathF.Round(imGuiSize.Y));
			var requestedSize = new Vector2(width, height);

			// Масштаб рендера применяется ЗДЕСЬ, а не из применения настроек - см.
			// ModelPreviewViewport.TrackAndApplyResize (единственная точка кадра, где ресайз
			// таргетов безопасен; настройка перечитывается каждый кадр и чинит расхождение сама).
			var scale = Math.Clamp(_editorSettings.RenderScale, 0.25f, 1f);
			_env.SetRenderScale(scale);

			if (requestedSize != _pendingSize || scale != _pendingRenderScale)
			{
				_pendingSize = requestedSize;
				_pendingRenderScale = scale;
				_resizeIdleSeconds = 0f;
				return false;
			}

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

		/// <summary>Ресайз офскрин-таргетов - тот же порядок барьеров/перепривязок, что у
		/// ModelPreviewViewport.ResizeTargets.</summary>
		private bool ResizeTargets(ImGuiRender imGuiRender, Vector2 newSize)
		{
			var width = (uint)newSize.X;
			var height = (uint)newSize.Y;

			_env.DilApi.ImmediateContext.Flush();
			_env.DilApi.ImmediateContext.WaitForIdle();

			imGuiRender.ReleaseRenderTargetBinding(_textureRef.GetTexID());

			// Сценовые таргеты - в РЕНДЕР-размере, ColorTarget всегда display - см.
			// ModelPreviewViewport.ResizeTargets (тот же порядок и те же причины).
			var sceneSize = _env.Pipeline.SceneSizeFor(newSize);

			_env.ColorTarget.Resize(newSize);
			_env.DepthTarget.Resize(sceneSize);
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

			// Оверлей выделения рисует в display-кадр ПОСЛЕ тонемапа - его таргеты остаются
			// отображаемого размера.
			_selectionOverlay?.Resize(newSize);

			// Снимок сцены после Resize - другая нативная текстура: transmissive-материалам всех
			// загруженных моделей нужно перепривязать _SceneColor (см. RegisterModelResources).
			foreach (var state in _models.Values)
			{
				if (state.Model == null)
				{
					continue;
				}
				foreach (var material in state.Model.materialObjects.Values)
				{
					material.SetTexture("_SceneColor", _env.SceneCopyTarget);
				}
			}

			_env.Pipeline.SetOffscreenViewportSize(newSize);

			ref var cameraComponent = ref _env.CameraEntity.GetComponent<CameraComponent>();
			cameraComponent.data.viewport = new Vector4(0, 0, width, height);
			cameraComponent.data.aspect = width / (float)height;
			cameraComponent.RecalculateProjection();

			return true;
		}

		// --- TRS-иерархия префаба -------------------------------------------------------------------

		public static Matrix4x4 ComputeWorldMatrix(Entity entity)
		{
			var local = LocalMatrix(entity);
			var parent = entity.Parent;
			return parent.IsNull ? local : local * ComputeWorldMatrix(parent);
		}

		/// <summary>Матрица РОДИТЕЛЬСКОГО пространства сущности, то есть то, чем нужно домножить её
		/// Position/Rotation, чтобы получить мировые. Нужна всем, кто держит состояние сущности в
		/// мире, - физике персонажа (см. <see cref="CharacterMotionDriver"/>) прежде всего.</summary>
		internal static Matrix4x4 ParentToWorldMatrix(Entity entity)
		{
			var parent = entity.Parent;
			return parent.IsNull ? Matrix4x4.Identity : ComputeWorldMatrix(parent);
		}

		private static Matrix4x4 LocalMatrix(Entity entity)
		{
			Vector3 pos = entity.HasPosition ? entity.Position.value : Vector3.Zero;
			Quaternion rot = entity.HasRotation ? entity.Rotation.value : Quaternion.Identity;
			Vector3 scale = entity.HasScale3 ? entity.Scale3.value : Vector3.One;
			return MathUtils.CreateTrs(pos, rot, scale);
		}

		private static void ApplyWorldMatrix(Entity entity, Matrix4x4 world)
		{
			var parent = entity.Parent;
			var local = world;
			if (!parent.IsNull)
			{
				var parentWorld = ComputeWorldMatrix(parent);
				if (Matrix4x4.Invert(parentWorld, out var parentInv))
				{
					local = world * parentInv;
				}
			}

			if (!Matrix4x4.Decompose(local, out var scale, out var rotation, out var translation))
			{
				return;
			}

			if (!entity.HasPosition)
			{
				entity.AddComponent<Position>();
			}
			entity.Position = new Position(translation.X, translation.Y, translation.Z);

			if (!entity.HasRotation)
			{
				entity.AddComponent<Rotation>();
			}
			entity.Rotation = new Rotation { value = rotation };

			if (!entity.HasScale3)
			{
				entity.AddComponent<Scale3>();
			}
			entity.Scale3 = new Scale3(scale.X, scale.Y, scale.Z);
		}
	}
}
