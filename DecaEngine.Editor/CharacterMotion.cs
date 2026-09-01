using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using DecaEngine.Editor.ECS;
using Friflo.Engine.ECS;

namespace DecaEngine.Editor;

/// <summary>
/// Тела персонажей, которых ведут геймплейные скрипты (пока это <see cref="CircleMoveComponent"/> с
/// включённым <see cref="CircleMoveComponent.Physical"/>).
///
/// Живёт СБОКУ от ECS, как <see cref="AnimationDriver"/>, и по той же причине: хендлы Bepu - это
/// нативное состояние, а компонент хранилище копирует при каждой смене архетипа, и хендл такого не
/// переживает. Ключ - идентификатор сущности префаба.
///
/// Персонаж двигается ЗАДАНИЕМ СКОРОСТИ капсуле, а не записью её позы. Разница ровно та, ради
/// которой физика тут вообще заведена: заданная поза - это телепорт, между кадрами тело оказывается
/// по другую сторону ступени, и решатель контактов не участвует вовсе. Скорость же решатель обязан
/// погасить о препятствие.
///
/// Горизонтальная скорость задаётся, ВЕРТИКАЛЬНАЯ остаётся своя - персонаж падает под гравитацией и
/// стоит на полу сам. Запись нуля по Y превратила бы капсулу в летающую: она бы не падала, и
/// «стоять на полу» пришлось бы изображать вручную.
/// </summary>
public sealed class CharacterMotionDriver
{
	private sealed class Character
	{
		public BodyHandle Body;
		public TypedIndex Shape;

		/// <summary>Размеры, под которые заведено тело. Форма Bepu неизменяема, поэтому правка полей
		/// компонента в инспекторе пересобирает капсулу - без этого ползунки радиуса выглядели бы
		/// работающими, не делая ничего.</summary>
		public float Radius;
		public float Height;

		/// <summary>Последнее НЕНУЛЕВОЕ направление хода. Персонаж, упёршийся в стену, стоит с нулевой
		/// скоростью, и доворот по ней развернул бы его в произвольную сторону - вместо этого он
		/// сохраняет ту, в которую шёл.</summary>
		public Vector3 Facing = Vector3.UnitZ;

		/// <summary>Направление, в которое корпус ДОВЕРНУЛСЯ к этому кадру: к <see cref="Facing"/> он
		/// идёт с пределом угловой скорости (TurnSpeed скрипта). Без предела смена направления
		/// разворачивала сущность за кадр - корпус «телепортировался», хотя ноги ещё шли по-старому.</summary>
		public Vector3 SmoothedFacing = Vector3.UnitZ;

		/// <summary>Предел доворота, рад/с; ноль и меньше - мгновенно (прежнее поведение).</summary>
		public float TurnSpeed;

		/// <summary>Засеян ли Facing из ФАКТИЧЕСКОГО поворота сущности. Тело пересоздаётся после
		/// каждого подъёма из рэгдолла, и дефолтный Facing=UnitZ у свежего состояния разворачивал
		/// вставшего персонажа рывком в +Z мира - «телепорт поворота» на первом кадре ходьбы.</summary>
		public bool FacingSeeded;

		/// <summary>Настройки доворота, снятые со СКРИПТА при рулении. <see cref="Apply"/> обходит
		/// тела, а не скрипты (их уже два вида), и знать, который из компонентов вёл это тело, ему
		/// незачем - достаточно того, что решил Steer.</summary>
		public bool FaceMotion = true;
		public Vector3 ModelForward = Vector3.UnitZ;

		/// <summary>Остаток coyote time: сколько ещё секунд прыжок разрешён ПОСЛЕ схода с опоры.
		/// Игрок, шагнувший с кромки и нажавший Space на кадр позже, по игровым меркам успел - а по
		/// голому лучу уже нет, и прыжок «не срабатывает» непредсказуемо.</summary>
		public float CoyoteLeft;
	}

	private readonly Dictionary<int, Character> _characters = new();
	private readonly List<int> _stale = new();

	public int CharacterCount => _characters.Count;

	/// <summary>Ввод игрока НА ЭТОТ КАДР (см. <see cref="PlayerInput"/>). Пишет вьюпорт перед
	/// <see cref="Steer"/>; пробник пишет руками - поэтому управление проверяется headless.</summary>
	public PlayerInput Input;

