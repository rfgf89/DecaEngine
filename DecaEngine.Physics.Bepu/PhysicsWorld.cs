using System;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities.Memory;

namespace DecaEngine.Physics;

/// <summary>Результат райкаста. <see cref="Hit"/> = false означает «луч ничего не задел» - остальные
/// поля тогда не определены.</summary>
public struct RayHit
{
	public bool Hit;
	public Vector3 Position;
	public Vector3 Normal;
	public float Distance;

	/// <summary>Во что попали. Для динамического тела это его handle, для статики - handle статика;
	/// различать их вызывающий может по <see cref="IsStatic"/>.</summary>
	public int Collidable;
	public bool IsStatic;
}

/// <summary>
/// Мир физики: симуляция Bepu с ФИКСИРОВАННЫМ шагом плюс накопитель времени кадра. Фиксированный
/// шаг - не вкусовщина: и решатель контактов, и моторы рэгдолла настраиваются под конкретный dt, и
/// на переменном шаге те же настройки дают разное поведение при разном FPS, вплоть до
/// расходящегося рэгдолла на просадке.
///
/// Помимо симуляции отдаёт райкасты - на них держится вся привязка анимации к геометрии (foot IK
/// щупает пол под стопой каждый кадр).
/// </summary>
public sealed class PhysicsWorld : IDisposable
{
	/// <summary>Шаг симуляции. 1/120 с, а не 1/60: рэгдолл с моторами и цепочками суставов на 60 Гц
	/// заметно «резиновый», а вдвое мельче шаг стоит дешевле, чем добавление субшагов решателю.</summary>
	public const float FixedTimeStep = 1f / 120f;

	/// <summary>Потолок шагов за один <see cref="Update"/>. Без него длинный кадр (компиляция
	/// шейдера, загрузка модели) порождает лавину шагов, каждый из которых снова удлиняет кадр -
	/// классическая спираль смерти. Лишнее время просто отбрасывается: замедление симуляции честнее
	/// зависания.</summary>
	private const int MaxStepsPerUpdate = 8;

	private readonly BufferPool _pool;
	private float _accumulator;

	public Simulation Simulation { get; }

	/// <summary>Доля шага, накопленная сверх последнего проинтегрированного: 0..1. Ею интерполируются
	/// позы для рендера, иначе тела заметно дрожат, когда частота кадров не кратна шагу симуляции.</summary>
	public float InterpolationAlpha => _accumulator / FixedTimeStep;

	/// <summary>Сборщик точек контакта для дебага. Заведён ВСЕГДА, но по умолчанию выключен: включить
	/// его на живой сцене - это одна запись в поле, а завести на живой сцене нельзя вовсе (колбэки
	/// копируются в симуляцию при её создании).</summary>
	public PhysicsContactRecorder Contacts { get; } = new();

	/// <summary>Свойства тел, читаемые узкой фазой (см. <see cref="PhysicsBodyProperties"/>). Заведены
	/// ВСЕГДА по той же причине, что и сборщик контактов: колбэки копируются в симуляцию при её
	/// создании, и подсунуть их позже нельзя.
	///
	/// Штатный <see cref="CollidableProperty{T}"/>, а не свои массивы по значению хендла: он ведётся
	/// самой симуляцией и потому переживает удаление и переиспользование хендлов - ровно то, на чём
	/// самописная таблица требовала ручной чистки в каждом Remove.</summary>
	public CollidableProperty<PhysicsBodyProperties> Bodies { get; }

	/// <summary>Гравитация, с которой мир создан. Нужна снаружи расчётам «с какой скоростью
	/// подпрыгнуть, чтобы подняться на h» (step-up персонажа): захардкоженные 9.81 молча врали бы
	/// в сцене с авторской гравитацией.</summary>
	public Vector3 Gravity { get; }

	public PhysicsWorld(Vector3 gravity, PhysicsMaterial? material = null)
	{
		Gravity = gravity;
		_pool = new BufferPool();
		Bodies = new CollidableProperty<PhysicsBodyProperties>(_pool);

		Simulation = Simulation.Create(_pool,
			new PhysicsNarrowPhaseCallbacks
			{
				Material = material ?? PhysicsMaterial.Default,
				Recorder = Contacts,
				Bodies = Bodies,
			},
			new PhysicsPoseCallbacks(gravity),
			// 8 итераций решателя и 1 субшаг - отправная точка Bepu для «обычной» сцены. Субшаги
			// понадобятся суставам рэгдолла; поднимать их заранее значит платить за каждый кадр.
			new SolveDescription(8, 1));
	}

