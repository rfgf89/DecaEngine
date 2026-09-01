using System;
using System.Numerics;
using DecaEngine.Editor.ECS;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;

namespace DecaEngine.Editor;

/// <summary>
/// Проверка геймплейных скриптов сцены (DECA_PROBE_GAMEPLAY=1, печатается из PreviewProbe).
///
/// Ни графики, ни физики: проверяется ровно то, что скрипт делает с трансформом. Смотреть на это
/// глазами в редакторе можно, но нельзя ОТЛИЧИТЬ близкие поломки друг от друга - «круг превратился
/// в спираль», «скорость не та», «персонаж смотрит вбок на пару градусов» и «обход пошёл не в ту
/// сторону» выглядят одинаково: лиса ходит по кругу.
///
/// Система гоняется НАСТОЯЩАЯ и через настоящий <see cref="SystemRoot"/> - тем же путём, которым её
/// тикает Play Mode (см. <see cref="InspectorWindow.UpdatePlayMode"/>). Повторить формулу круга в
/// пробнике было бы короче, но проверяло бы саму себя.
/// </summary>
public static class GameplayProbe
{
	/// <summary>Шагов на оборот. Шаг выбирается ОТ ПЕРИОДА, а не берётся равным 1/60 с: проверка
	/// замыкания круга требует целого числа шагов на оборот, иначе «недоехал до старта» и
	/// «накопленная ошибка» неразличимы.</summary>
	private const int StepsPerLap = 600;

	public static void Run()
	{
		ProbeLap();
		ProbeReverse();
		ProbeModelForward();
		ProbeDisabled();
		ProbePhysicalLap();
		ProbeObstacle();
		ProbeStaticChurn();
		ProbePlayer();
		ProbeStepUp();
		ProbeJump();
	}

	/// <summary>
	/// Прыжок - три утверждения на одной сцене. Дуга: высота апекса обязана сойтись с баллистикой
	/// v²/2g - «прыгает, но не так высоко» означает, что скорость кто-то съедает (прижим к земле,
	/// например, обязан прыжку не мешать). Второй Space В ВОЗДУХЕ не даёт второй дуги - даблджамп
	/// никто не заказывал, а появляется он бесплатно из любого неаккуратного coyote time. Нулевой
	/// JumpSpeed (старые сцены) не прыгает вовсе.
	/// </summary>
	private static void ProbeJump()
	{
		using var scene = new ScenePhysics(new Vector3(0f, -9.81f, 0f));
		BuildGround(scene, wall: false);

		float Arc(float jumpSpeed, int extraJumpFrame)
		{
			var store = new EntityStore();
			var entity = store.CreateEntity();
			entity.AddComponent(new EntityName("jump"));
			entity.AddComponent(new Position(0f, 0f, 0f));
			entity.AddComponent(new Rotation(0f, 0f, 0f, 1f));
			entity.AddComponent(new PlayerMoveComponent { WalkSpeed = 1f, JumpSpeed = jumpSpeed });
			entity.AddComponent(new CharacterBodyComponent
			{
				Radius = 0.18f,
				Height = 0.5f,
				Mass = 12f,
				StepHeight = 0f,
			});

			var driver = new CharacterMotionDriver();
			int settle = (int)MathF.Round(0.5f / PhysicsStep);
			int flight = (int)MathF.Round(1.4f / PhysicsStep);
			float top = 0f;

			for (int i = 0; i < settle + flight; i++)
			{
				driver.Input = new PlayerInput
				{
					MoveWorld = Vector3.UnitX,
					Jump = i == settle || i == settle + extraJumpFrame,
				};

				// deltaSeconds обязателен: coyote time убывает ИМ, и прогон без него - это вечное
				// окно прыжка (ровно так первая версия этой проверки поймала даблджамп, который
				// был багом самой проверки лишь наполовину).
				driver.Steer(store, scene, active: true, PhysicsStep);
				scene.Update(PhysicsStep);
				driver.Apply(store, scene);

				if (i >= settle)
				{
					top = MathF.Max(top, Position(entity).Y);
				}
			}

			float landedY = Position(entity).Y;
			driver.Clear(scene);

			// Апекс засчитывается, только если персонаж ВЕРНУЛСЯ на землю: улетевший в никуда
			// прошёл бы проверку высоты с блеском.
			return MathF.Abs(landedY) < 0.03f ? top : float.MaxValue;
		}

		float single = Arc(jumpSpeed: 3.5f, extraJumpFrame: int.MaxValue);
		float doubled = Arc(jumpSpeed: 3.5f, extraJumpFrame: (int)MathF.Round(0.25f / PhysicsStep));
		float disabled = Arc(jumpSpeed: 0f, extraJumpFrame: int.MaxValue);

		float expected = 3.5f * 3.5f / (2f * 9.81f);

		bool arcOk = MathF.Abs(single - expected) < expected * 0.15f;
		bool doubleOk = doubled < single + 0.05f;
		bool disabledOk = disabled < 0.03f;

		Console.WriteLine($"[probe] gameplay: прыжок - апекс {single:0.###} (баллистика {expected:0.###}) " +
			$"{(arcOk ? "OK" : "ДУГА НЕ ТА")}, второй Space в воздухе - апекс {doubled:0.###} " +
			$"{(doubleOk ? "ДАБЛДЖАМПА НЕТ OK" : "ДАБЛДЖАМП")}, JumpSpeed=0 - подъём {disabled:0.###} " +
			$"{(disabledOk ? "НЕ ПРЫГАЕТ OK" : "ПРЫГАЕТ БЕЗ ПРАВА")}");
	}

