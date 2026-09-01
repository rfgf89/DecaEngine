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

/// <summary>Локомоция и клипы: походка по скорости, root motion, сэмплирование позы через ozz. Часть <see cref="AnimationDriver"/> - файл на тему; состояние
/// персонажа (Character) и кадровый Update живут в основном файле.</summary>
public sealed partial class AnimationDriver
{
	/// <summary>
	/// Локомоушен-бленд (см. <see cref="LocomotionComponent"/>): стойка/шаг/бег по замеренной
	/// скорости сущности, темп шага масштабируется под неё. Возвращает false, когда позу вести
	/// нечем (нет компонента, выключен, нет ozz, клипы не нашлись) - тогда позой занимается
	/// обычный <see cref="Animator"/>. Причины фоллбека снаружи неразличимы намеренно: их
	/// показывает окно дебага, а вызывающему важно только «кто ведёт позу».
	/// </summary>
	private bool ApplyLocomotion(Entity entity, Character character, float deltaSeconds)
	{
		character.LocoActive = false;

		if (character.Pose == null || !entity.HasComponent<LocomotionComponent>())
		{
			return false;
		}

		var settings = entity.GetComponent<LocomotionComponent>();
		if (!settings.Enabled)
		{
			return false;
		}

		// Клипы ищутся по именам только при их СМЕНЕ - как AppliedClip у Animator.
		string key = $"{settings.IdleClip}\n{settings.WalkClip}\n{settings.RunClip}";
		if (!string.Equals(key, character.LocoClipsKey, StringComparison.Ordinal))
		{
			character.LocoClipsKey = key;
			character.LocoIdle = FindClip(character, settings.IdleClip ?? string.Empty);
			character.LocoWalk = FindClip(character, settings.WalkClip ?? string.Empty);
			character.LocoRun = FindClip(character, settings.RunClip ?? string.Empty);
			character.LocoOffsetsValid = false;
		}

		// Все три клипа обязательны. Смешивать «что нашлось» нельзя: ozz добирает недостающий вес
		// rest-позой, и персонаж с опечаткой в имени клипа ходил бы полурастворённым в bind-позу -
		// это хуже честного фоллбека на Animator, который сразу видно.
		if (character.LocoIdle == null || character.LocoWalk == null || character.LocoRun == null)
		{
			return false;
		}

		var idleClip = GetOzzClip(character, character.LocoIdle);
		var walkClip = GetOzzClip(character, character.LocoWalk);
		var runClip = GetOzzClip(character, character.LocoRun);

		if (idleClip == null || walkClip == null || runClip == null ||
			idleClip.Duration <= 0f || walkClip.Duration <= 0f || runClip.Duration <= 0f)
		{
			return false;
		}

		character.LocoPoseA ??= OzzPose.Create(character.Ozz);
		character.LocoPoseB ??= OzzPose.Create(character.Ozz);

		if (character.LocoPoseA == null || character.LocoPoseB == null)
		{
			return false;
		}

		if (!character.LocoOffsetsValid)
		{
			character.LocoWalkPhaseOffset = GaitPhaseOffset(character, walkClip);
			character.LocoRunPhaseOffset = GaitPhaseOffset(character, runClip);
			character.LocoWalkStride = MeasureStrideSpeed(character, walkClip);
			character.LocoRunStride = MeasureStrideSpeed(character, runClip);
			character.LocoOffsetsValid = true;
		}

		float walkSpeed = MathF.Max(settings.WalkSpeed, 1e-3f);
		float runSpeed = MathF.Max(settings.RunSpeed, walkSpeed + 1e-3f);

		// Скорость меряется по XZ-перемещению сущности: вертикаль - это кочки и падения, темпу шага
		// она не принадлежит. При нулевом шаге (режим редактирования) не двигается ничего - поза
		// считается по текущим фазе и скорости, как и весь остальной стек.
		if (deltaSeconds > 0f)
		{
			var worldPos = character.ModelToWorld.Translation;
			float raw = character.LocoSpeed;

			if (character.LocoHasPrev)
			{
				var delta = worldPos - character.LocoPrevWorld;
				raw = MathF.Sqrt(delta.X * delta.X + delta.Z * delta.Z) / deltaSeconds;

				// Потолок - от телепортов: перенос сущности при подъёме из рэгдолла - это метры за
				// кадр, и без потолка каждый подъём начинался бы со вспышки бега.
				raw = MathF.Min(raw, runSpeed * 2f);
			}

			character.LocoPrevWorld = worldPos;
			character.LocoHasPrev = true;

			float alpha = settings.Smoothing > 0f ? 1f - MathF.Exp(-settings.Smoothing * deltaSeconds) : 1f;
			character.LocoSpeed += (raw - character.LocoSpeed) * alpha;
		}

		float speed = character.LocoSpeed;

		// Два активных слоя и общая нормированная фаза. Частота цикла на отрезке стойка-шаг растёт
		// пропорционально скорости (длина шага авторская, темп подгоняется), на отрезке шаг-бег -
		// линейно между авторскими темпами: скорость в точке бленда по построению равна
		// lerp(WalkSpeed, RunSpeed, t), и отдельного множителя «догнать скорость» не нужно.
		OzzClip layerA, layerB;
		float timeA, timeB, weightA, weightB, frequency;

		// Время слоя - от общей фазы плюс СДВИГ ДО СОБЫТИЯ АЛЛЮРА клипа (см. LocoWalkPhaseOffset):
		// сама по себе общая фаза выравнивает только темп, а не то, ЧТО в этот момент делают ноги.
		float walkTime = (character.LocoPhase + character.LocoWalkPhaseOffset) % 1f * walkClip.Duration;
		float runTime = (character.LocoPhase + character.LocoRunPhaseOffset) % 1f * runClip.Duration;

		// Аллюр переключается с ГИСТЕРЕЗИСОМ (вверх на 60% отрезка, вниз на 40%), бленд - кроссфейд
		// по времени ~0.2 с (см. LocoRunGait). Темп внутри аллюра масштабируется под скорость:
		// застрявший на 2 м/с бегун - это замедленный ЧИСТЫЙ галоп, а не полусмесь аллюров.
		float switchUp = walkSpeed + 0.6f * (runSpeed - walkSpeed);
		float switchDown = walkSpeed + 0.4f * (runSpeed - walkSpeed);

		if (!character.LocoRunGait && speed > switchUp)
		{
			character.LocoRunGait = true;
		}
		else if (character.LocoRunGait && speed < switchDown)
		{
			character.LocoRunGait = false;
		}

		if (deltaSeconds > 0f)
		{
			float goal = character.LocoRunGait ? 1f : 0f;
			character.LocoGaitBlend += (goal - character.LocoGaitBlend) *
				(1f - MathF.Exp(-8f * deltaSeconds));
		}

		// Множитель темпа - от ПРИРОДНОЙ скорости шага клипа (см. LocoWalkStride), в единицах
		// модели: скорость сущности переводится масштабом. Авторские WalkSpeed/RunSpeed - только
		// пороги аллюра. Фоллбек на них - когда замер не удался (лапа не размечена).
		float worldScale = MathF.Max(WorldScaleOf(character.ModelToWorld), 1e-6f);
		float speedModel = speed / worldScale;

		float walkRate = character.LocoWalkStride > 1e-3f
			? speedModel / character.LocoWalkStride
			: speed / walkSpeed;
		float runRate = character.LocoRunStride > 1e-3f
			? speedModel / character.LocoRunStride
			: speed / runSpeed;

		if (speed <= walkSpeed && character.LocoGaitBlend < 0.5f)
		{
			float t = Math.Clamp(speed / walkSpeed, 0f, 1f);

			layerA = idleClip;
			timeA = character.LocoIdleTime;
			weightA = 1f - t;

			layerB = walkClip;
			timeB = walkTime;
			weightB = t;

			frequency = walkRate / walkClip.Duration;

			character.LocoIdleWeight = weightA;
			character.LocoWalkWeight = weightB;
			character.LocoRunWeight = 0f;
		}
		else
		{
			float t = Math.Clamp(character.LocoGaitBlend, 0f, 1f);

			layerA = walkClip;
			timeA = walkTime;
			weightA = 1f - t;

			layerB = runClip;
			timeB = runTime;
			weightB = t;

			// Темп каждого слоя гонится за реальной скоростью в ЕГО аллюре, между ними -
			// кроссфейдный вес: и разогнанный шаг, и замедленный галоп держат длину шага.
			float walkFrequency = walkRate / walkClip.Duration;
			float runFrequency = runRate / runClip.Duration;
			frequency = walkFrequency + (runFrequency - walkFrequency) * t;

			character.LocoIdleWeight = 0f;
			character.LocoWalkWeight = weightA;
			character.LocoRunWeight = weightB;
		}

		if (deltaSeconds > 0f)
		{
			character.LocoPhase = (character.LocoPhase + frequency * deltaSeconds) % 1f;
			character.LocoIdleTime = (character.LocoIdleTime + deltaSeconds) % idleClip.Duration;
		}

		bool ok =
			character.LocoPoseA.Sample(layerA, timeA) &&
			character.LocoPoseB.Sample(layerB, timeB) &&
			character.Pose.Blend([character.LocoPoseA, character.LocoPoseB], [weightA, weightB]) &&
			character.Pose.LocalToModel() &&
			character.Pose.ReadModelMatrices(character.Models) &&
			character.Pose.ReadLocalTransforms(character.Locals);

		if (!ok)
		{
			return false;
		}

		character.Models.CopyTo(character.Managed.ModelMatrices, 0);
		character.LocoActive = true;
		return true;
	}

