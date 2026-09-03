using System;
using System.Collections.Generic;
using System.Numerics;
using DecaEngine.Core;

namespace DecaEngine.Animation;

/// <summary>
/// Humanoid skeleton slots so systems can say "left foot" instead of a rig-specific joint name.
/// The set matches the common Unity/FBX humanoid layout: anything smaller misses shoulders and
/// toes (needed by retargeting and foot IK), anything bigger demands bones half the rigs lack.
/// </summary>
public enum HumanoidBone
{
	Hips,
	Spine,
	Chest,
	UpperChest,
	Neck,
	Head,

	LeftShoulder,
	LeftUpperArm,
	LeftLowerArm,
	LeftHand,

	RightShoulder,
	RightUpperArm,
	RightLowerArm,
	RightHand,

	LeftUpperLeg,
	LeftLowerLeg,
	LeftFoot,
	LeftToes,

	RightUpperLeg,
	RightLowerLeg,
	RightFoot,
	RightToes,

	Count,
}

public enum HumanoidSide
{
	None,
	Left,
	Right,
}

/// <summary>Slot metadata table; kept as data rather than derived from enum names, which are readability, not semantics.</summary>
public static class HumanoidBones
{
	public readonly record struct Info(HumanoidBone Bone, string Title, bool Required, HumanoidSide Side);

	/// <summary>
	/// Only slots without which a humanoid stops being one are required. Neck, chest, shoulders
	/// and toes are deliberately optional: real rigs often lack them entirely, and requiring them
	/// would declare half of normal models broken.
	/// </summary>
	public static readonly Info[] All =
	[
		new(HumanoidBone.Hips, "Hips", true, HumanoidSide.None),
		new(HumanoidBone.Spine, "Spine", true, HumanoidSide.None),
		new(HumanoidBone.Chest, "Chest", false, HumanoidSide.None),
		new(HumanoidBone.UpperChest, "Upper chest", false, HumanoidSide.None),
		new(HumanoidBone.Neck, "Neck", false, HumanoidSide.None),
		new(HumanoidBone.Head, "Head", true, HumanoidSide.None),

		new(HumanoidBone.LeftShoulder, "Shoulder (clavicle) L", false, HumanoidSide.Left),
		new(HumanoidBone.LeftUpperArm, "Upper arm L", true, HumanoidSide.Left),
		new(HumanoidBone.LeftLowerArm, "Forearm L", true, HumanoidSide.Left),
		new(HumanoidBone.LeftHand, "Hand L", true, HumanoidSide.Left),

		new(HumanoidBone.RightShoulder, "Shoulder (clavicle) R", false, HumanoidSide.Right),
		new(HumanoidBone.RightUpperArm, "Upper arm R", true, HumanoidSide.Right),
		new(HumanoidBone.RightLowerArm, "Forearm R", true, HumanoidSide.Right),
		new(HumanoidBone.RightHand, "Hand R", true, HumanoidSide.Right),

		new(HumanoidBone.LeftUpperLeg, "Thigh L", true, HumanoidSide.Left),
		new(HumanoidBone.LeftLowerLeg, "Shin L", true, HumanoidSide.Left),
		new(HumanoidBone.LeftFoot, "Foot L", true, HumanoidSide.Left),
		new(HumanoidBone.LeftToes, "Toes L", false, HumanoidSide.Left),

		new(HumanoidBone.RightUpperLeg, "Thigh R", true, HumanoidSide.Right),
		new(HumanoidBone.RightLowerLeg, "Shin R", true, HumanoidSide.Right),
		new(HumanoidBone.RightFoot, "Foot R", true, HumanoidSide.Right),
		new(HumanoidBone.RightToes, "Toes R", false, HumanoidSide.Right),
	];

	public static Info Of(HumanoidBone bone) => All[(int)bone];

	public static bool IsRequired(HumanoidBone bone) => All[(int)bone].Required;
}

/// <summary>
/// Avatar: mapping of humanoid slots to joint NAMES of a specific rig. Names, not indices:
/// indices depend on glTF node order and silently shift on re-export, after which animation still
/// plays but bends the wrong bones. Indices are resolved on the spot from a skeleton
/// (<see cref="Resolve"/>) and live only as long as that skeleton does.
/// </summary>
public sealed class HumanoidAvatar
{
	private readonly string[] _joints = new string[(int)HumanoidBone.Count];

