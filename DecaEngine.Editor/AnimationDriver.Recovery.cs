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

// Friflo ships its own Transform component; without this alias the name is ambiguous.
using Transform = DecaEngine.Core.Transform;

namespace DecaEngine.Editor;

/// <summary>Ragdoll get-up and hit reaction: start, blending, lying-pose detection.</summary>
public sealed partial class AnimationDriver
{
	/// <summary>Starts the ragdoll-to-animation pose transition, snapshotting the current pose.</summary>
	// modelToWorld is the entity transform AFTER the move to the lying spot; the snapshot is
	// rebased into it, since the pose matrices were read in the old one.
	public void BeginRecovery(int entityId, float duration, in Matrix4x4 modelToWorld,
		string getUpClip = "")
	{
		if (!_characters.TryGetValue(entityId, out var character) || duration <= 0f)
		{
			return;
		}

		var rebase = Matrix4x4.Identity;
		if (Matrix4x4.Invert(modelToWorld, out var worldToNew))
		{
			// Old model -> world -> new model.
			rebase = character.ModelToWorld * worldToNew;
		}

		character.RecoveryFrom ??= new Transform[character.Skeleton.JointCount];
		DecomposeModelMatrices(character, rebase, character.RecoveryFrom);

		// Empty or missing clip name falls back to the procedural morph.
		character.GetUpClip = string.IsNullOrEmpty(getUpClip) ? null : FindClip(character, getUpClip);

		if (character.GetUpClip != null && character.GetUpClip.Duration > 0f)
		{
			// Clamp the blend window to half the clip: longer would dilute the get-up itself.
			character.RecoveryDuration = character.GetUpClip.Duration;
			character.RecoveryBlendSeconds = MathF.Min(duration, character.GetUpClip.Duration * 0.5f);
		}
		else
		{
			character.GetUpClip = null;
			character.RecoveryDuration = duration;
			character.RecoveryBlendSeconds = duration;
		}

		character.RecoveryElapsed = 0f;
	}

	/// <summary>Clears play-time state that lives outside ECS. Call when leaving Play mode.</summary>
	// The Play Mode snapshot only rolls back components; everything reset here lives beside them.
	public void EndPlay()
	{
		foreach (var character in _characters.Values)
		{
			character.RecoveryElapsed = 0f;
			character.RecoveryDuration = 0f;
			character.GetUpClip = null;

			character.LocoPhase = 0f;
			character.LocoIdleTime = 0f;
			character.LocoSpeed = 0f;
			character.LocoHasPrev = false;

			character.LocoRunGait = false;
			character.LocoGaitBlend = 0f;

			character.ReactionDuration = 0f;
			character.ReactionElapsed = 0f;
			character.ReactionImpulsePending = false;

			// Destroyed, not returned to animation: the bodies ARE the accumulated state, and they
			// rebuild next frame from the restored pose.
			DestroyRagdoll(character);

			// Spring bone chains carry inertia; they rebuild from the pose too.
			character.Chains.Clear();
			character.ChainsBuilt = false;
		}
	}

	/// <summary>Starts a hit reaction: a temporary partial ragdoll on the upper body.</summary>
	// velocityChange is in m/s (a velocity delta, mass-independent). Requires a RagdollComponent;
	// a repeat hit restarts the envelope and adds to the impulse instead of being dropped.
	public void TriggerHitReaction(int entityId, Vector3 velocityChange, float duration = 0.7f,
		float strength = 1f)
	{
		if (!_characters.TryGetValue(entityId, out var character) || duration <= 0f)
		{
			return;
		}

		// Restart carries the CURRENT envelope weight; resetting to zero snaps back to pure
		// animation for one frame, which reads as a twitch under a burst of hits.
		float carried = 0f;
		if (character.ReactionDuration > 0f && character.ReactionElapsed < character.ReactionDuration)
		{
			float t = Math.Clamp(character.ReactionElapsed / character.ReactionDuration, 0f, 1f);
			float attack = Math.Clamp(character.ReactionElapsed / ReactionAttackSeconds, 0f, 1f);
			float release = 1f - t * t * (3f - 2f * t);
			carried = character.ReactionStrength * attack * release;
		}

		character.ReactionElapsed = Math.Clamp(carried, 0f, 1f) * ReactionAttackSeconds;
		character.ReactionDuration = duration;
		character.ReactionStrength = Math.Clamp(strength, 0f, 1f);
		character.ReactionImpulse = velocityChange;
		character.ReactionImpulsePending = true;
	}