	/// <summary>
	/// Фаза СОБЫТИЯ АЛЛЮРА в клипе: нижняя точка задней левой лапы (по humanoid-разметке), 0..1.
	/// Считается один раз при резолве клипов перебором 32 семплов - выравнивание грубое, но у цикла
	/// шага событие размазано на десятки миллисекунд, и тридцать второй доли цикла хватает.
	/// Без разметки (или кость не нашлась) сдвиг нулевой - то есть ровно прежнее поведение.
	/// </summary>
	private static float GaitPhaseOffset(Character character, OzzClip clip)
	{
		string footName = character.Avatar?[HumanoidBone.LeftFoot] ?? string.Empty;
		int foot = string.IsNullOrEmpty(footName) ? -1 : character.Skeleton.FindJoint(footName);

		if (foot < 0 || character.LocoPoseA == null)
		{
			return 0f;
		}

		const int samples = 32;
		var models = new Matrix4x4[character.Skeleton.JointCount];

		float bestPhase = 0f;
		float bestHeight = float.MaxValue;

		for (int k = 0; k < samples; k++)
		{
			float phase = (float)k / samples;

			if (!character.LocoPoseA.Sample(clip, phase * clip.Duration) ||
				!character.LocoPoseA.LocalToModel() ||
				!character.LocoPoseA.ReadModelMatrices(models))
			{
				return 0f;
			}

			float height = models[foot].Translation.Y;
			if (height < bestHeight)
			{
				bestHeight = height;
				bestPhase = phase;
			}
		}

		// Время слоя считается как (фаза + сдвиг): на общей фазе 0 клип стоит ровно в своём событии,
		// то есть сдвиг - это ФАЗА СОБЫТИЯ в клипе, как она есть.
		return bestPhase;
	}

