using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using DecaEngine.Core.Diagnostics;
using DecaEngine.Editor.ECS;
using Friflo.Engine.ECS;
using DecaEngine.Scene;

namespace DecaEngine.Editor;

/// <summary>
/// Physics bodies for script-driven characters, keyed by prefab entity id. Lives beside the ECS
/// (like <see cref="AnimationDriver"/>) because Bepu handles are native state that does not
/// survive component copies on archetype changes. Characters move by SETTING VELOCITY on the
/// capsule, never by writing the pose: a written pose is a teleport that bypasses the contact
/// solver. Only horizontal velocity is set; vertical stays the body's own so gravity works.
/// </summary>
public sealed class CharacterMotionDriver
{
	private sealed class Character
	{
		public BodyHandle Body;
		public TypedIndex Shape;

		/// <summary>Dimensions the body was built with; Bepu shapes are immutable, so inspector edits rebuild the capsule.</summary>
		public float Radius;
		public float Height;

		/// <summary>Cylindrical part of a horizontal capsule; zero means vertical. A horizontal capsule has Height = 2*radius, so "feet = center - half height" holds for both.</summary>
		public float Length;

		/// <summary>Last NON-ZERO travel direction; a wall-blocked character keeps facing where it was going instead of spinning arbitrarily.</summary>
		public Vector3 Facing = Vector3.UnitZ;

		/// <summary>Direction the torso has turned to this frame; approaches Facing at the TurnSpeed angular limit to avoid one-frame snaps.</summary>
		public Vector3 SmoothedFacing = Vector3.UnitZ;

		/// <summary>Turn limit, rad/s; zero or less means instant.</summary>
		public float TurnSpeed;

		/// <summary>Whether Facing was seeded from the entity's ACTUAL rotation; the body is recreated after ragdoll recovery and the default UnitZ would snap the character toward world +Z.</summary>
		public bool FacingSeeded;

		/// <summary>Turn settings captured from the steering script; Apply walks bodies, not scripts, and does not need to know which script drove them.</summary>
		public bool FaceMotion = true;
		public Vector3 ModelForward = Vector3.UnitZ;

		/// <summary>Remaining coyote time: seconds a jump is still allowed AFTER leaving the ground.</summary>
		public float CoyoteLeft;
	}

	private readonly Dictionary<int, Character> _characters = new();
	private readonly List<int> _stale = new();

	public int CharacterCount => _characters.Count;

	/// <summary>Times a capsule was rescued from under the floor; nonzero in a normal scene means something pushes characters through the one-sided mesh.</summary>
	public int FloorRescues { get; private set; }

	/// <summary>Player input FOR THIS FRAME; written by the viewport before Steer (or by a probe, so control is testable headless).</summary>
	public PlayerInput Input;

