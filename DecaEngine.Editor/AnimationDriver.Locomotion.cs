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

// Friflo has its own Transform component; the alias disambiguates it from the engine TRS.
using Transform = DecaEngine.Core.Transform;

namespace DecaEngine.Editor;

/// <summary>Locomotion and clips: speed-driven gait, root motion, ozz pose sampling.</summary>
public sealed partial class AnimationDriver
{
	// Returns false when nothing can drive the pose, so the plain Animator takes over.
	private bool ApplyLocomotion(Entity entity, Character character, float deltaSeconds)
	{
		character.LocoActive = false;

		if (character.Pose == null || !entity.HasComponent<LocomotionComponent>())
		{
			return false;
		}

		var settings = entity.GetComponent<LocomotionComponent>();
		if (!settings.Enabled)
		{
			return false;
		}

		// Clips are looked up by name only when the names change.
		string key = $"{settings.IdleClip}\n{settings.WalkClip}\n{settings.RunClip}";
		if (!string.Equals(key, character.LocoClipsKey, StringComparison.Ordinal))
		{
			character.LocoClipsKey = key;
			character.LocoIdle = FindClip(character, settings.IdleClip ?? string.Empty);
			character.LocoWalk = FindClip(character, settings.WalkClip ?? string.Empty);
			character.LocoRun = FindClip(character, settings.RunClip ?? string.Empty);
			character.LocoOffsetsValid = false;
		}

		// All three clips are required: ozz fills missing weight with the rest pose, so a typo in
		// one clip name would half-dissolve the character into bind pose.
		if (character.LocoIdle == null || character.LocoWalk == null || character.LocoRun == null)
		{
			return false;
		}

		var idleClip = GetOzzClip(character, character.LocoIdle);
		var walkClip = GetOzzClip(character, character.LocoWalk);
		var runClip = GetOzzClip(character, character.LocoRun);

		if (idleClip == null || walkClip == null || runClip == null ||
			idleClip.Duration <= 0f || walkClip.Duration <= 0f || runClip.Duration <= 0f)
		{
			return false;
		}

		character.LocoPoseA ??= OzzPose.Create(character.Ozz);
		character.LocoPoseB ??= OzzPose.Create(character.Ozz);

		if (character.LocoPoseA == null || character.LocoPoseB == null)
		{
			return false;
		}

		if (!character.LocoOffsetsValid)
		{
			character.LocoWalkPhaseOffset = GaitPhaseOffset(character, walkClip);
			character.LocoRunPhaseOffset = GaitPhaseOffset(character, runClip);
			character.LocoWalkStride = MeasureStrideSpeed(character, walkClip);
			character.LocoRunStride = MeasureStrideSpeed(character, runClip);
			character.LocoOffsetsValid = true;
		}

		float walkSpeed = MathF.Max(settings.WalkSpeed, 1e-3f);
		float runSpeed = MathF.Max(settings.RunSpeed, walkSpeed + 1e-3f);

		// Speed is XZ only: vertical motion is bumps and falls, not gait tempo.
		if (deltaSeconds > 0f)
		{
			var worldPos = character.ModelToWorld.Translation;
			float raw = character.LocoSpeed;

			if (character.LocoHasPrev)
			{
				var delta = worldPos - character.LocoPrevWorld;
				raw = MathF.Sqrt(delta.X * delta.X + delta.Z * delta.Z) / deltaSeconds;

				// Cap guards against teleports: a ragdoll get-up moves metres in one frame.
				raw = MathF.Min(raw, runSpeed * 2f);
			}

			character.LocoPrevWorld = worldPos;
			character.LocoHasPrev = true;

			float alpha = settings.Smoothing > 0f ? 1f - MathF.Exp(-settings.Smoothing * deltaSeconds) : 1f;
			character.LocoSpeed += (raw - character.LocoSpeed) * alpha;
		}

		float speed = character.LocoSpeed;

		OzzClip layerA, layerB;
		float timeA, timeB, weightA, weightB, frequency;

		// Layer time = shared phase plus the clip's gait-event offset; phase alone aligns
		// tempo but not what the legs are doing at that instant.
		float walkTime = (character.LocoPhase + character.LocoWalkPhaseOffset) % 1f * walkClip.Duration;
		float runTime = (character.LocoPhase + character.LocoRunPhaseOffset) % 1f * runClip.Duration;

		// Gait switches with hysteresis: up at 60% of the span, down at 40%.
		float switchUp = walkSpeed + 0.6f * (runSpeed - walkSpeed);
		float switchDown = walkSpeed + 0.4f * (runSpeed - walkSpeed);

		if (!character.LocoRunGait && speed > switchUp)
		{
			character.LocoRunGait = true;
		}
		else if (character.LocoRunGait && speed < switchDown)
		{
			character.LocoRunGait = false;
		}

		if (deltaSeconds > 0f)
		{
			float goal = character.LocoRunGait ? 1f : 0f;
			character.LocoGaitBlend += (goal - character.LocoGaitBlend) *
				(1f - MathF.Exp(-8f * deltaSeconds));
		}

		// Rate comes from the clip's natural stride speed in MODEL units; authored
		// WalkSpeed/RunSpeed are only gait thresholds, used as fallback when measuring failed.
		float worldScale = MathF.Max(WorldScaleOf(character.ModelToWorld), 1e-6f);
		float speedModel = speed / worldScale;

		float walkRate = character.LocoWalkStride > 1e-3f
			? speedModel / character.LocoWalkStride
			: speed / walkSpeed;
		float runRate = character.LocoRunStride > 1e-3f
			? speedModel / character.LocoRunStride
			: speed / runSpeed;

		if (speed <= walkSpeed && character.LocoGaitBlend < 0.5f)
		{
			float t = Math.Clamp(speed / walkSpeed, 0f, 1f);

			layerA = idleClip;
			timeA = character.LocoIdleTime;
			weightA = 1f - t;

			layerB = walkClip;
			timeB = walkTime;
			weightB = t;

			frequency = walkRate / walkClip.Duration;

			character.LocoIdleWeight = weightA;
			character.LocoWalkWeight = weightB;
			character.LocoRunWeight = 0f;
		}
		else
		{
			float t = Math.Clamp(character.LocoGaitBlend, 0f, 1f);

			layerA = walkClip;
			timeA = walkTime;
			weightA = 1f - t;

			layerB = runClip;
			timeB = runTime;
			weightB = t;

			// Each layer chases the real speed in its own gait so stride length holds.
			float walkFrequency = walkRate / walkClip.Duration;
			float runFrequency = runRate / runClip.Duration;
			frequency = walkFrequency + (runFrequency - walkFrequency) * t;

			character.LocoIdleWeight = 0f;
			character.LocoWalkWeight = weightA;
			character.LocoRunWeight = weightB;
		}

		if (deltaSeconds > 0f)
		{
			character.LocoPhase = (character.LocoPhase + frequency * deltaSeconds) % 1f;
			character.LocoIdleTime = (character.LocoIdleTime + deltaSeconds) % idleClip.Duration;
		}

		bool ok =
			character.LocoPoseA.Sample(layerA, timeA) &&
			character.LocoPoseB.Sample(layerB, timeB) &&
			character.Pose.Blend([character.LocoPoseA, character.LocoPoseB], [weightA, weightB]) &&
			character.Pose.LocalToModel() &&
			character.Pose.ReadModelMatrices(character.Models) &&
			character.Pose.ReadLocalTransforms(character.Locals);

		if (!ok)
		{
			return false;
		}

		character.Models.CopyTo(character.Managed.ModelMatrices, 0);
		character.LocoActive = true;
		return true;
	}

