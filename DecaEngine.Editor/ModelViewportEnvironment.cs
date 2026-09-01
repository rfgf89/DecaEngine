using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
using DecaEngine.Graphics.ProbeGi;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using DecaEngine.Scene;
using DecaEngine.Animation;

namespace DecaEngine.Editor
{
	/// <summary>Состояние мирового направленного света превью (тени, см. SimpleCullingAndRenderSystem /
	/// ShadowPass): направление приходит от «солнца» энвайронмента, баунды сцены задаёт вьюпорт после
	/// загрузки модели. Radius 0 = каскад не строится, шейдер откатывается на камерный key-свет.</summary>
	public sealed class PreviewShadowSettings
	{
		/// <summary>Live-тумблер: false - каскад не строится, ShadowPass no-op-ится, шейдер получает
		/// нулевой LightDirection и откатывается на камерный ключевой свет. Без пересоздания графа.</summary>
		public bool Enabled = true;

		public Vector3 LightDirection = new(0.45f, -0.72f, -0.35f);
		public Vector3 BoundsCenter;
		public float BoundsRadius;

		/// <summary>Число каскадов теней (1..<see cref="DecaEngine.Graphics.Diligent.ShadowRenderer.MaxCascades"/>).
		/// 1 (дефолт) - прежнее поведение превью: один ортокаскад на все баунды. Больше - мелкие
		/// каскады уточняют окрестность <see cref="FocusPoint"/>, последний всегда накрывает сцену
		/// целиком (см. SimpleCullingAndRenderSystem.BuildLightData). Опция уровня создания
		/// окружения: под число каскадов замораживаются команды ShadowPass.</summary>
		public int CascadeCount = 1;

		/// <summary>Точка интереса камеры - центр мелких каскадов (вьюпорт пушит её каждый кадр,
		/// см. PrefabSceneViewport.Update). Без неё (HasFocus false) каскады центрируются на
		/// баундах сцены.</summary>
		public Vector3 FocusPoint;
		public bool HasFocus;

		/// <summary>Азимут/высота «солнца» энвайронмента в градусах - положение света ДО
		/// пользовательских ползунков (см. <see cref="SetAngles"/>). Заполняется при создании
		/// окружения из PreviewEnvironmentMap.SunDirection.</summary>
		public float BaseYawDegrees;
		public float BaseElevationDegrees;

		/// <summary>Текущие абсолютные азимут/высота солнца в градусах - см. <see cref="SetAngles"/>.</summary>
		public float YawDegrees;
		public float ElevationDegrees;

		/// <summary>Пользовательский поворот энвайронмента вокруг Y в радианах (текущий яв минус
		/// базовый) - пушится в шейдеры неба/IBL (см. SkyBackgroundPS.hlsl / UnlitInstancedPS.hlsl),
		/// чтобы отражения и фон вращались синхронно с ключевым светом. Высота света на equirect-карту
		/// не переносится (поворот вокруг Y - единственный дешёвый для панорамы).</summary>
		public float EnvYawRadians => (YawDegrees - BaseYawDegrees) * (MathF.PI / 180f);

		/// <summary>Ставит солнце в абсолютные азимут (яв вокруг Y, от +Z к +X) и высоту над
		/// горизонтом. Читается BuildLightData каждый кадр, так что тени/ключевой свет подхватывают
		/// поворот live.</summary>
		public void SetAngles(float yawDegrees, float elevationDegrees)
		{
			YawDegrees = yawDegrees;
			ElevationDegrees = elevationDegrees;

			var yaw = yawDegrees * (MathF.PI / 180f);
			var elevation = elevationDegrees * (MathF.PI / 180f);

			// Направление НА солнце; свет светит в противоположную сторону.
			var sun = new Vector3(
				MathF.Cos(elevation) * MathF.Sin(yaw),
				MathF.Sin(elevation),
				MathF.Cos(elevation) * MathF.Cos(yaw));
			LightDirection = -sun;
		}
	}

	/// <summary>Отключаемые фичи Lighting-превью (биты PbrFeatureFlags в PreviewSettings-кбуфере,
	/// см. UnlitInstancedPS.hlsl). Основа для будущих настроек графики: каждая фича обязана
	/// корректно работать в выключенном состоянии.</summary>
	[Flags]
	public enum PreviewFeatureFlags
	{
		None = 0,

		/// <summary>Tangent-space нормал-мапы (_NormalTex).</summary>
		NormalMaps = 1 << 0,

		/// <summary>Ambient occlusion текстура (_OcclusionTex).</summary>
		Occlusion = 1 << 1,

		/// <summary>Тени мирового ключевого света (ShadowMaps + PCF). Требует включённого
		/// shadow-пасса окружения; без него бит безвреден (LightDirection нулевой).</summary>
		Shadows = 1 << 2,

		/// <summary>HDR-конвейер: шейдер модели пишет ЛИНЕЙНУЮ яркость без тонемапа и sRGB-энкода -
		/// их делает TonemapPass в конце кадра, по экспозиции от авто-адаптации (см.
		/// EyeAdaptationPass). Бит, а не кейворд: HDR - опция уровня создания окружения, а бит
		/// пушится в тот же кбуфер, что и остальные тумблеры.</summary>
		HdrOutput = 1 << 3,

		All = NormalMaps | Occlusion | Shadows,
	}

	/// <summary>
	/// Layout of the "PreviewSettings" cbuffer declared in UnlitInstancedPS.hlsl (see also
	/// WireframeOverlayPS.hlsl, which ignores it). Mode: 0 = Textured, 1 = Highlight, 2 = Channel debug,
	/// 3 = Lighting (PBR). Channel (used only when Mode == 2): 0 = Normal, 1 = UV, 2 = Tangent.
	/// Metallic/Roughness/BaseColor/HasBaseColorTexture/AlphaCutoff (used only when Mode == 3) are the
	/// material's glTF factors (see <see cref="ModelLoader.MaterialPbr"/>), pushed per material. Pushed
	/// to each material via <see cref="IMaterialObject.SetConstant{T}"/> by <see cref="ModelPreviewViewport"/>
	/// and <see cref="ModelIconBaker"/> - never touched by the main editor scene, so it stays at its
	/// zero-initialized (Textured) default there.
	/// </summary>
	public struct PreviewSettingsData
	{
		public int Mode;
		public int Channel;
		public float Metallic;
		public float Roughness;
		public Vector4 BaseColor;
		public int HasBaseColorTexture;
		public float AlphaCutoff;
		public int HasMetallicRoughnessTexture;
		public float Transmission;
		public float Dispersion;
		public float Ior;

		/// <summary>Толщина стекла в мировых единицах (thicknessFactor × масштаб узла) - для
		/// геометрического смещения рефракции. См. ModelLoader.MaterialPbrFactors.ThicknessWorld.</summary>
		public float ThicknessWorld;

		/// <summary>Битовая маска <see cref="PreviewFeatureFlags"/> - глобальные тумблеры фич,
		/// пушатся вместе с per-material данными.</summary>
		public int FeatureFlags;

		/// <summary>KHR_materials_volume, precomputed: rgb = attenuationColor, w = thickness /
		/// attenuationDistance (Beer-Lambert exponent, 0 = off). See ModelLoader.MaterialPbrFactors.</summary>
		public Vector4 VolumeAttenuation;

		/// <summary>glTF normalScale (xy-каналы нормал-мапы).</summary>
		public float NormalScale;

		/// <summary>glTF occlusionStrength (вес запечённого AO).</summary>
		public float OcclusionStrength;

		/// <summary>KHR_texture_transform offset (см. <see cref="MaterialPbrFactors.UvOffset"/>).</summary>
		public Vector2 UvOffset;

		/// <summary>KHR_texture_transform, предвычисленная 2x2-матрица UV (см.
		/// <see cref="MaterialPbrFactors.UvTransform"/>).</summary>
		public Vector4 UvTransform;

		/// <summary>1 = применять <see cref="UvTransform"/>/<see cref="UvOffset"/>; 0 (zero-init) -
		/// тождественное преобразование.</summary>
		public int UvHasTransform;

		/// <summary>Индекс UV-канала occlusionTexture (glTF texCoord 0/1, см.
		/// <see cref="MaterialPbrFactors.OcclusionUvSet"/>).</summary>
		public int OcclusionUvSet;

		/// <summary>Пользовательский поворот энвайронмента вокруг Y в радианах (см.
		/// <see cref="PreviewShadowSettings.EnvYawRadians"/>) - сдвигает equirect-UV неба/IBL, чтобы
		/// отражения вращались синхронно с ключевым светом. 0 (zero-init) = без поворота.</summary>
		public float EnvYawRadians;

		// Occupies the old explicit padding slot, so the float4s below stay aligned with the HLSL
		// cbuffer layout (SetConstant uploads Marshal.SizeOf rounded UP to 16).
		/// <summary>Режим фильтрации теней (SHADOW_MODE_* в UnlitInstancedPS.hlsl): 0 = PCSS
		/// (обязан оставаться нулём - zero-init сцен вне превью должен давать дефолтное качество),
		/// 1 = Hard, 2 = PCF 3x3, 3 = PCSS HQ. См. EditorSettings.ShadowFilterMode.</summary>
		public int ShadowMode;

