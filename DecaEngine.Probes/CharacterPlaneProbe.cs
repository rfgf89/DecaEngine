using System;
using System.Collections.Generic;
using System.Numerics;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics;
using Friflo.Engine.ECS;
using DecaEngine.Scene;
using DecaEngine.Animation;
using DecaEngine.Editor;

namespace DecaEngine.Probes;

/// <summary>
/// Воспроизведение ПОЛНОГО персонажа на плоскости (DECA_PROBE_CHARACTER=1): настоящие
/// AnimationDriver и CharacterMotionDriver, плоскость со стеной, матрица состояний - стоит, шаг,
/// бег, тёрка о стену на шаге, тёрка на бегу, дёрганые переключения аллюра. Это ответ на класс
/// багов «ноги упираются в тело», которые до этого ловились только скриншотами из редактора: у
/// каждого состояния меряется МИНИМАЛЬНАЯ дистанция лап и предплечий до оси корпуса в
/// пространстве модели - лапа внутри корпуса даёт число ниже толщины корпуса, и виден не только
/// факт, но и В КАКОМ ИМЕННО состоянии персонаж складывается.
///
/// Тёрки о стену здесь главные: свободный шаг и бег чисты почти при любой поломке бленда, а
/// паркующаяся у стены скорость - ровно то состояние, в котором пользователь трижды ловил
/// сложенные ноги.
/// </summary>
public static class CharacterPlaneProbe
{
	private const float Step = 1f / 60f;

	/// <summary>Порог «лапа в корпусе», единицы модели. Толщина корпуса лисы ~8.5 единиц; в чистом
	/// шаге лапы ходят в 20+ единицах от оси, в честном подборе галопа - в 12-15, а предплечье
	/// галопа С ФРОНТ-IK стабильно на 7.7 (подбор лап, ужатый IK на единицу, колено при этом
	/// чистое). 7.5 отделяет это от настоящего «внутри мяса» (провалы давали 4).</summary>
	private const float InsideThreshold = 7.5f;

