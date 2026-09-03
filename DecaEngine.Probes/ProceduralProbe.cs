using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DecaEngine.Graphics;
using DecaEngine.Animation;
using DecaEngine.Core;

namespace DecaEngine.Probes;

/// <summary>Procedural layer probe (DECA_PROBE_PROC=1): spring bones, foot IK, aim IK.</summary>
// Joints are looked up by name: glTF node order shifts silently on re-export.
public static class ProceduralProbe
{
	public static void Run(ModelLoader model)
	{
		if (model.Skeleton == null)
		{
			Console.WriteLine("[probe] proc: model has no skeleton - nothing to check");
			return;
		}

		if (!Ozz.IsAvailable)
		{
			Console.WriteLine("[probe] proc: native ozz unavailable - cannot check IK (two-bone/aim live there)");
			return;
		}

		using var skeleton = OzzSkeleton.Build(model.Skeleton);
		using var pose = OzzPose.Create(skeleton);
		if (skeleton == null || pose == null)
		{
			Console.WriteLine("[probe] proc: ozz skeleton failed to build");
			return;
		}

		int jointCount = model.Skeleton.JointCount;
		var locals = new Transform[jointCount];
		var models = new Matrix4x4[jointCount];

		ProbeSpringBones(model.Skeleton, pose, locals, models);
		ProbeFootIk(model.Skeleton, pose, locals, models);
		ProbeFootLocking(model, model.Skeleton, skeleton, pose, locals, models);
		ProbeAimIk(model.Skeleton, pose, locals, models);

		RagdollProbe.Run(model.Skeleton, pose, models);
	}

	private static bool Refresh(OzzPose pose, Transform[] locals, Matrix4x4[] models) =>
		pose.LocalToModel() && pose.ReadModelMatrices(models) && pose.ReadLocalTransforms(locals);

	// --- Spring bones -----------------------------------------------------------------------------

	// Asserts all three: no jitter at rest, lag on a jerk, convergence back afterwards.
	private static void ProbeSpringBones(PreparedSkeleton skeleton, OzzPose pose, Transform[] locals,
		Matrix4x4[] models)
	{
		var chainJoints = new[] { "b_Tail01_012", "b_Tail02_013", "b_Tail03_014" }
			.Select(skeleton.FindJoint)
			.Where(j => j >= 0)
			.ToArray();

		if (chainJoints.Length < 2)
		{
			Console.WriteLine("[probe] proc: no tail chain in the rig - spring bones skipped");
			return;
		}

		var chain = new SpringBoneChain
		{
			Joints = chainJoints,
			Stiffness = 0.08f,
			Drag = 0.2f,
			TailLength = 10f,
		};

		var chains = new List<SpringBoneChain> { chain };
		const float dt = 1f / 60f;

		if (!Refresh(pose, locals, models))
		{
			return;
		}

		var reference = (Transform[])locals.Clone();
		int tip = chainJoints[^1];

		for (int i = 0; i < 100; i++)
		{
			reference.CopyTo(locals, 0);
			pose.WriteLocalTransforms(locals);
			Refresh(pose, locals, models);
			SpringBones.Solve(skeleton, chains, locals, models, dt);
		}

		var restTip = models[tip].Translation;

		reference.CopyTo(locals, 0);
		pose.WriteLocalTransforms(locals);
		Refresh(pose, locals, models);
		float restDrift = Vector3.Distance(restTip, models[tip].Translation);

		int chainRoot = chainJoints[0];
		int chainParent = skeleton.Parents[chainRoot] >= 0 ? skeleton.Parents[chainRoot] : chainRoot;

		var jerked = (Transform[])reference.Clone();
		jerked[chainParent].rotation = Quaternion.Normalize(
			Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 3f) * jerked[chainParent].rotation);

		jerked.CopyTo(locals, 0);
		pose.WriteLocalTransforms(locals);
		Refresh(pose, locals, models);
		var animatedTip = models[tip].Translation;

		SpringBones.Solve(skeleton, chains, locals, models, dt);
		float lag = Vector3.Distance(models[tip].Translation, animatedTip);

		for (int i = 0; i < 300; i++)
		{
			jerked.CopyTo(locals, 0);
			pose.WriteLocalTransforms(locals);
			Refresh(pose, locals, models);
			SpringBones.Solve(skeleton, chains, locals, models, dt);
		}

		float settled = Vector3.Distance(models[tip].Translation, animatedTip);

