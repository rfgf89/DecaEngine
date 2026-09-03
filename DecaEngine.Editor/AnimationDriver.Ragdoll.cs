using System;
using System.Collections.Generic;
using System.Numerics;
using DecaEngine.Animation;
using DecaEngine.Core;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
using DecaEngine.Physics;
using DecaEngine.Scene;
using Friflo.Engine.ECS;

// Friflo defines its own Transform component; the alias resolves the ambiguity.
using Transform = DecaEngine.Core.Transform;

namespace DecaEngine.Editor;

/// <summary>Ragdoll part of <see cref="AnimationDriver"/>: Bepu sync, body description build, bone radii and mass.</summary>
public sealed partial class AnimationDriver
{
	// Must run as the LAST pose stage: the ragdoll either targets or replaces the final pose.
	private void SyncRagdoll(Entity entity, Character character, float deltaSeconds)
	{
		bool wanted = Physics != null && entity.HasComponent<RagdollComponent>();
		var settings = wanted ? entity.GetComponent<RagdollComponent>() : default;

		// Hit reaction runs even with the component disabled; the envelope ticks here so a
		// reaction that never gets a ragdoll frame still expires instead of hanging forever.
		bool reacting = wanted && character.ReactionDuration > 0f;
		if (reacting)
		{
			character.ReactionElapsed += deltaSeconds;
			if (character.ReactionElapsed >= character.ReactionDuration)
			{
				character.ReactionDuration = 0f;
				character.ReactionImpulsePending = false;
				reacting = false;
			}
		}

		character.ReactionWeight = 0f;
		character.ReactionDeviation = 0f;

		if (!wanted || (!settings.Enabled && !reacting))
		{
			DestroyRagdoll(character);
			return;
		}

		// Physical mode only once statics exist: released before the streamed floor reaches
		// physics (see ScenePhysics.HasStatics), the ragdoll falls forever with no way back.
		bool physical = settings.Physical && Physics!.HasStatics;

		float worldScale = WorldScaleOf(character.ModelToWorld);

		if (!character.RagdollBuilt || !SameRagdollSource(character.RagdollSource, settings) ||
			!SameScale(character.RagdollBuildScale, worldScale))
		{
			DestroyRagdoll(character);
			BuildRagdoll(character, settings);

			character.RagdollSource = settings;
			character.RagdollBuildScale = worldScale;
			character.RagdollBuilt = true;
		}

		var ragdoll = character.Ragdoll;
		if (ragdoll == null)
		{
			return;
		}

		ActiveRagdollCount++;

		// Servo target must be computed BEFORE reading bodies: physical mode overwrites Models.
		for (int i = 0; i < character.Models.Length; i++)
		{
			character.JointWorld[i] = character.Models[i] * character.ModelToWorld;
		}

		// Reaction = physics with strong servos pulling back to animation; Physical mode wins over it.
		bool reactionDrives = reacting && !physical;

		ragdoll.SetAnimationDriven(!physical && !reactionDrives);
		ragdoll.DriveToPose(character.JointWorld, deltaSeconds,
			reactionDrives ? ReactionServoStrength : settings.ServoStrength);

		if (reactionDrives && character.ReactionImpulsePending)
		{
			EnsureReactionMask(character);
			ragdoll.AddVelocity(character.ReactionImpulse, character.ReactionMask);
			character.ReactionImpulsePending = false;
		}

		if (physical)
		{
			ReadRagdollPose(character, ragdoll);
		}
		else if (reactionDrives)
		{
			BlendReactionPose(character, ragdoll);
		}
	}

	// ~60 as in typical active-ragdoll demos: recovers in fractions of a second, push still visible.
	private const float ReactionServoStrength = 60f;

	// Blends decomposed TRS, not matrices: lerping rotation matrices squashes bones halfway.
	private static void BlendReactionPose(Character character, Ragdoll ragdoll)
	{
		EnsureReactionMask(character);

		if (character.ReactionAnimated.Length != character.Models.Length)
		{
			character.ReactionAnimated = new Matrix4x4[character.Models.Length];
		}

		character.Models.CopyTo(character.ReactionAnimated, 0);
		ReadRagdollPose(character, ragdoll);

		// Envelope: fast attack so the push reads immediately, smooth release to the end.
		float t = Math.Clamp(character.ReactionElapsed / character.ReactionDuration, 0f, 1f);
		float attack = Math.Clamp(character.ReactionElapsed / ReactionAttackSeconds, 0f, 1f);
		float release = 1f - t * t * (3f - 2f * t);
		float envelope = character.ReactionStrength * attack * release;

		character.ReactionWeight = envelope;

		float deviation = 0f;

		for (int i = 0; i < character.Models.Length; i++)
		{
			float weight = envelope * character.ReactionMask[i];
			var animated = character.ReactionAnimated[i];

			if (weight <= 1e-4f)
			{
				character.Models[i] = animated;
				continue;
			}

			if (!Matrix4x4.Decompose(character.Models[i], out var scale, out var rotation, out var translation) ||
				!Matrix4x4.Decompose(animated, out var animScale, out var animRotation, out var animTranslation))
			{
				character.Models[i] = animated;
				continue;
			}

			character.Models[i] = MathUtils.CreateTrs(
				Vector3.Lerp(animTranslation, translation, weight),
				Quaternion.Slerp(animRotation, rotation, weight),
				Vector3.Lerp(animScale, scale, weight));

			deviation = MathF.Max(deviation,
				Vector3.Distance(character.Models[i].Translation, animTranslation));
		}

		character.ReactionDeviation = deviation;
		character.Models.CopyTo(character.Managed.ModelMatrices, 0);
	}

