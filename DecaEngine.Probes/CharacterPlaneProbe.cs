using System;
using System.Collections.Generic;
using System.Numerics;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics;
using Friflo.Engine.ECS;
using DecaEngine.Scene;
using DecaEngine.Animation;
using DecaEngine.Editor;

namespace DecaEngine.Probes;

/// <summary>Full-character probe on a plane (DECA_PROBE_CHARACTER=1).</summary>
public static class CharacterPlaneProbe
{
	private const float Step = 1f / 60f;

	// "Paw inside body" threshold in model units; fox torso is ~8.5 thick.
	private const float InsideThreshold = 7.5f;

	public static void Run(DecaEngine.Graphics.Diligent.DiligentSkinningPass skinning, ModelLoader model,
		string modelPath)
	{
		if (model.Skeleton == null)
		{
			Console.WriteLine("[probe] character: model has no skeleton - nobody to reproduce");
			return;
		}

		using var physics = new ScenePhysics(new Vector3(0f, -9.81f, 0f));
		BuildPlane(physics);

		var store = new EntityStore();
		var fox = store.CreateEntity();
		fox.AddComponent(new EntityName("plane fox"));
		fox.AddComponent(new Position(0f, 0f, -8f));
		fox.AddComponent(new Rotation(0f, 0f, 0f, 1f));
		fox.AddComponent(new Scale3(0.01f, 0.01f, 0.01f));
		fox.AddComponent(new Animator { ClipName = "Walk" });
		fox.AddComponent(new LocomotionComponent { IdleClip = "Survey", WalkSpeed = 1f, RunSpeed = 3f });
		fox.AddComponent(new CharacterBodyComponent { Radius = 0.18f, Height = 0.5f, Mass = 12f });
		fox.AddComponent(new PlayerMoveComponent { WalkSpeed = 1f, RunSpeed = 3f, Forward = -Vector3.UnitZ });
		fox.AddComponent(new FootIkComponent
		{
			AnkleHeight = 1f,
			MaxPelvisDrop = 25f,
			FrontLegs = true,
		});

		using var animation = new AnimationDriver(skinning) { Physics = physics };
		animation.AddInstance(fox.Id, model, -1);
		animation.SetAvatar(fox.Id, HumanoidAvatarAsset.Load(modelPath) ?? HumanoidAutoMap.Build(model.Skeleton));

		var motion = new CharacterMotionDriver();

		// Bone names are hardcoded to the Khronos Fox rig this probe reproduces.
		int hips = model.Skeleton.FindJoint("b_Hip_01");
		int neck = model.Skeleton.FindJoint("b_Neck_04");
		int[] probes =
		[
			model.Skeleton.FindJoint("b_LeftHand_011"),
			model.Skeleton.FindJoint("b_RightHand_08"),
			model.Skeleton.FindJoint("b_LeftForeArm_010"),
			model.Skeleton.FindJoint("b_RightForeArm_07"),
			model.Skeleton.FindJoint("b_LeftFoot02_018"),
			model.Skeleton.FindJoint("b_RightFoot02_022"),
			model.Skeleton.FindJoint("b_LeftFoot01_017"),
			model.Skeleton.FindJoint("b_RightFoot01_021"),
		];

		// Hind chains measure knee inversion; a healthy hock bends toward +Z (muzzle at -Z).
		int[][] hindChains =
		[
			[model.Skeleton.FindJoint("b_LeftLeg01_015"), model.Skeleton.FindJoint("b_LeftLeg02_016"),
				model.Skeleton.FindJoint("b_LeftFoot01_017")],
			[model.Skeleton.FindJoint("b_RightLeg01_019"), model.Skeleton.FindJoint("b_RightLeg02_020"),
				model.Skeleton.FindJoint("b_RightFoot01_021")],
		];

		if (hips < 0 || neck < 0 || Array.IndexOf(probes, -1) >= 0 ||
			Array.IndexOf(hindChains[0], -1) >= 0 || Array.IndexOf(hindChains[1], -1) >= 0)
		{
			Console.WriteLine("[probe] character: fox bones not found - the metric has nothing to hold on to");
			return;
		}

		// Directions are tuned to the wall at x=2.5; phases chain without teleporting.
		(string Name, float Seconds, Func<float, PlayerInput> Input)[] phases =
		[
			("standing", 2f, _ => default),
			("walk", 3f, _ => new PlayerInput { MoveWorld = Vector3.UnitZ }),
			("run", 3f, _ => new PlayerInput { MoveWorld = Vector3.UnitZ, Run = true }),
			("wall rub while walking", 4f, _ => new PlayerInput { MoveWorld = new Vector3(2f, 0f, 1f) }),
			("wall rub while running", 4f, _ => new PlayerInput { MoveWorld = new Vector3(2f, 0f, 1f), Run = true }),
			("jerky gait", 4f, t => new PlayerInput
			{
				MoveWorld = new Vector3(-1f, 0f, 0.3f),
				Run = (int)(t / 0.6f) % 2 == 0,
			}),
		];

		bool anyInside = false;
		bool strideReported = false;

		foreach (var phase in phases)
		{
			// Diagnostic split: separates a bad IK solver from a bad clip.
			if (string.Equals(phase.Name, "run", StringComparison.Ordinal))
			{
				RunPhase(("run without foot IK", 3f, _ => new PlayerInput { MoveWorld = Vector3.UnitZ, Run = true }),
					footIkEnabled: false, lockFeet: false);
				RunPhase(("run without locking", 3f, _ => new PlayerInput { MoveWorld = Vector3.UnitZ, Run = true }),
					footIkEnabled: true, lockFeet: false);
			}

			RunPhase(phase, footIkEnabled: true, lockFeet: true);
		}

		// Partial foot-IK weight is a separate solver path (ozz lerps corrections).
		foreach (float weight in new[] { 0.25f, 0.5f, 0.75f })
		{
			RunPhase(($"weight {weight:0.00}", 1.5f, _ => default), footIkEnabled: true, lockFeet: true,
				weight);
		}

		{
			ref var footIk = ref fox.GetComponent<FootIkComponent>();
			footIk.Enabled = true;
			footIk.LockFeet = true;

			float worstKnee = float.MinValue;
			float worstDistance = float.MaxValue;
			float worstWeight = -1f;

			for (float weight = 0f; weight <= 1.001f; weight += 0.1f)
			{
				footIk.Weight = weight;

				for (int i = 0; i < 30; i++)
				{
					animation.BeginFrame();
					animation.Update(fox, PrefabSceneViewport.ComputeWorldMatrix(fox), 0f);
				}

				if (!animation.TryGetPose(fox.Id, out var models, out _))
				{
					continue;
				}

				var axisA = models[hips].Translation;
				var axisB = models[neck].Translation;

				foreach (int joint in probes)
				{
					float distance = DistanceToSegment(models[joint].Translation, axisA, axisB);
					if (distance < worstDistance)
					{
						worstDistance = distance;
						worstWeight = weight;
					}
				}

				foreach (var chain in hindChains)
				{
					var hip = models[chain[0]].Translation;
					var mid = models[chain[1]].Translation;
					var foot = models[chain[2]].Translation;
					var axis = foot - hip;

					if (axis.LengthSquared() < 1e-6f)
					{
						continue;
					}

					var bend = mid - (hip + axis * (Vector3.Dot(mid - hip, axis) / axis.LengthSquared()));
					float reach = Vector3.Distance(hip, mid) + Vector3.Distance(mid, foot);

					if (bend.Length() > 0.02f * reach)
					{
						worstKnee = MathF.Max(worstKnee, Vector3.Dot(Vector3.Normalize(bend), Vector3.UnitZ));
					}
				}
			}

			footIk.Weight = 1f;

			bool broken = worstDistance < InsideThreshold || worstKnee > 0.3f;
			anyInside |= broken;

			Console.WriteLine($"[probe] character: weight sweep in the editor (dt=0) - worst distance " +
				$"{worstDistance:0.#} units (weight {worstWeight:0.0}), worst bend {worstKnee:0.00} " +
				$"{(broken ? "BREAKS THE POSE" : "OK")}");
		}

		// Same sweep on a step edge: uneven ground makes the IK corrections non-trivial.
		{
			ref var locomotion = ref fox.GetComponent<LocomotionComponent>();
			locomotion.Enabled = false;

			fox.GetComponent<Position>() = new Position(0.05f, 0.35f, -17f);

			ref var footIk = ref fox.GetComponent<FootIkComponent>();
			float worstKnee = float.MinValue;
			float worstKneeWeight = -1f;
			float worstDistance = float.MaxValue;
			float worstWeight = -1f;

			for (float weight = 0f; weight <= 1.001f; weight += 0.1f)
			{
				footIk.Weight = weight;

				for (int i = 0; i < 30; i++)
				{
					animation.BeginFrame();
					animation.Update(fox, PrefabSceneViewport.ComputeWorldMatrix(fox), 0f);
				}

				if (!animation.TryGetPose(fox.Id, out var models, out _))
				{
					continue;
				}

				var axisA = models[hips].Translation;
				var axisB = models[neck].Translation;

				foreach (int joint in probes)
				{
					float distance = DistanceToSegment(models[joint].Translation, axisA, axisB);
					if (distance < worstDistance)
					{
						worstDistance = distance;
						worstWeight = weight;
					}
				}

				float weightKnee = float.MinValue;
				float weightPaw = float.MaxValue;

				foreach (var chain in hindChains)
				{
					var hip = models[chain[0]].Translation;
					var mid = models[chain[1]].Translation;
					var foot = models[chain[2]].Translation;
					var axis = foot - hip;

					if (axis.LengthSquared() < 1e-6f)
					{
						continue;
					}

					var bend = mid - (hip + axis * (Vector3.Dot(mid - hip, axis) / axis.LengthSquared()));
					float reach = Vector3.Distance(hip, mid) + Vector3.Distance(mid, foot);

					if (bend.Length() > 0.02f * reach)
					{
						weightKnee = MathF.Max(weightKnee, Vector3.Dot(Vector3.Normalize(bend), Vector3.UnitZ));
					}
				}

				// Healthy hock->toe direction points at the muzzle (-Z model).
				foreach (var (hock, toe) in new[]
					{ (probes[6], probes[4]), (probes[7], probes[5]) })
				{
					var direction = models[toe].Translation - models[hock].Translation;
					direction.Y = 0f;

					if (direction.LengthSquared() > 1e-4f)
					{
						weightPaw = MathF.Min(weightPaw,
							Vector3.Dot(Vector3.Normalize(direction), -Vector3.UnitZ));
					}
				}

				Console.WriteLine($"[probe] character: step edge, weight {weight:0.0} - bend {weightKnee:0.00}, " +
					$"foot toward the muzzle {weightPaw:0.00}");

				if (weightKnee > worstKnee)
				{
					worstKnee = weightKnee;
					worstKneeWeight = weight;
				}
			}

			footIk.Weight = 1f;

			// Looser than InsideThreshold: a 0.35 step legitimately pulls a forearm to 7.6.
			bool broken = worstDistance < 7f || worstKnee > 0.3f;
			anyInside |= broken;

			Console.WriteLine($"[probe] character: weight sweep on the step edge (dt=0) - worst distance " +
				$"{worstDistance:0.#} units (weight {worstWeight:0.0}), worst bend {worstKnee:0.00} " +
				$"(weight {worstKneeWeight:0.0}) {(broken ? "BREAKS THE POSE" : "OK")}");
		}

		// Entity sunk below the floor: ground sits above the clip's support plane, so a
		// pelvis-only-down IK would tuck the paws instead of floating the pose up.
		{
			fox.GetComponent<Position>() = new Position(0f, -0.12f, -12f);

			ref var footIk = ref fox.GetComponent<FootIkComponent>();
			float worstKnee = float.MinValue;
			float worstDistance = float.MaxValue;

			for (float weight = 0f; weight <= 1.001f; weight += 0.25f)
			{
				footIk.Weight = weight;

				for (int i = 0; i < 30; i++)
				{
					animation.BeginFrame();
					animation.Update(fox, PrefabSceneViewport.ComputeWorldMatrix(fox), 0f);
				}

				if (!animation.TryGetPose(fox.Id, out var models, out _))
				{
					continue;
				}

				var axisA = models[hips].Translation;
				var axisB = models[neck].Translation;

				// Toes excluded: after AlignFeet they legitimately tuck under in deep bends.
				for (int p = 0; p < probes.Length; p++)
				{
					if (p == 4 || p == 5)
					{
						continue;
					}

					worstDistance = MathF.Min(worstDistance,
						DistanceToSegment(models[probes[p]].Translation, axisA, axisB));
				}

				foreach (var chain in hindChains)
				{
					var hip = models[chain[0]].Translation;
					var mid = models[chain[1]].Translation;
					var foot = models[chain[2]].Translation;
					var axis = foot - hip;

					if (axis.LengthSquared() < 1e-6f)
					{
						continue;
					}

					var bend = mid - (hip + axis * (Vector3.Dot(mid - hip, axis) / axis.LengthSquared()));
					float reach = Vector3.Distance(hip, mid) + Vector3.Distance(mid, foot);

					if (bend.Length() > 0.02f * reach)
					{
						worstKnee = MathF.Max(worstKnee, Vector3.Dot(Vector3.Normalize(bend), Vector3.UnitZ));
					}
				}
			}

			footIk.Weight = 1f;

			// Tighter than InsideThreshold: a floated pose measures 22, a tucked one 16.
			bool broken = worstDistance < 19f || worstKnee > 0.3f;
			anyInside |= broken;

			Console.WriteLine($"[probe] character: weight sweep while SUNK (dt=0) - worst distance " +
				$"{worstDistance:0.#} units, worst bend {worstKnee:0.00} " +
				$"{(broken ? "TUCKS THE PAWS" : "FLOATS THE POSE UP OK")}");
		}

		// On a ~15 degree slope, normal alignment must turn the foot by ~15 degrees, no more:
		// a large delta means AlignFeet composes its quaternions in the wrong order.
		{
			fox.GetComponent<Position>() = new Position(0f, 0.47f, -19f);

			ref var footIk = ref fox.GetComponent<FootIkComponent>();
			var pawWith = new Vector3[2];
			var pawWithout = new Vector3[2];

			foreach (bool align in new[] { false, true })
			{
				footIk.AlignToNormal = align;
				footIk.Weight = 1f;

				for (int i = 0; i < 30; i++)
				{
					animation.BeginFrame();
					animation.Update(fox, PrefabSceneViewport.ComputeWorldMatrix(fox), 0f);
				}

				if (!animation.TryGetPose(fox.Id, out var models, out _))
				{
					continue;
				}

				for (int leg = 0; leg < 2; leg++)
				{
					var direction = models[probes[4 + leg]].Translation - models[probes[6 + leg]].Translation;
					var target = align ? pawWith : pawWithout;
					target[leg] = direction.LengthSquared() > 1e-6f
						? Vector3.Normalize(direction)
						: Vector3.Zero;
				}
			}

			footIk.AlignToNormal = true;

			float worstTurn = 0f;
			for (int leg = 0; leg < 2; leg++)
			{
				if (pawWith[leg] != Vector3.Zero && pawWithout[leg] != Vector3.Zero)
				{
					worstTurn = MathF.Max(worstTurn, MathF.Acos(Math.Clamp(
						Vector3.Dot(pawWith[leg], pawWithout[leg]), -1f, 1f)) * 180f / MathF.PI);
				}
			}

			bool alignBroken = worstTurn > 40f;
			anyInside |= alignBroken;

			Console.WriteLine($"[probe] character: normal alignment on a slope - foot turn " +
				$"{worstTurn:0.#}° {(alignBroken ? "TWISTS THE FOOT OUT" : "OK")}");
		}

		// Bind pose and identity rotation are required: after motion phases FaceMotion leaves
		// Rotation turned, and "slope along the body" in world axes would be a lie.
		// Measured bind axes: body along Z, front at -Z, sides along X.
		fox.GetComponent<Animator>().ClipName = string.Empty;
		fox.GetComponent<Rotation>() = new Rotation(0f, 0f, 0f, 1f);

		{
			fox.GetComponent<Position>() = new Position(0f, 0.47f, -23f);

			ref var footIk = ref fox.GetComponent<FootIkComponent>();
			float pitchWithout = 0f;
			float pitchWith = 0f;
			float worstPawGap = float.MinValue;

			foreach (bool tilt in new[] { false, true })
			{
				footIk.AlignBodyToSlope = tilt;

				for (int i = 0; i < 30; i++)
				{
					animation.BeginFrame();
					animation.Update(fox, PrefabSceneViewport.ComputeWorldMatrix(fox), 0f);
				}

				if (!animation.TryGetPose(fox.Id, out var models, out _))
				{
					continue;
				}

				var spine = models[neck].Translation - models[hips].Translation;
				float horizontal = MathF.Sqrt(spine.X * spine.X + spine.Z * spine.Z);
				float pitch = MathF.Atan2(spine.Y, MathF.Max(horizontal, 1e-4f)) * 180f / MathF.PI;

				if (tilt)
				{
					pitchWith = pitch;

					// Slope surface: y(z) = 0.2 + (z + 24) * 0.27.
					var world = PrefabSceneViewport.ComputeWorldMatrix(fox);
					foreach (int paw in new[] { probes[0], probes[1] })
					{
						var pawWorld = Vector3.Transform(models[paw].Translation, world);
						float surface = 0.2f + (pawWorld.Z + 24f) * 0.27f;
						worstPawGap = MathF.Max(worstPawGap, pawWorld.Y - surface);
					}
				}
				else
				{
					pitchWithout = pitch;
				}
			}

			footIk.AlignBodyToSlope = true;

			// Gap is measured at the wrist joint, ~0.08 m above the sole in the clip.
			// The sign matters: the slope rises toward +Z, so the muzzle must go down.
			float pitchDelta = MathF.Abs(pitchWith - pitchWithout);
			bool tiltBroken = pitchDelta < 6f || pitchDelta > 30f || worstPawGap > 0.25f ||
				pitchWith > pitchWithout;
			anyInside |= tiltBroken;

			Console.WriteLine($"[probe] character: body pitch on a slope - {pitchWithout:0.#}° -> " +
				$"{pitchWith:0.#}° (delta {pitchDelta:0.#}°), front paw gap {worstPawGap:0.###} m " +
				$"{(tiltBroken ? "BODY DOES NOT FOLLOW/PAWS IN THE AIR" : "OK")}");
		}

		// Roll on a cross slope (rise along X, body along Z): pelvis twist must match it.
		{
			fox.GetComponent<Position>() = new Position(0f, 0.47f, -19f);

			ref var footIk = ref fox.GetComponent<FootIkComponent>();
			var pelvisRotations = new Quaternion[2];
			var pelvisLeans = new float[2];

			foreach (bool tilt in new[] { false, true })
			{
				footIk.AlignBodyToSlope = tilt;

				for (int i = 0; i < 30; i++)
				{
					animation.BeginFrame();
					animation.Update(fox, PrefabSceneViewport.ComputeWorldMatrix(fox), 0f);
				}

				if (!animation.TryGetPose(fox.Id, out var models, out _))
				{
					continue;
				}

				// Whole-pelvis orientation: this rig's joint lines are not anatomical axes,
				// so any two-point metric lies.
				var m = models[hips];
				var x = Vector3.Normalize(new Vector3(m.M11, m.M12, m.M13));
				var y = Vector3.Normalize(new Vector3(m.M21, m.M22, m.M23));
				var z = Vector3.Normalize(new Vector3(m.M31, m.M32, m.M33));

				pelvisRotations[tilt ? 1 : 0] = Quaternion.CreateFromRotationMatrix(new Matrix4x4(
					x.X, x.Y, x.Z, 0f, y.X, y.Y, y.Z, 0f, z.X, z.Y, z.Z, 0f, 0f, 0f, 0f, 1f));

				// Sign of the roll: slope rises toward +X, so pelvis up must lean to -X.
				pelvisLeans[tilt ? 1 : 0] = Vector3.Dot(y, Vector3.UnitX);
			}

			footIk.AlignBodyToSlope = true;

			// Full delta angle, not a projection: the bind pelvis orientation moves the twist
			// axis off world Z. The slope is across the body, so the whole delta is roll.
			var deltaRotation = Quaternion.Normalize(
				pelvisRotations[1] * Quaternion.Inverse(pelvisRotations[0]));
			float rollDelta = 2f * MathF.Acos(Math.Clamp(MathF.Abs(deltaRotation.W), 0f, 1f)) *
				180f / MathF.PI;

			float leanDelta = pelvisLeans[1] - pelvisLeans[0];
			bool rollBroken = rollDelta < 6f || rollDelta > 30f || leanDelta >= -0.05f;
			anyInside |= rollBroken;

			Console.WriteLine($"[probe] character: roll on a cross slope - pelvis twist {rollDelta:0.#}°, " +
				$"pelvis top lean toward the slope {leanDelta:0.00} " +
				$"{(rollBroken ? "NO ROLL/WRONG WAY" : "OK")}");
		}

		// Partial blend: legs must match exactly (mask leak) and the head must move (dead overlay).
		{
			fox.GetComponent<Position>() = new Position(0f, 0f, -8f);
			fox.GetComponent<Animator>().ClipName = "Walk";

			int head = model.Skeleton.FindJoint("b_Head_05");
			var legsWithout = new Vector3[probes.Length];
			Vector3 headWithout = default;

			for (int pass = 0; pass < 2 && head >= 0; pass++)
			{
				if (pass == 1)
				{
					fox.AddComponent(new OverlayClipComponent
					{
						Enabled = true,
						ClipName = "Survey",
						RootJoint = "b_Neck_04",
						Weight = 1f,
					});
				}

				for (int i = 0; i < 30; i++)
				{
					animation.BeginFrame();
					animation.Update(fox, PrefabSceneViewport.ComputeWorldMatrix(fox), 0f);
				}

				if (!animation.TryGetPose(fox.Id, out var models, out _))
				{
					continue;
				}

				if (pass == 0)
				{
					for (int p = 0; p < probes.Length; p++)
					{
						legsWithout[p] = models[probes[p]].Translation;
					}

					headWithout = models[head].Translation;
				}
				else
				{
					float worstLeg = 0f;
					for (int p = 0; p < probes.Length; p++)
					{
						worstLeg = MathF.Max(worstLeg,
							Vector3.Distance(legsWithout[p], models[probes[p]].Translation));
					}

					float headMoved = Vector3.Distance(headWithout, models[head].Translation);

					bool overlayBroken = worstLeg > 0.01f || headMoved < 0.5f;
					anyInside |= overlayBroken;

					Console.WriteLine($"[probe] character: partial blend (Survey on the neck) - " +
						$"paws moved by {worstLeg:0.####} units, head by {headMoved:0.#} units " +
						$"{(overlayBroken ? "MASK LEAKS/OVERLAY IS DEAD" : "OK")}");
				}
			}
		}

		// Root motion needs a synthetic clip: Fox clips walk in place, so they carry no root track.
		{
			int motionRoot = 0;
			while (model.Skeleton.Parents[motionRoot] >= 0)
			{
				motionRoot = model.Skeleton.Parents[motionRoot];
			}

			var motionTracks = new JointTrack[model.Skeleton.JointCount];
			for (int i = 0; i < motionTracks.Length; i++)
			{
				motionTracks[i] = new JointTrack();
			}

			var rootBind = model.Skeleton.BindLocals[motionRoot].position;
			motionTracks[motionRoot] = new JointTrack
			{
				TranslationTimes = [0f, 2f],
				Translations = [rootBind, rootBind + new Vector3(0f, 0f, 100f)],
			};

			var motionClip = new PreparedAnimation
			{
				Name = "MotionProbe",
				Duration = 2f,
				Tracks = motionTracks,
			};
			model.Animations.Add(motionClip);

			var walker = store.CreateEntity();
			walker.AddComponent(new EntityName("motion fox"));
			walker.AddComponent(new Position(5f, 0f, -8f));
			walker.AddComponent(new Rotation(0f, 0f, 0f, 1f));
			walker.AddComponent(new Scale3(0.01f, 0.01f, 0.01f));
			walker.AddComponent(new Animator { ClipName = "MotionProbe", RootMotion = true });

			animation.AddInstance(walker.Id, model, -1);
			animation.SetAvatar(walker.Id, HumanoidAutoMap.Build(model.Skeleton));

			float maxFrameStep = 0f;
			var previous = walker.GetComponent<Position>().value;

			for (int i = 0; i < 180; i++)
			{
				animation.BeginFrame();
				animation.Update(walker, PrefabSceneViewport.ComputeWorldMatrix(walker), Step);

				var current = walker.GetComponent<Position>().value;
				maxFrameStep = MathF.Max(maxFrameStep, Vector3.Distance(current, previous));
				previous = current;
			}

			float travelled = walker.GetComponent<Position>().value.Z - (-8f);
			float rootDrift = 0f;

			if (animation.TryGetPose(walker.Id, out var motionModels, out _))
			{
				var rootNow = motionModels[motionRoot].Translation;
				rootDrift = new Vector3(rootNow.X - rootBind.X, 0f, rootNow.Z - rootBind.Z).Length();
			}

			// 3 s at 0.5 m/s = 1.5 m; frame step ~8.3 mm. Root drift is in model units.
			bool motionBroken = MathF.Abs(travelled - 1.5f) > 0.03f || maxFrameStep > 0.05f ||
				rootDrift > 1f;
			anyInside |= motionBroken;

			Console.WriteLine($"[probe] character: root motion - path {travelled:0.###} m (expected 1.5), " +
				$"worst frame step {maxFrameStep * 1000f:0.#} mm, root drift in model {rootDrift:0.###} units " +
				$"{(motionBroken ? "PATH/COMPENSATION BROKEN" : "OK")}");
		}

		// Additive round trip: reference@0 plus delta(t) of the same clip must equal Survey@t.
		{
			var basePlus = store.CreateEntity();
			basePlus.AddComponent(new EntityName("additive fox"));
			basePlus.AddComponent(new Position(8f, 0f, -8f));
			basePlus.AddComponent(new Rotation(0f, 0f, 0f, 1f));
			basePlus.AddComponent(new Scale3(0.01f, 0.01f, 0.01f));
			basePlus.AddComponent(new Animator { ClipName = "Survey", Playing = false, Time = 0f });
			basePlus.AddComponent(new AdditiveClipComponent { ClipName = "Survey", Weight = 1f });

			var expected = store.CreateEntity();
			expected.AddComponent(new EntityName("additive fox expected"));
			expected.AddComponent(new Position(11f, 0f, -8f));
			expected.AddComponent(new Rotation(0f, 0f, 0f, 1f));
			expected.AddComponent(new Scale3(0.01f, 0.01f, 0.01f));
			expected.AddComponent(new Animator { ClipName = "Survey", Playing = false, Time = 0.5f });

			animation.AddInstance(basePlus.Id, model, -1);
			animation.AddInstance(expected.Id, model, -1);

			for (int i = 0; i < 30; i++)
			{
				animation.BeginFrame();
				animation.Update(basePlus, PrefabSceneViewport.ComputeWorldMatrix(basePlus), Step);
				animation.Update(expected, PrefabSceneViewport.ComputeWorldMatrix(expected), Step);
			}

			float worstJoint = float.MaxValue;

			if (animation.TryGetPose(basePlus.Id, out var actualModels, out _) &&
				animation.TryGetPose(expected.Id, out var expectedModels, out _))
			{
				worstJoint = 0f;
				for (int joint = 0; joint < model.Skeleton.JointCount; joint++)
				{
					worstJoint = MathF.Max(worstJoint, Vector3.Distance(
						actualModels[joint].Translation, expectedModels[joint].Translation));
				}
			}

			// Tolerance is loose because delta and original are ozz-quantized independently.
			bool additiveBroken = worstJoint > 0.5f;
			anyInside |= additiveBroken;

			Console.WriteLine($"[probe] character: additive (round-trip of the Survey delta) - worst " +
				$"joint mismatch {worstJoint:0.###} units {(additiveBroken ? "DELTA LIES" : "OK")}");
		}

		Console.WriteLine($"[probe] character: TOTAL - {(anyInside ? "THERE ARE STATES WITH LEGS INSIDE THE BODY" : "all states clean OK")}");

		void RunPhase((string Name, float Seconds, Func<float, PlayerInput> Input) phase, bool footIkEnabled,
			bool lockFeet, float weight = 1f)
		{
			ref var footIk = ref fox.GetComponent<FootIkComponent>();
			footIk.Enabled = footIkEnabled;
			footIk.LockFeet = lockFeet;
			footIk.Weight = weight;

			int steps = (int)MathF.Round(phase.Seconds / Step);
			float worst = float.MaxValue;
			float worstAt = 0f;
			float speedAtWorst = 0f;
			int worstJoint = -1;
			float worstKneeDot = float.MinValue;
			var infos = new List<AnimationDriver.CharacterInfo>();

			for (int i = 0; i < steps; i++)
			{
				float t = i * Step;

				motion.Input = phase.Input(t);
				motion.Steer(store, physics, active: true, Step, animation);
				physics.Update(Step);
				motion.Apply(store, physics);

				animation.BeginFrame();
				animation.Update(fox, PrefabSceneViewport.ComputeWorldMatrix(fox), Step);

				// First half second is the transient (crossfade, acceleration); measure steady state.
				if (t < 0.5f || !animation.TryGetPose(fox.Id, out var models, out _))
				{
					continue;
				}

				var axisA = models[hips].Translation;
				var axisB = models[neck].Translation;

				// Knee bend projected on +Z model: healthy is about -1, positive means inverted.
				// A near-straight leg (bend under 2% of reach) is skipped as noise.
				foreach (var chain in hindChains)
				{
					var hip = models[chain[0]].Translation;
					var mid = models[chain[1]].Translation;
					var foot = models[chain[2]].Translation;

					var axis = foot - hip;
					float lengthSquared = axis.LengthSquared();
					if (lengthSquared < 1e-6f)
					{
						continue;
					}

					var bend = mid - (hip + axis * (Vector3.Dot(mid - hip, axis) / lengthSquared));
					float reach = Vector3.Distance(hip, mid) + Vector3.Distance(mid, foot);

					if (bend.Length() > 0.02f * reach)
					{
						worstKneeDot = MathF.Max(worstKneeDot,
							Vector3.Dot(Vector3.Normalize(bend), Vector3.UnitZ));
					}
				}

				foreach (int joint in probes)
				{
					float distance = DistanceToSegment(models[joint].Translation, axisA, axisB);
					if (distance < worst)
					{
						worst = distance;
						worstAt = t;
						worstJoint = joint;

						animation.DescribeCharacters(infos);
						foreach (var info in infos)
						{
							if (info.EntityId == fox.Id)
							{
								speedAtWorst = info.LocoSpeed;
							}
						}
					}
				}
			}

			// +0.3, not zero: a healthy bend can drift sideways and project near zero.
			bool inverted = worstKneeDot > 0.3f;
			bool inside = worst < InsideThreshold || inverted;
			anyInside |= inside;

			string worstName = worstJoint >= 0 ? model.Skeleton.JointNames[worstJoint] : "-";

			if (!strideReported)
			{
				animation.DescribeCharacters(infos);
				foreach (var info in infos)
				{
					if (info.EntityId == fox.Id && info.Locomotion)
					{
						Console.WriteLine($"[probe] character: natural clip speeds - " +
							$"walk {info.LocoWalkStride:0.#} units/s, run {info.LocoRunStride:0.#} units/s " +
							$"(body 1 m/s = {1f / 0.01f:0} units/s)");
						strideReported = true;
					}
				}
			}

			Console.WriteLine($"[probe] character: phase '{phase.Name}' - min paw-to-body distance " +
				$"{worst:0.#} units ({worstName}, t={worstAt:0.0}, speed {speedAtWorst:0.00} m/s), " +
				$"knee bend {worstKneeDot:0.00} " +
				$"{(inverted ? "KNEE INVERTED" : worst < InsideThreshold ? "LEG INSIDE THE BODY" : "OK")}");
		}
	}

