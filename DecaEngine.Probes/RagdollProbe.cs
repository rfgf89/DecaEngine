using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using DecaEngine.Graphics;
using DecaEngine.Physics;
using DecaEngine.Animation;

namespace DecaEngine.Probes;

/// <summary>Ragdoll checks on a real rig: kinematic tracking, joint limits and dynamic stability.</summary>
public static class RagdollProbe
{
	public static void Run(PreparedSkeleton skeleton, OzzPose pose, Matrix4x4[] models)
	{
		var description = BuildDescription(skeleton);
		if (description.Count < 4)
		{
			Console.WriteLine("[probe] ragdoll: rig not recognised - skipped");
			return;
		}

		if (!pose.LocalToModel() || !pose.ReadModelMatrices(models))
		{
			return;
		}

		using var world = new PhysicsWorld(new Vector3(0f, -9.81f, 0f));

		var floor = world.AddBox(new Vector3(400f, 4f, 400f));
		world.AddStatic(new RigidPose(new Vector3(0f, -2f, 0f)), floor);

		// Joint world matrices equal model ones here: the entity transform is identity.
		var jointWorld = (Matrix4x4[])models.Clone();
		MarkHinges(description, skeleton, jointWorld);
		var ragdoll = Ragdoll.Build(world, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(description),
			jointWorld);

		ProbeKinematicTracking(world, ragdoll, jointWorld, models);
		ProbeHighFpsTracking(world, ragdoll, jointWorld, models);
		ProbeDynamicFall(world, ragdoll, jointWorld, skeleton);
		ProbeKneeHinge(skeleton, models);
		ProbeSceneScale(skeleton, models);
	}

	// Must mirror AnimationDriver.MarkHingeBones, else this probes a different ragdoll.
	private static void MarkHinges(List<RagdollBoneDesc> description, PreparedSkeleton skeleton,
		Matrix4x4[] jointWorld)
	{
		foreach (string name in new[]
			{ "b_LeftLeg02_016", "b_RightLeg02_020", "b_LeftForeArm_010", "b_RightForeArm_07" })
		{
			int joint = skeleton.FindJoint(name);
			if (joint < 0)
			{
				continue;
			}

			for (int i = 0; i < description.Count; i++)
			{
				var bone = description[i];
				if (bone.Joint != joint || bone.Parent < 0 || bone.ChildJoint < 0)
				{
					continue;
				}

				Ragdoll.MarkHinge(ref bone,
					jointWorld[description[bone.Parent].Joint].Translation,
					jointWorld[bone.Joint].Translation,
					jointWorld[bone.ChildJoint].Translation);

				description[i] = bone;
				break;
			}
		}
	}

