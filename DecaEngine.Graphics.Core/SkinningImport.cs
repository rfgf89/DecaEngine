using System;
using System.Collections.Generic;
using System.Numerics;
using SharpGLTF.Schema2;
using DecaEngine.Animation;
using DecaEngine.Core;

namespace DecaEngine.Graphics;

/// <summary>Parses glTF skeletons, skin weights and animation clips.</summary>
/// <remarks>SharpGLTF is not thread safe: all of this must run on the thread reading the document.</remarks>
public static class SkinningImport
{
	/// <summary>Conjugates a matrix by a Z reflection: M' = S * M * S, S = diag(1,1,-1,1),
	/// the right- to left-handed conversion applied to a full transform.</summary>
	public static Matrix4x4 MirrorZ(Matrix4x4 m)
	{
		m.M13 = -m.M13;
		m.M23 = -m.M23;
		m.M43 = -m.M43;

		m.M31 = -m.M31;
		m.M32 = -m.M32;
		m.M34 = -m.M34;

		return m;
	}

	/// <summary>Same conversion for a rotation: a Z reflection conjugates it to (-x,-y,z,w).</summary>
	public static Quaternion MirrorZ(Quaternion q) => new(-q.X, -q.Y, q.Z, q.W);

	public static Vector3 MirrorZ(Vector3 v) => new(v.X, v.Y, -v.Z);

	/// <summary>Builds one skeleton for the whole model: every skin's joints plus their ancestors
	/// up to the scene root. Returns null when the document has no skins.</summary>
	public static PreparedSkeleton BuildSkeleton(ModelRoot model, out Dictionary<int, int> nodeToJoint)
	{
		nodeToJoint = new Dictionary<int, int>();

		var wanted = new HashSet<Node>();
		foreach (var skin in model.LogicalSkins)
		{
			for (int i = 0; i < skin.JointsCount; i++)
			{
				for (var node = skin.GetJoint(i).Joint; node != null; node = node.VisualParent)
				{
					// Ancestor chain already collected; walking higher would be O(joints * depth).
					if (!wanted.Add(node))
					{
						break;
					}
				}
			}
		}

		if (wanted.Count == 0)
		{
			return null;
		}

		// Sort by depth for the parent-before-child contract; LogicalIndex keeps it deterministic,
		// since the .dmdl cache and IK/ragdoll settings store bone indices.
		var ordered = new List<Node>(wanted);
		var depths = new Dictionary<Node, int>();
		ordered.Sort((a, b) =>
		{
			int da = Depth(a, depths);
			int db = Depth(b, depths);
			return da != db ? da.CompareTo(db) : a.LogicalIndex.CompareTo(b.LogicalIndex);
		});

		var skeleton = new PreparedSkeleton
		{
			JointNames = new string[ordered.Count],
			Parents = new int[ordered.Count],
			BindLocals = new Transform[ordered.Count],
			InverseBind = new Matrix4x4[ordered.Count],
		};

		for (int i = 0; i < ordered.Count; i++)
		{
			nodeToJoint[ordered[i].LogicalIndex] = i;
		}

		for (int i = 0; i < ordered.Count; i++)
		{
			var node = ordered[i];
			var parent = node.VisualParent;

			skeleton.JointNames[i] = node.Name ?? $"Joint_{node.LogicalIndex}";
			skeleton.Parents[i] = parent != null && nodeToJoint.TryGetValue(parent.LogicalIndex, out int p) ? p : -1;

			// GetDecomposed is required: matrix-defined nodes throw on a direct Rotation read.
			var local = node.LocalTransform.GetDecomposed();
			skeleton.BindLocals[i] = new Transform
			{
				position = MirrorZ(local.Translation),
				rotation = MirrorZ(local.Rotation),
				scale = local.Scale,
			};
		}

		FillInverseBindMatrices(model, skeleton, nodeToJoint);
		return skeleton;
	}

	private static int Depth(Node node, Dictionary<Node, int> cache)
	{
		if (cache.TryGetValue(node, out int depth))
		{
			return depth;
		}

		depth = node.VisualParent == null ? 0 : Depth(node.VisualParent, cache) + 1;
		cache[node] = depth;
		return depth;
	}