		/// <summary>KHR_materials_sheen: rgb = sheenColorFactor (ноль = выключено), w =
		/// sheenRoughnessFactor. См. <see cref="MaterialPbrFactors.SheenColorRoughness"/>.</summary>
		public Vector4 SheenColorRoughness;

		/// <summary>KHR_materials_specular: rgb = specularColorFactor (может быть &gt;1, кламп в
		/// шейдере после умножения на F0 от IOR), w = specularFactor. Каждый пуш в Lighting-режиме
		/// обязан заполнить его ((1,1,1,1) = тождественно) - нулевой w глушит спекуляр в чёрный.
		/// См. <see cref="MaterialPbrFactors.SpecularColorFactor"/>.</summary>
		public Vector4 SpecularColorFactor;

		/// <summary>Probe-GI сетка (см. <see cref="ProbeGiTextures"/>): xyz = мировая позиция пробы
		/// (0,0,0), w = 1 - пробы запечены и атласы привязаны (0 = фича выключена/не готова).</summary>
		public Vector4 ProbeGridOrigin;

		/// <summary>xyz = шаг сетки проб в мировых единицах, w = normal-бейас точки сэмпла.</summary>
		public Vector4 ProbeGridCell;

		/// <summary>xyz = число проб по осям сетки. Сетка ПЛОТНАЯ: проба есть в каждом узле (см.
		/// <see cref="ProbeGiBakeResult"/>).</summary>
		public Vector4 ProbeGridCounts;

		/// <summary>xyz = тороидальное смещение сетки в пробах: узел c лежит в текселе
		/// (c + scroll) mod counts (см. <see cref="ProbeGiBakeResult.ScrollOffsetX"/>).</summary>
		public Vector4 ProbeGridScroll;

		/// <summary>Ручки probe-GI (см. GraphicsSettingsWindow): x = флор глушения солнечной доли
		/// эмбиента тенью, y = флор глушения env-спекуляра видимостью неба, z = интенсивность
		/// солнца, w = множитель probe-irradiance.</summary>
		public Vector4 ProbeGiParams;

		/// <summary>x = флор глушения небесной доли эмбиента тенью (1 = не гасится); y = сторона
		/// окто-карты видимости проб (см. ProbeGiBakeResult.VisRes - ручка «Visibility res»; 0 =
		/// шейдер берёт дефолт 8, чтобы zero-init кбуфера вне превью не ломал выборку); zw - резерв.</summary>
		public Vector4 ProbeGiParams2;

		/// <summary>Сетки мелких каскадов probe-GI (см. SampleProbeGi в UnlitInstancedPS.hlsl):
		/// семантика та же, что у базовых ProbeGrid*, Origin.w = 1 - каскад создан и его атласы
		/// _C1/_C2 привязаны. Нулевые (zero-init) = каскада нет, шейдер падает на базовый объём.</summary>
		public Vector4 ProbeGridOrigin1;
		public Vector4 ProbeGridCell1;
		public Vector4 ProbeGridCounts1;
		public Vector4 ProbeGridScroll1;
		public Vector4 ProbeGridOrigin2;
		public Vector4 ProbeGridCell2;
		public Vector4 ProbeGridCounts2;
		public Vector4 ProbeGridScroll2;

		/// <summary>Режим кривой тонмапа (см. Tonemap.hlsl / <see cref="ToneCurveMode"/>). Действует
		/// ТОЛЬКО в LDR-режиме: в HDR-конвейере кривую применяет TonemapPass в самом конце, а
		/// материал оставляет кадр линейным.
		///
		/// Заполняется в ДВУХ местах - вьюпортом и CLI-пробой (см. PreviewProbe): они пушат этот
		/// кбуфер независимо, и поле, добавленное только в одном, тихо приезжает нулём в другом.</summary>
		public int ToneCurve;
	}

	/// <summary>
	/// Off-screen ECS render environment for showing/baking a .gltf/.glb model: EntityStore +
	/// DiligentBatchRenderer + GraphicsPipeline + camera + color/depth render targets. Models are
	/// drawn unlit (see EditorSettings' default Unlit*Instanced shaders), so there's no light/shadow
	/// setup here - just geometry, culled and drawn via <see cref="SimpleCullingAndRenderSystem"/>.
	/// Shared by <see cref="ModelPreviewViewport"/> (interactive Inspector/Prefab viewport) and
	/// <see cref="ModelIconBaker"/> (background Asset Browser icon baking) - the two have different
	/// update/interaction loops but need the exact same scene scaffolding to render a model, so that
	/// setup lives here instead of being duplicated in both.
	/// </summary>
	public sealed class ModelViewportEnvironment
	{
		public const float CameraFovDegrees = 45f;

		public IGraphicsApi GraphicsApi { get; }
		public DiligentGraphicsApi DilApi { get; }
		public DiligentBatchRenderer BatchRenderer { get; }
		public GraphicsPipelineSimple Pipeline { get; }
		public EntityStore Store { get; }
		public RenderResourceManager ResourceManager { get; }
		public SystemRoot Root { get; }
		public Entity CameraEntity { get; }

		/// <summary>Создаёт и владеет всеми офскрин-таргетами (цвет/депт/scene-copy/AO)
		/// <see cref="Pipeline"/> сам - как свопчейн владел бы back buffer-ом, см.
		/// <see cref="GraphicsPipelineSimple"/>. Свойства ниже - тонкие проксирующие делегаты, чтобы
		/// вызывающему коду (ModelPreviewViewport, ModelIconBaker) не пришлось лезть в Pipeline самому.</summary>
		public IRenderTarget ColorTarget => Pipeline.Targets!.ColorTarget;
		public IRenderTarget DepthTarget => Pipeline.Targets!.DepthTarget;

		/// <summary>Сэмплируемая копия <see cref="ColorTarget"/> после opaque-дроу - источник
		/// рефракции для transmissive-материалов (см. ForwardPass / UnlitInstancedPS.hlsl).</summary>
		public IRenderTarget SceneCopyTarget => Pipeline.Targets!.SceneCopyTarget;

		/// <summary>Процедурный equirect-энвайронмент с префильтрованными по roughness мипами -
		/// источник отражений/ambient-освещения Lighting-режима (см. <see cref="PreviewEnvironmentMap"/>).
		/// ЧУЖОЙ ресурс: владеет им <see cref="SharedResources"/> (ключ - HDRI путь), это окружение
		/// его только читает и не освобождает (см. <see cref="Release"/>).</summary>
		public IGpuTexture EnvironmentMap { get; }

		/// <summary>Общий на процесс контейнер энвайронментов/сэмплеров (см.
		/// <see cref="SharedViewportResources"/>) - передаётся вызывающим (EditorManager и CLI-гарнессы),
		/// а не создаётся здесь.</summary>
		public SharedViewportResources SharedResources { get; }

		/// <summary>CPU-выборка радианса окружения по направлению (та же панорама, что в
		/// <see cref="EnvironmentMap"/>) - источник неба для CPU-бейка проб (см. ProbeGiBaker).</summary>
		public Func<Vector3, Vector3> EnvironmentRadiance { get; }

		/// <summary>AO-таргет SSAO-пасса (null = SSAO выключен). Владеет им и пересоздаёт при
		/// Resize <see cref="Pipeline"/> - см. <see cref="GraphicsPipelineSimple.SsaoResources"/>.</summary>
		public IRenderTarget? AoTarget => Pipeline.SsaoResources?.AoTarget;

		/// <summary>GI-таргет SSGI-пасса (null = SSGI выключен). Владеет им и пересоздаёт при
		/// Resize <see cref="Pipeline"/> - см. <see cref="GraphicsPipelineSimple.SsgiResources"/>.</summary>
		public IRenderTarget? GiTarget => Pipeline.SsgiResources?.GiTarget;

		/// <summary>Линейный HDR-таргет кадра - тот, в который рисует вся геометрия и пост-обработка.
		/// Ресайзится вместе с остальными (см. ModelPreviewViewport.ResizeTargets); показывается же
		/// всегда <see cref="ColorTarget"/>, уже после тонемапа.</summary>
		public IRenderTarget? HdrColorTarget => Pipeline.Targets?.HdrColorTarget;

		/// <summary>Включён ли HDR-конвейер превью - от него зависит бит
		/// <see cref="PreviewFeatureFlags.HdrOutput"/> в кбуфере материалов. В офскрин-окружении он
		/// теперь всегда включён (см. GraphicsPipelineSimple): тонемап живёт отдельным пассом при
		/// любом наборе фич, а тумблер авто-экспозиции лишь выбирает, мерить яркость или взять
		/// экспозицию из ручной экспокоррекции.</summary>
		public bool HdrOutput => Pipeline.Targets?.HdrColorTarget is not null;

		/// <summary>Текущий набор фич конвейера - см. <see cref="SetFeatures"/>.</summary>
		public PipelineFeatures Features => Pipeline.Features;