	/// <summary>
	/// Step-up - ПАРОЙ на одной сцене: пол, ступень 0.16 м и стена 1.2 м по пути игрока, ветки
	/// отличаются только <see cref="CharacterBodyComponent.StepHeight"/>. Три утверждения, и нужны
	/// все: со step-up ступень ПРОЙДЕНА (тело поднималось на её высоту) и стена ДЕРЖИТ (иначе это не
	/// step-up, а прыжки через всё подряд); без него та же ступень персонажа останавливает - пара
	/// доказывает, что проходимость дала именно новая механика, а не изменившаяся геометрия.
	/// </summary>
	private static void ProbeStepUp()
	{
		using var scene = new ScenePhysics(new Vector3(0f, -9.81f, 0f));

		var vertices = new List<Vector3>();
		var indices = new List<uint>();

		AddQuad(vertices, indices,
			new Vector3(-25f, 0f, -25f), new Vector3(-25f, 0f, 25f),
			new Vector3(25f, 0f, 25f), new Vector3(25f, 0f, -25f));
		AddBox(vertices, indices, new Vector3(1.2f, 0f, -3f), new Vector3(4f, 0.16f, 3f));
		AddBox(vertices, indices, new Vector3(5f, 0f, -3f), new Vector3(5.2f, 1.2f, 3f));

		scene.BeginStatics();
		scene.AddStaticMesh(
			System.Runtime.InteropServices.CollectionsMarshal.AsSpan(vertices),
			System.Runtime.InteropServices.CollectionsMarshal.AsSpan(indices));
		scene.EndStatics();

		(float FinalX, float TopY) Branch(float stepHeight)
		{
			var store = new EntityStore();
			var entity = store.CreateEntity();
			entity.AddComponent(new EntityName("step"));
			entity.AddComponent(new Position(0f, 0f, 0f));
			entity.AddComponent(new Rotation(0f, 0f, 0f, 1f));
			entity.AddComponent(new PlayerMoveComponent { WalkSpeed = 1f, RunSpeed = 3f });
			entity.AddComponent(new CharacterBodyComponent
			{
				Radius = 0.18f,
				Height = 0.5f,
				Mass = 12f,
				StepHeight = stepHeight,
			});

			var driver = new CharacterMotionDriver();
			int steps = (int)MathF.Round(8f / PhysicsStep);
			float topY = 0f;

			for (int i = 0; i < steps; i++)
			{
				driver.Input = new PlayerInput { MoveWorld = Vector3.UnitX };
				driver.Steer(store, scene, active: true);
				scene.Update(PhysicsStep);
				driver.Apply(store, scene);

				topY = MathF.Max(topY, Position(entity).Y);
			}

			driver.Clear(scene);
			return (Position(entity).X, topY);
		}

		var with = Branch(stepHeight: 0.25f);
		var without = Branch(stepHeight: 0f);

		bool climbedOk = with.TopY > 0.12f && with.FinalX > 4.4f;
		bool wallOk = with.FinalX < 5.0f;
		bool blockedOk = without.FinalX < 1.35f;

		Console.WriteLine($"[probe] gameplay: step-up - со ступенькой дошёл до x={with.FinalX:0.##} " +
			$"(поднимался до y={with.TopY:0.###}) {(climbedOk ? "ПРОШЁЛ OK" : "НЕ ВЗЯЛ СТУПЕНЬ")}, " +
			$"стена {(wallOk ? "ДЕРЖИТ OK" : "ПЕРЕПРЫГНУЛ СТЕНУ")}; без step-up дошёл до " +
			$"x={without.FinalX:0.##} {(blockedOk ? "СТУПЕНЬ ДЕРЖИТ OK" : "ПАРА НЕ РАЗОШЛАСЬ")}");
	}

