using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;

namespace DecaEngine.Physics;

/// <summary>Contact material: friction and the spring the solver uses to resolve penetration.</summary>
public struct PhysicsMaterial
{
	public float FrictionCoefficient;
	public float MaximumRecoveryVelocity;
	public SpringSettings SpringSettings;

	public static PhysicsMaterial Default => new()
	{
		FrictionCoefficient = 1f,
		MaximumRecoveryVelocity = 2f,
		// 30 Hz, damping ratio 1: critically damped. Lower feels soft, higher jitters at this step.
		SpringSettings = new SpringSettings(30f, 1f),
	};
}

/// <summary>Subgroup collision filter; bodies in different groups always collide.</summary>
// Ported from the bepuphysics2 demos (Apache-2.0, Demos/Demos/SubgroupCollisionFilter.cs).
public struct SubgroupCollisionFilter
{
	/// <summary>Group of related bodies; different groups always collide.</summary>
	public int GroupId;

	/// <summary>Subgroups this body belongs to.</summary>
	public ulong SubgroupMembership;

	/// <summary>Subgroups of its own group this body collides with.</summary>
	public ulong CollidableSubgroups;

	/// <summary>A body outside any linkage: collides with everything.</summary>
	public SubgroupCollisionFilter(int groupId)
	{
		GroupId = groupId;
		SubgroupMembership = ulong.MaxValue;
		CollidableSubgroups = ulong.MaxValue;
	}

	/// <summary>Puts the body in one subgroup; ids outside 0..63 do not fit the bit mask.</summary>
	public SubgroupCollisionFilter(int groupId, int subgroupId)
	{
		GroupId = groupId;
		SubgroupMembership = subgroupId is >= 0 and < 64 ? 1UL << subgroupId : 0UL;
		CollidableSubgroups = ulong.MaxValue;
	}

	/// <summary>Disables collision between a pair; must be mutual, pair order is not defined.</summary>
	public static void DisableCollision(ref SubgroupCollisionFilter a, ref SubgroupCollisionFilter b)
	{
		a.CollidableSubgroups &= ~b.SubgroupMembership;
		b.CollidableSubgroups &= ~a.SubgroupMembership;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool AllowCollision(in SubgroupCollisionFilter a, in SubgroupCollisionFilter b) =>
		a.GroupId != b.GroupId || (a.CollidableSubgroups & b.SubgroupMembership) > 0;
}

/// <summary>Per-body narrow phase data; kept in one struct so the hot loop takes one cache miss.</summary>
public struct PhysicsBodyProperties
{
	public SubgroupCollisionFilter Filter;

	// Friction would eat the velocity just written: mu*g*dt = 0.082 m/s per substep at mu=1, 1/120 s.
	/// <summary>Body velocity is set by code, not the solver: its contacts get zero friction.</summary>
	public bool VelocityDriven;
}

/// <summary>Narrow phase callbacks; one material per scene, per-body data lives in the properties table.</summary>
public struct PhysicsNarrowPhaseCallbacks : INarrowPhaseCallbacks
{
	public PhysicsMaterial Material;

	/// <summary>Per-body properties; Bepu copies these callbacks once, so the reference is safe.</summary>
	public CollidableProperty<PhysicsBodyProperties>? Bodies;

	/// <summary>Debug contact point sink; null means contacts are not collected at all.</summary>
	public PhysicsContactRecorder? Recorder;

	// Needed to turn manifold offsets, which are relative to collidable A, into world positions.
	private Simulation? _simulation;