	/// <summary>
	/// Creates/removes bodies and sets their velocity for the next step. Call BEFORE
	/// <see cref="ScenePhysics.Update"/>: velocity set after the step only applies to the next one,
	/// leaving the character a frame behind its own command.
	/// </summary>
	/// <param name="active">When inactive, all bodies are removed so a paused character stays where the scene author put it.</param>
	public void Steer(EntityStore? store, ScenePhysics? physics, bool active, float deltaSeconds = 0f,
		AnimationDriver? animation = null)
	{
		if (store == null || physics == null || !active)
		{
			Clear(physics);
			return;
		}

		// No floor yet - no bodies. Scene statics STREAM in: a capsule spawned into an empty world
		// falls below the future floor plane in a fraction of a second, and the one-sided mesh
		// then never sees it - the character falls forever. Bodies appear the first frame statics
		// exist (see ScenePhysics.HasStatics).
		if (!physics.HasStatics)
		{
			Clear(physics);
			return;
		}

		_rescueLogCooldown = MathF.Max(0f, _rescueLogCooldown - deltaSeconds);

		_stale.Clear();
		foreach (var id in _characters.Keys)
		{
			_stale.Add(id);
		}

		SteerPlayers(store, physics, deltaSeconds);

		store.Query<CircleMoveComponent, CharacterBodyComponent, Position, Rotation>().ForEachEntity(
			(ref CircleMoveComponent move, ref CharacterBodyComponent shape, ref Position position,
				ref Rotation rotation, Entity entity) =>
		{
			// Player input outranks the script: with both components, input drives the body -
			// otherwise two steerers would write velocity and traversal order would win.
			if (!move.Enabled || move.Radius <= 1e-4f || entity.HasComponent<PlayerMoveComponent>())
			{
				return;
			}

			// Fall/recover is decided BEFORE steering: a lying character has no body to steer.
			if (entity.HasComponent<FallRecoverComponent>() && animation != null)
			{
				if (!UpdateFallRecover(entity, physics, animation, move.Forward, deltaSeconds))
				{
					return;
				}
			}

			_stale.Remove(entity.Id);

			var parentToWorld = PrefabSceneViewport.ParentToWorldMatrix(entity);
			var character = EnsureBody(entity, shape, position.value, physics, parentToWorld);
			var body = physics.World.Simulation.Bodies[character.Body];

			// Must run first: steering and rays below measure from the feet, which must be ABOVE the floor.
			RescueFromUnderFloor(physics, character, body);

			SeedFacing(character, rotation.value, move.Forward, parentToWorld);

			// Steering is measured from the feet, not the capsule center: the circle is defined on
			// the ground, and mixing the two points shifts the circle by the capsule radius.
			var feet = body.Pose.Position - new Vector3(0f, character.Height * 0.5f, 0f);
			var velocity = CircleMotion.SteerVelocity(move, ToLocal(feet, parentToWorld), out float angle);

			// Phase is MEASURED, not integrated: the body can lag (blocked) or lead (pushed), and
			// an accumulated phase would diverge from it permanently.
			move.Angle = CircleMotion.Wrap(angle);

			var world = ToWorldDirection(velocity, parentToWorld);

			body.Velocity.Linear = new Vector3(world.X, body.Velocity.Linear.Y, world.Z);

			// The capsule must not tip or spin: orientation comes from facing smoothing, and both
			// angular velocity and accumulated rotation are cleared - velocity alone is not enough,
			// the solver can rotate the body via contacts within a step.
			body.Velocity.Angular = Vector3.Zero;
			body.Pose.Orientation = BodyOrientation(character);
			body.Awake = true;

			ApplyStepUp(physics, shape, character, body, world);

			var under = physics.SampleGround(
				feet + new Vector3(0f, 0.05f, 0f), -Vector3.UnitY, 0.25f);
			if (under.Hit)
			{
				ApplyGroundSnap(body, world, feet.Y - under.Position.Y);
			}

			if (world.LengthSquared() > 1e-6f)
			{
				character.Facing = Vector3.Normalize(world);
			}

			character.FaceMotion = move.FaceMotion;
			character.ModelForward = move.Forward;
			character.TurnSpeed = move.TurnSpeed * MathF.PI / 180f;
			AdvanceFacing(character, deltaSeconds);
		});

		foreach (var id in _stale)
		{
			RemoveCharacter(id, physics);
		}

		DetectRams(physics, animation, deltaSeconds);
	}

	/// <summary>Seeds torso facing from the entity's actual rotation for a freshly created body (first Play frame, ragdoll recovery); the default UnitZ would snap the character toward world +Z.</summary>
	private static void SeedFacing(Character character, in Quaternion rotation, Vector3 modelForward,
		in Matrix4x4 parentToWorld)
	{
		if (character.FacingSeeded)
		{
			return;
		}

		var forward = ToWorldDirection(Vector3.Transform(modelForward, rotation), parentToWorld);
		forward.Y = 0f;

		if (forward.LengthSquared() > 1e-6f)
		{
			forward = Vector3.Normalize(forward);
			character.Facing = forward;
			character.SmoothedFacing = forward;
		}

		character.FacingSeeded = true;
	}