	// Gait event phase (0..1): lowest point of the left foot, 32-sample scan, 0 if unmapped.
	private static float GaitPhaseOffset(Character character, OzzClip clip)
	{
		string footName = character.Avatar?[HumanoidBone.LeftFoot] ?? string.Empty;
		int foot = string.IsNullOrEmpty(footName) ? -1 : character.Skeleton.FindJoint(footName);

		if (foot < 0 || character.LocoPoseA == null)
		{
			return 0f;
		}

		const int samples = 32;
		var models = new Matrix4x4[character.Skeleton.JointCount];

		float bestPhase = 0f;
		float bestHeight = float.MaxValue;

		for (int k = 0; k < samples; k++)
		{
			float phase = (float)k / samples;

			if (!character.LocoPoseA.Sample(clip, phase * clip.Duration) ||
				!character.LocoPoseA.LocalToModel() ||
				!character.LocoPoseA.ReadModelMatrices(models))
			{
				return 0f;
			}

			float height = models[foot].Translation.Y;
			if (height < bestHeight)
			{
				bestHeight = height;
				bestPhase = phase;
			}
		}

		return bestPhase;
	}

	// Natural stride speed in model units: mean horizontal foot speed over its stance beat.
	// Returns 0 when it cannot be measured, and the caller falls back to authored speeds.
	private static float MeasureStrideSpeed(Character character, OzzClip clip)
	{
		string footName = character.Avatar?[HumanoidBone.LeftFoot] ?? string.Empty;
		int foot = string.IsNullOrEmpty(footName) ? -1 : character.Skeleton.FindJoint(footName);

		if (foot < 0 || character.LocoPoseA == null)
		{
			return 0f;
		}

		const int samples = 48;
		var positions = new Vector3[samples];
		float minHeight = float.MaxValue;
		float maxHeight = float.MinValue;

		for (int k = 0; k < samples; k++)
		{
			if (!character.LocoPoseA.Sample(clip, clip.Duration * k / samples) ||
				!character.LocoPoseA.LocalToModel() ||
				!character.LocoPoseA.ReadModelMatrices(character.Models))
			{
				return 0f;
			}

			positions[k] = character.Models[foot].Translation;
			minHeight = MathF.Min(minHeight, positions[k].Y);
			maxHeight = MathF.Max(maxHeight, positions[k].Y);
		}

		float threshold = minHeight + 0.25f * (maxHeight - minHeight);
		float dt = clip.Duration / samples;
		float travel = 0f;
		float seconds = 0f;

		for (int k = 0; k < samples; k++)
		{
			int next = (k + 1) % samples;
			if (positions[k].Y >= threshold || positions[next].Y >= threshold)
			{
				continue;
			}

			var step = positions[next] - positions[k];
			travel += MathF.Sqrt(step.X * step.X + step.Z * step.Z);
			seconds += dt;
		}

		return seconds > 1e-4f ? travel / seconds : 0f;
	}

