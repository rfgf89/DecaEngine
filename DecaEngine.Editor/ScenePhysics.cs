using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using DecaEngine.Graphics;
using DecaEngine.Physics;

namespace DecaEngine.Editor;

/// <summary>
/// Мир физики сцены префаба: обёртка над <see cref="PhysicsWorld"/>, которая знает про РЕДАКТОР -
/// про то, что геометрия сцены меняется под руками, что кадр может быть на паузе, и что всё
/// происходящее нужно уметь показать.
///
/// Заводится ЛЕНИВО - только когда в сцене появился персонаж, которому физика нужна (foot IK или
/// рэгдолл, см. <see cref="AnimationDriver"/>). В сцене без таких персонажей физики нет вовсе, и
/// кадр не платит за неё ничего: построение статики - это BVH по всем треугольникам сцены, и делать
/// его «на всякий случай» нельзя.
///
/// Статика - ОДИН меш на всю сцену, а не тело на объект. Причина в том, для чего она здесь нужна:
/// луч foot IK спрашивает «что подо мной», и ему безразлично, к какому объекту относится
/// треугольник. Один меш - один BVH, одна пересборка, и стоимость движения объекта не зависит от
/// того, сколько объектов в сцене.
/// </summary>
public sealed class ScenePhysics : IDisposable
{
	/// <summary>Луч, пущенный за последний кадр, - для дебага. Именно ЛУЧ, а не только его попадание:
	/// самый частый диагноз при разъезжающемся foot IK - «луч летит не туда» или «луч короткий», и
	/// оба видны только по самому лучу.</summary>
	public struct RecordedRay
	{
		public Vector3 Origin;
		public Vector3 Direction;
		public float Length;
		public bool Hit;
		public Vector3 HitPosition;
		public Vector3 HitNormal;
	}

	/// <summary>Потолок на записанные лучи. Райкастов за кадр бывает много (две ноги на персонажа -
	/// это мало, а вот произвольный код может звать их сотнями), и накопитель дебага не должен уметь
	/// расти неограниченно.</summary>
	private const int MaxRecordedRays = 256;

	private readonly List<Vector3> _staticVertices = new();
	private readonly List<uint> _staticIndices = new();
	private readonly List<RecordedRay> _rays = new();
	private readonly Stopwatch _stepTimer = new();

	private StaticHandle _staticHandle;
	private TypedIndex _staticShape;
	private bool _hasStatic;

	private bool _building;

	public PhysicsWorld World { get; }

	/// <summary>Идёт ли симуляция. Пауза - НЕ нулевой шаг времени: нулевой шаг тоже проходит через
	/// накопитель и в какой-то момент выдаёт шаг, а пауза обязана останавливать мир целиком, чтобы
	/// разобрать позу тела, которое только что провалилось сквозь пол.</summary>
	public bool Paused { get; set; }

	/// <summary>Замедление/ускорение времени симуляции. Отдельно от паузы: рэгдолл, разлетающийся за
	/// три кадра, на 0.1 разбирается по кадрам, а на паузе - никак.</summary>
	public float TimeScale { get; set; } = 1f;

	/// <summary>Пишутся ли лучи в <see cref="Rays"/>. Выключено - список пуст и не растёт.</summary>
	public bool RecordRays { get; set; }

	public IReadOnlyList<RecordedRay> Rays => _rays;

	// --- Счётчики последнего кадра (для окна дебага) ---------------------------------------------

	public int LastStepCount { get; private set; }
	public double LastStepMilliseconds { get; private set; }
	public int StaticTriangleCount { get; private set; }
	public int RayCastsThisFrame { get; private set; }

	public int BodyCount => World.Simulation.Bodies.ActiveSet.Count + SleepingBodyCount;

	/// <summary>Спящих тел. Bepu держит их в отдельных наборах, и «сколько всего тел» без этого
	/// счётчика систематически занижается ровно на успокоившиеся - то есть на большинство.</summary>
	public int SleepingBodyCount
	{
		get
		{
			int count = 0;
			for (int i = 1; i < World.Simulation.Bodies.Sets.Length; i++)
			{
				ref var set = ref World.Simulation.Bodies.Sets[i];
				if (set.Allocated)
				{
					count += set.Count;
				}
			}

			return count;
		}
	}

	public ScenePhysics(Vector3 gravity)
	{
		World = new PhysicsWorld(gravity);
	}

	// --- Статика сцены ---------------------------------------------------------------------------

	/// <summary>Начинает пересборку статики. Между Begin и End вызывающий сваливает сюда мировые
	/// треугольники сцены; старая статика живёт до самого <see cref="EndStatics"/>, чтобы неудачная
	/// или пустая пересборка не оставила сцену без пола.</summary>
	public void BeginStatics()
	{
		_staticVertices.Clear();
		_staticIndices.Clear();
		_building = true;
	}

