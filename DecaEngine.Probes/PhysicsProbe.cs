using System;
using System.Numerics;
using BepuPhysics;
using DecaEngine.Physics;
using DecaEngine.Scene;

namespace DecaEngine.Probes;

/// <summary>
/// Автономная проверка физического мира (DECA_PROBE_PHYSICS=1, печатается из PreviewProbe).
/// Графика тут не нужна вовсе - проверяется ровно то, на чём будет стоять процедурный слой:
/// гравитация действует, тело останавливается НА полу, а не в нём и не над ним, и райкаст находит
/// поверхность там, где она есть. Каждая из этих трёх вещей ломается по-своему тихо: тело,
/// провалившееся сквозь пол, и тело, зависшее над ним, выглядят в редакторе как «физика не
/// работает», а причины у них разные (winding меша против маржи спекулятивных контактов).
/// </summary>
public static class PhysicsProbe
{
	public static void Run()
	{
		using var world = new PhysicsWorld(new Vector3(0f, -9.81f, 0f));

		// Пол коробкой, а не мешем: у коробки нет вопроса о winding, и если тело провалится сквозь
		// НЕЁ, то виновата не геометрия, а сама симуляция. Меш проверяется отдельным шагом ниже.
		var floorShape = world.AddBox(new Vector3(50f, 1f, 50f));
		world.AddStatic(new RigidPose(new Vector3(0f, -0.5f, 0f)), floorShape);

		const float radius = 0.5f;
		var sphereShape = world.AddSphere(radius);
		var body = world.AddDynamic(new RigidPose(new Vector3(0f, 5f, 0f)), sphereShape, mass: 1f);

		float simulated = 0f;
		while (simulated < 3f)
		{
			world.Update(1f / 60f);
			simulated += 1f / 60f;
		}

		var rest = world.Simulation.Bodies[body].Pose.Position;

		// Допуск в сантиметр: решатель Bepu оставляет телу небольшое проникновение (contact
		// softness) и не обязан приводить его РОВНО на радиус. Нулевой допуск здесь означал бы
		// проверку, которая падает на корректной физике.
		bool resting = MathF.Abs(rest.Y - radius) < 0.01f;
		Console.WriteLine($"[probe] physics: сфера легла на y={rest.Y:0.####} (ожидалось {radius}) " +
			$"{(resting ? "OK" : "MISMATCH")}, снос по XZ {MathF.Sqrt(rest.X * rest.X + rest.Z * rest.Z):0.####}");

		// Луч сверху обязан попасть в ВЕРХ сферы, то есть на удалении 5 - 2r от старта.
		var hit = world.RayCast(new Vector3(0f, 5f, 0f), new Vector3(0f, -1f, 0f), 20f);
		float expected = 5f - 2f * radius;
		bool rayOk = hit.Hit && MathF.Abs(hit.Distance - expected) < 0.05f && hit.Normal.Y > 0.9f;

		Console.WriteLine($"[probe] physics: райкаст {(hit.Hit ? $"попал на {hit.Distance:0.####} (ожидалось {expected:0.##}), нормаль {hit.Normal}" : "НЕ ПОПАЛ")} " +
			$"{(rayOk ? "OK" : "MISMATCH")}");

		// Луч мимо сферы обязан достать пол, а не сферу: без этого предыдущая проверка прошла бы и у
		// райкаста, который бьёт во что попало.
		var floorHit = world.RayCast(new Vector3(10f, 5f, 10f), new Vector3(0f, -1f, 0f), 20f);
		bool floorOk = floorHit.Hit && floorHit.IsStatic && MathF.Abs(floorHit.Distance - 5f) < 0.01f;
		Console.WriteLine($"[probe] physics: райкаст мимо сферы {(floorHit.Hit ? $"попал в {(floorHit.IsStatic ? "статик" : "тело")} на {floorHit.Distance:0.####}" : "НЕ ПОПАЛ")} " +
			$"{(floorOk ? "OK" : "MISMATCH")}");

		ProbeTriangleMesh();
		ProbeSceneStatics();
		ProbeContacts();
	}

	/// <summary>
	/// Пересборка статики сцены (см. <see cref="ScenePhysics"/>). В редакторе она случается на
	/// КАЖДОЕ движение объекта, то есть постоянно, и ломается двумя тихими способами: старый меш
	/// остаётся висеть в мире (тело сталкивается с геометрией там, где её уже нет) или его буферы
	/// не освобождаются (утечка, растущая со временем работы редактора). Проверяется первое -
	/// второе из процесса не видно, но оба лечатся одним и тем же RemoveAndDispose.
	/// </summary>
	private static void ProbeSceneStatics()
	{
		using var scene = new ScenePhysics(new Vector3(0f, -9.81f, 0f));

		// Первый пол на y=0, второй - на y=2. Если старый не снялся, тело ляжет на 0.5, а не на 2.5.
		BuildFloor(scene, 0f);
		int firstTriangles = scene.StaticTriangleCount;

		BuildFloor(scene, 2f);

		var body = scene.World.AddDynamic(new RigidPose(new Vector3(0f, 6f, 0f)),
			scene.World.AddSphere(0.5f), mass: 1f);

		for (float simulated = 0f; simulated < 3f; simulated += 1f / 60f)
		{
			scene.Update(1f / 60f);
		}

		float rest = scene.World.Simulation.Bodies[body].Pose.Position.Y;
		bool onNewFloor = MathF.Abs(rest - 2.5f) < 0.01f;

		Console.WriteLine($"[probe] physics: пересборка статики - треугольников {firstTriangles} -> " +
			$"{scene.StaticTriangleCount}, сфера легла на y={rest:0.####} (ожидалось 2,5) " +
			$"{(onNewFloor ? "OK" : MathF.Abs(rest - 0.5f) < 0.01f ? "СТАРЫЙ ПОЛ НЕ СНЯЛСЯ" : "MISMATCH")}");

		// ПУСТАЯ пересборка обязана оставить прежний пол. Геометрия сцены стримится, и кадр, в
		// котором её модель ещё не дошла (или её выселили), даёт ноль треугольников - если такая
		// пересборка снесёт статику, всё стоящее на ней уйдёт в свободное падение, причём молча и
		// навсегда: следующая случится только от следующего движения объекта.
		scene.BeginStatics();
		scene.EndStatics();

		int afterEmpty = scene.StaticTriangleCount;

		for (float simulated = 0f; simulated < 1f; simulated += 1f / 60f)
		{
			scene.Update(1f / 60f);
		}

		float afterRest = scene.World.Simulation.Bodies[body].Pose.Position.Y;
		bool kept = afterEmpty == scene.StaticTriangleCount && afterEmpty > 0 &&
			MathF.Abs(afterRest - 2.5f) < 0.01f;

		Console.WriteLine($"[probe] physics: ПУСТАЯ пересборка - треугольников осталось {afterEmpty}, " +
			$"сфера на y={afterRest:0.####} {(kept ? "OK" : "ПОЛ СНЕСЁН ПУСТОЙ ПЕРЕСБОРКОЙ")}");
	}

	private static void BuildFloor(ScenePhysics scene, float height)
	{
		Vector3[] vertices =
		[
			new(-25f, height, -25f), new(-25f, height, 25f), new(25f, height, 25f), new(25f, height, -25f),
		];
		// Обход - как у геометрии движка (см. ProbeTriangleMesh: тот же порядок, что у
		// SampleGroundBuilder); разворот под Bepu делает AddTriangleMesh.
		uint[] indices = [0, 1, 2, 0, 2, 3];

		scene.BeginStatics();
		scene.AddStaticMesh(vertices, indices);
		scene.EndStatics();
	}

	/// <summary>
	/// Сбор точек контакта (см. <see cref="DecaEngine.Physics.PhysicsContactRecorder"/>). Проверяется
	/// не только то, что контакты появились, но и ГДЕ они: смещение в манифолде Bepu отсчитывается от
	/// позиции коллайдера A, и забыть про это - значит получить полный список контактов, нарисованных
	/// в начале координат. Ошибка, которая выглядит как «дебаг работает».
	/// </summary>
	private static void ProbeContacts()
	{
		using var world = new PhysicsWorld(new Vector3(0f, -9.81f, 0f));
		world.Contacts.Enabled = true;

		var floor = world.AddBox(new Vector3(50f, 1f, 50f));

		// Пол СМЕЩЁН по XZ вместе с телом: контакт у начала координат тогда означает ошибку
		// пересчёта, а не совпадение с центром сцены.
		world.AddStatic(new RigidPose(new Vector3(20f, -0.5f, -30f)), floor);

		var body = world.AddDynamic(new RigidPose(new Vector3(20f, 3f, -30f)), world.AddSphere(0.5f), mass: 1f);

		// Снимок берётся ПО ХОДУ, а не в конце: успокоившееся тело Bepu усыпляет, узкая фаза для него
		// больше не работает, и список контактов в покое пуст СОВЕРШЕННО ЗАКОННО. Проверка «в конце»
		// падала бы на исправном сборе.
		int count = 0;
		float worst = 0f;

		for (float simulated = 0f; simulated < 3f; simulated += 1f / 60f)
		{
			world.Update(1f / 60f);

			var frame = world.Contacts.Contacts;
			if (frame.Count == 0)
			{
				continue;
			}

			var position = world.Simulation.Bodies[body].Pose.Position;

			count = frame.Count;
			worst = 0f;
			foreach (var contact in frame)
			{
				worst = MathF.Max(worst, Vector3.Distance(contact.Position, position));
			}
		}

		// Контакт сферы обязан быть в пределах её радиуса от центра тела - с запасом на спекулятивную
		// маржу. Нулевой центр сцены отсюда далеко (пол смещён на 20/-30), так что нерассчитанное
		// смещение манифолда дало бы расхождение в десятки единиц, а не в доли.
		bool ok = count > 0 && worst < 0.75f;

		Console.WriteLine($"[probe] physics: контактов собрано {count}, " +
			$"дальше всех от центра тела на {worst:0.####} " +
			$"{(ok ? "OK" : count == 0 ? "НЕ СОБРАЛИСЬ" : "НЕ ТАМ (смещение манифолда не пересчитано)")}");
	}

	/// <summary>
	/// Тот же тест, но полом служит МЕШ из треугольников - проверка разворота winding в
	/// <see cref="PhysicsWorld.AddTriangleMesh"/>. Движок держит геометрию в левосторонней системе с
	/// развёрнутым обходом вершин, а Bepu считает лицевым треугольник против часовой стрелки: при
	/// неверном развороте нормали столкновений смотрят вниз, и тело проваливается сквозь пол, не
	/// заметив его.
	/// </summary>
	private static void ProbeTriangleMesh()
	{
		// Квадрат в плоскости y=0, обход - КАК У ГЕОМЕТРИИ ДВИЖКА, то есть тот же порядок, которым
		// площадку демо-сцены выкладывает SampleGroundBuilder: (a,b,c) + (a,c,d).
		//
		// Раньше он был выложен наоборот, и это скрывало настоящую ошибку: рукописный квадрат сам
		// компенсировал разворот, которого не делал AddTriangleMesh, проверка проходила, а вся
		// импортированная геометрия при этом не сталкивалась ни с чем. Рукописную геометрию в
		// проверке физики надо класть ровно так, как её кладёт движок, иначе проверяется она сама.
		Vector3[] vertices =
		[
			new(-25f, 0f, -25f), new(-25f, 0f, 25f), new(25f, 0f, 25f), new(25f, 0f, -25f),
		];
		uint[] indices = [0, 1, 2, 0, 2, 3];

		float rest = DropOntoMesh(vertices, indices);
		const float radius = 0.5f;
		bool resting = MathF.Abs(rest - radius) < 0.01f;

		Console.WriteLine($"[probe] physics: сфера на МЕШЕ легла на y={rest:0.####} (ожидалось {radius}) " +
			$"{(resting ? "OK" : (rest < 0f ? "ПРОВАЛИЛАСЬ" : "MISMATCH"))}");

		if (!resting)
		{
			// Меш в Bepu ОДНОСТОРОННИЙ, и провал сквозь него означает ровно одно: лицевая сторона
			// смотрит вниз. Печатаем результат ПРОТИВОПОЛОЖНОГО обхода - если сработал он, значит
			// разворот winding в AddTriangleMesh лишний (или наоборот), и гадать не нужно.
			uint[] flipped = [indices[0], indices[2], indices[1], indices[3], indices[5], indices[4]];
			float flippedRest = DropOntoMesh(vertices, flipped);

			Console.WriteLine($"[probe] physics: тот же меш с ОБРАТНЫМ обходом - y={flippedRest:0.####} " +
				$"{(MathF.Abs(flippedRest - radius) < 0.01f ? "ЛЁГ (разворот winding в AddTriangleMesh неверен)" : "тоже провалилась")}");
		}
	}

	/// <summary>Роняет сферу на меш из заданных треугольников и возвращает её итоговую высоту.</summary>
	private static float DropOntoMesh(Vector3[] vertices, uint[] indices)
	{
		using var world = new PhysicsWorld(new Vector3(0f, -9.81f, 0f));

		var meshShape = world.AddTriangleMesh(vertices, indices, Vector3.One);
		world.AddStatic(new RigidPose(Vector3.Zero), meshShape);

		var body = world.AddDynamic(new RigidPose(new Vector3(0f, 5f, 0f)), world.AddSphere(0.5f), mass: 1f);

		float simulated = 0f;
		while (simulated < 3f)
		{
			world.Update(1f / 60f);
			simulated += 1f / 60f;
		}

		return world.Simulation.Bodies[body].Pose.Position.Y;
	}
}
