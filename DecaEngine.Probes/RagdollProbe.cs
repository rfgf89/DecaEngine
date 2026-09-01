using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using DecaEngine.Graphics;
using DecaEngine.Physics;
using DecaEngine.Animation;

namespace DecaEngine.Probes;

/// <summary>
/// Проверка рэгдолла на реальном риге. Три вопроса, и все три - «молчаливые»: кинематический
/// рэгдолл может не следовать за анимацией (и персонаж поедет отдельно от своей физики), суставы
/// могут расходиться (конечности отрываются), а динамический рэгдолл - взорваться, разогнав тела в
/// бесконечность. Ни одно из трёх не даёт исключения; всё это видно только числами.
/// </summary>
public static class RagdollProbe
{
	public static void Run(PreparedSkeleton skeleton, OzzPose pose, Matrix4x4[] models)
	{
		var description = BuildDescription(skeleton);
		if (description.Count < 4)
		{
			Console.WriteLine("[probe] ragdoll: риг не опознан - пропущено");
			return;
		}

		if (!pose.LocalToModel() || !pose.ReadModelMatrices(models))
		{
			return;
		}

		using var world = new PhysicsWorld(new Vector3(0f, -9.81f, 0f));

		var floor = world.AddBox(new Vector3(400f, 4f, 400f));
		world.AddStatic(new RigidPose(new Vector3(0f, -2f, 0f)), floor);

		// Мировые матрицы джойнтов = модельные: трансформ сущности единичный, персонаж и так стоит
		// над полом (bind-поза лисы держит стопы на y~23).
		var jointWorld = (Matrix4x4[])models.Clone();
		MarkHinges(description, skeleton, jointWorld);
		var ragdoll = Ragdoll.Build(world, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(description),
			jointWorld);

		ProbeKinematicTracking(world, ragdoll, jointWorld, models);
		ProbeHighFpsTracking(world, ragdoll, jointWorld, models);
		ProbeDynamicFall(world, ragdoll, jointWorld, skeleton);
		ProbeKneeHinge(skeleton, models);
		ProbeSceneScale(skeleton, models);
	}

	/// <summary>Те же шарниры, что ставит редактор (см. AnimationDriver.MarkHingeBones), по именам
	/// костей лисы: проба, гоняющая рэгдолл без шарниров, проверяла бы не тот рэгдолл, который живёт
	/// в сцене.</summary>
	private static void MarkHinges(List<RagdollBoneDesc> description, PreparedSkeleton skeleton,
		Matrix4x4[] jointWorld)
	{
		foreach (string name in new[]
			{ "b_LeftLeg02_016", "b_RightLeg02_020", "b_LeftForeArm_010", "b_RightForeArm_07" })
		{
			int joint = skeleton.FindJoint(name);
			if (joint < 0)
			{
				continue;
			}

			for (int i = 0; i < description.Count; i++)
			{
				var bone = description[i];
				if (bone.Joint != joint || bone.Parent < 0 || bone.ChildJoint < 0)
				{
					continue;
				}

				Ragdoll.MarkHinge(ref bone,
					jointWorld[description[bone.Parent].Joint].Translation,
					jointWorld[bone.Joint].Translation,
					jointWorld[bone.ChildJoint].Translation);

				description[i] = bone;
				break;
			}
		}
	}

