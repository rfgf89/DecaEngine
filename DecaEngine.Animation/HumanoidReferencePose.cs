using System;
using System.Numerics;
using DecaEngine.Core;

namespace DecaEngine.Animation;

/// <summary>Per-limb deviation from the expected T-pose direction, in degrees.</summary>
public readonly record struct HumanoidPoseReport(
	float LeftArmDegrees,
	float RightArmDegrees,
	float LeftLegDegrees,
	float RightLegDegrees,
	bool Complete)
{
	public float Worst => MathF.Max(
		MathF.Max(LeftArmDegrees, RightArmDegrees),
		MathF.Max(LeftLegDegrees, RightLegDegrees));

	/// <summary>25 degrees: an A-pose differs from T by about 45, so the threshold separates
	/// them while allowing rigs with slightly lowered arms.</summary>
	public bool LooksLikeTPose => Complete && Worst <= 25f;
}

/// <summary>Captures and validates an avatar's reference pose, which is NOT the model's bind
/// pose: rigs ship in arbitrary poses, so the author sets a T-pose and captures it explicitly.</summary>
public static class HumanoidReferencePose
{
	/// <summary>Captures the reference pose from the current local TRS, keyed by bone NAME:
	/// joint indices shift on re-export and the mapping has to survive it.</summary>
	public static void Capture(HumanoidAvatar avatar, PreparedSkeleton skeleton, Transform[] locals)
	{
		if (avatar == null || skeleton == null || locals == null)
		{
			return;
		}

		avatar.ReferenceLocals.Clear();

		int count = Math.Min(skeleton.JointCount, locals.Length);
		for (int i = 0; i < count; i++)
		{
			avatar.ReferenceLocals[skeleton.JointNames[i]] = locals[i];
		}
	}

	/// <summary>Captures the reference pose from the skeleton's BIND pose.</summary>
	public static void CaptureFromBind(HumanoidAvatar avatar, PreparedSkeleton skeleton) =>
		Capture(avatar, skeleton, skeleton?.BindLocals ?? []);

	/// <summary>Scores the reference pose: arms along X, legs along -Y. Measures LIMB DIRECTION,
	/// not per-bone rotations, which only reflect the exporter's local axis convention.</summary>
	public static HumanoidPoseReport Evaluate(HumanoidAvatar avatar, PreparedSkeleton skeleton)
	{
		if (avatar == null || skeleton == null || !avatar.HasReferencePose)
		{
			return new HumanoidPoseReport(0f, 0f, 0f, 0f, Complete: false);
		}

		var models = BuildModelMatrices(avatar, skeleton);
		if (models == null)
		{
			return new HumanoidPoseReport(0f, 0f, 0f, 0f, Complete: false);
		}

		// Engine convention: left along +X, right along -X (see HumanoidAutoMap.AssignSides).
		bool complete =
			TryDirection(avatar, skeleton, models, HumanoidBone.LeftUpperArm, HumanoidBone.LeftHand, out var leftArm) &
			TryDirection(avatar, skeleton, models, HumanoidBone.RightUpperArm, HumanoidBone.RightHand, out var rightArm) &
			TryDirection(avatar, skeleton, models, HumanoidBone.LeftUpperLeg, HumanoidBone.LeftFoot, out var leftLeg) &
			TryDirection(avatar, skeleton, models, HumanoidBone.RightUpperLeg, HumanoidBone.RightFoot, out var rightLeg);

		return new HumanoidPoseReport(
			Angle(leftArm, Vector3.UnitX),
			Angle(rightArm, -Vector3.UnitX),
			Angle(leftLeg, -Vector3.UnitY),
			Angle(rightLeg, -Vector3.UnitY),
			complete);
	}

	/// <summary>Model matrices of the reference pose; bones missing from it fall back to the
	/// bind pose, since the reference may predate a re-export that added them.</summary>
	public static Matrix4x4[]? BuildModelMatrices(HumanoidAvatar avatar, PreparedSkeleton skeleton)
	{
		if (avatar == null || skeleton == null || skeleton.JointCount == 0)
		{
			return null;
		}

		var models = new Matrix4x4[skeleton.JointCount];

		for (int i = 0; i < skeleton.JointCount; i++)
		{
			if (!avatar.ReferenceLocals.TryGetValue(skeleton.JointNames[i], out var local))
			{
				local = skeleton.BindLocals[i];
			}

			var matrix = Matrix4x4.CreateScale(local.scale) *
				Matrix4x4.CreateFromQuaternion(local.rotation) *
				Matrix4x4.CreateTranslation(local.position);

			int parent = skeleton.Parents[i];
			models[i] = parent >= 0 ? matrix * models[parent] : matrix;
		}

		return models;
	}

	private static bool TryDirection(HumanoidAvatar avatar, PreparedSkeleton skeleton, Matrix4x4[] models,
		HumanoidBone from, HumanoidBone to, out Vector3 direction)
	{
		direction = Vector3.Zero;

		if (!avatar.IsAssigned(from) || !avatar.IsAssigned(to))
		{
			return false;
		}

		int fromJoint = skeleton.FindJoint(avatar[from]);
		int toJoint = skeleton.FindJoint(avatar[to]);

		if (fromJoint < 0 || toJoint < 0)
		{
			return false;
		}

		var delta = models[toJoint].Translation - models[fromJoint].Translation;
		if (delta.LengthSquared() < 1e-10f)
		{
			return false;
		}

		direction = Vector3.Normalize(delta);
		return true;
	}

	private static float Angle(Vector3 direction, Vector3 expected)
	{
		if (direction.LengthSquared() < 1e-10f)
		{
			return 180f;
		}

		float dot = Math.Clamp(Vector3.Dot(direction, expected), -1f, 1f);
		return MathF.Acos(dot) * (180f / MathF.PI);
	}
}