	/// <summary>
	/// Turns the torso toward the travel direction with an angular speed limit; zero limit means
	/// instant (legacy behavior). Horizontal plane only - both scripts produce horizontal
	/// directions by construction.
	/// </summary>
	private static void AdvanceFacing(Character character, float deltaSeconds)
	{
		if (character.TurnSpeed <= 0f)
		{
			character.SmoothedFacing = character.Facing;
			return;
		}

		if (deltaSeconds <= 0f)
		{
			return;
		}

		var current = character.SmoothedFacing;
		var target = character.Facing;

		if (current.LengthSquared() < 1e-8f || target.LengthSquared() < 1e-8f)
		{
			character.SmoothedFacing = target;
			return;
		}

		current = Vector3.Normalize(current);
		target = Vector3.Normalize(target);

		// Signed angle in the horizontal plane, step clamped to the limit. A 180-degree reversal
		// picks an arbitrary side (cross is zero, Atan2 keeps the sign of zero) - acceptable,
		// since a real creature's choice there is arbitrary too.
		float cross = current.Z * target.X - current.X * target.Z;
		float signedAngle = MathF.Atan2(cross, Math.Clamp(Vector3.Dot(current, target), -1f, 1f));
		float step = Math.Clamp(signedAngle, -character.TurnSpeed * deltaSeconds,
			character.TurnSpeed * deltaSeconds);

		character.SmoothedFacing = MathF.Abs(step) >= MathF.Abs(signedAngle)
			? target
			: Vector3.Transform(current, Quaternion.CreateFromAxisAngle(Vector3.UnitY, step));
	}

	/// <summary>Per-entity reaction cooldown: colliding capsules stay in contact for dozens of frames, and without a cooldown each frame would restart the hit reaction.</summary>
	private readonly Dictionary<int, float> _ramCooldown = new();

	/// <summary>
	/// Ram detection: two moving characters closing at speed both get a hit reaction. Detects by
	/// CLOSING SPEED (relative velocity projected on the separating axis), not distance - side by
	/// side characters touch capsules constantly and must not be shoved for it.
	/// </summary>
	private void DetectRams(ScenePhysics physics, AnimationDriver? animation, float deltaSeconds)
	{
		if (animation == null || _characters.Count < 2)
		{
			return;
		}

		foreach (var id in _characters.Keys)
		{
			if (_ramCooldown.TryGetValue(id, out float left))
			{
				float next = left - deltaSeconds;
				_ramCooldown[id] = next;
			}
		}

		var entries = _characters.ToArray();

		for (int a = 0; a < entries.Length; a++)
		{
			for (int b = a + 1; b < entries.Length; b++)
			{
				var bodyA = physics.World.Simulation.Bodies[entries[a].Value.Body];
				var bodyB = physics.World.Simulation.Bodies[entries[b].Value.Body];

				var separation = bodyB.Pose.Position - bodyA.Pose.Position;
				float distance = separation.Length();
				// Half-lengths of horizontal capsules count toward touch distance: long bodies'
				// centers never get closer than the sum of half-lengths.
				float touch = entries[a].Value.Radius + entries[b].Value.Radius +
					(entries[a].Value.Length + entries[b].Value.Length) * 0.5f + 0.06f;

				if (distance > touch || distance < 1e-4f)
				{
					continue;
				}

				var axis = separation / distance;
				float approach = Vector3.Dot(bodyA.Velocity.Linear - bodyB.Velocity.Linear, axis);

				if (approach < 1.4f)
				{
					continue;
				}

				// Shove along the collision axis plus an upward component: a purely horizontal
				// nudge barely reads on a quadruped whose body is stiff along travel.
				var shove = axis * approach * 0.7f + Vector3.UnitY * approach * 0.25f;

				Trigger(entries[b].Key, shove, animation);
				Trigger(entries[a].Key, -shove, animation);
			}
		}

		void Trigger(int entityId, Vector3 impulse, AnimationDriver driver)
		{
			if (_ramCooldown.TryGetValue(entityId, out float left) && left > 0f)
			{
				return;
			}

			_ramCooldown[entityId] = 0.6f;
			driver.TriggerHitReaction(entityId, impulse);
		}
	}

