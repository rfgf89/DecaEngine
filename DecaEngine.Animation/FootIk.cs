using System;
using System.Collections.Generic;
using System.Numerics;
using DecaEngine.Core;

namespace DecaEngine.Animation;

/// <summary>Ground query result; abstraction so the solver has no physics-library dependency.</summary>
public struct GroundSample
{
	public bool Hit;
	public Vector3 Position;
	public Vector3 Normal;
}

/// <summary>One leg: hip -> shin -> foot chain.</summary>
public sealed class FootIkLeg
{
	public int UpperJoint;
	public int LowerJoint;
	public int FootJoint;

	/// <summary>Ground-contact joint, -1 = the foot itself; two-bone IK still targets the foot.</summary>
	public int ToeJoint = -1;

	/// <summary>Front leg of a quadruped; groups legs for body slope tilt.</summary>
	public bool Front;

	/// <summary>Right leg; groups legs for lateral roll tilt.</summary>
	public bool Right;

	/// <summary>Knee aim hint in MODEL space; without it two-bone IK may bend the knee backwards.</summary>
	public Vector3 PoleVector = Vector3.UnitZ;

	/// <summary>Knee bend axis in the LOWER joint's LOCAL space (ozz mid axis).</summary>
	public Vector3 KneeAxis = Vector3.UnitX;

	/// <summary>Derive the knee axis from the pose; a wrong fixed axis makes targets fall short.</summary>
	public bool AutoKneeAxis = true;

	/// <summary>Contact-joint height above the sole: the ray hits the SURFACE, IK places the JOINT.</summary>
	public float AnkleHeight = 0.1f;

	internal float SmoothedLift;
	internal bool Initialized;

	// Stance comes from each leg's own lift envelope: absolute height thresholds are
	// wrong for digitigrades.
	internal float LiftMin;
	internal float LiftMax;
	internal bool EnvelopeInit;

	internal bool LockActive;
	internal Vector3 LockPointWorld;

	// Hold weight 0..1: an instant release reads as a foot snap.
	internal float LockBlend;

	// Drag frozen at the last locked frame; recomputing it during the fade jerks on release.
	internal Vector3 LockFrozenOffset;

	// World contact position last frame, for the contact-speed lock gate.
	internal Vector3 PrevContactWorld;
	internal bool PrevContactValid;


	/// <summary>Resets smoothing and locking; required after a teleport (lock points are world-space).</summary>
	public void ResetSmoothing()
	{
		Initialized = false;
		EnvelopeInit = false;
		LockActive = false;
		LockBlend = 0f;
		PrevContactValid = false;
	}
}

public sealed class FootIkSettings
{
	/// <summary>Pelvis joint; dropping it lets the lower foot reach when feet stand at different heights.</summary>
	public int PelvisJoint = -1;

	/// <summary>Up axis in model space.</summary>
	public Vector3 Up = Vector3.UnitY;

	/// <summary>Ray start height above the foot and probe depth below it.</summary>
	public float ProbeUp = 0.5f;
	public float ProbeDown = 1.5f;

	/// <summary>Pelvis drop limit; without it one missed ray drags the pelvis down indefinitely.</summary>
	public float MaxPelvisDrop = 0.4f;

	/// <summary>Smoothing rate, 1/s; hard snapping jitters on every triangle seam the ray crosses.</summary>
	public float Smoothing = 12f;

	/// <summary>Overall effect weight, 0..1; zero disables IK.</summary>
	public float Weight = 1f;

	/// <summary>Align the foot to the ground normal.</summary>
	public bool AlignToNormal = true;

	/// <summary>Pin the stance foot to a world point; removes sliding when clip tempo mismatches speed.</summary>
	public bool LockFeet = false;

	/// <summary>Tilt the body along the terrain slope; needs a pelvis and both leg groups grounded.</summary>
	public bool AlignBodyToSlope = false;

