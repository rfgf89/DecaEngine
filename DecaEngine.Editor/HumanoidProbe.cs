using System;
using System.Collections.Generic;
using System.Numerics;
using DecaEngine.Graphics;

namespace DecaEngine.Editor;

/// <summary>
/// Проверка автоматической разметки аватара (DECA_PROBE_HUMANOID=1).
///
/// Основная проверка идёт на СИНТЕТИЧЕСКИХ ригах, а не на модели из аргументов, и это не лень, а
/// единственный способ проверить разметку по существу. Разметка выглядит правдоподобной ровно до
/// того момента, как по ней поедет ретаргетинг: «голень назначена не на ту кость» и «голень
/// назначена правильно» в списке слотов отличаются одной цифрой в имени. У синтетического рига
/// правильный ответ известен ЗАРАНЕЕ - его строил тот же код, - и сравнение получается точным, а не
/// «на глаз».
///
/// Риги генерируются в нескольких соглашениях имён (Mixamo, Unreal, Blender) плюс БЕЗЫМЯННЫЙ - в
/// нём проверяется чистая топология, включая разведение сторон по знаку X. Плюс укороченный риг
/// без ключиц и носков: в реальных моделях их часто нет, и цепочка руки из трёх звеньев обязана
/// раскладываться со сдвигом.
///
/// Модель из аргументов размечается в конце - как «полевая» проверка на настоящих данных.
/// </summary>
public static class HumanoidProbe
{
	/// <summary>Описание одного сустава синтетического рига: слот, родитель и локальное смещение в
	/// T-позе.</summary>
	private readonly record struct RigJoint(HumanoidBone Slot, int Parent, Vector3 Offset, string Suffix);

	public static void Run(ModelLoader model)
	{
		ProbeSynthetic("Mixamo", Naming.Mixamo, shoulders: true, toes: true, fingers: true);
		ProbeSynthetic("Unreal", Naming.Unreal, shoulders: true, toes: true, fingers: true);
		ProbeSynthetic("Blender", Naming.Blender, shoulders: true, toes: true, fingers: true);
		ProbeSynthetic("без имён", Naming.Anonymous, shoulders: true, toes: true, fingers: true);
		ProbeSynthetic("укороченный", Naming.Mixamo, shoulders: false, toes: false, fingers: false);

		ProbeModel(model);
	}