	/// <summary>Joint name in the slot; empty means unassigned.</summary>
	public string this[HumanoidBone bone]
	{
		get => _joints[(int)bone] ?? string.Empty;
		set => _joints[(int)bone] = value ?? string.Empty;
	}

	public bool IsAssigned(HumanoidBone bone) => !string.IsNullOrEmpty(_joints[(int)bone]);

	/// <summary>
	/// Reference pose of the rig: local TRS keyed by bone NAME; empty until captured. It anchors
	/// retargeting - rotations transfer as a DEVIATION from the reference pose
	/// (target = target_ref * (source_ref^-1 * source)). ALL skeleton bones are stored, not just
	/// mapped slots: intermediate links (forearm twists, pelvis helpers) are needed to rebuild a
	/// slot's model pose from the root.
	/// </summary>
	public readonly Dictionary<string, Transform> ReferenceLocals = new(StringComparer.Ordinal);

	public bool HasReferencePose => ReferenceLocals.Count > 0;

	public void Clear()
	{
		for (int i = 0; i < _joints.Length; i++)
		{
			_joints[i] = string.Empty;
		}

		ReferenceLocals.Clear();
	}

	public HumanoidAvatar Clone()
	{
		var clone = new HumanoidAvatar();
		for (int i = 0; i < _joints.Length; i++)
		{
			clone._joints[i] = _joints[i];
		}

		foreach (var pair in ReferenceLocals)
		{
			clone.ReferenceLocals[pair.Key] = pair.Value;
		}

		return clone;
	}

	/// <summary>
	/// Resolves names to skeleton joint indices. -1 means the slot is unassigned OR the bone does
	/// not exist in this skeleton; callers need not distinguish, but the editor must display the
	/// two cases differently (see <see cref="HumanoidValidation"/>).
	/// </summary>
	public int[] Resolve(PreparedSkeleton skeleton)
	{
		var result = new int[(int)HumanoidBone.Count];

		for (int i = 0; i < result.Length; i++)
		{
			result[i] = string.IsNullOrEmpty(_joints[i]) ? -1 : skeleton.FindJoint(_joints[i]);
		}

		return result;
	}
}

/// <summary>Detected avatar problem; a typed record rather than a string so the editor can highlight the offending slot.</summary>
public readonly record struct HumanoidIssue(HumanoidBone Bone, string Message, bool Fatal);

public static class HumanoidValidation
{
	/// <summary>
	/// Validates an avatar against a skeleton: missing required slots, bones from another model,
	/// the same bone in two slots, and broken chains. A broken chain is the sneakiest - every
	/// bone exists yet the shin is not a descendant of the thigh, so two-bone IK and ragdolls
	/// produce nonsense without a single error.
	/// </summary>
	public static List<HumanoidIssue> Validate(HumanoidAvatar avatar, PreparedSkeleton skeleton)
	{
		var issues = new List<HumanoidIssue>();
		var resolved = avatar.Resolve(skeleton);

		foreach (var info in HumanoidBones.All)
		{
			int index = (int)info.Bone;

			if (!avatar.IsAssigned(info.Bone))
			{
				if (info.Required)
				{
					issues.Add(new HumanoidIssue(info.Bone, "required slot is not assigned", true));
				}

				continue;
			}

			if (resolved[index] < 0)
			{
				issues.Add(new HumanoidIssue(info.Bone,
					$"bone '{avatar[info.Bone]}' is not in the skeleton - avatar from another model?", true));
			}
		}

		// Duplicates: one bone in two slots.
		for (int i = 0; i < resolved.Length; i++)
		{
			if (resolved[i] < 0)
			{
				continue;
			}

			for (int j = i + 1; j < resolved.Length; j++)
			{
				if (resolved[i] == resolved[j])
				{
					issues.Add(new HumanoidIssue((HumanoidBone)j,
						$"same bone as in slot {HumanoidBones.Of((HumanoidBone)i).Title}", true));
				}
			}
		}

		CheckChain(issues, skeleton, resolved,
			HumanoidBone.LeftUpperLeg, HumanoidBone.LeftLowerLeg, HumanoidBone.LeftFoot);
		CheckChain(issues, skeleton, resolved,
			HumanoidBone.RightUpperLeg, HumanoidBone.RightLowerLeg, HumanoidBone.RightFoot);
		CheckChain(issues, skeleton, resolved,
			HumanoidBone.LeftUpperArm, HumanoidBone.LeftLowerArm, HumanoidBone.LeftHand);
		CheckChain(issues, skeleton, resolved,
			HumanoidBone.RightUpperArm, HumanoidBone.RightLowerArm, HumanoidBone.RightHand);

		return issues;
	}

