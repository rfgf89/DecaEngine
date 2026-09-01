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

/// <summary>Конвейер: векторы движения с отладочной визуализацией и граф рендера. Часть <see cref="GraphicsSettingsWindow"/> - файл на тему,
/// поля и применение изменений живут в основном файле.</summary>
public partial class GraphicsSettingsWindow
{
	/// <summary>Экранные векторы движения (см. MotionVectorPass) - подготовка к апскейлерам DLSS/FSR,
	/// а не эффект: буфер заполняется, но кадр не меняет, пока его никто не читает.
	///
	/// Ступень 1 восстанавливает векторы ИЗ ГЛУБИНЫ, поэтому в них движение одной лишь камеры: всё,
	/// что двигалось само, для временнЫх техник выглядит неподвижным. Секция об этом честно
	/// предупреждает - иначе отладочный вид читался бы как поломка.</summary>
	private void DrawMotionVectorSection()
	{
		ImGui.Spacing();

		// Метка НЕ совпадает с заголовком секции ("Motion vectors"): одинаковые метки в одном
		// ID-стеке ImGui дают коллизию ID, и клики по чекбоксу дерутся с заголовком - галка
		// "не работает". Тот же приём, что у SSGI: заголовок короткий, чекбокс - с уточнением.
		var motion = _settings.PreviewMotionVectors;
		if (ImGui.Checkbox("Motion vectors (вход апскейлеров)", ref motion))
		{
			_settings.PreviewMotionVectors = motion;
			_changed = true;
		}
		Tooltip("Экранные векторы движения в отдельный RG16F-буфер - вход апскейлеров (DLSS/FSR) и TAA.\n" +
			"Сам кадр не меняет: буфер пока никто не читает, галка стоит одного фуллскрин-дроу.\n" +
			"Применяется живьём.");

		// Масштаб и джиттер - ДО раннего выхода: они не зависят от галки векторов и обязаны
		// оставаться доступными, когда векторы выключены.
		var renderScale = _settings.RenderScale;
		if (Slider("Render scale", ref renderScale, 0.25f, 1f, "%.2f"))
		{
			_settings.RenderScale = renderScale;
			_changed = true;
		}
		Tooltip("Сцена и её пост-обработка рисуются в этой доле разрешения окна, до полного кадр\n" +
			"поднимает тонемап (пока билинейно - сюда встанет апскейлер). 1 = выключено.\n" +
			"Применяется живьём; на пол-разрешении сцена стоит ~четверть полной цены.");

		var jitter = _settings.TemporalJitter;
		if (ImGui.Checkbox("Temporal jitter", ref jitter))
		{
			_settings.TemporalJitter = jitter;
			_changed = true;
		}
		Tooltip("Суб-пиксельный джиттер проекции (Halton 2/3, 16 фаз) - вторая половина входа\n" +
			"темпорального апскейлера. Применяется живьём.\n" +
			"БЕЗ потребителя (TAA/апскейлера) картинка дрожит - это сырой вход техники, которой\n" +
			"ещё нет, а не баг. Отладочный вид векторов при этом ОБЯЗАН оставаться ровно серым:\n" +
			"векторы считаются по матрицам без джиттера, дрожание вычитает сам апскейлер.");

		if (!motion)
		{
			return;
		}

		var taau = _settings.TemporalUpscale;
		if (ImGui.Checkbox("Temporal upscale", ref taau))
		{
			_settings.TemporalUpscale = taau;
			_changed = true;
		}
		Tooltip("Темпоральный апскейл: сцена в рендер-разрешении (см. Render scale) + джиттер +\n" +
			"векторы собираются в полный кадр аккумуляцией истории. Джиттер включает сам.\n" +
			"На Render scale 1 работает как обычное TAA (сглаживание без потери разрешения).");

		if (taau)
		{
			var backend = _settings.UpscalerBackend;
			ImGui.SetNextItemWidth(220);
			if (ImGui.Combo("Upscaler backend", ref backend, "TAAU (встроенный)\0FSR (нативный)\0DLSS (нативный)\0"))
			{
				_settings.UpscalerBackend = backend;
				_changed = true;
			}
			Tooltip("TAAU - управляемый референс-бэкенд (шейдер движка). FSR - нативный ffx-api\n" +
				"(нужны DecaFfxShim.dll + amd_fidelityfx_upscaler_dx12.dll). DLSS - NVIDIA NGX\n" +
				"(нужны DecaFfxShim.dll + nvngx_dlss.dll и видеокарта RTX). Оба - только D3D12.\n" +
				"Применяется живьём; при недоступности молча остаётся TAAU.");

			// Честность: выбран нативный бэкенд, а работает TAAU - молчание тут заставило бы искать
			// разницу в картинке, которой нет.
			var activeName = _viewport?.Environment?.ActiveUpscalerName;
			if (backend != 0 && activeName is null)
			{
				ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f),
					(backend == 2 ? "DLSS" : "FSR") + " не активен - работает TAAU.");
				Tooltip("Нет DecaFfxShim.dll/нативной DLL рядом с экзешником, бэкенд не D3D12,\n" +
					"не то железо (DLSS - только NVIDIA RTX), или буфер векторов не создан.\n" +
					"Подробности - в консоли ([fsr]/[dlss] ...).");
			}
			else if (activeName is not null)
			{
				// Версия самой библиотеки: у FSR - активный провайдер ffx-рантайма (запрошен у
				// живого контекста), у DLSS - файловая версия nvngx_dlss.dll (= версия SDK-релиза).
				ImGui.TextDisabled($"Активен: {activeName}");
			}

