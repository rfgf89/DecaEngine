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

/// <summary>Пост-эффекты кадра: цветокоррекция, блум, туман, объёмный свет, автоэкспозиция. Часть <see cref="GraphicsSettingsWindow"/> - файл на тему,
/// поля и применение изменений живут в основном файле.</summary>
public partial class GraphicsSettingsWindow
{
	/// <summary>Цветокоррекция и виньетка (см. ColorGradePass). Единственное место, где художник
	/// вообще может править палитру кадра: до этого пасса в движке не было ни насыщенности, ни
	/// баланса белого, ни тонировки - только то, что зашито в текстуры.</summary>
	private void DrawColorGradeSection()
	{
		ImGui.Spacing();

		var grade = _settings.PreviewColorGrade;
		if (ImGui.Checkbox("Цветокоррекция", ref grade))
		{
			_settings.PreviewColorGrade = grade;
			_changed = true;
		}
		Tooltip("Финальный пасс по готовому кадру: насыщенность, контраст, баланс белого,\nтонировка теней и светов, виньетка.\nС дефолтными значениями кадр НЕ меняется - коррекцию набираешь сам.\nРаботает и в HDR, и в LDR.");

		if (!grade)
		{
			return;
		}

		ImGui.Spacing();
		ImGui.TextDisabled("Тон (live):");

		var saturation = _settings.GradeSaturation;
		if (Slider("Насыщенность", ref saturation, 0f, 2f, "%.2f"))
		{
			_settings.GradeSaturation = saturation;
		}
		Tooltip("1 - как есть, 0 - серый кадр, выше 1 - ярче цвета.\nГлавная ручка против перенасыщенных материалов: единый приглушённый\nконверт с одним-двумя акцентами читается дороже, чем десяток чистых цветов.");

		var contrast = _settings.GradeContrast;
		if (Slider("Контраст", ref contrast, 0f, 2f, "%.2f"))
		{
			_settings.GradeContrast = contrast;
		}
		Tooltip("Разведение тёмного и светлого вокруг среднего тона. 1 - без изменений.");

		var gamma = _settings.GradeGamma;
		if (Slider("Гамма", ref gamma, 0.2f, 3f, "%.2f"))
		{
			_settings.GradeGamma = gamma;
		}
		Tooltip("Средние тона: больше - светлее середина при тех же чёрном и белом.");

		var temperature = _settings.GradeTemperature;
		if (Slider("Температура", ref temperature, -1f, 1f, "%.2f"))
		{
			_settings.GradeTemperature = temperature;
		}
		Tooltip("Минус - холоднее (в синеву), плюс - теплее (в янтарь).\nНормирована по яркости: экспозицию не трогает, компенсировать не придётся.");

		var tint = _settings.GradeTint;
		if (Slider("Оттенок", ref tint, -1f, 1f, "%.2f"))
		{
			_settings.GradeTint = tint;
		}
		Tooltip("Минус - в зелёный, плюс - в пурпурный. Вторая ось баланса белого.");

		ImGui.Spacing();
		ImGui.TextDisabled("Тонировка (live):");

		var shadows = new Vector3(_settings.GradeShadowR, _settings.GradeShadowG, _settings.GradeShadowB);
		ImGui.SetNextItemWidth(180 * _scale);
		if (ImGui.ColorEdit3("Тени", ref shadows))
		{
			_settings.GradeShadowR = shadows.X;
			_settings.GradeShadowG = shadows.Y;
			_settings.GradeShadowB = shadows.Z;
			_changed = true;
		}
		Tooltip("АДДИТИВНАЯ тонировка: поднимает именно чёрное, светов не касается.\nНейтраль - чёрный. Классический приём: увести тени в холодное.");

		var highlights = new Vector3(_settings.GradeHighlightR, _settings.GradeHighlightG, _settings.GradeHighlightB);
		ImGui.SetNextItemWidth(180 * _scale);
		if (ImGui.ColorEdit3("Света", ref highlights))
		{
			_settings.GradeHighlightR = highlights.X;
			_settings.GradeHighlightG = highlights.Y;
			_settings.GradeHighlightB = highlights.Z;
			_changed = true;
		}
		Tooltip("МУЛЬТИПЛИКАТИВНАЯ тонировка: красит светлое, чёрное остаётся чёрным.\nНейтраль - белый. В паре с холодными тенями даёт тёплый ключ.");

		ImGui.Spacing();
		ImGui.TextDisabled("Виньетка (live):");

		var vignette = _settings.VignetteIntensity;
		if (Slider("Сила", ref vignette, 0f, 1f, "%.2f"))
		{
			_settings.VignetteIntensity = vignette;
		}
		Tooltip("Притемнение к краям кадра. 0 - выключена.\nЧасть того, почему кадр читается как СКОМПОНОВАННЫЙ, а не как скриншот.");

		var vignetteRadius = _settings.VignetteRadius;
		if (Slider("Радиус", ref vignetteRadius, 0.1f, 1.5f, "%.2f"))
		{
			_settings.VignetteRadius = vignetteRadius;
		}
		Tooltip("Размер чистой зоны в центре. Больше - виньетка отступает к краям.");

		var vignetteSmooth = _settings.VignetteSmoothness;
		if (Slider("Мягкость", ref vignetteSmooth, 0.01f, 1f, "%.2f"))
		{
			_settings.VignetteSmoothness = vignetteSmooth;
		}
		Tooltip("Ширина перехода. Малые значения дают видимое кольцо - обычно нужно мягко.");

		var vignetteRound = _settings.VignetteRoundness;
		if (Slider("Круглость", ref vignetteRound, 0f, 1f, "%.2f"))
		{
			_settings.VignetteRoundness = vignetteRound;
		}
		Tooltip("1 - круг с поправкой на формат кадра, 0 - овал, растянутый по всему кадру.");
	}

