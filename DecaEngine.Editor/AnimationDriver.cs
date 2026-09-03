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

// Friflo has its own Transform component; alias so the engine TRS type resolves unambiguously.
using Transform = DecaEngine.Core.Transform;

namespace DecaEngine.Editor;

/// <summary>Bridges authoring animation components to the runtime: owns per-character pose state and uploads skin palettes to the GPU skinning pass.</summary>
// State lives here, not in components: ozz native handles don't survive ECS archetype copies.
// One entity spawns several batch-renderer instances (one per mesh) but has exactly one pose.
public sealed partial class AnimationDriver : IDisposable
{
	private sealed class Character : IDisposable
	{
		public ModelLoader Model = null!;
		public PreparedSkeleton Skeleton = null!;

		public OzzSkeleton? Ozz;
		public OzzPose? Pose;
		public readonly Dictionary<PreparedAnimation, OzzClip?> Clips = new();

		public SkeletonPose Managed = null!;
		public readonly AnimationPlayer Player = new();

		public Transform[] Locals = [];
		public Matrix4x4[] Models = [];

		// Poses are computed in model space; physics needs world space.
		public Matrix4x4 ModelToWorld = Matrix4x4.Identity;

		// Mean bone length; all debug drawing is scaled by it, model units being arbitrary.
		public float Scale = 1f;

		public readonly List<int> Palettes = new();

		public readonly List<SpringBoneChain> Chains = new();

		// Rebuild trigger: rebuilding every frame would drop the chains' accumulated inertia.
		public SpringBoneComponent ChainSource;
		public bool ChainsBuilt;

		public string AppliedClip = string.Empty;

		// Scratch layers; blending writes into Pose, so sampling into it would clobber its input.
		public OzzPose? LocoPoseA;
		public OzzPose? LocoPoseB;

		public PreparedAnimation? LocoIdle;
		public PreparedAnimation? LocoWalk;
		public PreparedAnimation? LocoRun;
		public string LocoClipsKey = string.Empty;

		// Normalized 0..1 gait phase, shared by Walk and Run: their cycle lengths differ, so
		// blending by each clip's own seconds would pair the left leg of one with the right of the other.
		public float LocoPhase;

		// Per-clip offsets aligning phase 0 to a common gait event (rear-left foot plant).
		public float LocoWalkPhaseOffset;
		public float LocoRunPhaseOffset;
		public bool LocoOffsetsValid;

		// Gait is a discrete hysteretic state cross-faded over time, not a speed-driven weight:
		// walk and gallop have different front-to-rear phase relations, so a steady mix is wrong.
		public bool LocoRunGait;
		public float LocoGaitBlend;

		// Native stride speed measured from the clip (model units/s); playback rate scales by it,
		// not by the authored WalkSpeed/RunSpeed, which are only gait switch thresholds.
		public float LocoWalkStride;
		public float LocoRunStride;

		public float LocoIdleTime;

		// Smoothed measured speed, m/s.
		public float LocoSpeed;

		public Vector3 LocoPrevWorld;
		public bool LocoHasPrev;

		public bool LocoActive;
		public float LocoIdleWeight;
		public float LocoWalkWeight;
		public float LocoRunWeight;

		// Humanoid rig mapping; null when the model is unmapped.
		public HumanoidAvatar? Avatar;

		// Root motion joint: topmost ancestor of the mapped hips, resolved per clip.
		public PreparedAnimation? MotionClip;
		public int MotionJoint = -1;

		public OzzPose? OverlayPose;
		public PreparedAnimation? OverlayClip;
		public string OverlayClipName = string.Empty;
		public float OverlayTime;

		// Per-joint layer weights summing to 1, so ozz never blends in its rest pose.
		public float[]? OverlayMaskBase;
		public float[]? OverlayMaskLayer;
		public int OverlayRoot = -1;
		public float OverlayWeight = -1f;

		public OzzPose? AdditivePose;
		public PreparedAnimation? AdditiveSource;
		public PreparedAnimation? AdditiveDelta;
		public string AdditiveClipName = string.Empty;
		public float AdditiveTime;