	private static void CheckChain(List<HumanoidIssue> issues, PreparedSkeleton skeleton, int[] resolved,
		HumanoidBone upper, HumanoidBone middle, HumanoidBone lower)
	{
		CheckDescendant(issues, skeleton, resolved, upper, middle);
		CheckDescendant(issues, skeleton, resolved, middle, lower);
	}

	private static void CheckDescendant(List<HumanoidIssue> issues, PreparedSkeleton skeleton, int[] resolved,
		HumanoidBone ancestor, HumanoidBone descendant)
	{
		int a = resolved[(int)ancestor];
		int d = resolved[(int)descendant];

		if (a < 0 || d < 0)
		{
			return;
		}

		for (int joint = skeleton.Parents[d]; joint >= 0; joint = skeleton.Parents[joint])
		{
			if (joint == a)
			{
				return;
			}
		}

		issues.Add(new HumanoidIssue(descendant,
			$"not a descendant of slot {HumanoidBones.Of(ancestor).Title} - chain is broken", true));
	}
}

/// <summary>
/// Automatic avatar mapping from skeleton topology. Topology first, names second: rig names lie
/// more often than structure, but only names can tell LEFT from RIGHT - the sides are
/// topologically identical. The result must be shown to a human and hand-corrected (Humanoid
/// window): a silent mistake here looks like a retargeting bug, not a mapping bug.
/// </summary>
public static class HumanoidAutoMap
{
	public static HumanoidAvatar Build(PreparedSkeleton skeleton)
	{
		var avatar = new HumanoidAvatar();
		if (skeleton == null || skeleton.JointCount == 0)
		{
			return avatar;
		}

		var pose = new SkeletonPose(skeleton);
		pose.ComputeModelMatrices();

		var positions = new Vector3[skeleton.JointCount];
		for (int i = 0; i < positions.Length; i++)
		{
			positions[i] = pose.ModelMatrices[i].Translation;
		}

		var children = BuildChildren(skeleton);

		int hips = FindHips(skeleton, children);
		if (hips < 0)
		{
			return avatar;
		}

		avatar[HumanoidBone.Hips] = skeleton.JointNames[hips];

		var branches = new List<int>(children[hips]);

		// The spine is the branch with the HIGHEST tip - the head tops any skeleton. Maximum, not
		// minimum: on a quadruped the front legs hang off the spine, so its lowest point is the
		// floor, and a minimum-based pick sent the spine into the tail (seen on Fox).
		int spine = -1;
		float bestTop = float.NegativeInfinity;

		foreach (int branch in branches)
		{
			float top = TipExtent(branch, children, positions, highest: true);
			if (top > bestTop)
			{
				bestTop = top;
				spine = branch;
			}
		}

		if (spine >= 0)
		{
			MapSpine(avatar, skeleton, children, positions, spine);
		}

		// Legs are the two branches with the LOWEST tips among the rest - not "everything but the
		// spine": tails, skirts and coat flaps also leave the hips.
		var rest = branches.FindAll(branch => branch != spine);
		rest.Sort((a, b) => TipExtent(a, children, positions, highest: false)
			.CompareTo(TipExtent(b, children, positions, highest: false)));

		var legs = rest.Count >= 2 ? rest.GetRange(0, 2) : new List<int>();
		AssignSides(avatar, skeleton, children, positions, legs, arms: false);

		MapArms(avatar, skeleton, children, positions);
		MapByName(avatar, skeleton);
		DropDuplicates(avatar);

		return avatar;
	}

	/// <summary>
	/// Removes duplicate assignments, keeping the first by slot order. A duplicate is always a
	/// mapping error; an empty slot is honest - it shows red in the window and in validation,
	/// while a duplicate feeds nonsense to two-bone IK silently.
	/// </summary>
	private static void DropDuplicates(HumanoidAvatar avatar)
	{
		var seen = new HashSet<string>(StringComparer.Ordinal);

		foreach (var info in HumanoidBones.All)
		{
			if (avatar.IsAssigned(info.Bone) && !seen.Add(avatar[info.Bone]))
			{
				avatar[info.Bone] = string.Empty;
			}
		}
	}

	// --- Topology ----------------------------------------------------------------------------------