	/// <summary>
	/// Управление игрока (см. <see cref="PlayerMoveComponent"/>) - headless, ввод пишется в привод
	/// руками, ровно как его пишет вьюпорт. Три фазы одной сценой: ходьба по диагонали (заодно
	/// проверяется нормировка - зажатые W+D не должны давать корень из двух скорости), бег по Shift,
	/// отпущенные клавиши. Последняя фаза - не формальность: velocity-driven тело без ввода обязано
	/// ВСТАТЬ, а не скользить по инерции.
	/// </summary>
	private static void ProbePlayer()
	{
		using var scene = new ScenePhysics(new Vector3(0f, -9.81f, 0f));
		BuildGround(scene, wall: false);

		var store = new EntityStore();
		var entity = store.CreateEntity();
		entity.AddComponent(new EntityName("player"));
		entity.AddComponent(new Position(0f, 0f, 0f));
		entity.AddComponent(new Rotation(0f, 0f, 0f, 1f));
		entity.AddComponent(new PlayerMoveComponent { WalkSpeed = 1f, RunSpeed = 3f, Forward = Vector3.UnitZ });
		entity.AddComponent(new CharacterBodyComponent { Radius = 0.18f, Height = 0.5f, Mass = 12f });

		var driver = new CharacterMotionDriver();
		var diagonal = new Vector3(1f, 0f, 1f);

		Vector3 Run(PlayerInput input, float seconds)
		{
			var from = Position(entity);
			int steps = (int)MathF.Round(seconds / PhysicsStep);

			for (int i = 0; i < steps; i++)
			{
				driver.Input = input;
				driver.Steer(store, scene, active: true);
				scene.Update(PhysicsStep);
				driver.Apply(store, scene);
			}

			var delta = Position(entity) - from;
			return new Vector3(delta.X, 0f, delta.Z);
		}

		var walk = Run(new PlayerInput { MoveWorld = diagonal }, seconds: 2f);
		var sprint = Run(new PlayerInput { MoveWorld = diagonal, Run = true }, seconds: 1f);
		var stop = Run(default, seconds: 1f);

		var direction = Vector3.Normalize(diagonal);
		float walkDot = walk.Length() > 1e-4f ? Vector3.Dot(Vector3.Normalize(walk), direction) : 0f;

		// Разворот: «вперёд» модели (+Z, повёрнутый поворотом сущности) обязан смотреть по ходу.
		// Не через Facing(): тот читает Forward из компонента КРУГА, которого у игрока нет.
		var facing = Vector3.Transform(Vector3.UnitZ, Rotation(entity));
		float facingDot = Vector3.Dot(facing, direction);

		bool walkOk = MathF.Abs(walk.Length() - 2f) < 0.1f && walkDot > 0.999f;
		bool sprintOk = MathF.Abs(sprint.Length() - 3f) < 0.15f;
		bool stopOk = stop.Length() < 0.02f;
		bool facingOk = facingDot > 0.999f;

		Console.WriteLine($"[probe] gameplay: игрок - ходьба {walk.Length():0.###} м за 2 с " +
			$"(ожидалось 2, вдоль ввода {walkDot:0.####}) {(walkOk ? "OK" : "НЕ ТУДА/НЕ СТОЛЬКО")}, " +
			$"бег {sprint.Length():0.###} м за 1 с (ожидалось 3) {(sprintOk ? "OK" : "СКОРОСТЬ НЕ ТА")}");
		Console.WriteLine($"[probe] gameplay: игрок - без ввода снесло на {stop.Length():0.####} м " +
			$"{(stopOk ? "СТОИТ OK" : "СКОЛЬЗИТ")}, разворот по ходу {facingDot:0.####} " +
			$"{(facingOk ? "OK" : "СМОТРИТ НЕ ТУДА")}");
	}

