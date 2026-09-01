using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics.Diligent;
using DecaEngine.Graphics.ProbeGi;
using Engine.ImGui.Core;
using Friflo.Engine.ECS;
using Hexa.NET.ImGui;
using DecaEngine.Scene;
using DecaEngine.Animation;
using DecaEngine.Graphics;

namespace DecaEngine.Editor;

/// <summary>Экранные эффекты: AO (SSAO/GTAO), SSR и SSGI. Часть <see cref="GraphicsSettingsWindow"/> - файл на тему,
/// поля и применение изменений живут в основном файле.</summary>
public partial class GraphicsSettingsWindow
{
	/// <summary>Экранное затенение (см. SsaoPass). Сам тумблер - фича конвейера: ресурсы пасса
	/// заводятся на живом окружении по SetFeatures, модель не перечитывается (раньше секция обещала
	/// обратное - обещание протухло вместе с переездом фич на живой конвейер).</summary>
	private void DrawAoSection()
	{
		ImGui.Spacing();

		var ssao = _settings.PreviewSsao;
		if (ImGui.Checkbox("Ambient occlusion (screen-space)", ref ssao))
		{
			_settings.PreviewSsao = ssao;
			_changed = true;
		}
		Tooltip("Затенение в стыках и нишах по глубине кадра. Применяется живьём.");

		if (ssao)
		{
			var aoModeLabels = new[] { "SSAO", "GTAO" };
			var aoModeIndex = _settings.PreviewAoMode == AmbientOcclusionMode.Gtao ? 1 : 0;
			ImGui.SetNextItemWidth(120 * _scale);
			if (ImGui.Combo("AO technique", ref aoModeIndex, aoModeLabels, aoModeLabels.Length))
			{
				_settings.PreviewAoMode = aoModeIndex == 1 ? AmbientOcclusionMode.Gtao : AmbientOcclusionMode.Ssao;
				_changed = true;
			}
			Tooltip("SSAO - классическое спиральное затемнение.\nGTAO - горизонты + интеграл видимости: чище на плоскостях, чуть дороже.");

			// Ниже - кбуфер AoConstants: пуш на кадре, без перестройки конвейера, поэтому и не
			// вызывают _changed = true (см. Slider).
			var aoStrength = _settings.AoStrength;
			if (Slider("AO strength", ref aoStrength, 0.25f, 4f, "%.2f"))
			{
				_settings.AoStrength = aoStrength;
			}
			Tooltip("Контраст затемнения (степень видимости у GTAO, множитель интенсивности у SSAO).");

			var aoFloor = _settings.AoFloor;
			if (Slider("AO floor", ref aoFloor, 0f, 0.5f, "%.2f"))
			{
				_settings.AoFloor = aoFloor;
			}
			Tooltip("Нижний предел видимости: экранный AO - косвенная оценка и не вправе гасить свет в ноль.\n0 = разрешить полное затемнение.");

			var aoRadiusWorld = _settings.AoRadiusWorld;
			if (Slider("AO radius (world)", ref aoRadiusWorld, 0f, 5f, "%.2f"))
			{
				_settings.AoRadiusWorld = aoRadiusWorld;
			}
			Tooltip("Радиус поиска в МИРОВЫХ единицах. 0 - считать от габаритов модели ручкой ниже.\nНа сцене-уровне (Sponza: радиус баундов ~50) доля от баундов даёт метры, и тонкая\nгеометрия - шторы, флаги, листва - вместо контактной тени кладёт широкое пятно.\nДля таких сцен ставь 0.2-0.5.");

			var aoRadius = _settings.AoRadiusFraction;
			if (Slider("AO radius (bounds)", ref aoRadius, 0.02f, 0.6f, "%.3f"))
			{
				_settings.AoRadiusFraction = aoRadius;
			}
			Tooltip("Радиус поиска в долях габаритного радиуса модели - для превью ОДНОГО объекта\n(масштаб-инвариантно). Игнорируется, когда задана ручка выше.\nБольше - тень тянется дальше от стыков (крупные ниши), мельче - только контактная.");

			var aoDebug = _settings.AoDebugView;
			if (ImGui.Checkbox("AO debug view", ref aoDebug))
			{
				_settings.AoDebugView = aoDebug;
				_changed = true;
			}
			Tooltip("Отладочный вид AO: композит выводит саму видимость в grayscale вместо затенения кадра\n(белое - открыто, чёрное - заслонено). Видно ровно то, чем AO-пасс глушит эмбиент,\nтак что ручки strength/floor/radius и разницу SSAO против GTAO можно сравнивать напрямую.\nПрозрачная геометрия рисуется ПОСЛЕ композита и поверх отладки остаётся обычной.");
		}
	}