	public static void Run(DecaEngine.Graphics.Diligent.DiligentSkinningPass skinning, ModelLoader model,
		string modelPath)
	{
		if (model.Skeleton == null)
		{
			Console.WriteLine("[probe] character: модель без скелета - воспроизводить некого");
			return;
		}

		using var physics = new ScenePhysics(new Vector3(0f, -9.81f, 0f));
		BuildPlane(physics);

		var store = new EntityStore();
		var fox = store.CreateEntity();
		fox.AddComponent(new EntityName("plane fox"));
		fox.AddComponent(new Position(0f, 0f, -8f));
		fox.AddComponent(new Rotation(0f, 0f, 0f, 1f));
		fox.AddComponent(new Scale3(0.01f, 0.01f, 0.01f));
		fox.AddComponent(new Animator { ClipName = "Walk" });
		fox.AddComponent(new LocomotionComponent { IdleClip = "Survey", WalkSpeed = 1f, RunSpeed = 3f });
		fox.AddComponent(new CharacterBodyComponent { Radius = 0.18f, Height = 0.5f, Mass = 12f });
		fox.AddComponent(new PlayerMoveComponent { WalkSpeed = 1f, RunSpeed = 3f, Forward = -Vector3.UnitZ });
		fox.AddComponent(new FootIkComponent
		{
			AnkleHeight = 1f,
			MaxPelvisDrop = 25f,
			FrontLegs = true,
		});

		using var animation = new AnimationDriver(skinning) { Physics = physics };
		animation.AddInstance(fox.Id, model, -1);
		animation.SetAvatar(fox.Id, HumanoidAvatarAsset.Load(modelPath) ?? HumanoidAutoMap.Build(model.Skeleton));

		var motion = new CharacterMotionDriver();

		// Точки замера: передние лапы и предплечья, задние лапы и скакательные суставы. Ось
		// корпуса - таз..шея. Всё по именам костей Khronos Fox: пробник воспроизводит конкретного
		// персонажа, и абстрагировать риг здесь значило бы проверять не его.
		int hips = model.Skeleton.FindJoint("b_Hip_01");
		int neck = model.Skeleton.FindJoint("b_Neck_04");
		int[] probes =
		[
			model.Skeleton.FindJoint("b_LeftHand_011"),
			model.Skeleton.FindJoint("b_RightHand_08"),
			model.Skeleton.FindJoint("b_LeftForeArm_010"),
			model.Skeleton.FindJoint("b_RightForeArm_07"),
			model.Skeleton.FindJoint("b_LeftFoot02_018"),
			model.Skeleton.FindJoint("b_RightFoot02_022"),
			model.Skeleton.FindJoint("b_LeftFoot01_017"),
			model.Skeleton.FindJoint("b_RightFoot01_021"),
		];

		// Цепочки задних ног для метрики ВЫВОРОТА: скакательный сустав лисы в любой честной позе
		// согнут НАЗАД (+Z модели при морде в -Z). Метрика «в теле» выворот наружу не видит вовсе -
		// вывернутое колено торчит ОТ корпуса, дистанция до оси при этом растёт.
		int[][] hindChains =
		[
			[model.Skeleton.FindJoint("b_LeftLeg01_015"), model.Skeleton.FindJoint("b_LeftLeg02_016"),
				model.Skeleton.FindJoint("b_LeftFoot01_017")],
			[model.Skeleton.FindJoint("b_RightLeg01_019"), model.Skeleton.FindJoint("b_RightLeg02_020"),
				model.Skeleton.FindJoint("b_RightFoot01_021")],
		];

		if (hips < 0 || neck < 0 || Array.IndexOf(probes, -1) >= 0 ||
			Array.IndexOf(hindChains[0], -1) >= 0 || Array.IndexOf(hindChains[1], -1) >= 0)
		{
			Console.WriteLine("[probe] character: кости лисы не нашлись - метрике не к чему цепляться");
			return;
		}

		// Матрица состояний. Направления подобраны под стену на x=2.5: тёрки скользят вдоль неё,
		// свободные фазы идут параллельно. Каждая фаза начинается там, где закончилась прошлая, -
		// как в живой игре, без телепортов между состояниями.
		(string Name, float Seconds, Func<float, PlayerInput> Input)[] phases =
		[
			("стоит", 2f, _ => default),
			("шаг", 3f, _ => new PlayerInput { MoveWorld = Vector3.UnitZ }),
			("бег", 3f, _ => new PlayerInput { MoveWorld = Vector3.UnitZ, Run = true }),
			("тёрка на шаге", 4f, _ => new PlayerInput { MoveWorld = new Vector3(2f, 0f, 1f) }),
			("тёрка на бегу", 4f, _ => new PlayerInput { MoveWorld = new Vector3(2f, 0f, 1f), Run = true }),
			("дёрганый аллюр", 4f, t => new PlayerInput
			{
				MoveWorld = new Vector3(-1f, 0f, 0.3f),
				Run = (int)(t / 0.6f) % 2 == 0,
			}),
		];

		bool anyInside = false;
		bool strideReported = false;

		foreach (var phase in phases)
		{
			// Фаза «бег без foot IK» - диагностическая ветка: чистый клип от процедурки отличается
			// только этим тумблером, и красный бег с зелёным «бегом без IK» означает вину солвера,
			// два красных - вину самого клипа (или его темпа).
			if (string.Equals(phase.Name, "бег", StringComparison.Ordinal))
			{
				RunPhase(("бег без foot IK", 3f, _ => new PlayerInput { MoveWorld = Vector3.UnitZ, Run = true }),
					footIkEnabled: false, lockFeet: false);
				RunPhase(("бег без локинга", 3f, _ => new PlayerInput { MoveWorld = Vector3.UnitZ, Run = true }),
					footIkEnabled: true, lockFeet: false);
			}

			RunPhase(phase, footIkEnabled: true, lockFeet: true);
		}

		// Свип веса foot IK - жалоба «тянешь Weight с 0 на 1, и лапы выворачивает назад» приходит
		// именно с ползунка: частичный вес - отдельный путь солвера (ozz лерпит коррекции), и
		// единичный вес его не проверяет. Двумя средами: играющей (dt=1/60) и редакторской
		// (нулевой шаг - ровно то, что происходит под курсором на ползунке).
		foreach (float weight in new[] { 0.25f, 0.5f, 0.75f })
		{
			RunPhase(($"вес {weight:0.00}", 1.5f, _ => default), footIkEnabled: true, lockFeet: true,
				weight);
		}

		{
			ref var footIk = ref fox.GetComponent<FootIkComponent>();
			footIk.Enabled = true;
			footIk.LockFeet = true;

			float worstKnee = float.MinValue;
			float worstDistance = float.MaxValue;
			float worstWeight = -1f;

			for (float weight = 0f; weight <= 1.001f; weight += 0.1f)
			{
				footIk.Weight = weight;

				for (int i = 0; i < 30; i++)
				{
					animation.BeginFrame();
					animation.Update(fox, PrefabSceneViewport.ComputeWorldMatrix(fox), 0f);
				}

				if (!animation.TryGetPose(fox.Id, out var models, out _))
				{
					continue;
				}

				var axisA = models[hips].Translation;
				var axisB = models[neck].Translation;

				foreach (int joint in probes)
				{
					float distance = DistanceToSegment(models[joint].Translation, axisA, axisB);
					if (distance < worstDistance)
					{
						worstDistance = distance;
						worstWeight = weight;
					}
				}

				foreach (var chain in hindChains)
				{
					var hip = models[chain[0]].Translation;
					var mid = models[chain[1]].Translation;
					var foot = models[chain[2]].Translation;
					var axis = foot - hip;

					if (axis.LengthSquared() < 1e-6f)
					{
						continue;
					}

					var bend = mid - (hip + axis * (Vector3.Dot(mid - hip, axis) / axis.LengthSquared()));
					float reach = Vector3.Distance(hip, mid) + Vector3.Distance(mid, foot);

					if (bend.Length() > 0.02f * reach)
					{
						worstKnee = MathF.Max(worstKnee, Vector3.Dot(Vector3.Normalize(bend), Vector3.UnitZ));
					}
				}
			}

			footIk.Weight = 1f;

			bool broken = worstDistance < InsideThreshold || worstKnee > 0.3f;
			anyInside |= broken;

			Console.WriteLine($"[probe] character: свип веса в редакторе (dt=0) - худшая дистанция " +
				$"{worstDistance:0.#} ед. (вес {worstWeight:0.0}), худший изгиб {worstKnee:0.00} " +
				$"{(broken ? "ЛОМАЕТ ПОЗУ" : "OK")}");
		}

		// Тот же редакторский свип НА ПЕРЕПАДЕ: лиса краем на приступке, левые лапы выше правых -
		// IK работает по-настоящему, и частичный вес лерпит НАСТОЯЩИЕ коррекции. Поза - Walk на
		// ЗАМОРОЖЕННОМ времени (локомоушен выключен), как у лестничной лисы демо-сцены в режиме
		// редактирования - жалобы на вывернутые ползунком лапы приходят именно с неё.
		{
			ref var locomotion = ref fox.GetComponent<LocomotionComponent>();
			locomotion.Enabled = false;

			fox.GetComponent<Position>() = new Position(0.05f, 0.35f, -17f);

			ref var footIk = ref fox.GetComponent<FootIkComponent>();
			float worstKnee = float.MinValue;
			float worstKneeWeight = -1f;
			float worstDistance = float.MaxValue;
			float worstWeight = -1f;

			for (float weight = 0f; weight <= 1.001f; weight += 0.1f)
			{
				footIk.Weight = weight;

				for (int i = 0; i < 30; i++)
				{
					animation.BeginFrame();
					animation.Update(fox, PrefabSceneViewport.ComputeWorldMatrix(fox), 0f);
				}

				if (!animation.TryGetPose(fox.Id, out var models, out _))
				{
					continue;
				}

				var axisA = models[hips].Translation;
				var axisB = models[neck].Translation;

				foreach (int joint in probes)
				{
					float distance = DistanceToSegment(models[joint].Translation, axisA, axisB);
					if (distance < worstDistance)
					{
						worstDistance = distance;
						worstWeight = weight;
					}
				}

				float weightKnee = float.MinValue;
				float weightPaw = float.MaxValue;

				foreach (var chain in hindChains)
				{
					var hip = models[chain[0]].Translation;
					var mid = models[chain[1]].Translation;
					var foot = models[chain[2]].Translation;
					var axis = foot - hip;

					if (axis.LengthSquared() < 1e-6f)
					{
						continue;
					}

					var bend = mid - (hip + axis * (Vector3.Dot(mid - hip, axis) / axis.LengthSquared()));
					float reach = Vector3.Distance(hip, mid) + Vector3.Distance(mid, foot);

					if (bend.Length() > 0.02f * reach)
					{
						weightKnee = MathF.Max(weightKnee, Vector3.Dot(Vector3.Normalize(bend), Vector3.UnitZ));
					}
				}

				// «Задом наперёд» - направление СТУПНИ: горизонтальная проекция «скакательный
				// сустав → кончик лапы» у здоровой позы смотрит к морде (-Z модели), у вывернутой
				// ползунком - назад.
				foreach (var (hock, toe) in new[]
					{ (probes[6], probes[4]), (probes[7], probes[5]) })
				{
					var direction = models[toe].Translation - models[hock].Translation;
					direction.Y = 0f;

					if (direction.LengthSquared() > 1e-4f)
					{
						weightPaw = MathF.Min(weightPaw,
							Vector3.Dot(Vector3.Normalize(direction), -Vector3.UnitZ));
					}
				}

				Console.WriteLine($"[probe] character: перепад, вес {weight:0.0} - изгиб {weightKnee:0.00}, " +
					$"ступня к морде {weightPaw:0.00}");

				if (weightKnee > worstKnee)
				{
					worstKnee = weightKnee;
					worstKneeWeight = weight;
				}
			}

			footIk.Weight = 1f;

			// Порог здесь МЯГЧЕ общего (7, не 8): перепад 0.35 - боковая цель на пределе клампов
			// для ЧЕТЫРЁХ ног, и предплечье дальней стороны законно подходит к оси корпуса на 7.6
			// при чистом колене. «В теле» на этой позе начинается ниже.
			bool broken = worstDistance < 7f || worstKnee > 0.3f;
			anyInside |= broken;

			Console.WriteLine($"[probe] character: свип веса на перепаде (dt=0) - худшая дистанция " +
				$"{worstDistance:0.#} ед. (вес {worstWeight:0.0}), худший изгиб {worstKnee:0.00} " +
				$"(вес {worstKneeWeight:0.0}) {(broken ? "ЛОМАЕТ ПОЗУ" : "OK")}");
		}

		// Свип веса на УТОПЛЕННОЙ сущности - сценарий гизмо: автор перетащил персонажа чуть ниже
		// пола (в живом кадре это выглядит как «стоит по брюхо»). Земля под всеми лапами тогда ВЫШЕ
		// плоскости опоры клипа, и IK, умеющий только опускать таз, поджимает лапы в корпус при
		// любом весе - ровно жалоба «ноги задом наперёд, когда Weight выше нуля».
		{
			fox.GetComponent<Position>() = new Position(0f, -0.12f, -12f);

			ref var footIk = ref fox.GetComponent<FootIkComponent>();
			float worstKnee = float.MinValue;
			float worstDistance = float.MaxValue;

			for (float weight = 0f; weight <= 1.001f; weight += 0.25f)
			{
				footIk.Weight = weight;

				for (int i = 0; i < 30; i++)
				{
					animation.BeginFrame();
					animation.Update(fox, PrefabSceneViewport.ComputeWorldMatrix(fox), 0f);
				}

				if (!animation.TryGetPose(fox.Id, out var models, out _))
				{
					continue;
				}

				var axisA = models[hips].Translation;
				var axisB = models[neck].Translation;

				// БЕЗ носков (probes[4], probes[5]): поджатие - это ДОСЯГАЕМОСТЬ при поднятом тазе,
				// её меряют суставы цепочек. Носок после восстановления ориентации стопы (AlignFeet)
				// в глубоком сгибе легитимно уходит под корпус - он мерил бы ориентацию, не поджатие,
				// и честная поза давала 12.4 против калибровочных 22.
				for (int p = 0; p < probes.Length; p++)
				{
					if (p == 4 || p == 5)
					{
						continue;
					}

					worstDistance = MathF.Min(worstDistance,
						DistanceToSegment(models[probes[p]].Translation, axisA, axisB));
				}

				foreach (var chain in hindChains)
				{
					var hip = models[chain[0]].Translation;
					var mid = models[chain[1]].Translation;
					var foot = models[chain[2]].Translation;
					var axis = foot - hip;

					if (axis.LengthSquared() < 1e-6f)
					{
						continue;
					}

					var bend = mid - (hip + axis * (Vector3.Dot(mid - hip, axis) / axis.LengthSquared()));
					float reach = Vector3.Distance(hip, mid) + Vector3.Distance(mid, foot);

					if (bend.Length() > 0.02f * reach)
					{
						worstKnee = MathF.Max(worstKnee, Vector3.Dot(Vector3.Normalize(bend), Vector3.UnitZ));
					}
				}
			}

			footIk.Weight = 1f;

			// Порог здесь СВОЙ, теснее общего «в теле»: без подъёма таза лапы поджимались на треть
			// ноги (дистанция 16 против 22 у всплывшей позой) - глазами это «ноги задом», хотя до
			// «внутри мяса» не дотягивает. Утопленная сущность обязана ВСПЛЫТЬ позой на пол.
			bool broken = worstDistance < 19f || worstKnee > 0.3f;
			anyInside |= broken;

			Console.WriteLine($"[probe] character: свип веса УТОПЛЕННОЙ (dt=0) - худшая дистанция " +
				$"{worstDistance:0.#} ед., худший изгиб {worstKnee:0.00} " +
				$"{(broken ? "ПОДЖИМАЕТ ЛАПЫ" : "ВСПЛЫВАЕТ ПОЗОЙ OK")}");
		}

		// Доворот по нормали НА СКЛОНЕ - пара «доворот включён/выключен» на одной позе: на склоне
		// ~15° правильный доворот меняет ориентацию ступни на те же ~15°, и повороты ступней при
		// включении обязаны остаться МАЛЫМИ. Большая разница - сломанная композиция кватернионов
		// в AlignFeet, зона, где путаются все конвенции; горизонтальные свипы выше к ней слепы.
		{
			fox.GetComponent<Position>() = new Position(0f, 0.47f, -19f);

			ref var footIk = ref fox.GetComponent<FootIkComponent>();
			var pawWith = new Vector3[2];
			var pawWithout = new Vector3[2];

			foreach (bool align in new[] { false, true })
			{
				footIk.AlignToNormal = align;
				footIk.Weight = 1f;

				for (int i = 0; i < 30; i++)
				{
					animation.BeginFrame();
					animation.Update(fox, PrefabSceneViewport.ComputeWorldMatrix(fox), 0f);
				}

				if (!animation.TryGetPose(fox.Id, out var models, out _))
				{
					continue;
				}

				for (int leg = 0; leg < 2; leg++)
				{
					var direction = models[probes[4 + leg]].Translation - models[probes[6 + leg]].Translation;
					var target = align ? pawWith : pawWithout;
					target[leg] = direction.LengthSquared() > 1e-6f
						? Vector3.Normalize(direction)
						: Vector3.Zero;
				}
			}

			footIk.AlignToNormal = true;

			float worstTurn = 0f;
			for (int leg = 0; leg < 2; leg++)
			{
				if (pawWith[leg] != Vector3.Zero && pawWithout[leg] != Vector3.Zero)
				{
					worstTurn = MathF.Max(worstTurn, MathF.Acos(Math.Clamp(
						Vector3.Dot(pawWith[leg], pawWithout[leg]), -1f, 1f)) * 180f / MathF.PI);
				}
			}

			bool alignBroken = worstTurn > 40f;
			anyInside |= alignBroken;

			Console.WriteLine($"[probe] character: доворот по нормали на склоне - поворот ступни " +
				$"{worstTurn:0.#}° {(alignBroken ? "ВЫВОРАЧИВАЕТ СТУПНЮ" : "OK")}");
		}

		// Наклон корпуса на склоне ВДОЛЬ ТЕЛА (склон растёт по Z, тело лисы вдоль Z) - парой
		// вкл/выкл: разница углов оси корпуса (таз→шея) обязана быть ~углом склона. Плюс контакт
		// ПЕРЕДНИХ лап: без четвёртой пары ног и наклона лиса стояла горизонтально, зависнув
		// передними над рельефом, - жалоба со ступеней.
		// Поза - BIND, поворот - ЕДИНИЧНЫЙ: у замороженного кадра клипа корпус стоит диагонально, а
		// после фаз движения сущность остаётся ПОВЁРНУТОЙ приводом (FaceMotion пишет Rotation) - и
		// любые ожидания «склон вдоль тела» в мировых осях ложь. Оси скелета в bind (замерено):
		// тело вдоль Z, перед в -Z, бока по X.
		fox.GetComponent<Animator>().ClipName = string.Empty;
		fox.GetComponent<Rotation>() = new Rotation(0f, 0f, 0f, 1f);

		{
			fox.GetComponent<Position>() = new Position(0f, 0.47f, -23f);

			ref var footIk = ref fox.GetComponent<FootIkComponent>();
			float pitchWithout = 0f;
			float pitchWith = 0f;
			float worstPawGap = float.MinValue;

			foreach (bool tilt in new[] { false, true })
			{
				footIk.AlignBodyToSlope = tilt;

				for (int i = 0; i < 30; i++)
				{
					animation.BeginFrame();
					animation.Update(fox, PrefabSceneViewport.ComputeWorldMatrix(fox), 0f);
				}

				if (!animation.TryGetPose(fox.Id, out var models, out _))
				{
					continue;
				}

				var spine = models[neck].Translation - models[hips].Translation;
				float horizontal = MathF.Sqrt(spine.X * spine.X + spine.Z * spine.Z);
				float pitch = MathF.Atan2(spine.Y, MathF.Max(horizontal, 1e-4f)) * 180f / MathF.PI;

				if (tilt)
				{
					pitchWith = pitch;

					// Контакт передних лап с ПОВЕРХНОСТЬЮ СКЛОНА: y(z) = 0.2 + (z + 24) * 0.27.
					var world = PrefabSceneViewport.ComputeWorldMatrix(fox);
					foreach (int paw in new[] { probes[0], probes[1] })
					{
						var pawWorld = Vector3.Transform(models[paw].Translation, world);
						float surface = 0.2f + (pawWorld.Z + 24f) * 0.27f;
						worstPawGap = MathF.Max(worstPawGap, pawWorld.Y - surface);
					}
				}
				else
				{
					pitchWithout = pitch;
				}
			}

			footIk.AlignBodyToSlope = true;

			// Зазор лапы меряется ПО СУСТАВУ КИСТИ, а не по подошве: его природная высота в клипе
			// ~8 единиц (0.08 м), плюс недолёт на пределе цепочки. 0.25 - это «рядом со склоном»
			// против прежних «висят в воздухе»; главное утверждение здесь - дельта наклона.
			// ЗНАК обязателен: склон поднимается к ХВОСТУ (+Z), и нос обязан ОПУСТИТЬСЯ - угол оси
			// таз→шея упасть. Гейт по |дельте| был слеп к знаку, и инвертированный наклон (морда
			// задиралась В склон) годился ему так же, как правильный, - ровно он и жил в коде.
			float pitchDelta = MathF.Abs(pitchWith - pitchWithout);
			bool tiltBroken = pitchDelta < 6f || pitchDelta > 30f || worstPawGap > 0.25f ||
				pitchWith > pitchWithout;
			anyInside |= tiltBroken;

			Console.WriteLine($"[probe] character: наклон корпуса на склоне - {pitchWithout:0.#}° -> " +
				$"{pitchWith:0.#}° (дельта {pitchDelta:0.#}°), зазор передних лап {worstPawGap:0.###} м " +
				$"{(tiltBroken ? "КОРПУС НЕ ЛОЖИТСЯ/ЛАПЫ В ВОЗДУХЕ" : "OK")}");
		}

		// КРЕН на поперечном склоне (подъём вдоль X = поперёк тела-Z) - парой вкл/выкл: твист таза
		// вокруг оси тела обязан стать ~углом склона.
		{
			fox.GetComponent<Position>() = new Position(0f, 0.47f, -19f);

			ref var footIk = ref fox.GetComponent<FootIkComponent>();
			var pelvisRotations = new Quaternion[2];
			var pelvisLeans = new float[2];

			foreach (bool tilt in new[] { false, true })
			{
				footIk.AlignBodyToSlope = tilt;

				for (int i = 0; i < 30; i++)
				{
					animation.BeginFrame();
					animation.Update(fox, PrefabSceneViewport.ComputeWorldMatrix(fox), 0f);
				}

				if (!animation.TryGetPose(fox.Id, out var models, out _))
				{
					continue;
				}

				// Ориентация таза целиком: линии суставов у этого рига лежат не по анатомическим
				// осям (бедренные суставы разнесены вдоль ТЕЛА), и любая метрика «по двум точкам»
				// лжёт. Крен - это твист-компонента разницы поворотов таза вокруг оси тела.
				var m = models[hips];
				var x = Vector3.Normalize(new Vector3(m.M11, m.M12, m.M13));
				var y = Vector3.Normalize(new Vector3(m.M21, m.M22, m.M23));
				var z = Vector3.Normalize(new Vector3(m.M31, m.M32, m.M33));

				pelvisRotations[tilt ? 1 : 0] = Quaternion.CreateFromRotationMatrix(new Matrix4x4(
					x.X, x.Y, x.Z, 0f, y.X, y.Y, y.Z, 0f, z.X, z.Y, z.Z, 0f, 0f, 0f, 0f, 1f));

				// Наклон верха таза К СКЛОНУ - для ЗНАКА крена: склон поднимается к +X (левый бок
				// выше), корпус обязан лечь левым боком вверх, то есть верхняя ось таза - отклониться
				// К НИЗУ склона (-X). Дельта dot(up, +X) при включении обязана уйти в минус.
				pelvisLeans[tilt ? 1 : 0] = Vector3.Dot(y, Vector3.UnitX);
			}

			footIk.AlignBodyToSlope = true;

			// ПОЛНЫЙ угол дельты поворотов таза, не проекция на ось: при построчной конвенции
			// дельта сопрягается bind-ориентацией таза (в ней сидят корневые 90°), и ось твиста
			// уезжает с мировой Z куда угодно. Склон здесь поперёк тела - наклон нулевой, и весь
			// угол дельты по построению и есть крен.
			var deltaRotation = Quaternion.Normalize(
				pelvisRotations[1] * Quaternion.Inverse(pelvisRotations[0]));
			float rollDelta = 2f * MathF.Acos(Math.Clamp(MathF.Abs(deltaRotation.W), 0f, 1f)) *
				180f / MathF.PI;

			float leanDelta = pelvisLeans[1] - pelvisLeans[0];
			bool rollBroken = rollDelta < 6f || rollDelta > 30f || leanDelta >= -0.05f;
			anyInside |= rollBroken;

			Console.WriteLine($"[probe] character: крен на поперечном склоне - твист таза {rollDelta:0.#}°, " +
				$"наклон верха таза к склону {leanDelta:0.00} " +
				$"{(rollBroken ? "КРЕНА НЕТ/НЕ ТУДА" : "OK")}");
		}

		// Частичный бленд (OverlayClipComponent): шея играет Survey, ноги остаются в Walk. Пара
		// «без наложения / с наложением» на одном замороженном кадре: суставы ног обязаны совпасть
		// ТОЧНО - комплементарные посуставные веса не трогают базу вне поддерева, и любой сдвиг
		// лап означает, что маска протекает (или rest-поза подмешивается). Голова обязана УЙТИ -
		// мёртвое наложение неотличимо от протекающей маски только по ногам.
		{
			fox.GetComponent<Position>() = new Position(0f, 0f, -8f);
			fox.GetComponent<Animator>().ClipName = "Walk";

			int head = model.Skeleton.FindJoint("b_Head_05");
			var legsWithout = new Vector3[probes.Length];
			Vector3 headWithout = default;

			for (int pass = 0; pass < 2 && head >= 0; pass++)
			{
				if (pass == 1)
				{
					fox.AddComponent(new OverlayClipComponent
					{
						Enabled = true,
						ClipName = "Survey",
						RootJoint = "b_Neck_04",
						Weight = 1f,
					});
				}

				for (int i = 0; i < 30; i++)
				{
					animation.BeginFrame();
					animation.Update(fox, PrefabSceneViewport.ComputeWorldMatrix(fox), 0f);
				}

				if (!animation.TryGetPose(fox.Id, out var models, out _))
				{
					continue;
				}

				if (pass == 0)
				{
					for (int p = 0; p < probes.Length; p++)
					{
						legsWithout[p] = models[probes[p]].Translation;
					}

					headWithout = models[head].Translation;
				}
				else
				{
					float worstLeg = 0f;
					for (int p = 0; p < probes.Length; p++)
					{
						worstLeg = MathF.Max(worstLeg,
							Vector3.Distance(legsWithout[p], models[probes[p]].Translation));
					}

					float headMoved = Vector3.Distance(headWithout, models[head].Translation);

					bool overlayBroken = worstLeg > 0.01f || headMoved < 0.5f;
					anyInside |= overlayBroken;

					Console.WriteLine($"[probe] character: частичный бленд (Survey на шее) - " +
						$"лапы сдвинулись на {worstLeg:0.####} ед., голова на {headMoved:0.#} ед. " +
						$"{(overlayBroken ? "МАСКА ПРОТЕКАЕТ/НАЛОЖЕНИЕ МЕРТВО" : "OK")}");
				}
			}
		}

		// Root motion (Animator.RootMotion): СИНТЕТИЧЕСКИЙ клип - корень рига едет на 100 единиц
		// по +Z модели за 2 секунды, остальные кости молчат. Клипы Fox шагают на месте, и движения
		// корня в них нет по построению - настоящий путь проверяется только подложенным клипом.
		// Три утверждения: сущность прошла путь клипа (включая ЗАВОРОТ лупа - прогон 3 с на клипе
		// 2 с), путь без рывка на завороте (максимальный шаг за кадр ~ скорость клипа), а поза
		// осталась НА МЕСТЕ (компенсация: корень в пространстве модели не уехал).
		{
			int motionRoot = 0;
			while (model.Skeleton.Parents[motionRoot] >= 0)
			{
				motionRoot = model.Skeleton.Parents[motionRoot];
			}

			var motionTracks = new JointTrack[model.Skeleton.JointCount];
			for (int i = 0; i < motionTracks.Length; i++)
			{
				motionTracks[i] = new JointTrack();
			}

			var rootBind = model.Skeleton.BindLocals[motionRoot].position;
			motionTracks[motionRoot] = new JointTrack
			{
				TranslationTimes = [0f, 2f],
				Translations = [rootBind, rootBind + new Vector3(0f, 0f, 100f)],
			};

			var motionClip = new PreparedAnimation
			{
				Name = "MotionProbe",
				Duration = 2f,
				Tracks = motionTracks,
			};
			model.Animations.Add(motionClip);

			var walker = store.CreateEntity();
			walker.AddComponent(new EntityName("motion fox"));
			walker.AddComponent(new Position(5f, 0f, -8f));
			walker.AddComponent(new Rotation(0f, 0f, 0f, 1f));
			walker.AddComponent(new Scale3(0.01f, 0.01f, 0.01f));
			walker.AddComponent(new Animator { ClipName = "MotionProbe", RootMotion = true });

			animation.AddInstance(walker.Id, model, -1);
			animation.SetAvatar(walker.Id, HumanoidAutoMap.Build(model.Skeleton));

			float maxFrameStep = 0f;
			var previous = walker.GetComponent<Position>().value;

			for (int i = 0; i < 180; i++)
			{
				animation.BeginFrame();
				animation.Update(walker, PrefabSceneViewport.ComputeWorldMatrix(walker), Step);

				var current = walker.GetComponent<Position>().value;
				maxFrameStep = MathF.Max(maxFrameStep, Vector3.Distance(current, previous));
				previous = current;
			}

			float travelled = walker.GetComponent<Position>().value.Z - (-8f);
			float rootDrift = 0f;

			if (animation.TryGetPose(walker.Id, out var motionModels, out _))
			{
				var rootNow = motionModels[motionRoot].Translation;
				rootDrift = new Vector3(rootNow.X - rootBind.X, 0f, rootNow.Z - rootBind.Z).Length();
			}

			// 3 с на клипе 0.5 м/с = 1.5 м; шаг кадра ~8.3 мм. Дрейф корня - в ЕДИНИЦАХ МОДЕЛИ.
			bool motionBroken = MathF.Abs(travelled - 1.5f) > 0.03f || maxFrameStep > 0.05f ||
				rootDrift > 1f;
			anyInside |= motionBroken;

			Console.WriteLine($"[probe] character: root motion - путь {travelled:0.###} м (ожидалось 1.5), " +
				$"худший шаг кадра {maxFrameStep * 1000f:0.#} мм, дрейф корня в модели {rootDrift:0.###} ед. " +
				$"{(motionBroken ? "ПУТЬ/КОМПЕНСАЦИЯ СЛОМАНЫ" : "OK")}");
		}

		// Аддитив (AdditiveClipComponent): РАУНД-ТРИП дельты. База - Survey, замороженный на своём
		// ОПОРНОМ кадре (t=0), поверх - аддитивная дельта того же Survey полным весом на времени t.
		// По построению «опора + дельта(t)» обязана дать Survey@t: сверка с честно семплированным
		// Survey@t проверяет насквозь и конвертер (Conjugate(опора)×значение - зона конвенций
		// кватернионов, дважды стрелявшая в этом стеке), и досев каналов единицей, и additive-путь
		// шима. Заодно живость: результат обязан УЙТИ от опорного кадра.
		{
			var basePlus = store.CreateEntity();
			basePlus.AddComponent(new EntityName("additive fox"));
			basePlus.AddComponent(new Position(8f, 0f, -8f));
			basePlus.AddComponent(new Rotation(0f, 0f, 0f, 1f));
			basePlus.AddComponent(new Scale3(0.01f, 0.01f, 0.01f));
			basePlus.AddComponent(new Animator { ClipName = "Survey", Playing = false, Time = 0f });
			basePlus.AddComponent(new AdditiveClipComponent { ClipName = "Survey", Weight = 1f });

			var expected = store.CreateEntity();
			expected.AddComponent(new EntityName("additive fox expected"));
			expected.AddComponent(new Position(11f, 0f, -8f));
			expected.AddComponent(new Rotation(0f, 0f, 0f, 1f));
			expected.AddComponent(new Scale3(0.01f, 0.01f, 0.01f));
			expected.AddComponent(new Animator { ClipName = "Survey", Playing = false, Time = 0.5f });

			animation.AddInstance(basePlus.Id, model, -1);
			animation.AddInstance(expected.Id, model, -1);

			for (int i = 0; i < 30; i++)
			{
				animation.BeginFrame();
				animation.Update(basePlus, PrefabSceneViewport.ComputeWorldMatrix(basePlus), Step);
				animation.Update(expected, PrefabSceneViewport.ComputeWorldMatrix(expected), Step);
			}

			float worstJoint = float.MaxValue;

			if (animation.TryGetPose(basePlus.Id, out var actualModels, out _) &&
				animation.TryGetPose(expected.Id, out var expectedModels, out _))
			{
				worstJoint = 0f;
				for (int joint = 0; joint < model.Skeleton.JointCount; joint++)
				{
					worstJoint = MathF.Max(worstJoint, Vector3.Distance(
						actualModels[joint].Translation, expectedModels[joint].Translation));
				}
			}

			// «Мёртвый» аддитив ловится тем же сравнением: без дельты результат остался бы опорным
			// кадром и разошёлся бы с Survey@0.5 на весь размах осмотра. Допуск щедрее побитового:
			// и дельта, и оригинал прошли квантование ozz НЕЗАВИСИМО, плюс композиция двух
			// квантованных поворотов. 0.5 единицы (5 мм) - на порядок ниже видимого, «не та
			// конвенция» даёт десятки единиц.
			bool additiveBroken = worstJoint > 0.5f;
			anyInside |= additiveBroken;

			Console.WriteLine($"[probe] character: аддитив (раунд-трип дельты Survey) - худшее " +
				$"расхождение сустава {worstJoint:0.###} ед. {(additiveBroken ? "ДЕЛЬТА ВРЁТ" : "OK")}");
		}

		Console.WriteLine($"[probe] character: ИТОГ - {(anyInside ? "ЕСТЬ СОСТОЯНИЯ С НОГАМИ В ТЕЛЕ" : "все состояния чистые OK")}");

		void RunPhase((string Name, float Seconds, Func<float, PlayerInput> Input) phase, bool footIkEnabled,
			bool lockFeet, float weight = 1f)
		{
			ref var footIk = ref fox.GetComponent<FootIkComponent>();
			footIk.Enabled = footIkEnabled;
			footIk.LockFeet = lockFeet;
			footIk.Weight = weight;

			int steps = (int)MathF.Round(phase.Seconds / Step);
			float worst = float.MaxValue;
			float worstAt = 0f;
			float speedAtWorst = 0f;
			int worstJoint = -1;
			float worstKneeDot = float.MinValue;
			var infos = new List<AnimationDriver.CharacterInfo>();

			for (int i = 0; i < steps; i++)
			{
				float t = i * Step;

				motion.Input = phase.Input(t);
				motion.Steer(store, physics, active: true, Step, animation);
				physics.Update(Step);
				motion.Apply(store, physics);

				animation.BeginFrame();
				animation.Update(fox, PrefabSceneViewport.ComputeWorldMatrix(fox), Step);

				// Первые полсекунды фазы не меряются: переходное (кроссфейд аллюра, разгон,
				// подход к стене) - законно смешанное, вопрос пробника - УСТАНОВИВШЕЕСЯ состояние.
				if (t < 0.5f || !animation.TryGetPose(fox.Id, out var models, out _))
				{
					continue;
				}

				var axisA = models[hips].Translation;
				var axisB = models[neck].Translation;

				// Выворот: проекция изгиба КОЛЕНА (средний сустав цепочки) на +Z модели. Колено
				// лисы гнётся вперёд, к морде (-Z): у честной позы проекция около -1 (замерено -1.0
				// во всех фазах, включая стойку и бег без IK); уверенно ПОЛОЖИТЕЛЬНАЯ - колено
				// защёлкнулось в обратную сторону. Совсем прямая нога (изгиб меньше 2% длины) не
				// меряется: знак шума - не выворот.
				foreach (var chain in hindChains)
				{
					var hip = models[chain[0]].Translation;
					var mid = models[chain[1]].Translation;
					var foot = models[chain[2]].Translation;

					var axis = foot - hip;
					float lengthSquared = axis.LengthSquared();
					if (lengthSquared < 1e-6f)
					{
						continue;
					}

					var bend = mid - (hip + axis * (Vector3.Dot(mid - hip, axis) / lengthSquared));
					float reach = Vector3.Distance(hip, mid) + Vector3.Distance(mid, foot);

					if (bend.Length() > 0.02f * reach)
					{
						worstKneeDot = MathF.Max(worstKneeDot,
							Vector3.Dot(Vector3.Normalize(bend), Vector3.UnitZ));
					}
				}

				foreach (int joint in probes)
				{
					float distance = DistanceToSegment(models[joint].Translation, axisA, axisB);
					if (distance < worst)
					{
						worst = distance;
						worstAt = t;
						worstJoint = joint;

						animation.DescribeCharacters(infos);
						foreach (var info in infos)
						{
							if (info.EntityId == fox.Id)
							{
								speedAtWorst = info.LocoSpeed;
							}
						}
					}
				}
			}

			// Допуск +0.3, а не ноль: изгиб честной позы может уходить вбок (проекция мала), а
			// выворот - это уверенно ПОЛОЖИТЕЛЬНАЯ проекция, колено согнуто назад.
			bool inverted = worstKneeDot > 0.3f;
			bool inside = worst < InsideThreshold || inverted;
			anyInside |= inside;

			string worstName = worstJoint >= 0 ? model.Skeleton.JointNames[worstJoint] : "-";

			if (!strideReported)
			{
				animation.DescribeCharacters(infos);
				foreach (var info in infos)
				{
					if (info.EntityId == fox.Id && info.Locomotion)
					{
						Console.WriteLine($"[probe] character: природные скорости клипов - " +
							$"walk {info.LocoWalkStride:0.#} ед/с, run {info.LocoRunStride:0.#} ед/с " +
							$"(тело 1 м/с = {1f / 0.01f:0} ед/с)");
						strideReported = true;
					}
				}
			}

			Console.WriteLine($"[probe] character: фаза '{phase.Name}' - мин. дистанция лапа-корпус " +
				$"{worst:0.#} ед. ({worstName}, t={worstAt:0.0}, скорость {speedAtWorst:0.00} м/с), " +
				$"изгиб колена {worstKneeDot:0.00} " +
				$"{(inverted ? "КОЛЕНО ВЫВЕРНУТО" : worst < InsideThreshold ? "НОГА В ТЕЛЕ" : "OK")}");
		}
	}

