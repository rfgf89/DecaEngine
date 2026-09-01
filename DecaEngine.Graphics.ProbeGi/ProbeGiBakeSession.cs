using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
using UnsafeCollections.Collections.Native;
using UnsafeCollections.Collections.Unsafe;

namespace DecaEngine.Graphics.ProbeGi;

/// <summary>Состояние ПРОГРЕССИВНОГО бейка probe-GI: сетка, аккумуляторы поля и геометрические
/// суммы, копящиеся раунд за раундом (см. <see cref="ProbeGiBaker.RunRound"/>). Заменяет прежний
/// «бейк одним куском», который на сцене-уровне занимал секунды и целиком повторялся при каждом
/// движении ползунка света: теперь раунд стоит RaysPerRound лучей на пробу (единицы миллисекунд),
/// поле после любого раунда уже можно показывать, качество набирается со временем, а поворот солнца
/// не выбрасывает накопленное (см. <see cref="SetLighting"/>). Не потокобезопасна: раунды гонять
/// строго по одному, <see cref="ProbeGiBaker.Snapshot"/> звать между ними.</summary>
public sealed class ProbeGiBakeSession
{
	/// <summary>Размер ПЛОТНОЙ сетки проб (см. ProbeGiBakeResult.CountX): проба есть в каждом узле.
	/// </summary>
	public int CountX { get; }
	public int CountY { get; }
	public int CountZ { get; }

	/// <summary>Номер раскладки. С уходом прокрутки объём неподвижен всю жизнь сессии, так что
	/// растёт он больше никогда - оставлен, потому что по нему потребители (дебаг-оверлей)
	/// по-прежнему сверяют актуальность своей копии сетки.</summary>
	internal int LayoutGeneration;

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
	public bool NoGeometry { get; internal set; }

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
	public int RaysPerRound => Realtime ? RealtimeRaysPerRound : _bakeRaysPerRound;

	/// <summary>Сколько первых лучей веера - ФИКСИРОВАННЫЕ (см.
	/// <see cref="ProbeGiBaker.FixedRayCount"/>).</summary>
	public int FixedRays => ProbeGiBaker.FixedRayCount(RaysPerRound, Realtime);

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

	/// <summary>Punctual-света сцены (point/spot) - участвуют в прямом свете точек попадания лучей
	/// наравне с солнцем (подход RTXGI: теневой луч к свету + затухание, зеркало формул шейдинга
	/// UnlitInstancedPS). ShadowParams в записях ОБЯЗАН быть нулевым (см.
	/// <see cref="LightCulling.TryBuildBakeLight"/>): раскладка теневых слайсов меняется от камеры
	/// кадр к кадру и в сравнение изменений света входить не должна. Пустой массив = прежнее
	/// поведение (только солнце и небо).</summary>
	internal PunctualLight[] BakeLights = Array.Empty<PunctualLight>();

	/// <summary>Сквозной номер раунда за всю жизнь сессии (в отличие от <see cref="Round"/> не
	/// откатывается) - им поворачивается веер Фибоначчи, чтобы раунды после смены света не
	/// повторяли уже отстрелянные направления.</summary>
	public int Sequence { get; internal set; }

	// Поле проб в двойном буфере: раунд читает прошлое поле (мультибаунс собирается по соседним
	// пробам в точках попаданий) и пишет новое, после чего буферы меняются местами. Прежний бейк
	// клонировал массивы на каждой итерации - на сотнях тысяч проб это само по себе стоило дороже
	// раунда трассировки.
	internal Vector3[] L0R, L1XR, L1YR, L1ZR, L0W, L1XW, L1YW, L1ZW;
	internal float[] ValidityR, ValidityW, SunFracR, SunFracW;

	/// <summary>Постоянная составляющая поля проб (L0) на ЧТЕНИЕ - то, что сейчас видят сэмплеры.
	///
	/// Отдаётся span'ом, а не самим массивом: снаружи это нужно ровно затем, чтобы сверить поле с
	/// GPU-путём (см. сверочный прогон в пробах), и восемь буферов пинг-понга наружу выставлять
	/// незачем - перепутать R и W со стороны потребителя значит сравнить поле с самим собой из
	/// прошлого раунда и не заметить расхождения.</summary>
	public ReadOnlySpan<Vector3> IrradianceRead => L0R;

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
	public readonly ProbeGiBakeResult Result;

	/// <summary>Кэш радианса на поверхностях - источник отскока для лучей бейка (см.
	/// <see cref="SurfaceCache"/>). null, пока первый раунд его не построил, и навсегда, если кэш
	/// выключен настройками.</summary>
	public SurfaceCache? Surface { get; internal set; }

	/// <summary>Кэш поверхностей заказан, но ещё не построен - его захват стоит сотни миллисекунд и
	/// потому отложен до первого (фонового) раунда.</summary>
	internal bool WantsSurfaceCache;

	internal ProbeGiBakeSession(Vector3 origin, Vector3 cell, int cx, int cy, int cz,
		ProbeGiBakeOptions options, Vector3 sunDirection, Vector3 sunColor,
		float envYawRadians, Func<Vector3, Vector3> skyRadiance, int targetRounds)
	{
		CountX = cx;
		CountY = cy;
		CountZ = cz;
		ProbeCount = cx * cy * cz;
		Origin = origin;
		Cell = cell;
		TargetRounds = targetRounds;

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
			Origin = origin,
			Cell = cell,
		};

		// Атлас ровно по сетке - дырок в нём больше нет, каждый тексель принадлежит своей пробе.
		Result.Sh0 = new byte[n * 8];
		Result.Sh1 = new byte[n * 8];
		Result.Sh2 = new byte[n * 8];
		Result.Sh3 = new byte[n * 8];
		Result.Offset = new byte[n * 8];
		Result.Vis = new byte[n * ProbeGiBakeResult.VisRes * ProbeGiBakeResult.VisRes * 8];
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

	/// <summary>Обновляет punctual-света между раундами - та же механика, что у
	/// <see cref="SetLighting"/>: реальное изменение (сдвиг/перекраска/добавление лампы) откатывает
	/// вес раунда, и поле перетекает к новому решению без потери геометрии. ShadowParams в записях
	/// должен быть нулевым (см. <see cref="BakeLights"/>). Возвращает true при изменении.</summary>
	public bool SetPunctualLights(ReadOnlySpan<PunctualLight> lights)
	{
		bool changed = lights.Length != BakeLights.Length;
		if (!changed)
		{
			for (int i = 0; i < lights.Length; i++)
			{
				ref readonly var a = ref lights[i];
				ref var b = ref BakeLights[i];
				if ((a.PositionRange - b.PositionRange).LengthSquared() > 1e-10f
					|| (a.ColorIntensity - b.ColorIntensity).LengthSquared() > 1e-10f
					|| (a.DirectionType - b.DirectionType).LengthSquared() > 1e-10f
					|| (a.SpotAngles - b.SpotAngles).LengthSquared() > 1e-10f)
				{
					changed = true;
					break;
				}
			}
		}

		if (changed)
		{
			BakeLights = lights.ToArray();
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
	}

	/// <summary>Порог средней изменчивости, ниже которого объём считается сошедшимся и раунды
	/// останавливаются (см. <see cref="ProbeGiBakeOptions.RealtimeVariabilityThreshold"/>). Живая
	/// ручка - меняется между раундами.</summary>
	public float VariabilityThreshold { get; set; }

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
