using System;
using System.Numerics;
using DecaEngine.Core;

namespace DecaEngine.Animation;

/// <summary>
/// Поза скелета в трёх представлениях сразу: локальные TRS, модельные матрицы и палитра
/// скиннинг-матриц. Все три нужны одновременно и намеренно живут в одном объекте:
///
/// - <see cref="Locals"/> - то, что пишет семплер клипов и правит процедурный слой (IK, spring
///   bones работают именно в локальном пространстве родителя);
/// - <see cref="ModelMatrices"/> - то, что читают IK и рэгдолл, чтобы узнать, ГДЕ кость в мире
///   модели, и куда физика пишет результат;
/// - <see cref="SkinMatrices"/> - то, что уезжает в GPU.
///
/// Массивы переиспользуются между кадрами: поза персонажа считается каждый кадр, и аллокация трёх
/// массивов на 200 костей в кадре - это мусор, который потом собирают ровно в тот момент, когда
/// кадр не должен подтормаживать.
/// </summary>
public sealed class SkeletonPose
{
	public readonly PreparedSkeleton Skeleton;

	/// <summary>Локальные TRS относительно родителя. Стартуют с bind-позы.</summary>
	public readonly Transform[] Locals;

	/// <summary>Матрицы джойнтов в пространстве модели (не мира: мировое размещение добавляет
	/// трансформ сущности). Валидны после <see cref="ComputeModelMatrices"/>.</summary>
	public readonly Matrix4x4[] ModelMatrices;

	/// <summary>Палитра для скиннинга: <c>InverseBind * Model</c>. Валидна после
	/// <see cref="ComputeSkinMatrices"/>.</summary>
	public readonly Matrix4x4[] SkinMatrices;

	public SkeletonPose(PreparedSkeleton skeleton)
	{
		Skeleton = skeleton ?? throw new ArgumentNullException(nameof(skeleton));

		int count = skeleton.JointCount;
		Locals = new Transform[count];
		ModelMatrices = new Matrix4x4[count];
		SkinMatrices = new Matrix4x4[count];

		ResetToBind();
	}

	public void ResetToBind() => Skeleton.BindLocals.AsSpan().CopyTo(Locals);

	/// <summary>
	/// Локальные TRS -> модельные матрицы, одним проходом по массиву БЕЗ рекурсии: джойнты
	/// топологически упорядочены (см. <see cref="PreparedSkeleton"/>), поэтому к моменту обработки
	/// ребёнка родитель уже посчитан. Это и есть главная причина, по которой порядок джойнтов -
	/// контракт формата, а не деталь импорта.
	/// </summary>
	public void ComputeModelMatrices()
	{
		var parents = Skeleton.Parents;

		for (int i = 0; i < Locals.Length; i++)
		{
			ref var local = ref Locals[i];
			var matrix = Matrix4x4.CreateScale(local.scale)
				* Matrix4x4.CreateFromQuaternion(local.rotation)
				* Matrix4x4.CreateTranslation(local.position);

			int parent = parents[i];
			ModelMatrices[i] = parent < 0 ? matrix : matrix * ModelMatrices[parent];
		}
	}

	/// <summary>Палитра скиннинга. Порядок множителей - под строчную конвенцию движка (вершина
	/// умножается СЛЕВА: <c>mul(pos, matrix)</c> в HLSL, см. UnlitInstancedVS.hlsl): вершина сначала
	/// уходит из bind-позы в пространство джойнта, потом - в анимированное пространство модели.</summary>
	public void ComputeSkinMatrices()
	{
		var inverseBind = Skeleton.InverseBind;

		for (int i = 0; i < SkinMatrices.Length; i++)
		{
			SkinMatrices[i] = inverseBind[i] * ModelMatrices[i];
		}
	}

	/// <summary>Обе стадии разом - обычный порядок вызова, когда процедурному слою между ними
	/// вклиниваться не нужно.</summary>
	public void Finish()
	{
		ComputeModelMatrices();
		ComputeSkinMatrices();
	}
}

/// <summary>
/// Позиция «читающей головки» по ключам одной дорожки: индекс ключа, слева от которого шло время в
/// прошлом кадре. Без него каждый канал каждой кости каждый кадр делает бинарный поиск по своим
/// ключам - на риге в 200 костей с тремя каналами это 600 поисков в кадре на персонажа, при том что
/// воспроизведение почти всегда идёт ВПЕРЁД и нужный ключ - тот же или следующий.
///
/// Курсор - подсказка, а не состояние: неверный курсор даёт лишний бинарный поиск, но не неверную
/// позу, поэтому его можно свободно ронять при перемотке, смене клипа и переиспользовании плеера.
/// </summary>
public struct ClipCursor
{
	public int Translation;
	public int Rotation;
	public int Scale;

