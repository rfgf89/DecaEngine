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

/// <summary>Подъём после рэгдолла и хит-реакция: запуск, блендинг, определение лежачей позы. Часть <see cref="AnimationDriver"/> - файл на тему; состояние
/// персонажа (Character) и кадровый Update живут в основном файле.</summary>
public sealed partial class AnimationDriver
{
	/// <summary>
	/// Начинает переход «поза рэгдолла → поза анимации»: запоминает ТЕКУЩУЮ позу как исходную.
	///
	/// Снимок обязателен. Рэгдолл к этому моменту лежит в произвольной позе, а клип начинается со
	/// своей; переключить одно на другое мгновенно - это рывок на весь размах позы, ровно то, что в
	/// игре читается как «персонаж дёрнулся и телепортировался в стойку».
	///
	/// <paramref name="modelToWorld"/> - трансформ сущности ПОСЛЕ переноса к месту лёжки: снимок
	/// РЕБЕЙЗИТСЯ в него, потому что модельные матрицы позы считаны ещё в старом. Без ребейза
	/// лежачая поза рендерилась под новым трансформом со сдвигом на весь перенос - «телепорт» в
	/// момент начала подъёма, тем заметнее, чем дальше утолкали рэгдолл от точки падения.
	/// </summary>
	public void BeginRecovery(int entityId, float duration, in Matrix4x4 modelToWorld,
		string getUpClip = "")
	{
		if (!_characters.TryGetValue(entityId, out var character) || duration <= 0f)
		{
			return;
		}

		var rebase = Matrix4x4.Identity;
		if (Matrix4x4.Invert(modelToWorld, out var worldToNew))
		{
			// Старая модель -> мир -> новая модель. Без переноса матрицы совпадают, и ребейз
			// вырождается в единичный сам собой.
			rebase = character.ModelToWorld * worldToNew;
		}

		character.RecoveryFrom ??= new Transform[character.Skeleton.JointCount];
		DecomposeModelMatrices(character, rebase, character.RecoveryFrom);

		// Авторский клип подъёма (пусто или не нашёлся - процедурный морф, прежнее поведение):
		// клип ведёт позу целиком на всю свою длительность (см. ApplyGetUpClip).
		character.GetUpClip = string.IsNullOrEmpty(getUpClip) ? null : FindClip(character, getUpClip);

		if (character.GetUpClip != null && character.GetUpClip.Duration > 0f)
		{
			// Окно вливания снимка в начальную позу клипа - авторское (duration = GetUpDuration
			// компонента): им регулируется, как быстро лежащий перетекает в сидячую стартовую позу.
			// Кламп половиной клипа: окно длиннее половины разбавляло бы снимком уже сам подъём.
			character.RecoveryDuration = character.GetUpClip.Duration;
			character.RecoveryBlendSeconds = MathF.Min(duration, character.GetUpClip.Duration * 0.5f);
		}
		else
		{
			character.GetUpClip = null;
			character.RecoveryDuration = duration;
			character.RecoveryBlendSeconds = duration;
		}

		character.RecoveryElapsed = 0f;
	}

	/// <summary>
	/// Снимает состояние, накопленное за игру и живущее СБОКУ от ECS. Звать на выходе из Play.
	///
	/// Всё, что лежит в компонентах (время клипа, состояние цикла падения), откатывает снимок Play
	/// Mode. А переход позы при подъёме - нет: он живёт здесь. Персонаж, на котором нажали Stop в
	/// середине подъёма, остался бы навсегда смешанным между лежачей и стоячей позой, и выглядело бы
	/// это как «поза сломалась», а не как «забыли сбросить».
	/// </summary>
	public void EndPlay()
	{
		foreach (var character in _characters.Values)
		{
			character.RecoveryElapsed = 0f;
			character.RecoveryDuration = 0f;
			character.GetUpClip = null;

			// Локомоушен - тот же случай, что и переход позы: фаза, замер скорости и его история
			// живут сбоку от ECS, снимком Play Mode не откатываются и накапливаются за игру.
			character.LocoPhase = 0f;
			character.LocoIdleTime = 0f;
			character.LocoSpeed = 0f;
			character.LocoHasPrev = false;

			character.LocoRunGait = false;
			character.LocoGaitBlend = 0f;

			// Хит-реакция - тоже: Stop посреди толчка не должен оставлять персонажа полукачнувшимся.
			character.ReactionDuration = 0f;
			character.ReactionElapsed = 0f;
			character.ReactionImpulsePending = false;

			// Рэгдолл СНОСИТСЯ, а не «возвращается в анимацию». Его тела - это и есть накопленное за
			// игру состояние: персонаж, упавший за секунду до Stop, лежит там, где упал, и никакой
			// откат КОМПОНЕНТОВ его оттуда не поднимет - в компонентах ничего и не менялось
			// (Enabled и Physical у него авторские). Снесённый рэгдолл на следующем же кадре
			// собирается заново по восстановленной позе, то есть ровно там, где его поставил автор.
			DestroyRagdoll(character);

			// Цепочки spring bones копят инерцию - тот же случай. Пересобираются по позе.
			character.Chains.Clear();
			character.ChainsBuilt = false;
		}
	}