	/// <summary>
	/// Двигает симуляцию на прошедшее время кадра ФИКСИРОВАННЫМИ шагами. Возвращает число
	/// проинтегрированных шагов - ноль означает, что кадр короче шага и позы не менялись (потребитель
	/// может пропустить перезаливку трансформов).
	/// </summary>
	public int Update(float deltaSeconds)
	{
		// Отрицательное и нечеловечески большое дельта приходят реально: пауза в отладчике, свёрнутое
		// окно, перевод часов. Пропустить их дешевле, чем разбираться потом, почему сцена взорвалась.
		if (!float.IsFinite(deltaSeconds) || deltaSeconds <= 0f)
		{
			return 0;
		}

		_accumulator += Math.Min(deltaSeconds, MaxStepsPerUpdate * FixedTimeStep);

		bool recording = Contacts.Enabled;

		int steps = 0;
		while (_accumulator >= FixedTimeStep && steps < MaxStepsPerUpdate)
		{
			// Контакты - снимок ПОСЛЕДНЕГО шага, а не сумма по кадру: за восемь шагов один и тот же
			// контакт пола попал бы в список восемь раз, и картинка показывала бы не текущее
			// состояние, а его историю - причём с восьмикратным весом у долгих кадров.
			if (recording)
			{
				Contacts.Clear();
			}

			Simulation.Timestep(FixedTimeStep);
			_accumulator -= FixedTimeStep;
			steps++;
		}

		if (recording && steps > 0)
		{
			Contacts.Flush();
		}

		return steps;
	}

	// --- Тела ------------------------------------------------------------------------------------

	/// <summary>Динамическое тело: масса конечна, симуляция им управляет полностью.</summary>
	public BodyHandle AddDynamic(in RigidPose pose, TypedIndex shape, float mass, float speculativeMargin = 0.1f)
	{
		var inertia = ComputeInertia(shape, mass);
		var description = BodyDescription.CreateDynamic(pose, inertia,
			new CollidableDescription(shape, speculativeMargin), new BodyActivityDescription(0.01f));

		return Register(Simulation.Bodies.Add(description));
	}

	/// <summary>
	/// Заводит телу запись свойств по умолчанию: вне групп, с трением.
	///
	/// Обязательно на КАЖДОЕ тело и именно при создании. Хендлы Bepu переиспользуются, и тело,
	/// заведённое на месте снятого, унаследовало бы его фильтр - «ящик не сталкивается с полом»
	/// начиная со второй сцены. Перезапись при создании закрывает это без ручной чистки в Remove.
	/// </summary>
	private BodyHandle Register(BodyHandle handle)
	{
		// GroupId = 0 у всех «обычных» тел. Одинаковая группа тут безопасна: маски подгрупп полные,
		// и AllowCollision разрешает столкновение по второму условию (пересечение масок непусто).
		Bodies.Allocate(handle) = new PhysicsBodyProperties
		{
			Filter = new SubgroupCollisionFilter(0),
			VelocityDriven = false,
		};

		return handle;
	}

	/// <summary>
	/// Кинематическое тело: бесконечная масса, движется только тем, что ему задают снаружи. Именно
	/// им представляются кости персонажа, пока он в АНИМАЦИИ, а не в рэгдолле: они толкают
	/// окружение, но сами анимацию не сбивают.
	/// </summary>
	public BodyHandle AddKinematic(in RigidPose pose, TypedIndex shape, float speculativeMargin = 0.1f)
	{
		var description = BodyDescription.CreateKinematic(pose,
			new CollidableDescription(shape, speculativeMargin), new BodyActivityDescription(0.01f));

		return Register(Simulation.Bodies.Add(description));
	}

	public StaticHandle AddStatic(in RigidPose pose, TypedIndex shape) =>
		Simulation.Statics.Add(new StaticDescription(pose, shape));

	/// <summary>Тело, горизонтальную скорость которого задаёт код: его контакты становятся
	/// бестрениевыми (см. <see cref="PhysicsBodyProperties.VelocityDriven"/>). Ставить ПОСЛЕ
	/// заведения тела.</summary>
	public void SetVelocityDriven(BodyHandle handle, bool value) =>
		Bodies[handle].VelocityDriven = value;

	private int _nextCollisionGroup;

	/// <summary>Свежий номер группы связанных тел (см. <see cref="SubgroupCollisionFilter"/>). Одна
	/// группа - один рэгдолл. Ноль занят «обычными» телами и потому не выдаётся.</summary>
	public int NewCollisionGroup() => ++_nextCollisionGroup;