	private void ApplyClip(Entity entity, Character character, float deltaSeconds)
	{
		if (!entity.HasComponent<Animator>())
		{
			SamplePose(character);
			return;
		}

		// By ref: writing the component back each frame can move the entity between archetypes.
		ref var animator = ref entity.GetComponent<Animator>();

		// Clip is looked up only when the name changes.
		if (!string.Equals(animator.ClipName ?? string.Empty, character.AppliedClip, StringComparison.Ordinal))
		{
			character.AppliedClip = animator.ClipName ?? string.Empty;
			character.Player.Clip = FindClip(character, character.AppliedClip);
		}

		character.Player.Loop = animator.Loop;
		character.Player.Speed = animator.Speed;

		// Time lives in the component (scrubbable) but only the player knows looping and clip end.
		character.Player.Time = animator.Time;
		float timeBefore = character.Player.Time;

		if (animator.Playing)
		{
			character.Player.Advance(deltaSeconds);
		}

		animator.Time = character.Player.Time;

		SamplePose(character);

		if (animator.RootMotion)
		{
			ApplyRootMotion(entity, character, in animator, timeBefore);
		}
	}

	// Root motion after ozz motion_playback: root XZ is subtracted from the pose and accumulated
	// into the entity position. Y stays in the pose. Skipped for Character Body entities.
	private static void ApplyRootMotion(Entity entity, Character character, in Animator animator,
		float timeBefore)
	{
		var clip = character.Player.Clip;

		if (clip == null || character.Pose == null || !entity.HasComponent<Position>() ||
			entity.HasComponent<CharacterBodyComponent>())
		{
			return;
		}

		if (!ReferenceEquals(character.MotionClip, clip))
		{
			character.MotionClip = clip;
			character.MotionJoint = MotionJointOf(character);
		}

		if (character.MotionJoint < 0 || character.MotionJoint >= clip.Tracks.Length)
		{
			return;
		}

		var track = clip.Tracks[character.MotionJoint];
		if (track.TranslationTimes.Length < 2)
		{
			return;
		}

		// Root snaps back to the first key in XZ: the pose walks in place, travel goes to the entity.
		var atTime = SampleMotion(track, character.Player.Time);
		var offset = atTime - track.Translations[0];
		offset.Y = 0f;

		character.Locals[character.MotionJoint].position -= offset;

		if (!character.Pose.WriteLocalTransforms(character.Locals) ||
			!character.Pose.LocalToModel() ||
			!character.Pose.ReadModelMatrices(character.Models))
		{
			return;
		}

		character.Models.CopyTo(character.Managed.ModelMatrices, 0);

		// Frame delta must account for loop wrap: time going backwards means the player wrapped.
		var delta = atTime - SampleMotion(track, timeBefore);

		if (animator.Loop && clip.Duration > 1e-6f)
		{
			var net = track.Translations[^1] - track.Translations[0];

			if (character.Player.Speed >= 0f && character.Player.Time < timeBefore - 1e-6f)
			{
				delta += net;
			}
			else if (character.Player.Speed < 0f && character.Player.Time > timeBefore + 1e-6f)
			{
				delta -= net;
			}
		}

		delta.Y = 0f;

		if (delta.LengthSquared() < 1e-12f)
		{
			return;
		}

		// Delta is in model space, entity position is in parent space: model -> world -> parent.
		var worldDelta = Vector3.TransformNormal(delta, character.ModelToWorld);
		var parentToWorld = PrefabSceneViewport.ParentToWorldMatrix(entity);

		if (Matrix4x4.Invert(parentToWorld, out var worldToParent))
		{
			entity.GetComponent<Position>().value += Vector3.TransformNormal(worldDelta, worldToParent);
		}
	}