	/// <summary>
	/// Запускает хит-реакцию: временный частичный рэгдолл. Корпус получает толчок
	/// <paramref name="velocityChange"/> (м/с, приращение скорости - от массы не зависит) и на
	/// <paramref name="duration"/> секунд поза корпуса подмешивается из физики, ноги продолжают
	/// идти анимацией. Требует <see cref="RagdollComponent"/> на сущности (нечем реагировать);
	/// выключенный компонент - нормальный случай, тела соберутся на время реакции и снесутся после.
	/// Повторный удар во время реакции ПЕРЕЗАПУСКАЕТ конверт и добавляет толчок - очередь ударов
	/// сливается в один длинный, а не теряется.
	/// </summary>
	public void TriggerHitReaction(int entityId, Vector3 velocityChange, float duration = 0.7f,
		float strength = 1f)
	{
		if (!_characters.TryGetValue(entityId, out var character) || duration <= 0f)
		{
			return;
		}

		// Перезапуск ПОВЕРХ идущей реакции - БЕЗ обнуления конверта: атака стартует с ТЕКУЩЕГО
		// веса, а не с нуля. Обнуление на кадр возвращало позу в чистую анимацию и тут же снова
		// роняло в физику - при серии ударов (капсулы в контакте, кулдаун тарана короче конверта)
		// это читалось как «дёргается между рэгдоллом и анимацией».
		float carried = 0f;
		if (character.ReactionDuration > 0f && character.ReactionElapsed < character.ReactionDuration)
		{
			float t = Math.Clamp(character.ReactionElapsed / character.ReactionDuration, 0f, 1f);
			float attack = Math.Clamp(character.ReactionElapsed / ReactionAttackSeconds, 0f, 1f);
			float release = 1f - t * t * (3f - 2f * t);
			carried = character.ReactionStrength * attack * release;
		}

		character.ReactionElapsed = Math.Clamp(carried, 0f, 1f) * ReactionAttackSeconds;
		character.ReactionDuration = duration;
		character.ReactionStrength = Math.Clamp(strength, 0f, 1f);
		character.ReactionImpulse = velocityChange;
		character.ReactionImpulsePending = true;
	}

	/// <summary>Длительность атаки конверта реакции, с: толчок обязан быть виден почти сразу.</summary>
	private const float ReactionAttackSeconds = 0.06f;

	/// <summary>Идёт ли ещё подъём. По нему вызывающий понимает, когда персонаж снова управляем.</summary>
	public bool IsRecovering(int entityId) =>
		_characters.TryGetValue(entityId, out var character) && character.RecoveryElapsed < character.RecoveryDuration;