	/// <summary>Стохастические экранные отражения (см. SsrPass). Фича живого конвейера: тонкий
	/// G-buffer пишется всегда, тумблер лишь ставит/снимает пассы.</summary>
	private void DrawSsrSection()
	{
		ImGui.Spacing();

		var ssr = _settings.PreviewSsr;
		if (ImGui.Checkbox("SSR (stochastic reflections)", ref ssr))
		{
			_settings.PreviewSsr = ssr;
			_changed = true;
		}
		Tooltip("Экранные отражения: стохастический GGX-луч на пиксель по глубине кадра,\nтемпоральная аккумуляция по векторам движения (включаются сами).\nРезультат ЗАМЕНЯЕТ префильтрованный env-спекуляр, а не складывается поверх.\nПрименяется живьём.");

		if (ssr)
		{
			var rt = _settings.SsrRayTraced;
			var rtAvailable = _viewport?.RayTracingSupported ?? false;
			if (!rtAvailable)
			{
				ImGui.BeginDisabled();
			}
			if (ImGui.Checkbox("Ray-traced fallback", ref rt))
			{
				_settings.SsrRayTraced = rt;
				_changed = true;
			}
			if (!rtAvailable)
			{
				ImGui.EndDisabled();
			}
			Tooltip("Лучи, промахнувшиеся мимо экрана, добираются inline RayQuery по TLAS сцены\n(та же геометрия, что у аппаратного probe GI - он должен быть включён,\nиначе фолбэк молча не активируется). Хиты, видимые на экране, берут готовый\nпиксель кадра; внеэкранные шейдятся упрощённо (солнце + probe-поле + лампы).\nТребует D3D12 с inline-трассировкой.");

			// Текстурное альбедо внеэкранных RT-хитов - только при включённом RT-фолбэке.
			if (rt && rtAvailable)
			{
				var hitTex = _settings.SsrHitTextures;
				string[] hitTexModes = ["Off (потриугольное альбедо)", "Атлас 128² (дёшево)", "Bindless (полные текстуры)"];
				ImGui.SetNextItemWidth(220 * _scale);
				if (ImGui.Combo("RT hit textures", ref hitTex, hitTexModes, hitTexModes.Length))
				{
					_settings.SsrHitTextures = hitTex;
					_changed = true;
				}
				Tooltip("Чем шейдить внеэкранный RT-хит:\nOff - один усреднённый цвет на треугольник (как раньше);\nАтлас - даунсемпленные плитки 128² всех base color текстур одним Texture2DArray\n(у стриминговых/cooked моделей плитка вырождается в средний цвет материала);\nBindless - массив полноразмерных текстур, честные UV-детали в отражениях\n(дороже по дескрипторам; без поддержки девайса тихо падает до атласа).\nСмена пересобирает материалы SSR.");

				var traceMode = _settings.SsrTraceMode;
				string[] traceModes = ["Экранный марш → RT", "Только RT (без марша)"];
				ImGui.SetNextItemWidth(220 * _scale);
				if (ImGui.Combo("Trace mode", ref traceMode, traceModes, traceModes.Length))
				{
					_settings.SsrTraceMode = traceMode;
					_changed = true;
				}
				Tooltip("Как ищется точка отражения:\nЭкранный марш → RT - сначала 48 шагов по буферу глубины, RT добирает промахи (по умолчанию);\nТолько RT - марш пропускается, луч сразу идёт по TLAS. Экранные ДАННЫЕ при этом\nне теряются: радианс в точке хита всё равно берётся с экрана репроекцией.\nУходят артефакты марша (ложные хиты за тонкой геометрией, ошибки SSR thickness,\nзатухание у краёв кадра); цена - обход BVH вместо выборок глубины. Live.");

				var rtBounces = _settings.SsrRtBounces;
				if (SliderInt("RT bounces", ref rtBounces, 1, 4))
				{
					_settings.SsrRtBounces = rtBounces;
				}
				Tooltip("Отскоков RT-луча ВСЕГО: 1 - только первичный луч (зеркало в зеркале чёрное),\n2 - плюс один зеркальный отскок с металлических хитов (по умолчанию),\n3-4 - длиннее цепочки взаимных отражений хрома. Цена - по трассировке\nна отскок, только на зеркальных пикселях с тёмным хитом. Live.");
			}

			// Статус ЧЕСТНЫЙ, по фактически собранным ресурсам: галка может стоять, а фича - молча
			// остаться экранной (нет accel-а/трассировки). Без строки этот даунгрейд неотличим от
			// «отражения сломаны»: зеркало в упор показывает голую env-карту, чёрную ниже горизонта.
			if (rt)
			{
				var sceneReason = _sceneViewport?.SsrRayTracedBlockReason;
				ImGui.TextColored(sceneReason == null
						? new Vector4(0.45f, 0.85f, 0.45f, 1f)
						: new Vector4(1f, 0.6f, 0.2f, 1f),
					sceneReason == null ? "Scene View: RT-фолбэк активен" : $"Scene View: {sceneReason}");

				var previewReason = _viewport?.SsrRayTracedBlockReason;
				ImGui.TextColored(previewReason == null
						? new Vector4(0.45f, 0.85f, 0.45f, 1f)
						: new Vector4(1f, 0.6f, 0.2f, 1f),
					previewReason == null ? "Превью: RT-фолбэк активен" : $"Превью: {previewReason}");
			}

			var intensity = _settings.SsrIntensity;
			if (Slider("SSR intensity", ref intensity, 0f, 4f, "%.2f"))
			{
				_settings.SsrIntensity = intensity;
			}
			Tooltip("Множитель заменяющего отражения. 1 - энергетически честно\n(сколько env-спекуляра вычли, столько трейса и вложили).");

			var maxRough = _settings.SsrMaxRoughness;
			if (Slider("SSR max roughness", ref maxRough, 0.05f, 1f, "%.2f"))
			{
				_settings.SsrMaxRoughness = maxRough;
			}
			Tooltip("Потолок шероховатости: выше отражения плавно гаснут (остаётся префильтрованный env).\nЛуч один на пиксель, и на матовых поверхностях остаточный шум дороже,\nчем недостающий спекуляр.");

			var thickness = _settings.SsrThickness;
			if (Slider("SSR thickness", ref thickness, 0.01f, 5f, "%.2f",
				ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.Logarithmic))
			{
				_settings.SsrThickness = thickness;
			}
			Tooltip("Толщина поверхности при проверке пересечения, мировые единицы.\nМало - лучи проскальзывают сквозь тонкую геометрию (дыры в отражении);\nмного - «прилипание» отражений к силуэтам переднего плана.");

			var maxDist = _settings.SsrMaxDistance;
			if (Slider("SSR max distance", ref maxDist, 1f, 500f, "%.0f",
				ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.Logarithmic))
			{
				_settings.SsrMaxDistance = maxDist;
			}
			Tooltip("Дальность луча отражения в мировых единицах.");

			var rays = _settings.SsrRaysPerPixel;
			if (SliderInt("SSR ray reuse", ref rays, 1, 4))
			{
				_settings.SsrRaysPerPixel = rays;
			}
			Tooltip("Качество резолва: сколько чужих лучей переиспользует каждый пиксель (x2 тапов).\nВес каждого - BRDF/PDF (ratio estimator, как в Stochastic SSR Frostbite):\nрезкость зеркала и ширина матового лоба следуют из физики,\nручка меняет только остаточный шум и цену.");

			var history = _settings.SsrHistoryWeight;
			if (Slider("SSR history weight", ref history, 0f, 0.97f, "%.2f"))
			{
				_settings.SsrHistoryWeight = history;
			}
			Tooltip("Вес истории темпоральной аккумуляции: больше - глаже и инертнее\n(шлейф на движении гасится клампом по окрестности), 0 - сырой шум одного луча.");

			var debug = _settings.SsrDebugView;
			string[] debugModes = ["Off", "Reflection only", "Confidence", "G-buffer normals", "RT hit albedo", "RT bounce chain"];
			if (ImGui.Combo("SSR debug view", ref debug, debugModes, debugModes.Length))
			{
				_settings.SsrDebugView = debug;
				_changed = true;
			}
			Tooltip("Отладочные виды: только отражения (что именно подмешивается), confidence\n(где лучи попали и с каким весом), нормали G-buffer-а (вход трейса).");
		}
	}

