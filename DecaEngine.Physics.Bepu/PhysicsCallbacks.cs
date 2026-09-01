using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;

namespace DecaEngine.Physics;

/// <summary>
/// Материал контакта: трение и параметры пружины, с которыми решатель гасит проникновение. Лежит
/// отдельной структурой, потому что рэгдоллу и персонажу нужны РАЗНЫЕ настройки: конечности должны
/// скользить по полу, а капсула персонажа - нет.
/// </summary>
public struct PhysicsMaterial
{
	public float FrictionCoefficient;
	public float MaximumRecoveryVelocity;
	public SpringSettings SpringSettings;

	public static PhysicsMaterial Default => new()
	{
		FrictionCoefficient = 1f,
		MaximumRecoveryVelocity = 2f,
		// 30 Гц и коэффициент затухания 1 - апериодический отклик: контакт гасится без
		// перерегулирования. Меньшая частота даёт заметно «мягкий» пол, большая - дрожание на
		// шаге симуляции.
		SpringSettings = new SpringSettings(30f, 1f),
	};
}

/// <summary>
/// Фильтр столкновений по подгруппам - приём из демок самого Bepu (bepuphysics2, Apache-2.0,
/// <c>Demos/Demos/SubgroupCollisionFilter.cs</c>), перенесённый сюда как есть по смыслу.
///
/// Тела одной ГРУППЫ (один рэгдолл - одна группа) сталкиваются между собой по битовым маскам
/// подгрупп; тела разных групп - всегда. Это ровно та задача, которая здесь и стоит: не
/// сталкиваться должны только СМЕЖНЫЕ по суставу кости - их капсулы пересекаются по построению
/// (сустав общий), и решатель, расталкивая их, воюет с собственным шарниром. А несмежные - голова и
/// хвост, лапа и бок - сталкиваться ОБЯЗАНЫ: тряпичная кукла держит форму не только суставами, но и
/// собственным объёмом, иначе она сворачивается в невозможный узел.
///
/// Своя реализация того же (массивы по значению хендла) здесь была и работала, но у штатной есть то,
/// чего у неё не было: она лежит в <see cref="CollidableProperty{T}"/>, то есть в структуре, которую
/// ведёт сама симуляция, и переживает удаление и ПЕРЕИСПОЛЬЗОВАНИЕ хендлов без ручной чистки.
/// </summary>
public struct SubgroupCollisionFilter
{
	/// <summary>Группа связанных тел. У тел РАЗНЫХ групп столкновение разрешено всегда.</summary>
	public int GroupId;

	/// <summary>В каких подгруппах состоит это тело.</summary>
	public ulong SubgroupMembership;

	/// <summary>С какими подгруппами своей группы это тело сталкивается.</summary>
	public ulong CollidableSubgroups;

	/// <summary>Тело вне какой-либо связки: сталкивается со всем.</summary>
	public SubgroupCollisionFilter(int groupId)
	{
		GroupId = groupId;
		SubgroupMembership = ulong.MaxValue;
		CollidableSubgroups = ulong.MaxValue;
	}

	/// <summary>Тело - член подгруппы <paramref name="subgroupId"/> (для рэгдолла - номер кости).
	/// Подгрупп ровно 64: маска битовая, и кость номер 64 молча попала бы в кость номер 0.</summary>
	public SubgroupCollisionFilter(int groupId, int subgroupId)
	{
		GroupId = groupId;
		SubgroupMembership = subgroupId is >= 0 and < 64 ? 1UL << subgroupId : 0UL;
		CollidableSubgroups = ulong.MaxValue;
	}

	/// <summary>Запрещает столкновение пары. ВЗАИМНО: односторонняя запись была бы тихой ошибкой,
	/// зависящей от того, в каком порядке узкая фаза подала пару.</summary>
	public static void DisableCollision(ref SubgroupCollisionFilter a, ref SubgroupCollisionFilter b)
	{
		a.CollidableSubgroups &= ~b.SubgroupMembership;
		b.CollidableSubgroups &= ~a.SubgroupMembership;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool AllowCollision(in SubgroupCollisionFilter a, in SubgroupCollisionFilter b) =>
		a.GroupId != b.GroupId || (a.CollidableSubgroups & b.SubgroupMembership) > 0;
}

/// <summary>
/// Всё, что узкая фаза знает про КОНКРЕТНОЕ тело. Одна структура на тело, а не таблица на свойство:
/// горячий цикл читает их вместе, и второй индексируемый массив стоил бы второго промаха кеша.
/// </summary>
public struct PhysicsBodyProperties
{
	public SubgroupCollisionFilter Filter;

