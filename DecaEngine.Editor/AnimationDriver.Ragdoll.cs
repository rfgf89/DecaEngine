using System;
using System.Collections.Generic;
using System.Numerics;
using DecaEngine.Animation;
using DecaEngine.Core;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
using DecaEngine.Physics;
using DecaEngine.Scene;
using Friflo.Engine.ECS;

// В Friflo есть свой Transform-компонент, а поза скелета оперирует TRS движка - без явного алиаса
// имя разрешается неоднозначно.
using Transform = DecaEngine.Core.Transform;

namespace DecaEngine.Editor;

/// <summary>Рэгдолл: синхронизация с Bepu, сборка описания тела, замер радиусов костей и масс. Часть <see cref="AnimationDriver"/> - файл на тему; состояние
/// персонажа (Character) и кадровый Update живут в основном файле.</summary>
public sealed partial class AnimationDriver
{
	/// <summary>
	/// Ведёт рэгдолл персонажа: собирает и разбирает его по компоненту, гонит тела к позе анимации и
	/// - в физическом режиме - читает позу обратно из тел.
	///
	/// Идёт ПОСЛЕДНЕЙ стадией: рэгдолл либо получает готовую позу как цель, либо целиком её
	/// заменяет, и обе роли требуют, чтобы поза к этому моменту была окончательной.
	/// </summary>
	private void SyncRagdoll(Entity entity, Character character, float deltaSeconds)
	{
		bool wanted = Physics != null && entity.HasComponent<RagdollComponent>();
		var settings = wanted ? entity.GetComponent<RagdollComponent>() : default;

		// Хит-реакция живёт ПОВЕРХ компонента: у идущего персонажа рэгдолл авторски выключен (и
		// FallRecover гасит его каждый кадр), а реагировать на удар он обязан всё равно. Конверт
		// тикает здесь же - реакция без единого кадра рэгдолла обязана истечь, а не висеть вечно.
		bool reacting = wanted && character.ReactionDuration > 0f;
		if (reacting)
		{
			character.ReactionElapsed += deltaSeconds;
			if (character.ReactionElapsed >= character.ReactionDuration)
			{
				character.ReactionDuration = 0f;
				character.ReactionImpulsePending = false;
				reacting = false;
			}
		}

		character.ReactionWeight = 0f;
		character.ReactionDeviation = 0f;

		if (!wanted || (!settings.Enabled && !reacting))
		{
			DestroyRagdoll(character);
			return;
		}

		float worldScale = WorldScaleOf(character.ModelToWorld);

		if (!character.RagdollBuilt || !SameRagdollSource(character.RagdollSource, settings) ||
			!SameScale(character.RagdollBuildScale, worldScale))
		{
			DestroyRagdoll(character);
			BuildRagdoll(character, settings);

			character.RagdollSource = settings;
			character.RagdollBuildScale = worldScale;
			character.RagdollBuilt = true;
		}

		var ragdoll = character.Ragdoll;
		if (ragdoll == null)
		{
			return;
		}

		ActiveRagdollCount++;

		// Цель - поза анимации В МИРЕ. Считается ДО чтения из тел: в физическом режиме чтение
		// затрёт character.Models, а сервоприводам нужна именно анимационная цель.
		for (int i = 0; i < character.Models.Length; i++)
		{
			character.JointWorld[i] = character.Models[i] * character.ModelToWorld;
		}

		// Реакция переводит тела в физику с СИЛЬНЫМИ сервоприводами: они тянут корпус обратно к
		// анимации, и толчок читается как «качнулся и выправился», а не «обмяк». Настоящее падение
		// (Physical по компоненту) сильнее реакции: там поза целиком из тел, и подмешивать нечего.
		bool reactionDrives = reacting && !settings.Physical;

		ragdoll.SetAnimationDriven(!settings.Physical && !reactionDrives);
		ragdoll.DriveToPose(character.JointWorld, deltaSeconds,
			reactionDrives ? ReactionServoStrength : settings.ServoStrength);

		if (reactionDrives && character.ReactionImpulsePending)
		{
			EnsureReactionMask(character);
			ragdoll.AddVelocity(character.ReactionImpulse, character.ReactionMask);
			character.ReactionImpulsePending = false;
		}

		if (settings.Physical)
		{
			ReadRagdollPose(character, ragdoll);
		}
		else if (reactionDrives)
		{
			BlendReactionPose(character, ragdoll);
		}
	}