	/// <summary>Экранный отскок света (см. SsgiPass). Как и AO - фича живого конвейера.</summary>
	private void DrawSsgiSection()
	{
		ImGui.Spacing();

		var ssgi = _settings.PreviewSsgi;
		if (ImGui.Checkbox("SSGI (screen-space bounce)", ref ssgi))
		{
			_settings.PreviewSsgi = ssgi;
			_changed = true;
		}
		Tooltip("Экранный отскок света из кадра (color bleeding). Дополняет probe-GI\nконтактным переносом цвета там, где сетка проб слишком редкая.\nПрименяется живьём.");

		if (ssgi)
		{
			var giIntensity = _settings.SsgiIntensity;
			if (Slider("GI intensity", ref giIntensity, 0f, 4f, "%.2f"))
			{
				_settings.SsgiIntensity = giIntensity;
			}
			Tooltip("Множитель собранного отскока. 0 - пасс считается, но ничего не подмешивает.");

			var giSamples = _settings.SsgiSamples;
			if (SliderInt("GI samples", ref giSamples, 4, SsgiPassResources.MaxSampleCount))
			{
				_settings.SsgiSamples = giSamples;
			}
			Tooltip("Тапов на пиксель - главный рычаг шум/цена. 8 и ниже дают тот самый цветной снег,\n16-24 с размытием ниже уже читаются как мягкий отскок.");

			var giMaxLum = _settings.SsgiMaxLuminance;
			if (Slider("GI firefly clamp", ref giMaxLum, 0f, 32f, "%.2f",
				ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.Logarithmic))
			{
				_settings.SsgiMaxLuminance = giMaxLum;
			}
			Tooltip("Потолок яркости ОДНОГО тапа. В HDR-кадре солнечное пятно рядом с тенью светит\nв десятки единиц, и один такой тап из выборки - это белая/цветная точка-искра.\nНиже - чище и тусклее в контрастных местах; 0 - снять ограничение.");

			var giSaturation = _settings.SsgiSaturation;
			if (Slider("GI saturation", ref giSaturation, 0f, 1f, "%.2f"))
			{
				_settings.SsgiSaturation = giSaturation;
			}
			Tooltip("Насыщенность отскока: 1 - цвет отправителя как есть, 0 - серый bounce.\nАналог Bounce saturation у probe-GI: цветные ткани иначе светят как неон.");

			var giBlur = _settings.SsgiBlurRadius;
			if (SliderInt("GI blur radius", ref giBlur, 0, SsgiPassResources.MaxBlurRadius))
			{
				_settings.SsgiBlurRadius = giBlur;
			}
			Tooltip("Радиус билатерального (по глубине) размытия отскока в композите, пикселей.\nШире - глаже и дороже; силуэты не размазывает - вес режется по разрыву глубины.");

			var giRadiusWorld = _settings.SsgiRadiusWorld;
			if (Slider("GI radius (world)", ref giRadiusWorld, 0f, 20f, "%.2f"))
			{
				_settings.SsgiRadiusWorld = giRadiusWorld;
			}
			Tooltip("Радиус сбора в МИРОВЫХ единицах. 0 - считать от габаритов модели ручкой ниже.\nНа сцене-уровне (Sponza) доля от баундов даёт метры: отскок собирается с половины\nэкрана и вырождается в цветную дымку - ставь 1-3.");

			var giRadiusFraction = _settings.SsgiRadiusFraction;
			if (Slider("GI radius (bounds)", ref giRadiusFraction, 0.02f, 2f, "%.3f"))
			{
				_settings.SsgiRadiusFraction = giRadiusFraction;
			}
			Tooltip("Радиус сбора в долях габаритного радиуса модели - для превью ОДНОГО объекта\n(масштаб-инвариантно). Игнорируется, когда задана ручка выше.");

			var giDebug = _settings.SsgiDebugView;
			if (ImGui.Checkbox("GI debug view", ref giDebug))
			{
				_settings.SsgiDebugView = giDebug;
				_changed = true;
			}
			Tooltip("Отладочный вид SSGI: композит выводит ОДИН отскок вместо кадра с ним -\nвидно ровно то, что пасс подмешивает, и как на это влияют ручки выше.");
		}
	}

}
