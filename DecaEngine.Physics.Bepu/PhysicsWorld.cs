using System;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities.Memory;

namespace DecaEngine.Physics;

/// <summary>Raycast result; when <see cref="Hit"/> is false the other fields are undefined.</summary>
public struct RayHit
{
	public bool Hit;
	public Vector3 Position;
	public Vector3 Normal;
	public float Distance;

	/// <summary>Raw handle of the hit collidable; interpret via <see cref="IsStatic"/>.</summary>
	public int Collidable;
	public bool IsStatic;
}

/// <summary>
/// Physics world: Bepu simulation with a FIXED time step plus a frame-time accumulator. Fixed step
/// is required: contact solver and ragdoll motors are tuned for a specific dt.
/// </summary>
public sealed class PhysicsWorld : IDisposable
{
	/// <summary>Sim step. 1/120 s, not 1/60: ragdoll motors are visibly rubbery at 60 Hz and a
	/// finer step is cheaper than extra solver substeps.</summary>
	public const float FixedTimeStep = 1f / 120f;

	/// <summary>Step cap per <see cref="Update"/>; excess time is dropped to avoid the spiral of
	/// death after a long frame.</summary>
	private const int MaxStepsPerUpdate = 8;

	private readonly BufferPool _pool;
	private float _accumulator;

	public Simulation Simulation { get; }

	/// <summary>Fraction of a step accumulated past the last integrated one (0..1), used to
	/// interpolate render poses.</summary>
	public float InterpolationAlpha => _accumulator / FixedTimeStep;

	/// <summary>Debug contact recorder. Always created, off by default: callbacks are copied into
	/// the simulation at creation and cannot be added later.</summary>
	public PhysicsContactRecorder Contacts { get; } = new();

	/// <summary>Per-body properties read by the narrow phase; always created for the same reason
	/// as <see cref="Contacts"/>. Uses <see cref="CollidableProperty{T}"/> so it survives handle
	/// removal/reuse without manual cleanup.</summary>
	public CollidableProperty<PhysicsBodyProperties> Bodies { get; }

	/// <summary>Gravity the world was created with, for external jump/step-up math.</summary>
	public Vector3 Gravity { get; }

	public PhysicsWorld(Vector3 gravity, PhysicsMaterial? material = null)
	{
		Gravity = gravity;
		_pool = new BufferPool();
		Bodies = new CollidableProperty<PhysicsBodyProperties>(_pool);

		Simulation = Simulation.Create(_pool,
			new PhysicsNarrowPhaseCallbacks
			{
				Material = material ?? PhysicsMaterial.Default,
				Recorder = Contacts,
				Bodies = Bodies,
			},
			new PhysicsPoseCallbacks(gravity),
			// 8 solver iterations, 1 substep: Bepu's baseline for an ordinary scene.
			new SolveDescription(8, 1));
	}

	/// <summary>Advances the simulation by fixed steps; returns the number of steps integrated
	/// (0 = poses unchanged, consumers may skip transform re-upload).</summary>
	public int Update(float deltaSeconds)
	{
		// Negative/huge deltas really happen (debugger pause, minimized window, clock changes).
		if (!float.IsFinite(deltaSeconds) || deltaSeconds <= 0f)
		{
			return 0;
		}

		_accumulator += Math.Min(deltaSeconds, MaxStepsPerUpdate * FixedTimeStep);

		bool recording = Contacts.Enabled;

		int steps = 0;
		while (_accumulator >= FixedTimeStep && steps < MaxStepsPerUpdate)
		{
			// Contacts are a snapshot of the LAST step, not a per-frame sum, or a floor contact
			// would appear up to eight times.
			if (recording)
			{
				Contacts.Clear();
			}

			Simulation.Timestep(FixedTimeStep);
			_accumulator -= FixedTimeStep;
			steps++;
		}

		if (recording && steps > 0)
		{
			Contacts.Flush();
		}

		return steps;
	}

	// --- Bodies ----------------------------------------------------------------------------------

	/// <summary>Dynamic body: finite mass, fully simulation-driven.</summary>
	public BodyHandle AddDynamic(in RigidPose pose, TypedIndex shape, float mass, float speculativeMargin = 0.1f)
	{
		var inertia = ComputeInertia(shape, mass);
		var description = BodyDescription.CreateDynamic(pose, inertia,
			new CollidableDescription(shape, speculativeMargin), new BodyActivityDescription(0.01f));

		return Register(Simulation.Bodies.Add(description));
	}