	/// <summary>Сила сервоприводов реакции. Порядок величины - как у демонстрационного active
	/// ragdoll (60): достаточно, чтобы корпус вернулся к анимации за доли секунды, и мало,
	/// чтобы толчок вообще был виден.</summary>
	private const float ReactionServoStrength = 60f;

	/// <summary>
	/// Подмешивает позу тел к анимации по маске и конверту. Ноги в маске нулевые - они продолжают
	/// идти анимацией (и foot IK уже отработал по ней); смешиваются РАЗЛОЖЕННЫЕ TRS по той же
	/// причине, что и в подъёме: интерполяция матриц поворота напрямую плющит кости на полпути.
	/// </summary>
	private static void BlendReactionPose(Character character, Ragdoll ragdoll)
	{
		EnsureReactionMask(character);

		if (character.ReactionAnimated.Length != character.Models.Length)
		{
			character.ReactionAnimated = new Matrix4x4[character.Models.Length];
		}

		character.Models.CopyTo(character.ReactionAnimated, 0);
		ReadRagdollPose(character, ragdoll);

		// Конверт: быстрая атака (толчок обязан быть виден сразу) и плавный спад до конца реакции.
		float t = Math.Clamp(character.ReactionElapsed / character.ReactionDuration, 0f, 1f);
		float attack = Math.Clamp(character.ReactionElapsed / ReactionAttackSeconds, 0f, 1f);
		float release = 1f - t * t * (3f - 2f * t);
		float envelope = character.ReactionStrength * attack * release;

		character.ReactionWeight = envelope;

		float deviation = 0f;

		for (int i = 0; i < character.Models.Length; i++)
		{
			float weight = envelope * character.ReactionMask[i];
			var animated = character.ReactionAnimated[i];

			if (weight <= 1e-4f)
			{
				character.Models[i] = animated;
				continue;
			}

			if (!Matrix4x4.Decompose(character.Models[i], out var scale, out var rotation, out var translation) ||
				!Matrix4x4.Decompose(animated, out var animScale, out var animRotation, out var animTranslation))
			{
				character.Models[i] = animated;
				continue;
			}

			character.Models[i] = MathUtils.CreateTrs(
				Vector3.Lerp(animTranslation, translation, weight),
				Quaternion.Slerp(animRotation, rotation, weight),
				Vector3.Lerp(animScale, scale, weight));

			deviation = MathF.Max(deviation,
				Vector3.Distance(character.Models[i].Translation, animTranslation));
		}

		character.ReactionDeviation = deviation;
		character.Models.CopyTo(character.Managed.ModelMatrices, 0);
	}

	/// <summary>
	/// Маска реакции по humanoid-разметке: конечности (все шесть цепочек слотов и их поддеревья)
	/// нулевые, таз приглушён (его качает и так - через корпус), остальное единица. Без разметки
	/// маска целиком единичная - реакция честно качает всего персонажа, что хуже, но видно.
	/// </summary>
	private static void EnsureReactionMask(Character character)
	{
		if (character.ReactionMaskBuilt && character.ReactionMask.Length == character.Skeleton.JointCount)
		{
			return;
		}

		int count = character.Skeleton.JointCount;
		character.ReactionMask = new float[count];
		Array.Fill(character.ReactionMask, 1f);

		if (character.Avatar != null)
		{
			ReadOnlySpan<HumanoidBone> limbs =
			[
				HumanoidBone.LeftUpperArm, HumanoidBone.LeftLowerArm, HumanoidBone.LeftHand,
				HumanoidBone.RightUpperArm, HumanoidBone.RightLowerArm, HumanoidBone.RightHand,
				HumanoidBone.LeftUpperLeg, HumanoidBone.LeftLowerLeg, HumanoidBone.LeftFoot,
				HumanoidBone.RightUpperLeg, HumanoidBone.RightLowerLeg, HumanoidBone.RightFoot,
			];

			foreach (var slot in limbs)
			{
				int joint = character.Skeleton.FindJoint(character.Avatar[slot] ?? string.Empty);
				if (joint >= 0)
				{
					character.ReactionMask[joint] = 0f;
				}
			}

			int hips = character.Skeleton.FindJoint(character.Avatar[HumanoidBone.Hips] ?? string.Empty);
			if (hips >= 0)
			{
				character.ReactionMask[hips] = 0.3f;
			}

			// Поддеревья обнулённых костей (пальцы под кистью): джойнты топологически упорядочены,
			// одного прохода хватает. Нулевой РОДИТЕЛЬ обнуляет ребёнка - но только нулевой:
			// приглушённый таз своих детей не глушит, ноги обнулены явно, а корпус растёт из него
			// с полным весом.
			var parents = character.Skeleton.Parents;
			for (int i = 0; i < count; i++)
			{
				if (parents[i] >= 0 && character.ReactionMask[parents[i]] == 0f)
				{
					character.ReactionMask[i] = 0f;
				}
			}
		}

		character.ReactionMaskBuilt = true;
	}

