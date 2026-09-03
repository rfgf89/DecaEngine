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

// Friflo ships its own Transform component; the alias resolves the ambiguity.
using Transform = DecaEngine.Core.Transform;

namespace DecaEngine.Editor;

/// <summary>Procedural animation passes: foot IK and spring bones.</summary>
public sealed partial class AnimationDriver
{
	// Must run after look-at and before spring bones: secondary motion needs the final pose.
	private void ApplyFootIk(Entity entity, Character character, float deltaSeconds)
	{
		if (character.Pose == null || Physics == null || !entity.HasComponent<FootIkComponent>())
		{
			character.LegsBuilt = false;
			character.Legs.Clear();
			return;
		}

		var settings = entity.GetComponent<FootIkComponent>();
		if (!settings.Enabled || settings.Weight <= 0f)
		{
			return;
		}

		if (!character.LegsBuilt || !SameLegSource(character.LegSource, settings))
		{
			BuildLegs(character, settings);
			character.LegSource = settings;
			character.LegsBuilt = true;
		}

		if (character.Legs.Count == 0)
		{
			return;
		}

		// Numeric knobs refresh per frame; the leg set only on rebuild, which resets smoothing.
		character.IkSettings.Weight = Math.Clamp(settings.Weight, 0f, 1f);
		character.IkSettings.MaxPelvisDrop = settings.MaxPelvisDrop;
		character.IkSettings.Smoothing = settings.Smoothing;
		character.IkSettings.AlignToNormal = settings.AlignToNormal;
		character.IkSettings.LockFeet = settings.LockFeet;
		character.IkSettings.AlignBodyToSlope = settings.AlignBodyToSlope;

		foreach (var leg in character.Legs)
		{
			leg.AnkleHeight = settings.AnkleHeight;
		}

		var physics = Physics;

		character.IkApplied = FootIk.Solve(character.Pose, character.Skeleton, character.Legs,
			character.IkSettings, character.ModelToWorld, character.Locals, character.Models,
			(origin, direction, maximumT) => physics.SampleGround(origin, direction, maximumT),
			deltaSeconds);

		if (character.IkApplied)
		{
			character.Models.CopyTo(character.Managed.ModelMatrices, 0);
		}
	}

	// Authored name wins over the avatar mapping, which is auto-generated and can guess wrong.
	private static string JointOf(Character character, string authored, HumanoidBone slot) =>
		!string.IsNullOrEmpty(authored) ? authored : character.Avatar?[slot] ?? string.Empty;

	private static void BuildLegs(Character character, in FootIkComponent settings)
	{
		character.Legs.Clear();

		string pelvis = JointOf(character, settings.PelvisJoint, HumanoidBone.Hips);

		character.IkSettings.PelvisJoint = string.IsNullOrEmpty(pelvis)
			? -1
			: character.Skeleton.FindJoint(pelvis);

		// Ray length is in skeleton scale, not metres; an absolute constant misses the floor.
		// Start well above the foot: a ray born inside terrain passes through one-sided meshes.
		character.IkSettings.ProbeUp = character.Scale * 3f;
		character.IkSettings.ProbeDown = character.Scale * 2f;

		// Toe is the contact point on digitigrade rigs, where the mapped "foot" is the hock.
		AddLeg(character,
			JointOf(character, settings.LeftUpperJoint, HumanoidBone.LeftUpperLeg),
			JointOf(character, settings.LeftLowerJoint, HumanoidBone.LeftLowerLeg),
			JointOf(character, settings.LeftFootJoint, HumanoidBone.LeftFoot),
			JointOf(character, settings.LeftToeJoint, HumanoidBone.LeftToes));

		AddLeg(character,
			JointOf(character, settings.RightUpperJoint, HumanoidBone.RightUpperLeg),
			JointOf(character, settings.RightLowerJoint, HumanoidBone.RightLowerLeg),
			JointOf(character, settings.RightFootJoint, HumanoidBone.RightFoot),
			JointOf(character, settings.RightToeJoint, HumanoidBone.RightToes),
			right: true);

		// Quadruped front legs live in the ARM slots; opt-in, since on a biped those are arms.
		if (settings.FrontLegs)
		{
			AddLeg(character,
				JointOf(character, settings.FrontLeftUpperJoint, HumanoidBone.LeftUpperArm),
				JointOf(character, settings.FrontLeftLowerJoint, HumanoidBone.LeftLowerArm),
				JointOf(character, settings.FrontLeftFootJoint, HumanoidBone.LeftHand),
				settings.FrontLeftToeJoint,
				front: true);

			AddLeg(character,
				JointOf(character, settings.FrontRightUpperJoint, HumanoidBone.RightUpperArm),
				JointOf(character, settings.FrontRightLowerJoint, HumanoidBone.RightLowerArm),
				JointOf(character, settings.FrontRightFootJoint, HumanoidBone.RightHand),
				settings.FrontRightToeJoint,
				front: true, right: true);
		}
	}