	/// <summary>Один полный оборот: форма круга, скорость вдоль него, замыкание и разворот по
	/// касательной.</summary>
	private static void ProbeLap()
	{
		const float radius = 2f;
		const float speed = 1f;
		var center = new Vector3(3f, 0.5f, -4f);

		var (store, entity) = Build(center, radius, speed);
		var root = new SystemRoot { new CircleMoveSystem() };
		root.AddStore(store);

		float period = MathF.Tau * radius / speed;
		float dt = period / StepsPerLap;

		var start = Position(entity);

		float worstRadius = 0f;
		float worstFacing = 0f;
		float path = 0f;
		var previous = start;

		for (int i = 0; i < StepsPerLap; i++)
		{
			root.Update(new UpdateTick(dt, dt * (i + 1)));

			var position = Position(entity);

			// Радиус - в плоскости XZ: высота обязана остаться высотой ЦЕНТРА, и вертикальный увод
			// ловится отдельной проверкой ниже, а не размазывается по радиусу.
			var offset = position - center;
			worstRadius = MathF.Max(worstRadius, MathF.Abs(
				MathF.Sqrt(offset.X * offset.X + offset.Z * offset.Z) - radius));

			float travelled = (position - previous).Length();
			path += travelled;

			// Разворот сверяется с РЕАЛЬНЫМ перемещением, а не с формулой касательной: иначе проверка
			// не заметила бы, что вперёд у сущности вовсе не +Z (перепутанный atan2 даёт персонажа,
			// идущего боком, и на неподвижном кадре это не видно).
			if (travelled > 1e-6f)
			{
				var motion = Vector3.Normalize(position - previous);
				var forward = Facing(entity);
				worstFacing = MathF.Max(worstFacing,
					MathF.Acos(Math.Clamp(Vector3.Dot(forward, motion), -1f, 1f)));
			}

			previous = position;
		}

		var end = Position(entity);
		float closure = (end - start).Length();
		float expectedPath = period * speed;

		// Допуск пути - десятая доля процента: хорда короче дуги, и на 600 шагах эта разница
		// составляет 0.005%. Нулевой допуск ловил бы геометрию ломаной, а не ошибку скорости.
		bool shapeOk = worstRadius < 1e-3f && MathF.Abs(end.Y - center.Y) < 1e-4f;
		bool speedOk = MathF.Abs(path - expectedPath) < expectedPath * 1e-3f;
		bool closureOk = closure < 1e-3f;

		// Полградуса: поза берётся в конце шага, а перемещение - за весь шаг, поэтому касательная и
		// хорда законно расходятся на полшага по фазе (на 600 шагах это 0.3°).
		bool facingOk = worstFacing < 0.5f * MathF.PI / 180f;

		Console.WriteLine($"[probe] gameplay: круг - худшее отклонение радиуса {worstRadius:0.#####} " +
			$"{(shapeOk ? "OK" : "НЕ КРУГ")}, высота {Position(entity).Y:0.####} " +
			$"(ожидалась {center.Y})");
		Console.WriteLine($"[probe] gameplay: путь за оборот {path:0.####} " +
			$"(ожидался {expectedPath:0.####}) {(speedOk ? "OK" : "СКОРОСТЬ НЕ ТА")}");
		Console.WriteLine($"[probe] gameplay: замыкание оборота {closure:0.#####} " +
			$"{(closureOk ? "OK" : "КРУГ НЕ ЗАМКНУЛСЯ")}");
		Console.WriteLine($"[probe] gameplay: разворот по ходу - худшее расхождение " +
			$"{worstFacing * 180f / MathF.PI:0.###}° {(facingOk ? "OK" : "СМОТРИТ НЕ ТУДА")}");
	}

	/// <summary>Отрицательная скорость: обход в другую сторону, и персонаж смотрит по ходу, а не
	/// пятится. Проверка не формальная - знак теряется ровно в одном месте (касательная), и потеря
	/// даёт лису, идущую задом наперёд по правильному кругу.</summary>
	private static void ProbeReverse()
	{
		const float radius = 2f;
		var center = Vector3.Zero;

		var (store, entity) = Build(center, radius, speed: -1f);
		var root = new SystemRoot { new CircleMoveSystem() };
		root.AddStore(store);

		var start = Position(entity);
		root.Update(new UpdateTick(0.25f, 0.25f));
		var position = Position(entity);

		// Обход от +X при отрицательной скорости уходит в -Z.
		bool directionOk = position.Z < start.Z - 1e-4f;

		var motion = Vector3.Normalize(position - start);
		float facing = MathF.Acos(Math.Clamp(Vector3.Dot(Facing(entity), motion), -1f, 1f)) * 180f / MathF.PI;

		// Допуск крупнее, чем в обороте: шаг здесь один и большой (четверть секунды), и полшага по
		// фазе - это уже 3.6°. Смысл проверки - отличить «вперёд» от «назад», а не мерить точность.
		Console.WriteLine($"[probe] gameplay: обратный обход - z {start.Z:0.###} -> {position.Z:0.###} " +
			$"{(directionOk ? "OK" : "СТОРОНА НЕ ТА")}, разворот {facing:0.##}° " +
			$"{(facing < 5f ? "OK" : "ПЯТИТСЯ")}");
	}