	/// <summary>
	/// Телом управляет КОД, а не решатель: контакты такого тела не имеют трения.
	///
	/// Персонаж, чью горизонтальную скорость каждый кадр задаёт скрипт, - именно такое тело. Трение
	/// для него не полезно, а вредно: оно гасит ровно ту скорость, которую только что задали, и
	/// делает это на каждом субшаге. Замерено на капсуле лисы: при μ=1 и шаге 1/120 с потеря
	/// составляет μ·g·dt = 0.082 м/с за субшаг, то есть 12.4% пути за оборот. Персонаж при этом
	/// выглядит идущим правильно - расходится лишь скорость, а её потом связывают с анимационным
	/// клипом, и ошибка приезжает скольжением ног.
	/// </summary>
	public bool VelocityDriven;
}

/// <summary>
/// Колбэки узкой фазы. Держат ОДИН материал на всю сцену: пер-телесные настройки контакта требуют
/// сайд-таблицы, читаемой из горячего цикла на каждом воркере. Всё, что таблицы всё-таки требует
/// (фильтр подгрупп и бестрениевые тела), лежит в <see cref="PhysicsBodyProperties"/> - одной
/// структурой на тело, в штатном <see cref="CollidableProperty{T}"/>.
/// </summary>
public struct PhysicsNarrowPhaseCallbacks : INarrowPhaseCallbacks
{
	public PhysicsMaterial Material;

	/// <summary>Свойства тел. Ссылка в структуре живёт нормально: Bepu копирует колбэки к себе один
	/// раз, вместе со ссылкой.</summary>
	public CollidableProperty<PhysicsBodyProperties>? Bodies;

	/// <summary>Сборщик точек контакта для дебага (см. <see cref="PhysicsContactRecorder"/>); null -
	/// контакты не собираются вовсе. Ссылка в структуре живёт нормально: Bepu копирует колбэки к
	/// себе один раз, вместе со ссылкой.</summary>
	public PhysicsContactRecorder? Recorder;

	/// <summary>Симуляция нужна ровно для одного - перевода смещения контакта в мировую точку:
	/// манифолд отдаёт его ОТНОСИТЕЛЬНО позиции коллайдера A, и без его позы контакт нарисовать
	/// негде. Приходит в <see cref="Initialize"/>, потому что раньше её просто не существует.</summary>
	private Simulation? _simulation;

	public void Initialize(Simulation simulation)
	{
		_simulation = simulation;
		Bodies?.Initialize(simulation);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b,
		ref float speculativeMargin)
	{
		// Два статика не сталкиваются никогда - пара из них означала бы работу впустую.
		if (a.Mobility == CollidableMobility.Static && b.Mobility == CollidableMobility.Static)
		{
			return false;
		}

		// Фильтр спрашивается только у пары ТЕЛ: у статики своих свойств нет, и обращение к таблице
		// по её хендлу читало бы чужую запись - хендлы тел и статиков нумеруются независимо.
		if (Bodies != null && a.Mobility != CollidableMobility.Static &&
			b.Mobility != CollidableMobility.Static)
		{
			return SubgroupCollisionFilter.AllowCollision(
				Bodies[a.BodyHandle].Filter, Bodies[b.BodyHandle].Filter);
		}

		return true;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB) => true;

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold,
		out PairMaterialProperties pairMaterial) where TManifold : unmanaged, IContactManifold<TManifold>
	{
		pairMaterial.FrictionCoefficient = Bodies != null && IsVelocityDriven(pair.A, pair.B)
			? 0f
			: Material.FrictionCoefficient;
		pairMaterial.MaximumRecoveryVelocity = Material.MaximumRecoveryVelocity;
		pairMaterial.SpringSettings = Material.SpringSettings;

		if (Recorder is { Enabled: true } recorder && _simulation != null)
		{
			RecordContacts(recorder, workerIndex, pair, ref manifold);
		}

		return true;
	}

