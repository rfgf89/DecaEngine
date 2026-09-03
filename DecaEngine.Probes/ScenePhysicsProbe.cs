using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using BepuPhysics;
using DecaEngine.Core;
using DecaEngine.Core.Assets;
using DecaEngine.Core.Prefabs;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics;
using Friflo.Engine.ECS;
using UnsafeCollections.Collections.Unsafe;
using DecaEngine.Scene;
using DecaEngine.Animation;
using DecaEngine.Editor;

namespace DecaEngine.Probes;

/// <summary>Headless run of the real demo scene with physics (DECA_PROBE_SCENE=1); prints the
/// walker trajectory per second. DECA_PROBE_SCENEPATH overrides the prefab, otherwise the scene
/// is generated into a temp folder by the same code as File -> New Project.</summary>
public static class ScenePhysicsProbe
{
	private const float Step = 1f / 60f;

	private static readonly Dictionary<int, Vector3> _hipAtSettle = new();

	private static CharacterMotionState? _lastState;

	// Prints state TRANSITIONS, not per-frame state: a character stuck lying down looks fine
	// on any single frame; only the transition timeline shows it.
	private static void ReportStateChange(Entity character, float time)
	{
		if (character.IsNull || !character.HasComponent<FallRecoverComponent>())
		{
			return;
		}

		var state = character.GetComponent<FallRecoverComponent>().State;

		if (_lastState == state)
		{
			return;
		}

		_lastState = state;
		Console.WriteLine($"[probe] scene: t={time:0.0} s - character {state}");
	}

	public static void Run(IGraphicsApi api, DecaEngine.Graphics.Diligent.DiligentSkinningPass skinning)
	{
		string prefabPath = Environment.GetEnvironmentVariable("DECA_PROBE_SCENEPATH") ?? string.Empty;

		if (string.IsNullOrEmpty(prefabPath))
		{
			string temp = Path.Combine(Path.GetTempPath(), "DecaSceneProbe", "Assets");
			SamplePrefabBuilder.WriteScene(temp, _ => { });
			prefabPath = Path.Combine(temp, "Animation Sample.prefab.json");
		}

		if (!File.Exists(prefabPath))
		{
			Console.WriteLine($"[probe] scene: prefab not found: {prefabPath}");
			return;
		}

		Console.WriteLine($"[probe] scene: {prefabPath}");

		string assetsDirectory = Path.GetDirectoryName(Path.GetFullPath(prefabPath)) ?? ".";

		var store = new EntityStore();
		var root = PrefabAsset.Instantiate(store, prefabPath);

		using var physics = new ScenePhysics(new Vector3(0f, -9.81f, 0f));
		// Models intentionally live until process exit: their CPU vertex copies are referenced
		// by the static BVH and the probe ends right after the run.
		var models = BuildStatics(api, store, root, assetsDirectory, physics, flipWinding: false);

		ProbeStairsAndRamp(physics);
		ProbeWinding(api, store, root, assetsDirectory);
		ProbeReferenceWinding(api);
		ProbeBoneScaleBody(api, store, root, assetsDirectory);
		ProbeEditModeStillness(api, prefabPath, assetsDirectory, skinning);
		ProbeStopRestores(api, prefabPath, assetsDirectory, skinning);
		Simulate(store, root, physics, skinning, models, assetsDirectory);
	}

	// Rays and a player capsule against the demo stairs/ramp: the static mesh is one-sided,
	// so a wrongly-wound face is a hole, not a wall. Geometry constants must match
	// SampleGroundBuilder on purpose - the probe must not share code with the builder.
	private static void ProbeStairsAndRamp(ScenePhysics physics)
	{
		const float stepHeight = 0.16f;
		const float stepDepth = 0.5f;
		const int stepCount = 5;
		const float stairsStart = 1.5f;

		int risersHit = 0;
		for (int i = 0; i < stepCount; i++)
		{
			float x = stairsStart + i * stepDepth;
			float y = (i + 1) * stepHeight - stepHeight * 0.5f;
			var hit = physics.SampleGround(new Vector3(x - 0.3f, y, 0f), Vector3.UnitX, 0.6f);
			if (hit.Hit && MathF.Abs(hit.Position.X - x) < 0.01f)
			{
				risersHit++;
			}
		}

		var side = physics.SampleGround(new Vector3(2.75f, 0.08f, -2.5f), Vector3.UnitZ, 1f);
		bool sideOk = side.Hit && MathF.Abs(side.Position.Z + 2f) < 0.01f;

		var ramp = physics.SampleGround(new Vector3(-4f, 2f, 0f), -Vector3.UnitY, 3f);
		float rampExpected = 0.9f * (4f - 1.5f) / 5f;
		bool rampOk = ramp.Hit && MathF.Abs(ramp.Position.Y - rampExpected) < 0.01f;

		// End-cap ray cast from INSIDE the platform; from outside it would hit the outer wall first.
		var cap = physics.SampleGround(new Vector3(-6.7f, 0.45f, 0f), Vector3.UnitX, 0.4f);
		bool capOk = cap.Hit && MathF.Abs(cap.Position.X + 6.5f) < 0.01f;

		Console.WriteLine($"[probe] scene: stairs by rays - risers from the front {risersHit} of {stepCount} " +
			$"{(risersHit == stepCount ? "OK" : "FULL OF HOLES")}, side skirt {(sideOk ? "holds OK" : "PASSES THROUGH")}; " +
			$"ramp from above y={(ramp.Hit ? ramp.Position.Y : float.NaN):0.###} (expected {rampExpected:0.###}) " +
			$"{(rampOk ? "OK" : "FALLS THROUGH")}, end cap {(capOk ? "holds OK" : "PASSES THROUGH")}");

		// Player capsule up the stairs: 1 m/s along +X from x=0.5; feet below the tread means
		// the capsule is inside a step.
		var store = new EntityStore();
		var driver = new CharacterMotionDriver();

		float Expected(float x) => x < stairsStart ? 0f
			: x >= stairsStart + stepCount * stepDepth ? stepCount * stepHeight
			: (MathF.Floor((x - stairsStart) / stepDepth) + 1f) * stepHeight;

		// Two colliders: vertical capsule and one along the body (CharacterBodyComponent.Length).
		// Besides feet, the NOSE (0.6 m ahead) is measured: with a vertical capsule the model
		// sticks out past the collider and the nose sits inside risers most of the climb.
		foreach (float length in new[] { 0f, 0.8f })
		{
			var entity = store.CreateEntity();
			entity.AddComponent(new EntityName(length > 0f ? "stairs-long" : "stairs"));
			entity.AddComponent(new Position(0.5f, 0f, 0f));
			entity.AddComponent(new Rotation(0f, 0f, 0f, 1f));
			entity.AddComponent(new PlayerMoveComponent { WalkSpeed = 1f, RunSpeed = 3f });
			entity.AddComponent(new CharacterBodyComponent
			{
				Radius = 0.18f,
				Height = 0.5f,
				Mass = 12f,
				StepHeight = 0.25f,
				Length = length,
			});

			float worstSink = 0f;
			float worstNose = 0f;
			int noseInStep = 0;
			int frames = 0;
			int steps = (int)MathF.Round(8f / Step);
			for (int i = 0; i < steps; i++)
			{
				driver.Input = new PlayerInput { MoveWorld = Vector3.UnitX };
				driver.Steer(store, physics, active: true, Step);
				physics.Update(Step);
				driver.Apply(store, physics);

				var feet = entity.GetComponent<Position>().value;

				// Stop mid top landing (x 4..6): past its edge the character legitimately falls,
				// and measuring "sink" there would measure the fall.
				if (feet.X >= 5f)
				{
					break;
				}

				frames++;
				worstSink = MathF.Min(worstSink, feet.Y - Expected(feet.X));
				float nose = feet.Y - Expected(feet.X + 0.6f);
				worstNose = MathF.Min(worstNose, nose);
				if (nose < -0.03f)
				{
					noseInStep++;
				}
			}

			var final = entity.GetComponent<Position>().value;
			driver.Clear(physics);
			entity.DeleteEntity();

			bool climbed = final.X >= 5f && MathF.Abs(final.Y - stepCount * stepHeight) < 0.03f;
			bool noSink = worstSink > -0.03f;
			float noseShare = frames > 0 ? 100f * noseInStep / frames : 0f;
			string kind = length > 0f ? $"along the body {length:0.#}" : "vertical";
			Console.WriteLine($"[probe] scene: stairs with a {kind} capsule - reached x={final.X:0.##}, feet at y={final.Y:0.###} " +
				$"(landing {stepCount * stepHeight:0.##}) {(climbed ? "CLIMBED OK" : "DID NOT CLIMB")}, " +
				$"feet into the step {worstSink:0.###} {(noSink ? "OK" : "INSIDE THE STEP")}, " +
				$"nose in the riser {noseShare:0}% of frames (worst {worstNose:0.###})");
		}

		// Side approach onto the third step along +Z: without side skirts the capsule slides
		// under the tread and ends up inside the step. With skirts it must stop at the skirt.
		var sideEntity = store.CreateEntity();
		sideEntity.AddComponent(new EntityName("stairs-side"));
		sideEntity.AddComponent(new Position(2.75f, 0f, -3f));
		sideEntity.AddComponent(new Rotation(0f, 0f, 0f, 1f));
		sideEntity.AddComponent(new PlayerMoveComponent { WalkSpeed = 1f, RunSpeed = 3f });
		sideEntity.AddComponent(new CharacterBodyComponent
		{
			Radius = 0.18f,
			Height = 0.5f,
			Mass = 12f,
			StepHeight = 0.25f,
		});

		int sideSteps = (int)MathF.Round(3f / Step);
		for (int i = 0; i < sideSteps; i++)
		{
			driver.Input = new PlayerInput { MoveWorld = Vector3.UnitZ };
			driver.Steer(store, physics, active: true, Step);
			physics.Update(Step);
			driver.Apply(store, physics);
		}

		var sideFinal = sideEntity.GetComponent<Position>().value;
		driver.Clear(physics);

		// Inside a step = capsule center (feet + 0.25) below the third tread while z is within
		// the staircase (|z| < 2).
		bool insideStep = MathF.Abs(sideFinal.Z) < 2f && sideFinal.Y + 0.25f < 3f * stepHeight;
		bool blockedBySide = sideFinal.Z < -2f && sideFinal.Z > -2.3f && MathF.Abs(sideFinal.Y) < 0.03f;
		Console.WriteLine($"[probe] scene: stairs from the side - capsule reached z={sideFinal.Z:0.##}, feet at y={sideFinal.Y:0.###} " +
			$"{(blockedBySide ? "SKIRT HOLDS OK" : insideStep ? "INSIDE THE STEP (chest-deep in the stairs)" : "UNEXPECTED")}");
	}