	/// <summary>Выключенный компонент и нулевой радиус. Нулевой радиус - это деление на ноль в
	/// угловой скорости: без явной проверки он даёт не «стояние на месте», а NaN в трансформе,
	/// после которого персонаж исчезает из кадра целиком.</summary>
	private static void ProbeDisabled()
	{
		var (store, disabled) = Build(Vector3.Zero, radius: 2f, speed: 1f);
		disabled.GetComponent<CircleMoveComponent>().Enabled = false;

		var zeroRadius = store.CreateEntity();
		zeroRadius.AddComponent(new Position(1f, 2f, 3f));
		zeroRadius.AddComponent(new Rotation(0f, 0f, 0f, 1f));
		zeroRadius.AddComponent(new CircleMoveComponent { Radius = 0f, Speed = 1f });

		var root = new SystemRoot { new CircleMoveSystem() };
		root.AddStore(store);

		var disabledStart = Position(disabled);
		var zeroStart = Position(zeroRadius);

		for (int i = 0; i < 10; i++)
		{
			root.Update(new UpdateTick(1f / 60f, (i + 1) / 60f));
		}

		var zeroEnd = Position(zeroRadius);
		bool zeroOk = zeroEnd == zeroStart && float.IsFinite(zeroEnd.X) && float.IsFinite(zeroEnd.Y) &&
			float.IsFinite(zeroEnd.Z);

		Console.WriteLine($"[probe] gameplay: выключенный компонент - смещение " +
			$"{(Position(disabled) - disabledStart).Length():0.#####} " +
			$"{(Position(disabled) == disabledStart ? "OK" : "ДВИГАЕТ ВЫКЛЮЧЕННЫМ")}");
		Console.WriteLine($"[probe] gameplay: нулевой радиус - позиция {zeroEnd} " +
			$"{(zeroOk ? "OK" : "NAN/СДВИГ")}");
	}

	/// <summary>
	/// Модель, у которой «вперёд» - не +Z (у Khronos Fox морда смотрит в -Z). Проверка отдельная и
	/// нужна именно здесь: числа оборота у персонажа, идущего задом наперёд, ИДЕАЛЬНЫ - круг тот же,
	/// скорость та же, замыкание то же. Ошибку видно только по тому, куда смотрит собственный
	/// «вперёд» модели, и до этого поля она полдня выглядела как «всё OK, но в редакторе не то».
	/// </summary>
	private static void ProbeModelForward()
	{
		var forward = -Vector3.UnitZ;
		var (store, entity) = Build(Vector3.Zero, radius: 2f, speed: 1f, forward: forward);

		var root = new SystemRoot { new CircleMoveSystem() };
		root.AddStore(store);

		var start = Position(entity);
		root.Update(new UpdateTick(0.25f, 0.25f));

		var motion = Vector3.Normalize(Position(entity) - start);

		// Сверяются ДВЕ вещи, и вторая - главная. Первая: собственный «вперёд» модели лёг по ходу.
		// Вторая: ось +Z при этом смотрит ПРОТИВ хода - без неё проверка прошла бы и у системы,
		// которая Forward попросту игнорирует (у неё +Z совпал бы с ходом, и первое число тоже было
		// бы близко к нулю, если бы Forward остался равен +Z).
		float aligned = MathF.Acos(Math.Clamp(Vector3.Dot(Facing(entity), motion), -1f, 1f)) * 180f / MathF.PI;
		float axisZ = Vector3.Dot(Vector3.Transform(Vector3.UnitZ, Rotation(entity)), motion);

		Console.WriteLine($"[probe] gameplay: модель смотрит в {forward} - расхождение с ходом " +
			$"{aligned:0.##}° {(aligned < 5f ? "OK" : "ЗАДОМ НАПЕРЁД")}, ось +Z против хода " +
			$"{axisZ:0.###} {(axisZ < -0.9f ? "OK" : "FORWARD ПРОИГНОРИРОВАН")}");
	}

	/// <summary>Сущность на круге в нулевой фазе - ровно так, как её кладёт в сцену генератор
	/// демо-префаба (см. SamplePrefabBuilder.CreateCircleFox): в точке +X от центра.</summary>
	private static (EntityStore Store, Entity Entity) Build(Vector3 center, float radius, float speed,
		Vector3? forward = null, bool physical = false)
	{
		var store = new EntityStore();
		var entity = store.CreateEntity();

		var start = center + new Vector3(radius, 0f, 0f);

		entity.AddComponent(new Position(start.X, start.Y, start.Z));
		entity.AddComponent(new Rotation(0f, 0f, 0f, 1f));
		entity.AddComponent(new CircleMoveComponent
		{
			Enabled = true,
			Center = center,
			Radius = radius,
			Speed = speed,
			Angle = 0f,
			FaceMotion = true,
			Forward = forward ?? Vector3.UnitZ,
		});

		if (physical)
		{
			// Тело - ОТДЕЛЬНЫМ компонентом, как и в сцене: «физический ли персонаж» решает само его
			// присутствие. Габарит лисий (см. SamplePrefabBuilder): проверять физику на «человеке»
			// 1.8 м, когда в сцене ходит полуметровый зверь, значило бы мерить не тот масштаб
			// контактов.
			entity.AddComponent(new CharacterBodyComponent
			{
				Radius = 0.18f,
				Height = 0.5f,
				Mass = 12f,
			});
		}

		return (store, entity);
	}