	/// <summary>Writes default properties for every new body. Mandatory at creation: Bepu reuses
	/// handles, and a new body would otherwise inherit the removed body's collision filter.</summary>
	private BodyHandle Register(BodyHandle handle)
	{
		// GroupId 0 for all ordinary bodies is safe: subgroup masks are full, so AllowCollision
		// passes on the mask-intersection condition.
		Bodies.Allocate(handle) = new PhysicsBodyProperties
		{
			Filter = new SubgroupCollisionFilter(0),
			VelocityDriven = false,
		};

		return handle;
	}

	/// <summary>Kinematic body: infinite mass, externally driven. Used for character bones while
	/// animation-driven: they push the environment but are not pushed back.</summary>
	public BodyHandle AddKinematic(in RigidPose pose, TypedIndex shape, float speculativeMargin = 0.1f)
	{
		var description = BodyDescription.CreateKinematic(pose,
			new CollidableDescription(shape, speculativeMargin), new BodyActivityDescription(0.01f));

		return Register(Simulation.Bodies.Add(description));
	}

	public StaticHandle AddStatic(in RigidPose pose, TypedIndex shape) =>
		Simulation.Statics.Add(new StaticDescription(pose, shape));

	/// <summary>Marks a body whose horizontal velocity is code-driven: its contacts become
	/// frictionless (see <see cref="PhysicsBodyProperties.VelocityDriven"/>). Set AFTER creation.</summary>
	public void SetVelocityDriven(BodyHandle handle, bool value) =>
		Bodies[handle].VelocityDriven = value;

	private int _nextCollisionGroup;

	/// <summary>Fresh group id for linked bodies (one group per ragdoll). Zero is reserved for
	/// ordinary bodies and never handed out.</summary>
	public int NewCollisionGroup() => ++_nextCollisionGroup;

	/// <summary>Puts a body in a group as subgroup <paramref name="subgroupId"/>.</summary>
	public void SetCollisionGroup(BodyHandle handle, int group, int subgroupId) =>
		Bodies[handle].Filter = new SubgroupCollisionFilter(group, subgroupId);

	/// <summary>Disables collision for a PAIR of bodies (joint-adjacent ragdoll bones).</summary>
	public void DisableCollision(BodyHandle a, BodyHandle b) =>
		SubgroupCollisionFilter.DisableCollision(ref Bodies[a].Filter, ref Bodies[b].Filter);

	public void Remove(BodyHandle handle) => Simulation.Bodies.Remove(handle);

	public void Remove(StaticHandle handle) => Simulation.Statics.Remove(handle);

	/// <summary>Removes a shape WITH its buffers (RemoveAndDispose): mesh shapes own a BVH and a
	/// triangle array in the pool, and plain Remove would leak them on every scene rebuild.</summary>
	public void RemoveShape(TypedIndex shape) => Simulation.Shapes.RemoveAndDispose(shape, _pool);

	/// <summary>Inertia for a shape; Bepu has per-shape ComputeInertia with no shared interface,
	/// so the switch is centralized here.</summary>
	private BodyInertia ComputeInertia(TypedIndex shape, float mass)
	{
		switch (shape.Type)
		{
			case Sphere.Id:
				return Simulation.Shapes.GetShape<Sphere>(shape.Index).ComputeInertia(mass);
			case Capsule.Id:
				return Simulation.Shapes.GetShape<Capsule>(shape.Index).ComputeInertia(mass);
			case Box.Id:
				return Simulation.Shapes.GetShape<Box>(shape.Index).ComputeInertia(mass);
			case Cylinder.Id:
				return Simulation.Shapes.GetShape<Cylinder>(shape.Index).ComputeInertia(mass);
			case ConvexHull.Id:
				return Simulation.Shapes.GetShape<ConvexHull>(shape.Index).ComputeInertia(mass);
			default:
				// Meshes/compounds have no well-defined inertia; silently substituting one would
				// give a body that rotates wrong.
				throw new NotSupportedException(
					$"Shape type {shape.Type} cannot be used for a dynamic body - inertia is undefined.");
		}
	}

	// --- Shapes ----------------------------------------------------------------------------------

	public TypedIndex AddSphere(float radius) => Simulation.Shapes.Add(new Sphere(radius));