	/// <summary>
	/// Step-up: without it a capsule cannot climb steps at all - the vertical face kills the
	/// horizontal velocity via contact. A downward ray just ahead of the capsule finds support
	/// above the feet within StepHeight and gives the body enough vertical speed to lift the
	/// capsule bottom onto the edge (ballistics use the world's real gravity, not 9.81). The lower
	/// threshold skips gentle slopes (contacts handle those); no ceiling check above the step on
	/// purpose - the solver cancels the hop via contact if there is one.
	/// </summary>
	private static void ApplyStepUp(ScenePhysics physics, in CharacterBodyComponent shape,
		Character character, BodyReference body, Vector3 worldVelocity)
	{
		if (shape.StepHeight <= 0f)
		{
			return;
		}

		var horizontal = new Vector3(worldVelocity.X, 0f, worldVelocity.Z);
		float speed = horizontal.Length();

		if (speed < 1e-4f)
		{
			return;
		}

		var direction = horizontal / speed;
		var feet = body.Pose.Position - new Vector3(0f, character.Height * 0.5f, 0f);

		// Ray half a body ahead: closer and the capsule has already hit and lost speed; farther
		// and hops start a meter before the stairs. A horizontal capsule's "front" is its front
		// end, not radius-from-center.
		var origin = feet + direction * (character.Radius + character.Length * 0.5f + 0.06f) +
			new Vector3(0f, shape.StepHeight + 0.05f, 0f);

		var ground = physics.SampleGround(origin, -Vector3.UnitY, shape.StepHeight + 0.05f);
		if (!ground.Hit)
		{
			return;
		}

		float rise = ground.Position.Y - feet.Y;
		if (rise < 0.04f || rise > shape.StepHeight)
		{
			return;
		}

		float gravity = MathF.Max(physics.World.Gravity.Length(), 1e-3f);
		float climb = MathF.Sqrt(2f * gravity * rise) * 1.1f;

		if (body.Velocity.Linear.Y < climb)
		{
			body.Velocity.Linear = new Vector3(
				body.Velocity.Linear.X, climb, body.Velocity.Linear.Z);
		}
	}