		public readonly List<FootIkLeg> Legs = new();
		public readonly FootIkSettings IkSettings = new();
		public FootIkComponent LegSource;
		public bool LegsBuilt;

		// Whether IK actually ran, which differs from the component being enabled.
		public bool IkApplied;

		public Ragdoll? Ragdoll;
		public RagdollComponent RagdollSource;
		public bool RagdollBuilt;

		// Bepu bodies bake in capsule sizes at build time, so a scale change needs a rebuild.
		public float RagdollBuildScale;

		// Animated pose in world space, the servo target for ragdoll bodies.
		public Matrix4x4[] JointWorld = [];

		// Pose read back from the bodies; separate, since JointWorld is still needed as the target.
		public Matrix4x4[] RagdollWorld = [];

		// Joints driven by physics; the rest are derived from them down the hierarchy.
		public bool[] RagdollOwned = [];

		// Hit reaction envelope; 0 means no reaction is active.
		public float ReactionElapsed;
		public float ReactionDuration;
		public float ReactionStrength;

		// Impulse waiting for the first frame with bodies: a reaction can start before the
		// ragdoll is built.
		public Vector3 ReactionImpulse;
		public bool ReactionImpulsePending;

		// Per-joint reaction weight: torso 1, hips damped, legs 0, so the character keeps walking.
		public float[] ReactionMask = [];
		public bool ReactionMaskBuilt;

		// Animated pose before physics is mixed in, the other half of the blend.
		public Matrix4x4[] ReactionAnimated = [];

		// Max deviation from the animated pose, in model units.
		public float ReactionWeight;
		public float ReactionDeviation;

		// Debug snapshot, filled by intermediate stages and drawn at the end of the frame step.
		public bool HasLookAt;
		public Vector3 LookAtTarget;
		public int LookAtJoint = -1;

		// Pose snapshot taken when get-up starts; null if it never started.
		public Transform[]? RecoveryFrom;

		// Get-up clip driving the pose during recovery; null selects the procedural morph.
		public PreparedAnimation? GetUpClip;

		// Window over which the lying snapshot is blended out; short for clips, whose first
		// frame is already authored lying down.
		public float RecoveryBlendSeconds;

		public float RecoveryDuration;
		public float RecoveryElapsed;

		public float LastDelta;

		public void Dispose()
		{
			Ragdoll?.Destroy();
			Ragdoll = null;

			foreach (var clip in Clips.Values)
			{
				clip?.Dispose();
			}

			Clips.Clear();
			Pose?.Dispose();
			LocoPoseA?.Dispose();
			LocoPoseB?.Dispose();
			Ozz?.Dispose();
		}
	}

	private readonly DiligentSkinningPass _skinning;
	private readonly Dictionary<int, Character> _characters = new();

	public AnimationDriver(DiligentSkinningPass skinning) => _skinning = skinning;

	public int CharacterCount => _characters.Count;

	/// <summary>Exposes a character's live pose arrays, not copies, for test probes.</summary>
	public bool TryGetPose(int entityId, out Matrix4x4[] modelMatrices, out Matrix4x4[] skinMatrices)
	{
		if (_characters.TryGetValue(entityId, out var character))
		{
			modelMatrices = character.Managed.ModelMatrices;
			skinMatrices = character.Managed.SkinMatrices;
			return true;
		}

		modelMatrices = [];
		skinMatrices = [];
		return false;
	}

	/// <summary>Scene physics world; when null, foot IK and ragdoll stay off.</summary>
	public ScenePhysics? Physics { get; set; }

	/// <summary>Debug geometry sink; when null or disabled, no stage draws anything.</summary>
	public DebugDraw? Debug { get; set; }

	public AnimationDebugOptions DebugOptions { get; set; } = new();

	/// <summary>Bone highlighted by the Humanoid window; drawn regardless of the debug toggles.</summary>
	public string HighlightJoint { get; set; } = string.Empty;