	/// <summary>Есть ли в паре тело под управлением кода. Статики пропускаются: своих свойств у них
	/// нет (см. AllowContactGeneration).</summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private readonly bool IsVelocityDriven(CollidableReference a, CollidableReference b) =>
		(a.Mobility != CollidableMobility.Static && Bodies![a.BodyHandle].VelocityDriven) ||
		(b.Mobility != CollidableMobility.Static && Bodies![b.BodyHandle].VelocityDriven);

	/// <summary>Отдельным НЕ-инлайновым методом: горячий путь узкой фазы обязан оставаться коротким,
	/// а сбор контактов - выключенная по умолчанию ветка, которой незачем раздувать её тело.</summary>
	[MethodImpl(MethodImplOptions.NoInlining)]
	private readonly void RecordContacts<TManifold>(PhysicsContactRecorder recorder, int workerIndex,
		CollidablePair pair, ref TManifold manifold) where TManifold : unmanaged, IContactManifold<TManifold>
	{
		var origin = PositionOf(pair.A);
		bool againstStatic = pair.A.Mobility == CollidableMobility.Static ||
			pair.B.Mobility == CollidableMobility.Static;

		for (int i = 0; i < manifold.Count; i++)
		{
			manifold.GetContact(i, out var offset, out var normal, out float depth, out _);

			recorder.Record(workerIndex, new PhysicsContactRecorder.Contact
			{
				Position = origin + offset,
				Normal = normal,
				Depth = depth,
				AgainstStatic = againstStatic,
			});
		}
	}

	private readonly Vector3 PositionOf(CollidableReference collidable) =>
		collidable.Mobility == CollidableMobility.Static
			? _simulation!.Statics[collidable.StaticHandle].Pose.Position
			: _simulation!.Bodies[collidable.BodyHandle].Pose.Position;

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB,
		ref ConvexContactManifold manifold) => true;

	public void Dispose()
	{
	}
}

/// <summary>
/// Колбэки интегратора поз: гравитация и линейное затухание. Затухание задаётся ЗА СЕКУНДУ и
/// пересчитывается под шаг в <see cref="PrepareForIntegration"/> - иначе тела тормозили бы тем
/// сильнее, чем мельче шаг симуляции, и поведение зависело бы от частоты кадров.
/// </summary>
public struct PhysicsPoseCallbacks : IPoseIntegratorCallbacks
{
	public Vector3 Gravity;
	public float LinearDamping;
	public float AngularDamping;

	private Vector3Wide _gravityWideDt;
	private Vector<float> _linearDampingDt;
	private Vector<float> _angularDampingDt;

	public PhysicsPoseCallbacks(Vector3 gravity, float linearDamping = 0.03f, float angularDamping = 0.03f)
	{
		Gravity = gravity;
		LinearDamping = linearDamping;
		AngularDamping = angularDamping;
		_gravityWideDt = default;
		_linearDampingDt = default;
		_angularDampingDt = default;
	}

	public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;

	/// <summary>false: тела без ограничений интегрируются один раз на кадр, а не на каждый субшаг.
	/// Для гравитации и затухания разницы нет, а субшаги стоят денег.</summary>
	public readonly bool AllowSubstepsForUnconstrainedBodies => false;

	/// <summary>Кинематику интегрировать не нужно: её скорость задаёт код снаружи (анимация,
	/// контроллер персонажа), и гравитация к ней не применяется по определению.</summary>
	public readonly bool IntegrateVelocityForKinematics => false;

	public void Initialize(Simulation simulation)
	{
	}

	public void PrepareForIntegration(float dt)
	{
		// Затухание за секунду -> множитель за шаг. pow, а не линейное умножение: только так
		// результат за секунду не зависит от того, на сколько шагов её порезали.
		_linearDampingDt = new Vector<float>(MathF.Pow(MathHelper.Clamp(1f - LinearDamping, 0f, 1f), dt));
		_angularDampingDt = new Vector<float>(MathF.Pow(MathHelper.Clamp(1f - AngularDamping, 0f, 1f), dt));
		Vector3Wide.Broadcast(Gravity * dt, out _gravityWideDt);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void IntegrateVelocity(Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation,
		BodyInertiaWide localInertia, Vector<int> integrationMask, int workerIndex, Vector<float> dt,
		ref BodyVelocityWide velocity)
	{
		velocity.Linear = (velocity.Linear + _gravityWideDt) * _linearDampingDt;
		velocity.Angular *= _angularDampingDt;
	}
}
