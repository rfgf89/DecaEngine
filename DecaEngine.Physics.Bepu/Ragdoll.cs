using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Constraints;

namespace DecaEngine.Physics;

/// <summary>One ragdoll bone description; authored by the caller, not derived from the skeleton.</summary>
public struct RagdollBoneDesc
{
	/// <summary>Skeleton joint the body is bound to; also the proximal end of the capsule.</summary>
	public int Joint;

	/// <summary>Joint defining capsule direction/length; -1 = use <see cref="Length"/> along joint Y.</summary>
	public int ChildJoint;

	/// <summary>Index of the parent RAGDOLL bone in this description (-1 for root), not a skeleton joint.</summary>
	public int Parent;

	public float Radius;
	public float Length;
	public float Mass;

	/// <summary>Cos of max swing from the assembly direction. 1 = rigid, -1 = free, 0 = no limit.</summary>
	public float SwingLimitCos;

	/// <summary>Twist limit around the bone's long axis, radians from assembly pose; 0 = no limit.</summary>
	public float TwistLimitAngle;

	/// <summary>Hinge axis in world at assembly time; zero = not a hinge (cone + twist instead).
	/// Hinged joints get no cone/twist; <see cref="HingeMinAngle"/>/<see cref="HingeMaxAngle"/>
	/// bound the single remaining DOF (radians from assembly pose, positive = deeper flex).</summary>
	public Vector3 HingeAxisWorld;
	public float HingeMinAngle;
	public float HingeMaxAngle;
}

/// <summary>
/// Ragdoll: capsules per bone with jointed constraints, switchable between animation-driven and
/// physics-driven modes (plus active ragdoll via angular servos).
///
/// Bodies are ALWAYS dynamic, even in animation mode: Bepu forbids constraints between two
/// kinematic bodies (making the whole set kinematic corrupted the solver heap). Animation mode is
/// implemented by hard velocity writes each frame instead of infinite mass.
/// </summary>
public sealed class Ragdoll
{
	private struct Bone
	{
		public BodyHandle Body;

		/// <summary>Kept so it can be removed: shapes live in the simulation registry independently of bodies.</summary>
		public TypedIndex Shape;

		public int Joint;
		public int Parent;
		public BodyInertia DynamicInertia;

		/// <summary>Body orientation relative to the joint at assembly; Bepu capsules lie along local Y.</summary>
		public Quaternion JointToBody;

		/// <summary>Capsule-center-to-joint offset, in BODY space.</summary>
		public Vector3 BodyToJoint;

		public ConstraintHandle Socket;
		public ConstraintHandle Servo;
		public bool HasServo;
	}

	private readonly PhysicsWorld _world;
	private readonly Bone[] _bones;

	/// <summary>True when pose comes from animation; bodies stay dynamic either way (see class doc).</summary>
	public bool IsAnimationDriven { get; private set; } = true;

	public int BoneCount => _bones.Length;

	/// <summary>Skeleton joint corresponding to a ragdoll bone.</summary>
	public int JointOf(int bone) => _bones[bone].Joint;

	/// <summary>Body of a bone, for diagnostics and external impulses.</summary>
	public BodyHandle BodyOf(int bone) => _bones[bone].Body;

	private Ragdoll(PhysicsWorld world, Bone[] bones)
	{
		_world = world;
		_bones = bones;
	}