	public void Reset() => Translation = Rotation = Scale = 0;
}

/// <summary>Семплер клипов: клип + время -> локальные TRS. Без состояния - всё, что живёт между
/// кадрами, лежит в <see cref="AnimationPlayer"/>.</summary>
public static class ClipSampler
{
	/// <summary>
	/// Семплирует клип в локальные TRS позы. Каналы независимы: дорожка, не трогающая, скажем,
	/// масштаб, оставляет его из bind-позы - именно поэтому bind-поза здесь ОБЯЗАТЕЛЬНА как
	/// источник значений по умолчанию, а не просто «начальное состояние».
	/// </summary>
	/// <param name="cursors">Подсказки по каждой дорожке, длиной со скелет; можно передать пустой
	/// спан - тогда каждый канал ищет ключ бинарным поиском (см. <see cref="ClipCursor"/>).</param>
	public static void Sample(PreparedAnimation clip, float time, SkeletonPose pose, Span<ClipCursor> cursors)
	{
		var skeleton = pose.Skeleton;
		int jointCount = Math.Min(skeleton.JointCount, clip.Tracks.Length);

		for (int i = 0; i < jointCount; i++)
		{
			var track = clip.Tracks[i];
			ref var local = ref pose.Locals[i];
			var bind = skeleton.BindLocals[i];

			if (track.IsEmpty)
			{
				local = bind;
				continue;
			}

			ref var cursor = ref i < cursors.Length ? ref cursors[i] : ref Unused;

			local.position = track.TranslationTimes.Length > 0
				? SampleVector(track.TranslationTimes, track.Translations, time, ref cursor.Translation)
				: bind.position;

			local.rotation = track.RotationTimes.Length > 0
				? SampleQuaternion(track.RotationTimes, track.Rotations, time, ref cursor.Rotation)
				: bind.rotation;

			local.scale = track.ScaleTimes.Length > 0
				? SampleVector(track.ScaleTimes, track.Scales, time, ref cursor.Scale)
				: bind.scale;
		}

		// Джойнты за пределами дорожек клипа (клип из другого рига, скелет длиннее) остаются в
		// bind-позе, а не в позе прошлого кадра: иначе смена клипа оставляла бы хвост скелета
		// висеть в позе предыдущего.
		for (int i = jointCount; i < skeleton.JointCount; i++)
		{
			pose.Locals[i] = skeleton.BindLocals[i];
		}
	}

	/// <summary>Свалка для курсора джойнта, которому вызывающий его не выделил. Существует ради
	/// <c>ref</c>-тернарника выше: ветка без курсора обязана вернуть ЧТО-ТО по ссылке, а заводить
	/// локальную переменную в цикле нельзя - ref на неё живёт дольше витка.</summary>
	[ThreadStatic]
	private static ClipCursor Unused;

	/// <summary>
	/// Линейная интерполяция между соседними ключами. Время за пределами дорожки зажимается к
	/// крайнему ключу, а не заворачивается: заворачивать - дело плеера, который один знает, зациклен
	/// клип или нет, и дорожки внутри клипа кончаются в разное время.
	/// </summary>
	private static Vector3 SampleVector(float[] times, Vector3[] values, float time, ref int cursor)
	{
		int index = FindKey(times, time, ref cursor);
		if (index < 0)
		{
			return values[0];
		}

		if (index >= times.Length - 1)
		{
			return values[^1];
		}

		float t = Fraction(times[index], times[index + 1], time);
		return Vector3.Lerp(values[index], values[index + 1], t);
	}

	private static Quaternion SampleQuaternion(float[] times, Quaternion[] values, float time, ref int cursor)
	{
		int index = FindKey(times, time, ref cursor);
		if (index < 0)
		{
			return values[0];
		}

		if (index >= times.Length - 1)
		{
			return values[^1];
		}

		var from = values[index];
		var to = values[index + 1];

		// Кратчайшая дуга: q и -q - один поворот, но Slerp между ними пойдёт «длинным путём» через
		// пол-оборота. Экспортёры знак не нормализуют, и без этой проверки кости раз в несколько
		// кадров прокручиваются вокруг себя - характерный «дёрг» на середине клипа.
		if (Quaternion.Dot(from, to) < 0f)
		{
			to = -to;
		}

		float t = Fraction(times[index], times[index + 1], time);
		return Quaternion.Normalize(Quaternion.Slerp(from, to, t));
	}

