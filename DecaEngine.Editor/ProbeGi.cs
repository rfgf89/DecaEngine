using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Core;
using UnsafeCollections.Collections.Native;
using UnsafeCollections.Collections.Unsafe;

namespace DecaEngine.Editor;

/// <summary>
/// DDGI-лайт для превью: сетка irradiance-проб (SH L1) + sky visibility, запечённая CPU-трассировкой
/// по геометрии загруженной модели. Каждая проба пускает фиксированный веер лучей: промах = небо
/// (радианс энвайронмента), попадание = один отскок (солнце с теневым лучом + свет от проб прошлой
/// итерации - дёшёвая мультибаунс-аппроксимация DDGI). Результат пакуется в четыре 2D-атласа
/// RGBA16F (Z-слайсы столбиком), которые Lighting-режим шейдера сэмплирует вручную трилинейно
/// (см. UnlitInstancedPS.hlsl, SampleProbeGi) вместо константного ambient-уровня; sky visibility
/// из альфы SH0 глушит env-спекуляр в закрытых местах. Кеш первичных лучей переживает поворот
/// света: ребейк перетрассирует только теневые лучи и заново собирает радианс - быстрее полного.
/// </summary>
public sealed class ProbeGiBakeResult
{
	/// <summary>Размер ВИРТУАЛЬНОЙ сетки проб - система координат, в которой шейдер ищет ячейку по
	/// мировой позиции. Реально существуют только пробы кирпичей, отмеченных в
	/// <see cref="Indirection"/>: пустое пространство сетку не занимает (см. ProbeGiBaker).</summary>
	public int CountX, CountY, CountZ;
	public Vector3 Origin;
	public Vector3 Cell;

	/// <summary>Размер сетки КИРПИЧЕЙ (виртуальная сетка, делённая на
	/// <see cref="ProbeGiBaker.BrickCells"/>) - размер атласа индирекции.</summary>
	public int BrickCountX, BrickCountY, BrickCountZ;

	/// <summary>Сколько кирпичей реально запечено и по сколько их в ряду пула - раскладка атласов
	/// проб.</summary>
	public int BrickTotal, PoolColumns;

	public int PoolRows => PoolColumns > 0 ? (BrickTotal + PoolColumns - 1) / PoolColumns : 0;

	/// <summary>Сколько кирпичей вышло на каждом уровне подразделения (индекс = уровень) -
	/// диагностика раскладки: см. ProbeGiBaker.ClassifyBricks и вывод PreviewProbe.</summary>
	public int[] BricksPerLevel = Array.Empty<int>();

	/// <summary>Размер SH-атласов: пул кирпичей, кирпич - блок
	/// <see cref="ProbeGiBaker.BrickProbes"/> в ширину и BrickProbes² в высоту (z-слайсы столбиком
	/// внутри кирпича, как раньше вся сетка).</summary>
	public int ShWidth => PoolColumns * ProbeGiBaker.BrickProbes;
	public int ShHeight => PoolRows * ProbeGiBaker.BrickProbes * ProbeGiBaker.BrickProbes;

	/// <summary>Атласы RGBA16F, тексель пробы - см. <see cref="ShWidth"/>/<see cref="ShHeight"/> и
	/// ProbeGiBaker.ProbeTexel. Sh0: rgb = SH L0 (радианс), a = sky visibility. Sh1..3: rgb = SH L1
	/// x/y/z, a(Sh1) = валидность пробы (0 = внутри геометрии, соседям её не интерполировать).</summary>
	public byte[] Sh0, Sh1, Sh2, Sh3;

	/// <summary>Атлас РЕЛОКАЦИИ, RGBA16F, та же раскладка пула: rgb = смещение пробы от её узла
	/// сетки в МИРОВЫХ единицах, a = 1 при активной пробе.
	///
	/// Смещение обязано доехать до материального шейдера, а не остаться внутри раунда: и
	/// трилинейные веса, и тест Чебышёва считают расстояние от точки сэмпла ДО ПРОБЫ, и если
	/// шейдер будет думать, что проба стоит в узле, когда она сдвинута, оба соврут ровно на
	/// величину смещения - а тест видимости на это и рассчитан, он там точности в доли ячейки.
	///
	/// В запечке нули: релокация - режим реального времени (см.
	/// <see cref="ProbeGiBakeOptions.RealtimeRelocation"/>).</summary>
	public byte[] Offset;

	/// <summary>Окто-разрешение карты видимости на пробу (см. <see cref="Vis"/>).
	///
	/// Эталон (Majercik 2021, таблица 2) держит здесь 16 («8x8 irradiance, 16x16 visibility»), и
	/// 16 ЗАМЕРЕН - на дефолтном бюджете лучей он ХУЖЕ: яркость паразитной подсветки в интерьере
	/// Sponza 15.9 против 14.1 у восьмёрки (точечная выборка для сравнения - 16.7). Причина в том,
	/// что число лучей на пробу осталось прежним: вчетверо больше текселей - вчетверо меньше
	/// сэмплов на каждый, дисперсия оценки глубины растёт, а тест Чебышёва
	/// variance/(variance+diff²) при большой дисперсии как раз ПЕРЕСТАЁТ гасить вес заслонённой
	/// пробы. Поэтому это РУЧКА (окно Graphics, «Visibility res»), а не константа: поднимать её
	/// имеет смысл вместе с числом лучей на пробу.
	///
	/// НЕ const: значение читают и раскладка атласов, и оба шейдера (через кбуферы), и меняется оно
	/// только при пересоздании сессии - смена на живой сессии рассогласовала бы запись с чтением,
	/// поэтому ручка помечена как ребейк-уровня (см. GraphicsSettingsWindow).</summary>
	public static int VisRes { get; set; } = DefaultVisRes;

	public const int DefaultVisRes = 8;
	public const int MinVisRes = 8;
	public const int MaxVisRes = 24;

	/// <summary>DDGI visibility: RGBA16F атлас VisRes×VisRes окто-текселей на пробу (та же раскладка
	/// пула, что у SH-атласов, умноженная на VisRes по обеим осям). r = средняя дистанция до
	/// геометрии по направлению, g = средний квадрат дистанции - тест Чебышёва в SampleProbeGi
	/// отбрасывает пробы, заслонённые стеной от точки сэмпла (главный источник протечек света у
	/// стыков тонкой геометрии).</summary>
	public byte[] Vis;

	/// <summary>Карта индирекции RGBA8, размер BrickCountX × (BrickCountY*BrickCountZ), тексель
	/// (bx, bz*BrickCountY+by): r/g - младший/старший байт индекса кирпича в пуле, b > 0 - кирпич
	/// существует. Шейдер читает её ОДИН раз на сэмпл: все восемь углов трилинейной ячейки по
	/// построению лежат в одном кирпиче (см. ProbeGiBaker.BrickCells).</summary>
	public byte[] Indirection;
}

/// <summary>Настройки качества бейка probe-GI - крутятся теххудожником в окне Graphics
/// (см. GraphicsSettingsWindow), персистятся в <see cref="EditorSettings"/>. Любое изменение
/// требует ребейка (ModelPreviewViewport перезапускает его сам).</summary>
public sealed class ProbeGiBakeOptions
{
	/// <summary>Лучей на пробу ВСЕГО за сходимость сессии (см. <see cref="ProbeGiBakeSession"/>).
	/// Копятся раундами по <see cref="RaysPerRound"/> штук; больше = глаже поле и точнее sky
	/// visibility, линейно дольше сходимость. Мгновенная стоимость раунда от этой ручки не зависит -
	/// она задаёт только, сколько раундов бейк считается несошедшимся.</summary>
	public int RaysPerProbe = ProbeGiBaker.DefaultRaysPerProbe;

	/// <summary>Лучей на пробу за ОДИН раунд - единица работы фонового бейка (см.
	/// <see cref="ProbeGiBaker.RunRound"/>). Определяет задержку между кадрами с обновлённым полем:
	/// раунд должен укладываться в десяток миллисекунд, иначе прогрессивность теряет смысл.</summary>
	public int RaysPerRound = 16;

	/// <summary>Глубина мультибаунса. Прогрессивный бейк собирает отскок из ТЕКУЩЕГО поля проб, то
	/// есть переотскок по построению бесконечный (как в DDGI); эта ручка гасит обратную связь так,
	/// чтобы суммарная энергия совпала с N-баунсовым бейком - см.
	/// <see cref="ProbeGiBaker.BounceFeedback"/>. 1 = только небо+прямой отскок солнца.</summary>
	public int Bounces = 2;

	/// <summary>Множитель радианса неба в бейке (яркость небесного эмбиента).</summary>
	public float SkyIntensity = 1f;

	/// <summary>Собирать отскок из кэша радианса на поверхностях (см. <see cref="SurfaceCache"/>)
	/// вместо пересчёта из поля проб в каждой точке попадания. Даёт отскоку детализацию геометрии
	/// вместо детализации сетки проб; стоит одного теневого луча на воксель за раунд.</summary>
	public bool SurfaceCache = true;

	/// <summary>Доля ХРОМАТИЧНОСТИ альбедо, которая уходит в отскок (0 = серый баунс, 1 = полный
	/// цветной баунс). Яркость (люма) альбедо не трогается ни при каком значении - гасится только
	/// цвет, поэтому сила солнечного баунса от серого камня/пола остаётся прежней, а насыщенные
	/// красные/зелёные ткани перестают работать цветными лампочками. Компаундится по итерациям
	/// (см. <see cref="Bounces"/>): переотскок красное-в-красное умножал бы канал сам на себя и
	/// раскачивал хрому до неона.</summary>
	public float BounceSaturation = 0.5f;

	/// <summary>Плотность сетки: ячеек на максимальный габарит сцены (~22 по умолчанию).</summary>
	public float GridDensity = 22f;

	/// <summary>Потолок числа проб - защита от взрыва бейка на больших сценах (ячейка
	/// укрупняется, пока сетка не влезет). Клампится в
	/// [<see cref="ProbeGiBaker.MinProbeBudget"/>, <see cref="ProbeGiBaker.MaxProbeBudget"/>];
	/// сверху сетку дополнительно режут <see cref="ProbeGiBaker.MaxBricksPerAxis"/> и
	/// <see cref="ProbeGiBaker.MaxAtlasDimension"/>.</summary>
	public int MaxProbes = 8192;

	/// <summary>Режим РЕАЛЬНОГО ВРЕМЕНИ: раунды не сходятся и не останавливаются никогда, а поле
	/// копится не бегущим средним 1/(Round+1), а экспоненциальным с постоянной альфой (см.
	/// <see cref="ProbeGiBaker.RealtimeBlend"/>).
	///
	/// Разница принципиальная, а не в настройке качества. Бегущее среднее по построению перестаёт
	/// реагировать на изменения - вес N-го раунда стремится к нулю, - и это ровно то, что нужно
	/// ЗАПЕЧКЕ: чем дольше печём, тем меньше шума. Динамической сцене нужен противоположный
	/// компромисс: постоянная отзывчивость ценой остаточного шума, то есть окно усреднения
	/// фиксированной длины. Переключается на живой сессии (см.
	/// <see cref="ProbeGiBakeSession.Realtime"/>) - накопленное поле не выбрасывается.</summary>
	public bool Realtime = false;

	/// <summary>Лучей на пробу за раунд в режиме реального времени - отдельно от
	/// <see cref="RaysPerRound"/>, потому что цена ошибки там другая.
	///
	/// Все пробы раунда стреляют ОДНИМ веером (он лишь поворачивается от раунда к раунду), поэтому
	/// ошибка оценки скоррелирована по всей сетке: она проявляется не как шум отдельных проб, а как
	/// дыхание яркости всей сцены разом. Запечке это безразлично - вес раунда падает к нулю, и за
	/// сотню раундов веера усредняются; в реальном времени вес постоянный, и дыхание остаётся
	/// навсегда. Единственный дешёвый рычаг против него - число лучей: ошибка идёт как 1/sqrt(N).
	///
	/// Замерено на Sponza (см. SceneTraceVerifier.MeasureFlicker, размах средней яркости сетки за
	/// раунд): 16 лучей - 5.1%, 32 - 1.5%, 64 - 1.1%, 128 - 0.5%. Шестнадцать - это отчётливо
	/// видимое глазом мерцание, 64 - предел заметности. Стоимость линейна по числу лучей и на
	/// аппаратной трассировке остаётся единицами миллисекунд на раунд.</summary>
	public int RealtimeRaysPerRound = 64;

	/// <summary>Порог средней изменчивости объёма, ниже которого раунды останавливаются целиком
	/// (0 = приём выключен). Перенос «Probe Variability» из RTXGI-DDGI, см. ProbeVariabilityCS.hlsl.
	///
	/// Смысл: сон проб (см. <see cref="ProbeGiBakeSession"/>) экономит до трёх четвертей лучей, а
	/// классификация снимает ещё часть, но диспатч раунда всё равно идёт каждый кадр. На статичной
	/// сцене со статичным светом это чистая трата: поле сошлось, и следующая сотня раундов повторит
	/// то же значение. Изменчивость - коэффициент вариации яркости проб, усреднённый по объёму -
	/// даёт признак «повторять больше нечего», и раунд можно не пускать вовсе.
	///
	/// Возврат к работе обеспечивают те же события, что будят спящие пробы: смена света
	/// (<c>SetLighting</c>) и движение геометрии (<c>ReopenRelocation</c>) откатывают вес раунда,
	/// а пока вес выше пола, остановка запрещена. Сверх этого раз в
	/// <c>ProbeRoundGpu.VariabilityRefreshPeriod</c> раундов пускается полный раунд независимо ни от
	/// чего - страховка от изменения, которое не подняло ни одного из этих флагов.
	///
	/// Величина зависит от сцены и от числа лучей (шум оценки идёт как 1/sqrt(N), и изменчивость
	/// вместе с ним), поэтому она вынесена ручкой, а не зашита. Замерено на Sponza при 128 лучах:
	/// установившаяся изменчивость 0.058, при пороге 0.08 пропускается 94% раундов, а средняя
	/// яркость поля не меняется (0.8953 против 0.8954). Дефолт 0.08 - это измеренное значение с
	/// запасом на шум; восьмипроцентный разброс яркости пробы на глаз неразличим.</summary>
	public float RealtimeVariabilityThreshold = 0.08f;

	/// <summary>Куда может уехать УГОЛ коробки прокручиваемого объёма: минимальное и максимальное
	/// положение. По этой области меряется ёмкость пула - см. <see cref="ProbeGiBaker.BeginBake"/>,
	/// там же о том, почему мерить её по месту создания объёма нельзя. null - объём не прокручивается
	/// либо вызывающий области не знает; тогда ёмкость считается по-старому, от стартового места.
	/// </summary>
	public (Vector3 Min, Vector3 Max)? ScrollOriginRange;

	/// <summary>Потолок яркости ОДНОГО луча в режиме реального времени (0 = без ограничения) -
	/// подавление выбросов, тот же приём и по той же причине, что
	/// <see cref="EditorSettings.SsgiMaxLuminance"/> в экранном GI.
	///
	/// Дисперсию оценки пробы делает не равномерный шум, а редкие попадания в очень яркое: диск
	/// солнца в карте окружения имеет радианс в сотни единиц при среднем по сфере около четверти,
	/// и один такой луч из сотни сдвигает пробу целиком. Такие оценки не сходятся с числом лучей
	/// сколько-нибудь заметно - редкое событие остаётся редким, - поэтому давить их надо клампом, а
	/// не бюджетом. В запечке ограничение не нужно и НЕ ПРИМЕНЯЕТСЯ (см.
	/// <see cref="ProbeGiBakeSession.MaxRayLuminance"/>): там веера усредняются по сотне раундов, и
	/// выброс, попав однажды, растворяется вместе со своим весом.
	///
	/// Плата - недосчитанная энергия неба у самого солнца. Прямой солнечный свет от этого не
	/// страдает: он приходит отдельным членом (теневой луч плюс аналитическое солнце), а не выборкой
	/// панорамы.
	///
	/// ПО УМОЛЧАНИЮ ВЫКЛЮЧЕН, и это результат замера, а не осторожность. На Sponza с процедурным
	/// небом (64 луча, см. SceneTraceVerifier.MeasureFlicker) потолки 4, 2 и 1 не меняют ни одной
	/// цифры распределения - таких ярких лучей там просто нет; потолок 0.5 улучшает p99 с 6.3% до
	/// 6.0%, забирая при этом 19% средней яркости поля. То есть мерцание в этой сцене делают НЕ
	/// выбросы яркости. Ручка оставлена для сцен с настоящим солнечным диском в HDR-панораме, где
	/// радианс луча уходит в сотни.</summary>
	public float RealtimeMaxRayLuminance = 0f;

	/// <summary>Вес раунда в режиме реального времени - альфа экспоненциального среднего (см.
	/// <see cref="ProbeGiBaker.RealtimeBlend"/>). Прямой размен «отклик против стабильности»:
	/// возмущение затухает как (1-alpha)^n, а остаточное дрожание отдельной пробы идёт как
	/// sqrt(alpha/(2-alpha)) от дисперсии одного раунда. Вдвое меньше альфа - примерно в полтора
	/// раза спокойнее поле и вдвое дольше отклик на смену света.</summary>
	public float RealtimeBlend = ProbeGiBaker.RealtimeBlend;

	/// <summary>Предел ИЗМЕНЕНИЯ пробы за раунд, в долях её собственной яркости (0 = без предела).
	///
	/// Отвечает на вопрос «почему при нехватке лучей получается мигание, а не просто мягкая
	/// картинка». Вес раунда - фильтр по времени, он меняет шум на задержку РАВНОМЕРНО для всех проб,
	/// и его приходится ставить по худшей пробе: спокойным это лишняя вязкость, а буйным всё равно
	/// мало. Ограничитель скорости работает иначе - он не трогает пробу, пока та меняется в пределах
	/// нормы, и включается только там, где оценка скачет. Проба у края арки, у которой веер
	/// перекидывает лучи между тенью и небом, перестаёт вспыхивать и начинает переползать: качество
	/// у неё деградирует в ЗАДЕРЖКУ, а не во вспышки.
	///
	/// Установившееся значение ограничитель не смещает - он режет производную, а не величину, - и
	/// потому не теряет энергию, в отличие от клампа яркости луча (см.
	/// <see cref="RealtimeMaxRayLuminance"/>, который на замерах оказался бесполезен).
	///
	/// Масштаб берётся по полусумме старого и нового значений, а не по одному старому: иначе проба,
	/// стоящая в нуле (свет ещё не дошёл), осталась бы в нуле навсегда - ноль, умноженный на любой
	/// предел, остаётся нулём.</summary>
	public float RealtimeMaxStep = 0.03f;

	/// <summary>РЕЛОКАЦИЯ проб: насколько проба вправе отойти от своего узла сетки, в долях
	/// минимального шага сетки (0 = выключено). Штатное лекарство DDGI от главной болезни густой
	/// сетки: чем мельче ячейка, тем больше проб оказывается ВНУТРИ стен и колонн, а такая проба и
	/// мигает (её лучи мечутся между задними гранями и небом за краем), и течёт (интерполируется в
	/// точки по ту сторону стены).
	///
	/// Проба, у которой заметная доля лучей утыкается в задние грани, каждый раунд подтягивается в
	/// сторону наибольшего свободного пространства - того самого, которое её же лучи и намерили.
	/// Обратно к узлу она возвращается только убедившись, что там есть место (см. mainProbe в
	/// ProbeRoundCS.hlsl): критерий «мне тесно» без этой проверки качал бы пробу туда-сюда, ведь
	/// снаружи ей уже не тесно.
	///
	/// Предел 0.45 не случаен: сдвиг больше половины шага утащил бы пробу за пределы её ячейки, и
	/// трилинейная интерполяция начала бы врать сильнее, чем выигрывает.</summary>
	public float RealtimeRelocation = 0.45f;

	/// <summary>Гамма ПЕРЦЕПТИВНОГО НАКОПЛЕНИЯ (1 = линейное, выключено). Адаптация §4.2 статьи
	/// Majercik 2021 («perception-based exponential encoding», гамма 5.0 подобрана авторами
	/// экспериментально) к SH-представлению.
	///
	/// У них облучённость ХРАНИТСЯ как pow(E, 1/5) потексельно - к SH так нельзя: реконструкция
	/// E(n) = c0 + c1·n линейна по коэффициентам, и степень над ними её ломает. Поэтому здесь поле
	/// хранится линейно, а по перцептивной кривой ведётся только ТРАЕКТОРИЯ яркости: раунд
	/// смешивается как обычно, после чего яркость результата подправляется к
	/// pow(lerp(старая^(1/γ), новая^(1/γ), α), γ) - одним множителем на все четыре полосы SH,
	/// чтобы направленность не тронуть.
	///
	/// Зачем: линейное среднее по яркости аддитивно, а глаз логарифмичен. Вспышка-светлячок в 100×
	/// при α=0.04 двигает линейное среднее на +396%, перцептивное - на +34% (гасится примерно в
	/// α^(γ-1) раз); переход свет→тень, наоборот, ускоряется и читается как равномерное
	/// потемнение, а не бесконечный хвост. Цена - смещение установившегося уровня вниз на шумной
	/// оценке (среднее в степенном пространстве меньше арифметического) - меряется хвостовым
	/// замером, см. дефолт.</summary>
	public float RealtimeGamma = 5f;
}

/// <summary>Состояние ПРОГРЕССИВНОГО бейка probe-GI: сетка, аккумуляторы поля и геометрические
/// суммы, копящиеся раунд за раундом (см. <see cref="ProbeGiBaker.RunRound"/>). Заменяет прежний
/// «бейк одним куском», который на сцене-уровне занимал секунды и целиком повторялся при каждом
/// движении ползунка света: теперь раунд стоит RaysPerRound лучей на пробу (единицы миллисекунд),
/// поле после любого раунда уже можно показывать, качество набирается со временем, а поворот солнца
/// не выбрасывает накопленное (см. <see cref="SetLighting"/>). Не потокобезопасна: раунды гонять
/// строго по одному, <see cref="ProbeGiBaker.Snapshot"/> звать между ними.</summary>
public sealed class ProbeGiBakeSession
{
	/// <summary>Размер ВИРТУАЛЬНОЙ сетки проб (см. ProbeGiBakeResult.CountX) - в ней ищется ячейка
	/// по мировой позиции, но выделены только пробы живых кирпичей.</summary>
	public int CountX { get; }
	public int CountY { get; }
	public int CountZ { get; }

	/// <summary>Размер сетки кирпичей и число ЖИВЫХ из них: ProbeCount = BrickTotal * BrickProbes³,
	/// то есть пустое пространство проб не стоит.</summary>
	public int BrickCountX { get; }
	public int BrickCountY { get; }
	public int BrickCountZ { get; }
	public int BrickTotal { get; }
	public int PoolColumns { get; }

	/// <summary>Карта индирекции: индекс накрывающего кирпича по координатам в САМОЙ МЕЛКОЙ сетке
	/// кирпичей, -1 = пусто. Крупный кирпич прописан во все свои ячейки. Чистая геометрия -
	/// переживает и смену света, и любые раунды (но не прокрутку объёма, см.
	/// <see cref="Scroll"/>).</summary>
	internal int[] BrickIndex;

	/// <summary>Уровень подразделения накрывающего кирпича, по ячейке мелкой сетки - зеркало
	/// альфы карты индирекции.</summary>
	internal byte[] BrickLevelAt;

	/// <summary>Обратная карта: по три int на кирпич пула - его угол в координатах виртуальной
	/// сетки проб (уже домноженный на BrickCells).</summary>
	internal int[] BrickCellOrigin;