	// Run as a PAIR (hinged and free): one number alone cannot tell "held" from "never twisted".
	// Gravity off - this measures the joint, not a fall.
	private static void ProbeKneeHinge(PreparedSkeleton skeleton, Matrix4x4[] models)
	{
		int knee = skeleton.FindJoint("b_LeftLeg02_016");
		if (knee < 0)
		{
			return;
		}

		float hingedBend = float.NaN;
		float freeBend = float.NaN;

		foreach (bool hinged in new[] { true, false })
		{
			var description = BuildDescription(skeleton);
			if (description.Count < 4)
			{
				return;
			}

			var jointWorld = (Matrix4x4[])models.Clone();
			if (hinged)
			{
				MarkHinges(description, skeleton, jointWorld);
			}

			int kneeBone = -1;
			int upperJoint = -1;
			int footJoint = -1;

			for (int i = 0; i < description.Count; i++)
			{
				if (description[i].Joint == knee && description[i].Parent >= 0)
				{
					kneeBone = i;
					upperJoint = description[description[i].Parent].Joint;
					footJoint = description[i].ChildJoint;
				}
			}

			if (kneeBone < 0 || footJoint < 0)
			{
				return;
			}

			using var world = new PhysicsWorld(Vector3.Zero);
			var ragdoll = Ragdoll.Build(world,
				System.Runtime.InteropServices.CollectionsMarshal.AsSpan(description), jointWorld);
			ragdoll.SetAnimationDriven(false);

			var assemblyA = jointWorld[knee].Translation - jointWorld[upperJoint].Translation;
			var assemblyB = jointWorld[footJoint].Translation - jointWorld[knee].Translation;
			var axis = Vector3.Normalize(Vector3.Cross(assemblyA, assemblyB));

			var body = world.Simulation.Bodies[ragdoll.BodyOf(kneeBone)];

			// Half a second, NO longer: the signed angle wraps at +-180 deg and a free knee twisted
			// further wraps back to positive, reporting an inverted joint as correctly bent.
			for (int i = 0; i < 60; i++)
			{
				// Negative around the assembly axis extends the joint towards and past straight.
				body.Velocity.Angular = -axis * 6f;
				body.Velocity.Linear = Vector3.Zero;
				body.Awake = true;

				world.Update(1f / 120f);
			}

			var read = (Matrix4x4[])jointWorld.Clone();
			ragdoll.ReadPose(read);

			var a = read[knee].Translation - read[upperJoint].Translation;
			var b = read[footJoint].Translation - read[knee].Translation;
			float bend = MathF.Atan2(Vector3.Cross(a, b).Length(), Vector3.Dot(a, b));
			float signedBend = bend * MathF.Sign(Vector3.Dot(Vector3.Cross(a, b), axis));

			if (hinged)
			{
				hingedBend = signedBend;
			}
			else
			{
				freeBend = signedBend;
			}

			ragdoll.Destroy();
		}

		// The verdict is the SIGN, not the depth: depth depends on the forcing setup.
		bool hingedOk = hingedBend > 0f;
		bool freeInverted = freeBend < -5f * MathF.PI / 180f;

		Console.WriteLine($"[probe] ragdoll: knee hinge - extension past straight: hinged " +
			$"{hingedBend * 180f / MathF.PI:0.#}°, free {freeBend * 180f / MathF.PI:0.#}° " +
			$"{(hingedOk && freeInverted ? "HOLDS OK" : "DOES NOT HOLD/PAIR DID NOT DIVERGE")}");
	}