	/// <summary>
	/// Заводит и снимает тела и задаёт им скорость на ближайший шаг. Звать ДО
	/// <see cref="ScenePhysics.Update"/>: скорость, заданная после шага, применится только к
	/// следующему, и персонаж будет отставать от собственной команды на кадр.
	/// </summary>
	/// <param name="active">Идёт ли игра. Неактивный привод снимает все тела: персонаж на паузе
	/// обязан стоять там, куда его поставил автор сцены, а не там, где его застало выключение.</param>
	public void Steer(EntityStore? store, ScenePhysics? physics, bool active, float deltaSeconds = 0f,
		AnimationDriver? animation = null)
	{
		if (store == null || physics == null || !active)
		{
			Clear(physics);
			return;
		}

		_stale.Clear();
		foreach (var id in _characters.Keys)
		{
			_stale.Add(id);
		}

		SteerPlayers(store, physics, deltaSeconds);

		store.Query<CircleMoveComponent, CharacterBodyComponent, Position, Rotation>().ForEachEntity(
			(ref CircleMoveComponent move, ref CharacterBodyComponent shape, ref Position position,
				ref Rotation rotation, Entity entity) =>
		{
			// Игрок сильнее скрипта: сущность с обоими компонентами ведёт ввод, а не круг - иначе
			// два рулевых писали бы скорость одному телу, и побеждал бы порядок обхода.
			if (!move.Enabled || move.Radius <= 1e-4f || entity.HasComponent<PlayerMoveComponent>())
			{
				return;
			}

			// Падение/подъём решается ДО рулевого: пока персонаж лежит, тела у него нет, и рулить
			// нечем. Отсутствие компонента - обычный ходок, который не падает никогда.
			if (entity.HasComponent<FallRecoverComponent>() && animation != null)
			{
				if (!UpdateFallRecover(entity, physics, animation, move.Forward, deltaSeconds))
				{
					// Персонаж лежит или встаёт - тело снято, вести его нечем и незачем.
					return;
				}
			}

			_stale.Remove(entity.Id);

			var parentToWorld = PrefabSceneViewport.ParentToWorldMatrix(entity);
			var character = EnsureBody(entity, shape, position.value, physics, parentToWorld);
			var body = physics.World.Simulation.Bodies[character.Body];

			SeedFacing(character, rotation.value, move.Forward, parentToWorld);

			// Рулевое считается от ног, а не от центра капсулы: круг задан по земле, и полметра
			// разницы по высоте на него не влияют, но путать эти две точки в одном месте и не путать
			// в другом - верный способ получить круг, смещённый на радиус капсулы.
			var feet = body.Pose.Position - new Vector3(0f, character.Height * 0.5f, 0f);
			var velocity = CircleMotion.SteerVelocity(move, ToLocal(feet, parentToWorld), out float angle);

			// Фаза - ИЗМЕРЕННАЯ, а не проинтегрированная. У тела она может отставать (упёрлось в
			// ступень) или обгонять (столкнули), и накопленная фаза разошлась бы с ним навсегда.
			move.Angle = CircleMotion.Wrap(angle);

			var world = ToWorldDirection(velocity, parentToWorld);

			body.Velocity.Linear = new Vector3(world.X, body.Velocity.Linear.Y, world.Z);

			// Капсула не заваливается и не крутится: собственная ориентация тела персонажу не нужна
			// вовсе (её задаёт доворот по ходу), а завалившаяся капсула - это персонаж, лежащий на
			// боку и продолжающий идти. Гасится и скорость, и уже накопленный поворот: одной скорости
			// мало, решатель успевает довернуть тело контактом внутри шага.
			body.Velocity.Angular = Vector3.Zero;
			body.Pose.Orientation = Quaternion.Identity;
			body.Awake = true;

			ApplyStepUp(physics, shape, character, body, world);

			var under = physics.SampleGround(
				feet + new Vector3(0f, 0.05f, 0f), -Vector3.UnitY, 0.25f);
			if (under.Hit)
			{
				ApplyGroundSnap(body, world, feet.Y - under.Position.Y);
			}

			if (world.LengthSquared() > 1e-6f)
			{
				character.Facing = Vector3.Normalize(world);
			}

			character.FaceMotion = move.FaceMotion;
			character.ModelForward = move.Forward;
			character.TurnSpeed = move.TurnSpeed * MathF.PI / 180f;
			AdvanceFacing(character, deltaSeconds);
		});

		foreach (var id in _stale)
		{
			RemoveCharacter(id, physics);
		}

		DetectRams(physics, animation, deltaSeconds);
	}