	public TypedIndex AddBox(Vector3 size) => Simulation.Shapes.Add(new Box(size.X, size.Y, size.Z));

	/// <summary>Capsule: primary limb/character shape - no corners for the solver to catch, cheap
	/// in the narrow phase.</summary>
	public TypedIndex AddCapsule(float radius, float length) => Simulation.Shapes.Add(new Capsule(radius, length));

	/// <summary>
	/// Static triangle mesh from engine geometry. Winding is REVERSED: Bepu's triangle front face
	/// is opposite the engine's, and Bepu meshes are one-sided, so wrong winding means no
	/// collisions at all. Verified against imported geometry (ScenePhysicsProbe, DECA_PROBE_SCENE=1),
	/// which drops a sphere with both windings and prints both results.
	/// </summary>
	public TypedIndex AddTriangleMesh(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<uint> indices, Vector3 scale)
	{
		int triangleCount = indices.Length / 3;
		_pool.Take<Triangle>(triangleCount, out var triangles);

		for (int i = 0; i < triangleCount; i++)
		{
			// Swapping the 2nd/3rd vertices reverses the winding.
			triangles[i] = new Triangle(
				vertices[(int)indices[i * 3 + 0]],
				vertices[(int)indices[i * 3 + 2]],
				vertices[(int)indices[i * 3 + 1]]);
		}

		// Slice is MANDATORY: BufferPool.Take rounds the length up to a power of two, and Mesh
		// takes the triangle count from the buffer Length; the uninitialized tail would produce
		// NaN triangles that break BVH construction and kill all collisions.
		return Simulation.Shapes.Add(new Mesh(triangles.Slice(0, triangleCount), scale, _pool));
	}

	// --- Raycasts --------------------------------------------------------------------------------

	private struct ClosestHitHandler : IRayHitHandler
	{
		public RayHit Result;

		public bool AllowTest(CollidableReference collidable) => true;

		public bool AllowTest(CollidableReference collidable, int childIndex) => true;

		public void OnRayHit(in BepuPhysics.Trees.RayData ray, ref float maximumT, float t, in Vector3 normal,
			CollidableReference collidable, int childIndex)
		{
			// Shrinking maximumT is a correctness requirement, not an optimization: Bepu reports
			// hits in arbitrary order and does not guarantee farther hits are enumerated.
			maximumT = t;

			Result.Hit = true;
			Result.Distance = t;
			Result.Position = ray.Origin + ray.Direction * t;
			Result.Normal = normal;
			Result.IsStatic = collidable.Mobility == CollidableMobility.Static;
			Result.Collidable = collidable.RawHandleValue;
		}
	}

	/// <summary>
	/// Closest ray hit. The direction is NOT normalized here: Bepu measures maximumT in units of
	/// the direction length, and normalizing would silently change the caller's range semantics.
	/// </summary>
	public RayHit RayCast(Vector3 origin, Vector3 direction, float maximumT)
	{
		var handler = new ClosestHitHandler();
		Simulation.RayCast(origin, direction, maximumT, ref handler);
		return handler.Result;
	}

	private struct ClosestStaticHitHandler : IRayHitHandler
	{
		public RayHit Result;

		public bool AllowTest(CollidableReference collidable) =>
			collidable.Mobility == CollidableMobility.Static;

		public bool AllowTest(CollidableReference collidable, int childIndex) =>
			collidable.Mobility == CollidableMobility.Static;

		public void OnRayHit(in BepuPhysics.Trees.RayData ray, ref float maximumT, float t, in Vector3 normal,
			CollidableReference collidable, int childIndex)
		{
			maximumT = t;

			Result.Hit = true;
			Result.Distance = t;
			Result.Position = ray.Origin + ray.Direction * t;
			Result.Normal = normal;
			Result.IsStatic = true;
			Result.Collidable = collidable.RawHandleValue;
		}
	}

	/// <summary>
	/// Same but STATIC-only. For ground probes this is correctness, not optimization: a downward
	/// ray over a lying ragdoll would hit the character's own capsules.
	/// </summary>
	public RayHit RayCastStatic(Vector3 origin, Vector3 direction, float maximumT)
	{
		var handler = new ClosestStaticHitHandler();
		Simulation.RayCast(origin, direction, maximumT, ref handler);
		return handler.Result;
	}

	public void Dispose()
	{
		Simulation.Dispose();
		_pool.Clear();
	}
}