	// --- Физический путь: капсула в мире сцены (см. CharacterMotionDriver) --------------------------

	/// <summary>Шаг симуляции. Совпадает с типичным кадром редактора намеренно: у физики свой
	/// фиксированный шаг внутри (см. PhysicsWorld), и подавать ей нереалистично мелкое дельта значило
	/// бы проверять режим, в котором сцена никогда не работает.</summary>
	private const float PhysicsStep = 1f / 60f;

	/// <summary>
	/// Оборот на ФИЗИЧЕСКОМ теле: та же геометрия круга, но позицию задаёт симуляция, а скрипт задаёт
	/// только скорость.
	///
	/// Числа здесь заведомо грубее, чем у трансформа, и это не недостаток проверки, а свойство
	/// предмета: тело едет по инерции, гасится контактом с полом и возвращается на окружность
	/// рулевым, а не ставится на неё. Допуски подобраны по измеренному поведению - см. вывод.
	/// </summary>
	private static void ProbePhysicalLap()
	{
		const float radius = 2f;
		const float speed = 1f;

		using var scene = new ScenePhysics(new Vector3(0f, -9.81f, 0f));
		BuildGround(scene, wall: false);

		var (store, entity) = Build(Vector3.Zero, radius, speed, physical: true);
		var driver = new CharacterMotionDriver();

		float period = MathF.Tau * radius / speed;
		int steps = (int)MathF.Round(period / PhysicsStep);

		float worstRadius = 0f;
		float path = 0f;
		float turned = 0f;
		float previousAngle = 0f;
		var previous = Position(entity);
		float worstHeight = 0f;

		for (int i = 0; i < steps; i++)
		{
			// Порядок ровно тот же, что в кадре редактора (см. PrefabSceneViewport.PollScenePhysics):
			// рулевое до шага, перенос позы - после. Проверка, гоняющая свой порядок, не заметила бы
			// именно той ошибки, ради которой это разделено.
			driver.Steer(store, scene, active: true);
			scene.Update(PhysicsStep);
			driver.Apply(store, scene);

			var position = Position(entity);

			float distance = MathF.Sqrt(position.X * position.X + position.Z * position.Z);
			worstRadius = MathF.Max(worstRadius, MathF.Abs(distance - radius));
			worstHeight = MathF.Max(worstHeight, MathF.Abs(position.Y));

			var step = position - previous;
			path += MathF.Sqrt(step.X * step.X + step.Z * step.Z);

			// Пройденный УГОЛ копится приращениями: измеренная фаза свёрнута в один оборот, и разность
			// «конец минус начало» у полного круга дала бы ноль.
			float angle = MathF.Atan2(position.Z, position.X);
			turned += CircleMotion.Wrap(angle - previousAngle);
			previousAngle = angle;

			previous = position;
		}

		float expectedPath = period * speed;

		// Пять сантиметров на радиусе двухметрового круга - 2.5%. Рулевое возвращает тело на
		// окружность за время порядка 1/RadialGain, и в установившемся движении отклонение держится
		// заметно меньше; допуск оставлен с запасом на разгон в первые кадры.
		bool shapeOk = worstRadius < 0.05f;
		bool speedOk = MathF.Abs(path - expectedPath) < expectedPath * 0.05f;
		bool lapOk = MathF.Abs(turned - MathF.Tau) < 0.1f;

		// Ноги на полу. Это не придирка: капсула, заведённая с перепутанной серединой, живёт наполовину
		// в полу или парит над ним, и на картинке это выглядит как «модель не той высоты».
		bool groundOk = worstHeight < 0.03f;

		Console.WriteLine($"[probe] gameplay: физический круг - худшее отклонение радиуса " +
			$"{worstRadius:0.####} {(shapeOk ? "OK" : "НЕ КРУГ")}, отрыв ног от пола {worstHeight:0.####} " +
			$"{(groundOk ? "OK" : "ВИСИТ/ТОНЕТ")}");
		Console.WriteLine($"[probe] gameplay: физический круг - путь {path:0.###} " +
			$"(ожидался {expectedPath:0.###}) {(speedOk ? "OK" : "СКОРОСТЬ НЕ ТА")}, пройдено " +
			$"{turned / MathF.Tau:0.###} оборота {(lapOk ? "OK" : "ОБОРОТ НЕ ЗАКРЫТ")}");
	}

