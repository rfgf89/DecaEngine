using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Constraints;

namespace DecaEngine.Physics;

/// <summary>
/// Описание одной кости рэгдолла. Задаётся ВЫЗЫВАЮЩИМ, а не выводится из скелета автоматически:
/// какие кости рига физические, а какие - служебные (пальцы, вспомогательные узлы, кости
/// вторичного движения), знает только автор персонажа. Автоматика здесь дала бы рэгдолл из
/// двухсот тел, из которых нужны двадцать.
/// </summary>
public struct RagdollBoneDesc
{
	/// <summary>Джойнт скелета, к которому привязано тело; он же - проксимальный конец капсулы.</summary>
	public int Joint;

	/// <summary>Джойнт, задающий направление и длину капсулы (обычно ребёнок). -1 - использовать
	/// <see cref="Length"/> вдоль оси Y джойнта.</summary>
	public int ChildJoint;

	/// <summary>Индекс РОДИТЕЛЬСКОЙ КОСТИ РЭГДОЛЛА в описании, -1 у корня. Именно кости рэгдолла, а
	/// не джойнта скелета: между ними обычно есть пропущенные звенья.</summary>
	public int Parent;

	public float Radius;
	public float Length;
	public float Mass;

	/// <summary>Косинус максимального отклонения от исходного направления в суставе. 1 - сустав
	/// жёсткий, -1 - свободный. По умолчанию (0) ограничение не ставится вовсе.</summary>
	public float SwingLimitCos;

	/// <summary>
	/// Предел СКРУЧИВАНИЯ кости вокруг собственной длинной оси, радианы (± от позы сборки). Ноль -
	/// не ограничивать.
	///
	/// Отдельно от <see cref="SwingLimitCos"/>, потому что это другая степень свободы: конус
	/// ограничивает, куда кость может ОТКЛОНИТЬСЯ, и ничего не говорит о том, на сколько она может
	/// провернуться вокруг себя. Без этого предела лапа выворачивается на любой угол, оставаясь
	/// внутри конуса, - и персонаж выглядит сломанным при формально соблюдённых ограничениях.
	/// </summary>
	public float TwistLimitAngle;

	/// <summary>
	/// Ось ШАРНИРА в мире на момент сборки; ноль - сустав не шарнирный (обычный конус + твист).
	/// Колено и локоть - одноосные суставы: конус разрешает согнуть их и НАЗАД, и упавший персонаж
	/// заламывает ноги анатомически невозможно, формально не нарушая ни одного предела. Шарнир
	/// оставляет одну степень свободы, а <see cref="HingeMinAngle"/>/<see cref="HingeMaxAngle"/>
	/// ограничивают её диапазон (радианы от позы сборки; плюс - глубже в сгиб). Конус и твист у
	/// шарнирного сустава не ставятся: шарнир уже держит обе лишние степени.
	/// </summary>
	public Vector3 HingeAxisWorld;
	public float HingeMinAngle;
	public float HingeMaxAngle;
}

/// <summary>
/// Рэгдолл: набор капсул по костям с шарнирами в суставах, умеющий жить в двух режимах и
/// переключаться между ними.
///
/// - КИНЕМАТИЧЕСКИЙ: тела с бесконечной массой едут за позой анимации. Персонаж полностью
///   управляется аниматором, но при этом расталкивает окружение.
/// - ДИНАМИЧЕСКИЙ: тела падают сами, поза читается ИЗ НИХ.
///
/// Плюс active ragdoll - динамический режим с угловыми сервоприводами, тянущими каждый сустав к
/// позе анимации. Именно он даёт «живое» падение, когда персонаж ещё пытается держаться, а не
/// мешок костей.
///
/// Тела ВСЕГДА динамические, даже в режиме анимации. Это не небрежность, а требование Bepu: связь
/// между двумя КИНЕМАТИЧЕСКИМИ телами недопустима, а рэгдолл - это сплошь связанные тела, и в
/// момент, когда весь набор становился кинематическим, решатель портил кучу (STATUS_HEAP_CORRUPTION
/// на первом же шаге). Поэтому режим анимации реализован не бесконечной массой, а ЖЁСТКИМ
/// заданием скорости каждый кадр: гравитация и удары успевают внести вклад лишь за один шаг и тут
/// же перезаписываются, то есть тело ведёт себя как кинематическое, оставаясь динамическим.
/// </summary>
public sealed class Ragdoll
{
	private struct Bone
	{
		public BodyHandle Body;

