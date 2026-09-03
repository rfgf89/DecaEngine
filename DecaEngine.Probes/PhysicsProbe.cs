using System;
using System.Numerics;
using BepuPhysics;
using DecaEngine.Physics;
using DecaEngine.Scene;

namespace DecaEngine.Probes;

/// <summary>Standalone physics-world check (DECA_PROBE_PHYSICS=1, printed from PreviewProbe):
/// gravity, resting ON the floor (not in/above it), and raycasts hitting real surfaces.</summary>
public static class PhysicsProbe
{
	public static void Run()
	{
		using var world = new PhysicsWorld(new Vector3(0f, -9.81f, 0f));

		// Box floor, not a mesh: a box has no winding question, so falling through it blames
		// the simulation itself. The mesh path is checked separately below.
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

		// 1 cm tolerance: Bepu's contact softness leaves slight penetration; exact-radius rest
		// is not guaranteed and a zero tolerance would fail on correct physics.
		bool resting = MathF.Abs(rest.Y - radius) < 0.01f;
		Console.WriteLine($"[probe] physics: sphere rested at y={rest.Y:0.####} (expected {radius}) " +
			$"{(resting ? "OK" : "MISMATCH")}, XZ drift {MathF.Sqrt(rest.X * rest.X + rest.Z * rest.Z):0.####}");

		// Ray from above must hit the TOP of the sphere, i.e. at 5 - 2r from the origin.
		var hit = world.RayCast(new Vector3(0f, 5f, 0f), new Vector3(0f, -1f, 0f), 20f);
		float expected = 5f - 2f * radius;
		bool rayOk = hit.Hit && MathF.Abs(hit.Distance - expected) < 0.05f && hit.Normal.Y > 0.9f;

		Console.WriteLine($"[probe] physics: raycast {(hit.Hit ? $"hit at {hit.Distance:0.####} (expected {expected:0.##}), normal {hit.Normal}" : "MISSED")} " +
			$"{(rayOk ? "OK" : "MISMATCH")}");

		// A ray missing the sphere must reach the floor, proving hits are not indiscriminate.
		var floorHit = world.RayCast(new Vector3(10f, 5f, 10f), new Vector3(0f, -1f, 0f), 20f);
		bool floorOk = floorHit.Hit && floorHit.IsStatic && MathF.Abs(floorHit.Distance - 5f) < 0.01f;
		Console.WriteLine($"[probe] physics: raycast past the sphere {(floorHit.Hit ? $"hit a {(floorHit.IsStatic ? "static" : "body")} at {floorHit.Distance:0.####}" : "MISSED")} " +
			$"{(floorOk ? "OK" : "MISMATCH")}");

		ProbeTriangleMesh();
		ProbeSceneStatics();
		ProbeContacts();
	}

	// Scene static rebuild check: a stale mesh left in the world is the silent failure mode.
	private static void ProbeSceneStatics()
	{
		using var scene = new ScenePhysics(new Vector3(0f, -9.81f, 0f));

		// First floor at y=0, second at y=2; if the old one is not removed the body rests at 0.5.
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

		Console.WriteLine($"[probe] physics: statics rebuild - triangles {firstTriangles} -> " +
			$"{scene.StaticTriangleCount}, sphere rested at y={rest:0.####} (expected 2,5) " +
			$"{(onNewFloor ? "OK" : MathF.Abs(rest - 0.5f) < 0.01f ? "OLD FLOOR NOT REMOVED" : "MISMATCH")}");

		// An EMPTY rebuild must keep the previous floor: streamed geometry can be absent for a
		// frame, and wiping statics then would silently drop everything resting on them.
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

		Console.WriteLine($"[probe] physics: EMPTY rebuild - triangles left {afterEmpty}, " +
			$"sphere at y={afterRest:0.####} {(kept ? "OK" : "FLOOR WIPED BY EMPTY REBUILD")}");
	}

	private static void BuildFloor(ScenePhysics scene, float height)
	{
		Vector3[] vertices =
		[
			new(-25f, height, -25f), new(-25f, height, 25f), new(25f, height, 25f), new(25f, height, -25f),
		];
		// Same winding as engine geometry (see ProbeTriangleMesh); AddTriangleMesh flips for Bepu.
		uint[] indices = [0, 1, 2, 0, 2, 3];

		scene.BeginStatics();
		scene.AddStaticMesh(vertices, indices);
		scene.EndStatics();
	}

	// Contact capture check: Bepu manifold offsets are relative to collider A's position, so the
	// check verifies WHERE contacts land, not just that they exist.
	private static void ProbeContacts()
	{
		using var world = new PhysicsWorld(new Vector3(0f, -9.81f, 0f));
		world.Contacts.Enabled = true;

		var floor = world.AddBox(new Vector3(50f, 1f, 50f));

		// Floor offset in XZ along with the body: a contact near the origin then means a
		// conversion bug, not a coincidence with the scene center.
		world.AddStatic(new RigidPose(new Vector3(20f, -0.5f, -30f)), floor);

		var body = world.AddDynamic(new RigidPose(new Vector3(20f, 3f, -30f)), world.AddSphere(0.5f), mass: 1f);

		// Sample DURING simulation, not at the end: Bepu sleeps a settled body and its contact
		// list at rest is legitimately empty.
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

		// Contacts must lie within the sphere radius (plus speculative margin) of the body.
		bool ok = count > 0 && worst < 0.75f;

		Console.WriteLine($"[probe] physics: contacts collected {count}, " +
			$"farthest from the body center by {worst:0.####} " +
			$"{(ok ? "OK" : count == 0 ? "NONE COLLECTED" : "WRONG PLACE (manifold offset not rebased)")}");
	}

	// Same drop test on a triangle MESH floor: checks the winding flip in AddTriangleMesh.
	// The engine keeps left-handed geometry with reversed winding while Bepu treats CCW as front;
	// a wrong flip points collision normals down and bodies fall through.
	private static void ProbeTriangleMesh()
	{
		// Quad at y=0 wound EXACTLY like engine geometry (SampleGroundBuilder): (a,b,c)+(a,c,d).
		// Hand-laid test geometry must match the engine's winding or it only tests itself.
		Vector3[] vertices =
		[
			new(-25f, 0f, -25f), new(-25f, 0f, 25f), new(25f, 0f, 25f), new(25f, 0f, -25f),
		];
		uint[] indices = [0, 1, 2, 0, 2, 3];

		float rest = DropOntoMesh(vertices, indices);
		const float radius = 0.5f;
		bool resting = MathF.Abs(rest - radius) < 0.01f;

		Console.WriteLine($"[probe] physics: sphere on a MESH rested at y={rest:0.####} (expected {radius}) " +
			$"{(resting ? "OK" : (rest < 0f ? "FELL THROUGH" : "MISMATCH"))}");

		if (!resting)
		{
			// Bepu meshes are one-sided; retry with opposite winding to pinpoint the flip bug.
			uint[] flipped = [indices[0], indices[2], indices[1], indices[3], indices[5], indices[4]];
			float flippedRest = DropOntoMesh(vertices, flipped);

			Console.WriteLine($"[probe] physics: same mesh with REVERSED winding - y={flippedRest:0.####} " +
				$"{(MathF.Abs(flippedRest - radius) < 0.01f ? "RESTED (winding flip in AddTriangleMesh is wrong)" : "fell through as well")}");
		}
	}

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