	// The scene must stand still without Play and come alive with it. Verified as a PAIR of
	// runs differing only by the flag - only the difference is meaningful - and on BOTH
	// ragdoll motion and clip time at once.
	private static void ProbeEditModeStillness(IGraphicsApi api, string prefabPath,
		string assetsDirectory, DecaEngine.Graphics.Diligent.DiligentSkinningPass skinning)
	{
		var stopped = RunGated(api, prefabPath, assetsDirectory, skinning, playing: false);
		var playing = RunGated(api, prefabPath, assetsDirectory, skinning, playing: true);

		// Tolerances cover float accumulation only; a stopped scene should be exactly zero.
		bool stillWhenStopped = stopped.RagdollDrop < 0.01f && stopped.ClipTime < 0.01f;
		bool aliveWhenPlaying = playing.RagdollDrop > 0.05f && playing.ClipTime > 0.1f;

		Console.WriteLine($"[probe] scene: without Play - ragdoll dropped by {stopped.RagdollDrop:0.####} m, " +
			$"clip time {stopped.ClipTime:0.###} s {(stillWhenStopped ? "OK (scene is still)" : "SCENE LIVES WITHOUT PLAY")}");
		Console.WriteLine($"[probe] scene: with Play - ragdoll dropped by {playing.RagdollDrop:0.####} m, " +
			$"clip time {playing.ClipTime:0.###} s {(aliveWhenPlaying ? "OK (scene came alive)" : "SCENE DID NOT COME ALIVE ON PLAY")}");
	}

	// Stop must restore the authored state. Two separate rollback mechanisms are exercised:
	// entity transforms come back via the Play Mode snapshot, but ragdoll bodies live outside
	// ECS - so the RAGDOLL ROOT POSE is measured, not the entity position.
	private static void ProbeStopRestores(IGraphicsApi api, string prefabPath, string assetsDirectory,
		DecaEngine.Graphics.Diligent.DiligentSkinningPass skinning)
	{
		var store = new EntityStore();
		var root = PrefabAsset.Instantiate(store, prefabPath);

		using var physics = new ScenePhysics(new Vector3(0f, -9.81f, 0f));
		BuildStatics(api, store, root, assetsDirectory, physics, flipWinding: false, quiet: true);

		var animation = new AnimationDriver(skinning) { Physics = physics };
		var driver = new CharacterMotionDriver();
		var skinned = new List<Entity>();
		Entity ragdollFox = default;

		foreach (var entity in Descendants(root))
		{
			if (!entity.HasComponent<ModelRenderer>())
			{
				continue;
			}

			string path = Path.Combine(assetsDirectory,
				entity.GetComponent<ModelRenderer>().modelRef.Path ?? string.Empty);

			if (!File.Exists(path))
			{
				continue;
			}

			var model = ModelLoader.Load(api, path, new ModelLoadOptions
			{
				VertexShader = new EditorRef("shader/UnlitInstancedVS.hlsl"),
				PixelShader = new EditorRef("shader/UnlitInstancedPS.hlsl"),
				OptimizeMesh = false,
				GenerateLods = false,
			});

			if (model.Skeleton == null)
			{
				continue;
			}

			animation.AddInstance(entity.Id, model, -1);
			animation.SetAvatar(entity.Id, HumanoidAvatarAsset.Load(path) ?? HumanoidAutoMap.Build(model.Skeleton));
			skinned.Add(entity);

			if (entity.HasComponent<RagdollComponent>() && !entity.HasComponent<CircleMoveComponent>() &&
				ragdollFox.IsNull)
			{
				ragdollFox = entity;
			}
		}

		if (ragdollFox.IsNull)
		{
			return;
		}

		void Tick(bool playing)
		{
			physics.Paused = !playing;
			driver.Steer(store, physics, playing, playing ? Step : 0f, animation);
			physics.Update(Step);
			driver.Apply(store, physics);

			animation.BeginFrame();
			foreach (var entity in skinned)
			{
				animation.Update(entity, PrefabSceneViewport.ComputeWorldMatrix(entity), playing ? Step : 0f);
			}
		}

		// Snapshot of the authored state - exactly what Play Mode does on Play.
		var authored = new Dictionary<int, (Vector3 Position, Quaternion Rotation)>();
		foreach (var entity in Descendants(root))
		{
			authored[entity.Id] = (entity.Position.value, entity.Rotation.value);
		}

		Tick(playing: false);
		animation.TryGetRagdollRootWorld(ragdollFox.Id, out var before);

		for (int i = 0; i < 180; i++)
		{
			Tick(playing: true);
		}

		animation.TryGetRagdollRootWorld(ragdollFox.Id, out var during);

		// Stop: component rollback (snapshot) plus clearing state that lives outside ECS.
		foreach (var entity in Descendants(root))
		{
			if (authored.TryGetValue(entity.Id, out var pose))
			{
				entity.Position = new Position(pose.Position.X, pose.Position.Y, pose.Position.Z);
				entity.Rotation = new Rotation(pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W);
			}
		}

		animation.EndPlay();
		Tick(playing: false);

		animation.TryGetRagdollRootWorld(ragdollFox.Id, out var after);

		float fell = before.Y - during.Y;
		float residual = MathF.Abs(after.Y - before.Y);

		// 1 cm tolerance covers matrix decomposition only, not "almost restored".
		bool restored = residual < 0.01f;

		// The fall must be visible, otherwise there is nothing to compare and the check is blind.
		bool fellEnough = fell > 0.1f;

		Console.WriteLine($"[probe] scene: Stop - ragdoll root y={before.Y:0.###} → fell to " +
			$"{during.Y:0.###} (by {fell:0.###} m{(fellEnough ? "" : " - TOO LITTLE, the check is blind")}) " +
			$"→ after Stop {after.Y:0.###}, residual mismatch {residual:0.####} " +
			$"{(restored ? "OK (restored to the authored state)" : "DID NOT RESTORE")}");

		// Gizmo move in edit mode: ragdoll bodies are driven by VELOCITY, and with a zero step
		// velocity moves nothing - the pose must still follow the entity transform.
		const float shift = 3f;
		var moved = ragdollFox.Position.value;
		ragdollFox.Position = new Position(moved.X + shift, moved.Y, moved.Z);

		Tick(playing: false);
		animation.TryGetRagdollRootWorld(ragdollFox.Id, out var afterMove);

		float travelled = afterMove.X - after.X;

		bool follows = MathF.Abs(travelled - shift) < 0.01f;

		Console.WriteLine($"[probe] scene: editor move by {shift} m - ragdoll root travelled " +
			$"{travelled:0.###} m {(follows ? "OK (physics follows the transform)" : "PHYSICS IGNORES THE TRANSFORM")}");
	}

