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

// Friflo ships its own Transform component; the alias disambiguates it from the engine TRS.
using Transform = DecaEngine.Core.Transform;

namespace DecaEngine.Editor;

/// <summary>Layers on top of the base clip: overlay, additive clip and look-at.</summary>
public sealed partial class AnimationDriver
{
	// Partial blend (ozz partial_blend): runs AFTER the base clip and BEFORE look-at/foot IK.
	// Per-joint weights are complementary, so outside the subtree the base passes through intact.
	private void ApplyOverlayClip(Entity entity, Character character, float deltaSeconds)
	{
		if (character.Pose == null || !entity.HasComponent<OverlayClipComponent>())
		{
			return;
		}

		var settings = entity.GetComponent<OverlayClipComponent>();
		float weight = Math.Clamp(settings.Weight, 0f, 1f);

		if (!settings.Enabled || weight <= 0f || string.IsNullOrEmpty(settings.ClipName))
		{
			return;
		}

		if (!string.Equals(settings.ClipName, character.OverlayClipName, StringComparison.Ordinal))
		{
			character.OverlayClipName = settings.ClipName;
			character.OverlayClip = FindClip(character, settings.ClipName);
			character.OverlayTime = 0f;
		}

		if (character.OverlayClip == null)
		{
			return;
		}

		var clip = GetOzzClip(character, character.OverlayClip);
		if (clip == null || clip.Duration <= 0f)
		{
			return;
		}

		character.OverlayPose ??= OzzPose.Create(character.Ozz);
		if (character.OverlayPose == null)
		{
			return;
		}

		// Subtree root: the authored name wins over the avatar mapping, as everywhere.
		int root = character.Skeleton.FindJoint(
			JointOf(character, settings.RootJoint, HumanoidBone.Chest));
		if (root < 0)
		{
			return;
		}

		EnsureOverlayMasks(character, root, weight);

		if (deltaSeconds > 0f)
		{
			character.OverlayTime += deltaSeconds * MathF.Max(settings.Speed, 0f);
			character.OverlayTime = settings.Loop
				? character.OverlayTime % clip.Duration
				: MathF.Min(character.OverlayTime, clip.Duration);
		}

		// The blend output deliberately aliases the base layer: ozz writes each joint only after
		// reading every layer of that joint, so no separate pose copy is needed.
		bool ok =
			character.OverlayPose.Sample(clip, character.OverlayTime) &&
			character.Pose.Blend([character.Pose, character.OverlayPose], [1f, 1f],
				[character.OverlayMaskBase, character.OverlayMaskLayer]) &&
			character.Pose.LocalToModel() &&
			character.Pose.ReadModelMatrices(character.Models) &&
			character.Pose.ReadLocalTransforms(character.Locals);

		if (ok)
		{
			character.Models.CopyTo(character.Managed.ModelMatrices, 0);
		}
	}

	// Additive layer via ozz additive_layers: runs AFTER overlay and BEFORE look-at/foot IK.
	private void ApplyAdditiveClip(Entity entity, Character character, float deltaSeconds)
	{
		if (character.Pose == null || !entity.HasComponent<AdditiveClipComponent>())
		{
			return;
		}

		var settings = entity.GetComponent<AdditiveClipComponent>();
		float weight = Math.Clamp(settings.Weight, 0f, 1f);

		if (!settings.Enabled || weight <= 0f || string.IsNullOrEmpty(settings.ClipName))
		{
			return;
		}

		if (!string.Equals(settings.ClipName, character.AdditiveClipName, StringComparison.Ordinal))
		{
			character.AdditiveClipName = settings.ClipName;
			character.AdditiveSource = FindClip(character, settings.ClipName);
			character.AdditiveDelta = character.AdditiveSource != null
				? AdditiveClip.Build(character.AdditiveSource, character.Skeleton)
				: null;
			character.AdditiveTime = 0f;
		}

		if (character.AdditiveDelta == null)
		{
			return;
		}

		var clip = GetOzzClip(character, character.AdditiveDelta);
		if (clip == null || clip.Duration <= 0f)
		{
			return;
		}

		character.AdditivePose ??= OzzPose.Create(character.Ozz);
		if (character.AdditivePose == null)
		{
			return;
		}

		if (deltaSeconds > 0f)
		{
			character.AdditiveTime += deltaSeconds * MathF.Max(settings.Speed, 0f);
			character.AdditiveTime = settings.Loop
				? character.AdditiveTime % clip.Duration
				: MathF.Min(character.AdditiveTime, clip.Duration);
		}

		bool ok =
			character.AdditivePose.Sample(clip, character.AdditiveTime) &&
			character.Pose.BlendLayered([character.Pose, character.AdditivePose], [1f, weight],
				[null, null], [false, true]) &&
			character.Pose.LocalToModel() &&
			character.Pose.ReadModelMatrices(character.Models) &&
			character.Pose.ReadLocalTransforms(character.Locals);

		if (ok)
		{
			character.Models.CopyTo(character.Managed.ModelMatrices, 0);
		}
	}

	private static void EnsureOverlayMasks(Character character, int root, float weight)
	{
		if (character.OverlayRoot == root && character.OverlayWeight == weight &&
			character.OverlayMaskBase != null && character.OverlayMaskLayer != null)
		{
			return;
		}

		int jointCount = character.Skeleton.JointCount;
		character.OverlayMaskBase ??= new float[jointCount];
		character.OverlayMaskLayer ??= new float[jointCount];

		for (int joint = 0; joint < jointCount; joint++)
		{
			bool inSubtree = false;
			for (int j = joint; j >= 0; j = character.Skeleton.Parents[j])
			{
				if (j == root)
				{
					inSubtree = true;
					break;
				}
			}

			character.OverlayMaskBase[joint] = inSubtree ? 1f - weight : 1f;
			character.OverlayMaskLayer[joint] = inSubtree ? weight : 0f;
		}

		character.OverlayRoot = root;
		character.OverlayWeight = weight;
	}

	private static void ApplyLookAt(Entity entity, Character character)
	{
		if (character.Pose == null || !entity.HasComponent<LookAtComponent>())
		{
			return;
		}

		var lookAt = entity.GetComponent<LookAtComponent>();
		if (!lookAt.Enabled || lookAt.Weight <= 0f || string.IsNullOrEmpty(lookAt.Joint))
		{
			return;
		}

		int joint = character.Skeleton.FindJoint(lookAt.Joint);
		if (joint < 0)
		{
			return;
		}

		// The target arrives in WORLD space and IK works in model space; the two coincide here.
		if (character.Pose.AimIk(joint, lookAt.Target, lookAt.Forward, lookAt.Up, lookAt.Up, lookAt.Weight) &&
			character.Pose.LocalToModel() &&
			character.Pose.ReadModelMatrices(character.Models) &&
			character.Pose.ReadLocalTransforms(character.Locals))
		{
			character.Models.CopyTo(character.Managed.ModelMatrices, 0);
		}

		character.HasLookAt = true;
		character.LookAtTarget = lookAt.Target;
		character.LookAtJoint = joint;
	}

}