	/// <summary>
	/// Player steering: the direction arrives already in world space (keys+camera mapping is the
	/// viewport's job); same capsule discipline as the circle script - horizontal set, vertical
	/// kept, body orientation suppressed.
	/// </summary>
	private void SteerPlayers(EntityStore store, ScenePhysics physics, float deltaSeconds)
	{
		store.Query<PlayerMoveComponent, CharacterBodyComponent, Position, Rotation>().ForEachEntity(
			(ref PlayerMoveComponent move, ref CharacterBodyComponent shape, ref Position position,
				ref Rotation rotation, Entity entity) =>
		{
			if (!move.Enabled)
			{
				return;
			}

			_stale.Remove(entity.Id);

			var parentToWorld = PrefabSceneViewport.ParentToWorldMatrix(entity);
			var character = EnsureBody(entity, shape, position.value, physics, parentToWorld);
			var body = physics.World.Simulation.Bodies[character.Body];

			// Must run first: steering and rays below measure from the feet, which must be ABOVE the floor.
			RescueFromUnderFloor(physics, character, body);

			SeedFacing(character, rotation.value, move.Forward, parentToWorld);

			var direction = new Vector3(Input.MoveWorld.X, 0f, Input.MoveWorld.Z);
			float length = direction.Length();

			// Target direction comes from input; the torso turns toward it with a limit and
			// VELOCITY follows the TURNED direction (as in ozz motion samples, where movement
			// integrates behind the turn). On reversals the character carves an arc instead of
			// sliding sideways while the torso catches up.
			if (length > 1e-4f)
			{
				character.Facing = direction / length;
			}

			character.FaceMotion = move.FaceMotion;
			character.ModelForward = move.Forward;
			character.TurnSpeed = move.TurnSpeed * MathF.PI / 180f;
			AdvanceFacing(character, deltaSeconds);

			var world = length > 1e-4f
				? (move.FaceMotion && character.TurnSpeed > 0f ? character.SmoothedFacing : direction / length) *
					(Input.Run ? move.RunSpeed : move.WalkSpeed)
				: Vector3.Zero;

			body.Velocity.Linear = new Vector3(world.X, body.Velocity.Linear.Y, world.Z);
			body.Velocity.Angular = Vector3.Zero;
			body.Pose.Orientation = BodyOrientation(character);
			body.Awake = true;

			// One groundedness ray per frame, shared by jump eligibility, coyote time and ground
			// snapping (see ApplyGroundSnap).
			var feet = body.Pose.Position - new Vector3(0f, character.Height * 0.5f, 0f);
			var under = physics.SampleGround(
				feet + new Vector3(0f, 0.05f, 0f), -Vector3.UnitY, 0.25f);
			float gap = under.Hit ? feet.Y - under.Position.Y : float.MaxValue;

			// Re-arm coyote only WITHOUT upward velocity: a body that just jumped still reads
			// "near ground" for a few frames, and re-arming would grant a free double jump.
			bool grounded = gap < 0.06f && body.Velocity.Linear.Y <= 0.1f;

			character.CoyoteLeft = grounded
				? CoyoteSeconds
				: MathF.Max(0f, character.CoyoteLeft - deltaSeconds);

			if (Input.Jump && move.JumpSpeed > 0f && character.CoyoteLeft > 0f)
			{
				body.Velocity.Linear = new Vector3(
					body.Velocity.Linear.X, move.JumpSpeed, body.Velocity.Linear.Z);

				// Jumping BURNS coyote time, or a second press early in flight is a double jump.
				character.CoyoteLeft = 0f;
			}
			else
			{
				ApplyStepUp(physics, shape, character, body, world);
				ApplyGroundSnap(body, world, gap);
			}

		});
	}

	/// <summary>Coyote window; ~0.1 s (six frames) is what a player does not notice between eye and finger.</summary>
	private const float CoyoteSeconds = 0.12f;

	/// <summary>Rescue log cooldown: a body pinned under the floor would otherwise spam identical lines every frame.</summary>
	private float _rescueLogCooldown;

	/// <summary>
	/// Rescues the capsule from under the floor. The floor is a ONE-SIDED mesh (see
	/// PhysicsWorld.AddTriangleMesh): once the capsule center is below the plane the mesh stops
	/// seeing it and the solver can never help - free fall forever. Detection is geometric: a
	/// downward ray INSIDE the capsule's own column (center+radius down to feet) hitting a static
	/// more than half a radius above the feet is impossible without penetration. This is the ONLY
	/// place the body teleports; normal motion is velocity-driven. The ray deliberately reaches no
	/// deeper than half a capsule: a body placed far under the floor should stay visible, not be
	/// silently fixed.
	/// </summary>
	private void RescueFromUnderFloor(ScenePhysics physics, Character character, BodyReference body)
	{
		var feet = body.Pose.Position - new Vector3(0f, character.Height * 0.5f, 0f);
		var origin = body.Pose.Position + new Vector3(0f, character.Radius, 0f);
		var above = physics.SampleGround(origin, -Vector3.UnitY,
			character.Radius + character.Height * 0.5f);

		if (!above.Hit || above.Normal.Y < 0.3f)
		{
			return;
		}

		float depth = above.Position.Y - feet.Y;
		if (depth <= character.Radius * 0.5f)
		{
			return;
		}

		body.Pose.Position = body.Pose.Position + new Vector3(0f, depth, 0f);

		// Clamp vertical velocity at zero: accumulated fall speed would drive the body straight
		// back under on the next step.
		body.Velocity.Linear = new Vector3(body.Velocity.Linear.X,
			MathF.Max(body.Velocity.Linear.Y, 0f), body.Velocity.Linear.Z);
		body.Awake = true;
		FloorRescues++;

		if (_rescueLogCooldown <= 0f)
		{
			_rescueLogCooldown = 2f;
			EngineLog.Add(LogLevel.Warning,
				$"Character body was {depth:0.###} m under the floor and was lifted back (rescue #{FloorRescues}): " +
				"the floor reached the physics after the body did, or something pushed the body through the one-sided static mesh.");
		}
	}