	/// <summary>Заводит тело в группу как подгруппу <paramref name="subgroupId"/>.</summary>
	public void SetCollisionGroup(BodyHandle handle, int group, int subgroupId) =>
		Bodies[handle].Filter = new SubgroupCollisionFilter(group, subgroupId);

	/// <summary>Запрещает столкновение ПАРЫ тел (для рэгдолла - смежных по суставу костей).</summary>
	public void DisableCollision(BodyHandle a, BodyHandle b) =>
		SubgroupCollisionFilter.DisableCollision(ref Bodies[a].Filter, ref Bodies[b].Filter);

	public void Remove(BodyHandle handle) => Simulation.Bodies.Remove(handle);

	public void Remove(StaticHandle handle) => Simulation.Statics.Remove(handle);

	/// <summary>
	/// Убирает форму из реестра ВМЕСТЕ с её буферами. Именно RemoveAndDispose, а не Remove: у меша
	/// (и других составных форм) за <see cref="TypedIndex"/> стоит собственный BVH и массив
	/// треугольников в пуле, и простое снятие индекса оставило бы их висеть - а статику сцены
	/// приходится пересобирать на каждое движение объекта, то есть утечка была бы не разовой, а
	/// пропорциональной времени работы редактора.
	/// </summary>
	public void RemoveShape(TypedIndex shape) => Simulation.Shapes.RemoveAndDispose(shape, _pool);

	/// <summary>Тензор инерции формы. Отдельным методом, потому что у каждой формы Bepu свой
	/// ComputeInertia и общего интерфейса под него нет - развилка неизбежна и лучше пусть она будет
	/// в одном месте.</summary>
	private BodyInertia ComputeInertia(TypedIndex shape, float mass)
	{
		switch (shape.Type)
		{
			case Sphere.Id:
				return Simulation.Shapes.GetShape<Sphere>(shape.Index).ComputeInertia(mass);
			case Capsule.Id:
				return Simulation.Shapes.GetShape<Capsule>(shape.Index).ComputeInertia(mass);
			case Box.Id:
				return Simulation.Shapes.GetShape<Box>(shape.Index).ComputeInertia(mass);
			case Cylinder.Id:
				return Simulation.Shapes.GetShape<Cylinder>(shape.Index).ComputeInertia(mass);
			case ConvexHull.Id:
				return Simulation.Shapes.GetShape<ConvexHull>(shape.Index).ComputeInertia(mass);
			default:
				// Меш и составные формы динамическими телами здесь не бывают: у произвольного меша
				// нет корректного тензора инерции без дополнительных допущений, и молча подставить
				// шар значило бы получить тело, вращающееся не так, как выглядит.
				throw new NotSupportedException(
					$"Shape type {shape.Type} cannot be used for a dynamic body - inertia is undefined.");
		}
	}

	// --- Формы -----------------------------------------------------------------------------------

	public TypedIndex AddSphere(float radius) => Simulation.Shapes.Add(new Sphere(radius));

	public TypedIndex AddBox(Vector3 size) => Simulation.Shapes.Add(new Box(size.X, size.Y, size.Z));

	/// <summary>Капсула - основная форма конечности рэгдолла и тела персонажа: у неё нет углов, за
	/// которые цепляется решатель, и она дёшева в узкой фазе.</summary>
	public TypedIndex AddCapsule(float radius, float length) => Simulation.Shapes.Add(new Capsule(radius, length));