	/// <summary>
	/// Шарнир колена - ПАРОЙ: голени полсекунды принудительно выкручивают к прямой и дальше, в
	/// обратный сгиб. С шарниром знаковый угол сгиба обязан остаться у предела разгибания
	/// (положительным, около пяти градусов запаса до прямой), без шарнира - уйти в уверенный минус:
	/// конус 120° разгибание через прямую разрешает. Одно число с шарниром ничего не доказывает -
	/// «не вывернулось» бывает и у сустава, который просто никто не выкручивал.
	/// Без гравитации: меряется сустав, а не падение.
	/// </summary>
	private static void ProbeKneeHinge(PreparedSkeleton skeleton, Matrix4x4[] models)
	{
		int knee = skeleton.FindJoint("b_LeftLeg02_016");
		if (knee < 0)
		{
			return;
		}

		float hingedBend = float.NaN;
		float freeBend = float.NaN;

		foreach (bool hinged in new[] { true, false })
		{
			var description = BuildDescription(skeleton);
			if (description.Count < 4)
			{
				return;
			}

			var jointWorld = (Matrix4x4[])models.Clone();
			if (hinged)
			{
				MarkHinges(description, skeleton, jointWorld);
			}

			int kneeBone = -1;
			int upperJoint = -1;
			int footJoint = -1;

			for (int i = 0; i < description.Count; i++)
			{
				if (description[i].Joint == knee && description[i].Parent >= 0)
				{
					kneeBone = i;
					upperJoint = description[description[i].Parent].Joint;
					footJoint = description[i].ChildJoint;
				}
			}

			if (kneeBone < 0 || footJoint < 0)
			{
				return;
			}

			using var world = new PhysicsWorld(Vector3.Zero);
			var ragdoll = Ragdoll.Build(world,
				System.Runtime.InteropServices.CollectionsMarshal.AsSpan(description), jointWorld);
			ragdoll.SetAnimationDriven(false);

			var assemblyA = jointWorld[knee].Translation - jointWorld[upperJoint].Translation;
			var assemblyB = jointWorld[footJoint].Translation - jointWorld[knee].Translation;
			var axis = Vector3.Normalize(Vector3.Cross(assemblyA, assemblyB));

			var body = world.Simulation.Bodies[ragdoll.BodyOf(kneeBone)];

			// Полсекунды, НЕ дольше: знаковая метрика меряет угол в диапазоне ±180°, и свободное
			// колено, выкручиваемое дольше, прокручивается на почти полный оборот - знак замыкается
			// обратно в плюс, и вывернутый сустав отчитывается «согнутым правильно».
			for (int i = 0; i < 60; i++)
			{
				// Минус вокруг оси сборки - разгибание: от позы сборки к прямой и дальше в выворот.
				body.Velocity.Angular = -axis * 6f;
				body.Velocity.Linear = Vector3.Zero;
				body.Awake = true;

				world.Update(1f / 120f);
			}

			var read = (Matrix4x4[])jointWorld.Clone();
			ragdoll.ReadPose(read);

			var a = read[knee].Translation - read[upperJoint].Translation;
			var b = read[footJoint].Translation - read[knee].Translation;
			float bend = MathF.Atan2(Vector3.Cross(a, b).Length(), Vector3.Dot(a, b));
			float signedBend = bend * MathF.Sign(Vector3.Dot(Vector3.Cross(a, b), axis));

			if (hinged)
			{
				hingedBend = signedBend;
			}
			else
			{
				freeBend = signedBend;
			}

			ragdoll.Destroy();
		}

		// Граница - ЗНАК: суть пары «прошло колено через прямую или нет», а не глубина выворота
		// (её задаёт борьба принудительной скорости с пружинами, и она деталь постановки). С
		// шарниром сгиб обязан остаться положительным, без - уйти в минус: без второй ветки «не
		// вывернулось» неотличимо от «нечем было выворачивать».
		bool hingedOk = hingedBend > 0f;
		bool freeInverted = freeBend < -5f * MathF.PI / 180f;

		Console.WriteLine($"[probe] ragdoll: шарнир колена - выкручивание к прямой: с шарниром " +
			$"{hingedBend * 180f / MathF.PI:0.#}°, без {freeBend * 180f / MathF.PI:0.#}° " +
			$"{(hingedOk && freeInverted ? "ДЕРЖИТ OK" : "НЕ ДЕРЖИТ/ПАРА НЕ РАЗОШЛАСЬ")}");
	}