	// Authored inverse binds win over ones derived from the bind pose: the two can differ.
	private static void FillInverseBindMatrices(ModelRoot model, PreparedSkeleton skeleton,
		Dictionary<int, int> nodeToJoint)
	{
		var authored = new bool[skeleton.JointCount];

		foreach (var skin in model.LogicalSkins)
		{
			for (int i = 0; i < skin.JointsCount; i++)
			{
				var (joint, inverseBind) = skin.GetJoint(i);
				if (!nodeToJoint.TryGetValue(joint.LogicalIndex, out int jointIndex) || authored[jointIndex])
				{
					continue;
				}

				skeleton.InverseBind[jointIndex] = MirrorZ(inverseBind);
				authored[jointIndex] = true;
			}
		}

		// Single pass: topological order guarantees the parent is done before the child.
		var bindModel = new Matrix4x4[skeleton.JointCount];
		for (int i = 0; i < skeleton.JointCount; i++)
		{
			var t = skeleton.BindLocals[i];
			var local = Matrix4x4.CreateScale(t.scale)
				* Matrix4x4.CreateFromQuaternion(t.rotation)
				* Matrix4x4.CreateTranslation(t.position);

			int parent = skeleton.Parents[i];
			bindModel[i] = parent < 0 ? local : local * bindModel[parent];

			if (!authored[i])
			{
				skeleton.InverseBind[i] = Matrix4x4.Invert(bindModel[i], out var inverted)
					? inverted
					: Matrix4x4.Identity;
			}
		}
	}

	/// <summary>Skin stream of a primitive, remapped to skeleton joint indices; null when unskinned.
	/// JOINTS_1/WEIGHTS_1 are folded down to the four heaviest influences and renormalized.</summary>
	public static SkinVertex[] ReadSkinVertices(MeshPrimitive primitive, Skin skin,
		Dictionary<int, int> nodeToJoint, int vertexCount)
	{
		var jointsAccessor = primitive.GetVertexAccessor("JOINTS_0");
		var weightsAccessor = primitive.GetVertexAccessor("WEIGHTS_0");

		if (skin == null || jointsAccessor == null || weightsAccessor == null)
		{
			return null;
		}

		// Per-skin table: the same local index means a different bone in another skin.
		var skinToSkeleton = new int[skin.JointsCount];
		for (int i = 0; i < skin.JointsCount; i++)
		{
			skinToSkeleton[i] = nodeToJoint.TryGetValue(skin.GetJoint(i).Joint.LogicalIndex, out int j) ? j : 0;
		}

		var joints0 = jointsAccessor.AsVector4Array();
		var weights0 = weightsAccessor.AsVector4Array();

		var joints1 = primitive.GetVertexAccessor("JOINTS_1")?.AsVector4Array();
		var weights1 = primitive.GetVertexAccessor("WEIGHTS_1")?.AsVector4Array();

		var result = new SkinVertex[vertexCount];
		Span<int> bestJoint = stackalloc int[SkinVertex.MaxInfluences];
		Span<float> bestWeight = stackalloc float[SkinVertex.MaxInfluences];

		for (int v = 0; v < vertexCount; v++)
		{
			bestJoint.Clear();
			bestWeight.Clear();

			if (v < joints0.Count)
			{
				AccumulateInfluences(joints0[v], weights0[v], skinToSkeleton, bestJoint, bestWeight);
			}

			if (joints1 != null && weights1 != null && v < joints1.Count)
			{
				AccumulateInfluences(joints1[v], weights1[v], skinToSkeleton, bestJoint, bestWeight);
			}

			result[v] = PackInfluences(bestJoint, bestWeight);
		}

		return result;
	}

	// Zero weights are skipped: glTF leaves garbage joint indices in unused slots.
	private static void AccumulateInfluences(Vector4 joints, Vector4 weights, int[] skinToSkeleton,
		Span<int> bestJoint, Span<float> bestWeight)
	{
		for (int c = 0; c < 4; c++)
		{
			float weight = c switch { 0 => weights.X, 1 => weights.Y, 2 => weights.Z, _ => weights.W };
			if (weight <= 0f)
			{
				continue;
			}

			int local = (int)(c switch { 0 => joints.X, 1 => joints.Y, 2 => joints.Z, _ => joints.W });
			int joint = (uint)local < (uint)skinToSkeleton.Length ? skinToSkeleton[local] : 0;

			int weakest = 0;
			for (int i = 1; i < bestWeight.Length; i++)
			{
				if (bestWeight[i] < bestWeight[weakest])
				{
					weakest = i;
				}
			}

			if (weight > bestWeight[weakest])
			{
				bestWeight[weakest] = weight;
				bestJoint[weakest] = joint;
			}
		}
	}

