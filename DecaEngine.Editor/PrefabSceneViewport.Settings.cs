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
	/// <summary>Применение окна Graphics к сцене: фичи конвейера, живые ручки, пересоздание окружения. Часть <see cref="PrefabSceneViewport"/> - файл на тему;
	/// состояние, конструктор и кадровые Update/Render живут в основном файле.</summary>
	public partial class PrefabSceneViewport
	{
		private void ApplyPendingUpscalerSettings()
		{
			if (!_pendingUpscalerApply || _env is null)
			{
				return;
			}

			_pendingUpscalerApply = false;
			ViewportSettingsPush.Upscaler(_env, _editorSettings);
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
				// Чекбокс «Auto exposure» действует и на сцену - раньше её автоэкспозиция ходила
				// строго за HDR-тумблером, и снятая галка (по подписи - общая) молча игнорировалась.
				EyeAdaptation = _appliedSceneHdr && _editorSettings.PreviewEyeAdaptation,
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
		private void ApplyBloomSettings() => ViewportSettingsPush.Bloom(_env, _editorSettings);

		private void ApplyColorGradeSettings() => ViewportSettingsPush.ColorGrade(_env, _editorSettings);

		private void ApplyFogSettings() => ViewportSettingsPush.Fog(_env, _editorSettings);

		private void ApplyVolumetricSettings() => ViewportSettingsPush.Volumetric(_env, _editorSettings);

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

	}
}
