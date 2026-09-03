using System;
using System.Collections.Generic;
using System.Numerics;
using DecaEngine.Core;

namespace DecaEngine.Animation;

/// <summary>Secondary-motion bone chain, ordered root to tip; each joint must be the direct
/// child of the previous one.</summary>
public sealed class SpringBoneChain
{
	public int[] Joints = [];

	/// <summary>Pull back towards the animated pose per step, 0..1; 1 disables the effect.</summary>
	public float Stiffness = 0.08f;

	/// <summary>Velocity lost per step, 0..1.</summary>
	public float Drag = 0.2f;

	/// <summary>External force in MODEL space, usually gravity.</summary>
	public Vector3 Gravity = Vector3.Zero;

	/// <summary>Length of the virtual tail bone; zero leaves the last joint rigid.</summary>
	public float TailLength;

	// Verlet rather than position+velocity: a root teleport cannot fling the chain, since no
	// velocity survives the position swap.
	internal Vector3[] Tips = [];
	internal Vector3[] PreviousTips = [];
	internal bool Initialized;

	/// <summary>Resets the simulation to the current pose; required after a teleport.</summary>
	public void Reset() => Initialized = false;
}

/// <summary>Secondary-motion solver; must run last, after animation, blending and IK.</summary>
public static class SpringBones
{
	/// <summary>Steps every chain, editing locals and the chain bones' model matrices in place.</summary>
	public static void Solve(PreparedSkeleton skeleton, IReadOnlyList<SpringBoneChain> chains,
		Transform[] locals, Matrix4x4[] models, float deltaSeconds)
	{
		if (skeleton == null || chains == null || locals == null || models == null || deltaSeconds <= 0f)
		{
			return;
		}

		foreach (var chain in chains)
		{
			SolveChain(skeleton, chain, locals, models, deltaSeconds);
		}
	}

	private static void SolveChain(PreparedSkeleton skeleton, SpringBoneChain chain, Transform[] locals,
		Matrix4x4[] models, float deltaSeconds)
	{
		int count = chain.Joints.Length;
		if (count == 0)
		{
			return;
		}

		if (!chain.Initialized || chain.Tips.Length != count)
		{
			chain.Tips = new Vector3[count];
			chain.PreviousTips = new Vector3[count];

			for (int i = 0; i < count; i++)
			{
				chain.Tips[i] = AnimatedTip(skeleton, chain, models, i);
				chain.PreviousTips[i] = chain.Tips[i];
			}

			chain.Initialized = true;
			return;
		}

		for (int i = 0; i < count; i++)
		{
			int joint = chain.Joints[i];
			var head = models[joint].Translation;
			var animatedTip = AnimatedTip(skeleton, chain, models, i);

			float length = Vector3.Distance(head, animatedTip);
			if (length < 1e-5f)
			{
				// A zero-length bone defines no direction to rotate around.
				chain.Tips[i] = animatedTip;
				chain.PreviousTips[i] = animatedTip;
				continue;
			}

			// Squaring the step keeps the trajectory identical across simulation rates.
			var inertia = (chain.Tips[i] - chain.PreviousTips[i]) * (1f - chain.Drag);
			var next = chain.Tips[i] + inertia + chain.Gravity * (deltaSeconds * deltaSeconds);

			next = Vector3.Lerp(next, animatedTip, Math.Clamp(chain.Stiffness, 0f, 1f));

			// Rigid constraint: the tip must keep its distance from the bone head.
			var direction = next - head;
			float distance = direction.Length();
			next = distance > 1e-5f ? head + direction * (length / distance) : animatedTip;

			chain.PreviousTips[i] = chain.Tips[i];
			chain.Tips[i] = next;

			ApplyRotation(skeleton, locals, models, joint, head, animatedTip, next);

			RefreshDescendants(skeleton, chain, locals, models, i);
		}
	}

	private static Vector3 AnimatedTip(PreparedSkeleton skeleton, SpringBoneChain chain, Matrix4x4[] models, int index)
	{
		int joint = chain.Joints[index];

		if (index + 1 < chain.Joints.Length)
		{
			return models[chain.Joints[index + 1]].Translation;
		}

		// The bone's own local translation approximates the direction a child would grow in.
		var axis = skeleton.BindLocals[joint].position;
		if (axis.LengthSquared() < 1e-10f)
		{
			axis = Vector3.UnitY;
		}

		axis = Vector3.Normalize(axis);
		var model = models[joint];
		var direction = Vector3.TransformNormal(axis, model);

		return model.Translation + Vector3.Normalize(direction) * chain.TailLength;
	}

	private static void ApplyRotation(PreparedSkeleton skeleton, Transform[] locals, Matrix4x4[] models,
		int joint, Vector3 head, Vector3 animatedTip, Vector3 newTip)
	{
		var from = animatedTip - head;
		var to = newTip - head;

		if (from.LengthSquared() < 1e-10f || to.LengthSquared() < 1e-10f)
		{
			return;
		}

		var correction = FromToRotation(Vector3.Normalize(from), Vector3.Normalize(to));

		int parent = skeleton.Parents[joint];
		var parentRotation = parent >= 0 ? RotationOf(models[parent]) : Quaternion.Identity;

		// Correction applies on the left in model space, then the parent rotation is undone.
		var modelRotation = correction * (locals[joint].rotation * parentRotation);
		locals[joint].rotation = Quaternion.Normalize(modelRotation * Quaternion.Inverse(parentRotation));

		models[joint] = Compose(locals[joint], parent >= 0 ? models[parent] : Matrix4x4.Identity);
	}

	// Only the chain below the edited bone: spring chains are leaves, a full pass would be waste.
	private static void RefreshDescendants(PreparedSkeleton skeleton, SpringBoneChain chain, Transform[] locals,
		Matrix4x4[] models, int index)
	{
		for (int i = index + 1; i < chain.Joints.Length; i++)
		{
			int joint = chain.Joints[i];
			int parent = skeleton.Parents[joint];
			models[joint] = Compose(locals[joint], parent >= 0 ? models[parent] : Matrix4x4.Identity);
		}
	}

	private static Matrix4x4 Compose(in Transform local, in Matrix4x4 parent) =>
		Matrix4x4.CreateScale(local.scale)
		* Matrix4x4.CreateFromQuaternion(local.rotation)
		* Matrix4x4.CreateTranslation(local.position)
		* parent;

	// Rows are normalised first: CreateFromRotationMatrix on a scaled basis returns garbage.
	private static Quaternion RotationOf(in Matrix4x4 matrix)
	{
		var normalized = matrix;
		normalized.Translation = Vector3.Zero;

		var x = Vector3.Normalize(new Vector3(normalized.M11, normalized.M12, normalized.M13));
		var y = Vector3.Normalize(new Vector3(normalized.M21, normalized.M22, normalized.M23));
		var z = Vector3.Normalize(new Vector3(normalized.M31, normalized.M32, normalized.M33));

		normalized.M11 = x.X; normalized.M12 = x.Y; normalized.M13 = x.Z;
		normalized.M21 = y.X; normalized.M22 = y.Y; normalized.M23 = y.Z;
		normalized.M31 = z.X; normalized.M32 = z.Y; normalized.M33 = z.Z;

		return Quaternion.CreateFromRotationMatrix(normalized);
	}

	// Anti-parallel inputs are special-cased: the rotation axis degenerates to zero there.
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

		var cross = Vector3.Cross(from, to);
		return Quaternion.Normalize(new Quaternion(cross, 1f + dot));
	}
}
