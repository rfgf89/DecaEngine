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

/// <summary>Все ручки probe-GI и неба - самая большая секция окна. Часть <see cref="GraphicsSettingsWindow"/> - файл на тему,
/// поля и применение изменений живут в основном файле.</summary>
public partial class GraphicsSettingsWindow
{
	private void DrawProbeGiSection()
	{
		ImGui.Spacing();

		// Небо стоит ДО раннего выхода по !enabled: оно рисуется независимо от probe GI, и прятать
		// его вместе с настройками бейка - значит терять галку ровно тогда, когда GI выключен.
		var sky = _settings.PreviewSkyBackground;
		if (ImGui.Checkbox("Sky background", ref sky))
		{
			_settings.PreviewSkyBackground = sky;
			_changed = true;
		}
		Tooltip("Рисовать окружение фоном вместо чистой заливки.\nПереключается на живом конвейере.");

		ImGui.Spacing();

		var enabled = _settings.PreviewProbeGi;
		if (ImGui.Checkbox("Probe GI (DDGI-lite)", ref enabled))
		{
			_settings.PreviewProbeGi = enabled;
			_changed = true;
		}
		Tooltip("CPU-бейк сетки irradiance-проб (SH L1) + sky visibility по геометрии модели:\nнебо и отскоки света вместо константного эмбиента. Требует включённых теней.");

		ImGui.SameLine();
		ImGui.TextDisabled($"[превью: {_viewport.ProbeGiStatus}]");
		if (_sceneViewport != null)
		{
			ImGui.SameLine();
			ImGui.TextDisabled($"[сцена: {_sceneViewport.ProbeGiStatus}]");
		}

		if (!enabled)
		{
			return;
		}

		var realtime = _settings.ProbeGiRealtime;
		if (ImGui.Checkbox("Реальное время (без сходимости)", ref realtime))
		{
			_settings.ProbeGiRealtime = realtime;
			_changed = true;
		}
		Tooltip("Запечка против динамики. Обычный режим копит поле бегущим средним и\n" +
			"останавливается по сходимости: чем дольше, тем меньше шума, но и тем слабее\n" +
			"реакция на изменения - для статичной сцены это то, что нужно.\n" +
			"В реальном времени раунды не останавливаются, а среднее становится\n" +
			"экспоненциальным с постоянной альфой: поле отслеживает свет бесконечно,\n" +
			"ценой остаточного шума. Переключается на живой сессии, без ребейка.");

		if (realtime)
		{
			ImGui.Indent();
			var realtimeRays = _settings.ProbeGiRealtimeRays;
			if (SliderInt("Лучей за раунд", ref realtimeRays, 8, 1024))
			{
				_settings.ProbeGiRealtimeRays = realtimeRays;
			}
			Tooltip("Против ДЫХАНИЯ ЯРКОСТИ всей сцены. Веер лучей у всех проб раунда общий,\n" +
				"поэтому ошибка оценки скоррелирована по сетке и видна не как шум отдельных\n" +
				"проб, а как общая пульсация; гасится она числом лучей (1/sqrt(N)).\n" +
				"Замерено на Sponza (размах средней яркости за раунд, alpha 0.15):\n" +
				"  16 лучей - 5.1% (видно отчётливо), 32 - 1.5%, 64 - 1.1%, 128 - 0.5%.\n" +
				"Стоимость раунда линейна по числу лучей. Live, ребейка не требует.");

			var blend = _settings.ProbeGiRealtimeBlend;
			if (Slider("Вес раунда", ref blend, 0.01f, 0.5f, "%.3f"))
			{
				_settings.ProbeGiRealtimeBlend = blend;
				_changed = true;
			}
			Tooltip("Против мигания ОТДЕЛЬНЫХ проб - именно этой ручкой, а не лучами.\n" +
				"Альфа экспоненциального среднего: возмущение затухает как (1-alpha)^n,\n" +
				"а дрожание пробы идёт как sqrt(alpha/(2-alpha)). Редактор выпускает не\n" +
				"больше раунда за кадр, поэтому 0.04 при 60 к/с - установление за ~1.2 с.\n" +
				"Замерено на Sponza при 64 лучах (p99 и максимум смены пробы за раунд):\n" +
				"  0.15 - p99 6.3%, max 79%   0.08 - p99 3.4%, max 48%\n" +
				"  0.04 - p99 1.8%, max 24%   0.02 - p99 1.0%, max 12% (отклик 2.5 с)\n" +
				"Меньше - спокойнее картинка, дольше догоняет смену света. Live.");

			var maxStep = _settings.ProbeGiRealtimeMaxStep;
			if (Slider("Предел шага", ref maxStep, 0f, 0.2f, "%.3f"))
			{
				_settings.ProbeGiRealtimeMaxStep = maxStep;
				_changed = true;
			}
			Tooltip("Главная ручка против «диско»: сколько проба вправе изменить за раунд.\n" +
				"Вес раунда - фильтр, ОДИНАКОВЫЙ для всех проб, и ставить его приходится\n" +
				"по худшей: спокойным это лишняя вязкость, буйным всё равно мало. Предел\n" +
				"шага спокойных не трогает вовсе и включается только там, где оценка\n" +
				"скачет - проба перестаёт вспыхивать и начинает переползать. То есть при\n" +
				"нехватке лучей качество деградирует в ЗАДЕРЖКУ, а не во вспышки.\n" +
				"Яркость от него не теряется: режется производная, а не величина.\n" +
				"Замерено на Sponza при 8 лучах, весе 0.5, сетке 64, небе 12\n" +
				"(доля проб, дёрнувшихся за раунд больше чем на 10%):\n" +
				"  выключен - 61.2%   0.10 - 22.0%   0.03 - 0.6%   0.01 - 0.1%\n" +
				"Средняя яркость поля при этом 5.61 / 5.56 / 5.60 / 5.70 - не уезжает.\n" +
				"0 = выключить. Меньше - спокойнее и медленнее отклик. Live.");

			var relocation = _settings.ProbeGiRealtimeRelocation;
			if (Slider("Релокация проб", ref relocation, 0f, 0.45f, "%.2f"))
			{
				_settings.ProbeGiRealtimeRelocation = relocation;
				_changed = true;
			}
			Tooltip("Лечит ПРИЧИНУ мигания густой сетки, а не следствие. Чем мельче ячейка,\n" +
				"тем больше проб оказывается ВНУТРИ стен и колонн - а такая проба и мигает\n" +
				"(её лучи мечутся между задними гранями и небом за краем), и течёт светом\n" +
				"сквозь стену. Здесь такая проба каждый раунд отодвигается наружу через\n" +
				"ближайшую заднюю грань - её же лучи и показывают, где выход.\n" +
				"Значение - предел отхода в долях шага сетки. Выше 0.45 нельзя:\n" +
				"проба покинет свою ячейку, и трилинейная интерполяция соврёт сильнее,\n" +
				"чем выигрыш. 0 = выключить. Live.");

			var gamma = _settings.ProbeGiRealtimeGamma;
			if (Slider("Гамма накопления", ref gamma, 1f, 8f, "%.1f"))
			{
				_settings.ProbeGiRealtimeGamma = gamma;
				_changed = true;
			}
			Tooltip("Перцептивное накопление (Majercik 2021, §4.2, адаптация к SH): яркость\n" +
				"пробы копится не линейно, а по кривой глаза - редкая вспышка-светлячок\n" +
				"давится примерно в (вес раунда)^(гамма-1) раз, а переход свет→тень\n" +
				"ускоряется и читается как ровное потемнение, без бесконечного хвоста.\n" +
				"Замерено на Sponza (сетка 64, небо 12, 64 луча, вес 0.04): худший рывок\n" +
				"пробы за раунд 84% - 38% (гамма 3) - 29% (гамма 5); вместе с пределом\n" +
				"шага 0.03: 7% - 3%. Средняя яркость поля при этом теряет ~0.1%.\n" +
				"1 = линейно (выключено). Авторская величина - 5. Live.");

			var variability = _settings.ProbeGiVariabilityThreshold;
			if (Slider("Порог сходимости", ref variability, 0f, 0.3f, "%.3f"))
			{
				_settings.ProbeGiVariabilityThreshold = variability;
				_changed = true;
			}
			Tooltip("Probe Variability из RTXGI-DDGI: изменчивость пробы - это коэффициент\n" +
				"вариации её яркости (разброс, делённый на среднее), усреднённый по объёму.\n" +
				"Величина безразмерная, поэтому тёмный интерьер и залитый солнцем двор\n" +
				"сравнимы между собой. Опустилась ниже порога - объём сошёлся, и раунды\n" +
				"перестают пускаться ВОВСЕ, пока свет или геометрия не тронутся; раз в 32\n" +
				"раунда полный проход идёт всё равно, страховкой от незамеченного изменения.\n" +
				"Это следующая ступень после сна проб: сон экономит три четверти лучей,\n" +
				"а здесь диспатча нет совсем.\n" +
				"Замерено на Sponza (128 лучей): установившаяся изменчивость 0.058, при\n" +
				"пороге 0.08 пропускается 94% раундов, средняя яркость поля не меняется.\n" +
				"Величина зависит от сцены и числа лучей (шум оценки идёт как 1/sqrt(N)):\n" +
				"поставишь ниже установившейся - остановка не включится никогда, слишком\n" +
				"высоко - объём замрёт не досчитавшись. 0 = выключить. Live.");
			ImGui.Unindent();
		}


		// Тумблера CPU/GPU больше нет: раунды крутит только GPU (compute), CPU-раунд остался
		// эталоном сверки в CLI. Аппаратная трассировка - единственная ручка пути.
		{
			var hardwareSupported = _viewport.RayTracingSupported;
			if (!hardwareSupported)
			{
				ImGui.BeginDisabled();
			}

			var hardware = _settings.ProbeGiHardwareRayTracing;
			if (ImGui.Checkbox("Аппаратное ускорение трассировки", ref hardware))
			{
				_settings.ProbeGiHardwareRayTracing = hardware;
				_changed = true;
			}

			if (!hardwareSupported)
			{
				ImGui.EndDisabled();
				ImGui.SameLine();
				ImGui.TextDisabled("(устройство не умеет)");
			}

			Tooltip("Пускать лучи через RayQuery по аппаратным структурам ускорения (BLAS/TLAS)\n" +
				"вместо программного обхода собственного BVH. Ускоряет САМ обход - то есть\n" +
				"содержимое диспатча; накладные на границах диспатчей это не трогает.\n" +
				"Требует поддержки inline-трассировки (DXR 1.1 / VK_KHR_ray_query).\n" +
				"Недоступно или не собралось - молча откатывается на программный путь.");
		}

		ImGui.Spacing();
		ImGui.TextDisabled("Live (без ребейка):");

		// Логарифмический: на линейной шкале 0.25-128 рабочая зона (1-4) занимает пару пикселей у
		// левого края, и ручка мгновенно уезжает в сотни - эмбиент пересвечивается.
		var boost = _settings.ProbeGiAmbientBoost;
		if (Slider("Ambient boost", ref boost, 0.25f, 128f, "%.2f",
			ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.Logarithmic))
		{
			_settings.ProbeGiAmbientBoost = boost;
		}
		Tooltip("Множитель готовой probe-irradiance - экспозиция эмбиента.\nРабочая зона 1-4; десятки и выше пересвечивают сцену в белое (кламп при пуше - 128).");

		var shadowFloor = _settings.ProbeGiShadowFloor;
		if (Slider("Sun bounce in shadow", ref shadowFloor, 0f, 1f, "%.2f"))
		{
			_settings.ProbeGiShadowFloor = shadowFloor;
		}
		Tooltip("Сколько СОЛНЕЧНОЙ доли эмбиента остаётся в экранной тени ключа\n(0 - контактные тени жёстче, 1 - мягче). Действует там, где поле проб\nсолнечное - см. красный канал Probe debug view; в небесных сценах\n(двор в тени) эффекта почти нет - там крути Sky ambient in shadow.");

		var skyShadowFloor = _settings.ProbeGiSkyShadowFloor;
		if (Slider("Sky ambient in shadow", ref skyShadowFloor, 0.05f, 1f, "%.2f"))
		{
			_settings.ProbeGiSkyShadowFloor = skyShadowFloor;
		}
		Tooltip("Сколько НЕБЕСНОЙ доли эмбиента остаётся в экранной тени ключа.\n1 (дефолт) - физически честно: двор в тени залит небом (Intel Sponza).\nНиже - тени темнеют целиком, под более контрастный муд.");

		var specFloor = _settings.ProbeGiSpecularFloor;
		if (Slider("Env specular occlusion", ref specFloor, 0f, 1f, "%.2f"))
		{
			_settings.ProbeGiSpecularFloor = specFloor;
		}
		Tooltip("Флор глушения отражений окружения запечённой видимостью неба\n(0 - в интерьерах отражения гаснут полностью).");

		var bias = _settings.ProbeGiNormalBias;
		if (Slider("Normal bias", ref bias, 0f, 1f, "%.2f"))
		{
			_settings.ProbeGiNormalBias = bias;
		}
		Tooltip("Сдвиг точки сэмпла вдоль нормали в долях ячейки сетки -\nот утечек света/тьмы сквозь тонкие стены.");

		var viewBias = _settings.ProbeGiViewBias;
		if (Slider("View bias", ref viewBias, 0f, 1f, "%.2f"))
		{
			_settings.ProbeGiViewBias = viewBias;
		}
		Tooltip("Куда именно сдвигать точку сэмпла: 1 - к камере, 0 - по нормали.\n" +
			"1 (дефолт): фасеток нет, но при движении камеры пробы будто\n" +
			"съезжают и меняют яркость - освещение видозависимо.\n" +
			"0: от камеры не зависит вовсе, но на жёстких рёбрах в эмбиенте\n" +
			"проступают грани треугольников. У статьи DDGI - 0.8.");

		ImGui.Spacing();
		ImGui.TextDisabled("Бейк (изменение перепекает пробы):");

		var skyIntensity = _settings.ProbeGiSkyIntensity;
		if (Slider("Sky intensity", ref skyIntensity, 0.25f, 12f, "%.2f"))
		{
			_settings.ProbeGiSkyIntensity = skyIntensity;
		}
		Tooltip("Яркость неба в бейке - небесная часть эмбиента (двор в тени, ниши, sky visibility).");

		// Границы = ровно тот кламп, с которым значение уходит в бейк (ProbeGi.cs: Clamp(..., 16, 512)):
		// разметка 32..256 срезала оба конца, и заявленный в доке диапазон был недостижим из окна.
		var rays = _settings.ProbeGiRaysPerProbe;
		if (SliderInt("Rays per probe", ref rays, 16, 512))
		{
			_settings.ProbeGiRaysPerProbe = rays;
		}
		Tooltip("Лучей на пробу: больше - глаже поле и точнее sky visibility, линейно дольше бейк.");

		var visRes = _settings.ProbeGiVisRes;
		if (SliderInt("Visibility res", ref visRes, ProbeGiBakeResult.MinVisRes, ProbeGiBakeResult.MaxVisRes))
		{
			_settings.ProbeGiVisRes = visRes;
		}
		Tooltip("Сторона окто-карты ГЛУБИН на пробу (DDGI visibility): по ней тест Чебышёва решает,\n" +
			"не заслонена ли проба стеной от затеняемой точки - это главная защита от протечек\n" +
			"света сквозь тонкую геометрию (шторы, кромки колонн).\n" +
			"8 = ~25° на тексель, 16 = ~12° (значение эталонной статьи Majercik 2021).\n" +
			"ВНИМАНИЕ: атлас растёт квадратично, а лучей на тексель становится во столько же раз\n" +
			"МЕНЬШЕ - при неизменном Rays per probe качество падает (замерено: на 16 паразитная\n" +
			"подсветка интерьера Sponza выросла с 14.1 до 15.9). Поднимать вместе с Rays per probe.\n" +
			"Ребейк: раскладка атласов задаётся при создании сессии.");

		// Верх = ProbeGi.cs: Clamp(options.Bounces, 1, 6). Разметка до 4 отрезала два последних
		// переотскока, которые движок принимает и которые обещает дока ручки.
		var bounces = _settings.ProbeGiBounces;
		if (SliderInt("Bounces", ref bounces, 1, 6))
		{
			_settings.ProbeGiBounces = bounces;
		}
		Tooltip("Итераций сбора: 1 - только небо + прямой отскок солнца,\nкаждая следующая добавляет переотскок (глубоким дворам нужно 2-3).");

		var bounceSat = _settings.ProbeGiBounceSaturation;
		if (Slider("Bounce saturation", ref bounceSat, 0f, 1f, "%.2f"))
		{
			_settings.ProbeGiBounceSaturation = bounceSat;
		}
		Tooltip("Насыщенность цветного отскока: 0 - серый баунс, 1 - полный цвет альбедо.\nЯркость баунса не меняется (солнце рассеивается так же сильно) -\nниже значение, тем меньше яркие цветные ткани светят как лампочки.");

		// Низ = ProbeGi.cs: Clamp(options.GridDensity, 4f, 64f) - самая грубая сетка была недоступна.
		// Верх = 44: значение уходит в бейк с множителем 1.45 (ProbeGiViewportShared.BuildOptions),
		// а бейкер клампит произведение к 64 - всё выше 44 давало одну и ту же сетку, мёртвая
		// четверть диапазона ползунка.
		var density = _settings.ProbeGiGridDensity;
		if (Slider("Grid density", ref density, 4f, 44f, "%.0f"))
		{
			_settings.ProbeGiGridDensity = density;
		}
		Tooltip("Ячеек сетки на максимальный габарит сцены: плотнее - меньше утечек и точнее\nлокализация света, дороже бейк (число проб растёт кубически).");

		// Верх списка = ProbeGiBaker.MaxProbeBudget: раньше комбо обрывалось на 32k, и поднятый
		// потолок бейкера был недостижим из окна.
		int[] probeCaps = [2048, 4096, 8192, 16384, 32768, 131072, 524288, ProbeGiBaker.MaxProbeBudget];
		var capLabels = new[] { "2k", "4k", "8k", "16k", "32k", "128k", "512k", "2M" };
		var capIndex = Array.IndexOf(probeCaps, _settings.ProbeGiMaxProbes);
		if (capIndex < 0)
		{
			capIndex = Array.IndexOf(probeCaps, 8192);
			_settings.ProbeGiMaxProbes = probeCaps[capIndex];
			_changed = true;
		}

		ImGui.SetNextItemWidth(120 * _scale);
		if (ImGui.Combo("Max probes", ref capIndex, capLabels, capLabels.Length))
		{
			_settings.ProbeGiMaxProbes = probeCaps[capIndex];
			_changed = true;
		}
		Tooltip("Потолок числа проб: ячейка укрупняется, пока сетка не влезет.\nБейк растёт линейно по числу проб (32k ~ 1.3 с на Sponza), плюс сетку сверху\nрежут потолок по оси (128) и предел размера атласа видимости.");

		ImGui.Spacing();
		if (ImGui.Button("Rebake now", new Vector2(120 * _scale, 0)))
		{
			// Оба вьюпорта: раньше кнопка трогала только превью, и Scene View жил старым полем.
			_viewport.RequestProbeRebake();
			_sceneViewport?.RequestProbeRebake();
		}
		Tooltip("Принудительный ребейк превью И сцены (например, после правки HDR-файла на диске).");

		ImGui.SameLine();
		var debugView = _settings.ProbeGiDebugView;
		if (ImGui.Checkbox("Probe debug view", ref debugView))
		{
			_settings.ProbeGiDebugView = debugView;
			_changed = true;
		}
		Tooltip("Отладочный вид поля проб: R = солнечная доля (зона действия Sun bounce in shadow),\nG = видимость неба (зона Env specular occlusion), B = экранная тень ключа.");

		var debugProbes = _settings.ProbeGiDebugProbes;
		if (ImGui.Checkbox("Probe placement", ref debugProbes))
		{
			_settings.ProbeGiDebugProbes = debugProbes;
			_changed = true;
		}
		Tooltip("Где стоит каждая проба и что с ней сделала релокация:\n" +
			"  зелёное пятно - проба на своём узле сетки, всё в порядке;\n" +
			"  жёлтое-красное - отодвинута из геометрии (краснее = дальше);\n" +
			"  синее - признана невалидной (замурована, в интерполяцию не идёт);\n" +
			"  фон - валидность поля вокруг.\n" +
			"Отмечаются пробы, рядом с которыми есть поверхность - то есть ровно те,\n" +
			"из-за которых густая сетка и мигает. Пробы в открытом воздухе не видны:\n" +
			"своего прохода геометрии у этого вида нет, метки рисуются на поверхностях.\n" +
			"Старше галочки Probe debug view, если включены обе.");

		var showProbes = _settings.ProbeGiShowProbes;
		if (ImGui.Checkbox("Probe spheres", ref showProbes))
		{
			_settings.ProbeGiShowProbes = showProbes;
			_changed = true;
		}
		Tooltip("Шарик на каждую пробу В ЕЁ ФАКТИЧЕСКОЙ позиции (с релокацией) - в отличие от\n" +
			"Probe placement, это настоящая геометрия с депт-тестом, видны и пробы в воздухе:\n" +
			"  цвет - накопленный пробой свет (SH L0): тёмный в тени, яркий у света;\n" +
			"  красный - проба считает себя в стене (после релокации таких почти нет);\n" +
			"  голубая кромка - проба переехала релокацией (видно, кого вытащило).\n" +
			"Рисуется поверх сцены с её глубиной. Live, ребейка не требует.");
		// --- Отладка BVH (структура ускорения трассировки проб) ---
		ImGui.Separator();

		var bvhDebug = _settings.ProbeGiBvhDebug;
		if (ImGui.Checkbox("BVH boxes", ref bvhDebug))
		{
			_settings.ProbeGiBvhDebug = bvhDebug;
			_changed = true;
		}
		Tooltip("Каркасные боксы узлов BVH - дерева, по которому идёт трассировка проб.\n" +
			"Видно, во что реально свернулась геометрия сцены: раздутый узел (один бокс\n" +
			"на пол-сцены) - это лучи, перебирающие лишние треугольники.\n" +
			"Само дерево кешируется рядом с моделью файлом .bhv.bin: его сборка на тяжёлом\n" +
			"ассете стоит десятки секунд и делается один раз на версию файла модели.\n" +
			"Статистика дерева печатается в консоль при включении.");

		if (bvhDebug)
		{
			var bvhLeaves = _settings.ProbeGiBvhDebugLeaves;
			if (ImGui.Checkbox("BVH leaves only", ref bvhLeaves))
			{
				_settings.ProbeGiBvhDebugLeaves = bvhLeaves;
				_changed = true;
			}
			Tooltip("Только листья дерева - фактическая гранулярность разбиения\n" +
				"(в листе <= 4 треугольника). Список режется на 20000 боксах.");

			if (!bvhLeaves)
			{
				var bvhDepth = _settings.ProbeGiBvhDebugDepth;
				if (SliderInt("BVH depth", ref bvhDepth, 0, 16))
				{
					_settings.ProbeGiBvhDebugDepth = bvhDepth;
				}
				Tooltip("До какой глубины показывать узлы (0 = только корневой бокс всей сцены).\n" +
					"Боксов на уровне вдвое больше, чем на предыдущем.");
			}
		}
	}

	// --- Отложенное применение -------------------------------------------------------------------

}