	// Rounding remainder goes to the heaviest slot so weights sum to exactly WeightScale;
	// an influence-less vertex is pinned to joint 0 instead of collapsing to the origin.
	private static SkinVertex PackInfluences(Span<int> joints, Span<float> weights)
	{
		float sum = 0f;
		for (int i = 0; i < weights.Length; i++)
		{
			sum += weights[i];
		}

		if (sum <= 0f)
		{
			return new SkinVertex { J0 = 0, W0 = (ushort)SkinVertex.WeightScale };
		}

		float inverseSum = SkinVertex.WeightScale / sum;
		Span<int> packed = stackalloc int[SkinVertex.MaxInfluences];

		int total = 0;
		int heaviest = 0;
		for (int i = 0; i < weights.Length; i++)
		{
			packed[i] = (int)MathF.Round(weights[i] * inverseSum);
			total += packed[i];

			if (weights[i] > weights[heaviest])
			{
				heaviest = i;
			}
		}

		packed[heaviest] += (int)SkinVertex.WeightScale - total;
		packed[heaviest] = Math.Clamp(packed[heaviest], 0, (int)SkinVertex.WeightScale);

		return new SkinVertex
		{
			J0 = (ushort)joints[0], J1 = (ushort)joints[1], J2 = (ushort)joints[2], J3 = (ushort)joints[3],
			W0 = (ushort)packed[0], W1 = (ushort)packed[1], W2 = (ushort)packed[2], W3 = (ushort)packed[3],
		};
	}

	/// <summary>Document clips split per skeleton joint; keys stay raw and CUBICSPLINE channels are
	/// read as linear over the node values, dropping tangents.</summary>
	public static List<PreparedAnimation> BuildAnimations(ModelRoot model, PreparedSkeleton skeleton,
		Dictionary<int, int> nodeToJoint)
	{
		var animations = new List<PreparedAnimation>();
		if (skeleton == null)
		{
			return animations;
		}

		foreach (var source in model.LogicalAnimations)
		{
			var clip = new PreparedAnimation
			{
				Name = source.Name ?? $"Animation_{source.LogicalIndex}",
				Duration = source.Duration,
				Tracks = new JointTrack[skeleton.JointCount],
			};

			for (int i = 0; i < clip.Tracks.Length; i++)
			{
				clip.Tracks[i] = new JointTrack();
			}

			bool any = false;
			foreach (var channel in source.Channels)
			{
				if (channel.TargetNode == null ||
					!nodeToJoint.TryGetValue(channel.TargetNode.LogicalIndex, out int joint))
				{
					// Channel targets a node outside the skeleton (camera, light, prop).
					continue;
				}

				var track = clip.Tracks[joint];
				switch (channel.TargetNodePath)
				{
					case PropertyPath.translation:
						(track.TranslationTimes, track.Translations) =
							ReadKeys(channel.GetTranslationSampler(), MirrorZ);
						any |= track.TranslationTimes.Length > 0;
						break;

					case PropertyPath.rotation:
						(track.RotationTimes, track.Rotations) =
							ReadKeys(channel.GetRotationSampler(), MirrorZ);
						any |= track.RotationTimes.Length > 0;
						break;

					case PropertyPath.scale:
						(track.ScaleTimes, track.Scales) = ReadKeys(channel.GetScaleSampler(), s => s);
						any |= track.ScaleTimes.Length > 0;
						break;
				}
			}

			// Clips that touch no joint (morphs, light animation) would be dead entries in the UI.
			if (any)
			{
				animations.Add(clip);
			}
		}

		return animations;
	}

	private static (float[] Times, T[] Values) ReadKeys<T>(IAnimationSampler<T> sampler, Func<T, T> convert)
		where T : struct
	{
		if (sampler == null)
		{
			return ([], []);
		}

		var keys = sampler.InterpolationMode == AnimationInterpolationMode.CUBICSPLINE
			? CubicValues(sampler)
			: sampler.GetLinearKeys();

		var times = new List<float>();
		var values = new List<T>();

		foreach (var (key, value) in keys)
		{
			times.Add(key);
			values.Add(convert(value));
		}

		return (times.ToArray(), values.ToArray());
	}

	private static IEnumerable<(float, T)> CubicValues<T>(IAnimationSampler<T> sampler) where T : struct
	{
		foreach (var (key, value) in sampler.GetCubicKeys())
		{
			yield return (key, value.Value);
		}
	}
}