	/// <summary>
	/// Статический меш из треугольников движка. Обход РАЗВОРАЧИВАЕТСЯ: лицевая сторона треугольника
	/// Bepu противоположна лицевой стороне того же треугольника в движке.
	///
	/// Меш в Bepu ОДНОСТОРОННИЙ, поэтому цена ошибки здесь не «нормали чуть не те», а полное
	/// отсутствие столкновений: тело проходит сквозь пол в свободном падении, молча и до конца сцены.
	///
	/// РАНЬШЕ ЗДЕСЬ БЫЛО «как есть», и это была ошибка, прожившая долго, потому что проверяли её
	/// РУКОПИСНЫМ квадратом. Квадрат в пробнике был выложен в уже развёрнутом порядке - он и
	/// компенсировал разворот, которого не делал этот метод. На настоящей импортированной геометрии
	/// всё падало сквозь пол. Замерено на демо-площадке (сфера: -17.3 против 0.25 при обратном
	/// обходе) и, что важнее, на ЧУЖОЙ модели - Sponza из Khronos-семплов, где «как есть» роняет
	/// сферу сквозь пол на изнанку плиты (-0.68 против 0.23). Вторая модель здесь обязательна:
	/// по своей собственной геометрии «неверная конвенция» и «наша опечатка в генераторе» неразличимы.
	///
	/// Проверка живёт в <c>ScenePhysicsProbe</c> (DECA_PROBE_SCENE=1): она роняет сферу на реальную
	/// сцену ОБОИМИ обходами и печатает оба числа - одно само по себе ничего не доказывает.
	/// </summary>
	public TypedIndex AddTriangleMesh(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<uint> indices, Vector3 scale)
	{
		int triangleCount = indices.Length / 3;
		_pool.Take<Triangle>(triangleCount, out var triangles);

		for (int i = 0; i < triangleCount; i++)
		{
			// Вторая и третья вершины меняются местами - это и есть разворот обхода.
			triangles[i] = new Triangle(
				vertices[(int)indices[i * 3 + 0]],
				vertices[(int)indices[i * 3 + 2]],
				vertices[(int)indices[i * 3 + 1]]);
		}

		// Slice ОБЯЗАТЕЛЕН: BufferPool.Take округляет длину вверх до степени двойки, а Mesh берёт
		// число треугольников из Length буфера. Без среза в меш уезжал бы хвост неинициализированной
		// памяти - мусорные треугольники с NaN-координатами ломают построение BVH, и меш перестаёт
		// сталкиваться ВООБЩЕ (тело просто пролетает сквозь него в свободном падении).
		return Simulation.Shapes.Add(new Mesh(triangles.Slice(0, triangleCount), scale, _pool));
	}

	// --- Райкасты --------------------------------------------------------------------------------

	private struct ClosestHitHandler : IRayHitHandler
	{
		public RayHit Result;

		public bool AllowTest(CollidableReference collidable) => true;

		public bool AllowTest(CollidableReference collidable, int childIndex) => true;

		public void OnRayHit(in BepuPhysics.Trees.RayData ray, ref float maximumT, float t, in Vector3 normal,
			CollidableReference collidable, int childIndex)
		{
			// Сужение maximumT - не оптимизация, а условие корректности: без него обработчик
			// получал бы хиты в произвольном порядке, и «ближайший» пришлось бы искать сравнением,
			// а Bepu не гарантирует, что дальние хиты вообще будут перечислены.
			maximumT = t;

			Result.Hit = true;
			Result.Distance = t;
			Result.Position = ray.Origin + ray.Direction * t;
			Result.Normal = normal;
			Result.IsStatic = collidable.Mobility == CollidableMobility.Static;
			Result.Collidable = collidable.RawHandleValue;
		}
	}

	/// <summary>
	/// Ближайшее пересечение луча со сценой. Направление НЕ нормализуется здесь: Bepu трактует
	/// maximumT в единицах длины направления, и нормализация молча меняла бы смысл дальности у
	/// вызывающего, который передал ненормализованный вектор осознанно (например, «до этой точки»).
	/// </summary>
	public RayHit RayCast(Vector3 origin, Vector3 direction, float maximumT)
	{
		var handler = new ClosestHitHandler();
		Simulation.RayCast(origin, direction, maximumT, ref handler);
		return handler.Result;
	}

	private struct ClosestStaticHitHandler : IRayHitHandler
	{
		public RayHit Result;

		public bool AllowTest(CollidableReference collidable) =>
			collidable.Mobility == CollidableMobility.Static;

		public bool AllowTest(CollidableReference collidable, int childIndex) =>
			collidable.Mobility == CollidableMobility.Static;

		public void OnRayHit(in BepuPhysics.Trees.RayData ray, ref float maximumT, float t, in Vector3 normal,
			CollidableReference collidable, int childIndex)
		{
			maximumT = t;

			Result.Hit = true;
			Result.Distance = t;
			Result.Position = ray.Origin + ray.Direction * t;
			Result.Normal = normal;
			Result.IsStatic = true;
			Result.Collidable = collidable.RawHandleValue;
		}
	}

	/// <summary>
	/// То же, но луч видит ТОЛЬКО СТАТИКУ. Для «пощупать пол» это не оптимизация, а корректность:
	/// динамические тела в сцене - это капсулы самих персонажей, и луч, пущенный сверху вниз над
	/// лежащим рэгдоллом, попадает в его же туловище. Замерено циклом подъёма: персонаж «вставал» на
	/// высоту собственной капсулы (0.288 вместо 0) - на себя самого.
	/// </summary>
	public RayHit RayCastStatic(Vector3 origin, Vector3 direction, float maximumT)
	{
		var handler = new ClosestStaticHitHandler();
		Simulation.RayCast(origin, direction, maximumT, ref handler);
		return handler.Result;
	}

	public void Dispose()
	{
		Simulation.Dispose();
		_pool.Clear();
	}
}