	/// <summary>Блум - оптическое рассеяние (см. BloomPass). Сама галка уровня окружения (пасс
	/// владеет своей цепочкой таргетов), всё остальное живое.</summary>
	private void DrawBloomSection()
	{
		ImGui.Spacing();

		var bloom = _settings.PreviewBloom;
		if (ImGui.Checkbox("Блум", ref bloom))
		{
			_settings.PreviewBloom = bloom;
			_changed = true;
		}
		Tooltip("Свечение вокруг ярких мест. Не «делает ярче» - делает источник ЧИТАЕМЫМ КАК СВЕТ:\nдисплей не способен показать лампу ярче белой бумаги, и разницу между ними\nглазу передаёт именно рассеяние в оптике.");

		if (!bloom)
		{
			return;
		}

		ImGui.Spacing();

		var threshold = _settings.BloomThreshold;
		if (Slider("Порог", ref threshold, 0f, 4f, "%.2f"))
		{
			_settings.BloomThreshold = threshold;
		}
		Tooltip("Яркость, выше которой начинается свечение, в ОТОБРАЖАЕМЫХ единицах.\n1.0 - светятся только настоящие пересветы (то, что дисплей уже не покажет ярче).\nПривязан к авто-экспозиции, поэтому не зависит от абсолютной яркости сцены.\nНиже 1.0 - светиться начинает и то, что пересветом не является.");

		var knee = _settings.BloomKnee;
		if (Slider("Мягкость порога", ref knee, 0.0001f, 1f, "%.3f"))
		{
			_settings.BloomKnee = knee;
		}
		Tooltip("Ширина плавного перехода вокруг порога.\nБез него на градиенте видна ступенька: поверхность светлеет, и ровно на пороге\nу неё вдруг включается ореол.");

		var radius = _settings.BloomRadius;
		if (Slider("Радиус", ref radius, 0f, 4f, "%.2f"))
		{
			_settings.BloomRadius = radius;
		}
		Tooltip("Ширина тента при сборке цепочки вверх.\nБольше - мягче и дальше растекается ореол; 0 - только билинейная выборка,\nи между уровнями видны кольца.");

		var intensity = _settings.BloomIntensity;
		if (Slider("Интенсивность", ref intensity, 0f, 3f, "%.2f"))
		{
			_settings.BloomIntensity = intensity;
		}
		Tooltip("Сколько ореола подмешивать в кадр. Нормирована на число звеньев цепочки,\nпоэтому не скачет при смене разрешения вьюпорта.");
	}

