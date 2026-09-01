using System.Text.Json;
using System.Text.Json.Serialization;
using DecaEngine.Core;
using DecaEngine.Core.Assets;
using DecaEngine.Graphics.ProbeGi;
using DecaEngine.Graphics;

namespace DecaEngine.Editor;

/// <summary>
/// ????????????? ????????? ?????? ????????? (? ??????? ?? <see cref="ProjectConfig"/>,
/// ??????? ?????? ???????????? ??????????? ???????). ???????? ?
/// %AppData%/DecaEngine/editor_settings.json, ?????????? <see cref="RecentProjectsManager"/>.
/// ????????????? ? <see cref="SettingsWindow"/> ? ???????? "General" ? "Editor".
/// </summary>
public class EditorSettings
{
	private static readonly string FilePath = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
		"DecaEngine",
		"editor_settings.json");

	/// <summary>?????????? ???????? ???????? ??????????, ? ?????????, ? ????? 25%.</summary>
	public static readonly int[] AllowedUiScalePercents = { 50, 75, 100, 125, 150 };

	// --- General ---

	/// <summary>????????????? ????????? ????????? ???????? ?????? ??? ?????? ?????????.</summary>
	public bool AutoLoadLastProject { get; set; } = false;

	// --- Editor ---

	/// <summary>??????? ?????????? ????????? ? ?????????: 50, 75, 100, 125 ??? 150.</summary>
	public int UiScalePercent { get; set; } = 100;

	public float UiScaleMultiplier => UiScalePercent / 100f;

	// --- Appearance ---

	/// <summary>??????? ??????? ????????? (??. <see cref="EditorPalette"/>).</summary>
	public SerializableColor AccentColor { get; set; } = new(EditorPalette.DefaultAccent);
	public SerializableColor BackgroundColor { get; set; } = new(EditorPalette.DefaultBackground);
	public SerializableColor SurfaceColor { get; set; } = new(EditorPalette.DefaultSurface);
	public SerializableColor TextColor { get; set; } = new(EditorPalette.DefaultText);
	/// <summary>????????? ???? ????????? (??. <see cref="EditorPalette.Selection"/>) - ?????? ?????????
	/// ?????? ????????? ? Accent, ?????? ????????????? ??????????.</summary>
	public SerializableColor SelectionColor { get; set; } = new(EditorPalette.DefaultSelection);
	/// <summary>????????? ????????? ???? ?????? Asset Browser (??. <see cref="EditorPalette.IconAccent"/>) -
	/// ?????? ???? ?????? ?????? ???????? ?? ??????? ? Accent, ?????? ????????????? ??????????,
	/// ??? ? Selection.</summary>
	public SerializableColor IconAccentColor { get; set; } = new(EditorPalette.DefaultIconAccent);

	// --- Graphics Pipeline ---

	/// <summary>Вертикальная синхронизация окна редактора (см. IGraphicsApi.PresentInterval:
	/// true = 1, false = 0). Применяется живьём из окна Graphics; переменная окружения DECA_VSYNC
	/// при старте старше сохранённого значения (см. GraphicsSettingsWindow.DrawDisplaySection).</summary>
	public bool VSync { get; set; } = true;

	/// <summary>Vertex shader used by ModelLoader to render loaded glTF models, relative to "EditorAssets/".</summary>
	public EditorRef DefaultVertexShader { get; set; } = new("shader/UnlitInstancedVS.hlsl");

	/// <summary>Pixel shader used by ModelLoader to render loaded glTF models, relative to "EditorAssets/".</summary>
	public EditorRef DefaultPixelShader { get; set; } = new("shader/UnlitInstancedPS.hlsl");

	/// <summary>Equirect .hdr для IBL-окружения превью моделей (абсолютный путь или относительно
	/// "EditorAssets/"). Пусто = процедурное небо. Применяется при старте редактора (энвайронмент
	/// создаётся вместе с превью-вьюпортом).</summary>
	public string PreviewEnvironmentHdr { get; set; } = "";

	// --- Физика сцены и отладка анимации (см. AnimationPhysicsDebugWindow) ---

	/// <summary>Разрешена ли физика в сцене префаба вообще. Сам мир всё равно заводится ЛЕНИВО -
	/// только под персонажа с foot IK/рэгдоллом или под включённый дебаг физики (см.
	/// PrefabSceneViewport.ScenePhysicsWanted); этот тумблер - способ выключить её насовсем, когда
	/// сцена тяжёлая, а физика в ней не нужна.</summary>
	public bool ScenePhysicsEnabled { get; set; } = true;

	/// <summary>Ускорение свободного падения по Y в единицах мира. Настраивается, потому что масштаб
	/// моделей в движке произволен: у метрового персонажа -9.81 - это метры в секунду за секунду, а
	/// у модели с габаритом в сотни единиц то же число выглядит как падение на Луне.</summary>
	public float SceneGravity { get; set; } = -9.81f;

	/// <summary>Пауза симуляции - НЕ нулевой шаг: пауза останавливает мир целиком, чтобы можно было
	/// разглядеть позу тела, которое только что провалилось сквозь пол.</summary>
	public bool ScenePhysicsPaused { get; set; } = false;

	/// <summary>Замедление/ускорение времени симуляции. Рэгдолл, разлетающийся за три кадра, на 0.1
	/// разбирается по кадрам.</summary>
	public float ScenePhysicsTimeScale { get; set; } = 1f;

	/// <summary>Множитель яркости дебаг-линий. Линии рисуются в HDR-таргет ДО тонемапа, экспозиция
	/// которого заранее неизвестна, - см. DebugLineOverlay.Intensity.</summary>
	public float DebugLineIntensity { get; set; } = 4f;

	public AnimationDebugOptions AnimationDebug { get; set; } = new();

	public PhysicsDebugOptions PhysicsDebug { get; set; } = new();

	// --- Preview Graphics (см. SettingsWindow.DrawGraphicsSection) ---

	/// <summary>Нормал-мапы в Lighting-превью (live-тумблер, бит PreviewFeatureFlags.NormalMaps).</summary>
	public bool PreviewNormalMaps { get; set; } = true;

	/// <summary>Запечённый AO (occlusionTexture) в Lighting-превью (live-тумблер).</summary>
	public bool PreviewBakedOcclusion { get; set; } = true;

	/// <summary>Тени мирового света в превью (live-тумблер: выключение убирает и теневой каскад,
	/// и сэмплинг - свет откатывается на камерный риг).</summary>
	public bool PreviewShadows { get; set; } = true;

	/// <summary>SSAO-пасс превью. Фича конвейера: ресурсы пасса заводятся на живом окружении через
	/// GraphicsPipelineSimple.SetFeatures, модель не перечитывается.</summary>
	public bool PreviewSsao { get; set; } = true;

	/// <summary>Техника AO-пасса превью при включённом <see cref="PreviewSsao"/>: классический SSAO
	/// или GTAO (см. <see cref="AmbientOcclusionMode"/>). Как и сам тумблер - фича живого
	/// конвейера. Строкой в json - чтобы settings-файл читался глазами.</summary>
	[JsonConverter(typeof(JsonStringEnumConverter))]
	public AmbientOcclusionMode PreviewAoMode { get; set; } = AmbientOcclusionMode.Ssao;

	/// <summary>SSGI-пасс превью (экранная глобальная иллюминация: один отскок света из кадра,
	/// color bleeding - см. SsgiCommon.hlsl). Фича живого конвейера, как SSAO.</summary>
	public bool PreviewSsgi { get; set; } = true;

	/// <summary>Кривая тонмапа (см. Tonemap.hlsl): 0 - PBR Neutral, 1 - ACES, 2 - AgX.
	///
	/// PBR Neutral - эталонная кривая glTF Sample Viewer: ниже ~0.76 она тождественна, то есть
	/// НАРОЧНО не добавляет ни контраста, ни глубины теней. Это правильно для оценки материала и
	/// ровно поэтому кадр с ней читается плоским. ACES и AgX - фильмические, со своим носком и
	/// плечом; AgX при этом не уводит оттенок насыщенных ярких цветов, ACES уводит.</summary>
	public int ToneCurve { get; set; } = 0;

	// --- Векторы движения (см. MotionVectorPass) ------------------------------------------------

	/// <summary>Считать экранные векторы движения - вход будущих временнЫх техник (апскейлеры
	/// DLSS/FSR, TAA). Сам кадр не меняет: пасс лишь заполняет свой RG16F-буфер, и пока его никто не
	/// читает, галка стоит одного фуллскрин-дроу. Фича живого конвейера, заводится по SetFeatures.
	///
	/// (историческая оговорка про MSAA снята: мультисемплинга в конвейере больше нет, а он при
	/// мультисемплинге остаётся неразрешённым (см. PipelineFeatures.MotionVectors). Окно Graphics
	/// показывает это через ModelViewportEnvironment.MotionVectorsAvailable.</summary>
	public bool PreviewMotionVectors { get; set; } = false;

	/// <summary>Отладочный вид векторов: кадр замещается их раскраской (R - смещение по X, G - по Y,
	/// ровный серый - ноль). Live, аналог <see cref="AoDebugView"/>. Единственный способ увидеть
	/// результат <see cref="PreviewMotionVectors"/>, пока апскейлера нет.</summary>
	public bool MotionVectorDebugView { get; set; } = false;

	/// <summary>Смещение в ПИКСЕЛЯХ, на котором шкала отладочного вида упирается в край; за ним
	/// шейдер гасит синий канал, и перегруженная область становится ядовито-жёлтой. Live.</summary>
	public float MotionVectorDebugRange { get; set; } = MotionVectorDebugPassResources.DefaultRangePixels;

	/// <summary>Суб-пиксельный джиттер проекции (Halton 2/3, 16 фаз) - вторая половина входа
	/// темпорального апскейлера после векторов. Live-ручка конвейера, граф не пересобирает.
	///
	/// БЕЗ потребителя (TAA/апскейлера) картинка от него ЗАМЕТНО дрожит - это не баг, а сырой вход
	/// техники, которой ещё нет; потому и выключен по умолчанию.</summary>
	public bool TemporalJitter { get; set; } = false;

	/// <summary>Масштаб рендера сцены (0.25..1): геометрия и вся сценовая пост-обработка рисуются в
	/// уменьшенные таргеты, до отображаемого разрешения кадр поднимает тонемап (пока билинейно - на
	/// это место встанет апскейлер). Применяется живьём через ресайз-путь вьюпорта; 1 = выключено.</summary>
	public float RenderScale { get; set; } = 1f;

	/// <summary>Темпоральный апскейл (TAAU, см. TemporalUpscalePass): аккумулирует display-кадр из
	/// рендер-разрешения по джиттеру и векторам движения, джиттер включает сам. Требует галки
	/// векторов; при её снятии молча гаснет. Фича живого конвейера, заводится по SetFeatures.</summary>
	public bool TemporalUpscale { get; set; } = false;

	/// <summary>Бэкенд слота апскейлера: 0 - встроенный TAAU, 1 - нативный FSR (ffx-api, требует
	/// amd_fidelityfx_upscaler_dx12.dll), 2 - нативный DLSS (NGX, требует nvngx_dlss.dll и
	/// NVIDIA RTX). Обоим нужен DecaFfxShim.dll рядом с экзешником и D3D12; при недоступности
	/// молча работает TAAU - окно Graphics это показывает. Live-ручка.</summary>
	public int UpscalerBackend { get; set; } = 0;

	/// <summary>Вес текущего кадра в аккумуляторе TAAU (см. TemporalUpscalePassResources.SetBlendAlpha):
	/// меньше - стабильнее и резче на статике, больше - отзывчивее к движению. Live.</summary>
	public float TaauBlendAlpha { get; set; } = TemporalUpscalePassResources.DefaultBlendAlpha;

	/// <summary>Сила встроенного шарпена FSR (RCAS): 0 - выключен, 0..1. Live.</summary>
	public float FsrSharpness { get; set; } = 0f;

	/// <summary>Ветка провайдера FSR - индекс комбо: 0 - авто (новейший рабочий: FSR 4 &gt; FSR 2,
	/// минуя 3.x), 1 - принудительно FSR 2, 2 - принудительно FSR 3.1 (на текущем SDK выдаёт
	/// деградировавший кадр - оставлен для проверки глазами). Смена пересоздаёт контекст.</summary>
	public int FsrProvider { get; set; } = 0;

	/// <summary>Пресет качества DLSS - индекс комбо (0 - Performance, 1 - Balanced, 2 - Quality,
	/// 3 - DLAA), в значение NGX переводится вьюпортом. Смена пересоздаёт NGX-фичу (мгновенно,
	/// но с GPU-барьером).</summary>
	public int DlssQuality { get; set; } = 1;

	// --- Цветокоррекция и виньетка (см. ColorGradePass) -----------------------------------------

	/// <summary>Включает финальный пасс цветокоррекции. Фича живого конвейера: пасс владеет своей
	/// копией кадра и заводится по SetFeatures. Работает в ОБОИХ режимах (HDR и LDR).</summary>
	public bool PreviewColorGrade { get; set; } = false;

	/// <summary>Насыщенность: 1 - без изменений, 0 - серый кадр. Главная ручка против
	/// перенасыщенных материалов.</summary>
	public float GradeSaturation { get; set; } = ColorGradePassResources.DefaultSaturation;

	/// <summary>Контраст вокруг среднего тона: 1 - без изменений.</summary>
	public float GradeContrast { get; set; } = ColorGradePassResources.DefaultContrast;

	/// <summary>Гамма средних тонов: 1 - без изменений, больше - светлее середина.</summary>
	public float GradeGamma { get; set; } = ColorGradePassResources.DefaultGamma;

	/// <summary>Температура: -1 холоднее, +1 теплее. Нормирована по яркости, экспозицию не меняет.</summary>
	public float GradeTemperature { get; set; } = ColorGradePassResources.DefaultTemperature;

	/// <summary>Оттенок: -1 зелёный, +1 пурпурный.</summary>
	public float GradeTint { get; set; } = ColorGradePassResources.DefaultTint;

	/// <summary>Тонировка ТЕНЕЙ - аддитивная, нейтраль чёрный (поднимает именно чёрное).</summary>
	public float GradeShadowR { get; set; }
	public float GradeShadowG { get; set; }
	public float GradeShadowB { get; set; }

	/// <summary>Тонировка СВЕТОВ - мультипликативная, нейтраль белый.</summary>
	public float GradeHighlightR { get; set; } = 1f;
	public float GradeHighlightG { get; set; } = 1f;
	public float GradeHighlightB { get; set; } = 1f;

	/// <summary>Сила виньетки: 0 - нет.</summary>
	public float VignetteIntensity { get; set; } = ColorGradePassResources.DefaultVignetteIntensity;

	/// <summary>Радиус чистой зоны в долях полукадра.</summary>
	public float VignetteRadius { get; set; } = ColorGradePassResources.DefaultVignetteRadius;

	/// <summary>Мягкость края виньетки.</summary>
	public float VignetteSmoothness { get; set; } = ColorGradePassResources.DefaultVignetteSmoothness;

	/// <summary>Круглость: 1 - круг с учётом формата кадра, 0 - овал по всему кадру.</summary>
	public float VignetteRoundness { get; set; } = ColorGradePassResources.DefaultVignetteRoundness;

	// --- Блум (оптическое рассеяние, см. BloomPass) ---------------------------------------------

	/// <summary>Включает пасс блума. Фича живого конвейера: пасс владеет своей цепочкой таргетов,
	/// и она заводится по SetFeatures без пересоздания окружения.</summary>
	public bool PreviewBloom { get; set; } = false;

	/// <summary>Порог яркости в ОТОБРАЖАЕМЫХ единицах: 1.0 - светятся только настоящие пересветы
	/// (то, что дисплей уже не может показать ярче). Привязан к авто-экспозиции, поэтому не
	/// зависит от абсолютной яркости сцены.</summary>
	public float BloomThreshold { get; set; } = BloomPassResources.DefaultThreshold;

	/// <summary>Ширина мягкого колена вокруг порога - без него на градиенте видна ступенька.</summary>
	public float BloomKnee { get; set; } = BloomPassResources.DefaultKnee;

	/// <summary>Радиус тента апсэмпла в текселях: шире - мягче и дальше растекается ореол.</summary>
	public float BloomRadius { get; set; } = BloomPassResources.DefaultRadius;

	/// <summary>Общая интенсивность ореола.</summary>
	public float BloomIntensity { get; set; } = BloomPassResources.DefaultIntensity;

	// --- Атмосферный туман (воздушная перспектива, см. FogPass) -------------------------------

	/// <summary>Включает пасс тумана. Фича живого конвейера: пассу нужны депт и scene-copy, но они
	/// заводятся по SetFeatures - переключение не пересоздаёт окружение.</summary>
	public bool PreviewFog { get; set; } = false;

	/// <summary>Плотность среды на опорной высоте, 1/единица мира. Главная ручка эффекта.</summary>
	public float FogDensity { get; set; } = FogPassResources.DefaultDensity;

	/// <summary>Скорость спада плотности по высоте. 0 - однородная дымка без высотного профиля.</summary>
	public float FogHeightFalloff { get; set; } = FogPassResources.DefaultHeightFalloff;

	/// <summary>Высота, на которой плотность равна <see cref="FogDensity"/>, в единицах мира.</summary>
	public float FogHeightRef { get; set; } = FogPassResources.DefaultHeightRef;

	/// <summary>Дистанция, до которой тумана нет - иначе дымка садится на ближние предметы.</summary>
	public float FogStartDistance { get; set; } = FogPassResources.DefaultStartDistance;

	/// <summary>Предельная дальность тумана; её же получает небо (у фона глубины нет).</summary>
	public float FogMaxDistance { get; set; } = FogPassResources.DefaultMaxDistance;

	/// <summary>Потолок непрозрачности 0..1: меньше единицы - сквозь дымку всегда что-то видно.</summary>
	public float FogMaxOpacity { get; set; } = FogPassResources.DefaultMaxOpacity;

	/// <summary>Цвет среды (линейный RGB) - тень дымки.</summary>
	public float FogColorR { get; set; } = FogPassResources.DefaultColorR;
	public float FogColorG { get; set; } = FogPassResources.DefaultColorG;
	public float FogColorB { get; set; } = FogPassResources.DefaultColorB;

	/// <summary>Цвет подсветки среды солнцем (линейный RGB).</summary>
	public float FogSunColorR { get; set; } = FogPassResources.DefaultSunColorR;
	public float FogSunColorG { get; set; } = FogPassResources.DefaultSunColorG;
	public float FogSunColorB { get; set; } = FogPassResources.DefaultSunColorB;

	/// <summary>Сила солнечной подсветки 0..1 - ради неё туман и ставят: дымка перестаёт
	/// быть серой пеленой и начинает светиться со стороны источника.</summary>
	public float FogSunStrength { get; set; } = FogPassResources.DefaultSunStrength;

	/// <summary>Резкость солнечного пятна в дымке: малые - широкое свечение, большие - компактный ореол.</summary>
	public float FogSunSharpness { get; set; } = FogPassResources.DefaultSunSharpness;

	// --- Объёмный свет: god rays и объёмный туман (см. VolumetricLightPass) --------------------
	//
	// Отдельно от тумана выше не по недосмотру: тот считает воздушную перспективу аналитически и по
	// построению не знает о тенях, а световые столбы - это именно тени в среде. Пассы независимы,
	// включаются порознь и хорошо работают вместе (марш - за свечение, аналитика - за дальнюю дымку).

	/// <summary>Включает пасс объёмного света. Фича живого конвейера: пассу нужны депт, scene-copy
	/// и shadow map, но они заводятся по SetFeatures - переключение не пересоздаёт окружение.</summary>
	public bool PreviewVolumetric { get; set; } = false;

	/// <summary>Плотность среды на опорной высоте, 1/единица мира. Главная ручка эффекта.</summary>
	public float VolumetricDensity { get; set; } = VolumetricLightPassResources.DefaultDensity;

	/// <summary>Скорость спада плотности по высоте. 0 - однородная среда без высотного профиля.</summary>
	public float VolumetricHeightFalloff { get; set; } = VolumetricLightPassResources.DefaultHeightFalloff;

	/// <summary>Высота, на которой плотность равна <see cref="VolumetricDensity"/>, в единицах мира.</summary>
	public float VolumetricHeightRef { get; set; } = VolumetricLightPassResources.DefaultHeightRef;

	/// <summary>Дистанция, с которой начинается марш - у самой камеры среда даёт только шум.</summary>
	public float VolumetricStartDistance { get; set; } = VolumetricLightPassResources.DefaultStartDistance;

	/// <summary>Предельная дальность марша. Прямо определяет цену пасса и крупность шага.</summary>
	public float VolumetricMaxDistance { get; set; } = VolumetricLightPassResources.DefaultMaxDistance;

	/// <summary>Число шагов марша - ручка «качество против цены». На яркость эффекта не влияет
	/// (интегрирование аналитическое по отрезку), только на гладкость границ столбов.</summary>
	public int VolumetricSteps { get; set; } = VolumetricLightPassResources.DefaultSteps;

	/// <summary>Потолок непрозрачности среды 0..1: сколько марш вправе съесть от исходного кадра.</summary>
	public float VolumetricMaxOpacity { get; set; } = VolumetricLightPassResources.DefaultMaxOpacity;

	/// <summary>Насколько тень режет солнечное рассеяние: 1 - настоящие столбы, 0 - ровный туман.</summary>
	public float VolumetricShadowStrength { get; set; } = VolumetricLightPassResources.DefaultShadowStrength;

	/// <summary>Коэффициент рассеяния: во сколько раз плотность превращается в свет.</summary>
	public float VolumetricScattering { get; set; } = VolumetricLightPassResources.DefaultScattering;

	/// <summary>Коэффициент экстинкции: насколько среда гасит проходящий сквозь неё свет. Отдельно
	/// от рассеяния - так среду можно сделать светящейся, но почти прозрачной.</summary>
	public float VolumetricExtinction { get; set; } = VolumetricLightPassResources.DefaultExtinction;

	/// <summary>Анизотропия фазовой функции -1..1: >0 - рассеяние вперёд, столбы вспыхивают при
	/// взгляде против солнца (как в реальной дымке).</summary>
	public float VolumetricAnisotropy { get; set; } = VolumetricLightPassResources.DefaultAnisotropy;

	/// <summary>Цвет солнечного рассеяния (линейный RGB) - цвет самих столбов.</summary>
	public float VolumetricSunColorR { get; set; } = VolumetricLightPassResources.DefaultSunColorR;
	public float VolumetricSunColorG { get; set; } = VolumetricLightPassResources.DefaultSunColorG;
	public float VolumetricSunColorB { get; set; } = VolumetricLightPassResources.DefaultSunColorB;

	/// <summary>Сила солнечного рассеяния - главная ручка яркости god rays.</summary>
	public float VolumetricSunIntensity { get; set; } = VolumetricLightPassResources.DefaultSunIntensity;

	/// <summary>Цвет небесного рассеяния (линейный RGB) - свет среды В ТЕНИ, тени его не режут.</summary>
	public float VolumetricAmbientColorR { get; set; } = VolumetricLightPassResources.DefaultAmbientColorR;
	public float VolumetricAmbientColorG { get; set; } = VolumetricLightPassResources.DefaultAmbientColorG;
	public float VolumetricAmbientColorB { get; set; } = VolumetricLightPassResources.DefaultAmbientColorB;

	/// <summary>Сила небесного рассеяния: без него среда в тени чёрная, и столбы читаются как
	/// вырезанные ножницами.</summary>
	public float VolumetricAmbientIntensity { get; set; } = VolumetricLightPassResources.DefaultAmbientIntensity;

	/// <summary>Во сколько раз небесная доля рассеяния слабее в затенённом объёме. Без этого
	/// глушения эффект вырождается в молочную пелену по всему кадру - крытый интерьер светится
	/// наравне с залитым солнцем двором.</summary>
	public float VolumetricAmbientShadowFloor { get; set; } = VolumetricLightPassResources.DefaultAmbientShadowFloor;

	/// <summary>Множитель рассеяния punctual-светов средой (конусы спотов/ореолы ламп в дымке).
	/// 0 - среда видит только солнце и небо, 1 - физическая доля света ламп.</summary>
	public float VolumetricPunctualScatter { get; set; } = 1f;

	/// <summary>Видимый угловой ДИАМЕТР диска солнца, градусы - ширина полутени PCSS (см.
	/// LightComponent.SunAngularSize; сюда пишется солнце-сущность Scene View, см.
	/// PrefabSceneViewport.SyncSunEntity). Реальное солнце ~0.53, дефолт чуть крупнее - мягкость
	/// видна и на коротких тенях.</summary>
	public float SunAngularSize { get; set; } = 1f;

	/// <summary>Режим фильтрации теней (солнце И punctual-света) - значения ШЕЙДЕРНЫЕ
	/// (SHADOW_MODE_* в UnlitInstancedPS.hlsl): 0 = PCSS (дефолт, ноль обязан оставаться PCSS -
	/// сцены вне превью кбуфер не заполняют), 1 = Hard (1 тап), 2 = PCF 3x3, 3 = PCSS HQ
	/// (32 тапа). Порядок в комбо окна - по возрастанию цены, см. GraphicsSettingsWindow.</summary>
	public int ShadowFilterMode { get; set; } = 0;

	/// <summary>Потолок стороны текстуры при загрузке модели (см. ModelLoadOptions.MaxTextureSize).
	///
	/// Прямо определяет ПИКОВУЮ память загрузки, и не по чуть-чуть: загрузчик декодирует ВСЕ
	/// текстуры модели разом и только потом начинает заливать их на GPU (см. Parallel.For в
	/// ModelLoader.PrepareModel), так что в памяти одновременно лежит вся их несжатая RGBA-копия.
	/// При 2048 одна текстура - 16 МБ; на ассете уровня Intel Sponza с сотнями текстур это
	/// гигабайты, причём в Large Object Heap, который не уплотняется и собирается лениво - отсюда
	/// закоммиченные десятки гигабайт при куда меньшем живом наборе.
	///
	/// Каждое уменьшение вдвое режет пик ВЧЕТВЕРО. 1024 на превью визуально почти не отличается,
	/// 512 заметно мылит крупные планы. Ручка ПЕРЕЗАГРУЗКИ: потолок запечён в уже декодированных
	/// текстурах, поэтому применяется перечитыванием модели (кнопкой в окне Graphics).</summary>
	public int PreviewMaxTextureSize { get; set; } = 2048;

	/// <summary>
	/// Стриминг моделей сцены по расстоянию до камеры (см. <see cref="ModelStreamer"/>). Выключение
	/// НЕ отключает загрузку - оно делает все модели сцены постоянно резидентными: радиус уходит в
	/// бесконечность, и никто ничего не отпускает.
	///
	/// Ручка нужна не только для вкуса. Стриминг заводит и снимает регистрации мешей/материалов/
	/// батчей ПО ХОДУ кадров, и это единственный путь в редакторе, где набор батчей меняется вне
	/// первого кадра. Возможность его выключить разделяет «проблема в сцене» и «проблема в
	/// стриминге» одним тумблером, без пересборки.
	/// </summary>
	public bool SceneStreaming { get; set; } = true;

	/// <summary>
	/// GPU-скиннинг скиннед-моделей сцены. Выключение НЕ убирает модель - она рисуется в bind-позе
	/// обычным статическим путём: не заводятся ни отдельные инстансы в мега-буфере вершин, ни батчи
	/// под них, ни compute-проход.
	///
	/// Ручка ИНСТАНЦИРОВАНИЯ: скиннед-инстансы регистрируются в момент появления модели в сцене,
	/// поэтому переключение действует на следующие инстанцирования - уже показанную модель нужно
	/// переоткрыть (или заново открыть префаб).
	/// </summary>
	public bool SceneSkinning { get; set; } = true;

	/// <summary>Радиус стриминга в мировых единицах при включённом <see cref="SceneStreaming"/>:
	/// дальше него модель отпускается, ближе - берётся. Гистерезис выгрузки добавляет к нему запас
	/// (см. <see cref="ModelStreamer.StreamOutHysteresis"/>), чтобы модель не мигала на границе.</summary>
	public float SceneStreamingRadius { get; set; } = 200f;

	/// <summary>УСТАРЕЛО: MSAA выпилен из конвейера, поле осталось только чтобы старые json-настройки
	/// не падали при чтении. Значение игнорируется. Ручка была ПЕРЕЗАГРУЗКИ: число сэмплов пеклось в PSO всей
	/// геометрии, поэтому применяется пересозданием окружения (кнопкой в окне Graphics).</summary>
	public int PreviewMsaaSamples { get; set; } = 4;

	/// <summary>Скай-фон (энвайронмент как фон кадра). Фича живого конвейера.</summary>
	public bool PreviewSkyBackground { get; set; } = true;

	/// <summary>Анизотропная фильтрация текстур моделей (8x). Применяется при следующей загрузке
	/// модели в превью.</summary>
	public bool PreviewAnisotropicFiltering { get; set; } = true;

	// --- AO (SSAO/GTAO, см. SsaoPass.cs / GraphicsSettingsWindow) ---

	/// <summary>Сила AO: показатель контраста видимости у GTAO, множитель интенсивности у SSAO.
	/// Live (кбуфер AoConstants).</summary>
	public float AoStrength { get; set; } = 1.5f;

	/// <summary>Нижний предел видимости AO: экранная оценка не вправе гасить свет в ноль.
	/// Live.</summary>
	public float AoFloor { get; set; } = 0.12f;

	/// <summary>Мировой радиус AO в долях габаритного радиуса модели (см.
	/// ModelViewportEnvironment.AoRangeOfBoundsRadius - дефолт). Live.</summary>
	public float AoRadiusFraction { get; set; } = ModelViewportEnvironment.AoRangeOfBoundsRadius;

	/// <summary>Радиус AO в МИРОВЫХ единицах; 0 = считать от габаритов модели по
	/// <see cref="AoRadiusFraction"/>. Доля баундов разумна для превью одного объекта, но на
	/// сцене-уровне (Sponza: радиус ~50) даёт метры: контактная тень превращается в широкое пятно
	/// вокруг тонкой геометрии - штор, флагов, листвы. Live.</summary>
	public float AoRadiusWorld { get; set; } = 0f;

	/// <summary>Отладочный вид AO: композит (SsaoCompositePS.hlsl) выводит видимость в grayscale
	/// вместо умножения на неё кадра. Live, аналог <see cref="ProbeGiDebugView"/> для экранного AO.</summary>
	public bool AoDebugView { get; set; } = false;

	// --- SSGI (экранный отскок света, см. SsgiPass.cs / GraphicsSettingsWindow) ---

	/// <summary>Множитель собранного bounce. Live (кбуфер GiConstants).</summary>
	public float SsgiIntensity { get; set; } = SsgiPassResources.DefaultIntensity;

	/// <summary>Тапов на пиксель в оценке GI (4..<see cref="SsgiPassResources.MaxSampleCount"/>):
	/// главный рычаг шум/цена. Live.</summary>
	public int SsgiSamples { get; set; } = SsgiPassResources.DefaultSampleCount;

	/// <summary>Потолок яркости ОДНОГО тапа (firefly clamp): в HDR-кадре солнечное пятно рядом с
	/// затенённой стеной даёт тап в десятки единиц, и один такой из восьми превращал пасс в
	/// цветной снег. 0 = без ограничения. Live.</summary>
	public float SsgiMaxLuminance { get; set; } = SsgiPassResources.DefaultMaxLuminance;

	/// <summary>Насыщенность отскока: 1 - цвет отправителя как есть, 0 - серый bounce.
	/// Live, аналог <see cref="ProbeGiBounceSaturation"/> для экранного GI.</summary>
	public float SsgiSaturation { get; set; } = SsgiPassResources.DefaultSaturation;

	/// <summary>Радиус билатерального размытия bounce в композите, пикселей
	/// (0..<see cref="SsgiPassResources.MaxBlurRadius"/>). Live.</summary>
	public int SsgiBlurRadius { get; set; } = SsgiPassResources.DefaultBlurRadius;

	/// <summary>Радиус сбора GI в МИРОВЫХ единицах; 0 = считать от габаритов модели по
	/// <see cref="SsgiRadiusFraction"/> (та же логика, что у <see cref="AoRadiusWorld"/>).
	/// Live.</summary>
	public float SsgiRadiusWorld { get; set; } = 0f;

	/// <summary>Радиус сбора GI в долях габаритного радиуса модели (см.
	/// ModelViewportEnvironment.GiRangeOfBoundsRadius - дефолт). Live.</summary>
	public float SsgiRadiusFraction { get; set; } = ModelViewportEnvironment.GiRangeOfBoundsRadius;

	/// <summary>Отладочный вид SSGI: композит выводит один только bounce вместо кадра с ним.
	/// Live, аналог <see cref="AoDebugView"/>.</summary>
	public bool SsgiDebugView { get; set; } = false;

	// --- SSR (стохастические экранные отражения, см. SsrPass.cs / GraphicsSettingsWindow) ---

	/// <summary>Экранные отражения (SSR-пасс): стохастический GGX-луч на пиксель по тонкому
	/// G-buffer-у + темпоральная аккумуляция; результат ЗАМЕНЯЕТ env-спекуляр. Live-фича конвейера
	/// (сам G-buffer пишется всегда); тянет за собой буфер векторов движения.</summary>
	public bool PreviewSsr { get; set; } = false;

	/// <summary>RT-фолбэк отражений: лучи, промахнувшиеся мимо экрана, добираются inline RayQuery
	/// по TLAS сцены (нужны inline-трассировка устройства И включённый аппаратный probe GI - его
	/// accel и питает лучи). Смена пересобирает материалы SSR, но не окружение.</summary>
	public bool SsrRayTraced { get; set; } = false;

	/// <summary>Текстурное альбедо внеэкранных RT-хитов (только с <see cref="SsrRayTraced"/>):
	/// 0 - выкл (потриугольное усреднённое альбедо), 1 - атлас даунсемпленных плиток 128²
	/// (дёшево), 2 - «bindless»-массив полноразмерных base color текстур (честнее и дороже;
	/// без поддержки индексации массивов на девайсе тихо падает до атласа). Смена пересобирает
	/// материалы SSR, как <see cref="SsrRayTraced"/>.</summary>
	public int SsrHitTextures { get; set; } = 0;

	/// <summary>Множитель заменяющего отражения (1 - энергетически честно). Live (кбуфер SsrConstants).</summary>
	public float SsrIntensity { get; set; } = SsrPassResources.DefaultIntensity;

	/// <summary>Потолок perceptual roughness: выше отражения плавно гаснут. Live.</summary>
	public float SsrMaxRoughness { get; set; } = SsrPassResources.DefaultMaxRoughness;

	/// <summary>Толщина поверхности при проверке пересечения луча с глубиной, мировые единицы. Live.</summary>
	public float SsrThickness { get; set; } = SsrPassResources.DefaultThickness;

	/// <summary>Дальность луча отражения, мировые единицы. Live.</summary>
	public float SsrMaxDistance { get; set; } = SsrPassResources.DefaultMaxDistance;

	/// <summary>Вес истории темпоральной аккумуляции (0..0.97): больше - глаже и инертнее. Live.</summary>
	public float SsrHistoryWeight { get; set; } = SsrPassResources.DefaultHistoryWeight;

	/// <summary>Качество резолва отражений (1..4): сколько чужих лучей переиспользует пиксель
	/// (x2 тапов, вес BRDF/PDF - см. SsrResolvePS). Live.</summary>
	public int SsrRaysPerPixel { get; set; } = SsrPassResources.DefaultRaysPerPixel;

	/// <summary>Отладочный вид SSR: 0 - кадр, 1 - только отражения, 2 - confidence, 3 - нормали
	/// G-buffer-а, 4 - альбедо RT-хитов. Live, аналог <see cref="SsgiDebugView"/>.</summary>
	public int SsrDebugView { get; set; } = 0;

	/// <summary>ВСЕГО отскоков RT-луча отражений (1..4): 1 - только первичный луч, 2+ -
	/// зеркальные продолжения с металлических хитов («зеркало в зеркале»). Только с
	/// <see cref="SsrRayTraced"/>; live (кбуфер SsrConstants).</summary>
	public int SsrRtBounces { get; set; } = SsrPassResources.DefaultRtBounces;

	/// <summary>Режим трассировки отражений (только с <see cref="SsrRayTraced"/>): 0 - экранный
	/// марш, затем RT для промахнувшихся лучей; 1 - сразу RT (марш пропускается целиком). Во
	/// втором режиме экранные ДАННЫЕ по-прежнему используются - радианс в точке хита берётся с
	/// экрана репроекцией; уходят артефакты самого марша. Live (кбуфер SsrConstants).</summary>
	public int SsrTraceMode { get; set; } = 0;

	// --- Авто-экспозиция (см. EyeAdaptationPass.cs / TonemapPass.cs / GraphicsSettingsWindow) ---

	/// <summary>Авто-экспозиция превью («адаптация глаза»): яркость кадра меряется по готовому
	/// HDR-кадру и приводится к <see cref="EyeAdaptationKey"/>. Вместе с ней конвейер превью
	/// становится HDR (линейный RGBA16F-кадр + отдельный тонемап): перестройка идёт по SetFeatures
	/// на живом окружении, модель не перечитывается. Выключено по умолчанию: кадр тогда идентичен
	/// прежнему.</summary>
	public bool PreviewEyeAdaptation { get; set; } = false;

	/// <summary>HDR-режим окна Scene View: авто-экспозиция + отдельный тонемап для сцены префаба
	/// (галочка в тулбаре Scene View, см. PrefabSceneViewport). Применяется live - вьюпорт
	/// пересоздаёт своё окружение, резидентные модели переезжают в него без перечитывания с диска.</summary>
	public bool SceneViewHdr { get; set; } = false;

	/// <summary>Базовая скорость полёта камеры Scene View (единиц/сек, см. SceneCamera.FlySpeed) -
	/// колесо мыши при зажатой RMB крутит именно её. Персистится между сессиями редактора: подобранная
	/// под масштаб конкретного проекта скорость не должна сбрасываться при каждом перезапуске.</summary>
	public float SceneCameraSpeed { get; set; } = SceneCamera.DefaultFlySpeed;

	/// <summary>Key value: средняя яркость, к которой экспозиция приводит кадр. 0.18 - классический
	/// «средне-серый» фотографии. Live (кбуферы EyeAdaptation/TonemapConstants).</summary>
	public float EyeAdaptationKey { get; set; } = 0.18f;

	/// <summary>Нижняя граница ИЗМЕРЕННОЙ яркости: без неё почти чёрный кадр (закрытая дверь,
	/// камера в стене) вытягивался бы в белый шум. Live.</summary>
	public float EyeAdaptationMinLuminance { get; set; } = 0.03f;

	/// <summary>Верхняя граница измеренной яркости - симметричная защита от выжженного кадра
	/// (солнце в объективе не должно топить сцену в чёрный). Live.</summary>
	public float EyeAdaptationMaxLuminance { get; set; } = 8f;

	/// <summary>Скорость адаптации к более СВЕТЛОМУ кадру, 1/сек («зажмуриться»). Live.</summary>
	public float EyeAdaptationSpeedUp { get; set; } = 3f;

	/// <summary>Скорость адаптации к более ТЁМНОМУ кадру, 1/сек - заметно медленнее: глаз тоже
	/// привыкает к темноте дольше, чем к свету. Live.</summary>
	public float EyeAdaptationSpeedDown { get; set; } = 1f;

	/// <summary>Экспокоррекция в стопах поверх авто-экспозиции (художественная ручка «сделай
	/// светлее/темнее»). Live.</summary>
	public float EyeAdaptationExposureCompensation { get; set; } = 0f;

	// --- Probe GI (DDGI-lite, см. ProbeGi.cs / GraphicsSettingsWindow) ---

	/// <summary>Probe-GI превью: CPU-бейк сетки irradiance-проб + sky visibility после загрузки
	/// модели. Live-тумблер: выключение прячет пробы (эмбиент откатывается на константный),
	/// включение запускает бейк.</summary>
	public bool PreviewProbeGi { get; set; } = true;

	/// <summary>Интенсивность солнца - общая для аналитического ключа шейдера и бейка баунса
	/// (см. keyIntensity в UnlitInstancedPS.hlsl). Изменение перепекает пробы.</summary>
	public float ProbeGiSunIntensity { get; set; } = 2f;

	/// <summary>Множитель яркости неба в бейке проб (небесная часть эмбиента). Ребейк.</summary>
	public float ProbeGiSkyIntensity { get; set; } = 1f;

	/// <summary>Множитель готовой probe-irradiance в шейдере (экспозиция эмбиента). Live.</summary>
	public float ProbeGiAmbientBoost { get; set; } = 1f;

	/// <summary>Флор глушения СОЛНЕЧНОЙ доли probe-эмбиента экранной тенью ключа (0 = солнечный
	/// баунс в тени гасится в ноль, 1 = не гасится; небесной доли тень не касается). Live.</summary>
	public float ProbeGiShadowFloor { get; set; } = 0.3f;

	/// <summary>Флор глушения НЕБЕСНОЙ доли probe-эмбиента экранной тенью ключа. 1 (дефолт) =
	/// небо в тени не гасится (физически честный вид залитого небом двора); ниже - тень
	/// затемняется целиком, под муд. Live.</summary>
	public float ProbeGiSkyShadowFloor { get; set; } = 1f;

	/// <summary>Отладочный вид probe-GI в превью: R = солнечная доля поля, G = видимость неба,
	/// B = экранная тень ключа. Live, не влияет на бейк.</summary>
	public bool ProbeGiDebugView { get; set; } = false;

	/// <summary>Сторона окто-карты видимости проб (DDGI depth), 8..24 - см.
	/// ProbeGiBakeResult.VisRes. Больше = точнее тест Чебышёва по направлению (меньше протечек
	/// света сквозь тонкую геометрию), но вчетверо больше атлас при удвоении и вчетверо меньше
	/// лучей на тексель: без соответствующего роста Rays per probe качество ПАДАЕТ (замерено).
	/// Ребейк: раскладка атласов задаётся при создании сессии.</summary>
	public int ProbeGiVisRes { get; set; } = 8;

	/// <summary>Отладочный вид BVH проб: каркасные боксы узлов дерева поверх сцены (см.
	/// ModelPreviewViewport.PopulateBvhDebugOverlay). Показывает, во что реально свернулась
	/// геометрия для трассировки - пустые/раздутые узлы сразу видно глазом. Live.</summary>
	public bool ProbeGiBvhDebug { get; set; } = false;

	/// <summary>Глубина спуска для отладочных боксов BVH (0 = только корень). Боксов на уровне
	/// вдвое больше, чем на предыдущем, - глубже ~10 сцена превращается в кашу.</summary>
	public int ProbeGiBvhDebugDepth { get; set; } = 6;

	/// <summary>Показывать только ЛИСТЬЯ BVH (фактическая гранулярность разбиения) вместо всех
	/// узлов до заданной глубины.</summary>
	public bool ProbeGiBvhDebugLeaves { get; set; } = false;

	/// <summary>Отладочный вид РАССТАНОВКИ проб (канал 10 в UnlitInstancedPS): пятно на поверхности
	/// рядом с каждой пробой - зелёное на своём узле сетки, жёлтое-красное при релокации, синее у
	/// невалидной (замурованной) пробы. Live, не влияет на бейк.</summary>
	public bool ProbeGiDebugProbes { get; set; } = false;

	/// <summary>Флор глушения env-спекуляра запечённой видимостью неба (интерьеру нечего зеркалить
	/// из зенита): 0 = в закрытых местах отражение гаснет в ноль. Live.</summary>
	public float ProbeGiSpecularFloor { get; set; } = 0.2f;

	/// <summary>Normal-бейас точки сэмпла проб в долях минимальной ячейки - от утечек света/тьмы
	/// сквозь тонкие стены. Live.</summary>
	public float ProbeGiNormalBias { get; set; } = 0.3f;

	/// <summary>Доля ВЗГЛЯДА в направлении этого сдвига, 0..1 (остальное - нормаль). Live.
	///
	/// Ручка нужна потому, что края шкалы дают разные артефакты и универсально верного нет:
	/// 1 - сдвиг строго к камере: фасеток нет, но освещение видозависимо (при движении камеры
	/// пробы будто съезжают и меняют яркость); 0 - строго по нормали: от камеры не зависит вовсе,
	/// но на жёстких рёбрах в эмбиенте проступают грани треугольников. У статьи (Majercik 2021,
	/// §4.1) - 0.8. Дефолт 1 сохраняет прежнее поведение движка.</summary>
	public float ProbeGiViewBias { get; set; } = 1f;

	/// <summary>Лучей на пробу (16-512). Больше = глаже поле, линейно дольше бейк. Ребейк.</summary>
	public int ProbeGiRaysPerProbe { get; set; } = ProbeGiBaker.DefaultRaysPerProbe;

	/// <summary>Итераций сбора (1-6, кламп в ProbeGiBakeSession): каждая после первой добавляет
	/// переотскок (мультибаунс). Глубоким дворам нужно 2-3. Ребейк.</summary>
	public int ProbeGiBounces { get; set; } = 2;

	/// <summary>Насыщенность цветного отскока в бейке (0-1): доля хроматичности альбедо, уходящая
	/// в баунс. Яркость баунса не меняется - гасится только цвет, поэтому солнце рассеивается
	/// по-прежнему сильно, а яркие цветные ткани не светят как лампочки. Ребейк.</summary>
	public float ProbeGiBounceSaturation { get; set; } = 0.5f;

	/// <summary>Плотность сетки проб: ячеек на максимальный габарит сцены (4-64, кламп в
	/// ProbeGiBaker). Ребейк.</summary>
	public float ProbeGiGridDensity { get; set; } = 22f;

	/// <summary>Потолок числа проб (ProbeGiBaker.MinProbeBudget..MaxProbeBudget, то есть 512..2M) -
	/// ячейка укрупняется, пока сетка не влезет. Ребейк.</summary>
	public int ProbeGiMaxProbes { get; set; } = 8192;

	/// <summary>Пускать лучи GPU-раунда через аппаратную трассировку (RayQuery по BLAS/TLAS) вместо
	/// программного обхода собственного BVH. Действует только если устройство умеет
	/// inline-трассировку; иначе тихо игнорируется.</summary>
	public bool ProbeGiHardwareRayTracing { get; set; } = false;

	/// <summary>Режим реального времени вместо запечки (см. <see cref="ProbeGiBakeOptions.Realtime"/>):
	/// раунды крутятся, пока жива сессия, а поле копится экспоненциальным средним с постоянной
	/// альфой - то есть отслеживает изменения освещения бесконечно, а не сходится к статичному
	/// снимку. Live: переключается на живой сессии, накопленное поле не выбрасывается.</summary>
	public bool ProbeGiRealtime { get; set; } = false;

	/// <summary>Лучей на пробу за раунд в режиме реального времени (8-1024, см.
	/// <see cref="ProbeGiBakeOptions.RealtimeRaysPerRound"/>). Главная ручка против мерцания: веер
	/// у всех проб раунда общий, поэтому ошибка оценки видна как дыхание яркости всей сцены, и
	/// гасится она только числом лучей. Live.</summary>
	public int ProbeGiRealtimeRays { get; set; } = 64;

	/// <summary>Вес раунда в режиме реального времени - альфа экспоненциального среднего (см.
	/// <see cref="ProbeGiBaker.RealtimeBlend"/>). Главная ручка против мигания ОТДЕЛЬНЫХ проб:
	/// меньше - спокойнее поле и медленнее отклик на смену света. Live.</summary>
	public float ProbeGiRealtimeBlend { get; set; } = ProbeGiBaker.RealtimeBlend;

	/// <summary>Предел изменения пробы за раунд в долях её яркости, 0 = без предела (см.
	/// <see cref="ProbeGiBakeOptions.RealtimeMaxStep"/>). Главная ручка против «диско»: в отличие от
	/// веса раунда, действует ИЗБИРАТЕЛЬНО - спокойные пробы не трогает, буйным меняет вспышки на
	/// переползание, и яркости при этом не теряет. Live.</summary>
	public float ProbeGiRealtimeMaxStep { get; set; } = 0.03f;

	/// <summary>Показывать пробы шариками в превью (см. ProbeDebugOverlay): позиция - с учётом
	/// релокации, цвет - накопленный SH L0, красный - «в стене», голубая кромка - переехала.
	/// Live (пересобирает рендер-граф, но не сессию).</summary>
	public bool ProbeGiShowProbes { get; set; } = false;

	/// <summary>Гамма перцептивного накопления в реальном времени, 1 = линейное (см.
	/// <see cref="ProbeGiBakeOptions.RealtimeGamma"/>): светлячки давятся, переход свет→тень
	/// ускоряется, ценой лёгкого смещения уровня вниз на шумной оценке. Live.</summary>
	public float ProbeGiRealtimeGamma { get; set; } = 5f;

	/// <summary>Порог средней изменчивости, ниже которого объём считается сошедшимся и раунды
	/// останавливаются целиком, 0 = выключено (см.
	/// <see cref="ProbeGiBakeOptions.RealtimeVariabilityThreshold"/>). Live.</summary>
	public float ProbeGiVariabilityThreshold { get; set; } = 0.08f;

	/// <summary>Релокация проб: насколько проба вправе отойти от узла сетки, в долях шага сетки
	/// (0 = выключено, см. <see cref="ProbeGiBakeOptions.RealtimeRelocation"/>). Лечит ПРИЧИНУ
	/// мигания густой сетки - пробы, оказавшиеся внутри стен и колонн. Live.</summary>
	public float ProbeGiRealtimeRelocation { get; set; } = 0.45f;


	/// <summary>
	/// Настройки сериализуются ВМЕСТЕ С ПОЛЯМИ.
	///
	/// Почти всё здесь - свойства, но <see cref="AnimationDebug"/> и <see cref="PhysicsDebug"/> -
	/// структуры из публичных ПОЛЕЙ, а System.Text.Json поля по умолчанию не трогает вовсе. Без
	/// этого флага обе уезжали в файл пустым объектом (проверено: в живом editor_settings.json
	/// лежало `"AnimationDebug": {}`), то есть каждая галочка дебага молча терялась при перезапуске
	/// редактора - причём выглядело это как «настройки не сохраняются иногда», ведь всё остальное
	/// сохранялось исправно.
	/// </summary>
	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		WriteIndented = true,
		IncludeFields = true,
	};

	public static EditorSettings Load()
	{
		try
		{
			if (File.Exists(FilePath))
			{
				var json = File.ReadAllText(FilePath);
				var loaded = JsonSerializer.Deserialize<EditorSettings>(json, SerializerOptions);
				if (loaded is not null)
				{
					loaded.UiScalePercent = ClampToAllowedScale(loaded.UiScalePercent);
					return loaded;
				}
			}
		}
		catch
		{
			// ????????????/?????????? ???? ???????? ? ?????????? ???????? ?? ?????????.
		}

		return new EditorSettings();
	}

	public void Save()
	{
		try
		{
			var directory = Path.GetDirectoryName(FilePath);
			if (!string.IsNullOrEmpty(directory))
			{
				Directory.CreateDirectory(directory);
			}

			var json = JsonSerializer.Serialize(this, SerializerOptions);
			File.WriteAllText(FilePath, json);
		}
		catch
		{
			// ????????????? ????????? ????????? ?? ?????? ????????? ?????? ?????????.
		}
	}

	/// <summary>????????? ???????????? ???????? ???????? ?? ?????????? ??????????? (50..150, ??? 25).</summary>
	public static int ClampToAllowedScale(int percent)
	{
		var closest = AllowedUiScalePercents[0];
		var closestDistance = Math.Abs(percent - closest);

		foreach (var allowed in AllowedUiScalePercents)
		{
			var distance = Math.Abs(percent - allowed);
			if (distance < closestDistance)
			{
				closest = allowed;
				closestDistance = distance;
			}
		}

		return closest;
	}
}