	/// <summary>Body tilt limit, radians.</summary>
	public float MaxBodyTilt = 0.4f;

	internal float SmoothedTilt;
	internal float SmoothedRoll;
	internal bool TiltInitialized;
}

/// <summary>
/// Terrain foot IK. Step order is fixed: probe ground, drop pelvis, two-bone IK, align feet.
/// Requires the native ozz shim; without it Solve returns false and the pose stays animated.
/// </summary>
public static class FootIk
{
	/// <summary>Solves and applies foot IK; rays are cast in WORLD space, IK runs in MODEL space.</summary>
	public static bool Solve(OzzPose pose, PreparedSkeleton skeleton, IReadOnlyList<FootIkLeg> legs,
		FootIkSettings settings, Matrix4x4 modelToWorld, Transform[] locals, Matrix4x4[] models,
		Func<Vector3, Vector3, float, GroundSample> raycast, float deltaSeconds)
	{
		if (pose == null || legs == null || settings == null || raycast == null || legs.Count == 0 ||
			settings.Weight <= 0f)
		{
			return false;
		}

		if (!Matrix4x4.Invert(modelToWorld, out var worldToModel))
		{
			return false;
		}

		var up = Vector3.Normalize(settings.Up);

		// Targets are ABSOLUTE model-space heights, independent of the pelvis shift below.
		Span<float> targetHeights = legs.Count <= 8 ? stackalloc float[legs.Count] : new float[legs.Count];
		Span<GroundSample> hits = legs.Count <= 8 ? stackalloc GroundSample[legs.Count] : new GroundSample[legs.Count];
		Span<float> lifts = legs.Count <= 8 ? stackalloc float[legs.Count] : new float[legs.Count];
		Span<float> reaches = legs.Count <= 8 ? stackalloc float[legs.Count] : new float[legs.Count];

		// Sampled BEFORE body tilt: tilt moves feet and would read as swing in the lift signal.
		for (int i = 0; i < legs.Count; i++)
		{
			var contactModel = models[ContactOf(legs[i])].Translation;
			var contactWorld = Vector3.Transform(contactModel, modelToWorld);
			var worldUp = Vector3.Normalize(Vector3.TransformNormal(up, modelToWorld));

			hits[i] = raycast(contactWorld + worldUp * settings.ProbeUp, -worldUp,
				settings.ProbeUp + settings.ProbeDown);

			// RAW, not clamped at 0: the lock envelope needs the full cycle shape.
			lifts[i] = Vector3.Dot(contactModel, up) - legs[i].AnkleHeight;

			if (Environment.GetEnvironmentVariable("DECA_TILT_DEBUG") == "1")
			{
				Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
					$"[ray] i={i} front={legs[i].Front} contactW=({contactWorld.X:F2}|{contactWorld.Y:F2}|{contactWorld.Z:F2}) " +
					$"hit={hits[i].Hit} at=({hits[i].Position.X:F2}|{hits[i].Position.Y:F2}|{hits[i].Position.Z:F2})"));
			}
		}

		// Body tilt BEFORE leg targets: a tilted pelvis moves every foot the targets read from.
		if (!ApplyBodyTilt(pose, skeleton, legs, settings, hits, worldToModel, up, locals, models,
			deltaSeconds))
		{
			return false;
		}

		float pelvisDelta = float.MaxValue;

		for (int i = 0; i < legs.Count; i++)
		{
			var leg = legs[i];
			var contactModel = models[ContactOf(leg)].Translation;
			float contactHeight = Vector3.Dot(contactModel, up);
			var sample = hits[i];

			if (!sample.Hit)
			{
				// No ground: leg stays animated, and 0 blocks a pelvis raise but allows a drop.
				targetHeights[i] = contactHeight;
				pelvisDelta = MathF.Min(pelvisDelta, 0f);
				continue;
			}

			var groundModel = Vector3.Transform(sample.Position, worldToModel);

			// Only the TERRAIN is smoothed; smoothing the full target lags fast swings.
			// The clip's lift rides on top, or a swinging leg drags the pelvis into the ground.
			float ground = Approach(leg, Vector3.Dot(groundModel, up), settings.Smoothing, deltaSeconds);
			float desired = ground + leg.AnkleHeight + MathF.Max(lifts[i], 0f);

			var footModel = models[leg.FootJoint].Translation;
			reaches[i] =
				Vector3.Distance(models[leg.UpperJoint].Translation, models[leg.LowerJoint].Translation) +
				Vector3.Distance(models[leg.LowerJoint].Translation, footModel);

			// Unclamped: per-leg clamping happens after the pelvis shift.
			targetHeights[i] = desired;
			pelvisDelta = MathF.Min(pelvisDelta, desired - contactHeight);

			if (Environment.GetEnvironmentVariable("DECA_TILT_DEBUG") == "1")
			{
				Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
					$"[height] i={i} contact={contactHeight:F1} ground={ground:F1} lift={lifts[i]:F1} " +
					$"desired={desired:F1}"));
			}
		}

		// MINIMUM delta in both directions: the pelvis rises only when ALL feet allow it.
		float pelvisDrop = pelvisDelta == float.MaxValue
			? 0f
			: Math.Clamp(pelvisDelta, -settings.MaxPelvisDrop, settings.MaxPelvisDrop) * settings.Weight;

		if (settings.PelvisJoint >= 0 && pelvisDrop != 0f)
		{
			// Offset is expressed in the pelvis PARENT's space, where its local translation lives.
			int parent = skeleton.Parents[settings.PelvisJoint];
			var offset = up * pelvisDrop;

			if (parent >= 0 && Matrix4x4.Invert(models[parent], out var parentInverse))
			{
				offset = Vector3.TransformNormal(offset, parentInverse);
			}

			locals[settings.PelvisJoint].position += offset;

			if (!pose.WriteLocalTransforms(locals) || !pose.LocalToModel() || !pose.ReadModelMatrices(models))
			{
				return false;
			}
		}

		// Captured AFTER tilt/pelvis but BEFORE two-bone IK, which drags the foot with the
		// knee; AlignFeet restores them afterwards.
		Span<Quaternion> footRotations = legs.Count <= 8
			? stackalloc Quaternion[legs.Count]
			: new Quaternion[legs.Count];

		for (int i = 0; i < legs.Count; i++)
		{
			// System.Numerics q1*q2 applies the RIGHT factor first: model = parent * local.
			int foot = legs[i].FootJoint;
			int parent = skeleton.Parents[foot];
			var parentRotation = parent >= 0
				? Quaternion.CreateFromRotationMatrix(Orthonormal(models[parent]))
				: Quaternion.Identity;

			footRotations[i] = parentRotation * locals[foot].rotation;
		}

		Quaternion[]? preSolveLocals = null;
		if (Environment.GetEnvironmentVariable("DECA_TILT_DEBUG") == "1")
		{
			preSolveLocals = new Quaternion[legs.Count * 3];
			for (int i = 0; i < legs.Count; i++)
			{
				preSolveLocals[i * 3] = locals[legs[i].UpperJoint].rotation;
				preSolveLocals[i * 3 + 1] = locals[legs[i].LowerJoint].rotation;
				preSolveLocals[i * 3 + 2] = locals[legs[i].FootJoint].rotation;
			}
		}

		// Targets are computed AFTER the pelvis shift, from the updated model matrices.
		for (int i = 0; i < legs.Count; i++)
		{
			var leg = legs[i];

			if (!hits[i].Hit)
			{
				// No ground: release the lock, its world point no longer exists.
				leg.LockActive = false;
				leg.LockBlend = ApproachValue(leg.LockBlend, 0f, LockRate, deltaSeconds);
				continue;
			}

			var footModel = models[leg.FootJoint].Translation;
			var contactModel = models[ContactOf(leg)].Translation;

			// Delta is measured at the CONTACT joint but applied to the FOOT, which two-bone
			// solves to. Weight goes into the TARGET and the solve is always full, so every
			// weight step is a valid pose; blending corrections can flip the knee.
			// The clamp is tighter upward: shortening folds the joint into the silhouette early.
			float current = Vector3.Dot(contactModel, up);
			float delta = Math.Clamp(targetHeights[i] - current, -0.5f * reaches[i], 0.25f * reaches[i]);
			var target = footModel + up * (delta * settings.Weight);

			if (Environment.GetEnvironmentVariable("DECA_TILT_DEBUG") == "1")
			{
				Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
					$"[solve] i={i} current={current:F1} target={targetHeights[i]:F1} delta={delta:F1} " +
					$"foot=({footModel.X:F1}|{footModel.Y:F1}|{footModel.Z:F1})"));
			}

			if (settings.LockFeet)
			{
				// Gated on WORLD-space contact speed: a stance foot is still in world space,
				// and locking a foot that is still landing jerks it.
				var contactWorld = Vector3.Transform(contactModel, modelToWorld);
				float contactSpeed = 0f;

				if (leg.PrevContactValid && deltaSeconds > 0f)
				{
					contactSpeed = Vector3.Distance(contactWorld, leg.PrevContactWorld) / deltaSeconds;
				}

				leg.PrevContactWorld = contactWorld;
				leg.PrevContactValid = true;

				// Threshold scales with leg length; first frame / zero dt read as slow and allow lock.
				float worldReach = reaches[i] * Vector3.TransformNormal(up, modelToWorld).Length();
				bool slowEnough = contactSpeed < 1.2f * worldReach;

				bool entered = UpdateLockState(leg, lifts[i], reaches[i], deltaSeconds, slowEnough);

				// Below half weight the lock point FOLLOWS the foot: pinning at entry would
				// grab a foot that is still moving.
				if (entered || (leg.LockActive && leg.LockBlend < 0.5f))
				{
					leg.LockPointWorld = Vector3.Transform(contactModel, modelToWorld);
				}

				if (leg.LockActive || leg.LockBlend > 1e-3f)
				{
					if (leg.LockActive)
					{
						var lockModel = Vector3.Transform(leg.LockPointWorld, worldToModel);

						// Release BEFORE the leg stretches straight and yanks the pelvis; reach is
						// checked at the FOOT, matching the chain length in reaches.
						var footAtLock = lockModel + (footModel - contactModel);
						if (Vector3.Distance(models[leg.UpperJoint].Translation, footAtLock) >
							0.95f * reaches[i])
						{
							leg.LockActive = false;
						}

						// Past a third of leg length the lock is still reachable, but holding it
						// drags the foot under the body.
						var lockOffset = lockModel - contactModel;
						lockOffset -= up * Vector3.Dot(lockOffset, up);
						if (lockOffset.Length() > 0.35f * reaches[i])
						{
							leg.LockActive = false;
						}

						// Saturated and FROZEN: recomputing it live while the weight fades turns
						// a growing drift into a jerk on release.
						float dragLimit = 0.35f * reaches[i];
						float dragLength = lockOffset.Length();
						if (dragLength > dragLimit)
						{
							lockOffset *= dragLimit / dragLength;
						}

						leg.LockFrozenOffset = lockOffset;
					}

					leg.LockBlend = ApproachValue(leg.LockBlend, leg.LockActive ? 1f : 0f,
						LockRate, deltaSeconds);

					if (leg.LockBlend > 1e-3f)
					{
						// Only the HORIZONTAL is locked, so locking and the terrain height
						// channel never fight over one axis.
						target += leg.LockFrozenOffset * (leg.LockBlend * Math.Clamp(settings.Weight, 0f, 1f));
					}
				}
			}

			pose.TwoBoneIk(leg.UpperJoint, leg.LowerJoint, leg.FootJoint, target,
				PoleOf(leg, models), KneeAxisOf(leg, models));
		}

		if (!pose.LocalToModel() || !pose.ReadModelMatrices(models) || !pose.ReadLocalTransforms(locals))
		{
			return false;
		}

		AlignFeet(skeleton, legs, hits, footRotations, settings, worldToModel, up, locals, models);

		if (!pose.WriteLocalTransforms(locals) || !pose.LocalToModel() || !pose.ReadModelMatrices(models))
		{
			return false;
		}

		if (preSolveLocals != null)
		{
			for (int i = 0; i < legs.Count; i++)
			{
				var leg = legs[i];
				float upperTurn = TurnOf(preSolveLocals[i * 3], locals[leg.UpperJoint].rotation);
				float lowerTurn = TurnOf(preSolveLocals[i * 3 + 1], locals[leg.LowerJoint].rotation);
				float footTurn = TurnOf(preSolveLocals[i * 3 + 2], locals[leg.FootJoint].rotation);

				Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
					$"[final] i={i} contact={Vector3.Dot(models[ContactOf(leg)].Translation, up):F1} " +
					$"foot={Vector3.Dot(models[leg.FootJoint].Translation, up):F1} " +
					$"target={targetHeights[i]:F1} " +
					$"local turns: thigh {upperTurn:F0}° knee {lowerTurn:F0}° foot {footTurn:F0}°"));
			}
		}

		return true;
	}

	private static float TurnOf(Quaternion before, Quaternion after)
	{
		var delta = Quaternion.Normalize(after * Quaternion.Inverse(before));
		return 2f * MathF.Acos(Math.Clamp(MathF.Abs(delta.W), 0f, 1f)) * 180f / MathF.PI;
	}

	private static int ContactOf(FootIkLeg leg) => leg.ToeJoint >= 0 ? leg.ToeJoint : leg.FootJoint;

	// Pole = upper-bone direction (ozz foot_ik trick): never degenerate, always leans to the bend.
	private static Vector3 PoleOf(FootIkLeg leg, Matrix4x4[] models)
	{
		var direction = models[leg.LowerJoint].Translation - models[leg.UpperJoint].Translation;
		return direction.LengthSquared() > 1e-8f ? direction : leg.PoleVector;
	}

	// Leg-plane normal in the LOWER joint's local space, where ozz expects it. The SIGN matters:
	// the opposite axis hits the same target with the leg plane flipped and the thigh twisted.
	private static Vector3 KneeAxisOf(FootIkLeg leg, Matrix4x4[] models)
	{
		if (!leg.AutoKneeAxis)
		{
			return leg.KneeAxis;
		}

		var upper = models[leg.UpperJoint].Translation;
		var mid = models[leg.LowerJoint].Translation;
		var foot = models[leg.FootJoint].Translation;

		var axis = Vector3.Cross(foot - mid, mid - upper);
		if (axis.LengthSquared() < 1e-8f || !Matrix4x4.Invert(models[leg.LowerJoint], out var midInverse))
		{
			return leg.KneeAxis;
		}

		var local = Vector3.TransformNormal(axis, midInverse);
		return local.LengthSquared() > 1e-10f ? Vector3.Normalize(local) : leg.KneeAxis;
	}

	// Requires both the front and hind leg groups to be grounded.
	private static bool ApplyBodyTilt(OzzPose pose, PreparedSkeleton skeleton,
		IReadOnlyList<FootIkLeg> legs, FootIkSettings settings, ReadOnlySpan<GroundSample> hits,
		in Matrix4x4 worldToModel, Vector3 up, Transform[] locals, Matrix4x4[] models,
		float deltaSeconds)
	{
		if (!settings.AlignBodyToSlope || settings.PelvisJoint < 0)
		{
			return true;
		}

		float frontGround = 0f, hindGround = 0f, leftGround = 0f, rightGround = 0f;
		int frontCount = 0, hindCount = 0, leftCount = 0, rightCount = 0;
		var frontCenter = Vector3.Zero;
		var hindCenter = Vector3.Zero;
		var leftCenter = Vector3.Zero;
		var rightCenter = Vector3.Zero;

		for (int i = 0; i < legs.Count; i++)
		{
			if (!hits[i].Hit)
			{
				continue;
			}

			float ground = Vector3.Dot(Vector3.Transform(hits[i].Position, worldToModel), up);
			var foot = models[ContactOf(legs[i])].Translation;

			if (legs[i].Front)
			{
				frontGround += ground;
				frontCenter += foot;
				frontCount++;
			}
			else
			{
				hindGround += ground;
				hindCenter += foot;
				hindCount++;
			}

			if (legs[i].Right)
			{
				rightGround += ground;
				rightCenter += foot;
				rightCount++;
			}
			else
			{
				leftGround += ground;
				leftCenter += foot;
				leftCount++;
			}
		}

		if (frontCount == 0 || hindCount == 0)
		{
			return true;
		}

		frontGround /= frontCount;
		hindGround /= hindCount;
		frontCenter /= frontCount;
		hindCenter /= hindCount;

		var span = frontCenter - hindCenter;
		span -= up * Vector3.Dot(span, up);
		float distance = span.Length();

		if (distance < 1e-4f)
		{
			return true;
		}

		float target = Math.Clamp(MathF.Atan2(frontGround - hindGround, distance),
			-settings.MaxBodyTilt, settings.MaxBodyTilt);

		float rollTarget = 0f;
		if (leftCount > 0 && rightCount > 0)
		{
			leftGround /= leftCount;
			rightGround /= rightCount;
			leftCenter /= leftCount;
			rightCenter /= rightCount;

			var lateral = rightCenter - leftCenter;
			lateral -= up * Vector3.Dot(lateral, up);
			float lateralDistance = lateral.Length();

			if (lateralDistance > 1e-4f)
			{
				rollTarget = Math.Clamp(MathF.Atan2(rightGround - leftGround, lateralDistance),
					-settings.MaxBodyTilt, settings.MaxBodyTilt);
			}
		}

		if (Environment.GetEnvironmentVariable("DECA_TILT_DEBUG") == "1")
		{
			Console.WriteLine($"[tilt] front={frontGround:0.#}({frontCount}) hind={hindGround:0.#}({hindCount}) " +
				$"dist={distance:0.#} target={target * 180f / MathF.PI:0.#}deg " +
				$"left={leftGround / MathF.Max(leftCount, 1):0.#}({leftCount}) " +
				$"right={rightGround / MathF.Max(rightCount, 1):0.#}({rightCount}) " +
				$"rollTarget={rollTarget * 180f / MathF.PI:0.#}deg smoothedRoll={settings.SmoothedRoll * 180f / MathF.PI:0.#}deg");

			for (int i = 0; i < legs.Count; i++)
			{
				var footPosition = models[legs[i].FootJoint].Translation;
				Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
					$"[tilt]   leg{i} front={legs[i].Front} joint={legs[i].FootJoint} " +
					$"foot=({footPosition.X:F1}|{footPosition.Y:F1}|{footPosition.Z:F1})"));
			}
		}

		// First frame snaps so a character spawned on a slope starts tilted.
		if (!settings.TiltInitialized)
		{
			settings.SmoothedTilt = target;
			settings.SmoothedRoll = rollTarget;
			settings.TiltInitialized = true;
		}
		else
		{
			// Zero dt (edit mode) applies instantly, as in Approach.
			float alpha = settings.Smoothing > 0f && deltaSeconds > 0f
				? 1f - MathF.Exp(-settings.Smoothing * deltaSeconds)
				: 1f;
			settings.SmoothedTilt += (target - settings.SmoothedTilt) * alpha;
			settings.SmoothedRoll += (rollTarget - settings.SmoothedRoll) * alpha;
		}

		float weight = Math.Clamp(settings.Weight, 0f, 1f);
		float angle = settings.SmoothedTilt * weight;
		float roll = settings.SmoothedRoll * weight;

		if (MathF.Abs(angle) < 1e-3f && MathF.Abs(roll) < 1e-3f)
		{
			return true;
		}

		// Pitch about (forward x up), positive lifts the nose; roll about forward, positive
		// lifts the right side. The cross ORDER is load-bearing: (up x forward) flips pitch.
		var forward = span / distance;
		var axis = Vector3.Cross(forward, up);
		if (axis.LengthSquared() < 1e-8f)
		{
			return true;
		}

		// Not Quaternion.Concatenate: it multiplies in reverse order and loses the roll.
		var tilt = Quaternion.CreateFromAxisAngle(forward, roll) *
			Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), angle);

		if (Environment.GetEnvironmentVariable("DECA_TILT_DEBUG") == "1")
		{
			Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
				$"[apply] angle={angle * 180f / MathF.PI:F1} roll={roll * 180f / MathF.PI:F1} " +
				$"fwd=({forward.X:F2}|{forward.Y:F2}|{forward.Z:F2}) " +
				$"fc=({frontCenter.X:F1}|{frontCenter.Y:F1}|{frontCenter.Z:F1}) " +
				$"hc=({hindCenter.X:F1}|{hindCenter.Y:F1}|{hindCenter.Z:F1})"));
		}

		int pelvis = settings.PelvisJoint;
		int parent = skeleton.Parents[pelvis];
		var parentRotation = parent >= 0
			? Quaternion.CreateFromRotationMatrix(Orthonormal(models[parent]))
			: Quaternion.Identity;

		// Model = parent * local, so the tilt goes on the LEFT; same convention as AlignFeet.
		var modelRotation = tilt * (parentRotation * locals[pelvis].rotation);
		locals[pelvis].rotation = Quaternion.Normalize(
			Quaternion.Inverse(parentRotation) * modelRotation);

		bool applied = pose.WriteLocalTransforms(locals) && pose.LocalToModel() &&
			pose.ReadModelMatrices(models);

		if (Environment.GetEnvironmentVariable("DECA_TILT_DEBUG") == "1")
		{
			var m = models[pelvis];
			Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
				$"[after] applied={applied} pelvisRow1=({m.M11:F2}|{m.M12:F2}|{m.M13:F2}) " +
				$"row2=({m.M21:F2}|{m.M22:F2}|{m.M23:F2})"));
		}

		return applied;
	}

	// 1/s; must outrun height smoothing to grab within one stance frame.
	private const float LockRate = 25f;

	// Returns true on the entry frame. A non-cycling leg keeps a collapsed envelope and never
	// locks, so an idle foot cannot get pinned.
	private static bool UpdateLockState(FootIkLeg leg, float lift, float reach, float deltaSeconds,
		bool slowEnough)
	{
		if (!leg.EnvelopeInit)
		{
			leg.LiftMin = lift;
			leg.LiftMax = lift;
			leg.EnvelopeInit = true;
		}

		float span = MathF.Max(leg.LiftMax - leg.LiftMin, 1e-6f);
		float relax = 0.5f * span * MathF.Max(deltaSeconds, 0f);

		leg.LiftMin = MathF.Min(lift, leg.LiftMin + relax);
		leg.LiftMax = MathF.Max(lift, leg.LiftMax - relax);
		span = leg.LiftMax - leg.LiftMin;

		if (span < 0.01f * MathF.Max(reach, 1e-6f))
		{
			leg.LockActive = false;
			return false;
		}

		// Hysteresis: entry below exit, or a boundary foot re-grabs every frame.
		float enter = leg.LiftMin + 0.20f * span;
		float exit = leg.LiftMin + 0.35f * span;

		if (!leg.LockActive && lift < enter && slowEnough)
		{
			leg.LockActive = true;
			return true;
		}

		if (leg.LockActive && lift > exit)
		{
			leg.LockActive = false;
		}

		return false;
	}

	private static float ApproachValue(float value, float target, float rate, float deltaSeconds)
	{
		if (deltaSeconds <= 0f || rate <= 0f)
		{
			return value;
		}

		return value + (target - value) * (1f - MathF.Exp(-rate * deltaSeconds));
	}

	// Frame-rate-independent exponential approach: covers 1 - exp(-rate*dt) per frame.
	private static float Approach(FootIkLeg leg, float target, float rate, float deltaSeconds)
	{
		if (!leg.Initialized)
		{
			leg.SmoothedLift = target;
			leg.Initialized = true;
			return target;
		}

		float alpha = rate > 0f && deltaSeconds > 0f ? 1f - MathF.Exp(-rate * deltaSeconds) : 1f;
		leg.SmoothedLift += (target - leg.SmoothedLift) * alpha;
		return leg.SmoothedLift;
	}

	// Restores the clip's model-space foot orientation after two-bone IK, then applies the
	// up-to-normal tilt. The restore is FULL: only the tilt is weighted, since the solver's
	// plane turn is not proportional to weight.
	private static void AlignFeet(PreparedSkeleton skeleton, IReadOnlyList<FootIkLeg> legs,
		ReadOnlySpan<GroundSample> hits, ReadOnlySpan<Quaternion> footRotations,
		FootIkSettings settings, Matrix4x4 worldToModel, Vector3 up, Transform[] locals,
		Matrix4x4[] models)
	{
		float weight = Math.Clamp(settings.Weight, 0f, 1f);

		for (int i = 0; i < legs.Count; i++)
		{
			if (!hits[i].Hit)
			{
				continue;
			}

			var desired = footRotations[i];

			if (settings.AlignToNormal)
			{
				var normalModel = Vector3.TransformNormal(hits[i].Normal, worldToModel);
				if (normalModel.LengthSquared() > 1e-10f)
				{
					var correction = FromToRotation(up, Vector3.Normalize(normalModel));
					desired = Quaternion.Slerp(Quaternion.Identity, correction, weight) * desired;
				}
			}

			int foot = legs[i].FootJoint;
			int parent = skeleton.Parents[foot];
			var parentRotation = parent >= 0 ? Quaternion.CreateFromRotationMatrix(Orthonormal(models[parent]))
				: Quaternion.Identity;

			// Inverse(parent) * model, paired with the capture convention in Solve.
			locals[foot].rotation = Quaternion.Normalize(Quaternion.Inverse(parentRotation) * desired);
		}
	}

	private static Matrix4x4 Orthonormal(in Matrix4x4 matrix)
	{
		var x = Vector3.Normalize(new Vector3(matrix.M11, matrix.M12, matrix.M13));
		var y = Vector3.Normalize(new Vector3(matrix.M21, matrix.M22, matrix.M23));
		var z = Vector3.Normalize(new Vector3(matrix.M31, matrix.M32, matrix.M33));

		return new Matrix4x4(
			x.X, x.Y, x.Z, 0f,
			y.X, y.Y, y.Z, 0f,
			z.X, z.Y, z.Z, 0f,
			0f, 0f, 0f, 1f);
	}

	private static Quaternion FromToRotation(Vector3 from, Vector3 to)
	{
		float dot = Vector3.Dot(from, to);
		if (dot > 0.999999f)
		{
			return Quaternion.Identity;
		}

		if (dot < -0.999999f)
		{
			var axis = Vector3.Cross(Vector3.UnitX, from);
			if (axis.LengthSquared() < 1e-8f)
			{
				axis = Vector3.Cross(Vector3.UnitY, from);
			}

			return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
		}

		return Quaternion.Normalize(new Quaternion(Vector3.Cross(from, to), 1f + dot));
	}
}