		/// <summary>Капсула кости. Хранится, чтобы её было чем убрать: формы живут в реестре
		/// симуляции отдельно от тел, и снятие тела их не трогает (см. <see cref="Destroy"/>).</summary>
		public TypedIndex Shape;

		public int Joint;
		public int Parent;
		public BodyInertia DynamicInertia;

		/// <summary>Ориентация тела относительно джойнта в момент сборки: капсула Bepu лежит вдоль
		/// своей локальной оси Y, а кость смотрит куда угодно, и без этой поправки поза, прочитанная
		/// из физики, приходила бы повёрнутой.</summary>
		public Quaternion JointToBody;

		/// <summary>Смещение центра капсулы от джойнта, в пространстве ТЕЛА.</summary>
		public Vector3 BodyToJoint;

		public ConstraintHandle Socket;
		public ConstraintHandle Servo;
		public bool HasServo;
	}

	private readonly PhysicsWorld _world;
	private readonly Bone[] _bones;

	/// <summary>Идёт ли поза от анимации (true) или от физики (false). См. шапку о том, почему это
	/// НЕ кинематические тела.</summary>
	public bool IsAnimationDriven { get; private set; } = true;

	public int BoneCount => _bones.Length;

	/// <summary>Джойнт скелета, которому соответствует кость рэгдолла.</summary>
	public int JointOf(int bone) => _bones[bone].Joint;

	/// <summary>Тело кости - для диагностики (скорости, силы) и для внешних воздействий: толчок от
	/// взрыва прикладывается к конкретной кости, а не к рэгдоллу целиком.</summary>
	public BodyHandle BodyOf(int bone) => _bones[bone].Body;

	private Ragdoll(PhysicsWorld world, Bone[] bones)
	{
		_world = world;
		_bones = bones;
	}

	/// <summary>
	/// Собирает рэгдолл по текущей позе. <paramref name="jointWorld"/> - МИРОВЫЕ матрицы джойнтов
	/// (модельные, домноженные на трансформ сущности): физика живёт в мире, и собирать её в
	/// пространстве модели значило бы городить второй набор координат.
	///
	/// Стартует КИНЕМАТИЧЕСКИМ: персонаж в момент появления управляется анимацией, а не валится.
	/// </summary>
	public static Ragdoll Build(PhysicsWorld world, ReadOnlySpan<RagdollBoneDesc> description,
		Matrix4x4[] jointWorld)
	{
		var bones = new Bone[description.Length];

		for (int i = 0; i < description.Length; i++)
		{
			var desc = description[i];
			var jointMatrix = jointWorld[desc.Joint];
			var jointPosition = jointMatrix.Translation;

			// Направление и длина кости - по ребёнку, если он задан. Иначе кость считается
			// смотрящей вдоль своей локальной Y: так ориентированы концевые кости (голова, кисть),
			// у которых ребёнка в риге просто нет.
			Vector3 direction;
			float length;

			if (desc.ChildJoint >= 0)
			{
				var toChild = jointWorld[desc.ChildJoint].Translation - jointPosition;
				length = toChild.Length();
				direction = length > 1e-5f ? toChild / length : Vector3.UnitY;
			}
			else
			{
				length = desc.Length;
				direction = Vector3.Normalize(new Vector3(jointMatrix.M21, jointMatrix.M22, jointMatrix.M23));
			}

			// Капсула Bepu измеряется ЦИЛИНДРИЧЕСКОЙ частью: полная длина больше на два радиуса.
			// Без вычета концевые полусферы удлиняли бы каждую кость, и суставы не сходились бы.
			float cylinder = MathF.Max(length - 2f * desc.Radius, 0.01f);
			var shape = world.AddCapsule(desc.Radius, cylinder);

			// Капсула лежит вдоль локальной Y - разворачиваем её вдоль кости.
			var orientation = FromToRotation(Vector3.UnitY, direction);
			var center = jointPosition + direction * (length * 0.5f);

			var body = world.AddDynamic(new RigidPose(center, orientation), shape,
				desc.Mass <= 0f ? 1f : desc.Mass);

			bones[i] = new Bone
			{
				Body = body,
				Shape = shape,
				Joint = desc.Joint,
				Parent = desc.Parent,
				DynamicInertia = world.Simulation.Bodies[body].LocalInertia,
				JointToBody = Quaternion.Conjugate(RotationOf(jointMatrix)) * orientation,
				BodyToJoint = Vector3.Transform(jointPosition - center, Quaternion.Conjugate(orientation)),
			};
		}

		// Фильтр подгрупп (см. SubgroupCollisionFilter - приём из демок Bepu): весь рэгдолл - одна
		// группа, каждая кость - своя подгруппа.
		//
		// Не сталкиваются только СМЕЖНЫЕ по суставу кости - родитель с ребёнком. Их капсулы
		// пересекаются по построению (сустав у них общий), и решатель, расталкивая их контактами,
		// воюет с собственным шарниром: персонаж дрожит и уползает по полу вместо того, чтобы лежать.
		//
		// Все остальные пары сталкиваются, и это НЕ мелочь: голова о туловище, лапа о лапу, хвост о
		// бок - именно они не дают персонажу сложиться сам сквозь себя. Тряпичная кукла держит форму
		// не одними суставами: у неё есть объём, и он тоже часть ограничений. Запрет столкновений
		// внутри рэгдолла ЦЕЛИКОМ убирал войну с шарнирами вместе со всей этой защитой, и персонаж
		// сворачивался в физически невозможный узел.
		//
		// Подгрупп ровно 64 - маска битовая. Рэгдолл длиннее (полный риг с пальцами) в неё не
		// поместится: кости с 64-й получают пустую маску членства, то есть сталкиваются со всеми
		// своими соседями. Дрожащий сустав честнее молча перепутанных битов у костей 64 и 0.
		int group = world.NewCollisionGroup();

		for (int i = 0; i < bones.Length; i++)
		{
			world.SetCollisionGroup(bones[i].Body, group, i);
		}

		for (int i = 0; i < bones.Length; i++)
		{
			int parent = bones[i].Parent;
			if (parent >= 0)
			{
				world.DisableCollision(bones[i].Body, bones[parent].Body);
			}
		}

		var ragdoll = new Ragdoll(world, bones);
		ragdoll.CreateConstraints(description);
		return ragdoll;
	}