	/// <summary>Засеивает направление корпуса из ФАКТИЧЕСКОГО поворота сущности - для только что
	/// созданного тела (первый кадр Play, подъём из рэгдолла: тело на падении снимается и
	/// пересоздаётся). Дефолтный Facing=UnitZ свежего состояния разворачивал вставшего персонажа
	/// рывком в +Z мира.</summary>
	private static void SeedFacing(Character character, in Quaternion rotation, Vector3 modelForward,
		in Matrix4x4 parentToWorld)
	{
		if (character.FacingSeeded)
		{
			return;
		}

		var forward = ToWorldDirection(Vector3.Transform(modelForward, rotation), parentToWorld);
		forward.Y = 0f;

		if (forward.LengthSquared() > 1e-6f)
		{
			forward = Vector3.Normalize(forward);
			character.Facing = forward;
			character.SmoothedFacing = forward;
		}

		character.FacingSeeded = true;
	}

	/// <summary>
	/// Ведёт корпус к направлению хода с пределом угловой скорости. Нулевой предел - мгновенно
	/// (прежнее поведение и старые сцены). Плоскость - только горизонталь: направления хода
	/// горизонтальны по построению обоих скриптов.
	/// </summary>
	private static void AdvanceFacing(Character character, float deltaSeconds)
	{
		if (character.TurnSpeed <= 0f)
		{
			character.SmoothedFacing = character.Facing;
			return;
		}

		if (deltaSeconds <= 0f)
		{
			return;
		}

		var current = character.SmoothedFacing;
		var target = character.Facing;

		if (current.LengthSquared() < 1e-8f || target.LengthSquared() < 1e-8f)
		{
			character.SmoothedFacing = target;
			return;
		}

		current = Vector3.Normalize(current);
		target = Vector3.Normalize(target);

		// Подписанный угол в горизонтальной плоскости; шаг ограничен пределом. Разворот на 180°
		// идёт через произвольную из сторон (cross нулевой, Atan2 отдаёт знак нуля) - для
		// перепрыгивания через «ровно назад» этого достаточно, а выбор стороны там и у живого
		// существа произволен.
		float cross = current.Z * target.X - current.X * target.Z;
		float signedAngle = MathF.Atan2(cross, Math.Clamp(Vector3.Dot(current, target), -1f, 1f));
		float step = Math.Clamp(signedAngle, -character.TurnSpeed * deltaSeconds,
			character.TurnSpeed * deltaSeconds);

		character.SmoothedFacing = MathF.Abs(step) >= MathF.Abs(signedAngle)
			? target
			: Vector3.Transform(current, Quaternion.CreateFromAxisAngle(Vector3.UnitY, step));
	}

	/// <summary>Кулдаун реакций по сущностям: капсулы, столкнувшись, остаются в контакте десятки
	/// кадров, и без кулдауна каждый из них перезапускал бы конверт - реакция выглядела бы как
	/// вечная тряска, а не как толчок.</summary>
	private readonly Dictionary<int, float> _ramCooldown = new();