	private static List<int>[] BuildChildren(PreparedSkeleton skeleton)
	{
		var children = new List<int>[skeleton.JointCount];
		for (int i = 0; i < children.Length; i++)
		{
			children[i] = new List<int>();
		}

		for (int i = 0; i < skeleton.JointCount; i++)
		{
			int parent = skeleton.Parents[i];
			if (parent >= 0)
			{
				children[parent].Add(i);
			}
		}

		return children;
	}

	/// <summary>
	/// The hips are the FIRST node from the top with three or more branches (spine plus two
	/// legs). First, specifically: many rigs have a utility root ("Armature", "root") that also
	/// has several children, and picking it misses the whole hierarchy.
	/// </summary>
	private static int FindHips(PreparedSkeleton skeleton, List<int>[] children)
	{
		for (int i = 0; i < skeleton.JointCount; i++)
		{
			if (children[i].Count >= 3)
			{
				return i;
			}
		}

		// No hip fork happens only on truncated rigs (upper body only); then the hips are the
		// root - mapping half is better than nothing.
		for (int i = 0; i < skeleton.JointCount; i++)
		{
			if (skeleton.Parents[i] < 0)
			{
				return i;
			}
		}

		return -1;
	}

	/// <summary>Highest (or lowest) point of a branch subtree. Tips, not the bone itself: leg and spine roots leave the hips at nearly one height, but their tips span the full body.</summary>
	private static float TipExtent(int joint, List<int>[] children, Vector3[] positions, bool highest)
	{
		float extent = positions[joint].Y;

		foreach (int child in children[joint])
		{
			float childExtent = TipExtent(child, children, positions, highest);
			extent = highest ? MathF.Max(extent, childExtent) : MathF.Min(extent, childExtent);
		}

		return extent;
	}

	/// <summary>Maps the spine chain from hips to head; the head is the farthest descendant on the branch, intermediate links distribute by link count.</summary>
	private static void MapSpine(HumanoidAvatar avatar, PreparedSkeleton skeleton, List<int>[] children,
		Vector3[] positions, int spineRoot)
	{
		var chain = new List<int>();
		int current = spineRoot;

		while (current >= 0)
		{
			chain.Add(current);

			// Follow the branch with the HIGHEST tip, not the longest: at the chest the spine
			// forks into neck and arms, and the arm chain is LONGER than neck+head - a
			// longest-branch walk declared the hand to be the head.
			int next = -1;
			float bestTop = float.NegativeInfinity;

			foreach (int child in children[current])
			{
				float top = TipExtent(child, children, positions, highest: true);
				if (top > bestTop)
				{
					bestTop = top;
					next = child;
				}
			}

			current = next;
		}

		if (chain.Count == 0)
		{
			return;
		}

		avatar[HumanoidBone.Head] = skeleton.JointNames[chain[^1]];

		// Intermediate slots fill BY IMPORTANCE: spine, then neck, then chest - so a short rig
		// (Spine + Head) fills Spine, not a Chest it physically lacks.
		var middle = chain.GetRange(0, chain.Count - 1);

		if (middle.Count == 0)
		{
			return;
		}

		avatar[HumanoidBone.Spine] = skeleton.JointNames[middle[0]];

		if (middle.Count >= 2)
		{
			avatar[HumanoidBone.Neck] = skeleton.JointNames[middle[^1]];
		}

		if (middle.Count >= 3)
		{
			avatar[HumanoidBone.Chest] = skeleton.JointNames[middle[1]];
		}

		if (middle.Count >= 4)
		{
			avatar[HumanoidBone.UpperChest] = skeleton.JointNames[middle[2]];
		}
	}

	private static int ChainDepth(int joint, List<int>[] children)
	{
		int depth = 0;

		foreach (int child in children[joint])
		{
			depth = Math.Max(depth, ChainDepth(child, children));
		}

		return depth + 1;
	}