	// Limbs and their subtrees zeroed, hips damped; without an avatar the mask is all ones.
	private static void EnsureReactionMask(Character character)
	{
		if (character.ReactionMaskBuilt && character.ReactionMask.Length == character.Skeleton.JointCount)
		{
			return;
		}

		int count = character.Skeleton.JointCount;
		character.ReactionMask = new float[count];
		Array.Fill(character.ReactionMask, 1f);

		if (character.Avatar != null)
		{
			ReadOnlySpan<HumanoidBone> limbs =
			[
				HumanoidBone.LeftUpperArm, HumanoidBone.LeftLowerArm, HumanoidBone.LeftHand,
				HumanoidBone.RightUpperArm, HumanoidBone.RightLowerArm, HumanoidBone.RightHand,
				HumanoidBone.LeftUpperLeg, HumanoidBone.LeftLowerLeg, HumanoidBone.LeftFoot,
				HumanoidBone.RightUpperLeg, HumanoidBone.RightLowerLeg, HumanoidBone.RightFoot,
			];

			foreach (var slot in limbs)
			{
				int joint = character.Skeleton.FindJoint(character.Avatar[slot] ?? string.Empty);
				if (joint >= 0)
				{
					character.ReactionMask[joint] = 0f;
				}
			}

			int hips = character.Skeleton.FindJoint(character.Avatar[HumanoidBone.Hips] ?? string.Empty);
			if (hips >= 0)
			{
				character.ReactionMask[hips] = 0.3f;
			}

			// Joints are topologically ordered, one pass suffices; only a ZERO parent zeroes its
			// child - the damped hips must not mute the torso growing out of them.
			var parents = character.Skeleton.Parents;
			for (int i = 0; i < count; i++)
			{
				if (parents[i] >= 0 && character.ReactionMask[parents[i]] == 0f)
				{
					character.ReactionMask[i] = 0f;
				}
			}
		}

		character.ReactionMaskBuilt = true;
	}

	// Joints without a body are rebuilt from parent local TRS or they'd stay in the animated pose;
	// joints are topologically ordered, so one pass is enough.
	private static void ReadRagdollPose(Character character, Ragdoll ragdoll)
	{
		if (!Matrix4x4.Invert(character.ModelToWorld, out var worldToModel))
		{
			return;
		}

		character.JointWorld.CopyTo(character.RagdollWorld, 0);
		ragdoll.ReadPose(character.RagdollWorld);

		Array.Clear(character.RagdollOwned);
		for (int i = 0; i < ragdoll.BoneCount; i++)
		{
			character.RagdollOwned[ragdoll.JointOf(i)] = true;
		}

		var parents = character.Skeleton.Parents;

		// Bepu poses are rigid (unit scale) while worldToModel carries 1/scale; pre-multiplying by
		// scale cancels it in the linear part without touching translation (its row is identity).
		var counterScale = Matrix4x4.CreateScale(WorldScaleOf(character.ModelToWorld));

		for (int i = 0; i < character.Models.Length; i++)
		{
			if (character.RagdollOwned[i])
			{
				character.Models[i] = counterScale * character.RagdollWorld[i] * worldToModel;
				continue;
			}

			var local = character.Locals[i];
			var localMatrix = MathUtils.CreateTrs(
				local.position, local.rotation, local.scale);

			character.Models[i] = parents[i] >= 0
				? localMatrix * character.Models[parents[i]]
				: localMatrix;
		}

		character.Models.CopyTo(character.Managed.ModelMatrices, 0);
	}

	private static void DestroyRagdoll(Character character)
	{
		character.Ragdoll?.Destroy();
		character.Ragdoll = null;
		character.RagdollBuilt = false;
	}

	// Average of the axis lengths: the ragdoll is built isotropic (Bepu capsules can't scale per-axis).
	private static float WorldScaleOf(in Matrix4x4 transform)
	{
		float x = new Vector3(transform.M11, transform.M12, transform.M13).Length();
		float y = new Vector3(transform.M21, transform.M22, transform.M23).Length();
		float z = new Vector3(transform.M31, transform.M32, transform.M33).Length();

		return (x + y + z) / 3f;
	}