	/// <summary>
	/// Тот же рэгдолл, но в МАСШТАБЕ СЦЕНЫ.
	///
	/// Всё остальное здесь считает лису в её собственных единицах (габарит ~160), а в демо-сцене она
	/// стоит с масштабом сущности 0.01, то есть её кости - это капсулы радиусом 2 сантиметра, и
	/// падают они в поле 9.81 м/с². Относительно собственного размера это в сто раз более резкое
	/// падение, чем в модельных единицах, а настройки суставов (жёсткости пружин, пороги засыпания,
	/// спекулятивная маржа) заданы АБСОЛЮТНЫМИ числами и вместе с масштабом не едут.
	///
	/// Проверка сравнивает два масштаба между собой, а не судит один: «рэгдолл успокоился» само по
	/// себе ничего не значит без второй точки, потому что порог успокоения зависит от размера.
	/// </summary>
	private static void ProbeSceneScale(PreparedSkeleton skeleton, Matrix4x4[] models)
	{
		// Третий прогон - НАМЕРЕННО без предела скручивания. Без него метрика слепа: «50° при пределе
		// 50°» одинаково выглядит и у работающего ограничителя, и у рэгдолла, который просто некуда
		// было скручивать. Пара с выключенным пределом показывает, что мерить есть что.
		foreach (var (scale, twistLimited) in new[] { (1f, true), (0.01f, true), (0.01f, false) })
		{
			var description = BuildDescription(skeleton);
			if (description.Count < 4)
			{
				return;
			}

			for (int i = 0; i < description.Count; i++)
			{
				var bone = description[i];
				bone.Radius *= scale;
				bone.Length *= scale;
				bone.TwistLimitAngle = twistLimited ? bone.TwistLimitAngle : 0f;
				description[i] = bone;
			}

			using var world = new PhysicsWorld(new Vector3(0f, -9.81f, 0f));

			var floor = world.AddBox(new Vector3(400f * scale, 4f * scale, 400f * scale));
			world.AddStatic(new RigidPose(new Vector3(0f, -2f * scale, 0f)), floor);

			var jointWorld = new Matrix4x4[models.Length];
			for (int i = 0; i < models.Length; i++)
			{
				jointWorld[i] = models[i] * Matrix4x4.CreateScale(scale);
			}

			var ragdoll = Ragdoll.Build(world,
				System.Runtime.InteropServices.CollectionsMarshal.AsSpan(description), jointWorld);
			ragdoll.SetAnimationDriven(false);

			var (restRelative, restAxis) = CaptureRest(world, ragdoll, description);

			float initialSpread = Spread(world, ragdoll);
			float impactSpeed = 0f;

			for (float simulated = 0f; simulated < 6f; simulated += 1f / 60f)
			{
				world.Update(1f / 60f);

				if (simulated >= 3f && impactSpeed == 0f)
				{
					impactSpeed = MaxSpeed(world, ragdoll);
				}
			}

			float finalSpread = Spread(world, ragdoll);
			float finalSpeed = MaxSpeed(world, ragdoll);
			int overlaps = CountSelfOverlaps(world, ragdoll, description);
			float worstTwist = WorstTwist(world, ragdoll, description, restRelative, restAxis);

			// Разлёт меряется ОТНОСИТЕЛЬНО начального габарита: у рэгдолла, оставшегося рэгдоллом,
			// он около единицы, а у разлетевшегося растёт в разы. Абсолютное число тут бессмысленно -
			// оно и есть масштаб, который проверяется.
			float growth = initialSpread > 1e-6f ? finalSpread / initialSpread : 0f;
			bool intact = growth < 1.5f && finalSpeed <= impactSpeed;

			float twistDegrees = worstTwist * 180f / MathF.PI;
			string twistVerdict = twistLimited
				? twistDegrees <= TwistLimitDegrees + TwistToleranceDegrees ? "OK" : "ВЫВОРАЧИВАЕТСЯ"
				: twistDegrees > TwistLimitDegrees + TwistToleranceDegrees
					? "(без предела - и есть что ограничивать)"
					: "(без предела, но и так не крутит - ПРОВЕРКА СЛЕПАЯ)";

			Console.WriteLine($"[probe] ragdoll: масштаб {scale}, скручивание " +
				$"{(twistLimited ? $"±{TwistLimitDegrees}°" : "без предела")} - габарит " +
				$"{initialSpread:0.###} -> {finalSpread:0.###} (×{growth:0.##}), скорость " +
				$"{impactSpeed:0.###} -> {finalSpeed:0.###} {(intact ? "OK" : "РАЗЛЕТЕЛСЯ")}, " +
				$"самопересечений {overlaps} {(overlaps == 0 ? "OK" : "СКЛАДЫВАЕТСЯ СКВОЗЬ СЕБЯ")}, " +
				$"худшее скручивание {twistDegrees:0.#}° {twistVerdict}");
		}
	}

