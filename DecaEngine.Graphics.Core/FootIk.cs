using System;
using System.Collections.Generic;
using System.Numerics;

namespace DecaEngine.Graphics;

/// <summary>Результат запроса «что под этой точкой». Абстракция, а не тип физики: солвер живёт в
/// графическом слое и не должен тянуть за собой BepuPhysics - вызывающий подставляет свой
/// райкаст (в редакторе это <c>PhysicsWorld.RayCast</c>, в тестах - плоскость).</summary>
public struct GroundSample
{
	public bool Hit;
	public Vector3 Position;
	public Vector3 Normal;
}

/// <summary>Одна нога: цепочка бедро -> голень -> стопа.</summary>
public sealed class FootIkLeg
{
	public int UpperJoint;
	public int LowerJoint;
	public int FootJoint;

	/// <summary>Сустав точки ОПОРЫ, когда контакт с землёй ниже сустава стопы (-1 - опора и есть
	/// стопа). У человека стопа лежит на полу, и её сустав - честная точка контакта. У дигитиграда
	/// (лиса) «стопа» разметки - скакательный сустав: он висит над землёй на длину плюсны и смещён
	/// от места контакта по горизонтали, поэтому луч под ним щупает НЕ ТУ ступень, а локинг
	/// прибивал к миру сустав, который в опоре обязан проворачиваться над прижатым носком, - нога
	/// шла «по-человечески», половиной лапы по поверхности. Всё, что касается ЗЕМЛИ (луч, подъём,
	/// локинг, канал высоты), считается по этому суставу; two-bone IK по-прежнему решается до
	/// стопы - дельта контакта переносится на неё как есть (плюсна жёсткая, и на рабочих поправках
	/// перенос точен в том же порядке, что и сам солвер).</summary>
	public int ToeJoint = -1;

	/// <summary>Передняя ли это нога четвероногого - группировка для наклона корпуса (см.
	/// <see cref="FootIkSettings.AlignBodyToSlope"/>): наклон считается от перепада средних высот
	/// опоры под передней и задней парами.</summary>
	public bool Front;

	/// <summary>Правая ли это нога - группировка для ПОПЕРЕЧНОГО наклона (roll): перепад опоры
	/// лево/право наклоняет корпус вокруг оси перед-зад. Персонаж, стоящий боком к лестнице, без
	/// roll держит корпус горизонтальным - ровно та поза «углы наклона неправильные».</summary>
	public bool Right;

	/// <summary>Подсказка, куда смотрит колено, в пространстве МОДЕЛИ. Без неё two-bone IK волен
	/// согнуть ногу в любую сторону вокруг оси бедро-стопа, и колени выворачиваются назад.</summary>
	public Vector3 PoleVector = Vector3.UnitZ;

	/// <summary>
	/// Ось сгиба колена в ЛОКАЛЬНОМ пространстве среднего сустава (mid axis у ozz). Используется
	/// только когда <see cref="AutoKneeAxis"/> выключен.
	/// </summary>
	public Vector3 KneeAxis = Vector3.UnitX;

	/// <summary>
	/// Считать ось колена из текущей позы вместо <see cref="KneeAxis"/>. По умолчанию включено, и
	/// это не удобство, а защита от тихой ошибки: ozz поворачивает колено ВОКРУГ этой оси на угол из
	/// теоремы косинусов, и при неверной оси длина цепочки после поворота получается не та - стопа
	/// систематически НЕ ДОХОДИТ до вполне достижимой цели. Ошибка при этом выглядит как «солвер
	/// неточный», а не как «ось не та».
	/// </summary>
	public bool AutoKneeAxis = true;

	/// <summary>Высота сустава ОПОРЫ (носка, если он задан, иначе стопы) над подошвой. Без неё
	/// стопа утапливается в пол ровно на толщину ноги: райкаст даёт точку ПОВЕРХНОСТИ, а IK ставит
	/// туда СУСТАВ.</summary>
	public float AnkleHeight = 0.1f;

	/// <summary>Состояние сглаживания: высота, к которой стопа шла в прошлом кадре.</summary>
	internal float SmoothedLift;
	internal bool Initialized;

	// --- Локинг опорной стопы (см. FootIkSettings.LockFeet) ------------------------------------
	//
	// Огибающая подъёма стопы над плоскостью опоры: стойка распознаётся относительно
	// СОБСТВЕННОГО цикла ноги, а не авторской высоты щиколотки - скакательный сустав
	// четвероногого и В ОПОРЕ висит на десяток единиц выше подошвы, и любой абсолютный порог
	// для него ложь.

	internal float LiftMin;
	internal float LiftMax;
	internal bool EnvelopeInit;

	/// <summary>Стопа в стойке и удерживается за точку мира.</summary>
	internal bool LockActive;
	internal Vector3 LockPointWorld;

	/// <summary>Вес удержания 0..1 - захват и отпуск ПЛАВНЫЕ: мгновенный отпуск в момент отрыва
	/// читается как щелчок стопы.</summary>
	internal float LockBlend;

	/// <summary>Увод, КАКИМ он был в последний активный кадр захвата. На спаде веса цель ведётся
	/// от него, а не от живого пересчёта: устаревший пин продолжает уезжать от анимационной стопы
	/// (на шаге на месте - на длину ноги за долю секунды), и «спад веса × растущий увод» давал
	/// рывок лапы на каждом отпуске (замерено дребезгом: 106-115 мм/кадр² против 23-46 у клипа).</summary>
	internal Vector3 LockFrozenOffset;

	/// <summary>Мировая позиция точки опоры прошлого кадра - для скорости контакта: захват
	/// впускается только на ЗАМЕДЛИВШЕЙСЯ лапе (см. Solve).</summary>
	internal Vector3 PrevContactWorld;
	internal bool PrevContactValid;


	/// <summary>Забывает сглаженную высоту: следующий кадр возьмёт цель как есть, без плавного
	/// подхода. Нужен при телепорте персонажа - иначе стопа поедет к новой земле от высоты,
	/// оставшейся от старой. Локинг забывается по той же причине: точка захвата - мировая, и
	/// после телепорта нога тянулась бы к полу на старом месте.</summary>
	public void ResetSmoothing()
	{
		Initialized = false;
		EnvelopeInit = false;
		LockActive = false;
		LockBlend = 0f;
		PrevContactValid = false;
	}
}

public sealed class FootIkSettings
{
	/// <summary>Джойнт таза - его опускание позволяет дотянуться до нижней стопы, когда ноги стоят
	/// на разной высоте (ступеньки, склон).</summary>
	public int PelvisJoint = -1;