	/// <summary>Arms are the two side branches off the chest/neck, searched from the most-branching spine bone regardless of its name.</summary>
	private static void MapArms(HumanoidAvatar avatar, PreparedSkeleton skeleton, List<int>[] children,
		Vector3[] positions)
	{
		int chest = FindChestJoint(avatar, skeleton, children);
		if (chest < 0)
		{
			return;
		}

		// The spine branch is excluded from candidates - it is already mapped, and without the
		// exclusion the neck would land in an arm slot.
		int neck = avatar.IsAssigned(HumanoidBone.Neck) ? skeleton.FindJoint(avatar[HumanoidBone.Neck]) : -1;
		int head = avatar.IsAssigned(HumanoidBone.Head) ? skeleton.FindJoint(avatar[HumanoidBone.Head]) : -1;

		var candidates = new List<int>();
		foreach (int child in children[chest])
		{
			// Both the spine branch and already-mapped legs are excluded: the chest may turn out
			// to be the hips themselves (a rig with no neck/chest), and the legs would become arms.
			if (child != neck && child != head && !IsAncestorOf(skeleton, child, head) &&
				!IsAssignedJoint(avatar, skeleton, child))
			{
				candidates.Add(child);
			}
		}

		if (candidates.Count < 2)
		{
			return;
		}

		// Take the two LONGEST side branches: a chest can carry anything (cape, belt, backpack),
		// but usually nothing there is longer than an arm.
		candidates.Sort((a, b) => ChainDepth(b, children).CompareTo(ChainDepth(a, children)));
		var arms = candidates.GetRange(0, 2);

		AssignSides(avatar, skeleton, children, positions, arms, arms: true);
	}

	/// <summary>
	/// The bone the arms branch from: the FIRST branching spine bone by preference order, not the
	/// one with the most children - the hips usually have more children than the chest (spine,
	/// two legs, tail), and a max-children pick declared the HIPS to be the chest, mapping hind
	/// legs into arm slots (seen on Fox).
	/// </summary>
	private static int FindChestJoint(HumanoidAvatar avatar, PreparedSkeleton skeleton, List<int>[] children)
	{
		HumanoidBone[] preferred =
		[
			HumanoidBone.UpperChest, HumanoidBone.Chest, HumanoidBone.Spine, HumanoidBone.Hips,
		];

		foreach (var bone in preferred)
		{
			if (!avatar.IsAssigned(bone))
			{
				continue;
			}

			int joint = skeleton.FindJoint(avatar[bone]);
			if (joint >= 0 && children[joint].Count >= 3)
			{
				return joint;
			}
		}

		return -1;
	}

	private static bool IsAssignedJoint(HumanoidAvatar avatar, PreparedSkeleton skeleton, int joint)
	{
		string name = skeleton.JointNames[joint];

		foreach (var info in HumanoidBones.All)
		{
			if (string.Equals(avatar[info.Bone], name, StringComparison.Ordinal))
			{
				return true;
			}
		}

		return false;
	}