	/// <summary>
	/// Таран: два персонажа со скриптами движения сблизились на скорости - оба получают
	/// хит-реакцию (см. <see cref="AnimationDriver.TriggerHitReaction"/>). Детект по СБЛИЖЕНИЮ
	/// (проекция относительной скорости на разделяющую ось), а не по расстоянию: идущие бок о бок
	/// персонажи касаются капсулами постоянно, и толкать их за это нельзя.
	/// </summary>
	private void DetectRams(ScenePhysics physics, AnimationDriver? animation, float deltaSeconds)
	{
		if (animation == null || _characters.Count < 2)
		{
			return;
		}

		foreach (var id in _characters.Keys)
		{
			if (_ramCooldown.TryGetValue(id, out float left))
			{
				float next = left - deltaSeconds;
				_ramCooldown[id] = next;
			}
		}

		var entries = _characters.ToArray();

		for (int a = 0; a < entries.Length; a++)
		{
			for (int b = a + 1; b < entries.Length; b++)
			{
				var bodyA = physics.World.Simulation.Bodies[entries[a].Value.Body];
				var bodyB = physics.World.Simulation.Bodies[entries[b].Value.Body];

				var separation = bodyB.Pose.Position - bodyA.Pose.Position;
				float distance = separation.Length();
				float touch = entries[a].Value.Radius + entries[b].Value.Radius + 0.06f;

				if (distance > touch || distance < 1e-4f)
				{
					continue;
				}

				var axis = separation / distance;
				float approach = Vector3.Dot(bodyA.Velocity.Linear - bodyB.Velocity.Linear, axis);

				if (approach < 1.4f)
				{
					continue;
				}

				// Толчок вдоль оси столкновения с добавкой вверх: чисто горизонтальный качок у
				// четвероногого почти не читается - корпус жёсткий вдоль хода.
				var shove = axis * approach * 0.7f + Vector3.UnitY * approach * 0.25f;

				Trigger(entries[b].Key, shove, animation);
				Trigger(entries[a].Key, -shove, animation);
			}
		}

		void Trigger(int entityId, Vector3 impulse, AnimationDriver driver)
		{
			if (_ramCooldown.TryGetValue(entityId, out float left) && left > 0f)
			{
				return;
			}

			_ramCooldown[entityId] = 0.6f;
			driver.TriggerHitReaction(entityId, impulse);
		}
	}

	/// <summary>
	/// Step-up: капсула без него не берёт ступени вовсе - вертикальная стенка глушит горизонтальную
	/// скорость контактом. Луч сразу ПЕРЕД капсулой сверху вниз ищет опору выше ног; найденная в
	/// пределах <see cref="CharacterBodyComponent.StepHeight"/> - это ступень, и тело получает
	/// вертикальную скорость, достаточную, чтобы поднять НИЗ капсулы на её кромку (баллистика от
	/// реальной гравитации мира - захардкоженные 9.81 врали бы в сцене с авторской).
	///
	/// Порог снизу отсекает пологие склоны: пандус и кочку капсула берёт контактом сама, и
	/// подпрыгивать на них значило бы скакать по всей сцене. Потолка над ступенью луч не проверяет
	/// осознанно: низкая ниша над лестницей - авторская экзотика, и решатель просто не пустит тело
	/// в неё, отменив подскок контактом.
	/// </summary>
	private static void ApplyStepUp(ScenePhysics physics, in CharacterBodyComponent shape,
		Character character, BodyReference body, Vector3 worldVelocity)
	{
		if (shape.StepHeight <= 0f)
		{
			return;
		}

		var horizontal = new Vector3(worldVelocity.X, 0f, worldVelocity.Z);
		float speed = horizontal.Length();

		if (speed < 1e-4f)
		{
			return;
		}

		var direction = horizontal / speed;
		var feet = body.Pose.Position - new Vector3(0f, character.Height * 0.5f, 0f);

		// Луч на полкорпуса впереди: ближе - капсула уже упёрлась и потеряла скорость, дальше -
		// подскоки начинаются за метр до лестницы.
		var origin = feet + direction * (character.Radius + 0.06f) +
			new Vector3(0f, shape.StepHeight + 0.05f, 0f);

		var ground = physics.SampleGround(origin, -Vector3.UnitY, shape.StepHeight + 0.05f);
		if (!ground.Hit)
		{
			return;
		}

		float rise = ground.Position.Y - feet.Y;
		if (rise < 0.04f || rise > shape.StepHeight)
		{
			return;
		}

		float gravity = MathF.Max(physics.World.Gravity.Length(), 1e-3f);
		float climb = MathF.Sqrt(2f * gravity * rise) * 1.1f;

		if (body.Velocity.Linear.Y < climb)
		{
			body.Velocity.Linear = new Vector3(
				body.Velocity.Linear.X, climb, body.Velocity.Linear.Z);
		}
	}

