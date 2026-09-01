using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DecaEngine.Graphics;

namespace DecaEngine.Editor;

/// <summary>
/// Проверка процедурного слоя (DECA_PROBE_PROC=1): spring bones, foot IK, aim IK. Настраивается по
/// ИМЕНАМ костей, а не по индексам: индексы зависят от порядка узлов в glTF и молча разъезжаются при
/// переэкспорте модели, а имена - нет.
///
/// Физика здесь не нужна: foot IK принимает райкаст функцией, и подставить плоскость точнее и
/// воспроизводимее, чем гонять симуляцию, - у проверки IK и проверки физики разные предметы
/// (последняя живёт в PhysicsProbe).
/// </summary>
public static class ProceduralProbe
{
	public static void Run(ModelLoader model)
	{
		if (model.Skeleton == null)
		{
			Console.WriteLine("[probe] proc: модель без скелета - проверять нечего");
			return;
		}

		if (!Ozz.IsAvailable)
		{
			Console.WriteLine("[probe] proc: нативный ozz недоступен - IK не проверить (two-bone/aim живут в нём)");
			return;
		}

		using var skeleton = OzzSkeleton.Build(model.Skeleton);
		using var pose = OzzPose.Create(skeleton);
		if (skeleton == null || pose == null)
		{
			Console.WriteLine("[probe] proc: скелет ozz не собрался");
			return;
		}

		int jointCount = model.Skeleton.JointCount;
		var locals = new Transform[jointCount];
		var models = new Matrix4x4[jointCount];

		ProbeSpringBones(model.Skeleton, pose, locals, models);
		ProbeFootIk(model.Skeleton, pose, locals, models);
		ProbeFootLocking(model, model.Skeleton, skeleton, pose, locals, models);
		ProbeAimIk(model.Skeleton, pose, locals, models);

		// Рэгдолл - единственная часть процедурного слоя, которой физика нужна по существу, поэтому
		// он живёт отдельным файлом и получает уже готовую позу.
		RagdollProbe.Run(model.Skeleton, pose, models);
	}

	private static bool Refresh(OzzPose pose, Transform[] locals, Matrix4x4[] models) =>
		pose.LocalToModel() && pose.ReadModelMatrices(models) && pose.ReadLocalTransforms(locals);

	// --- Spring bones -----------------------------------------------------------------------------

	/// <summary>
	/// Три утверждения, и все три обязательны. Цепочка в покое НЕ должна дёргать позу (иначе
	/// «вторичное движение» превращается в вечную мелкую тряску). При рывке она ОБЯЗАНА отстать -
	/// иначе никакого вторичного движения нет вовсе, а есть жёстко привязанный хвост. И она обязана
	/// ВЕРНУТЬСЯ - расходящаяся цепочка выглядит как взорвавшийся персонаж и ловится только так.
	/// </summary>
	private static void ProbeSpringBones(PreparedSkeleton skeleton, OzzPose pose, Transform[] locals,
		Matrix4x4[] models)
	{
		var chainJoints = new[] { "b_Tail01_012", "b_Tail02_013", "b_Tail03_014" }
			.Select(skeleton.FindJoint)
			.Where(j => j >= 0)
			.ToArray();

		if (chainJoints.Length < 2)
		{
			Console.WriteLine("[probe] proc: цепочки хвоста в риге нет - spring bones пропущены");
			return;
		}

		var chain = new SpringBoneChain
		{
			Joints = chainJoints,
			Stiffness = 0.08f,
			Drag = 0.2f,
			TailLength = 10f,
		};

		var chains = new List<SpringBoneChain> { chain };
		const float dt = 1f / 60f;

		if (!Refresh(pose, locals, models))
		{
			return;
		}

		var reference = (Transform[])locals.Clone();
		int tip = chainJoints[^1];

		// 1. Покой: сто шагов при неподвижной позе.
		for (int i = 0; i < 100; i++)
		{
			reference.CopyTo(locals, 0);
			pose.WriteLocalTransforms(locals);
			Refresh(pose, locals, models);
			SpringBones.Solve(skeleton, chains, locals, models, dt);
		}

		var restTip = models[tip].Translation;

		reference.CopyTo(locals, 0);
		pose.WriteLocalTransforms(locals);
		Refresh(pose, locals, models);
		float restDrift = Vector3.Distance(restTip, models[tip].Translation);

		// 2. Рывок: разворачиваем корень цепочки на 60° и делаем ОДИН шаг.
		int chainRoot = chainJoints[0];
		int chainParent = skeleton.Parents[chainRoot] >= 0 ? skeleton.Parents[chainRoot] : chainRoot;

		var jerked = (Transform[])reference.Clone();
		jerked[chainParent].rotation = Quaternion.Normalize(
			Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 3f) * jerked[chainParent].rotation);