	// The same ragdoll at SCENE scale. Joint settings (spring stiffness, sleep thresholds,
	// speculative margin) are ABSOLUTE numbers and do not scale with the entity, so the two scales
	// are compared against each other rather than judged separately.
	private static void ProbeSceneScale(PreparedSkeleton skeleton, Matrix4x4[] models)
	{
		// The third run deliberately drops the twist limit: without it "50 deg at a 50 deg limit"
		// looks the same whether the limiter works or there was nothing to twist.
		foreach (var (scale, twistLimited) in new[] { (1f, true), (0.01f, true), (0.01f, false) })
		{
			var description = BuildDescription(skeleton);
			if (description.Count < 4)
			{
				return;
			}

			for (int i = 0; i < description.Count; i++)
			{
				var bone = description[i];
				bone.Radius *= scale;
				bone.Length *= scale;
				bone.TwistLimitAngle = twistLimited ? bone.TwistLimitAngle : 0f;
				description[i] = bone;
			}

			using var world = new PhysicsWorld(new Vector3(0f, -9.81f, 0f));

			var floor = world.AddBox(new Vector3(400f * scale, 4f * scale, 400f * scale));
			world.AddStatic(new RigidPose(new Vector3(0f, -2f * scale, 0f)), floor);

			var jointWorld = new Matrix4x4[models.Length];
			for (int i = 0; i < models.Length; i++)
			{
				jointWorld[i] = models[i] * Matrix4x4.CreateScale(scale);
			}

			var ragdoll = Ragdoll.Build(world,
				System.Runtime.InteropServices.CollectionsMarshal.AsSpan(description), jointWorld);
			ragdoll.SetAnimationDriven(false);

			var (restRelative, restAxis) = CaptureRest(world, ragdoll, description);

			float initialSpread = Spread(world, ragdoll);
			float impactSpeed = 0f;

			for (float simulated = 0f; simulated < 6f; simulated += 1f / 60f)
			{
				world.Update(1f / 60f);

				if (simulated >= 3f && impactSpeed == 0f)
				{
					impactSpeed = MaxSpeed(world, ragdoll);
				}
			}

			float finalSpread = Spread(world, ragdoll);
			float finalSpeed = MaxSpeed(world, ragdoll);
			int overlaps = CountSelfOverlaps(world, ragdoll, description);
			float worstTwist = WorstTwist(world, ragdoll, description, restRelative, restAxis);

			// Measured RELATIVE to the initial extent; an absolute number would just be the scale.
			float growth = initialSpread > 1e-6f ? finalSpread / initialSpread : 0f;
			bool intact = growth < 1.5f && finalSpeed <= impactSpeed;

			float twistDegrees = worstTwist * 180f / MathF.PI;
			string twistVerdict = twistLimited
				? twistDegrees <= TwistLimitDegrees + TwistToleranceDegrees ? "OK" : "TWISTS OUT"
				: twistDegrees > TwistLimitDegrees + TwistToleranceDegrees
					? "(no limit - so there is something to limit)"
					: "(no limit, yet it does not twist - CHECK IS BLIND)";

			Console.WriteLine($"[probe] ragdoll: scale {scale}, twist " +
				$"{(twistLimited ? $"±{TwistLimitDegrees}°" : "no limit")} - extent " +
				$"{initialSpread:0.###} -> {finalSpread:0.###} (×{growth:0.##}), speed " +
				$"{impactSpeed:0.###} -> {finalSpeed:0.###} {(intact ? "OK" : "BLEW APART")}, " +
				$"self-overlaps {overlaps} {(overlaps == 0 ? "OK" : "FOLDS THROUGH ITSELF")}, " +
				$"worst twist {twistDegrees:0.#}° {twistVerdict}");
		}
	}

	// Twist limit this probe applies to the ragdoll, in degrees.
	private const float TwistLimitDegrees = 50f;

	// The twist limit is a SPRING, not a wall: a joint legitimately overshoots it on impact.
	private const float TwistToleranceDegrees = 15f;

	// Worst twist of a bone around its own long axis relative to its parent, via swing-twist
	// decomposition. The swing cone does not constrain twist at all.
	private static float WorstTwist(PhysicsWorld world, Ragdoll ragdoll, List<RagdollBoneDesc> description,
		Quaternion[] restRelative, Vector3[] restAxis)
	{
		float worst = 0f;

		for (int i = 0; i < ragdoll.BoneCount; i++)
		{
			if (description[i].Parent < 0)
			{
				continue;
			}

			var relative = RelativeRotation(world, ragdoll, description[i].Parent, i);

			// Measured FROM THE ASSEMBLY POSE, not from zero: bones are already rotated there.
			var delta = Quaternion.Concatenate(Quaternion.Conjugate(restRelative[i]), relative);

			// Around a FIXED axis (the assembly-pose bone axis): taking it from the current
			// rotation would define the decomposition relative to itself.
			var axis = restAxis[i];
			float projection = delta.X * axis.X + delta.Y * axis.Y + delta.Z * axis.Z;

			float twist = 2f * MathF.Atan2(projection, delta.W);

			// Wrap into (-pi, pi]: 2*atan2 spans twice that, so 350 deg would not read as -10.
			twist = MathF.IEEERemainder(twist, MathF.Tau);

			worst = MathF.Max(worst, MathF.Abs(twist));
		}

		return worst;
	}

	private static Quaternion RelativeRotation(PhysicsWorld world, Ragdoll ragdoll, int parent, int child)
	{
		var parentPose = world.Simulation.Bodies[ragdoll.BodyOf(parent)].Pose;
		var childPose = world.Simulation.Bodies[ragdoll.BodyOf(child)].Pose;

		return Quaternion.Concatenate(childPose.Orientation, Quaternion.Conjugate(parentPose.Orientation));
	}