	/// <summary>Уровень кирпича пула: шаг между его пробами равен Cell * 2^Level.</summary>
	internal byte[] BrickLevel;

	/// <summary>Занят ли слот пула кирпичом. У прокручиваемого объёма пул заводится С ЗАПАСОМ (см.
	/// <see cref="ProbeGiBaker.ScrollHeadroom"/>), и часть слотов в любой момент пустует: их пробы
	/// существуют в буферах, но раунд их не считает, а индирекция на них не смотрит.</summary>
	internal bool[] BrickAlive;

	/// <summary>Сколько раундов слот ещё считается СВЕЖИМ - счётчик заводится при заселении слота
	/// новым кирпичом (прокрутка) и тикает вместе с раундами.
	///
	/// Первый из этих раундов «холодный»: поле принимается целиком (вес 1), а накопители - счётчики
	/// лучей, окто-карта глубин и смещение релокации - обнуляются, иначе новый кирпич унаследовал бы
	/// геометрию прежнего жильца слота. Всё окно (столько же раундов, сколько даёт инициализация,
	/// см. <see cref="ProbeGiBaker.RelocationRounds"/>) пробам слота разрешена релокация - они только
	/// что появились и половина из них стоит в стенах.</summary>
	internal byte[] BrickFresh;

	/// <summary>Номер раскладки: растёт на каждой прокрутке. По нему потребители кэшей раскладки
	/// (GPU-буферы кирпичей, дебаг-оверлей) понимают, что их копия протухла.</summary>
	internal int LayoutGeneration;

	/// <summary>Раскладка изменилась и ещё не доехала до GPU (буферы кирпичей, карта индирекции).
	/// Снимает <see cref="ProbeRoundGpu.SyncBrickState"/> на границе раунда.</summary>
	internal bool BrickStateDirty;

	/// <summary>Кэш осмотра геометрии под прокрутку - живёт ровно у прокручиваемых объёмов (см.
	/// <see cref="Scroll"/>). У базового объёма null: он никуда не едет.</summary>
	internal ProbeGiBaker.BrickScratch? Scratch;

	public int ProbeCount { get; }
	public Vector3 Origin { get; internal set; }
	public Vector3 Cell { get; }

	/// <summary>Сколько раундов влито в поле ПОСЛЕ последней смены освещения. Задаёт вес нового
	/// раунда (бегущее среднее 1/(Round+1)) и сходимость.</summary>
	public int Round { get; internal set; }

	/// <summary>Раундов до сходимости: RaysPerProbe/RaysPerRound, но не меньше минимума на разгон
	/// мультибаунса (см. ProbeGiBaker). После сходимости вызывающий перестаёт крутить раунды - поле
	/// статично и CPU свободен.</summary>
	public int TargetRounds { get; }

	/// <summary>Режим реального времени (см. <see cref="ProbeGiBakeOptions.Realtime"/>). Меняется на
	/// ЖИВОЙ сессии - это не параметр раскладки, а всего лишь пол веса раунда и признак «не
	/// останавливаться», так что накопленное поле переживает переключение в обе стороны и работает
	/// стартовым приближением.</summary>
	public bool Realtime { get; set; }

	/// <summary>Печь нечего (пустой BVH) - раунды крутить бессмысленно даже в реальном времени.
	/// Ставится <see cref="ProbeGiBaker.RunRound"/>.</summary>
	internal bool NoGeometry;

	/// <summary>Альфа экспоненциального среднего в реальном времени - живая ручка (см.
	/// <see cref="ProbeGiBakeOptions.RealtimeBlend"/>).</summary>
	public float RealtimeBlend { get; set; } = ProbeGiBaker.RealtimeBlend;

	/// <summary>Пол веса раунда: в запечке - почти ноль (среднее должно сходиться), в реальном
	/// времени - фиксированная альфа экспоненциального среднего.</summary>
	internal float MinBlend => Realtime
		? Math.Clamp(RealtimeBlend, 0.005f, 1f)
		: ProbeGiBaker.MinRoundBlend;

	/// <summary>Сошлось ли поле. В реальном времени - никогда: раунды идут, пока жива сессия, иначе
	/// динамики не будет по определению. Пустая сцена - исключение, там крутить нечего.</summary>
	public bool Converged => NoGeometry || (!Realtime && Round >= TargetRounds);

	/// <summary>Прогресс сходимости 0..1 - для статуса в окне Graphics. В реальном времени понятия
	/// сходимости нет, поле всегда «готово» и всегда обновляется.</summary>
	public float Progress => Realtime || TargetRounds <= 0
		? 1f
		: Math.Clamp(Round / (float)TargetRounds, 0f, 1f);

	private readonly int _bakeRaysPerRound;

	/// <summary>Лучей за раунд в режиме реального времени - живая ручка (см.
	/// <see cref="ProbeGiBakeOptions.RealtimeRaysPerRound"/>): менять её между раундами можно, буфер
	/// направлений заводится сразу под потолок.</summary>
	public int RealtimeRaysPerRound { get; set; }

	/// <summary>Лучей за ТЕКУЩИЙ раунд - зависит от режима: запечка копит качество раундами и может
	/// позволить себе редкий веер, реальное время платит за него мерцанием.</summary>
	internal int RaysPerRound => Realtime ? RealtimeRaysPerRound : _bakeRaysPerRound;

	/// <summary>Сколько первых лучей веера - ФИКСИРОВАННЫЕ (см.
	/// <see cref="ProbeGiBaker.FixedRayCount"/>).</summary>
	internal int FixedRays => ProbeGiBaker.FixedRayCount(RaysPerRound, Realtime);

	/// <summary>Потолок яркости луча в реальном времени - живая ручка (см.
	/// <see cref="ProbeGiBakeOptions.RealtimeMaxRayLuminance"/>).</summary>
	public float RealtimeMaxRayLuminance { get; set; }

	/// <summary>Потолок яркости луча для ТЕКУЩЕГО режима. В запечке кламп выключен всегда: там он
	/// был бы чистой потерей энергии, а выбросы и так растворяются усреднением по раундам. Заодно
	/// это сохраняет побитовую сверку GPU с CPU-эталоном - она идёт по сессиям запечки.</summary>
	internal float MaxRayLuminance => Realtime ? MathF.Max(RealtimeMaxRayLuminance, 0f) : 0f;

	/// <summary>Предел изменения пробы за раунд - живая ручка (см.
	/// <see cref="ProbeGiBakeOptions.RealtimeMaxStep"/>).</summary>
	public float RealtimeMaxStep { get; set; }

	/// <summary>Предел изменения для ТЕКУЩЕГО режима. В запечке выключен: там торопиться некуда, а
	/// ограничитель только замедлил бы сходимость.</summary>
	internal float MaxStep => Realtime ? MathF.Max(RealtimeMaxStep, 0f) : 0f;

	/// <summary>Предел релокации - живая ручка (см.
	/// <see cref="ProbeGiBakeOptions.RealtimeRelocation"/>).</summary>
	public float RealtimeRelocation { get; set; }

	/// <summary>Гамма перцептивного накопления - живая ручка (см.
	/// <see cref="ProbeGiBakeOptions.RealtimeGamma"/>).</summary>
	public float RealtimeGamma { get; set; }

	/// <summary>Гамма накопления для ТЕКУЩЕГО режима. В запечке всегда 1 (линейно): там среднее
	/// обязано сходиться к честному интегралу, перцептивное смещение было бы чистой ошибкой, а
	/// заодно сохраняется сверка с CPU-эталоном.</summary>
	internal float AccumulationGamma => Realtime ? Math.Clamp(RealtimeGamma, 1f, 8f) : 1f;

	/// <summary>Сколько раундов релокации осталось в текущем окне. Релокация НЕ работает постоянно:
	/// каждый переезд обесценивает и накопленное поле, и окто-карту глубин, то есть отправляет пробу
	/// в холодный старт. Majercik 2021 (§5) на этом настаивает прямо - «we do not move probes around
	/// dynamic geometry because this causes instability; a stable result is preferable to an unstable
	/// result with lower average error», - и двигает пробы только на инициализации.
	///
	/// Замурованная движущимся объектом проба при этом не портит картинку: её ловят backface-
	/// эвристики (§4.1, см. укорачивание глубин и валидность) - облучённость обнуляется, глубины
	/// укорачиваются, вес в интерполяции падает, и проба просто молчит, пока объект её накрывает.
	/// Окно открывается РОВНО ОДИН РАЗ - конструктором сессии, на инициализации. Раньше его
	/// переоткрывала пересборка TLAS, то есть КАЖДЫЙ КАДР драга гизмо, и это было прямым
	/// нарушением §5, процитированного выше: релокация включалась у всей сетки, сон проб
	/// выключался, а вес раунда откатывался вдесятеро тоже у всей сетки - видимое кипение
	/// поля всё время, пока тащат объект (см. PrefabSceneViewport.PollSceneProbePoses)
	/// (пересобрался TLAS), а не каждый раунд просто так.</summary>
	internal int RelocationRoundsLeft;

	/// <summary>Предел релокации в МИРОВЫХ единицах для текущего режима. В запечке выключена: она
	/// сдвигает пробы, а значит обесценивает и накопленное поле, и окто-карту глубин - в режиме,
	/// который считает сходимость, это чистая потеря. Заодно сохраняется побитовая сверка с
	/// CPU-эталоном, идущая по сессиям запечки.</summary>
	internal float RelocationLimit => Realtime && RelocationRoundsLeft > 0
		? MathF.Max(RealtimeRelocation, 0f) * MathF.Min(Cell.X, MathF.Min(Cell.Y, Cell.Z))
		: 0f;

	/// <summary>Геометрия сцены сдвинулась (пересобрался TLAS).
	///
	/// ЗАПЕЧКЕ это обязано сбросить сходимость: она считает раунды до TargetRounds и после
	/// этого останавливается совсем (см. Converged), так что без отката поле навсегда осталось бы
	/// с объектом в старой позе.
	///
	/// В РЕАЛЬНОМ ВРЕМЕНИ не делает НИЧЕГО, и это главное: там alpha - экспоненциальное
	/// среднее с постоянной MinBlend, поле следит за сценой само. Откат же поднимал вес
	/// раунда с MinBlend (~0.05) до 0.5 - вдесятеро и у ВСЕЙ сетки разом, - а тащат объект
	/// много кадров подряд, то есть всё время драга поле шло практически нефильтрованным
	/// (видимое кипение — см. PrefabSceneViewport.PollSceneProbePoses).
	///
	/// Релокацию НЕ трогает намеренно ни в каком режиме: Majercik 2021 §5 двигает пробы
	/// только на инициализации (см. <see cref="RelocationRoundsLeft"/>).</summary>
	public void InvalidateGeometry()
	{
		if (!Realtime)
		{
			Round = Math.Min(Round, ProbeGiBaker.RestartRound);
		}

		// В реальном времени вес раунда НЕ откатывается (см. вызывающий код в PrefabSceneViewport -
		// там разобрано, почему глобальный откат на каждый кадр драга гизмо хуже болезни). Но
		// остановку сошедшегося объёма снять обязательно: она проверяет ровно то состояние, которое
		// движение объекта не трогает - вес на полу и закрытое окно релокации, - и без этой отметки
		// объём, признанный сошедшимся, замирает НАВСЕГДА. На картинке это выглядит как след:
		// объект уехал, а его освещение осталось лежать на прежнем месте, потому что раунды больше
		// не идут и экспоненциальному среднему нечем работать.
		// Отметка снимается один раз, следующим же раундом (см. ProbeRoundGpu.IsConverged): дальше
		// объём сходится заново обычным порядком и, если сцена успокоилась, снова остановится.
		GeometryVersion++;
	}

	/// <summary>Счётчик изменений геометрии сцены. Растёт на каждый <see cref="InvalidateGeometry"/>;
	/// <see cref="ProbeRoundGpu"/> сравнивает его со своим снимком и по расхождению снимает
	/// остановку сошедшегося объёма.</summary>
	internal int GeometryVersion { get; private set; }

	/// <summary>Тратит раунд окна релокации - зовут оба пути, продвигая раунд.</summary>
	internal void ConsumeRelocationRound()
	{
		if (RelocationRoundsLeft > 0)
		{
			RelocationRoundsLeft--;
		}
	}

	/// <summary>Смещения проб от их узлов сетки, мировые единицы (см.
	/// <see cref="ProbeGiBakeResult.Offset"/>). CPU-путь копит их здесь, GPU - в своём буфере.</summary>
	internal readonly Vector3[] ProbeOffset;

	internal readonly float SkyIntensity, BounceSaturation, Feedback;

	// Освещение меняется МЕЖДУ раундами (см. SetLighting) - поворот солнца больше не требует
	// перезапуска бейка.
	internal Vector3 SunDirection, SunColor;
	internal float EnvYaw;
	internal Func<Vector3, Vector3> SkyRadiance;

	/// <summary>Сквозной номер раунда за всю жизнь сессии (в отличие от <see cref="Round"/> не
	/// откатывается) - им поворачивается веер Фибоначчи, чтобы раунды после смены света не
	/// повторяли уже отстрелянные направления.</summary>
	internal int Sequence;

	// Поле проб в двойном буфере: раунд читает прошлое поле (мультибаунс собирается по соседним
	// пробам в точках попаданий) и пишет новое, после чего буферы меняются местами. Прежний бейк
	// клонировал массивы на каждой итерации - на сотнях тысяч проб это само по себе стоило дороже
	// раунда трассировки.
	internal Vector3[] L0R, L1XR, L1YR, L1ZR, L0W, L1XW, L1YW, L1ZW;
	internal float[] ValidityR, ValidityW, SunFracR, SunFracW;

	/// <summary>Видимость неба: чистая геометрия, читается только владеющей пробой - одинарный
	/// буфер (в отличие от валидности, которую читает сбор по соседям).</summary>
	internal readonly float[] SkyVis;

	// Геометрические накопители: от освещения не зависят, копятся точными суммами по ВСЕМ раундам
	// и переживают поворот солнца без потери качества.
	internal readonly int[] RayTotal, MissTotal, BackTotal;

	/// <summary>Суммы по окто-карте глубин. VisWeight - сумма ВЕСОВ, а не число лучей: глубина
	/// укладывается по конусу с cosine-power лобой (см. RunRound и §4.4 статьи Majercik 2019), и
	/// каждый луч вносит в тексель свою долю.</summary>
	internal readonly float[] VisSumT, VisSumT2, VisWeight;

	/// <summary>Буферы атласов переиспользуются: снимок берётся каждый раунд, а это десятки
	/// мегабайт - пересоздавать их незачем (см. ProbeGiBaker.Snapshot).</summary>
	internal readonly ProbeGiBakeResult Result;

	/// <summary>Кэш радианса на поверхностях - источник отскока для лучей бейка (см.
	/// <see cref="SurfaceCache"/>). null, пока первый раунд его не построил, и навсегда, если кэш
	/// выключен настройками.</summary>
	public SurfaceCache? Surface { get; internal set; }

	/// <summary>Кэш поверхностей заказан, но ещё не построен - его захват стоит сотни миллисекунд и
	/// потому отложен до первого (фонового) раунда.</summary>
	internal bool WantsSurfaceCache;

	internal ProbeGiBakeSession(Vector3 origin, Vector3 cell, int cx, int cy, int cz,
		int nbx, int nby, int nbz, ProbeGiBaker.BrickLayout layout, int poolColumns,
		ProbeGiBakeOptions options, Vector3 sunDirection, Vector3 sunColor, float envYawRadians,
		Func<Vector3, Vector3> skyRadiance, int targetRounds)
	{
		CountX = cx;
		CountY = cy;
		CountZ = cz;
		BrickCountX = nbx;
		BrickCountY = nby;
		BrickCountZ = nbz;
		BrickIndex = layout.Index;
		BrickLevelAt = layout.LevelAt;
		BrickLevel = layout.Level;
		BrickAlive = layout.Alive;
		BrickTotal = layout.Total;

		// На инициализации свежесть НЕ взводится до конца: холодный раунд тут не нужен (буферы и
		// так нулевые), а спрятать объём до первого раунда значило бы поменять поведение CPU-пути,
		// который свежесть не тикает вовсе. Окно релокации на старте открывает сама сессия
		// (RelocationRoundsLeft), поэтому слоту достаётся ровно «не холодный, но виден».
		BrickFresh = new byte[layout.Total];
		for (int slot = 0; slot < layout.Total; slot++)
		{
			BrickFresh[slot] = layout.Alive[slot] ? (byte)(ProbeGiBaker.RelocationRounds - 1) : (byte)0;
		}
		PoolColumns = poolColumns;
		ProbeCount = layout.Total * ProbeGiBaker.BrickProbes
			* ProbeGiBaker.BrickProbes * ProbeGiBaker.BrickProbes;
		Origin = origin;
		Cell = cell;
		TargetRounds = targetRounds;

		// Угол кирпича в координатах виртуальной сетки проб: RunRound восстанавливает по нему
		// мировую позицию пробы, а прямой карты (BrickIndex) для этого мало.
		BrickCellOrigin = new int[layout.Total * 3];
		for (int slot = 0; slot < layout.Total; slot++)
		{
			BrickCellOrigin[slot * 3 + 0] = layout.Anchor[slot * 3 + 0] * ProbeGiBaker.BrickCells;
			BrickCellOrigin[slot * 3 + 1] = layout.Anchor[slot * 3 + 1] * ProbeGiBaker.BrickCells;
			BrickCellOrigin[slot * 3 + 2] = layout.Anchor[slot * 3 + 2] * ProbeGiBaker.BrickCells;
		}

		_bakeRaysPerRound = Math.Clamp(options.RaysPerRound, 4, 128);
		RealtimeRaysPerRound = Math.Clamp(options.RealtimeRaysPerRound, 4, 1024);
		RealtimeMaxRayLuminance = MathF.Max(options.RealtimeMaxRayLuminance, 0f);
		RealtimeBlend = options.RealtimeBlend;
		RealtimeMaxStep = MathF.Max(options.RealtimeMaxStep, 0f);
		RealtimeRelocation = Math.Clamp(options.RealtimeRelocation, 0f, 0.45f);
		RealtimeGamma = Math.Clamp(options.RealtimeGamma, 1f, 8f);
		VariabilityThreshold = MathF.Max(options.RealtimeVariabilityThreshold, 0f);
		// Свежая сессия - пробы стоят в узлах сетки, часть из них внутри стен: окно открыто.
		RelocationRoundsLeft = ProbeGiBaker.RelocationRounds;
		Realtime = options.Realtime;
		SkyIntensity = Math.Clamp(options.SkyIntensity, 0f, 16f);
		BounceSaturation = Math.Clamp(options.BounceSaturation, 0f, 1f);
		Feedback = ProbeGiBaker.BounceFeedback(Math.Clamp(options.Bounces, 1, 6));

		SunDirection = Vector3.Normalize(sunDirection);
		SunColor = sunColor;
		EnvYaw = envYawRadians;
		SkyRadiance = skyRadiance;

		int n = ProbeCount;
		L0R = new Vector3[n]; L1XR = new Vector3[n]; L1YR = new Vector3[n]; L1ZR = new Vector3[n];
		L0W = new Vector3[n]; L1XW = new Vector3[n]; L1YW = new Vector3[n]; L1ZW = new Vector3[n];
		ValidityR = new float[n]; ValidityW = new float[n];
		SunFracR = new float[n]; SunFracW = new float[n];
		SkyVis = new float[n];
		ProbeOffset = new Vector3[n];
		RayTotal = new int[n]; MissTotal = new int[n]; BackTotal = new int[n];

		int visCells = n * ProbeGiBakeResult.VisRes * ProbeGiBakeResult.VisRes;
		VisSumT = new float[visCells];
		VisSumT2 = new float[visCells];
		VisWeight = new float[visCells];

		Result = new ProbeGiBakeResult
		{
			CountX = cx,
			CountY = cy,
			CountZ = cz,
			BrickCountX = nbx,
			BrickCountY = nby,
			BrickCountZ = nbz,
			BrickTotal = layout.Total,
			PoolColumns = poolColumns,
			Origin = origin,
			Cell = cell,
			Indirection = new byte[nbx * nby * nbz * 4],
			BricksPerLevel = new int[ProbeGiBaker.MaxBrickLevel],
		};

		for (int slot = 0; slot < BrickTotal; slot++)
		{
			if (BrickAlive[slot])
			{
				Result.BricksPerLevel[BrickLevel[slot]]++;
			}
		}

		// Пул может быть шире, чем есть кирпичей (последний ряд неполон) - атласы выделяем под всю
		// прямоугольную раскладку, хвост остаётся нулевым и никем не читается.
		int poolProbes = Result.ShWidth * Result.ShHeight;
		Result.Sh0 = new byte[poolProbes * 8];
		Result.Sh1 = new byte[poolProbes * 8];
		Result.Sh2 = new byte[poolProbes * 8];
		Result.Sh3 = new byte[poolProbes * 8];
		Result.Offset = new byte[poolProbes * 8];
		Result.Vis = new byte[poolProbes * ProbeGiBakeResult.VisRes * ProbeGiBakeResult.VisRes * 8];

		WriteIndirection();
	}

	/// <summary>Пересобирает карту индирекции из текущей раскладки. У неподвижного объёма это
	/// делается один раз (геометрия сетки не меняется), у прокручиваемого - на каждой прокрутке и
	/// на выходе кирпича из свежести.
	///
	/// СВЕЖИЙ кирпич помечается как НЕСУЩЕСТВУЮЩИЙ (b = 0), хотя слот у него уже есть, и это
	/// главная деталь всей прокрутки. Слот только что отобрали у прежнего жильца, а его тексели в
	/// атласах ещё держат СТАРОЕ поле - поле места, откуда объём уехал. Покажи мы такой кирпич
	/// сразу, во въехавшей области вспыхнули бы блоки чужого освещения; спрятанный, он честно
	/// проваливается на более крупный каскад (см. ProbeGiSampleBody: b &lt; 0.5 - промах) ровно до
	/// того раунда, который запишет его собственные значения.</summary>
	internal void WriteIndirection()
	{
		int nbx = BrickCountX, nby = BrickCountY;
		Array.Clear(Result.Indirection);
		for (int i = 0; i < BrickIndex.Length; i++)
		{
			int slot = BrickIndex[i];
			if (slot < 0 || BrickFresh[slot] >= ProbeGiBaker.RelocationRounds)
			{
				continue;
			}

			int bx = i % nbx, by = i / nbx % nby, bz = i / (nbx * nby);
			int texel = ((bz * nby + by) * nbx + bx) * 4;
			Result.Indirection[texel + 0] = (byte)(slot & 255);
			Result.Indirection[texel + 1] = (byte)(slot >> 8 & 255);
			Result.Indirection[texel + 2] = BrickConfidence(slot);
			Result.Indirection[texel + 3] = BrickLevelAt[i];
		}
	}

