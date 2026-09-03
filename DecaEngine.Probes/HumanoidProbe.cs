using System;
using System.Collections.Generic;
using System.Numerics;
using DecaEngine.Graphics;
using DecaEngine.Animation;
using DecaEngine.Core;

namespace DecaEngine.Probes;

// Checks synthetic rigs whose correct mapping is known up front, then the model from args.
/// <summary>Humanoid avatar auto-mapping probe (DECA_PROBE_HUMANOID=1).</summary>
public static class HumanoidProbe
{
	// One synthetic rig joint: slot, parent and local T-pose offset.
	private readonly record struct RigJoint(HumanoidBone Slot, int Parent, Vector3 Offset, string Suffix);

	public static void Run(ModelLoader model)
	{
		ProbeSynthetic("Mixamo", Naming.Mixamo, shoulders: true, toes: true, fingers: true);
		ProbeSynthetic("Unreal", Naming.Unreal, shoulders: true, toes: true, fingers: true);
		ProbeSynthetic("Blender", Naming.Blender, shoulders: true, toes: true, fingers: true);
		ProbeSynthetic("unnamed", Naming.Anonymous, shoulders: true, toes: true, fingers: true);
		ProbeSynthetic("reduced", Naming.Mixamo, shoulders: false, toes: false, fingers: false);

		ProbeModel(model);
	}

	// --- Synthetic rigs ----------------------------------------------------------------------------

	private static void ProbeSynthetic(string title, Func<HumanoidBone, string, string> naming,
		bool shoulders, bool toes, bool fingers)
	{
		var joints = BuildRig(shoulders, toes, fingers);
		var skeleton = BuildSkeleton(joints, naming);

		var avatar = HumanoidAutoMap.Build(skeleton);
		var issues = HumanoidValidation.Validate(avatar, skeleton);

		int expected = 0;
		int correct = 0;
		var wrong = new List<string>();

		for (int i = 0; i < joints.Count; i++)
		{
			var slot = joints[i].Slot;
			if (slot >= HumanoidBone.Count)
			{
				continue;
			}

			expected++;

			string actual = avatar[slot];
			string want = skeleton.JointNames[i];

			if (string.Equals(actual, want, StringComparison.Ordinal))
			{
				correct++;
			}
			else
			{
				wrong.Add($"{HumanoidBones.Of(slot).Title}: expected '{want}', got '{(actual.Length > 0 ? actual : "-")}'");
			}
		}

		Console.WriteLine($"[probe] humanoid [{title}]: slots guessed {correct}/{expected}, " +
			$"validation issues {issues.Count} " +
			$"{(correct == expected && issues.Count == 0 ? "OK" : "MAPPING IS WRONG")}");

		ProbeReferencePose(title, avatar, skeleton);

		foreach (string line in wrong)
		{
			Console.WriteLine($"[probe] humanoid [{title}]:   {line}");
		}

		foreach (var issue in issues)
		{
			Console.WriteLine($"[probe] humanoid [{title}]:   {HumanoidBones.Of(issue.Bone).Title} - {issue.Message}");
		}
	}

	// The synthetic rig is built exactly in T-pose, so all four limbs must evaluate near zero degrees.
	private static void ProbeReferencePose(string title, HumanoidAvatar avatar, PreparedSkeleton skeleton)
	{
		HumanoidReferencePose.CaptureFromBind(avatar, skeleton);

		var report = HumanoidReferencePose.Evaluate(avatar, skeleton);

		Console.WriteLine($"[probe] humanoid [{title}]: T-pose - arms {report.LeftArmDegrees:0.#}°/" +
			$"{report.RightArmDegrees:0.#}°, legs {report.LeftLegDegrees:0.#}°/{report.RightLegDegrees:0.#}° " +
			$"{(report.LooksLikeTPose ? "OK" : "DOES NOT LOOK LIKE T")}");

		string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
			$"deca_humanoid_ref_{Environment.ProcessId}.glb");

		HumanoidAvatarAsset.Save(avatar, path);
		var loaded = HumanoidAvatarAsset.Load(path);

		int missing = 0;
		float worst = 0f;