	// Two seconds of the scene with the given Play flag; returns ragdoll drop and clip time.
	private static (float RagdollDrop, float ClipTime) RunGated(IGraphicsApi api, string prefabPath,
		string assetsDirectory, DecaEngine.Graphics.Diligent.DiligentSkinningPass skinning,
		bool playing)
	{
		// A fresh scene instance per run: this check MOVES characters, and a shared store would
		// poison every check that follows.
		var store = new EntityStore();
		var root = PrefabAsset.Instantiate(store, prefabPath);

		using var physics = new ScenePhysics(new Vector3(0f, -9.81f, 0f));
		BuildStatics(api, store, root, assetsDirectory, physics, flipWinding: false, quiet: true);

		// The gate replicates the editor exactly (see PrefabSceneViewport): paused world plus a
		// zero animation step. A probe running its own gate variant would only test itself.
		physics.Paused = !playing;

		var animation = new AnimationDriver(skinning) { Physics = physics };
		var models = new Dictionary<int, ModelLoader>();
		Entity ragdollFox = default;
		Entity walker = default;

		foreach (var entity in Descendants(root))
		{
			if (!entity.HasComponent<ModelRenderer>())
			{
				continue;
			}

			string path = Path.Combine(assetsDirectory,
				entity.GetComponent<ModelRenderer>().modelRef.Path ?? string.Empty);

			if (!File.Exists(path))
			{
				continue;
			}

			var model = ModelLoader.Load(api, path, new ModelLoadOptions
			{
				VertexShader = new EditorRef("shader/UnlitInstancedVS.hlsl"),
				PixelShader = new EditorRef("shader/UnlitInstancedPS.hlsl"),
				OptimizeMesh = false,
				GenerateLods = false,
			});

			if (model.Skeleton == null)
			{
				continue;
			}

			models[entity.Id] = model;
			animation.AddInstance(entity.Id, model, -1);
			animation.SetAvatar(entity.Id, HumanoidAvatarAsset.Load(path) ?? HumanoidAutoMap.Build(model.Skeleton));

			// The ragdoll fox falls with no move script; the walker is the one that has it.
			if (entity.HasComponent<RagdollComponent>() && !entity.HasComponent<CircleMoveComponent>() &&
				ragdollFox.IsNull)
			{
				ragdollFox = entity;
			}

			if (entity.HasComponent<CircleMoveComponent>() && walker.IsNull)
			{
				walker = entity;
			}
		}

		float startY = ragdollFox.IsNull ? 0f : ragdollFox.Position.value.Y;
		var driver = new CharacterMotionDriver();

		for (int i = 0; i < 120; i++)
		{
			driver.Steer(store, physics, playing, playing ? Step : 0f, animation);
			physics.Update(Step);
			driver.Apply(store, physics);

			animation.BeginFrame();
			foreach (var (id, _) in models)
			{
				if (store.TryGetEntityById(id, out var entity))
				{
					animation.Update(entity, PrefabSceneViewport.ComputeWorldMatrix(entity),
						playing ? Step : 0f);
				}
			}
		}

		// Ragdoll drop is measured on the BONE, not the entity transform: the entity stays put
		// during the whole fall.
		float drop = 0f;
		if (!ragdollFox.IsNull && animation.TryGetRagdollRootWorld(ragdollFox.Id, out var rootWorld))
		{
			drop = MathF.Max(startY - rootWorld.Y, 0f);
		}

		float clipTime = walker.IsNull || !walker.HasComponent<Animator>()
			? 0f
			: walker.GetComponent<Animator>().Time;

		return (drop, clipTime);
	}

	// Which debug buffer collider wireframes land in (depth-tested vs on-top), checked by
	// counting vertices: visually "depth-tested" and "not drawn at all" are identical when the
	// character occludes its own collider. Also verifies the global physics flag stays
	// independent from the collider depth flag - merging them floods the screen when scene
	// statics are enabled.
	private static void ProbeColliderOverlay(ScenePhysics physics)
	{
		var draw = new DebugDraw { Enabled = true };

		var onTopOptions = new PhysicsDebugOptions { Colliders = true, CollidersDepthTested = false };
		draw.Clear();
		PhysicsDebugDraw.Draw(draw, physics, onTopOptions);
		int onTopBucket = draw.OnTopCount;
		int depthBucket = draw.DepthTestedCount;

		var depthOptions = new PhysicsDebugOptions { Colliders = true, CollidersDepthTested = true };
		draw.Clear();
		PhysicsDebugDraw.Draw(draw, physics, depthOptions);
		int depthOnTop = draw.OnTopCount;
		int depthDepth = draw.DepthTestedCount;

		var mixedOptions = new PhysicsDebugOptions
		{
			Colliders = true,
			CollidersDepthTested = true,
			OnTop = true,
		};
		draw.Clear();
		PhysicsDebugDraw.Draw(draw, physics, mixedOptions);
		int mixedOnTop = draw.OnTopCount;

		bool byDefaultOnTop = onTopBucket > 0 && depthBucket == 0;
		bool switchable = depthDepth > 0 && depthOnTop == 0;
		bool independent = mixedOnTop == 0;

		Console.WriteLine($"[probe] scene: debug colliders - on top by default: vertices " +
			$"{onTopBucket} on top / {depthBucket} depth-tested {(byDefaultOnTop ? "OK" : "NOT ON TOP")}; " +
			$"checkbox back: {depthOnTop} / {depthDepth} {(switchable ? "OK" : "DOES NOT SWITCH")}; " +
			$"the global physics flag leaves them alone {(independent ? "OK" : "DRAGS THEM ALONG")}");

		ReportCapsuleRadii(physics);
	}