	/// <summary>Насколько кирпичу можно верить, 0..255 - канал b индирекции. Прогретый кирпич даёт
	/// 255; кирпич, ещё доживающий своё окно свежести, - пропорционально прожитому.
	///
	/// Зачем полутон вместо флага. Прогревающий диспатч (см. ProbeRoundGpu.WarmColdBricks) снимает
	/// с въехавшего кирпича холод за один заход, и до этой поправки он тут же появлялся В ПОЛНУЮ
	/// СИЛУ - с полем, собранным одним веером лучей, то есть шумным. Прокрутка приводит такие
	/// кирпичи непрерывно, пока летит камера, и вся въехавшая полоса рябила: ровно то, что читается
	/// как «пробы пересчитываются, когда двигаешь камеру». Проявление за окно свежести отдаёт эти
	/// раунды более крупному каскаду, который там давно сошёлся.
	///
	/// Только для ПРОКРУЧИВАЕМЫХ объёмов. У базового полутон был бы опасен: его свежесть тикает
	/// лишь GPU-путь, а CPU-путь не тикает её вовсе (см. конструктор сессии), и там кирпичи
	/// застряли бы на стартовом весе навсегда - весь объём светил бы в пятую силу.</summary>
	private byte BrickConfidence(int slot)
	{
		int fresh = BrickFresh[slot];
		if (Scratch == null || fresh <= 0)
		{
			return 255;
		}

		// fresh падает от RelocationRounds-1 (только прогрет) до нуля (прожил окно целиком).
		int lived = ProbeGiBaker.RelocationRounds - fresh;
		return (byte)Math.Clamp(lived * 255 / ProbeGiBaker.RelocationRounds, 1, 255);
	}

	/// <summary>Обновляет освещение между раундами. Если оно реально изменилось, сходимость
	/// откатывается к <see cref="ProbeGiBaker.RestartRound"/>: вес раунда подскакивает, и поле
	/// перетекает к новому решению за единицы раундов - при этом НЕ выбрасываются ни накопленная
	/// геометрия (видимость/валидность/окто-глубины), ни старое поле как стартовое приближение.
	/// Прежний код на любое движение ползунка света запускал полный ребейк с дебаунсом. Возвращает
	/// true, если освещение изменилось. Небесную функцию сравнивать нечем (делегат), но её смена
	/// означает перезагрузку окружения, а та и так пересоздаёт сессию.</summary>
	public bool SetLighting(Vector3 sunDirection, Vector3 sunColor, float envYawRadians,
		Func<Vector3, Vector3> skyRadiance)
	{
		var dir = Vector3.Normalize(sunDirection);
		bool changed = (dir - SunDirection).LengthSquared() > 1e-10f
			|| (sunColor - SunColor).LengthSquared() > 1e-10f
			|| MathF.Abs(envYawRadians - EnvYaw) > 1e-6f;

		SunDirection = dir;
		SunColor = sunColor;
		EnvYaw = envYawRadians;
		SkyRadiance = skyRadiance;

		if (changed)
		{
			Round = Math.Min(Round, ProbeGiBaker.RestartRound);
		}

		return changed;
	}

	/// <summary>Продвигает счётчики раунда, когда работу сделал GPU (см. ProbeRoundGpu). CPU-буферы
	/// поля в этом режиме не используются и менять их местами незачем - пинг-понг ведёт сам
	/// GPU-объект, - но номер раунда и порядковый номер веера обязаны идти в ногу: от первого
	/// зависит вес раунда, от второго - направления лучей.</summary>
	public void AdvanceRound()
	{
		Sequence++;
		Round++;
		ConsumeRelocationRound();
		ConsumeFreshRound();
	}

	/// <summary>Сколько кирпичей СЕЙЧАС спрятано из индирекции как холодные (слот только что отобран
	/// прокруткой, своего поля в нём ещё нет - см. <see cref="WriteIndirection"/>). Это ровно те
	/// кирпичи, на месте которых выборка проваливается на более крупный каскад, то есть прямая мера
	/// «протечки при движении»: чем дольше и чем больше их держится, тем заметнее артефакт.</summary>
	internal int ColdBrickCount
	{
		get
		{
			int cold = 0;
			for (int slot = 0; slot < BrickTotal; slot++)
			{
				if (BrickFresh[slot] >= ProbeGiBaker.RelocationRounds)
				{
					cold++;
				}
			}

			return cold;
		}
	}

	/// <summary>Слоты, прогретые ПОСЛЕДНИМ <see cref="ThawColdBricks"/>, и сколько их. Снаружи это
	/// единственный способ узнать, какие именно слоты этот раунд заселял с нуля: сами счётчики
	/// свежести к моменту проверки уже тикнуты (см. смоук прокрутки в PreviewProbe).</summary>
	internal bool[]? LastThawed;

	internal int LastThawedCount;

	/// <summary>Снимает холод со ВСЕХ холодных слотов и возвращает их в индирекцию: зовётся сразу
	/// после того, как прогревающий диспатч записал им собственное поле (см.
	/// ProbeRoundGpu.WarmColdBricks). Это тот же переход, что делает <see cref="ConsumeFreshRound"/>
	/// в конце раунда, но выполненный НЕ ДОЖИДАЯСЬ конца раунда - именно ожидание и растягивало
	/// протечку на несколько кадров, потому что раунд идёт порциями через кадры.
	///
	/// Окно релокации слоту при этом остаётся открытым (счётчик падает на единицу, а не в ноль): у
	/// свежих проб половина стоит в стенах, и выбираться им ещё нужно.</summary>
	internal bool ThawColdBricks()
	{
		bool thawed = false;
		LastThawed ??= new bool[BrickTotal];
		Array.Clear(LastThawed);
		LastThawedCount = 0;

		for (int slot = 0; slot < BrickTotal; slot++)
		{
			if (BrickFresh[slot] >= ProbeGiBaker.RelocationRounds)
			{
				BrickFresh[slot]--;
				LastThawed[slot] = true;
				LastThawedCount++;
				thawed = true;
			}
		}

		if (thawed)
		{
			WriteIndirection();
			BrickStateDirty = true;
		}

		return thawed;
	}

	/// <summary>Диапазоны слотов, которые СЕЙЧАС холодны, слитые в непрерывные отрезки. Пробы одного
	/// кирпича лежат в буферах подряд (probe = slot * BrickProbes³ + local), поэтому отрезок слотов -
	/// это готовый диапазон диспатча, а слияние соседних экономит сами диспатчи: прокрутка заселяет
	/// плиту соседних слотов, и их обычно десятки.</summary>
	internal List<(int Start, int End)> ColdBrickRuns()
	{
		var runs = new List<(int Start, int End)>();
		int runStart = -1;

		for (int slot = 0; slot <= BrickTotal; slot++)
		{
			bool cold = slot < BrickTotal && BrickFresh[slot] >= ProbeGiBaker.RelocationRounds;
			if (cold)
			{
				if (runStart < 0)
				{
					runStart = slot;
				}

				continue;
			}

			if (runStart >= 0)
			{
				runs.Add((runStart, slot));
				runStart = -1;
			}
		}

		return runs;
	}

	/// <summary>Тикает свежесть слотов, заселённых прокруткой (см. <see cref="BrickFresh"/>).
	/// Раунд, который только что закончился, уже записал их собственное поле - значит холодный старт
	/// позади, кирпич можно показывать материалам, а окно релокации у него ещё открыто.</summary>
	private void ConsumeFreshRound()
	{
		bool wasCold = false, changed = false;
		for (int slot = 0; slot < BrickTotal; slot++)
		{
			if (BrickFresh[slot] == 0)
			{
				continue;
			}

			wasCold |= BrickFresh[slot] >= ProbeGiBaker.RelocationRounds;
			BrickFresh[slot]--;
			changed = true;
		}

		if (!changed)
		{
			return;
		}

		// У ПРОКРУЧИВАЕМОГО объёма индирекцию переписывает каждый шаг счётчика: там b - это
		// уверенность кирпича (см. BrickConfidence), и она растёт с каждым прожитым раундом.
		// У остальных - только когда холодный раунд у кого-то ЗАКОНЧИЛСЯ: прочие шаги меняют лишь
		// право на релокацию, а его читает буфер кирпичей, не текстура.
		if (wasCold || Scratch != null)
		{
			WriteIndirection();
		}

		BrickStateDirty = true;
	}

	/// <summary>ТОРОИДАЛЬНАЯ ПРОКРУТКА объёма: сдвигает его на целое число кирпичей вслед за точкой
	/// интереса, СОХРАНЯЯ поле там, где объём с собой пересёкся.
	///
	/// Это замена пересозданию каскада, и разница принципиальная, а не в скорости. Пересоздание
	/// означало новую сессию (десятки мегабайт аккумуляторов), новый комплект GPU-буферов вместе с
	/// выгрузкой ВСЕГО BVH сцены, семь новых атласов, переприязку материалов, Flush + WaitForIdle и
	/// холодный старт поля целиком - отсюда и рывок на каждое движение камеры, и дебаунс, который
	/// откладывал его до остановки. Прокрутка не создаёт НИЧЕГО: кирпич, оставшийся в объёме,
	/// удерживает свой слот пула вместе с накопленным полем, освободившиеся слоты переселяются во
	/// въехавшую область, и заново осматривается только она.
	///
	/// Сдвиг квантуется <see cref="ProbeGiBaker.ScrollQuantumBricks"/> кирпичами: мельче сетки проб
	/// ехать нельзя, иначе решётка проб сойдёт со своих мировых позиций и вся экономия прокрутки
	/// (кирпич остался на месте - поле уцелело) пропадёт вместе с ней.
	///
	/// Метод ПРИВАТНЫЙ, и это часть лечения. Прокрутка меняет мировые позиции проб, а их читают трое:
	/// материалы (угол сетки в кбуфере плюс карта индирекции), compute-раунд (буферы кирпичей) и
	/// дебаг-оверлей. Пока прокрутку разрешалось звать откуда угодно, она правила состояние на CPU
	/// немедленно, а на GPU уезжала только на границе раунда - и в промежутке эти трое читали РАЗНЫЕ
	/// поколения раскладки: шарики проб уже уехали за камерой, освещение ещё лежало по-старому.
	/// Границы раунда при этом ждать приходится долго - забор регулярно пропускает кадры, - так что
	/// расхождение держалось на экране, а не мелькало. Снаружи теперь есть только
	/// <see cref="RequestScroll"/>, а применяется заявка в одном месте - там же, откуда раскладка
	/// тем же вызовом уходит на GPU (см. ProbeRoundGpu.RunRound).</summary>
	/// <param name="desiredOrigin">Желаемый угол объёма; фактический округляется к сетке кирпичей.</param>
	/// <returns>true, если объём реально переехал.</returns>
	private bool Scroll(ProbeGiBaker baker, Vector3 desiredOrigin)
	{
		if (Scratch == null)
		{
			return false;
		}

		int quantum = ProbeGiBaker.ScrollQuantumBricks;
		var brickSize = Cell * ProbeGiBaker.BrickCells;
		var delta = desiredOrigin - Origin;
		int sx = (int)MathF.Round(delta.X / (brickSize.X * quantum)) * quantum;
		int sy = (int)MathF.Round(delta.Y / (brickSize.Y * quantum)) * quantum;
		int sz = (int)MathF.Round(delta.Z / (brickSize.Z * quantum)) * quantum;
		if (sx == 0 && sy == 0 && sz == 0)
		{
			return false;
		}

		// Кто где стоял - в координатах НОВОЙ сетки: по этой карте раскладка узнаёт кирпич, который
		// никуда не уехал, и оставляет ему слот. Без неё прокрутка выродилась бы в пересоздание с
		// сохранением буферов - поле всё равно начиналось бы с нуля.
		int cells = BrickCountX * BrickCountY * BrickCountZ;
		var reuseSlot = new int[cells];
		Array.Fill(reuseSlot, -1);
		var reuseLevel = new byte[cells];
		for (int slot = 0; slot < BrickTotal; slot++)
		{
			if (!BrickAlive[slot])
			{
				continue;
			}

			int ax = BrickCellOrigin[slot * 3 + 0] / ProbeGiBaker.BrickCells - sx;
			int ay = BrickCellOrigin[slot * 3 + 1] / ProbeGiBaker.BrickCells - sy;
			int az = BrickCellOrigin[slot * 3 + 2] / ProbeGiBaker.BrickCells - sz;
			if (ax < 0 || ay < 0 || az < 0
				|| ax >= BrickCountX || ay >= BrickCountY || az >= BrickCountZ)
			{
				continue;
			}

			int at = (az * BrickCountY + ay) * BrickCountX + ax;
			reuseSlot[at] = slot;
			reuseLevel[at] = BrickLevel[slot];
		}

		Origin += new Vector3(sx * brickSize.X, sy * brickSize.Y, sz * brickSize.Z);
		Result.Origin = Origin;
		Scratch.Shift(sx, sy, sz);

		var layout = baker.ClassifyBricks(Origin, Cell, BrickCountX, BrickCountY, BrickCountZ,
			Scratch, reuseSlot, reuseLevel, BrickTotal);

		BrickIndex = layout.Index;
		BrickLevelAt = layout.LevelAt;
		BrickLevel = layout.Level;
		BrickAlive = layout.Alive;
		for (int slot = 0; slot < BrickTotal; slot++)
		{
			BrickCellOrigin[slot * 3 + 0] = layout.Anchor[slot * 3 + 0] * ProbeGiBaker.BrickCells;
			BrickCellOrigin[slot * 3 + 1] = layout.Anchor[slot * 3 + 1] * ProbeGiBaker.BrickCells;
			BrickCellOrigin[slot * 3 + 2] = layout.Anchor[slot * 3 + 2] * ProbeGiBaker.BrickCells;

			if (layout.Fresh[slot] != 0)
			{
				BrickFresh[slot] = ProbeGiBaker.RelocationRounds;
			}
			else if (!layout.Alive[slot])
			{
				BrickFresh[slot] = 0;
			}
		}

		WriteIndirection();
		LayoutGeneration++;
		BrickStateDirty = true;
		return true;
	}

	/// <summary>Куда объём хочет переехать; null - заявок нет.</summary>
	private Vector3? _scrollRequest;

	/// <summary>Просит объём переехать углом в <paramref name="desiredOrigin"/>. Заявка НЕ исполняется
	/// на месте: раскладку двигает <see cref="ApplyPendingScroll"/> на границе раунда, атомарно с
	/// выгрузкой на GPU (см. <see cref="Scroll"/> о том, почему иначе разъезжаются шарики и свет).
	///
	/// Заявка перезаписывается, а не копится: пока камера летит, вьюпорт шлёт новую каждый кадр, и
	/// исполнить осмысленно можно только последнюю.</summary>
	internal void RequestScroll(Vector3 desiredOrigin) => _scrollRequest = desiredOrigin;

	/// <summary>Есть неисполненная заявка на переезд. Читает <see cref="ProbeRoundGpu"/>: объём с
	/// незакрытой заявкой сошедшимся считать нельзя, ему вот-вот привезут пустые кирпичи с краю.
	/// </summary>
	internal bool HasPendingScroll => _scrollRequest.HasValue;

	/// <summary>Порог средней изменчивости, ниже которого объём считается сошедшимся и раунды
	/// останавливаются (см. <see cref="ProbeGiBakeOptions.RealtimeVariabilityThreshold"/>). Живая
	/// ручка - меняется между раундами.</summary>
	public float VariabilityThreshold { get; set; }

	/// <summary>Исполняет отложенную заявку на переезд. Зовётся ТОЛЬКО с границы раунда и только
	/// оттуда, где следом идёт выгрузка раскладки на GPU.</summary>
	/// <returns>true, если объём переехал.</returns>
	internal bool ApplyPendingScroll(ProbeGiBaker baker)
	{
		if (_scrollRequest is not { } desired)
		{
			return false;
		}

		_scrollRequest = null;
		return Scroll(baker, desired);
	}

	/// <summary>Предел релокации для СВЕЖИХ проб (см. <see cref="BrickFresh"/>) - у них своё окно,
	/// не общесеточное: прокрутка приводит новые пробы непрерывно, пока летит камера, а открывать
	/// ради них релокацию всему объёму запрещает Majercik 2021 §5 (см.
	/// <see cref="RelocationRoundsLeft"/>) - это расшатало бы поле, которое прокрутка как раз и
	/// бережёт.</summary>
	internal float FreshRelocationLimit => Realtime
		? MathF.Max(RealtimeRelocation, 0f) * MathF.Min(Cell.X, MathF.Min(Cell.Y, Cell.Z))
		: 0f;

	/// <summary>Меняет местами читающий и пишущий буферы поля - конец раунда.</summary>
	internal void Swap()
	{
		(L0R, L0W) = (L0W, L0R);
		(L1XR, L1XW) = (L1XW, L1XR);
		(L1YR, L1YW) = (L1YW, L1YR);
		(L1ZR, L1ZW) = (L1ZW, L1ZR);
		(ValidityR, ValidityW) = (ValidityW, ValidityR);
		(SunFracR, SunFracW) = (SunFracW, SunFracR);
	}
}

/// <summary>Кэш радианса НА ПОВЕРХНОСТЯХ (подход Lumen/Unity Surface GI, здесь - мировая
/// разреженная воксельная параметризация). Смысл в том, чтобы отскок собирался не из редкой сетки
/// проб, а из значений, привязанных к самой геометрии: у проб шаг в метры, и красная штора отдаёт
/// свет на колонну «пятном размером с ячейку», тогда как вокселы кэша идут по поверхности с шагом
/// в разы мельче. Луч бейка проб, попав в геометрию, берёт готовый радианс отсюда вместо того,
/// чтобы каждый раз пересобирать его из поля проб.
///
/// Почему воксели, а не карты по мешам, как в Lumen: до пиксельного шейдера здесь не доходит
/// стабильный идентификатор инстанса (ECS + куллинг раздают слоты сами), а UV-развёртки под
/// лайтмап у glTF-моделей обычно нет. Мировая сетка не требует ни того, ни другого.
///
/// Вокселы существуют только там, где есть поверхность, поэтому при шаге вчетверо мельче пробного
/// их получается не в 64 раза больше, а единицы-десятки тысяч. И стоят они дёшево: раунд тратит на
/// воксель ОДИН теневой луч (резкая часть - солнце) плюс выборку поля проб (гладкая часть - небо и
/// переотскок), тогда как проба тратит десятки лучей.</summary>
public sealed class SurfaceCache
{
	/// <summary>Во сколько раз шаг вокселя мельче шага сетки проб.</summary>
	public const int Subdivision = 4;

	public int CountX { get; }
	public int CountY { get; }
	public int CountZ { get; }
	public Vector3 Origin { get; }
	public Vector3 Voxel { get; }

	/// <summary>Индекс вокселя в плотных массивах по координатам сетки, -1 = поверхности тут нет.</summary>
	private readonly int[] _index;

	/// <summary>Захваченная геометрия поверхности - считается один раз, от света не зависит.</summary>
	public Vector3[] Position = Array.Empty<Vector3>();
	public Vector3[] Normal = Array.Empty<Vector3>();
	public Vector3[] Albedo = Array.Empty<Vector3>();

	/// <summary>Исходящий радианс вокселя и доля солнца в нём - то, что забирают лучи бейка проб.</summary>
	public Vector3[] Radiance = Array.Empty<Vector3>();
	public float[] SunFraction = Array.Empty<float>();

	public int VoxelCount { get; private set; }

	internal SurfaceCache(Vector3 origin, Vector3 voxel, int cx, int cy, int cz)
	{
		Origin = origin;
		Voxel = voxel;
		CountX = cx;
		CountY = cy;
		CountZ = cz;
		_index = new int[cx * cy * cz];
	}

	/// <summary>Индекс вокселя, накрывающего мировую точку, или -1. Точка сдвигается вдоль нормали
	/// наружу: попадание луча лежит ровно НА поверхности, а из-за округления может оказаться в
	/// соседнем вокселе по ту сторону геометрии.</summary>
	public int Lookup(Vector3 worldPos)
	{
		var f = (worldPos - Origin) / Voxel;
		int x = (int)MathF.Floor(f.X), y = (int)MathF.Floor(f.Y), z = (int)MathF.Floor(f.Z);
		if (x < 0 || y < 0 || z < 0 || x >= CountX || y >= CountY || z >= CountZ)
		{
			return -1;
		}

		return _index[(z * CountY + y) * CountX + x];
	}

	/// <summary>Плотная карта «ячейка сетки → индекс вокселя или -1» - GPU-проход кэша ищет по ней
	/// точку попадания (см. SurfaceLookup в ProbeRoundCS.hlsl).</summary>
	public int[] ExportIndex() => _index;

	internal void Allocate(int[] denseIndex, int voxelCount)
	{
		Array.Copy(denseIndex, _index, denseIndex.Length);
		VoxelCount = voxelCount;
		Position = new Vector3[voxelCount];
		Normal = new Vector3[voxelCount];
		Albedo = new Vector3[voxelCount];
		Radiance = new Vector3[voxelCount];
		SunFraction = new float[voxelCount];
	}
}

/// <summary>Узел BVH в раскладке под StructuredBuffer - обязан совпадать байт-в-байт с BvhNode в
/// SceneTrace.hlsl. Паддинг явный: полагаться на то, как компилятор шейдеров разложит float3 в
/// структурированном буфере, нельзя.</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct BvhNodeGpu
{
	public Vector3 BoundsMin;

	/// <summary>&lt; 0 - лист (Start/Count задают срез в порядке треугольников), иначе индекс левого
	/// ребёнка; правый лежит в Start (см. ProbeGiBaker.Node).</summary>
	public int Left;

	public Vector3 BoundsMax;
	public int Start;
	public int Count;
	public int Pad0, Pad1, Pad2;
}

/// <summary>Треугольник сцены под StructuredBuffer - зеркало BvhTriangle в SceneTrace.hlsl.</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct BvhTriangleGpu
{
	public Vector3 A;
	public float Pad0;
	public Vector3 E1;
	public float Pad1;
	public Vector3 E2;
	public float Pad2;

	/// <summary>Линейное альбедо для отскока - трассировка на GPU возвращает его сразу, чтобы
	/// вызывающему не пришлось лезть за материалом.</summary>
	public Vector3 Albedo;
	public float Pad3;
}

/// <summary>Инстанс сцены для аппаратной трассировки: во что попал луч и где это стоит. Зеркало
/// SceneInstance в SceneTrace.hlsl в части, которую видит шейдер (первый треугольник меша и
/// альбедо); матрица шейдеру не нужна - её знает TLAS.
///
/// SourceInstance - индекс в ModelLoader.instances, откуда инстанс пришёл. Он нужен, чтобы
/// вызывающий мог забрать СВЕЖУЮ позу для пересборки TLAS: часть инстансов модели в геометрию не
/// попадает (стекло, листва, вырожденные меши), поэтому нумерация здесь своя.</summary>
/// <summary>SourceModel/LocalTransform - происхождение инстанса для слежения за позами
/// МУЛЬТИМОДЕЛЬНОЙ сцены (см. PrefabSceneViewport): индекс модели в списке, отданном бейкеру, и
/// локальная матрица glTF-инстанса. Мировая поза = LocalTransform * мир записи сцены, и когда
/// запись двигают гизмо, пересобрать TLAS можно без пересбора бейкера.</summary>
public readonly record struct ProbeGeometryInstance(int MeshSlot, int SourceInstance, Vector3 Albedo,
	Matrix4x4 Transform, int SourceModel = 0, Matrix4x4 LocalTransform = default);

/// <summary>
/// Геометрия сцены для АППАРАТНОЙ трассировки: треугольники в ОБЪЕКТНОМ пространстве, по одному
/// экземпляру на меш, плюс таблица инстансов с матрицами.
///
/// Почему не мировая похлёбка <see cref="ProbeGiBaker.ExportBvh"/>, которой пользуется программный
/// путь: она приколочена к позам объектов намертво - сдвинули инстанс, и надо перестраивать и
/// треугольники, и BVH целиком. Здесь же геометрия от позы не зависит вовсе, BLAS на меш строится
/// один раз, а движение мира стоит пересборки одного TLAS (см. ProbeSceneAccel).
///
/// Треугольники того же меша, использованного несколькими инстансами, лежат в единственном
/// экземпляре: BLAS и атрибуты для них общие, разъезжаются только матрица и альбедо.
/// </summary>
public sealed class ProbeInstancedGeometry
{
	/// <summary>Треугольники всех мешей подряд, в объектном пространстве. Поле альбедо не
	/// заполняется: оно свойство ИНСТАНСА (один меш может стоять в сцене с разными материалами),
	/// поэтому шейдер берёт его из <see cref="Instances"/>.</summary>
	public required BvhTriangleGpu[] Triangles { get; init; }