	/// <summary>Шарниры и ограничения. Точка крепления - ПРОКСИМАЛЬНЫЙ конец кости (сам джойнт), а
	/// не центр капсулы: сустав анатомически находится там, и крепление за центр давало бы висящие
	/// на полдлины кости конечности.</summary>
	private void CreateConstraints(ReadOnlySpan<RagdollBoneDesc> description)
	{
		for (int i = 0; i < _bones.Length; i++)
		{
			int parent = _bones[i].Parent;
			if (parent < 0)
			{
				continue;
			}

			var childPose = _world.Simulation.Bodies[_bones[i].Body].Pose;
			var parentPose = _world.Simulation.Bodies[_bones[parent].Body].Pose;

			var anchorWorld = childPose.Position +
				Vector3.Transform(_bones[i].BodyToJoint, childPose.Orientation);

			var socket = new BallSocket
			{
				LocalOffsetA = Vector3.Transform(anchorWorld - parentPose.Position,
					Quaternion.Conjugate(parentPose.Orientation)),
				LocalOffsetB = _bones[i].BodyToJoint,
				SpringSettings = new SpringSettings(30f, 1f),
			};

			_bones[i].Socket = _world.Simulation.Solver.Add(_bones[parent].Body, _bones[i].Body, socket);

			// Угловой сервопривод заводится СРАЗУ, даже если active ragdoll выключен: добавление
			// связи в решатель на лету стоит перестройки батчей, и делать это в момент, когда
			// персонажа сбивают с ног, - худший из возможных выборов. Пока он не нужен, ему просто
			// ставится нулевая максимальная сила.
			var servo = new AngularServo
			{
				TargetRelativeRotationLocalA = Quaternion.Conjugate(parentPose.Orientation) * childPose.Orientation,
				ServoSettings = new ServoSettings(float.MaxValue, 0f, 0f),
				SpringSettings = new SpringSettings(20f, 1f),
			};

			_bones[i].Servo = _world.Simulation.Solver.Add(_bones[parent].Body, _bones[i].Body, servo);
			_bones[i].HasServo = true;

			if (description[i].HingeAxisWorld.LengthSquared() > 1e-8f)
			{
				// Шарнир: одна ось вращения плюс диапазон угла вокруг неё. Заменяет и конус, и
				// твист - лишние степени свободы уже удержаны выравниванием осей.
				var axis = Vector3.Normalize(description[i].HingeAxisWorld);

				var hinge = new AngularHinge
				{
					LocalHingeAxisA = Vector3.Transform(axis, Quaternion.Conjugate(parentPose.Orientation)),
					LocalHingeAxisB = Vector3.Transform(axis, Quaternion.Conjugate(childPose.Orientation)),
					SpringSettings = new SpringSettings(30f, 1f),
				};

				_world.Simulation.Solver.Add(_bones[parent].Body, _bones[i].Body, hinge);

				AddTwistRange(parent, i, parentPose, childPose, axis,
					description[i].HingeMinAngle, description[i].HingeMaxAngle);

				continue;
			}

			if (description[i].SwingLimitCos != 0f)
			{
				var swing = new SwingLimit
				{
					AxisLocalA = Vector3.Transform(Vector3.Transform(Vector3.UnitY, childPose.Orientation),
						Quaternion.Conjugate(parentPose.Orientation)),
					AxisLocalB = Vector3.UnitY,
					MinimumDot = description[i].SwingLimitCos,
					SpringSettings = new SpringSettings(30f, 1f),
				};

				_world.Simulation.Solver.Add(_bones[parent].Body, _bones[i].Body, swing);
			}

			if (description[i].TwistLimitAngle > 0f)
			{
				AddTwistLimit(parent, i, parentPose, childPose, description[i].TwistLimitAngle);
			}
		}
	}

