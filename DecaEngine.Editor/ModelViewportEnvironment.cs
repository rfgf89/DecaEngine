using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Core;
using DecaEngine.Graphics.Diligent;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;

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

		// Explicit padding up to the next 16-byte boundary, so the float4s below stay aligned with
		// the HLSL cbuffer layout (SetConstant uploads Marshal.SizeOf rounded UP to 16).
		public int Pad1;

		/// <summary>KHR_materials_sheen: rgb = sheenColorFactor (ноль = выключено), w =
		/// sheenRoughnessFactor. См. <see cref="MaterialPbrFactors.SheenColorRoughness"/>.</summary>
		public Vector4 SheenColorRoughness;

		/// <summary>KHR_materials_specular: rgb = specularColorFactor (может быть &gt;1, кламп в
		/// шейдере после умножения на F0 от IOR), w = specularFactor. Каждый пуш в Lighting-режиме
		/// обязан заполнить его ((1,1,1,1) = тождественно) - нулевой w глушит спекуляр в чёрный.
		/// См. <see cref="MaterialPbrFactors.SpecularColorFactor"/>.</summary>
		public Vector4 SpecularColorFactor;
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

		/// <summary>Создаёт и владеет всеми офскрин-таргетами (цвет/депт/scene-copy/MSAA/AO)
		/// <see cref="Pipeline"/> сам - как свопчейн владел бы back buffer-ом, см.
		/// <see cref="GraphicsPipelineSimple"/>. Свойства ниже - тонкие проксирующие делегаты, чтобы
		/// вызывающему коду (ModelPreviewViewport, ModelIconBaker) не пришлось лезть в Pipeline самому.</summary>
		public IRenderTarget ColorTarget => Pipeline.Targets!.ColorTarget;
		public IRenderTarget DepthTarget => Pipeline.Targets!.DepthTarget;

		/// <summary>Сэмплируемая копия <see cref="ColorTarget"/> после opaque-дроу - источник
		/// рефракции для transmissive-материалов (см. ForwardPass / UnlitInstancedPS.hlsl).</summary>
		public IRenderTarget SceneCopyTarget => Pipeline.Targets!.SceneCopyTarget;

		/// <summary>Процедурный equirect-энвайронмент с префильтрованными по roughness мипами -
		/// источник отражений/ambient-освещения Lighting-режима (см. <see cref="PreviewEnvironmentMap"/>).</summary>
		public IGpuTexture EnvironmentMap { get; }

		/// <summary>MSAA sample count окружения (1 = выключено).</summary>
		public uint MsaaSamples { get; }

		/// <summary>Мультисемпловая пара таргетов при <see cref="MsaaSamples"/> &gt; 1, иначе null -
		/// геометрия рисуется в них и резолвится в <see cref="ColorTarget"/> (см. ForwardPass).</summary>
		public IRenderTarget? MsaaColorTarget => Pipeline.Targets?.MsaaColorTarget;
		public IRenderTarget? MsaaDepthTarget => Pipeline.Targets?.MsaaDepthTarget;

		/// <summary>AO-таргет SSAO-пасса (null = SSAO выключен). Владеет им и пересоздаёт при
		/// Resize <see cref="Pipeline"/> - см. <see cref="GraphicsPipelineSimple.SsaoResources"/>.</summary>
		public IRenderTarget? AoTarget => Pipeline.SsaoResources?.AoTarget;

		/// <summary>GI-таргет SSGI-пасса (null = SSGI выключен). Владеет им и пересоздаёт при
		/// Resize <see cref="Pipeline"/> - см. <see cref="GraphicsPipelineSimple.SsgiResources"/>.</summary>
		public IRenderTarget? GiTarget => Pipeline.SsgiResources?.GiTarget;

		/// <summary>Состояние мирового света/теней (null = тени выключены). Вьюпорт обновляет баунды
		/// после загрузки модели (см. ModelPreviewViewport.FrameAll).</summary>
		public PreviewShadowSettings ShadowSettings { get; }

		private SimpleCullingAndRenderSystem _cullingSystem;

		/// <summary>Освобождает GPU-ресурсы окружения - для пересоздания превью с новыми
		/// рестарт-левел опциями на лету (см. ModelPreviewViewport.RecreateEnvironment). Вызывающий
		/// обязан сперва дождаться GPU (Flush + WaitForIdle) и отвязать ImGui-биндинги таргетов.
		/// Материалы/меши резидентных моделей не трогаются - они пересоздадутся перезагрузкой
		/// модели (та же семантика, что у резидент-кеша всегда была).</summary>
		public void Release()
		{
			// Освобождает и все офскрин-таргеты (цвет/депт/scene-copy/MSAA), и sky/SSAO-ресурсы
			// (материалы + AO-таргет) - их создаёт и владеет ими Pipeline (см. GraphicsPipelineSimple),
			// а не это окружение.
			Pipeline.Release();
			_cullingSystem.Dispose();
			BatchRenderer.Release();

			EnvironmentMap.Release();
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

		/// <summary>Мировой радиус сбора GI-пасса (см. SsgiPassResources.SetWorldRange): пушится
		/// вместе с AO-радиусом после кадрирования модели. No-op когда SSGI выключен.</summary>
		public void SetGiWorldRange(float worldRange)
		{
			Pipeline.SsgiResources?.SetWorldRange(worldRange);
		}

		/// <param name="skyBackground">Рисовать ли энвайронмент фоном кадра (см. SkyBackgroundVS/PS).
		/// Интерактивное превью включает - тогда в отражениях сфер и за моделью одно и то же небо;
		/// бейкер иконок оставляет false, чтобы PNG сохраняли прозрачный фон.</param>
		/// <param name="environmentHdrPath">Путь к equirect .hdr для IBL-окружения; null/пусто или
		/// ошибка чтения - процедурное небо (см. <see cref="PreviewEnvironmentMap.Create"/>).</param>
		/// <param name="msaaSamples">MSAA (1 = выключено): геометрия рисуется в мультисемпловую
		/// пару таргетов и резолвится в <see cref="ColorTarget"/> (см. ForwardPass). Тумблер уровня
		/// создания окружения - PSO пекутся под sample count.</param>
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
		public ModelViewportEnvironment(IGraphicsApi graphicsApi, uint width, uint height,
			string colorTargetName, string depthTargetName, bool skyBackground = false,
			string environmentHdrPath = null, uint msaaSamples = 1, bool ssao = false, bool shadows = false,
			AmbientOcclusionMode aoMode = AmbientOcclusionMode.Ssao, bool ssgi = false)
		{
			GraphicsApi = graphicsApi;
			DilApi = (DiligentGraphicsApi)graphicsApi;
			MsaaSamples = Math.Max(1u, msaaSamples);

			BatchRenderer = new DiligentBatchRenderer(DilApi, MsaaSamples);

			var environmentResult = PreviewEnvironmentMap.Create(graphicsApi, environmentHdrPath);
			EnvironmentMap = environmentResult.Texture;

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

			// Цвет/депт/scene-copy/MSAA/sky/SSAO-ресурсы создаёт и владеет ими Pipeline сам, изнутри
			// своего render-графа (см. GraphicsPipelineSimple, SkyPassResources, SsaoPass) - здесь
			// передаются только тумблеры/готовый EnvironmentMap.
			//
			// Alpha 0: геометрия пишет alpha 1, а фон остаётся прозрачным, так что при показе через
			// ImGui.Image (стандартный альфа-блендинг) подложку рисует UI - нейтральный градиент в
			// ModelPreviewViewport.Render, тема окна под иконками Asset Browser-а и т.п. RGB очистки
			// задаём средне-серым под тон подложки: билинейная фильтрация на краях силуэта
			// подмешивает цвет фоновых текселей, и сильно выбивающийся RGB давал бы грязную обводку.
			Pipeline = new GraphicsPipelineSimple(graphicsApi, BatchRenderer, colorTargetName, depthTargetName,
				width, height, new Vector4(0.4f, 0.4f, 0.4f, 0f), MsaaSamples,
				skyBackground: skyBackground, environmentMap: EnvironmentMap,
				ssao: ssao, enableShadowPass: shadows, aoMode: aoMode, ssgi: ssgi);

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

			_cullingSystem = new SimpleCullingAndRenderSystem(ResourceManager, Pipeline, ShadowSettings);
			Root = new SystemRoot()
			{
				new GpuInstanceBufferSystem(),
				_cullingSystem
			};
			Root.AddStore(Store);
		}

		public void SetCameraTransform(Vector3 eye, Vector3 target)
		{
			var viewMatrix = Matrix4x4.CreateLookAtLeftHanded(eye, target, Vector3.UnitY);
			var rotation = Quaternion.CreateFromRotationMatrix(Matrix4x4.Transpose(viewMatrix));

			CameraEntity.Position = new Position(eye.X, eye.Y, eye.Z);
			CameraEntity.Rotation = new Rotation { value = rotation };
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
		public static void RegisterModelResources(DiligentBatchRenderer batchRenderer, ModelLoader modelLoader,
			Dictionary<int, MeshId> meshIdMap, Dictionary<int, MaterialId> materialIdMap,
			IGraphicsApi? graphicsApi = null, IGpuTexture? sceneCopy = null, IGpuTexture? environmentMap = null)
		{
			// Энвайронмент-мип-сэмплер: трилинейный + Wrap, чтобы equirect-шов по горизонтали
			// заворачивался бесшовно, а SampleLevel по roughness блендил соседние мипы.
			ISamplerObject? environmentSampler = null;
			if (graphicsApi != null && environmentMap != null)
			{
				environmentSampler = graphicsApi.CreateSampler(
					name: "_EnvMap_Sampler",
					filter: TextureFilter.Linear,
					address: TextureAddress.Wrap,
					comparisonFunction: CompFunction.Always,
					border: Vector4.Zero);
			}

			// Сэмплер снимка сцены (см. ForwardPass/UnlitInstancedPS: _SceneColor) - линейный clamp,
			// чтобы рефракционный UV-сдвиг за краем экрана растягивал крайние пиксели, а не заворачивал
			// картинку с противоположной стороны.
			ISamplerObject? sceneCopySampler = null;
			if (graphicsApi != null && sceneCopy != null)
			{
				sceneCopySampler = graphicsApi.CreateSampler(
					name: "_SceneColor_Sampler",
					filter: TextureFilter.Linear,
					address: TextureAddress.Clamp,
					comparisonFunction: CompFunction.Always,
					border: Vector4.Zero);
			}

			var baseMaterialState = batchRenderer.GetBaseState();
			IStateObject? lineListState = null, lineStripState = null, pointState = null;

			for (int i = 0; i < modelLoader.materialObjects.Count; i++)
			{
				var kvp = modelLoader.materialObjects.GetAt(i);

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
				}

				// Слот объявлен в шейдере только под MATERIAL_TRANSMISSION (см. UnlitInstancedPS.hlsl) -
				// у остальных материалов этот кейворд выключен, и ресурса просто нет в PSO; привязка
				// к ним оставляла бы immutable sampler без соответствующего шейдерного ресурса
				// (Diligent-варнинг "not assigned to any texture or sampler").
				if (sceneCopySampler != null &&
					modelLoader.MaterialPbr.TryGetValue(kvp.Key, out var pbr) && pbr.TransmissionFactor > 0f)
				{
					kvp.Value.SetTexture("_SceneColor", sceneCopy);
					kvp.Value.SetImmutableSampler("_SceneColor", sceneCopySampler);

					batchRenderer.SetMaterialTransparent(materialIdMap[kvp.Key], true);
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
			int meshIndex, int materialIndex, DecaEngine.Graphics.Transform t)
		{
			if (!meshIdMap.TryGetValue(meshIndex, out var meshId))
			{
				return null;
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

			if (!batchCache.TryGetValue((meshIndex, materialIndex), out var batchId))
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