	// Relative compare with a dead zone: decomposed scale jitters ~1e-7 and an exact compare
	// would rebuild the ragdoll (restart the fall) every single frame.
	private static bool SameScale(float a, float b) =>
		MathF.Abs(a - b) <= 1e-3f * MathF.Max(MathF.Abs(a), MathF.Abs(b));

	// Structure only: Physical/ServoStrength are mode knobs, rebuilding on them would reset the fall.
	private static bool SameRagdollSource(in RagdollComponent a, in RagdollComponent b) =>
		string.Equals(a.RootJoint, b.RootJoint, StringComparison.Ordinal) &&
		a.MaxDepth == b.MaxDepth && a.BoneRadius == b.BoneRadius && a.TotalMass == b.TotalMass;

	private void BuildRagdoll(Character character, in RagdollComponent settings)
	{
		if (Physics == null)
		{
			return;
		}

		var description = BuildRagdollDescription(character, settings, WorldScaleOf(character.ModelToWorld));
		if (description.Count < 2)
		{
			// A one-bone ragdoll is just a falling capsule; refuse so a bad root joint stays visible.
			return;
		}

		for (int i = 0; i < character.Models.Length; i++)
		{
			character.JointWorld[i] = character.Models[i] * character.ModelToWorld;
		}

		MarkHingeBones(character, description);

		character.Ragdoll = Ragdoll.Build(Physics.World,
			System.Runtime.InteropServices.CollectionsMarshal.AsSpan(description), character.JointWorld);
	}