	/// <summary>Вертикаль в пространстве модели.</summary>
	public Vector3 Up = Vector3.UnitY;

	/// <summary>Насколько выше стопы начинать луч и насколько глубоко щупать.</summary>
	public float ProbeUp = 0.5f;
	public float ProbeDown = 1.5f;

	/// <summary>Предел опускания таза. Без него шаг в пропасть утягивает таз к центру Земли -
	/// достаточно одному лучу не найти опоры на разумной глубине.</summary>
	public float MaxPelvisDrop = 0.4f;

	/// <summary>Скорость сглаживания, 1/с. Резкая привязка даёт дрожание на любом стыке
	/// треугольников: луч перескакивает между гранями, и стопа скачет на миллиметры каждый кадр.</summary>
	public float Smoothing = 12f;

	/// <summary>Общий вес эффекта, 0..1. Ноль - IK выключен, поза остаётся анимационной.</summary>
	public float Weight = 1f;

	/// <summary>Доворачивать ли стопу по нормали поверхности.</summary>
	public bool AlignToNormal = true;

	/// <summary>Прибивать ли опорную стопу к точке мира (foot locking). Убирает остаточное
	/// скольжение, когда темп клипа не совпадает с реальной скоростью персонажа - у чистого
	/// вертикального IK стопа в опоре едет по полу вместе с моделью.</summary>
	public bool LockFeet = false;

	/// <summary>Наклонять ли корпус по рельефу (см. одноимённое поле компонента). Требует таза и
	/// обеих групп ног (передней и задней) с опорой.</summary>
	public bool AlignBodyToSlope = false;

	/// <summary>Предел наклона корпуса, радианы (~22°): лестница демо-сцены наклоняет тело
	/// заметно, но персонаж не должен вставать на голову от артефакта луча.</summary>
	public float MaxBodyTilt = 0.4f;

	/// <summary>Состояние сглаживания наклона: углы, к которым корпус шёл в прошлом кадре.
	/// Живёт здесь же, где резолвнутый таз, - объект настроек персистентен на персонажа.</summary>
	internal float SmoothedTilt;
	internal float SmoothedRoll;
	internal bool TiltInitialized;
}