	/// <summary>Builds a ragdoll from the current pose; jointWorld are WORLD-space joint matrices.
	/// Starts animation-driven.</summary>
	public static Ragdoll Build(PhysicsWorld world, ReadOnlySpan<RagdollBoneDesc> description,
		Matrix4x4[] jointWorld)
	{
		var bones = new Bone[description.Length];

		for (int i = 0; i < description.Length; i++)
		{
			var desc = description[i];
			var jointMatrix = jointWorld[desc.Joint];
			var jointPosition = jointMatrix.Translation;

			// Bone direction/length from the child joint if given, else along the joint's local Y
			// (terminal bones such as head/hand have no child in the rig).
			Vector3 direction;
			float length;

			if (desc.ChildJoint >= 0)
			{
				var toChild = jointWorld[desc.ChildJoint].Translation - jointPosition;
				length = toChild.Length();
				direction = length > 1e-5f ? toChild / length : Vector3.UnitY;
			}
			else
			{
				length = desc.Length;
				direction = Vector3.Normalize(new Vector3(jointMatrix.M21, jointMatrix.M22, jointMatrix.M23));
			}

			// Bepu capsule length is the CYLINDRICAL part only; subtract the end hemispheres.
			float cylinder = MathF.Max(length - 2f * desc.Radius, 0.01f);
			var shape = world.AddCapsule(desc.Radius, cylinder);

			// Capsule lies along local Y; rotate it along the bone.
			var orientation = FromToRotation(Vector3.UnitY, direction);
			var center = jointPosition + direction * (length * 0.5f);

			var body = world.AddDynamic(new RigidPose(center, orientation), shape,
				desc.Mass <= 0f ? 1f : desc.Mass);

			bones[i] = new Bone
			{
				Body = body,
				Shape = shape,
				Joint = desc.Joint,
				Parent = desc.Parent,
				DynamicInertia = world.Simulation.Bodies[body].LocalInertia,
				JointToBody = Quaternion.Conjugate(RotationOf(jointMatrix)) * orientation,
				BodyToJoint = Vector3.Transform(jointPosition - center, Quaternion.Conjugate(orientation)),
			};
		}

		// Subgroup filter: only joint-ADJACENT bones skip collision (their capsules overlap by
		// construction and contacts would fight the socket); all other pairs still collide so the
		// ragdoll keeps its volume. Mask is 64-bit: bones past 63 collide with everything.
		int group = world.NewCollisionGroup();

		for (int i = 0; i < bones.Length; i++)
		{
			world.SetCollisionGroup(bones[i].Body, group, i);
		}

		for (int i = 0; i < bones.Length; i++)
		{
			int parent = bones[i].Parent;
			if (parent >= 0)
			{
				world.DisableCollision(bones[i].Body, bones[parent].Body);
			}
		}

		var ragdoll = new Ragdoll(world, bones);
		ragdoll.CreateConstraints(description);
		return ragdoll;
	}

	/// <summary>Joints and limits; anchor is the PROXIMAL end of the bone (the joint itself), not the capsule center.</summary>
	private void CreateConstraints(ReadOnlySpan<RagdollBoneDesc> description)
	{
		for (int i = 0; i < _bones.Length; i++)
		{
			int parent = _bones[i].Parent;
			if (parent < 0)
			{
				continue;
			}

			var childPose = _world.Simulation.Bodies[_bones[i].Body].Pose;
			var parentPose = _world.Simulation.Bodies[_bones[parent].Body].Pose;

			var anchorWorld = childPose.Position +
				Vector3.Transform(_bones[i].BodyToJoint, childPose.Orientation);

			var socket = new BallSocket
			{
				LocalOffsetA = Vector3.Transform(anchorWorld - parentPose.Position,
					Quaternion.Conjugate(parentPose.Orientation)),
				LocalOffsetB = _bones[i].BodyToJoint,
				SpringSettings = new SpringSettings(30f, 1f),
			};

			_bones[i].Socket = _world.Simulation.Solver.Add(_bones[parent].Body, _bones[i].Body, socket);

			// Servo is created up front even when active ragdoll is off: adding a constraint later
			// forces a solver batch rebuild at the worst possible moment. Unused = zero max force.
			var servo = new AngularServo
			{
				TargetRelativeRotationLocalA = Quaternion.Conjugate(parentPose.Orientation) * childPose.Orientation,
				ServoSettings = new ServoSettings(float.MaxValue, 0f, 0f),
				SpringSettings = new SpringSettings(20f, 1f),
			};

			_bones[i].Servo = _world.Simulation.Solver.Add(_bones[parent].Body, _bones[i].Body, servo);
			_bones[i].HasServo = true;

			if (description[i].HingeAxisWorld.LengthSquared() > 1e-8f)
			{
				// Hinge replaces cone + twist; excess DOFs are held by axis alignment.
				var axis = Vector3.Normalize(description[i].HingeAxisWorld);

				var hinge = new AngularHinge
				{
					LocalHingeAxisA = Vector3.Transform(axis, Quaternion.Conjugate(parentPose.Orientation)),
					LocalHingeAxisB = Vector3.Transform(axis, Quaternion.Conjugate(childPose.Orientation)),
					SpringSettings = new SpringSettings(30f, 1f),
				};

				_world.Simulation.Solver.Add(_bones[parent].Body, _bones[i].Body, hinge);

				AddTwistRange(parent, i, parentPose, childPose, axis,
					description[i].HingeMinAngle, description[i].HingeMaxAngle);

				continue;
			}

			if (description[i].SwingLimitCos != 0f)
			{
				var swing = new SwingLimit
				{
					AxisLocalA = Vector3.Transform(Vector3.Transform(Vector3.UnitY, childPose.Orientation),
						Quaternion.Conjugate(parentPose.Orientation)),
					AxisLocalB = Vector3.UnitY,
					MinimumDot = description[i].SwingLimitCos,
					SpringSettings = new SpringSettings(30f, 1f),
				};

				_world.Simulation.Solver.Add(_bones[parent].Body, _bones[i].Body, swing);
			}

			if (description[i].TwistLimitAngle > 0f)
			{
				AddTwistLimit(parent, i, parentPose, childPose, description[i].TwistLimitAngle);
			}
		}
	}