	/// <summary>Атмосферный туман - воздушная перспектива (см. FogPass). Сама галка - уровня
	/// окружения (пассу нужны депт и scene-copy), всё остальное живое.</summary>
	private void DrawFogSection()
	{
		ImGui.Spacing();

		var fog = _settings.PreviewFog;
		if (ImGui.Checkbox("Атмосферный туман", ref fog))
		{
			_settings.PreviewFog = fog;
			_changed = true;
		}
		Tooltip("Воздушная перспектива: дальние планы теряют контраст и уходят в дымку.\nГлавный источник ощущения ГЛУБИНЫ в кадре - никакая GI его не заменяет.");

		if (!fog)
		{
			return;
		}

		ImGui.Spacing();

		// Логарифмический: рабочая зона плотности - тысячные доли, и на линейной шкале она
		// занимала бы пару пикселей у левого края (та же причина, что у Ambient boost).
		var density = _settings.FogDensity;
		if (Slider("Плотность", ref density, 0.0002f, 0.5f, "%.4f",
			ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.Logarithmic))
		{
			_settings.FogDensity = density;
		}
		Tooltip("Главная ручка. Плотность на опорной высоте, 1/единица мира.\nМасштабно-зависима: сцене в десятки единиц нужны сотые, отдельной модели - десятые.");

		var heightFalloff = _settings.FogHeightFalloff;
		if (Slider("Спад по высоте", ref heightFalloff, 0f, 1f, "%.3f"))
		{
			_settings.FogHeightFalloff = heightFalloff;
		}
		Tooltip("Как быстро дымка редеет вверх.\n0 - однородный туман без высотного профиля;\nбольше - низкая стелющаяся пелена, из которой торчат верхушки геометрии.");

		var heightRef = _settings.FogHeightRef;
		if (Slider("Опорная высота", ref heightRef, -50f, 50f, "%.1f"))
		{
			_settings.FogHeightRef = heightRef;
		}
		Tooltip("Высота (Y мира), на которой плотность равна заданной выше.\nОбычно - уровень пола сцены.");

		var start = _settings.FogStartDistance;
		if (Slider("Ближняя отсечка", ref start, 0f, 50f, "%.1f"))
		{
			_settings.FogStartDistance = start;
		}
		Tooltip("Дистанция, до которой тумана нет вовсе.\nБез неё дымка садится на самые ближние предметы и мылит их.");

		var maxDistance = _settings.FogMaxDistance;
		if (Slider("Предельная дальность", ref maxDistance, 10f, 5000f, "%.0f",
			ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.Logarithmic))
		{
			_settings.FogMaxDistance = maxDistance;
		}
		Tooltip("Потолок дальности. Её же получает НЕБО - у фона глубины нет,\nи без этого горизонт остался бы единственным местом без дымки.");

		var maxOpacity = _settings.FogMaxOpacity;
		if (Slider("Потолок плотности", ref maxOpacity, 0f, 1f, "%.2f"))
		{
			_settings.FogMaxOpacity = maxOpacity;
		}
		Tooltip("Сколько дымка вправе закрыть дальний план.\n1 - полностью, меньше - сквозь туман всегда что-то видно.");

		ImGui.Spacing();
		ImGui.TextDisabled("Цвет:");

		var color = new Vector3(_settings.FogColorR, _settings.FogColorG, _settings.FogColorB);
		ImGui.SetNextItemWidth(180 * _scale);
		if (ImGui.ColorEdit3("Цвет среды", ref color))
		{
			_settings.FogColorR = color.X;
			_settings.FogColorG = color.Y;
			_settings.FogColorB = color.Z;
			_changed = true;
		}
		Tooltip("Тень дымки - то, во что уходит дальний план ВНЕ стороны солнца.\nСизый/голубоватый читается как даль, тёплый - как пыль или смог.");

		var sunColor = new Vector3(_settings.FogSunColorR, _settings.FogSunColorG, _settings.FogSunColorB);
		ImGui.SetNextItemWidth(180 * _scale);
		if (ImGui.ColorEdit3("Цвет подсветки", ref sunColor))
		{
			_settings.FogSunColorR = sunColor.X;
			_settings.FogSunColorG = sunColor.Y;
			_settings.FogSunColorB = sunColor.Z;
			_changed = true;
		}
		Tooltip("Цвет дымки в сторону солнца. Обычно теплее и ярче цвета среды.");

		var sunStrength = _settings.FogSunStrength;
		if (Slider("Сила подсветки", ref sunStrength, 0f, 1f, "%.2f"))
		{
			_settings.FogSunStrength = sunStrength;
		}
		Tooltip("Ради этого туман и ставят: дымка перестаёт быть серой пеленой\nи начинает светиться со стороны источника. 0 - одноцветный туман.");

		var sunSharpness = _settings.FogSunSharpness;
		if (Slider("Резкость пятна", ref sunSharpness, 1f, 64f, "%.1f",
			ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.Logarithmic))
		{
			_settings.FogSunSharpness = sunSharpness;
		}
		Tooltip("Малые значения - широкое мягкое свечение на полнеба,\nбольшие - компактный ореол вокруг диска.");
	}