			// Настройки САМОГО бэкенда - под тем, к кому относятся: чужие ручки в чужом режиме
			// только сбивали бы с толку.
			switch (backend)
			{
				case 0:
					var alpha = _settings.TaauBlendAlpha;
					if (Slider("TAAU: вес кадра", ref alpha, 0.02f, 0.5f, "%.2f"))
					{
						_settings.TaauBlendAlpha = alpha;
					}
					Tooltip("Вес текущего кадра в аккумуляторе истории. Меньше - стабильнее и резче\n" +
						"на статике, но дольше сходится и заметнее шлейфы; больше - отзывчивее к\n" +
						"движению, но дрожание джиттера глушится слабее. Классика TAA - 0.10.");
					break;

				case 1:
					var provider = _settings.FsrProvider;
					ImGui.SetNextItemWidth(220);
					if (ImGui.Combo("FSR: провайдер", ref provider, "Авто (новейший рабочий)\0FSR 2\0FSR 3.1\0"))
					{
						_settings.FsrProvider = provider;
						_changed = true;
					}
					Tooltip("Ветка провайдера ffx-рантайма. Авто берёт новейшее РАБОЧЕЕ поколение под\n" +
						"текущее железо (FSR 4 на RDNA4, иначе FSR 2). FSR 3.1 оставлен для проверки\n" +
						"глазами. Смена пересоздаёт контекст - мгновенно, история рвётся на кадр.");

					if (provider == 2)
					{
						ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f),
							"FSR 3.1 на текущем SDK деградирует кадр (известная проблема).");
						Tooltip("Провайдер 3.1.5 из SDK 2.3.0 на этой интеграции сводит кадр в размытую\n" +
							"кашу; все документированные параметры перепробованы, в официальном сэмпле\n" +
							"AMD он работает - подозрение на чтение протухшего дескриптора из кучи\n" +
							"приложения. Подробности - в логе консоли.");
					}

					var sharp = _settings.FsrSharpness;
					if (Slider("FSR: sharpness", ref sharp, 0f, 1f, "%.2f"))
					{
						_settings.FsrSharpness = sharp;
					}
					Tooltip("Встроенный шарпен FSR (RCAS) поверх апскейла: 0 - выключен, 1 - максимум.\n" +
						"Применяется живьём, бесплатной резкости не бывает - на шумной сцене\n" +
						"перешарп подчёркивает и шум.");
					break;

				case 2:
					var quality = _settings.DlssQuality;
					ImGui.SetNextItemWidth(220);
					if (ImGui.Combo("DLSS: качество", ref quality, "Performance\0Balanced\0Quality\0DLAA\0"))
					{
						_settings.DlssQuality = quality;
						_changed = true;
					}
					Tooltip("Пресет внутренней обработки DLSS (модель/веса). Разрешение рендера им НЕ\n" +
						"управляется - его задаёт Render scale выше; DLAA осмыслен при Render scale 1\n" +
						"(чистое сглаживание без апскейла). Смена пересоздаёт фичу - мгновенно.");
					break;
			}
		}

		var debug = _settings.MotionVectorDebugView;
		if (ImGui.Checkbox("Motion vector debug view", ref debug))
		{
			_settings.MotionVectorDebugView = debug;
			_changed = true;
		}
		Tooltip("Замещает кадр раскраской векторов: R - смещение по X, G - по Y, РОВНЫЙ СЕРЫЙ - ноль.\n" +
			"Главная проверка: на неподвижной камере кадр обязан быть равномерно серым при любом\n" +
			"диапазоне. Ядовито-жёлтые пятна - вектор ушёл за диапазон, подними ползунок ниже.\n" +
			"Движущиеся объекты в ступени 1 остаются серыми - векторов у них ещё нет, это не баг.");

		if (debug)
		{
			var range = _settings.MotionVectorDebugRange;
			if (Slider("Debug range (px)", ref range, 1f, 64f, "%.1f"))
			{
				_settings.MotionVectorDebugRange = range;
			}
			Tooltip("Смещение в ПИКСЕЛЯХ, на котором шкала упирается в край. Мельче - видно дрожание\n" +
				"на медленном движении камеры, крупнее - не забивается на быстрых разворотах.");
		}
	}

	/// <summary>Тумблеры отдельных пассов рендер-графа - инструмент отладки и профилирования.
	///
	/// Выключение НЕ пересобирает граф: команды пасса остаются записанными, просто не проигрываются
	/// (см. IRenderGraphPass.Enabled), поэтому переключать можно хоть каждый кадр и мерить разницу.
	/// Список берётся из живого графа, а не хардкодится - он зависит от включённых фич конвейера.</summary>
	private void DrawRenderGraphSection()
	{
		ImGui.Spacing();
		ImGui.TextDisabled("Отладочный список: вырубить ЛЮБОЙ пасс на живом графе и посмотреть, что он даёт.\n" +
			"Фичи конвейера переключайте выше - там снимается ещё и подготовка их ресурсов.");
		ImGui.Spacing();

		var pipeline = _viewport.Environment?.Pipeline;
		if (pipeline == null)
		{
			ImGui.TextDisabled("Конвейер превью ещё не поднят.");
			return;
		}

		var names = pipeline.PassNames;
		if (names.Count == 0)
		{
			ImGui.TextDisabled("Граф пуст (кадр ещё не собирался).");
			return;
		}

		for (int i = 0; i < names.Count; i++)
		{
			var name = names[i];
			var enabled = pipeline.IsPassEnabled(name);

			// ID по ИНДЕКСУ: имена пассов не обязаны быть уникальными (два оверлея, каскады),
			// а одинаковый ImGui-id склеил бы их чекбоксы в один.
			if (ImGui.Checkbox($"{name}##pass{i}", ref enabled))
			{
				pipeline.SetPassEnabled(name, enabled);
				_sceneViewport?.Environment?.Pipeline?.SetPassEnabled(name, enabled);
			}
		}

		ImGui.Spacing();
		ImGui.TextDisabled("Выключенный пасс исключается из графа целиком, вместе со своими переходами\n" +
			"состояний ресурсов, поэтому выключать можно любой - включая структурные\n" +
			"(Forward, Tonemap): кадр просто останется неотрисованным, но не сломается.\n" +
			"Галка ПЕРЕВЕШИВАЕТ настройки выше и переживает пересборку графа: пасс,\n" +
			"снятый здесь, не вернётся от включения своей фичи - верните его тут же.");
	}

}