	private static void BuildPlane(ScenePhysics physics)
	{
		var vertices = new List<Vector3>();
		var indices = new List<uint>();

		AddQuad(vertices, indices,
			new Vector3(-25f, 0f, -25f), new Vector3(-25f, 0f, 25f),
			new Vector3(25f, 0f, 25f), new Vector3(25f, 0f, -25f));

		// Wall along z, tall enough that step-up cannot climb it.
		AddQuad(vertices, indices,
			new Vector3(2.5f, 0f, -25f), new Vector3(2.5f, 2f, -25f),
			new Vector3(2.5f, 2f, 25f), new Vector3(2.5f, 0f, 25f));

		// Step for the weight sweep: on flat ground foot IK is identity and proves nothing.
		AddQuad(vertices, indices,
			new Vector3(0f, 0.16f, -15.4f), new Vector3(0f, 0.16f, -14.6f),
			new Vector3(0.4f, 0.16f, -14.6f), new Vector3(0.4f, 0.16f, -15.4f));
		AddQuad(vertices, indices,
			new Vector3(0f, 0f, -15.4f), new Vector3(0f, 0.16f, -15.4f),
			new Vector3(0f, 0.16f, -14.6f), new Vector3(0f, 0f, -14.6f));

		// Tall step (0.35): target at the clamp limit, leg stretched nearly straight.
		AddQuad(vertices, indices,
			new Vector3(0f, 0.35f, -17.4f), new Vector3(0f, 0.35f, -16.6f),
			new Vector3(0.4f, 0.35f, -16.6f), new Vector3(0.4f, 0.35f, -17.4f));
		AddQuad(vertices, indices,
			new Vector3(0f, 0f, -17.4f), new Vector3(0f, 0.35f, -17.4f),
			new Vector3(0f, 0.35f, -16.6f), new Vector3(0f, 0f, -16.6f));

		// ~15 degree ramp rising ALONG the skeleton axis (model X); a cross-axis slope is
		// indistinguishable from flat for body pitch.
		AddQuad(vertices, indices,
			new Vector3(-1f, 0.2f, -20f), new Vector3(-1f, 0.2f, -18f),
			new Vector3(1f, 0.74f, -18f), new Vector3(1f, 0.74f, -20f));

		// Cross slope (rise along Z) for body roll.
		AddQuad(vertices, indices,
			new Vector3(-1f, 0.2f, -24f), new Vector3(-1f, 0.74f, -22f),
			new Vector3(1f, 0.74f, -22f), new Vector3(1f, 0.2f, -24f));

		physics.BeginStatics();
		physics.AddStaticMesh(
			System.Runtime.InteropServices.CollectionsMarshal.AsSpan(vertices),
			System.Runtime.InteropServices.CollectionsMarshal.AsSpan(indices));
		physics.EndStatics();
	}

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

	private static float DistanceToSegment(Vector3 point, Vector3 a, Vector3 b)
	{
		var axis = b - a;
		float length = axis.LengthSquared();

		if (length < 1e-8f)
		{
			return Vector3.Distance(point, a);
		}

		float t = Math.Clamp(Vector3.Dot(point - a, axis) / length, 0f, 1f);
		return Vector3.Distance(point, a + axis * t);
	}
}
