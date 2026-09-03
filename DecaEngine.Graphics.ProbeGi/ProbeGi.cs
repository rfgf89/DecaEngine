using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
using UnsafeCollections.Collections.Native;
using UnsafeCollections.Collections.Unsafe;

namespace DecaEngine.Graphics.ProbeGi;

public sealed class ProbeGiBaker
{
	// --- Scene: world triangles + BVH ---------------------------------------------------------

	private struct Tri
	{
		public Vector3 A, E1, E2;

		// Linear-space albedo (mean of base color texture x factor).
		public Vector3 Albedo;
	}

	private struct Node
	{
		public Vector3 Min, Max;

		// Leaf (Left<0): Start/Count slice _order. Else Left/Start = children, not adjacent.
		public int Left, Start, Count;
	}

	private Tri[] _tris = Array.Empty<Tri>();

	private ProbeInstancedGeometry _instanced = new()
	{
		Triangles = Array.Empty<BvhTriangleGpu>(),
		Meshes = Array.Empty<(int, int)>(),
		Instances = Array.Empty<ProbeGeometryInstance>(),
		HitTextureKeys = Array.Empty<(int, int)>(),
	};

	/// <summary>Object-space scene geometry plus instance table; BLAS/TLAS source for the HW path.</summary>
	public ProbeInstancedGeometry InstancedGeometry => _instanced;

	// Zero-length normals oct-encode to garbage; (0,0,1) is at least valid.
	private static Vector3 SafeNormalize(Vector3 n)
	{
		float lenSq = n.LengthSquared();
		return lenSq > 1e-12f ? n / MathF.Sqrt(lenSq) : Vector3.UnitZ;
	}

	/// <summary>Model instance world matrix; baked geometry and TLAS refits must share this one.</summary>
	public static Matrix4x4 InstanceMatrix(Transform t) =>
		Matrix4x4.CreateScale(t.scale.X, t.scale.Y, t.scale.Z) *
		Matrix4x4.CreateFromQuaternion(t.rotation) *
		Matrix4x4.CreateTranslation(t.position);
	private int[] _order = Array.Empty<int>();
	private Node[] _nodes = Array.Empty<Node>();
	private int _nodeCount;
	private float _sceneEpsilon = 1e-3f;
	private float _rayTMax = 1e4f;

	public bool HasGeometry => _tris.Length > 0;

	/// <summary>Triangle count in the BVH.</summary>
	public int TriangleCount => _tris.Length;

	// --- BVH disk cache (see ProbeGiBvhCache) --------------------------------------------------

	/// <summary>Serializable mirror of the private Tri.</summary>
	public struct CachedTri
	{
		public Vector3 A, E1, E2, Albedo;
	}

	/// <summary>Serializable mirror of the private Node.</summary>
	public struct CachedNode
	{
		public Vector3 Min, Max;
		public int Left, Start, Count;
	}

	/// <summary>Full snapshot of a built BVH, enough to restore a baker without model geometry.</summary>
	public sealed class BvhCacheData
	{
		public required CachedTri[] Triangles { get; init; }
		public required CachedNode[] Nodes { get; init; }
		public required int[] Order { get; init; }
		public required int NodeCount { get; init; }
		public required float SceneEpsilon { get; init; }
		public required float RayTMax { get; init; }
		public required BvhTriangleGpu[] ObjectTriangles { get; init; }
		public required (int First, int Count)[] MeshSlots { get; init; }
		public required ProbeGeometryInstance[] Instances { get; init; }
		public required (int Model, int Material)[] HitTextureKeys { get; init; }
	}

	private ProbeGiBaker(BvhCacheData data)
	{
		_tris = new Tri[data.Triangles.Length];
		for (int i = 0; i < _tris.Length; i++)
		{
			var t = data.Triangles[i];
			_tris[i] = new Tri { A = t.A, E1 = t.E1, E2 = t.E2, Albedo = t.Albedo };
		}

		_nodes = new Node[data.Nodes.Length];
		for (int i = 0; i < _nodes.Length; i++)
		{
			var n = data.Nodes[i];
			_nodes[i] = new Node { Min = n.Min, Max = n.Max, Left = n.Left, Start = n.Start, Count = n.Count };
		}

		_order = data.Order;
		_nodeCount = data.NodeCount;
		_sceneEpsilon = data.SceneEpsilon;
		_rayTMax = data.RayTMax;

		_instanced = new ProbeInstancedGeometry
		{
			Triangles = data.ObjectTriangles,
			Meshes = data.MeshSlots,
			Instances = data.Instances,
			HitTextureKeys = data.HitTextureKeys,
		};
	}

	/// <summary>Snapshot of the current BVH for writing to the cache.</summary>
	public BvhCacheData ExportCache()
	{
		var triangles = new CachedTri[_tris.Length];
		for (int i = 0; i < triangles.Length; i++)
		{
			ref var t = ref _tris[i];
			triangles[i] = new CachedTri { A = t.A, E1 = t.E1, E2 = t.E2, Albedo = t.Albedo };
		}

		var nodes = new CachedNode[_nodeCount];
		for (int i = 0; i < nodes.Length; i++)
		{
			ref var n = ref _nodes[i];
			nodes[i] = new CachedNode { Min = n.Min, Max = n.Max, Left = n.Left, Start = n.Start, Count = n.Count };
		}

		return new BvhCacheData
		{
			Triangles = triangles,
			Nodes = nodes,
			Order = _order,
			NodeCount = _nodeCount,
			SceneEpsilon = _sceneEpsilon,
			RayTMax = _rayTMax,
			ObjectTriangles = _instanced.Triangles,
			MeshSlots = _instanced.Meshes,
			Instances = _instanced.Instances,
			HitTextureKeys = _instanced.HitTextureKeys,
		};
	}

	/// <summary>Baker for one model, reusing the &lt;model&gt;.bhv.bin BVH cache when it is current.</summary>
	public static ProbeGiBaker LoadOrBuild(ModelLoader model, string modelPath, out bool fromCache)
	{
		fromCache = false;

		if (!string.IsNullOrEmpty(modelPath))
		{
			var cached = ProbeGiBvhCache.TryRead(modelPath);
			if (cached != null)
			{
				fromCache = true;
				return new ProbeGiBaker(cached);
			}
		}

		var baker = new ProbeGiBaker(model);

		if (!string.IsNullOrEmpty(modelPath) && baker.HasGeometry)
		{
			ProbeGiBvhCache.Write(modelPath, baker.ExportCache());
		}

		return baker;
	}

	// --- BVH diagnostics -----------------------------------------------------------------------

	/// <summary>Summary of the built tree, for debug output and the overlay.</summary>
	public readonly record struct BvhStats(int Triangles, int Nodes, int Leaves, int MaxDepth,
		float AvgLeafTriangles, Vector3 Min, Vector3 Max);

	public BvhStats GetStats()
	{
		if (_nodeCount == 0)
		{
			return new BvhStats(0, 0, 0, 0, 0f, Vector3.Zero, Vector3.Zero);
		}

		int leaves = 0, maxDepth = 0;
		long leafTris = 0;
		CountStats(0, 1, ref leaves, ref maxDepth, ref leafTris);

		return new BvhStats(_tris.Length, _nodeCount, leaves, maxDepth,
			leaves > 0 ? (float)leafTris / leaves : 0f, _nodes[0].Min, _nodes[0].Max);
	}

	private void CountStats(int nodeIndex, int depth, ref int leaves, ref int maxDepth, ref long leafTris)
	{
		ref var node = ref _nodes[nodeIndex];
		if (depth > maxDepth)
		{
			maxDepth = depth;
		}

		if (node.Left < 0)
		{
			leaves++;
			leafTris += node.Count;
			return;
		}

		CountStats(node.Left, depth + 1, ref leaves, ref maxDepth, ref leafTris);
		CountStats(node.Start, depth + 1, ref leaves, ref maxDepth, ref leafTris);
	}

	/// <summary>Node boxes for debug drawing, down to maxDepth (0 = root only).</summary>
	public List<(Vector3 Min, Vector3 Max, int Depth)> CollectDebugBoxes(int maxDepth, bool leavesOnly)
	{
		var boxes = new List<(Vector3, Vector3, int)>();
		if (_nodeCount > 0)
		{
			CollectBoxes(0, 0, maxDepth, leavesOnly, boxes);
		}

		return boxes;
	}

	private void CollectBoxes(int nodeIndex, int depth, int maxDepth, bool leavesOnly,
		List<(Vector3, Vector3, int)> boxes)
	{
		ref var node = ref _nodes[nodeIndex];
		bool isLeaf = node.Left < 0;

		if (!leavesOnly || isLeaf)
		{
			if (depth <= maxDepth || (leavesOnly && isLeaf))
			{
				boxes.Add((node.Min, node.Max, depth));
			}
		}

		if (isLeaf || depth >= maxDepth)
		{
			return;
		}

		CollectBoxes(node.Left, depth + 1, maxDepth, leavesOnly, boxes);
		CollectBoxes(node.Start, depth + 1, maxDepth, leavesOnly, boxes);
	}