	/// <summary>
	/// Рулевое ИГРОКА: направление уже пришло в мире (перевод из клавиш и камеры - дело вьюпорта),
	/// сюда остаётся скорость и та же дисциплина капсулы, что у скрипта круга: горизонталь задаётся,
	/// вертикаль своя, ориентация тела гасится.
	/// </summary>
	private void SteerPlayers(EntityStore store, ScenePhysics physics, float deltaSeconds)
	{
		store.Query<PlayerMoveComponent, CharacterBodyComponent, Position, Rotation>().ForEachEntity(
			(ref PlayerMoveComponent move, ref CharacterBodyComponent shape, ref Position position,
				ref Rotation rotation, Entity entity) =>
		{
			if (!move.Enabled)
			{
				return;
			}

			_stale.Remove(entity.Id);

			var parentToWorld = PrefabSceneViewport.ParentToWorldMatrix(entity);
			var character = EnsureBody(entity, shape, position.value, physics, parentToWorld);
			var body = physics.World.Simulation.Bodies[character.Body];

			SeedFacing(character, rotation.value, move.Forward, parentToWorld);

			var direction = new Vector3(Input.MoveWorld.X, 0f, Input.MoveWorld.Z);
			float length = direction.Length();

			// Целевое направление - из ввода, корпус доворачивается к нему с пределом, а СКОРОСТЬ
			// идёт вдоль ДОВЁРНУТОГО направления - как в motion-семплах ozz, где движение
			// интегрируется за поворотом (руль - угловая скорость), а не тело догоняет вектор
			// скорости. На развороте персонаж режет дугу; с мгновенной скоростью по вводу он ехал
			// боком, пока корпус доворачивался.
			if (length > 1e-4f)
			{
				character.Facing = direction / length;
			}

			character.FaceMotion = move.FaceMotion;
			character.ModelForward = move.Forward;
			character.TurnSpeed = move.TurnSpeed * MathF.PI / 180f;
			AdvanceFacing(character, deltaSeconds);

			var world = length > 1e-4f
				? (move.FaceMotion && character.TurnSpeed > 0f ? character.SmoothedFacing : direction / length) *
					(Input.Run ? move.RunSpeed : move.WalkSpeed)
				: Vector3.Zero;

			body.Velocity.Linear = new Vector3(world.X, body.Velocity.Linear.Y, world.Z);
			body.Velocity.Angular = Vector3.Zero;
			body.Pose.Orientation = Quaternion.Identity;
			body.Awake = true;

			// Заземлённость - одним лучом на кадр: её делят прыжок (можно ли), coyote time
			// (только что можно было) и прижим к земле (см. ApplyGroundSnap).
			var feet = body.Pose.Position - new Vector3(0f, character.Height * 0.5f, 0f);
			var under = physics.SampleGround(
				feet + new Vector3(0f, 0.05f, 0f), -Vector3.UnitY, 0.25f);
			float gap = under.Hit ? feet.Y - under.Position.Y : float.MaxValue;

			// Перевзвод coyote - только БЕЗ скорости вверх: только что прыгнувшее тело первые
			// кадры ещё «у земли» по лучу, и без этой проверки каждый прыжок перевзводил бы окно -
			// то есть дарил даблджамп в первые сотые доли полёта.
			bool grounded = gap < 0.06f && body.Velocity.Linear.Y <= 0.1f;

			character.CoyoteLeft = grounded
				? CoyoteSeconds
				: MathF.Max(0f, character.CoyoteLeft - deltaSeconds);

			if (Input.Jump && move.JumpSpeed > 0f && character.CoyoteLeft > 0f)
			{
				body.Velocity.Linear = new Vector3(
					body.Velocity.Linear.X, move.JumpSpeed, body.Velocity.Linear.Z);

				// Прыжок СЖИГАЕТ coyote time: иначе второе нажатие в первые сотые доли секунды
				// полёта - это даблджамп, которого никто не заказывал.
				character.CoyoteLeft = 0f;
			}
			else
			{
				ApplyStepUp(physics, shape, character, body, world);
				ApplyGroundSnap(body, world, gap);
			}

		});
	}

	/// <summary>Окно coyote time. Одна десятая секунды - шесть кадров: столько игрок «не замечает»
	/// между глазом и пальцем, дольше - уже прыжки с воздуха.</summary>
	private const float CoyoteSeconds = 0.12f;