	/// <summary>Срез <see cref="Triangles"/> на каждый меш - по нему строится его BLAS, и он же
	/// даёт базу для CommittedPrimitiveIndex.</summary>
	public required (int First, int Count)[] Meshes { get; init; }

	/// <summary>Инстансы в порядке, в котором они уедут в TLAS: индекс здесь и есть InstanceID() в
	/// шейдере.</summary>
	public required ProbeGeometryInstance[] Instances { get; init; }

	public int TriangleCount => Triangles.Length;
}

public sealed class ProbeGiBaker
{
	// --- Сцена: мировые треугольники + BVH ---------------------------------------------------

	private struct Tri
	{
		public Vector3 A, E1, E2;

		/// <summary>Линейное альбедо для отскока (среднее по base color текстуре × фактор).</summary>
		public Vector3 Albedo;
	}

	private struct Node
	{
		public Vector3 Min, Max;

		/// <summary>Лист (Left &lt; 0): Start/Count - срез в _order. Внутренний узел: Left/Start -
		/// индексы левого/правого детей (правый НЕ Left+1: между ними всё левое поддерево -
		/// депth-first нумерация BuildNode).</summary>
		public int Left, Start, Count;
	}

	private Tri[] _tris = Array.Empty<Tri>();

	/// <summary>Объектная геометрия для аппаратного пути - собирается тем же проходом по модели, что
	/// и мировая похлёбка (см. конструктор).</summary>
	private ProbeInstancedGeometry _instanced = new()
	{
		Triangles = Array.Empty<BvhTriangleGpu>(),
		Meshes = Array.Empty<(int, int)>(),
		Instances = Array.Empty<ProbeGeometryInstance>(),
	};

	/// <summary>Геометрия сцены в объектном пространстве плюс таблица инстансов - основа BLAS/TLAS
	/// аппаратного пути (см. <see cref="ProbeInstancedGeometry"/>). Программному пути не нужна: он
	/// ходит по мировому BVH из <see cref="ExportBvh"/>.</summary>
	public ProbeInstancedGeometry InstancedGeometry => _instanced;

	/// <summary>Мировая матрица инстанса модели. Публичная и одна на всех нарочно: по ней строится
	/// и запечённая геометрия, и пересборка TLAS на движение объекта - разъехавшись, эти две
	/// сдвинули бы лучи относительно самой сцены.</summary>
	public static Matrix4x4 InstanceMatrix(Transform t) =>
		Matrix4x4.CreateScale(t.scale.X, t.scale.Y, t.scale.Z) *
		Matrix4x4.CreateFromQuaternion(t.rotation) *
		Matrix4x4.CreateTranslation(t.position);
	private int[] _order = Array.Empty<int>();
	private Node[] _nodes = Array.Empty<Node>();
	private int _nodeCount;
	private float _sceneEpsilon = 1e-3f;
	private float _rayTMax = 1e4f;

	public bool HasGeometry => _tris.Length > 0;

	/// <summary>Число треугольников в BVH - диагностика «бейк ничего не видит» (см. PreviewProbe).</summary>
	public int TriangleCount => _tris.Length;

	// --- Дисковый кеш BVH (см. ProbeGiBvhCache) ------------------------------------------------

	/// <summary>Треугольник мировой похлёбки в сериализуемом виде (зеркало приватного Tri).</summary>
	public struct CachedTri
	{
		public Vector3 A, E1, E2, Albedo;
	}

	/// <summary>Узел BVH в сериализуемом виде (зеркало приватного Node).</summary>
	public struct CachedNode
	{
		public Vector3 Min, Max;
		public int Left, Start, Count;
	}

	/// <summary>Полный слепок построенного BVH - всё, что нужно, чтобы поднять бейкер без обхода
	/// геометрии модели.</summary>
	public sealed class BvhCacheData
	{
		public required CachedTri[] Triangles { get; init; }
		public required CachedNode[] Nodes { get; init; }
		public required int[] Order { get; init; }
		public required int NodeCount { get; init; }
		public required float SceneEpsilon { get; init; }
		public required float RayTMax { get; init; }
		public required BvhTriangleGpu[] ObjectTriangles { get; init; }
		public required (int First, int Count)[] MeshSlots { get; init; }
		public required ProbeGeometryInstance[] Instances { get; init; }
	}

	/// <summary>Восстановление из кеша - конструктор без единого обращения к геометрии модели.</summary>
	private ProbeGiBaker(BvhCacheData data)
	{
		_tris = new Tri[data.Triangles.Length];
		for (int i = 0; i < _tris.Length; i++)
		{
			var t = data.Triangles[i];
			_tris[i] = new Tri { A = t.A, E1 = t.E1, E2 = t.E2, Albedo = t.Albedo };
		}

		_nodes = new Node[data.Nodes.Length];
		for (int i = 0; i < _nodes.Length; i++)
		{
			var n = data.Nodes[i];
			_nodes[i] = new Node { Min = n.Min, Max = n.Max, Left = n.Left, Start = n.Start, Count = n.Count };
		}

		_order = data.Order;
		_nodeCount = data.NodeCount;
		_sceneEpsilon = data.SceneEpsilon;
		_rayTMax = data.RayTMax;

		_instanced = new ProbeInstancedGeometry
		{
			Triangles = data.ObjectTriangles,
			Meshes = data.MeshSlots,
			Instances = data.Instances,
		};
	}

	/// <summary>Слепок текущего BVH для записи в кеш.</summary>
	public BvhCacheData ExportCache()
	{
		var triangles = new CachedTri[_tris.Length];
		for (int i = 0; i < triangles.Length; i++)
		{
			ref var t = ref _tris[i];
			triangles[i] = new CachedTri { A = t.A, E1 = t.E1, E2 = t.E2, Albedo = t.Albedo };
		}

		var nodes = new CachedNode[_nodeCount];
		for (int i = 0; i < nodes.Length; i++)
		{
			ref var n = ref _nodes[i];
			nodes[i] = new CachedNode { Min = n.Min, Max = n.Max, Left = n.Left, Start = n.Start, Count = n.Count };
		}

		return new BvhCacheData
		{
			Triangles = triangles,
			Nodes = nodes,
			Order = _order,
			NodeCount = _nodeCount,
			SceneEpsilon = _sceneEpsilon,
			RayTMax = _rayTMax,
			ObjectTriangles = _instanced.Triangles,
			MeshSlots = _instanced.Meshes,
			Instances = _instanced.Instances,
		};
	}

	/// <summary>
	/// Бейкер по одной модели: сперва пробуем кеш &lt;модель&gt;.bhv.bin рядом с ней, и только если
	/// его нет (или он от другой версии файла) - строим BVH и кладём результат в кеш. Сборка стоит
	/// десятки секунд на тяжёлом ассете, а геометрия между запусками не меняется.
	/// </summary>
	public static ProbeGiBaker LoadOrBuild(ModelLoader model, string modelPath, out bool fromCache)
	{
		fromCache = false;

		if (!string.IsNullOrEmpty(modelPath))
		{
			var cached = ProbeGiBvhCache.TryRead(modelPath);
			if (cached != null)
			{
				fromCache = true;
				return new ProbeGiBaker(cached);
			}
		}

		var baker = new ProbeGiBaker(model);

		if (!string.IsNullOrEmpty(modelPath) && baker.HasGeometry)
		{
			ProbeGiBvhCache.Write(modelPath, baker.ExportCache());
		}

		return baker;
	}

	// --- Диагностика BVH -----------------------------------------------------------------------

	/// <summary>Сводка по построенному дереву - для отладочного вывода и оверлея.</summary>
	public readonly record struct BvhStats(int Triangles, int Nodes, int Leaves, int MaxDepth,
		float AvgLeafTriangles, Vector3 Min, Vector3 Max);

	public BvhStats GetStats()
	{
		if (_nodeCount == 0)
		{
			return new BvhStats(0, 0, 0, 0, 0f, Vector3.Zero, Vector3.Zero);
		}

		int leaves = 0, maxDepth = 0;
		long leafTris = 0;
		CountStats(0, 1, ref leaves, ref maxDepth, ref leafTris);

		return new BvhStats(_tris.Length, _nodeCount, leaves, maxDepth,
			leaves > 0 ? (float)leafTris / leaves : 0f, _nodes[0].Min, _nodes[0].Max);
	}

	private void CountStats(int nodeIndex, int depth, ref int leaves, ref int maxDepth, ref long leafTris)
	{
		ref var node = ref _nodes[nodeIndex];
		if (depth > maxDepth)
		{
			maxDepth = depth;
		}

		if (node.Left < 0)
		{
			leaves++;
			leafTris += node.Count;
			return;
		}

		CountStats(node.Left, depth + 1, ref leaves, ref maxDepth, ref leafTris);
		CountStats(node.Start, depth + 1, ref leaves, ref maxDepth, ref leafTris);
	}

	/// <summary>
	/// Боксы узлов дерева для отладочной отрисовки. <paramref name="maxDepth"/> - до какой глубины
	/// спускаться (0 = только корень); <paramref name="leavesOnly"/> - брать только листья, то есть
	/// показывать фактическую гранулярность разбиения, а не вложенные объёмы.
	/// </summary>
	public List<(Vector3 Min, Vector3 Max, int Depth)> CollectDebugBoxes(int maxDepth, bool leavesOnly)
	{
		var boxes = new List<(Vector3, Vector3, int)>();
		if (_nodeCount > 0)
		{
			CollectBoxes(0, 0, maxDepth, leavesOnly, boxes);
		}

		return boxes;
	}

	private void CollectBoxes(int nodeIndex, int depth, int maxDepth, bool leavesOnly,
		List<(Vector3, Vector3, int)> boxes)
	{
		ref var node = ref _nodes[nodeIndex];
		bool isLeaf = node.Left < 0;

		if (!leavesOnly || isLeaf)
		{
			if (depth <= maxDepth || (leavesOnly && isLeaf))
			{
				boxes.Add((node.Min, node.Max, depth));
			}
		}

		if (isLeaf || depth >= maxDepth)
		{
			return;
		}

		CollectBoxes(node.Left, depth + 1, maxDepth, leavesOnly, boxes);
		CollectBoxes(node.Start, depth + 1, maxDepth, leavesOnly, boxes);
	}

	/// <summary>Порог луча по дальности, за которым попадание считается промахом - тот же, что
	/// использует CPU-трассировщик; GPU-обход обязан брать его отсюда, иначе пути разойдутся.</summary>
	public float RayTMax => _rayTMax;

	/// <summary>Отступ теневого луча от поверхности - GPU-путь обязан брать его отсюда, иначе
	/// самозатенение разойдётся с CPU-эталоном.</summary>
	public float SceneEpsilon => _sceneEpsilon;

	/// <summary>Направления лучей конкретного раунда. GPU-путь берёт их отсюда, а не пересчитывает
	/// у себя: расхождение в последнем бите синуса увело бы луч на соседний треугольник у силуэта,
	/// и сверка с CPU-эталоном перестала бы что-либо значить (см. ProbeRoundCS.hlsl).</summary>
	public static Vector3[] RoundRayDirections(int rays, int sequence) =>
		BuildRotatedFibonacciSphere(rays, sequence);

	/// <summary>Сколько первых лучей веера не вращать (RTXGI-DDGI, RTXGI_DDGI_NUM_FIXED_RAYS).
	///
	/// Смысл приёма (ProbeRayCommon.hlsl: «Don't rotate fixed rays so relocation/classification are
	/// temporally stable»): решения о ПЕРЕЕЗДЕ пробы и о её отключении принимаются по геометрии -
	/// доле задних граней, ближайшему выходу наружу, запасу свободного места. Считать их по вееру,
	/// который каждый раунд повёрнут заново, значит мерить дрожащей линейкой: у пробы на кромке
	/// геометрии доля задних граней гуляет от раунда к раунду просто из-за смены направлений, и
	/// проба то уезжает, то возвращается, каждый раз сбрасывая накопители. Небольшой набор лучей,
	/// НЕ зависящий от номера раунда, даёт этим решениям устойчивую опору.
	///
	/// Фиксированные лучи не участвуют в оценке радианса и в карте глубин (см. ProbeRoundCS): они
	/// не вращаются, поэтому их направления представлены в среднем вдвое чаще остальных, и подмешать
	/// их значило бы внести в оценку постоянное смещение по этим направлениям. Трассируются они не
	/// впустую - именно они и делают всю геометрическую работу.
	///
	/// ТОЛЬКО в реальном времени. В запечке веер вращается по номеру раунда, оба пути (CPU и GPU)
	/// обязаны совпасть луч в луч ради сверки, и делить веер значило бы зеркалить всю раскладку ещё
	/// и в CPU-бейкере; выгоды при этом нет - в запечке проба переезжает один раз на инициализации.
	/// Доля - восьмая часть веера (у эталона 32 из 288, тот же порядок) с полом 16 и потолком 32,
	/// и только начиная с 64 лучей. Пол не занижен сознательно: по этим лучам ищется БЛИЖАЙШАЯ
	/// передняя грань, и на слишком редком веере проба рискует не заметить рядом стоящую
	/// поверхность и решить, что вокруг просторно (ветка возврата к узлу в ProbeRoundCS). Потолок
	/// держит цену: сверх 32 устойчивость решений уже не растёт, а лучи из оценки радианса
	/// вычитаются. На коротком веере (меньше 64) деления нет вовсе - отдать четверть выборки ради
	/// устойчивости релокации невыгодно, шум радианса дороже.</summary>
	public static int FixedRayCount(int rays, bool realtime) =>
		realtime && rays >= 64 ? Math.Min(32, Math.Max(rays / 8, 16)) : 0;

	/// <summary>Направления раунда с учётом фиксированных лучей: [0, FixedRays) - НЕвращаемый веер
	/// Фибоначчи, [FixedRays, rays) - обычный, повёрнутый по номеру раунда. Оба - равномерные
	/// сферические выборки, поэтому каждая часть остаётся корректной сама по себе.</summary>
	public static Vector3[] RoundRayDirections(ProbeGiBakeSession session) =>
		RoundRayDirections(session.RaysPerRound, session.Sequence, session.FixedRays);

	/// <inheritdoc cref="RoundRayDirections(ProbeGiBakeSession)"/>
	public static Vector3[] RoundRayDirections(int rays, int sequence, int fixedRays)
	{
		if (fixedRays <= 0)
		{
			return BuildRotatedFibonacciSphere(rays, sequence);
		}

		var dirs = new Vector3[rays];
		Array.Copy(BuildFibonacciSphere(fixedRays), dirs, fixedRays);
		Array.Copy(BuildRotatedFibonacciSphere(rays - fixedRays, sequence), 0,
			dirs, fixedRays, rays - fixedRays);
		return dirs;
	}

	/// <summary>Вес раунда в бегущем среднем - GPU-путь считает его тем же способом, что и
	/// <see cref="RunRound"/>, иначе поля разойдутся по яркости.
	///
	/// Сессия, а не просто номер раунда: пол веса зависит от режима (см.
	/// <see cref="ProbeGiBakeSession.MinBlend"/>), и в реальном времени именно он превращает бегущее
	/// среднее в экспоненциальное - формула та же, разъезжаются только асимптоты.</summary>
	public static float RoundBlendWeight(ProbeGiBakeSession session)
	{
		int averaged = session.Round - BootstrapRounds;
		return averaged < 0 ? 1f : MathF.Max(1f / (averaged + 1), session.MinBlend);
	}

	/// <summary>Трассирует один луч CPU-обходом. Это тот же код, которым идёт бейк, вынесенный
	/// наружу как ЭТАЛОН для сверки GPU-путей (см. SceneTrace.hlsl и сверочный прогон в
	/// PreviewProbe): CPU-трассировщик уже рабочий, и расхождение с ним - это баг GPU-обхода.</summary>
	public bool TraceRay(Vector3 origin, Vector3 direction, float tMax,
		out float t, out Vector3 normal, out Vector3 albedo)
	{
		normal = Vector3.UnitY;
		albedo = Vector3.Zero;

		if (!TraceClosest(origin, direction, out t, out int triIndex) || t > tMax)
		{
			t = 0f;
			return false;
		}

		ref var tri = ref _tris[triIndex];
		normal = Vector3.Normalize(Vector3.Cross(tri.E1, tri.E2));
		albedo = tri.Albedo;
		return true;
	}

	/// <summary>Выгружает BVH в раскладке под StructuredBuffer для compute-обхода на GPU (см.
	/// SceneTrace.hlsl) - путь для железа без аппаратной трассировки. Структура ровно та же, по
	/// которой ходит CPU-трассировщик, поэтому compute-путь можно сверять с ним луч в луч.</summary>
	public (BvhNodeGpu[] Nodes, uint[] Order, BvhTriangleGpu[] Triangles) ExportBvh()
	{
		var nodes = new BvhNodeGpu[Math.Max(_nodeCount, 1)];
		for (int i = 0; i < _nodeCount; i++)
		{
			ref var node = ref _nodes[i];
			nodes[i] = new BvhNodeGpu
			{
				BoundsMin = node.Min,
				BoundsMax = node.Max,
				Left = node.Left,
				Start = node.Start,
				Count = node.Count,
			};
		}

		var order = new uint[Math.Max(_order.Length, 1)];
		for (int i = 0; i < _order.Length; i++)
		{
			order[i] = (uint)_order[i];
		}

		var triangles = new BvhTriangleGpu[Math.Max(_tris.Length, 1)];
		for (int i = 0; i < _tris.Length; i++)
		{
			ref var tri = ref _tris[i];
			triangles[i] = new BvhTriangleGpu
			{
				A = tri.A,
				E1 = tri.E1,
				E2 = tri.E2,
				Albedo = tri.Albedo,
			};
		}

		return (nodes, order, triangles);
	}

	/// <summary>Число лучей на пробу по умолчанию (сферический Фибоначчи, фиксированный веер -
	/// при L1-проекции регулярность паттерна не полосит). Реальное берётся из
	/// <see cref="ProbeGiBakeOptions.RaysPerProbe"/>.</summary>
	public const int DefaultRaysPerProbe = 96;

	/// <summary>Потолок бюджета проб (верхний кламп <see cref="ProbeGiBakeOptions.MaxProbes"/>) -
	/// им же размечен комбо "Max probes" в окне Graphics. Ограничивает бейк, а не раскладку
	/// атласов: ячейка укрупняется, пока сетка не влезет в бюджет.</summary>
	public const int MaxProbeBudget = 2097152;

	/// <summary>Нижний кламп бюджета проб: сетка меньше 2x2x2 по осям бессмысленна, а совсем
	/// мелкий бюджет схлопывает ячейку в габарит сцены.</summary>
	public const int MinProbeBudget = 512;

	/// <summary>Потолок числа КИРПИЧЕЙ по одной оси. Это не про стоимость бейка (её держит
	/// <see cref="MaxProbeBudget"/>, теперь считая только живые кирпичи), а про размер карты
	/// индирекции: она имеет размер BrickCountX x (BrickCountY*BrickCountZ) и обязана влезть в
	/// <see cref="MaxAtlasDimension"/>. 256³ кирпичей - это 256*256 = 65536 в высоту, вчетверо за
	/// пределом, поэтому 128: 128*128 = 16384 ровно в предел.</summary>
	public const int MaxBricksPerAxis = 128;

	/// <summary>Предел стороны текстуры, в который обязаны влезть атласы проб (гарантия D3D12 и
	/// Vulkan-реализаций - 16384). Проверяется по САМОМУ большому измерению окто-атласа видимости
	/// (высота пула = PoolRows*BrickProbes²*<see cref="ProbeGiBakeResult.VisRes"/>) и по высоте
	/// карты индирекции.</summary>
	public const int MaxAtlasDimension = 16384;

	/// <summary>Читает CPU-копии мешей (<see cref="IMeshObject.VertexData"/>) - вызывать на потоке,
	/// владеющем моделью (главном); дальше Bake можно уносить в фон.</summary>
	public unsafe ProbeGiBaker(ModelLoader model)
		: this(new[] { (model, Matrix4x4.Identity) }, trackSourceInstances: true)
	{
	}