	/// <summary>
	/// Природная скорость шага клипа: средняя горизонтальная скорость задней левой лапы в
	/// пространстве модели за её ТАКТ ОПОРЫ (нижняя четверть размаха высоты), на авторском темпе.
	/// У in-place клипа опорная лапа едет назад ровно со скоростью, с которой персонаж «должен»
	/// ехать вперёд, - это и есть скорость, при которой лапы не скользят. Ноль - замер не удался
	/// (нет разметки, лапа не циклится), вызывающий откатывается на авторские числа.
	/// </summary>
	private static float MeasureStrideSpeed(Character character, OzzClip clip)
	{
		string footName = character.Avatar?[HumanoidBone.LeftFoot] ?? string.Empty;
		int foot = string.IsNullOrEmpty(footName) ? -1 : character.Skeleton.FindJoint(footName);

		if (foot < 0 || character.LocoPoseA == null)
		{
			return 0f;
		}

		const int samples = 48;
		var positions = new Vector3[samples];
		float minHeight = float.MaxValue;
		float maxHeight = float.MinValue;

		for (int k = 0; k < samples; k++)
		{
			if (!character.LocoPoseA.Sample(clip, clip.Duration * k / samples) ||
				!character.LocoPoseA.LocalToModel() ||
				!character.LocoPoseA.ReadModelMatrices(character.Models))
			{
				return 0f;
			}

			positions[k] = character.Models[foot].Translation;
			minHeight = MathF.Min(minHeight, positions[k].Y);
			maxHeight = MathF.Max(maxHeight, positions[k].Y);
		}

		float threshold = minHeight + 0.25f * (maxHeight - minHeight);
		float dt = clip.Duration / samples;
		float travel = 0f;
		float seconds = 0f;

		for (int k = 0; k < samples; k++)
		{
			int next = (k + 1) % samples;
			if (positions[k].Y >= threshold || positions[next].Y >= threshold)
			{
				continue;
			}

			var step = positions[next] - positions[k];
			travel += MathF.Sqrt(step.X * step.X + step.Z * step.Z);
			seconds += dt;
		}

		return seconds > 1e-4f ? travel / seconds : 0f;
	}