	/// <summary>
	/// Переносит позу из тел рэгдолла обратно в пространство модели.
	///
	/// Джойнты, у которых тела НЕТ (пальцы, кости хвоста, всё, что глубже MaxDepth), пересчитываются
	/// от родителя по локальной TRS. Без этого они остались бы там, где их оставила анимация, -
	/// то есть у лежащего персонажа кисти висели бы в воздухе на месте стоящей позы. Один проход по
	/// массиву достаточен: джойнты топологически упорядочены, родитель к моменту обработки ребёнка
	/// уже посчитан.
	/// </summary>
	private static void ReadRagdollPose(Character character, Ragdoll ragdoll)
	{
		if (!Matrix4x4.Invert(character.ModelToWorld, out var worldToModel))
		{
			return;
		}

		character.JointWorld.CopyTo(character.RagdollWorld, 0);
		ragdoll.ReadPose(character.RagdollWorld);

		Array.Clear(character.RagdollOwned);
		for (int i = 0; i < ragdoll.BoneCount; i++)
		{
			character.RagdollOwned[ragdoll.JointOf(i)] = true;
		}

		var parents = character.Skeleton.Parents;

		// Поза тела Bepu ЖЁСТКАЯ - поворот и позиция, масштаб единичный, - а worldToModel несёт
		// ОБРАТНЫЙ масштаб сущности. Голое произведение RagdollWorld * worldToModel даёт модельную
		// матрицу с масштабом 1/scale в линейной части: позиция кости переводится в модельные
		// единицы правильно, но каждый привязанный к кости офсет вершины раздувается в те же 1/scale
		// раз. При масштабе лисы 0.01 это персонаж, разорванный в СТО раз (замерено headless-прогоном
		// сцены: деформированный габарит 9501 при bind 175, и уже на ПЕРВОМ кадре физики - это не
		// разлёт симуляции, а чистая ошибка пространства). Домножение на масштаб слева гасит его в
		// линейной части, не трогая перевод позиции: строка трансляции у скейл-матрицы единичная.
		var counterScale = Matrix4x4.CreateScale(WorldScaleOf(character.ModelToWorld));

		for (int i = 0; i < character.Models.Length; i++)
		{
			if (character.RagdollOwned[i])
			{
				character.Models[i] = counterScale * character.RagdollWorld[i] * worldToModel;
				continue;
			}

			var local = character.Locals[i];
			// Полным именем: MathUtils есть в нескольких пространствах имён движка, и короткое имя
			// разрешается не в то.
			var localMatrix = MathUtils.CreateTrs(
				local.position, local.rotation, local.scale);

			character.Models[i] = parents[i] >= 0
				? localMatrix * character.Models[parents[i]]
				: localMatrix;
		}

		character.Models.CopyTo(character.Managed.ModelMatrices, 0);
	}

	private static void DestroyRagdoll(Character character)
	{
		character.Ragdoll?.Destroy();
		character.Ragdoll = null;
		character.RagdollBuilt = false;
	}

	/// <summary>Совпадает ли СТРОЕНИЕ рэгдолла. Physical и ServoStrength сюда не входят: это ручки
	/// режима, и пересобирать на них тела значило бы ронять персонажа заново на каждом кадре, пока
	/// ползунок силы сервоприводов под курсором.</summary>
	/// <summary>Средний масштаб трансформа - длина осей его линейной части. Средний, а не покомпонентный:
	/// рэгдолл всё равно строится изотропным (капсула Bepu не умеет неравномерного масштаба), и
	/// сравнивать по осям значило бы обещать точность, которой в сборке нет.</summary>
	private static float WorldScaleOf(in Matrix4x4 transform)
	{
		float x = new Vector3(transform.M11, transform.M12, transform.M13).Length();
		float y = new Vector3(transform.M21, transform.M22, transform.M23).Length();
		float z = new Vector3(transform.M31, transform.M32, transform.M33).Length();

		return (x + y + z) / 3f;
	}