	// Assembly-pose snapshot: the reference the joint's own twist limit is defined against.
	private static (Quaternion[] Relative, Vector3[] Axis) CaptureRest(PhysicsWorld world, Ragdoll ragdoll,
		List<RagdollBoneDesc> description)
	{
		var relative = new Quaternion[ragdoll.BoneCount];
		var axis = new Vector3[ragdoll.BoneCount];

		for (int i = 0; i < ragdoll.BoneCount; i++)
		{
			relative[i] = Quaternion.Identity;
			axis[i] = Vector3.UnitY;

			if (description[i].Parent < 0)
			{
				continue;
			}

			relative[i] = RelativeRotation(world, ragdoll, description[i].Parent, i);

			var bone = Vector3.Transform(Vector3.UnitY, relative[i]);
			float length = bone.Length();
			axis[i] = length > 1e-5f ? bone / length : Vector3.UnitY;
		}

		return (relative, axis);
	}

	// Counts NON-ADJACENT bones interpenetrating; adjacent ones share a joint by construction.
	// Distance is between capsule SEGMENTS, not centers: parallel bones overlap along their length.
	private static int CountSelfOverlaps(PhysicsWorld world, Ragdoll ragdoll,
		List<RagdollBoneDesc> description)
	{
		int count = 0;

		for (int i = 0; i < ragdoll.BoneCount; i++)
		{
			for (int j = i + 1; j < ragdoll.BoneCount; j++)
			{
				if (description[i].Parent == j || description[j].Parent == i)
				{
					continue;
				}

				var (a0, a1, ra) = Segment(world, ragdoll, description, i);
				var (b0, b1, rb) = Segment(world, ragdoll, description, j);

				// Bepu contacts sit at a small steady penetration, so zero tolerance would
				// count normal touching as overlap.
				float allowed = (ra + rb) * 0.75f;

				if (SegmentDistance(a0, a1, b0, b1) < allowed)
				{
					count++;
				}
			}
		}

		return count;
	}

	private static (Vector3 A, Vector3 B, float Radius) Segment(PhysicsWorld world, Ragdoll ragdoll,
		List<RagdollBoneDesc> description, int bone)
	{
		var pose = ragdoll.PoseOf(bone);
		var shape = ragdoll.ShapeOf(bone);

		float radius = description[bone].Radius;
		float halfLength = 0f;

		if (shape.Exists && shape.Type == BepuPhysics.Collidables.Capsule.Id)
		{
			var capsule = world.Simulation.Shapes.GetShape<BepuPhysics.Collidables.Capsule>(shape.Index);
			radius = capsule.Radius;
			halfLength = capsule.HalfLength;
		}

		// A Bepu capsule lies along its own Y.
		var axis = Vector3.Transform(Vector3.UnitY, pose.Orientation) * halfLength;
		return (pose.Position - axis, pose.Position + axis, radius);
	}

	// Sampled rather than solved: with ~20 segments the closed form is not worth its edge cases.
	private static float SegmentDistance(Vector3 a0, Vector3 a1, Vector3 b0, Vector3 b1)
	{
		const int steps = 16;
		float best = float.MaxValue;

		for (int i = 0; i <= steps; i++)
		{
			var pa = Vector3.Lerp(a0, a1, i / (float)steps);

			for (int j = 0; j <= steps; j++)
			{
				best = MathF.Min(best, Vector3.Distance(pa, Vector3.Lerp(b0, b1, j / (float)steps)));
			}
		}

		return best;
	}

	private static float Spread(PhysicsWorld world, Ragdoll ragdoll)
	{
		float worst = 0f;

		for (int i = 0; i < ragdoll.BoneCount; i++)
		{
			var a = world.Simulation.Bodies[ragdoll.BodyOf(i)].Pose.Position;

			for (int j = i + 1; j < ragdoll.BoneCount; j++)
			{
				var b = world.Simulation.Bodies[ragdoll.BodyOf(j)].Pose.Position;
				worst = MathF.Max(worst, Vector3.Distance(a, b));
			}
		}

		return worst;
	}