	public void Initialize(Simulation simulation)
	{
		_simulation = simulation;
		Bodies?.Initialize(simulation);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b,
		ref float speculativeMargin)
	{
		if (a.Mobility == CollidableMobility.Static && b.Mobility == CollidableMobility.Static)
		{
			return false;
		}

		// Bodies only: static handles are numbered separately and would index a foreign entry.
		if (Bodies != null && a.Mobility != CollidableMobility.Static &&
			b.Mobility != CollidableMobility.Static)
		{
			return SubgroupCollisionFilter.AllowCollision(
				Bodies[a.BodyHandle].Filter, Bodies[b.BodyHandle].Filter);
		}

		return true;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB) => true;

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold,
		out PairMaterialProperties pairMaterial) where TManifold : unmanaged, IContactManifold<TManifold>
	{
		pairMaterial.FrictionCoefficient = Bodies != null && IsVelocityDriven(pair.A, pair.B)
			? 0f
			: Material.FrictionCoefficient;
		pairMaterial.MaximumRecoveryVelocity = Material.MaximumRecoveryVelocity;
		pairMaterial.SpringSettings = Material.SpringSettings;

		if (Recorder is { Enabled: true } recorder && _simulation != null)
		{
			RecordContacts(recorder, workerIndex, pair, ref manifold);
		}

		return true;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private readonly bool IsVelocityDriven(CollidableReference a, CollidableReference b) =>
		(a.Mobility != CollidableMobility.Static && Bodies![a.BodyHandle].VelocityDriven) ||
		(b.Mobility != CollidableMobility.Static && Bodies![b.BodyHandle].VelocityDriven);

	// Kept out of line: this branch is off by default and must not bloat the narrow phase hot path.
	[MethodImpl(MethodImplOptions.NoInlining)]
	private readonly void RecordContacts<TManifold>(PhysicsContactRecorder recorder, int workerIndex,
		CollidablePair pair, ref TManifold manifold) where TManifold : unmanaged, IContactManifold<TManifold>
	{
		var origin = PositionOf(pair.A);
		bool againstStatic = pair.A.Mobility == CollidableMobility.Static ||
			pair.B.Mobility == CollidableMobility.Static;

		for (int i = 0; i < manifold.Count; i++)
		{
			manifold.GetContact(i, out var offset, out var normal, out float depth, out _);

			recorder.Record(workerIndex, new PhysicsContactRecorder.Contact
			{
				Position = origin + offset,
				Normal = normal,
				Depth = depth,
				AgainstStatic = againstStatic,
			});
		}
	}

	private readonly Vector3 PositionOf(CollidableReference collidable) =>
		collidable.Mobility == CollidableMobility.Static
			? _simulation!.Statics[collidable.StaticHandle].Pose.Position
			: _simulation!.Bodies[collidable.BodyHandle].Pose.Position;

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB,
		ref ConvexContactManifold manifold) => true;

	public void Dispose()
	{
	}
}

/// <summary>Pose integrator callbacks: gravity plus damping, where damping is given per second.</summary>
public struct PhysicsPoseCallbacks : IPoseIntegratorCallbacks
{
	public Vector3 Gravity;
	public float LinearDamping;
	public float AngularDamping;

	private Vector3Wide _gravityWideDt;
	private Vector<float> _linearDampingDt;
	private Vector<float> _angularDampingDt;

	public PhysicsPoseCallbacks(Vector3 gravity, float linearDamping = 0.03f, float angularDamping = 0.03f)
	{
		Gravity = gravity;
		LinearDamping = linearDamping;
		AngularDamping = angularDamping;
		_gravityWideDt = default;
		_linearDampingDt = default;
		_angularDampingDt = default;
	}

	public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;

	// Gravity and damping do not need substeps, and substeps are not free.
	public readonly bool AllowSubstepsForUnconstrainedBodies => false;

	// Kinematic velocity is driven from outside; gravity does not apply to it.
	public readonly bool IntegrateVelocityForKinematics => false;

	public void Initialize(Simulation simulation)
	{
	}

	public void PrepareForIntegration(float dt)
	{
		// Per-second damping to per-step: pow, so the result is independent of the step count.
		_linearDampingDt = new Vector<float>(MathF.Pow(MathHelper.Clamp(1f - LinearDamping, 0f, 1f), dt));
		_angularDampingDt = new Vector<float>(MathF.Pow(MathHelper.Clamp(1f - AngularDamping, 0f, 1f), dt));
		Vector3Wide.Broadcast(Gravity * dt, out _gravityWideDt);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void IntegrateVelocity(Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation,
		BodyInertiaWide localInertia, Vector<int> integrationMask, int workerIndex, Vector<float> dt,
		ref BodyVelocityWide velocity)
	{
		velocity.Linear = (velocity.Linear + _gravityWideDt) * _linearDampingDt;
		velocity.Angular *= _angularDampingDt;
	}
}