	/// <summary>Сравнение масштабов ОТНОСИТЕЛЬНОЕ и с мёртвой зоной. Точное сравнение здесь недопустимо:
	/// масштаб приезжает из разложения матрицы, его младшие разряды шумят на уровне 1e-7, и рэгдолл
	/// пересобирался бы каждый кадр - то есть персонаж падал бы заново на каждом кадре, ни разу не
	/// успев упасть.</summary>
	private static bool SameScale(float a, float b) =>
		MathF.Abs(a - b) <= 1e-3f * MathF.Max(MathF.Abs(a), MathF.Abs(b));

	private static bool SameRagdollSource(in RagdollComponent a, in RagdollComponent b) =>
		string.Equals(a.RootJoint, b.RootJoint, StringComparison.Ordinal) &&
		a.MaxDepth == b.MaxDepth && a.BoneRadius == b.BoneRadius && a.TotalMass == b.TotalMass;

	private void BuildRagdoll(Character character, in RagdollComponent settings)
	{
		if (Physics == null)
		{
			return;
		}

		var description = BuildRagdollDescription(character, settings, WorldScaleOf(character.ModelToWorld));
		if (description.Count < 2)
		{
			// Рэгдолл из одной кости - это не рэгдолл, а падающая капсула. Молча его не собираем:
			// собранный он выглядел бы как «работает», и разбираться, почему персонаж не гнётся,
			// пришлось бы в физике, а не в имени корневой кости.
			return;
		}

		for (int i = 0; i < character.Models.Length; i++)
		{
			character.JointWorld[i] = character.Models[i] * character.ModelToWorld;
		}

		MarkHingeBones(character, description);

		character.Ragdoll = Ragdoll.Build(Physics.World,
			System.Runtime.InteropServices.CollectionsMarshal.AsSpan(description), character.JointWorld);
	}