	private static void BuildPlane(ScenePhysics physics)
	{
		var vertices = new List<Vector3>();
		var indices = new List<uint>();

		AddQuad(vertices, indices,
			new Vector3(-25f, 0f, -25f), new Vector3(-25f, 0f, 25f),
			new Vector3(25f, 0f, 25f), new Vector3(25f, 0f, -25f));

		// Стена вдоль z - в неё упираются обе тёрки. Высокая: step-up не должен её взять.
		AddQuad(vertices, indices,
			new Vector3(2.5f, 0f, -25f), new Vector3(2.5f, 2f, -25f),
			new Vector3(2.5f, 2f, 25f), new Vector3(2.5f, 0f, 25f));

		// Приступок для свипа веса на ПЕРЕПАДЕ (в стороне от маршрутов фаз): лиса встаёт на его
		// край, левые лапы на 0.16 выше правых, и foot IK реально работает - на ровном полу он
		// тождественный, и свип веса там ничего не проверяет.
		AddQuad(vertices, indices,
			new Vector3(0f, 0.16f, -15.4f), new Vector3(0f, 0.16f, -14.6f),
			new Vector3(0.4f, 0.16f, -14.6f), new Vector3(0.4f, 0.16f, -15.4f));
		AddQuad(vertices, indices,
			new Vector3(0f, 0f, -15.4f), new Vector3(0f, 0.16f, -15.4f),
			new Vector3(0f, 0.16f, -14.6f), new Vector3(0f, 0f, -14.6f));

		// Высокий приступок (0.35, как перепад лестничной лисы демо-сцены) - глубокая цель на
		// пределе кламповки, где нога вытягивается почти в струну.
		AddQuad(vertices, indices,
			new Vector3(0f, 0.35f, -17.4f), new Vector3(0f, 0.35f, -16.6f),
			new Vector3(0.4f, 0.35f, -16.6f), new Vector3(0.4f, 0.35f, -17.4f));
		AddQuad(vertices, indices,
			new Vector3(0f, 0f, -17.4f), new Vector3(0f, 0.35f, -17.4f),
			new Vector3(0f, 0.35f, -16.6f), new Vector3(0f, 0f, -16.6f));

		// Наклонная площадка ~15° для доворота по нормали и НАКЛОНА КОРПУСА. Подъём - ВДОЛЬ ОСИ
		// СКЕЛЕТА лисы: в пространстве модели её перед-зад лежит по X (замерено лучами: лапы
		// раскинуты по X на ±42 единицы), и склон поперёк этой оси для наклона корпуса неотличим
		// от горизонтали - первая версия склона вдоль Z намерила ровно проекцию, 4.4° из 15.
		AddQuad(vertices, indices,
			new Vector3(-1f, 0.2f, -20f), new Vector3(-1f, 0.2f, -18f),
			new Vector3(1f, 0.74f, -18f), new Vector3(1f, 0.74f, -20f));

		// Поперечный склон (подъём вдоль Z = поперёк оси скелета) - для КРЕНА корпуса: персонаж
		// боком к лестнице без roll держит корпус горизонтальным.
		AddQuad(vertices, indices,
			new Vector3(-1f, 0.2f, -24f), new Vector3(-1f, 0.74f, -22f),
			new Vector3(1f, 0.74f, -22f), new Vector3(1f, 0.2f, -24f));

		physics.BeginStatics();
		physics.AddStaticMesh(
			System.Runtime.InteropServices.CollectionsMarshal.AsSpan(vertices),
			System.Runtime.InteropServices.CollectionsMarshal.AsSpan(indices));
		physics.EndStatics();
	}

	private static void AddQuad(List<Vector3> vertices, List<uint> indices, Vector3 a, Vector3 b,
		Vector3 c, Vector3 d)
	{
		uint start = (uint)vertices.Count;
		vertices.Add(a);
		vertices.Add(b);
		vertices.Add(c);
		vertices.Add(d);

		indices.Add(start);
		indices.Add(start + 1);
		indices.Add(start + 2);
		indices.Add(start);
		indices.Add(start + 2);
		indices.Add(start + 3);
	}

	private static float DistanceToSegment(Vector3 point, Vector3 a, Vector3 b)
	{
		var axis = b - a;
		float length = axis.LengthSquared();

		if (length < 1e-8f)
		{
			return Vector3.Distance(point, a);
		}

		float t = Math.Clamp(Vector3.Dot(point - a, axis) / length, 0f, 1f);
		return Vector3.Distance(point, a + axis * t);
	}
}