	/// <summary>Сцена из нескольких моделей с мировыми матрицами (окно Scene View, см.
	/// PrefabSceneViewport): треугольники каждого инстанса каждой модели попадают в мировой BVH
	/// через InstanceMatrix(инстанс) * World. При trackSourceInstances=false SourceInstance пишется
	/// -1 - слежение за движением инстансов по исходной модели (PollProbeAccel превью) для
	/// мульти-модельной сцены не имеет смысла: её позы задают сущности префаба, и на их изменение
	/// сцена просто пересоздаёт сессию.</summary>
	public unsafe ProbeGiBaker(IReadOnlyList<(ModelLoader Model, Matrix4x4 World)> models,
		bool trackSourceInstances = false)
	{
		var tris = new List<Tri>();

		// Параллельно мировой похлёбке собирается ОБЪЕКТНАЯ геометрия для аппаратной трассировки
		// (см. ProbeInstancedGeometry): те же меши, но по разу на меш и без матрицы инстанса.
		// Дедуп - по паре (модель, меш): меш, инстанцированный многими сущностями сцены, получает
		// один срез треугольников/BLAS.
		var objectTris = new List<BvhTriangleGpu>();
		var meshSlots = new List<(int First, int Count)>();
		var meshSlotByMeshId = new Dictionary<(ModelLoader, int), int>();
		var geometryInstances = new List<ProbeGeometryInstance>();

		for (int modelIndex = 0; modelIndex < models.Count; modelIndex++)
		{
		var (model, world) = models[modelIndex];
		for (int sourceIndex = 0; sourceIndex < model.instances.Count; sourceIndex++)
		{
			var instance = model.instances[sourceIndex];
			if (instance.meshId < 0 || instance.meshId >= model.Meshes.Count)
			{
				continue;
			}

			// Стекло свет не блокирует, линии/точки не геометрия. Реально «дырявые» материалы
			// (листва/трава/решётки: средняя альфа base color текстуры мала) тоже пропускаем
			// ЦЕЛИКОМ: трассировщик не сэмплирует текстуры, и ажурные квады стали бы сплошными
			// стенами - крона дерева наглухо гасила бы солнце и небо во всём дворе (пропадал
			// солнечный баунс от пола - галереи чернели, см. Sponza). Критерий - именно средняя
			// альфа, а НЕ AlphaCutoff: экспортеры сплошь метят камень/ткань как MASK/BLEND
			// (альфа ~1), и фильтр по режиму выкидывал из BVH всю сцену. Цена - листва не даёт
			// GI-тени; экранная тень от неё остаётся за shadow map-ой.
			Vector3 albedo = new(0.5f);
			if (model.MaterialPbr.TryGetValue(instance.materialId, out var pbr))
			{
				bool sparse = pbr.AlphaCutoff > 0f && pbr.HasBaseColorTexture && pbr.AverageAlpha < 0.6f;
				if (pbr.Topology != ModelLoader.MeshTopologyTriangles || pbr.TransmissionFactor > 0.5f ||
					sparse)
				{
					continue;
				}

				albedo = pbr.AverageBaseColor.LengthSquared() > 1e-6f
					? pbr.AverageBaseColor
					: new Vector3(pbr.BaseColorFactor.X, pbr.BaseColorFactor.Y, pbr.BaseColorFactor.Z);
			}

			// Кламп сверху: альбедо ~1 в замкнутом дворе раскачивает мультибаунс до пересвета.
			albedo = Vector3.Min(albedo, new Vector3(0.85f));

			var mesh = model.Meshes[instance.meshId];
			if (mesh.IndexCount < 3 || mesh.VertexData == null || mesh.IndexData == null)
			{
				continue;
			}

			var matrix = InstanceMatrix(instance.transform) * world;

			int vertexCount = UnsafeArray.GetLength(mesh.VertexData);
			var vertices = new ReadOnlySpan<Vertex>(UnsafeArray.GetPtr<Vertex>(mesh.VertexData, 0), vertexCount);

			// ModelLoader всегда строит 32-битные индексы (см. PreparedMesh.Indices: uint[]).
			var indices = new ReadOnlySpan<uint>(UnsafeArray.GetPtr<uint>(mesh.IndexData, 0), mesh.IndexCount);

			// Объектная копия меша - только для первого встреченного его инстанса: остальные
			// переиспользуют и срез треугольников, и построенный по нему BLAS.
			if (!meshSlotByMeshId.TryGetValue((model, instance.meshId), out int meshSlot))
			{
				int firstObjectTri = objectTris.Count;
				for (int i = 0; i + 2 < indices.Length; i += 3)
				{
					uint j0 = indices[i], j1 = indices[i + 1], j2 = indices[i + 2];
					if (j0 >= vertexCount || j1 >= vertexCount || j2 >= vertexCount)
					{
						continue;
					}

					var oa = vertices[(int)j0].Position;
					var oe1 = vertices[(int)j1].Position - oa;
					var oe2 = vertices[(int)j2].Position - oa;
					if (Vector3.Cross(oe1, oe2).LengthSquared() < 1e-16f)
					{
						continue;
					}

					// Альбедо здесь не пишется - оно у ИНСТАНСА (см. ProbeInstancedGeometry).
					objectTris.Add(new BvhTriangleGpu { A = oa, E1 = oe1, E2 = oe2 });
				}

				// Меш целиком выродился (нулевой масштаб в самих вершинах, склеенные точки) -
				// инстансу не на что ссылаться, BLAS строить не из чего.
				meshSlot = objectTris.Count > firstObjectTri ? meshSlots.Count : -1;
				if (meshSlot >= 0)
				{
					meshSlots.Add((firstObjectTri, objectTris.Count - firstObjectTri));
				}

				meshSlotByMeshId[(model, instance.meshId)] = meshSlot;
			}

			if (meshSlot >= 0)
			{
				geometryInstances.Add(new ProbeGeometryInstance(meshSlot,
					trackSourceInstances ? sourceIndex : -1, albedo, matrix,
					modelIndex, InstanceMatrix(instance.transform)));
			}

			for (int i = 0; i + 2 < indices.Length; i += 3)
			{
				uint i0 = indices[i], i1 = indices[i + 1], i2 = indices[i + 2];
				if (i0 >= vertexCount || i1 >= vertexCount || i2 >= vertexCount)
				{
					continue;
				}

				var a = Vector3.Transform(vertices[(int)i0].Position, matrix);
				var b = Vector3.Transform(vertices[(int)i1].Position, matrix);
				var c = Vector3.Transform(vertices[(int)i2].Position, matrix);

				var e1 = b - a;
				var e2 = c - a;
				if (Vector3.Cross(e1, e2).LengthSquared() < 1e-16f)
				{
					continue;
				}

				tris.Add(new Tri { A = a, E1 = e1, E2 = e2, Albedo = albedo });
			}
		}
		}

		_tris = tris.ToArray();

		// Вырожденность проверяется в СВОЁМ пространстве у каждой похлёбки, поэтому счётчики
		// треугольников могут разойтись на единицы на патологических матрицах (нулевой масштаб
		// схлопывает мировой треугольник, оставляя объектный живым). Пути от этого не разъезжаются:
		// каждый читает атрибуты из своего массива, а сверяются они по попаданиям луча.
		_instanced = new ProbeInstancedGeometry
		{
			Triangles = objectTris.ToArray(),
			Meshes = meshSlots.ToArray(),
			Instances = geometryInstances.ToArray(),
		};

		if (_tris.Length == 0)
		{
			return;
		}

		BuildBvh();
	}

	// --- BVH (медианный сплит по крупнейшей оси, лист ≤ 4 треугольников) ----------------------

	private void BuildBvh()
	{
		int n = _tris.Length;
		_order = new int[n];
		var centroids = new Vector3[n];
		for (int i = 0; i < n; i++)
		{
			_order[i] = i;
			centroids[i] = _tris[i].A + (_tris[i].E1 + _tris[i].E2) * (1f / 3f);
		}

		_nodes = new Node[2 * n];
		_nodeCount = 0;
		var sceneMin = new Vector3(float.MaxValue);
		var sceneMax = new Vector3(float.MinValue);

		BuildNode(0, n, centroids, ref sceneMin, ref sceneMax);

		var size = sceneMax - sceneMin;
		float maxDim = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
		_sceneEpsilon = MathF.Max(maxDim * 5e-4f, 1e-5f);
		_rayTMax = MathF.Max(maxDim * 4f, 1f);
	}

	private int BuildNode(int start, int count, Vector3[] centroids, ref Vector3 outMin, ref Vector3 outMax)
	{
		int nodeIndex = _nodeCount++;
		var min = new Vector3(float.MaxValue);
		var max = new Vector3(float.MinValue);
		for (int i = start; i < start + count; i++)
		{
			ref var tri = ref _tris[_order[i]];
			var b = tri.A + tri.E1;
			var c = tri.A + tri.E2;
			min = Vector3.Min(min, Vector3.Min(tri.A, Vector3.Min(b, c)));
			max = Vector3.Max(max, Vector3.Max(tri.A, Vector3.Max(b, c)));
		}

		outMin = Vector3.Min(outMin, min);
		outMax = Vector3.Max(outMax, max);

		if (count <= 4)
		{
			_nodes[nodeIndex] = new Node { Min = min, Max = max, Left = -1, Start = start, Count = count };
			return nodeIndex;
		}

		var size = max - min;
		int axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;

		// Медиана по центроидам: сортировка среза _order компаратором по оси.
		Array.Sort(_order, start, count, Comparer<int>.Create((x, y) =>
			GetAxis(centroids[x], axis).CompareTo(GetAxis(centroids[y], axis))));

		int half = count / 2;
		var dummyMin = new Vector3(float.MaxValue);
		var dummyMax = new Vector3(float.MinValue);
		int left = BuildNode(start, half, centroids, ref dummyMin, ref dummyMax);
		int right = BuildNode(start + half, count - half, centroids, ref dummyMin, ref dummyMax);

		_nodes[nodeIndex] = new Node { Min = min, Max = max, Left = left, Start = right, Count = 0 };
		return nodeIndex;
	}

