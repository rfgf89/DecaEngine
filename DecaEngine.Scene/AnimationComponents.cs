using System.Numerics;
using Friflo.Engine.ECS;

namespace DecaEngine.Scene
{
	/// <summary>Authoring data only: runtime state (ozz skeleton and pose, skinning palette, spring
	/// inertia, ragdoll bodies) lives in a side registry because ECS freely copies components on
	/// archetype changes. Joints are named, never indexed - glTF node order shifts on re-export.</summary>
	public struct Animator() : IComponent
	{
		/// <summary>Clip name from the model; empty leaves the character in bind pose.</summary>
		public string ClipName = string.Empty;

		public float Speed = 1f;
		public bool Loop = true;
		public bool Playing = true;

		/// <summary>Playback time in seconds; kept in the component so it is scrubbable and
		/// captured by the Play Mode snapshot.</summary>
		public float Time = 0f;

		/// <summary>Root motion: the root joint's XZ translation is subtracted from the pose and
		/// accumulated into the entity position. Does not combine with Character Body.</summary>
		public bool RootMotion = false;
	}

	/// <summary>Idle/walk/run blend driven by the entity's MEASURED speed (not a scripted value, so
	/// the clip matches what is on screen), with playback rate scaled to stop foot sliding.
	/// Takes over the pose from <see cref="Animator"/> while active.</summary>
	public struct LocomotionComponent() : IComponent
	{
		public bool Enabled = true;

		public string IdleClip = "Idle";

		public string WalkClip = "Walk";
		public string RunClip = "Run";

		/// <summary>Speed (m/s) at which the walk clip plays at its authored rate.</summary>
		public float WalkSpeed = 1f;

		/// <summary>Same for the run clip (m/s). Walk and run are distinct gaits: switched
		/// discretely with hysteresis and a short crossfade, never blended by speed.</summary>
		public float RunSpeed = 3f;

		/// <summary>Measured-speed smoothing, 1/s; body speed is noisy from contacts.</summary>
		public float Smoothing = 8f;
	}

	/// <summary>Partial blend: the subtree under <see cref="RootJoint"/> plays its own clip while
	/// the rest of the skeleton keeps the base pose. Applied before look-at and foot IK.</summary>
	public struct OverlayClipComponent() : IComponent
	{
		public bool Enabled = true;

		public string ClipName = string.Empty;

		/// <summary>Subtree root; empty means the humanoid "chest" slot.</summary>
		public string RootJoint = string.Empty;

		/// <summary>Subtree weight 0..1; outside the subtree the base pose is untouched.</summary>
		public float Weight = 1f;

		public float Speed = 1f;
		public bool Loop = true;
	}

	/// <summary>Additive layer: the clip becomes a delta from its own first frame and is summed on
	/// top of the current pose, unlike <see cref="OverlayClipComponent"/> which replaces it.</summary>
	public struct AdditiveClipComponent() : IComponent
	{
		public bool Enabled = true;

		/// <summary>Source clip; the delta is derived from it, no authored delta clip needed.</summary>
		public string ClipName = string.Empty;

		public float Weight = 1f;
		public float Speed = 1f;
		public bool Loop = true;
	}

	/// <summary>Foot placement on terrain. Exactly two legs: a variable count would need a list,
	/// which the ECS store cannot copy. More legs are configured in code via FootIkLeg.</summary>
	public struct FootIkComponent() : IComponent
	{
		public bool Enabled = true;

		/// <summary>Pelvis joint; dropping it reaches the lower foot on steps. Empty = leave it.</summary>
		public string PelvisJoint = string.Empty;

		public string LeftUpperJoint = string.Empty;
		public string LeftLowerJoint = string.Empty;
		public string LeftFootJoint = string.Empty;

		public string RightUpperJoint = string.Empty;
		public string RightLowerJoint = string.Empty;
		public string RightFootJoint = string.Empty;

		/// <summary>Contact joint when the ground touch is below the ankle (digitigrade rigs).
		/// Empty falls back to the humanoid "toe" slot.</summary>
		public string LeftToeJoint = string.Empty;
		public string RightToeJoint = string.Empty;