	// Spread of capsule radii in the world: identical radii across a mesh-built ragdoll mean
	// the radii were not derived from the mesh, which a screenshot cannot distinguish from
	// "the model just looks like that".
	private static void ReportCapsuleRadii(ScenePhysics physics)
	{
		var simulation = physics.World.Simulation;
		var radii = new List<float>();

		for (int setIndex = 0; setIndex < simulation.Bodies.Sets.Length; setIndex++)
		{
			ref var set = ref simulation.Bodies.Sets[setIndex];
			if (!set.Allocated)
			{
				continue;
			}

			for (int i = 0; i < set.Count; i++)
			{
				var shape = simulation.Bodies[set.IndexToHandle[i]].Collidable.Shape;
				if (shape.Exists && shape.Type == BepuPhysics.Collidables.Capsule.Id)
				{
					radii.Add(simulation.Shapes.GetShape<BepuPhysics.Collidables.Capsule>(shape.Index).Radius);
				}
			}
		}

		if (radii.Count == 0)
		{
			Console.WriteLine("[probe] scene: no capsules in the world - nothing to measure radius spread on");
			return;
		}

		radii.Sort();

		// Round to 0.1 mm: radii come from vertex averaging, and counting distinct values
		// without rounding would count low-bit noise.
		var distinct = new HashSet<int>();
		foreach (float r in radii)
		{
			distinct.Add((int)MathF.Round(r * 10000f));
		}

		float min = radii[0];
		float max = radii[^1];

		// All world capsules are counted, including the walker body, so the verdict is by the
		// NUMBER of distinct values, not the max/min ratio.
		Console.WriteLine($"[probe] scene: capsule radii - {radii.Count} of them (walker body included), " +
			$"{min:0.####}..{max:0.####} m, distinct values {distinct.Count} " +
			$"{(distinct.Count > 3 ? "OK - follows body part thickness" : "ALL IDENTICAL (BoneRadius override?)")}");
	}

	// A body sized like a ragdoll bone (r=0.02 m) on the scene floor. AddDynamic's default
	// 0.1 m speculative margin is 5x the body: contacts are created far off the surface and
	// the solver jitters or ejects the body. Both settling and late speed are checked -
	// one without the other is not a diagnosis.
	private static void ProbeBoneScaleBody(IGraphicsApi api, EntityStore store, Entity root,
		string assetsDirectory)
	{
		foreach (float margin in new[] { 0.1f, 0.002f })
		{
			using var physics = new ScenePhysics(new Vector3(0f, -9.81f, 0f));
			BuildStatics(api, store, root, assetsDirectory, physics, flipWinding: false, quiet: true);

			const float radius = 0.02f;
			var shape = physics.World.AddCapsule(radius, 0.1f);
			var body = physics.World.AddDynamic(new RigidPose(new Vector3(2f, 0.5f, -4.3f)), shape,
				mass: 0.5f, speculativeMargin: margin);

			float peakSpeed = 0f;
			float lateSpeed = 0f;

			for (int i = 0; i < 240; i++)
			{
				physics.Update(Step);

				float speed = physics.World.Simulation.Bodies[body].Velocity.Linear.Length();
				peakSpeed = MathF.Max(peakSpeed, speed);

				// Last second: the body must already be at rest; speed here is energy the
				// simulation is pumping, not dissipating.
				if (i >= 180)
				{
					lateSpeed = MathF.Max(lateSpeed, speed);
				}
			}

			var pose = physics.World.Simulation.Bodies[body].Pose;
			bool settled = lateSpeed < 0.05f && MathF.Abs(pose.Position.Y) < 0.2f;

			Console.WriteLine($"[probe] scene: bone-sized body (r={radius}), margin {margin} - " +
				$"rested at y={pose.Position.Y:0.####}, peak speed {peakSpeed:0.###}, " +
				$"at the end {lateSpeed:0.####} {(settled ? "OK" : "DID NOT SETTLE")}");
		}
	}

	// Winding check on a FOREIGN canonical model (Khronos Sponza): separates "our ground
	// generator winds wrong" from "the engine<->Bepu winding convention is wrong for any
	// imported geometry".
	private static void ProbeReferenceWinding(IGraphicsApi api)
	{
		string path = Path.Combine(AppContext.BaseDirectory, "EditorAssets", "models", "Sponza.gltf");

		if (!File.Exists(path))
		{
			Console.WriteLine("[probe] scene: Sponza not found - nothing to check the winding convention against");
			return;
		}

		var model = ModelLoader.Load(api, path, new ModelLoadOptions
		{
			VertexShader = new EditorRef("shader/UnlitInstancedVS.hlsl"),
			PixelShader = new EditorRef("shader/UnlitInstancedPS.hlsl"),
			OptimizeMesh = false,
			GenerateLods = false,
		});

		float direct = DropOnModel(model, flipWinding: false);
		float flipped = DropOnModel(model, flipWinding: true);

		// "Fell or not" is useless here: Sponza is a closed interior and a sphere passing the
		// floor's top face still lands on its underside. The correct winding stops HIGHER.
		bool directHigher = direct > flipped + 0.1f;

		Console.WriteLine($"[probe] scene: Sponza (foreign model, convention) - stock winding: " +
			$"y={direct:0.###}, broken: y={flipped:0.###} " +
			$"{(directHigher ? "OK (stock holds on the upper surface)" : "STOCK LETS IT THROUGH THE FLOOR")}");
	}