	// Knees/elbows become hinges: a ball-socket cone would let them bend backwards. Axis and range
	// come from the build pose; without an avatar the joint stays conical.
	private static void MarkHingeBones(Character character, List<RagdollBoneDesc> description)
	{
		if (character.Avatar == null)
		{
			return;
		}

		ReadOnlySpan<HumanoidBone> hinges =
		[
			HumanoidBone.LeftLowerLeg, HumanoidBone.RightLowerLeg,
			HumanoidBone.LeftLowerArm, HumanoidBone.RightLowerArm,
		];

		foreach (var slot in hinges)
		{
			int joint = character.Skeleton.FindJoint(character.Avatar[slot] ?? string.Empty);
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

				// "Upper" point is the parent RAGDOLL bone's joint, not the skeleton parent:
				// the hinge links these two bodies, a skipped link would give the wrong axis.
				Ragdoll.MarkHinge(ref bone,
					character.JointWorld[description[bone.Parent].Joint].Translation,
					character.JointWorld[bone.Joint].Translation,
					character.JointWorld[bone.ChildJoint].Translation);

				description[i] = bone;
				break;
			}
		}
	}

	// Only visited joints WITH a child become bones; leaf joints (finger tips, head top) only
	// set the parent capsule's length. Depth is authored via MaxDepth.
	private static List<RagdollBoneDesc> BuildRagdollDescription(Character character,
		in RagdollComponent settings, float worldScale)
	{
		var result = new List<RagdollBoneDesc>();
		var skeleton = character.Skeleton;

		// Root: authored joint, else avatar hips, else skeleton root (a rig with a helper
		// "Armature" root then gets one junk link - still better than not building at all).
		string rootName = JointOf(character, settings.RootJoint, HumanoidBone.Hips);
		int root = string.IsNullOrEmpty(rootName) ? 0 : skeleton.FindJoint(rootName);

		if (root < 0)
		{
			return result;
		}

		// Per-bone capsule radius is measured from the mesh; authored BoneRadius > 0 forces a
		// single radius for the whole skeleton (rigs without a skin stream, stylization).
		float authoredRadius = settings.BoneRadius;
		var meshRadii = authoredRadius > 0f ? [] : MeasureBoneRadii(character);

		// Radii are in MODEL units and scaled to world here; bone lengths arrive already
		// world-scaled from the joint world matrices. Fallback is relative to skeleton size.
		float RadiusOf(int joint)
		{
			if (authoredRadius > 0f)
			{
				return authoredRadius * worldScale;
			}

			float measured = joint < meshRadii.Length ? meshRadii[joint] : 0f;
			return (measured > 1e-4f ? measured : character.Scale * 0.12f) * worldScale;
		}

		// Maps joint -> ragdoll bone: between two ragdoll bones there are usually skipped links.
		var boneOfJoint = new Dictionary<int, int>();

		var queue = new Queue<(int Joint, int Depth, int ParentBone)>();
		queue.Enqueue((root, 0, -1));

		while (queue.Count > 0)
		{
			var (joint, depth, parentBone) = queue.Dequeue();

			int child = FirstChild(skeleton, joint);
			int bone = parentBone;

			if (child >= 0)
			{
				bone = result.Count;
				boneOfJoint[joint] = bone;

				result.Add(new RagdollBoneDesc
				{
					Joint = joint,
					ChildJoint = child,
					Parent = parentBone,
					Radius = RadiusOf(joint),

					// Fallback length for end bones (head, hand) - in world units, like the radii.
					Length = character.Scale * worldScale,

					// 120-degree swing: limbs can settle naturally but can't fold back through the joint.
					SwingLimitCos = -0.5f,

					// Twist is a separate DOF the cone never limits; ~50 deg is a plausible per-link cap.
					TwistLimitAngle = 50f * (MathF.PI / 180f),
				});
			}

			if (depth >= settings.MaxDepth)
			{
				continue;
			}

			for (int i = joint + 1; i < skeleton.JointCount; i++)
			{
				if (skeleton.Parents[i] == joint)
				{
					queue.Enqueue((i, depth + 1, bone));
				}
			}
		}

		DistributeMass(result, settings.TotalMass);
		return result;
	}

	// Weighted MEAN vertex distance to the bone axis, in model units, in bind pose: a max would
	// catch vertices of adjacent body parts. Influences below 0.3 are ignored (seam vertices).
	private static unsafe float[] MeasureBoneRadii(Character character)
	{
		var skeleton = character.Skeleton;
		int count = skeleton.JointCount;

		// Bind-pose matrices: the managed pose already holds the current clip frame, radii would drift.
		var bind = new Matrix4x4[count];
		for (int i = 0; i < count; i++)
		{
			var local = skeleton.BindLocals[i];
			var matrix = MathUtils.CreateTrs(
				local.position, local.rotation, local.scale);
			bind[i] = skeleton.Parents[i] >= 0 ? matrix * bind[skeleton.Parents[i]] : matrix;
		}

		var sum = new float[count];
		var weight = new float[count];
		var model = character.Model;

		void Accumulate(int joint, ushort rawWeight, Vector3 position)
		{
			float w = rawWeight / SkinVertex.WeightScale;
			if (w < 0.3f || joint >= count)
			{
				return;
			}

			var start = bind[joint].Translation;
			int child = FirstChild(skeleton, joint);
			var end = child >= 0 ? bind[child].Translation : start;

			var axis = end - start;
			float lengthSq = axis.LengthSquared();
			float t = lengthSq > 1e-8f
				? Math.Clamp(Vector3.Dot(position - start, axis) / lengthSq, 0f, 1f)
				: 0f;

			sum[joint] += Vector3.Distance(position, start + axis * t) * w;
			weight[joint] += w;
		}

		for (int meshIndex = 0; meshIndex < model.Meshes.Count; meshIndex++)
		{
			var skinStream = meshIndex < model.MeshSkin.Count ? model.MeshSkin[meshIndex] : null;
			var mesh = model.Meshes[meshIndex];

			if (skinStream == null || mesh.VertexData == null)
			{
				continue;
			}

			int vertexCount = Math.Min(
				UnsafeCollections.Collections.Unsafe.UnsafeArray.GetLength(mesh.VertexData),
				skinStream.Length);
			var vertices = new ReadOnlySpan<Vertex>(
				UnsafeCollections.Collections.Unsafe.UnsafeArray.GetPtr<Vertex>(mesh.VertexData, 0),
				vertexCount);

			for (int v = 0; v < vertexCount; v++)
			{
				var s = skinStream[v];
				var position = vertices[v].Position;

				Accumulate(s.J0, s.W0, position);
				Accumulate(s.J1, s.W1, position);
				Accumulate(s.J2, s.W2, position);
				Accumulate(s.J3, s.W3, position);
			}
		}

		var radii = new float[count];
		for (int i = 0; i < count; i++)
		{
			radii[i] = weight[i] > 0f ? sum[i] / weight[i] : 0f;
		}

		return radii;
	}

	// Mass proportional to capsule volume: an equal split makes the head as heavy as the pelvis.
	private static void DistributeMass(List<RagdollBoneDesc> bones, float totalMass)
	{
		if (bones.Count == 0)
		{
			return;
		}

		float mass = totalMass > 0f ? totalMass : 70f;
		float sum = 0f;

		Span<float> volumes = bones.Count <= 64 ? stackalloc float[bones.Count] : new float[bones.Count];

		for (int i = 0; i < bones.Count; i++)
		{
			float radius = MathF.Max(bones[i].Radius, 1e-4f);
			volumes[i] = radius * radius * MathF.Max(bones[i].Length, radius);
			sum += volumes[i];
		}

		for (int i = 0; i < bones.Count; i++)
		{
			var bone = bones[i];
			bone.Mass = sum > 0f ? mass * (volumes[i] / sum) : mass / bones.Count;
			bones[i] = bone;
		}
	}

}