		/// <summary>Меняет набор фич БЕЗ пересоздания окружения и без перезагрузки сцены - см.
		/// <see cref="GraphicsPipelineSimple.SetFeatures"/>. Именно этим окно настроек применяет
		/// SSAO (и его технику), SSGI, туман, god rays, блум, грейдинг, небо и авто-экспозицию.
		///
		/// Тени тумблером фич НЕ переключаются: у превью для них свой, ещё более дешёвый путь -
		/// <see cref="PreviewShadowSettings.Enabled"/> плюс бит <see cref="PreviewFeatureFlags.Shadows"/>,
		/// и ShadowPass при выключенных тенях сам вырождается в no-op (см. ApplyGraphicsSettings).
		/// Поэтому <see cref="PipelineFeatures.Shadows"/> здесь остаётся тем, с чем окружение создано.</summary>
		public void SetFeatures(PipelineFeatures features)
		{
			features.Shadows = Pipeline.Features.Shadows;
			Pipeline.SetFeatures(features);
		}

		/// <summary>Состояние мирового света/теней (null = тени выключены). Вьюпорт обновляет баунды
		/// после загрузки модели (см. ModelPreviewViewport.FrameAll).</summary>
		public PreviewShadowSettings ShadowSettings { get; }

		private SimpleCullingAndRenderSystem? _cullingSystem;
		private CullingAndRenderSystem? _mainCullingSystem;

		/// <summary>Солнце-сущность (LightComponent + SunComponent + CascadedShadowComponent) -
		/// существует только в режиме <c>mainCascades</c>: каскады строит ОСНОВНОЙ
		/// <see cref="CullingAndRenderSystem"/>, направление света = +Z поворота этой сущности.
		/// Вьюпорт синхронизирует её с <see cref="ShadowSettings"/> каждый кадр (см.
		/// PrefabSceneViewport.SyncSunEntity).</summary>
		public Entity SunEntity { get; private set; }

		/// <summary>Освобождает GPU-ресурсы окружения - для пересоздания превью с новыми
		/// рестарт-левел опциями на лету (см. ModelPreviewViewport.RecreateEnvironment). Вызывающий
		/// обязан сперва дождаться GPU (Flush + WaitForIdle) и отвязать ImGui-биндинги таргетов.
		/// Материалы/меши резидентных моделей не трогаются - они пересоздадутся перезагрузкой
		/// модели (та же семантика, что у резидент-кеша всегда была).</summary>
		public void Release()
		{
			// Освобождает и все офскрин-таргеты (цвет/депт/scene-copy), и sky/SSAO-ресурсы
			// (материалы + AO-таргет) - их создаёт и владеет ими Pipeline (см. GraphicsPipelineSimple),
			// а не это окружение.
			Pipeline.Release();
			_cullingSystem?.Dispose();
			_mainCullingSystem?.Dispose();
			BatchRenderer.Release();

			// EnvironmentMap НЕ освобождаем - им владеет SharedResources (несколько окружений могут
			// делить одну и ту же HDRI-текстуру), см. class-doc EnvironmentMap.
		}

		/// <summary>Перепривязывает ресайзабельные таргеты к SSAO-материалам ПОСЛЕ Resize - Resize
		/// пересоздаёт нативные текстуры, и SRB иначе держали бы уничтоженные (та же история, что с
		/// _SceneColor у материалов модели, см. ModelPreviewViewport.ResizeTargets). No-op когда SSAO
		/// выключен.</summary>
		public void RebindPostProcessTargets()
		{
			Pipeline.RebindSsaoTargets();
		}

		/// <summary>Доля габаритного радиуса модели, дающая мировой радиус влияния AO-пасса (см.
		/// <see cref="SetAoWorldRange"/>). На стандартном кадрировании даёт примерно тот же охват,
		/// что прежний экранный радиус, но не схлопывается при приближении камеры.</summary>
		public const float AoRangeOfBoundsRadius = 0.15f;

		/// <summary>Доля габаритного радиуса модели, дающая мировой радиус сбора GI - шире AO-шного:
		/// bounce-свет тянется заметно дальше контактной тени (см. SsgiCommon.hlsl).</summary>
		public const float GiRangeOfBoundsRadius = 0.5f;

		/// <summary>Мировой радиус влияния AO-пасса (см. SsaoPassResources.SetWorldRange): пушится
		/// вьюпортом/пробой после кадрирования модели, чтобы контактная тень под нависающей
		/// геометрией не пропадала при приближении камеры. No-op когда AO выключен.</summary>
		public void SetAoWorldRange(float worldRange)
		{
			Pipeline.SsaoResources?.SetWorldRange(worldRange);
		}

		/// <summary>Сила и нижний предел AO - живые ручки окна Graphics (см.
		/// SsaoPassResources.SetStrength). No-op когда AO выключен.</summary>
		public void SetAoStrength(float power, float floor)
		{
			Pipeline.SsaoResources?.SetStrength(power, floor);
		}

		/// <summary>Отладочный вид AO - живая ручка окна Graphics (см.
		/// SsaoPassResources.SetDebugView). No-op когда AO выключен.</summary>
		public void SetAoDebugView(bool enabled)
		{
			Pipeline.SsaoResources?.SetDebugView(enabled);
		}

		/// <summary>Отладочный вид векторов движения - живая ручка окна Graphics (см.
		/// MotionVectorDebugPassResources.SetDebugView). No-op когда векторы выключены.
		///
		/// <paramref name="rangePixels"/> - на скольких пикселях смещения шкала упирается в край.</summary>
		public void SetMotionVectorDebug(bool enabled, float rangePixels)
		{
			Pipeline.MotionVectorDebugResources?.SetDebugView(enabled, rangePixels);
		}

		/// <summary>Есть ли у конвейера буфер векторов движения на самом деле - окну настроек.</summary>
		public bool MotionVectorsAvailable => Pipeline.MotionVectorResources is not null;

		/// <summary>Суб-пиксельный джиттер проекции - живая ручка окна Graphics (см.
		/// GraphicsPipelineSimple.SetTemporalJitter). От векторов движения не зависит:
		/// это мутация одной матрицы на CPU, а не ресурсы конвейера.</summary>
		public void SetTemporalJitter(bool enabled)
		{
			Pipeline.SetTemporalJitter(enabled);
		}

		/// <summary>Работает ли нативный бэкенд апскейла прямо сейчас (FSR/DLSS) - окну Graphics,
		/// чтобы честно показать «выбран нативный, но работает TAAU» (нет шима/DLL, нет буфера
		/// векторов, не то железо).</summary>
		public bool NativeUpscalerActive => Pipeline.NativeUpscaler is not null;

		/// <summary>Какой именно нативный бэкенд активен - тем же индексом, что в настройке
		/// (1 - FSR, 2 - DLSS, 0 - никакой/TAAU).</summary>
		public int ActiveUpscalerKind { get; private set; }

		/// <summary>Подпись активного нативного бэкенда с версией библиотеки ("FSR 2.3.4",
		/// "DLSS 310.7.0.0") - окну Graphics. Null, когда работает встроенный TAAU.</summary>
		public string? ActiveUpscalerName => Pipeline.NativeUpscaler?.DebugName;

		/// <summary>Живые настройки самих апскейлеров: вес кадра TAAU, сила RCAS-шарпена FSR,
		/// пресет качества DLSS (значение NVSDK_NGX_PerfQuality_Value). Первые две - копеечные
		/// (кбуфер/параметр диспатча); смена пресета DLSS пересоздаёт NGX-фичу, поэтому перед ней -
		/// GPU-барьер, как при ресайзе.</summary>
		public void SetUpscalerTuning(float taauBlendAlpha, float fsrSharpness, int dlssQuality,
			int fsrProviderMajor = 0)
		{
			Pipeline.TemporalUpscaleResources?.SetBlendAlpha(taauBlendAlpha);

			switch (Pipeline.NativeUpscaler)
			{
				case FsrUpscalerBackend fsr:
					fsr.SetSharpness(fsrSharpness);

					// Смена ветки провайдера пересоздаёт нативный контекст И его текстуры (маски,
					// типизированная копия глубины) - GPU-барьер + ОБЯЗАТЕЛЬНАЯ инвалидация графа:
					// заморожённый буфер держит нативные указатели этих текстур, записанные при
					// рекорде, и без переписи реплей копировал бы в уничтоженную (AV в CopyTexture).
					if (fsr.ProviderMajor != fsrProviderMajor)
					{
						DilApi.ImmediateContext.Flush();
						DilApi.ImmediateContext.WaitForIdle();
						fsr.SetProvider(fsrProviderMajor);
						Pipeline.InvalidateGraph();
						Console.WriteLine($"[upscaler] провайдер FSR: {fsr.DebugName}");
					}

					break;
				case DlssUpscalerBackend dlss when dlss.Quality != dlssQuality:
					// Тот же контракт, что у смены провайдера FSR выше.
					DilApi.ImmediateContext.Flush();
					DilApi.ImmediateContext.WaitForIdle();
					dlss.SetQuality(dlssQuality);
					Pipeline.InvalidateGraph();
					break;
			}
		}