/// <summary>
/// Привязка стоп к рельефу. Порядок шагов не произволен и менять его нельзя:
///
/// 1. щупаем пол под каждой стопой (позы стоп берутся из ПОЗЫ АНИМАЦИИ);
/// 2. опускаем таз на величину самой глубокой стопы - иначе нижняя нога упрётся в предел
///    вытягивания и оторвётся от земли;
/// 3. только теперь дотягиваем каждую стопу two-bone IK - после сдвига таза цели изменились;
/// 4. доворачиваем стопу по нормали.
///
/// Требует нативного ozz (<see cref="OzzPose"/>): two-bone IK живёт там. Без шима метод возвращает
/// false и поза остаётся анимационной - это штатная деградация, а не ошибка.
/// </summary>
public static class FootIk
{
	/// <summary>
	/// Считает и применяет IK. <paramref name="modelToWorld"/> - трансформ сущности: луч пускается в
	/// МИРЕ (пол - объект мира), а IK работает в пространстве МОДЕЛИ, и без обеих матриц одну из
	/// сторон пришлось бы приближать.
	/// </summary>
	public static bool Solve(OzzPose pose, PreparedSkeleton skeleton, IReadOnlyList<FootIkLeg> legs,
		FootIkSettings settings, Matrix4x4 modelToWorld, Transform[] locals, Matrix4x4[] models,
		Func<Vector3, Vector3, float, GroundSample> raycast, float deltaSeconds)
	{
		if (pose == null || legs == null || settings == null || raycast == null || legs.Count == 0 ||
			settings.Weight <= 0f)
		{
			return false;
		}

		if (!Matrix4x4.Invert(modelToWorld, out var worldToModel))
		{
			return false;
		}

		var up = Vector3.Normalize(settings.Up);

		// Целевая ВЫСОТА стопы вдоль вертикали, в пространстве модели, - абсолютная, а не «на
		// столько-то поднять». Абсолютная цель не зависит от того, куда к моменту её применения
		// успел уехать таз, и снимает целый класс ошибок порядка шагов: относительный вариант
		// приходилось бы каждый раз поправлять на смещение таза вручную.
		Span<float> targetHeights = legs.Count <= 8 ? stackalloc float[legs.Count] : new float[legs.Count];
		Span<GroundSample> hits = legs.Count <= 8 ? stackalloc GroundSample[legs.Count] : new GroundSample[legs.Count];
		Span<float> lifts = legs.Count <= 8 ? stackalloc float[legs.Count] : new float[legs.Count];
		Span<float> reaches = legs.Count <= 8 ? stackalloc float[legs.Count] : new float[legs.Count];

		// Лучи - ОТДЕЛЬНЫМ проходом, до наклона корпуса: рельеф под лапами - вход и для наклона,
		// и для вертикальных целей ног. Здесь же снимается ПОДЪЁМ стопы над плоскостью опоры -
		// именно ДО наклона: наклон поднимает лапы в пространстве модели, и подъём, снятый после
		// него, принимал наклон за замах - лапы держались в воздухе на его высоту.
		for (int i = 0; i < legs.Count; i++)
		{
			var contactModel = models[ContactOf(legs[i])].Translation;
			var contactWorld = Vector3.Transform(contactModel, modelToWorld);
			var worldUp = Vector3.Normalize(Vector3.TransformNormal(up, modelToWorld));

			hits[i] = raycast(contactWorld + worldUp * settings.ProbeUp, -worldUp,
				settings.ProbeUp + settings.ProbeDown);

			// Подъём - СЫРОЙ, без среза нулём: он питает огибающую локинга, а та обязана видеть
			// форму цикла целиком. AnkleHeight, завышенная против сустава опоры (носок в опоре ниже
			// неё), со срезом прижимала сигнал стойки к нулю на пол-цикла - захват держался до
			// самого замаха и дёргал ногу против клипа (бег с локингом: колено +0.59). Каналу
			// высоты отрицательный подъём нельзя (вдавил бы стопу под рельеф) - ноль берётся там.
			lifts[i] = Vector3.Dot(contactModel, up) - legs[i].AnkleHeight;

			if (Environment.GetEnvironmentVariable("DECA_TILT_DEBUG") == "1")
			{
				Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
					$"[ray] i={i} front={legs[i].Front} contactW=({contactWorld.X:F2}|{contactWorld.Y:F2}|{contactWorld.Z:F2}) " +
					$"hit={hits[i].Hit} at=({hits[i].Position.X:F2}|{hits[i].Position.Y:F2}|{hits[i].Position.Z:F2})"));
			}
		}

		// Наклон корпуса - ДО целей ног: наклонённый таз меняет позы всех ног, и цели обязаны
		// считаться по ним. Хиты не перецеливаются: наклон сдвигает лапы по горизонтали на
		// сантиметры, рельеф под ними тот же.
		if (!ApplyBodyTilt(pose, skeleton, legs, settings, hits, worldToModel, up, locals, models,
			deltaSeconds))
		{
			return false;
		}

		float pelvisDelta = float.MaxValue;

		for (int i = 0; i < legs.Count; i++)
		{
			var leg = legs[i];
			var contactModel = models[ContactOf(leg)].Translation;
			float contactHeight = Vector3.Dot(contactModel, up);
			var sample = hits[i];

			if (!sample.Hit)
			{
				// Под стопой пусто (шаг с обрыва): нога остаётся анимационной. Ноль в дельту таза -
				// нога без земли запрещает ПОДЪЁМ (см. ниже), но не мешает опусканию к другим.
				targetHeights[i] = contactHeight;
				pelvisDelta = MathF.Min(pelvisDelta, 0f);
				continue;
			}

			var groundModel = Vector3.Transform(sample.Position, worldToModel);

			// Подъём стопы ИЗ КЛИПА сохраняется: цель - рельеф под стопой плюс её текущая высота над
			// плоскостью опоры клипа (ноль модели: сущность стоит ногами на земле). Прижимать каждую
			// стопу к поверхности безусловно нельзя: у идущего персонажа «самая глубокая стопа» - это
			// всегда лапа в замахе (земля под ней ниже её самой), и таз вминался в пол на высоту
			// замаха каждый шаг - персонаж шёл по колено в земле. На ровном полу поправка при этом
			// вырождается в ноль сама собой, и IK не трогает клип вовсе.
			//
			// Сглаживается ТОЛЬКО РЕЛЬЕФ, не полная цель: сглаживание существует против дрожи луча
			// на стыках треугольников, а сглаженная ПОЛНАЯ цель отстаёт от собственного маха клипа -
			// на галопе, где лапа машет быстро, IK держал её у отставшей высоты и прижимал под
			// корпус («нога в теле» на чистом беге; шаг машет медленно, и там отставание невидимо).
			float ground = Approach(leg, Vector3.Dot(groundModel, up), settings.Smoothing, deltaSeconds);
			float desired = ground + leg.AnkleHeight + MathF.Max(lifts[i], 0f);

			var footModel = models[leg.FootJoint].Translation;
			reaches[i] =
				Vector3.Distance(models[leg.UpperJoint].Translation, models[leg.LowerJoint].Translation) +
				Vector3.Distance(models[leg.LowerJoint].Translation, footModel);

			// Цель здесь - НЕКЛАМПЛЕННАЯ, и дельта таза считается по ней же: таз обязан узнать
			// ПОЛНУЮ потребность ног. Кламп долей длины ноги (см. третий проход) до сдвига таза
			// резал и его: утопленная в лестницу лиса всплывала лишь на зарезанный минимум, ноги
			// съедали остаток сгибом и всё равно не дотягивались - копыта висели над ступенью на
			// недобор клампа при перекошенных коленях.
			//
			// Без второго сглаживания: рельеф уже сглажен выше, а мах клипа в desired обязан
			// пройти нефильтрованным (см. коммент про галоп).
			targetHeights[i] = desired;
			pelvisDelta = MathF.Min(pelvisDelta, desired - contactHeight);

			if (Environment.GetEnvironmentVariable("DECA_TILT_DEBUG") == "1")
			{
				Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
					$"[height] i={i} contact={contactHeight:F1} ground={ground:F1} lift={lifts[i]:F1} " +
					$"desired={desired:F1}"));
			}
		}

		// Таз сдвигается на МИНИМАЛЬНУЮ из дельт по ногам - в обе стороны. Вниз это прежнее
		// «опуститься к самой глубокой стопе». Вверх подъём случается, только когда земля выше
		// опоры под ВСЕМИ лапами разом (сущность утоплена в пол гизмо): минимум по ногам зануляет
		// подъём, если хоть одна нога стоит на плоскости, - «одна нога на возвышении» по-прежнему
		// решается сгибом, а не всплытием. Без подъёма утопленный персонаж поджимал лапы в корпус
		// на любом весе (клампы целей не дают дотянуться, но треть ноги вверх - уже «ноги задом»).
		float pelvisDrop = pelvisDelta == float.MaxValue
			? 0f
			: Math.Clamp(pelvisDelta, -settings.MaxPelvisDrop, settings.MaxPelvisDrop) * settings.Weight;

		if (settings.PelvisJoint >= 0 && pelvisDrop != 0f)
		{
			// Сдвиг задаётся в пространстве РОДИТЕЛЯ таза - там же живёт его локальная трансляция.
			int parent = skeleton.Parents[settings.PelvisJoint];
			var offset = up * pelvisDrop;

			if (parent >= 0 && Matrix4x4.Invert(models[parent], out var parentInverse))
			{
				offset = Vector3.TransformNormal(offset, parentInverse);
			}

			locals[settings.PelvisJoint].position += offset;

			if (!pose.WriteLocalTransforms(locals) || !pose.LocalToModel() || !pose.ReadModelMatrices(models))
			{
				return false;
			}
		}

		// Анимационная ориентация стоп - ПОСЛЕ наклона корпуса и сдвига таза (они меняют позу
		// осознанно), но ДО two-bone: тот крутит колено, и жёстко висящая на голени стопа
		// поворачивается вместе с ним. У дигитиграда это разворачивало лапу носком назад-вверх -
		// голень с плюсной читались как лежащая на полу «человеческая подошва», хотя в клипе
		// плюсна почти вертикальна. После солва ориентация восстанавливается (см. AlignFeet).
		Span<Quaternion> footRotations = legs.Count <= 8
			? stackalloc Quaternion[legs.Count]
			: new Quaternion[legs.Count];

		for (int i = 0; i < legs.Count; i++)
		{
			// КОНВЕНЦИЯ, дважды ловившая лапу носком вверх: у System.Numerics оператор * применяет
			// ПРАВЫЙ кватернион первым (Transform(v, a*b) = сначала b, потом a), а строчные матрицы
			// цепляются наоборот (M_local * M_parent, local первым). Модельная ориентация сустава
			// поэтому parent * local, и раскладывается обратно как Inverse(parent) * model. Оба
			// неверных варианта (кватернион из матрицы; locals * parent) замерены [final]-трейсом:
			// носок 40.6/38.9 при цели 16.8/14.0 - выше скакательного сустава.
			int foot = legs[i].FootJoint;
			int parent = skeleton.Parents[foot];
			var parentRotation = parent >= 0
				? Quaternion.CreateFromRotationMatrix(Orthonormal(models[parent]))
				: Quaternion.Identity;

			footRotations[i] = parentRotation * locals[foot].rotation;
		}

		// Снимок локалей ноги до солва - только для диагностики поворотов (см. [final] ниже).
		Quaternion[]? preSolveLocals = null;
		if (Environment.GetEnvironmentVariable("DECA_TILT_DEBUG") == "1")
		{
			preSolveLocals = new Quaternion[legs.Count * 3];
			for (int i = 0; i < legs.Count; i++)
			{
				preSolveLocals[i * 3] = locals[legs[i].UpperJoint].rotation;
				preSolveLocals[i * 3 + 1] = locals[legs[i].LowerJoint].rotation;
				preSolveLocals[i * 3 + 2] = locals[legs[i].FootJoint].rotation;
			}
		}

		// Цели считаются ПОСЛЕ сдвига таза - по обновлённым модельным матрицам.
		for (int i = 0; i < legs.Count; i++)
		{
			var leg = legs[i];

			if (!hits[i].Hit)
			{
				// Земли нет - и держаться не за что: подвисшая над обрывом стопа с активным
				// локингом тянулась бы к точке, которой больше не существует.
				leg.LockActive = false;
				leg.LockBlend = ApproachValue(leg.LockBlend, 0f, LockRate, deltaSeconds);
				continue;
			}

			var footModel = models[leg.FootJoint].Translation;
			var contactModel = models[ContactOf(leg)].Translation;

			// Сдвиг вдоль вертикали: горизонтальное положение стопы задаёт анимация - КРОМЕ
			// стойки при включённом локинге, где горизонталь держит точка захвата (см. ниже).
			// Дельта считается ПО ТОЧКЕ ОПОРЫ, а прикладывается к СТОПЕ - two-bone решается до неё;
			// после сдвига таза оба сустава уехали одинаково, и дельта у них общая.
			//
			// Вес - В ЦЕЛИ, а солв всегда ПОЛНЫЙ (ozz weight=1). Частичный вес обязан быть полным
			// решением к промежуточной цели: тогда каждая точка ползунка - честная IK-поза со своей
			// плоскостью ноги и правильным знаком колена. Оба «поворотных» способа - вес внутри ozz
			// (лерп коррекций) и slerp-бленд поз до/после - на большой коррекции ведут колено по
			// длинной дуге: на середине ползунка нога проворачивалась через прямую в выворот
			// (замерено на перепаде 0.16: вес 0.5-0.7 - вывернутое колено, 0 и 1 - правильные).
			// Остаточная поправка ограничена ДОЛЕЙ ДЛИНЫ НОГИ - ПОСЛЕ сдвига таза: кламп защищает
			// силуэт ноги в её конечной конфигурации, и перепад, который взял на себя таз, в него
			// не входит. IK не трогает XZ стопы (это шаг клипа), и весь остаток уходит в сгиб
			// коленей - на рельефе круче, чем нога способна отработать, согнутый сустав протыкает
			// корпус (задние ноги лисы уходили в её же тело на склоне). Вверх теснее, чем вниз:
			// удлинение распрямляет ногу и выглядит терпимо, укорочение складывает сустав в силуэт
			// уже на трети цепочки. Насыщение - осознанная деградация: стопа частично отрывается от
			// рельефа вместо позы, ломающей тело. Длина - ЦЕПОЧКИ ДО СТОПЫ, без плюсны: дельта
			// контакта прикладывается к стопе как есть, и отрабатывает её ровно two-bone до стопы -
			// пределы с плюсной в длине разрешали цели за досягаемостью цепочки, и колено
			// выворачивалось (замерено: бег с локингом +0.94, перепад на весе 1.0 +0.35).
			float current = Vector3.Dot(contactModel, up);
			float delta = Math.Clamp(targetHeights[i] - current, -0.5f * reaches[i], 0.25f * reaches[i]);
			var target = footModel + up * (delta * settings.Weight);

			if (Environment.GetEnvironmentVariable("DECA_TILT_DEBUG") == "1")
			{
				Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
					$"[solve] i={i} current={current:F1} target={targetHeights[i]:F1} delta={delta:F1} " +
					$"foot=({footModel.X:F1}|{footModel.Y:F1}|{footModel.Z:F1})"));
			}

			if (settings.LockFeet)
			{
				// Скорость точки опоры В МИРЕ - фильтр захвата: огибающая впускает лок у самого
				// касания, когда лапа ещё летит на посадочной скорости, и пин осаживал её за пару
				// кадров - рывок на каждом такте (дребезг 106-115 мм/кадр² против 23-46 у клипа;
				// весь излишек - от локинга, ветка NOLOCK ровняется с клипом). Мир, а не модель:
				// у идущего персонажа опорная лапа неподвижна именно в мире, в пространстве модели
				// она едет назад со скоростью тела.
				var contactWorld = Vector3.Transform(contactModel, modelToWorld);
				float contactSpeed = 0f;

				if (leg.PrevContactValid && deltaSeconds > 0f)
				{
					contactSpeed = Vector3.Distance(contactWorld, leg.PrevContactWorld) / deltaSeconds;
				}

				leg.PrevContactWorld = contactWorld;
				leg.PrevContactValid = true;

				// Порог - в масштабе ноги: длина цепочки в секунду. Опорная лапа держится на долях
				// этой скорости, посадочный мах - в разы выше. Первый кадр и нулевой шаг дают
				// нулевую скорость - захват разрешён, как раньше.
				float worldReach = reaches[i] * Vector3.TransformNormal(up, modelToWorld).Length();
				bool slowEnough = contactSpeed < 1.2f * worldReach;

				bool entered = UpdateLockState(leg, lifts[i], reaches[i], deltaSeconds, slowEnough);

				// Пока вес удержания не набрал половину, точка захвата СЛЕДУЕТ за стопой: захват
				// на самом входе в опору пришпиливает лапу там, где она ещё движется, и связка
				// «ранний захват + страховочный отпуск» дёргает стопу на каждом такте. Пин
				// затягивается только на окрепшем захвате.
				if (entered || (leg.LockActive && leg.LockBlend < 0.5f))
				{
					leg.LockPointWorld = Vector3.Transform(contactModel, modelToWorld);
				}

				if (leg.LockActive || leg.LockBlend > 1e-3f)
				{
					if (leg.LockActive)
					{
						var lockModel = Vector3.Transform(leg.LockPointWorld, worldToModel);

						// Предел досягаемости: тело ушло вперёд, точку захвата больше не достать -
						// отпустить РАНЬШЕ, чем нога вытянется в струну и начнёт дёргать таз.
						// Проверяется СТОПА, КАКОЙ ОНА СТАНЕТ при удержании (захваченная точка плюс
						// жёсткий офсет плюсны): длина в reaches - цепочки до стопы, и дистанция до
						// точки НОСКА сравнивала бы длины разных отрезков.
						var footAtLock = lockModel + (footModel - contactModel);
						if (Vector3.Distance(models[leg.UpperJoint].Translation, footAtLock) >
							0.95f * reaches[i])
						{
							leg.LockActive = false;
						}

						// Страховка от рассинхрона темпа: цель, уехавшая по горизонтали дальше трети
						// ноги от анимационной точки опоры, отпускается. Удержание такой цели
						// физически достижимо (нога дотягивается), но тащит лапу под корпус - при
						// темпе клипа, разошедшемся со скоростью тела, это «нога в теле» каждый такт.
						var lockOffset = lockModel - contactModel;
						lockOffset -= up * Vector3.Dot(lockOffset, up);
						if (lockOffset.Length() > 0.35f * reaches[i])
						{
							leg.LockActive = false;
						}

						// Увод НАСЫЩАЕТСЯ порогом отпуска и ЗАМОРАЖИВАЕТСЯ: на спаде веса цель
						// ведётся от последнего активного увода, а не от живого пересчёта -
						// устаревший пин продолжает уезжать, и «спад веса × растущий увод» был
						// рывком лапы на каждом отпуске (дребезг 106-115 мм/кадр² против 23-46 у
						// клипа) и перегибом почти прямой ноги в выворот на галопе (+0.58..0.72).
						float dragLimit = 0.35f * reaches[i];
						float dragLength = lockOffset.Length();
						if (dragLength > dragLimit)
						{
							lockOffset *= dragLimit / dragLength;
						}

						leg.LockFrozenOffset = lockOffset;
					}

					leg.LockBlend = ApproachValue(leg.LockBlend, leg.LockActive ? 1f : 0f,
						LockRate, deltaSeconds);

					if (leg.LockBlend > 1e-3f)
					{
						// Лочится ТОЛЬКО ГОРИЗОНТАЛЬ: вертикаль продолжает вести канал высоты -
						// локинг и подстройка к рельефу не дерутся за одну координату. Держится
						// ТОЧКА ОПОРЫ (у дигитиграда - носок): горизонтальный увод контакта от
						// точки захвата переносится на цель стопы, и нога в опоре проворачивается
						// над прижатым носком, а не замирает скакательным суставом в воздухе.
						// Вес - тоже в цели (см. выше).
						target += leg.LockFrozenOffset * (leg.LockBlend * Math.Clamp(settings.Weight, 0f, 1f));
					}
				}
			}

			pose.TwoBoneIk(leg.UpperJoint, leg.LowerJoint, leg.FootJoint, target,
				PoleOf(leg, models), KneeAxisOf(leg, models));
		}

		if (!pose.LocalToModel() || !pose.ReadModelMatrices(models) || !pose.ReadLocalTransforms(locals))
		{
			return false;
		}

		AlignFeet(skeleton, legs, hits, footRotations, settings, worldToModel, up, locals, models);

		if (!pose.WriteLocalTransforms(locals) || !pose.LocalToModel() || !pose.ReadModelMatrices(models))
		{
			return false;
		}

		if (preSolveLocals != null)
		{
			for (int i = 0; i < legs.Count; i++)
			{
				// Повороты ЛОКАЛЕЙ суставов ноги за солв: большая разница между соседями - это
				// candy-wrap скиннинга в зоне их блендинга.
				var leg = legs[i];
				float upperTurn = TurnOf(preSolveLocals[i * 3], locals[leg.UpperJoint].rotation);
				float lowerTurn = TurnOf(preSolveLocals[i * 3 + 1], locals[leg.LowerJoint].rotation);
				float footTurn = TurnOf(preSolveLocals[i * 3 + 2], locals[leg.FootJoint].rotation);

				Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
					$"[final] i={i} contact={Vector3.Dot(models[ContactOf(leg)].Translation, up):F1} " +
					$"foot={Vector3.Dot(models[leg.FootJoint].Translation, up):F1} " +
					$"target={targetHeights[i]:F1} " +
					$"повороты локалей: бедро {upperTurn:F0}° колено {lowerTurn:F0}° стопа {footTurn:F0}°"));
			}
		}

		return true;
	}

	/// <summary>Полный угол между двумя локальными поворотами, градусы.</summary>
	private static float TurnOf(Quaternion before, Quaternion after)
	{
		var delta = Quaternion.Normalize(after * Quaternion.Inverse(before));
		return 2f * MathF.Acos(Math.Clamp(MathF.Abs(delta.W), 0f, 1f)) * 180f / MathF.PI;
	}

	/// <summary>Сустав, которым нога касается земли: носок, если он задан, иначе стопа.</summary>
	private static int ContactOf(FootIkLeg leg) => leg.ToeJoint >= 0 ? leg.ToeJoint : leg.FootJoint;

	/// <summary>
	/// Полюс колена - НАПРАВЛЕНИЕ ВЕРХНЕЙ КОСТИ (бедро → колено) текущей позы. Приём из
	/// канонического сэмпла foot_ik самого ozz (там - ось кости среднего сустава,
	/// models[knee].cols[1]): вектор существует ВСЕГДА, следует за позой и по построению наклонён
	/// в сторону изгиба - колено смещено от линии бедро-стопа именно туда, куда указывает бедро.
	///
	/// Ровно ВЕРХНЕЙ, не нижней: нижняя кость согнутой ноги откинута в ПРОТИВОПОЛОЖНУЮ от изгиба
	/// сторону, и полюс из неё заворачивает колено назад (замерено свипом: изгиб +0.97 при любом
	/// весе). История остальных вариантов, чтобы не переизобретать: константный полюс (UnitZ)
	/// выкручивал голень под корпус в позах галопа (two-bone IK разворачивает плоскость ноги к
	/// полюсу даже при нулевой поправке); полюс из ВЕКТОРА ИЗГИБА вырождался в шум на почти прямой
	/// ноге (каждый такт шага!) и требовал памяти с порогом уверенности. Ось кости не вырождается
	/// в принципе.
	/// </summary>
	private static Vector3 PoleOf(FootIkLeg leg, Matrix4x4[] models)
	{
		var direction = models[leg.LowerJoint].Translation - models[leg.UpperJoint].Translation;
		return direction.LengthSquared() > 1e-8f ? direction : leg.PoleVector;
	}

	/// <summary>
	/// Ось сгиба колена: нормаль к плоскости, в которой лежит нога сейчас, переведённая в локальное
	/// пространство среднего сустава (ozz ждёт её именно там). Вырожденный случай - нога вытянута в
	/// прямую линию, тогда плоскости нет и нормаль вырождается в ноль; тогда берётся заданная
	/// вручную ось, иначе поворот пошёл бы вокруг нулевого вектора.
	///
	/// ЗНАК НОРМАЛИ НЕ ПРОИЗВОЛЕН: ozz гнёт колено вокруг неё в положительную сторону, и с
	/// противоположной осью он достигает ту же цель, ПЕРЕВЕРНУВ ПЛОСКОСТЬ НОГИ, - бедро
	/// скручивалось на 180° вокруг своей оси КАЖДЫЙ солв даже при тождественной цели. Позиции
	/// суставов при этом сходятся точно (все позиционные пробники слепы), а скиннинг между
	/// суставами сворачивается в жгут - «сетка выворачивается на суставах». Ловится только
	/// трейсом поворотов локалей ([final] в DECA_TILT_DEBUG: было бедро 180°, стало 0°).
	/// </summary>
	private static Vector3 KneeAxisOf(FootIkLeg leg, Matrix4x4[] models)
	{
		if (!leg.AutoKneeAxis)
		{
			return leg.KneeAxis;
		}

		var upper = models[leg.UpperJoint].Translation;
		var mid = models[leg.LowerJoint].Translation;
		var foot = models[leg.FootJoint].Translation;

		var axis = Vector3.Cross(foot - mid, mid - upper);
		if (axis.LengthSquared() < 1e-8f || !Matrix4x4.Invert(models[leg.LowerJoint], out var midInverse))
		{
			return leg.KneeAxis;
		}

		var local = Vector3.TransformNormal(axis, midInverse);
		return local.LengthSquared() > 1e-10f ? Vector3.Normalize(local) : leg.KneeAxis;
	}

	/// <summary>
	/// Наклон корпуса по рельефу: перепад СРЕДНИХ высот опоры под передней и задней парами ног
	/// превращается в поворот таза вокруг боковой оси. Четвероногое на лестнице ложится телом
	/// вдоль перепада - без наклона оно стоит горизонтально, зависнув лапами над ступенями, и
	/// никакие отдельно согнутые ноги этого не прячут. Требует обеих групп с опорой: одной паре
	/// наклоняться не от чего.
	/// </summary>
	private static bool ApplyBodyTilt(OzzPose pose, PreparedSkeleton skeleton,
		IReadOnlyList<FootIkLeg> legs, FootIkSettings settings, ReadOnlySpan<GroundSample> hits,
		in Matrix4x4 worldToModel, Vector3 up, Transform[] locals, Matrix4x4[] models,
		float deltaSeconds)
	{
		if (!settings.AlignBodyToSlope || settings.PelvisJoint < 0)
		{
			return true;
		}

		float frontGround = 0f, hindGround = 0f, leftGround = 0f, rightGround = 0f;
		int frontCount = 0, hindCount = 0, leftCount = 0, rightCount = 0;
		var frontCenter = Vector3.Zero;
		var hindCenter = Vector3.Zero;
		var leftCenter = Vector3.Zero;
		var rightCenter = Vector3.Zero;

		for (int i = 0; i < legs.Count; i++)
		{
			if (!hits[i].Hit)
			{
				continue;
			}

			float ground = Vector3.Dot(Vector3.Transform(hits[i].Position, worldToModel), up);
			var foot = models[ContactOf(legs[i])].Translation;

			if (legs[i].Front)
			{
				frontGround += ground;
				frontCenter += foot;
				frontCount++;
			}
			else
			{
				hindGround += ground;
				hindCenter += foot;
				hindCount++;
			}

			if (legs[i].Right)
			{
				rightGround += ground;
				rightCenter += foot;
				rightCount++;
			}
			else
			{
				leftGround += ground;
				leftCenter += foot;
				leftCount++;
			}
		}

		if (frontCount == 0 || hindCount == 0)
		{
			return true;
		}

		frontGround /= frontCount;
		hindGround /= hindCount;
		frontCenter /= frontCount;
		hindCenter /= hindCount;

		var span = frontCenter - hindCenter;
		span -= up * Vector3.Dot(span, up);
		float distance = span.Length();

		if (distance < 1e-4f)
		{
			return true;
		}

		float target = Math.Clamp(MathF.Atan2(frontGround - hindGround, distance),
			-settings.MaxBodyTilt, settings.MaxBodyTilt);

		// Поперечный наклон (roll) - тем же приёмом от перепада лево/право. Персонаж боком к
		// лестнице без него держит корпус горизонтальным, свесив ноги нижней стороны.
		float rollTarget = 0f;
		if (leftCount > 0 && rightCount > 0)
		{
			leftGround /= leftCount;
			rightGround /= rightCount;
			leftCenter /= leftCount;
			rightCenter /= rightCount;

			var lateral = rightCenter - leftCenter;
			lateral -= up * Vector3.Dot(lateral, up);
			float lateralDistance = lateral.Length();

			if (lateralDistance > 1e-4f)
			{
				rollTarget = Math.Clamp(MathF.Atan2(rightGround - leftGround, lateralDistance),
					-settings.MaxBodyTilt, settings.MaxBodyTilt);
			}
		}

		if (Environment.GetEnvironmentVariable("DECA_TILT_DEBUG") == "1")
		{
			Console.WriteLine($"[tilt] front={frontGround:0.#}({frontCount}) hind={hindGround:0.#}({hindCount}) " +
				$"dist={distance:0.#} target={target * 180f / MathF.PI:0.#}deg " +
				$"left={leftGround / MathF.Max(leftCount, 1):0.#}({leftCount}) " +
				$"right={rightGround / MathF.Max(rightCount, 1):0.#}({rightCount}) " +
				$"rollTarget={rollTarget * 180f / MathF.PI:0.#}deg smoothedRoll={settings.SmoothedRoll * 180f / MathF.PI:0.#}deg");

			for (int i = 0; i < legs.Count; i++)
			{
				var footPosition = models[legs[i].FootJoint].Translation;
				Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
					$"[tilt]   leg{i} front={legs[i].Front} joint={legs[i].FootJoint} " +
					$"foot=({footPosition.X:F1}|{footPosition.Y:F1}|{footPosition.Z:F1})"));
			}
		}

		// Сглаживание тем же темпом, что у высот: наклон питается теми же лучами и дрожит на тех
		// же стыках треугольников. Первый кадр - целевым значением: персонаж, появившийся на
		// склоне, стоит наклонённым сразу, а не доворачивается из горизонтали.
		if (!settings.TiltInitialized)
		{
			settings.SmoothedTilt = target;
			settings.SmoothedRoll = rollTarget;
			settings.TiltInitialized = true;
		}
		else
		{
			// При нулевом шаге (режим редактирования) - МГНОВЕННО, как Approach у высот: замерший
			// на нуле наклон в редакторе выглядит как «фича не работает», и ровно так и было.
			float alpha = settings.Smoothing > 0f && deltaSeconds > 0f
				? 1f - MathF.Exp(-settings.Smoothing * deltaSeconds)
				: 1f;
			settings.SmoothedTilt += (target - settings.SmoothedTilt) * alpha;
			settings.SmoothedRoll += (rollTarget - settings.SmoothedRoll) * alpha;
		}

		float weight = Math.Clamp(settings.Weight, 0f, 1f);
		float angle = settings.SmoothedTilt * weight;
		float roll = settings.SmoothedRoll * weight;

		if (MathF.Abs(angle) < 1e-3f && MathF.Abs(roll) < 1e-3f)
		{
			return true;
		}

		// Оси: наклон (pitch) - вокруг боковой («перед-зад» × вертикаль), положительный угол
		// поднимает нос, когда опора под передними выше; крен (roll) - вокруг самой оси перед-зад,
		// положительный поднимает правый бок, когда опора справа выше. Вращается ТАЗ - корпус
		// пивотится вокруг бёдер, а ноги дальше доводит их собственный IK.
		//
		// Порядок в cross НЕ произволен: оператор * у кватернионов System.Numerics применяет
		// правый множитель первым, и с осью «вертикаль × перед-зад» положительный угол ОПУСКАЛ
		// нос - лиса на склоне к хвосту задирала морду (ось таз-шея 11.2° → 26.3° вместо → -4°;
		// гейт пробника по |дельте| был к знаку слеп).
		var forward = span / distance;
		var axis = Vector3.Cross(forward, up);
		if (axis.LengthSquared() < 1e-8f)
		{
			return true;
		}

		// Композиция - явным умножением в ТОЙ ЖЕ конвенции, в которой поворот применяется ниже
		// (modelRotation = tilt * поза): Concatenate у System.Numerics складывает в обратном
		// порядке, и крен терялся в наклоне (твист таза выходил равным углу наклона, а не крена).
		var tilt = Quaternion.CreateFromAxisAngle(forward, roll) *
			Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), angle);

		if (Environment.GetEnvironmentVariable("DECA_TILT_DEBUG") == "1")
		{
			Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
				$"[apply] angle={angle * 180f / MathF.PI:F1} roll={roll * 180f / MathF.PI:F1} " +
				$"fwd=({forward.X:F2}|{forward.Y:F2}|{forward.Z:F2}) " +
				$"fc=({frontCenter.X:F1}|{frontCenter.Y:F1}|{frontCenter.Z:F1}) " +
				$"hc=({hindCenter.X:F1}|{hindCenter.Y:F1}|{hindCenter.Z:F1})"));
		}

		int pelvis = settings.PelvisJoint;
		int parent = skeleton.Parents[pelvis];
		var parentRotation = parent >= 0
			? Quaternion.CreateFromRotationMatrix(Orthonormal(models[parent]))
			: Quaternion.Identity;

		// Модельная ориентация = parent * local, наклон - слева (применяется ПОСЛЕДНИМ, то есть
		// в пространстве модели), локаль обратно = Inverse(parent) * model - та же конвенция, что
		// в AlignFeet (см. коммент у footRotations в Solve). Прежняя раскладка locals * parent
		// сопрягала наклон bind-ориентацией корня (в ней сидят корневые 90°), и ось наклона
		// уезжала с боковой куда попало.
		var modelRotation = tilt * (parentRotation * locals[pelvis].rotation);
		locals[pelvis].rotation = Quaternion.Normalize(
			Quaternion.Inverse(parentRotation) * modelRotation);

		bool applied = pose.WriteLocalTransforms(locals) && pose.LocalToModel() &&
			pose.ReadModelMatrices(models);

		if (Environment.GetEnvironmentVariable("DECA_TILT_DEBUG") == "1")
		{
			var m = models[pelvis];
			Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
				$"[after] applied={applied} pelvisRow1=({m.M11:F2}|{m.M12:F2}|{m.M13:F2}) " +
				$"row2=({m.M21:F2}|{m.M22:F2}|{m.M23:F2})"));
		}

		return applied;
	}

	/// <summary>Темп захвата и отпуска локинга, 1/с. Быстрее сглаживания высоты: захват обязан
	/// успеть за один кадр опоры, а видимая часть отпуска - за первые сантиметры замаха.</summary>
	private const float LockRate = 25f;

	/// <summary>
	/// Вход/выход стойки по огибающей подъёма стопы. Возвращает true в кадр ВХОДА - в этот момент
	/// вызывающий захватывает точку мира.
	///
	/// Огибающая адаптивная: минимум и максимум подъёма медленно стягиваются к текущему значению и
	/// мгновенно раздвигаются им - за цикл-другой она выучивает размах именно ЭТОЙ ноги в именно
	/// ЭТОМ клипе. Нога, которая не циклится (персонаж стоит), не лочится вовсе: её огибающая
	/// схлопнута, и «стойка» была бы вечной - лапа приклеилась бы к полу насовсем.
	/// </summary>
	private static bool UpdateLockState(FootIkLeg leg, float lift, float reach, float deltaSeconds,
		bool slowEnough)
	{
		if (!leg.EnvelopeInit)
		{
			leg.LiftMin = lift;
			leg.LiftMax = lift;
			leg.EnvelopeInit = true;
		}

		float span = MathF.Max(leg.LiftMax - leg.LiftMin, 1e-6f);
		float relax = 0.5f * span * MathF.Max(deltaSeconds, 0f);

		leg.LiftMin = MathF.Min(lift, leg.LiftMin + relax);
		leg.LiftMax = MathF.Max(lift, leg.LiftMax - relax);
		span = leg.LiftMax - leg.LiftMin;

		if (span < 0.01f * MathF.Max(reach, 1e-6f))
		{
			leg.LockActive = false;
			return false;
		}

		// Гистерезис: порог входа НИЖЕ порога выхода, иначе стопа на границе стойки дребезжит
		// захватом каждый кадр, и каждый захват - новая точка, то есть то же скольжение, только
		// ступеньками.
		float enter = leg.LiftMin + 0.20f * span;
		float exit = leg.LiftMin + 0.35f * span;

		// Вход - только на замедлившейся лапе (см. фильтр скорости в Solve): по одной лишь высоте
		// захват случался у самого касания, на посадочной скорости, и пин осаживал лапу рывком.
		if (!leg.LockActive && lift < enter && slowEnough)
		{
			leg.LockActive = true;
			return true;
		}

		if (leg.LockActive && lift > exit)
		{
			leg.LockActive = false;
		}

		return false;
	}

	private static float ApproachValue(float value, float target, float rate, float deltaSeconds)
	{
		if (deltaSeconds <= 0f || rate <= 0f)
		{
			return value;
		}

		return value + (target - value) * (1f - MathF.Exp(-rate * deltaSeconds));
	}

	/// <summary>Экспоненциальное приближение к цели, независимое от частоты кадров: за
	/// <paramref name="deltaSeconds"/> покрывается доля <c>1 - exp(-rate*dt)</c>, а не фиксированная
	/// доля на кадр (та дала бы разную скорость при разном FPS).</summary>
	private static float Approach(FootIkLeg leg, float target, float rate, float deltaSeconds)
	{
		if (!leg.Initialized)
		{
			leg.SmoothedLift = target;
			leg.Initialized = true;
			return target;
		}

		float alpha = rate > 0f && deltaSeconds > 0f ? 1f - MathF.Exp(-rate * deltaSeconds) : 1f;
		leg.SmoothedLift += (target - leg.SmoothedLift) * alpha;
		return leg.SmoothedLift;
	}

	/// <summary>
	/// Ориентация стопы после солва. Идёт ПОСЛЕ two-bone IK и делает две вещи:
	///
	/// 1. ВОССТАНАВЛИВАЕТ анимационную ориентацию стопы в пространстве модели. Two-bone крутит
	///    колено, и стопа - жёсткий ребёнок голени - поворачивается вместе с ним; сама её позиция
	///    (цель IK) при этом верная, а ориентация - произвол солва. У человека это едва заметный
	///    наклон подошвы, у дигитиграда с длинной плюсной - лапа носком назад-вверх («голень как
	///    подошва»): в клипе Khronos Fox плюсна в опоре почти вертикальна, после солва без
	///    восстановления она лежала горизонтально (замерено кадрами legshot).
	/// 2. Поверх восстановленной - доворот «от вертикали к нормали» рельефа (по флагу): на плоском
	///    полу нормаль совпадает с вертикалью, и коррекция вырождается в единичную сама собой.
	///
	/// Восстановление - ПОЛНОЕ, вес только на довороте по нормали. Поворот плоскости ноги солвом
	/// не пропорционален весу (ozz разворачивает плоскость к полюсу даже при нулевой поправке), и
	/// взвешенная отмена оставляла лапу развёрнутой на малых весах: замерено свипом на перепаде -
	/// «ступня к морде» шла -0.98 → +0.87 по ползунку вместо ровного +1. Видимый результат при
	/// этом непрерывен по весу: на нуле солв не запускается вовсе и ориентация анимационная же.
	/// </summary>
	private static void AlignFeet(PreparedSkeleton skeleton, IReadOnlyList<FootIkLeg> legs,
		ReadOnlySpan<GroundSample> hits, ReadOnlySpan<Quaternion> footRotations,
		FootIkSettings settings, Matrix4x4 worldToModel, Vector3 up, Transform[] locals,
		Matrix4x4[] models)
	{
		float weight = Math.Clamp(settings.Weight, 0f, 1f);

		for (int i = 0; i < legs.Count; i++)
		{
			if (!hits[i].Hit)
			{
				// Без земли нога не решалась - и ориентацию ей никто не портил.
				continue;
			}

			var desired = footRotations[i];

			if (settings.AlignToNormal)
			{
				var normalModel = Vector3.TransformNormal(hits[i].Normal, worldToModel);
				if (normalModel.LengthSquared() > 1e-10f)
				{
					var correction = FromToRotation(up, Vector3.Normalize(normalModel));
					desired = Quaternion.Slerp(Quaternion.Identity, correction, weight) * desired;
				}
			}

			int foot = legs[i].FootJoint;
			int parent = skeleton.Parents[foot];
			var parentRotation = parent >= 0 ? Quaternion.CreateFromRotationMatrix(Orthonormal(models[parent]))
				: Quaternion.Identity;

			// Inverse(parent) * model - парная к захвату раскладка (см. коммент у footRotations).
			locals[foot].rotation = Quaternion.Normalize(Quaternion.Inverse(parentRotation) * desired);
		}
	}

	private static Matrix4x4 Orthonormal(in Matrix4x4 matrix)
	{
		var x = Vector3.Normalize(new Vector3(matrix.M11, matrix.M12, matrix.M13));
		var y = Vector3.Normalize(new Vector3(matrix.M21, matrix.M22, matrix.M23));
		var z = Vector3.Normalize(new Vector3(matrix.M31, matrix.M32, matrix.M33));

		return new Matrix4x4(
			x.X, x.Y, x.Z, 0f,
			y.X, y.Y, y.Z, 0f,
			z.X, z.Y, z.Z, 0f,
			0f, 0f, 0f, 1f);
	}

	private static Quaternion FromToRotation(Vector3 from, Vector3 to)
	{
		float dot = Vector3.Dot(from, to);
		if (dot > 0.999999f)
		{
			return Quaternion.Identity;
		}

		if (dot < -0.999999f)
		{
			var axis = Vector3.Cross(Vector3.UnitX, from);
			if (axis.LengthSquared() < 1e-8f)
			{
				axis = Vector3.Cross(Vector3.UnitY, from);
			}

			return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
		}

		return Quaternion.Normalize(new Quaternion(Vector3.Cross(from, to), 1f + dot));
	}
}
