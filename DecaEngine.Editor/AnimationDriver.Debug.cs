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

/// <summary>Debug drawing and introspection: skeleton, chains, IK, ragdoll joints.</summary>
public sealed partial class AnimationDriver
{
	/// <summary>Flat per-character summary for the debug window, deliberately not a reference
	/// to <see cref="Character"/>.</summary>
	public readonly record struct CharacterInfo(
		int EntityId,
		string Clip,
		float Time,
		bool Playing,
		int JointCount,
		int LegCount,
		bool IkApplied,
		int ChainCount,
		int RagdollBones,
		bool RagdollPhysical,
		bool Locomotion,
		float LocoSpeed,
		float LocoIdleWeight,
		float LocoWalkWeight,
		float LocoRunWeight,
		float LocoWalkPhaseOffset,
		float LocoRunPhaseOffset,
		float ReactionWeight,
		float ReactionDeviation,
		float LocoWalkStride,
		float LocoRunStride);

	public void DescribeCharacters(List<CharacterInfo> result)
	{
		result.Clear();

		foreach (var pair in _characters)
		{
			var character = pair.Value;

			result.Add(new CharacterInfo(
				pair.Key,
				string.IsNullOrEmpty(character.AppliedClip) ? "(bind)" : character.AppliedClip,
				character.Player.Time,
				character.Player.Clip != null,
				character.Skeleton.JointCount,
				character.Legs.Count,
				character.IkApplied,
				character.Chains.Count,
				character.Ragdoll?.BoneCount ?? 0,
				character.Ragdoll != null && !character.Ragdoll.IsAnimationDriven,
				character.LocoActive,
				character.LocoSpeed,
				character.LocoIdleWeight,
				character.LocoWalkWeight,
				character.LocoRunWeight,
				character.LocoWalkPhaseOffset,
				character.LocoRunPhaseOffset,
				character.ReactionWeight,
				character.ReactionDeviation,
				character.LocoWalkStride,
				character.LocoRunStride));
		}
	}

	/// <summary>Draws ragdoll constraints. Lives here, not in PhysicsDebugDraw: the Bepu solver
	/// keeps constraints in a flat list with no notion of which ragdoll they belong to.</summary>
	public void DrawRagdollJoints(DebugDraw draw, bool onTop)
	{
		if (draw is not { Enabled: true })
		{
			return;
		}

		foreach (var character in _characters.Values)
		{
			var ragdoll = character.Ragdoll;
			if (ragdoll == null)
			{
				continue;
			}

			for (int bone = 0; bone < ragdoll.BoneCount; bone++)
			{
				var pose = ragdoll.PoseOf(bone);
				int parent = ragdoll.ParentOf(bone);

				draw.Cross(pose.Position, character.Scale * 0.1f, DebugColor.White, onTop);

				if (parent >= 0)
				{
					draw.Line(ragdoll.PoseOf(parent).Position, pose.Position, DebugColor.Yellow, onTop);
				}
			}
		}
	}

	// Mean bone length as the model's characteristic size: scales debug draw and IK ray length.
	private static float MeasureScale(PreparedSkeleton skeleton)
	{
		var pose = new SkeletonPose(skeleton);
		pose.ComputeModelMatrices();

		float sum = 0f;
		int count = 0;

		for (int i = 0; i < skeleton.JointCount; i++)
		{
			int parent = skeleton.Parents[i];
			if (parent < 0)
			{
				continue;
			}

			sum += Vector3.Distance(pose.ModelMatrices[i].Translation, pose.ModelMatrices[parent].Translation);
			count++;
		}

		return count > 0 && sum > 1e-5f ? sum / count : 1f;
	}

	private void DrawDebug(Character character)
	{
		var draw = Debug;
		if (draw is not { Enabled: true })
		{
			return;
		}

		DrawHighlight(draw, character);

		var options = DebugOptions;
		if (!options.AnyEnabled)
		{
			return;
		}

		bool onTop = options.OnTop;
		var toWorld = character.ModelToWorld;

		if (options.BindPose)
		{
			DrawBindPose(draw, character, onTop);
		}

		if (options.Skeleton || options.JointAxes || options.JointNames)
		{
			DrawSkeleton(draw, character, options, onTop);
		}

		if (options.SpringChains)
		{
			DrawChains(draw, character, onTop);
		}

		if (options.LookAt && character.HasLookAt)
		{
			var target = Vector3.Transform(character.LookAtTarget, toWorld);
			draw.Cross(target, character.Scale * 0.3f, DebugColor.Magenta, onTop);

			if (character.LookAtJoint >= 0)
			{
				var joint = Vector3.Transform(character.Models[character.LookAtJoint].Translation, toWorld);
				draw.Line(joint, target, DebugColor.Dim(DebugColor.Magenta), onTop);
			}
		}

		if (options.FootIk)
		{
			DrawFootIk(draw, character, onTop);
		}
	}