		/// <summary>Живой выбор бэкенда слота апскейлера: 0 - встроенный TAAU, 1 - FSR, 2 - DLSS
		/// (индексы = EditorSettings.UpscalerBackend). Смена ждёт GPU: конвейер перепривязывает
		/// вход тонемапа на живом материале. No-op, когда буфера векторов нет (выключенные векторы); при отсутствии шима/DLL/железа TryCreate вернёт null - остаётся TAAU.</summary>
		public void SetUpscalerBackend(int kind)
		{
			if (kind == ActiveUpscalerKind && (kind == 0) == (Pipeline.NativeUpscaler is null))
			{
				return;
			}

			DilApi.ImmediateContext.Flush();
			DilApi.ImmediateContext.WaitForIdle();

			if (kind == 0)
			{
				Pipeline.SetNativeUpscaler(null);
				ActiveUpscalerKind = 0;
				return;
			}

			if (Pipeline.MotionVectorResources is null || Pipeline.Targets?.HdrColorTarget is null)
			{
				return;
			}

			// Нативные бэкенды апскейлеров живут в шиме, собранном под D3D12 (см. native/DecaFfxShim:
			// он линкует d3d12/dxgi и nvsdk_ngx_d). На Vulkan-устройстве им передаются указатели на
			// чужие типы ресурсов, и DecaDlss_Create падает по доступу ПРЯМО В КОНСТРУКТОРЕ
			// окружения - то есть редактор не стартует вовсе и сменить настройку уже негде.
			// Тихий откат на встроенный TAAU здесь честнее: он и так штатный фоллбек при отсутствии
			// шима или неподходящего железа.
			if (DilApi.Device.GetDeviceInfo().Type != global::Diligent.RenderDeviceType.D3D12)
			{
				Console.WriteLine($"[upscaler] бэкенд {kind} доступен только на D3D12 - остаётся встроенный TAAU");
				Pipeline.SetNativeUpscaler(null);
				ActiveUpscalerKind = 0;
				return;
			}

			// Размеры - ФАКТИЧЕСКИЕ на этот момент: сценовые по депту (масштаб рендера мог уже
			// примениться), отображаемые по ColorTarget. Дальнейшие ресайзы дёрнут Resize бэкенда
			// через RebindSsaoTargets.
			var renderSize = Pipeline.Targets.DepthTarget.Size;
			var displaySize = Pipeline.Targets.ColorTarget.Size;
			INativeUpscalerBackend? backend = kind == 2
				? DlssUpscalerBackend.TryCreate(GraphicsApi, "Preview",
					Pipeline.Targets.HdrColorTarget, Pipeline.Targets.DepthTarget,
					Pipeline.MotionVectorResources.MotionTarget,
					(uint)renderSize.X, (uint)renderSize.Y, (uint)displaySize.X, (uint)displaySize.Y)
				: FsrUpscalerBackend.TryCreate(GraphicsApi, "Preview",
					Pipeline.Targets.HdrColorTarget, Pipeline.Targets.DepthTarget,
					Pipeline.MotionVectorResources.MotionTarget,
					(uint)renderSize.X, (uint)renderSize.Y, (uint)displaySize.X, (uint)displaySize.Y,
					cameraNear: 0.05f, fovYRad: CameraFovDegrees * MathF.PI / 180f);

			// Неудача создания НЕ трогает текущее состояние: остаётся прежний бэкенд (или TAAU) -
			// молча падать на TAAU при живом прежнем бэкенде было бы шагом назад.
			if (backend is not null)
			{
				Pipeline.SetNativeUpscaler(backend);
				ActiveUpscalerKind = kind;
				Console.WriteLine($"[upscaler] нативный бэкенд активен: {backend.DebugName}");
			}
			else if (Pipeline.NativeUpscaler is null)
			{
				ActiveUpscalerKind = 0;
			}
		}

		/// <summary>Масштаб рендера сцены (см. GraphicsPipelineSimple.SetRenderScale). Только
		/// запоминает значение; true = изменился, и вызывающий ОБЯЗАН прогнать ресайз-путь
		/// (см. ModelPreviewViewport.ResizeTargets) - до него сцена рисуется в старом размере.</summary>
		public bool SetRenderScale(float scale)
		{
			return Pipeline.SetRenderScale(scale);
		}

		/// <summary>Мировой радиус сбора GI-пасса (см. SsgiPassResources.SetWorldRange): пушится
		/// вместе с AO-радиусом после кадрирования модели. No-op когда SSGI выключен.</summary>
		public void SetGiWorldRange(float worldRange)
		{
			Pipeline.SsgiResources?.SetWorldRange(worldRange);
		}

		/// <summary>Живые ручки оценки SSGI - окно Graphics (см. SsgiPassResources.SetParams).
		/// No-op когда SSGI выключен.</summary>
		public void SetGiParams(float intensity, int sampleCount, float maxLuminance, float saturation)
		{
			Pipeline.SsgiResources?.SetParams(intensity, sampleCount, maxLuminance, saturation);
		}

		/// <summary>Собран ли батч-рендерер окружения под тонкий G-buffer отражений (MRT-слоты в PSO
		/// геометрии) - предикат для <c>ModelLoadOptions.ReflectionGbuffer</c> вьюпортов.</summary>
		public bool ReflectionGbuffer => BatchRenderer.ReflectionGbuffer;

		/// <summary>Живые ручки SSR - окно Graphics (см. SsrPassResources). No-op пока SSR не включался.</summary>
		public void SetSsrParams(float intensity, float maxRoughness, float thickness, float maxDistance,
			float historyWeight, int raysPerPixel, int debugView,
			int rtBounces = SsrPassResources.DefaultRtBounces, int traceMode = 0)
		{
			Pipeline.SsrResources?.SetParams(intensity, maxRoughness, thickness, maxDistance,
				historyWeight, raysPerPixel, debugView, rtBounces, traceMode);
		}

		/// <summary>Поворот env-карты и солнце RT-фолбэка для SSR - пушится вьюпортом вместе с
		/// остальными покадровыми данными света (см. SsrPassResources).</summary>
		/// <param name="sunTanHalfAngle">Тангенс половинного углового размера солнца - мягкость
		/// края тени у RT-хитов (см. SsrPassResources.SetSun).</param>
		public void SetSsrEnvironment(float envYawRadians, Vector3 dirTowardSun, Vector3 sunColor,
			float ambient, float sunTanHalfAngle = 0f)
		{
			Pipeline.SsrResources?.SetEnvironmentYaw(envYawRadians);
			Pipeline.SsrResources?.SetSun(dirTowardSun, sunColor, ambient, sunTanHalfAngle);
		}

		/// <summary>Свет RT-хитов SSR из probe-поля: SH-атласы + сетка базового объёма. null -
		/// поля нет (слоты возвращаются на плейсхолдер). ОБЯЗАТЕЛЕН с null ПЕРЕД освобождением
		/// атласов - SRB трейса иначе держал бы уничтоженные текстуры (см.
		/// SsrPassResources.SetProbeField).</summary>
		public void SetSsrProbeField(ProbeGiTextures? textures)
		{
			Pipeline.SsrResources?.SetProbeField(
				textures?.Sh0, textures?.Sh1, textures?.Sh2, textures?.Sh3,
				textures?.GridOrigin ?? Vector4.Zero,
				textures?.GridCell ?? Vector4.Zero,
				textures?.GridCounts ?? Vector4.Zero);
		}

		/// <summary>Живые ручки тумана - окно Graphics (см. FogPassResources). No-op когда туман выключен.
		/// Сама галка живой НЕ является: пассу нужны депт и scene-copy, то есть он создаётся вместе с
		/// конвейером (см. GraphicsPipelineSimple) и переключается пересозданием окружения.</summary>
		public void SetFogParams(float density, float heightFalloff, float heightRef, float startDistance,
			float maxDistance, float maxOpacity)
		{
			Pipeline.FogResources?.SetParams(density, heightFalloff, heightRef, startDistance, maxDistance,
				maxOpacity);
		}

		/// <summary>Цвета дымки и её солнечной подсветки (линейные). No-op когда туман выключен.</summary>
		public void SetFogColors(Vector3 color, Vector3 sunColor, float sunStrength, float sunSharpness)
		{
			Pipeline.FogResources?.SetColors(color, sunColor, sunStrength, sunSharpness);
		}

		/// <summary>Направление НА солнце - туману оно нужно для подсветки среды. ShadowSettings.LightDirection
		/// указывает ОТ солнца, поэтому вызывающий обязан передать его С МИНУСОМ (та же конвенция,
		/// что у бейкера проб).</summary>
		public void SetFogSun(Vector3 sunDirection)
		{
			Pipeline.FogResources?.SetSun(sunDirection);
		}

		/// <summary>Живые ручки объёмного света - god rays и объёмный туман (см.
		/// VolumetricLightPassResources). No-op когда пасс выключен: сама галка, как и у тумана,
		/// живой НЕ является - пассу нужны депт, scene-copy и shadow map, то есть он создаётся
		/// вместе с конвейером и переключается пересозданием окружения.</summary>
		public void SetVolumetricParams(float density, float heightFalloff, float heightRef,
			float startDistance, float maxDistance, int steps, float maxOpacity, float shadowStrength)
		{
			Pipeline.VolumetricResources?.SetParams(density, heightFalloff, heightRef, startDistance,
				maxDistance, steps, maxOpacity, shadowStrength);
		}