	/// <summary>Предел скручивания, который проба ставит рэгдоллу (см. BuildDescription), градусы.</summary>
	private const float TwistLimitDegrees = 50f;

	/// <summary>
	/// Допуск сверх предела. Не придирка: <see cref="TwistLimit"/> - это ПРУЖИНА, а не стенка. Она
	/// возвращает сустав в разрешённый диапазон с конечной жёсткостью, и на ударе о пол сустав
	/// законно проскакивает предел на несколько градусов. Ноль здесь означал бы проверку, падающую
	/// на исправном ограничителе.
	/// </summary>
	private const float TwistToleranceDegrees = 15f;

	/// <summary>
	/// Худшее скручивание кости вокруг собственной длинной оси относительно родителя.
	///
	/// Меряется swing-twist разложением: относительный поворот раскладывается на «куда отклонилась»
	/// и «на сколько провернулась вокруг себя», и берётся вторая часть. Именно её конус
	/// (<see cref="SwingLimit"/>) не ограничивает вовсе - кость может провернуться на любой угол,
	/// оставаясь внутри разрешённого конуса, и на картинке это выглядит как вывернутая лапа при
	/// формально соблюдённых ограничениях.
	/// </summary>
	private static float WorstTwist(PhysicsWorld world, Ragdoll ragdoll, List<RagdollBoneDesc> description,
		Quaternion[] restRelative, Vector3[] restAxis)
	{
		float worst = 0f;

		for (int i = 0; i < ragdoll.BoneCount; i++)
		{
			if (description[i].Parent < 0)
			{
				continue;
			}

			var relative = RelativeRotation(world, ragdoll, description[i].Parent, i);

			// Поворот ОТ ПОЗЫ СБОРКИ, а не от нуля. Первая версия меряла от нуля и показывала 117°
			// на исправном ограничителе: в позе сборки кость уже повёрнута относительно родителя, и
			// этот постоянный сдвиг целиком уезжал в «скручивание».
			var delta = Quaternion.Concatenate(Quaternion.Conjugate(restRelative[i]), relative);

			// Swing-twist вокруг ФИКСИРОВАННОЙ оси - оси кости в позе сборки. Брать ось из текущего
			// поворота нельзя: разложение тогда определено относительно самого себя, и «скручивание»
			// смешивается с отклонением.
			var axis = restAxis[i];
			float projection = delta.X * axis.X + delta.Y * axis.Y + delta.Z * axis.Z;

			float twist = 2f * MathF.Atan2(projection, delta.W);

			// Приведение в (-π, π]: 2*atan2 даёт диапазон вдвое шире, и поворот на 350° иначе
			// читался бы как 350°, а не как -10°.
			twist = MathF.IEEERemainder(twist, MathF.Tau);

			worst = MathF.Max(worst, MathF.Abs(twist));
		}

		return worst;
	}

	/// <summary>Поворот кости в пространстве её родителя.</summary>
	private static Quaternion RelativeRotation(PhysicsWorld world, Ragdoll ragdoll, int parent, int child)
	{
		var parentPose = world.Simulation.Bodies[ragdoll.BodyOf(parent)].Pose;
		var childPose = world.Simulation.Bodies[ragdoll.BodyOf(child)].Pose;

		return Quaternion.Concatenate(childPose.Orientation, Quaternion.Conjugate(parentPose.Orientation));
	}