	// --- Синтетические риги ------------------------------------------------------------------------

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
				wrong.Add($"{HumanoidBones.Of(slot).Title}: ждали '{want}', получили '{(actual.Length > 0 ? actual : "-")}'");
			}
		}

		Console.WriteLine($"[probe] humanoid [{title}]: слотов угадано {correct}/{expected}, " +
			$"проблем валидации {issues.Count} " +
			$"{(correct == expected && issues.Count == 0 ? "OK" : "РАЗМЕТКА НЕВЕРНА")}");

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

	/// <summary>
	/// Референсная поза: снятие, оценка и round-trip через файл.
	///
	/// Синтетический риг построен ТОЧНО в T-позе, поэтому здесь известен правильный ответ: оценка
	/// обязана дать около нуля градусов по всем четырём конечностям. Это ловит и ошибку в самой
	/// оценке (перепутанные стороны, не та ось), и ошибку в сборке модельных матриц из референса.
	/// </summary>
	private static void ProbeReferencePose(string title, HumanoidAvatar avatar, PreparedSkeleton skeleton)
	{
		HumanoidReferencePose.CaptureFromBind(avatar, skeleton);

		var report = HumanoidReferencePose.Evaluate(avatar, skeleton);

		Console.WriteLine($"[probe] humanoid [{title}]: T-поза - руки {report.LeftArmDegrees:0.#}°/" +
			$"{report.RightArmDegrees:0.#}°, ноги {report.LeftLegDegrees:0.#}°/{report.RightLegDegrees:0.#}° " +
			$"{(report.LooksLikeTPose ? "OK" : "НЕ ПОХОЖЕ НА T")}");

		// Round-trip через файл: референс - это несколько десятков TRS, и потеря точности или
		// пропуск кости здесь проявились бы уже в ретаргетинге, где искать её будет негде.
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

		Console.WriteLine($"[probe] humanoid [{title}]: round-trip позы - костей " +
			$"{loaded?.ReferenceLocals.Count ?? 0}/{avatar.ReferenceLocals.Count}, потеряно {missing}, " +
			$"худшее расхождение {worst:0.#######} {(missing == 0 && worst < 1e-5f ? "OK" : "ПОТЕРИ")}");

		try
		{
			System.IO.File.Delete(HumanoidAvatarAsset.PathFor(path));
		}
		catch (Exception)
		{
		}
	}

	/// <summary>
	/// Скелет человека в T-позе, метры. Пропорции взяты условно-анатомическими, но важны в нём не
	/// они, а СТРОЕНИЕ: таз с тремя ветвями, позвоночник с ветвлением на груди, руки вдоль X, ноги
	/// вниз, пальцы веером от кисти. Именно за эти признаки цепляется автомат.
	///
	/// Левая сторона - в сторону +X: это соглашение движка для безымянных ригов (см.
	/// HumanoidAutoMap.AssignSides), и синтетический риг обязан ему следовать, иначе безымянный
	/// случай проверялся бы против неверного ожидания.
	/// </summary>
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

		// Пять пальцев веером: на них проверяется ограничитель спуска по конечности - без него
		// цепочка руки уходит в мизинец, и кистью объявляется его фаланга.
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

			// Имена доводятся до уникальности номером. Это не украшение: движок ищет кости ПО ИМЕНИ,
			// и риг с повторами - это риг, у которого все одноимённые кости для поиска одна и та же.
			// Безымянное соглашение выдаёт одну и ту же строку на все суставы, и без этого шага оно
			// проверяло бы не топологию, а поведение при коллизии имён.
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

			// Обратная bind-матрица автомату не нужна вовсе (он смотрит только на иерархию и
			// позиции), но оставлять массив пустым нельзя: скелет обязан быть согласованным, иначе
			// проверка молча зависела бы от того, чего не проверяет.
			skeleton.InverseBind[i] = Matrix4x4.Identity;
		}

		return skeleton;
	}

	// --- Соглашения имён ---------------------------------------------------------------------------

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

		/// <summary>Имена, не говорящие ни о чём. Проверяют чистую топологию: разметку цепочек и
		/// разведение сторон по знаку X - единственное, что автомату остаётся, когда имена молчат.</summary>
		public static string Anonymous(HumanoidBone slot, string suffix) => "j";
	}

	// --- Модель из аргументов ----------------------------------------------------------------------

	/// <summary>
	/// Разметка настоящей модели - без ожидаемого ответа, просто печать. Лиса не человек, и это не
	/// мешает: автомат опирается на СТРОЕНИЕ (таз с тремя ветвями, длинная цепочка вверх до головы,
	/// две цепочки вниз), а оно у четвероногого такое же. Передние лапы приезжают в слоты рук -
	/// структурно это верно, и именно так их и надо ретаргетить.
	/// </summary>
	private static void ProbeModel(ModelLoader model)
	{
		var skeleton = model.Skeleton;

		if (skeleton == null)
		{
			Console.WriteLine("[probe] humanoid [модель]: скелета нет - размечать нечего");
			return;
		}

		var avatar = HumanoidAutoMap.Build(skeleton);
		var issues = HumanoidValidation.Validate(avatar, skeleton);

		foreach (var info in HumanoidBones.All)
		{
			Console.WriteLine($"[probe] humanoid [модель]: {info.Title,-18} {(info.Required ? "*" : " ")} " +
				$"{(avatar.IsAssigned(info.Bone) ? avatar[info.Bone] : "-")}");
		}

		Console.WriteLine($"[probe] humanoid [модель]: проблем {issues.Count}");

		foreach (var issue in issues)
		{
			Console.WriteLine($"[probe] humanoid [модель]:   {HumanoidBones.Of(issue.Bone).Title} - {issue.Message}");
		}

		ProbeRoundTrip(avatar);
	}

	/// <summary>Аватар живёт в файле рядом с моделью, и потеря слота при записи или чтении выглядит
	/// потом как «автомат разметил хуже, чем в прошлый раз».</summary>
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

		Console.WriteLine($"[probe] humanoid: round-trip аватара - расхождений {mismatch} " +
			$"{(mismatch == 0 ? "OK" : "ПОТЕРЯНЫ СЛОТЫ")}");

		try
		{
			System.IO.File.Delete(HumanoidAvatarAsset.PathFor(path));
		}
		catch (Exception)
		{
			// Временный файл, который не удалился, - не повод ронять пробник.
		}
	}
}