	// All three joints required: a missing one would index the pose with -1. Toe is optional.
	private static void AddLeg(Character character, string upper, string lower, string foot,
		string toe = "", bool front = false, bool right = false)
	{
		int upperJoint = character.Skeleton.FindJoint(upper ?? string.Empty);
		int lowerJoint = character.Skeleton.FindJoint(lower ?? string.Empty);
		int footJoint = character.Skeleton.FindJoint(foot ?? string.Empty);

		if (upperJoint < 0 || lowerJoint < 0 || footJoint < 0)
		{
			return;
		}

		character.Legs.Add(new FootIkLeg
		{
			UpperJoint = upperJoint,
			LowerJoint = lowerJoint,
			FootJoint = footJoint,
			ToeJoint = character.Skeleton.FindJoint(toe ?? string.Empty),
			Front = front,
			Right = right,
		});
	}

	private static bool SameLegSource(in FootIkComponent a, in FootIkComponent b) =>
		a.FrontLegs == b.FrontLegs &&
		string.Equals(a.PelvisJoint, b.PelvisJoint, StringComparison.Ordinal) &&
		string.Equals(a.LeftUpperJoint, b.LeftUpperJoint, StringComparison.Ordinal) &&
		string.Equals(a.LeftLowerJoint, b.LeftLowerJoint, StringComparison.Ordinal) &&
		string.Equals(a.LeftFootJoint, b.LeftFootJoint, StringComparison.Ordinal) &&
		string.Equals(a.RightUpperJoint, b.RightUpperJoint, StringComparison.Ordinal) &&
		string.Equals(a.RightLowerJoint, b.RightLowerJoint, StringComparison.Ordinal) &&
		string.Equals(a.RightFootJoint, b.RightFootJoint, StringComparison.Ordinal) &&
		string.Equals(a.FrontLeftUpperJoint, b.FrontLeftUpperJoint, StringComparison.Ordinal) &&
		string.Equals(a.FrontLeftLowerJoint, b.FrontLeftLowerJoint, StringComparison.Ordinal) &&
		string.Equals(a.FrontLeftFootJoint, b.FrontLeftFootJoint, StringComparison.Ordinal) &&
		string.Equals(a.FrontRightUpperJoint, b.FrontRightUpperJoint, StringComparison.Ordinal) &&
		string.Equals(a.FrontRightLowerJoint, b.FrontRightLowerJoint, StringComparison.Ordinal) &&
		string.Equals(a.FrontRightFootJoint, b.FrontRightFootJoint, StringComparison.Ordinal) &&
		string.Equals(a.LeftToeJoint, b.LeftToeJoint, StringComparison.Ordinal) &&
		string.Equals(a.RightToeJoint, b.RightToeJoint, StringComparison.Ordinal) &&
		string.Equals(a.FrontLeftToeJoint, b.FrontLeftToeJoint, StringComparison.Ordinal) &&
		string.Equals(a.FrontRightToeJoint, b.FrontRightToeJoint, StringComparison.Ordinal);

	// --- Spring bones ------------------------------------------------------------------------------

	private static void ApplySpringBones(Entity entity, Character character, float deltaSeconds)
	{
		if (!entity.HasComponent<SpringBoneComponent>())
		{
			character.ChainsBuilt = false;
			character.Chains.Clear();
			return;
		}

		var settings = entity.GetComponent<SpringBoneComponent>();
		if (!settings.Enabled || string.IsNullOrEmpty(settings.RootJoint) || settings.Length < 2)
		{
			return;
		}

		if (!character.ChainsBuilt || !SameChainSource(character.ChainSource, settings))
		{
			character.Chains.Clear();

			var joints = BuildChain(character.Skeleton, settings.RootJoint, settings.Length);
			if (joints.Length >= 2)
			{
				character.Chains.Add(new SpringBoneChain { Joints = joints });
			}

			character.ChainSource = settings;
			character.ChainsBuilt = true;
		}

		foreach (var chain in character.Chains)
		{
			// Numeric params refresh per frame; the joint list only on rebuild, which drops inertia.
			chain.Stiffness = settings.Stiffness;
			chain.Drag = settings.Drag;
			chain.TailLength = settings.TailLength;
			chain.Gravity = settings.Gravity;
		}

		SpringBones.Solve(character.Skeleton, character.Chains, character.Locals, character.Models, deltaSeconds);
		character.Models.CopyTo(character.Managed.ModelMatrices, 0);
	}

	// Structure only: numeric params are excluded so editing them does not drop inertia.
	private static bool SameChainSource(in SpringBoneComponent a, in SpringBoneComponent b) =>
		string.Equals(a.RootJoint, b.RootJoint, StringComparison.Ordinal) && a.Length == b.Length;

	// Follows the first child only: a spring chain is linear, forks would drag in half the skeleton.
	private static int[] BuildChain(PreparedSkeleton skeleton, string rootName, int length)
	{
		int root = skeleton.FindJoint(rootName);
		if (root < 0)
		{
			return [];
		}

		var chain = new List<int> { root };
		int current = root;

		while (chain.Count < length)
		{
			int child = FirstChild(skeleton, current);
			if (child < 0)
			{
				break;
			}

			chain.Add(child);
			current = child;
		}

		return chain.ToArray();
	}

	// Joints are topologically ordered, so children always follow their parent.
	private static int FirstChild(PreparedSkeleton skeleton, int joint)
	{
		for (int i = joint + 1; i < skeleton.JointCount; i++)
		{
			if (skeleton.Parents[i] == joint)
			{
				return i;
			}
		}

		return -1;
	}
}