	private void ApplyClip(Entity entity, Character character, float deltaSeconds)
	{
		if (!entity.HasComponent<Animator>())
		{
			SamplePose(character);
			return;
		}

		// Компонент правится ПО ССЫЛКЕ. Прежний вариант читал копию и возвращал её через
		// AddComponent каждый кадр - а это обращение к хранилищу сущностей на каждом кадре на
		// каждого персонажа, которое в худшем случае двигает сущность между архетипами. Здесь нужно
		// изменить одно поле, и ref-доступ делает ровно это, ничего не трогая в структуре стора.
		ref var animator = ref entity.GetComponent<Animator>();

		// Клип ищется по имени только при СМЕНЕ имени: линейный поиск по списку клипов дёшев, но
		// делать его каждый кадр на каждого персонажа незачем.
		if (!string.Equals(animator.ClipName ?? string.Empty, character.AppliedClip, StringComparison.Ordinal))
		{
			character.AppliedClip = animator.ClipName ?? string.Empty;
			character.Player.Clip = FindClip(character, character.AppliedClip);
		}

		character.Player.Loop = animator.Loop;
		character.Player.Speed = animator.Speed;

		// Время живёт в КОМПОНЕНТЕ (его видно и можно скрабить в инспекторе), но двигает его плеер:
		// только он знает про зацикливание и про конец незацикленного клипа.
		character.Player.Time = animator.Time;
		float timeBefore = character.Player.Time;

		if (animator.Playing)
		{
			character.Player.Advance(deltaSeconds);
		}

		animator.Time = character.Player.Time;

		SamplePose(character);

		if (animator.RootMotion)
		{
			ApplyRootMotion(entity, character, in animator, timeBefore);
		}
	}

	/// <summary>
	/// Root motion по образцу ozz motion_playback: XZ-трансляция КОРНЕВОЙ кости клипа вычитается из
	/// позы (персонаж остаётся на месте в пространстве модели) и накапливается дельтами в позицию
	/// сущности - тело движется со скоростью, которую задал автор анимации, включая заворот лупа.
	/// Вертикаль остаётся в позе: прыжок в клипе - это движение позы, а не сущности.
	///
	/// Не сочетается с Character Body (телом владеет его рулевое) и, как остальная процедурка,
	/// требует нативного ozz - без него стадия молча пропускается.
	/// </summary>
	private static void ApplyRootMotion(Entity entity, Character character, in Animator animator,
		float timeBefore)
	{
		var clip = character.Player.Clip;

		if (clip == null || character.Pose == null || !entity.HasComponent<Position>() ||
			entity.HasComponent<CharacterBodyComponent>())
		{
			return;
		}

		if (!ReferenceEquals(character.MotionClip, clip))
		{
			character.MotionClip = clip;
			character.MotionJoint = MotionJointOf(character);
		}

		if (character.MotionJoint < 0 || character.MotionJoint >= clip.Tracks.Length)
		{
			return;
		}

		var track = clip.Tracks[character.MotionJoint];
		if (track.TranslationTimes.Length < 2)
		{
			// Одного ключа (или пустого канала) движению не хватает по построению.
			return;
		}

		// Компенсация: корень возвращается к ПЕРВОМУ ключу по XZ - поза шагает на месте, а весь
		// путь уходит в сущность. Y не трогается: вертикаль клипа - это поза, не путь.
		var atTime = SampleMotion(track, character.Player.Time);
		var offset = atTime - track.Translations[0];
		offset.Y = 0f;

		character.Locals[character.MotionJoint].position -= offset;

		if (!character.Pose.WriteLocalTransforms(character.Locals) ||
			!character.Pose.LocalToModel() ||
			!character.Pose.ReadModelMatrices(character.Models))
		{
			return;
		}

		character.Models.CopyTo(character.Managed.ModelMatrices, 0);

		// Дельта пути за кадр - с учётом заворота лупа: время после меньше времени до (при прямом
		// ходе) означает, что плеер завернулся, и к дельте добавляется полный путь цикла.
		var delta = atTime - SampleMotion(track, timeBefore);

		if (animator.Loop && clip.Duration > 1e-6f)
		{
			var net = track.Translations[^1] - track.Translations[0];

			if (character.Player.Speed >= 0f && character.Player.Time < timeBefore - 1e-6f)
			{
				delta += net;
			}
			else if (character.Player.Speed < 0f && character.Player.Time > timeBefore + 1e-6f)
			{
				delta -= net;
			}
		}

		delta.Y = 0f;

		if (delta.LengthSquared() < 1e-12f)
		{
			return;
		}

		// Дельта живёт в пространстве МОДЕЛИ, позиция сущности - в пространстве РОДИТЕЛЯ:
		// модель -> мир -> родитель.
		var worldDelta = Vector3.TransformNormal(delta, character.ModelToWorld);
		var parentToWorld = PrefabSceneViewport.ParentToWorldMatrix(entity);

		if (Matrix4x4.Invert(parentToWorld, out var worldToParent))
		{
			entity.GetComponent<Position>().value += Vector3.TransformNormal(worldDelta, worldToParent);
		}
	}

