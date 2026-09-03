using System;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using DecaEngine.Graphics;
using DecaEngine.Physics;
using DecaEngine.Scene;

namespace DecaEngine.Editor;

/// <summary>Draws the physics world as debug lines, using the simulation's actual shapes.</summary>
public static class PhysicsDebugDraw
{
	// Engine-wide legend: orange dynamic, cyan kinematic, grey asleep.
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

		// Iterate all sets, not just the active one: sleeping bodies live in separate sets.
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
					// Own depth flag: a character capsule sits inside its mesh and is hidden otherwise.
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

	// Arrow length is the raw velocity in world units per second, not normalized.
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

	// Meshes draw as a bounding box: per-triangle wireframe costs millions of lines.
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

				// Bepu HalfLength covers only the cylindrical part; WireCapsule wants its full length.
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
				// Unsupported shape (compound, hull): mark it, so it never reads as "no body".
				draw.Cross(pose.Position, 0.1f, DebugColor.Magenta, onTop);
				break;
			}
		}
	}

	// Normal arrow length is the penetration depth.
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

	// A miss stays fully grey, so "ray too short" is distinguishable from "nothing there".
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