	/// <summary>
	/// Ось и диапазон шарнира из ПОЗЫ СБОРКИ - для коленей и локтей (см.
	/// <see cref="RagdollBoneDesc.HingeAxisWorld"/>). Ось - нормаль плоскости текущего изгиба;
	/// диапазон анатомический и самокалибрующийся: разгибание до почти прямой (пять градусов
	/// запаса - не через прямую в обратный сгиб), сгиб до ~140 градусов суммарного угла. Почти
	/// прямая конечность оси не даёт (шум вместо направления - ровно та ловушка, что была у полюса
	/// foot IK) - сустав остаётся конусным.
	/// </summary>
	public static void MarkHinge(ref RagdollBoneDesc bone, Vector3 upperWorld, Vector3 midWorld,
		Vector3 footWorld)
	{
		var a = midWorld - upperWorld;
		var b = footWorld - midWorld;

		float aLength = a.Length();
		float bLength = b.Length();

		if (aLength < 1e-5f || bLength < 1e-5f)
		{
			return;
		}

		var axis = Vector3.Cross(a, b);
		float bend = MathF.Atan2(axis.Length() / (aLength * bLength),
			Vector3.Dot(a, b) / (aLength * bLength));

		const float straightMargin = 5f * MathF.PI / 180f;
		const float maxFlex = 140f * MathF.PI / 180f;

		if (bend < straightMargin)
		{
			return;
		}

		bone.HingeAxisWorld = axis / axis.Length();
		bone.HingeMinAngle = -MathF.Max(bend - straightMargin, 0f);
		bone.HingeMaxAngle = MathF.Max(maxFlex - bend, 0f);
	}

	/// <summary>
	/// Предел скручивания вокруг длинной оси кости (см. <see cref="RagdollBoneDesc.TwistLimitAngle"/>).
	///
	/// У <see cref="TwistLimit"/> ось скручивания - это **Z** базиса, а «нулевым углом» считается его
	/// X (так написано в документации Bepu, и перепутать здесь легко: у капсулы длинная ось - Y).
	/// Поэтому строится отдельный базис: Z вдоль кости, X - любой перпендикуляр, лишь бы ОДИН И ТОТ
	/// ЖЕ для обоих тел.
	///
	/// Базис снимается в позе СБОРКИ и выражается в локальных пространствах каждого тела - тогда
	/// текущее скручивание в этой позе равно нулю, а предел ±angle отсчитывается от неё. Взять базис
	/// «из мира» и не переводить в локальные - значит привязать сустав к ориентации персонажа в
	/// момент сборки: повернувшись, он получил бы перекрученный сустав на ровном месте.
	/// </summary>
	private void AddTwistLimit(int parent, int child, in RigidPose parentPose, in RigidPose childPose,
		float angle)
	{
		// Длинная ось капсулы ребёнка в мире - вокруг неё и меряется скручивание.
		var twist = Vector3.Transform(Vector3.UnitY, childPose.Orientation);
		float length = twist.Length();

		if (length < 1e-5f)
		{
			return;
		}

		AddTwistRange(parent, child, parentPose, childPose, twist / length, -angle, angle);
	}