	/// <summary>Снимок позы сборки: относительные повороты и оси костей. Точка отсчёта для замера
	/// скручивания - предел в связи задан ОТ НЕЁ же.</summary>
	private static (Quaternion[] Relative, Vector3[] Axis) CaptureRest(PhysicsWorld world, Ragdoll ragdoll,
		List<RagdollBoneDesc> description)
	{
		var relative = new Quaternion[ragdoll.BoneCount];
		var axis = new Vector3[ragdoll.BoneCount];

		for (int i = 0; i < ragdoll.BoneCount; i++)
		{
			relative[i] = Quaternion.Identity;
			axis[i] = Vector3.UnitY;

			if (description[i].Parent < 0)
			{
				continue;
			}

			relative[i] = RelativeRotation(world, ragdoll, description[i].Parent, i);

			// Длинная ось капсулы ребёнка, выраженная в пространстве родителя.
			var bone = Vector3.Transform(Vector3.UnitY, relative[i]);
			float length = bone.Length();
			axis[i] = length > 1e-5f ? bone / length : Vector3.UnitY;
		}

		return (relative, axis);
	}

	/// <summary>
	/// Сколько НЕСМЕЖНЫХ костей проникают друг в друга глубже допуска.
	///
	/// Это и есть числовой ответ на «должна ли кукла складываться сквозь себя». Смежные (родитель и
	/// ребёнок) пересекаются по построению - у них общий сустав, - и они не считаются. А голова,
	/// лежащая ВНУТРИ туловища, или лапа, прошедшая сквозь бок, - это ровно то, что видно на
	/// картинке как «свернулся в узел», и никакой другой метрикой это не ловится: габарит у
	/// сложившегося персонажа как раз маленький, скорости нулевые, всё «успокоилось».
	///
	/// Расстояние - между ОТРЕЗКАМИ капсул, а не между центрами: две длинные кости, лежащие
	/// параллельно бок о бок, по центрам далеки, а пересекаются всей длиной.
	/// </summary>
	private static int CountSelfOverlaps(PhysicsWorld world, Ragdoll ragdoll,
		List<RagdollBoneDesc> description)
	{
		int count = 0;

		for (int i = 0; i < ragdoll.BoneCount; i++)
		{
			for (int j = i + 1; j < ragdoll.BoneCount; j++)
			{
				if (description[i].Parent == j || description[j].Parent == i)
				{
					continue;
				}

				var (a0, a1, ra) = Segment(world, ragdoll, description, i);
				var (b0, b1, rb) = Segment(world, ragdoll, description, j);

				// Допуск в четверть суммы радиусов: контакты Bepu живут с небольшим постоянным
				// проникновением (contact softness), и нулевой допуск считал бы нормальное касание
				// лежащих рядом костей за проникновение.
				float allowed = (ra + rb) * 0.75f;

				if (SegmentDistance(a0, a1, b0, b1) < allowed)
				{
					count++;
				}
			}
		}

		return count;
	}

	/// <summary>Отрезок оси капсулы кости в мире плюс её радиус.</summary>
	private static (Vector3 A, Vector3 B, float Radius) Segment(PhysicsWorld world, Ragdoll ragdoll,
		List<RagdollBoneDesc> description, int bone)
	{
		var pose = ragdoll.PoseOf(bone);
		var shape = ragdoll.ShapeOf(bone);

		float radius = description[bone].Radius;
		float halfLength = 0f;

		if (shape.Exists && shape.Type == BepuPhysics.Collidables.Capsule.Id)
		{
			var capsule = world.Simulation.Shapes.GetShape<BepuPhysics.Collidables.Capsule>(shape.Index);
			radius = capsule.Radius;
			halfLength = capsule.HalfLength;
		}

		// Капсула Bepu лежит вдоль собственной Y.
		var axis = Vector3.Transform(Vector3.UnitY, pose.Orientation) * halfLength;
		return (pose.Position - axis, pose.Position + axis, radius);
	}