	/// <summary>
	/// Успокоился ли рэгдолл: скорость самой быстрой кости в ДОЛЯХ характерного размера скелета за
	/// секунду. Доля, а не абсолют - у лисы габарит 160 единиц модели, у метрового персонажа 1.8, и
	/// одно и то же число означает для них совершенно разное.
	/// </summary>
	public bool IsRagdollSettled(int entityId, float relativeSpeed)
	{
		if (!_characters.TryGetValue(entityId, out var character) || character.Ragdoll == null ||
			Physics == null)
		{
			return true;
		}

		float threshold = relativeSpeed * character.Scale * WorldScaleOf(character.ModelToWorld);
		var bodies = Physics.World.Simulation.Bodies;

		for (int i = 0; i < character.Ragdoll.BoneCount; i++)
		{
			if (bodies[character.Ragdoll.BodyOf(i)].Velocity.Linear.Length() > threshold)
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>
	/// Куда «смотрит» лежащий персонаж: горизонтальная проекция оси таз→шея текущей позы в мире.
	/// Для разворота сущности ПЕРЕД подъёмом: встать вдоль тела, а не докручиваться из поворота,
	/// с которым персонаж когда-то упал, - укатившийся рэгдолл лежит под произвольным углом, и
	/// подъём без разворота проворачивал корпус на весь этот угол («странно поднимается»).
	/// Ложь (false) - у почти вертикально лежащей оси (рэгдолл замер сидя): горизонтальной
	/// проекции не из чего взяться, и прежний поворот честнее случайного.
	/// </summary>
	public bool TryGetLyingFacing(int entityId, out Vector3 worldForward)
	{
		worldForward = default;

		if (!_characters.TryGetValue(entityId, out var character) || character.Avatar == null)
		{
			return false;
		}

		int hips = character.Skeleton.FindJoint(character.Avatar[HumanoidBone.Hips] ?? string.Empty);
		int neck = character.Skeleton.FindJoint(character.Avatar[HumanoidBone.Neck] ?? string.Empty);

		if (neck < 0)
		{
			neck = character.Skeleton.FindJoint(character.Avatar[HumanoidBone.Head] ?? string.Empty);
		}

		if (hips < 0 || neck < 0)
		{
			return false;
		}

		var direction =
			Vector3.Transform(character.Models[neck].Translation, character.ModelToWorld) -
			Vector3.Transform(character.Models[hips].Translation, character.ModelToWorld);
		direction.Y = 0f;

		// Порог - доля длины оси: лежащее тело даёт почти всю длину в горизонталь, сидящее - крохи.
		float span = Vector3.Distance(character.Models[neck].Translation, character.Models[hips].Translation) *
			WorldScaleOf(character.ModelToWorld);

		if (direction.Length() < 0.3f * MathF.Max(span, 1e-6f))
		{
			return false;
		}

		worldForward = Vector3.Normalize(direction);
		return true;
	}

	/// <summary>Мировая позиция таза (или корня рэгдолла) - туда персонаж встаёт. Именно кость, а не
	/// трансформ сущности: сущность всё это время стояла там, откуда персонаж упал, а лежит он уже в
	/// другом месте.</summary>
	public bool TryGetRagdollRootWorld(int entityId, out Vector3 position)
	{
		position = Vector3.Zero;

		if (!_characters.TryGetValue(entityId, out var character) || character.Ragdoll == null ||
			character.Ragdoll.BoneCount == 0 || Physics == null)
		{
			return false;
		}

		position = Physics.World.Simulation.Bodies[character.Ragdoll.BodyOf(0)].Pose.Position;
		return true;
	}

	/// <summary>
	/// Поза подъёма из АВТОРСКОГО клипа (см. BeginRecovery): семплирует клип по времени
	/// восстановления, без зацикливания. Возвращает true, пока подъём ведёт позу, - обычный стек
	/// (локомоушен, наложения, IK) в это время не работает. Снимок лёжки вливается поверх в
	/// ApplyRecoveryBlend коротким окном.
	/// </summary>
	private bool ApplyGetUpClip(Character character)
	{
		if (character.GetUpClip == null)
		{
			return false;
		}

		if (character.Pose == null || character.RecoveryElapsed >= character.RecoveryDuration)
		{
			character.GetUpClip = null;
			return false;
		}

		var clip = GetOzzClip(character, character.GetUpClip);
		if (clip == null || clip.Duration <= 0f)
		{
			character.GetUpClip = null;
			return false;
		}

		bool ok =
			character.Pose.Sample(clip, MathF.Min(character.RecoveryElapsed, clip.Duration)) &&
			character.Pose.LocalToModel() &&
			character.Pose.ReadModelMatrices(character.Models) &&
			character.Pose.ReadLocalTransforms(character.Locals);

		if (!ok)
		{
			character.GetUpClip = null;
			return false;
		}

		character.Models.CopyTo(character.Managed.ModelMatrices, 0);
		return true;
	}

	/// <summary>
	/// На спине ли лежит персонаж: куда смотрит в мире «спинной верх» таза - ось, которая в
	/// bind-позе смотрела в модельный +Y. Для выбора клипа подъёма (со спины/с живота).
	/// </summary>
	public bool TryGetLyingSide(int entityId, out bool onBack)
	{
		onBack = false;

		if (!_characters.TryGetValue(entityId, out var character) || character.Avatar == null)
		{
			return false;
		}

		int hips = character.Skeleton.FindJoint(character.Avatar[HumanoidBone.Hips] ?? string.Empty);
		if (hips < 0)
		{
			return false;
		}

		if (!Matrix4x4.Invert(BindModelMatrix(character.Skeleton, hips), out var inverseBind))
		{
			return false;
		}

		var upLocal = Vector3.TransformNormal(Vector3.UnitY, inverseBind);
		var upWorld = Vector3.TransformNormal(upLocal, character.Models[hips] * character.ModelToWorld);

		if (upWorld.LengthSquared() < 1e-10f)
		{
			return false;
		}

		onBack = Vector3.Normalize(upWorld).Y < 0f;
		return true;
	}

	/// <summary>Модельная матрица джойнта в BIND-позе - композицией локалей вверх по родителям.</summary>
	private static Matrix4x4 BindModelMatrix(PreparedSkeleton skeleton, int joint)
	{
		var result = Matrix4x4.Identity;

		for (int j = joint; j >= 0; j = skeleton.Parents[j])
		{
			var bind = skeleton.BindLocals[j];
			result *= MathUtils.CreateTrs(bind.position, bind.rotation, bind.scale);
		}

		return result;
	}

	/// <summary>
	/// Смешивает позу подъёма с позой анимации. Идёт ПОСЛЕДНЕЙ стадией, после рэгдолла: он к этому
	/// моменту уже переведён в режим анимации и позу не пишет, а всё, что до него, - это как раз та
	/// целевая поза, к которой персонаж встаёт.
	///
	/// Смешиваются РАЗЛОЖЕННЫЕ TRS, а не матрицы напрямую: покомпонентная интерполяция матриц
	/// поворота даёт неортогональный базис в середине перехода, то есть кости, которые на полпути
	/// сплющиваются и растягиваются.
	/// </summary>
	private void ApplyRecoveryBlend(Character character)
	{
		if (character.RecoveryElapsed >= character.RecoveryDuration || character.RecoveryFrom == null)
		{
			return;
		}

		character.RecoveryElapsed += character.LastDelta;

		// Вес - по ОКНУ ВЛИВАНИЯ, не по всей длительности: у морфа они совпадают (прежнее
		// поведение), у авторского клипа окно короткое - дальше клип ведёт позу сам.
		float window = character.RecoveryBlendSeconds > 0f
			? character.RecoveryBlendSeconds
			: character.RecoveryDuration;
		float t = Math.Clamp(character.RecoveryElapsed / window, 0f, 1f);

		// Сглаживание на концах (smoothstep): линейный вес даёт заметный излом скорости в начале и в
		// конце подъёма - персонаж трогается и останавливается рывком.
		float weight = t * t * (3f - 2f * t);

		for (int i = 0; i < character.Models.Length; i++)
		{
			if (!Matrix4x4.Decompose(character.Models[i], out var scale, out var rotation, out var translation))
			{
				continue;
			}

			var from = character.RecoveryFrom[i];

			character.Models[i] = MathUtils.CreateTrs(
				Vector3.Lerp(from.position, translation, weight),
				Quaternion.Slerp(from.rotation, rotation, weight),
				Vector3.Lerp(from.scale, scale, weight));
		}

		character.Models.CopyTo(character.Managed.ModelMatrices, 0);
	}

	private static void DecomposeModelMatrices(Character character, in Matrix4x4 rebase, Transform[] target)
	{
		for (int i = 0; i < character.Models.Length; i++)
		{
			if (Matrix4x4.Decompose(character.Models[i] * rebase, out var scale, out var rotation, out var translation))
			{
				target[i] = new Transform { position = translation, rotation = rotation, scale = scale };
			}
			else
			{
				target[i] = new Transform { position = Vector3.Zero, rotation = Quaternion.Identity, scale = Vector3.One };
			}
		}
	}

}