		Console.WriteLine($"[probe] proc: spring bones - rest {restDrift:0.####} " +
			$"{(restDrift < 0.01f ? "OK" : "JITTERS")}, lag on a jerk {lag:0.###} " +
			$"{(lag > 0.05f ? "OK" : "NO INERTIA")}, return {settled:0.####} " +
			$"{(settled < lag * 0.1f ? "OK" : "DOES NOT CONVERGE")}");
	}

	// --- Foot IK ----------------------------------------------------------------------------------

	private static void ProbeFootIk(PreparedSkeleton skeleton, OzzPose pose, Transform[] locals, Matrix4x4[] models)
	{
		var legs = new List<FootIkLeg>();
		AddLeg(skeleton, legs, "b_LeftLeg01_015", "b_LeftLeg02_016", "b_LeftFoot01_017");
		AddLeg(skeleton, legs, "b_RightLeg01_019", "b_RightLeg02_020", "b_RightFoot01_021");

		if (legs.Count < 2)
		{
			Console.WriteLine("[probe] proc: no hind legs found in the rig - foot IK skipped");
			return;
		}

		// One leg at a time: the fox's bind-pose hind feet differ by 12 units in height,
		// so a shared ground plane would hit the knee fold limit, not the solver.
		foreach (var leg in legs)
		{
			ProbeSingleLeg(skeleton, pose, leg, locals, models);
		}

		ProbePelvisDrop(skeleton, pose, legs, locals, models);
	}

	// Reachable target, pelvis disabled: isolates two-bone IK accuracy from pelvis drop.
	private static void ProbeSingleLeg(PreparedSkeleton skeleton, OzzPose pose, FootIkLeg leg,
		Transform[] locals, Matrix4x4[] models)
	{
		var settings = new FootIkSettings
		{
			PelvisJoint = -1,
			ProbeUp = 20f,
			ProbeDown = 40f,
			// Single-step check: smoothing would spread the expected value over frames.
			Smoothing = 0f,
			AlignToNormal = false,
		};

		if (!Refresh(pose, locals, models))
		{
			return;
		}

		float startY = models[leg.FootJoint].Translation.Y;

		// Ground is 1 unit above the model origin: higher saturates the correction clamp.
		const float groundY = 1f;
		leg.AnkleHeight = startY;

		GroundSample Ground(Vector3 origin, Vector3 direction, float distance)
		{
			float travel = origin.Y - groundY;
			return travel >= 0f && travel <= distance
				? new GroundSample { Hit = true, Position = new Vector3(origin.X, groundY, origin.Z), Normal = Vector3.UnitY }
				: default;
		}

		leg.ResetSmoothing();
		var single = new List<FootIkLeg> { leg };

		bool solved = FootIk.Solve(pose, skeleton, single, settings, Matrix4x4.Identity, locals, models, Ground, 1f / 60f);

		float resultY = models[leg.FootJoint].Translation.Y;
		float expected = groundY + leg.AnkleHeight;
		float error = MathF.Abs(resultY - expected);

		// Chain limits |L1-L2|..L1+L2: without them an unreachable target reads as solver error.
		var hip = models[leg.UpperJoint].Translation;
		float upperLength = Vector3.Distance(hip, models[leg.LowerJoint].Translation);
		float lowerLength = Vector3.Distance(models[leg.LowerJoint].Translation, models[leg.FootJoint].Translation);
		float maxReach = upperLength + lowerLength;
		float minReach = MathF.Abs(upperLength - lowerLength);

		// Reach is measured to the target, not the solved foot, which is always in range.
		var targetPoint = new Vector3(
			models[leg.FootJoint].Translation.X, expected, models[leg.FootJoint].Translation.Z);
		float targetReach = Vector3.Distance(hip, targetPoint);

		string verdict = error < 0.02f
			? "OK"
			: targetReach >= maxReach - 0.02f
				? "target OUT OF REACH (leg fully extended)"
				: targetReach <= minReach + 0.02f
					? "target closer than the folding limit"
					: "TOO LARGE";

		Console.WriteLine($"[probe] proc: foot IK ({skeleton.JointNames[leg.FootJoint]}) - " +
			$"{(solved ? "solved" : "NOT SOLVED")}, y {startY:0.###} -> {resultY:0.###}, expected {expected:0.###}, " +
			$"error {error:0.####}; reach {minReach:0.##}..{maxReach:0.##}, to target {targetReach:0.##} {verdict}");
	}

	// Ground far below both feet: the pelvis must drop exactly MaxPelvisDrop, no more, no less.
	private static void ProbePelvisDrop(PreparedSkeleton skeleton, OzzPose pose, List<FootIkLeg> legs,
		Transform[] locals, Matrix4x4[] models)
	{
		int pelvis = skeleton.FindJoint("b_Hip_01");
		if (pelvis < 0)
		{
			return;
		}

		const float maxDrop = 3f;
		var settings = new FootIkSettings
		{
			PelvisJoint = pelvis,
			ProbeUp = 20f,
			ProbeDown = 80f,
			MaxPelvisDrop = maxDrop,
			Smoothing = 0f,
			AlignToNormal = false,
		};

		if (!Refresh(pose, locals, models))
		{
			return;
		}

		float pelvisBefore = models[pelvis].Translation.Y;

		// Below the model origin, not below the foot: the solver keeps foot lift over origin,
		// so any plane still above zero reads as a step up, not a drop.
		const float groundY = -20f;

		GroundSample Ground(Vector3 origin, Vector3 direction, float distance)
		{
			float travel = origin.Y - groundY;
			return travel >= 0f && travel <= distance
				? new GroundSample { Hit = true, Position = new Vector3(origin.X, groundY, origin.Z), Normal = Vector3.UnitY }
				: default;
		}

		foreach (var leg in legs)
		{
			leg.ResetSmoothing();

			// Restore the authored ankle height: ProbeSingleLeg overwrote it on the shared legs.
			leg.AnkleHeight = 0.5f;
		}

		FootIk.Solve(pose, skeleton, legs, settings, Matrix4x4.Identity, locals, models, Ground, 1f / 60f);

		float drop = pelvisBefore - models[pelvis].Translation.Y;
		Console.WriteLine($"[probe] proc: pelvis dropped by {drop:0.###} with a cap of {maxDrop} " +
			$"{(MathF.Abs(drop - maxDrop) < 0.02f ? "OK" : "MISMATCH")}");
	}

	// A/B on tempo mismatch: only the locked-vs-unlocked pair proves locking, one branch cannot.
	private static void ProbeFootLocking(ModelLoader model, PreparedSkeleton skeleton, OzzSkeleton ozz,
		OzzPose pose, Transform[] locals, Matrix4x4[] models)
	{
		var walk = model.Animations.FirstOrDefault(
			a => string.Equals(a.Name, "Walk", StringComparison.Ordinal));
		int leftFoot = skeleton.FindJoint("b_LeftFoot01_017");
		int rightFoot = skeleton.FindJoint("b_RightFoot01_021");

		if (walk == null || leftFoot < 0 || rightFoot < 0)
		{
			Console.WriteLine("[probe] proc: locking skipped - no Walk clip or no hind feet");
			return;
		}

		using var clip = OzzClip.Build(ozz, walk);
		if (clip == null || clip.Duration <= 0f)
		{
			Console.WriteLine("[probe] proc: locking skipped - the Walk clip failed to build in ozz");
			return;
		}

		const int cycleSamples = 60;
		var leftPositions = new Vector3[cycleSamples];
		float leftMin = float.MaxValue, leftMax = float.MinValue, rightMin = float.MaxValue;

		for (int k = 0; k < cycleSamples; k++)
		{
			if (!pose.Sample(clip, clip.Duration * k / cycleSamples) || !Refresh(pose, locals, models))
			{
				return;
			}

			leftPositions[k] = models[leftFoot].Translation;
			leftMin = MathF.Min(leftMin, leftPositions[k].Y);
			leftMax = MathF.Max(leftMax, leftPositions[k].Y);
			rightMin = MathF.Min(rightMin, models[rightFoot].Translation.Y);
		}

		// Stance = lowest tenth of the height span: at the edges locking releases by design.
		var stance = new bool[cycleSamples];
		float threshold = leftMin + 0.10f * (leftMax - leftMin);
		float strideTravel = 0f;
		float strideSeconds = 0f;

		for (int k = 0; k < cycleSamples; k++)
		{
			stance[k] = leftPositions[k].Y < threshold;

			int next = (k + 1) % cycleSamples;
			if (leftPositions[k].Y < threshold && leftPositions[next].Y < threshold)
			{
				var step = leftPositions[next] - leftPositions[k];
				strideTravel += MathF.Sqrt(step.X * step.X + step.Z * step.Z);
				strideSeconds += clip.Duration / cycleSamples;
			}
		}

		if (strideSeconds < 1e-4f || strideTravel < 1e-3f)
		{
			Console.WriteLine("[probe] proc: locking skipped - the clip has no stance beat");
			return;
		}

		float naturalSpeed = strideTravel / strideSeconds;

		GroundSample Ground(Vector3 origin, Vector3 direction, float distance)
		{
			float travel = origin.Y;
			return travel >= 0f && travel <= distance
				? new GroundSample { Hit = true, Position = new Vector3(origin.X, 0f, origin.Z), Normal = Vector3.UnitY }
				: default;
		}

		float Slide(bool locking)
		{
			var legs = new List<FootIkLeg>();
			AddLeg(skeleton, legs, "b_LeftLeg01_015", "b_LeftLeg02_016", "b_LeftFoot01_017");
			AddLeg(skeleton, legs, "b_RightLeg01_019", "b_RightLeg02_020", "b_RightFoot01_021");

			if (legs.Count < 2)
			{
				return float.NaN;
			}

			legs[0].AnkleHeight = leftMin;
			legs[1].AnkleHeight = rightMin;

			var settings = new FootIkSettings
			{
				PelvisJoint = -1,
				ProbeUp = 20f,
				ProbeDown = 60f,
				Smoothing = 30f,
				MaxPelvisDrop = 0f,
				AlignToNormal = false,
				LockFeet = locking,
			};

			// Clip speed +10%: a larger mismatch trips the 0.35-leg-length release safeguard.
			// Travel is along -Z, the fox's facing axis; any other axis never cancels the swing.
			float speed = naturalSpeed * 1.1f;
			const float dt = 1f / 60f;
			const int frames = 240;

			// Skipped: the locking envelope needs a cycle or two to learn the leg's span.
			const int warmup = 90;

			float slide = 0f;
			var previous = Vector3.Zero;
			bool previousStance = false;

			for (int i = 0; i < frames; i++)
			{
				float time = i * dt;
				float clipTime = time % clip.Duration;
				int k = (int)(clipTime / clip.Duration * cycleSamples) % cycleSamples;
				var world = Matrix4x4.CreateTranslation(0f, 0f, -speed * time);

				if (!pose.Sample(clip, clipTime) || !Refresh(pose, locals, models))
				{
					return float.NaN;
				}

				FootIk.Solve(pose, skeleton, legs, settings, world, locals, models, Ground, dt);

				var footWorld = Vector3.Transform(models[leftFoot].Translation, world);

				if (i > warmup && previousStance && stance[k])
				{
					slide += Vector3.Distance(footWorld, previous);
				}

				previous = footWorld;
				previousStance = stance[k];
			}

			return slide;
		}

		float unlocked = Slide(locking: false);
		float locked = Slide(locking: true);

		// Locking is a damper, not a hard pin, so the bar is "noticeably less", not "near zero".
		bool ok = unlocked > 5f && locked < unlocked * 0.7f;

		Console.WriteLine($"[probe] proc: stance foot locking - stance slide without locking " +
			$"{unlocked:0.##}, with locking {locked:0.##} " +
			$"{(ok ? "OK" : "DOES NOT HOLD/PAIR DID NOT DIVERGE")}");
	}

	private static void AddLeg(PreparedSkeleton skeleton, List<FootIkLeg> legs, string upper, string lower, string foot)
	{
		int upperJoint = skeleton.FindJoint(upper);
		int lowerJoint = skeleton.FindJoint(lower);
		int footJoint = skeleton.FindJoint(foot);

		if (upperJoint < 0 || lowerJoint < 0 || footJoint < 0)
		{
			return;
		}

		legs.Add(new FootIkLeg
		{
			UpperJoint = upperJoint,
			LowerJoint = lowerJoint,
			FootJoint = footJoint,
			AnkleHeight = 0.5f,
		});
	}

	// --- Aim IK -----------------------------------------------------------------------------------

	// Compares angle-to-target before/after: the forward axis is rig-specific, exact hits are not.
	private static void ProbeAimIk(PreparedSkeleton skeleton, OzzPose pose, Transform[] locals, Matrix4x4[] models)
	{
		int head = skeleton.FindJoint("b_Head_05");
		if (head < 0)
		{
			Console.WriteLine("[probe] proc: no head in the rig - aim IK skipped");
			return;
		}

		pose.WriteLocalTransforms(locals);
		if (!Refresh(pose, locals, models))
		{
			return;
		}

		var forward = Vector3.UnitZ;
		var headPosition = models[head].Translation;
		var target = headPosition + new Vector3(60f, 40f, 0f);

		float before = AngleToTarget(models[head], forward, target);
		bool solved = pose.AimIk(head, target, forward, Vector3.UnitY, Vector3.UnitY);
		Refresh(pose, locals, models);
		float after = AngleToTarget(models[head], forward, target);

		Console.WriteLine($"[probe] proc: aim IK - {(solved ? "solved" : "NOT SOLVED")}, " +
			$"angle to target {before * 180f / MathF.PI:0.#}° -> {after * 180f / MathF.PI:0.#}° " +
			$"{(solved && after < before - 0.01f ? "OK" : "NO IMPROVEMENT")}");
	}

	private static float AngleToTarget(in Matrix4x4 joint, Vector3 localForward, Vector3 target)
	{
		var direction = Vector3.TransformNormal(localForward, joint);
		var toTarget = target - joint.Translation;

		if (direction.LengthSquared() < 1e-10f || toTarget.LengthSquared() < 1e-10f)
		{
			return 0f;
		}

		float cos = Vector3.Dot(Vector3.Normalize(direction), Vector3.Normalize(toTarget));
		return MathF.Acos(Math.Clamp(cos, -1f, 1f));
	}
}