	/// <summary>Characters simulated as ragdolls this frame.</summary>
	public int ActiveRagdollCount { get; private set; }

	/// <summary>Registers another skinned instance of an entity against its palette slice.</summary>
	public void AddInstance(int entityId, ModelLoader model, int paletteOffset)
	{
		if (model.Skeleton == null)
		{
			return;
		}

		if (!_characters.TryGetValue(entityId, out var character))
		{
			int jointCount = model.Skeleton.JointCount;

			character = new Character
			{
				Model = model,
				Skeleton = model.Skeleton,
				Managed = new SkeletonPose(model.Skeleton),
				Locals = new Transform[jointCount],
				Models = new Matrix4x4[jointCount],
				JointWorld = new Matrix4x4[jointCount],
				RagdollWorld = new Matrix4x4[jointCount],
				RagdollOwned = new bool[jointCount],
			};

			character.Ozz = OzzSkeleton.Build(model.Skeleton);
			character.Pose = character.Ozz != null ? OzzPose.Create(character.Ozz) : null;
			character.Scale = MeasureScale(model.Skeleton);

			_characters[entityId] = character;
		}

		// Negative offset means no palette slice, as in headless probes with no renderer instances.
		if (paletteOffset >= 0)
		{
			character.Palettes.Add(paletteOffset);
		}
	}

	/// <summary>Sets the humanoid mapping, discarding legs and ragdoll built from the old one.</summary>
	public void SetAvatar(int entityId, HumanoidAvatar? avatar)
	{
		if (!_characters.TryGetValue(entityId, out var character) ||
			ReferenceEquals(character.Avatar, avatar))
		{
			return;
		}

		character.Avatar = avatar;
		character.LegsBuilt = false;
		character.Legs.Clear();
		DestroyRagdoll(character);
	}

	public void Remove(int entityId)
	{
		if (_characters.Remove(entityId, out var character))
		{
			character.Dispose();
		}
	}

	public void Clear()
	{
		foreach (var character in _characters.Values)
		{
			character.Dispose();
		}

		_characters.Clear();
	}

	/// <summary>Destroys all ragdolls and forgets the world; call before disposing ScenePhysics.
	/// Deliberately keeps the characters, which own the palette slices.</summary>
	public void DetachPhysics()
	{
		foreach (var character in _characters.Values)
		{
			DestroyRagdoll(character);
		}

		Physics = null;
	}

	public void Dispose() => Clear();

	/// <summary>Per-frame step for one entity; stage order is fixed, as procedural stages edit
	/// the pose the clip stage produced.</summary>
	public void Update(Entity entity, in Matrix4x4 modelToWorld, float deltaSeconds)
	{
		if (!_characters.TryGetValue(entity.Id, out var character))
		{
			return;
		}

		character.ModelToWorld = modelToWorld;
		character.LastDelta = deltaSeconds;
		character.IkApplied = false;
		character.HasLookAt = false;

		// A get-up clip drives the whole pose: overlays, look-at and foot IK stay off during it.
		if (!ApplyGetUpClip(character))
		{
			if (!ApplyLocomotion(entity, character, deltaSeconds))
			{
				ApplyClip(entity, character, deltaSeconds);
			}

			ApplyOverlayClip(entity, character, deltaSeconds);
			ApplyAdditiveClip(entity, character, deltaSeconds);
			ApplyLookAt(entity, character);
			ApplyFootIk(entity, character, deltaSeconds);
		}

		ApplySpringBones(entity, character, deltaSeconds);
		SyncRagdoll(entity, character, deltaSeconds);
		ApplyRecoveryBlend(character);

		// Palette last, from the final pose; all instances of a character share one skeleton.
		character.Managed.ComputeSkinMatrices();

		foreach (int palette in character.Palettes)
		{
			_skinning.SetPalette(palette, character.Managed.SkinMatrices);
		}

		DrawDebug(character);
	}

	/// <summary>Resets per-frame counters; call once before iterating entities.</summary>
	public void BeginFrame() => ActiveRagdollCount = 0;
}