	/// <summary>
	/// Прижим к земле на спусках: идущее тело, у которого под ногами появился МАЛЫЙ зазор (сход с
	/// кочки, кромка ступени вниз), дотягивается к опоре вместо короткой баллистики - без прижима
	/// персонаж на каждом спуске на мгновение зависает с лапами в воздухе. Порог сверху отделяет
	/// спуск от честного полёта (прыжок, падение с высоты), проверка вертикальной скорости - от
	/// только что случившегося прыжка (у него скорость вверх).
	/// </summary>
	private static void ApplyGroundSnap(BodyReference body, Vector3 worldVelocity, float gap)
	{
		if (gap < 0.01f || gap > 0.12f || body.Velocity.Linear.Y > 0.1f ||
			new Vector3(worldVelocity.X, 0f, worldVelocity.Z).LengthSquared() < 1e-4f)
		{
			return;
		}

		// Скоростью, а не позой - по общей дисциплине капсулы: телепорт проскочил бы контакт.
		// Тяга пропорциональна зазору с потолком: постоянная большая тяга на миллиметровом зазоре
		// вбивала бы тело в пол на каждом кадре.
		float pull = MathF.Min(gap * 30f, 1.5f);
		body.Velocity.Linear = new Vector3(
			body.Velocity.Linear.X,
			MathF.Min(body.Velocity.Linear.Y, -pull),
			body.Velocity.Linear.Z);
	}

	/// <summary>
	/// Ведёт цикл «идёт → падает → встаёт → идёт» (см. <see cref="FallRecoverComponent"/>).
	/// Возвращает true, если персонажем сейчас управляет скрипт движения.
	///
	/// Тело скрипта и рэгдолл НЕ СОСУЩЕСТВУЮТ. Капсула, оставленная на время падения, дерётся с
	/// костями за то же место: персонаж «лежит», подпираемый невидимым цилиндром, а на подъёме
	/// выстреливает из него. Поэтому на падении тело снимается, а на подъёме заводится заново - уже
	/// там, где персонаж оказался.
	/// </summary>
	private bool UpdateFallRecover(Entity entity, ScenePhysics physics, AnimationDriver animation,
		Vector3 modelForward, float deltaSeconds)
	{
		ref var fall = ref entity.GetComponent<FallRecoverComponent>();
		ref var ragdoll = ref entity.GetComponent<RagdollComponent>();

		fall.StateTime += deltaSeconds;

		switch (fall.State)
		{
			case CharacterMotionState.Moving:
			{
				if (fall.FallEvery <= 0f || fall.StateTime < fall.FallEvery)
				{
					return true;
				}

				// Рэгдолл переводится в физику, тело снимается. Порядок важен: сняв тело раньше, мы
				// на один кадр оставили бы персонажа вовсе без физики, и он успел бы провалиться.
				ragdoll.Enabled = true;
				ragdoll.Physical = true;

				RemoveCharacter(entity.Id, physics);

				fall.State = CharacterMotionState.Falling;
				fall.StateTime = 0f;
				return false;
			}

			case CharacterMotionState.Falling:
			{
				// Покой спрашивается НЕ РАНЬШЕ MinFallTime: тела рэгдолла заводятся с нулевой
				// скоростью и в первом же кадре формально уже «успокоились» (см. MinFallTime).
				// Дальше - покой ИЛИ потолок ожидания: рэгдолл, зацепившийся за геометрию, может
				// подрагивать сколько угодно, и без потолка персонаж не встал бы никогда.
				bool settled = fall.StateTime >= fall.MinFallTime &&
					animation.IsRagdollSettled(entity.Id, fall.SettleSpeed);

				if (!settled && fall.StateTime < fall.SettleTimeout)
				{
					return false;
				}

				// Персонаж встаёт ТАМ, ГДЕ ЛЕЖИТ: сущность всё это время стояла в точке падения, а
				// тело уехало. Без переноса он вставал бы рывком обратно на место падения.
				if (animation.TryGetRagdollRootWorld(entity.Id, out var root))
				{
					var parentToWorld = PrefabSceneViewport.ParentToWorldMatrix(entity);
					var local = ToLocal(root, parentToWorld);

					// Высота - НЕ от кости (таз лежащего висит над полом на своей толщине, встать на
					// ней значит зависнуть) и НЕ «с которой падал»: пол под кругом больше не ровный
					// (кочка), и рэгдолл, съехавший со склона, вставал бы висящим в воздухе. Пол
					// спрашивается лучом под местом, где персонаж лёг; промах луча (лёг за краем
					// геометрии) оставляет прежнюю высоту - хуже от неё не станет.
					ref var shape = ref entity.GetComponent<CharacterBodyComponent>();
					float reach = MathF.Max(shape.Height, 0.1f);
					var ground = physics.SampleGround(
						root + new Vector3(0f, reach, 0f), -Vector3.UnitY, reach * 4f);

					float y = ground.Hit
						? ToLocal(ground.Position, parentToWorld).Y
						: entity.Position.value.Y;

					entity.Position = new Position(local.X, y, local.Z);

					// Встать ВДОЛЬ лежащего тела, а не докручиваться из поворота, с которым падал:
					// укатившийся (или утолканный) рэгдолл лежит под произвольным углом, и подъём
					// без разворота проворачивал корпус на весь этот угол. Снимок лежачей позы
					// ребейзится уже в ПОВЁРНУТЫЙ трансформ (см. BeginRecovery ниже), поэтому
					// видимая поза от разворота не двигается.
					if (animation.TryGetLyingFacing(entity.Id, out var lyingForward))
					{
						entity.GetComponent<Rotation>().value = CircleMotion.FacingFor(modelForward,
							ToLocalDirection(lyingForward, parentToWorld));
					}
				}

				// Клип подъёма - по фактической позе лёжки (спина/живот); заданный лишь один идёт
				// на обе, пустые оба - процедурный морф, прежнее поведение.
				string getUpClip = animation.TryGetLyingSide(entity.Id, out bool onBack) && onBack
					? fall.GetUpBackClip
					: fall.GetUpBellyClip;

				if (string.IsNullOrEmpty(getUpClip))
				{
					getUpClip = string.IsNullOrEmpty(fall.GetUpBellyClip)
						? fall.GetUpBackClip
						: fall.GetUpBellyClip;
				}

				// Рэгдолл обратно в режим анимации, поза - переходом от лежачей. Трансформ - УЖЕ
				// ПЕРЕНЕСЁННЫЙ: снимок лежачей позы ребейзится в него (см. BeginRecovery), иначе
				// поза прыгала бы на величину переноса в момент начала подъёма.
				ragdoll.Physical = false;
				animation.BeginRecovery(entity.Id, fall.GetUpDuration,
					PrefabSceneViewport.ComputeWorldMatrix(entity), getUpClip ?? string.Empty);

				fall.State = CharacterMotionState.Recovering;
				fall.StateTime = 0f;
				return false;
			}

			default:
			{
				if (animation.IsRecovering(entity.Id))
				{
					return false;
				}

				// Рэгдолл ГАСИТСЯ, а не остаётся в режиме следования: kinematic-тела костей у
				// идущего персонажа сидят ровно там же, где его капсула, и решатель каждый шаг
				// выталкивал её из них - тело ехало 2.2-2.7 м/с при заданной 1 м/с и мотало его
				// вокруг круга (радиус гулял 0.85..2.5). Выглядит это как «сломалось рулевое»,
				// а рулевое исправно рулит телом с посторонней тягой. Заодно восстанавливается
				// инвариант старта: у идущего персонажа рэгдолла нет, его включает падение.
				ragdoll.Enabled = false;

				fall.State = CharacterMotionState.Moving;
				fall.StateTime = 0f;
				return true;
			}
		}
	}