		/// <summary>Оптика среды объёмного света: рассеяние, экстинкция, анизотропия.</summary>
		public void SetVolumetricScattering(float scattering, float extinction, float anisotropy)
		{
			Pipeline.VolumetricResources?.SetScattering(scattering, extinction, anisotropy);
		}

		/// <summary>Цвета и силы солнечного (это и есть столбы) и небесного рассеяния - линейные.</summary>
		public void SetVolumetricColors(Vector3 sunColor, float sunIntensity, Vector3 ambientColor,
			float ambientIntensity, float ambientShadowFloor)
		{
			Pipeline.VolumetricResources?.SetColors(sunColor, sunIntensity, ambientColor, ambientIntensity,
				ambientShadowFloor);
		}

		/// <summary>Множитель рассеяния punctual-светов средой - см.
		/// VolumetricLightPassResources.SetPunctualScatter.</summary>
		public void SetVolumetricPunctualScatter(float intensity)
		{
			Pipeline.VolumetricResources?.SetPunctualScatter(intensity);
		}

		/// <summary>Есть ли в конвейере теневой пасс - без него god rays невозможны, остаётся ровный
		/// объёмный туман (см. VolumetricLightPassResources.ShadowsAvailable). false и когда сам пасс
		/// выключен.</summary>
		public bool VolumetricShadowsAvailable => Pipeline.VolumetricResources?.ShadowsAvailable ?? false;

		/// <summary>Живые ручки цветокоррекции (см. ColorGradePassResources). No-op когда грейдинг
		/// выключен: сама галка - уровня окружения, пасс создаётся вместе с конвейером.</summary>
		public void SetColorGrade(float saturation, float contrast, float gamma, float temperature,
			float tint, Vector3 shadowTint, Vector3 highlightTint)
		{
			Pipeline.ColorGradeResources?.SetGrade(saturation, contrast, gamma, temperature, tint);
			Pipeline.ColorGradeResources?.SetTints(shadowTint, highlightTint);
		}

		/// <summary>Живые ручки виньетки (см. ColorGradePassResources.SetVignette).</summary>
		public void SetVignette(float intensity, float radius, float smoothness, float roundness)
		{
			Pipeline.ColorGradeResources?.SetVignette(intensity, radius, smoothness, roundness);
		}

		/// <summary>Кривая тонмапа HDR-конвейера (см. Tonemap.hlsl). No-op в LDR - там кривую
		/// применяет сам UnlitInstancedPS по полю кбуфера PreviewSettings.</summary>
		public void SetToneCurve(int curve)
		{
			Pipeline.TonemapResources?.SetCurve(curve);
		}

		/// <summary>Живые ручки блума (см. BloomPassResources). No-op когда блум выключен: сама
		/// галка - уровня окружения, пасс создаётся вместе с конвейером.</summary>
		public void SetBloomParams(float threshold, float knee, float radius, float intensity)
		{
			Pipeline.BloomResources?.SetParams(threshold, knee, radius, intensity);
		}

		/// <summary>Живые ручки композита SSGI: радиус билатерального размытия bounce и отладочный
		/// вид (см. SsgiPassResources.SetCompositeParams). No-op когда SSGI выключен.</summary>
		public void SetGiCompositeParams(int blurRadius, bool debugView)
		{
			Pipeline.SsgiResources?.SetCompositeParams(blurRadius, debugView);
		}

		/// <summary>Ручки авто-экспозиции - живые (см. EyeAdaptationPassResources.SetParams и
		/// TonemapPassResources.SetParams: key/экспокоррекцию тонемап и замер обязаны видеть
		/// одинаковыми). No-op когда авто-экспозиция выключена.</summary>
		public void SetEyeAdaptationParams(float key, float minLuminance, float maxLuminance,
			float exposureCompensation)
		{
			Pipeline.EyeAdaptationResources?.SetParams(key, minLuminance, maxLuminance, exposureCompensation);
			Pipeline.TonemapResources?.SetParams(key, exposureCompensation);

			// Тот же key - туману: его цвет задан в отображаемых единицах и пересчитывается в
			// линейные через adapted/key (см. FogPassResources.SetExposure). Разъедься эти два
			// значения - дымка разошлась бы с кадром по яркости.
			Pipeline.FogResources?.SetExposure(Pipeline.AutoExposure, key);
			Pipeline.BloomResources?.SetExposure(Pipeline.AutoExposure, key);
			Pipeline.VolumetricResources?.SetExposure(Pipeline.AutoExposure, key);
		}

		/// <summary>Скорости временной адаптации в 1/сек (к светлому / к тёмному) - живая ручка.
		/// No-op когда авто-экспозиция выключена.</summary>
		public void SetEyeAdaptationSpeed(float speedUp, float speedDown)
		{
			Pipeline.EyeAdaptationResources?.SetSpeed(speedUp, speedDown);
		}

		/// <summary>Шаг времени кадра для временной адаптации - пушится каждый кадр (см.
		/// ModelPreviewViewport.Update). No-op когда авто-экспозиция выключена.</summary>
		public void SetEyeAdaptationDeltaTime(float deltaTime)
		{
			Pipeline.EyeAdaptationResources?.SetDeltaTime(deltaTime);

			// Нативному апскейлеру нужна та же длительность кадра (темпоральная аккумуляция) -
			// пуш тем же путём, чтобы у вызывающих не было второй ручки, о которой можно забыть.
			Pipeline.NativeUpscaler?.SetDeltaTime(deltaTime);
		}

		/// <summary>Пропустить тонемап (копировать кадр как есть) - для отладочных режимов превью,
		/// которые пишут уже отображаемые значения (см. TonemapPassResources.SetPassthrough).
		/// No-op когда HDR-конвейер выключен.</summary>
		public void SetTonemapPassthrough(bool passthrough)
		{
			Pipeline.TonemapResources?.SetPassthrough(passthrough);

			// Небо - единственное, что рисуется в кадр НЕ шейдером модели, и про режим превью оно
			// само не знает: на время passthrough оно обязано писать display-space, иначе фон
			// отладочных видов уезжает в темноту (линейная яркость без энкода).
			if (HdrOutput)
			{
				Pipeline.SkyResources?.SetHdrOutput(!passthrough);
			}
		}