	/// <summary>Кратчайшее расстояние между двумя отрезками. Перебором по параметру: отрезков здесь
	/// два десятка, точная формула с вырожденными случаями стоила бы больше внимания, чем экономит
	/// времени.</summary>
	private static float SegmentDistance(Vector3 a0, Vector3 a1, Vector3 b0, Vector3 b1)
	{
		const int steps = 16;
		float best = float.MaxValue;

		for (int i = 0; i <= steps; i++)
		{
			var pa = Vector3.Lerp(a0, a1, i / (float)steps);

			for (int j = 0; j <= steps; j++)
			{
				best = MathF.Min(best, Vector3.Distance(pa, Vector3.Lerp(b0, b1, j / (float)steps)));
			}
		}

		return best;
	}

	/// <summary>Габарит рэгдолла - максимальное расстояние между телами. Именно он растёт у
	/// разлетающегося и стоит на месте у целого.</summary>
	private static float Spread(PhysicsWorld world, Ragdoll ragdoll)
	{
		float worst = 0f;

		for (int i = 0; i < ragdoll.BoneCount; i++)
		{
			var a = world.Simulation.Bodies[ragdoll.BodyOf(i)].Pose.Position;

			for (int j = i + 1; j < ragdoll.BoneCount; j++)
			{
				var b = world.Simulation.Bodies[ragdoll.BodyOf(j)].Pose.Position;
				worst = MathF.Max(worst, Vector3.Distance(a, b));
			}
		}

		return worst;
	}

	/// <summary>
	/// Кинематический режим: тела обязаны ехать за позой. Проверяется тем, что поза, ПРОЧИТАННАЯ
	/// обратно из тел, совпадает с той, которую в них гнали. Расхождение здесь означает ошибку в
	/// переводе «джойнт -> тело» (капсула лежит вдоль своей Y, джойнт смотрит куда угодно), и
	/// проявляется она как персонаж, у которого физика живёт отдельно от картинки.
	/// </summary>
	private static void ProbeKinematicTracking(PhysicsWorld world, Ragdoll ragdoll, Matrix4x4[] jointWorld,
		Matrix4x4[] reference)
	{
		for (int i = 0; i < 30; i++)
		{
			ragdoll.DriveToPose(jointWorld, PhysicsWorld.FixedTimeStep);
			world.Update(PhysicsWorld.FixedTimeStep);
		}

		var readBack = (Matrix4x4[])jointWorld.Clone();
		ragdoll.ReadPose(readBack);

		float worst = 0f;
		for (int i = 0; i < ragdoll.BoneCount; i++)
		{
			int joint = ragdoll.JointOf(i);
			worst = MathF.Max(worst,
				Vector3.Distance(readBack[joint].Translation, reference[joint].Translation));
		}

		Console.WriteLine($"[probe] ragdoll: кинематическое слежение - максимальное расхождение " +
			$"{worst:0.####} {(worst < 0.05f ? "OK" : "НЕ СЛЕДУЕТ ЗА ПОЗОЙ")}");
	}