		foreach (var pair in avatar.ReferenceLocals)
		{
			if (loaded == null || !loaded.ReferenceLocals.TryGetValue(pair.Key, out var back))
			{
				missing++;
				continue;
			}

			worst = MathF.Max(worst, Vector3.Distance(pair.Value.position, back.position));
			worst = MathF.Max(worst, (pair.Value.rotation - back.rotation).Length());
			worst = MathF.Max(worst, Vector3.Distance(pair.Value.scale, back.scale));
		}

		Console.WriteLine($"[probe] humanoid [{title}]: pose round-trip - bones " +
			$"{loaded?.ReferenceLocals.Count ?? 0}/{avatar.ReferenceLocals.Count}, lost {missing}, " +
			$"worst mismatch {worst:0.#######} {(missing == 0 && worst < 1e-5f ? "OK" : "LOSSES")}");

		try
		{
			System.IO.File.Delete(HumanoidAvatarAsset.PathFor(path));
		}
		catch (Exception)
		{
		}
	}

	// Human T-pose skeleton in metres; left side points to +X, the engine convention for unnamed rigs.
	private static List<RigJoint> BuildRig(bool shoulders, bool toes, bool fingers)
	{
		var joints = new List<RigJoint>();

		int Add(HumanoidBone slot, int parent, Vector3 offset, string suffix = "")
		{
			joints.Add(new RigJoint(slot, parent, offset, suffix));
			return joints.Count - 1;
		}

		int hips = Add(HumanoidBone.Hips, -1, new Vector3(0f, 0.95f, 0f));
		int spine = Add(HumanoidBone.Spine, hips, new Vector3(0f, 0.12f, 0f));
		int chest = Add(HumanoidBone.Chest, spine, new Vector3(0f, 0.16f, 0f));
		int neck = Add(HumanoidBone.Neck, chest, new Vector3(0f, 0.20f, 0f));
		Add(HumanoidBone.Head, neck, new Vector3(0f, 0.10f, 0f));

		AddArm(Add, chest, shoulders, fingers, side: +1f);
		AddArm(Add, chest, shoulders, fingers, side: -1f);

		AddLeg(Add, hips, toes, side: +1f);
		AddLeg(Add, hips, toes, side: -1f);

		return joints;
	}

	private static void AddArm(Func<HumanoidBone, int, Vector3, string, int> add, int chest,
		bool shoulders, bool fingers, float side)
	{
		bool left = side > 0f;
		int parent = chest;

		if (shoulders)
		{
			parent = add(left ? HumanoidBone.LeftShoulder : HumanoidBone.RightShoulder, chest,
				new Vector3(0.04f * side, 0.12f, 0f), "");
		}

		int upper = add(left ? HumanoidBone.LeftUpperArm : HumanoidBone.RightUpperArm, parent,
			new Vector3(0.12f * side, shoulders ? 0f : 0.12f, 0f), "");
		int lower = add(left ? HumanoidBone.LeftLowerArm : HumanoidBone.RightLowerArm, upper,
			new Vector3(0.28f * side, 0f, 0f), "");
		int hand = add(left ? HumanoidBone.LeftHand : HumanoidBone.RightHand, lower,
			new Vector3(0.26f * side, 0f, 0f), "");

		if (!fingers)
		{
			return;
		}

		// Five splayed fingers exercise the limb-descent limiter: without it the arm chain runs
		// into a finger and its phalanx is declared the hand.
		for (int i = 0; i < 5; i++)
		{
			add(HumanoidBone.Count, hand, new Vector3(0.08f * side, 0f, (i - 2) * 0.02f), $"Finger{i + 1}");
		}
	}

	private static void AddLeg(Func<HumanoidBone, int, Vector3, string, int> add, int hips, bool toes, float side)
	{
		bool left = side > 0f;

		int upper = add(left ? HumanoidBone.LeftUpperLeg : HumanoidBone.RightUpperLeg, hips,
			new Vector3(0.09f * side, -0.06f, 0f), "");
		int lower = add(left ? HumanoidBone.LeftLowerLeg : HumanoidBone.RightLowerLeg, upper,
			new Vector3(0f, -0.42f, 0f), "");
		int foot = add(left ? HumanoidBone.LeftFoot : HumanoidBone.RightFoot, lower,
			new Vector3(0f, -0.40f, 0f), "");

		if (toes)
		{
			add(left ? HumanoidBone.LeftToes : HumanoidBone.RightToes, foot,
				new Vector3(0f, -0.06f, 0.14f), "");
		}
	}

	private static PreparedSkeleton BuildSkeleton(List<RigJoint> joints,
		Func<HumanoidBone, string, string> naming)
	{
		int count = joints.Count;

		var skeleton = new PreparedSkeleton
		{
			JointNames = new string[count],
			Parents = new int[count],
			BindLocals = new Transform[count],
			InverseBind = new Matrix4x4[count],
		};

		var used = new HashSet<string>(StringComparer.Ordinal);

		for (int i = 0; i < count; i++)
		{
			var joint = joints[i];

			// Names must be unique: the engine looks bones up by name, and the anonymous convention
			// returns the same string for every joint.
			string name = naming(joint.Slot, joint.Suffix);
			if (!used.Add(name))
			{
				name = $"{name}_{i}";
				used.Add(name);
			}

			skeleton.JointNames[i] = name;
			skeleton.Parents[i] = joint.Parent;
			skeleton.BindLocals[i] = new Transform
			{
				position = joint.Offset,
				rotation = Quaternion.Identity,
				scale = Vector3.One,
			};

			skeleton.InverseBind[i] = Matrix4x4.Identity;
		}

		return skeleton;
	}

	// --- Naming conventions ------------------------------------------------------------------------

	private static class Naming
	{
		public static string Mixamo(HumanoidBone slot, string suffix) => slot switch
		{
			HumanoidBone.Hips => "mixamorig:Hips",
			HumanoidBone.Spine => "mixamorig:Spine",
			HumanoidBone.Chest => "mixamorig:Spine1",
			HumanoidBone.UpperChest => "mixamorig:Spine2",
			HumanoidBone.Neck => "mixamorig:Neck",
			HumanoidBone.Head => "mixamorig:Head",
			HumanoidBone.LeftShoulder => "mixamorig:LeftShoulder",
			HumanoidBone.LeftUpperArm => "mixamorig:LeftArm",
			HumanoidBone.LeftLowerArm => "mixamorig:LeftForeArm",
			HumanoidBone.LeftHand => "mixamorig:LeftHand",
			HumanoidBone.RightShoulder => "mixamorig:RightShoulder",
			HumanoidBone.RightUpperArm => "mixamorig:RightArm",
			HumanoidBone.RightLowerArm => "mixamorig:RightForeArm",
			HumanoidBone.RightHand => "mixamorig:RightHand",
			HumanoidBone.LeftUpperLeg => "mixamorig:LeftUpLeg",
			HumanoidBone.LeftLowerLeg => "mixamorig:LeftLeg",
			HumanoidBone.LeftFoot => "mixamorig:LeftFoot",
			HumanoidBone.LeftToes => "mixamorig:LeftToeBase",
			HumanoidBone.RightUpperLeg => "mixamorig:RightUpLeg",
			HumanoidBone.RightLowerLeg => "mixamorig:RightLeg",
			HumanoidBone.RightFoot => "mixamorig:RightFoot",
			HumanoidBone.RightToes => "mixamorig:RightToeBase",
			_ => $"mixamorig:{suffix}",
		};

		public static string Unreal(HumanoidBone slot, string suffix) => slot switch
		{
			HumanoidBone.Hips => "pelvis",
			HumanoidBone.Spine => "spine_01",
			HumanoidBone.Chest => "spine_02",
			HumanoidBone.UpperChest => "spine_03",
			HumanoidBone.Neck => "neck_01",
			HumanoidBone.Head => "head",
			HumanoidBone.LeftShoulder => "clavicle_l",
			HumanoidBone.LeftUpperArm => "upperarm_l",
			HumanoidBone.LeftLowerArm => "lowerarm_l",
			HumanoidBone.LeftHand => "hand_l",
			HumanoidBone.RightShoulder => "clavicle_r",
			HumanoidBone.RightUpperArm => "upperarm_r",
			HumanoidBone.RightLowerArm => "lowerarm_r",
			HumanoidBone.RightHand => "hand_r",
			HumanoidBone.LeftUpperLeg => "thigh_l",
			HumanoidBone.LeftLowerLeg => "calf_l",
			HumanoidBone.LeftFoot => "foot_l",
			HumanoidBone.LeftToes => "ball_l",
			HumanoidBone.RightUpperLeg => "thigh_r",
			HumanoidBone.RightLowerLeg => "calf_r",
			HumanoidBone.RightFoot => "foot_r",
			HumanoidBone.RightToes => "ball_r",
			_ => suffix.ToLowerInvariant(),
		};

		public static string Blender(HumanoidBone slot, string suffix) => slot switch
		{
			HumanoidBone.Hips => "DEF-hips",
			HumanoidBone.Spine => "DEF-spine",
			HumanoidBone.Chest => "DEF-chest",
			HumanoidBone.UpperChest => "DEF-chest.001",
			HumanoidBone.Neck => "DEF-neck",
			HumanoidBone.Head => "DEF-head",
			HumanoidBone.LeftShoulder => "DEF-shoulder.L",
			HumanoidBone.LeftUpperArm => "DEF-upper_arm.L",
			HumanoidBone.LeftLowerArm => "DEF-forearm.L",
			HumanoidBone.LeftHand => "DEF-hand.L",
			HumanoidBone.RightShoulder => "DEF-shoulder.R",
			HumanoidBone.RightUpperArm => "DEF-upper_arm.R",
			HumanoidBone.RightLowerArm => "DEF-forearm.R",
			HumanoidBone.RightHand => "DEF-hand.R",
			HumanoidBone.LeftUpperLeg => "DEF-thigh.L",
			HumanoidBone.LeftLowerLeg => "DEF-shin.L",
			HumanoidBone.LeftFoot => "DEF-foot.L",
			HumanoidBone.LeftToes => "DEF-toe.L",
			HumanoidBone.RightUpperLeg => "DEF-thigh.R",
			HumanoidBone.RightLowerLeg => "DEF-shin.R",
			HumanoidBone.RightFoot => "DEF-foot.R",
			HumanoidBone.RightToes => "DEF-toe.R",
			_ => $"DEF-{suffix}",
		};

		// Meaningless names: forces the mapper onto pure topology and the X-sign side split.
		public static string Anonymous(HumanoidBone slot, string suffix) => "j";
	}

	// --- Model from args ---------------------------------------------------------------------------

	// No expected answer here, just a printout: quadrupeds map front legs into the arm slots, which
	// is structurally correct.
	private static void ProbeModel(ModelLoader model)
	{
		var skeleton = model.Skeleton;

		if (skeleton == null)
		{
			Console.WriteLine("[probe] humanoid [model]: no skeleton - nothing to map");
			return;
		}

		var avatar = HumanoidAutoMap.Build(skeleton);
		var issues = HumanoidValidation.Validate(avatar, skeleton);

		foreach (var info in HumanoidBones.All)
		{
			Console.WriteLine($"[probe] humanoid [model]: {info.Title,-18} {(info.Required ? "*" : " ")} " +
				$"{(avatar.IsAssigned(info.Bone) ? avatar[info.Bone] : "-")}");
		}

		Console.WriteLine($"[probe] humanoid [model]: issues {issues.Count}");

		foreach (var issue in issues)
		{
			Console.WriteLine($"[probe] humanoid [model]:   {HumanoidBones.Of(issue.Bone).Title} - {issue.Message}");
		}

		ProbeRoundTrip(avatar);
	}

	private static void ProbeRoundTrip(HumanoidAvatar avatar)
	{
		string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
			$"deca_humanoid_probe_{Environment.ProcessId}.glb");

		HumanoidAvatarAsset.Save(avatar, path);
		var loaded = HumanoidAvatarAsset.Load(path);

		int mismatch = 0;
		foreach (var info in HumanoidBones.All)
		{
			if (!string.Equals(avatar[info.Bone], loaded?[info.Bone] ?? string.Empty, StringComparison.Ordinal))
			{
				mismatch++;
			}
		}

		Console.WriteLine($"[probe] humanoid: avatar round-trip - mismatches {mismatch} " +
			$"{(mismatch == 0 ? "OK" : "SLOTS LOST")}");

		try
		{
			System.IO.File.Delete(HumanoidAvatarAsset.PathFor(path));
		}
		catch (Exception)
		{
		}
	}
}