	/// <summary>
	/// Ground snap on descents: a moving body with a SMALL gap under its feet is pulled to the
	/// support instead of a short ballistic hop - otherwise the character briefly floats on every
	/// downhill edge. The upper threshold separates descent from genuine flight; the vertical
	/// velocity check excludes a jump that just happened.
	/// </summary>
	private static void ApplyGroundSnap(BodyReference body, Vector3 worldVelocity, float gap)
	{
		if (gap < 0.01f || gap > 0.12f || body.Velocity.Linear.Y > 0.1f ||
			new Vector3(worldVelocity.X, 0f, worldVelocity.Z).LengthSquared() < 1e-4f)
		{
			return;
		}

		// Velocity, not pose, per the capsule discipline; pull scales with the gap and is capped,
		// or a constant strong pull would hammer the body into the floor every frame.
		float pull = MathF.Min(gap * 30f, 1.5f);
		body.Velocity.Linear = new Vector3(
			body.Velocity.Linear.X,
			MathF.Min(body.Velocity.Linear.Y, -pull),
			body.Velocity.Linear.Z);
	}

	/// <summary>
	/// Drives the walk -> fall -> get up -> walk cycle (see <see cref="FallRecoverComponent"/>).
	/// Returns true when the movement script currently controls the character. The script body and
	/// the ragdoll never coexist: a capsule left in place fights the bones for the same space, so
	/// the body is removed on fall and recreated on recovery where the character ended up.
	/// </summary>
	private bool UpdateFallRecover(Entity entity, ScenePhysics physics, AnimationDriver animation,
		Vector3 modelForward, float deltaSeconds)
	{
		ref var fall = ref entity.GetComponent<FallRecoverComponent>();
		ref var ragdoll = ref entity.GetComponent<RagdollComponent>();

		fall.StateTime += deltaSeconds;

		switch (fall.State)
		{
			case CharacterMotionState.Moving:
			{
				if (fall.FallEvery <= 0f || fall.StateTime < fall.FallEvery)
				{
					return true;
				}

				// Order matters: enabling the ragdoll before removing the body avoids a frame with
				// no physics at all, during which the character could fall through.
				ragdoll.Enabled = true;
				ragdoll.Physical = true;

				RemoveCharacter(entity.Id, physics);

				fall.State = CharacterMotionState.Falling;
				fall.StateTime = 0f;
				return false;
			}

			case CharacterMotionState.Falling:
			{
				// Do not poll for rest before MinFallTime: ragdoll bodies spawn at zero velocity
				// and formally look "settled" on the very first frame. After that: settled OR a
				// timeout - a snagged ragdoll can jitter forever.
				bool settled = fall.StateTime >= fall.MinFallTime &&
					animation.IsRagdollSettled(entity.Id, fall.SettleSpeed);

				if (!settled && fall.StateTime < fall.SettleTimeout)
				{
					return false;
				}

				// The character gets up WHERE IT LIES: the entity stayed at the fall point while
				// the body traveled, so the transform is moved to the body first.
				if (animation.TryGetRagdollRootWorld(entity.Id, out var root))
				{
					var parentToWorld = PrefabSceneViewport.ParentToWorldMatrix(entity);
					var local = ToLocal(root, parentToWorld);

					// Height comes from a ground ray under the lying spot - not from the bone (the
					// pelvis hovers at its own thickness) and not from the fall height (the floor
					// may be uneven). A ray miss keeps the previous height.
					ref var shape = ref entity.GetComponent<CharacterBodyComponent>();
					float reach = MathF.Max(shape.Height, 0.1f);
					var ground = physics.SampleGround(
						root + new Vector3(0f, reach, 0f), -Vector3.UnitY, reach * 4f);

					float y = ground.Hit
						? ToLocal(ground.Position, parentToWorld).Y
						: entity.Position.value.Y;

					entity.Position = new Position(local.X, y, local.Z);

					// Stand up ALONG the lying body, not the pre-fall rotation: a rolled ragdoll
					// lies at an arbitrary angle. The lying-pose snapshot is rebased into the
					// already-rotated transform (see BeginRecovery), so the visible pose does not move.
					if (animation.TryGetLyingFacing(entity.Id, out var lyingForward))
					{
						entity.GetComponent<Rotation>().value = CircleMotion.FacingFor(modelForward,
							ToLocalDirection(lyingForward, parentToWorld));
					}
				}

				// Get-up clip chosen by the actual lying side (back/belly); a single configured
				// clip serves both, none means the procedural morph.
				string getUpClip = animation.TryGetLyingSide(entity.Id, out bool onBack) && onBack
					? fall.GetUpBackClip
					: fall.GetUpBellyClip;

				if (string.IsNullOrEmpty(getUpClip))
				{
					getUpClip = string.IsNullOrEmpty(fall.GetUpBellyClip)
						? fall.GetUpBackClip
						: fall.GetUpBellyClip;
				}

				// Ragdoll back to animation mode; the transform passed is the ALREADY MOVED one so
				// the lying-pose snapshot rebases without a visible jump (see BeginRecovery).
				ragdoll.Physical = false;
				animation.BeginRecovery(entity.Id, fall.GetUpDuration,
					PrefabSceneViewport.ComputeWorldMatrix(entity), getUpClip ?? string.Empty);

				fall.State = CharacterMotionState.Recovering;
				fall.StateTime = 0f;
				return false;
			}

			default:
			{
				if (animation.IsRecovering(entity.Id))
				{
					return false;
				}

				// The ragdoll is DISABLED, not left following: kinematic bone bodies sit exactly
				// where the capsule is, and the solver pushes the capsule out of them every step
				// (measured 2.2-2.7 m/s at a commanded 1 m/s). Also restores the invariant that a
				// walking character has no ragdoll until a fall enables it.
				ragdoll.Enabled = false;

				fall.State = CharacterMotionState.Moving;
				fall.StateTime = 0f;
				return true;
			}
		}
	}