		/// <summary>Height of the contact joint above the sole, in model units: the raycast gives a
		/// surface point but IK places the JOINT there. Too large lifts the feet off flat ground.</summary>
		public float AnkleHeight = 0.1f;

		/// <summary>Pelvis drop limit; without it a step into a chasm drags the character under.</summary>
		public float MaxPelvisDrop = 0.4f;

		/// <summary>Smoothing rate, 1/s; the ray jumps between faces at triangle seams.</summary>
		public float Smoothing = 12f;

		public float Weight = 1f;
		public bool AlignToNormal = true;

		/// <summary>Pin the planted foot to a world point, removing residual sliding when the clip
		/// rate does not match the real speed.</summary>
		public bool LockFeet = true;

		/// <summary>Front legs of a quadruped: a second pair of IK chains. Off by default because
		/// empty names resolve to the humanoid ARM slots, i.e. a biped's hands.</summary>
		public bool FrontLegs = false;

		public string FrontLeftUpperJoint = string.Empty;
		public string FrontLeftLowerJoint = string.Empty;
		public string FrontLeftFootJoint = string.Empty;

		public string FrontRightUpperJoint = string.Empty;
		public string FrontRightLowerJoint = string.Empty;
		public string FrontRightFootJoint = string.Empty;

		/// <summary>Front toes are authored only: there is no humanoid "hand toe" slot, and an
		/// empty field means the hand itself is the contact.</summary>
		public string FrontLeftToeJoint = string.Empty;
		public string FrontRightToeJoint = string.Empty;

		/// <summary>Pitch the pelvis along the slope, from the height difference between the front
		/// and rear contact pairs. Requires <see cref="FrontLegs"/>.</summary>
		public bool AlignBodyToSlope = true;
	}

	/// <summary>Secondary-motion chain (tail, braid, cloak). Given as a root joint plus a length;
	/// the system walks first children from there.</summary>
	public struct SpringBoneComponent() : IComponent
	{
		public bool Enabled = true;

		public string RootJoint = string.Empty;

		/// <summary>Joint count including the root.</summary>
		public int Length = 3;

		/// <summary>Pull back toward the animated pose per step, 0..1; 1 disables the effect.</summary>
		public float Stiffness = 0.08f;

		/// <summary>Velocity loss per step, 0..1; without it the chain oscillates forever.</summary>
		public float Drag = 0.2f;

		/// <summary>Length of the virtual tip bone: the last joint has no child to aim at.</summary>
		public float TailLength = 0.1f;

		/// <summary>External force in model space (usually gravity).</summary>
		public Vector3 Gravity = Vector3.Zero;
	}

	/// <summary>Aims one joint at a target. Chains are built from several entities or in code.</summary>
	public struct LookAtComponent() : IComponent
	{
		public bool Enabled = true;

		public string Joint = string.Empty;

		/// <summary>World-space point to look at.</summary>
		public Vector3 Target = Vector3.Zero;

		/// <summary>Gaze and up axes in joint-local space; rig-dependent and not guessable.</summary>
		public Vector3 Forward = Vector3.UnitZ;
		public Vector3 Up = Vector3.UnitY;

		public float Weight = 1f;
	}

	/// <summary>Character ragdoll. Bodies follow skeleton chains from <see cref="RootJoint"/>;
	/// <see cref="MaxDepth"/> bounds them, since a full finger rig would give hundreds of bodies.</summary>
	public struct RagdollComponent() : IComponent
	{
		/// <summary>Disabling DESTROYS the bodies - this is not a pause.</summary>
		public bool Enabled = false;

		/// <summary>false: pose drives physics; true: physics drives pose (the "knock down" switch).</summary>
		public bool Physical = false;

		/// <summary>Angular servo strength pulling joints to the animated pose; 0 = limp ragdoll.</summary>
		public float ServoStrength = 0f;

		public string RootJoint = string.Empty;

		/// <summary>Skeleton traversal depth from the root.</summary>
		public int MaxDepth = 4;

		/// <summary>One capsule radius for the whole skeleton, in model units. ZERO (the default)
		/// measures each bone's radius from the mesh instead, which any real character needs.</summary>
		public float BoneRadius = 0f;

		public float TotalMass = 70f;
	}
}