	// Reaction envelope attack, seconds.
	private const float ReactionAttackSeconds = 0.06f;

	/// <summary>True while the get-up is still running, i.e. the character is not yet controllable.</summary>
	public bool IsRecovering(int entityId) =>
		_characters.TryGetValue(entityId, out var character) && character.RecoveryElapsed < character.RecoveryDuration;

	/// <summary>Whether the ragdoll has settled.</summary>
	// relativeSpeed is in skeleton spans per second, not world units: rig scales differ wildly.
	public bool IsRagdollSettled(int entityId, float relativeSpeed)
	{
		if (!_characters.TryGetValue(entityId, out var character) || character.Ragdoll == null ||
			Physics == null)
		{
			return true;
		}

		float threshold = relativeSpeed * character.Scale * WorldScaleOf(character.ModelToWorld);
		var bodies = Physics.World.Simulation.Bodies;

		for (int i = 0; i < character.Ragdoll.BoneCount; i++)
		{
			if (bodies[character.Ragdoll.BodyOf(i)].Velocity.Linear.Length() > threshold)
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>World facing of a lying character: horizontal projection of the hips-to-neck axis.</summary>
	// False when that axis is near vertical (ragdoll settled sitting): no meaningful projection.
	public bool TryGetLyingFacing(int entityId, out Vector3 worldForward)
	{
		worldForward = default;

		if (!_characters.TryGetValue(entityId, out var character) || character.Avatar == null)
		{
			return false;
		}

		int hips = character.Skeleton.FindJoint(character.Avatar[HumanoidBone.Hips] ?? string.Empty);
		int neck = character.Skeleton.FindJoint(character.Avatar[HumanoidBone.Neck] ?? string.Empty);

		if (neck < 0)
		{
			neck = character.Skeleton.FindJoint(character.Avatar[HumanoidBone.Head] ?? string.Empty);
		}

		if (hips < 0 || neck < 0)
		{
			return false;
		}

		var direction =
			Vector3.Transform(character.Models[neck].Translation, character.ModelToWorld) -
			Vector3.Transform(character.Models[hips].Translation, character.ModelToWorld);
		direction.Y = 0f;

		// Threshold is a fraction of the axis length, so it holds across rig scales.
		float span = Vector3.Distance(character.Models[neck].Translation, character.Models[hips].Translation) *
			WorldScaleOf(character.ModelToWorld);

		if (direction.Length() < 0.3f * MathF.Max(span, 1e-6f))
		{
			return false;
		}

		worldForward = Vector3.Normalize(direction);
		return true;
	}

	/// <summary>World position of the ragdoll root bone - where the character gets up.</summary>
	// The bone, not the entity transform: the entity never left the spot where the fall started.
	public bool TryGetRagdollRootWorld(int entityId, out Vector3 position)
	{
		position = Vector3.Zero;

		if (!_characters.TryGetValue(entityId, out var character) || character.Ragdoll == null ||
			character.Ragdoll.BoneCount == 0 || Physics == null)
		{
			return false;
		}

		position = Physics.World.Simulation.Bodies[character.Ragdoll.BodyOf(0)].Pose.Position;
		return true;
	}

	// True while the get-up clip drives the pose: the normal stack (locomotion, layers, IK) is off.
	private bool ApplyGetUpClip(Character character)
	{
		if (character.GetUpClip == null)
		{
			return false;
		}

		if (character.Pose == null || character.RecoveryElapsed >= character.RecoveryDuration)
		{
			character.GetUpClip = null;
			return false;
		}

		var clip = GetOzzClip(character, character.GetUpClip);
		if (clip == null || clip.Duration <= 0f)
		{
			character.GetUpClip = null;
			return false;
		}

		bool ok =
			character.Pose.Sample(clip, MathF.Min(character.RecoveryElapsed, clip.Duration)) &&
			character.Pose.LocalToModel() &&
			character.Pose.ReadModelMatrices(character.Models) &&
			character.Pose.ReadLocalTransforms(character.Locals);

		if (!ok)
		{
			character.GetUpClip = null;
			return false;
		}

		character.Models.CopyTo(character.Managed.ModelMatrices, 0);
		return true;
	}

	/// <summary>Whether the character lies on its back, for picking the get-up clip.</summary>
	// Measured from the hips axis that pointed at model +Y in the bind pose.
	public bool TryGetLyingSide(int entityId, out bool onBack)
	{
		onBack = false;

		if (!_characters.TryGetValue(entityId, out var character) || character.Avatar == null)
		{
			return false;
		}

		int hips = character.Skeleton.FindJoint(character.Avatar[HumanoidBone.Hips] ?? string.Empty);
		if (hips < 0)
		{
			return false;
		}

		if (!Matrix4x4.Invert(BindModelMatrix(character.Skeleton, hips), out var inverseBind))
		{
			return false;
		}

		var upLocal = Vector3.TransformNormal(Vector3.UnitY, inverseBind);
		var upWorld = Vector3.TransformNormal(upLocal, character.Models[hips] * character.ModelToWorld);

		if (upWorld.LengthSquared() < 1e-10f)
		{
			return false;
		}

		onBack = Vector3.Normalize(upWorld).Y < 0f;
		return true;
	}

	private static Matrix4x4 BindModelMatrix(PreparedSkeleton skeleton, int joint)
	{
		var result = Matrix4x4.Identity;

		for (int j = joint; j >= 0; j = skeleton.Parents[j])
		{
			var bind = skeleton.BindLocals[j];
			result *= MathUtils.CreateTrs(bind.position, bind.rotation, bind.scale);
		}

		return result;
	}

	// Must run LAST, after the ragdoll stage. Blends decomposed TRS, not matrices: componentwise
	// matrix lerp yields a non-orthogonal basis mid-transition, i.e. bones that squash and stretch.
	private void ApplyRecoveryBlend(Character character)
	{
		if (character.RecoveryElapsed >= character.RecoveryDuration || character.RecoveryFrom == null)
		{
			return;
		}

		character.RecoveryElapsed += character.LastDelta;

		// Weighted by the blend window, not the full duration: after it the clip drives alone.
		float window = character.RecoveryBlendSeconds > 0f
			? character.RecoveryBlendSeconds
			: character.RecoveryDuration;
		float t = Math.Clamp(character.RecoveryElapsed / window, 0f, 1f);

		// Smoothstep: a linear weight kinks the velocity at both ends of the get-up.
		float weight = t * t * (3f - 2f * t);

		for (int i = 0; i < character.Models.Length; i++)
		{
			if (!Matrix4x4.Decompose(character.Models[i], out var scale, out var rotation, out var translation))
			{
				continue;
			}

			var from = character.RecoveryFrom[i];

			character.Models[i] = MathUtils.CreateTrs(
				Vector3.Lerp(from.position, translation, weight),
				Quaternion.Slerp(from.rotation, rotation, weight),
				Vector3.Lerp(from.scale, scale, weight));
		}

		character.Models.CopyTo(character.Managed.ModelMatrices, 0);
	}

	private static void DecomposeModelMatrices(Character character, in Matrix4x4 rebase, Transform[] target)
	{
		for (int i = 0; i < character.Models.Length; i++)
		{
			if (Matrix4x4.Decompose(character.Models[i] * rebase, out var scale, out var rotation, out var translation))
			{
				target[i] = new Transform { position = translation, rotation = rotation, scale = scale };
			}
			else
			{
				target[i] = new Transform { position = Vector3.Zero, rotation = Quaternion.Identity, scale = Vector3.One };
			}
		}
	}

}