	/// <summary>Derives hinge axis/range from the assembly pose (knees/elbows). A near-straight limb
	/// gives no reliable axis, so the joint stays cone-limited in that case.</summary>
	public static void MarkHinge(ref RagdollBoneDesc bone, Vector3 upperWorld, Vector3 midWorld,
		Vector3 footWorld)
	{
		var a = midWorld - upperWorld;
		var b = footWorld - midWorld;

		float aLength = a.Length();
		float bLength = b.Length();

		if (aLength < 1e-5f || bLength < 1e-5f)
		{
			return;
		}

		var axis = Vector3.Cross(a, b);
		float bend = MathF.Atan2(axis.Length() / (aLength * bLength),
			Vector3.Dot(a, b) / (aLength * bLength));

		const float straightMargin = 5f * MathF.PI / 180f;
		const float maxFlex = 140f * MathF.PI / 180f;

		if (bend < straightMargin)
		{
			return;
		}

		bone.HingeAxisWorld = axis / axis.Length();
		bone.HingeMinAngle = -MathF.Max(bend - straightMargin, 0f);
		bone.HingeMaxAngle = MathF.Max(maxFlex - bend, 0f);
	}

	/// <summary>Twist limit around the bone's long axis. Bepu's TwistLimit twists around basis Z
	/// with X as the zero angle, so a dedicated basis is built (Z along bone, shared X) and taken
	/// in ASSEMBLY pose local spaces so the limit is +/-angle from that pose.</summary>
	private void AddTwistLimit(int parent, int child, in RigidPose parentPose, in RigidPose childPose,
		float angle)
	{
		var twist = Vector3.Transform(Vector3.UnitY, childPose.Orientation);
		float length = twist.Length();

		if (length < 1e-5f)
		{
			return;
		}

		AddTwistRange(parent, child, parentPose, childPose, twist / length, -angle, angle);
	}

	/// <summary>Rotation limit around an arbitrary axis with an asymmetric range; also used for hinge angle.</summary>
	private void AddTwistRange(int parent, int child, in RigidPose parentPose, in RigidPose childPose,
		Vector3 axisWorld, float minimumAngle, float maximumAngle)
	{
		// Pick the helper axis least aligned with the twist axis to avoid a degenerate cross product.
		var helper = MathF.Abs(axisWorld.X) < 0.7f ? Vector3.UnitX : Vector3.UnitZ;
		var x = Vector3.Normalize(Vector3.Cross(helper, axisWorld));
		var y = Vector3.Cross(axisWorld, x);

		// Matrix rows are basis vectors (same convention as MathUtils.CreateTrs).
		var basis = Quaternion.CreateFromRotationMatrix(new Matrix4x4(
			x.X, x.Y, x.Z, 0f,
			y.X, y.Y, y.Z, 0f,
			axisWorld.X, axisWorld.Y, axisWorld.Z, 0f,
			0f, 0f, 0f, 1f));

		var limit = new TwistLimit
		{
			LocalBasisA = Quaternion.Concatenate(basis, Quaternion.Conjugate(parentPose.Orientation)),
			LocalBasisB = Quaternion.Concatenate(basis, Quaternion.Conjugate(childPose.Orientation)),
			MinimumAngle = minimumAngle,
			MaximumAngle = maximumAngle,
			SpringSettings = new SpringSettings(30f, 1f),
		};

		_world.Simulation.Solver.Add(_bones[parent].Body, _bones[child].Body, limit);
	}

