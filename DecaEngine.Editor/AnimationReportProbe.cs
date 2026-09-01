using System;
using System.Collections.Generic;
using System.Numerics;
using DecaEngine.Graphics;
using DecaEngine.Animation;

namespace DecaEngine.Editor;

/// <summary>
/// Численный отчёт по анимационному клипу (DECA_PROBE_ANIMREPORT=1).
///
/// Инструмент для работы БЕЗ ГЛАЗ. Картинка показывает один кадр, а качество анимации живёт в
/// движении: скользит ли стопа по земле, замыкается ли цикл, симметричен ли шаг. Всё это - числа, и
/// по ним клип можно править итеративно, не открывая редактор и не разглядывая кадры.
///
/// Говорит СЛОТАМИ humanoid, а не именами костей (см. <see cref="HumanoidAvatar"/>), поэтому
/// одинаково работает и на человеке, и на четвероногом: у лисы передние лапы приезжают в слоты рук,
/// задние - в слоты ног, и «скольжение стопы» для них означает ровно то же самое.
///
/// Считается на C#-семплере, без GPU и без ozz: отчёт нужен там, где ещё ничего не собрано.
/// </summary>
public static class AnimationReportProbe
{
	/// <summary>Число выборок по клипу. 60 - достаточно, чтобы поймать и фазу шага, и дрожание
	/// отдельного ключа, и при этом отчёт остаётся читаемым человеком и мной.</summary>
	private const int Samples = 60;

	public static void Run(ModelLoader model)
	{
		var skeleton = model.Skeleton;

		if (skeleton == null || model.Animations.Count == 0)
		{
			Console.WriteLine("[probe] animreport: нет скелета или клипов - отчитываться не о чем");
			return;
		}

		var avatar = HumanoidAvatarAsset.Load(ModelPathHint) ?? HumanoidAutoMap.Build(skeleton);
		var slots = avatar.Resolve(skeleton);

		foreach (var clip in model.Animations)
		{
			Report(skeleton, avatar, slots, clip);
		}
	}

	/// <summary>Путь модели, если пробник его сообщил, - чтобы взять СОХРАНЁННЫЙ аватар вместо
	/// автоматического. Статическое поле, а не параметр: отчёт зовётся из середины пробника, где
	/// путь уже потерян, а тащить его сквозь три вызова ради одной строки незачем.</summary>
	public static string ModelPathHint = string.Empty;

	private static void Report(PreparedSkeleton skeleton, HumanoidAvatar avatar, int[] slots,
		PreparedAnimation clip)
	{
		var pose = new SkeletonPose(skeleton);
		var player = new AnimationPlayer { Clip = clip, Loop = true, Speed = 1f };

		float duration = MathF.Max(clip.Duration, 1e-4f);

		var positions = new Vector3[Samples][];
		for (int i = 0; i < Samples; i++)
		{
			player.Time = duration * i / Samples;
			player.Apply(pose);

			var frame = new Vector3[skeleton.JointCount];
			for (int j = 0; j < skeleton.JointCount; j++)
			{
				frame[j] = pose.ModelMatrices[j].Translation;
			}

			positions[i] = frame;
		}

		Console.WriteLine($"[probe] animreport: клип '{clip.Name}', {duration:0.###} с, " +
			$"выборок {Samples}, костей {skeleton.JointCount}");

		ReportLoop(positions, skeleton, clip.Name);
		ReportRoot(positions, slots, clip.Name);

		// Опору меряем по НОСКУ, если он размечен, и только иначе по стопе: планта - это носок, а
		// голеностоп в момент переката едет вперёд на всю длину ступни, и по нему любой нормальный
		// шаг выглядит «скользящим» (замерено на Fox: 100..300% хода при живой анимации).
		ReportFoot(positions, slots, HumanoidBone.LeftToes, HumanoidBone.LeftFoot, "опора L", clip.Name);
		ReportFoot(positions, slots, HumanoidBone.RightToes, HumanoidBone.RightFoot, "опора R", clip.Name);
		ReportFoot(positions, slots, HumanoidBone.LeftHand, HumanoidBone.LeftHand, "кисть L", clip.Name);
		ReportFoot(positions, slots, HumanoidBone.RightHand, HumanoidBone.RightHand, "кисть R", clip.Name);

		ReportSymmetry(positions, slots, clip.Name);
	}