	/// <summary>Ray distance cutoff; the GPU traversal must read it from here or the paths diverge.</summary>
	public float RayTMax => _rayTMax;

	/// <summary>Shadow ray surface offset; the GPU path must read it from here to match the CPU.</summary>
	public float SceneEpsilon => _sceneEpsilon;

	/// <summary>Ray directions for a round; the GPU path reads these instead of recomputing them.</summary>
	public static Vector3[] RoundRayDirections(int rays, int sequence) =>
		BuildRotatedFibonacciSphere(rays, sequence);

	/// <summary>Leading rays kept unrotated so relocation/classification stay temporally stable
	/// (RTXGI_DDGI_NUM_FIXED_RAYS). Realtime only; they are excluded from the radiance estimate.</summary>
	public static int FixedRayCount(int rays, bool realtime) =>
		realtime && rays >= 64 ? Math.Min(32, Math.Max(rays / 8, 16)) : 0;

	/// <summary>Round directions: [0, FixedRays) unrotated Fibonacci, the rest rotated per round.</summary>
	public static Vector3[] RoundRayDirections(ProbeGiBakeSession session) =>
		RoundRayDirections(session.RaysPerRound, session.Sequence, session.FixedRays);

	/// <inheritdoc cref="RoundRayDirections(ProbeGiBakeSession)"/>
	public static Vector3[] RoundRayDirections(int rays, int sequence, int fixedRays)
	{
		if (fixedRays <= 0)
		{
			return BuildRotatedFibonacciSphere(rays, sequence);
		}

		var dirs = new Vector3[rays];
		Array.Copy(BuildFibonacciSphere(fixedRays), dirs, fixedRays);
		Array.Copy(BuildRotatedFibonacciSphere(rays - fixedRays, sequence), 0,
			dirs, fixedRays, rays - fixedRays);
		return dirs;
	}

	/// <summary>Round weight in the running average; the GPU path must use the same formula.</summary>
	public static float RoundBlendWeight(ProbeGiBakeSession session)
	{
		int averaged = session.Round - BootstrapRounds;
		return averaged < 0 ? 1f : MathF.Max(1f / (averaged + 1), session.MinBlend);
	}

	/// <summary>Traces one ray on the CPU; the reference the GPU traversals are validated against.</summary>
	public bool TraceRay(Vector3 origin, Vector3 direction, float tMax,
		out float t, out Vector3 normal, out Vector3 albedo)
	{
		normal = Vector3.UnitY;
		albedo = Vector3.Zero;

		if (!TraceClosest(origin, direction, out t, out int triIndex) || t > tMax)
		{
			t = 0f;
			return false;
		}

		ref var tri = ref _tris[triIndex];
		normal = Vector3.Normalize(Vector3.Cross(tri.E1, tri.E2));
		albedo = tri.Albedo;
		return true;
	}

	/// <summary>Exports the BVH in StructuredBuffer layout for the compute traversal fallback.</summary>
	public (BvhNodeGpu[] Nodes, uint[] Order, BvhTriangleGpu[] Triangles) ExportBvh()
	{
		var nodes = new BvhNodeGpu[Math.Max(_nodeCount, 1)];
		for (int i = 0; i < _nodeCount; i++)
		{
			ref var node = ref _nodes[i];
			nodes[i] = new BvhNodeGpu
			{
				BoundsMin = node.Min,
				BoundsMax = node.Max,
				Left = node.Left,
				Start = node.Start,
				Count = node.Count,
			};
		}

		var order = new uint[Math.Max(_order.Length, 1)];
		for (int i = 0; i < _order.Length; i++)
		{
			order[i] = (uint)_order[i];
		}

		var triangles = new BvhTriangleGpu[Math.Max(_tris.Length, 1)];
		for (int i = 0; i < _tris.Length; i++)
		{
			ref var tri = ref _tris[i];
			triangles[i] = new BvhTriangleGpu
			{
				A = tri.A,
				E1 = tri.E1,
				E2 = tri.E2,
				Albedo = tri.Albedo,
			};
		}

		return (nodes, order, triangles);
	}

	/// <summary>Default rays per probe; the effective value comes from ProbeGiBakeOptions.</summary>
	public const int DefaultRaysPerProbe = 96;

	/// <summary>Upper clamp on the probe budget; the cell grows until the grid fits.</summary>
	public const int MaxProbeBudget = 2097152;

	/// <summary>Lower clamp on the probe budget; below 2x2x2 the grid is meaningless.</summary>
	public const int MinProbeBudget = 512;

	/// <summary>Coarse guard against degenerately elongated bounds; BeginBake checks atlas size.</summary>
	public const int MaxProbesPerAxis = 512;

	/// <summary>Texture side limit the probe atlases must fit; D3D12/Vulkan guarantee 16384.</summary>
	public const int MaxAtlasDimension = 16384;

	// Reads CPU mesh copies: call on the thread owning the model; Bake itself may go background.
	public unsafe ProbeGiBaker(ModelLoader model)
		: this(new[] { (model, Matrix4x4.Identity) }, trackSourceInstances: true)
	{
	}