	// Kinematic mode: a mismatch here means a bad joint -> body transform conversion.
	private static void ProbeKinematicTracking(PhysicsWorld world, Ragdoll ragdoll, Matrix4x4[] jointWorld,
		Matrix4x4[] reference)
	{
		for (int i = 0; i < 30; i++)
		{
			ragdoll.DriveToPose(jointWorld, PhysicsWorld.FixedTimeStep);
			world.Update(PhysicsWorld.FixedTimeStep);
		}

		var readBack = (Matrix4x4[])jointWorld.Clone();
		ragdoll.ReadPose(readBack);

		float worst = 0f;
		for (int i = 0; i < ragdoll.BoneCount; i++)
		{
			int joint = ragdoll.JointOf(i);
			worst = MathF.Max(worst,
				Vector3.Distance(readBack[joint].Translation, reference[joint].Translation));
		}

		Console.WriteLine($"[probe] ragdoll: kinematic tracking - worst mismatch " +
			$"{worst:0.####} {(worst < 0.05f ? "OK" : "DOES NOT FOLLOW THE POSE")}");
	}

	// Frame MUCH shorter than the sim step (1/600 vs 1/120): a velocity of delta/frame integrates
	// over a whole step and overshoots by step/frame, which diverges below half a step. Both setup
	// details are load-bearing: bodies start one metre OFF (divergence amplifies error, not zero),
	// and the frame is 1/600 rather than 1/240, which sits exactly on the stability boundary.
	private static void ProbeHighFpsTracking(PhysicsWorld world, Ragdoll ragdoll, Matrix4x4[] jointWorld,
		Matrix4x4[] reference)
	{
		const float frame = 1f / 600f;

		var displaced = (Matrix4x4[])jointWorld.Clone();
		for (int i = 0; i < displaced.Length; i++)
		{
			displaced[i].Translation += new Vector3(1f, 0f, 0f);
		}

		ragdoll.TeleportToPose(displaced);

		for (int i = 0; i < 300; i++)
		{
			ragdoll.DriveToPose(jointWorld, frame);
			world.Update(frame);
		}

		var readBack = (Matrix4x4[])jointWorld.Clone();
		ragdoll.ReadPose(readBack);

		float worst = 0f;
		bool finite = true;

		for (int i = 0; i < ragdoll.BoneCount; i++)
		{
			int joint = ragdoll.JointOf(i);
			var position = readBack[joint].Translation;

			finite &= float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z);
			worst = MathF.Max(worst,
				Vector3.Distance(position, reference[joint].Translation));
		}