		jerked.CopyTo(locals, 0);
		pose.WriteLocalTransforms(locals);
		Refresh(pose, locals, models);
		var animatedTip = models[tip].Translation;

		SpringBones.Solve(skeleton, chains, locals, models, dt);
		float lag = Vector3.Distance(models[tip].Translation, animatedTip);

		// 3. Возврат: держим повёрнутую позу и ждём, пока цепочка догонит.
		for (int i = 0; i < 300; i++)
		{
			jerked.CopyTo(locals, 0);
			pose.WriteLocalTransforms(locals);
			Refresh(pose, locals, models);
			SpringBones.Solve(skeleton, chains, locals, models, dt);
		}

		float settled = Vector3.Distance(models[tip].Translation, animatedTip);

		Console.WriteLine($"[probe] proc: spring bones - покой {restDrift:0.####} " +
			$"{(restDrift < 0.01f ? "OK" : "ДРОЖИТ")}, отставание при рывке {lag:0.###} " +
			$"{(lag > 0.05f ? "OK" : "НЕТ ИНЕРЦИИ")}, возврат {settled:0.####} " +
			$"{(settled < lag * 0.1f ? "OK" : "НЕ СХОДИТСЯ")}");
	}

	// --- Foot IK ----------------------------------------------------------------------------------

	private static void ProbeFootIk(PreparedSkeleton skeleton, OzzPose pose, Transform[] locals, Matrix4x4[] models)
	{
		var legs = new List<FootIkLeg>();
		AddLeg(skeleton, legs, "b_LeftLeg01_015", "b_LeftLeg02_016", "b_LeftFoot01_017");
		AddLeg(skeleton, legs, "b_RightLeg01_019", "b_RightLeg02_020", "b_RightFoot01_021");

		if (legs.Count < 2)
		{
			Console.WriteLine("[probe] proc: задних ног в риге не нашлось - foot IK пропущен");
			return;
		}

		// По НОГЕ отдельно, и пол ставится относительно ЕЁ стопы. Общая плоскость под обе ноги для
		// этой модели нефизична: у лисы задние стопы в bind-позе разнесены по высоте на 12 единиц
		// при длине ноги ~15, и требование поставить обе на один уровень упирается не в качество
		// солвера, а в предел складывания колена. Проверять надо солвер, а не выбор тестовой сцены.
		foreach (var leg in legs)
		{
			ProbeSingleLeg(skeleton, pose, leg, locals, models);
		}

		ProbePelvisDrop(skeleton, pose, legs, locals, models);
	}

	/// <summary>Одна нога, пол на единицу выше её стопы: заведомо достижимая цель, на которой
	/// аналитический two-bone IK обязан попадать ТОЧНО. Таз при этом выключен - его поведение
	/// проверяется отдельно (см. <see cref="ProbePelvisDrop"/>), и смешивать два эффекта в одной
	/// проверке значит не знать, который из них сломался.</summary>
	private static void ProbeSingleLeg(PreparedSkeleton skeleton, OzzPose pose, FootIkLeg leg,
		Transform[] locals, Matrix4x4[] models)
	{
		var settings = new FootIkSettings
		{
			PelvisJoint = -1,
			ProbeUp = 20f,
			ProbeDown = 40f,
			// Мгновенное сглаживание: проверка одношаговая, экспоненциальное приближение только
			// размазало бы ожидаемое значение по кадрам.
			Smoothing = 0f,
			AlignToNormal = false,
		};

		if (!Refresh(pose, locals, models))
		{
			return;
		}

		float startY = models[leg.FootJoint].Translation.Y;

		// Пол - на единицу выше ПЛОСКОСТИ ОПОРЫ (нуля модели), а не выше стопы: солвер сохраняет
		// подъём стопы над плоскостью и ограничивает поправку долей длины ноги, и пол «чуть выше
		// стопы» в абсолюте (y около 36) означал бы рельеф в две длины ноги - клапан насыщается, и
		// проверялась бы деградация, а не точность. Здесь проверяется не семантика замаха, а
		// точность two-bone IK, поэтому высота щиколотки берётся равной bind-высоте стопы - замах
		// тогда нулевой, и цель равна «пол + щиколотка», как и ожидание, а поправка равна единице.
		const float groundY = 1f;
		leg.AnkleHeight = startY;

		GroundSample Ground(Vector3 origin, Vector3 direction, float distance)
		{
			float travel = origin.Y - groundY;
			return travel >= 0f && travel <= distance
				? new GroundSample { Hit = true, Position = new Vector3(origin.X, groundY, origin.Z), Normal = Vector3.UnitY }
				: default;
		}

		leg.ResetSmoothing();
		var single = new List<FootIkLeg> { leg };

		bool solved = FootIk.Solve(pose, skeleton, single, settings, Matrix4x4.Identity, locals, models, Ground, 1f / 60f);

		float resultY = models[leg.FootJoint].Translation.Y;
		float expected = groundY + leg.AnkleHeight;
		float error = MathF.Abs(resultY - expected);

		// Пределы цепочки: two-bone IK не может поставить стопу ближе к бедру, чем |L1-L2|, и дальше,
		// чем L1+L2. Без этих чисел недолёт неотличим от ошибки солвера - а это разные диагнозы.
		var hip = models[leg.UpperJoint].Translation;
		float upperLength = Vector3.Distance(hip, models[leg.LowerJoint].Translation);
		float lowerLength = Vector3.Distance(models[leg.LowerJoint].Translation, models[leg.FootJoint].Translation);
		float maxReach = upperLength + lowerLength;
		float minReach = MathF.Abs(upperLength - lowerLength);

		// Расстояние от бедра до ЦЕЛИ - именно оно решает, достижима она или нет. Сравнивать надо с
		// ним, а не с итоговым положением стопы: последнее по определению лежит в пределах цепочки,
		// и по нему недостижимую цель от ошибки солвера не отличить.
		var targetPoint = new Vector3(
			models[leg.FootJoint].Translation.X, expected, models[leg.FootJoint].Translation.Z);
		float targetReach = Vector3.Distance(hip, targetPoint);

		string verdict = error < 0.02f
			? "OK"
			: targetReach >= maxReach - 0.02f
				? "цель ВНЕ ДОСЯГАЕМОСТИ (нога вытянута)"
				: targetReach <= minReach + 0.02f
					? "цель ближе предела складывания"
					: "СЛИШКОМ БОЛЬШАЯ";

		Console.WriteLine($"[probe] proc: foot IK ({skeleton.JointNames[leg.FootJoint]}) - " +
			$"{(solved ? "решён" : "НЕ РЕШЁН")}, y {startY:0.###} -> {resultY:0.###}, ожидалось {expected:0.###}, " +
			$"ошибка {error:0.####}; досягаемость {minReach:0.##}..{maxReach:0.##}, до цели {targetReach:0.##} {verdict}");
	}

	/// <summary>
	/// Опускание таза. Пол ставится НИЖЕ обеих стоп на заведомо большую величину, и проверяется, что
	/// таз ушёл вниз ровно на клапан <see cref="FootIkSettings.MaxPelvisDrop"/> - не глубже (иначе
	/// шаг в пропасть утягивает персонажа под землю) и не мельче (иначе клапан не работает вовсе).
	/// </summary>
	private static void ProbePelvisDrop(PreparedSkeleton skeleton, OzzPose pose, List<FootIkLeg> legs,
		Transform[] locals, Matrix4x4[] models)
	{
		int pelvis = skeleton.FindJoint("b_Hip_01");
		if (pelvis < 0)
		{
			return;
		}

		const float maxDrop = 3f;
		var settings = new FootIkSettings
		{
			PelvisJoint = pelvis,
			ProbeUp = 20f,
			ProbeDown = 80f,
			MaxPelvisDrop = maxDrop,
			Smoothing = 0f,
			AlignToNormal = false,
		};

		if (!Refresh(pose, locals, models))
		{
			return;
		}

		float pelvisBefore = models[pelvis].Translation.Y;

		// Пол - ниже ПЛОСКОСТИ ОПОРЫ (нуля модели), а не «ниже стопы»: солвер сохраняет подъём
		// стопы над нулём, и пол, опущенный относительно стопы, но оставшийся ВЫШЕ нуля, в новой
		// семантике - это возвышение, на которое ногу поднимают, а не пропасть, в которую опускают
		// таз (ровно на этом тест и сломался после смены семантики: «на 20 ниже стопы» у лисы со
		// стопами на y=23..35 - это ещё y=+3).

		const float groundY = -20f;

		GroundSample Ground(Vector3 origin, Vector3 direction, float distance)
		{
			float travel = origin.Y - groundY;
			return travel >= 0f && travel <= distance
				? new GroundSample { Hit = true, Position = new Vector3(origin.X, groundY, origin.Z), Normal = Vector3.UnitY }
				: default;
		}

		foreach (var leg in legs)
		{
			leg.ResetSmoothing();

			// ProbeSingleLeg перед этим подменил щиколотку на bind-высоту стопы (общий список ног) -
			// вернуть авторскую: здесь пропасть задана ниже нуля модели, подъём стопы её не съедает,
			// и цель честно недостижима на величину много больше клапана.
			leg.AnkleHeight = 0.5f;
		}

		FootIk.Solve(pose, skeleton, legs, settings, Matrix4x4.Identity, locals, models, Ground, 1f / 60f);

		float drop = pelvisBefore - models[pelvis].Translation.Y;
		Console.WriteLine($"[probe] proc: таз опустился на {drop:0.###} при клапане {maxDrop} " +
			$"{(MathF.Abs(drop - maxDrop) < 0.02f ? "OK" : "MISMATCH")}");
	}

	/// <summary>
	/// Локинг опорной стопы - ПАРОЙ на рассинхроне темпа. Модель едет по миру с постоянной
	/// скоростью, а клип Walk шагает НА МЕСТЕ: без локинга опорная лапа обязана ехать по полу ровно
	/// со скоростью модели, с локингом - стоять в точке захвата. Стойка размечается ПО КЛИПУ заранее
	/// (нижняя четверть размаха высоты стопы), в обеих ветках меряется одно и то же - путь лапы в
	/// МИРЕ за кадры стойки. Одна ветка не доказывает ничего: «мало скольжения» бывает и без
	/// локинга, если модель еле ползёт.
	///
	/// Высота щиколотки в ветках подставляется равной минимуму стопы в клипе, а пол - в ноль мира:
	/// вертикальный канал тогда тождественный, и пара мерит ТОЛЬКО локинг, а не его смесь с
	/// подстройкой высоты.
	/// </summary>
	private static void ProbeFootLocking(ModelLoader model, PreparedSkeleton skeleton, OzzSkeleton ozz,
		OzzPose pose, Transform[] locals, Matrix4x4[] models)
	{
		var walk = model.Animations.FirstOrDefault(
			a => string.Equals(a.Name, "Walk", StringComparison.Ordinal));
		int leftFoot = skeleton.FindJoint("b_LeftFoot01_017");
		int rightFoot = skeleton.FindJoint("b_RightFoot01_021");

		if (walk == null || leftFoot < 0 || rightFoot < 0)
		{
			Console.WriteLine("[probe] proc: локинг пропущен - нет клипа Walk или задних лап");
			return;
		}

		using var clip = OzzClip.Build(ozz, walk);
		if (clip == null || clip.Duration <= 0f)
		{
			Console.WriteLine("[probe] proc: локинг пропущен - клип Walk не собрался в ozz");
			return;
		}

		// Разметка стойки, минимумы высот и ПРИРОДНАЯ скорость шага - одним проходом по циклу.
		const int cycleSamples = 60;
		var leftPositions = new Vector3[cycleSamples];
		float leftMin = float.MaxValue, leftMax = float.MinValue, rightMin = float.MaxValue;

		for (int k = 0; k < cycleSamples; k++)
		{
			if (!pose.Sample(clip, clip.Duration * k / cycleSamples) || !Refresh(pose, locals, models))
			{
				return;
			}

			leftPositions[k] = models[leftFoot].Translation;
			leftMin = MathF.Min(leftMin, leftPositions[k].Y);
			leftMax = MathF.Max(leftMax, leftPositions[k].Y);
			rightMin = MathF.Min(rightMin, models[rightFoot].Translation.Y);
		}

		// Стойка размечается ЯДРОМ - нижней десятой размаха высоты, а не нижней четвертью: на краях
		// такта лапа клипа сама разгоняется и тормозит, там рассинхрон мгновенной скорости велик
		// при любом среднем темпе, и локинг на краях ОТПУСКАЕТ по страховке осознанно. Мерить
		// удержание можно только там, где лапа клипа стоит, - в ядре.
		var stance = new bool[cycleSamples];
		float threshold = leftMin + 0.10f * (leftMax - leftMin);
		float strideTravel = 0f;
		float strideSeconds = 0f;

		for (int k = 0; k < cycleSamples; k++)
		{
			stance[k] = leftPositions[k].Y < threshold;

			int next = (k + 1) % cycleSamples;
			if (leftPositions[k].Y < threshold && leftPositions[next].Y < threshold)
			{
				var step = leftPositions[next] - leftPositions[k];
				strideTravel += MathF.Sqrt(step.X * step.X + step.Z * step.Z);
				strideSeconds += clip.Duration / cycleSamples;
			}
		}

		if (strideSeconds < 1e-4f || strideTravel < 1e-3f)
		{
			Console.WriteLine("[probe] proc: локинг пропущен - у клипа не нашлось такта опоры");
			return;
		}

		float naturalSpeed = strideTravel / strideSeconds;

		GroundSample Ground(Vector3 origin, Vector3 direction, float distance)
		{
			float travel = origin.Y;
			return travel >= 0f && travel <= distance
				? new GroundSample { Hit = true, Position = new Vector3(origin.X, 0f, origin.Z), Normal = Vector3.UnitY }
				: default;
		}

		float Slide(bool locking)
		{
			var legs = new List<FootIkLeg>();
			AddLeg(skeleton, legs, "b_LeftLeg01_015", "b_LeftLeg02_016", "b_LeftFoot01_017");
			AddLeg(skeleton, legs, "b_RightLeg01_019", "b_RightLeg02_020", "b_RightFoot01_021");

			if (legs.Count < 2)
			{
				return float.NaN;
			}

			legs[0].AnkleHeight = leftMin;
			legs[1].AnkleHeight = rightMin;

			var settings = new FootIkSettings
			{
				PelvisJoint = -1,
				ProbeUp = 20f,
				ProbeDown = 60f,
				Smoothing = 30f,
				MaxPelvisDrop = 0f,
				AlignToNormal = false,
				LockFeet = locking,
			};

			// Скорость модели - ПРИРОДНАЯ скорость клипа плюс 10% рассинхрона: ровно тот остаток,
			// который локинг обязан убирать. Модель, ползущая много медленнее клипа, - негодная
			// сцена дважды: мировой путь стопы тогда доминируется махом самого клипа (метрика
			// меряет клип, а не скольжение), а огромный рассинхрон срабатывает страховочным
			// отпуском на 0.35 длины ноги - и пара меряет страховку, а не удержание.
			// Движение - ВДОЛЬ ХОДА ЛИСЫ (морда в -Z): мах опорной лапы сокращается с движением
			// модели только по этой оси, движение поперёк хода не сокращает его никаким темпом.
			float speed = naturalSpeed * 1.1f;
			const float dt = 1f / 60f;
			const int frames = 240;

			// Первые полторы секунды не меряются: огибающая локинга выучивает размах ноги за
			// цикл-другой, и до этого обе ветки честно одинаковы.
			const int warmup = 90;

			float slide = 0f;
			var previous = Vector3.Zero;
			bool previousStance = false;

			for (int i = 0; i < frames; i++)
			{
				float time = i * dt;
				float clipTime = time % clip.Duration;
				int k = (int)(clipTime / clip.Duration * cycleSamples) % cycleSamples;
				var world = Matrix4x4.CreateTranslation(0f, 0f, -speed * time);

				if (!pose.Sample(clip, clipTime) || !Refresh(pose, locals, models))
				{
					return float.NaN;
				}

				FootIk.Solve(pose, skeleton, legs, settings, world, locals, models, Ground, dt);

				var footWorld = Vector3.Transform(models[leftFoot].Translation, world);

				if (i > warmup && previousStance && stance[k])
				{
					slide += Vector3.Distance(footWorld, previous);
				}

				previous = footWorld;
				previousStance = stance[k];
			}

			return slide;
		}

		float unlocked = Slide(locking: false);
		float locked = Slide(locking: true);

		// Локинг - ДЕМПФЕР, а не абсолютный пин: захват плавный (полвеса точка ещё следует за
		// стопой), на краях такта и на пределе увода он отпускает по страховкам - всё это осознанно
		// куплено против «ноги в теле» и дёрганья. Поэтому планка - «заметно меньше», а не «в разы»:
		// требовать от демпфера нулевого скольжения значит выключить страховки обратно.
		bool ok = unlocked > 5f && locked < unlocked * 0.7f;

		Console.WriteLine($"[probe] proc: локинг опорной лапы - скольжение в стойке без локинга " +
			$"{unlocked:0.##}, с локингом {locked:0.##} " +
			$"{(ok ? "OK" : "НЕ ДЕРЖИТ/ПАРА НЕ РАЗОШЛАСЬ")}");
	}

	private static void AddLeg(PreparedSkeleton skeleton, List<FootIkLeg> legs, string upper, string lower, string foot)
	{
		int upperJoint = skeleton.FindJoint(upper);
		int lowerJoint = skeleton.FindJoint(lower);
		int footJoint = skeleton.FindJoint(foot);

		if (upperJoint < 0 || lowerJoint < 0 || footJoint < 0)
		{
			return;
		}

		legs.Add(new FootIkLeg
		{
			UpperJoint = upperJoint,
			LowerJoint = lowerJoint,
			FootJoint = footJoint,
			AnkleHeight = 0.5f,
		});
	}

	// --- Aim IK -----------------------------------------------------------------------------------

	/// <summary>
	/// Aim IK проверяется УГЛОМ ДО ЦЕЛИ до и после: какая именно ось кости считается «взглядом»,
	/// зависит от рига, и требовать точного попадания без знания рига нельзя. А вот то, что после
	/// доворота цель стала БЛИЖЕ к оси взгляда, обязано выполняться на любом риге - и ровно это
	/// ломается, если перепутаны пространство цели или порядок домножения коррекции.
	/// </summary>
	private static void ProbeAimIk(PreparedSkeleton skeleton, OzzPose pose, Transform[] locals, Matrix4x4[] models)
	{
		int head = skeleton.FindJoint("b_Head_05");
		if (head < 0)
		{
			Console.WriteLine("[probe] proc: головы в риге нет - aim IK пропущен");
			return;
		}

		pose.WriteLocalTransforms(locals);
		if (!Refresh(pose, locals, models))
		{
			return;
		}

		var forward = Vector3.UnitZ;
		var headPosition = models[head].Translation;
		var target = headPosition + new Vector3(60f, 40f, 0f);

		float before = AngleToTarget(models[head], forward, target);
		bool solved = pose.AimIk(head, target, forward, Vector3.UnitY, Vector3.UnitY);
		Refresh(pose, locals, models);
		float after = AngleToTarget(models[head], forward, target);

		Console.WriteLine($"[probe] proc: aim IK - {(solved ? "решён" : "НЕ РЕШЁН")}, " +
			$"угол до цели {before * 180f / MathF.PI:0.#}° -> {after * 180f / MathF.PI:0.#}° " +
			$"{(solved && after < before - 0.01f ? "OK" : "НЕ УЛУЧШИЛСЯ")}");
	}

	private static float AngleToTarget(in Matrix4x4 joint, Vector3 localForward, Vector3 target)
	{
		var direction = Vector3.TransformNormal(localForward, joint);
		var toTarget = target - joint.Translation;

		if (direction.LengthSquared() < 1e-10f || toTarget.LengthSquared() < 1e-10f)
		{
			return 0f;
		}

		float cos = Vector3.Dot(Vector3.Normalize(direction), Vector3.Normalize(toTarget));
		return MathF.Acos(Math.Clamp(cos, -1f, 1f));
	}
}