	/// <summary>Multi-model scene; each instance enters the world BVH via InstanceMatrix * World.</summary>
	public unsafe ProbeGiBaker(IReadOnlyList<(ModelLoader Model, Matrix4x4 World)> models,
		bool trackSourceInstances = false)
	{
		var tris = new List<Tri>();

		// Object-space geometry for HW tracing, deduped per (model, mesh) so one BLAS is shared.
		var objectTris = new List<BvhTriangleGpu>();
		var meshSlots = new List<(int First, int Count)>();
		var meshSlotByMeshId = new Dictionary<(ModelLoader, int), int>();
		var geometryInstances = new List<ProbeGeometryInstance>();

		// Unique scene base color textures keyed by (model, material); -1 means over the cap.
		var hitTextureKeys = new List<(int Model, int Material)>();
		var hitTextureIndexByKey = new Dictionary<(int, int), int>();

		for (int modelIndex = 0; modelIndex < models.Count; modelIndex++)
		{
		var (model, world) = models[modelIndex];
		for (int sourceIndex = 0; sourceIndex < model.instances.Count; sourceIndex++)
		{
			var instance = model.instances[sourceIndex];
			if (instance.meshId < 0 || instance.meshId >= model.Meshes.Count)
			{
				continue;
			}

			// Skip glass, non-triangles, and cutout materials: the tracer samples no textures, so
			// lacy quads would act as solid walls. Keyed on mean alpha, not AlphaCutoff.
			Vector3 albedo = new(0.5f);
			bool pbrFound = model.MaterialPbr.TryGetValue(instance.materialId, out var pbr);
			if (pbrFound)
			{
				bool sparse = pbr.AlphaCutoff > 0f && pbr.HasBaseColorTexture && pbr.AverageAlpha < 0.6f;
				if (pbr.Topology != ModelLoader.MeshTopologyTriangles || pbr.TransmissionFactor > 0.5f ||
					sparse)
				{
					continue;
				}

				albedo = pbr.AverageBaseColor.LengthSquared() > 1e-6f
					? pbr.AverageBaseColor
					: new Vector3(pbr.BaseColorFactor.X, pbr.BaseColorFactor.Y, pbr.BaseColorFactor.Z);
			}

			// Clamp: albedo near 1 makes multi-bounce blow out in a closed courtyard.
			albedo = Vector3.Min(albedo, new Vector3(0.85f));

			var mesh = model.Meshes[instance.meshId];
			if (mesh.IndexCount < 3 || mesh.VertexData == null || mesh.IndexData == null)
			{
				continue;
			}

			var matrix = InstanceMatrix(instance.transform) * world;

			model.TriangleAlbedo.TryGetValue(instance.meshId, out var triAlbedo);
			var albedoCap = new Vector3(0.85f);

			// MR factors are only multipliers per glTF, so trust them only without an MR texture;
			// unknown metalness must mean non-metal or hit diffuse gets zeroed.
			model.TriangleMetalness.TryGetValue(instance.meshId, out var triMetalness);
			model.TriangleRoughness.TryGetValue(instance.meshId, out var triRoughness);
			bool factorsAuthoritative = pbrFound && !pbr.HasMetallicRoughnessTexture;
			float materialMetalness = factorsAuthoritative ? pbr.MetallicFactor : 0f;
			float materialRoughness = factorsAuthoritative ? pbr.RoughnessFactor : 1f;

			// Only meshes with real UVs qualify: synthesized zeros would sample one texel per mesh.
			int hitTextureIndex = -1;
			bool meshHasUv = instance.meshId < model.MeshHasUv.Count && model.MeshHasUv[instance.meshId];
			if (meshHasUv && pbrFound && pbr.HasBaseColorTexture &&
				model.MaterialBaseColor.ContainsKey(instance.materialId))
			{
				var textureKey = (modelIndex, instance.materialId);
				if (!hitTextureIndexByKey.TryGetValue(textureKey, out hitTextureIndex))
				{
					hitTextureIndex = hitTextureKeys.Count < ProbeInstancedGeometry.MaxHitTextures
						? hitTextureKeys.Count
						: -1;
					if (hitTextureIndex >= 0)
					{
						hitTextureKeys.Add(textureKey);
					}

					hitTextureIndexByKey[textureKey] = hitTextureIndex;
				}
			}

			var baseColorFactor = pbrFound
				? new Vector3(pbr.BaseColorFactor.X, pbr.BaseColorFactor.Y, pbr.BaseColorFactor.Z)
				: Vector3.One;

			int vertexCount = UnsafeArray.GetLength(mesh.VertexData);
			var vertices = new ReadOnlySpan<Vertex>(UnsafeArray.GetPtr<Vertex>(mesh.VertexData, 0), vertexCount);

			// ModelLoader always builds 32-bit indices.
			var indices = new ReadOnlySpan<uint>(UnsafeArray.GetPtr<uint>(mesh.IndexData, 0), mesh.IndexCount);

			// Object copy built for the first instance only; the rest reuse its slice and BLAS.
			if (!meshSlotByMeshId.TryGetValue((model, instance.meshId), out int meshSlot))
			{
				int firstObjectTri = objectTris.Count;
				for (int i = 0; i + 2 < indices.Length; i += 3)
				{
					uint j0 = indices[i], j1 = indices[i + 1], j2 = indices[i + 2];
					if (j0 >= vertexCount || j1 >= vertexCount || j2 >= vertexCount)
					{
						continue;
					}

					var oa = vertices[(int)j0].Position;
					var oe1 = vertices[(int)j1].Position - oa;
					var oe2 = vertices[(int)j2].Position - oa;
					if (Vector3.Cross(oe1, oe2).LengthSquared() < 1e-16f)
					{
						continue;
					}

					// KHR_texture_transform is baked in here, taken from the mesh's first instance.
					var uv0 = vertices[(int)j0].TexCoord;
					var uv1 = vertices[(int)j1].TexCoord;
					var uv2 = vertices[(int)j2].TexCoord;
					if (pbrFound && pbr.HasUvTransform)
					{
						var t = pbr.UvTransform;
						uv0 = new Vector2(uv0.X * t.X + uv0.Y * t.Y, uv0.X * t.Z + uv0.Y * t.W) + pbr.UvOffset;
						uv1 = new Vector2(uv1.X * t.X + uv1.Y * t.Y, uv1.X * t.Z + uv1.Y * t.W) + pbr.UvOffset;
						uv2 = new Vector2(uv2.X * t.X + uv2.Y * t.Y, uv2.X * t.Z + uv2.Y * t.W) + pbr.UvOffset;
					}

					// Fold wrap to zero before half packing: half loses UV precision past u=8.
					var uvShift = new Vector2(
						MathF.Floor(MathF.Min(uv0.X, MathF.Min(uv1.X, uv2.X))),
						MathF.Floor(MathF.Min(uv0.Y, MathF.Min(uv1.Y, uv2.Y))));

					// The HW SceneTrace path reads tri.albedo, so it must be filled here too.
					objectTris.Add(new BvhTriangleGpu
					{
						A = oa, E1 = oe1, E2 = oe2,
						UvA = BvhTriangleGpu.PackUv(uv0 - uvShift),
						UvB = BvhTriangleGpu.PackUv(uv1 - uvShift),
						UvC = BvhTriangleGpu.PackUv(uv2 - uvShift),
						Albedo = triAlbedo != null
							? Vector3.Min(triAlbedo[i / 3], albedoCap)
							: albedo,
						Metalness = triMetalness != null ? triMetalness[i / 3] : materialMetalness,
						Roughness = triRoughness != null ? triRoughness[i / 3] : materialRoughness,
						NormalA = BvhTriangleGpu.PackOctNormal(SafeNormalize(vertices[(int)j0].Normal)),
						NormalB = BvhTriangleGpu.PackOctNormal(SafeNormalize(vertices[(int)j1].Normal)),
						NormalC = BvhTriangleGpu.PackOctNormal(SafeNormalize(vertices[(int)j2].Normal)),
					});
				}

				// Fully degenerate mesh: nothing for an instance to reference, no BLAS to build.
				meshSlot = objectTris.Count > firstObjectTri ? meshSlots.Count : -1;
				if (meshSlot >= 0)
				{
					meshSlots.Add((firstObjectTri, objectTris.Count - firstObjectTri));
				}

				meshSlotByMeshId[(model, instance.meshId)] = meshSlot;
			}

			if (meshSlot >= 0)
			{
				geometryInstances.Add(new ProbeGeometryInstance(meshSlot,
					trackSourceInstances ? sourceIndex : -1, albedo, matrix,
					modelIndex, InstanceMatrix(instance.transform),
					hitTextureIndex, baseColorFactor));
			}

			for (int i = 0; i + 2 < indices.Length; i += 3)
			{
				uint i0 = indices[i], i1 = indices[i + 1], i2 = indices[i + 2];
				if (i0 >= vertexCount || i1 >= vertexCount || i2 >= vertexCount)
				{
					continue;
				}

				var a = Vector3.Transform(vertices[(int)i0].Position, matrix);
				var b = Vector3.Transform(vertices[(int)i1].Position, matrix);
				var c = Vector3.Transform(vertices[(int)i2].Position, matrix);

				var e1 = b - a;
				var e2 = c - a;
				if (Vector3.Cross(e1, e2).LengthSquared() < 1e-16f)
				{
					continue;
				}

				tris.Add(new Tri
				{
					A = a, E1 = e1, E2 = e2,
					Albedo = triAlbedo != null ? Vector3.Min(triAlbedo[i / 3], albedoCap) : albedo,
				});
			}
		}
		}

		_tris = tris.ToArray();

		// Degeneracy is tested per space, so world and object triangle counts may differ slightly.
		_instanced = new ProbeInstancedGeometry
		{
			Triangles = objectTris.ToArray(),
			Meshes = meshSlots.ToArray(),
			Instances = geometryInstances.ToArray(),
			HitTextureKeys = hitTextureKeys.ToArray(),
		};

		if (_tris.Length == 0)
		{
			return;
		}

		BuildBvh();
	}

	// --- BVH (median split on the largest axis, leaf <= 4 triangles) ---------------------------

	private void BuildBvh()
	{
		int n = _tris.Length;
		_order = new int[n];
		var centroids = new Vector3[n];
		for (int i = 0; i < n; i++)
		{
			_order[i] = i;
			centroids[i] = _tris[i].A + (_tris[i].E1 + _tris[i].E2) * (1f / 3f);
		}

		_nodes = new Node[2 * n];
		_nodeCount = 0;
		var sceneMin = new Vector3(float.MaxValue);
		var sceneMax = new Vector3(float.MinValue);

		BuildNode(0, n, centroids, ref sceneMin, ref sceneMax);

		var size = sceneMax - sceneMin;
		float maxDim = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
		_sceneEpsilon = MathF.Max(maxDim * 5e-4f, 1e-5f);
		_rayTMax = MathF.Max(maxDim * 4f, 1f);
	}

	private int BuildNode(int start, int count, Vector3[] centroids, ref Vector3 outMin, ref Vector3 outMax)
	{
		int nodeIndex = _nodeCount++;
		var min = new Vector3(float.MaxValue);
		var max = new Vector3(float.MinValue);
		for (int i = start; i < start + count; i++)
		{
			ref var tri = ref _tris[_order[i]];
			var b = tri.A + tri.E1;
			var c = tri.A + tri.E2;
			min = Vector3.Min(min, Vector3.Min(tri.A, Vector3.Min(b, c)));
			max = Vector3.Max(max, Vector3.Max(tri.A, Vector3.Max(b, c)));
		}

		outMin = Vector3.Min(outMin, min);
		outMax = Vector3.Max(outMax, max);

		if (count <= 4)
		{
			_nodes[nodeIndex] = new Node { Min = min, Max = max, Left = -1, Start = start, Count = count };
			return nodeIndex;
		}

		var size = max - min;
		int axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;

		Array.Sort(_order, start, count, Comparer<int>.Create((x, y) =>
			GetAxis(centroids[x], axis).CompareTo(GetAxis(centroids[y], axis))));

		int half = count / 2;
		var dummyMin = new Vector3(float.MaxValue);
		var dummyMax = new Vector3(float.MinValue);
		int left = BuildNode(start, half, centroids, ref dummyMin, ref dummyMax);
		int right = BuildNode(start + half, count - half, centroids, ref dummyMin, ref dummyMax);

		_nodes[nodeIndex] = new Node { Min = min, Max = max, Left = left, Start = right, Count = 0 };
		return nodeIndex;
	}