	/// <summary>Removes bodies, constraints and shapes. Bepu removes constraints with the body, but
	/// shapes live in the registry on their own and must be removed explicitly.</summary>
	public void Destroy()
	{
		for (int i = 0; i < _bones.Length; i++)
		{
			_world.Remove(_bones[i].Body);
			_world.RemoveShape(_bones[i].Shape);
		}
	}

	/// <summary>World pose of a bone body (debug capsule wireframe is drawn from this, not the joint pose).</summary>
	public RigidPose PoseOf(int bone) => _world.Simulation.Bodies[_bones[bone].Body].Pose;

	/// <summary>Shape of a bone, so debug draw can show the actual dimensions.</summary>
	public TypedIndex ShapeOf(int bone) => _bones[bone].Shape;

	/// <summary>Parent RAGDOLL bone, -1 for root.</summary>
	public int ParentOf(int bone) => _bones[bone].Parent;

	/// <summary>Switches the pose source. Entering physics keeps current velocities so momentum carries over.</summary>
	public void SetAnimationDriven(bool animationDriven)
	{
		if (animationDriven == IsAnimationDriven)
		{
			return;
		}

		IsAnimationDriven = animationDriven;

		for (int i = 0; i < _bones.Length; i++)
		{
			// Via Awakener: the Bodies indexer returns by value, so writing Awake there is CS1612.
			_world.Simulation.Awakener.AwakenBody(_bones[i].Body);
		}
	}

	/// <summary>Drives bodies toward the animation pose: hard velocities in animation mode
	/// (teleporting would tunnel through obstacles), angular servos in dynamic mode.</summary>
	public void DriveToPose(Matrix4x4[] jointWorld, float deltaSeconds, float servoStrength = 0f)
	{
		if (deltaSeconds <= 0f)
		{
			// Zero step = edit mode: no time to drive with, so place bodies directly.
			TeleportToPose(jointWorld);
			return;
		}

		// Divisor must be >= the fixed sim step: with frames shorter than 1/120 the per-frame
		// velocity overshoots each integrated step and feedback blows up to NaN within seconds.
		float driveSeconds = MathF.Max(deltaSeconds, PhysicsWorld.FixedTimeStep);

		for (int i = 0; i < _bones.Length; i++)
		{
			var target = TargetPose(i, jointWorld);
			var body = _world.Simulation.Bodies[_bones[i].Body];

			if (IsAnimationDriven)
			{
				var current = body.Pose;

				body.Velocity.Linear = (target.Position - current.Position) / driveSeconds;
				body.Velocity.Angular = AngularVelocity(current.Orientation, target.Orientation, driveSeconds);
				body.Awake = true;
			}
			else if (servoStrength > 0f && _bones[i].HasServo && _bones[i].Parent >= 0)
			{
				var parentTarget = TargetPose(_bones[i].Parent, jointWorld);

				var servo = new AngularServo
				{
					TargetRelativeRotationLocalA =
						Quaternion.Conjugate(parentTarget.Orientation) * target.Orientation,
					ServoSettings = new ServoSettings(float.MaxValue, 0f, servoStrength),
					SpringSettings = new SpringSettings(20f, 1f),
				};

				_world.Simulation.Solver.ApplyDescription(_bones[i].Servo, servo);
			}
		}
	}

