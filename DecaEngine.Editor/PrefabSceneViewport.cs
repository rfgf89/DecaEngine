using System;
using System.Collections.Generic;
using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Core.Diagnostics;
using DecaEngine.Core.Assets;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Core;
using DecaEngine.Graphics.Diligent;
using DecaEngine.Graphics.ProbeGi;
using Engine.ImGui.Core;
using System.Threading.Tasks;
using Friflo.Engine.ECS;
using Hexa.NET.ImGui;
using Hexa.NET.ImGuizmo;
using UnsafeCollections.Collections.Unsafe;

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
	public class PrefabSceneViewport
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

		private void UpdateAnimation(float deltaSeconds)
		{
			// Диагностика идёт ДО раннего выхода: если счётчики растут и без персонажей, значит
			// источник роста вообще не в анимации, и это тоже ответ.
			// System.Environment полным именем: у вьюпорта есть своё свойство Environment (окружение
			// сцены), и короткое имя разрешается в него.
			if (System.Environment.GetEnvironmentVariable("DECA_ANIM_DIAG") == "1" && (_animDiagFrame++ % 10) == 0)
			{
				Console.WriteLine($"[animdiag] кадр {_animDiagFrame}: " +
					$"{((DiligentBatchRenderer)_env.BatchRenderer).DiagCounters}, " +
					$"записей={_rendered.Count}, персонажей={_animation?.CharacterCount ?? 0}");
			}

			if (_animation == null || _animation.CharacterCount == 0)
			{
				return;
			}

			// Хранилище префаба, а не окружения: компоненты анимации автор ставит на сущность
			// префаба, рядом с ModelRenderer. Store пересоздаётся при перезагрузке префаба, поэтому
			// он берётся из _lastStore, а не кешируется драйвером.
			var store = _lastStore;
			if (store == null)
			{
				return;
			}

			// Проводка драйвера к физике и дебагу идёт КАЖДЫЙ кадр: и то, и другое включается
			// галочками на живой сцене, а драйвер мог быть создан задолго до этого.
			_animation.Physics = _physics;
			_animation.Debug = _debugDraw;
			_animation.DebugOptions = _editorSettings.AnimationDebug;
			_animation.HighlightJoint = HighlightJoint;
			_animation.BeginFrame();

			foreach (var record in _rendered.Values)
			{
				if (!store.TryGetEntityById(record.EntityId, out var entity))
				{
					continue;
				}

				// Humanoid-разметка модели - каждый кадр, потому что её правят в окне Humanoid на
				// живой сцене. Сравнение по ссылке внутри SetAvatar делает повторный вызов
				// бесплатным, а кеш по пути модели - и загрузку тоже.
				if (!string.IsNullOrEmpty(record.ResolvedPath) &&
					_models.TryGetValue(record.ResolvedPath, out var avatarState) &&
					avatarState.Model?.Skeleton != null)
				{
					_animation.SetAvatar(record.EntityId,
						AvatarFor(record.ResolvedPath, avatarState.Model.Skeleton));
				}

				// Мировой трансформ сущности: поза считается в пространстве МОДЕЛИ, а физика живёт в
				// мире, и перевод между ними нужен и лучу foot IK, и рэгдоллу.
				_animation.Update(entity, record.LastWorld, deltaSeconds);
			}

			_env.BatchRenderer.ExecuteSkinning();
		}

		// --- Humanoid-разметка (см. HumanoidAvatar) -----------------------------------------------

		/// <summary>Аватары по пути модели. Кеш по ПУТИ, а не по сущности: одна модель встречается в
		/// сцене много раз, а разметка у неё одна - она свойство рига, а не персонажа.</summary>
		private readonly Dictionary<string, HumanoidAvatar> _avatars = new();

		/// <summary>Пути моделей, у которых разметка получена автоматом, а не прочитана из файла.
		/// Различать обязательно: сохранённая разметка - решение человека, автоматическая - догадка,
		/// и окно Humanoid показывает это прямым текстом.</summary>
		private readonly HashSet<string> _autoAvatars = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// Аватар модели: из файла рядом с ней, а если файла нет - автоматический.
		///
		/// Автоматический строится молча намеренно: без него foot IK и рэгдолл требовали бы ручной
		/// разметки ДО первого запуска, то есть не работали бы «из коробки» ни на одной модели. Цена
		/// известна и ограничена - авторские поля компонентов всё равно старше разметки, а сама
		/// разметка видна и правится в окне Humanoid.
		/// </summary>
		private HumanoidAvatar AvatarFor(string modelPath, PreparedSkeleton skeleton)
		{
			if (_avatars.TryGetValue(modelPath, out var cached))
			{
				return cached;
			}

			var avatar = HumanoidAvatarAsset.Load(modelPath);

			if (avatar == null)
			{
				avatar = HumanoidAutoMap.Build(skeleton);
				_autoAvatars.Add(modelPath);
			}
			else
			{
				_autoAvatars.Remove(modelPath);
			}

			_avatars[modelPath] = avatar;
			return avatar;
		}

		/// <summary>Автоматическая ли разметка у этой модели - для окна Humanoid.</summary>
		public bool IsAvatarAuto(string modelPath) => _autoAvatars.Contains(modelPath);

		/// <summary>Забывает разметку модели: следующий кадр перечитает её с диска. Звать после
		/// сохранения аватара из окна Humanoid - иначе персонажи сцены продолжат жить по разметке,
		/// которую только что заменили.</summary>
		public void InvalidateAvatar(string modelPath)
		{
			if (!string.IsNullOrEmpty(modelPath))
			{
				_avatars.Remove(modelPath);
				_autoAvatars.Remove(modelPath);
			}
		}

		/// <summary>Скиннед-модель выделенной сущности - вход для окна Humanoid. Скелет и путь
		/// приходят вместе: путь нужен, чтобы положить аватар рядом с моделью, а скелет - чтобы было
		/// что размечать.</summary>
		public (PreparedSkeleton? Skeleton, string? ModelPath, string Name) SelectedSkinnedModel
		{
			get
			{
				if (_highlightedId < 0 || !_rendered.TryGetValue(_highlightedId, out var record) ||
					string.IsNullOrEmpty(record.ResolvedPath) ||
					!_models.TryGetValue(record.ResolvedPath, out var state) ||
					state.Model?.Skeleton == null)
				{
					return (null, null, string.Empty);
				}

				return (state.Model.Skeleton, record.ResolvedPath,
					System.IO.Path.GetFileName(record.ResolvedPath));
			}
		}

		/// <summary>Кость, подсвеченная окном Humanoid. Пусто - подсветки нет.</summary>
		public string HighlightJoint { get; set; } = string.Empty;

		// --- Физика сцены (см. ScenePhysics) ------------------------------------------------------

		/// <summary>
		/// Кадровый шаг физики: заводит или убирает мир по надобности, догоняет статику и двигает
		/// симуляцию. Идёт ДО анимации - и это не вкусовщина: луч foot IK обязан щупать мир в том
		/// состоянии, в котором он будет нарисован, а рэгдолл в этом же кадре читает позу из тел,
		/// проинтегрированных ЗДЕСЬ, и тут же задаёт им новую цель.
		/// </summary>
		private void PollScenePhysics(float deltaSeconds)
		{
			if (!ScenePhysicsWanted())
			{
				if (_physics != null)
				{
					// Драйвер держит рэгдоллы ЭТОГО мира - без их сноса они остались бы хендлами в
					// уничтоженной симуляции. Отвязка, а не Clear: персонажи со своими палитрами
					// скиннинга обязаны пережить выключение физики (см. AnimationDriver.DetachPhysics).
					_animation?.DetachPhysics();
					_motion.Clear(_physics);
					_physics.Dispose();
					_physics = null;
				}

				return;
			}

			if (_physics == null)
			{
				_physics = new ScenePhysics(new Vector3(0f, _editorSettings.SceneGravity, 0f));
				_physicsStaticsDirty = true;
			}

			// Симуляция идёт ТОЛЬКО в Play. Мир при этом заводится и статику держит всегда - построение
		// BVH по всей геометрии сцены в момент нажатия кнопки отдало бы первый кадр игры под
		// подвисание, - но шагов не делает: в режиме редактирования сцена обязана стоять там, где её
		// поставил автор. Рэгдолл, разъезжающийся, пока автор двигает объекты, - это не «живая
		// сцена», а невозможность её собрать.
		//
		// Ручная пауза остаётся сверху: она останавливает и то, что идёт по Play.
		_physics.Paused = _editorSettings.ScenePhysicsPaused || !IsPlaying;
			_physics.TimeScale = _editorSettings.ScenePhysicsTimeScale;
			_physics.RecordRays = _editorSettings.PhysicsDebug.Rays;

			bool recordContacts = _editorSettings.PhysicsDebug.NeedsContactRecording;
			if (_physics.World.Contacts.Enabled != recordContacts)
			{
				_physics.World.Contacts.Enabled = recordContacts;

				// Выключение обязано и ОЧИСТИТЬ: иначе на экране навсегда остался бы снимок шага,
				// на котором галочка ещё была включена, и он выглядел бы как живые контакты.
				if (!recordContacts)
				{
					_physics.World.Contacts.Clear();
				}
			}

			if (_physicsStaticsDirty)
			{
				_physicsStaticsDirty = false;
				RebuildPhysicsStatics();
			}

			// Скорость задаётся ДО шага, поза читается ПОСЛЕ. Слить их в один вызов нельзя: скорость,
			// заданная после шага, применится только к следующему (персонаж отстаёт от собственной
			// команды на кадр), а поза, прочитанная до шага, - это поза прошлого кадра.
			_motion.Input = _playerInput;
			_playerInput = default;
			_motion.Steer(_lastStore, _physics, IsPlaying, deltaSeconds, _animation);

			_physics.Update(deltaSeconds);

			// Тело сдвинулось - трансформ сущности за ним. SyncScene, который перекладывает трансформы
			// в инстансы, идёт РАНЬШЕ по кадру, поэтому картинка отстаёт от физики ровно на кадр -
			// столько же, сколько и Play-Mode-системы, которые EditorManager тикает после вьюпорта.
			_motion.Apply(_lastStore, _physics);
		}

		/// <summary>Нужна ли физика этой сцене вообще. Мир заводится только под конкретного
		/// потребителя - персонажа с foot IK или рэгдоллом либо явно включённый дебаг физики:
		/// построение статики - это BVH по всей геометрии сцены.</summary>
		private bool ScenePhysicsWanted()
		{
			if (!_editorSettings.ScenePhysicsEnabled)
			{
				return false;
			}

			if (_editorSettings.PhysicsDebug.AnyEnabled)
			{
				return true;
			}

			var store = _lastStore;
			if (store == null)
			{
				return false;
			}

			foreach (var record in _rendered.Values)
			{
				if (store.TryGetEntityById(record.EntityId, out var entity) &&
					(entity.HasComponent<FootIkComponent>() || entity.HasComponent<RagdollComponent>() ||
						IsPhysicalCharacter(entity)))
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>Ведёт ли этого персонажа физика геймплейного скрипта. Мир заводится по нему СРАЗУ,
		/// не дожидаясь Play: статика - это BVH по всей геометрии сцены, и строить его в момент
		/// нажатия кнопки значило бы отдать первый кадр игры под подвисание.</summary>
		private static bool IsPhysicalCharacter(Entity entity) => entity.HasComponent<CharacterBodyComponent>();

		/// <summary>
		/// Состояние привода персонажей - для окна дебага. Молчащий персонаж в редакторе выглядит
		/// одинаково независимо от того, ЧТО именно его не пускает: физика выключена галочкой, игра не
		/// запущена, компонент тела не доехал из префаба или тело есть, но упёрлось. Разбирать это
		/// глазами по коду - самое дорогое, что можно тут сделать, поэтому все четыре числа выведены
		/// наружу разом.
		/// </summary>
		public (bool Playing, bool HasPhysics, bool Paused, int Scripts, int WithBody, int Bodies) ScriptCharacterStatus
		{
			get
			{
				int scripts = 0;
				int withBody = 0;
				var store = _lastStore;

				if (store != null)
				{
					// Скрипты и тела считаются ОТДЕЛЬНО: сцена, сгенерированная до появления
					// Character Body, приезжает со скриптом и без тела, и «1 и 0» отвечает на вопрос
					// сразу, а «0 из 0» отправило бы искать поломку в физике.
					foreach (var entity in store.Query<CircleMoveComponent>().Entities)
					{
						scripts++;
						withBody += entity.HasComponent<CharacterBodyComponent>() ? 1 : 0;
					}

					// Игрок - тоже скрипт движения, только рулит им клавиатура (см. PlayerMoveComponent).
					foreach (var entity in store.Query<PlayerMoveComponent>().Entities)
					{
						scripts++;
						withBody += entity.HasComponent<CharacterBodyComponent>() ? 1 : 0;
					}
				}

				return (IsPlaying, _physics != null, _physics?.Paused ?? false, scripts, withBody,
					_motion.CharacterCount);
			}
		}

		/// <summary>
		/// Пересобирает статику физики по геометрии сцены. Скиннед-модели в неё НЕ идут: персонаж не
		/// должен быть полом сам себе - его собственная стопа немедленно нашла бы лучом его же ногу,
		/// и foot IK поднял бы его на высоту собственного бедра.
		/// </summary>
		private void RebuildPhysicsStatics()
		{
			if (_physics == null)
			{
				return;
			}

			_physics.BeginStatics();

			foreach (var record in _rendered.Values)
			{
				if (!record.Instantiated || string.IsNullOrEmpty(record.ResolvedPath) ||
					!_models.TryGetValue(record.ResolvedPath, out var state) || state.Model == null ||
					state.Model.Skeleton != null)
				{
					continue;
				}

				_physicsPositions.Clear();
				_physicsIndices.Clear();
				AppendRecordGeometry(record, state.Model, _physicsPositions, _physicsIndices);

				_physics.AddStaticMesh(
					System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_physicsPositions),
					System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_physicsIndices));
			}

			_physics.EndStatics();

			// Скретч держит мировую копию всей сцены - на большом уровне это десятки мегабайт,
			// которые до следующей пересборки не нужны никому.
			_physicsPositions.Clear();
			_physicsIndices.Clear();
			_physicsPositions.TrimExcess();
			_physicsIndices.TrimExcess();
		}

		// --- Дебаг-линии (см. DebugDraw / DebugLineOverlay) ---------------------------------------

		/// <summary>Открывает кадр дебага. Звать ДО любой стадии, которая рисует: список линий - это
		/// кадр целиком, а не накопитель.</summary>
		private void BeginDebugFrame()
		{
			// Подсветка кости из окна Humanoid включает дебаг сама по себе: её просят как раз тогда,
			// когда ни один слой ещё не включён - «покажи, какая это кость».
			_debugDraw.Enabled = _editorSettings.AnimationDebug.AnyEnabled ||
				_editorSettings.PhysicsDebug.AnyEnabled ||
				!string.IsNullOrEmpty(HighlightJoint);

			_debugDraw.Clear();
		}

		/// <summary>Закрывает кадр дебага: дорисовывает то, что принадлежит миру (физика), и заливает
		/// всё на GPU. Звать ПОСЛЕ анимации и до исполнения графа.</summary>
		private void EndDebugFrame()
		{
			if (_debugDraw.Enabled && _physics != null)
			{
				var options = _editorSettings.PhysicsDebug;
				PhysicsDebugDraw.Draw(_debugDraw, _physics, options);

				if (options.RagdollJoints)
				{
					_animation?.DrawRagdollJoints(_debugDraw, options.OnTop);
				}
			}

			_animation?.DescribeCharacters(_debugCharacters);
			PollDebugLineOverlay();
		}

		/// <summary>Ведёт GPU-оверлей дебаг-линий за содержимым кадра. Создание/снятие пересобирает
		/// граф (команды заморожены, см. GraphicsPipelineSimple.DebugOverlay), поэтому проверка
		/// «есть что рисовать» обязана быть дешёвой - она и есть пара сравнений.</summary>
		private void PollDebugLineOverlay()
		{
			if (!_debugDraw.Enabled || _debugDraw.TotalCount == 0)
			{
				if (_env.Pipeline.DebugOverlay != null)
				{
					_env.Pipeline.DebugOverlay = null;
					_env.Pipeline.InvalidateGraph();
				}

				return;
			}

			if (_debugLineOverlay == null)
			{
				if (_debugOverlayFailed)
				{
					return;
				}

				try
				{
					_debugLineOverlay = new DebugLineOverlay(_env.DilApi, _graphicsApi, _env.BatchRenderer,
						_env.Pipeline.Targets?.RenderColorFormat ?? TextureObjectFormat.R8G8B8A8UNorm);
				}
				catch (Exception ex)
				{
					// Один раз и больше не пробовать: не собравшийся шейдер не соберётся и на
					// следующем кадре, а поток одинаковых ошибок в консоли скрыл бы настоящие.
					_debugOverlayFailed = true;
					EngineLog.Add(LogLevel.Error, $"Debug draw: overlay unavailable: {ex.Message}");
					return;
				}
			}

			_debugLineOverlay.Intensity = _editorSettings.DebugLineIntensity;

			bool commandsDirty = _debugLineOverlay.Upload(_debugDraw);

			if (_env.Pipeline.DebugOverlay == null)
			{
				_env.Pipeline.DebugOverlay = _debugLineOverlay.Draw;
				commandsDirty = true;
			}

			if (commandsDirty)
			{
				_env.Pipeline.InvalidateGraph();
			}
		}

		/// <summary>Снимает дебаг-оверлей с конвейера и освобождает его. Звать перед пересозданием
		/// окружения и на выходе: оверлей держит буферы и PSO этого конкретного конвейера.</summary>
		private void ReleaseDebugOverlay()
		{
			if (_debugLineOverlay == null)
			{
				return;
			}

			_env.Pipeline.DebugOverlay = null;
			_env.Pipeline.InvalidateGraph();
			_env.DilApi.ImmediateContext.Flush();
			_env.DilApi.ImmediateContext.WaitForIdle();

			_debugLineOverlay.Dispose();
			_debugLineOverlay = null;
		}

		private readonly HashSet<int> _visitedThisSync = new();
		private readonly List<int> _removeScratch = new();

		// Зеркала punctual-светов (point/spot) префаба в РЕНДЕР-сторе окружения: SimpleCullingAndRender
		// System собирает света из _env.Store, а сущности префаба живут в своём сторе с ЛОКАЛЬНЫМИ
		// трансформами - зеркало несёт мировые (ComputeWorldMatrix, как у геометрии). Ключ - id
		// сущности префаба. Синк покомпонентно каждый кадр: светов единицы, а ручки инспектора
		// (цвет/интенсивность/углы) обязаны быть живыми - пул светов и так перезаливается за кадр.
		private readonly Dictionary<int, Entity> _lightMirrors = new();

		/// <summary>Скретч сборки punctual-светов для бейка проб (см. PollSceneProbeBake) - чтобы не
		/// аллоцировать список каждый кадр.</summary>
		private readonly List<PunctualLight> _probeBakeLightsScratch = new();

		/// <summary>TLAS для RT-теней (режим «Ray-traced» комбо Shadow filtering, см.
		/// ModelPreviewViewport._rtShadowScene - та же роль). Отдельный от _sceneAccel проб: живёт
		/// независимо от probe-GI, BLAS-ы кешируются по мешам и переживают движение - на позы
		/// отвечает пересборка TLAS.</summary>
		private DiligentRayTracingScene? _rtShadowScene;
		private readonly List<DiligentRayTracingScene.Instance> _rtShadowInstances = new();
		private bool _appliedRtShadows;

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

		/// <summary>Модели, по которым считается <see cref="_probeBakerTask"/>: их освобождение
		/// обязано задачу дождаться (она читает CPU-копии вершин в неуправляемой памяти).</summary>
		private List<(ModelLoader Model, Matrix4x4 World)>? _probeBakerModels;

		/// <summary>Снимок сцены (модель + мировая матрица записи), под который собран ЖИВОЙ
		/// <see cref="_probeBaker"/>. По нему BeginProbeSession решает, устарело ли дерево: BVH
		/// мировой и позам соответствует намертво.</summary>
		private List<(ModelLoader Model, Matrix4x4 World)>? _probeBakerBuiltFor;

		/// <summary>Записи сцены в порядке инстансов строящегося дерева. Переезжают в
		/// <see cref="_probeSceneRecords"/> ТОЛЬКО вместе с готовым бейкером: пара «бейкер + записи»
		/// сопоставляется по индексу, и рассинхрон уводит в TLAS чужие позы.</summary>
		private List<RenderedModel>? _probeBakerRecords;

		/// <summary>Совпадает ли СОСТАВ сцены (набор моделей в том же порядке). Изменился - дерево
		/// пересобирается при любом режиме трассировки: в нём просто нет геометрии новой записи.</summary>
		private static bool SameSceneComposition(List<(ModelLoader Model, Matrix4x4 World)>? a,
			List<(ModelLoader Model, Matrix4x4 World)> b)
		{
			if (a == null || a.Count != b.Count)
			{
				return false;
			}

			for (int i = 0; i < a.Count; i++)
			{
				if (!ReferenceEquals(a[i].Model, b[i].Model))
				{
					return false;
				}
			}

			return true;
		}

		/// <summary>Совпадают ли ПОЗЫ записей. Матрицы сравниваются точным равенством намеренно:
		/// любое отличие означает, что мировые треугольники дерева уже не там, где геометрия.</summary>
		private static bool SameScenePoses(List<(ModelLoader Model, Matrix4x4 World)>? a,
			List<(ModelLoader Model, Matrix4x4 World)> b)
		{
			if (!SameSceneComposition(a, b))
			{
				return false;
			}

			for (int i = 0; i < a!.Count; i++)
			{
				if (a[i].World != b[i].World)
				{
					return false;
				}
			}

			return true;
		}

		private ProbeGiBakeSession? _probeSession;
		private Task? _probeRoundTask;
		private ProbeGiTextures? _probeTextures;
		private int _probeTextureGeneration;
		private float _probeSessionDelay = -1f;

		// --- Динамический GPU-путь сцены (реальное время, см. ModelPreviewViewport) ---------------
		// Движение сущности при живом GPU-пути НЕ пересоздаёт сессию: позы уезжают в TLAS, поле
		// перетекает само. Прежний путь на любое движение перепекал BVH на главном потоке (десятки
		// миллисекунд - тот самый лаг гизмо) и выбрасывал накопленное поле.
		private ProbeRoundPipelines? _scenePipelines;
		private ProbeSceneAccel? _sceneAccel;
		private ProbeRoundGpu? _sceneGpu;
		private bool _sceneGpuDisabled;

		// --- Собственный accel SSR (RT-фолбэк отражений БЕЗ probe GI) --------------------------
		// Когда accel проб недоступен (probe GI выключен/программный/ещё не собрался), SSR строит
		// геометрию сам: тем же конструктором ProbeGiBaker (сбор треугольников), но без сессии и
		// бейка. Предпочтение всегда у _sceneAccel - его позы живые (PollSceneProbePoses); свой
		// пересобирается по смене состава/поз сцены с дебаунсом (пересборка BLAS всей сцены дорогая).
		private ProbeSceneAccel? _ssrOwnAccel;
		private List<(ModelLoader Model, Matrix4x4 World)>? _ssrOwnBuiltFor;
		private float _ssrOwnRebuildDelay = -1f;

		// Наборы текстур RT-хитов (текстурное альбедо отражений, см. SsrHitTextures) - по одному
		// на каждый accel: живут и умирают вместе с ним, привязка выбирает набор того accel-а,
		// который реально ушёл в SetRayScene.
		private SsrHitTextures? _sceneAccelHitTextures;
		private SsrHitTextures? _ssrOwnHitTextures;

		/// <summary>Записи сцены в порядке списка моделей, отданного бейкеру: по
		/// ProbeGeometryInstance.SourceModel отсюда берётся ЖИВАЯ мировая матрица записи
		/// (RenderedModel - класс, LastWorld обновляется гизмо).</summary>
		private readonly List<RenderedModel> _probeSceneRecords = new();
		private readonly List<Matrix4x4> _probeScenePoses = new();
		private bool _sceneTlasDirty;

		/// <summary>Дебаг-вид проб сцены - общий жизненный цикл с превью (см.
		/// ProbeGiViewportShared.PollOverlays).</summary>
		private readonly List<(ProbeDebugOverlay Overlay, ProbeGiTextures Textures)> _sceneDebugOverlays = new();
		private bool _sceneDebugFailed;

		// --- Выделение (см. SyncSelectionHighlight / SelectionOutlineOverlay) --------------------
		private SelectionOutlineOverlay? _selectionOverlay;
		private int _highlightedId = -1;
		private bool _structuralDirtySelection;
		private readonly List<Vector3> _selectionPositions = new();
		private readonly List<uint> _selectionIndices = new();

		/// <summary>Результат клика по вьюпорту (см. <see cref="Render"/>): Clicked = был клик по
		/// сцене (не по гизмо), Entity = сущность префаба под курсором, null = клик в пустоту.</summary>
		public struct PickResult
		{
			public bool Clicked;
			public Entity? Entity;
		}

		private ShadingMode _shading = ShadingMode.Lighting;
		private PreviewFeatureFlags _featureFlags = PreviewFeatureFlags.All;

		private float _lightYawOffsetDegrees;
		private float _lightElevationOffsetDegrees;

		// Полёт/орбита/пан/фокус - см. SceneCamera. Заменяет прежние _orbitTarget/_yaw/_pitch/_distance/
		// _orbiting/_panning; ModelPreviewViewport оставлен на старой орбитальной камере намеренно -
		// там она правильная (см. задачу).
		private readonly SceneCamera _camera;
		private bool _framePending = true;

		private ImTextureRef _textureRef;
		private bool _textureBound;
		private ImGuiRender? _lastImGuiRender;
		private Vector2 _pendingSize;
		private float _resizeIdleSeconds;

		/// <summary>Масштаб рендера, увиденный последним TrackAndApplyResize, - смена сбрасывает
		/// дебаунс-таймер, как смена размера окна (см. ModelPreviewViewport).</summary>
		private float _pendingRenderScale = 1f;

		/// <summary>См. ModelPreviewViewport._pendingUpscalerApply - та же отложка.</summary>
		private bool _pendingUpscalerApply;

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
				new[] { 0, 2, 3 }[Math.Clamp(_editorSettings.FsrProvider, 0, 2)]);
		}

		// Матрицы кадра, под которыми камера рендерила последний Update, - по ним же строится
		// гизмо в Render, чтобы оно попадало пиксель в пиксель в отрендеренную геометрию.
		private Vector3 _lastEye;

		public ImGuizmoOperation Operation { get; set; } = ImGuizmoOperation.Translate;

		/// <summary>Текущий режим шейдинга - см. <see cref="SetShading"/>.</summary>
		public ShadingMode Shading => _shading;

		/// <summary>Смещения ползунков света от базового положения солнца энвайронмента
		/// (см. <see cref="SetLightRotation"/>).</summary>
		public float LightYawDegrees => _lightYawOffsetDegrees;
		public float LightElevationDegrees => _lightElevationOffsetDegrees;

		/// <summary>Есть ли в сцене хоть один отрендеренный инстанс модели.</summary>
		public bool HasContent
		{
			get
			{
				foreach (var record in _rendered.Values)
				{
					if (record.EnvEntities.Count > 0)
					{
						return true;
					}
				}
				return false;
			}
		}

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

		/// <summary>Применяет фичи конвейера к ЖИВОМУ окружению - см.
		/// <see cref="GraphicsPipelineSimple.SetFeatures"/>.</summary>
		private void ApplyPipelineFeatures()
		{
			_appliedSsao = _editorSettings.PreviewSsao;
			_appliedAoMode = _editorSettings.PreviewAoMode;
			_appliedSsgi = _editorSettings.PreviewSsgi;
			_appliedSky = _editorSettings.PreviewSkyBackground;
			_appliedSceneHdr = _editorSettings.SceneViewHdr;
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
				EyeAdaptation = _appliedSceneHdr,
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

			// RT-вариант трейса обязан получить TLAS сцены ДО первого кадра (тот же контракт, что у
			// RT-теней) - ресурсы могли только что пересоздаться под новый вариант шейдера; probe-поле
			// для света RT-хитов привязывается по той же причине здесь же.
			UpdateSsrRayScene();
			_env.SetSsrProbeField(_probeTextures);

			// Смена RT-фолбэка пересоздала SSR-ресурсы - живые ручки откатились в дефолты.
			PushSsrSettings();
		}


		/// <summary>Статус RT-фолбэка SSR для окна Graphics: null - работает; иначе человекочитаемая
		/// причина, почему фича при включённой галке МОЛЧА осталась экранной. Тихий даунгрейд без
		/// этой строки неотличим от «отражения сломаны»: зеркало в упор показывает голую env-карту
		/// (чёрную ниже горизонта), и виноватой кажется цветокоррекция.</summary>
		public string? SsrRayTracedBlockReason
		{
			get
			{
				if (_graphicsApi.RayTracing < RayTracingSupport.Inline)
				{
					return "нет inline-трассировки (нужен D3D12)";
				}
				if (_sceneAccel == null && _ssrOwnAccel == null)
				{
					return "accel сцены ещё не собран (сцена пуста или идёт загрузка)";
				}
				if (_env.Pipeline.SsrResources is not { RayTraced: true })
				{
					return "ресурсы SSR ещё не пересобраны под RT-вариант";
				}
				return null;
			}
		}

		/// <summary>Доступен ли RT-фолбэк SSR прямо сейчас: галка настроек + inline-трассировка +
		/// СУЩЕСТВУЮЩИЙ accel сцены (ProbeSceneAccel живёт при аппаратном probe GI - его TLAS и
		/// таблицы атрибутов и питают лучи отражений). Без accel-а фича молча остаётся экранной.</summary>
		private bool SsrRayTracedEnabled() =>
			_editorSettings.SsrRayTraced &&
			_graphicsApi.RayTracing >= RayTracingSupport.Inline &&
			(_sceneAccel != null || _ssrOwnAccel != null);

		/// <summary>Привязывает TLAS и таблицы атрибутов сцены RT-варианту SSR-трейса. Зовётся после
		/// каждого создания/пересоздания <see cref="_sceneAccel"/> и после смены фич: дескриптор
		/// указывает на сам объект TLAS, но ПЕРЕСОЗДАНИЕ объекта привязку протухает.</summary>
		private void UpdateSsrRayScene()
		{
			var accel = _sceneAccel ?? _ssrOwnAccel;
			if (accel != null)
			{
				_env.Pipeline.SsrResources?.SetRayScene(accel.Tlas, accel.MeshTriangles,
					accel.Instances);
				PushSsrHitTextures();
			}
		}

		/// <summary>Привязывает трейсу набор текстур RT-хитов ТОГО accel-а, что ушёл в SetRayScene
		/// (индексы текстур в его таблице инстансов указывают именно в этот набор). Зовётся вместе
		/// с каждым SetRayScene и при апгрейдах стриминга (см. PollSsrOwnRayScene).</summary>
		private void PushSsrHitTextures()
		{
			var ssr = _env.Pipeline.SsrResources;
			if (ssr is not { RayTraced: true } || ssr.HitTextureMode == 0)
			{
				return;
			}

			var set = _sceneAccel != null ? _sceneAccelHitTextures : _ssrOwnHitTextures;
			if (set == null)
			{
				ssr.SetHitTextures(null, null);
			}
			else if (ssr.HitTextureMode == 1)
			{
				ssr.SetHitTextures(set.GetAtlas(), null);
			}
			else
			{
				ssr.SetHitTextures(null, set.GetFullTextures());
			}
		}

		/// <summary>Ведёт собственный accel SSR (см. поля у _ssrOwnAccel): нужен, только когда
		/// RT-фолбэк включён, а accel-а проб нет. Пересборка синхронная и дорогая (BLAS всей
		/// сцены) - с дебаунсом по сменам состава/поз. Зовётся каждый кадр из Update.</summary>
		private void PollSsrOwnRayScene(float deltaTime)
		{
			bool wanted = _editorSettings.PreviewSsr && _editorSettings.SsrRayTraced
				&& _graphicsApi.RayTracing >= RayTracingSupport.Inline
				&& _sceneAccel == null;

			var sceneModels = new List<(ModelLoader Model, Matrix4x4 World)>();
			if (wanted)
			{
				foreach (var record in _rendered.Values)
				{
					if (record.Instantiated && !string.IsNullOrEmpty(record.ResolvedPath) &&
						_models.TryGetValue(record.ResolvedPath, out var state) && state.Model != null)
					{
						sceneModels.Add((state.Model, record.LastWorld));
					}
				}
			}

			// Стриминг дорастил текстуру - bindless-привязка указывает на старую, перепушиваем
			// (наборы обоих accel-ов; проверка - сравнение счётчиков, копейки).
			if (_ssrOwnHitTextures?.RefreshStreams() == true ||
				_sceneAccelHitTextures?.RefreshStreams() == true)
			{
				PushSsrHitTextures();
			}

			if (!wanted || sceneModels.Count == 0)
			{
				if (_ssrOwnAccel != null)
				{
					// Возврат на accel проб / выключение / опустевшая сцена: трейс-материал не
					// должен держать умирающий TLAS (и отражать призрак старой сцены).
					_env.Pipeline.SsrResources?.SetHitTextures(null, null);
					_env.DilApi.ImmediateContext.Flush();
					_env.DilApi.ImmediateContext.WaitForIdle();
					_ssrOwnAccel.Dispose();
					_ssrOwnAccel = null;
					_ssrOwnBuiltFor = null;
					_ssrOwnHitTextures?.Dispose();
					_ssrOwnHitTextures = null;
					ApplyPipelineFeatures();
				}

				_ssrOwnRebuildDelay = -1f;
				return;
			}

			if (_ssrOwnAccel != null && SameScenePoses(_ssrOwnBuiltFor, sceneModels))
			{
				_ssrOwnRebuildDelay = -1f;
				return;
			}

			// Дебаунс: драг гизмо меняет позы каждый кадр, а пересборка - BLAS всей сцены.
			if (_ssrOwnRebuildDelay < 0f)
			{
				_ssrOwnRebuildDelay = 0.4f;
				return;
			}

			_ssrOwnRebuildDelay -= deltaTime;
			if (_ssrOwnRebuildDelay > 0f)
			{
				return;
			}

			_ssrOwnRebuildDelay = -1f;

			try
			{
				var geometry = new ProbeGiBaker(sceneModels).InstancedGeometry;
				if (geometry.Instances.Length == 0)
				{
					// Геометрии нет (CPU-копии мешей недоступны) - тихий пропуск с бэкоффом.
					_ssrOwnRebuildDelay = 5f;
					return;
				}

				_env.DilApi.ImmediateContext.Flush();
				_env.DilApi.ImmediateContext.WaitForIdle();
				_ssrOwnAccel?.Dispose();
				_ssrOwnAccel = new ProbeSceneAccel(_env.DilApi, geometry);
				_ssrOwnBuiltFor = sceneModels;

				// Набор текстур хитов сшит с индексами ЭТОЙ геометрии - пересобирается вместе с ней.
				_ssrOwnHitTextures?.Dispose();
				var hitModels = new List<ModelLoader>(sceneModels.Count);
				foreach (var (m, _) in sceneModels)
				{
					hitModels.Add(m);
				}
				_ssrOwnHitTextures = SsrHitTextures.Build(_graphicsApi, geometry, hitModels);

				// Фича могла ждать появления accel-а (SsrRayTracedEnabled), ресурсы -
				// пересборки под RT-вариант; привязка внутри ApplyPipelineFeatures.
				ApplyPipelineFeatures();
			}
			catch (Exception ex)
			{
				EngineLog.Add(LogLevel.Warning,
					$"SSR: собственный accel сцены не собрался: {ex.Message}");
				_ssrOwnAccel?.Dispose();
				_ssrOwnAccel = null;
				_ssrOwnBuiltFor = null;

				// Бэкофф: причина не исчезнет через кадр - не молотим пересборкой.
				_ssrOwnRebuildDelay = 5f;
			}
		}

		/// <summary>Обработчик "OK" окна настроек: диф env-level опций против применённых - при
		/// изменении окружение пересоздаётся (отложенно, в начале Update - посреди ImGui-кадра
		/// старый таргет ещё может лежать в draw list-е), live-биты применяются сразу.</summary>
		private void OnGraphicsSettingsChanged()
		{
			// Пересоздание - только под то, что запечено не в конвейер: HDRI энвайронмента
			// (пересчёт IBL), анизотропия (в сэмплеры материалов). Остальное - фичи конвейера,
			// применяются на живом окружении (см. ApplyPipelineFeatures).
			bool needsRecreate =
				_appliedHdrPath != (ResolveEnvironmentHdrPath(_editorSettings.PreviewEnvironmentHdr) ?? "") ||
				_appliedAniso != _editorSettings.PreviewAnisotropicFiltering ||
				// Потолок текстуры печётся при загрузке - как анизотропия, требует перечитывания
				// моделей, а не просто пересоздания конвейера (см. dropModels в RecreateEnvironment).
				_appliedMaxTextureSize != ClampedMaxTextureSize() ||
				// RT-тени - кейворд в вариантах шейдера (ModelLoadOptions.RtShadows): пересечение
				// границы режима «Ray-traced» перечитывает сцену.
				_appliedRtShadows != RtShadowsEnabled();

			_pendingEnvironmentRecreate |= needsRecreate;

			if (!needsRecreate)
			{
				ApplyPipelineFeatures();
			}

			// Ручки ЗАПЕЧКИ (сетка/качество) - пересоздание сессии с дебаунсом, как в превью
			// (см. ModelPreviewViewport.ApplyGiSettings): live-ручки реального времени сюда не
			// входят, они подтягиваются в живую сессию каждым раундом.
			var wantedBake = (_editorSettings.PreviewProbeGi,
				_editorSettings.ProbeGiSkyIntensity,
				_editorSettings.ProbeGiRaysPerProbe,
				_editorSettings.ProbeGiBounces,
				_editorSettings.ProbeGiBounceSaturation,
				_editorSettings.ProbeGiGridDensity,
				_editorSettings.ProbeGiMaxProbes,
				// Путь трассировки выбирается ОДИН РАЗ при подъёме GPU-комплекта (кейворд шейдера
				// плюс структуры ускорения, см. TryBeginSceneProbeGpu), поэтому галка обязана быть ЗДЕСЬ.
				// Без этого она меняла только EditorSettings и не трогала живую сессию: сцена продолжала
				// трассировать тем путём, с которым сессию завели (по умолчанию - программным), и включение
				// аппаратной не давало РОВНО НИЧЕГО до ребейка по другой ручке.
				_editorSettings.ProbeGiHardwareRayTracing,
				// Сторона окто-карты видимости - раскладка атласов (см. ProbeGiBakeResult.VisRes).
				_editorSettings.ProbeGiVisRes);
			if (wantedBake != _appliedProbeBake)
			{
				_appliedProbeBake = wantedBake;
				RequestProbeSession(0.25f);
			}

			ApplyGraphicsSettings();
		}

		// Снимок ручек запечки, под которыми заведена текущая сценовая сессия проб.
		private (bool On, float Sky, int Rays, int Bounces, float Sat, float Density, int Max,
			bool HardwareTrace, int VisRes) _appliedProbeBake;

		/// <summary>Пересоздаёт окружение сцены под новые env-level опции БЕЗ перезагрузки сцены:
		/// резидентные ModelLoader-ы переезжают в новый батч-рендерер перерегистрацией (CPU-копии
		/// мешей живут в IMeshObject), записи сущностей пересоберутся следующим SyncScene из уже
		/// готовых моделей - ни чтения с диска, ни прогресс-баров. Исключение - смена анизотропии:
		/// она печётся в сэмплеры текстур при загрузке, такие модели перечитываются с диска.</summary>
		private void RecreateEnvironment()
		{
			// Кадры с ресурсами старого окружения могут быть в полёте - без ожидания GPU
			// освобождение роняет драйвер (та же дисциплина, что в ResizeTargets).
			_env.DilApi.ImmediateContext.Flush();
			_env.DilApi.ImmediateContext.WaitForIdle();

			if (_textureBound && _lastImGuiRender != null)
			{
				_lastImGuiRender.ReleaseRenderTargetBinding(_textureRef.GetTexID());
				_textureBound = false;
			}

			// Оверлей выделения держал таргеты/PSO старого окружения.
			_selectionOverlay?.Dispose();
			_selectionOverlay = null;
			_highlightedId = -1;

			// Атласы проб привязаны к материалам и переживать пересоздание окружения не должны -
			// сброс за барьером выше; сессия заведётся заново после пересборки сцены.
			ResetProbeGi();

			// Записи ссылались на EntityStore/ресурс-менеджер старого окружения - оно освобождается
			// целиком, поэтому просто забываем их (без Unregister). SyncScene пересоздаст.
			// Камеру не трогаем: пересоздание окружения - не смена сцены, ракурс пользователя
			// обязан пережить его незаметно.
			_rendered.Clear();
			_lightMirrors.Clear();
			_transformsDirty = false;
			_structuralDirtySelection = false;
			_physicsStaticsDirty = true;

			bool dropModels = _appliedAniso != _editorSettings.PreviewAnisotropicFiltering ||
				_appliedMaxTextureSize != ClampedMaxTextureSize() ||
				// RT-тени - кейворд в вариантах шейдера (в подписи ModelStore): резидентные модели
				// скомпилированы под другой набор, перерегистрация их не спасёт - перечитываем.
				_appliedRtShadows != RtShadowsEnabled();

			// TLAS RT-теней держит BLAS-ы по мешам умирающего батч-рендерера - освобождаем до
			// окружения (GPU уже дождались выше), пересоберётся первым же SyncScene.
			_rtShadowScene?.Release();
			_rtShadowScene = null;

			// Собственный accel SSR привязан к материалам умирающего окружения - пересоберётся
			// первым же PollSsrOwnRayScene нового.
			_ssrOwnAccel?.Dispose();
			_ssrOwnAccel = null;
			_ssrOwnBuiltFor = null;

			// Дебаг-оверлей держит буферы и PSO УМИРАЮЩЕГО конвейера - снимается до его сноса,
			// пересоздастся сам на первом же кадре с включённым дебагом.
			ReleaseDebugOverlay();

			_env.Release();
			_env = CreateEnvironment();
			_env.Root.Add(new ModelStreamingSystem(_streamer));
			ApplyLightRotation();

			// Переезд резидентных моделей в новый батч-рендерер делает стример: регистрация заново
			// создаёт GPU-стороны (мега-буферы, PSO под новые форматы), сами меши/материалы/
			// текстуры не перечитываются. Исключение - смена анизотропии (dropModels): она печётся в
			// сэмплеры при загрузке, кеш непригоден, модели перечитаются с диска обычным путём
			// SyncScene -> Acquire.
			_streamer.MigrateEnvironment(_env, dropModels);

			ApplyGraphicsSettings();
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

		/// <summary>Обходит дерево префаба и приводит env-сцену в соответствие: новые ModelRenderer-ы
		/// начинают загрузку, готовые модели инстанцируются, сдвинутые сущности переставляют свои
		/// инстансы, удалённые - убирают их.</summary>
		private void SyncScene(Entity root)
		{
			_visitedThisSync.Clear();
			_visitedLightsThisSync.Clear();
			bool structuralChange = false;
			bool boundsDirty = false;

			SyncEntity(root, ref structuralChange, ref boundsDirty);

			// Света, пропавшие из дерева (удалены/лишились компонента/сменили тип на направленный), -
			// убираем зеркала. Пересборки графа не требуется: пул светов и ClusterParams полностью
			// живые, замороженные команды перечитывают их каждый кадр.
			_removeScratch.Clear();
			foreach (var kvp in _lightMirrors)
			{
				if (!_visitedLightsThisSync.Contains(kvp.Key))
				{
					_removeScratch.Add(kvp.Key);
				}
			}
			foreach (var id in _removeScratch)
			{
				if (!_lightMirrors[id].IsNull)
				{
					_lightMirrors[id].DeleteEntity();
				}
				_lightMirrors.Remove(id);
			}

			// Сущности, пропавшие из дерева (удалены/лишились компонента), - убираем их инстансы.
			_removeScratch.Clear();
			foreach (var kvp in _rendered)
			{
				if (!_visitedThisSync.Contains(kvp.Key))
				{
					_removeScratch.Add(kvp.Key);
				}
			}
			foreach (var id in _removeScratch)
			{
				RemoveRecord(_rendered[id]);
				_rendered.Remove(id);
				structuralChange = true;
			}

			if (structuralChange)
			{
				// Команды графа заморожены после первого Compile - новые/удалённые батчи он не
				// увидит без пересборки, а освобождение/создание GPU-ресурсов требует барьера
				// (см. ModelPreviewViewport.PollPendingLoad - тот же порядок).
				_env.DilApi.ImmediateContext.Flush();
				_env.DilApi.ImmediateContext.WaitForIdle();

				// Ёмкости под новые батчи/инстансы наращиваются ЗДЕСЬ, пока GPU уже остановлен, а
				// граф всё равно будет пересобран. Иначе рост обнаруживается позже - в ветке
				// _transformsDirty, которая идёт ПОСЛЕ записи команд и барьера не делает; она
				// построена на допущении «движение ёмкости не меняет», и это допущение кто-то должен
				// обеспечивать. Лишним вызовом это не является: внутри стоит проверка, и при
				// достаточных ёмкостях он ничего не делает.
				_env.BatchRenderer.CheckAndReallocateBuffers();

				// Заливку СОДЕРЖИМОГО инстансов здесь делать нельзя, и это главное про это место:
				// мировые матрицы инстансов производит GpuInstanceBufferSystem внутри
				// _env.Root.Update, а он идёт ПОЗЖЕ по кадру. На кадре появления модели её матрицы
				// ещё не посчитаны, и любая заливка отсюда отправила бы на GPU нули - объект
				// схлопнут в точку и невидим. Дальше его никто не перезаливал, потому что грязных
				// флагов не осталось, и он оживал только от первого сдвига.
				//
				// Поэтому поднимается _transformsDirty: ветка движения идёт ПОСЛЕ Root.Update и
				// перезальёт инстансы уже с настоящими матрицами - тем же путём, которым это и так
				// работает при перетаскивании.
				_transformsDirty = true;

				// Первый шаг анимации СРАЗУ после инстанцирования, до пересборки графа. Персонаж
				// появляется в драйвере именно здесь, а кадровый UpdateAnimation идёт РАНЬШЕ по
				// коду - то есть на кадре появления модели скиннинг не диспетчеризовался ни разу,
				// и приёмник скиннед-инстанса оставался в GPU-буфере незаполненным: модель была
				// невидима (схлопнута), хотя обводка выделения рисовалась. Появлялась она только
				// после первого сдвига, который поднимал _transformsDirty и попутно всё дозаливал.
				// Нулевой шаг времени: позу посчитать нужно, а двигать её - нет.
				UpdateAnimation(0f);

				_env.Pipeline.InvalidateGraph();

				// Ручки AO/SSGI - только после барьера (SetConstant трогает ImmediateContext).
				PushPostProcessRanges();
				ApplyMaterialSettings();

				// TLAS RT-теней - тоже после барьера: свежезагруженные модели принесли новые меши
				// (BLAS) и материалы (привязка _SceneTlas обязана случиться до их первого дроу).
				UpdateRtShadowScene();
				boundsDirty = true;

				// Контур выделения строится по env-инстансам - структурное изменение (модель
				// догрузилась/сущность удалена) обязано его перепечь (см. SyncSelectionHighlight).
				_structuralDirtySelection = true;

				// По той же причине устарела и статика физики (см. RebuildPhysicsStatics).
				_physicsStaticsDirty = true;

				// Геометрия сцены сменилась - пробы пересобираются (мировой BVH бейкера статичен).
				RequestProbeSession(0.3f);
			}

			if (boundsDirty)
			{
				UpdateShadowBounds();
				if (_framePending)
				{
					FrameAll();
				}
			}

			// Движение сущностей: при живом GPU-пути позы уезжают в TLAS без пересоздания сессии -
			// и без прежнего лага (ребейк BVH на главном потоке + потеря поля). CPU-путь остаётся
			// статическим - там по-старому, ребейк за дебаунсом.
			if (_transformsDirty)
			{
				// Условие требует И СТРУКТУР УСКОРЕНИЯ, а не только живого GPU-пути: позы
				// уезжают именно в TLAS, а его нет на программной трассировке (_sceneAccel == null,
				// там шейдер ходит по BVH бейкера, а тот собран под СТАРЫЕ позы). Без этой
				// проверки ветка выбиралась по «жив ли GPU-путь», PollSceneProbePoses тут же выходил
				// по _sceneAccel == null, а ребейк никто не заказывал - движение терялось МОЛЧА, и GI
				// оставался от старой сцены до следующего ребейка по другой причине.
				if (_sceneGpu != null && _sceneAccel != null && !_sceneGpuDisabled)
				{
					_sceneTlasDirty = true;
				}
				else
				{
					RequestProbeSession(0.4f);
				}

				// TLAS RT-теней живёт отдельно от проб и следует за позами сам: пересборка верхнего
				// уровня дешёвая (BLAS-ы кешируются по мешам), гизмо-драг тянет её каждый кадр.
				UpdateRtShadowScene();
			}
		}

		/// <summary>Пересобирает TLAS RT-теней по актуальным позам сцены и привязывает его
		/// материалам всех резидентных моделей (см. ModelPreviewViewport.UpdateRtShadowScene -
		/// та же роль; здесь мировая поза инстанса = локальный TRS × мировая матрица записи).
		/// No-op вне режима «Ray-traced». Вызывается из ветки структурного изменения (после
		/// барьера) и из ветки движения сущностей.</summary>
		private void UpdateRtShadowScene()
		{
			if (!RtShadowsEnabled())
			{
				return;
			}

			_rtShadowInstances.Clear();
			foreach (var record in _rendered.Values)
			{
				var model = record.Resident?.Model;
				if (model == null || !record.Instantiated)
				{
					continue;
				}

				foreach (var instance in model.instances)
				{
					if (instance.meshId < 0 || instance.meshId >= model.Meshes.Count ||
						model.Meshes[instance.meshId] is not DiligentMesh mesh ||
						mesh.IndexCount < 3 || mesh.VertexBuffer == null || mesh.IndexBuffer == null)
					{
						continue;
					}

					var t = instance.transform;
					var local = Matrix4x4.CreateScale(t.scale.X, t.scale.Y, t.scale.Z) *
						Matrix4x4.CreateFromQuaternion(t.rotation) *
						Matrix4x4.CreateTranslation(t.position);
					_rtShadowInstances.Add(new DiligentRayTracingScene.Instance(mesh,
						local * record.LastWorld, (uint)_rtShadowInstances.Count));
				}
			}

			if (_rtShadowInstances.Count == 0)
			{
				return;
			}

			_rtShadowScene ??= new DiligentRayTracingScene(_env.DilApi);
			_rtShadowScene.Rebuild(_rtShadowInstances);
			if (_rtShadowScene.Tlas == null)
			{
				return;
			}

			// Привязка идемпотентна (дескриптор указывает на сам объект TLAS) - после структурного
			// изменения она докрывает материалы свежезагруженных моделей.
			foreach (var kvp in ((DiligentBatchRenderer)_env.BatchRenderer).GetMaterials())
			{
				kvp.Value.SetAccelStructure("_SceneTlas", _rtShadowScene.Tlas);
			}
		}

		private void SyncEntity(Entity entity, ref bool structuralChange, ref bool boundsDirty)
		{
			if (entity.HasComponent<ModelRenderer>())
			{
				var assetPath = entity.GetComponent<ModelRenderer>().modelRef.Path ?? "";
				_visitedThisSync.Add(entity.Id);

				if (!_rendered.TryGetValue(entity.Id, out var record) ||
					!string.Equals(record.AssetPath, assetPath, StringComparison.OrdinalIgnoreCase))
				{
					if (record != null)
					{
						RemoveRecord(record);
						structuralChange = true;
					}

					record = new RenderedModel { AssetPath = assetPath, EntityId = entity.Id };
					_rendered[entity.Id] = record;
				}

				if (record.ResolvedPath == null && assetPath.Length > 0)
				{
					record.ResolvedPath = ResolveAssetPath(assetPath);
					if (record.ResolvedPath == null)
					{
						EngineLog.Add(LogLevel.Warning, $"Prefab scene: asset not found: '{assetPath}'");
						// Путь не зарезолвился - помечаем запись "пустой" моделью, чтобы не искать
						// файл заново каждый кадр; смена Path в компоненте пересоздаст запись.
						record.ResolvedPath = "";
					}
				}

				if (!string.IsNullOrEmpty(record.ResolvedPath))
				{
					var world = ComputeWorldMatrix(entity);
					var anchor = world.Translation;

					// Решение стриминга по камере: в радиусе - держим ссылку (загрузка стартует из
					// ModelStreamingSystem по приоритету расстояния), вышли за радиус (с гистерезисом) -
					// снимаем инстансы и отпускаем; стример выселит модель с GPU после паузы
					// (UnloadAfterSeconds - буфер против дребезга на границе радиуса).
					if (record.Resident == null)
					{
						if (_streamer.ShouldBeResident(anchor, currentlyResident: false))
						{
							record.Resident = _streamer.Acquire(record.ResolvedPath, anchor);
						}
					}
					else
					{
						record.Resident.Anchor = anchor;

						if (!_streamer.ShouldBeResident(anchor, currentlyResident: true))
						{
							if (record.Instantiated)
							{
								RemoveRecord(record, releaseResident: false);
								structuralChange = true;
							}

							_streamer.Release(record.Resident);
							record.Resident = null;
						}
					}

					if (record.Resident is { } state)
					{
						if (!record.Instantiated && state.Ready)
						{
							InstantiateRecord(record, state, world);
							structuralChange = true;
						}
						else if (record.Instantiated && world != record.LastWorld)
						{
							UpdateRecordTransforms(record, state, world);
							boundsDirty = true;
							_transformsDirty = true;

							// Объект поехал - его треугольники в статике физики устарели. Стопа
							// персонажа обязана встать на пол там, где он ТЕПЕРЬ, а не там, где он
							// был при последней пересборке.
							//
							// НО ТОЛЬКО ЕСЛИ ОН В СТАТИКЕ ВООБЩЕ ЕСТЬ. Скиннед-модели в неё не идут
							// (см. RebuildPhysicsStatics: персонаж не должен быть полом сам себе), и
							// пересборка на их движение - это работа впустую, которая ЛОМАЕТ сцену:
							// идущий персонаж двигается каждый кадр, статика пересобирается каждый
							// кадр, а пересборка снимает и заводит заново меш пола. Тело, стоящее на
							// нём, каждый кадр теряет накопленные импульсы контакта, не успевает
							// опереться и проваливается в свободном падении - вместе с рэгдоллами, а
							// лучи foot IK при этом бьют то в пол, то в пустоту.
							_physicsStaticsDirty |= state.Model?.Skeleton == null;
						}
					}
				}
			}

			// Punctual-света (point/spot) - зеркалом в рендер-стор окружения с МИРОВЫМ трансформом
			// (Position сущности префаба локален родителю). Направленные и солнце сюда не идут:
			// направленный свет Scene View - это солнце окружения (см. SyncSunEntity).
			if (entity.HasComponent<LightComponent>() && !entity.HasComponent<SunComponent>())
			{
				var light = entity.GetComponent<LightComponent>();
				if (light.Type is LightType.Point or LightType.Spot)
				{
					_visitedLightsThisSync.Add(entity.Id);

					var world = ComputeWorldMatrix(entity);
					Matrix4x4.Decompose(world, out _, out var worldRot, out var worldPos);

					if (!_lightMirrors.TryGetValue(entity.Id, out var mirror) || mirror.IsNull)
					{
						mirror = _env.Store.CreateEntity(
							new Position(worldPos.X, worldPos.Y, worldPos.Z),
							new Rotation(worldRot.X, worldRot.Y, worldRot.Z, worldRot.W),
							light);
						_lightMirrors[entity.Id] = mirror;
					}
					else
					{
						mirror.Position = new Position(worldPos.X, worldPos.Y, worldPos.Z);
						mirror.Rotation = new Rotation(worldRot.X, worldRot.Y, worldRot.Z, worldRot.W);
						ref var mirrorLight = ref mirror.GetComponent<LightComponent>();
						mirrorLight = light;
					}
				}
			}

			foreach (var child in entity.ChildEntities)
			{
				SyncEntity(child, ref structuralChange, ref boundsDirty);
			}
		}

		/// <summary>Резолвит путь AssetRef (относительный к "Assets" проекта, forward-slash) в
		/// абсолютный. Фолбэк - папка "Assets" вверх по пути от текущего .prefab.json: префаб можно
		/// открыть и без загруженного проекта.</summary>
		private string? ResolveAssetPath(string assetPath)
		{
			if (Path.IsPathRooted(assetPath))
			{
				return File.Exists(assetPath) ? assetPath : null;
			}

			var relative = assetPath.Replace('/', Path.DirectorySeparatorChar);

			var assetsRoot = _projectSession.AssetsPath;
			if (assetsRoot != null)
			{
				var candidate = Path.Combine(assetsRoot, relative);
				if (File.Exists(candidate))
				{
					return candidate;
				}
			}

			if (_currentPrefabPath != null)
			{
				var dir = Path.GetDirectoryName(Path.GetFullPath(_currentPrefabPath));
				while (dir != null)
				{
					if (string.Equals(Path.GetFileName(dir), "Assets", StringComparison.OrdinalIgnoreCase))
					{
						var candidate = Path.Combine(dir, relative);
						return File.Exists(candidate) ? candidate : null;
					}
					dir = Path.GetDirectoryName(dir);
				}
			}

			return null;
		}

		/// <summary>Опции загрузки для стримера - те же, что были у прямого BeginLoadAsync.
		/// Фабрика (а не снимок): анизотропию пользователь меняет на лету, а смена, требующая
		/// перечитывания, обрабатывается в RecreateEnvironment (dropModels).</summary>
		private ModelLoadOptions BuildLoadOptions() => new()
		{
			VertexShader = _editorSettings.DefaultVertexShader,
			PixelShader = _editorSettings.DefaultPixelShader,
			OptimizeMesh = false,
			GenerateLods = false,
			AnisotropicFiltering = _editorSettings.PreviewAnisotropicFiltering,
			// log2 масштаба рендера - см. ModelPreviewViewport.BuildLoadOptions.
			MipLodBias = MathF.Log2(Math.Clamp(_editorSettings.RenderScale, 0.25f, 1f)),
			// Потолок стороны текстуры - та же ручка окна Graphics, что и в превью, и до сих пор её
			// здесь просто не было: в Scene View «Max texture size» не действовал вовсе. Это не
			// косметика, а ПИК памяти запечки и заливки - сцена тянет на порядок больше текстур, чем
			// одиночная модель (см. EditorSettings.PreviewMaxTextureSize).
			MaxTextureSize = ClampedMaxTextureSize(),
			RtShadows = RtShadowsEnabled(),
			// Кейворд записи G-buffer-а отражений - у окружения он теперь безусловный
			// (см. ModelViewportEnvironment).
			ReflectionGbuffer = true,
			// Текстуры не декодируются в фоновой фазе загрузки вовсе - они приезжают из стола по
			// приоритету от камеры (см. ModelStore). В кадр модель попадает уже с ними: показ ждёт
			// ModelStore.ModelTexturesReady, иначе она появлялась бы на 1x1-филлерах и домигивала
			// текстуры десятки кадров.
			StreamTextures = true
		};

		/// <summary>Потолок текстуры в том виде, в каком он уходит в загрузчик - тем же методом, что
		/// и в превью: сравнивать сырую настройку с заклампленной значило бы вечно видеть расхождение
		/// на значениях вне [128, 8192] и перечитывать сцену каждым нажатием OK.</summary>
		private int ClampedMaxTextureSize() => Math.Clamp(_editorSettings.PreviewMaxTextureSize, 128, 8192);

		/// <summary>Модель догрузилась и зарегистрирована стримером: атласы проб уже живут -
		/// новорождённой модели вместо плейсхолдеров сразу привязываются настоящие (кбуфер с сеткой
		/// она получит из ApplyMaterialSettings после структурной пересборки в SyncScene).</summary>
		private void OnStreamedModelReady(ModelStreamer.Resident resident)
		{
			_probeTextures?.Bind(resident.Model!);
		}

		/// <summary>Стример сейчас снимет регистрации батч-рендерера ОДНОЙ конкретной модели
		/// (партиционное выселение - см. ModelStreamer.ResidencyResetting): снимаем инстанс-сущности
		/// только тех записей, что ссылаются на ИМЕННО этого резидента, пока его BatchId-ы ещё валидны.
		/// Записи других моделей не трогаются - в отличие от прежней версии (полный сброс всей сцены
		/// на любое частичное выселение), см. задачу про сужение этого события.</summary>
		private void OnStreamerResidencyResetting(ModelStreamer.Resident resident)
		{
			// Стример сейчас освободит эту модель - фоновая сборка BVH (читает геометрию ВСЕЙ сцены)
			// обязана перестать её читать ДО этого.
			WaitProbeBakerTask();

			foreach (var record in _rendered.Values)
			{
				if (record.Instantiated && ReferenceEquals(record.Resident, resident))
				{
					RemoveRecord(record, releaseResident: false);
				}
			}

			// Контур выделения ссылался на снятые сущности; пробы - на материалы/BVH выселяемой
			// модели. Сброс проб безопасен: барьер GPU стример делает сразу после этого события.
			_highlightedId = -1;
			_env.Pipeline.PostOverlay = null;
			_structuralDirtySelection = true;
			_physicsStaticsDirty = true;
		}

		private void OnStreamerResidencyReset(ModelStreamer.Resident resident)
		{
			ResetProbeGi();
			_framePending |= _rendered.Count == 0;
		}

		/// <summary>Создаёт env-сущности под все инстансы модели: комбинированный трансформ =
		/// локальный трансформ glTF-инстанса * мировая матрица сущности префаба.</summary>
		private void InstantiateRecord(RenderedModel record, ModelStreamer.Resident state, Matrix4x4 world)
		{
			var model = state.Model!;
			for (int i = 0; i < model.instances.Count; i++)
			{
				var instance = model.instances[i];
				var combined = ComposeInstanceTransform(instance.transform, world);
				var entity = ModelViewportGeometry.CreateInstanceEntity(_env.Store, _env.ResourceManager,
					_env.BatchRenderer, state.MeshIds, state.MaterialIds, state.BatchCache,
					instance.meshId, instance.materialId, combined,
					model, state.SkinBases,
					// Все скиннед-инстансы модели принадлежат ОДНОЙ сущности префаба: это один
					// персонаж из нескольких мешей, и поза у них общая.
					palette => EnsureAnimation().AddInstance(record.EntityId, model, palette));
				if (entity != null)
				{
					record.EnvEntities.Add(entity.Value);
					record.InstanceIndices.Add(i);
				}
			}

			record.LastWorld = world;
			record.Instantiated = true;
		}

		/// <summary>Переставляет env-сущности записи под новую мировую матрицу сущности префаба.
		/// GpuUpdateTag снимается системой после применения (см. GpuInstanceBufferSystem) - каждое
		/// движение обязано перевесить его заново.</summary>
		private void UpdateRecordTransforms(RenderedModel record, ModelStreamer.Resident state, Matrix4x4 world)
		{
			var model = state.Model!;
			for (int i = 0; i < record.EnvEntities.Count; i++)
			{
				var instance = model.instances[record.InstanceIndices[i]];
				var t = ComposeInstanceTransform(instance.transform, world);

				var entity = record.EnvEntities[i];
				entity.Position = new Position(t.position.X, t.position.Y, t.position.Z);
				entity.Rotation = new Rotation(t.rotation.X, t.rotation.Y, t.rotation.Z, t.rotation.W);
				entity.Scale3 = new Scale3(t.scale.X, t.scale.Y, t.scale.Z);
				entity.AddTag<GpuUpdateTag>();
			}

			record.LastWorld = world;
		}

		private static DecaEngine.Graphics.Transform ComposeInstanceTransform(
			DecaEngine.Graphics.Transform instanceLocal, Matrix4x4 world)
		{
			var combined = MathUtils.CreateTrs(
				instanceLocal.position, instanceLocal.rotation, instanceLocal.scale) * world;

			if (!Matrix4x4.Decompose(combined, out var scale, out var rotation, out var translation))
			{
				// Скошенный трансформ (неравномерный скейл под поворотом) - восстанавливаем TRS из
				// базиса матрицы. Прежний фолбэк "позиция + Identity" молча ВЫБРАСЫВАЛ поворот:
				// объект рисовался неповёрнутым, а его punctual-тень вдобавок кулилась не там.
				// Ортонормированный базис под скосом приближение, но приближение В ТОМ ЖЕ месте
				// мира, а не другая поза.
				translation = combined.Translation;
				var bx = new Vector3(combined.M11, combined.M12, combined.M13);
				var by = new Vector3(combined.M21, combined.M22, combined.M23);
				var bz = new Vector3(combined.M31, combined.M32, combined.M33);
				scale = new Vector3(bx.Length(), by.Length(), bz.Length());

				var rx = scale.X > 1e-8f ? bx / scale.X : Vector3.UnitX;
				var ry = scale.Y > 1e-8f ? by / scale.Y : Vector3.UnitY;
				ry -= rx * Vector3.Dot(ry, rx);
				ry = ry.LengthSquared() > 1e-12f ? Vector3.Normalize(ry) : Vector3.UnitY;
				var rz = Vector3.Cross(rx, ry);
				rotation = Quaternion.CreateFromRotationMatrix(new Matrix4x4(
					rx.X, rx.Y, rx.Z, 0f,
					ry.X, ry.Y, ry.Z, 0f,
					rz.X, rz.Y, rz.Z, 0f,
					0f, 0f, 0f, 1f));
			}

			return new DecaEngine.Graphics.Transform
			{
				position = translation,
				rotation = rotation,
				scale = scale,
			};
		}

		/// <summary>Снимает env-сущности записи. <paramref name="releaseResident"/> - отпустить ли и
		/// ссылку стримера на модель (запись умирает совсем); false - при сбросе регистраций
		/// стримера (модель остаётся нужна, запись переинстанцируется) и при стрим-ауте по радиусу
		/// (ссылка отпускается вызывающим отдельно).</summary>
		private void RemoveRecord(RenderedModel record, bool releaseResident = true)
		{
			foreach (var entity in record.EnvEntities)
			{
				_env.ResourceManager.UnregisterRenderable(entity);
				entity.DeleteEntity();
			}
			record.EnvEntities.Clear();
			record.InstanceIndices.Clear();
			record.Instantiated = false;

			// Поза персонажа держит нативные объекты ozz и участки палитры, привязанные к уже
			// снятым инстансам: пережить их она не должна.
			_animation?.Remove(record.EntityId);

			if (releaseResident && record.Resident != null)
			{
				_streamer.Release(record.Resident);
				record.Resident = null;
			}
		}

		private void ClearScene()
		{
			if (_rendered.Count == 0 && _models.Count == 0 && _lightMirrors.Count == 0)
			{
				return;
			}

			// Физика сцены умирает вместе со сценой: её статика - это геометрия исчезающих моделей,
			// а рэгдоллы держат хендлы тел этого мира. Рэгдоллы сносятся ПЕРВЫМИ - иначе они
			// остались бы хендлами в уничтоженной симуляции. Самих персонажей снимают ниже
			// RemoveRecord-ы, по одному вместе с их записями.
			_animation?.DetachPhysics();
			_physics?.Dispose();
			_physics = null;
			_physicsStaticsDirty = true;

			foreach (var record in _rendered.Values)
			{
				RemoveRecord(record);
			}
			_rendered.Clear();

			// Зеркала светов живут в рендер-сторе окружения - оно переживает смену префаба,
			// сущности надо снять руками (в отличие от RecreateEnvironment, где стор умирает целиком).
			foreach (var mirror in _lightMirrors.Values)
			{
				if (!mirror.IsNull)
				{
					mirror.DeleteEntity();
				}
			}
			_lightMirrors.Clear();

			// «Сначала очистить предыдущее, потом грузить новое»: смена/закрытие префаба освобождает
			// резидентные модели прошлой сцены с GPU (барьер + сброс регистраций + Release +
			// пересборка графа внутри ClearAll) и отменяет её фоновые загрузки - модели нового
			// префаба стартуют в пустом батч-рендерере. Прежний кеш жил вечно и копил geometry
			// footprint всех когда-либо открытых префабов.
			WaitProbeBakerTask();
			_streamer.ClearAll();

			// Выделение ссылалось на сущности умершего стора - контур снимается вместе со сценой
			// (пересборка графа ниже подхватит и снятый PostOverlay-хук).
			_highlightedId = -1;
			_env.Pipeline.PostOverlay = null;

			// Замороженные команды графа продолжали бы рисовать снятые инстансы (см. комментарий в
			// ModelPreviewViewport.ClearWireframeOverlay) - барьер + пересборка обязательны.
			_env.DilApi.ImmediateContext.Flush();
			_env.DilApi.ImmediateContext.WaitForIdle();

			// Пробы пересчитаются под новую сцену; сброс за барьером выше.
			ResetProbeGi();

			_env.Pipeline.InvalidateGraph();
		}

		// --- Выделение: контур отдельным пассом и пикинг --------------------------------------------

		/// <summary>Держит контур выделения (см. <see cref="SelectionOutlineOverlay"/>) в согласии со
		/// сценой: перепекает мировую геометрию силуэта при смене выделения, движении сущностей или
		/// структурных изменениях (модель догрузилась/удалена). Пустое выделение снимает
		/// PostOverlay-хук с конвейера.</summary>
		private void SyncSelectionHighlight(Entity? selected)
		{
			int id = selected.HasValue && !selected.Value.IsNull ? selected.Value.Id : -1;
			bool selectionChanged = id != _highlightedId;

			if (!selectionChanged && !_transformsDirty && !_structuralDirtySelection)
			{
				return;
			}

			_highlightedId = id;
			_structuralDirtySelection = false;

			_selectionPositions.Clear();
			_selectionIndices.Clear();
			if (selected.HasValue && id != -1)
			{
				CollectSelectionGeometry(selected.Value);
			}

			if (_selectionPositions.Count == 0)
			{
				if (_env.Pipeline.PostOverlay != null)
				{
					_env.Pipeline.PostOverlay = null;
					_env.Pipeline.InvalidateGraph();
				}
				return;
			}

			_selectionOverlay ??= new SelectionOutlineOverlay(_env.DilApi, _graphicsApi, _env.BatchRenderer,
				(IRenderTarget)_env.ColorTarget);

			// Пересборка команд пасса нужна только когда изменилось ЧИСЛО индексов/пересозданы
			// буферы либо хук ещё не висел; обновление содержимого буферов на месте (гизмо-драг)
			// замороженные команды переживают.
			bool commandsDirty = _selectionOverlay.UpdateGeometry(_selectionPositions, _selectionIndices);
			if (_env.Pipeline.PostOverlay == null)
			{
				_env.Pipeline.PostOverlay = _selectionOverlay.Draw;
				commandsDirty = true;
			}

			if (commandsDirty)
			{
				_env.Pipeline.InvalidateGraph();
			}
		}

		/// <summary>Собирает мировую геометрию силуэта выделенной сущности И её потомков (выделение
		/// родителя подсвечивает всё поддерево, как в обычных редакторах).</summary>
		private void CollectSelectionGeometry(Entity entity)
		{
			if (_rendered.TryGetValue(entity.Id, out var record) && record.Instantiated &&
				!string.IsNullOrEmpty(record.ResolvedPath) &&
				_models.TryGetValue(record.ResolvedPath, out var state) && state.Model != null)
			{
				AppendRecordGeometry(record, state.Model, _selectionPositions, _selectionIndices);
			}

			foreach (var child in entity.ChildEntities)
			{
				CollectSelectionGeometry(child);
			}
		}

		/// <summary>Запекает вершины инстансов записи в мировое пространство (CPU-копии вершин живут
		/// в IMeshObject, пока жива модель - тот же источник, что у probe-GI BVH, см. ProbeGiBaker).
		///
		/// Приёмник приходит параметром: та же самая мировая геометрия нужна и обводке выделения, и
		/// статике физики (см. RebuildPhysicsStatics), и переписывать её вторым таким же методом
		/// значило бы завести второе место, где можно ошибиться с фильтром топологии или с
		/// трансформом инстанса.</summary>
		private void AppendRecordGeometry(RenderedModel record, ModelLoader model,
			List<Vector3> targetPositions, List<uint> targetIndices)
			=> AppendModelGeometry(model,
				System.Runtime.InteropServices.CollectionsMarshal.AsSpan(record.InstanceIndices),
				record.LastWorld, targetPositions, targetIndices);

		/// <summary>Та же запечка, но по ЯВНОМУ списку инстансов и явной мировой матрице - вход для
		/// headless-прогона сцены (см. <see cref="ScenePhysicsProbe"/>). Вынесено, чтобы у пробника не
		/// завелась вторая копия фильтра топологии и склейки трансформа инстанса: разойдясь с этой,
		/// она проверяла бы не ту статику, которую строит редактор.</summary>
		internal static unsafe void AppendModelGeometry(ModelLoader model, ReadOnlySpan<int> instanceIndices,
			Matrix4x4 world, List<Vector3> targetPositions, List<uint> targetIndices)
		{
			for (int i = 0; i < instanceIndices.Length; i++)
			{
				var instance = model.instances[instanceIndices[i]];
				if (instance.meshId < 0 || instance.meshId >= model.Meshes.Count)
				{
					continue;
				}

				// Только треугольные меши: PSO маски - TriangleList, индексы линий/точек дали бы
				// мусорные треугольники в силуэте (тот же фильтр, что у ProbeGiBaker).
				if (model.MaterialPbr.TryGetValue(instance.materialId, out var pbr) &&
					pbr.Topology != ModelLoader.MeshTopologyTriangles)
				{
					continue;
				}

				var mesh = model.Meshes[instance.meshId];
				if (mesh.IndexCount < 3 || mesh.VertexData == null || mesh.IndexData == null)
				{
					continue;
				}

				var t = ComposeInstanceTransform(instance.transform, world);
				var matrix = MathUtils.CreateTrs(t.position, t.rotation, t.scale);

				int vertexCount = UnsafeArray.GetLength(mesh.VertexData);
				var vertices = new ReadOnlySpan<Vertex>(UnsafeArray.GetPtr<Vertex>(mesh.VertexData, 0), vertexCount);

				// ModelLoader всегда строит 32-битные индексы (см. PreparedMesh.Indices: uint[]).
				var indices = new ReadOnlySpan<uint>(UnsafeArray.GetPtr<uint>(mesh.IndexData, 0), mesh.IndexCount);

				int baseVertex = targetPositions.Count;
				for (int v = 0; v < vertexCount; v++)
				{
					targetPositions.Add(Vector3.Transform(vertices[v].Position, matrix));
				}
				for (int j = 0; j < indices.Length; j++)
				{
					targetIndices.Add((uint)baseVertex + indices[j]);
				}
			}
		}

		/// <summary>Пикинг кликом: луч из камеры через пиксель, сферный broadphase по баундам мешей,
		/// затем точное пересечение с треугольниками (CPU-копии вершин). Возвращает сущность префаба
		/// ближайшего попадания, null - клик в пустоту.</summary>
		private unsafe Entity? Pick(Vector2 cursor, Vector2 size, Vector2 mouse)
		{
			// Луч в мировом пространстве - те же камера/проекция, под которыми рендерился кадр.
			float u = (mouse.X - cursor.X) / size.X * 2f - 1f;
			float v = 1f - (mouse.Y - cursor.Y) / size.Y * 2f;
			float tanHalf = MathF.Tan(CameraFovDegrees * (MathF.PI / 180f) * 0.5f);

			var view = Matrix4x4.CreateLookAtLeftHanded(_lastEye, _camera.Target, Vector3.UnitY);
			if (!Matrix4x4.Invert(view, out var invView))
			{
				return null;
			}

			var dirView = new Vector3(u * tanHalf * (size.X / size.Y), v * tanHalf, 1f);
			var dir = Vector3.Normalize(Vector3.TransformNormal(dirView, invView));
			var origin = _lastEye;

			float bestT = float.PositiveInfinity;
			int bestId = -1;

			foreach (var kvp in _rendered)
			{
				var record = kvp.Value;
				if (!record.Instantiated || string.IsNullOrEmpty(record.ResolvedPath) ||
					!_models.TryGetValue(record.ResolvedPath, out var state) || state.Model == null)
				{
					continue;
				}

				var model = state.Model;
				for (int i = 0; i < record.InstanceIndices.Count; i++)
				{
					var instance = model.instances[record.InstanceIndices[i]];
					if (instance.meshId < 0 || instance.meshId >= model.Meshes.Count)
					{
						continue;
					}

					var mesh = model.Meshes[instance.meshId];
					var t = ComposeInstanceTransform(instance.transform, record.LastWorld);
					var matrix = MathUtils.CreateTrs(t.position, t.rotation, t.scale);

					// Broadphase: сфера баундов меша в мире.
					var center = Vector3.Transform(mesh.Center, matrix);
					var maxScale = MathF.Max(MathF.Abs(t.scale.X), MathF.Max(MathF.Abs(t.scale.Y), MathF.Abs(t.scale.Z)));
					var radius = mesh.Radius * maxScale;
					if (!RayIntersectsSphere(origin, dir, center, radius, out var sphereT) || sphereT >= bestT)
					{
						continue;
					}

					// Точная фаза: луч в локальном пространстве инстанса против CPU-треугольников.
					// Без CPU-копий (не должно случаться для моделей превью-лоадера) - берём сферу.
					if (mesh.VertexData == null || mesh.IndexData == null || mesh.IndexCount < 3 ||
						!Matrix4x4.Invert(matrix, out var invMatrix))
					{
						if (sphereT < bestT)
						{
							bestT = sphereT;
							bestId = kvp.Key;
						}
						continue;
					}

					var lo = Vector3.Transform(origin, invMatrix);
					var ld = Vector3.TransformNormal(dir, invMatrix);

					int vertexCount = UnsafeArray.GetLength(mesh.VertexData);
					var vertices = new ReadOnlySpan<Vertex>(UnsafeArray.GetPtr<Vertex>(mesh.VertexData, 0), vertexCount);
					var indices = new ReadOnlySpan<uint>(UnsafeArray.GetPtr<uint>(mesh.IndexData, 0), mesh.IndexCount);

					for (int j = 0; j + 2 < indices.Length; j += 3)
					{
						uint j0 = indices[j], j1 = indices[j + 1], j2 = indices[j + 2];
						if (j0 >= vertexCount || j1 >= vertexCount || j2 >= vertexCount)
						{
							continue;
						}

						if (!RayIntersectsTriangle(lo, ld, vertices[(int)j0].Position,
								vertices[(int)j1].Position, vertices[(int)j2].Position, out var localT))
						{
							continue;
						}

						// t локального луча не сравним между инстансами (масштаб) - переводим точку
						// попадания обратно в мир и меряем вдоль мирового луча.
						var worldHit = Vector3.Transform(lo + ld * localT, matrix);
						var worldT = Vector3.Dot(worldHit - origin, dir);
						if (worldT > 0f && worldT < bestT)
						{
							bestT = worldT;
							bestId = kvp.Key;
						}
					}
				}
			}

			if (bestId < 0 || _lastStore == null)
			{
				return null;
			}

			var picked = _lastStore.GetEntityById(bestId);
			return picked.IsNull ? null : picked;
		}

		private static bool RayIntersectsSphere(Vector3 origin, Vector3 dir, Vector3 center, float radius, out float t)
		{
			t = 0f;
			var oc = center - origin;
			float proj = Vector3.Dot(oc, dir);
			float distSq = oc.LengthSquared() - proj * proj;
			float radiusSq = radius * radius;
			if (distSq > radiusSq)
			{
				return false;
			}

			float half = MathF.Sqrt(radiusSq - distSq);
			t = proj - half;
			if (t < 0f)
			{
				t = proj + half;
			}
			return t >= 0f;
		}

		/// <summary>Möller-Trumbore; ld может быть ненормированным (локальное пространство инстанса) -
		/// t возвращается в его единицах.</summary>
		private static bool RayIntersectsTriangle(Vector3 lo, Vector3 ld, Vector3 a, Vector3 b, Vector3 c, out float t)
		{
			t = 0f;
			var e1 = b - a;
			var e2 = c - a;
			var p = Vector3.Cross(ld, e2);
			float det = Vector3.Dot(e1, p);
			if (MathF.Abs(det) < 1e-12f)
			{
				return false;
			}

			float invDet = 1f / det;
			var s = lo - a;
			float bu = Vector3.Dot(s, p) * invDet;
			if (bu < 0f || bu > 1f)
			{
				return false;
			}

			var q = Vector3.Cross(s, e1);
			float bv = Vector3.Dot(ld, q) * invDet;
			if (bv < 0f || bu + bv > 1f)
			{
				return false;
			}

			t = Vector3.Dot(e2, q) * invDet;
			return t > 0f;
		}

		// --- Probe GI сцены -------------------------------------------------------------------------

		private bool ProbesEnabled => _editorSettings.SceneViewHdr;

		/// <summary>Статус проб СЦЕНЫ для окна Graphics - раньше окно показывало только превью
		/// модели, и при работающем в сцене GI писало «нет проб».</summary>
		public string ProbeGiStatus
		{
			get
			{
				if (!ProbesEnabled)
				{
					return "выключен (нужен Scene View HDR)";
				}

				var s = _probeSession;
				if (s == null)
				{
					return "нет проб";
				}

				var grid = $"{s.CountX}x{s.CountY}x{s.CountZ}";
				if (_sceneGpu == null)
				{
					return $"{grid}, GPU-путь не поднялся (см. консоль)";
				}

				// Какой путь трассировки ЖИВОЙ. Снаружи это было невидимо: галка в окне Graphics
				// показывает ЖЕЛАНИЕ, а путь выбирается при подъёме сессии и законно может с ним не
				// совпадать: устройство не умеет inline-трассировки либо сессию ещё не перезавели.
				// Без этой строки одно от другого отличалось только под профайлером.
				grid += _sceneGpu.Hardware ? ", трассировка аппаратная"
					: ", трассировка программная";

				return s.Realtime
					? $"{grid}, реальное время"
					: s.Converged
						? $"{grid}, готово"
						: $"{grid}, раунд {s.Round}/{s.TargetRounds}";
			}
		}

		/// <summary>Цвет/интенсивность солнца для бейка - тот же keyIntensity, что у аналитического
		/// света (ProbeGiParams.z), иначе баунс разойдётся по яркости с прямым светом.</summary>
		private Vector3 ProbeSunColor() =>
			new Vector3(1f, 0.98f, 0.92f) * Math.Clamp(_editorSettings.ProbeGiSunIntensity, 0.1f, 16f);

		/// <summary>Принудительный ребейк проб сцены - кнопка «Rebake now» окна Graphics (зеркало
		/// ModelPreviewViewport.RequestProbeRebake; раньше кнопка перепекала только превью, а сессия
		/// сцены жила старым полем до первого структурного изменения).</summary>
		public void RequestProbeRebake() => RequestProbeSession(0f);

		/// <summary>Просит пересоздать сессию бейка (изменилась геометрия/позы сцены). Дебаунс -
		/// драг гизмо шлёт изменение каждый кадр, а новая сессия выбрасывает накопленное поле.</summary>
		private void RequestProbeSession(float delaySeconds)
		{
			if (ProbesEnabled && HasContent)
			{
				_probeSessionDelay = delaySeconds;
			}
		}

		/// <summary>Заводит сессию бейка по ВСЕЙ сцене: мульти-модельный BVH из (модель, мировая
		/// матрица записи) - см. новый конструктор ProbeGiBaker. Бейкер пересобирается на каждую
		/// сессию: в отличие от превью, геометрия сцены меняется, и BVH обязан ей соответствовать
		/// (десятки миллисекунд на главном потоке - за дебаунсом).</summary>
		private void BeginProbeSession()
		{
			if (!ProbesEnabled || _env.ShadowSettings == null || !TryComputeSceneBounds(out var min, out var max))
			{
				return;
			}

			// Список записей собирается ЛОКАЛЬНО и переносится в поле только вместе с бейкером, под
			// который он собран. Иначе, пока фоновая сборка считается, _probeSceneRecords уже новый,
			// а _probeBaker ещё старый - и PollSceneProbePoses, сопоставляющий их ПО ИНДЕКСУ
			// (ProbeGeometryInstance.SourceModel), утаскивает в TLAS матрицы от чужих записей.
			// Геометрия трассировки разъезжается с видимой, и пробы начинают ловить свет и тень из
			// ниоткуда - те самые веера и ромбы в отладочных видах.
			var sceneModels = new List<(ModelLoader Model, Matrix4x4 World)>();
			var sceneRecords = new List<RenderedModel>();
			foreach (var record in _rendered.Values)
			{
				if (record.Instantiated && !string.IsNullOrEmpty(record.ResolvedPath) &&
					_models.TryGetValue(record.ResolvedPath, out var state) && state.Model != null)
				{
					sceneModels.Add((state.Model, record.LastWorld));
					// Индекс здесь = SourceModel инстансов бейкера - по нему слежение за позами
					// достаёт живую LastWorld записи (см. PollSceneProbePoses).
					sceneRecords.Add(record);
				}
			}

			if (sceneModels.Count == 0)
			{
				return;
			}

			// Сборка BVH - в ФОНЕ: на сцене уровня Sponza это миллионы треугольников и десятки
			// секунд чистого CPU; на потоке рендера она вешала редактор целиком уже после того, как
			// модели показались (см. ModelPreviewViewport.BeginProbeSession - та же схема).
			//
			// Когда дерево устарело. Состав сцены - всегда: в старом BVH просто нет геометрии новой
			// записи. Позы - только на ПРОГРАММНОЙ трассировке, где лучи ходят по мировому BVH и он
			// приколочен к позам намертво; при аппаратной позы живут в TLAS и обновляются без
			// ребейка (см. PollSceneProbePoses - ради этого его и делали, ребейк на каждое движение
			// был тем самым лагом гизмо).
			//
			// Без этой сверки дерево после первой сборки оставалось бы под стартовые позы навсегда,
			// и подвинутый объект продолжал бы светить и затенять со старого места.
			bool tlasTracksPoses = _sceneGpu != null && _sceneAccel != null && !_sceneGpuDisabled;
			bool treeStale = _probeBaker == null
				|| !SameSceneComposition(_probeBakerBuiltFor, sceneModels)
				|| (!tlasTracksPoses && !SameScenePoses(_probeBakerBuiltFor, sceneModels));

			if (treeStale)
			{
				if (_probeBakerTask != null)
				{
					return;
				}

				var models = sceneModels;
				_probeBakerModels = models;

				// Записи поедут в поле вместе с готовым бейкером (см. PollProbeBake) - до тех пор
				// живой бейкер и _probeSceneRecords обязаны оставаться согласованной парой.
				_probeBakerRecords = sceneRecords;
				_probeBakerTask = Task.Run(() => new ProbeGiBaker(models));
				return;
			}

			if (!_probeBaker.HasGeometry)
			{
				return;
			}

			// Дерево актуально: состав тот же, значит порядок записей совпадает с порядком инстансов
			// живого бейкера - список можно принять.
			_probeSceneRecords.Clear();
			_probeSceneRecords.AddRange(sceneRecords);

			_probeSceneBoundsMin = min;
			_probeSceneBoundsMax = max;

			// LightDirection указывает ОТ солнца, бейкер ждёт направление НА солнце.
			_probeSession = _probeBaker.BeginBake(min, max,
				Vector3.Normalize(-_env.ShadowSettings.LightDirection), ProbeSunColor(),
				_env.ShadowSettings.EnvYawRadians, _env.EnvironmentRadiance, BuildSceneProbeOptions());

			TryBeginSceneProbeGpu();
		}

		/// <summary>Настройки бейка сцены.</summary>
		private ProbeGiBakeOptions BuildSceneProbeOptions() =>
			ProbeGiViewportShared.BuildOptions(_editorSettings);

		private Vector3 _probeSceneBoundsMin, _probeSceneBoundsMax;

		/// <summary>Поднимает GPU-путь сцены: атласы под запись из шейдера, аппаратные структуры,
		/// compute-раунды. Только в реальном времени - ради него всё и делается: движение сущностей
		/// перестаёт перепекать сессию (см. PollSceneProbePoses). При любой осечке молча остаёмся на
		/// CPU-пути - он рабочий, просто статический.</summary>
		private void TryBeginSceneProbeGpu()
		{
			var session = _probeSession;
			var baker = _probeBaker;
			if (session == null || baker == null || _sceneGpuDisabled)
			{
				return;
			}

			// Сессия пересоздана (структурное изменение сцены) - прежний GPU-комплект обязан уйти
			// ДО нового: он привязан к старой сессии, и без освобождения каждая пересборка текла бы
			// полным набором буферов. Оверлей - первым: замороженные команды графа держат атласы.
			ReleaseSceneProbeDebugOverlay();
			ReleaseSceneProbeGpu();
			if (_probeTextures != null)
			{
				// РАНЬШЕ освобождения: SRB SSR-трейса держит SH-атласы (свет RT-хитов).
				_env.SetSsrProbeField(null);
				_env.DilApi.ImmediateContext.Flush();
				_env.DilApi.ImmediateContext.WaitForIdle();
				_probeTextures.Release();
				_probeTextures = null;
			}

			try
			{
				// Кэш поверхностей - только запечке: в реальном времени он не читается (этап 3), а
				// его захват - сотни миллисекунд НА ПОТОКЕ РЕНДЕРА (стопор кадров, таймауты
				// кадрового объекта при создании сессии большой сцены).
				if (!_editorSettings.ProbeGiRealtime)
				{
					baker.EnsureSurfaceCache(session);
				}

				_probeTextures = new ProbeGiTextures(_graphicsApi, session.Result,
					$"_sceneProbeGi{_probeTextureGeneration++}", gpuWritable: true);
				BindProbeTextures();
				ApplyMaterialSettings();

				bool hardware = _editorSettings.ProbeGiHardwareRayTracing
					&& _graphicsApi.RayTracing >= RayTracingSupport.Inline;
				_sceneAccel = hardware ? new ProbeSceneAccel(_env.DilApi, baker.InstancedGeometry) : null;

				// Набор текстур RT-хитов - вместе с accel-ом: индексы в его таблице инстансов
				// указывают именно в этот набор. Модели - _probeBakerBuiltFor: снимок, под который
				// собран ЖИВОЙ бейкер (его порядок = индексы моделей в HitTextureKeys).
				// НЕ _probeBakerModels - это модели ЗАДАЧИ, и PollProbeBake обнуляет его при её
				// завершении, то есть здесь он всегда null, набор молча не строился, и bindless
				// уходил на плейсхолдеры («каша» вместо текстур в отражениях).
				_sceneAccelHitTextures?.Dispose();
				_sceneAccelHitTextures = null;
				if (_sceneAccel != null && _probeBakerBuiltFor != null)
				{
					var hitModels = new List<ModelLoader>(_probeBakerBuiltFor.Count);
					foreach (var (m, _) in _probeBakerBuiltFor)
					{
						hitModels.Add(m);
					}
					_sceneAccelHitTextures = SsrHitTextures.Build(_graphicsApi,
						baker.InstancedGeometry, hitModels);
				}

				if (_scenePipelines != null && _scenePipelines.Hardware != hardware)
				{
					_scenePipelines.Dispose();
					_scenePipelines = null;
				}

				_scenePipelines ??= new ProbeRoundPipelines(_env.DilApi, hardware);
				_sceneGpu = new ProbeRoundGpu(_env.DilApi, _scenePipelines, session, baker,
					_probeTextures, _env.EnvironmentMap, _env.ShadowSettings!.EnvYawRadians, _sceneAccel);

				// RT-фолбэк SSR питается этим же accel-ом: он мог только что появиться (фича ждала
				// его) или пересоздаться (дескриптор протух) - фичи и привязка обновляются здесь же.
				if (_editorSettings.SsrRayTraced)
				{
					ApplyPipelineFeatures();
				}
				UpdateSsrRayScene();
				_env.SetSsrProbeField(_probeTextures);
			}
			catch (Exception ex)
			{
				EngineLog.Add(LogLevel.Warning,
					$"Scene probe GI: GPU path unavailable, CPU fallback: {ex.Message}");
				_sceneGpuDisabled = true;
				ReleaseSceneProbeGpu();
			}
		}

		/// <summary>Ведёт дебаг-вид проб сцены за галочкой Probe spheres и жизнью атласов - зеркало
		/// ModelPreviewViewport.PollProbeDebugOverlay для единственного объёма сцены.</summary>
		private void PollSceneProbeDebugOverlay() =>
			ProbeGiViewportShared.PollOverlays(_sceneDebugOverlays,
				ProbesEnabled && _editorSettings.ProbeGiShowProbes && _sceneGpu != null,
				ref _sceneDebugFailed, _env, _graphicsApi, _probeSession, _probeTextures);

		private void ReleaseSceneProbeDebugOverlay() =>
			ProbeGiViewportShared.ReleaseOverlays(_sceneDebugOverlays, _env);

		/// <summary>Освобождает GPU-путь сцены за барьером (конвейеры переживают - их компиляция
		/// дорогая, а от сессии они не зависят).</summary>
		private void ReleaseSceneProbeGpu()
		{
			if (_sceneGpu == null && _sceneAccel == null)
			{
				return;
			}

			// Трейс не должен держать view умирающего атласа текстур хитов (та же дисциплина,
			// что у probe-атласов выше).
			if (_sceneAccelHitTextures != null)
			{
				_env.Pipeline.SsrResources?.SetHitTextures(null, null);
			}

			_env.DilApi.ImmediateContext.Flush();
			_env.DilApi.ImmediateContext.WaitForIdle();
			_sceneGpu?.Dispose();
			_sceneGpu = null;

			bool hadAccel = _sceneAccel != null;
			_sceneAccel?.Dispose();
			_sceneAccel = null;
			_sceneAccelHitTextures?.Dispose();
			_sceneAccelHitTextures = null;

			// RT-вариант SSR-трейса держал дескриптор на только что уничтоженный TLAS - откат на
			// экранный вариант (SsrRayTracedEnabled без accel-а даёт false, ресурсы пересоберутся).
			if (hadAccel && _editorSettings.SsrRayTraced)
			{
				ApplyPipelineFeatures();
			}
		}

		/// <summary>Ведёт TLAS за позами сущностей - сердце динамики сцены: гизмо двигает запись,
		/// TLAS пересобирается из живых LastWorld, поле перетекает само. Сессия при этом НЕ
		/// пересоздаётся - ни ребейка BVH на главном потоке, ни потери накопленного.</summary>
		private void PollSceneProbePoses()
		{
			var session = _probeSession;
			var baker = _probeBaker;
			if (!_sceneTlasDirty || session == null || baker == null
				|| _sceneGpu == null || _sceneAccel == null)
			{
				return;
			}

			// Только на границе раунда - иначе половина проб отследит старую сцену, половина новую
			// (см. ProbeRoundGpu.AtRoundStart).
			if (!_sceneGpu.AtRoundStart)
			{
				return;
			}

			_sceneTlasDirty = false;

			var instances = baker.InstancedGeometry.Instances;
			_probeScenePoses.Clear();
			for (int i = 0; i < instances.Length; i++)
			{
				int model = instances[i].SourceModel;
				_probeScenePoses.Add(model >= 0 && model < _probeSceneRecords.Count
					? instances[i].LocalTransform * _probeSceneRecords[model].LastWorld
					: instances[i].Transform);
			}

			try
			{
				_sceneAccel.Rebuild(
					System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_probeScenePoses));

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
				EngineLog.Add(LogLevel.Error,
					$"Scene probe GI: TLAS rebuild failed, scene frozen for tracing: {ex.Message}");
				_sceneGpuDisabled = true;
			}
		}

		/// <summary>Покадровый привод бейка: забирает завершившийся CPU-раунд в атласы, тикает
		/// дебаунс пересоздания сессии, запускает следующий раунд. Раунды строго по одному - сессия
		/// не потокобезопасна, и всё, что её трогает (свет), делается пока фоновая задача не бежит.</summary>
		private void PollProbeBake(float deltaTime)
		{
			if (!ProbesEnabled)
			{
				return;
			}

			if (_probeRoundTask != null)
			{
				if (!_probeRoundTask.IsCompleted)
				{
					return;
				}

				var finished = _probeRoundTask;
				_probeRoundTask = null;

				if (finished.IsCompletedSuccessfully)
				{
					UploadProbeSnapshot();
				}
				else
				{
					_probeSession = null;
					EngineLog.Add(LogLevel.Error, "Scene probe GI: bake round failed: " +
						(finished.Exception?.GetBaseException().Message ?? "Unknown error"));
				}
			}

			// Фоновая сборка BVH завершилась - принимаем результат и заводим сессию.
			if (_probeBakerTask != null && _probeBakerTask.IsCompleted)
			{
				var task = _probeBakerTask;
				var builtFor = _probeBakerModels;
				var builtRecords = _probeBakerRecords;
				_probeBakerTask = null;
				_probeBakerModels = null;
				_probeBakerRecords = null;

				if (!task.IsCompletedSuccessfully)
				{
					EngineLog.Add(LogLevel.Error,
						$"Scene probe GI: failed to build BVH: {task.Exception?.GetBaseException().Message}");
				}
				else
				{
					_probeBaker = task.Result;

					// Снимок поз, под который собрано дерево: по нему BeginProbeSession поймёт, что
					// сцену снова подвинули и BVH пора пересобирать.
					_probeBakerBuiltFor = builtFor;

					// Записи - СТРОГО вместе с бейкером: их порядок и есть SourceModel его инстансов,
					// по нему PollSceneProbePoses тянет живые позы в TLAS.
					_probeSceneRecords.Clear();
					if (builtRecords != null)
					{
						_probeSceneRecords.AddRange(builtRecords);
					}

					BeginProbeSession();
				}
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
			if (session == null || baker == null)
			{
				return;
			}

			// Живые ручки реального времени и свет - перед каждым раундом (см. ModelPreviewViewport).
			session.Realtime = _editorSettings.ProbeGiRealtime && _sceneGpu != null;
			session.RealtimeRaysPerRound = Math.Clamp(_editorSettings.ProbeGiRealtimeRays, 8, 1024);
			session.RealtimeBlend = Math.Clamp(_editorSettings.ProbeGiRealtimeBlend, 0.01f, 0.5f);
			session.RealtimeMaxStep = Math.Clamp(_editorSettings.ProbeGiRealtimeMaxStep, 0f, 0.2f);
			session.RealtimeGamma = Math.Clamp(_editorSettings.ProbeGiRealtimeGamma, 1f, 8f);
			// Порог сходимости - без этой строки ползунок «Порог сходимости» действовал только при
			// пересоздании сессии, хотя помечен Live (значение из BuildOptionsCore застывало).
			session.VariabilityThreshold = MathF.Max(_editorSettings.ProbeGiVariabilityThreshold, 0f);
			// Релокация проб - такая же live-ручка, как соседние, но её здесь не было: в Scene View
			// ручка «Relocation» окна Graphics не делала НИЧЕГО, а пробы, замурованные в стенах,
			// оставались замурованными (в превью слот есть, см. ModelPreviewViewport.PollProbeBake).
			session.RealtimeRelocation = Math.Clamp(_editorSettings.ProbeGiRealtimeRelocation, 0f, 0.45f);

			PollSceneProbePoses();

			// Свет подтягивается перед каждым раундом: поворот солнца откатывает сходимость, и поле
			// само перетекает к новому решению, не выбрасывая накопленное (см.
			// ProbeGiBakeSession.SetLighting).
			if (_env.ShadowSettings != null)
			{
				session.SetLighting(Vector3.Normalize(-_env.ShadowSettings.LightDirection),
					ProbeSunColor(), _env.ShadowSettings.EnvYawRadians, _env.EnvironmentRadiance);
			}

			// Punctual-света сцены - в бейк той же механикой: движение/правка лампы реально меняет
			// список (сравнение внутри SetPunctualLights) и откатывает вес раунда. Зеркала уже несут
			// МИРОВЫЕ Position/Rotation (см. SyncEntity).
			_probeBakeLightsScratch.Clear();
			foreach (var mirror in _lightMirrors.Values)
			{
				if (mirror.IsNull)
				{
					continue;
				}

				ref var mirrorLight = ref mirror.GetComponent<LightComponent>();
				if (LightCulling.TryBuildBakeLight(ref mirrorLight, mirror, out var bakeLight))
				{
					_probeBakeLightsScratch.Add(bakeLight);
				}
			}

			session.SetPunctualLights(
				System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_probeBakeLightsScratch));

			if (session.Converged)
			{
				return;
			}

			// GPU-раунды: запись команд на потоке рендера, атласы пишет шейдер - ни фоновой задачи,
			// ни снимка (см. ModelPreviewViewport.PollProbeBake, тот же привод).
			if (_sceneGpu != null)
			{
				if (!_sceneGpu.IsReady)
				{
					return;
				}

				try
				{
					// Общий цикл порций (см. ProbeGiViewportShared.DriveChunks): бюджет тратится
					// целиком, переходя границы раундов. Объём один - бюджет лучей кадра весь его.
					ProbeGiViewportShared.DriveChunks(_sceneGpu, session, baker,
						_sceneGpu.ChunksPerFrame(session.RaysPerRound));
				}
				catch (Exception ex)
				{
					EngineLog.Add(LogLevel.Error,
						$"Scene probe GI: GPU round failed, probes disabled: {ex.Message}");
					_sceneGpuDisabled = true;
					ReleaseSceneProbeGpu();
				}
			}

			// CPU-привода больше нет (см. ModelPreviewViewport.PollProbeBake): не поднялся GPU -
			// пробы сцены стоят, консоль объяснила почему.
		}

		/// <summary>Снимок поля сессии в GPU-атласы: создаются один раз на сетку и обновляются НА
		/// МЕСТЕ (см. ProbeGiTextures.Update); смена сетки пересоздаёт их за GPU-барьером.</summary>
		private void UploadProbeSnapshot()
		{
			var session = _probeSession;
			if (session == null || _probeBaker == null)
			{
				return;
			}

			try
			{
				var snapshot = _probeBaker.Snapshot(session);

				if (_probeTextures != null && !_probeTextures.Matches(snapshot))
				{
					_env.DilApi.ImmediateContext.Flush();
					_env.DilApi.ImmediateContext.WaitForIdle();
					_probeTextures.Release();
					_probeTextures = null;
				}

				if (_probeTextures == null)
				{
					_probeTextures = new ProbeGiTextures(_graphicsApi, snapshot,
						$"_sceneProbeGi{_probeTextureGeneration++}");
					BindProbeTextures();
					ApplyMaterialSettings();
				}
				else
				{
					_probeTextures.Update(snapshot);
				}
			}
			catch (Exception ex)
			{
				_probeTextures = null;
				EngineLog.Add(LogLevel.Error, $"Scene probe GI: failed to upload atlases: {ex.Message}");
			}
		}

		/// <summary>Привязывает атласы проб к материалам ВСЕХ загруженных моделей (в отличие от
		/// превью с одной резидентной) - см. ProbeGiTextures.Bind.</summary>
		private void BindProbeTextures()
		{
			if (_probeTextures == null)
			{
				return;
			}

			foreach (var state in _models.Values)
			{
				if (state.Model != null)
				{
					_probeTextures.Bind(state.Model);
				}
			}
		}

		/// <summary>Сброс probe-GI (смена сцены/пересоздание окружения/выключение галочки). Звать ЗА
		/// GPU-барьером - освобождает атласы. Недосчитавшийся фоновый раунд осиротевает вместе со
		/// своей сессией: чистый CPU-таск, его результат просто некому забрать.</summary>
		/// <summary>Дожидается фоновой сборки BVH. ОБЯЗАТЕЛЬНО перед освобождением любой модели
		/// сцены: задача читает CPU-копии вершин в неуправляемой памяти, и Release из-под неё - это
		/// обращение к освобождённой памяти.</summary>
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
				// Причина уже будет доложена из PollProbeBake - здесь нужен только факт завершения.
			}

			_probeBakerTask = null;
			_probeBakerModels = null;
			_probeBakerRecords = null;
		}

		private void ResetProbeGi()
		{
			// Дерево уходит вместе с бейкером - снимок поз, под который оно собрано, тоже
			// недействителен (иначе следующая сборка сочла бы сцену неизменной и не состоялась).
			_probeBakerBuiltFor = null;
			ResetProbeGiCore();
		}

		private void ResetProbeGiCore()
		{
			// До всего: фоновая сборка BVH ещё может читать геометрию моделей, которые вызывающий
			// вот-вот освободит.
			WaitProbeBakerTask();

			// Оверлей первым (держит атласы в замороженных командах), затем GPU-объект.
			ReleaseSceneProbeDebugOverlay();
			ReleaseSceneProbeGpu();
			_sceneTlasDirty = false;
			_probeSceneRecords.Clear();

			_probeBaker = null;
			_probeSession = null;
			_probeRoundTask = null;
			_probeSessionDelay = -1f;

			if (_probeTextures != null)
			{
				_probeTextures.Release();
				_probeTextures = null;
			}
		}

		// --- Баунды/тени/пост-процесс ---------------------------------------------------------------

		/// <summary>AABB всей env-сцены по сферам мешей инстансов - питает кадрирование камеры и
		/// ортокамеру мирового света.</summary>
		private bool TryComputeSceneBounds(out Vector3 min, out Vector3 max)
		{
			min = new Vector3(float.PositiveInfinity);
			max = new Vector3(float.NegativeInfinity);
			bool any = false;

			foreach (var record in _rendered.Values)
			{
				any |= AccumulateRecordBounds(record, ref min, ref max);
			}

			return FinalizeBounds(any, ref min, ref max);
		}

		/// <summary>AABB одной сущности префаба И её поддерева - фокус по F на выделении (см.
		/// SceneCamera.Frame / FrameSelection). Сущности без своей записи в _rendered (света, группы,
		/// пустышки) в баунды геометрии не попадают - если во всём поддереве не нашлось НИ ОДНОЙ
		/// модели, фолбэком идёт мировая позиция сущности с условным радиусом (иначе F на пустышке
		/// был бы неотличим от щелчка мимо и просто ничего не делал бы).</summary>
		private bool TryComputeEntityBounds(Entity entity, out Vector3 min, out Vector3 max)
		{
			min = new Vector3(float.PositiveInfinity);
			max = new Vector3(float.NegativeInfinity);
			bool any = AccumulateEntityBounds(entity, ref min, ref max);

			if (!any)
			{
				const float fallbackRadius = 0.5f;
				var center = ComputeWorldMatrix(entity).Translation;
				min = center - new Vector3(fallbackRadius);
				max = center + new Vector3(fallbackRadius);
				any = true;
			}

			return FinalizeBounds(any, ref min, ref max);
		}

		private bool AccumulateEntityBounds(Entity entity, ref Vector3 min, ref Vector3 max)
		{
			bool any = _rendered.TryGetValue(entity.Id, out var record) &&
				AccumulateRecordBounds(record, ref min, ref max);

			foreach (var child in entity.ChildEntities)
			{
				any |= AccumulateEntityBounds(child, ref min, ref max);
			}

			return any;
		}

		/// <summary>Общий накопитель AABB одной записи _rendered - сферы мешей её инстансов в мировом
		/// пространстве; общий код TryComputeSceneBounds и TryComputeEntityBounds.</summary>
		private bool AccumulateRecordBounds(RenderedModel record, ref Vector3 min, ref Vector3 max)
		{
			if (!record.Instantiated || string.IsNullOrEmpty(record.ResolvedPath) ||
				!_models.TryGetValue(record.ResolvedPath, out var state) || state.Model == null)
			{
				return false;
			}

			var model = state.Model;
			bool any = false;
			for (int i = 0; i < record.EnvEntities.Count; i++)
			{
				var instance = model.instances[record.InstanceIndices[i]];
				if (instance.meshId < 0 || instance.meshId >= model.Meshes.Count)
				{
					continue;
				}

				var mesh = model.Meshes[instance.meshId];
				var t = ComposeInstanceTransform(instance.transform, record.LastWorld);
				var worldCenter = Vector3.Transform(mesh.Center * t.scale, t.rotation) + t.position;
				var maxScale = MathF.Max(MathF.Abs(t.scale.X), MathF.Max(MathF.Abs(t.scale.Y), MathF.Abs(t.scale.Z)));
				var radius = mesh.Radius * maxScale;

				min = Vector3.Min(min, worldCenter - new Vector3(radius));
				max = Vector3.Max(max, worldCenter + new Vector3(radius));
				any = true;
			}

			return any;
		}

		/// <summary>NaN/Infinity в трансформах не должны ронять кадрирование - общий хвост
		/// TryComputeSceneBounds/TryComputeEntityBounds.</summary>
		private static bool FinalizeBounds(bool any, ref Vector3 min, ref Vector3 max)
		{
			if (!any)
			{
				return false;
			}

			if (float.IsNaN(min.X) || float.IsInfinity(min.X) || float.IsNaN(max.X) || float.IsInfinity(max.X) ||
				float.IsNaN(min.Y) || float.IsInfinity(min.Y) || float.IsNaN(max.Y) || float.IsInfinity(max.Y) ||
				float.IsNaN(min.Z) || float.IsInfinity(min.Z) || float.IsNaN(max.Z) || float.IsInfinity(max.Z))
			{
				return false;
			}

			return true;
		}

		/// <summary>Баунды каскада теней мирового света - пересчитываются при любом изменении сцены
		/// (см. SimpleCullingAndRenderSystem.BuildLightData).</summary>
		private void UpdateShadowBounds()
		{
			if (_env.ShadowSettings == null || !TryComputeSceneBounds(out var min, out var max))
			{
				return;
			}

			_env.ShadowSettings.BoundsCenter = (min + max) * 0.5f;
			_env.ShadowSettings.BoundsRadius = MathF.Max(0.05f, (max - min).Length() * 0.5f);
		}

		/// <summary>Мировые радиусы AO/SSGI от габаритов сцены (та же логика, что у
		/// ModelPreviewViewport.AoWorldRange/GiWorldRange). Звать только после GPU-барьера.</summary>
		private void PushPostProcessRanges()
		{
			float radius = 0f;
			if (TryComputeSceneBounds(out var min, out var max))
			{
				radius = MathF.Max(0.05f, (max - min).Length() * 0.5f);
			}

			var aoWorld = _editorSettings.AoRadiusWorld;
			var aoRange = aoWorld > 0f
				? Math.Clamp(aoWorld, 0.01f, 1000f)
				: radius * Math.Clamp(_editorSettings.AoRadiusFraction, 0.01f, 1f);
			if (aoRange > 0f)
			{
				_env.SetAoWorldRange(aoRange);
			}

			var giWorld = _editorSettings.SsgiRadiusWorld;
			var giRange = giWorld > 0f
				? Math.Clamp(giWorld, 0.01f, 1000f)
				: radius * Math.Clamp(_editorSettings.SsgiRadiusFraction, 0.01f, 2f);
			if (giRange > 0f)
			{
				_env.SetGiWorldRange(giRange);
			}
		}

		// --- Настройки графики/шейдинга -------------------------------------------------------------

		/// <summary>Live-биты настроек графики из окна Settings - зеркало (упрощённое)
		/// ModelPreviewViewport.ApplyGraphicsSettings: биты фич, рантайм-тумблер теней, ручки AO/SSGI.</summary>
		/// <summary>Пуш живых ручек тумана - зеркало ModelPreviewViewport.ApplyFogSettings. Направление
		/// солнца сюда НЕ входит: оно пушится покадрово вместе с базисом камеры (см.
		/// ModelViewportEnvironment.SetCameraTransform) - в сцене солнце вращают гизмо, а то не
		/// поднимает событие настроек.</summary>
		/// <summary>Пуш живых ручек блума - зеркало ModelPreviewViewport.ApplyBloomSettings.</summary>
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

		/// <summary>Пуш живых ручек объёмного света - зеркало ModelPreviewViewport.ApplyVolumetricSettings.
		/// Направление солнца сюда не входит по той же причине, что и у тумана: в сцене солнце
		/// вращают гизмо, а те не поднимают событие настроек.</summary>
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

			_env.SetVolumetricPunctualScatter(Math.Max(_editorSettings.VolumetricPunctualScatter, 0f));
		}

		private void ApplyGraphicsSettings()
		{
			// Радиусы AO/SSGI - живьём, как в превью (ModelPreviewViewport.ApplyGiSettings): раньше
			// они пушились только из ветки структурного изменения сцены, и ползунки «AO/GI radius»
			// в Scene View не делали ничего до первого движения объекта.
			PushPostProcessRanges();

			// Стриминг - живая ручка: радиус читается стримером на каждом Tick, пересоздавать ничего
			// не нужно. Выключенный стриминг = бесконечный радиус, то есть все модели сцены остаются
			// резидентными и никто ничего не отпускает (см. EditorSettings.SceneStreaming).
			_streamer.StreamRadius = _editorSettings.SceneStreaming
				? MathF.Max(1f, _editorSettings.SceneStreamingRadius)
				: float.PositiveInfinity;

			// Скиннинг читается в момент ИНСТАНЦИРОВАНИЯ модели, поэтому здесь только пушим значение;
			// уже показанные модели останутся как есть до переоткрытия префаба (см. EditorSettings).
			// Переменная окружения DECA_SKINNING=0 при этом остаётся сильнее настройки: она нужна
			// как аварийный путь, когда редактор не доживает до окна Graphics.
			if (System.Environment.GetEnvironmentVariable("DECA_SKINNING") != "0")
			{
				ModelViewportGeometry.SkinningEnabled = _editorSettings.SceneSkinning;
			}

			// UAV на мега-буфере вершин - только когда скиннинг включён: иначе его выключение не
			// возвращало бы описание буфера к исходному, и «выключить и проверить» переставало быть
			// достоверной проверкой.
			DiligentBatchRenderer.SkinningUav = ModelViewportGeometry.SkinningEnabled;

			ApplyFogSettings();
			ApplyVolumetricSettings();
			ApplyBloomSettings();
			ApplyColorGradeSettings();
			_env.SetToneCurve(_editorSettings.ToneCurve);

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

			// От РЕАЛЬНО созданного окружения, а не от настроек: HDR - рестарт-левел, и до
			// пересоздания шейдер обязан продолжать писать display-space.
			if (_env.HdrOutput)
			{
				flags |= PreviewFeatureFlags.HdrOutput;
			}
			_featureFlags = flags;

			if (_env.ShadowSettings != null)
			{
				_env.ShadowSettings.Enabled = _editorSettings.PreviewShadows;
			}

			// Ручки авто-экспозиции - живьём (сам HDR-тумблер - рестарт-левел, см. SetHdrEnabled);
			// no-op без HDR. Границы яркости держим упорядоченными - перевёрнутый диапазон намертво
			// фиксирует экспозицию (см. ModelPreviewViewport.ApplyGraphicsSettings).
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

			_env.SetAoStrength(Math.Clamp(_editorSettings.AoStrength, 0.1f, 4f),
				Math.Clamp(_editorSettings.AoFloor, 0f, 1f));
			_env.SetAoDebugView(_editorSettings.AoDebugView);

			// Отладочный вид векторов движения - живая ручка кбуфера, граф не пересобирает
			// (см. MotionVectorDebugPassResources).
			_env.SetMotionVectorDebug(_editorSettings.MotionVectorDebugView,
				Math.Clamp(_editorSettings.MotionVectorDebugRange, 0.25f, 256f));
			_env.SetTemporalJitter(_editorSettings.TemporalJitter);

			// Бэкенд апскейлера - ОТЛОЖЕННО, в начале Update: смена ждёт GPU и пишет init-команды
			// NGX, посреди ImGui-кадра это роняло редактор (см. ModelPreviewViewport).
			_pendingUpscalerApply = true;

			// Масштаб рендера здесь НЕ применяется - только в TrackAndApplyResize: применение
			// настроек срабатывает посреди ImGui-кадра, и синхронный ResizeTargets отсюда ломал
			// кадр (биндинг превью уже в draw list-е) - см. ModelPreviewViewport.TrackAndApplyResize.

			_env.SetGiParams(
				Math.Clamp(_editorSettings.SsgiIntensity, 0f, 4f),
				Math.Clamp(_editorSettings.SsgiSamples, 4, SsgiPassResources.MaxSampleCount),
				Math.Max(0f, _editorSettings.SsgiMaxLuminance),
				Math.Clamp(_editorSettings.SsgiSaturation, 0f, 1f));
			_env.SetGiCompositeParams(
				Math.Clamp(_editorSettings.SsgiBlurRadius, 0, SsgiPassResources.MaxBlurRadius),
				_editorSettings.SsgiDebugView);

			PushSsrSettings();

			ApplyMaterialSettings();

			// Рантайм-тумблер теней меняет ЧИСЛО записей каскадов в данных ShadowPass, а его цикл
			// заморожен с командами графа - пересборка обязательна (дёшево и происходит только по
			// "OK" настроек/пересозданию окружения).
			_env.Pipeline.InvalidateGraph();
		}

		/// <summary>Зеркало ModelPreviewViewport.ApplyLightRotation: направление света/теней,
		/// поворот фонового неба и IBL-отражений (яв уходит в кбуфер материалов).</summary>
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
			ApplyMaterialSettings();
		}

		/// <summary>Живые ручки SSR из настроек. Отдельным методом, потому что зовётся из ДВУХ мест:
		/// применения настроек и <see cref="ApplyPipelineFeatures"/> - смена RT-фолбэка пересоздаёт
		/// SSR-ресурсы (в трейс запечён вариант шейдера), и без повторного пуша ручки откатывались бы
		/// в дефолты.</summary>
		private void PushSsrSettings()
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

		/// <summary>Покадровые данные SSR: поворот env-карты (композит вычитает ровно тот env-цвет,
		/// что вложил форвард) и солнце RT-фолбэка. Цвет солнца - константа дневного света: точный
		/// вклад ключа в RT-хиты не воспроизвести без полного шейдинга, а для отражений вне экрана
		/// достаточно правдоподобной яркости.</summary>
		private void PushSsrEnvironment()
		{
			var shadowSettings = _env.ShadowSettings;
			if (shadowSettings == null)
			{
				return;
			}

			// Цвет ключа - тот же, что у прямого света превью (SimpleCullingAndRenderSystem,
			// LightColor (1, 0.97, 0.9) x intensity 1); ambient-вес - ambientLevel форвард-пасса
			// при мировом свете (0.55, см. UnlitInstancedPS) - RT-хиты и экранные пиксели теперь
			// освещаются одной и той же моделью.
			// Угловой размер солнца (та же ручка, что у PCSS прямого вида) - мягкость края тени
			// у RT-хитов: без неё теневой луч бинарный, и в отражении край тени рвался в чёрное.
			float sunTanHalfAngle = MathF.Tan(
				Math.Clamp(_editorSettings.SunAngularSize, 0.01f, 20f) * 0.5f * MathF.PI / 180f);

			_env.SetSsrEnvironment(shadowSettings.EnvYawRadians,
				-Vector3.Normalize(shadowSettings.LightDirection),
				new Vector3(1f, 0.97f, 0.9f), 0.55f, sunTanHalfAngle);
		}

		/// <summary>Пушит режим шейдинга + PBR-факторы в кбуфер PreviewSettings каждого материала
		/// всех загруженных моделей - усечённое зеркало
		/// ModelPreviewViewport.ApplyPreviewSettingsToMaterials (без probe-GI атласов и HDR).</summary>
		private void ApplyMaterialSettings()
		{
			int mode = _shading switch
			{
				ShadingMode.Textured => 0,
				ShadingMode.Normal => 2,
				ShadingMode.Uv => 2,
				ShadingMode.Tangent => 2,
				_ => 3,
			};
			int channel = _shading switch
			{
				ShadingMode.Uv => 1,
				ShadingMode.Tangent => 2,
				// PunctualShadowDebug требует Mode == 3 (см. mode switch выше: попадает в default => 3)
				// и Channel == 11 - тот же канал, что читает DECA_PROBE_PUNCTUALDEBUG в PreviewProbe.cs.
				ShadingMode.PunctualShadowDebug => PunctualDebugChannel,
				// Кластерные виды - тоже поверх Mode == 3 (default выше), каналы фиксированные.
				ShadingMode.ClusterDepthSlices => 20,
				ShadingMode.ClusterScreenTiles => 21,
				ShadingMode.ClusterLightCount => 14,
				// Проецируемая глубина света на поверхность - тоже поверх Mode == 3.
				ShadingMode.LightDepthReceiver => 22,
				ShadingMode.LightDepthOccluder => 23,
				ShadingMode.LightDepthGap => 24,
				// Каскадные тени солнца - тоже поверх Mode == 3.
				ShadingMode.SunShadowCascades => 28,
				_ => 0,
			};

			// Отладочные виды probe-GI живут не в комбо шейдинга, а галками в окне Graphics - ровно
			// как в превью (см. ModelPreviewViewport.ApplyPreviewSettingsToMaterials). В Scene View
			// они не читались вовсе: галка ставилась, картинка не менялась.
			//
			// Расстановка проб (канал 10) старше вида поля (канал 9): попросили оба - показываем
			// более частный, где пробы стоят. Комбо шейдинга старше обоих: если выбран явный
			// диагностический вид, он и остаётся - его выбрали руками только что.
			if (channel == 0)
			{
				if (_editorSettings.ProbeGiDebugProbes)
				{
					channel = 10;
				}
				else if (_editorSettings.ProbeGiDebugView)
				{
					channel = 9;
				}
			}

			// Отладочные виды (Textured/каналы) пишут в кадр уже отображаемые значения - HDR-конвейер
			// обязан прокинуть их мимо экспозиции и кривой (no-op без HDR, см.
			// ModelViewportEnvironment.SetTonemapPassthrough). Условие именно по каналу, а не только
			// по mode: диагностические каналы (11..21) живут ПОВЕРХ mode == 3, и без этого их палитра
			// уезжала через авто-экспозицию - у кластерных видов цвет и есть всё содержание картинки
			// (номер среза кодируется яркостью восьмёрки), тонемап делал соседние срезы неразличимыми.
			// AoDebugView сюда входит по той же причине, что и каналы: он тоже пишет в кадр уже
			// отображаемые значения, но идёт мимо PreviewSettings (его читает сам AO-пасс), поэтому
			// условие по mode/channel его не ловило - в Scene View отладочный вид AO уезжал через
			// авто-экспозицию. В превью он в этом условии есть.
			_env.SetTonemapPassthrough(mode != 3 || channel != 0 || _editorSettings.AoDebugView);

			foreach (var state in _models.Values)
			{
				var model = state.Model;
				if (model == null)
				{
					continue;
				}

				var data = new PreviewSettingsData
				{
					// Кривая действует только в LDR - в HDR её применяет TonemapPass (см. Tonemap.hlsl).
					ToneCurve = _editorSettings.ToneCurve,
					Mode = mode,
					Channel = channel,
					EnvYawRadians = _env.ShadowSettings?.EnvYawRadians ?? 0f,
					ShadowMode = _editorSettings.ShadowFilterMode,
					// Live-ручки солнца/эмбиента шейдер читает и без probe-GI (ProbeGiParams.z =
					// интенсивность солнца) - значения те же, что пушит превью.
					ProbeGiParams = new Vector4(
						Math.Clamp(_editorSettings.ProbeGiShadowFloor, 0f, 1f),
						Math.Clamp(_editorSettings.ProbeGiSpecularFloor, 0f, 1f),
						Math.Clamp(_editorSettings.ProbeGiSunIntensity, 0.1f, 16f),
						Math.Clamp(_editorSettings.ProbeGiAmbientBoost, 0.1f, 128f)),
					// y - сторона окто-карты видимости (см. ProbeGiBakeResult.VisRes): по ней шейдер
					// раскладывает тайл пробы в атласе, разойтись с сессией нельзя.
					ProbeGiParams2 = new Vector4(
						Math.Clamp(_editorSettings.ProbeGiSkyShadowFloor, 0.01f, 1f),
						ProbeGiBakeResult.VisRes, 0f, 0f),
				};

				// Сетка проб сцены (Origin.w = 1 - тумблер в шейдере; нули = выключено). Атласы уже
				// привязаны в BindProbeTextures; бейас - от минимального шага сетки, тот же расчёт,
				// что у превью (см. ModelPreviewViewport.ApplyPreviewSettingsToMaterials).
				if (_probeTextures != null && ProbesEnabled)
				{
					ProbeGiViewportShared.PushGrid(ref data, _probeTextures,
						_editorSettings.ProbeGiNormalBias, _editorSettings.ProbeGiViewBias);
				}

				for (int i = 0; i < model.materialObjects.Count; i++)
				{
					var kvp = model.materialObjects.GetAt(i);

					if (!model.MaterialPbr.TryGetValue(kvp.Key, out var pbr))
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
		}

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

		internal static Matrix4x4 ComputeWorldMatrix(Entity entity)
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