	private static float Fraction(float from, float to, float time)
	{
		float span = to - from;
		// Ключи с совпадающим временем экспортёры ставят намеренно - это ступенька (мгновенная
		// смена позы). Деление на ноль дало бы NaN, который расползётся по всей цепочке костей.
		return span > 1e-6f ? Math.Clamp((time - from) / span, 0f, 1f) : 0f;
	}

	/// <summary>
	/// Индекс ключа, слева от которого лежит <paramref name="time"/>; -1, если время раньше первого
	/// ключа. Начинает от курсора и проверяет соседей - при обычном воспроизведении вперёд это
	/// одно-два сравнения; на промахе падает на бинарный поиск.
	/// </summary>
	private static int FindKey(float[] times, float time, ref int cursor)
	{
		if (time < times[0])
		{
			cursor = 0;
			return -1;
		}

		int hint = Math.Clamp(cursor, 0, times.Length - 1);

		if (times[hint] <= time)
		{
			if (hint == times.Length - 1 || time < times[hint + 1])
			{
				cursor = hint;
				return hint;
			}

			// Следующий интервал - самый частый случай при воспроизведении вперёд.
			if (hint + 1 == times.Length - 1 || (hint + 2 < times.Length && time < times[hint + 2]))
			{
				cursor = hint + 1;
				return hint + 1;
			}
		}

		int low = 0;
		int high = times.Length - 1;
		while (low < high)
		{
			int mid = (low + high + 1) / 2;
			if (times[mid] <= time)
			{
				low = mid;
			}
			else
			{
				high = mid - 1;
			}
		}

		cursor = low;
		return low;
	}
}

/// <summary>
/// Проигрыватель одного клипа: время, скорость, зацикливание плюс курсоры дорожек. Один плеер на
/// сущность; блендинг двух клипов появится вместе с ozz (см. задачу шима) - здесь намеренно один
/// клип, чтобы фундамент можно было проверить до нативной части.
/// </summary>
public sealed class AnimationPlayer
{
	private ClipCursor[] _cursors = [];
	private PreparedAnimation _clip;

	public PreparedAnimation Clip
	{
		get => _clip;
		set
		{
			if (ReferenceEquals(_clip, value))
			{
				return;
			}

			_clip = value;
			Time = 0f;

			// Курсоры прошлого клипа указывают в чужие дорожки. Формально это безопасно (курсор -
			// лишь подсказка, см. ClipCursor), но первый кадр нового клипа тогда весь уходит в
			// промахи, а обнулить массив дешевле, чем ловить их.
			ResetCursors();
		}
	}

	public float Time;
	public float Speed = 1f;
	public bool Loop = true;

	/// <summary>Клип доиграл до конца и не зациклен. Потребитель (стейт-машина, скрипт) читает флаг
	/// и решает, что дальше; сам плеер просто замирает на последнем кадре.</summary>
	public bool Finished { get; private set; }

	public void Advance(float deltaSeconds)
	{
		if (_clip == null || _clip.Duration <= 0f)
		{
			return;
		}

		Time += deltaSeconds * Speed;

		if (Loop)
		{
			// Заворот через floor, а не через %: у отрицательной скорости остаток в C# отрицательный,
			// и время уходило бы в минус вместо заворота к концу клипа.
			Time -= _clip.Duration * MathF.Floor(Time / _clip.Duration);
			Finished = false;
		}
		else if (Time >= _clip.Duration)
		{
			Time = _clip.Duration;
			Finished = true;
		}
		else if (Time < 0f)
		{
			Time = 0f;
			Finished = true;
		}
	}

	/// <summary>Семплирует текущий клип в позу и досчитывает модельные матрицы и палитру. Если клипа
	/// нет - поза остаётся bind-позой: скиннед-меш без анимации обязан рисоваться, а не схлопываться.</summary>
	public void Apply(SkeletonPose pose)
	{
		if (_clip == null)
		{
			pose.ResetToBind();
			pose.Finish();
			return;
		}

		if (_cursors.Length < pose.Skeleton.JointCount)
		{
			_cursors = new ClipCursor[pose.Skeleton.JointCount];
		}

		ClipSampler.Sample(_clip, Time, pose, _cursors);
		pose.Finish();
	}

	/// <summary>Перемотка: время ставится напрямую, курсоры сбрасываются - прыжок назад по времени
	/// как раз тот случай, где подсказка вперёд бесполезна.</summary>
	public void Seek(float time)
	{
		Time = time;
		Finished = false;
		ResetCursors();
	}

	private void ResetCursors() => Array.Clear(_cursors);
}
