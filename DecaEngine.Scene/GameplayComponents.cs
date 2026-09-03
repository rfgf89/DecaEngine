using System.Numerics;
using Friflo.Engine.ECS;

namespace DecaEngine.Scene
{
	/// <summary>Spins the entity around <see cref="Axis"/> while Play Mode is running.</summary>
	public struct RotateComponent : IComponent
	{
		public Vector3 Axis;
		public float DegreesPerSecond;
	}

	/// <summary>Moves the entity around a circle in the XZ plane during Play Mode.</summary>
	/// <remarks>Behaviour only; physical shape lives in <see cref="CharacterBodyComponent"/>.</remarks>
	public struct CircleMoveComponent : IComponent
	{
		public bool Enabled;

		/// <summary>Circle centre, in parent space like <see cref="Friflo.Engine.ECS.Position"/>.</summary>
		public Vector3 Center;

		public float Radius;

		/// <summary>Units per second along the circle; the sign picks the direction.</summary>
		public float Speed;

		/// <summary>Current phase in radians, measured from +X towards +Z.</summary>
		public float Angle;

		public bool FaceMotion;

		/// <summary>Turn rate cap in degrees/s. Zero means instant (legacy scenes).</summary>
		public float TurnSpeed;

		/// <summary>Model-space forward axis; engine convention is +Z, but Khronos Fox faces -Z.
		/// Only the horizontal component is used, so a vertical forward yields no turn, not NaN.</summary>
		public Vector3 Forward;

		// The editor creates components via new T(), so defaults live here, not at call sites.
		public CircleMoveComponent()
		{
			Enabled = true;
			Center = Vector3.Zero;
			Radius = 2f;
			Speed = 1f;
			Angle = 0f;
			FaceMotion = true;
			Forward = Vector3.UnitZ;
		}
	}

	/// <summary>Player-driven character: WASD/arrows move the capsule in Play, Shift runs.</summary>
	/// <remarks>Settings only; per-frame input lives in <see cref="PlayerInput"/>. Requires
	/// <see cref="CharacterBodyComponent"/>.</remarks>
	public struct PlayerMoveComponent : IComponent
	{
		public bool Enabled;

		/// <summary>Speed without Shift, m/s.</summary>
		public float WalkSpeed;

		/// <summary>Speed with Shift, m/s.</summary>
		public float RunSpeed;

		public bool FaceMotion;

		/// <summary>Model-space forward axis - see <see cref="CircleMoveComponent.Forward"/>.</summary>
		public Vector3 Forward;

		/// <summary>Jump velocity, m/s. Zero disables jumping (legacy scenes).</summary>
		public float JumpSpeed;

		/// <summary>Turn rate cap in degrees/s. Zero means instant (legacy scenes).</summary>
		public float TurnSpeed;

		public PlayerMoveComponent()
		{
			Enabled = true;
			WalkSpeed = 1f;
			RunSpeed = 3f;
			FaceMotion = true;
			Forward = Vector3.UnitZ;
			JumpSpeed = 3.5f;
			TurnSpeed = 360f;
		}
	}

	/// <summary>One frame of player input, already converted to world space by the viewport.</summary>
	/// <remarks>Deliberately not a component: it must not reach scene serialization or Play snapshots.</remarks>
	public struct PlayerInput
	{
		/// <summary>World-space move direction in the XZ plane; zero means stand still.</summary>
		public Vector3 MoveWorld;

		public bool Run;

		/// <summary>Jump key edge, not hold: a held Space must not autohop on every landing.</summary>
		public bool Jump;
	}

	/// <summary>What the character is doing right now.</summary>
	public enum CharacterMotionState
	{
		/// <summary>Following its route, pose driven by animation.</summary>
		Moving,

		/// <summary>Falling: pose driven by physics, script body detached.</summary>
		Falling,

		/// <summary>Getting up: pose blends from the lying snapshot back to animation.</summary>
		Recovering,
	}

	/// <summary>Drops the character as a ragdoll, waits for it to settle, then gets it back up.</summary>
	/// <remarks>Requires <see cref="RagdollComponent"/> and <see cref="CharacterBodyComponent"/>.</remarks>
	public struct FallRecoverComponent : IComponent
	{
		/// <summary>Seconds of motion before falling. Zero disables timed falls.</summary>
		public float FallEvery;

		/// <summary>Seconds to lie down before the settle check runs at all. Ragdoll bodies start
		/// from the animated pose at zero velocity, so they read as settled on the first frame.</summary>
		public float MinFallTime;

		/// <summary>Ceiling on the settle wait: a snagged ragdoll can jitter forever.</summary>
		public float SettleTimeout;

		/// <summary>Settle threshold as bone speed in fractions of skeleton size per second, since
		/// any absolute number only suits one model scale.</summary>
		public float SettleSpeed;

		/// <summary>Get-up pose blend duration in seconds; clamped to half the clip length.</summary>
		public float GetUpDuration;

		/// <summary>Authored get-up clips, picked by actual lying pose; empty means procedural morph.</summary>
		public string GetUpBackClip;
		public string GetUpBellyClip;

		/// <summary>Runtime state; kept in the component so Play Mode reverts it and the inspector shows it.</summary>
		public CharacterMotionState State;

		/// <summary>Seconds spent in the current state.</summary>
		public float StateTime;

		public FallRecoverComponent()
		{
			FallEvery = 6f;
			MinFallTime = 1.2f;
			SettleTimeout = 4f;
			SettleSpeed = 0.05f;
			GetUpDuration = 0.6f;
			GetUpBackClip = string.Empty;
			GetUpBellyClip = string.Empty;
			State = CharacterMotionState.Moving;
			StateTime = 0f;
		}
	}

	/// <summary>Physical body of a character: a scene-space capsule whose horizontal velocity is
	/// set by a motion script. Presence of this component is what makes a character physical.</summary>
	/// <remarks>Driven by velocity, not by pose: setting a pose teleports past the contact solver.</remarks>
	public struct CharacterBodyComponent : IComponent
	{
		/// <summary>Capsule radius in METRES (world units, unlike <see cref="RagdollComponent.BoneRadius"/>).</summary>
		public float Radius;

		/// <summary>Total height including the caps, metres. Below two radii it degenerates to a sphere.</summary>
		public float Height;

		/// <summary>Mass in kg. Does not affect self-motion, only collisions with other bodies.</summary>
		public float Mass;

		/// <summary>Max step height in metres; obstacles below it are cleared by a vertical hop.
		/// Zero disables step-up (legacy scenes).</summary>
		public float StepHeight;

		/// <summary>Length of the capsule's cylindrical part, metres. Zero means a vertical capsule
		/// sized by <see cref="Height"/>; above zero the capsule lies HORIZONTAL along the facing
		/// direction with height of two radii, which is what quadrupeds need.</summary>
		public float Length;

		public CharacterBodyComponent()
		{
			Radius = 0.3f;
			Height = 1.8f;
			Mass = 70f;
			StepHeight = 0.25f;
			Length = 0f;
		}
	}
}