		Console.WriteLine($"[probe] ragdoll: tracking at a 1/600 frame from a one-metre offset - mismatch " +
			$"{worst:0.####} {(finite && worst < 0.05f ? "OK" : "OSCILLATED/FLEW AWAY")}");
	}

	// Dynamic mode: the ragdoll must settle, stay finite and stay above the floor.
	private static void ProbeDynamicFall(PhysicsWorld world, Ragdoll ragdoll, Matrix4x4[] jointWorld,
		PreparedSkeleton skeleton)
	{
		ragdoll.SetAnimationDriven(false);

		// Sideways nudge: with self-collision off, a bind-pose ragdoll just stands on its legs.
		// Scaled off hip height so it is not tied to one rig's units.
		var hip = world.Simulation.Bodies[ragdoll.BodyOf(0)];
		hip.Velocity.Linear += new Vector3(jointWorld[ragdoll.JointOf(0)].Translation.Y * 0.5f, 0f, 0f);
		hip.Awake = true;

		float simulated = 0f;
		float speedAtImpact = 0f;

		while (simulated < 6f)
		{
			world.Update(1f / 60f);
			simulated += 1f / 60f;

			// Speed just after landing is the reference: an absolute "settled" threshold would
			// depend on model scale, whereas requiring a DECREASE does not.
			if (simulated >= 3f && speedAtImpact == 0f)
			{
				speedAtImpact = MaxSpeed(world, ragdoll);
			}
		}

		ragdoll.ReadPose(jointWorld);

		bool finite = true;
		float lowest = float.MaxValue;
		float highestSpeed = 0f;

		for (int i = 0; i < ragdoll.BoneCount; i++)
		{
			int joint = ragdoll.JointOf(i);
			var position = jointWorld[joint].Translation;

			finite &= float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z);
			lowest = MathF.Min(lowest, position.Y);
		}

		highestSpeed = MaxSpeed(world, ragdoll);

		Console.WriteLine($"[probe] ragdoll: free fall 6 s - " +
			$"{(finite ? "coordinates finite OK" : "NaN/Inf - BLEW APART")}, " +
			$"lowest bone at y={lowest:0.##} {(lowest > -5f ? "OK" : "FELL THROUGH")}, " +
			$"speed {speedAtImpact:0.##} -> {highestSpeed:0.##} " +
			$"{(highestSpeed < speedAtImpact ? "damping OK" : "DIVERGING")}");
	}

	private static float MaxSpeed(PhysicsWorld world, Ragdoll ragdoll)
	{
		float speed = 0f;
		for (int i = 0; i < ragdoll.BoneCount; i++)
		{
			speed = MathF.Max(speed, world.Simulation.Bodies[ragdoll.BodyOf(i)].Velocity.Linear.Length());
		}

		return speed;
	}

	// Fox ragdoll by bone name; radii are tuned for the model's own scale (~160 units long).
	private static List<RagdollBoneDesc> BuildDescription(PreparedSkeleton skeleton)
	{
		var bones = new List<RagdollBoneDesc>();
		var index = new Dictionary<string, int>();

		void Add(string joint, string child, string parent, float radius, float mass)
		{
			int jointIndex = skeleton.FindJoint(joint);
			int childIndex = child != null ? skeleton.FindJoint(child) : -1;

			if (jointIndex < 0 || (child != null && childIndex < 0))
			{
				return;
			}

			index[joint] = bones.Count;
			bones.Add(new RagdollBoneDesc
			{
				Joint = jointIndex,
				ChildJoint = childIndex,
				Parent = parent != null && index.TryGetValue(parent, out int p) ? p : -1,
				Radius = radius,
				Length = 8f,
				Mass = mass,

				// Must mirror AnimationDriver.BuildRagdollDescription.
				SwingLimitCos = -0.5f,
				TwistLimitAngle = TwistLimitDegrees * (MathF.PI / 180f),
			});
		}

		Add("b_Hip_01", "b_Spine01_02", null, 6f, 12f);
		Add("b_Spine01_02", "b_Spine02_03", "b_Hip_01", 6f, 10f);
		Add("b_Spine02_03", "b_Neck_04", "b_Spine01_02", 5f, 8f);
		Add("b_Neck_04", "b_Head_05", "b_Spine02_03", 3f, 3f);
		Add("b_Head_05", null, "b_Neck_04", 4f, 4f);

		Add("b_LeftLeg01_015", "b_LeftLeg02_016", "b_Hip_01", 2f, 3f);
		Add("b_LeftLeg02_016", "b_LeftFoot01_017", "b_LeftLeg01_015", 1.5f, 2f);
		Add("b_RightLeg01_019", "b_RightLeg02_020", "b_Hip_01", 2f, 3f);
		Add("b_RightLeg02_020", "b_RightFoot01_021", "b_RightLeg01_019", 1.5f, 2f);

		Add("b_LeftUpperArm_09", "b_LeftForeArm_010", "b_Spine02_03", 2f, 3f);
		Add("b_LeftForeArm_010", "b_LeftHand_011", "b_LeftUpperArm_09", 1.5f, 2f);
		Add("b_RightUpperArm_06", "b_RightForeArm_07", "b_Spine02_03", 2f, 3f);
		Add("b_RightForeArm_07", "b_RightHand_08", "b_RightUpperArm_06", 1.5f, 2f);

		return bones;
	}
}