	/// <summary>Самый верхний предок таза разметки (без разметки - нулевой джойнт): авторское
	/// движение живёт на корневом узле рига, а не на тазе - таз качается внутри цикла.</summary>
	private static int MotionJointOf(Character character)
	{
		int joint = character.Skeleton.FindJoint(character.Avatar?[HumanoidBone.Hips] ?? string.Empty);

		if (joint < 0)
		{
			joint = 0;
		}

		while (character.Skeleton.Parents[joint] >= 0)
		{
			joint = character.Skeleton.Parents[joint];
		}

		return joint;
	}

	/// <summary>Линейная интерполяция дорожки трансляции корня. Линейный проход осознанно: у
	/// motion-дорожки единицы-десятки ключей, и звать её приходится дважды за кадр.</summary>
	private static Vector3 SampleMotion(JointTrack track, float time)
	{
		var times = track.TranslationTimes;
		var values = track.Translations;

		if (time <= times[0])
		{
			return values[0];
		}

		for (int i = 1; i < times.Length; i++)
		{
			if (time <= times[i])
			{
				float span = times[i] - times[i - 1];
				float t = span > 1e-9f ? (time - times[i - 1]) / span : 1f;
				return Vector3.Lerp(values[i - 1], values[i], t);
			}
		}

		return values[^1];
	}

	/// <summary>Семплирует клип в позу: нативным ozz, если он есть, иначе C#-семплером. Оба пути
	/// оставляют результат в одном виде - модельных матрицах <see cref="Character.Models"/> и
	/// локальных TRS, - поэтому процедурные стадии ниже про этот выбор не знают.</summary>
	private static void SamplePose(Character character)
	{
		var clip = character.Player.Clip;
		var ozzClip = clip != null ? GetOzzClip(character, clip) : null;

		if (character.Pose != null && ozzClip != null &&
			character.Pose.Sample(ozzClip, character.Player.Time) &&
			character.Pose.LocalToModel() &&
			character.Pose.ReadModelMatrices(character.Models) &&
			character.Pose.ReadLocalTransforms(character.Locals))
		{
			character.Models.CopyTo(character.Managed.ModelMatrices, 0);
			return;
		}

		character.Player.Apply(character.Managed);
		character.Managed.ModelMatrices.CopyTo(character.Models, 0);
		character.Managed.Locals.CopyTo(character.Locals, 0);
	}

	private static OzzClip? GetOzzClip(Character character, PreparedAnimation clip)
	{
		if (character.Ozz == null)
		{
			return null;
		}

		if (!character.Clips.TryGetValue(clip, out var built))
		{
			// Неудачная сборка кешируется как null: иначе кадр за кадром повторялась бы одна и та же
			// провалившаяся перепаковка клипа в сжатый формат ozz.
			built = OzzClip.Build(character.Ozz, clip);
			character.Clips[clip] = built;
		}

		return built;
	}

	private static PreparedAnimation? FindClip(Character character, string name)
	{
		if (string.IsNullOrEmpty(name))
		{
			return null;
		}

		foreach (var clip in character.Model.Animations)
		{
			if (string.Equals(clip.Name, name, StringComparison.Ordinal))
			{
				return clip;
			}
		}

		return null;
	}

}