		/// <param name="skyBackground">Рисовать ли энвайронмент фоном кадра (см. SkyBackgroundVS/PS).
		/// Интерактивное превью включает - тогда в отражениях сфер и за моделью одно и то же небо;
		/// бейкер иконок оставляет false, чтобы PNG сохраняли прозрачный фон.</param>
		/// <param name="environmentHdrPath">Путь к equirect .hdr для IBL-окружения; null/пусто или
		/// ошибка чтения - процедурное небо (см. <see cref="PreviewEnvironmentMap.Create"/>).</param>
		/// <param name="ssao">Экранное контактное затемнение (AO-пасс, см. ForwardPass/SsaoCommon.hlsl).
		/// false = пасс не создаётся вовсе, кадр идентичен прежнему.</param>
		/// <param name="aoMode">Техника AO-пасса при включённом <paramref name="ssao"/>: классический
		/// SSAO или GTAO (см. <see cref="AmbientOcclusionMode"/>). Опция уровня создания окружения -
		/// шейдер пекётся в материалы SsaoPassResources.</param>
		/// <param name="shadows">Тени от мирового ключевого света («солнца» энвайронмента): shadow-пасс
		/// в графе + каскад в SimpleCullingAndRenderSystem. false = пасс не создаётся, свет остаётся
		/// камерным, кадр идентичен прежнему.</param>
		/// <param name="ssgi">Экранная глобальная иллюминация (SSGI-пасс, см. SsgiCommon.hlsl): один
		/// отскок света из уже отрисованного кадра (color bleeding). false = пасс не создаётся вовсе,
		/// кадр идентичен прежнему.</param>
		/// <param name="eyeAdaptation">Авто-экспозиция («адаптация глаза»): замер средней яркости
		/// кадра + временное сглаживание, по которым экспонируется финальный тонемап (см.
		/// EyeAdaptationPass / TonemapPass). Включает HDR-конвейер целиком - кадр становится линейным
		/// RGBA16F, а тонемап уезжает из UnlitInstancedPS в отдельный пасс; false = ни таргетов, ни
		/// пассов не создаётся, кадр идентичен прежнему. Опция уровня создания окружения: под формат
		/// цветового таргета пекутся PSO.</param>
		/// <param name="mainCascades">Каскады теней строит ОСНОВНОЙ <see cref="CullingAndRenderSystem"/>
		/// (точный камерный CSM основного пайплайна) вместо <see cref="SimpleCullingAndRenderSystem"/>:
		/// в сторе заводится солнце-сущность, дистанции каскадов и поворот солнца пушит вьюпорт
		/// (см. PrefabSceneViewport.SyncSunEntity). Только вместе с <paramref name="shadows"/>.</param>
		public ModelViewportEnvironment(IGraphicsApi graphicsApi, uint width, uint height,
			string colorTargetName, string depthTargetName, SharedViewportResources sharedResources,
			bool skyBackground = false,
			string environmentHdrPath = null, bool ssao = false, bool shadows = false,
			AmbientOcclusionMode aoMode = AmbientOcclusionMode.Ssao, bool ssgi = false, bool eyeAdaptation = false,
			bool mainCascades = false, bool fog = false, bool bloom = false, bool colorGrade = false,
			bool volumetric = false, bool motionVectors = false, bool temporalUpscale = false,
			int upscalerBackend = 0, bool ssr = false, bool ssrRayTraced = false)
		{
			GraphicsApi = graphicsApi;
			DilApi = (DiligentGraphicsApi)graphicsApi;
			SharedResources = sharedResources;

			// Формат цветового таргета пекётся во все PSO геометрии. Офскрин-конвейер теперь ВСЕГДА
			// HDR (см. GraphicsPipelineSimple), поэтому формат один и тот же при любом наборе фич -
			// именно поэтому тумблеры пост-обработки больше не требуют пересоздания окружения.
			//
			// Тонкий G-buffer отражений (MRT-слоты в PSO геометрии) - безусловно, по той же логике
			// безусловного HDR: тумблер SSR остаётся живой фичей, а не пересозданием окружения.
			// Модели при этом обязаны грузиться с ModelLoadOptions.ReflectionGbuffer = true (см.
			// BuildLoadOptions вьюпортов).
			BatchRenderer = new DiligentBatchRenderer(DilApi,
				TextureObjectFormat.R16G16B16A16Float, reflectionGbuffer: true);

			// Резолвится из общего контейнера - НЕ создаётся заново: несколько окружений с одним и тем
			// же HDRI-путём (или все процедурные) делят одну GPU-текстуру, см. SharedViewportResources.
			var environmentResult = sharedResources.GetEnvironment(environmentHdrPath);
			EnvironmentMap = environmentResult.Texture;
			EnvironmentRadiance = environmentResult.Radiance;

			if (shadows)
			{
				// Направление СВЕТА = от солнца энвайронмента (SunDirection указывает НА солнце).
				// Раскладываем его на азимут/высоту - опорные значения для ползунков поворота света
				// (см. ModelPreviewViewport.SetLightRotation); та же сферическая конвенция, что в
				// PreviewShadowSettings.SetAngles.
				var sun = Vector3.Normalize(environmentResult.SunDirection);
				var baseYaw = MathF.Atan2(sun.X, sun.Z) * (180f / MathF.PI);
				var baseElevation = MathF.Asin(Math.Clamp(sun.Y, -1f, 1f)) * (180f / MathF.PI);

				ShadowSettings = new PreviewShadowSettings
				{
					LightDirection = -sun,
					BaseYawDegrees = baseYaw,
					BaseElevationDegrees = baseElevation,
					YawDegrees = baseYaw,
					ElevationDegrees = baseElevation,
				};
			}

			// Цвет/депт/scene-copy/sky/SSAO-ресурсы создаёт и владеет ими Pipeline сам, изнутри
			// своего render-графа (см. GraphicsPipelineSimple, SkyPassResources, SsaoPass) - здесь
			// передаются только тумблеры/готовый EnvironmentMap.
			//
			// Alpha 0: геометрия пишет alpha 1, а фон остаётся прозрачным, так что при показе через
			// ImGui.Image (стандартный альфа-блендинг) подложку рисует UI - нейтральный градиент в
			// ModelPreviewViewport.Render, тема окна под иконками Asset Browser-а и т.п. RGB очистки
			// задаём средне-серым под тон подложки: билинейная фильтрация на краях силуэта
			// подмешивает цвет фоновых текселей, и сильно выбивающийся RGB давал бы грязную обводку.
			Pipeline = new GraphicsPipelineSimple(graphicsApi, BatchRenderer, colorTargetName, depthTargetName,
				width, height, new Vector4(0.4f, 0.4f, 0.4f, 0f),
				skyBackground: skyBackground, environmentMap: EnvironmentMap,
				ssao: ssao, enableShadowPass: shadows, aoMode: aoMode, ssgi: ssgi, eyeAdaptation: eyeAdaptation,
				fog: fog, bloom: bloom, colorGrade: colorGrade, volumetric: volumetric,
				motionVectors: motionVectors, temporalUpscale: temporalUpscale,
				ssr: ssr, ssrRayTraced: ssrRayTraced);

			if (upscalerBackend != 0 && temporalUpscale)
			{
				SetUpscalerBackend(upscalerBackend);
			}

			Store = new EntityStore();
			ResourceManager = new RenderResourceManager(16, 16, Store, BatchRenderer);

			var cameraComponent = new CameraComponent(new CameraData(CameraFovDegrees, 0.05f, 2000f,
				new Vector4(0, 0, width, height)));
			cameraComponent.data.cullFlags = CullFlags.None;

			CameraEntity = Store.CreateEntity(
				new Position(0, 0, -4f),
				new Rotation { value = Quaternion.Identity },
				new Scale3(1, 1, 1),
				cameraComponent);

			if (shadows && mainCascades)
			{
				// Солнце-сущность для основного CSM - тот же набор компонентов, что у солнца
				// основной сцены (см. EditorManager): направление света = +Z её поворота (вьюпорт
				// перезапишет из ShadowSettings первым же кадром, стартовое - от солнца
				// энвайронмента), цвет/интенсивность - константы SimpleCullingAndRenderSystem.
				// CascadeDistances ОБЯЗАНЫ быть валидными сразу: нулевые дистанции дают вырожденные
				// срезы (n == f) и NaN в матрицах каскадов ещё до первой синхронизации вьюпорта -
				// он подменит их на адаптивные к габаритам сцены (см. PrefabSceneViewport.SyncSunEntity).
				var sunDirection = ShadowSettings != null
					? Vector3.Normalize(ShadowSettings.LightDirection)
					: new Vector3(0.45f, -0.72f, -0.35f);
				var sunUp = MathF.Abs(sunDirection.Y) > 0.95f ? Vector3.UnitX : Vector3.UnitY;
				var sunView = Matrix4x4.CreateLookAtLeftHanded(Vector3.Zero, sunDirection, sunUp);

				SunEntity = Store.CreateEntity(
					new Position(0, 50, 0),
					new Rotation { value = Quaternion.CreateFromRotationMatrix(Matrix4x4.Transpose(sunView)) },
					new LightComponent
					{
						Type = LightType.Directional,
						Color = new Vector3(1f, 0.97f, 0.9f),
						Intensity = 1f,
						ShadowStrength = 1f,
					},
					new SunComponent()
					{

					},
					new CascadedShadowComponent
					{
						CascadeDistances = [0.01f, 10f, 30f, 100f, 300f]
					});

				_mainCullingSystem = new CullingAndRenderSystem(ResourceManager, graphicsApi, Pipeline);
				Root = new SystemRoot()
				{
					// Before GpuInstanceBufferSystem/culling: it produces WorldMatrix/WorldTransformDirtyTag
					// for hierarchy children (same ordering as EditorManager's real SystemRoot). Without
					// it, a light/entity nested under a parent with its own transform (Super > MegaEntity
					// > ...) never gets its WorldMatrix composed here, so LightCulling.GetWorldPositionRotation
					// silently falls back to raw local Position/Rotation - the exact gap that let the
					// nested-light punctual shadow defect go untested by every probe harness scenario.
					new DecaEngine.Core.Entities.TransformSystem(),
					new GpuInstanceBufferSystem(),
					_mainCullingSystem
				};
			}
			else
			{
				_cullingSystem = new SimpleCullingAndRenderSystem(ResourceManager, Pipeline, ShadowSettings);
				Root = new SystemRoot()
				{
					// See the TransformSystem comment in the branch above - same reasoning applies here.
					new DecaEngine.Core.Entities.TransformSystem(),
					new GpuInstanceBufferSystem(),
					_cullingSystem
				};
			}
			Root.AddStore(Store);
		}

		public void SetCameraTransform(Vector3 eye, Vector3 target)
		{
			var viewMatrix = Matrix4x4.CreateLookAtLeftHanded(eye, target, Vector3.UnitY);
			var rotation = Quaternion.CreateFromRotationMatrix(Matrix4x4.Transpose(viewMatrix));

			CameraEntity.Position = new Position(eye.X, eye.Y, eye.Z);
			CameraEntity.Rotation = new Rotation { value = rotation };

			// Мировой базис камеры - туману, чтобы он восстановил мировой луч пикселя. Считается
			// ИМЕННО ЗДЕСЬ, из eye/target, а не разбором viewMatrix: это ровно те три вектора, из
			// которых CreateLookAtLeftHanded и строит матрицу, так что перепутать строки со
			// столбцами (и получить туман, «приклеенный» к экрану) здесь просто негде.
			var fog = Pipeline.FogResources;
			var volumetric = Pipeline.VolumetricResources;
			if (fog is not null || volumetric is not null)
			{
				var forward = target - eye;
				forward = forward.LengthSquared() > 1e-8f ? Vector3.Normalize(forward) : Vector3.UnitZ;

				// Тот же порядок, что у левосторонней LookAt: x = up X z, y = z X x.
				var right = Vector3.Cross(Vector3.UnitY, forward);
				right = right.LengthSquared() > 1e-8f
					? Vector3.Normalize(right)
					// Взгляд строго вертикально вниз/вверх - up вырождается, берём любой
					// перпендикуляр, иначе базис схлопывается в ноль и туман выдаёт NaN.
					: Vector3.UnitX;

				var up = Vector3.Cross(forward, right);
				fog?.SetCamera(right, up, forward);

				// Объёмный свет восстанавливает мировой луч пикселя ровно так же и потому получает
				// тот же базис - считать его дважды по-разному значило бы завести два источника
				// правды об одном и том же.
				volumetric?.SetCamera(right, up, forward);

				// Солнце пушится ЗДЕСЬ ЖЕ, а не из ApplyGraphicsSettings: в Scene View его вращают
				// гизмо сущности, а то не поднимает событие настроек - подсветка дымки и столбы
				// отставали бы от света до следующего движения ползунка. LightDirection указывает
				// ОТ солнца.
				if (ShadowSettings is not null)
				{
					fog?.SetSun(-ShadowSettings.LightDirection);
					volumetric?.SetSun(-ShadowSettings.LightDirection);
				}
			}
		}
	}