	/// <summary>
	/// Переносит позы тел в трансформы сущностей. Звать ПОСЛЕ <see cref="ScenePhysics.Update"/> -
	/// в этом и смысл разделения: до шага у тела поза прошлого кадра, и сцена рисовалась бы с
	/// отставанием на кадр от собственной физики.
	/// </summary>
	public void Apply(EntityStore? store, ScenePhysics? physics)
	{
		if (store == null || physics == null || _characters.Count == 0)
		{
			return;
		}

		// Обход по ТЕЛУ, а не по скрипту: скриптов движения уже два (круг и игрок), а перенос позы
		// тела в трансформ у них одинаковый. Настройки доворота снял Steer - тот, кто телом рулил.
		store.Query<CharacterBodyComponent, Position, Rotation>().ForEachEntity(
			(ref CharacterBodyComponent shape, ref Position position, ref Rotation rotation,
				Entity entity) =>
		{
			if (!_characters.TryGetValue(entity.Id, out var character))
			{
				return;
			}

			var parentToWorld = PrefabSceneViewport.ParentToWorldMatrix(entity);
			var pose = physics.World.Simulation.Bodies[character.Body].Pose;
			var feet = pose.Position - new Vector3(0f, character.Height * 0.5f, 0f);

			position.value = ToLocal(feet, parentToWorld);

			if (character.FaceMotion)
			{
				rotation.value = CircleMotion.FacingFor(character.ModelForward,
					ToLocalDirection(character.SmoothedFacing, parentToWorld));
			}
		});
	}