	/// <summary>Добавляет меш В МИРОВЫХ КООРДИНАТАХ. Порядок вершин передаётся КАК ЕСТЬ - см.
	/// PhysicsWorld.AddTriangleMesh: разворот обхода «по документации» даёт односторонний меш,
	/// сквозь который тела проваливаются без единого столкновения.</summary>
	public void AddStaticMesh(ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices)
	{
		if (!_building || positions.Length == 0 || indices.Length < 3)
		{
			return;
		}

		uint baseVertex = (uint)_staticVertices.Count;

		foreach (var position in positions)
		{
			_staticVertices.Add(position);
		}

		// Кратность трём обеспечивается здесь, а не проверкой у вызывающего: хвост в один-два индекса
		// приехал бы в Mesh уже как треугольник из чужих вершин.
		int triangleIndices = indices.Length - indices.Length % 3;
		for (int i = 0; i < triangleIndices; i++)
		{
			_staticIndices.Add(baseVertex + indices[i]);
		}
	}

	/// <summary>Завершает пересборку: снимает прежний статик и заводит новый одним мешом.</summary>
	public void EndStatics()
	{
		if (!_building)
		{
			return;
		}

		_building = false;

		// Пустая пересборка НЕ ТРОГАЕТ прежнюю статику - проверка стоит ДО сноса.
		//
		// Раньше она стояла после, и это ровно противоречило замыслу, описанному у BeginStatics:
		// «старая статика живёт до самого EndStatics, чтобы неудачная или пустая пересборка не
		// оставила сцену без пола». Оставляла. Пересборка идёт на любое движение объекта, а
		// геометрия сцены СТРИМИТСЯ - в кадре, где модель пола ещё не дошла (или её выселили), сбор
		// давал ноль треугольников, пол снимался, и всё, что на нём стояло, уходило в свободное
		// падение. Причём молча и НАВСЕГДА: следующая пересборка случится только от следующего
		// движения, а падать уже начали все.
		if (_staticIndices.Count < 3)
		{
			_staticVertices.Clear();
			_staticIndices.Clear();
			return;
		}

		if (_hasStatic)
		{
			World.Remove(_staticHandle);
			World.RemoveShape(_staticShape);
			_hasStatic = false;
			StaticTriangleCount = 0;
		}

		_staticShape = World.AddTriangleMesh(
			System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_staticVertices),
			System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_staticIndices),
			Vector3.One);

		// Меш уже в мире - поза статика единичная. Масштаб тоже: он запечён в вершины, и повторить
		// его здесь значило бы применить дважды.
		_staticHandle = World.AddStatic(new RigidPose(Vector3.Zero), _staticShape);
		_hasStatic = true;
		StaticTriangleCount = _staticIndices.Count / 3;

		// Списки держат мировые копии всей сцены - на Sponza это десятки мегабайт, которые после
		// постройки BVH не нужны никому.
		_staticVertices.Clear();
		_staticIndices.Clear();
		_staticVertices.TrimExcess();
		_staticIndices.TrimExcess();
	}

	// --- Кадр ------------------------------------------------------------------------------------

	/// <summary>Двигает симуляцию и обновляет счётчики. Возвращает число проинтегрированных шагов.</summary>
	public int Update(float deltaSeconds)
	{
		_rays.Clear();
		RayCastsThisFrame = 0;

		if (Paused)
		{
			LastStepCount = 0;
			LastStepMilliseconds = 0.0;
			return 0;
		}

		_stepTimer.Restart();
		LastStepCount = World.Update(deltaSeconds * MathF.Max(TimeScale, 0f));
		_stepTimer.Stop();

		LastStepMilliseconds = _stepTimer.Elapsed.TotalMilliseconds;
		return LastStepCount;
	}

	// --- Запросы ---------------------------------------------------------------------------------

	/// <summary>
	/// Райкаст с записью для дебага. Возвращает <see cref="GroundSample"/>, а не <see cref="RayHit"/>,
	/// потому что главный потребитель здесь - foot IK, который принимает райкаст ДЕЛЕГАТОМ и про
	/// Bepu ничего не знает (см. FootIk: солвер живёт в графическом слое и физику за собой не тянет).
	///
	/// Луч видит ТОЛЬКО СТАТИКУ. «Пол» - это сцена, а вся динамика в ней - капсулы персонажей: луч
	/// высоты подъёма попадал в туловище лежащего рэгдолла (персонаж вставал НА СЕБЯ, на 0.288 вместо
	/// 0), а лучи foot IK у персонажа с kinematic-рэгдоллом точно так же находят «пол» на собственных
	/// капсулах ног.
	/// </summary>
	public GroundSample SampleGround(Vector3 origin, Vector3 direction, float maximumT)
	{
		RayCastsThisFrame++;

		var hit = World.RayCastStatic(origin, direction, maximumT);

		if (RecordRays && _rays.Count < MaxRecordedRays)
		{
			_rays.Add(new RecordedRay
			{
				Origin = origin,
				Direction = direction,
				Length = maximumT,
				Hit = hit.Hit,
				HitPosition = hit.Position,
				HitNormal = hit.Normal,
			});
		}

		return new GroundSample
		{
			Hit = hit.Hit,
			Position = hit.Position,
			Normal = hit.Normal,
		};
	}

	public void Dispose()
	{
		// Формы освобождает Simulation.Dispose вместе с пулом, поэтому отдельного снятия статика
		// здесь нет: оно только удлинило бы путь выхода.
		World.Dispose();
	}
}