	/// <summary>
	/// Mesh/material registration, instance-entity creation and camera-framing math shared between
	/// <see cref="ModelPreviewViewport"/> and <see cref="ModelIconBaker"/> - both populate a
	/// <see cref="ModelViewportEnvironment"/> from a loaded <see cref="ModelLoader"/> and frame a camera
	/// around either the whole model or a single sub-mesh.
	/// </summary>
	public static class ModelViewportGeometry
	{
		/// <param name="materials">Какой набор материалов регистрировать - по умолчанию (null) это
		/// ПЕРВЫЙ, встроенный в <paramref name="modelLoader"/> набор (<see cref="ModelLoader.materialObjects"/>).
		/// Второе (третье, ...) окружение того же РАЗДЕЛЯЕМОГО <paramref name="modelLoader"/> (см.
		/// DecaEngine.Editor.ECS.ModelStore) обязано передать сюда СВОЙ набор из
		/// <see cref="ModelLoader.BuildAdditionalMaterialSet"/> - регистрация мутирует материалы (см.
		/// class-doc DiligentBatchRenderer.Register), так что делить один и тот же набор между двумя
		/// батч-рендерерами нельзя.</param>
		/// <param name="envMapSampler">Общий "_EnvMap_Sampler" (см. <see cref="SharedViewportResources"/>) -
		/// привязывается вместе с <paramref name="environmentMap"/>, null пропускает энвайронмент-биндинг
		/// целиком (сохраняет прежнее поведение "нет графического API/окружения").</param>
		/// <param name="sceneCopySampler">Общий "_SceneColor_Sampler" (см. <see cref="SharedViewportResources"/>) -
		/// привязывается вместе с <paramref name="sceneCopy"/> только к transmissive-материалам.</param>
		/// <summary>
		/// Аварийный выключатель скиннинга: <c>DECA_SKINNING=0</c> регистрирует скиннед-меши как
		/// обычные статические (bind-поза), не заводя ни отдельных инстансов в мега-буфере, ни
		/// батчей под них, ни compute-прохода. Нужен для локализации: делит вопрос «падает из-за
		/// скиннинга или из-за чего-то ещё» на два независимых, каждый проверяется одним запуском.
		/// </summary>
		/// <remarks>Переменная окружения задаёт лишь СТАРТОВОЕ значение и остаётся как аварийный
		/// путь: она действует до чтения настроек, то есть работает даже когда редактор падает
		/// раньше, чем доходит до окна Graphics. Дальше значение ведёт настройка
		/// <see cref="EditorSettings.SceneSkinning"/>.</remarks>
		public static bool SkinningEnabled { get; set; } =
			Environment.GetEnvironmentVariable("DECA_SKINNING") != "0";

		public static void RegisterModelResources(DiligentBatchRenderer batchRenderer, ModelLoader modelLoader,
			Dictionary<int, MeshId> meshIdMap, Dictionary<int, MaterialId> materialIdMap,
			ISamplerObject? envMapSampler = null, IGpuTexture? sceneCopy = null, IGpuTexture? environmentMap = null,
			OrderedDictionary<int, IMaterialObject>? materials = null, ISamplerObject? sceneCopySampler = null,
			Dictionary<int, int>? skinBaseMap = null)
		{
			var materialSet = materials ?? modelLoader.materialObjects;

			// Энвайронмент-мип-сэмплер и сэмплер снимка сцены (_SceneColor, см. ForwardPass/
			// UnlitInstancedPS) - оба переданы вызывающим из общего SharedViewportResources, а не
			// создаются здесь: раньше каждый вызов регистрации плодил новую пару сэмплеров (пер модель
			// ПЕР окружение) для одного и того же неизменного sampler state.
			var environmentSampler = environmentMap != null ? envMapSampler : null;

			var baseMaterialState = batchRenderer.GetBaseState();
			IStateObject? lineListState = null, lineStripState = null, pointState = null;

			for (int i = 0; i < materialSet.Count; i++)
			{
				var kvp = materialSet.GetAt(i);

				// Не-треугольные материалы-клоны (см. ModelLoader.MakeTopologyMaterialKey) получают
				// PSO с соответствующей примитивной топологией; стейты шарятся между материалами.
				int topology = modelLoader.MaterialPbr.TryGetValue(kvp.Key, out var pbrTopology) ? pbrTopology.Topology : 0;
				kvp.Value.SetState(topology switch
				{
					ModelLoader.MeshTopologyLineList => lineListState ??= batchRenderer.GetTopologyState(PrimitiveTopologyType.LineList),
					ModelLoader.MeshTopologyLineStrip => lineStripState ??= batchRenderer.GetTopologyState(PrimitiveTopologyType.LineStrip),
					ModelLoader.MeshTopologyPoints => pointState ??= batchRenderer.GetTopologyState(PrimitiveTopologyType.PointList),
					_ => baseMaterialState,
				});
				materialIdMap[kvp.Key] = batchRenderer.Register(kvp.Value);

				if (environmentSampler != null)
				{
					kvp.Value.SetTexture("_EnvMap", environmentMap);
					kvp.Value.SetImmutableSampler("_EnvMap", environmentSampler);

					// Placeholder для атласов probe-GI: слоты объявлены в шейдере безусловно, и без
					// привязки дескрипторы невалидны (Vulkan validation VUID-08114), даже когда
					// probe-GI выключен и шейдер их не читает (ProbeGridOrigin.w=0). Настоящие атласы
					// перепривяжет ProbeGiTextures.Bind после бейка. Сэмплер не нужен - Load-only.
					kvp.Value.SetTexture("_ProbeSh0", environmentMap);
					kvp.Value.SetTexture("_ProbeSh1", environmentMap);
					kvp.Value.SetTexture("_ProbeSh2", environmentMap);
					kvp.Value.SetTexture("_ProbeSh3", environmentMap);
					kvp.Value.SetTexture("_ProbeVis", environmentMap);
					kvp.Value.SetTexture("_ProbeOffset", environmentMap);

					// Каскады: слоты объявлены безусловно, как и базовые - без плейсхолдера
					// дескрипторы невалидны (VUID-08114), даже когда каскадов нет и мёртвая ветка
					// шейдера их не читает. Настоящие атласы перепривяжет ProbeGiTextures.Bind.
					foreach (var suffix in new[] { "_C1", "_C2" })
					{
						kvp.Value.SetTexture($"_ProbeSh0{suffix}", environmentMap);
						kvp.Value.SetTexture($"_ProbeSh1{suffix}", environmentMap);
						kvp.Value.SetTexture($"_ProbeSh2{suffix}", environmentMap);
						kvp.Value.SetTexture($"_ProbeSh3{suffix}", environmentMap);
						kvp.Value.SetTexture($"_ProbeVis{suffix}", environmentMap);
						kvp.Value.SetTexture($"_ProbeOffset{suffix}", environmentMap);
					}
				}

				// Слот объявлен в шейдере только под MATERIAL_TRANSMISSION (см. UnlitInstancedPS.hlsl) -
				// у остальных материалов этот кейворд выключен, и ресурса просто нет в PSO; привязка
				// к ним оставляла бы immutable sampler без соответствующего шейдерного ресурса
				// (Diligent-варнинг "not assigned to any texture or sampler").
				if (sceneCopySampler != null && sceneCopy != null &&
					modelLoader.MaterialPbr.TryGetValue(kvp.Key, out var pbr) && pbr.TransmissionFactor > 0f)
				{
					kvp.Value.SetTexture("_SceneColor", sceneCopy);
					kvp.Value.SetImmutableSampler("_SceneColor", sceneCopySampler);

					batchRenderer.SetMaterialTransparent(materialIdMap[kvp.Key], true);
				}

				// Альфа-тест при записи тени - только для РЕАЛЬНО дырявой геометрии (листва). Критерий
				// дословно тот же, что у probe-GI при отборе треугольников в BVH (см. ProbeGi.cs), и
				// по той же причине: одной пометки MASK недостаточно - экспортеры сплошь метят ею
				// камень и ткань с альфой ~1, и по режиму под альфа-тест уехала бы вся сцена. Средняя
				// альфа отделяет крону (много прозрачного) от них. А вот BLEND - случай отдельный,
				// см. ниже.
				//
				// Цена ошибки здесь не косметическая: каждый такой материал добавляет дроу-колл в
				// КАЖДЫЙ теневой каскад (см. ShadowRenderer.ExecuteDrawShadows).
				if (modelLoader.MaterialPbr.TryGetValue(kvp.Key, out var maskPbr))
				{
					// МЯГКАЯ накладка - не кастер вовсе, ни сплошным квадом, ни по альфе. Это декали
					// грязи и потёков: они лежат в миллиметрах от стены, которую украшают, и их тень
					// падает на эту же стену, дублируя их собственный рисунок крупными тёмными
					// кляксами. Альфа-тест тут не лечит ничего - он лишь меняет форму кляксы со
					// сплошного квада на форму текстуры, ровно это и было видно на скриншотах.
					//
					// Отбор НЕ по одному alphaMode: этот ассет метит BLEND-ом и декали, и крону
					// (dirt_decal и LeafSpring - все BLEND), так что по режиму вместе с кляксами
					// уехала бы и тень дерева. Различает их бинарность альфы: у вырезки она почти
					// везде 0 или 1 (бинарная тень осмысленна), у мягкой размазки размазана по
					// диапазону (бинарной тени у неё быть не может в принципе).
					//
					// Неизвестная бинарность (-1: пикселей не было, кеш ещё не запечён) считается
					// вырезкой - то есть измерение способно тень только УБРАТЬ, но не появиться из
					// ниоткуда; на промахе кеша поведение остаётся прежним.
					if (maskPbr.AlphaMode == MaterialAlphaMode.Blend && maskPbr.SoftAlphaFraction > 0.25f)
					{
						batchRenderer.SetMaterialShadowCasting(materialIdMap[kvp.Key].materialId, false);
					}
					else if (maskPbr.AlphaCutoff > 0f && maskPbr.HasBaseColorTexture && maskPbr.AverageAlpha < 0.6f &&
						modelLoader.MaterialBaseColor.TryGetValue(kvp.Key, out var maskBaseColor))
					{
						batchRenderer.SetMaterialAlphaTestedShadow(materialIdMap[kvp.Key].materialId, maskBaseColor,
							maskPbr.AlphaCutoff);
					}
				}
			}

			for (int i = 0; i < modelLoader.Meshes.Count; i++)
			{
				// Пустой меш (без единого индекса - в glTF бывают меши без треугольников/только
				// точки-линии без геометрии) не регистрируем вовсе: batch с нулевым draw-каунтом
				// в лучшем случае рисует "ничего" в очищенный таргет, в худшем - ломает нативный
				// indirect-draw. Инстансы такого меша отсеются в CreateInstanceEntity по отсутствию
				// ключа в meshIdMap, и бейкер/превью корректно пропустят этап (см. BakeNextStage).
				if (modelLoader.Meshes[i].IndexCount == 0)
				{
					continue;
				}

				meshIdMap[i] = batchRenderer.Register(modelLoader.Meshes[i]);

				// Скин-стрим меша - в общий GPU-буфер скиннинга. Один раз на МЕШ, а не на инстанс:
				// веса у всех инстансов одной модели общие, различается только палитра.
				if (SkinningEnabled && skinBaseMap != null &&
					i < modelLoader.MeshSkin.Count && modelLoader.MeshSkin[i] != null)
				{
					skinBaseMap[i] = batchRenderer.Skinning.RegisterSkinStream(modelLoader.MeshSkin[i]);
				}
			}
		}

