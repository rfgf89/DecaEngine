using System.Numerics;
using DecaEngine.Core;

namespace DecaEngine.Animation;

/// <summary>Per-vertex skinning attributes (16 bytes, separate stream parallel to Vertex); weights are normalized at import so they sum to exactly <see cref="WeightScale"/>.</summary>
public struct SkinVertex
{
	/// <summary>Weight denominator: float weight = W? / <see cref="WeightScale"/>.</summary>
	public const float WeightScale = 65535f;

	/// <summary>Max influences per vertex. Mirrored in SkinningCS.hlsl - change both together.</summary>
	public const int MaxInfluences = 4;

	public ushort J0, J1, J2, J3;
	public ushort W0, W1, W2, W3;

	public readonly bool IsUnskinned => W0 == 0 && W1 == 0 && W2 == 0 && W3 == 0;
}

/// <summary>
/// Model skeleton: flat joint array sorted topologically (parent always before child) - a contract
/// relied on by single-pass model-matrix computation and the ozz skeleton. All data is already in
/// the engine's left-handed space (see <see cref="SkinningImport.MirrorZ"/>); conversion from
/// glTF's right-handed space happens entirely at import.
/// </summary>
public sealed class PreparedSkeleton
{
	/// <summary>Joint names; used to find bones for IK, ragdoll and spring bones.</summary>
	public string[] JointNames = [];

	/// <summary>Parent index per joint, -1 for the root. Always less than the joint's own index.</summary>
	public int[] Parents = [];

	/// <summary>Local TRS of each joint in bind pose (also the default pose when a clip omits a channel).</summary>
	public Transform[] BindLocals = [];

	/// <summary>Inverse bind matrix (model space -> joint space); computed from bind pose for joints not in the skin.</summary>
	public Matrix4x4[] InverseBind = [];

	public int JointCount => Parents.Length;

	/// <summary>Joint index by name, -1 if absent. Linear search on purpose: setup-time only.</summary>
	public int FindJoint(string name)
	{
		for (int i = 0; i < JointNames.Length; i++)
		{
			if (string.Equals(JointNames[i], name, System.StringComparison.Ordinal))
			{
				return i;
			}
		}

		return -1;
	}
}

/// <summary>One animation clip; tracks keep raw glTF keys with their own times - no resampling, ozz repacks on its side anyway.</summary>
public sealed class PreparedAnimation
{
	public string Name = string.Empty;

	/// <summary>Clip duration in seconds = max key time across all tracks.</summary>
	public float Duration;

	/// <summary>One track per skeleton joint, same indexing as <see cref="PreparedSkeleton"/>; empty track = bind pose.</summary>
	public JointTrack[] Tracks = [];
}

/// <summary>Keys of one joint in a clip. Channels are independent: glTF may animate rotation only.</summary>
public sealed class JointTrack
{
	public float[] TranslationTimes = [];
	public Vector3[] Translations = [];

	public float[] RotationTimes = [];
	public Quaternion[] Rotations = [];

	public float[] ScaleTimes = [];
	public Vector3[] Scales = [];

	public bool IsEmpty => TranslationTimes.Length == 0 && RotationTimes.Length == 0 && ScaleTimes.Length == 0;
}