	private static bool IsAncestorOf(PreparedSkeleton skeleton, int ancestor, int descendant)
	{
		for (int joint = descendant; joint >= 0; joint = skeleton.Parents[joint])
		{
			if (joint == ancestor)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Assigns two symmetric branches to left/right slots and maps their links. Side comes from
	/// the NAME first and only then from the X sign: left/right depend on which way the model
	/// faces, a convention geometry cannot express - which is exactly why pure topology cannot
	/// tell the sides apart.
	/// </summary>
	private static void AssignSides(HumanoidAvatar avatar, PreparedSkeleton skeleton, List<int>[] children,
		Vector3[] positions, List<int> branches, bool arms)
	{
		if (branches.Count != 2)
		{
			return;
		}

		int first = branches[0];
		int second = branches[1];

		var firstSide = SideFromName(skeleton.JointNames[first]);
		var secondSide = SideFromName(skeleton.JointNames[second]);

		bool firstIsLeft;

		if (firstSide != HumanoidSide.None)
		{
			firstIsLeft = firstSide == HumanoidSide.Left;
		}
		else if (secondSide != HumanoidSide.None)
		{
			firstIsLeft = secondSide == HumanoidSide.Right;
		}
		else
		{
			// Convention: the character faces +Z, left is toward +X. This cannot be guessed, so
			// the Humanoid window has a swap-sides button - more honest than silently being wrong
			// half the time.
			firstIsLeft = positions[first].X > positions[second].X;
		}

		MapLimb(avatar, skeleton, children, first, arms, firstIsLeft ? HumanoidSide.Left : HumanoidSide.Right);
		MapLimb(avatar, skeleton, children, second, arms, firstIsLeft ? HumanoidSide.Right : HumanoidSide.Left);
	}

	/// <summary>
	/// Maps one limb. For arms the key question is whether a CLAVICLE exists: a four-link chain
	/// starts with it, a three-link one starts at the upper arm - misjudging shifts the whole arm
	/// by a slot. A leg's extra link comes at the END (toes), so its slot list is fixed.
	/// </summary>
	private static void MapLimb(HumanoidAvatar avatar, PreparedSkeleton skeleton, List<int>[] children,
		int rootJoint, bool arm, HumanoidSide side)
	{
		var chain = LimbChain(children, rootJoint);
		if (chain.Count == 0)
		{
			return;
		}

		if (!arm)
		{
			Assign(avatar, skeleton, chain, side == HumanoidSide.Left
				? [HumanoidBone.LeftUpperLeg, HumanoidBone.LeftLowerLeg, HumanoidBone.LeftFoot, HumanoidBone.LeftToes]
				: [HumanoidBone.RightUpperLeg, HumanoidBone.RightLowerLeg, HumanoidBone.RightFoot, HumanoidBone.RightToes]);

			return;
		}

		bool hasShoulder = chain.Count >= 4;

		if (side == HumanoidSide.Left)
		{
			Assign(avatar, skeleton, chain, hasShoulder
				? [HumanoidBone.LeftShoulder, HumanoidBone.LeftUpperArm, HumanoidBone.LeftLowerArm, HumanoidBone.LeftHand]
				: [HumanoidBone.LeftUpperArm, HumanoidBone.LeftLowerArm, HumanoidBone.LeftHand]);
		}
		else
		{
			Assign(avatar, skeleton, chain, hasShoulder
				? [HumanoidBone.RightShoulder, HumanoidBone.RightUpperArm, HumanoidBone.RightLowerArm, HumanoidBone.RightHand]
				: [HumanoidBone.RightUpperArm, HumanoidBone.RightLowerArm, HumanoidBone.RightHand]);
		}
	}

	/// <summary>
	/// Collects a limb chain from the root link down along the longest branch, with two stops:
	/// a bone with three or more branches (a hand with fingers - otherwise the arm chain runs
	/// into the pinky) and a cap of four links (more do not fit humanoid slots).
	/// </summary>
	private static List<int> LimbChain(List<int>[] children, int rootJoint)
	{
		var chain = new List<int>();
		int current = rootJoint;

		while (current >= 0 && chain.Count < 4)
		{
			chain.Add(current);

			if (children[current].Count >= 3)
			{
				break;
			}

			int next = -1;
			int bestDepth = -1;

			foreach (int child in children[current])
			{
				int depth = ChainDepth(child, children);
				if (depth > bestDepth)
				{
					bestDepth = depth;
					next = child;
				}
			}

			current = next;
		}

		return chain;
	}

	/// <summary>
	/// Assigns a chain to slots via an EXPLICIT list, not "root slot plus link index" arithmetic:
	/// slots are only contiguous within one limb, and index math wrote a fifth arm link into the
	/// first slot of the NEXT limb - an error that looks like random mapping.
	/// </summary>
	private static void Assign(HumanoidAvatar avatar, PreparedSkeleton skeleton, List<int> chain,
		ReadOnlySpan<HumanoidBone> slots)
	{
		int count = Math.Min(chain.Count, slots.Length);

		for (int i = 0; i < count; i++)
		{
			avatar[slots[i]] = skeleton.JointNames[chain[i]];
		}
	}

	// --- Names -------------------------------------------------------------------------------------

	/// <summary>
	/// Name-based fill-in for EMPTY slots only: topology has already spoken, and overriding it by
	/// name would lose exactly where names lie.
	/// </summary>
	private static void MapByName(HumanoidAvatar avatar, PreparedSkeleton skeleton)
	{
		foreach (var info in HumanoidBones.All)
		{
			if (avatar.IsAssigned(info.Bone))
			{
				continue;
			}

			string[] keywords = KeywordsOf(info.Bone);
			if (keywords.Length == 0)
			{
				continue;
			}

			int best = -1;
			int bestLength = int.MaxValue;

			for (int joint = 0; joint < skeleton.JointCount; joint++)
			{
				string name = Normalize(skeleton.JointNames[joint]);
				if (name.Length == 0 || Taken(avatar, skeleton.JointNames[joint]))
				{
					continue;
				}

				if (info.Side != HumanoidSide.None && SideFromName(skeleton.JointNames[joint]) != info.Side)
				{
					continue;
				}

				foreach (string keyword in keywords)
				{
					// Shortest matching name wins: between "neck" and "neck_twist_01" the former
					// is almost always the real bone.
					if (name.Contains(keyword, StringComparison.Ordinal) && name.Length < bestLength)
					{
						best = joint;
						bestLength = name.Length;
						break;
					}
				}
			}

			if (best >= 0)
			{
				avatar[info.Bone] = skeleton.JointNames[best];
			}
		}
	}

	private static bool Taken(HumanoidAvatar avatar, string jointName)
	{
		foreach (var info in HumanoidBones.All)
		{
			if (string.Equals(avatar[info.Bone], jointName, StringComparison.Ordinal))
			{
				return true;
			}
		}

		return false;
	}

	private static string[] KeywordsOf(HumanoidBone bone) => bone switch
	{
		HumanoidBone.Hips => ["hips", "hip", "pelvis"],
		HumanoidBone.Spine => ["spine"],
		HumanoidBone.Chest => ["chest"],
		HumanoidBone.UpperChest => ["upperchest"],
		HumanoidBone.Neck => ["neck"],
		HumanoidBone.Head => ["head"],

		HumanoidBone.LeftShoulder or HumanoidBone.RightShoulder => ["shoulder", "clavicle", "collar"],
		HumanoidBone.LeftUpperArm or HumanoidBone.RightUpperArm => ["upperarm", "arm"],
		HumanoidBone.LeftLowerArm or HumanoidBone.RightLowerArm => ["lowerarm", "forearm"],
		HumanoidBone.LeftHand or HumanoidBone.RightHand => ["hand", "wrist"],

		HumanoidBone.LeftUpperLeg or HumanoidBone.RightUpperLeg => ["upleg", "upperleg", "thigh"],
		HumanoidBone.LeftLowerLeg or HumanoidBone.RightLowerLeg => ["lowerleg", "calf", "shin"],
		HumanoidBone.LeftFoot or HumanoidBone.RightFoot => ["foot", "ankle"],
		HumanoidBone.LeftToes or HumanoidBone.RightToes => ["toe", "ball"],

		_ => [],
	};

	/// <summary>
	/// Side from the name. Whole words and single-letter markers are checked, but the letter only
	/// AT SEPARATORS (_l, .r, l_): a bare "l" occurs in half of all bone names.
	/// </summary>
	public static HumanoidSide SideFromName(string name)
	{
		if (string.IsNullOrEmpty(name))
		{
			return HumanoidSide.None;
		}

		string lower = name.ToLowerInvariant();

		if (lower.Contains("left", StringComparison.Ordinal))
		{
			return HumanoidSide.Left;
		}

		if (lower.Contains("right", StringComparison.Ordinal))
		{
			return HumanoidSide.Right;
		}

		foreach (char separator in ".:_- ")
		{
			if (lower.Contains($"{separator}l{separator}", StringComparison.Ordinal) ||
				lower.EndsWith($"{separator}l", StringComparison.Ordinal) ||
				lower.StartsWith($"l{separator}", StringComparison.Ordinal))
			{
				return HumanoidSide.Left;
			}

			if (lower.Contains($"{separator}r{separator}", StringComparison.Ordinal) ||
				lower.EndsWith($"{separator}r", StringComparison.Ordinal) ||
				lower.StartsWith($"r{separator}", StringComparison.Ordinal))
			{
				return HumanoidSide.Right;
			}
		}

		return HumanoidSide.None;
	}

	/// <summary>Exporter prefixes stripped WHOLE, including their separator: stripping a bare "b" would turn "ball" into "all" and lose the toes.</summary>
	private static readonly string[] RawPrefixes =
		["mixamorig:", "mixamorig", "b_", "bone_", "def-", "def_", "org-", "org_", "bip01_", "bip_"];

	/// <summary>Name without case, separators, digits and exporter prefixes; digits go on purpose - Spine01/spine_1/Spine are one bone, link numbering is recovered from topology.</summary>
	public static string Normalize(string name)
	{
		if (string.IsNullOrEmpty(name))
		{
			return string.Empty;
		}

		string lower = name.ToLowerInvariant();

		foreach (string prefix in RawPrefixes)
		{
			if (lower.StartsWith(prefix, StringComparison.Ordinal) && lower.Length > prefix.Length)
			{
				lower = lower[prefix.Length..];
				break;
			}
		}

		var builder = new System.Text.StringBuilder(lower.Length);

		foreach (char c in lower)
		{
			if (char.IsLetter(c))
			{
				builder.Append(c);
			}
		}

		return builder.ToString();
	}
}