	/// <summary>
	/// Слежение на кадре МНОГО КОРОЧЕ шага симуляции (1/600 против 1/120) - редактор на высоком FPS.
	/// Скорость, посчитанная как «дельта / кадр», интегрируется целым шагом и проезжает в
	/// (шаг/кадр) раз дальше нужного; следующий кадр исправляет перелёт новым перелётом. При
	/// кадре короче ПОЛОВИНЫ шага коэффициент перелёта больше двух, и колебание РАСХОДИТСЯ - тела
	/// улетают в бесконечность, широкая фаза Bepu умирает переполнением стека на NaN-габаритах (в
	/// редакторе это краш «SplitSubtreesIntoChildrenBinned x1850», без единого слова о телах).
	///
	/// Обе особенности постановки обязательны, без любой из них проверка слепа:
	/// - тела стартуют СО СМЕЩЕНИЕМ в метр - раскачка усиливает ОШИБКУ, а у тел, уже стоящих на
	///   цели, усиливать нечего (первая версия проверки прошла и на сломанном делителе);
	/// - кадр 1/600, а не 1/240: на 1/240 коэффициент ровно два - граница устойчивости, колебание
	///   не растёт.
	/// </summary>
	private static void ProbeHighFpsTracking(PhysicsWorld world, Ragdoll ragdoll, Matrix4x4[] jointWorld,
		Matrix4x4[] reference)
	{
		const float frame = 1f / 600f;

		var displaced = (Matrix4x4[])jointWorld.Clone();
		for (int i = 0; i < displaced.Length; i++)
		{
			displaced[i].Translation += new Vector3(1f, 0f, 0f);
		}

		ragdoll.TeleportToPose(displaced);

		// Полсекунды: исправному делителю хватает одного шагового кадра, чтобы закрыть метр,
		// сломанному - пятнадцати, чтобы уйти в тысячи единиц.
		for (int i = 0; i < 300; i++)
		{
			ragdoll.DriveToPose(jointWorld, frame);
			world.Update(frame);
		}

		var readBack = (Matrix4x4[])jointWorld.Clone();
		ragdoll.ReadPose(readBack);

		float worst = 0f;
		bool finite = true;

		for (int i = 0; i < ragdoll.BoneCount; i++)
		{
			int joint = ragdoll.JointOf(i);
			var position = readBack[joint].Translation;

			finite &= float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z);
			worst = MathF.Max(worst,
				Vector3.Distance(position, reference[joint].Translation));
		}