	private void DrawHighlight(DebugDraw draw, Character character)
	{
		if (string.IsNullOrEmpty(HighlightJoint))
		{
			return;
		}

		int joint = character.Skeleton.FindJoint(HighlightJoint);
		if (joint < 0)
		{
			return;
		}

		var world = character.Models[joint] * character.ModelToWorld;

		draw.Cross(world.Translation, character.Scale * 0.5f, DebugColor.Magenta, onTop: true);
		draw.Axes(world, character.Scale * 0.6f, onTop: true);
		draw.Label(world.Translation, HighlightJoint, DebugColor.Magenta);
	}

	private static void DrawSkeleton(DebugDraw draw, Character character, in AnimationDebugOptions options,
		bool onTop)
	{
		var toWorld = character.ModelToWorld;
		var parents = character.Skeleton.Parents;

		for (int i = 0; i < character.Models.Length; i++)
		{
			var world = character.Models[i] * toWorld;

			if (options.Skeleton)
			{
				int parent = parents[i];
				if (parent >= 0)
				{
					var from = Vector3.Transform(character.Models[parent].Translation, toWorld);
					// Colour encodes the pose source: orange for physics-driven, cyan for animated.
					var color = character.RagdollOwned.Length > i && character.RagdollOwned[i]
						? DebugColor.Orange
						: DebugColor.Cyan;

					draw.Bone(from, world.Translation, color, 0.12f, onTop);
				}
				else
				{
					draw.Cross(world.Translation, character.Scale * 0.25f, DebugColor.Yellow, onTop);
				}
			}

			if (options.JointAxes)
			{
				draw.Axes(world, character.Scale * 0.35f, onTop);
			}

			if (options.JointNames)
			{
				draw.Label(world.Translation, character.Skeleton.JointNames[i], DebugColor.White);
			}
		}
	}

	// Recomputed on demand rather than cached: this is drawn rarely, the matrices would live always.
	private static void DrawBindPose(DebugDraw draw, Character character, bool onTop)
	{
		var pose = new SkeletonPose(character.Skeleton);
		pose.ComputeModelMatrices();

		var toWorld = character.ModelToWorld;
		var parents = character.Skeleton.Parents;

		for (int i = 0; i < pose.ModelMatrices.Length; i++)
		{
			int parent = parents[i];
			if (parent < 0)
			{
				continue;
			}

			var from = Vector3.Transform(pose.ModelMatrices[parent].Translation, toWorld);
			var to = Vector3.Transform(pose.ModelMatrices[i].Translation, toWorld);

			draw.Line(from, to, DebugColor.Dim(DebugColor.Grey, 0.7f), onTop);
		}
	}

	private static void DrawChains(DebugDraw draw, Character character, bool onTop)
	{
		var toWorld = character.ModelToWorld;

		foreach (var chain in character.Chains)
		{
			for (int i = 1; i < chain.Joints.Length; i++)
			{
				var from = Vector3.Transform(character.Models[chain.Joints[i - 1]].Translation, toWorld);
				var to = Vector3.Transform(character.Models[chain.Joints[i]].Translation, toWorld);

				draw.Line(from, to, DebugColor.Green, onTop);
				draw.Cross(to, character.Scale * 0.12f, DebugColor.Green, onTop);
			}
		}
	}

	// The IK rays themselves are drawn by the physics debug, where they are cast.
	private static void DrawFootIk(DebugDraw draw, Character character, bool onTop)
	{
		var toWorld = character.ModelToWorld;
		var color = character.IkApplied ? DebugColor.Yellow : DebugColor.Red;

		foreach (var leg in character.Legs)
		{
			var upper = Vector3.Transform(character.Models[leg.UpperJoint].Translation, toWorld);
			var lower = Vector3.Transform(character.Models[leg.LowerJoint].Translation, toWorld);
			var foot = Vector3.Transform(character.Models[leg.FootJoint].Translation, toWorld);

			draw.Line(upper, lower, color, onTop);
			draw.Line(lower, foot, color, onTop);
			draw.Cross(foot, character.Scale * 0.2f, color, onTop);

			// Contact point: for a digitigrade rig this is the toe, so the metatarsal is drawn too.
			var contact = foot;
			if (leg.ToeJoint >= 0)
			{
				contact = Vector3.Transform(character.Models[leg.ToeJoint].Translation, toWorld);
				draw.Line(foot, contact, color, onTop);
			}

			// Sole: the contact joint minus its height. This is what IK plants on the surface.
			var sole = contact - Vector3.TransformNormal(Vector3.UnitY * leg.AnkleHeight, toWorld);
			draw.Line(contact, sole, DebugColor.Dim(color), onTop);
		}
	}
}