	private static float DropOnModel(ModelLoader model, bool flipWinding)
	{
		using var physics = new ScenePhysics(new Vector3(0f, -9.81f, 0f));

		var positions = new List<Vector3>();
		var indices = new List<uint>();

		var instanceIndices = new int[model.instances.Count];
		for (int i = 0; i < instanceIndices.Length; i++)
		{
			instanceIndices[i] = i;
		}

		PrefabSceneViewport.AppendModelGeometry(model, instanceIndices, Matrix4x4.Identity,
			positions, indices);

		if (flipWinding)
		{
			for (int i = 0; i + 2 < indices.Count; i += 3)
			{
				(indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
			}
		}

		physics.BeginStatics();
		physics.AddStaticMesh(
			System.Runtime.InteropServices.CollectionsMarshal.AsSpan(positions),
			System.Runtime.InteropServices.CollectionsMarshal.AsSpan(indices));
		physics.EndStatics();

		var body = physics.World.AddDynamic(new RigidPose(new Vector3(0f, 3f, 0f)),
			physics.World.AddSphere(0.25f), mass: 10f);

		for (int i = 0; i < 120; i++)
		{
			physics.Update(Step);
		}

		return physics.World.Simulation.Bodies[body].Pose.Position.Y;
	}

	// Does the scene static hold a body at all, and in which triangle winding. Bepu meshes are
	// ONE-SIDED: a winding mistake means no collision at all, so the winding is determined by
	// experiment (drop a sphere both ways), not by remembering the convention.
	private static void ProbeWinding(IGraphicsApi api, EntityStore store, Entity root, string assetsDirectory)
	{
		float direct = DropSphere(api, store, root, assetsDirectory, flipWinding: false);
		float flipped = DropSphere(api, store, root, assetsDirectory, flipWinding: true);

		// The sphere drops from 2 m onto the floor (y=0) and must rest at its radius.
		const float expected = 0.25f;
		bool directOk = MathF.Abs(direct - expected) < 0.02f;
		bool flippedOk = MathF.Abs(flipped - expected) < 0.02f;

		// The STOCK path must hold: PhysicsWorld.AddTriangleMesh does the one-sided flip
		// itself; "flipped" here is geometry broken on purpose.
		Console.WriteLine($"[probe] scene: triangle winding - stock: sphere at y={direct:0.###} " +
			$"{(directOk ? "HOLDS OK" : "FELL THROUGH")}, deliberately broken: y={flipped:0.###} " +
			$"{(flippedOk ? "holds too - THE FLIP CHANGES NOTHING, the check is blind" : "falls through, as it should")}");
	}

	private static float DropSphere(IGraphicsApi api, EntityStore store, Entity root,
		string assetsDirectory, bool flipWinding)
	{
		using var physics = new ScenePhysics(new Vector3(0f, -9.81f, 0f));
		BuildStatics(api, store, root, assetsDirectory, physics, flipWinding, quiet: true);

		// Drop where the character stands: the floor is flat there and that is the spot in question.
		var body = physics.World.AddDynamic(new RigidPose(new Vector3(2f, 2f, -4.3f)),
			physics.World.AddSphere(0.25f), mass: 10f);

		for (int i = 0; i < 120; i++)
		{
			physics.Update(Step);
		}

		return physics.World.Simulation.Bodies[body].Pose.Position.Y;
	}

	// Scene statics built by the same rule as the editor: every non-skinned model into one
	// mesh with its prefab world matrix; skinned models are excluded (a character must not be
	// its own floor).
	private static Dictionary<int, ModelLoader> BuildStatics(IGraphicsApi api, EntityStore store,
		Entity root, string assetsDirectory, ScenePhysics physics, bool flipWinding, bool quiet = false)
	{
		var loaded = new Dictionary<int, ModelLoader>();
		var positions = new List<Vector3>();
		var indices = new List<uint>();

		physics.BeginStatics();

		foreach (var entity in Descendants(root))
		{
			if (!entity.HasComponent<ModelRenderer>())
			{
				continue;
			}

			string reference = entity.GetComponent<ModelRenderer>().modelRef.Path ?? string.Empty;
			if (reference.Length == 0)
			{
				continue;
			}

			string path = Path.Combine(assetsDirectory, reference);
			if (!File.Exists(path))
			{
				Console.WriteLine($"[probe] scene: model not found: {path}");
				continue;
			}

			var model = ModelLoader.Load(api, path, new ModelLoadOptions
			{
				VertexShader = new EditorRef("shader/UnlitInstancedVS.hlsl"),
				PixelShader = new EditorRef("shader/UnlitInstancedPS.hlsl"),
				OptimizeMesh = false,
				GenerateLods = false,
			});

			loaded[entity.Id] = model;

			if (model.Skeleton != null)
			{
				if (!quiet)
				{
					// World-space bind extent via the same transform composition the renderer
					// uses; distinguishes "renderer lost the entity scale" from "skinning
					// palette exploded" - only the first is visible here.
					positions.Clear();
					indices.Clear();

					var all = new int[model.instances.Count];
					for (int i = 0; i < all.Length; i++)
					{
						all[i] = i;
					}

					PrefabSceneViewport.AppendModelGeometry(model, all,
						PrefabSceneViewport.ComputeWorldMatrix(entity), positions, indices);

					var min = new Vector3(float.MaxValue);
					var max = new Vector3(float.MinValue);
					foreach (var p in positions)
					{
						min = Vector3.Min(min, p);
						max = Vector3.Max(max, p);
					}

					var size = positions.Count > 0 ? max - min : Vector3.Zero;

					Console.WriteLine($"[probe] scene: '{entity.GetComponent<EntityName>().value}' - " +
						$"skinned ({model.Skeleton.JointCount} bones), not added to statics; " +
						$"world extent (bind) {size.X:0.###}×{size.Y:0.###}×{size.Z:0.###}, " +
						$"bottom y={min.Y:0.###}");
				}

				continue;
			}

			var instanceIndices = new int[model.instances.Count];
			for (int i = 0; i < instanceIndices.Length; i++)
			{
				instanceIndices[i] = i;
			}

			positions.Clear();
			indices.Clear();
			PrefabSceneViewport.AppendModelGeometry(model, instanceIndices,
				PrefabSceneViewport.ComputeWorldMatrix(entity), positions, indices);

			if (flipWinding)
			{
				for (int i = 0; i + 2 < indices.Count; i += 3)
				{
					(indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
				}
			}

			physics.AddStaticMesh(
				System.Runtime.InteropServices.CollectionsMarshal.AsSpan(positions),
				System.Runtime.InteropServices.CollectionsMarshal.AsSpan(indices));

			if (!quiet)
			{
				Console.WriteLine($"[probe] scene: '{entity.GetComponent<EntityName>().value}' - " +
					$"{indices.Count / 3} triangles into statics");
			}
		}

		physics.EndStatics();

		if (!quiet)
		{
			Console.WriteLine($"[probe] scene: statics built, triangles {physics.StaticTriangleCount}");
		}

		return loaded;
	}

	// Steps the scene with the editor's frame order (Steer -> physics step -> Apply ->
	// AnimationDriver.Update per character) using the real AnimationDriver, and prints the
	// walker trajectory plus the DEFORMED mesh extent of every character (CPU skinning with
	// the same palette the GPU would get).
	private static void Simulate(EntityStore store, Entity root, ScenePhysics physics,
		DecaEngine.Graphics.Diligent.DiligentSkinningPass skinning,
		Dictionary<int, ModelLoader> models, string assetsDirectory)
	{
		var driver = new CharacterMotionDriver();

		// Palette offset -1: instances are not registered in the batch renderer, nothing to upload.
		var animation = new AnimationDriver(skinning) { Physics = physics };
		var skinnedEntities = new List<Entity>();
		var hipJointOf = new Dictionary<int, int>();

		foreach (var entity in Descendants(root))
		{
			if (models.TryGetValue(entity.Id, out var model) && model.Skeleton != null)
			{
				animation.AddInstance(entity.Id, model, -1);

				string modelPath = Path.Combine(assetsDirectory,
					entity.GetComponent<ModelRenderer>().modelRef.Path ?? string.Empty);
				var avatar = HumanoidAvatarAsset.Load(modelPath) ?? HumanoidAutoMap.Build(model.Skeleton);
				animation.SetAvatar(entity.Id, avatar);

				hipJointOf[entity.Id] = avatar.Resolve(model.Skeleton)[(int)HumanoidBone.Hips];
				skinnedEntities.Add(entity);
			}
		}

		Entity character = default;
		var move = default(CircleMoveComponent);

		store.Query<CircleMoveComponent, CharacterBodyComponent>().ForEachEntity(
			(ref CircleMoveComponent m, ref CharacterBodyComponent body, Entity entity) =>
		{
			character = entity;
			move = m;
		});

		if (character.IsNull)
		{
			Console.WriteLine("[probe] scene: no character with a move script and a Character Body in the scene");
			return;
		}

		var start = character.Position.value;
		Console.WriteLine($"[probe] scene: character '{character.GetComponent<EntityName>().value}' " +
			$"from {start}, circle R={move.Radius} around {move.Center}, {move.Speed} units/s");

		// 14 s covers one fall cycle; leaks across repeated cycles need a long run - see the
		// body counter in trajectory lines. DECA_PROBE_SCENESECONDS overrides.
		float seconds = float.TryParse(
			Environment.GetEnvironmentVariable("DECA_PROBE_SCENESECONDS"),
			System.Globalization.NumberStyles.Float,
			System.Globalization.CultureInfo.InvariantCulture, out float custom) && custom > 0f
			? custom
			: 14f;
		int steps = (int)MathF.Round(seconds / Step);

		float worstRadius = 0f;
		float lowest = start.Y;
		float highest = start.Y;
		float highestAt = 0f;
		float movingSeconds = 0f;
		float turned = 0f;
		float previousAngle = CircleMotion.AngleOf(move, start);
		bool walkerIkSeen = false;
		float? walkerLowY = null;
		float walkerWalkWeight = -1f;
		float walkerLyingIdleWeight = -1f;
		float playerIdleWeight = -1f;
		float reactionPeak = 0f;
		float reactionAfter = -1f;
		float parkedPurity = -1f;
		float parkedSpeed = -1f;
		var recoveryPrevHip = Vector3.Zero;
		var recoveryPrevState = CharacterMotionState.Moving;
		bool recoveryPrevValid = false;
		float worstRecoveryJump = -1f;
		float worstFrameJump = 0f;
		float worstFrameJumpAt = 0f;
		var characterInfos = new List<AnimationDriver.CharacterInfo>();

		// The player fox gets no input in a headless run: its locomotion must idle - the
		// walk/idle pair is checked on one scene with one mechanism.
		Entity player = default;
		store.Query<PlayerMoveComponent, CharacterBodyComponent>().ForEachEntity(
			(ref PlayerMoveComponent _, ref CharacterBodyComponent _, Entity entity) => player = entity);

		// Ray over the mound crest BEFORE the run distinguishes "no geometry", "geometry
		// inside-out" and "capsule failed the slope". The MIRRORED-side ray catches the
		// RH->LH import trap: only the mound made the platform z-asymmetric.
		var crest = physics.SampleGround(new Vector3(0f, 1f, -2.3f), -Vector3.UnitY, 2f);
		var mirrored = physics.SampleGround(new Vector3(0f, 1f, 2.3f), -Vector3.UnitY, 2f);
		Console.WriteLine($"[probe] scene: mound - ray over the crest (z=-2.3) " +
			$"{(crest.Hit ? $"y={crest.Position.Y:0.###}" : "MISSED")}, " +
			$"on the mirrored side (z=+2.3) {(mirrored.Hit ? $"y={mirrored.Position.Y:0.###}" : "MISSED")}");

		// DECA_PROBE_SCENEINPUT=1: synthetic player input (rotating heading, alternating run)
		// to exercise wall/mound/character collisions a no-input run never produces.
		bool syntheticInput = Environment.GetEnvironmentVariable("DECA_PROBE_SCENEINPUT") == "1";

		for (int i = 0; i < steps; i++)
		{
			if (syntheticInput)
			{
				float now = (i + 1) * Step;
				float heading = now * 0.7f;

				driver.Input = new PlayerInput
				{
					MoveWorld = new Vector3(MathF.Cos(heading), 0f, MathF.Sin(heading)),
					Run = ((int)(now / 10f) & 1) == 0,
				};
			}

			// From t=4 to t=10 the player runs diagonally into the right wall and slides at
			// ~1.7 m/s, between Walk and Run: the gait hysteresis must settle on a PURE gait.
			// Direction (2,0,1) keeps the slide short of the open platform edge; input is
			// released at t=10 so the player doesn't run off the world by t=14.
			if (!syntheticInput && i + 1 > 240 && i + 1 <= 600)
			{
				driver.Input = new PlayerInput { MoveWorld = new Vector3(2f, 0f, 1f), Run = true };
			}

			// Frame order matches PrefabSceneViewport: physics strictly before animation -
			// foot IK rays probe this frame's world and the ragdoll reads integrated bodies.
			driver.Steer(store, physics, active: true, Step, animation);
			physics.Update(Step);
			driver.Apply(store, physics);

			animation.BeginFrame();
			foreach (var skinnedEntity in skinnedEntities)
			{
				animation.Update(skinnedEntity, PrefabSceneViewport.ComputeWorldMatrix(skinnedEntity), Step);
			}

			ReportStateChange(character, (i + 1) * Step);
			LegSnapshotProbe.Poll(physics, animation, skinnedEntities, models, (i + 1) * Step, Step);

			// Finiteness check EVERY step: a body at infinity gives a NaN bound and Bepu's
			// broad phase dies with a stack overflow that names no culprit.
			foreach (var skinnedEntity in skinnedEntities)
			{
				var entityPos = skinnedEntity.Position.value;
				bool finite = float.IsFinite(entityPos.X) && float.IsFinite(entityPos.Y) &&
					float.IsFinite(entityPos.Z);

				if (finite && animation.TryGetRagdollRootWorld(skinnedEntity.Id, out var ragdollRoot))
				{
					finite = float.IsFinite(ragdollRoot.X) && float.IsFinite(ragdollRoot.Y) &&
						float.IsFinite(ragdollRoot.Z);
				}

				if (!finite)
				{
					Console.WriteLine($"[probe] scene: NON-FINITE POSE on " +
						$"'{skinnedEntity.GetComponent<EntityName>().value}' at t={(i + 1) * Step:0.00} s - " +
						$"entity {entityPos}, the world cannot go on");
					return;
				}
			}

			// WALKING time, not run time: a lying/recovering character doesn't circle, and an
			// expectation based on total time only passes with some external push.
			if (!character.HasComponent<FallRecoverComponent>() ||
				character.GetComponent<FallRecoverComponent>().State == CharacterMotionState.Moving)
			{
				movingSeconds += Step;
			}

			// Recovery continuity: the hip world position on the Falling->Recovering frame must
			// stay at the lying spot - the entity is re-based to the ragdoll that frame and the
			// lying-pose snapshot must be rebased too (see BeginRecovery).
			if (character.HasComponent<FallRecoverComponent>() &&
				hipJointOf.TryGetValue(character.Id, out int walkerHip) && walkerHip >= 0 &&
				animation.TryGetPose(character.Id, out var walkerPose, out _))
			{
				var hipWorld = Vector3.Transform(walkerPose[walkerHip].Translation,
					PrefabSceneViewport.ComputeWorldMatrix(character));
				var walkerState = character.GetComponent<FallRecoverComponent>().State;

				if (recoveryPrevValid)
				{
					float jump = Vector3.Distance(hipWorld, recoveryPrevHip);

					if (walkerState == CharacterMotionState.Recovering &&
						recoveryPrevState == CharacterMotionState.Falling)
					{
						worstRecoveryJump = MathF.Max(worstRecoveryJump, jump);
					}

					// Worst per-frame jump over the WHOLE run catches any pose teleport;
					// honest velocities move centimeters per frame.
					if (jump > worstFrameJump)
					{
						worstFrameJump = jump;
						worstFrameJumpAt = (i + 1) * Step;
					}
				}

				recoveryPrevHip = hipWorld;
				recoveryPrevState = walkerState;
				recoveryPrevValid = true;
			}

			var position = character.Position.value;

			float dx = position.X - move.Center.X;
			float dz = position.Z - move.Center.Z;
			float distance = MathF.Sqrt(dx * dx + dz * dz);

			worstRadius = MathF.Max(worstRadius, MathF.Abs(distance - move.Radius));
			lowest = MathF.Min(lowest, position.Y);

			if (position.Y > highest)
			{
				highest = position.Y;
				highestAt = (i + 1) * Step;
			}

			// Foot IK and locomotion weights are sampled ON THE MOUND CREST (t=3), not at the
			// end of the run where the character is already a ragdoll.
			if (i + 1 == 180)
			{
				animation.DescribeCharacters(characterInfos);
				foreach (var info in characterInfos)
				{
					if (info.EntityId == character.Id)
					{
						// AT LEAST two legs: a quadruped walker has four, exact equality is stale.
					walkerIkSeen = info.LegCount >= 2 && info.IkApplied;
						walkerWalkWeight = info.Locomotion ? info.LocoWalkWeight : -1f;

						// Informational: zero phase on BOTH clips may mean lost humanoid mapping
						// (alignment silently dead) but is also legal for authored clips.
						Console.WriteLine($"[probe] scene: locomotion - gait phases walk=" +
							$"{info.LocoWalkPhaseOffset:0.00}, run={info.LocoRunPhaseOffset:0.00}");
					}
					else if (!player.IsNull && info.EntityId == player.Id)
					{
						playerIdleWeight = info.Locomotion ? info.LocoIdleWeight : -1f;
					}
				}
			}

			// Hit reaction: push at t=3.5. Peak is the MAX over the reaction window, not a
			// fixed-frame slice - the envelope shape is an implementation detail; "after" is
			// sampled at t=4.6 when the envelope must have expired.
			if (i + 1 == 210)
			{
				animation.TriggerHitReaction(character.Id, new Vector3(0f, 0.8f, 2.2f));
			}

			if (i + 1 > 210 && i + 1 <= 270)
			{
				animation.DescribeCharacters(characterInfos);
				foreach (var info in characterInfos)
				{
					if (info.EntityId == character.Id)
					{
						reactionPeak = MathF.Max(reactionPeak, info.ReactionDeviation);
					}
				}
			}

			if (i + 1 == 276)
			{
				animation.DescribeCharacters(characterInfos);
				foreach (var info in characterInfos)
				{
					if (info.EntityId == character.Id)
					{
						reactionAfter = info.ReactionDeviation;
					}
				}
			}

			// Gait-parking slice at t=10: the player has rubbed the wall for seconds, all
			// crossfades are over, the weight must be pure.
			if (i + 1 == 600 && !player.IsNull)
			{
				animation.DescribeCharacters(characterInfos);
				foreach (var info in characterInfos)
				{
					if (info.EntityId == player.Id && info.Locomotion)
					{
						parkedPurity = MathF.Max(info.LocoWalkWeight, info.LocoRunWeight);
						parkedSpeed = info.LocoSpeed;
					}
				}
			}

			// Lying slice by STATE, not by second: when recovery starts depends on how the
			// ragdoll settled, so a fixed time lands anywhere in the cycle.
			if (character.HasComponent<FallRecoverComponent>() &&
				character.GetComponent<FallRecoverComponent>() is { State: CharacterMotionState.Falling, StateTime: > 1f })
			{
				animation.DescribeCharacters(characterInfos);
				foreach (var info in characterInfos)
				{
					if (info.EntityId == character.Id)
					{
						walkerLyingIdleWeight = info.Locomotion ? info.LocoIdleWeight : -1f;
					}
				}
			}

			float angle = MathF.Atan2(dz, dx);
			turned += CircleMotion.Wrap(angle - previousAngle);
			previousAngle = angle;

			// One line per second: final numbers can't distinguish "recovered at t=4" from
			// "never walked at all".
			if ((i + 1) % 60 == 0)
			{
				Console.WriteLine($"[probe] scene: t={(i + 1) * Step:0.0} s  " +
					$"pos=({position.X:0.00}, {position.Y:0.000}, {position.Z:0.00})  " +
					$"radius={distance:0.000}  laps={turned / MathF.Tau:0.000}  " +
					$"bodies={physics.BodyCount}");
			}

			// Collider debug on the first frame WITH character bodies: before the first
			// Steer/Update the world holds only statics and the check would see nothing.
			if (i == 0)
			{
				ProbeColliderOverlay(physics);
			}

			// Deformed extent at the START (first physics frame) and at the END: a tear from
			// the very first step and an accumulating tear are diagnosed differently.
			if (i == 0 || i == steps - 1)
			{
				foreach (var skinnedEntity in skinnedEntities)
				{
					float? low = ReportDeformedExtents(animation, models[skinnedEntity.Id], skinnedEntity,
						(i + 1) * Step);

					// Walker's lowest point on the last frame catches "sinking into the floor"
					// that lives in the POSE, invisible to any body metric.
					if (i == steps - 1 && skinnedEntity.Id == character.Id)
					{
						walkerLowY = low;
					}
				}
			}

			// Hip of each ragdoll character for the rest metric: sampled at 6 s (settled) and
			// at the end; the difference is the drift. By STEP NUMBER, not time: accumulated
			// (i+1)*Step never equals 6.0 exactly.
			if (i + 1 == 360 || i + 1 == steps)
			{
				foreach (var skinnedEntity in skinnedEntities)
				{
					// Rest metric only for characters that SHOULD be lying; for walkers/players
					// drift equals distance walked.
					if (skinnedEntity.HasComponent<CircleMoveComponent>() ||
						skinnedEntity.HasComponent<PlayerMoveComponent>() ||
						!skinnedEntity.HasComponent<RagdollComponent>() ||
						!hipJointOf.TryGetValue(skinnedEntity.Id, out int hip) || hip < 0 ||
						!animation.TryGetPose(skinnedEntity.Id, out var modelMatrices, out _))
					{
						continue;
					}

					var world = PrefabSceneViewport.ComputeWorldMatrix(skinnedEntity);
					var hipWorld = (modelMatrices[hip] * world).Translation;

					if (i + 1 == 360)
					{
						_hipAtSettle[skinnedEntity.Id] = hipWorld;
					}
					else
					{
						// A ragdoll at rest must not creep: hip drift is the "rolls along the
						// floor" failure no palette extent or single-frame velocity catches.
						var settled = _hipAtSettle.TryGetValue(skinnedEntity.Id, out var s) ? s : hipWorld;
						float drift = new Vector2(hipWorld.X - settled.X, hipWorld.Z - settled.Z).Length();

						Console.WriteLine($"[probe] scene: '{skinnedEntity.GetComponent<EntityName>().value}' " +
							$"rest - hip at y={hipWorld.Y:0.###}, drift over 6..14 s {drift:0.###} m " +
							$"{(drift < 0.15f ? "LIES STILL OK" : "CREEPS/ROLLS")}" +
							$"{(hipWorld.Y < -0.05f ? " UNDER THE FLOOR" : "")}");
					}
				}
			}
		}

		float expectedTurns = movingSeconds * move.Speed / (MathF.Tau * move.Radius);

		bool onGround = MathF.Abs(lowest - start.Y) < 0.05f;
		bool circleOk = worstRadius < 0.1f;
		bool progressOk = MathF.Abs(turned / MathF.Tau - expectedTurns) < 0.1f;

		// Mound on the circle path (height 0.12): rising by its height proves the capsule TOOK
		// the slope. The upper bound is deliberately tight - it catches launching off broken
		// crest geometry, which a generous bound would grade as "took the mound especially well".
		bool moundOk = highest - start.Y > 0.08f && highest - start.Y < 0.18f;

		Console.WriteLine($"[probe] scene: TOTAL - laps {turned / MathF.Tau:0.000} " +
			$"(expected {expectedTurns:0.000} over {movingSeconds:0.0} s of walking) " +
			$"{(progressOk ? "OK" : "DID NOT GET THERE")}, " +
			$"worst radius deviation {worstRadius:0.####} {(circleOk ? "OK" : "LEFT THE CIRCLE")}, " +
			$"lowest descent {lowest - start.Y:0.####} {(onGround ? "OK" : "FELL THROUGH")}, " +
			$"highest rise {highest - start.Y:0.###} (t={highestAt:0.00}) " +
			$"{(moundOk ? "TOOK THE MOUND OK" : "DID NOT TAKE THE MOUND")}, " +
			$"foot IK on the crest {(walkerIkSeen ? "applied OK" : "NOT APPLIED")}, " +
			// Lower bound matches the clip's own natural sag (-0.036 without foot IK): demanding
			// more from IK than from the clip would flag the animation, not a failure.
			$"paws at the end y={walkerLowY:0.###} " +
			$"{(walkerLowY is > -0.06f and < 0.05f ? "ON THE FLOOR OK" : "SINKS IN/FLOATS")}");

		// Threshold is normal per-frame motion of a falling body (centimeters); a missing
		// snapshot rebase jumps by the whole ragdoll travel. -1 = no recovery happened.
		if (worstRecoveryJump >= 0f)
		{
			bool recoveryOk = worstRecoveryJump < 0.1f && worstFrameJump < 0.15f;
			Console.WriteLine($"[probe] scene: pose continuity - hip jump on the recovery start frame " +
				$"{worstRecoveryJump:0.###} m, worst over the run {worstFrameJump:0.###} m " +
				$"(t={worstFrameJumpAt:0.00}) {(recoveryOk ? "NO TELEPORTS OK" : "POSE TELEPORT PRESENT")}");
		}

		// Locomotion verified in PAIRS on one scene (walking-in-step AND idle-in-stance): a
		// single weight alone proves nothing - a stuck 1.0 on walk looks like a working blend
		// until the first stop.
		bool locoOk = walkerWalkWeight > 0.8f && walkerLyingIdleWeight > 0.8f && playerIdleWeight > 0.8f;

		Console.WriteLine($"[probe] scene: locomotion - walker on the crest walk={walkerWalkWeight:0.00}, " +
			$"same one lying idle={walkerLyingIdleWeight:0.00}, player with no input idle={playerIdleWeight:0.00} " +
			$"{(locoOk ? "OK" : "WRONG WEIGHTS")}");

		// Peak proves physics actually moved bones; "after" proves the reaction ENDED - a
		// stuck envelope is visually indistinguishable from a finished one.
		bool reactionOk = reactionPeak > 1.5f && reactionAfter >= 0f && reactionAfter < 0.2f;

		Console.WriteLine($"[probe] scene: hit reaction - peak pose deviation {reactionPeak:0.##} " +
			$"model units, after decay {reactionAfter:0.###} {(reactionOk ? "OK" : "NO SWING/STUCK")}");

		// Speed must actually sit between Walk and Run, otherwise this tests free running,
		// whose weight is pure even with broken gait switching.
		bool parkedOk = parkedPurity > 0.9f && parkedSpeed > 1.2f && parkedSpeed < 2.7f;

		Console.WriteLine($"[probe] scene: gait parking - player at the wall {parkedSpeed:0.00} m/s, " +
			$"gait purity {parkedPurity:0.00} {(parkedOk ? "PURE OK" : "HALF-BLEND/WRONG SPEED")}");
	}

	// CPU-skins vertices with the same palette the GPU would get and compares the deformed
	// extent to the bind extent: a healthy palette stays near 1x, a palette missing the
	// inverse bind matrix blows up by orders of magnitude.
	private static unsafe float? ReportDeformedExtents(AnimationDriver animation, ModelLoader model,
		Entity entity, float time)
	{
		if (!animation.TryGetPose(entity.Id, out _, out var skin))
		{
			return null;
		}

		var world = PrefabSceneViewport.ComputeWorldMatrix(entity);
		var min = new Vector3(float.MaxValue);
		var max = new Vector3(float.MinValue);
		var bindMin = min;
		var bindMax = max;
		float worldLowY = float.MaxValue;
		bool finite = true;
		int counted = 0;

		for (int meshIndex = 0; meshIndex < model.Meshes.Count; meshIndex++)
		{
			var skinStream = meshIndex < model.MeshSkin.Count ? model.MeshSkin[meshIndex] : null;
			var mesh = model.Meshes[meshIndex];

			if (skinStream == null || mesh.VertexData == null)
			{
				continue;
			}

			int vertexCount = Math.Min(UnsafeArray.GetLength(mesh.VertexData), skinStream.Length);
			var vertices = new ReadOnlySpan<Vertex>(UnsafeArray.GetPtr<Vertex>(mesh.VertexData, 0), vertexCount);

			for (int v = 0; v < vertexCount; v++)
			{
				var bind = vertices[v].Position;
				bindMin = Vector3.Min(bindMin, bind);
				bindMax = Vector3.Max(bindMax, bind);

				var s = skinStream[v];
				if (s.IsUnskinned)
				{
					continue;
				}

				// Same convolution as SkinningCS.hlsl: sum of weight * (skin[joint] * bindPos).
				var deformed =
					Vector3.Transform(bind, skin[s.J0]) * (s.W0 / SkinVertex.WeightScale) +
					Vector3.Transform(bind, skin[s.J1]) * (s.W1 / SkinVertex.WeightScale) +
					Vector3.Transform(bind, skin[s.J2]) * (s.W2 / SkinVertex.WeightScale) +
					Vector3.Transform(bind, skin[s.J3]) * (s.W3 / SkinVertex.WeightScale);

				finite &= float.IsFinite(deformed.X) && float.IsFinite(deformed.Y) && float.IsFinite(deformed.Z);
				min = Vector3.Min(min, deformed);
				max = Vector3.Max(max, deformed);
				worldLowY = MathF.Min(worldLowY, Vector3.Transform(deformed, world).Y);
				counted++;
			}
		}

		if (counted == 0)
		{
			return null;
		}

		float bindExtent = (bindMax - bindMin).Length();
		float deformedExtent = (max - min).Length();
		float ratio = bindExtent > 1e-6f ? deformedExtent / bindExtent : 0f;

		// 3x is a generous ceiling: a live pose changes extent far less, a broken palette
		// gives tens.
		Console.WriteLine($"[probe] scene: '{entity.GetComponent<EntityName>().value}' t={time:0.0} s - " +
			$"deformed extent {deformedExtent:0.##} (bind {bindExtent:0.##}, ×{ratio:0.##}), " +
			$"world bottom y={worldLowY:0.###} " +
			$"{(!finite ? "NAN IN THE PALETTE" : ratio < 3f ? "OK" : "PALETTE BLOWN APART")}");

		return worldLowY;
	}

	private static IEnumerable<Entity> Descendants(Entity entity)
	{
		yield return entity;

		foreach (var child in entity.ChildEntities)
		{
			foreach (var descendant in Descendants(child))
			{
				yield return descendant;
			}
		}
	}
}