	/// <summary>
	/// Copies body poses into entity transforms. Call AFTER <see cref="ScenePhysics.Update"/> -
	/// before the step the body still holds last frame's pose and the scene would render a frame
	/// behind its own physics.
	/// </summary>
	public void Apply(EntityStore? store, ScenePhysics? physics)
	{
		if (store == null || physics == null || _characters.Count == 0)
		{
			return;
		}

		// Iterate by BODY, not by script: there are two movement scripts and the pose transfer is
		// identical for both; turn settings were captured by whichever script steered.
		store.Query<CharacterBodyComponent, Position, Rotation>().ForEachEntity(
			(ref CharacterBodyComponent shape, ref Position position, ref Rotation rotation,
				Entity entity) =>
		{
			if (!_characters.TryGetValue(entity.Id, out var character))
			{
				return;
			}

			var parentToWorld = PrefabSceneViewport.ParentToWorldMatrix(entity);
			var pose = physics.World.Simulation.Bodies[character.Body].Pose;
			var feet = pose.Position - new Vector3(0f, character.Height * 0.5f, 0f);

			position.value = ToLocal(feet, parentToWorld);

			if (character.FaceMotion)
			{
				rotation.value = CircleMotion.FacingFor(character.ModelForward,
					ToLocalDirection(character.SmoothedFacing, parentToWorld));
			}
		});
	}

	/// <summary>Removes all bodies. Call when scene physics turns off or the prefab changes: handles belong to a specific simulation and cannot outlive it.</summary>
	public void Clear(ScenePhysics? physics)
	{
		if (_characters.Count == 0)
		{
			return;
		}

		if (physics != null)
		{
			foreach (var character in _characters.Values)
			{
				physics.World.Remove(character.Body);
				physics.World.RemoveShape(character.Shape);
			}
		}

		_characters.Clear();
	}