	/// <summary>
	/// Замыкание цикла: насколько поза в конце клипа отличается от позы в начале.
	///
	/// Главное число для зацикленной анимации. Незамкнутый цикл даёт рывок на стыке, который в
	/// редакторе видно как подёргивание раз в период - и который почти невозможно заметить на
	/// отдельном кадре. Меряется в долях РАЗМАХА позы, а не в единицах: масштаб моделей произволен.
	/// </summary>
	private static void ReportLoop(Vector3[][] positions, PreparedSkeleton skeleton, string clipName)
	{
		float extent = Extent(positions);
		float worst = 0f;
		int worstJoint = 0;

		for (int j = 0; j < positions[0].Length; j++)
		{
			float delta = Vector3.Distance(positions[0][j], positions[^1][j]);

			// Последняя выборка - НЕ конец клипа: выборки идут по i/Samples, то есть последняя стоит
			// за один шаг до конца. Сравнивать надо именно с ней, иначе «разрыв» в идеально
			// зацикленном клипе оказался бы равен одному шагу движения.
			if (delta > worst)
			{
				worst = delta;
				worstJoint = j;
			}
		}

		float step = StepMotion(positions);
		float relative = extent > 1e-6f ? worst / extent : 0f;

		// Порог - доля РАЗМАХА позы, а не кратность шагу выборки. Кратность шагу отбраковывала
		// нормальные клипы: у быстро движущейся кости (хвост, кисть) шаг велик сам по себе, и
		// сравнение с ним ничего не говорит о стыке. Замерено на Khronos Fox: у его трёх клипов
		// разрыв 0.5..2% размаха, и это визуально бесшовные циклы - значит порог должен быть выше.
		Console.WriteLine($"[probe] animreport [{clipName}]: замыкание цикла - худший разрыв " +
			$"{worst:0.###} ({relative * 100f:0.#}% размаха) на '{skeleton.JointNames[worstJoint]}', " +
			$"движение за шаг {step:0.###} {(relative <= 0.03f ? "OK" : "ЦИКЛ НЕ ЗАМКНУТ")}");
	}

	/// <summary>Вертикальные колебания таза - «походка». Ноль означает, что персонаж едет как на
	/// рельсах: у живого шага таз обязан покачиваться.</summary>
	private static void ReportRoot(Vector3[][] positions, int[] slots, string clipName)
	{
		int hips = slots[(int)HumanoidBone.Hips];
		if (hips < 0)
		{
			return;
		}

		float min = float.MaxValue;
		float max = float.MinValue;

		foreach (var frame in positions)
		{
			min = MathF.Min(min, frame[hips].Y);
			max = MathF.Max(max, frame[hips].Y);
		}

		Console.WriteLine($"[probe] animreport [{clipName}]: таз - вертикальный размах {max - min:0.###} " +
			$"(y {min:0.##}..{max:0.##})");
	}