	/// <summary>
	/// ГЛАВНАЯ проверка физического режима: стена поперёк круга.
	///
	/// Обе ветки гоняются на ОДНОЙ сцене и одном компоненте, отличаясь только флагом Physical, и
	/// сравниваются между собой. Само по себе «тело остановилось» ничего не доказывает - оно могло
	/// остановиться от чего угодно; доказывает пара: трансформ проходит сквозь стену, тело - нет.
	/// Ради этой пары физика в скрипт и заводилась.
	/// </summary>
	private static void ProbeObstacle()
	{
		const float radius = 2f;
		const float seconds = 8f;
		int steps = (int)MathF.Round(seconds / PhysicsStep);

		using var scene = new ScenePhysics(new Vector3(0f, -9.81f, 0f));
		BuildGround(scene, wall: true);

		var (physicalStore, physicalEntity) = Build(Vector3.Zero, radius, speed: 1f, physical: true);
		var driver = new CharacterMotionDriver();

		var (transformStore, transformEntity) = Build(Vector3.Zero, radius, speed: 1f);
		var root = new SystemRoot { new CircleMoveSystem() };
		root.AddStore(transformStore);

		float physicalMinX = float.MaxValue;
		float transformMinX = float.MaxValue;

		for (int i = 0; i < steps; i++)
		{
			driver.Steer(physicalStore, scene, active: true);
			scene.Update(PhysicsStep);
			driver.Apply(physicalStore, scene);

			root.Update(new UpdateTick(PhysicsStep, PhysicsStep * (i + 1)));

			physicalMinX = MathF.Min(physicalMinX, Position(physicalEntity).X);
			transformMinX = MathF.Min(transformMinX, Position(transformEntity).X);
		}

		// Стена стоит в плоскости x=0 (полутолщина 0.1) и тянется от центра наружу, так что круг
		// упирается в неё лицом. Тело обязано остаться по СВОЮ сторону: ближе x=0.1+0.18 (стена плюс
		// радиус капсулы) ему не подойти, и запас 5 см - на продавливание контакта решателем.
		bool blocked = physicalMinX > 0.23f;
		bool crossed = transformMinX < -1f;

		Console.WriteLine($"[probe] gameplay: стена поперёк круга - тело дошло до x={physicalMinX:0.###} " +
			$"{(blocked ? "OK (не прошло)" : "ПРОШЛО СКВОЗЬ СТЕНУ")}, трансформ - до x={transformMinX:0.###} " +
			$"{(crossed ? "OK (прошёл, как и должен)" : "НЕ ПРОШЁЛ - СРАВНИВАТЬ НЕ С ЧЕМ")}");
	}

	/// <summary>
	/// Пересборка статики ПОД идущим персонажем.
	///
	/// Проверка появилась по факту поломки, и это её главная ценность. Вьюпорт помечал статику
	/// устаревшей на движение ЛЮБОЙ модели, а идущий персонаж двигается каждый кадр - пол снимался и
	/// заводился заново по шестьдесят раз в секунду. Тело при этом каждый кадр теряет накопленные
	/// импульсы контакта, не успевает опереться и уходит в свободное падение; в редакторе это выглядит
	/// как «персонаж провалился сквозь пол», а заодно сыплются рэгдоллы и лучи foot IK.
	///
	/// Печатаются ОБА числа - со стабильной статикой и с пересобираемой. Одно само по себе ничего не
	/// значит: «тело стоит» проходит и там, где пересборки просто нет, а пара показывает цену.
	/// </summary>
	private static void ProbeStaticChurn()
	{
		float stable = RunChurn(rebuildEveryFrame: false);
		float churn = RunChurn(rebuildEveryFrame: true);

		// Сантиметр - тот же допуск, что у прочих проверок опоры: решатель Bepu оставляет телу
		// небольшое проникновение и не обязан приводить его РОВНО на ноль.
		bool stableOk = MathF.Abs(stable) < 0.01f;

		Console.WriteLine($"[probe] gameplay: статика стабильна - ноги на {stable:0.####} " +
			$"{(stableOk ? "OK" : "ПРОВАЛИЛСЯ")}; статика пересобирается каждый кадр - ноги на " +
			$"{churn:0.####} {(MathF.Abs(churn) < 0.01f ? "(тоже держится)" : "- ЦЕНА ПЕРЕСБОРКИ, персонаж проваливается")}");
	}