	/// <summary>Applies a velocity delta (not an impulse; effect must not depend on bone mass) weighted per joint.</summary>
	public void AddVelocity(Vector3 deltaVelocity, float[] jointWeights)
	{
		for (int i = 0; i < _bones.Length; i++)
		{
			int joint = _bones[i].Joint;
			float weight = joint >= 0 && joint < jointWeights.Length ? jointWeights[joint] : 0f;

			if (weight <= 0f)
			{
				continue;
			}

			var body = _world.Simulation.Bodies[_bones[i].Body];
			body.Velocity.Linear += deltaVelocity * weight;
			body.Awake = true;
		}
	}

	/// <summary>Places bodies at the pose directly, zeroing velocities; only valid when time is not advancing.</summary>
	public void TeleportToPose(Matrix4x4[] jointWorld)
	{
		for (int i = 0; i < _bones.Length; i++)
		{
			var target = TargetPose(i, jointWorld);
			var body = _world.Simulation.Bodies[_bones[i].Body];

			body.Pose = target;

			// Zero velocities, or the body flies off its new position on the first sim step.
			body.Velocity.Linear = Vector3.Zero;
			body.Velocity.Angular = Vector3.Zero;
			body.Awake = true;
		}
	}

	/// <summary>Reads the pose from bodies into WORLD joint matrices; inverse of <see cref="DriveToPose"/>.</summary>
	public void ReadPose(Matrix4x4[] jointWorld)
	{
		for (int i = 0; i < _bones.Length; i++)
		{
			var pose = _world.Simulation.Bodies[_bones[i].Body].Pose;

			var jointRotation = pose.Orientation * Quaternion.Conjugate(_bones[i].JointToBody);
			var jointPosition = pose.Position + Vector3.Transform(_bones[i].BodyToJoint, pose.Orientation);

			jointWorld[_bones[i].Joint] =
				Matrix4x4.CreateFromQuaternion(jointRotation) * Matrix4x4.CreateTranslation(jointPosition);
		}
	}

	private RigidPose TargetPose(int bone, Matrix4x4[] jointWorld)
	{
		var jointMatrix = jointWorld[_bones[bone].Joint];
		var orientation = RotationOf(jointMatrix) * _bones[bone].JointToBody;
		var position = jointMatrix.Translation - Vector3.Transform(_bones[bone].BodyToJoint, orientation);

		return new RigidPose(position, orientation);
	}

	/// <summary>Angular velocity taking one orientation to another over the step, via the shortest arc.</summary>
	private static Vector3 AngularVelocity(Quaternion from, Quaternion to, float deltaSeconds)
	{
		var delta = to * Quaternion.Conjugate(from);
		if (delta.W < 0f)
		{
			delta = new Quaternion(-delta.X, -delta.Y, -delta.Z, -delta.W);
		}

		var axis = new Vector3(delta.X, delta.Y, delta.Z);
		float sin = axis.Length();

		if (sin < 1e-6f)
		{
			return Vector3.Zero;
		}

		float angle = 2f * MathF.Atan2(sin, delta.W);
		return axis * (angle / (sin * deltaSeconds));
	}

	private static Quaternion RotationOf(in Matrix4x4 matrix)
	{
		var x = Vector3.Normalize(new Vector3(matrix.M11, matrix.M12, matrix.M13));
		var y = Vector3.Normalize(new Vector3(matrix.M21, matrix.M22, matrix.M23));
		var z = Vector3.Normalize(new Vector3(matrix.M31, matrix.M32, matrix.M33));

		return Quaternion.CreateFromRotationMatrix(new Matrix4x4(
			x.X, x.Y, x.Z, 0f,
			y.X, y.Y, y.Z, 0f,
			z.X, z.Y, z.Z, 0f,
			0f, 0f, 0f, 1f));
	}

	private static Quaternion FromToRotation(Vector3 from, Vector3 to)
	{
		float dot = Vector3.Dot(from, to);
		if (dot > 0.999999f)
		{
			return Quaternion.Identity;
		}

		if (dot < -0.999999f)
		{
			var axis = Vector3.Cross(Vector3.UnitX, from);
			if (axis.LengthSquared() < 1e-8f)
			{
				axis = Vector3.Cross(Vector3.UnitZ, from);
			}

			return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
		}

		return Quaternion.Normalize(new Quaternion(Vector3.Cross(from, to), 1f + dot));
	}
}