	private static float GetAxis(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

	// --- Трассировка --------------------------------------------------------------------------

	private static bool RayBox(Vector3 origin, Vector3 invDir, float tMax, in Node node)
	{
		float tx1 = (node.Min.X - origin.X) * invDir.X;
		float tx2 = (node.Max.X - origin.X) * invDir.X;
		float tmin = MathF.Min(tx1, tx2);
		float tmax = MathF.Max(tx1, tx2);

		float ty1 = (node.Min.Y - origin.Y) * invDir.Y;
		float ty2 = (node.Max.Y - origin.Y) * invDir.Y;
		tmin = MathF.Max(tmin, MathF.Min(ty1, ty2));
		tmax = MathF.Min(tmax, MathF.Max(ty1, ty2));

		float tz1 = (node.Min.Z - origin.Z) * invDir.Z;
		float tz2 = (node.Max.Z - origin.Z) * invDir.Z;
		tmin = MathF.Max(tmin, MathF.Min(tz1, tz2));
		tmax = MathF.Min(tmax, MathF.Max(tz1, tz2));

		return tmax >= MathF.Max(tmin, 0f) && tmin <= tMax;
	}

	/// <summary>Möller–Trumbore, двусторонний. Возвращает t или -1.</summary>
	private static float RayTri(Vector3 origin, Vector3 dir, in Tri tri)
	{
		var p = Vector3.Cross(dir, tri.E2);
		float det = Vector3.Dot(tri.E1, p);
		if (MathF.Abs(det) < 1e-9f)
		{
			return -1f;
		}

		float invDet = 1f / det;
		var s = origin - tri.A;
		float u = Vector3.Dot(s, p) * invDet;
		if (u < 0f || u > 1f)
		{
			return -1f;
		}

		var q = Vector3.Cross(s, tri.E1);
		float v = Vector3.Dot(dir, q) * invDet;
		if (v < 0f || u + v > 1f)
		{
			return -1f;
		}

		float t = Vector3.Dot(tri.E2, q) * invDet;
		return t > 0f ? t : -1f;
	}

	private bool TraceClosest(Vector3 origin, Vector3 dir, out float hitT, out int hitTri)
	{
		hitT = _rayTMax;
		hitTri = -1;

		var invDir = new Vector3(
			1f / (MathF.Abs(dir.X) < 1e-12f ? MathF.CopySign(1e-12f, dir.X) : dir.X),
			1f / (MathF.Abs(dir.Y) < 1e-12f ? MathF.CopySign(1e-12f, dir.Y) : dir.Y),
			1f / (MathF.Abs(dir.Z) < 1e-12f ? MathF.CopySign(1e-12f, dir.Z) : dir.Z));

		Span<int> stack = stackalloc int[64];
		int sp = 0;
		stack[sp++] = 0;

		while (sp > 0)
		{
			ref var node = ref _nodes[stack[--sp]];
			if (!RayBox(origin, invDir, hitT, node))
			{
				continue;
			}

			if (node.Left < 0)
			{
				for (int i = node.Start; i < node.Start + node.Count; i++)
				{
					int triIndex = _order[i];
					float t = RayTri(origin, dir, _tris[triIndex]);
					if (t > 0f && t < hitT)
					{
						hitT = t;
						hitTri = triIndex;
					}
				}
			}
			else if (sp + 2 <= stack.Length)
			{
				stack[sp++] = node.Left;
				stack[sp++] = node.Start;
			}
		}

		return hitTri >= 0;
	}

	private bool TraceAnyHit(Vector3 origin, Vector3 dir, float tMax)
	{
		var invDir = new Vector3(
			1f / (MathF.Abs(dir.X) < 1e-12f ? MathF.CopySign(1e-12f, dir.X) : dir.X),
			1f / (MathF.Abs(dir.Y) < 1e-12f ? MathF.CopySign(1e-12f, dir.Y) : dir.Y),
			1f / (MathF.Abs(dir.Z) < 1e-12f ? MathF.CopySign(1e-12f, dir.Z) : dir.Z));

		Span<int> stack = stackalloc int[64];
		int sp = 0;
		stack[sp++] = 0;

		while (sp > 0)
		{
			ref var node = ref _nodes[stack[--sp]];
			if (!RayBox(origin, invDir, tMax, node))
			{
				continue;
			}

			if (node.Left < 0)
			{
				for (int i = node.Start; i < node.Start + node.Count; i++)
				{
					float t = RayTri(origin, dir, _tris[_order[i]]);
					if (t > 0f && t < tMax)
					{
						return true;
					}
				}
			}
			else if (sp + 2 <= stack.Length)
			{
				stack[sp++] = node.Left;
				stack[sp++] = node.Start;
			}
		}

		return false;
	}

	// --- Разреженная сетка кирпичей ------------------------------------------------------------

	/// <summary>Проб на ось кирпича. Кирпич - минимальная единица выделения: пробы существуют
	/// только там, где рядом есть геометрия, а пустое пространство (в сцене-уровне это бОльшая
	/// часть баунда) не стоит ничего. Ровно этим Unity APV набирает плотность у поверхностей, не
	/// упираясь в бюджет.</summary>
	public const int BrickProbes = 4;

	/// <summary>Ячеек на ось кирпича = BrickProbes - 1. Граничные пробы соседних кирпичей
	/// ДУБЛИРУЮТСЯ, и это принципиально: любая точка попадает в ячейку одного кирпича, все восемь
	/// углов её трилинейной интерполяции лежат в нём же, и шейдеру хватает одного чтения
	/// индирекции на сэмпл вместо восьми.</summary>
	public const int BrickCells = BrickProbes - 1;

	/// <summary>Сколько уровней подразделения кирпичей. Кирпич уровня L имеет шаг проб cell*2^L и
	/// накрывает 2^L мелких кирпичей по каждой оси, то есть стоит в 8^L раз меньше проб. Уровень
	/// выбирается по «плоскости» геометрии (см. ClassifyBricks); 3 уровня дают крупному кирпичу
	/// экономию в 64 раза - достаточно, чтобы ровный пол уровня перестал съедать бюджет.</summary>
	public const int MaxBrickLevel = 3;


	/// <summary>Кирпич хранится в атласе блоком BrickProbes × BrickProbes² текселей; сколько таких
	/// блоков в ряду пула, выбирается так, чтобы САМЫЙ большой атлас (окто-карта видимости, вдвое
	/// по восемь раз крупнее) вышел примерно квадратным.</summary>
	private static int ChoosePoolColumns(int brickTotal)
	{
		if (brickTotal <= 0)
		{
			return 1;
		}

		// Ширина vis-атласа = cols*BrickProbes*VisRes, высота = ceil(n/cols)*BrickProbes²*VisRes;
		// приравняв, получаем cols ≈ sqrt(n * BrickProbes).
		int cols = (int)MathF.Ceiling(MathF.Sqrt(brickTotal * (float)BrickProbes));
		int maxCols = MaxAtlasDimension / (BrickProbes * ProbeGiBakeResult.VisRes);
		return Math.Clamp(cols, 1, maxCols);
	}

	/// <summary>Тексель пробы в SH-атласе пула: slot = brick*BrickProbes³ + (lz*BrickProbes+ly)*
	/// BrickProbes + lx (см. RunRound/Snapshot).</summary>
	private static (int X, int Y) ProbeTexel(int slot, int poolColumns)
	{
		const int perBrick = BrickProbes * BrickProbes * BrickProbes;
		int brick = slot / perBrick;
		int local = slot - brick * perBrick;
		int lx = local % BrickProbes;
		int ly = local / BrickProbes % BrickProbes;
		int lz = local / (BrickProbes * BrickProbes);

		int col = brick % poolColumns;
		int row = brick / poolColumns;
		return (col * BrickProbes + lx,
			row * BrickProbes * BrickProbes + lz * BrickProbes + ly);
	}

	/// <summary>Захватывает поверхности сцены в разреженную воксельную сетку (см.
	/// <see cref="SurfaceCache"/>): для каждого вокселя, где есть геометрия, запоминает точку на
	/// поверхности, нормаль и альбедо - взвешенные площадью средние по попавшим треугольникам.
	/// Чистая геометрия, считается один раз на сессию.</summary>
	private SurfaceCache BuildSurfaceCache(Vector3 origin, Vector3 cell, int cx, int cy, int cz)
	{
		const int sub = SurfaceCache.Subdivision;
		var voxel = cell / sub;
		int vx = Math.Max(1, (cx - 1) * sub);
		int vy = Math.Max(1, (cy - 1) * sub);
		int vz = Math.Max(1, (cz - 1) * sub);

		var cache = new SurfaceCache(origin, voxel, vx, vy, vz);
		int total = vx * vy * vz;
		var dense = new int[total];
		var posSum = new Vector3[total];
		var normalSum = new Vector3[total];
		var albedoSum = new Vector3[total];
		var areaSum = new float[total];

		// Идём по треугольникам, а не по вокселям: сцена - это сотни тысяч треугольников против
		// миллионов вокселей, и почти все вокселя пустые. Треугольник раскладывается по вокселям
		// своего AABB (консервативно - AABB, а не точное пересечение).
		var lockObj = new object();
		Parallel.For(0, _tris.Length, () => (Voxels: new Dictionary<int, (Vector3 P, Vector3 N, Vector3 A, float W)>(), Dummy: 0),
			(t, _, local) =>
		{
			ref var tri = ref _tris[t];
			var b = tri.A + tri.E1;
			var c = tri.A + tri.E2;
			var cross = Vector3.Cross(tri.E1, tri.E2);
			float area = cross.Length() * 0.5f;
			if (area <= 1e-12f)
			{
				return local;
			}

			var normal = cross / (area * 2f);
			var centroid = (tri.A + b + c) / 3f;
			var triMin = Vector3.Min(tri.A, Vector3.Min(b, c));
			var triMax = Vector3.Max(tri.A, Vector3.Max(b, c));

			var lo = (triMin - origin) / voxel;
			var hi = (triMax - origin) / voxel;
			int x0 = Math.Clamp((int)MathF.Floor(lo.X), 0, vx - 1), x1 = Math.Clamp((int)MathF.Floor(hi.X), 0, vx - 1);
			int y0 = Math.Clamp((int)MathF.Floor(lo.Y), 0, vy - 1), y1 = Math.Clamp((int)MathF.Floor(hi.Y), 0, vy - 1);
			int z0 = Math.Clamp((int)MathF.Floor(lo.Z), 0, vz - 1), z1 = Math.Clamp((int)MathF.Floor(hi.Z), 0, vz - 1);

			// Крупный треугольник (пол, стена) накрывает много вокселей - в каждый кладём его точку,
			// зажатую в этот воксель, иначе центроид увёл бы позицию вокселя за его пределы.
			for (int z = z0; z <= z1; z++)
			for (int y = y0; y <= y1; y++)
			for (int x = x0; x <= x1; x++)
			{
				int v = (z * vy + y) * vx + x;
				var boxMin = origin + new Vector3(x * voxel.X, y * voxel.Y, z * voxel.Z);
				var point = Vector3.Clamp(centroid, boxMin, boxMin + voxel);
				var add = (point * area, normal * area, tri.Albedo * area, area);
				local.Voxels[v] = local.Voxels.TryGetValue(v, out var prev)
					? (prev.P + add.Item1, prev.N + add.Item2, prev.A + add.Item3, prev.W + area)
					: add;
			}

			return local;
		},
		local =>
		{
			lock (lockObj)
			{
				foreach (var (v, acc) in local.Voxels)
				{
					posSum[v] += acc.P;
					normalSum[v] += acc.N;
					albedoSum[v] += acc.A;
					areaSum[v] += acc.W;
				}
			}
		});

		int count = 0;
		for (int v = 0; v < total; v++)
		{
			dense[v] = areaSum[v] > 1e-12f ? count++ : -1;
		}

		cache.Allocate(dense, count);
		for (int v = 0; v < total; v++)
		{
			int slot = dense[v];
			if (slot < 0)
			{
				continue;
			}

			float inv = 1f / areaSum[v];
			cache.Position[slot] = posSum[v] * inv;
			var n = normalSum[v] * inv;
			float len = n.Length();
			// Нормали сошлись в ноль (воксель на ребре, где грани смотрят навстречу) - берём любую
			// осмысленную: такой воксель всё равно почти не виден.
			cache.Normal[slot] = len > 1e-4f ? n / len : Vector3.UnitY;
			cache.Albedo[slot] = albedoSum[v] * inv;
		}

		return cache;
	}

	/// <summary>Строит захват поверхностей, если он заказан и ещё не построен. Обычно это делает
	/// первый раунд (захват стоит сотни миллисекунд и не должен вставать на главном потоке), но
	/// GPU-пути кэш нужен уже при создании буферов - там он вызывается напрямую.</summary>
	public void EnsureSurfaceCache(ProbeGiBakeSession s)
	{
		// Реальному времени кэш не нужен ВООБЩЕ (см. RunRound): его статичная геометрия врёт на
		// движущейся сцене, отскок идёт из поля. Один гейт здесь закрывает все места вызова и
		// заодно экономит сотни миллисекунд захвата на главном потоке при создании сессии.
		// WantsSurfaceCache НЕ сбрасывается: живое переключение в запечку достроит кэш первым же
		// её раундом.
		if (!s.WantsSurfaceCache || s.Realtime)
		{
			return;
		}

		s.WantsSurfaceCache = false;
		s.Surface = BuildSurfaceCache(s.Origin, s.Cell, s.CountX, s.CountY, s.CountZ);
	}

	/// <summary>Пересчитывает исходящий радианс кэша поверхностей. Резкая часть - солнце с теневым
	/// лучом на каждый воксель (это и даёт отскоку детализацию, недоступную сетке проб); гладкая -
	/// небо и переотскок, взятые из поля проб, которому такой детализации и не нужно. Один луч на
	/// воксель за раунд, поэтому кэш идёт в ногу с пробами, а не удваивает стоимость бейка.</summary>
	private void UpdateSurfaceCache(ProbeGiBakeSession s)
	{
		var cache = s.Surface;
		if (cache == null || cache.VoxelCount == 0)
		{
			return;
		}

		var sunDir = s.SunDirection;
		var sunColor = s.SunColor;
		float feedback = s.Feedback;
		float bounceSaturation = s.BounceSaturation;
		var l0R = s.L0R; var l1xR = s.L1XR; var l1yR = s.L1YR; var l1zR = s.L1ZR;
		var validityR = s.ValidityR; var sunFracR = s.SunFracR;
		float offset = _sceneEpsilon * 4f;

		static float Lum(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

		Parallel.For(0, cache.VoxelCount, v =>
		{
			var normal = cache.Normal[v];
			var pos = cache.Position[v] + normal * offset;

			var sunIrradiance = Vector3.Zero;
			float ndotl = Vector3.Dot(normal, sunDir);
			if (ndotl > 0f && !TraceAnyHit(pos, sunDir, _rayTMax))
			{
				sunIrradiance = sunColor * ndotl;
			}

			// Небо и переотскок - из поля проб. Оно грубое, но именно эта часть освещения меняется
			// в пространстве плавно, так что разрешения сетки проб ей хватает.
			var ambient = Vector3.Zero;
			float ambientFrac = 0f;
			if (feedback > 0f)
			{
				ambient = EvalIrradiance(s, l0R, l1xR, l1yR, l1zR, validityR, sunFracR,
					pos, normal, out ambientFrac) * feedback;
			}

			var irradiance = sunIrradiance + ambient;
			var albedo = Vector3.Lerp(new Vector3(Lum(cache.Albedo[v])), cache.Albedo[v], bounceSaturation);
			cache.Radiance[v] = albedo * irradiance * (1f / MathF.PI);

			float lumIrr = Lum(irradiance);
			cache.SunFraction[v] = lumIrr > 1e-6f
				? Math.Clamp((Lum(sunIrradiance) + Lum(ambient) * ambientFrac) / lumIrr, 0f, 1f)
				: 0f;
		});
	}

	// --- Бейк ---------------------------------------------------------------------------------

	/// <summary>Вес раунда снизу (см. <see cref="RunRound"/>): бегущее среднее 1/(Round+1) на очень
	/// длинных сессиях загнало бы вес в ноль и заморозило поле намертво.</summary>
	internal const float MinRoundBlend = 0.02f;

	/// <summary>Пол веса раунда в режиме реального времени (см.
	/// <see cref="ProbeGiBakeOptions.Realtime"/>) - он же альфа экспоненциального среднего, к которой
	/// сходится вес после первых раундов.
	///
	/// Размен прямой: возмущение затухает как (1-alpha)^n, а остаточное дрожание ОТДЕЛЬНОЙ пробы
	/// идёт как sqrt(alpha/(2-alpha)) от дисперсии одного раунда. Редактор выпускает не больше
	/// одного раунда за кадр, так что при 60 к/с 0.04 - это установление за ~1.2 секунды.
	///
	/// Замерено на Sponza при 64 лучах (см. SceneTraceVerifier.MeasureFlicker; p99 и максимум - по
	/// относительной смене пробы за раунд, доля - сколько проб дёрнулось больше чем на 10%):
	///
	///   alpha 0.15 - p99 6.3%, max 79%, доля 0.6%   (мигание видно отчётливо)
	///   alpha 0.08 - p99 3.4%, max 48%, доля 0.0%
	///   alpha 0.04 - p99 1.8%, max 24%, доля 0.0%   (выбрано)
	///   alpha 0.02 - p99 1.0%, max 12%, доля 0.0%   (отклик уже за 2.5 с)
	///
	/// Против ХВОСТА распределения альфа работает много лучше числа лучей: учетверение лучей при
	/// 0.15 убирает дыхание сцены, но отдельные пробы продолжают дёргаться, потому что их разброс
	/// делает не шум оценки, а смена того, во что попадает веер.</summary>
	internal const float RealtimeBlend = 0.04f;

	/// <summary>Длина окна релокации в раундах - ПЯТЬ, как в Majercik 2021 (§5: «cap the number of
	/// iterations at five to prevent probes from moving back and forth (infinitely) through tangent
	/// backfaces»). Длинное окно (пробовали 32) не улучшает позиции, а даёт касательным задним
	/// граням качать пробу туда-обратно.</summary>
	internal const int RelocationRounds = 5;

	/// <summary>Разгонные раунды, НЕ попадающие в усреднение. Отскок собирается из текущего поля, и
	/// у самого первого раунда это поле пустое - его радианс занижен ровно на весь мультибаунс.
	/// Копить такие раунды в общее среднее нельзя: холодный старт остался бы в результате навсегда
	/// (замерено на Sponza - поле выходило процентов на восемь темнее прежнего бейка). Поэтому
	/// первые раунды идут с полным весом, только раскачивая отскок (сходится геометрически, трёх
	/// хватает), а усреднение стартует уже по прогретому полю.</summary>
	private const int BootstrapRounds = 3;

	/// <summary>К какому «возрасту» откатывается сходимость при смене освещения (см.
	/// <see cref="ProbeGiBakeSession.SetLighting"/>). Разгон заново не нужен - старое поле остаётся
	/// приличным стартовым приближением для отскока, - но вес первого раунда после смены должен
	/// быть большим, чтобы новое решение проступило за единицы раундов.</summary>
	internal const int RestartRound = BootstrapRounds + 1;

	/// <summary>Минимум УСРЕДНЯЕМЫХ раундов независимо от
	/// <see cref="ProbeGiBakeOptions.RaysPerProbe"/> - иначе на низком качестве поле остаётся
	/// откровенно шумным.</summary>
	private const int MinAveragedRounds = 4;

	/// <summary>Синхронный бейк до сходимости - обёртка над сессией для headless-путей вроде
	/// PreviewProbe, которым нечего ждать между кадрами. Редактор вместо этого крутит
	/// <see cref="RunRound"/> из PollProbeBake и показывает поле, не дожидаясь сходимости.</summary>
	public ProbeGiBakeResult Bake(Vector3 boundsMin, Vector3 boundsMax, Vector3 sunDirection,
		Vector3 sunColor, float envYawRadians, Func<Vector3, Vector3> skyRadiance,
		ProbeGiBakeOptions? options = null)
	{
		var session = BeginBake(boundsMin, boundsMax, sunDirection, sunColor, envYawRadians,
			skyRadiance, options);

		// Условие явное, а не Converged: синхронный бейк обязан завершиться, даже если в настройках
		// стоит режим реального времени (там сходимости нет по определению).
		while (!session.NoGeometry && session.Round < session.TargetRounds)
		{
			RunRound(session);
		}

		return Snapshot(session);
	}

	/// <summary>Раскладывает сетку проб по баундам сцены и заводит аккумуляторы прогрессивного
	/// бейка - лучей не пускает, так что звать можно и с главного потока. Дальше крутите
	/// <see cref="RunRound"/> в фоне, пока <see cref="ProbeGiBakeSession.Converged"/> не станет
	/// true, а <see cref="Snapshot"/> - между раундами: поле пригодно к показу уже после первого.
	/// skyRadiance - линейный радианс неба по мировому направлению ДО пользовательского поворота
	/// (envYaw применяется внутри, той же конвенцией, что SampleEnvironment в шейдере).
	/// sunDirection - НА солнце.</summary>
	/// <param name="scrollable">Объём будет ездить за камерой (см.
	/// <see cref="ProbeGiBakeSession.Scroll"/>): пул заводится с запасом слотов, а осмотр геометрии
	/// сохраняется в кэше, чтобы сдвиг стоил только въехавшей области.</param>
	public ProbeGiBakeSession BeginBake(Vector3 boundsMin, Vector3 boundsMax, Vector3 sunDirection,
		Vector3 sunColor, float envYawRadians, Func<Vector3, Vector3> skyRadiance,
		ProbeGiBakeOptions? options = null, bool scrollable = false)
	{
		options ??= new ProbeGiBakeOptions();
		float density = Math.Clamp(options.GridDensity, 4f, 64f);
		int maxProbes = Math.Clamp(options.MaxProbes, MinProbeBudget, MaxProbeBudget);

		// Сетка: ячейка ~1/density крупнейшего измерения, не больше maxProbes, минимум 2 по оси.
		var size = Vector3.Max(boundsMax - boundsMin, new Vector3(1e-3f));
		float maxDim = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
		var margin = new Vector3(maxDim * 0.02f);
		var min = boundsMin - margin;
		var full = size + margin * 2f;

		// Сетка кирпичей: укрупняем ячейку, пока ЖИВЫЕ кирпичи не влезут в бюджет. Считать бюджет
		// по живым, а не по всему баунду - и есть весь смысл разреженности: на сцене-уровне пустое
		// пространство занимает большую часть коробки, и раньше оно съедало бюджет наравне с
		// геометрией, заставляя грубеть сетку у поверхностей.
		float cellTarget = MathF.Max(maxDim, 1e-3f) / density;
		int cx, cy, cz, nbx, nby, nbz, poolSlots;
		BrickLayout layout;
		BrickScratch? scratch = null;
		while (true)
		{
			nbx = BricksPerAxis(full.X, cellTarget);
			nby = BricksPerAxis(full.Y, cellTarget);
			nbz = BricksPerAxis(full.Z, cellTarget);

			cx = nbx * BrickCells + 1;
			cy = nby * BrickCells + 1;
			cz = nbz * BrickCells + 1;
			var probeCell = new Vector3(full.X / (cx - 1), full.Y / (cy - 1), full.Z / (cz - 1));

			// Кэш осмотра заводится под КАЖДЫЙ пробный размер сетки и переживает только принятый:
			// он привязан к раскладке кирпичей, а укрупнение ячейки её меняет целиком.
			if (scrollable)
			{
				scratch = new BrickScratch();
				scratch.Resize(nbx, nby, nbz);
			}

			layout = ClassifyBricks(min, probeCell, nbx, nby, nbz, scratch);

			// Бюджет и атласы меряются по ЁМКОСТИ пула, а не по насчитанным кирпичам: запас
			// прокрутки - это настоящие пробы, они занимают и атлас, и время раунда.
			//
			// У ПРОКРУЧИВАЕМОГО объёма ёмкость меряется по ХУДШЕМУ размещению коробки, а не по тому,
			// где объём оказался в момент создания. Разница принципиальна и стоила блочных дыр в
			// освещении: каскад создаётся там, где стоит камера, и если она в этот момент смотрела
			// на сцену снаружи или из пустого места, коробка накрывала почти пустоту. Дальше камера
			// влетает внутрь здания, прокрутка приносит плотную геометрию - а слотов под неё нет, и
			// AssignSlots выбрасывает лишние кирпичи. Замерено в редакторе: пул 234 слота против
			// 4471 требуемых, то есть в двадцать раз, и 95% каскада просто отсутствовало (в логе -
			// «ran out of pool slots ... x19,1»). Запасом ScrollHeadroom такое не лечится: он взят
			// под смену плотности на треть, а не в двадцать раз.
			//
			// Худшее ищется перебором размещений по решётке внутри области прокрутки. Своя разметка
			// (BrickScratch) на каждую пробу обязательна: кэш осмотра привязан к КОНКРЕТНОМУ углу
			// коробки, и переиспользование вернуло бы чужие ответы. Стоит это по осмотру на позицию
			// (единицы миллисекунд) и происходит один раз на создание каскада - против рывка от
			// пересоздания объёма посреди полёта камеры это ничто.
			int worstTotal = layout.Total;
			if (scrollable && options.ScrollOriginRange is { } originRange)
			{
				// Область задана в координатах УГЛА КОРОБКИ без поля (его добавляет min выше), так
				// что тот же отступ надо снять и здесь - иначе развёртка поедет на его величину.
				worstTotal = Math.Max(worstTotal, WorstBrickTotal(
					(originRange.Min - margin, originRange.Max - margin),
					probeCell, nbx, nby, nbz));
			}

			poolSlots = scrollable
				? Math.Max(layout.Total, (int)MathF.Ceiling(worstTotal * ScrollHeadroom))
				: layout.Total;
			long probes = (long)poolSlots * BrickProbes * BrickProbes * BrickProbes;
			int poolColumns = ChoosePoolColumns(poolSlots);
			int poolRows = poolColumns > 0 ? (poolSlots + poolColumns - 1) / poolColumns : 0;

			// Кроме бюджета проб пул обязан влезть в атласы (см. MaxAtlasDimension): окто-карта
			// видимости крупнее SH-атласа в VisRes раз по обеим осям и упирается в предел первой.
			bool fitsBudget = probes <= maxProbes;
			bool fitsAtlas =
				(long)poolRows * BrickProbes * BrickProbes * ProbeGiBakeResult.VisRes <= MaxAtlasDimension
				&& (long)nby * nbz <= MaxAtlasDimension;
			if ((fitsBudget && fitsAtlas) || (nbx == 1 && nby == 1 && nbz == 1))
			{
				break;
			}

			cellTarget *= 1.25f;
		}

		var cell = new Vector3(full.X / (cx - 1), full.Y / (cy - 1), full.Z / (cz - 1));

		// Усредняемых раундов ровно столько, чтобы набрать заказанные RaysPerProbe лучей; сверху -
		// разгон. Итоговое качество сошедшегося поля выходит тем же, что у прежнего бейка одним
		// куском, только приходит оно постепенно.
		int averagedRounds = Math.Max(MinAveragedRounds,
			(int)MathF.Ceiling(Math.Clamp(options.RaysPerProbe, 16, 512)
				/ (float)Math.Clamp(options.RaysPerRound, 4, 128)));

		layout = layout.Pad(poolSlots);
		var session = new ProbeGiBakeSession(min, cell, cx, cy, cz, nbx, nby, nbz, layout,
			ChoosePoolColumns(layout.Total), options, Vector3.Normalize(sunDirection), sunColor,
			envYawRadians, skyRadiance, BootstrapRounds + averagedRounds)
		{
			Scratch = scratch,
		};

		// Захват поверхностей (сотни миллисекунд на сцене-уровне) откладывается до первого раунда:
		// BeginBake зовётся с ГЛАВНОГО потока, и здесь он встал бы видимым фризом редактора.
		session.WantsSurfaceCache = options.SurfaceCache;
		return session;
	}

	/// <summary>Число кирпичей по оси под заданный шаг ячейки. Кратности размеру крупных групп тут
	/// НЕ навязывается: округление до неё квантовало бы поиск плотности шагом в 2-4 раза по каждой
	/// оси, а неполная группа у границы баунда просто не сливается и остаётся мелкой.</summary>
	private static int BricksPerAxis(float extent, float cellTarget) =>
		Math.Clamp((int)MathF.Ceiling(extent / (cellTarget * BrickCells)), 1, MaxBricksPerAxis);

	/// <summary>Раскладка кирпичей по уровням подразделения. Index - на КАЖДУЮ ячейку самой мелкой
	/// сетки кирпичей: индекс накрывающего её кирпича в пуле или -1 (пусто); крупный кирпич
	/// прописан во все свои ячейки, поэтому шейдеру хватает одного чтения. Anchor/Level - по три
	/// int и по байту на кирпич пула: угол (в координатах мелкой сетки кирпичей) и уровень.</summary>
	/// <param name="Alive">Занят ли слот пула - у прокручиваемого объёма пул с запасом (см.
	/// <see cref="ScrollHeadroom"/>), и часть слотов пустует.</param>
	/// <param name="Fresh">Слот ЗАСЕЛЁН ЗАНОВО этой раскладкой (кирпич в нём другой, чем был):
	/// накопленное поле прежнего жильца к нему отношения не имеет.</param>
	/// <param name="Live">Сколько кирпичей раскладка насчитала - против <paramref name="Total"/>
	/// (ёмкость пула) это диагностика того, влезла ли она.</param>
	internal readonly record struct BrickLayout(int[] Index, byte[] LevelAt, int[] Anchor,
		byte[] Level, int Total, bool[] Alive, byte[] Fresh, int Live)
	{
		/// <summary>Расширяет раскладку до ёмкости пула: слоты сверх насчитанных кирпичей остаются
		/// пустыми и достаются въезжающим кирпичам при первой же прокрутке.</summary>
		internal BrickLayout Pad(int slots)
		{
			if (slots <= Total)
			{
				return this;
			}

			var anchor = new int[slots * 3];
			var level = new byte[slots];
			var alive = new bool[slots];
			var fresh = new byte[slots];
			Array.Copy(Anchor, anchor, Anchor.Length);
			Array.Copy(Level, level, Level.Length);
			Array.Copy(Alive, alive, Alive.Length);
			Array.Copy(Fresh, fresh, Fresh.Length);
			return this with { Anchor = anchor, Level = level, Alive = alive, Fresh = fresh, Total = slots };
		}
	}

	/// <summary>Кэш осмотра геометрии под прокрутку объёма: занятость кирпичей по сетке ТЕКУЩЕГО
	/// положения объёма.
	///
	/// Смысл в одном: осмотр коробки - это обход BVH, и на инициализации их тысячи. Прокрутка
	/// сдвигает объём на кирпич, то есть подавляющее большинство его кирпичей остаётся на прежних
	/// МЕСТАХ МИРА, и повторять их осмотр незачем - въехавших в кадре десятки, а не тысячи. Кэш и
	/// делает прокрутку дешёвой настолько, чтобы гонять её прямо в движении камеры.</summary>
	internal sealed class BrickScratch
	{
		private int _nbx, _nby, _nbz;

		internal bool[] Has = Array.Empty<bool>();
		internal bool[] HasKnown = Array.Empty<bool>();

		internal void Resize(int nbx, int nby, int nbz)
		{
			int n = nbx * nby * nbz;
			_nbx = nbx;
			_nby = nby;
			_nbz = nbz;
			Has = new bool[n];
			HasKnown = new bool[n];
		}

		/// <summary>Сдвигает кэш на целое число кирпичей: то, что осталось в объёме, переезжает
		/// вместе с ним и остаётся известным, въехавшее объявляется неизвестным - его и осмотрит
		/// раскладка. Осмотр привязан к МЕСТУ В МИРЕ, а не к индексу, поэтому сдвиг честен.</summary>
		internal void Shift(int sx, int sy, int sz)
		{
			int n = _nbx * _nby * _nbz;
			var has = new bool[n];
			var known = new bool[n];

			for (int z = 0; z < _nbz; z++)
			for (int y = 0; y < _nby; y++)
			for (int x = 0; x < _nbx; x++)
			{
				int sourceX = x + sx, sourceY = y + sy, sourceZ = z + sz;
				if (sourceX < 0 || sourceY < 0 || sourceZ < 0
					|| sourceX >= _nbx || sourceY >= _nby || sourceZ >= _nbz)
				{
					continue;
				}

				int to = (z * _nby + y) * _nbx + x;
				int from = (sourceZ * _nby + sourceY) * _nbx + sourceX;
				has[to] = Has[from];
				known[to] = HasKnown[from];
			}

			Has = has;
			HasKnown = known;
		}
	}

	/// <summary>Квант прокрутки объёма - ОДИН кирпич, мельче сетки проб уже нельзя: сдвиг на долю
	/// кирпича увёл бы решётку проб с её мировых позиций, и вся экономия прокрутки (кирпич остался
	/// на месте - поле уцелело) исчезла бы вместе с ней.
	///
	/// Соблазнительно было взять квант по размеру самой крупной группы слияния (четыре кирпича):
	/// якоря слитых кирпичей кратны их размеру, шейдер выборки восстанавливает угол кирпича
	/// округлением вниз по этому же шагу (см. ProbeGiSampleBody), и кратный сдвиг сохранял бы
	/// слияния при переезде. Замер это отверг: каскад Sponza - 13x13x13 проб, то есть ЧЕТЫРЕ кирпича
	/// по оси, и такой квант означал бы «объём умеет ездить только целиком» - прокрутка не
	/// срабатывала вовсе (в смоуке DECA_PROBE_SCROLL все шаги уходили в отказ).
	///
	/// Ценой стало то, что слитая группа, не попавшая на свой шаг после сдвига, не переиспользуется:
	/// разметка соберёт на её месте мелкие кирпичи, и они заселятся как свежие. Это корректно
	/// (шейдер видит только выровненные группы - разметка других не выпускает) и дёшево ровно там,
	/// где слияние и работает - на ровных полах и стенах, где поле меняется медленно.</summary>
	internal const int ScrollQuantumBricks = 1;

	/// <summary>Запас слотов пула у прокручиваемого объёма, долей от насчитанных кирпичей.
	///
	/// Прокрутка переселяет кирпичи из уехавшей области во въехавшую, и если новая область плотнее
	/// старой, свободных слотов не хватит: лишние кирпичи останутся без поля, и в них будет дырка до
	/// следующей прокрутки. Запас - страховка ровно от этого. Он не бесплатен (пул задаёт и размер
	/// атласов, и число проб в раунде), поэтому взят скромным: смена плотности геометрии на треть за
	/// один шаг прокрутки - это уже въезд в принципиально другую часть сцены.</summary>
	internal const float ScrollHeadroom = 1.35f;

	/// <summary>Насколько сонаправлены нормали геометрии в кирпиче: |Σn|/N. Единица - всё лежит в
	/// одной плоскости и смотрит в одну сторону.</summary>
	private const float FlatNormalCoherence = 0.9f;

	/// <summary>Размечает кирпичи по уровням. Кирпич нужен там, где рядом геометрия (коробка
	/// расширяется на ячейку: шейдер сэмплит со смещением по нормали и берёт углы соседней ячейки).
	/// Дальше группы кирпичей, вся геометрия которых ПЛОСКАЯ, сливаются в один крупный: над ровной
	/// стеной или полом поле меняется почти линейно, и мельчить там нечего - сэкономленный бюджет
	/// уходит на углы, ниши и основания колонн, где поле как раз ломается. Идём от крупного уровня
	/// к мелкому, чтобы слияние побеждало там, где возможно.
	///
	/// Слоты пула раздаются ОТДЕЛЬНЫМ шагом, после разметки, и это нужно прокрутке объёма (см.
	/// <see cref="ProbeGiBakeSession.Scroll"/>): кирпич, который после сдвига остался на прежнем
	/// месте мира, обязан удержать свой слот вместе с накопленным в нём полем. Раздача по порядку
	/// разметки, как было раньше, перетасовала бы слоты при каждом сдвиге, и прокрутка ничем не
	/// отличалась бы от пересоздания.</summary>
	/// <param name="scratch">Кэш осмотра геометрии (см. <see cref="BrickScratch"/>) - при прокрутке
	/// избавляет от обхода BVH по всей области, оставляя только въехавшую.</param>
	/// <param name="reuseSlot">Слот, стоявший в этой ячейке до сдвига, или -1 - карта в координатах
	/// НОВОЙ сетки.</param>
	/// <param name="capacity">Ёмкость пула; 0 - раздать ровно столько слотов, сколько кирпичей.</param>
	/// <summary>Наибольшее число живых кирпичей, какое коробка объёма может набрать, гуляя по
	/// области прокрутки. Ёмкость пула прокручиваемого объёма считается по нему, а не по месту, где
	/// объём оказался при создании (см. вызов в <see cref="BeginBake"/> - там же, почему).
	///
	/// Решётка 3x3x3 по области прокрутки: углы ловят «камера забралась в тесный угол сцены», центр
	/// - «камера посреди самого плотного зала». Дробить мельче смысла нет - коробка сама размером с
	/// заметную долю сцены, и соседние узлы решётки накрывают почти одно и то же.</summary>
	private int WorstBrickTotal((Vector3 Min, Vector3 Max) originRange, Vector3 cell,
		int nbx, int nby, int nbz)
	{
		var span = originRange.Max - originRange.Min;
		int worst = 0;
		for (int i = 0; i < 27; i++)
		{
			var t = new Vector3(i % 3, i / 3 % 3, i / 9) * 0.5f;
			var probe = new BrickScratch();
			probe.Resize(nbx, nby, nbz);
			worst = Math.Max(worst,
				ClassifyBricks(originRange.Min + span * t, cell, nbx, nby, nbz, probe).Total);
		}

		return worst;
	}

	internal BrickLayout ClassifyBricks(Vector3 origin, Vector3 cell, int nbx, int nby, int nbz,
		BrickScratch? scratch = null, int[]? reuseSlot = null, byte[]? reuseLevel = null,
		int capacity = 0)
	{
		int n = nbx * nby * nbz;
		var has = scratch?.Has ?? new bool[n];
		var hasKnown = scratch?.HasKnown;
		var brickSize = cell * BrickCells;

		Parallel.For(0, n, i =>
		{
			if (hasKnown != null && hasKnown[i])
			{
				return;
			}

			int bx = i % nbx;
			int by = i / nbx % nby;
			int bz = i / (nbx * nby);
			var boxMin = origin + new Vector3(bx * brickSize.X, by * brickSize.Y, bz * brickSize.Z) - cell;
			has[i] = InspectBox(boxMin, boxMin + brickSize + cell * 2f).HasGeometry;
		});

		if (hasKnown != null)
		{
			Array.Fill(hasKnown, true);
		}

		var index = new int[n];
		Array.Fill(index, -1);
		var levelAt = new byte[n];
		var taken = new bool[n];
		var anchors = new List<int>();
		var levels = new List<byte>();

		// ПРОКРУЧИВАЕМЫЙ объём кирпичи не сливает, и это результат замера, а не упрощение.
		//
		// Слияние держится на выравнивании: якорь группы кратен её размеру в координатах сетки, и
		// шейдер выборки восстанавливает угол кирпича округлением вниз по этому же шагу. Сдвиг на
		// один кирпич (мельче нельзя, см. ScrollQuantumBricks) сбивает фазу выравнивания, поэтому
		// кэш «плоскости» после него недействителен - а его пересчёт это обход BVH по коробкам
		// размером с группу, и мерилось это в 30-50 мс на сдвиг: ровно тот рывок, ради устранения
		// которого прокрутка и писалась.
		//
		// Платы при этом почти нет: слияние срабатывает на КРУПНЫХ ровных поверхностях, а каскад -
		// это маленькая коробка вокруг камеры, плотно занятая геометрией. На Sponza разметка не
		// сливает ни одного кирпича даже у базового объёма (levels [96/0/0] в выводе смоука), и
		// число проб каскада от этого не меняется.
		int mergeFrom = scratch == null ? MaxBrickLevel - 1 : 0;
		for (int level = mergeFrom; level >= 1; level--)
		{
			int step = 1 << level;
			for (int az = 0; az + step <= nbz; az += step)
			for (int ay = 0; ay + step <= nby; ay += step)
			for (int ax = 0; ax + step <= nbx; ax += step)
			{
				bool anyGeometry = false, free = true;
				for (int dz = 0; dz < step && free; dz++)
				for (int dy = 0; dy < step && free; dy++)
				for (int dx = 0; dx < step && free; dx++)
				{
					int s = ((az + dz) * nby + (ay + dy)) * nbx + (ax + dx);
					free = !taken[s];
					anyGeometry |= has[s];
				}

				if (!anyGeometry || !free)
				{
					continue;
				}

				// Плоскость меряется на коробке ВСЕЙ группы, а не по под-кирпичам: требовать
				// плоскости от каждого - значит не слить ничего никогда (под-кирпич на стыке пола и
				// стены не плоский, а группа целиком может быть ровным полом).
				var groupMin = origin + new Vector3(
					ax * brickSize.X, ay * brickSize.Y, az * brickSize.Z) - cell;
				var groupMax = groupMin + brickSize * step + cell * 2f;
				if (InspectBox(groupMin, groupMax).Coherence < FlatNormalCoherence)
				{
					continue;
				}

				int brick = levels.Count;
				anchors.Add(ax); anchors.Add(ay); anchors.Add(az);
				levels.Add((byte)level);
				for (int dz = 0; dz < step; dz++)
				for (int dy = 0; dy < step; dy++)
				for (int dx = 0; dx < step; dx++)
				{
					int s = ((az + dz) * nby + (ay + dy)) * nbx + (ax + dx);
					index[s] = brick;
					levelAt[s] = (byte)level;
					taken[s] = true;
				}
			}
		}

		for (int i = 0; i < n; i++)
		{
			if (taken[i] || !has[i])
			{
				continue;
			}

			index[i] = levels.Count;
			anchors.Add(i % nbx); anchors.Add(i / nbx % nby); anchors.Add(i / (nbx * nby));
			levels.Add(0);
			levelAt[i] = 0;
		}

		if (levels.Count == 0)
		{
			// Ни одного кирпича быть не должно (бейк зовётся только при HasGeometry), но пустой пул
			// дал бы атлас нулевой высоты и падение на создании текстуры - держим один кирпич.
			index[0] = 0;
			anchors.Add(0); anchors.Add(0); anchors.Add(0);
			levels.Add(0);
		}

		return AssignSlots(index, levelAt, anchors, levels, nbx, nby, nbz,
			reuseSlot, reuseLevel, capacity);
	}

	/// <summary>Раздаёт кирпичам разметки слоты пула. Кирпич, стоявший на этом же месте с этим же
	/// уровнем до сдвига, получает СВОЙ прежний слот - в нём накопленное поле, ради сохранения
	/// которого прокрутка и делается; остальные разбирают освободившиеся слоты и объявляются
	/// свежими (см. <see cref="ProbeGiBakeSession.BrickFresh"/>).
	///
	/// Без карты переиспользования (обычная разметка) раздача выходит тождественной - слоты идут
	/// по порядку разметки, ровно как раньше.</summary>
	private static BrickLayout AssignSlots(int[] index, byte[] levelAt, List<int> anchors,
		List<byte> levels, int nbx, int nby, int nbz, int[]? reuseSlot, byte[]? reuseLevel,
		int capacity)
	{
		int live = levels.Count;
		int slots = capacity > 0 ? capacity : live;
		var slotOf = new int[live];
		Array.Fill(slotOf, -1);
		var slotTaken = new bool[slots];

		if (reuseSlot != null && reuseLevel != null)
		{
			for (int brick = 0; brick < live; brick++)
			{
				int at = (anchors[brick * 3 + 2] * nby + anchors[brick * 3 + 1]) * nbx
					+ anchors[brick * 3 + 0];
				int slot = reuseSlot[at];
				if (slot >= 0 && slot < slots && !slotTaken[slot] && reuseLevel[at] == levels[brick])
				{
					slotOf[brick] = slot;
					slotTaken[slot] = true;
				}
			}
		}

		var alive = new bool[slots];
		var fresh = new byte[slots];
		var anchorOut = new int[slots * 3];
		var levelOut = new byte[slots];
		int next = 0;
		int dropped = 0;

		for (int brick = 0; brick < live; brick++)
		{
			if (slotOf[brick] < 0)
			{
				while (next < slots && slotTaken[next])
				{
					next++;
				}

				if (next >= slots)
				{
					// Пул исчерпан: въехавшая область плотнее уехавшей сильнее, чем взят запас (см.
					// ScrollHeadroom). Кирпич остаётся без слота - в индирекции дырка, и выборка
					// провалится на более крупный каскад. Хуже, чем полное поле, но лучше, чем
					// пересоздание объёма посреди движения камеры.
					dropped++;
					continue;
				}

				slotOf[brick] = next;
				slotTaken[next] = true;
				fresh[next] = 1;
			}

			int slot = slotOf[brick];
			alive[slot] = true;
			levelOut[slot] = levels[brick];
			anchorOut[slot * 3 + 0] = anchors[brick * 3 + 0];
			anchorOut[slot * 3 + 1] = anchors[brick * 3 + 1];
			anchorOut[slot * 3 + 2] = anchors[brick * 3 + 2];
		}

		for (int i = 0; i < index.Length; i++)
		{
			index[i] = index[i] >= 0 ? slotOf[index[i]] : -1;
		}

		if (dropped > 0)
		{
			// Числа в тексте - не украшение: по одному лишь «сколько потеряли» нельзя отличить
			// нехватку запаса (въехали в область на треть плотнее - ровно то, подо что взят
			// ScrollHeadroom) от того, что ёмкость посчитана по принципиально другому месту сцены.
			// Второе лечится не запасом, а тем, где и когда меряется пул, поэтому в лог идут и
			// требуемое число кирпичей, и ёмкость, и во сколько раз одно больше другого.
			EditorConsoleLog.Add(LogLevel.Warning,
				$"Probe GI: scrolled volume ran out of pool slots, {dropped} brick(s) dropped " +
				$"(need {live}, pool {slots}, x{(float)live / Math.Max(slots, 1):F1})");
		}

		return new BrickLayout(index, levelAt, anchorOut, levelOut, slots, alive, fresh, live);
	}

	/// <summary>Осматривает коробку: есть ли в ней геометрия и насколько сонаправлены нормали её
	/// треугольников (см. <see cref="FlatNormalCoherence"/>). Нормали взвешиваются площадью - иначе
	/// россыпь мелких треугольников декора перевесила бы плиту пола, на которой они лежат.</summary>
	private (bool HasGeometry, float Coherence) InspectBox(Vector3 boxMin, Vector3 boxMax)
	{
		if (_nodeCount == 0)
		{
			return (false, 0f);
		}

		var normalSum = Vector3.Zero;
		float areaSum = 0f;

		Span<int> stack = stackalloc int[64];
		int sp = 0;
		stack[sp++] = 0;

		while (sp > 0)
		{
			ref var node = ref _nodes[stack[--sp]];
			if (node.Min.X > boxMax.X || node.Max.X < boxMin.X ||
				node.Min.Y > boxMax.Y || node.Max.Y < boxMin.Y ||
				node.Min.Z > boxMax.Z || node.Max.Z < boxMin.Z)
			{
				continue;
			}

			if (node.Left < 0)
			{
				for (int i = node.Start; i < node.Start + node.Count; i++)
				{
					ref var tri = ref _tris[_order[i]];
					var b = tri.A + tri.E1;
					var c = tri.A + tri.E2;
					var triMin = Vector3.Min(tri.A, Vector3.Min(b, c));
					var triMax = Vector3.Max(tri.A, Vector3.Max(b, c));
					if (triMin.X > boxMax.X || triMax.X < boxMin.X ||
						triMin.Y > boxMax.Y || triMax.Y < boxMin.Y ||
						triMin.Z > boxMax.Z || triMax.Z < boxMin.Z)
					{
						continue;
					}

					// Векторное произведение рёбер - нормаль длиной в две площади: и направление, и
					// вес в одном значении.
					var cross = Vector3.Cross(tri.E1, tri.E2);
					normalSum += cross;
					areaSum += cross.Length();
				}
			}
			else if (sp + 2 <= stack.Length)
			{
				stack[sp++] = node.Left;
				stack[sp++] = node.Start;
			}
		}

		return areaSum > 1e-12f ? (true, normalSum.Length() / areaSum) : (false, 0f);
	}

	/// <summary>Один раунд прогрессивного бейка: пускает RaysPerRound лучей на пробу повёрнутым
	/// веером Фибоначчи, вливает радианс в поле бегущим средним и копит геометрические суммы
	/// (видимость неба, валидность, окто-карта глубин). Это тяжёлая часть - зовите в фоне; внутри
	/// раунд параллелится по пробам, но сами раунды обязаны идти строго по одному.</summary>
	public void RunRound(ProbeGiBakeSession s)
	{
		if (!HasGeometry)
		{
			// Печь нечего - помечаем сессию сошедшейся, чтобы вызывающий не крутил пустые раунды.
			// Номера раунда для этого мало: в реальном времени сходимости нет, и вызывающий гонял бы
			// пустые раунды вечно.
			s.NoGeometry = true;
			s.Round = s.TargetRounds;
			return;
		}

		// В реальном времени кэш поверхностей не захватывается, не обновляется и не читается -
		// зеркало GPU-раунда (см. ProbeRelocation.z в ProbeRoundCS.hlsl): его статичная геометрия
		// врёт на движущейся сцене, отскок идёт из поля проб в точке попадания.
		SurfaceCache? surface = null;
		if (!s.Realtime)
		{
			EnsureSurfaceCache(s);

			// Кэш обновляется ПЕРЕД лучами раунда: он собирает небо и переотскок из поля прошлого
			// раунда, а лучи этого раунда уже забирают из него свежий радианс. Так кэш и поле
			// сходятся вместе, не обгоняя друг друга.
			UpdateSurfaceCache(s);
			surface = s.Surface;
		}

		int rays = s.RaysPerRound;
		var dirs = BuildRotatedFibonacciSphere(rays, s.Sequence++);

		// Вес нового раунда. Разгонные раунды кладутся целиком (alpha = 1): они не усредняются, а
		// только раскачивают отскок, из которого будут собирать последующие. Дальше - честное
		// бегущее среднее по УСРЕДНЯЕМЫМ раундам; пол не даёт весу схлопнуться в ноль на длинных
		// сессиях, а в реальном времени он же и держит окно усреднения конечным.
		float alpha = RoundBlendWeight(s);

		int cx = s.CountX, cy = s.CountY, cz = s.CountZ;
		int probeCount = s.ProbeCount;
		var origin = s.Origin;
		var cell = s.Cell;
		var sunDir = s.SunDirection;
		var sunColor = s.SunColor;
		float bounceSaturation = s.BounceSaturation;
		float feedback = s.Feedback;
		float maxRayLuminance = s.MaxRayLuminance;
		float maxStep = s.MaxStep;
		float accumGamma = s.AccumulationGamma;
		float relocLimit = s.RelocationLimit;
		var probeOffsets = s.ProbeOffset;
		float visMax = cell.Length() * 1.5f;
		float gatherOffset = cell.Length() * 0.05f;

		// Поворот энвайронмента: шейдер сдвигает equirect-U на +yaw (см. SampleEnvironment), т.е.
		// мировое направление d видит небо в направлении с азимутом φ+yaw. SkyIntensity - ручка
		// яркости небесного эмбиента (окно Graphics).
		var skyRadiance = s.SkyRadiance;
		float skyIntensity = s.SkyIntensity;
		float cosYaw = MathF.Cos(s.EnvYaw), sinYaw = MathF.Sin(s.EnvYaw);
		Vector3 RotatedSky(Vector3 d) => skyRadiance(new Vector3(
			d.X * cosYaw - d.Z * sinYaw, d.Y, d.X * sinYaw + d.Z * cosYaw)) * skyIntensity;

		const float y00 = 0.28209479f;
		const float y1 = 0.48860251f;
		int res = ProbeGiBakeResult.VisRes;
		float domega = 4f * MathF.PI / rays;

		static float Lum(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

		// Раунд ЧИТАЕТ прошлое поле (мультибаунс собирается по соседним пробам в точках попаданий)
		// и ПИШЕТ новое - см. двойной буфер в ProbeGiBakeSession.
		var l0R = s.L0R; var l1xR = s.L1XR; var l1yR = s.L1YR; var l1zR = s.L1ZR;
		var validityR = s.ValidityR; var sunFracR = s.SunFracR;
		var l0W = s.L0W; var l1xW = s.L1XW; var l1yW = s.L1YW; var l1zW = s.L1ZW;
		var validityW = s.ValidityW; var sunFracW = s.SunFracW;

		const int perBrick = BrickProbes * BrickProbes * BrickProbes;
		var brickCellOrigin = s.BrickCellOrigin;
		var brickLevel = s.BrickLevel;

		Parallel.For(0, probeCount, p =>
		{
			// Слот пробы = кирпич * BrickProbes³ + локальный индекс; мировая позиция собирается
			// через угол кирпича в виртуальной сетке (плотных координат у пробы больше нет), а шаг
			// между пробами зависит от уровня подразделения кирпича.
			int brick = p / perBrick;
			int local = p - brick * perBrick;
			int step = 1 << brickLevel[brick];
			int px = brickCellOrigin[brick * 3 + 0] + local % BrickProbes * step;
			int py = brickCellOrigin[brick * 3 + 1] + local / BrickProbes % BrickProbes * step;
			int pz = brickCellOrigin[brick * 3 + 2] + local / (BrickProbes * BrickProbes) * step;
			// Трассируем из АКТУАЛЬНОЙ позиции - с учётом накопленной релокации (зеркало
			// ProbeRoundCS.hlsl): иначе статистика задних граней описывала бы узел сетки, а не то
			// место, где проба стоит, и релокация не сошлась бы.
			var probeOffset = probeOffsets[p];
			var probePos = origin + new Vector3(px * cell.X, py * cell.Y, pz * cell.Z) + probeOffset;

			// Кламп окто-глубин привязан к шагу СВОЕГО кирпича: у крупного он шире, иначе его
			// средняя видимость упиралась бы в потолок и тест Чебышёва глушил бы всё подряд.
			float probeVisMax = visMax * step;

			var sum0 = Vector3.Zero;
			var sumX = Vector3.Zero;
			var sumY = Vector3.Zero;
			var sumZ = Vector3.Zero;
			float sunLum = 0f, totalLum = 0f;
			int missCount = 0, backCount = 0;
			int visBase = p * res * res;

			// Для релокации - зеркало ProbeRoundCS.hlsl: ближайшая ЗАДНЯЯ грань есть ближайший
			// выход наружу, ближайшая передняя - мера свободного места вокруг.
			float closestBackT = _rayTMax, closestFrontT = _rayTMax;
			var closestBackDir = Vector3.UnitY;

			for (int r = 0; r < rays; r++)
			{
				var dir = dirs[r];
				Vector3 radiance;
				float sunShare = 0f;
				float hitT;

				if (!TraceClosest(probePos, dir, out float t, out int triIndex))
				{
					radiance = RotatedSky(dir);
					missCount++;
					hitT = _rayTMax;
				}
				else
				{
					hitT = t;
					ref var tri = ref _tris[triIndex];
					var normal = Vector3.Normalize(Vector3.Cross(tri.E1, tri.E2));
					if (Vector3.Dot(normal, dir) > 0f)
					{
						// Задняя грань = луч вышел изнутри геометрии (проба в стене).
						radiance = Vector3.Zero;
						backCount++;

						// Порядок важен: релокации нужна ПОЛНАЯ дистанция (ближайший выход из
						// стены), укорачивание - только для записи в глубину.
						if (t < closestBackT)
						{
							closestBackT = t;
							closestBackDir = dir;
						}

						// Глубина задней грани укорачивается на 80% - зеркало ProbeRoundCS.hlsl
						// (Majercik 2021, §4.1): тест Чебышёва должен считать её заслоняющей, иначе
						// проба заявляет, что видит в эту сторону далеко, и свет течёт сквозь стену.
						hitT = t * 0.2f;
					}
					else
					{
						closestFrontT = MathF.Min(closestFrontT, t);
						var hitPos = probePos + dir * t;

						// Кэш поверхностей (см. SurfaceCache): у точки попадания уже есть готовый
						// исходящий радианс, посчитанный на СВОЁМ разрешении - вчетверо мельче шага
						// проб. Это и есть смысл surface GI: отскок берётся с детализацией
						// геометрии, а не размазывается по ячейке сетки проб.
						int voxel = surface?.Lookup(hitPos + normal * gatherOffset) ?? -1;
						if (voxel >= 0)
						{
							radiance = surface!.Radiance[voxel];
							sunShare = surface.SunFraction[voxel];
						}
						else
						{
							// Кэша тут нет (воксель не захвачен) - считаем отскок по-старому, из
							// поля проб.
							var sunIrradiance = Vector3.Zero;
							float ndotl = Vector3.Dot(normal, sunDir);
							if (ndotl > 0f &&
								!TraceAnyHit(hitPos + normal * (_sceneEpsilon * 4f), sunDir, _rayTMax))
							{
								sunIrradiance = sunColor * ndotl;
							}

							var prevIrradiance = Vector3.Zero;
							float prevFrac = 0f;
							if (feedback > 0f)
							{
								prevIrradiance = EvalIrradiance(s, l0R, l1xR, l1yR, l1zR, validityR,
									sunFracR, hitPos + normal * gatherOffset, normal, out prevFrac) * feedback;
							}

							var irradiance = sunIrradiance + prevIrradiance;

							// Хрома-кламп альбедо: тянем цвет к собственной люме, ЯРКОСТЬ не меняем
							// (lerp к Lum линеен - Lum(результата) == Lum(альбедо)). Поэтому
							// солнечный баунс от камня/пола по силе прежний, а насыщенная ткань
							// перестаёт быть цветной лампочкой.
							var albedo = Vector3.Lerp(new Vector3(Lum(tri.Albedo)), tri.Albedo, bounceSaturation);
							radiance = albedo * irradiance * (1f / MathF.PI);

							// Солнечная доля яркости этого луча: прямой вклад солнца + солнечная
							// часть собранного поля (переотскок наследует долю источника).
							float lumIrr = Lum(irradiance);
							sunShare = lumIrr > 1e-6f
								? (Lum(sunIrradiance) + Lum(prevIrradiance) * prevFrac) / lumIrr
								: 0f;
						}
					}
				}

				// Подавление выбросов - зеркало GPU-раунда (см. ProbeRoundCS.hlsl): редкий луч в
				// очень яркое двигает пробу целиком, и числом лучей это не лечится. В запечке
				// потолок нулевой, кламп не срабатывает вовсе.
				if (maxRayLuminance > 0f)
				{
					float rayLum = Lum(radiance);
					if (rayLum > maxRayLuminance)
					{
						// Масштаб, а не обрезание по каналам: обрезание увело бы цвет.
						radiance *= maxRayLuminance / rayLum;
					}
				}

				// Окто-карта глубин (DDGI depth), точные суммы по всем раундам. Кламп по масштабу
				// ячейки, как в оригинальном DDGI: без него луч-промах вносит в среднюю глубину
				// октанта дистанцию в несколько габаритов сцены, средняя «видимость» становится
				// огромной, и тест Чебышёва не срабатывает НИКОГДА - протечки у стыков остаются.
				// Луч размазывается по КОНУСУ текселей - зеркало ProbeRoundCS.hlsl и §4.4 статьи
				// Majercik 2019. При укладке в один ближайший тексель большинству из 64 октантов не
				// достаётся ни одного сэмпла за раунд, и тест Чебышёва работает по карте, которой
				// почти нет.
				float tv = MathF.Min(hitT, probeVisMax);
				for (int dt = 0; dt < res * res; dt++)
				{
					var texelUv = new Vector2((dt % res + 0.5f) / res, (dt / res + 0.5f) / res);
					float w = MathF.Max(0f, Vector3.Dot(OctDecode(texelUv), dir));
					for (int sq = 0; sq < DepthSharpnessSquarings; sq++)
					{
						w *= w;
					}

					if (w < DepthWeightEpsilon)
					{
						continue;
					}

					int visAt = visBase + dt;
					s.VisSumT[visAt] += tv * w;
					s.VisSumT2[visAt] += tv * tv * w;
					s.VisWeight[visAt] += w;
				}

				float lum = Lum(radiance);
				sunLum += lum * sunShare;
				totalLum += lum;

				sum0 += radiance * (y00 * domega);
				sumX += radiance * (y1 * dir.X * domega);
				sumY += radiance * (y1 * dir.Y * domega);
				sumZ += radiance * (y1 * dir.Z * domega);
			}

			var new0 = Vector3.Lerp(l0R[p], sum0, alpha);
			var new1 = Vector3.Lerp(l1xR[p], sumX, alpha);
			var new2 = Vector3.Lerp(l1yR[p], sumY, alpha);
			var new3 = Vector3.Lerp(l1zR[p], sumZ, alpha);

			// Перцептивное накопление - зеркало GPU-раунда (см. ProbeRoundCS.hlsl и
			// ProbeGiBakeOptions.RealtimeGamma): яркость движется по гамма-кривой, направленность
			// не трогается. В запечке accumGamma = 1 и блок мёртв.
			if (accumGamma > 1f && alpha < 1f)
			{
				float lumOld = Lum(l0R[p]);
				float lumNew = Lum(sum0);
				float lumLinear = Lum(new0);

				// Только на потемнение - зеркало ProbeRoundCS.hlsl (симметричная кривая душила
				// подъём из темноты).
				if (lumNew < lumOld && lumLinear > 1e-6f)
				{
					float invGamma = 1f / accumGamma;
					float lumPerceptual = MathF.Pow(
						MathF.Pow(MathF.Max(lumOld, 0f), invGamma) * (1f - alpha)
							+ MathF.Pow(MathF.Max(lumNew, 0f), invGamma) * alpha,
						accumGamma);
					float k = lumPerceptual / lumLinear;
					new0 *= k;
					new1 *= k;
					new2 *= k;
					new3 *= k;
				}
			}

			// Ограничитель скорости - зеркало GPU-раунда (см. ProbeRoundCS.hlsl): режем производную,
			// а не величину, поэтому установившееся значение не смещается.
			if (maxStep > 0f && alpha < 1f)
			{
				var delta = new0 - l0R[p];
				float deltaLen = delta.Length();
				float scale = 0.5f * (l0R[p].Length() + new0.Length()) + 1e-4f;
				float limit = maxStep * scale;
				if (deltaLen > limit)
				{
					// Один множитель на все полосы SH: порознь они изменили бы направленность поля.
					float k = limit / deltaLen;
					new0 = l0R[p] + (new0 - l0R[p]) * k;
					new1 = l1xR[p] + (new1 - l1xR[p]) * k;
					new2 = l1yR[p] + (new2 - l1yR[p]) * k;
					new3 = l1zR[p] + (new3 - l1zR[p]) * k;
				}
			}

			l0W[p] = new0;
			l1xW[p] = new1;
			l1yW[p] = new2;
			l1zW[p] = new3;

			// Релокация - зеркало ProbeRoundCS.hlsl: проба, стоящая внутри стены или колонны,
			// отодвигается наружу через ближайшую заднюю грань.
			bool relocated = false;
			if (relocLimit > 0f)
			{
				float backFrac = backCount / (float)rays;
				var newOffset = probeOffset;
				float offsetLen = probeOffset.Length();

				if (backFrac > 0.25f && closestBackT < _rayTMax)
				{
					newOffset = probeOffset + closestBackDir * (closestBackT + gatherOffset);
				}
				// Возврата к узлу нет - зеркало ProbeRoundCS.hlsl: у тонкой геометрии он качал пробу
				// туда-обратно с сбросом накопителей на каждый переезд.

				float newLen = newOffset.Length();
				if (newLen > relocLimit)
				{
					newOffset *= relocLimit / newLen;
				}

				// Порог, а не любое движение: возврат к узлу идёт долями (0.75 за раунд), и сброс
				// на каждый мелкий шаг держал бы пробу в вечном холодном старте.
				relocated = (newOffset - probeOffset).Length() > relocLimit * 0.1f;
				probeOffsets[p] = newOffset;
			}

			float roundSunFrac = totalLum > 1e-6f ? Math.Clamp(sunLum / totalLum, 0f, 1f) : 0f;
			sunFracW[p] = sunFracR[p] + (roundSunFrac - sunFracR[p]) * alpha;

			// Видимость неба и валидность зависят только от геометрии, не от света - копим их
			// точными долями по ВСЕМ раундам сессии. Поэтому поворот солнца их не обесценивает:
			// именно тут прогрессивный бейк выигрывает у прежнего полного ребейка.
			int rayTotal = s.RayTotal[p] + rays;
			int missTotal = s.MissTotal[p] + missCount;
			int backTotal = s.BackTotal[p] + backCount;
			s.RayTotal[p] = rayTotal;
			s.MissTotal[p] = missTotal;
			s.BackTotal[p] = backTotal;
			s.SkyVis[p] = missTotal / (float)rayTotal;

			// Проба в стене видит в основном задние грани - гасим её вес в интерполяции.
			validityW[p] = Math.Clamp(1f - backTotal / (float)rayTotal * 3f, 0f, 1f);

			// Сброс геометрии переехавшей пробы - ПОСЛЕ того, как она отдала этот раунд (лучи-то
			// пущены ещё с прежнего места), и обязательно после накопления счётчиков выше, иначе
			// они тут же затёрли бы обнуление. Копить с нуля начинает следующий раунд.
			//
			// Без этого сброса переезд был бы половинчатым: радианс проба считала бы уже с нового
			// места, а валидность осталась бы заниженной старой статистикой задних граней - то есть
			// выбравшаяся из стены проба продолжала бы числиться замурованной, - и тест Чебышёва
			// мерил бы глубины от точки, где пробы больше нет.
			if (relocated)
			{
				s.RayTotal[p] = 0;
				s.MissTotal[p] = 0;
				s.BackTotal[p] = 0;
				int visReset = p * res * res;
				for (int i = 0; i < res * res; i++)
				{
					s.VisSumT[visReset + i] = 0f;
					s.VisSumT2[visReset + i] = 0f;
					s.VisWeight[visReset + i] = 0f;
				}
			}
		});

		s.Swap();
		s.Round++;
		s.ConsumeRelocationRound();
	}

	/// <summary>Пакует ТЕКУЩЕЕ состояние сессии в атласы. Буферы результата переиспользуются между
	/// снимками (пересоздавать десятки мегабайт каждый раунд незачем), поэтому звать строго между
	/// раундами и отдавать результат потребителю до следующего <see cref="RunRound"/>.</summary>
	public ProbeGiBakeResult Snapshot(ProbeGiBakeSession s)
	{
		int res = ProbeGiBakeResult.VisRes;
		var result = s.Result;
		int shWidth = result.ShWidth;
		int visWidth = shWidth * res;
		int poolColumns = result.PoolColumns;

		Parallel.For(0, s.ProbeCount, p =>
		{
			// Тексель пробы в пуле кирпичей (см. ProbeTexel) - плотной раскладки по сетке больше нет.
			var (px, py) = ProbeTexel(p, poolColumns);
			int texel = (py * shWidth + px) * 8;
			WriteHalf4(result.Sh0, texel, s.L0R[p], s.SkyVis[p]);
			WriteHalf4(result.Sh1, texel, s.L1XR[p], s.ValidityR[p]);
			WriteHalf4(result.Sh2, texel, s.L1YR[p], s.SunFracR[p]);
			WriteHalf4(result.Sh3, texel, s.L1ZR[p], 1f);
			WriteHalf4(result.Offset, texel, s.ProbeOffset[p], 1f);

			// Среднее по всей пробе - заполнитель октантов, куда за все раунды не попал ни один луч.
			int visBase = p * res * res;
			float totalT = 0f;
			float totalWeight = 0f;
			for (int i = 0; i < res * res; i++)
			{
				totalT += s.VisSumT[visBase + i];
				totalWeight += s.VisWeight[visBase + i];
			}

			float meanAll = totalWeight > 0f ? totalT / totalWeight : 0f;

			// Окто-блок видимости пробы: res×res текселей начиная с (px*res, py*res).
			for (int ty = 0; ty < res; ty++)
			{
				for (int tx = 0; tx < res; tx++)
				{
					int src = visBase + ty * res + tx;
					float weight = s.VisWeight[src];
					float mean = weight > 0f ? s.VisSumT[src] / weight : meanAll;
					float mean2 = weight > 0f ? s.VisSumT2[src] / weight : meanAll * meanAll;
					int dst = ((py * res + ty) * visWidth + px * res + tx) * 8;
					WriteHalf4(result.Vis, dst, new Vector3(mean, mean2, 0f), 0f);
				}
			}
		});

		return result;
	}

	/// <summary>Множитель обратной связи поля под заданную глубину мультибаунса. Прогрессивный бейк
	/// собирает отскок из ТЕКУЩЕГО поля, то есть переотскок по построению бесконечный: суммарная
	/// энергия идёт как 1/(1-r*f) при средней отражательной способности сцены r, тогда как прежний
	/// N-итерационный бейк давал (1-r^N)/(1-r). Приравняв, получаем f = (1-r^(N-1))/(1-r^N). Берём
	/// r=0.5 (дефолтное альбедо трассировщика) - точность оценки тут не важна, важно, что при
	/// переходе на прогрессивный бейк сцены не поедут по яркости.</summary>
	internal static float BounceFeedback(int bounces)
	{
		if (bounces <= 1)
		{
			return 0f;
		}

		const float r = 0.5f;
		float rn = MathF.Pow(r, bounces);
		return (1f - rn / r) / (1f - rn);
	}

	/// <summary>Резкость лобы, которой луч размазывается по окто-карте глубин: степень косинуса
	/// берётся шестью возведениями в квадрат (то есть 64) - дешевле pow, а порог веса отсекает всё
	/// дальше 26 градусов от луча. Обязано совпадать с ProbeRoundCS.hlsl.</summary>
	private const int DepthSharpnessSquarings = 6;
	private const float DepthWeightEpsilon = 0.001f;

	/// <summary>Обратное окто-преобразование: направление по точке карты (зеркало ProbeOctDecode в
	/// ProbeRoundCS.hlsl).</summary>
	private static Vector3 OctDecode(Vector2 uv)
	{
		var p = uv * 2f - Vector2.One;
		var d = new Vector3(p.X, p.Y, 1f - MathF.Abs(p.X) - MathF.Abs(p.Y));
		if (d.Z < 0f)
		{
			d = new Vector3(
				(1f - MathF.Abs(d.Y)) * (d.X >= 0f ? 1f : -1f),
				(1f - MathF.Abs(d.X)) * (d.Y >= 0f ? 1f : -1f),
				d.Z);
		}

		return Vector3.Normalize(d);
	}

	/// <summary>Окто-кодирование направления в [0,1]² - обязано бит-в-бит совпадать с OctEncode в
	/// UnlitInstancedPS.hlsl (иначе шейдер читает чужие тексели видимости).</summary>
	private static Vector2 OctEncode(Vector3 d)
	{
		float sum = MathF.Abs(d.X) + MathF.Abs(d.Y) + MathF.Abs(d.Z);
		float px = d.X / sum, py = d.Y / sum;
		if (d.Z < 0f)
		{
			(px, py) = ((1f - MathF.Abs(py)) * (px >= 0f ? 1f : -1f),
						(1f - MathF.Abs(px)) * (py >= 0f ? 1f : -1f));
		}

		return new Vector2(px * 0.5f + 0.5f, py * 0.5f + 0.5f);
	}

	/// <summary>CPU-аналог шейдерного SampleProbeGi: трилинейная интерполяция 8 проб с весами
	/// валидности, затем SH L1 → irradiance по нормали. sunFrac/fracOut - интерполяция доли
	/// солнечного света теми же весами (см. Bake).</summary>
	private static Vector3 EvalIrradiance(ProbeGiBakeSession s,
		Vector3[] l0, Vector3[] l1x, Vector3[] l1y, Vector3[] l1z, float[] validity, float[] sunFrac,
		Vector3 pos, Vector3 normal, out float fracOut)
	{
		fracOut = 0f;

		var origin = s.Origin;
		var cell = s.Cell;
		var f = (pos - origin) / cell;
		f = Vector3.Clamp(f, Vector3.Zero,
			new Vector3(s.CountX - 1, s.CountY - 1, s.CountZ - 1));

		// Кирпич, накрывающий точку. Все восемь углов ячейки лежат в ОДНОМ кирпиче (граничные пробы
		// соседей дублируются - см. BrickCells), поэтому индирекция читается один раз. Кирпича нет -
		// вокруг пустота, поля здесь не запечено.
		int fbx = Math.Min((int)(f.X / BrickCells), s.BrickCountX - 1);
		int fby = Math.Min((int)(f.Y / BrickCells), s.BrickCountY - 1);
		int fbz = Math.Min((int)(f.Z / BrickCells), s.BrickCountZ - 1);
		int cellIndex = (fbz * s.BrickCountY + fby) * s.BrickCountX + fbx;
		int brick = s.BrickIndex[cellIndex];
		if (brick < 0)
		{
			return Vector3.Zero;
		}

		// Шаг проб кирпича = 2^Level мелких ячеек; локальные координаты считаются в этом шаге.
		int step = 1 << s.BrickLevelAt[cellIndex];
		var brickOrigin = new Vector3(
			s.BrickCellOrigin[brick * 3 + 0], s.BrickCellOrigin[brick * 3 + 1],
			s.BrickCellOrigin[brick * 3 + 2]);
		var lf = (f - brickOrigin) / step;
		int lx = Math.Clamp((int)MathF.Floor(lf.X), 0, BrickCells - 1);
		int ly = Math.Clamp((int)MathF.Floor(lf.Y), 0, BrickCells - 1);
		int lz = Math.Clamp((int)MathF.Floor(lf.Z), 0, BrickCells - 1);
		var t = Vector3.Clamp(lf - new Vector3(lx, ly, lz), Vector3.Zero, Vector3.One);

		int slotBase = brick * BrickProbes * BrickProbes * BrickProbes;
		var probeStep = cell * step;

		var sh0 = Vector3.Zero;
		var shX = Vector3.Zero;
		var shY = Vector3.Zero;
		var shZ = Vector3.Zero;
		float fracSum = 0f;
		float weightSum = 0f;

		for (int corner = 0; corner < 8; corner++)
		{
			int ox = corner & 1, oy = (corner >> 1) & 1, oz = (corner >> 2) & 1;
			int index = slotBase
				+ ((lz + oz) * BrickProbes + (ly + oy)) * BrickProbes + (lx + ox);
			float w = (ox == 1 ? t.X : 1f - t.X) * (oy == 1 ? t.Y : 1f - t.Y) * (oz == 1 ? t.Z : 1f - t.Z)
				* validity[index];

			// Мягкий backface-вес - зеркало wrap shading-а в SampleProbeGi (см. UnlitInstancedPS):
			// без него мультибаунс за несколько итераций протаскивает свет сквозь стены (проба за
			// стеной подмешивается в сбор на точке попадания) - поле внутри помещений засорялось
			// солнечным баунсом наружных стен.
			var probePos = origin + (brickOrigin + new Vector3(lx + ox, ly + oy, lz + oz) * (float)step) * cell;
			var toProbe = probePos - pos;
			float toProbeLen = toProbe.Length();
			float wrap = (Vector3.Dot(toProbe / MathF.Max(toProbeLen, 1e-4f), normal) + 1f) * 0.5f;
			w *= wrap * wrap + 0.05f;

			sh0 += l0[index] * w;
			shX += l1x[index] * w;
			shY += l1y[index] * w;
			shZ += l1z[index] * w;
			fracSum += sunFrac[index] * w;
			weightSum += w;
		}

		if (weightSum < 1e-4f)
		{
			return Vector3.Zero;
		}

		float inv = 1f / weightSum;
		fracOut = Math.Clamp(fracSum * inv, 0f, 1f);
		var e = (sh0 * inv) * 0.8862269f
			+ ((shX * inv) * normal.X + (shY * inv) * normal.Y + (shZ * inv) * normal.Z) * 1.0233267f;
		return Vector3.Max(e, Vector3.Zero);
	}

	private static void WriteHalf4(byte[] bytes, int offset, Vector3 rgb, float a)
	{
		WriteHalf(bytes, offset + 0, rgb.X);
		WriteHalf(bytes, offset + 2, rgb.Y);
		WriteHalf(bytes, offset + 4, rgb.Z);
		WriteHalf(bytes, offset + 6, a);
	}

	private static void WriteHalf(byte[] bytes, int offset, float value)
	{
		ushort bits = BitConverter.HalfToUInt16Bits((Half)value);
		bytes[offset] = (byte)bits;
		bytes[offset + 1] = (byte)(bits >> 8);
	}

	/// <summary>Веер Фибоначчи, повёрнутый детерминированной по номеру раунда ориентацией. Без
	/// поворота каждый раунд стрелял бы ровно в те же направления, и накопление раундов не давало
	/// бы ничего сверх первого - вся прогрессивность держится на этом повороте.</summary>
	private static Vector3[] BuildRotatedFibonacciSphere(int count, int sequence)
	{
		var dirs = BuildFibonacciSphere(count);
		if (sequence == 0)
		{
			return dirs;
		}

		// Равномерная ориентация по Шумейку из низкодискрепансной тройки (аддитивная рекуррента на
		// иррациональных константах: покрывает пространство ориентаций ровнее, чем ГПСЧ, - на
		// десятке раундов это заметно меньше пятен в поле).
		float u1 = Frac(sequence * 0.7548776662f);
		float u2 = Frac(sequence * 0.5698402909f);
		float u3 = Frac(sequence * 0.6180339887f);
		float r1 = MathF.Sqrt(1f - u1), r2 = MathF.Sqrt(u1);
		var rotation = new Quaternion(
			r1 * MathF.Sin(2f * MathF.PI * u2), r1 * MathF.Cos(2f * MathF.PI * u2),
			r2 * MathF.Sin(2f * MathF.PI * u3), r2 * MathF.Cos(2f * MathF.PI * u3));

		for (int i = 0; i < count; i++)
		{
			dirs[i] = Vector3.Transform(dirs[i], rotation);
		}

		return dirs;

		static float Frac(float v) => v - MathF.Floor(v);
	}

	private static Vector3[] BuildFibonacciSphere(int count)
	{
		var dirs = new Vector3[count];
		float golden = MathF.PI * (3f - MathF.Sqrt(5f));
		for (int i = 0; i < count; i++)
		{
			float y = 1f - (i + 0.5f) * 2f / count;
			float radius = MathF.Sqrt(MathF.Max(1f - y * y, 0f));
			float phi = golden * i;
			dirs[i] = new Vector3(MathF.Cos(phi) * radius, y, MathF.Sin(phi) * radius);
		}

		return dirs;
	}
}

/// <summary>GPU-сторона probe-GI: четыре атласа + привязка к материалам модели и данные для
/// PreviewSettings-кбуфера (см. PreviewSettingsData.ProbeGrid*). Владеет текстурами.</summary>
public sealed class ProbeGiTextures : IReleaseObject
{
	public IGpuTexture Sh0 { get; }
	public IGpuTexture Sh1 { get; }
	public IGpuTexture Sh2 { get; }
	public IGpuTexture Sh3 { get; }

	/// <summary>Окто-атлас видимости (DDGI depth, см. ProbeGiBakeResult.Vis).</summary>
	public IGpuTexture Vis { get; }

	/// <summary>Атлас релокации: смещение пробы от узла сетки (см.
	/// ProbeGiBakeResult.Offset).</summary>
	public IGpuTexture Offset { get; }

	/// <summary>Карта индирекции кирпичей (см. ProbeGiBakeResult.Indirection) - шейдер читает её
	/// первой и по ней находит блок пробы в пуле.</summary>
	public IGpuTexture Indirection { get; }

	/// <summary>Угол объёма в мире. НЕ константа: прокручиваемый объём ездит за камерой, и материалы
	/// читают его отсюда каждый кадр (см. ProbeGiViewportShared.PushGrid).</summary>
	public Vector4 GridOrigin { get; private set; }

	public Vector4 GridCell { get; }
	public Vector4 GridCounts { get; }

	/// <summary>Размер сетки кирпичей (xyz) и ширина пула в кирпичах (w) - шейдеру нужно и то, и
	/// другое, чтобы развернуть индекс кирпича в координаты блока в атласе.</summary>
	public Vector4 GridBricks { get; }

	/// <summary>Минимальный из шагов сетки - база для normal-бейаса сэмпла (см. GridCell.w).</summary>
	public float MinCellSize { get; }

	private readonly IGraphicsApi _api;

	/// <summary>Атласы заведены с UAV - их пишет compute-раунд напрямую (см. ProbeRoundCS.hlsl), и
	/// <see cref="Update"/> в этом режиме не нужен.</summary>
	public bool GpuWritable { get; }

	public ProbeGiTextures(IGraphicsApi api, ProbeGiBakeResult result, string namePrefix,
		bool gpuWritable = false)
	{
		_api = api;
		GpuWritable = gpuWritable;

		// Размер атласов задаёт ПУЛ кирпичей, а не габарит сетки: пустое пространство в атласе не
		// лежит (см. ProbeGiBakeResult.ShWidth).
		int width = result.ShWidth;
		int height = result.ShHeight;

		// Изменяемые, а не Immutable: прогрессивный бейк перезаливает атласы каждый раунд (см.
		// Update) - пересоздавать текстуры и переприязывать их к материалам столько раз нельзя.
		Sh0 = api.CreateTexture2DMutable($"{namePrefix} ProbeSH0", width, height, true, gpuWritable);
		Sh1 = api.CreateTexture2DMutable($"{namePrefix} ProbeSH1", width, height, true, gpuWritable);
		Sh2 = api.CreateTexture2DMutable($"{namePrefix} ProbeSH2", width, height, true, gpuWritable);
		Sh3 = api.CreateTexture2DMutable($"{namePrefix} ProbeSH3", width, height, true, gpuWritable);
		Vis = api.CreateTexture2DMutable($"{namePrefix} ProbeVis",
			width * ProbeGiBakeResult.VisRes, height * ProbeGiBakeResult.VisRes, true, gpuWritable);
		Offset = api.CreateTexture2DMutable($"{namePrefix} ProbeOffset", width, height, true, gpuWritable);

		// Индирекция - целочисленные индексы, упакованные по байтам: RGBA8, без интерполяции
		// (шейдер читает её через Load).
		Indirection = api.CreateTexture2DMutable($"{namePrefix} ProbeIndirection",
			result.BrickCountX, result.BrickCountY * result.BrickCountZ, floatFormat: false);

		GridOrigin = new Vector4(result.Origin, 1f);

		// w = normal-бейас сэмпла в мировых единицах (доля ячейки, дефолт 0.3) - от утечек через
		// тонкие стены. Вьюпорт может переопределить его из настроек через MinCellSize.
		var cell = result.Cell;
		MinCellSize = MathF.Min(cell.X, MathF.Min(cell.Y, cell.Z));
		GridCell = new Vector4(cell, MinCellSize * 0.3f);
		GridCounts = new Vector4(result.CountX, result.CountY, result.CountZ, 0f);
		GridBricks = new Vector4(result.BrickCountX, result.BrickCountY, result.BrickCountZ,
			result.PoolColumns);

		// Индирекцию заливаем всегда - она геометрична и compute-раундом не пишется. Остальное в
		// GPU-режиме заполнит первый же раунд.
		if (gpuWritable)
		{
			_api.UpdateTexture2D(Indirection, result.Indirection);
		}
		else
		{
			// После GridCounts/GridBricks - Update сверяется с ними (см. Matches).
			Update(result);
		}
	}

	/// <summary>Заливает свежий снимок бейка в уже созданные атласы - привязки материалов и
	/// параметры сетки при этом не трогаются, так что зов дёшев и не требует Flush+WaitForIdle.
	/// Сетка обязана совпадать с той, под которую текстуры создавались (см. <see cref="Matches"/>);
	/// сменилась сетка - пересоздавайте объект.</summary>
	public void Update(ProbeGiBakeResult result)
	{
		if (!Matches(result))
		{
			throw new ArgumentException("Probe grid size does not match the allocated atlases", nameof(result));
		}

		_api.UpdateTexture2D(Sh0, result.Sh0);
		_api.UpdateTexture2D(Sh1, result.Sh1);
		_api.UpdateTexture2D(Sh2, result.Sh2);
		_api.UpdateTexture2D(Sh3, result.Sh3);
		_api.UpdateTexture2D(Vis, result.Vis);
		_api.UpdateTexture2D(Offset, result.Offset);

		// Индирекция геометрична и от раунда к раунду не меняется, но заливать её всё равно надо -
		// текстура создаётся пустой, а стоит это один RGBA8 размером с сетку кирпичей.
		_api.UpdateTexture2D(Indirection, result.Indirection);
	}

	/// <summary>Догоняет прокрутку объёма: новый угол в мире и переписанная карта индирекции. Сами
	/// атласы не трогаются - в этом и смысл прокрутки: их содержимое (поле проб) переезжает вместе
	/// со слотами пула, а не пересчитывается (см. ProbeGiBakeSession.Scroll).</summary>
	public void ApplyScroll(ProbeGiBakeResult result)
	{
		GridOrigin = new Vector4(result.Origin, 1f);
		_api.UpdateTexture2D(Indirection, result.Indirection);
	}

	/// <summary>Та же раскладка пула и сетки кирпичей, что у выделенных атласов - можно обновлять
	/// на месте.</summary>
	public bool Matches(ProbeGiBakeResult result) =>
		result.CountX == (int)GridCounts.X
		&& result.CountY == (int)GridCounts.Y
		&& result.CountZ == (int)GridCounts.Z
		&& result.BrickCountX == (int)GridBricks.X
		&& result.BrickCountY == (int)GridBricks.Y
		&& result.BrickCountZ == (int)GridBricks.Z
		&& result.PoolColumns == (int)GridBricks.W;

	/// <summary>Привязывает атласы ко всем материалам модели (шейдер читает их через Load -
	/// сэмплер не нужен). slotSuffix - "" для базового объёма, "_C1"/"_C2" для мелких каскадов
	/// (см. SampleProbeGi в UnlitInstancedPS.hlsl).</summary>
	public void Bind(ModelLoader model, string slotSuffix = "")
	{
		for (int i = 0; i < model.materialObjects.Count; i++)
		{
			var material = model.materialObjects.GetAt(i).Value;
			material.SetTexture($"_ProbeSh0{slotSuffix}", Sh0);
			material.SetTexture($"_ProbeSh1{slotSuffix}", Sh1);
			material.SetTexture($"_ProbeSh2{slotSuffix}", Sh2);
			material.SetTexture($"_ProbeSh3{slotSuffix}", Sh3);
			material.SetTexture($"_ProbeVis{slotSuffix}", Vis);
			material.SetTexture($"_ProbeOffset{slotSuffix}", Offset);
			material.SetTexture($"_ProbeIndirection{slotSuffix}", Indirection);
		}
	}

	public void Release()
	{
		Indirection.Release();
		Sh0.Release();
		Sh1.Release();
		Sh2.Release();
		Sh3.Release();
		Vis.Release();
		Offset.Release();
	}
}