	// Topmost ancestor of the hips: authored travel lives on the rig root, not on the hips.
	private static int MotionJointOf(Character character)
	{
		int joint = character.Skeleton.FindJoint(character.Avatar?[HumanoidBone.Hips] ?? string.Empty);

		if (joint < 0)
		{
			joint = 0;
		}

		while (character.Skeleton.Parents[joint] >= 0)
		{
			joint = character.Skeleton.Parents[joint];
		}

		return joint;
	}

	// Linear scan on purpose: motion tracks have a handful of keys and are sampled twice a frame.
	private static Vector3 SampleMotion(JointTrack track, float time)
	{
		var times = track.TranslationTimes;
		var values = track.Translations;

		if (time <= times[0])
		{
			return values[0];
		}

		for (int i = 1; i < times.Length; i++)
		{
			if (time <= times[i])
			{
				float span = times[i] - times[i - 1];
				float t = span > 1e-9f ? (time - times[i - 1]) / span : 1f;
				return Vector3.Lerp(values[i - 1], values[i], t);
			}
		}

		return values[^1];
	}

	// Samples via native ozz when available, else the C# sampler; both leave the same output.
	private static void SamplePose(Character character)
	{
		var clip = character.Player.Clip;
		var ozzClip = clip != null ? GetOzzClip(character, clip) : null;

		if (character.Pose != null && ozzClip != null &&
			character.Pose.Sample(ozzClip, character.Player.Time) &&
			character.Pose.LocalToModel() &&
			character.Pose.ReadModelMatrices(character.Models) &&
			character.Pose.ReadLocalTransforms(character.Locals))
		{
			character.Models.CopyTo(character.Managed.ModelMatrices, 0);
			return;
		}

		character.Player.Apply(character.Managed);
		character.Managed.ModelMatrices.CopyTo(character.Models, 0);
		character.Managed.Locals.CopyTo(character.Locals, 0);
	}

	private static OzzClip? GetOzzClip(Character character, PreparedAnimation clip)
	{
		if (character.Ozz == null)
		{
			return null;
		}

		if (!character.Clips.TryGetValue(clip, out var built))
		{
			// A failed build is cached as null so the repack is not retried every frame.
			built = OzzClip.Build(character.Ozz, clip);
			character.Clips[clip] = built;
		}

		return built;
	}

	private static PreparedAnimation? FindClip(Character character, string name)
	{
		if (string.IsNullOrEmpty(name))
		{
			return null;
		}

		foreach (var clip in character.Model.Animations)
		{
			if (string.Equals(clip.Name, name, StringComparison.Ordinal))
			{
				return clip;
			}
		}

		return null;
	}

}