	/// <summary>Снимает все тела. Звать при выключении физики сцены и при смене префаба: хендлы
	/// принадлежат КОНКРЕТНОЙ симуляции, и пережить её уничтожение не могут (см.
	/// <see cref="AnimationDriver.DetachPhysics"/> - там та же причина).</summary>
	public void Clear(ScenePhysics? physics)
	{
		if (_characters.Count == 0)
		{
			return;
		}

		if (physics != null)
		{
			foreach (var character in _characters.Values)
			{
				physics.World.Remove(character.Body);
				physics.World.RemoveShape(character.Shape);
			}
		}

		_characters.Clear();
	}

	private Character EnsureBody(Entity entity, in CharacterBodyComponent shape, Vector3 localPosition,
		ScenePhysics physics, Matrix4x4 parentToWorld)
	{
		float radius = MathF.Max(shape.Radius, 1e-3f);
		float height = MathF.Max(shape.Height, radius * 2f);

		if (_characters.TryGetValue(entity.Id, out var existing))
		{
			// Сравнение точное, а не с допуском: размеры приезжают прямо из полей компонента и между
			// кадрами либо не меняются вовсе, либо меняются рукой в инспекторе. Мёртвая зона нужна
			// там, где величину РАЗЛАГАЮТ из матрицы (см. пересборку рэгдолла по масштабу сущности).
			if (existing.Radius == radius && existing.Height == height)
			{
				return existing;
			}

			RemoveCharacter(entity.Id, physics);
		}

		// Начальная поза - из трансформа сущности: автор поставил персонажа туда, где он должен
		// начать, и старт из любой другой точки выглядел бы как рывок в момент запуска.
		var feet = Vector3.Transform(localPosition, parentToWorld);

		var character = new Character
		{
			// Длина Bepu - это ЦИЛИНДРИЧЕСКАЯ часть, без полусфер: полная высота, переданная сюда как
			// есть, дала бы капсулу на два радиуса выше заказанной, и персонаж парил бы над полом.
			Shape = physics.World.AddCapsule(radius, MathF.Max(height - radius * 2f, 0f)),
			Radius = radius,
			Height = height,
			Facing = Vector3.UnitZ,
		};

		character.Body = physics.World.AddDynamic(
			new RigidPose(feet + new Vector3(0f, height * 0.5f, 0f)), character.Shape,
			MathF.Max(shape.Mass, 1e-3f));

		// Контакты персонажа - без трения. Трение гасило бы ровно ту скорость, которую скрипт задаёт
		// каждый кадр (замерено: 12.4% пути за оборот), а «не скользить по полу» этому телу и не
		// нужно - оно не катится и не съезжает, его горизонтальную скорость целиком задаёт код.
		physics.World.SetVelocityDriven(character.Body, true);

		_characters[entity.Id] = character;
		return character;
	}

	private void RemoveCharacter(int id, ScenePhysics physics)
	{
		if (!_characters.Remove(id, out var character))
		{
			return;
		}

		physics.World.Remove(character.Body);
		physics.World.RemoveShape(character.Shape);
	}

	// --- Пространства ------------------------------------------------------------------------------
	//
	// Тело живёт в МИРЕ, а Position/Rotation сущности - в родительском пространстве. В демо-сцене
	// корень префаба единичный, и разницы нет вовсе; но молчаливое допущение «они совпадают»
	// сломалось бы ровно тогда, когда персонажа положат в сдвинутое поддерево, и выглядело бы это
	// как «физика уехала», а не как «забыли про иерархию».

	private static Vector3 ToLocal(Vector3 world, Matrix4x4 parentToWorld) =>
		Matrix4x4.Invert(parentToWorld, out var inverse) ? Vector3.Transform(world, inverse) : world;

	private static Vector3 ToWorldDirection(Vector3 local, Matrix4x4 parentToWorld) =>
		Vector3.TransformNormal(local, parentToWorld);

	private static Vector3 ToLocalDirection(Vector3 world, Matrix4x4 parentToWorld) =>
		Matrix4x4.Invert(parentToWorld, out var inverse)
			? Vector3.TransformNormal(world, inverse)
			: world;
}