	private Character EnsureBody(Entity entity, in CharacterBodyComponent shape, Vector3 localPosition,
		ScenePhysics physics, Matrix4x4 parentToWorld)
	{
		float radius = MathF.Max(shape.Radius, 1e-3f);
		float length = MathF.Max(shape.Length, 0f);

		// A horizontal capsule (Length > 0) lies on its side: its height is exactly two radii and
		// the authored Height is ignored (see CharacterBodyComponent.Length).
		float height = length > 0f ? radius * 2f : MathF.Max(shape.Height, radius * 2f);

		if (_characters.TryGetValue(entity.Id, out var existing))
		{
			// Exact comparison, no epsilon: sizes come straight from component fields and only
			// change via the inspector; tolerances belong where values are decomposed from matrices.
			if (existing.Radius == radius && existing.Height == height && existing.Length == length)
			{
				return existing;
			}

			RemoveCharacter(entity.Id, physics);
		}

		// Initial pose comes from the entity transform: starting anywhere else reads as a jump at Play.
		var feet = Vector3.Transform(localPosition, parentToWorld);

		var character = new Character
		{
			// Bepu capsule length is the CYLINDRICAL part, without hemispheres: passing the full
			// height would make the capsule two radii taller and float the character.
			Shape = physics.World.AddCapsule(radius, length > 0f ? length : MathF.Max(height - radius * 2f, 0f)),
			Radius = radius,
			Height = height,
			Length = length,
			Facing = Vector3.UnitZ,
		};

		character.Body = physics.World.AddDynamic(
			new RigidPose(feet + new Vector3(0f, height * 0.5f, 0f), BodyOrientation(character)), character.Shape,
			MathF.Max(shape.Mass, 1e-3f));

		// Frictionless contacts: friction eats exactly the velocity the script sets every frame
		// (measured 12.4% of the per-lap distance), and this body never rolls or slides on its own.
		physics.World.SetVelocityDriven(character.Body, true);

		_characters[entity.Id] = character;
		return character;
	}

	/// <summary>
	/// Capsule orientation. Vertical is identity (Bepu capsule axis is Y). Horizontal lies along
	/// the SMOOTHED facing: a 90-degree rotation around Y x facing maps Y to the horizontal view
	/// vector. Written every frame together with zeroed angular velocity - the body has no
	/// rotation of its own, steering supplies it.
	/// </summary>
	private static Quaternion BodyOrientation(Character character)
	{
		if (character.Length <= 0f)
		{
			return Quaternion.Identity;
		}

		var facing = new Vector3(character.SmoothedFacing.X, 0f, character.SmoothedFacing.Z);
		if (facing.LengthSquared() < 1e-8f)
		{
			facing = Vector3.UnitZ;
		}

		facing = Vector3.Normalize(facing);
		var axis = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, facing));
		return Quaternion.CreateFromAxisAngle(axis, MathF.PI * 0.5f);
	}

	private void RemoveCharacter(int id, ScenePhysics physics)
	{
		if (!_characters.Remove(id, out var character))
		{
			return;
		}

		physics.World.Remove(character.Body);
		physics.World.RemoveShape(character.Shape);
	}

	// Spaces: the body lives in WORLD space while entity Position/Rotation are in parent space.
	// The demo prefab root is identity, but assuming they coincide would break the moment a
	// character is placed under a transformed subtree.

	private static Vector3 ToLocal(Vector3 world, Matrix4x4 parentToWorld) =>
		Matrix4x4.Invert(parentToWorld, out var inverse) ? Vector3.Transform(world, inverse) : world;

	private static Vector3 ToWorldDirection(Vector3 local, Matrix4x4 parentToWorld) =>
		Vector3.TransformNormal(local, parentToWorld);

	private static Vector3 ToLocalDirection(Vector3 world, Matrix4x4 parentToWorld) =>
		Matrix4x4.Invert(parentToWorld, out var inverse)
			? Vector3.TransformNormal(world, inverse)
			: world;
}