	/// <summary>Объёмный свет - god rays и объёмный туман (см. VolumetricLightPass). Сама галка -
	/// уровня окружения (пассу нужны депт, scene-copy и shadow map), всё остальное живое.</summary>
	private void DrawVolumetricSection()
	{
		ImGui.Spacing();

		var volumetric = _settings.PreviewVolumetric;
		if (ImGui.Checkbox("Объёмный свет", ref volumetric))
		{
			_settings.PreviewVolumetric = volumetric;
			_changed = true;
		}
		Tooltip("Световые столбы (god rays) и светящийся объёмный туман.\n" +
			"Рейкмарш вдоль луча с выборкой каскадных теней в каждой точке -\n" +
			"поэтому столбы точно повторяют геометрию, отбрасывающую тень.\n" +
			"Не заменяет атмосферный туман и не конфликтует с ним: тот отвечает\n" +
			"за дальнюю дымку, этот - за рассеянный свет.");

		if (!volumetric)
		{
			return;
		}

		// Тени - не опция этой секции, а условие существования эффекта: без них выборка идёт по
		// неинициализированному shadow map, и сила тени принудительно занулена на CPU (см.
		// VolumetricLightPassResources.SetParams). Сказать об этом прямо дешевле, чем оставить
		// человека крутить ползунок, который ничего не делает.
		if (!_viewport.VolumetricShadowsAvailable && _sceneViewport?.VolumetricShadowsAvailable != true)
		{
			ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f), "Теней нет - столбов не будет");
			Tooltip("Марш берёт тени из каскадного shadow map. Без теневого пасса остаётся\n" +
				"только ровный объёмный туман - включите тени в секции Sun & Shadows.");
		}

		ImGui.Spacing();

		// Логарифмический по той же причине, что у плотности тумана: рабочая зона - сотые доли.
		var density = _settings.VolumetricDensity;
		if (Slider("Плотность среды", ref density, 0.0005f, 1f, "%.4f",
			ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.Logarithmic))
		{
			_settings.VolumetricDensity = density;
		}
		Tooltip("Главная ручка. Сколько вещества в воздухе, 1/единица мира.\n" +
			"Больше - плотнее столбы и мутнее кадр.");

		var sunIntensity = _settings.VolumetricSunIntensity;
		if (Slider("Сила лучей", ref sunIntensity, 0f, 8f, "%.2f"))
		{
			_settings.VolumetricSunIntensity = sunIntensity;
		}
		Tooltip("Яркость СОЛНЕЧНОГО рассеяния - это и есть god rays.\n" +
			"Именно её режет тень: где тени нет, столб светится.");

		var anisotropy = _settings.VolumetricAnisotropy;
		if (Slider("Анизотропия", ref anisotropy, -0.95f, 0.95f, "%.2f"))
		{
			_settings.VolumetricAnisotropy = anisotropy;
		}
		Tooltip("Направленность рассеяния (фазовая функция Хеньи-Гринштейна).\n" +
			"0.6..0.85 - как реальная дымка: столбы вспыхивают при взгляде ПРОТИВ солнца.\n" +
			"0 - ровное свечение со всех сторон. Отрицательные - рассеяние назад (редко нужно).\n" +
			"Общую яркость не меняет, только перераспределяет её по направлениям.");

		var shadowStrength = _settings.VolumetricShadowStrength;
		if (Slider("Сила тени", ref shadowStrength, 0f, 1f, "%.2f"))
		{
			_settings.VolumetricShadowStrength = shadowStrength;
		}
		Tooltip("Насколько тень режет лучи. 1 - настоящие столбы с чёткими краями,\n" +
			"0 - тень игнорируется и остаётся однородный объёмный туман.");

		var punctualScatter = _settings.VolumetricPunctualScatter;
		if (Slider("Свет ламп в среде", ref punctualScatter, 0f, 4f, "%.2f"))
		{
			_settings.VolumetricPunctualScatter = punctualScatter;
		}
		Tooltip("Рассеяние света point/spot-источников: конус спота и ореол лампы в дымке.\n" +
			"1 - физическая доля (яркость берётся из самих светов), 0 - среда видит только\n" +
			"солнце и небо. Тени ламп режут конус той же «Силой тени», что и солнечные.");

		ImGui.Spacing();
		ImGui.TextDisabled("Качество марша:");

		// Верх = кламп путей применения и пасса (Clamp(4, 256)): прежние 192 оставляли верхнюю
		// четверть диапазона недостижимой из окна.
		var steps = _settings.VolumetricSteps;
		if (SliderInt("Шагов", ref steps, 8, 256))
		{
			_settings.VolumetricSteps = steps;
		}
		Tooltip("Главная ручка ЦЕНЫ пасса - шаги считаются на каждый пиксель.\n" +
			"На яркость НЕ влияет (интеграл берётся аналитически по отрезку),\n" +
			"только на гладкость границ: мало шагов - зернистые края столбов.\n" +
			"32-64 обычно достаточно, выше 96 разница почти не видна.");

		var maxDistance = _settings.VolumetricMaxDistance;
		if (Slider("Дальность марша", ref maxDistance, 10f, 2000f, "%.0f",
			ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.Logarithmic))
		{
			_settings.VolumetricMaxDistance = maxDistance;
		}
		Tooltip("Докуда идёт луч. Шагов фиксированное число, поэтому вдвое большая\n" +
			"дальность = вдвое более крупный шаг и более рваные столбы.\n" +
			"Держите в пределах последнего каскада теней: дальше него столбы\n" +
			"выключаются разом (за каскадами всё считается освещённым).");

		var start = _settings.VolumetricStartDistance;
		if (Slider("Ближняя отсечка", ref start, 0f, 20f, "%.2f"))
		{
			_settings.VolumetricStartDistance = start;
		}
		Tooltip("С какой дистанции начинать марш.\nУ самой камеры среда даёт только шум и съедает шаги.");

		ImGui.Spacing();
		ImGui.TextDisabled("Оптика среды:");

		var scattering = _settings.VolumetricScattering;
		if (Slider("Рассеяние", ref scattering, 0f, 4f, "%.2f"))
		{
			_settings.VolumetricScattering = scattering;
		}
		Tooltip("Во сколько раз плотность превращается в СВЕТ.\nОбщий множитель яркости всего эффекта.");

		var extinction = _settings.VolumetricExtinction;
		if (Slider("Экстинкция", ref extinction, 0.01f, 4f, "%.2f",
			ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.Logarithmic))
		{
			_settings.VolumetricExtinction = extinction;
		}
		Tooltip("Насколько среда ГАСИТ проходящий сквозь неё свет.\n" +
			"Разведена с рассеянием намеренно: низкая экстинкция при высоком рассеянии\n" +
			"даёт светящиеся столбы без замутнения кадра - физически такого вещества\n" +
			"не бывает, но запрос «лучи есть, а даль не в молоке» самый частый.");

		var maxOpacity = _settings.VolumetricMaxOpacity;
		if (Slider("Потолок плотности", ref maxOpacity, 0f, 1f, "%.2f"))
		{
			_settings.VolumetricMaxOpacity = maxOpacity;
		}
		Tooltip("Сколько среда вправе съесть от исходного кадра.\n1 - до полного молока, меньше - сквозь неё всегда что-то видно.");

		var heightFalloff = _settings.VolumetricHeightFalloff;
		if (Slider("Спад по высоте", ref heightFalloff, 0f, 1f, "%.3f"))
		{
			_settings.VolumetricHeightFalloff = heightFalloff;
		}
		Tooltip("Как быстро среда редеет вверх.\n0 - однородный объём;\nбольше - низкая стелющаяся пелена, в которой лучи видны только у пола.");

		var heightRef = _settings.VolumetricHeightRef;
		if (Slider("Опорная высота", ref heightRef, -50f, 50f, "%.1f"))
		{
			_settings.VolumetricHeightRef = heightRef;
		}
		Tooltip("Высота (Y мира), на которой плотность равна заданной выше.\nОбычно - уровень пола сцены.");

		ImGui.Spacing();
		ImGui.TextDisabled("Цвет:");

		var sunColor = new Vector3(_settings.VolumetricSunColorR, _settings.VolumetricSunColorG,
			_settings.VolumetricSunColorB);
		ImGui.SetNextItemWidth(180 * _scale);
		if (ImGui.ColorEdit3("Цвет лучей", ref sunColor))
		{
			_settings.VolumetricSunColorR = sunColor.X;
			_settings.VolumetricSunColorG = sunColor.Y;
			_settings.VolumetricSunColorB = sunColor.Z;
			_changed = true;
		}
		Tooltip("Цвет самих столбов. Обычно берётся от солнца - тёплый на закате.");

		var ambientColor = new Vector3(_settings.VolumetricAmbientColorR,
			_settings.VolumetricAmbientColorG, _settings.VolumetricAmbientColorB);
		ImGui.SetNextItemWidth(180 * _scale);
		if (ImGui.ColorEdit3("Цвет в тени", ref ambientColor))
		{
			_settings.VolumetricAmbientColorR = ambientColor.X;
			_settings.VolumetricAmbientColorG = ambientColor.Y;
			_settings.VolumetricAmbientColorB = ambientColor.Z;
			_changed = true;
		}
		Tooltip("Цвет среды ТАМ, КУДА СОЛНЦЕ НЕ ДОШЛО - свет неба, тени его не режут.\nОбычно холоднее цвета лучей.");

		var ambientIntensity = _settings.VolumetricAmbientIntensity;
		if (Slider("Сила в тени", ref ambientIntensity, 0f, 3f, "%.2f"))
		{
			_settings.VolumetricAmbientIntensity = ambientIntensity;
		}
		Tooltip("Без неё среда в тени абсолютно чёрная,\nи столбы читаются как вырезанные ножницами, а не как свет в дымке.");

		var ambientFloor = _settings.VolumetricAmbientShadowFloor;
		if (Slider("Небо в тени", ref ambientFloor, 0f, 1f, "%.2f"))
		{
			_settings.VolumetricAmbientShadowFloor = ambientFloor;
		}
		Tooltip("Во сколько раз свечение слабее там, куда солнце не дошло.\n" +
			"ГЛАВНАЯ ручка против «молока»: на 1 крытый интерьер светится наравне\n" +
			"с залитым солнцем двором, кадр теряет контраст и насыщенность целиком.\n" +
			"0.1..0.2 - свечение живёт только у проёмов, интерьер остаётся плотным.");
	}

	private void DrawExposureSection()
	{
		// Кривая - ПЕРЕД галкой авто-экспозиции и вне её ветки: она действует в обоих режимах
		// (в LDR её применяет сам UnlitInstancedPS, в HDR - TonemapPass), а авто-экспозиция по
		// умолчанию выключена. Спрячь её внутрь - и главная ручка «почему плоско» осталась бы
		// недоступной в дефолтной конфигурации.
		var curveLabels = new[] { "PBR Neutral", "ACES (filmic)", "AgX (filmic)" };
		var curve = Math.Clamp(_settings.ToneCurve, 0, curveLabels.Length - 1);
		if (curve != _settings.ToneCurve)
		{
			// Кламп только для показа оставлял окно врущим: комбо на "PBR Neutral", в настройках -
			// прежний мусорный индекс, и шейдер берёт свою ветку по нему.
			_settings.ToneCurve = curve;
			_changed = true;
		}

		ImGui.SetNextItemWidth(180 * _scale);
		if (ImGui.Combo("Кривая тонмапа", ref curve, curveLabels, curveLabels.Length))
		{
			_settings.ToneCurve = curve;
			_changed = true;
		}
		Tooltip("PBR Neutral - эталон glTF: ниже ~0.76 тождественна, то есть НАРОЧНО не добавляет\n" +
			"ни контраста, ни глубины теней. Правильно для оценки материала и ровно поэтому\n" +
			"кадр с ней читается плоским.\n\n" +
			"ACES - классическая киношная кривая: контраст в средних тонах, глубокий носок,\n" +
			"укатанные света. Уводит оттенок насыщенных ярких цветов (оранжевый в жёлтый).\n\n" +
			"AgX - тот же фильмический контраст, но БЕЗ сдвига оттенка: пересвет уходит в белый\n" +
			"через десатурацию, а не через смену тона. Обычно лучший выбор для «покрасивее».");

		ImGui.Spacing();

		var eyeAdaptation = _settings.PreviewEyeAdaptation;
		if (ImGui.Checkbox("Auto exposure (eye adaptation)", ref eyeAdaptation))
		{
			_settings.PreviewEyeAdaptation = eyeAdaptation;
			_changed = true;
		}
		Tooltip("Замер средней яркости готового кадра + временное сглаживание: экспозиция приводит сцену\nк Key value, как глаз привыкает к свету. Переводит превью на HDR-конвейер (линейный кадр,\nтонемап отдельным пассом) - конвейер перестраивается на месте, модель не перезагружается.");

		if (!eyeAdaptation)
		{
			return;
		}

		ImGui.Spacing();

		var key = _settings.EyeAdaptationKey;
		if (Slider("Key value", ref key, 0.02f, 1f, "%.3f"))
		{
			_settings.EyeAdaptationKey = key;
		}
		Tooltip("Средняя яркость, к которой экспонируется кадр. 0.18 - фотографический средне-серый;\nвыше - светлее вся картинка целиком.");

		var ev = _settings.EyeAdaptationExposureCompensation;
		if (Slider("Exposure compensation (EV)", ref ev, -4f, 4f, "%.2f"))
		{
			_settings.EyeAdaptationExposureCompensation = ev;
		}
		Tooltip("Художественная поправка в стопах поверх авто-экспозиции: +1 EV - вдвое светлее.");

		var minLum = _settings.EyeAdaptationMinLuminance;
		if (Slider("Min luminance", ref minLum, 0.001f, 1f, "%.3f", ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.Logarithmic))
		{
			_settings.EyeAdaptationMinLuminance = minLum;
		}
		Tooltip("Нижняя граница ИЗМЕРЕННОЙ яркости: без неё почти чёрный кадр\n(камера уткнулась в стену) вытягивается в шум.");

		var maxLum = _settings.EyeAdaptationMaxLuminance;
		if (Slider("Max luminance", ref maxLum, 0.1f, 64f, "%.2f", ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.Logarithmic))
		{
			_settings.EyeAdaptationMaxLuminance = maxLum;
		}
		Tooltip("Верхняя граница измеренной яркости - от провала сцены в чёрный,\nкогда в кадр попадает солнце.");

		var speedUp = _settings.EyeAdaptationSpeedUp;
		if (Slider("Adapt speed (to light)", ref speedUp, 0.1f, 10f, "%.2f"))
		{
			_settings.EyeAdaptationSpeedUp = speedUp;
		}
		Tooltip("Скорость привыкания к более СВЕТЛОМУ кадру, 1/сек («зажмуриться»).");

		var speedDown = _settings.EyeAdaptationSpeedDown;
		if (Slider("Adapt speed (to dark)", ref speedDown, 0.1f, 10f, "%.2f"))
		{
			_settings.EyeAdaptationSpeedDown = speedDown;
		}
		Tooltip("Скорость привыкания к более ТЁМНОМУ кадру, 1/сек.\nПривычно медленнее подъёма - так же ведёт себя глаз.");
	}

}
