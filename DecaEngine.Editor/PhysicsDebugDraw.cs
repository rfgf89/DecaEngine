using System;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using DecaEngine.Graphics;
using DecaEngine.Physics;

namespace DecaEngine.Editor;

/// <summary>
/// Переводит состояние мира физики в дебаг-линии: каркасы коллайдеров, точки контакта, лучи,
/// скорости.
///
/// Рисуется по РЕАЛЬНЫМ формам из реестра симуляции, а не по тому, что заказывал вызывающий. Это
/// принципиально: половина ошибок физики - это расхождение между «я создал капсулу такой» и «в
/// симуляции лежит вот такая» (капсула Bepu измеряется цилиндрической частью, у меша не тот
/// масштаб, тело осталось от прошлой сборки). Дебаг, рисующий заказ, такие ошибки скрывает.
///
/// Статический класс без состояния: всё, что нужно, - симуляция и приёмник линий.
/// </summary>
public static class PhysicsDebugDraw
{
	/// <summary>Цвет тела по его состоянию. Кодировка одна на весь движок (см. легенду в окне
	/// дебага): оранжевое - динамика, голубое - кинематика, серое - спит.</summary>
	private static Vector4 BodyColor(bool kinematic, bool awake)
	{
		if (!awake)
		{
			return DebugColor.Grey;
		}

		return kinematic ? DebugColor.Cyan : DebugColor.Orange;
	}

	public static void Draw(DebugDraw draw, ScenePhysics physics, in PhysicsDebugOptions options)
	{
		if (draw is not { Enabled: true } || !options.AnyEnabled)
		{
			return;
		}

		var simulation = physics.World.Simulation;

		if (options.Colliders || options.Velocities)
		{
			DrawBodies(draw, simulation, options);
		}

		if (options.Statics)
		{
			DrawStatics(draw, simulation, options.OnTop);
		}

		if (options.Contacts)
		{
			DrawContacts(draw, physics, options.OnTop);
		}

		if (options.Rays)
		{
			DrawRays(draw, physics, options.OnTop);
		}
	}

	private static void DrawBodies(DebugDraw draw, Simulation simulation, in PhysicsDebugOptions options)
	{
		var bodies = simulation.Bodies;

		// Обход по НАБОРАМ, а не по активному: спящие тела лежат в отдельных наборах, и рисовать
		// только активные значит показывать пустую сцену через секунду после того, как всё легло, -
		// ровно тогда, когда и появляется вопрос «а где мои тела».
		for (int setIndex = 0; setIndex < bodies.Sets.Length; setIndex++)
		{
			ref var set = ref bodies.Sets[setIndex];
			if (!set.Allocated)
			{
				continue;
			}

			bool awake = setIndex == 0;

			for (int i = 0; i < set.Count; i++)
			{
				var body = bodies[set.IndexToHandle[i]];
				var pose = body.Pose;
				bool kinematic = body.Kinematic;
				var color = BodyColor(kinematic, awake);

				if (options.Colliders)
				{
					// Коллайдеры - по СВОЕМУ флагу: капсула персонажа целиком внутри его меша, и с
					// депт-тестом её не видно вовсе (см. PhysicsDebugOptions.CollidersDepthTested).
					DrawShape(draw, simulation, body.Collidable.Shape, pose, color,
						!options.CollidersDepthTested);
				}

				if (options.Velocities && awake)
				{
					DrawVelocity(draw, pose.Position, body.Velocity, options.OnTop);
				}
			}
		}
	}

	/// <summary>Скорость стрелкой из центра тела. Длина - сама скорость в единицах мира за секунду:
	/// не нормализованная и не масштабированная, потому что вопрос к ней обычно количественный -
	/// «почему оно летит так быстро», - а нормализованная стрелка на него не отвечает.</summary>
	private static void DrawVelocity(DebugDraw draw, Vector3 position, in BodyVelocity velocity, bool onTop)
	{
		if (velocity.Linear.LengthSquared() > 1e-6f)
		{
			draw.Arrow(position, position + velocity.Linear, DebugColor.Green, onTop);
		}

		if (velocity.Angular.LengthSquared() > 1e-6f)
		{
			draw.Arrow(position, position + velocity.Angular, DebugColor.Magenta, onTop);
		}
	}

	private static void DrawStatics(DebugDraw draw, Simulation simulation, bool onTop)
	{
		var statics = simulation.Statics;

		for (int i = 0; i < statics.Count; i++)
		{
			var reference = statics[statics.IndexToHandle[i]];
			DrawShape(draw, simulation, reference.Shape, reference.Pose, DebugColor.Blue, onTop);
		}
	}