	/// <summary>Предел вращения вокруг ПРОИЗВОЛЬНОЙ оси с несимметричным диапазоном - им же
	/// ограничивается угол шарнира (там ось - ось сгиба, а не длинная ось кости, и диапазон
	/// смещён: разгибать почти нельзя, сгибать - можно).</summary>
	private void AddTwistRange(int parent, int child, in RigidPose parentPose, in RigidPose childPose,
		Vector3 axisWorld, float minimumAngle, float maximumAngle)
	{
		// Перпендикуляр берётся от той оси мира, которая с осью наименее сонаправлена: взять
		// фиксированную (скажем, всегда UnitX) значит получить вырожденное произведение у оси,
		// смотрящей вдоль неё, - а такие в скелете есть всегда.
		var helper = MathF.Abs(axisWorld.X) < 0.7f ? Vector3.UnitX : Vector3.UnitZ;
		var x = Vector3.Normalize(Vector3.Cross(helper, axisWorld));
		var y = Vector3.Cross(axisWorld, x);

		// Строки матрицы - базисные векторы (та же конвенция, что у MathUtils.CreateTrs).
		var basis = Quaternion.CreateFromRotationMatrix(new Matrix4x4(
			x.X, x.Y, x.Z, 0f,
			y.X, y.Y, y.Z, 0f,
			axisWorld.X, axisWorld.Y, axisWorld.Z, 0f,
			0f, 0f, 0f, 1f));

		var limit = new TwistLimit
		{
			LocalBasisA = Quaternion.Concatenate(basis, Quaternion.Conjugate(parentPose.Orientation)),
			LocalBasisB = Quaternion.Concatenate(basis, Quaternion.Conjugate(childPose.Orientation)),
			MinimumAngle = minimumAngle,
			MaximumAngle = maximumAngle,
			SpringSettings = new SpringSettings(30f, 1f),
		};

		_world.Simulation.Solver.Add(_bones[parent].Body, _bones[child].Body, limit);
	}

	/// <summary>
	/// Убирает рэгдолл из мира целиком: тела, их связи и формы. Нужен потому, что рэгдолл в
	/// редакторе - НЕ вечная сущность: галочка Enabled его создаёт и уничтожает, персонажа удаляют
	/// из сцены, префаб перезагружают. Без этого каждое такое событие оставляло бы в симуляции
	/// два десятка невидимых капсул, которые продолжают падать и расталкивать живых.
	///
	/// Связи снимать отдельно не нужно: удаление тела в Bepu снимает и все связи, в которых оно
	/// участвует. А вот формы - нужно: они лежат в реестре симуляции сами по себе, и тело их не
	/// «владеет».
	/// </summary>
	public void Destroy()
	{
		for (int i = 0; i < _bones.Length; i++)
		{
			_world.Remove(_bones[i].Body);
			_world.RemoveShape(_bones[i].Shape);
		}
	}

	/// <summary>Мировая поза тела кости - для дебага (каркас капсулы рисуется по НЕЙ, а не по позе
	/// джойнта: расхождение этих двух и есть то, что ищут, когда физика персонажа живёт отдельно от
	/// картинки).</summary>
	public RigidPose PoseOf(int bone) => _world.Simulation.Bodies[_bones[bone].Body].Pose;

	/// <summary>Форма кости - чтобы дебаг мог нарисовать её РЕАЛЬНЫЕ размеры, а не те, что были
	/// заказаны в описании.</summary>
	public TypedIndex ShapeOf(int bone) => _bones[bone].Shape;

	/// <summary>Родительская КОСТЬ РЭГДОЛЛА, -1 у корня: по ней дебаг рисует связи суставов.</summary>
	public int ParentOf(int bone) => _bones[bone].Parent;

	/// <summary>
	/// Переключает источник позы. Переход в физику НЕ обнуляет скорости: тела к этому моменту уже
	/// движутся со скоростью, которую им задавала анимация, и персонаж падает ПРОДОЛЖАЯ движение, а
	/// не с места - именно это отличает подсечку на бегу от выключения питания.
	/// </summary>
	public void SetAnimationDriven(bool animationDriven)
	{
		if (animationDriven == IsAnimationDriven)
		{
			return;
		}

		IsAnimationDriven = animationDriven;

		for (int i = 0; i < _bones.Length; i++)
		{
			// Через Activator, а не присваиванием Awake у BodyReference: индексатор Bodies возвращает
			// значение, и запись в его поле компилятор не пропускает (CS1612).
			_world.Simulation.Awakener.AwakenBody(_bones[i].Body);
		}
	}

