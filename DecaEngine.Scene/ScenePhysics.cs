using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using DecaEngine.Graphics;
using DecaEngine.Physics;
using DecaEngine.Animation;

namespace DecaEngine.Scene;

/// <summary>Editor-aware physics world for a prefab scene; statics are one merged mesh.</summary>
public sealed class ScenePhysics : IDisposable
{
	/// <summary>A ray cast during the last frame, kept whole for debug drawing.</summary>
	public struct RecordedRay
	{
		public Vector3 Origin;
		public Vector3 Direction;
		public float Length;
		public bool Hit;
		public Vector3 HitPosition;
		public Vector3 HitNormal;
	}

	private const int MaxRecordedRays = 256;

	private readonly List<Vector3> _staticVertices = new();
	private readonly List<uint> _staticIndices = new();
	private readonly List<RecordedRay> _rays = new();
	private readonly Stopwatch _stepTimer = new();

	private StaticHandle _staticHandle;
	private TypedIndex _staticShape;
	private bool _hasStatic;

	private bool _building;

	public PhysicsWorld World { get; }

	/// <summary>Stops the world entirely; not the same as stepping with dt = 0.</summary>
	public bool Paused { get; set; }

	/// <summary>Simulation time multiplier.</summary>
	public float TimeScale { get; set; } = 1f;

	/// <summary>Whether casts are recorded into <see cref="Rays"/>.</summary>
	public bool RecordRays { get; set; }

	public IReadOnlyList<RecordedRay> Rays => _rays;

	// --- Last-frame counters ---------------------------------------------------------------------

	public int LastStepCount { get; private set; }
	public double LastStepMilliseconds { get; private set; }
	public int StaticTriangleCount { get; private set; }

	/// <summary>False until the first non-empty rebuild: bodies must not be spawned floorless.</summary>
	public bool HasStatics => _hasStatic;
	public int RayCastsThisFrame { get; private set; }

	public int BodyCount => World.Simulation.Bodies.ActiveSet.Count + SleepingBodyCount;

	/// <summary>Bepu keeps sleeping bodies in sets past index 0, outside the active set.</summary>
	public int SleepingBodyCount
	{
		get
		{
			int count = 0;
			for (int i = 1; i < World.Simulation.Bodies.Sets.Length; i++)
			{
				ref var set = ref World.Simulation.Bodies.Sets[i];
				if (set.Allocated)
				{
					count += set.Count;
				}
			}

			return count;
		}
	}

	public ScenePhysics(Vector3 gravity)
	{
		World = new PhysicsWorld(gravity);
	}

	// --- Scene statics ---------------------------------------------------------------------------

	/// <summary>Starts a rebuild; the previous statics stay live until <see cref="EndStatics"/>.</summary>
	public void BeginStatics()
	{
		_staticVertices.Clear();
		_staticIndices.Clear();
		_building = true;
	}

	/// <summary>Adds a mesh in WORLD space; winding is passed through unchanged.</summary>
	public void AddStaticMesh(ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices)
	{
		if (!_building || positions.Length == 0 || indices.Length < 3)
		{
			return;
		}

		uint baseVertex = (uint)_staticVertices.Count;

		foreach (var position in positions)
		{
			_staticVertices.Add(position);
		}

		// Drop the ragged tail: Bepu's Mesh would read it as a triangle of foreign vertices.
		int triangleIndices = indices.Length - indices.Length % 3;
		for (int i = 0; i < triangleIndices; i++)
		{
			_staticIndices.Add(baseVertex + indices[i]);
		}
	}

	/// <summary>Finishes the rebuild: swaps the old statics for one merged mesh.</summary>
	public void EndStatics()
	{
		if (!_building)
		{
			return;
		}

		_building = false;

		// Must precede the removal: scene geometry streams, so an empty rebuild is transient.
		if (_staticIndices.Count < 3)
		{
			_staticVertices.Clear();
			_staticIndices.Clear();
			return;
		}

		if (_hasStatic)
		{
			World.Remove(_staticHandle);
			World.RemoveShape(_staticShape);
			_hasStatic = false;
			StaticTriangleCount = 0;
		}

		_staticShape = World.AddTriangleMesh(
			System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_staticVertices),
			System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_staticIndices),
			Vector3.One);

		// Vertices are already world-space, so pose and scale must stay identity.
		_staticHandle = World.AddStatic(new RigidPose(Vector3.Zero), _staticShape);
		_hasStatic = true;
		StaticTriangleCount = _staticIndices.Count / 3;

		// These hold a world-space copy of the whole scene: tens of MB once the BVH is built.
		_staticVertices.Clear();
		_staticIndices.Clear();
		_staticVertices.TrimExcess();
		_staticIndices.TrimExcess();
	}

	// --- Frame -----------------------------------------------------------------------------------

	/// <summary>Advances the simulation and returns the number of integrated steps.</summary>
	public int Update(float deltaSeconds)
	{
		_rays.Clear();
		RayCastsThisFrame = 0;

		if (Paused)
		{
			LastStepCount = 0;
			LastStepMilliseconds = 0.0;
			return 0;
		}

		_stepTimer.Restart();
		LastStepCount = World.Update(deltaSeconds * MathF.Max(TimeScale, 0f));
		_stepTimer.Stop();

		LastStepMilliseconds = _stepTimer.Elapsed.TotalMilliseconds;
		return LastStepCount;
	}

	// --- Queries ---------------------------------------------------------------------------------

	/// <summary>Casts against STATICS ONLY, so a character never hits its own ragdoll.</summary>
	public GroundSample SampleGround(Vector3 origin, Vector3 direction, float maximumT)
	{
		RayCastsThisFrame++;

		var hit = World.RayCastStatic(origin, direction, maximumT);

		if (RecordRays && _rays.Count < MaxRecordedRays)
		{
			_rays.Add(new RecordedRay
			{
				Origin = origin,
				Direction = direction,
				Length = maximumT,
				Hit = hit.Hit,
				HitPosition = hit.Position,
				HitNormal = hit.Normal,
			});
		}

		return new GroundSample
		{
			Hit = hit.Hit,
			Position = hit.Position,
			Normal = hit.Normal,
		};
	}

	public void Dispose()
	{
		// Simulation.Dispose frees the shapes along with the pool; no explicit static removal.
		World.Dispose();
	}
}