	/// <summary>
	/// Каркас формы по её ФАКТИЧЕСКИМ данным из реестра. Меш рисуется габаритной коробкой, а не
	/// треугольниками: статика сцены - это вся её геометрия, и каркас по треугольникам не только
	/// стоит миллионы линий, но и закрашивает экран так, что не видно ничего, ради чего дебаг
	/// включали. Треугольники и так видны - это сама сцена.
	/// </summary>
	private static void DrawShape(DebugDraw draw, Simulation simulation, TypedIndex shape, in RigidPose pose,
		Vector4 color, bool onTop)
	{
		if (!shape.Exists)
		{
			return;
		}

		switch (shape.Type)
		{
			case Sphere.Id:
			{
				var sphere = simulation.Shapes.GetShape<Sphere>(shape.Index);
				draw.WireSphere(pose.Position, sphere.Radius, color, 20, onTop);
				break;
			}

			case Capsule.Id:
			{
				var capsule = simulation.Shapes.GetShape<Capsule>(shape.Index);

				// HalfLength, а не Length: Bepu хранит именно половину ЦИЛИНДРИЧЕСКОЙ части, а
				// рисователь принимает полную её длину - см. DebugDraw.WireCapsule.
				draw.WireCapsule(pose.Position, pose.Orientation, capsule.Radius, capsule.HalfLength * 2f,
					color, 14, onTop);
				break;
			}

			case Box.Id:
			{
				var box = simulation.Shapes.GetShape<Box>(shape.Index);
				draw.WireBox(pose.Position, pose.Orientation,
					new Vector3(box.HalfWidth, box.HalfHeight, box.HalfLength), color, onTop);
				break;
			}

			case Cylinder.Id:
			{
				var cylinder = simulation.Shapes.GetShape<Cylinder>(shape.Index);
				draw.WireCylinder(pose.Position, pose.Orientation, cylinder.Radius,
					cylinder.HalfLength * 2f, color, 14, onTop);
				break;
			}

			case Mesh.Id:
			{
				var mesh = simulation.Shapes.GetShape<Mesh>(shape.Index);
				mesh.ComputeBounds(pose.Orientation, out var min, out var max);

				draw.WireBox(pose.Position + min, pose.Position + max, DebugColor.Dim(color), onTop);
				break;
			}

			default:
			{
				// Форма, которую здесь ещё не научились рисовать (составная, выпуклая оболочка), -
				// крест в её положении. Молчание было бы хуже: тело есть, а на экране пусто, и это
				// читается как «тела нет», то есть как совсем другой диагноз.
				draw.Cross(pose.Position, 0.1f, DebugColor.Magenta, onTop);
				break;
			}
		}
	}

	/// <summary>Точки контакта: крест в точке, стрелка по нормали, длина стрелки - глубина
	/// проникновения. Именно глубина отвечает на вопрос «почему тело дрожит»: контакт с растущей
	/// глубиной - это решатель, который не справляется, а не «плохая геометрия».</summary>
	private static void DrawContacts(DebugDraw draw, ScenePhysics physics, bool onTop)
	{
		var contacts = physics.World.Contacts.Contacts;

		for (int i = 0; i < contacts.Count; i++)
		{
			var contact = contacts[i];
			var color = contact.AgainstStatic ? DebugColor.Yellow : DebugColor.Red;

			draw.Cross(contact.Position, 0.03f, color, onTop);
			draw.Arrow(contact.Position, contact.Position + contact.Normal * MathF.Max(contact.Depth, 0.02f),
				color, onTop);
		}
	}

	/// <summary>Лучи кадра: сам луч серым, попавшая часть - зелёным до точки попадания, плюс нормаль
	/// поверхности. Промах остаётся полностью серым - так «луч не долетел» отличимо от «под стопой
	/// действительно пусто».</summary>
	private static void DrawRays(DebugDraw draw, ScenePhysics physics, bool onTop)
	{
		foreach (var ray in physics.Rays)
		{
			var end = ray.Origin + ray.Direction * ray.Length;

			if (!ray.Hit)
			{
				draw.Line(ray.Origin, end, DebugColor.Grey, onTop);
				continue;
			}

			draw.Line(ray.Origin, ray.HitPosition, DebugColor.Green, onTop);
			draw.Line(ray.HitPosition, end, DebugColor.Dim(DebugColor.Grey), onTop);

			float normalLength = Vector3.Distance(ray.Origin, ray.HitPosition) * 0.25f + 1e-3f;
			draw.Arrow(ray.HitPosition, ray.HitPosition + ray.HitNormal * normalLength, DebugColor.Cyan, onTop);
		}
	}
}