	/// <summary>Две секунды ходьбы; возвращает высоту ног в конце. Ноль - персонаж на полу.</summary>
	private static float RunChurn(bool rebuildEveryFrame)
	{
		using var scene = new ScenePhysics(new Vector3(0f, -9.81f, 0f));
		BuildGround(scene, wall: false);

		var (store, entity) = Build(Vector3.Zero, radius: 2f, speed: 1f, physical: true);
		var driver = new CharacterMotionDriver();

		int steps = (int)MathF.Round(2f / PhysicsStep);

		for (int i = 0; i < steps; i++)
		{
			if (rebuildEveryFrame)
			{
				BuildGround(scene, wall: false);
			}

			driver.Steer(store, scene, active: true);
			scene.Update(PhysicsStep);
			driver.Apply(store, scene);
		}

		return Position(entity).Y;
	}

	/// <summary>Пол, а при <paramref name="wall"/> - ещё и стена поперёк круга. Оба - МЕШЕМ, тем же
	/// путём, которым в сцену попадает её геометрия (см. PrefabSceneViewport.RebuildPhysicsStatics):
	/// коробкой-примитивом проверялся бы код, которым сцена не пользуется.</summary>
	private static void BuildGround(ScenePhysics scene, bool wall)
	{
		var vertices = new List<Vector3>();
		var indices = new List<uint>();

		AddQuad(vertices, indices,
			new Vector3(-25f, 0f, -25f), new Vector3(-25f, 0f, 25f),
			new Vector3(25f, 0f, 25f), new Vector3(25f, 0f, -25f));

		if (wall)
		{
			AddBox(vertices, indices, new Vector3(-0.1f, 0f, 0.5f), new Vector3(0.1f, 1.2f, 6f));
		}

		scene.BeginStatics();
		scene.AddStaticMesh(
			System.Runtime.InteropServices.CollectionsMarshal.AsSpan(vertices),
			System.Runtime.InteropServices.CollectionsMarshal.AsSpan(indices));
		scene.EndStatics();
	}

	/// <summary>Обход - КАК У ГЕОМЕТРИИ ДВИЖКА, тот же (a,b,c)+(a,c,d), которым выкладывает площадку
	/// SampleGroundBuilder; разворот под односторонний меш Bepu делает PhysicsWorld.AddTriangleMesh.
	/// Класть здесь «удобный» порядок нельзя: рукописная геометрия, выложенная наоборот, компенсирует
	/// ошибку конвенции и прячет её - ровно это и случилось однажды.</summary>
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

	private static void AddBox(List<Vector3> vertices, List<uint> indices, Vector3 min, Vector3 max)
	{
		// Все шесть граней: односторонний меш с пропущенной гранью - это стена с дырой ровно с той
		// стороны, с которой в неё и идут.
		AddQuad(vertices, indices,
			new Vector3(min.X, min.Y, min.Z), new Vector3(min.X, max.Y, min.Z),
			new Vector3(max.X, max.Y, min.Z), new Vector3(max.X, min.Y, min.Z));
		AddQuad(vertices, indices,
			new Vector3(max.X, min.Y, max.Z), new Vector3(max.X, max.Y, max.Z),
			new Vector3(min.X, max.Y, max.Z), new Vector3(min.X, min.Y, max.Z));
		AddQuad(vertices, indices,
			new Vector3(min.X, min.Y, max.Z), new Vector3(min.X, max.Y, max.Z),
			new Vector3(min.X, max.Y, min.Z), new Vector3(min.X, min.Y, min.Z));
		AddQuad(vertices, indices,
			new Vector3(max.X, min.Y, min.Z), new Vector3(max.X, max.Y, min.Z),
			new Vector3(max.X, max.Y, max.Z), new Vector3(max.X, min.Y, max.Z));
		AddQuad(vertices, indices,
			new Vector3(min.X, min.Y, min.Z), new Vector3(max.X, min.Y, min.Z),
			new Vector3(max.X, min.Y, max.Z), new Vector3(min.X, min.Y, max.Z));
		AddQuad(vertices, indices,
			new Vector3(min.X, max.Y, max.Z), new Vector3(max.X, max.Y, max.Z),
			new Vector3(max.X, max.Y, min.Z), new Vector3(min.X, max.Y, min.Z));
	}

	private static Vector3 Position(Entity entity) => entity.GetComponent<Position>().value;

	private static Quaternion Rotation(Entity entity) => entity.GetComponent<Rotation>().value;

	/// <summary>Куда смотрит сущность сейчас: собственный «вперёд» модели, повёрнутый её поворотом.
	/// Именно эта величина обязана лечь по ходу движения - а не ось +Z, которая совпадает с ней
	/// только у моделей, экспортированных мордой в +Z.</summary>
	private static Vector3 Facing(Entity entity) => Vector3.Transform(
		Vector3.Normalize(entity.GetComponent<CircleMoveComponent>().Forward), Rotation(entity));
}