	/// <summary>
	/// Гонит тела к позе анимации. В кинематическом режиме - ЖЁСТКО, скоростью, вычисленной из
	/// разницы поз: телепортировать тело нельзя, иначе оно проходит сквозь препятствия, не заметив
	/// их, и перестаёт что-либо расталкивать. В динамическом - через угловые сервоприводы, то есть
	/// это и есть active ragdoll.
	/// </summary>
	public void DriveToPose(Matrix4x4[] jointWorld, float deltaSeconds, float servoStrength = 0f)
	{
		if (deltaSeconds <= 0f)
		{
			// Нулевой шаг - это режим редактирования: время не идёт, и «гнать скоростью» нечем.
			// Тела СТАВЯТСЯ в позу напрямую. Без этого рэгдолл остаётся там, где его собрали, и
			// персонаж, которого автор двигает гизмо, перестаёт зависеть от собственного трансформа:
			// сущность едет, а поза, прочитанная из тел, стоит на прежнем месте.
			TeleportToPose(jointWorld);
			return;
		}

		// Делитель - НЕ МЕНЬШЕ шага симуляции. Скорость, посчитанную на кадр, интегрируют
		// фиксированные шаги 1/120, и при FPS выше 120 кадр КОРОЧЕ шага: в кадр, куда попадает шаг,
		// тело проезжает в (шаг/кадр) раз больше своей дельты, следующий кадр «исправляет» перелёт
		// новым перелётом - экспоненциальная раскачка до бесконечности за доли секунды. Дальше
		// NaN-габарит и смерть широкой фазы Bepu переполнением стека при построении дерева - краш
		// выглядит как «Bepu сломался», а не как «делитель не тот». Headless-пробник этого не видит
		// ПО ПОСТРОЕНИЮ: он шагает ровным кадром 1/60, длиннее шага симуляции.
		float driveSeconds = MathF.Max(deltaSeconds, PhysicsWorld.FixedTimeStep);

		for (int i = 0; i < _bones.Length; i++)
		{
			var target = TargetPose(i, jointWorld);
			var body = _world.Simulation.Bodies[_bones[i].Body];

			if (IsAnimationDriven)
			{
				var current = body.Pose;

				body.Velocity.Linear = (target.Position - current.Position) / driveSeconds;
				body.Velocity.Angular = AngularVelocity(current.Orientation, target.Orientation, driveSeconds);
				body.Awake = true;
			}
			else if (servoStrength > 0f && _bones[i].HasServo && _bones[i].Parent >= 0)
			{
				var parentTarget = TargetPose(_bones[i].Parent, jointWorld);

				var servo = new AngularServo
				{
					TargetRelativeRotationLocalA =
						Quaternion.Conjugate(parentTarget.Orientation) * target.Orientation,
					ServoSettings = new ServoSettings(float.MaxValue, 0f, servoStrength),
					SpringSettings = new SpringSettings(20f, 1f),
				};

				_world.Simulation.Solver.ApplyDescription(_bones[i].Servo, servo);
			}
		}
	}

	/// <summary>
	/// Толчок телам: приращение СКОРОСТИ (не импульс в ньютонах - от массы костей эффект зависеть
	/// не должен, толчок в 2 м/с обязан качнуть и мышь, и лошадь одинаково), взвешенное по
	/// джойнтам. Вес на джойнт приходит снаружи: кто и как распределяет удар по телу (маска
	/// хит-реакции, точка попадания), рэгдоллу знать незачем.
	/// </summary>
	public void AddVelocity(Vector3 deltaVelocity, float[] jointWeights)
	{
		for (int i = 0; i < _bones.Length; i++)
		{
			int joint = _bones[i].Joint;
			float weight = joint >= 0 && joint < jointWeights.Length ? jointWeights[joint] : 0f;

			if (weight <= 0f)
			{
				continue;
			}

			var body = _world.Simulation.Bodies[_bones[i].Body];
			body.Velocity.Linear += deltaVelocity * weight;
			body.Awake = true;
		}
	}

