using System;
using System.Numerics;
using BepuPhysics;
using DecaEngine.Editor.ECS;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using DecaEngine.Scene;
using DecaEngine.Editor;

namespace DecaEngine.Probes;

/// <summary>Headless gameplay-script probes (DECA_PROBE_GAMEPLAY=1); drives the real systems via <see cref="SystemRoot"/>, the same path Play Mode ticks.</summary>
public static class GameplayProbe
{
	/// <summary>Step derived from the period: closure needs a whole number of steps per lap.</summary>
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
		ProbeOwnBoneOverlap();
		ProbeFloorRescue();
		ProbeLateFloor();
		ProbePlayer();
		ProbeStepUp();
		ProbeJump();
	}

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

				// deltaSeconds drives coyote-time decay; without it the jump window never closes.
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

			// A body that never lands would pass the height check; discard its apex.
			return MathF.Abs(landedY) < 0.03f ? top : float.MaxValue;
		}

		float single = Arc(jumpSpeed: 3.5f, extraJumpFrame: int.MaxValue);
		float doubled = Arc(jumpSpeed: 3.5f, extraJumpFrame: (int)MathF.Round(0.25f / PhysicsStep));
		float disabled = Arc(jumpSpeed: 0f, extraJumpFrame: int.MaxValue);

		float expected = 3.5f * 3.5f / (2f * 9.81f);

		bool arcOk = MathF.Abs(single - expected) < expected * 0.15f;
		bool doubleOk = doubled < single + 0.05f;
		bool disabledOk = disabled < 0.03f;

		Console.WriteLine($"[probe] gameplay: jump - apex {single:0.###} (ballistic {expected:0.###}) " +
			$"{(arcOk ? "OK" : "WRONG ARC")}, second Space in mid-air - apex {doubled:0.###} " +
			$"{(doubleOk ? "NO DOUBLE JUMP OK" : "DOUBLE JUMP")}, JumpSpeed=0 - lift {disabled:0.###} " +
			$"{(disabledOk ? "DOES NOT JUMP OK" : "JUMPS WITHOUT PERMISSION")}");
	}

	/// <summary>A/B pair on one scene, differing only in StepHeight: isolates the mechanic from the geometry.</summary>
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

			// Steer needs deltaSeconds: TurnSpeed is a per-second limit, so zero dt means no turn.
			for (int i = 0; i < steps; i++)
			{
				driver.Input = new PlayerInput { MoveWorld = Vector3.UnitX };
				driver.Steer(store, scene, active: true, PhysicsStep);
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

		Console.WriteLine($"[probe] gameplay: step-up - with a step reached x={with.FinalX:0.##} " +
			$"(rose to y={with.TopY:0.###}) {(climbedOk ? "CLIMBED OK" : "DID NOT TAKE THE STEP")}, " +
			$"wall {(wallOk ? "HOLDS OK" : "JUMPED OVER THE WALL")}; without step-up reached " +
			$"x={without.FinalX:0.##} {(blockedOk ? "STEP HOLDS OK" : "PAIR DID NOT DIVERGE")}");
	}

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
				driver.Steer(store, scene, active: true, PhysicsStep);
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

		// Not via Facing(): it reads Forward from the circle component, which the player lacks.
		var facing = Vector3.Transform(Vector3.UnitZ, Rotation(entity));
		float facingDot = Vector3.Dot(facing, direction);

		bool walkOk = MathF.Abs(walk.Length() - 2f) < 0.1f && walkDot > 0.999f;
		bool sprintOk = MathF.Abs(sprint.Length() - 3f) < 0.15f;
		bool stopOk = stop.Length() < 0.02f;
		bool facingOk = facingDot > 0.999f;

		Console.WriteLine($"[probe] gameplay: player - walk {walk.Length():0.###} m in 2 s " +
			$"(expected 2, along input {walkDot:0.####}) {(walkOk ? "OK" : "WRONG WAY/WRONG AMOUNT")}, " +
			$"run {sprint.Length():0.###} m in 1 s (expected 3) {(sprintOk ? "OK" : "WRONG SPEED")}");
		Console.WriteLine($"[probe] gameplay: player - with no input drifted {stop.Length():0.####} m " +
			$"{(stopOk ? "STANDS OK" : "SLIDES")}, facing along motion {facingDot:0.####} " +
			$"{(facingOk ? "OK" : "LOOKS THE WRONG WAY")}");
	}

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

			// Radius is measured in the XZ plane; vertical drift is checked separately below.
			var offset = position - center;
			worstRadius = MathF.Max(worstRadius, MathF.Abs(
				MathF.Sqrt(offset.X * offset.X + offset.Z * offset.Z) - radius));

			float travelled = (position - previous).Length();
			path += travelled;

			// Compare facing to actual motion, not the tangent formula, to catch a wrong forward axis.
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

		// Chord-vs-arc error over 600 steps is 0.005%; zero tolerance would test the polyline.
		bool shapeOk = worstRadius < 1e-3f && MathF.Abs(end.Y - center.Y) < 1e-4f;
		bool speedOk = MathF.Abs(path - expectedPath) < expectedPath * 1e-3f;
		bool closureOk = closure < 1e-3f;

		// Pose samples at step end but motion spans the step: half-step phase lag (~0.3 deg) is legal.
		bool facingOk = worstFacing < 0.5f * MathF.PI / 180f;

		Console.WriteLine($"[probe] gameplay: circle - worst radius deviation {worstRadius:0.#####} " +
			$"{(shapeOk ? "OK" : "NOT A CIRCLE")}, height {Position(entity).Y:0.####} " +
			$"(expected {center.Y})");
		Console.WriteLine($"[probe] gameplay: path per lap {path:0.####} " +
			$"(expected {expectedPath:0.####}) {(speedOk ? "OK" : "WRONG SPEED")}");
		Console.WriteLine($"[probe] gameplay: lap closure {closure:0.#####} " +
			$"{(closureOk ? "OK" : "CIRCLE DID NOT CLOSE")}");
		Console.WriteLine($"[probe] gameplay: facing along motion - worst mismatch " +
			$"{worstFacing * 180f / MathF.PI:0.###}° {(facingOk ? "OK" : "LOOKS THE WRONG WAY")}");
	}

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

		// Starting at +X, negative speed moves toward -Z.
		bool directionOk = position.Z < start.Z - 1e-4f;

		var motion = Vector3.Normalize(position - start);
		float facing = MathF.Acos(Math.Clamp(Vector3.Dot(Facing(entity), motion), -1f, 1f)) * 180f / MathF.PI;

		// One big 0.25 s step: half-step phase lag is already 3.6 deg, hence the coarse tolerance.
		Console.WriteLine($"[probe] gameplay: reverse direction - z {start.Z:0.###} -> {position.Z:0.###} " +
			$"{(directionOk ? "OK" : "WRONG SIDE")}, facing {facing:0.##}° " +
			$"{(facing < 5f ? "OK" : "WALKS BACKWARDS")}");
	}

	/// <summary>Zero radius is a division by zero in angular speed: unguarded it yields NaN, not standing still.</summary>
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

		Console.WriteLine($"[probe] gameplay: disabled component - displacement " +
			$"{(Position(disabled) - disabledStart).Length():0.#####} " +
			$"{(Position(disabled) == disabledStart ? "OK" : "MOVES WHILE DISABLED")}");
		Console.WriteLine($"[probe] gameplay: zero radius - position {zeroEnd} " +
			$"{(zeroOk ? "OK" : "NAN/DRIFT")}");
	}

	/// <summary>Model whose forward is -Z (Khronos Fox): lap metrics cannot catch a backward walker.</summary>
	private static void ProbeModelForward()
	{
		var forward = -Vector3.UnitZ;
		var (store, entity) = Build(Vector3.Zero, radius: 2f, speed: 1f, forward: forward);

		var root = new SystemRoot { new CircleMoveSystem() };
		root.AddStore(store);

		var start = Position(entity);
		root.Update(new UpdateTick(0.25f, 0.25f));

		var motion = Vector3.Normalize(Position(entity) - start);

		// +Z must point AGAINST motion, or a system that ignores Forward would pass too.
		float aligned = MathF.Acos(Math.Clamp(Vector3.Dot(Facing(entity), motion), -1f, 1f)) * 180f / MathF.PI;
		float axisZ = Vector3.Dot(Vector3.Transform(Vector3.UnitZ, Rotation(entity)), motion);

		Console.WriteLine($"[probe] gameplay: model faces {forward} - mismatch with motion " +
			$"{aligned:0.##}° {(aligned < 5f ? "OK" : "BACK TO FRONT")}, +Z axis against motion " +
			$"{axisZ:0.###} {(axisZ < -0.9f ? "OK" : "FORWARD IGNORED")}");
	}

	/// <summary>Entity at phase zero (+X from center), matching SamplePrefabBuilder.CreateCircleFox.</summary>
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
			// Fox-sized capsule (see SamplePrefabBuilder): tests the contact scale the scene uses.
			entity.AddComponent(new CharacterBodyComponent
			{
				Radius = 0.18f,
				Height = 0.5f,
				Mass = 12f,
			});
		}

		return (store, entity);
	}

	// --- Physical path: capsule in the scene world (see CharacterMotionDriver) ----------------------

	/// <summary>Matches a typical editor frame on purpose; physics has its own fixed step inside.</summary>
	private const float PhysicsStep = 1f / 60f;

	/// <summary>Tolerances are coarser by nature: the body is steered onto the circle, not placed on it.</summary>
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
			// Same order as the editor frame (PrefabSceneViewport.PollScenePhysics): Steer, step, Apply.
			driver.Steer(store, scene, active: true, PhysicsStep);
			scene.Update(PhysicsStep);
			driver.Apply(store, scene);

			var position = Position(entity);

			float distance = MathF.Sqrt(position.X * position.X + position.Z * position.Z);
			worstRadius = MathF.Max(worstRadius, MathF.Abs(distance - radius));
			worstHeight = MathF.Max(worstHeight, MathF.Abs(position.Y));

			var step = position - previous;
			path += MathF.Sqrt(step.X * step.X + step.Z * step.Z);

			// Accumulate angle increments: wrapped phase makes end-minus-start zero after a full lap.
			float angle = MathF.Atan2(position.Z, position.X);
			turned += CircleMotion.Wrap(angle - previousAngle);
			previousAngle = angle;

			previous = position;
		}

		float expectedPath = period * speed;

		// 2.5% radius tolerance leaves headroom for spin-up; steady-state error is far smaller.
		bool shapeOk = worstRadius < 0.05f;
		bool speedOk = MathF.Abs(path - expectedPath) < expectedPath * 0.05f;
		bool lapOk = MathF.Abs(turned - MathF.Tau) < 0.1f;

		// A capsule with a wrong center half-sinks or floats; on screen it reads as wrong model height.
		bool groundOk = worstHeight < 0.03f;

		Console.WriteLine($"[probe] gameplay: physical circle - worst radius deviation " +
			$"{worstRadius:0.####} {(shapeOk ? "OK" : "NOT A CIRCLE")}, feet off the floor {worstHeight:0.####} " +
			$"{(groundOk ? "OK" : "FLOATS/SINKS")}");
		Console.WriteLine($"[probe] gameplay: physical circle - path {path:0.###} " +
			$"(expected {expectedPath:0.###}) {(speedOk ? "OK" : "WRONG SPEED")}, covered " +
			$"{turned / MathF.Tau:0.###} of a lap {(lapOk ? "OK" : "LAP NOT CLOSED")}");
	}

	/// <summary>Wall across the circle: only the pair proves it - transform passes through, body must not.</summary>
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

		// Wall plane x=0, half-thickness 0.1, plus capsule radius 0.18; 5 cm slack for solver penetration.
		bool blocked = physicalMinX > 0.23f;
		bool crossed = transformMinX < -1f;

		Console.WriteLine($"[probe] gameplay: wall across the circle - body reached x={physicalMinX:0.###} " +
			$"{(blocked ? "OK (did not pass)" : "PASSED THROUGH THE WALL")}, transform - reached x={transformMinX:0.###} " +
			$"{(crossed ? "OK (passed, as it should)" : "DID NOT PASS - NOTHING TO COMPARE AGAINST")}");
	}

	/// <summary>Rebuilding statics under a walking body drops contact impulses; the pair shows the cost.</summary>
	private static void ProbeStaticChurn()
	{
		float stable = RunChurn(rebuildEveryFrame: false);
		float churn = RunChurn(rebuildEveryFrame: true);

		// Bepu's solver leaves a little penetration; it never settles exactly on zero.
		bool stableOk = MathF.Abs(stable) < 0.01f;

		Console.WriteLine($"[probe] gameplay: statics stable - feet at {stable:0.####} " +
			$"{(stableOk ? "OK" : "FELL THROUGH")}; statics rebuilt every frame - feet at " +
			$"{churn:0.####} {(MathF.Abs(churn) < 0.01f ? "(holds too)" : "- COST OF REBUILDING, the character falls through")}");
	}

	/// <summary>Returns feet height after two seconds of walking; zero means on the floor.</summary>
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

			driver.Steer(store, scene, active: true, PhysicsStep);
			scene.Update(PhysicsStep);
			driver.Apply(store, scene);
		}

		return Position(entity).Y;
	}

	/// <summary>Pose-driven ragdoll bones overlap the own capsule and shove it; the one-sided floor stops seeing a capsule pushed below the plane.</summary>
	private static void ProbeOwnBoneOverlap()
	{
		using var scene = new ScenePhysics(new Vector3(0f, -9.81f, 0f));
		BuildGround(scene, wall: false);

		var (store, entity) = Build(Vector3.Zero, radius: 2f, speed: 1f, physical: true);
		var driver = new CharacterMotionDriver();

		// First Steer creates the capsule; bones are built at its feet, like a ragdoll follows the pose.
		driver.Steer(store, scene, active: true, PhysicsStep);

		// Fox-like bone heights above the feet; horizontal capsules, radius = limb thickness.
		float[] heights = { 0.08f, 0.15f, 0.22f, 0.30f, 0.36f, 0.42f, 0.46f };
		var bones = new BodyHandle[heights.Length];
		var orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
		var start = Position(entity);

		for (int i = 0; i < heights.Length; i++)
		{
			var shape = scene.World.AddCapsule(0.06f, 0.12f);
			bones[i] = scene.World.AddDynamic(
				new RigidPose(start + new Vector3(0f, heights[i], 0f), orientation), shape, 0.6f);
		}

		int steps = (int)MathF.Round(5f / PhysicsStep);
		float minFeet = float.MaxValue;

		for (int i = 0; i < steps; i++)
		{
			driver.Steer(store, scene, active: true, PhysicsStep);

			// Bone goals use last frame's feet: animation runs after physics (PrefabSceneViewport.Update).
			var feet = Position(entity);
			for (int b = 0; b < bones.Length; b++)
			{
				var body = scene.World.Simulation.Bodies[bones[b]];
				var goal = feet + new Vector3(0f, heights[b], 0f);
				body.Velocity.Linear = (goal - body.Pose.Position) / PhysicsStep;
				body.Velocity.Angular = Vector3.Zero;
				body.Awake = true;
			}

			scene.Update(PhysicsStep);
			driver.Apply(store, scene);
			minFeet = MathF.Min(minFeet, Position(entity).Y);
		}

		float finalFeet = Position(entity).Y;
		bool held = MathF.Abs(finalFeet) < 0.02f && minFeet > -0.05f;

		Console.WriteLine($"[probe] gameplay: own bones inside the capsule - feet end at {finalFeet:0.####}, " +
			$"minimum {minFeet:0.####}, rescues from under the floor {driver.FloorRescues} " +
			$"{(held ? "OK (holds)" : "FELL THROUGH")}");
	}

	/// <summary>A capsule forced under the one-sided floor must be lifted by exactly one rescue, then stay put.</summary>
	private static void ProbeFloorRescue()
	{
		using var scene = new ScenePhysics(new Vector3(0f, -9.81f, 0f));
		BuildGround(scene, wall: false);

		var (store, entity) = Build(Vector3.Zero, radius: 2f, speed: 1f, physical: true);
		var driver = new CharacterMotionDriver();
		driver.Steer(store, scene, active: true, PhysicsStep);
		scene.Update(PhysicsStep);
		driver.Apply(store, scene);

		// The only dynamic body is the capsule; feet at -0.3 puts its center below the plane.
		var handle = scene.World.Simulation.Bodies.ActiveSet.IndexToHandle[0];
		var body = scene.World.Simulation.Bodies[handle];
		body.Pose.Position = body.Pose.Position - new Vector3(0f, 0.3f, 0f);

		driver.Steer(store, scene, active: true, PhysicsStep);
		int rescuesAfterFirst = driver.FloorRescues;
		scene.Update(PhysicsStep);
		driver.Apply(store, scene);
		float feetAfterFirst = Position(entity).Y;

		for (int i = 0; i < 30; i++)
		{
			driver.Steer(store, scene, active: true, PhysicsStep);
			scene.Update(PhysicsStep);
			driver.Apply(store, scene);
		}

		float feetLater = Position(entity).Y;
		bool ok = rescuesAfterFirst == 1 && MathF.Abs(feetAfterFirst) < 0.02f &&
			MathF.Abs(feetLater) < 0.02f && driver.FloorRescues == 1;

		Console.WriteLine($"[probe] gameplay: capsule under the floor (feet at -0.3) - rescues {rescuesAfterFirst}, " +
			$"feet immediately at {feetAfterFirst:0.####}, half a second later at {feetLater:0.####}, " +
			$"rescues in total {driver.FloorRescues} {(ok ? "OK (lifted by a single rescue)" : "NOT RESCUED")}");
	}

	/// <summary>Statics stream in late: no bodies may exist until the floor arrives, then the walk starts.</summary>
	private static void ProbeLateFloor()
	{
		using var scene = new ScenePhysics(new Vector3(0f, -9.81f, 0f));

		var (store, entity) = Build(Vector3.Zero, radius: 2f, speed: 1f, physical: true);
		var driver = new CharacterMotionDriver();

		int second = (int)MathF.Round(1f / PhysicsStep);
		for (int i = 0; i < second; i++)
		{
			driver.Steer(store, scene, active: true, PhysicsStep);
			scene.Update(PhysicsStep);
			driver.Apply(store, scene);
		}

		int bodiesWithoutFloor = driver.CharacterCount;
		float feetWithoutFloor = Position(entity).Y;

		BuildGround(scene, wall: false);

		for (int i = 0; i < second; i++)
		{
			driver.Steer(store, scene, active: true, PhysicsStep);
			scene.Update(PhysicsStep);
			driver.Apply(store, scene);
		}

		float feetWithFloor = Position(entity).Y;
		float travelled = Vector3.Distance(Position(entity), new Vector3(2f, 0f, 0f));
		bool ok = bodiesWithoutFloor == 0 && MathF.Abs(feetWithoutFloor) < 1e-4f &&
			MathF.Abs(feetWithFloor) < 0.02f && driver.CharacterCount == 1 && travelled > 0.5f;

		Console.WriteLine($"[probe] gameplay: floor arrived a second after Play - without a floor bodies {bodiesWithoutFloor}, " +
			$"feet at {feetWithoutFloor:0.####}; with a floor bodies {driver.CharacterCount}, feet at {feetWithFloor:0.####}, " +
			$"travelled {travelled:0.##} m {(ok ? "OK (waited for the floor)" : "FELL THROUGH/DID NOT WALK")}");
	}

	/// <summary>Built as a MESH, the path scene geometry takes; a box primitive would test unused code.</summary>
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

	/// <summary>Same (a,b,c)+(a,c,d) winding as SampleGroundBuilder; a flipped order would mask a winding-convention bug in the one-sided Bepu mesh.</summary>
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
		// All six faces: a one-sided mesh missing a face is a wall with a hole on the approach side.
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

	/// <summary>Model's own forward rotated by the entity - not +Z, which only matches +Z-facing exports.</summary>
	private static Vector3 Facing(Entity entity) => Vector3.Transform(
		Vector3.Normalize(entity.GetComponent<CircleMoveComponent>().Forward), Rotation(entity));
}