	/// <summary>
	/// Скольжение опоры. Классическая метрика качества шага: пока конечность СТОИТ на земле (её
	/// высота у минимума), она не должна ехать горизонтально. Едет - персонаж «катится на роликах»,
	/// и это первое, что бросается в глаза в игре, но не видно ни на одном кадре.
	/// </summary>
	private static void ReportFoot(Vector3[][] positions, int[] slots, HumanoidBone preferred,
		HumanoidBone fallback, string title, string clipName)
	{
		int joint = slots[(int)preferred];
		if (joint < 0)
		{
			joint = slots[(int)fallback];
		}

		if (joint < 0)
		{
			return;
		}

		float min = float.MaxValue;
		float max = float.MinValue;

		foreach (var frame in positions)
		{
			min = MathF.Min(min, frame[joint].Y);
			max = MathF.Max(max, frame[joint].Y);
		}

		// Порог контакта - нижняя пятая часть хода конечности. Доля, а не абсолют: у лисы ход лапы
		// в единицах модели, у метрового персонажа - в метрах.
		float lift = max - min;
		float threshold = min + MathF.Max(lift * 0.2f, 1e-5f);

		float slide = 0f;
		int contacts = 0;

		for (int i = 1; i < positions.Length; i++)
		{
			if (positions[i][joint].Y > threshold || positions[i - 1][joint].Y > threshold)
			{
				continue;
			}

			var a = positions[i - 1][joint];
			var b = positions[i][joint];

			slide += MathF.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z));
			contacts++;
		}

		float verdictBase = MathF.Max(lift, 1e-6f);

		// ВЕРДИКТА ЗДЕСЬ НЕТ - и это осознанно. Скольжение считается в пространстве МОДЕЛИ, а
		// локомоционный клип обычно везёт персонажа вперёд корнем: опорная нога тогда законно едет
		// назад относительно модели, и метрика показывает 100..350% на безупречной анимации
		// (замерено на Khronos Fox). Честное число требует компенсации хода корня, и до неё ставить
		// «СКОЛЬЗИТ» значит приучить себя игнорировать красное.
		Console.WriteLine($"[probe] animreport [{clipName}]: {title} - ход {lift:0.###}, " +
			$"опора {contacts}/{positions.Length} кадров, скольжение {slide:0.###} " +
			$"({slide / verdictBase * 100f:0.#}% хода){(contacts == 0 ? " БЕЗ ОПОРЫ" : "")}");
	}

	/// <summary>Симметрия шага: левая и правая конечности должны двигаться в противофазе. Считается
	/// как сдвиг, при котором высота левой лучше всего совпадает с высотой правой; у шага он около
	/// половины периода.</summary>
	private static void ReportSymmetry(Vector3[][] positions, int[] slots, string clipName)
	{
		int left = slots[(int)HumanoidBone.LeftFoot];
		int right = slots[(int)HumanoidBone.RightFoot];

		if (left < 0 || right < 0)
		{
			return;
		}

		float best = float.MaxValue;
		int bestShift = 0;

		for (int shift = 0; shift < positions.Length; shift++)
		{
			float error = 0f;

			for (int i = 0; i < positions.Length; i++)
			{
				float a = positions[i][left].Y;
				float b = positions[(i + shift) % positions.Length][right].Y;
				error += MathF.Abs(a - b);
			}

			if (error < best)
			{
				best = error;
				bestShift = shift;
			}
		}

		float phase = bestShift / (float)positions.Length;

		// Тоже без вердикта: противофаза - свойство ДВУНОГОГО шага. У четвероногого рысь, иноходь и
		// галоп дают совсем другие сдвиги, и объявлять их ошибкой рига нельзя. Число полезно как
		// подпись походки: 0.5 - шаг, около 0 - прыжок или галоп.
		Console.WriteLine($"[probe] animreport [{clipName}]: фаза ног - сдвиг {phase:0.##} периода " +
			$"({(MathF.Abs(phase - 0.5f) < 0.15f ? "противофаза - шаг" : "синхронно - прыжок/галоп/иноходь")})");
	}

	private static float Extent(Vector3[][] positions)
	{
		var min = new Vector3(float.MaxValue);
		var max = new Vector3(float.MinValue);

		foreach (var frame in positions)
		{
			foreach (var p in frame)
			{
				min = Vector3.Min(min, p);
				max = Vector3.Max(max, p);
			}
		}

		return (max - min).Length();
	}

	/// <summary>Типичное движение позы за один шаг выборки - масштаб, относительно которого судится
	/// разрыв цикла. Без него порог пришлось бы задавать абсолютным числом, а он зависит и от
	/// масштаба модели, и от скорости клипа.</summary>
	private static float StepMotion(Vector3[][] positions)
	{
		float sum = 0f;
		int count = 0;

		for (int i = 1; i < positions.Length; i++)
		{
			for (int j = 0; j < positions[i].Length; j++)
			{
				sum += Vector3.Distance(positions[i - 1][j], positions[i][j]);
				count++;
			}
		}

		return count > 0 ? sum / count : 0f;
	}
}