		/// <summary>
		/// Creates one instance entity for the given mesh/material, reusing (and lazily creating) the
		/// batch for that (meshIndex, materialIndex) pair. Returns null if meshIndex has no registered
		/// mesh (e.g. dead reference) - caller should skip it.
		/// </summary>
		public static Entity? CreateInstanceEntity(EntityStore store, RenderResourceManager resourceManager,
			DiligentBatchRenderer batchRenderer, Dictionary<int, MeshId> meshIdMap,
			Dictionary<int, MaterialId> materialIdMap, Dictionary<(int, int), BatchId> batchCache,
			int meshIndex, int materialIndex, DecaEngine.Core.Transform t,
			ModelLoader? skinnedModel = null, Dictionary<int, int>? skinBaseMap = null,
			Action<int>? onSkinnedPalette = null)
		{
			if (!meshIdMap.TryGetValue(meshIndex, out var meshId))
			{
				return null;
			}

			// Скиннед-меш: инстансу нужен СВОЙ приёмник деформированных вершин и свой участок
			// палитры, поэтому и meshId, и батч у него собственные - общий батч означал бы, что все
			// персонажи с этой моделью рисуют одну и ту же позу. Кеш батчей для таких мешей поэтому
			// намеренно не используется (см. ниже).
			int paletteOffset = -1;
			bool skinned = SkinningEnabled &&
				skinnedModel?.Skeleton != null &&
				skinBaseMap != null &&
				onSkinnedPalette != null &&
				skinBaseMap.ContainsKey(meshIndex);

			if (skinned)
			{
				(meshId, paletteOffset) = batchRenderer.RegisterSkinnedInstance(
					meshId, skinnedModel!.Skeleton.JointCount, skinBaseMap![meshIndex]);
			}

			if (!materialIdMap.TryGetValue(materialIndex, out var matId))
			{
				if (materialIdMap.Count == 0)
				{
					// No material registered at all for this model - falling through would leave matId
					// as default(MaterialId) (id 0), which was never registered with the batch renderer.
					// Drawing a batch that references it hits an invalid material slot on the native
					// (Diligent) side, which is undefined behavior rather than a catchable .NET exception.
					return null;
				}

				foreach (var candidate in materialIdMap.Values)
				{
					matId = candidate;
					break;
				}
			}

			BatchId batchId;
			if (skinned)
			{
				batchId = batchRenderer.CreateBatch(meshId, matId);
			}
			else if (!batchCache.TryGetValue((meshIndex, materialIndex), out batchId))
			{
				batchId = batchRenderer.CreateBatch(meshId, matId);
				batchCache[(meshIndex, materialIndex)] = batchId;
			}

			var entity = store.CreateEntity(
				new Position(t.position.X, t.position.Y, t.position.Z),
				new Scale3(t.scale.X, t.scale.Y, t.scale.Z),
				new Rotation(t.rotation.X, t.rotation.Y, t.rotation.Z, t.rotation.W),
				Tags.Get<GpuUpdateTag>());

			resourceManager.RegisterRenderable(entity, batchId);

			if (skinned)
			{
				// Наружу отдаётся только офсет палитры: КОМУ принадлежит этот скиннед-инстанс,
				// решает вызывающий (в сцене - сущность префаба, в пробнике - сам пробник), и
				// тащить сюда его модель владения незачем.
				onSkinnedPalette!(paletteOffset);
			}

			return entity;
		}

		/// <summary>
		/// AABB of one sub-mesh across its instances (bounding-sphere of the mesh, transformed by each
		/// instance). If the sub-mesh has no instances, falls back to its local bounding sphere.
		/// </summary>
		public static (Vector3 Min, Vector3 Max) ComputeSubMeshBounds(ModelLoader model, int meshIndex)
		{
			var mesh = model.Meshes[meshIndex];
			var min = new Vector3(float.PositiveInfinity);
			var max = new Vector3(float.NegativeInfinity);
			var any = false;

			foreach (var instance in model.instances)
			{
				if (instance.meshId != meshIndex)
				{
					continue;
				}

				var t = instance.transform;
				var worldCenter = Vector3.Transform(mesh.Center * t.scale, t.rotation) + t.position;
				var maxScale = MathF.Max(MathF.Abs(t.scale.X), MathF.Max(MathF.Abs(t.scale.Y), MathF.Abs(t.scale.Z)));
				var radius = mesh.Radius * maxScale;

				min = Vector3.Min(min, worldCenter - new Vector3(radius));
				max = Vector3.Max(max, worldCenter + new Vector3(radius));
				any = true;
			}

			if (!any)
			{
				min = mesh.Center - new Vector3(mesh.Radius);
				max = mesh.Center + new Vector3(mesh.Radius);
			}

			return (min, max);
		}

		/// <summary>Distance at which a bounding sphere of the given radius exactly fills the vertical FOV, plus a margin.</summary>
		public static float ComputeFramingDistance(float radius, float fovDegrees)
		{
			var halfFovRad = fovDegrees * (MathF.PI / 180f) * 0.5f;
			return Math.Clamp(radius / MathF.Sin(halfFovRad) * 1.25f, 0.2f, 1500f);
		}

		public static Vector3 ComputeOrbitEye(Vector3 target, float distance, float yaw, float pitch)
		{
			return target + distance * new Vector3(
				MathF.Cos(pitch) * MathF.Sin(yaw),
				MathF.Sin(pitch),
				MathF.Cos(pitch) * MathF.Cos(yaw));
		}
	}
}