		Console.WriteLine($"[probe] ragdoll: слежение на кадре 1/600 из смещения в метр - расхождение " +
			$"{worst:0.####} {(finite && worst < 0.05f ? "OK" : "РАСКАЧКА/УЛЕТЕЛ")}");
	}

	/// <summary>
	/// Динамический режим: рэгдолл падает на пол. Проверяется, что он ОСТАНАВЛИВАЕТСЯ (а не
	/// продолжает разгоняться), что все тела конечны и что ни одно не оказалось под полом.
	/// Разлетающийся рэгдолл - самый частый способ сломать физику персонажа, и ловится он ровно
	/// проверкой на конечность и на скорость в конце.
	/// </summary>
	private static void ProbeDynamicFall(PhysicsWorld world, Ragdoll ragdoll, Matrix4x4[] jointWorld,
		PreparedSkeleton skeleton)
	{
		ragdoll.SetAnimationDriven(false);

		// Толчок вбок. Без него проверка теперь проверяла бы не то: после отключения самостолкновений
		// (см. Ragdoll.Build) идеально выставленный в bind-позу рэгдолл СТОИТ на капсулах ног, как
		// статуэтка, - раньше его валили контакты собственных ног друг о друга, то есть падение
		// начинал побочный эффект бага. В игре переход в рэгдолл всегда происходит с ненулевой
		// скоростью, и толчок - её модель. Масштаб - от высоты таза: абсолютное число в единицах
		// модели было бы верным ровно для одного рига.
		var hip = world.Simulation.Bodies[ragdoll.BodyOf(0)];
		hip.Velocity.Linear += new Vector3(jointWorld[ragdoll.JointOf(0)].Translation.Y * 0.5f, 0f, 0f);
		hip.Awake = true;

		float simulated = 0f;
		float speedAtImpact = 0f;

		while (simulated < 6f)
		{
			world.Update(1f / 60f);
			simulated += 1f / 60f;

			// Скорость сразу после приземления - точка отсчёта. Абсолютный порог «успокоился»
			// бессмыслен: он зависит от масштаба модели (у лисы габарит ~160 единиц), а вот
			// ТРЕБОВАНИЕ УБЫВАНИЯ от него не зависит и ловит именно то, что нужно, - расходящуюся
			// симуляцию, в которой энергия растёт вместо того, чтобы гаситься.
			if (simulated >= 3f && speedAtImpact == 0f)
			{
				speedAtImpact = MaxSpeed(world, ragdoll);
			}
		}

		ragdoll.ReadPose(jointWorld);

		bool finite = true;
		float lowest = float.MaxValue;
		float highestSpeed = 0f;

		for (int i = 0; i < ragdoll.BoneCount; i++)
		{
			int joint = ragdoll.JointOf(i);
			var position = jointWorld[joint].Translation;

			finite &= float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z);
			lowest = MathF.Min(lowest, position.Y);
		}

		highestSpeed = MaxSpeed(world, ragdoll);

		Console.WriteLine($"[probe] ragdoll: свободное падение 6 с - " +
			$"{(finite ? "координаты конечны OK" : "NaN/Inf - РАЗЛЕТЕЛСЯ")}, " +
			$"нижняя кость на y={lowest:0.##} {(lowest > -5f ? "OK" : "ПРОВАЛИЛСЯ")}, " +
			$"скорость {speedAtImpact:0.##} -> {highestSpeed:0.##} " +
			$"{(highestSpeed < speedAtImpact ? "гасится OK" : "РАСХОДИТСЯ")}");
	}

	private static float MaxSpeed(PhysicsWorld world, Ragdoll ragdoll)
	{
		float speed = 0f;
		for (int i = 0; i < ragdoll.BoneCount; i++)
		{
			speed = MathF.Max(speed, world.Simulation.Bodies[ragdoll.BodyOf(i)].Velocity.Linear.Length());
		}

		return speed;
	}

	/// <summary>Описание рэгдолла лисы по именам костей. Радиусы подобраны под масштаб модели
	/// (~160 единиц в длину): капсула тоньше кости выглядит как скелет, толще - как бочка.</summary>
	private static List<RagdollBoneDesc> BuildDescription(PreparedSkeleton skeleton)
	{
		var bones = new List<RagdollBoneDesc>();
		var index = new Dictionary<string, int>();

		void Add(string joint, string child, string parent, float radius, float mass)
		{
			int jointIndex = skeleton.FindJoint(joint);
			int childIndex = child != null ? skeleton.FindJoint(child) : -1;

			if (jointIndex < 0 || (child != null && childIndex < 0))
			{
				return;
			}

			index[joint] = bones.Count;
			bones.Add(new RagdollBoneDesc
			{
				Joint = jointIndex,
				ChildJoint = childIndex,
				Parent = parent != null && index.TryGetValue(parent, out int p) ? p : -1,
				Radius = radius,
				Length = 8f,
				Mass = mass,

				// Те же ограничения, что ставит редактор (см. AnimationDriver.BuildRagdollDescription):
				// проба, гоняющая рэгдолл со СВОИМИ настройками, проверяла бы не тот рэгдолл, который
				// живёт в сцене.
				SwingLimitCos = -0.5f,
				TwistLimitAngle = TwistLimitDegrees * (MathF.PI / 180f),
			});
		}

		Add("b_Hip_01", "b_Spine01_02", null, 6f, 12f);
		Add("b_Spine01_02", "b_Spine02_03", "b_Hip_01", 6f, 10f);
		Add("b_Spine02_03", "b_Neck_04", "b_Spine01_02", 5f, 8f);
		Add("b_Neck_04", "b_Head_05", "b_Spine02_03", 3f, 3f);
		Add("b_Head_05", null, "b_Neck_04", 4f, 4f);

		Add("b_LeftLeg01_015", "b_LeftLeg02_016", "b_Hip_01", 2f, 3f);
		Add("b_LeftLeg02_016", "b_LeftFoot01_017", "b_LeftLeg01_015", 1.5f, 2f);
		Add("b_RightLeg01_019", "b_RightLeg02_020", "b_Hip_01", 2f, 3f);
		Add("b_RightLeg02_020", "b_RightFoot01_021", "b_RightLeg01_019", 1.5f, 2f);

		Add("b_LeftUpperArm_09", "b_LeftForeArm_010", "b_Spine02_03", 2f, 3f);
		Add("b_LeftForeArm_010", "b_LeftHand_011", "b_LeftUpperArm_09", 1.5f, 2f);
		Add("b_RightUpperArm_06", "b_RightForeArm_07", "b_Spine02_03", 2f, 3f);
		Add("b_RightForeArm_07", "b_RightHand_08", "b_RightUpperArm_06", 1.5f, 2f);

		return bones;
	}
}