	/// <summary>
	/// СТАВИТ тела в позу напрямую, гася скорости.
	///
	/// В симуляции так делать нельзя - телепортированное тело проходит сквозь препятствия, не заметив
	/// их, и ничего не расталкивает; для идущего времени есть <see cref="DriveToPose"/>. Но там, где
	/// времени нет - режим редактирования, первый кадр после сборки, перенос персонажа - это
	/// единственный способ вообще куда-то его поставить.
	/// </summary>
	public void TeleportToPose(Matrix4x4[] jointWorld)
	{
		for (int i = 0; i < _bones.Length; i++)
		{
			var target = TargetPose(i, jointWorld);
			var body = _world.Simulation.Bodies[_bones[i].Body];

			body.Pose = target;

			// Скорости гасятся обязательно: тело, поставленное на новое место со старой скоростью,
			// на первом же шаге игры улетит с неё, и выглядеть это будет как «персонаж прыгнул при
			// нажатии Play».
			body.Velocity.Linear = Vector3.Zero;
			body.Velocity.Angular = Vector3.Zero;
			body.Awake = true;
		}
	}

	/// <summary>Читает позу из тел в МИРОВЫЕ матрицы джойнтов. Обратная операция к
	/// <see cref="DriveToPose"/>: ею персонаж рисуется, пока лежит рэгдоллом.</summary>
	public void ReadPose(Matrix4x4[] jointWorld)
	{
		for (int i = 0; i < _bones.Length; i++)
		{
			var pose = _world.Simulation.Bodies[_bones[i].Body].Pose;

			var jointRotation = pose.Orientation * Quaternion.Conjugate(_bones[i].JointToBody);
			var jointPosition = pose.Position + Vector3.Transform(_bones[i].BodyToJoint, pose.Orientation);

			jointWorld[_bones[i].Joint] =
				Matrix4x4.CreateFromQuaternion(jointRotation) * Matrix4x4.CreateTranslation(jointPosition);
		}
	}

	private RigidPose TargetPose(int bone, Matrix4x4[] jointWorld)
	{
		var jointMatrix = jointWorld[_bones[bone].Joint];
		var orientation = RotationOf(jointMatrix) * _bones[bone].JointToBody;
		var position = jointMatrix.Translation - Vector3.Transform(_bones[bone].BodyToJoint, orientation);

		return new RigidPose(position, orientation);
	}

	/// <summary>Угловая скорость, переводящая одну ориентацию в другую за шаг. Кратчайшая дуга:
	/// без проверки знака тело раз в несколько кадров решало бы доехать «длинным путём» и
	/// прокручивалось вокруг себя.</summary>
	private static Vector3 AngularVelocity(Quaternion from, Quaternion to, float deltaSeconds)
	{
		var delta = to * Quaternion.Conjugate(from);
		if (delta.W < 0f)
		{
			delta = new Quaternion(-delta.X, -delta.Y, -delta.Z, -delta.W);
		}

		var axis = new Vector3(delta.X, delta.Y, delta.Z);
		float sin = axis.Length();

		if (sin < 1e-6f)
		{
			return Vector3.Zero;
		}

		float angle = 2f * MathF.Atan2(sin, delta.W);
		return axis * (angle / (sin * deltaSeconds));
	}

	private static Quaternion RotationOf(in Matrix4x4 matrix)
	{
		var x = Vector3.Normalize(new Vector3(matrix.M11, matrix.M12, matrix.M13));
		var y = Vector3.Normalize(new Vector3(matrix.M21, matrix.M22, matrix.M23));
		var z = Vector3.Normalize(new Vector3(matrix.M31, matrix.M32, matrix.M33));

		return Quaternion.CreateFromRotationMatrix(new Matrix4x4(
			x.X, x.Y, x.Z, 0f,
			y.X, y.Y, y.Z, 0f,
			z.X, z.Y, z.Z, 0f,
			0f, 0f, 0f, 1f));
	}

	private static Quaternion FromToRotation(Vector3 from, Vector3 to)
	{
		float dot = Vector3.Dot(from, to);
		if (dot > 0.999999f)
		{
			return Quaternion.Identity;
		}

		if (dot < -0.999999f)
		{
			var axis = Vector3.Cross(Vector3.UnitX, from);
			if (axis.LengthSquared() < 1e-8f)
			{
				axis = Vector3.Cross(Vector3.UnitZ, from);
			}

			return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
		}

		return Quaternion.Normalize(new Quaternion(Vector3.Cross(from, to), 1f + dot));
	}
}