	private static float GetAxis(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

	// --- Tracing --------------------------------------------------------------------------------

	private static bool RayBox(Vector3 origin, Vector3 invDir, float tMax, in Node node)
	{
		float tx1 = (node.Min.X - origin.X) * invDir.X;
		float tx2 = (node.Max.X - origin.X) * invDir.X;
		float tmin = MathF.Min(tx1, tx2);
		float tmax = MathF.Max(tx1, tx2);

		float ty1 = (node.Min.Y - origin.Y) * invDir.Y;
		float ty2 = (node.Max.Y - origin.Y) * invDir.Y;
		tmin = MathF.Max(tmin, MathF.Min(ty1, ty2));
		tmax = MathF.Min(tmax, MathF.Max(ty1, ty2));

		float tz1 = (node.Min.Z - origin.Z) * invDir.Z;
		float tz2 = (node.Max.Z - origin.Z) * invDir.Z;
		tmin = MathF.Max(tmin, MathF.Min(tz1, tz2));
		tmax = MathF.Min(tmax, MathF.Max(tz1, tz2));

		return tmax >= MathF.Max(tmin, 0f) && tmin <= tMax;
	}

	// Moller-Trumbore, double-sided. Returns t or -1.
	private static float RayTri(Vector3 origin, Vector3 dir, in Tri tri)
	{
		var p = Vector3.Cross(dir, tri.E2);
		float det = Vector3.Dot(tri.E1, p);
		if (MathF.Abs(det) < 1e-9f)
		{
			return -1f;
		}

		float invDet = 1f / det;
		var s = origin - tri.A;
		float u = Vector3.Dot(s, p) * invDet;
		if (u < 0f || u > 1f)
		{
			return -1f;
		}

		var q = Vector3.Cross(s, tri.E1);
		float v = Vector3.Dot(dir, q) * invDet;
		if (v < 0f || u + v > 1f)
		{
			return -1f;
		}

		float t = Vector3.Dot(tri.E2, q) * invDet;
		return t > 0f ? t : -1f;
	}

	private bool TraceClosest(Vector3 origin, Vector3 dir, out float hitT, out int hitTri)
	{
		hitT = _rayTMax;
		hitTri = -1;

		var invDir = new Vector3(
			1f / (MathF.Abs(dir.X) < 1e-12f ? MathF.CopySign(1e-12f, dir.X) : dir.X),
			1f / (MathF.Abs(dir.Y) < 1e-12f ? MathF.CopySign(1e-12f, dir.Y) : dir.Y),
			1f / (MathF.Abs(dir.Z) < 1e-12f ? MathF.CopySign(1e-12f, dir.Z) : dir.Z));

		Span<int> stack = stackalloc int[64];
		int sp = 0;
		stack[sp++] = 0;

		while (sp > 0)
		{
			ref var node = ref _nodes[stack[--sp]];
			if (!RayBox(origin, invDir, hitT, node))
			{
				continue;
			}

			if (node.Left < 0)
			{
				for (int i = node.Start; i < node.Start + node.Count; i++)
				{
					int triIndex = _order[i];
					float t = RayTri(origin, dir, _tris[triIndex]);
					if (t > 0f && t < hitT)
					{
						hitT = t;
						hitTri = triIndex;
					}
				}
			}
			else if (sp + 2 <= stack.Length)
			{
				stack[sp++] = node.Left;
				stack[sp++] = node.Start;
			}
		}

		return hitTri >= 0;
	}

	private bool TraceAnyHit(Vector3 origin, Vector3 dir, float tMax)
	{
		var invDir = new Vector3(
			1f / (MathF.Abs(dir.X) < 1e-12f ? MathF.CopySign(1e-12f, dir.X) : dir.X),
			1f / (MathF.Abs(dir.Y) < 1e-12f ? MathF.CopySign(1e-12f, dir.Y) : dir.Y),
			1f / (MathF.Abs(dir.Z) < 1e-12f ? MathF.CopySign(1e-12f, dir.Z) : dir.Z));

		Span<int> stack = stackalloc int[64];
		int sp = 0;
		stack[sp++] = 0;

		while (sp > 0)
		{
			ref var node = ref _nodes[stack[--sp]];
			if (!RayBox(origin, invDir, tMax, node))
			{
				continue;
			}

			if (node.Left < 0)
			{
				for (int i = node.Start; i < node.Start + node.Count; i++)
				{
					float t = RayTri(origin, dir, _tris[_order[i]]);
					if (t > 0f && t < tMax)
					{
						return true;
					}
				}
			}
			else if (sp + 2 <= stack.Length)
			{
				stack[sp++] = node.Left;
				stack[sp++] = node.Start;
			}
		}

		return false;
	}

	// --- Dense probe grid ------------------------------------------------------------------------

	// Storage order: rows along Y within a Z plane, planes stacked. Addresses all CPU field buffers.
	internal static int StorageIndex(int sx, int sy, int sz, int cx, int cy) =>
		(sz * cy + sy) * cx + sx;

	// Atlas width equals the grid X axis, so divmod is the whole addressing.
	private static (int X, int Y) ProbeTexel(int storageIndex, int cx) =>
		(storageIndex % cx, storageIndex / cx);

	// Area-weighted surface capture into a sparse voxel grid; built once per session.
	private SurfaceCache BuildSurfaceCache(Vector3 origin, Vector3 cell, int cx, int cy, int cz)
	{
		const int sub = SurfaceCache.Subdivision;
		var voxel = cell / sub;
		int vx = Math.Max(1, (cx - 1) * sub);
		int vy = Math.Max(1, (cy - 1) * sub);
		int vz = Math.Max(1, (cz - 1) * sub);

		var cache = new SurfaceCache(origin, voxel, vx, vy, vz);
		int total = vx * vy * vz;
		var dense = new int[total];
		var posSum = new Vector3[total];
		var normalSum = new Vector3[total];
		var albedoSum = new Vector3[total];
		var areaSum = new float[total];

		// Iterate triangles, not voxels: most voxels are empty. Scatter is by AABB, conservative.
		var lockObj = new object();
		Parallel.For(0, _tris.Length, () => (Voxels: new Dictionary<int, (Vector3 P, Vector3 N, Vector3 A, float W)>(), Dummy: 0),
			(t, _, local) =>
		{
			ref var tri = ref _tris[t];
			var b = tri.A + tri.E1;
			var c = tri.A + tri.E2;
			var cross = Vector3.Cross(tri.E1, tri.E2);
			float area = cross.Length() * 0.5f;
			if (area <= 1e-12f)
			{
				return local;
			}

			var normal = cross / (area * 2f);
			var centroid = (tri.A + b + c) / 3f;
			var triMin = Vector3.Min(tri.A, Vector3.Min(b, c));
			var triMax = Vector3.Max(tri.A, Vector3.Max(b, c));

			var lo = (triMin - origin) / voxel;
			var hi = (triMax - origin) / voxel;
			int x0 = Math.Clamp((int)MathF.Floor(lo.X), 0, vx - 1), x1 = Math.Clamp((int)MathF.Floor(hi.X), 0, vx - 1);
			int y0 = Math.Clamp((int)MathF.Floor(lo.Y), 0, vy - 1), y1 = Math.Clamp((int)MathF.Floor(hi.Y), 0, vy - 1);
			int z0 = Math.Clamp((int)MathF.Floor(lo.Z), 0, vz - 1), z1 = Math.Clamp((int)MathF.Floor(hi.Z), 0, vz - 1);

			// Clamp the centroid into each voxel, else a large triangle drags the point outside.
			for (int z = z0; z <= z1; z++)
			for (int y = y0; y <= y1; y++)
			for (int x = x0; x <= x1; x++)
			{
				int v = (z * vy + y) * vx + x;
				var boxMin = origin + new Vector3(x * voxel.X, y * voxel.Y, z * voxel.Z);
				var point = Vector3.Clamp(centroid, boxMin, boxMin + voxel);
				var add = (point * area, normal * area, tri.Albedo * area, area);
				local.Voxels[v] = local.Voxels.TryGetValue(v, out var prev)
					? (prev.P + add.Item1, prev.N + add.Item2, prev.A + add.Item3, prev.W + area)
					: add;
			}

			return local;
		},
		local =>
		{
			lock (lockObj)
			{
				foreach (var (v, acc) in local.Voxels)
				{
					posSum[v] += acc.P;
					normalSum[v] += acc.N;
					albedoSum[v] += acc.A;
					areaSum[v] += acc.W;
				}
			}
		});

		int count = 0;
		for (int v = 0; v < total; v++)
		{
			dense[v] = areaSum[v] > 1e-12f ? count++ : -1;
		}

		cache.Allocate(dense, count);
		for (int v = 0; v < total; v++)
		{
			int slot = dense[v];
			if (slot < 0)
			{
				continue;
			}

			float inv = 1f / areaSum[v];
			cache.Position[slot] = posSum[v] * inv;
			var n = normalSum[v] * inv;
			float len = n.Length();
			cache.Normal[slot] = len > 1e-4f ? n / len : Vector3.UnitY;
			cache.Albedo[slot] = albedoSum[v] * inv;
		}

		return cache;
	}

	/// <summary>Builds the surface capture if requested and not yet built.</summary>
	public void EnsureSurfaceCache(ProbeGiBakeSession s)
	{
		// Realtime never uses the cache: its static geometry lies on a moving scene.
		// WantsSurfaceCache stays set so switching to a bake builds it on the next round.
		if (!s.WantsSurfaceCache || s.Realtime)
		{
			return;
		}

		s.WantsSurfaceCache = false;
		s.Surface = BuildSurfaceCache(s.Origin, s.Cell, s.CountX, s.CountY, s.CountZ);
	}

	// Punctual direct light at a surface point; attenuation mirrors UnlitInstancedPS so the bounce
	// matches shading. Lamps go into the static share of lighting, not the sun-modulated one.
	private Vector3 EvalPunctualLights(PunctualLight[] lights, Vector3 pos, Vector3 normal)
	{
		if (lights.Length == 0)
		{
			return Vector3.Zero;
		}

		var sum = Vector3.Zero;
		for (int i = 0; i < lights.Length; i++)
		{
			ref var l = ref lights[i];
			var lightPos = new Vector3(l.PositionRange.X, l.PositionRange.Y, l.PositionRange.Z);
			float range = l.PositionRange.W;
			var toLight = lightPos - pos;
			float distSq = toLight.LengthSquared();
			if (distSq > range * range)
			{
				continue;
			}

			float dist = MathF.Sqrt(MathF.Max(distSq, 1e-6f));
			var dir = toLight / dist;
			float ndotl = Vector3.Dot(normal, dir);
			if (ndotl <= 0f)
			{
				continue;
			}

			// Smooth falloff window, mirroring the clustered shading in UnlitInstancedPS.
			float distRatio = dist / range;
			float distRatio2 = distRatio * distRatio;
			float distFactor = Math.Clamp(1f - distRatio2 * distRatio2, 0f, 1f);
			float atten = distFactor * distFactor / (distSq + 1e-2f);

			if (l.DirectionType.W > 0.5f)
			{
				float cd = Vector3.Dot(-dir,
					new Vector3(l.DirectionType.X, l.DirectionType.Y, l.DirectionType.Z));
				float spotFactor = Math.Clamp((cd - l.SpotAngles.X) * l.SpotAngles.Y, 0f, 1f);
				atten *= spotFactor * spotFactor;
				if (atten <= 0f)
				{
					continue;
				}
			}

			float shadowStart = _sceneEpsilon * 4f;
			if (TraceAnyHit(pos + dir * shadowStart, dir, dist - shadowStart * 2f))
			{
				continue;
			}

			sum += new Vector3(l.ColorIntensity.X, l.ColorIntensity.Y, l.ColorIntensity.Z)
				* l.ColorIntensity.W * (ndotl * atten);
		}

		return sum;
	}

	// Sharp part (sun) is shadow-traced per voxel; smooth part (sky, re-bounce) comes from the
	// probe field. One ray per voxel per round keeps the cache in step with the probes.
	private void UpdateSurfaceCache(ProbeGiBakeSession s)
	{
		var cache = s.Surface;
		if (cache == null || cache.VoxelCount == 0)
		{
			return;
		}

		var sunDir = s.SunDirection;
		var sunColor = s.SunColor;
		var bakeLights = s.BakeLights;
		float feedback = s.Feedback;
		float bounceSaturation = s.BounceSaturation;
		var l0R = s.L0R; var l1xR = s.L1XR; var l1yR = s.L1YR; var l1zR = s.L1ZR;
		var validityR = s.ValidityR; var sunFracR = s.SunFracR;
		float offset = _sceneEpsilon * 4f;

		static float Lum(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

		Parallel.For(0, cache.VoxelCount, v =>
		{
			var normal = cache.Normal[v];
			var pos = cache.Position[v] + normal * offset;

			var sunIrradiance = Vector3.Zero;
			float ndotl = Vector3.Dot(normal, sunDir);
			if (ndotl > 0f && !TraceAnyHit(pos, sunDir, _rayTMax))
			{
				sunIrradiance = sunColor * ndotl;
			}

			// Lamps go into the static share; the realtime sun shadow modulates only the sun share.
			var lampIrradiance = EvalPunctualLights(bakeLights, pos, normal);

			var ambient = Vector3.Zero;
			float ambientFrac = 0f;
			if (feedback > 0f)
			{
				ambient = EvalIrradiance(s, l0R, l1xR, l1yR, l1zR, validityR, sunFracR,
					pos, normal, out ambientFrac) * feedback;
			}

			var irradiance = sunIrradiance + lampIrradiance + ambient;
			var albedo = Vector3.Lerp(new Vector3(Lum(cache.Albedo[v])), cache.Albedo[v], bounceSaturation);
			cache.Radiance[v] = albedo * irradiance * (1f / MathF.PI);

			float lumIrr = Lum(irradiance);
			cache.SunFraction[v] = lumIrr > 1e-6f
				? Math.Clamp((Lum(sunIrradiance) + Lum(ambient) * ambientFrac) / lumIrr, 0f, 1f)
				: 0f;
		});
	}

	// --- Bake -----------------------------------------------------------------------------------

	// Floor on the round weight: 1/(Round+1) would reach zero and freeze the field.
	internal const float MinRoundBlend = 0.02f;

	/// <summary>Realtime round weight floor, i.e. the exponential-average alpha; ~1.2 s to settle
	/// at 60 fps. Measured on Sponza: 0.15 flickers visibly, 0.02 responds too slowly.</summary>
	public const float RealtimeBlend = 0.04f;

	// Five, per Majercik 2021 5: longer windows let tangent backfaces oscillate the probe.
	internal const int RelocationRounds = 5;

	// Warm-up rounds excluded from the average: round one has an empty field and no multi-bounce.
	private const int BootstrapRounds = 3;

	// Convergence age restored on a lighting change; the old field is still a decent start.
	internal const int RestartRound = BootstrapRounds + 1;

	private const int MinAveragedRounds = 4;

	/// <summary>Synchronous bake to convergence; a session wrapper for headless paths.</summary>
	public ProbeGiBakeResult Bake(Vector3 boundsMin, Vector3 boundsMax, Vector3 sunDirection,
		Vector3 sunColor, float envYawRadians, Func<Vector3, Vector3> skyRadiance,
		ProbeGiBakeOptions? options = null, PunctualLight[]? punctualLights = null)
	{
		var session = BeginBake(boundsMin, boundsMax, sunDirection, sunColor, envYawRadians,
			skyRadiance, options, punctualLights);

		// Explicit condition, not Converged: realtime sessions never converge by definition.
		while (!session.NoGeometry && session.Round < session.TargetRounds)
		{
			RunRound(session);
		}

		return Snapshot(session);
	}

	/// <summary>Lays out the probe grid and accumulators; traces nothing, so it is main-thread safe.
	/// skyRadiance is linear sky radiance before envYaw; sunDirection points TOWARDS the sun.</summary>
	public ProbeGiBakeSession BeginBake(Vector3 boundsMin, Vector3 boundsMax, Vector3 sunDirection,
		Vector3 sunColor, float envYawRadians, Func<Vector3, Vector3> skyRadiance,
		ProbeGiBakeOptions? options = null, PunctualLight[]? punctualLights = null)
	{
		options ??= new ProbeGiBakeOptions();
		float density = Math.Clamp(options.GridDensity, 4f, 64f);
		int maxProbes = Math.Clamp(options.MaxProbes, MinProbeBudget, MaxProbeBudget);

		// Cell is ~1/density of the largest extent, capped by maxProbes, min 2 probes per axis.
		var size = Vector3.Max(boundsMax - boundsMin, new Vector3(1e-3f));
		float maxDim = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
		var margin = new Vector3(maxDim * 0.02f);
		var min = boundsMin - margin;
		var full = size + margin * 2f;

		// Grow the cell until the grid fits the budget; cost is just the product of the sides.
		float cellTarget = MathF.Max(maxDim, 1e-3f) / density;
		int cx, cy, cz;
		while (true)
		{
			cx = ProbesPerAxis(full.X, cellTarget);
			cy = ProbesPerAxis(full.Y, cellTarget);
			cz = ProbesPerAxis(full.Z, cellTarget);

			// The visibility atlas is VisRes times larger on both axes, so it hits the limit first.
			long probes = (long)cx * cy * cz;
			bool fitsBudget = probes <= maxProbes;
			bool fitsAtlas = (long)cx * ProbeGiBakeResult.VisRes <= MaxAtlasDimension
				&& (long)cz * cy * ProbeGiBakeResult.VisRes <= MaxAtlasDimension;
			if ((fitsBudget && fitsAtlas) || (cx <= 2 && cy <= 2 && cz <= 2))
			{
				break;
			}

			cellTarget *= 1.25f;
		}

		var cell = new Vector3(full.X / (cx - 1), full.Y / (cy - 1), full.Z / (cz - 1));

		// Enough averaged rounds to accumulate the requested RaysPerProbe, plus the warm-up.
		int averagedRounds = Math.Max(MinAveragedRounds,
			(int)MathF.Ceiling(Math.Clamp(options.RaysPerProbe, 16, 512)
				/ (float)Math.Clamp(options.RaysPerRound, 4, 128)));

		var session = new ProbeGiBakeSession(min, cell, cx, cy, cz, options,
			Vector3.Normalize(sunDirection), sunColor, envYawRadians, skyRadiance,
			BootstrapRounds + averagedRounds);

		// Surface capture is deferred to the first round: BeginBake runs on the main thread.
		session.WantsSurfaceCache = options.SurfaceCache;
		if (punctualLights is { Length: > 0 })
		{
			session.BakeLights = punctualLights;
		}

		return session;
	}

	// Probes per axis: one more than cells. Minimum two, or there is nothing to interpolate.
	private static int ProbesPerAxis(float extent, float cellTarget) =>
		Math.Clamp((int)MathF.Ceiling(extent / cellTarget) + 1, 2, MaxProbesPerAxis);

	// Normals are area-weighted, else a scatter of decor triangles outvotes the floor slab.
	private (bool HasGeometry, float Coherence) InspectBox(Vector3 boxMin, Vector3 boxMax)
	{
		if (_nodeCount == 0)
		{
			return (false, 0f);
		}

		var normalSum = Vector3.Zero;
		float areaSum = 0f;

		Span<int> stack = stackalloc int[64];
		int sp = 0;
		stack[sp++] = 0;

		while (sp > 0)
		{
			ref var node = ref _nodes[stack[--sp]];
			if (node.Min.X > boxMax.X || node.Max.X < boxMin.X ||
				node.Min.Y > boxMax.Y || node.Max.Y < boxMin.Y ||
				node.Min.Z > boxMax.Z || node.Max.Z < boxMin.Z)
			{
				continue;
			}

			if (node.Left < 0)
			{
				for (int i = node.Start; i < node.Start + node.Count; i++)
				{
					ref var tri = ref _tris[_order[i]];
					var b = tri.A + tri.E1;
					var c = tri.A + tri.E2;
					var triMin = Vector3.Min(tri.A, Vector3.Min(b, c));
					var triMax = Vector3.Max(tri.A, Vector3.Max(b, c));
					if (triMin.X > boxMax.X || triMax.X < boxMin.X ||
						triMin.Y > boxMax.Y || triMax.Y < boxMin.Y ||
						triMin.Z > boxMax.Z || triMax.Z < boxMin.Z)
					{
						continue;
					}

					// Edge cross product carries direction and twice the area as its length.
					var cross = Vector3.Cross(tri.E1, tri.E2);
					normalSum += cross;
					areaSum += cross.Length();
				}
			}
			else if (sp + 2 <= stack.Length)
			{
				stack[sp++] = node.Left;
				stack[sp++] = node.Start;
			}
		}

		return areaSum > 1e-12f ? (true, normalSum.Length() / areaSum) : (false, 0f);
	}

	/// <summary>One progressive bake round; heavy, run it in the background. Rounds must not
	/// overlap, though a round parallelises internally over probes.</summary>
	public void RunRound(ProbeGiBakeSession s)
	{
		if (!HasGeometry)
		{
			s.NoGeometry = true;
			s.Round = s.TargetRounds;
			return;
		}

		// Realtime skips the surface cache entirely, mirroring the GPU round in ProbeRoundCS.hlsl.
		SurfaceCache? surface = null;
		if (!s.Realtime)
		{
			EnsureSurfaceCache(s);

			// Must update before this round's rays so cache and field converge in step.
			UpdateSurfaceCache(s);
			surface = s.Surface;
		}

		int rays = s.RaysPerRound;
		var dirs = BuildRotatedFibonacciSphere(rays, s.Sequence++);

		// Warm-up rounds land at alpha = 1; after that it is a floored running average.
		float alpha = RoundBlendWeight(s);

		int cx = s.CountX, cy = s.CountY, cz = s.CountZ;
		int probeCount = s.ProbeCount;
		var origin = s.Origin;
		var cell = s.Cell;
		var sunDir = s.SunDirection;
		var sunColor = s.SunColor;
		var bakeLights = s.BakeLights;
		float bounceSaturation = s.BounceSaturation;
		float feedback = s.Feedback;
		float maxRayLuminance = s.MaxRayLuminance;
		float maxStep = s.MaxStep;
		float accumGamma = s.AccumulationGamma;
		float relocLimit = s.RelocationLimit;
		var probeOffsets = s.ProbeOffset;
		float visMax = cell.Length() * 1.5f;
		float gatherOffset = cell.Length() * 0.05f;

		// Environment yaw: the shader shifts equirect U by +yaw, so direction d sees azimuth phi+yaw.
		var skyRadiance = s.SkyRadiance;
		float skyIntensity = s.SkyIntensity;
		float cosYaw = MathF.Cos(s.EnvYaw), sinYaw = MathF.Sin(s.EnvYaw);
		Vector3 RotatedSky(Vector3 d) => skyRadiance(new Vector3(
			d.X * cosYaw - d.Z * sinYaw, d.Y, d.X * sinYaw + d.Z * cosYaw)) * skyIntensity;

		const float y00 = 0.28209479f;
		const float y1 = 0.48860251f;
		int res = ProbeGiBakeResult.VisRes;
		float domega = 4f * MathF.PI / rays;

		static float Lum(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

		// A round reads the previous field and writes the new one; see the double buffer in the session.
		var l0R = s.L0R; var l1xR = s.L1XR; var l1yR = s.L1YR; var l1zR = s.L1ZR;
		var validityR = s.ValidityR; var sunFracR = s.SunFracR;
		var l0W = s.L0W; var l1xW = s.L1XW; var l1yW = s.L1YW; var l1zW = s.L1ZW;
		var validityW = s.ValidityW; var sunFracW = s.SunFracW;

		int gridX = s.CountX, gridY = s.CountY, gridZ = s.CountZ;

		// Iterated by storage index; the volume is static, so storage coords are grid coords.
		Parallel.For(0, probeCount, p =>
		{
			int px = p % gridX;
			int py = p / gridX % gridY;
			int pz = p / (gridX * gridY);

			// Trace from the relocated position, or backface stats would describe the grid node.
			var probeOffset = probeOffsets[p];
			var probePos = origin + new Vector3(px * cell.X, py * cell.Y, pz * cell.Z) + probeOffset;

			float probeVisMax = visMax;

			var sum0 = Vector3.Zero;
			var sumX = Vector3.Zero;
			var sumY = Vector3.Zero;
			var sumZ = Vector3.Zero;
			float sunLum = 0f, totalLum = 0f;
			int missCount = 0, backCount = 0;
			int visBase = p * res * res;

			// For relocation: nearest backface is the nearest way out, nearest front measures room.
			float closestBackT = _rayTMax, closestFrontT = _rayTMax;
			var closestBackDir = Vector3.UnitY;

			for (int r = 0; r < rays; r++)
			{
				var dir = dirs[r];
				Vector3 radiance;
				float sunShare = 0f;
				float hitT;

				if (!TraceClosest(probePos, dir, out float t, out int triIndex))
				{
					radiance = RotatedSky(dir);
					missCount++;
					hitT = _rayTMax;
				}
				else
				{
					hitT = t;
					ref var tri = ref _tris[triIndex];
					var normal = Vector3.Normalize(Vector3.Cross(tri.E1, tri.E2));
					if (Vector3.Dot(normal, dir) > 0f)
					{
						// Backface = the ray left from inside geometry, i.e. the probe is in a wall.
						radiance = Vector3.Zero;
						backCount++;

						// Order matters: relocation needs the full distance, shortening is for depth.
						if (t < closestBackT)
						{
							closestBackT = t;
							closestBackDir = dir;
						}

						// Backface depth shortened 80% (Majercik 2021 4.1) so Chebyshev occludes it.
						hitT = t * 0.2f;
					}
					else
					{
						closestFrontT = MathF.Min(closestFrontT, t);
						var hitPos = probePos + dir * t;

						// The hit point already has outgoing radiance at voxel resolution.
						int voxel = surface?.Lookup(hitPos + normal * gatherOffset) ?? -1;
						if (voxel >= 0)
						{
							radiance = surface!.Radiance[voxel];
							sunShare = surface.SunFraction[voxel];
						}
						else
						{
							var sunIrradiance = Vector3.Zero;
							float ndotl = Vector3.Dot(normal, sunDir);
							if (ndotl > 0f &&
								!TraceAnyHit(hitPos + normal * (_sceneEpsilon * 4f), sunDir, _rayTMax))
							{
								sunIrradiance = sunColor * ndotl;
							}

							var lampIrradiance = EvalPunctualLights(bakeLights,
								hitPos + normal * (_sceneEpsilon * 4f), normal);

							var prevIrradiance = Vector3.Zero;
							float prevFrac = 0f;
							if (feedback > 0f)
							{
								prevIrradiance = EvalIrradiance(s, l0R, l1xR, l1yR, l1zR, validityR,
									sunFracR, hitPos + normal * gatherOffset, normal, out prevFrac) * feedback;
							}

							var irradiance = sunIrradiance + lampIrradiance + prevIrradiance;

							// Chroma clamp towards luma; lerp is linear so brightness is preserved.
							var albedo = Vector3.Lerp(new Vector3(Lum(tri.Albedo)), tri.Albedo, bounceSaturation);
							radiance = albedo * irradiance * (1f / MathF.PI);

							float lumIrr = Lum(irradiance);
							sunShare = lumIrr > 1e-6f
								? (Lum(sunIrradiance) + Lum(prevIrradiance) * prevFrac) / lumIrr
								: 0f;
						}
					}
				}

				// Firefly suppression, mirroring ProbeRoundCS.hlsl. Disabled during bakes.
				if (maxRayLuminance > 0f)
				{
					float rayLum = Lum(radiance);
					if (rayLum > maxRayLuminance)
					{
						// Scale, not per-channel clip: clipping would shift hue.
						radiance *= maxRayLuminance / rayLum;
					}
				}

				// DDGI octahedral depth. Clamped to cell scale, else misses make visibility huge
				// and Chebyshev never occludes. Splatted over a texel cone (Majercik 2019 4.4).
				float tv = MathF.Min(hitT, probeVisMax);
				for (int dt = 0; dt < res * res; dt++)
				{
					var texelUv = new Vector2((dt % res + 0.5f) / res, (dt / res + 0.5f) / res);
					float w = MathF.Max(0f, Vector3.Dot(OctDecode(texelUv), dir));
					for (int sq = 0; sq < DepthSharpnessSquarings; sq++)
					{
						w *= w;
					}

					if (w < DepthWeightEpsilon)
					{
						continue;
					}

					int visAt = visBase + dt;
					s.VisSumT[visAt] += tv * w;
					s.VisSumT2[visAt] += tv * tv * w;
					s.VisWeight[visAt] += w;
				}

				float lum = Lum(radiance);
				sunLum += lum * sunShare;
				totalLum += lum;

				sum0 += radiance * (y00 * domega);
				sumX += radiance * (y1 * dir.X * domega);
				sumY += radiance * (y1 * dir.Y * domega);
				sumZ += radiance * (y1 * dir.Z * domega);
			}

			var new0 = Vector3.Lerp(l0R[p], sum0, alpha);
			var new1 = Vector3.Lerp(l1xR[p], sumX, alpha);
			var new2 = Vector3.Lerp(l1yR[p], sumY, alpha);
			var new3 = Vector3.Lerp(l1zR[p], sumZ, alpha);

			// Perceptual accumulation: luma moves on a gamma curve, directionality untouched.
			if (accumGamma > 1f && alpha < 1f)
			{
				float lumOld = Lum(l0R[p]);
				float lumNew = Lum(sum0);
				float lumLinear = Lum(new0);

				// Darkening only: a symmetric curve throttled brightening out of darkness.
				if (lumNew < lumOld && lumLinear > 1e-6f)
				{
					float invGamma = 1f / accumGamma;
					float lumPerceptual = MathF.Pow(
						MathF.Pow(MathF.Max(lumOld, 0f), invGamma) * (1f - alpha)
							+ MathF.Pow(MathF.Max(lumNew, 0f), invGamma) * alpha,
						accumGamma);
					float k = lumPerceptual / lumLinear;
					new0 *= k;
					new1 *= k;
					new2 *= k;
					new3 *= k;
				}
			}

			// Slew limiter: clamps the derivative, not the value, so the steady state is unbiased.
			if (maxStep > 0f && alpha < 1f)
			{
				var delta = new0 - l0R[p];
				float deltaLen = delta.Length();
				float scale = 0.5f * (l0R[p].Length() + new0.Length()) + 1e-4f;
				float limit = maxStep * scale;
				if (deltaLen > limit)
				{
					// One factor for all SH bands: scaling separately would rotate the field.
					float k = limit / deltaLen;
					new0 = l0R[p] + (new0 - l0R[p]) * k;
					new1 = l1xR[p] + (new1 - l1xR[p]) * k;
					new2 = l1yR[p] + (new2 - l1yR[p]) * k;
					new3 = l1zR[p] + (new3 - l1zR[p]) * k;
				}
			}

			l0W[p] = new0;
			l1xW[p] = new1;
			l1yW[p] = new2;
			l1zW[p] = new3;

			// Relocation: a probe inside a wall is pushed out through the nearest backface.
			bool relocated = false;
			if (relocLimit > 0f)
			{
				float backFrac = backCount / (float)rays;
				var newOffset = probeOffset;
				float offsetLen = probeOffset.Length();

				if (backFrac > 0.25f && closestBackT < _rayTMax)
				{
					newOffset = probeOffset + closestBackDir * (closestBackT + gatherOffset);
				}
				// No pull back to the node: on thin geometry it oscillated the probe every round.

				float newLen = newOffset.Length();
				if (newLen > relocLimit)
				{
					newOffset *= relocLimit / newLen;
				}

				// Thresholded, not any motion: resetting on every small step means a permanent cold start.
				relocated = (newOffset - probeOffset).Length() > relocLimit * 0.1f;
				probeOffsets[p] = newOffset;
			}

			float roundSunFrac = totalLum > 1e-6f ? Math.Clamp(sunLum / totalLum, 0f, 1f) : 0f;
			sunFracW[p] = sunFracR[p] + (roundSunFrac - sunFracR[p]) * alpha;

			// Sky visibility and validity are pure geometry, so they accumulate over all rounds.
			int rayTotal = s.RayTotal[p] + rays;
			int missTotal = s.MissTotal[p] + missCount;
			int backTotal = s.BackTotal[p] + backCount;
			s.RayTotal[p] = rayTotal;
			s.MissTotal[p] = missTotal;
			s.BackTotal[p] = backTotal;
			s.SkyVis[p] = missTotal / (float)rayTotal;

			// A probe in a wall mostly sees backfaces; damp its interpolation weight.
			validityW[p] = Math.Clamp(1f - backTotal / (float)rayTotal * 3f, 0f, 1f);

			// Reset a relocated probe's geometry only after this round's counters are accumulated,
			// else the stale backface stats would keep it marked as walled in.
			if (relocated)
			{
				s.RayTotal[p] = 0;
				s.MissTotal[p] = 0;
				s.BackTotal[p] = 0;
				int visReset = p * res * res;
				for (int i = 0; i < res * res; i++)
				{
					s.VisSumT[visReset + i] = 0f;
					s.VisSumT2[visReset + i] = 0f;
					s.VisWeight[visReset + i] = 0f;
				}
			}
		});

		s.Swap();
		s.Round++;
		s.ConsumeRelocationRound();
	}

	/// <summary>Packs the current session state into atlases. Result buffers are reused between
	/// snapshots, so call only between rounds and consume before the next RunRound.</summary>
	public ProbeGiBakeResult Snapshot(ProbeGiBakeSession s)
	{
		int res = ProbeGiBakeResult.VisRes;
		var result = s.Result;
		int shWidth = result.ShWidth;
		int visWidth = shWidth * res;

		Parallel.For(0, s.ProbeCount, p =>
		{
			var (px, py) = ProbeTexel(p, shWidth);
			int texel = (py * shWidth + px) * 8;
			WriteHalf4(result.Sh0, texel, s.L0R[p], s.SkyVis[p]);
			WriteHalf4(result.Sh1, texel, s.L1XR[p], s.ValidityR[p]);
			WriteHalf4(result.Sh2, texel, s.L1YR[p], s.SunFracR[p]);
			WriteHalf4(result.Sh3, texel, s.L1ZR[p], 1f);
			WriteHalf4(result.Offset, texel, s.ProbeOffset[p], 1f);

			// Probe-wide mean fills octants that never received a ray.
			int visBase = p * res * res;
			float totalT = 0f;
			float totalWeight = 0f;
			for (int i = 0; i < res * res; i++)
			{
				totalT += s.VisSumT[visBase + i];
				totalWeight += s.VisWeight[visBase + i];
			}

			float meanAll = totalWeight > 0f ? totalT / totalWeight : 0f;

			// Probe visibility block: res x res texels starting at (px*res, py*res).
			for (int ty = 0; ty < res; ty++)
			{
				for (int tx = 0; tx < res; tx++)
				{
					int src = visBase + ty * res + tx;
					float weight = s.VisWeight[src];
					float mean = weight > 0f ? s.VisSumT[src] / weight : meanAll;
					float mean2 = weight > 0f ? s.VisSumT2[src] / weight : meanAll * meanAll;
					int dst = ((py * res + ty) * visWidth + px * res + tx) * 8;
					WriteHalf4(result.Vis, dst, new Vector3(mean, mean2, 0f), 0f);
				}
			}
		});

		return result;
	}

	// Feedback factor matching N-bounce energy: f = (1-r^(N-1))/(1-r^N) at assumed albedo r=0.5.
	internal static float BounceFeedback(int bounces)
	{
		if (bounces <= 1)
		{
			return 0f;
		}

		const float r = 0.5f;
		float rn = MathF.Pow(r, bounces);
		return (1f - rn / r) / (1f - rn);
	}

	// Depth splat lobe: cos^64 via six squarings. Must match ProbeRoundCS.hlsl.
	private const int DepthSharpnessSquarings = 6;
	private const float DepthWeightEpsilon = 0.001f;

	// Mirror of ProbeOctDecode in ProbeRoundCS.hlsl.
	private static Vector3 OctDecode(Vector2 uv)
	{
		var p = uv * 2f - Vector2.One;
		var d = new Vector3(p.X, p.Y, 1f - MathF.Abs(p.X) - MathF.Abs(p.Y));
		if (d.Z < 0f)
		{
			d = new Vector3(
				(1f - MathF.Abs(d.Y)) * (d.X >= 0f ? 1f : -1f),
				(1f - MathF.Abs(d.X)) * (d.Y >= 0f ? 1f : -1f),
				d.Z);
		}

		return Vector3.Normalize(d);
	}

	// Must match OctEncode in UnlitInstancedPS.hlsl bit for bit.
	private static Vector2 OctEncode(Vector3 d)
	{
		float sum = MathF.Abs(d.X) + MathF.Abs(d.Y) + MathF.Abs(d.Z);
		float px = d.X / sum, py = d.Y / sum;
		if (d.Z < 0f)
		{
			(px, py) = ((1f - MathF.Abs(py)) * (px >= 0f ? 1f : -1f),
						(1f - MathF.Abs(px)) * (py >= 0f ? 1f : -1f));
		}

		return new Vector2(px * 0.5f + 0.5f, py * 0.5f + 0.5f);
	}

	// CPU twin of the shader's SampleProbeGi: validity-weighted trilinear, then SH L1 irradiance.
	private static Vector3 EvalIrradiance(ProbeGiBakeSession s,
		Vector3[] l0, Vector3[] l1x, Vector3[] l1y, Vector3[] l1z, float[] validity, float[] sunFrac,
		Vector3 pos, Vector3 normal, out float fracOut)
	{
		fracOut = 0f;

		var origin = s.Origin;
		var cell = s.Cell;
		var f = (pos - origin) / cell;
		f = Vector3.Clamp(f, Vector3.Zero,
			new Vector3(s.CountX - 1, s.CountY - 1, s.CountZ - 1));

		int lx = Math.Clamp((int)MathF.Floor(f.X), 0, s.CountX - 2);
		int ly = Math.Clamp((int)MathF.Floor(f.Y), 0, s.CountY - 2);
		int lz = Math.Clamp((int)MathF.Floor(f.Z), 0, s.CountZ - 2);
		var t = Vector3.Clamp(f - new Vector3(lx, ly, lz), Vector3.Zero, Vector3.One);

		var sh0 = Vector3.Zero;
		var shX = Vector3.Zero;
		var shY = Vector3.Zero;
		var shZ = Vector3.Zero;
		float fracSum = 0f;
		float weightSum = 0f;

		for (int corner = 0; corner < 8; corner++)
		{
			int ox = corner & 1, oy = (corner >> 1) & 1, oz = (corner >> 2) & 1;
			int index = StorageIndex(lx + ox, ly + oy, lz + oz, s.CountX, s.CountY);
			float w = (ox == 1 ? t.X : 1f - t.X) * (oy == 1 ? t.Y : 1f - t.Y) * (oz == 1 ? t.Z : 1f - t.Z)
				* validity[index];

			// Soft backface weight (wrap shading, as in SampleProbeGi): without it multi-bounce
			// drags light through walls.
			var probePos = origin + new Vector3(lx + ox, ly + oy, lz + oz) * cell;
			var toProbe = probePos - pos;
			float toProbeLen = toProbe.Length();
			float wrap = (Vector3.Dot(toProbe / MathF.Max(toProbeLen, 1e-4f), normal) + 1f) * 0.5f;
			w *= wrap * wrap + 0.05f;

			sh0 += l0[index] * w;
			shX += l1x[index] * w;
			shY += l1y[index] * w;
			shZ += l1z[index] * w;
			fracSum += sunFrac[index] * w;
			weightSum += w;
		}

		if (weightSum < 1e-4f)
		{
			return Vector3.Zero;
		}

		float inv = 1f / weightSum;
		fracOut = Math.Clamp(fracSum * inv, 0f, 1f);
		var e = (sh0 * inv) * 0.8862269f
			+ ((shX * inv) * normal.X + (shY * inv) * normal.Y + (shZ * inv) * normal.Z) * 1.0233267f;
		return Vector3.Max(e, Vector3.Zero);
	}

	private static void WriteHalf4(byte[] bytes, int offset, Vector3 rgb, float a)
	{
		WriteHalf(bytes, offset + 0, rgb.X);
		WriteHalf(bytes, offset + 2, rgb.Y);
		WriteHalf(bytes, offset + 4, rgb.Z);
		WriteHalf(bytes, offset + 6, a);
	}

	private static void WriteHalf(byte[] bytes, int offset, float value)
	{
		ushort bits = BitConverter.HalfToUInt16Bits((Half)value);
		bytes[offset] = (byte)bits;
		bytes[offset + 1] = (byte)(bits >> 8);
	}

	// Fibonacci fan rotated per round number; progressive accumulation depends on this rotation.
	private static Vector3[] BuildRotatedFibonacciSphere(int count, int sequence)
	{
		var dirs = BuildFibonacciSphere(count);
		if (sequence == 0)
		{
			return dirs;
		}

		// Shoemake uniform orientation from a low-discrepancy triple; fewer blotches than a PRNG.
		float u1 = Frac(sequence * 0.7548776662f);
		float u2 = Frac(sequence * 0.5698402909f);
		float u3 = Frac(sequence * 0.6180339887f);
		float r1 = MathF.Sqrt(1f - u1), r2 = MathF.Sqrt(u1);
		var rotation = new Quaternion(
			r1 * MathF.Sin(2f * MathF.PI * u2), r1 * MathF.Cos(2f * MathF.PI * u2),
			r2 * MathF.Sin(2f * MathF.PI * u3), r2 * MathF.Cos(2f * MathF.PI * u3));

		for (int i = 0; i < count; i++)
		{
			dirs[i] = Vector3.Transform(dirs[i], rotation);
		}

		return dirs;

		static float Frac(float v) => v - MathF.Floor(v);
	}

	private static Vector3[] BuildFibonacciSphere(int count)
	{
		var dirs = new Vector3[count];
		float golden = MathF.PI * (3f - MathF.Sqrt(5f));
		for (int i = 0; i < count; i++)
		{
			float y = 1f - (i + 0.5f) * 2f / count;
			float radius = MathF.Sqrt(MathF.Max(1f - y * y, 0f));
			float phi = golden * i;
			dirs[i] = new Vector3(MathF.Cos(phi) * radius, y, MathF.Sin(phi) * radius);
		}

		return dirs;
	}
}