	/// <summary>
	/// Колени и локти по humanoid-разметке становятся ШАРНИРАМИ (см.
	/// <see cref="RagdollBoneDesc.HingeAxisWorld"/>): ball-socket с конусом разрешает согнуть их
	/// назад, и упавший персонаж заламывает конечности, не нарушая ни одного предела. Ось и диапазон
	/// считает <see cref="Ragdoll.MarkHinge"/> из позы сборки; без разметки (или с прямой в момент
	/// сборки конечностью) сустав остаётся конусным - хуже, но не сломано.
	/// </summary>
	private static void MarkHingeBones(Character character, List<RagdollBoneDesc> description)
	{
		if (character.Avatar == null)
		{
			return;
		}

		ReadOnlySpan<HumanoidBone> hinges =
		[
			HumanoidBone.LeftLowerLeg, HumanoidBone.RightLowerLeg,
			HumanoidBone.LeftLowerArm, HumanoidBone.RightLowerArm,
		];

		foreach (var slot in hinges)
		{
			int joint = character.Skeleton.FindJoint(character.Avatar[slot] ?? string.Empty);
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

				// «Верх» - джойнт РОДИТЕЛЬСКОЙ КОСТИ РЭГДОЛЛА, а не родительский джойнт скелета:
				// шарнир связывает именно эти два тела, и ось из пропущенного звена была бы осью
				// не того сустава.
				Ragdoll.MarkHinge(ref bone,
					character.JointWorld[description[bone.Parent].Joint].Translation,
					character.JointWorld[bone.Joint].Translation,
					character.JointWorld[bone.ChildJoint].Translation);

				description[i] = bone;
				break;
			}
		}
	}

	/// <summary>
	/// Строит описание рэгдолла обходом скелета от корневой кости вглубь до <c>MaxDepth</c>. Костью
	/// рэгдолла становится каждый посещённый джойнт, У КОТОРОГО ЕСТЬ РЕБЁНОК: концевые джойнты
	/// (кончики пальцев, макушка) задают только длину родительской капсулы и своего тела не
	/// получают - иначе у персонажа выросли бы висящие ни на чём обрубки.
	///
	/// Автоматика здесь допустима ровно потому, что глубину задаёт автор: это его способ сказать
	/// «дальше кости служебные». Полный обход рига дал бы двести тел вместо двадцати.
	/// </summary>
	private static List<RagdollBoneDesc> BuildRagdollDescription(Character character,
		in RagdollComponent settings, float worldScale)
	{
		var result = new List<RagdollBoneDesc>();
		var skeleton = character.Skeleton;

		// Корень рэгдолла - заданный автором, иначе таз из humanoid-разметки, иначе просто корень
		// скелета. Последнее - именно фолбэк, а не выбор: у рига со служебным корнем («Armature»)
		// рэгдолл от него получит лишнее звено, но это лучше, чем не собраться вовсе.
		string rootName = JointOf(character, settings.RootJoint, HumanoidBone.Hips);
		int root = string.IsNullOrEmpty(rootName) ? 0 : skeleton.FindJoint(rootName);

		if (root < 0)
		{
			return result;
		}

		// Радиус капсулы каждой кости - ИЗ МЕША: средневзвешенное расстояние привязанных к джойнту
		// вершин до оси кости. Один радиус на весь скелет (прежняя схема) не соответствует телу по
		// построению: туловище лисы втрое толще лапы, и капсулы либо тонут в туловище (персонаж
		// лежит наполовину В полу - замерено: таз на y=0.018 при видимой толщине корпуса ~0.15 м),
		// либо распирают лапы. Авторское BoneRadius > 0 остаётся принудительным override на весь
		// скелет - под риги без скин-стрима и под намеренную стилизацию.
		float authoredRadius = settings.BoneRadius;
		var meshRadii = authoredRadius > 0f ? [] : MeasureBoneRadii(character);

		// Радиусы - в единицах МОДЕЛИ (и мешевые, и авторский: автор видит скелет в них же), в мир
		// переводятся масштабом сущности. Длины костей приезжают из мировых матриц джойнтов, то есть
		// уже отмасштабированными. Фолбэк - доля характерного размера скелета: масштаб моделей
		// произволен, и любая константа осмысленна ровно для одного из них.
		float RadiusOf(int joint)
		{
			if (authoredRadius > 0f)
			{
				return authoredRadius * worldScale;
			}

			float measured = joint < meshRadii.Length ? meshRadii[joint] : 0f;
			return (measured > 1e-4f ? measured : character.Scale * 0.12f) * worldScale;
		}

		// Индекс кости рэгдолла по джойнту - чтобы найти РОДИТЕЛЬСКУЮ КОСТЬ, а не родительский
		// джойнт: между двумя костями рэгдолла обычно есть пропущенные звенья скелета.
		var boneOfJoint = new Dictionary<int, int>();

		var queue = new Queue<(int Joint, int Depth, int ParentBone)>();
		queue.Enqueue((root, 0, -1));

		while (queue.Count > 0)
		{
			var (joint, depth, parentBone) = queue.Dequeue();

			int child = FirstChild(skeleton, joint);
			int bone = parentBone;

			if (child >= 0)
			{
				bone = result.Count;
				boneOfJoint[joint] = bone;

				result.Add(new RagdollBoneDesc
				{
					Joint = joint,
					ChildJoint = child,
					Parent = parentBone,
					Radius = RadiusOf(joint),

					// Запасная длина концевой кости - тоже в мире: её берут капсулы джойнтов без
					// ребёнка (голова, кисть), и в пространстве модели она была бы в разы длиннее.
					Length = character.Scale * worldScale,

					// Предел отклонения в суставе - не жёсткий и не свободный: 120 градусов размаха
					// не мешают конечности лечь естественно, но не дают ей вывернуться назад через
					// сустав, из-за чего рэгдолл выглядит сломанным, а не мёртвым.
					SwingLimitCos = -0.5f,

					// Скручивание - ДРУГАЯ степень свободы, конусом не ограниченная вовсе: без этого
					// предела кость проворачивается вокруг себя на любой угол, формально оставаясь
					// внутри конуса, и лапа выглядит вывернутой. 50° - примерно предел живого сустава
					// на звено; ровно столько и остаётся, если не гнаться за анатомией конкретного
					// рига, которой у произвольной модели всё равно нет.
					TwistLimitAngle = 50f * (MathF.PI / 180f),
				});
			}

			if (depth >= settings.MaxDepth)
			{
				continue;
			}

			for (int i = joint + 1; i < skeleton.JointCount; i++)
			{
				if (skeleton.Parents[i] == joint)
				{
					queue.Enqueue((i, depth + 1, bone));
				}
			}
		}

		DistributeMass(result, settings.TotalMass);
		return result;
	}

	/// <summary>
	/// Толщина каждой кости ПО МЕШУ: средневзвешенное перпендикулярное расстояние от привязанных к
	/// джойнту вершин до оси кости (джойнт → первый ребёнок), в единицах модели, в bind-позе.
	///
	/// Средневзвешенное, а не максимум: вершины лежат НА поверхности части тела, и их средняя
	/// дистанция до оси - это и есть её радиус; максимум цеплял бы вершины смежных частей, слабо
	/// привязанные к кости на стыке. Влияния легче 0.3 не считаются вовсе - вершина стыка, поровну
	/// разделённая между двумя костями, говорит о толщине обеих хуже, чем «своя» вершина о своей.
	/// </summary>
	private static unsafe float[] MeasureBoneRadii(Character character)
	{
		var skeleton = character.Skeleton;
		int count = skeleton.JointCount;

		// Модельные матрицы bind-позы. Managed-поза не годится: к моменту пересборки рэгдолла в ней
		// уже текущий кадр клипа, и радиусы гуляли бы от позы к позе.
		var bind = new Matrix4x4[count];
		for (int i = 0; i < count; i++)
		{
			var local = skeleton.BindLocals[i];
			var matrix = MathUtils.CreateTrs(
				local.position, local.rotation, local.scale);
			bind[i] = skeleton.Parents[i] >= 0 ? matrix * bind[skeleton.Parents[i]] : matrix;
		}

		var sum = new float[count];
		var weight = new float[count];
		var model = character.Model;

		void Accumulate(int joint, ushort rawWeight, Vector3 position)
		{
			float w = rawWeight / SkinVertex.WeightScale;
			if (w < 0.3f || joint >= count)
			{
				return;
			}

			var start = bind[joint].Translation;
			int child = FirstChild(skeleton, joint);
			var end = child >= 0 ? bind[child].Translation : start;

			var axis = end - start;
			float lengthSq = axis.LengthSquared();
			float t = lengthSq > 1e-8f
				? Math.Clamp(Vector3.Dot(position - start, axis) / lengthSq, 0f, 1f)
				: 0f;

			sum[joint] += Vector3.Distance(position, start + axis * t) * w;
			weight[joint] += w;
		}

		for (int meshIndex = 0; meshIndex < model.Meshes.Count; meshIndex++)
		{
			var skinStream = meshIndex < model.MeshSkin.Count ? model.MeshSkin[meshIndex] : null;
			var mesh = model.Meshes[meshIndex];

			if (skinStream == null || mesh.VertexData == null)
			{
				continue;
			}

			int vertexCount = Math.Min(
				UnsafeCollections.Collections.Unsafe.UnsafeArray.GetLength(mesh.VertexData),
				skinStream.Length);
			var vertices = new ReadOnlySpan<Vertex>(
				UnsafeCollections.Collections.Unsafe.UnsafeArray.GetPtr<Vertex>(mesh.VertexData, 0),
				vertexCount);

			for (int v = 0; v < vertexCount; v++)
			{
				var s = skinStream[v];
				var position = vertices[v].Position;

				Accumulate(s.J0, s.W0, position);
				Accumulate(s.J1, s.W1, position);
				Accumulate(s.J2, s.W2, position);
				Accumulate(s.J3, s.W3, position);
			}
		}

		var radii = new float[count];
		for (int i = 0; i < count; i++)
		{
			radii[i] = weight[i] > 0f ? sum[i] / weight[i] : 0f;
		}

		return radii;
	}

	/// <summary>Раскладывает общую массу по костям пропорционально ОБЪЁМУ капсулы. Поровну нельзя:
	/// голова весила бы столько же, сколько таз, и персонаж падал бы, кувыркаясь через голову.</summary>
	private static void DistributeMass(List<RagdollBoneDesc> bones, float totalMass)
	{
		if (bones.Count == 0)
		{
			return;
		}

		float mass = totalMass > 0f ? totalMass : 70f;
		float sum = 0f;

		Span<float> volumes = bones.Count <= 64 ? stackalloc float[bones.Count] : new float[bones.Count];

		for (int i = 0; i < bones.Count; i++)
		{
			float radius = MathF.Max(bones[i].Radius, 1e-4f);
			volumes[i] = radius * radius * MathF.Max(bones[i].Length, radius);
			sum += volumes[i];
		}

		for (int i = 0; i < bones.Count; i++)
		{
			var bone = bones[i];
			bone.Mass = sum > 0f ? mass * (volumes[i] / sum) : mass / bones.Count;
			bones[i] = bone;
		}
	}

	// --- Дебаг -------------------------------------------------------------------------------------

}
