using System;
using System.Collections.Generic;
using System.Numerics;
using DecaEngine.Core;

namespace DecaEngine.Animation;

/// <summary>
/// Слоты humanoid-скелета. Смысл всей затеи в том, чтобы системы говорили «левая стопа», а не
/// <c>b_LeftFoot01_017</c>: имя джойнта - свойство КОНКРЕТНОЙ модели, и всё, что настроено по именам
/// (foot IK, рэгдолл, привязка оружия, ретаргетинг клипов), приходится настраивать заново под каждый
/// риг и переделывать после каждого переэкспорта.
///
/// Набор намеренно совпадает по составу с общепринятым (Unity/FBX humanoid): не ради совместимости
/// форматов, а потому что он проверен временем - меньший не покрывает плечи и носки, без которых не
/// работает ни ретаргетинг, ни foot IK, а больший требует от художника размечать кости, которых в
/// половине ригов просто нет.
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

/// <summary>Справочник о слотах: что обязательно, к какой стороне и цепочке относится. Таблицей, а не
/// разбором имени enum-а: имена слотов - это удобство чтения, а не данные, и выводить из них логику
/// значит сломать её первым же переименованием.</summary>
public static class HumanoidBones
{
	public readonly record struct Info(HumanoidBone Bone, string Title, bool Required, HumanoidSide Side);

	/// <summary>
	/// Обязательными помечены только те слоты, без которых humanoid перестаёт быть humanoid-ом:
	/// таз, позвоночник, голова и обе цепочки конечностей. Шея, грудь, плечи и носки - НЕ обязательны
	/// осознанно: в реальных ригах их часто нет вовсе (шея слита с головой, ключицы отсутствуют), и
	/// требовать их значит объявить сломанными половину нормальных моделей.
	/// </summary>
	public static readonly Info[] All =
	[
		new(HumanoidBone.Hips, "Таз", true, HumanoidSide.None),
		new(HumanoidBone.Spine, "Позвоночник", true, HumanoidSide.None),
		new(HumanoidBone.Chest, "Грудь", false, HumanoidSide.None),
		new(HumanoidBone.UpperChest, "Верх груди", false, HumanoidSide.None),
		new(HumanoidBone.Neck, "Шея", false, HumanoidSide.None),
		new(HumanoidBone.Head, "Голова", true, HumanoidSide.None),

		new(HumanoidBone.LeftShoulder, "Плечо (ключица) L", false, HumanoidSide.Left),
		new(HumanoidBone.LeftUpperArm, "Плечо L", true, HumanoidSide.Left),
		new(HumanoidBone.LeftLowerArm, "Предплечье L", true, HumanoidSide.Left),
		new(HumanoidBone.LeftHand, "Кисть L", true, HumanoidSide.Left),

		new(HumanoidBone.RightShoulder, "Плечо (ключица) R", false, HumanoidSide.Right),
		new(HumanoidBone.RightUpperArm, "Плечо R", true, HumanoidSide.Right),
		new(HumanoidBone.RightLowerArm, "Предплечье R", true, HumanoidSide.Right),
		new(HumanoidBone.RightHand, "Кисть R", true, HumanoidSide.Right),

		new(HumanoidBone.LeftUpperLeg, "Бедро L", true, HumanoidSide.Left),
		new(HumanoidBone.LeftLowerLeg, "Голень L", true, HumanoidSide.Left),
		new(HumanoidBone.LeftFoot, "Стопа L", true, HumanoidSide.Left),
		new(HumanoidBone.LeftToes, "Носок L", false, HumanoidSide.Left),

		new(HumanoidBone.RightUpperLeg, "Бедро R", true, HumanoidSide.Right),
		new(HumanoidBone.RightLowerLeg, "Голень R", true, HumanoidSide.Right),
		new(HumanoidBone.RightFoot, "Стопа R", true, HumanoidSide.Right),
		new(HumanoidBone.RightToes, "Носок R", false, HumanoidSide.Right),
	];

	public static Info Of(HumanoidBone bone) => All[(int)bone];

	public static bool IsRequired(HumanoidBone bone) => All[(int)bone].Required;
}

/// <summary>
/// Аватар: соответствие слотов humanoid именам джойнтов КОНКРЕТНОГО рига.
///
/// Хранит ИМЕНА, а не индексы, по той же причине, по которой имена хранят компоненты анимации:
/// индексы зависят от порядка узлов в glTF и молча разъезжаются при переэкспорте модели - анимация
/// после этого продолжает работать, но гнёт не те кости. Индексы получаются на месте, из скелета
/// (<see cref="Resolve"/>), и живут ровно столько, сколько живёт этот скелет.
/// </summary>
public sealed class HumanoidAvatar
{
	private readonly string[] _joints = new string[(int)HumanoidBone.Count];

	/// <summary>Имя джойнта в слоте; пусто - слот не назначен.</summary>
	public string this[HumanoidBone bone]
	{
		get => _joints[(int)bone] ?? string.Empty;
		set => _joints[(int)bone] = value ?? string.Empty;
	}

	public bool IsAssigned(HumanoidBone bone) => !string.IsNullOrEmpty(_joints[(int)bone]);

	/// <summary>
	/// Референсная поза рига: локальные TRS по ИМЕНАМ костей. Пусто - позу ещё не снимали.
	///
	/// Это опорная точка ретаргетинга: повороты переносятся между ригами не абсолютно, а как
	/// ОТКЛОНЕНИЕ от референсной позы (<c>цель = целевой_реф * (источник_реф⁻¹ * источник)</c>).
	/// Без неё «поднятая рука» одного рига означала бы у другого что угодно - лишь бы кватернионы
	/// совпали.
	///
	/// Хранятся ВСЕ кости скелета, а не только размеченные слоты: между слотами почти всегда есть
	/// промежуточные звенья (скрутки предплечья, вспомогательные узлы таза), и без них модельную
	/// позу слота не восстановить - цепочка от корня оборвётся на первом же неразмеченном узле.
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
	/// Разворачивает имена в индексы джойнтов скелета. -1 - слот не назначен ИЛИ такой кости в
	/// скелете нет; различать эти два случая вызывающему не нужно, а вот показать их по-разному
	/// обязан редактор (см. <see cref="HumanoidValidation"/>): «не назначено» - работа не сделана,
	/// «нет такой кости» - аватар от другой модели.
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

/// <summary>Найденная проблема аватара. Отдельным типом, а не строкой: редактор подсвечивает
/// проблемный слот, а для этого ему нужен сам слот, а не текст про него.</summary>
public readonly record struct HumanoidIssue(HumanoidBone Bone, string Message, bool Fatal);

public static class HumanoidValidation
{
	/// <summary>
	/// Проверяет аватар против скелета. Ловит ровно то, что иначе проявляется уже в кадре и выглядит
	/// как ошибка анимации: незаполненные обязательные слоты, кости от другой модели, одна и та же
	/// кость в двух слотах и разорванные цепочки.
	///
	/// Разрыв цепочки - самая коварная из четырёх: аватар выглядит заполненным, каждая кость
	/// существует, а «голень» при этом не потомок «бедра», и любая система, считающая цепочку ноги
	/// непрерывной (two-bone IK, рэгдолл), даёт бессмыслицу без единой ошибки.
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
					issues.Add(new HumanoidIssue(info.Bone, "обязательный слот не назначен", true));
				}

				continue;
			}

			if (resolved[index] < 0)
			{
				issues.Add(new HumanoidIssue(info.Bone,
					$"кости '{avatar[info.Bone]}' нет в скелете - аватар от другой модели?", true));
			}
		}

		// Дубли: одна кость в двух слотах.
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
						$"та же кость, что в слоте {HumanoidBones.Of((HumanoidBone)i).Title}", true));
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
			$"не потомок слота {HumanoidBones.Of(ancestor).Title} - цепочка разорвана", true));
	}
}

/// <summary>
/// Автоматическая разметка аватара по скелету.
///
/// Сначала ТОПОЛОГИЯ, потом имена, а не наоборот. Имена в ригах врут чаще, чем строение: их
/// переименовывают, локализуют, пишут с опечатками, а вот таз всегда остаётся узлом, из которого
/// расходятся три ветви, и стопа всегда третья по цепочке вниз. Имена здесь нужны для того, чего
/// топология дать не может в принципе, - для РАЗЛИЧЕНИЯ СТОРОН: левая и правая ноги топологически
/// неотличимы.
///
/// Результат ОБЯЗАН показываться человеку и правиться руками (см. окно Humanoid). Молчаливый
/// автомат здесь - это анимация, которая играет, но гнёт не те кости, а такую ошибку ищут в
/// ретаргетинге, а не в разметке.
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

		// Позвоночник - ветвь с САМЫМ ВЫСОКИМ кончиком: голова у любого скелета выше всего, и это
		// свойство, которое не зависит ни от имён, ни от числа звеньев.
		//
		// Именно по МАКСИМУМУ, а не по минимуму. Разница не косметическая: у четвероногого передние
		// лапы висят на позвоночнике, то есть САМАЯ НИЗКАЯ точка его поддерева - пол, и отбор по
		// минимуму уводил позвоночник в хвост (проверено на Fox: слот «Позвоночник» уезжал в
		// b_Tail01_012, и дальше рушилась вся разметка).
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

		// Ноги - две ветви с самыми НИЗКИМИ кончиками среди оставшихся. Не «все, кроме позвоночника»:
		// из таза выходят ещё хвост, юбка и полы плаща, и взять их за ноги значит получить
		// персонажа, стоящего на хвосте.
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
	/// Снимает повторные назначения одной и той же кости, оставляя первое по порядку слотов.
	///
	/// Дубль - это всегда ошибка разметки, и оставить его значит отдать наружу аватар, у которого
	/// «голень» и «предплечье» - одна кость: two-bone IK по такому решает бессмыслицу, ничем не
	/// жалуясь. Пустой слот честнее: он виден в окне красным и в валидации отдельной строкой.
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

	// --- Топология ---------------------------------------------------------------------------------

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
	/// Таз - ПЕРВЫЙ сверху узел, из которого расходятся три и более ветви. Именно первый: у многих
	/// ригов корень скелета - служебный узел («Armature», «root», «Reference»), у него тоже бывает
	/// несколько детей, и взять его за таз значит промахнуться на всю иерархию. Три ветви - это
	/// позвоночник и две ноги; узел с двумя ветвями тазом быть не может.
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

		// Рига без развилки на тазе не бывает у человекоподобного, но бывает у обрезанного
		// (только верх тела). Тогда таз - корень: лучше разметить половину, чем ничего.
		for (int i = 0; i < skeleton.JointCount; i++)
		{
			if (skeleton.Parents[i] < 0)
			{
				return i;
			}
		}

		return -1;
	}

	/// <summary>Самая высокая (или самая низкая) точка поддерева ветви. По кончикам, а не по самой
	/// кости: первое звено ноги и первое звено позвоночника выходят из таза почти на одной высоте, а
	/// вот их кончики - стопа и голова - разнесены на весь рост.</summary>
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

	/// <summary>Раскладывает цепочку позвоночника от таза до головы по слотам. Голова - самый
	/// дальний потомок по этой ветви; всё, что между тазом и головой, распределяется по числу
	/// звеньев: у рига из двух звеньев это Spine+Head, из пяти - вплоть до UpperChest и Neck.</summary>
	private static void MapSpine(HumanoidAvatar avatar, PreparedSkeleton skeleton, List<int>[] children,
		Vector3[] positions, int spineRoot)
	{
		var chain = new List<int>();
		int current = spineRoot;

		while (current >= 0)
		{
			chain.Add(current);

			// Вверх по ветви с самым ВЫСОКИМ кончиком. Не по самой длинной: на груди позвоночник
			// ветвится на шею и две руки, и цепочка «плечо-предплечье-кисть-пальцы» ДЛИННЕЕ, чем
			// «шея-голова», - разметка уезжала в руку и объявляла головой кисть.
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

		// Промежуточные слоты заполняются ПО ВАЖНОСТИ: сначала позвоночник, потом шея, потом грудь.
		// Так на коротком риге (Spine + Head) заполняется именно Spine, а не Chest, которого у него
		// нет физически.
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

	/// <summary>Руки - две боковые ветви, отходящие от груди/шеи. Ищутся от той кости позвоночника, у
	/// которой ветвлений больше всего: это и есть грудь, независимо от того, как она названа.</summary>
	private static void MapArms(HumanoidAvatar avatar, PreparedSkeleton skeleton, List<int>[] children,
		Vector3[] positions)
	{
		int chest = FindChestJoint(avatar, skeleton, children);
		if (chest < 0)
		{
			return;
		}

		// Ветвь позвоночника исключается из кандидатов: она уже размечена, и без исключения шея
		// уехала бы в руку.
		int neck = avatar.IsAssigned(HumanoidBone.Neck) ? skeleton.FindJoint(avatar[HumanoidBone.Neck]) : -1;
		int head = avatar.IsAssigned(HumanoidBone.Head) ? skeleton.FindJoint(avatar[HumanoidBone.Head]) : -1;

		var candidates = new List<int>();
		foreach (int child in children[chest])
		{
			// Из кандидатов выброшены и ветвь позвоночника, и уже размеченные ноги. Ноги - потому
			// что грудью может оказаться сам таз (риг без шеи и груди), и тогда в руки уехали бы
			// именно они.
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

		// Из всех боковых ветвей берутся две САМЫЕ ДЛИННЫЕ: у рига с грудью может висеть что угодно
		// (плащ, ремень, рюкзак), но длиннее руки среди них обычно ничего нет.
		candidates.Sort((a, b) => ChainDepth(b, children).CompareTo(ChainDepth(a, children)));
		var arms = candidates.GetRange(0, 2);

		AssignSides(avatar, skeleton, children, positions, arms, arms: true);
	}

	/// <summary>
	/// Кость, от которой отходят руки, - ПЕРВАЯ СВЕРХУ кость позвоночника с ветвлением.
	///
	/// Именно первая по списку предпочтения, а не «та, у которой детей больше всего». Разница
	/// решающая: у таза детей обычно больше, чем у груди (позвоночник, две ноги, хвост), и отбор по
	/// максимуму объявлял грудью ТАЗ - после чего руками становились ноги, а ноги оставались
	/// неразмеченными (проверено на Fox: в слоты рук уезжали задние лапы).
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
	/// Раскладывает две симметричные ветви по левому и правому слоту и размечает их звенья.
	///
	/// Сторона определяется ПО ИМЕНИ, и только если имя молчит - по знаку X. Наоборот нельзя:
	/// «право» и «лево» зависят от того, куда персонаж смотрит, а это соглашение модели, которое из
	/// геометрии не выводится. Имя же его прямо называет - и именно поэтому автомат по одной
	/// топологии принципиально не может развести стороны сам.
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
			// Соглашение: персонаж смотрит вдоль +Z, левая сторона - в сторону +X. Угадать здесь
			// нельзя, поэтому в окне Humanoid есть кнопка «поменять стороны» - и это честнее, чем
			// молча ошибиться в половине случаев.
			firstIsLeft = positions[first].X > positions[second].X;
		}

		MapLimb(avatar, skeleton, children, first, arms, firstIsLeft ? HumanoidSide.Left : HumanoidSide.Right);
		MapLimb(avatar, skeleton, children, second, arms, firstIsLeft ? HumanoidSide.Right : HumanoidSide.Left);
	}

	/// <summary>
	/// Размечает одну конечность. Ключевой вопрос здесь один - есть ли у руки КЛЮЧИЦА: цепочка из
	/// четырёх звеньев начинается с неё, из трёх - сразу с плеча. Различить их иначе нельзя, а
	/// ошибка сдвигает всю руку на слот и выглядит как «плечо назначено на предплечье».
	///
	/// У ноги обратная ситуация: лишнее звено приходит В КОНЦЕ (носок), поэтому список слотов у неё
	/// всегда один и тот же, а недостающие просто не заполняются.
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
	/// Собирает цепочку конечности от корневого звена вниз.
	///
	/// Спуск - по самой длинной ветви (у стопы есть носок, у кисти пальцы), но с ДВУМЯ
	/// ограничителями. Первый: остановка на кости, из которой расходятся три и более ветви, - это
	/// кисть с пальцами, и без остановки цепочка руки уезжала бы в мизинец. Второй: потолок в
	/// четыре звена - больше в humanoid-слоты не укладывается.
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
	/// Раскладывает цепочку по слотам ЯВНЫМ списком.
	///
	/// Явным, а не арифметикой «корневой слот плюс номер звена»: слоты идут в enum-е подряд только
	/// внутри одной конечности, и пятое звено руки при таком счёте писалось в первый слот СЛЕДУЮЩЕЙ
	/// (проверено: кость стопы приезжала в «ключицу R»). Ошибка выглядит как случайная разметка, а
	/// не как выход за границу.
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

	// --- Имена -------------------------------------------------------------------------------------

	/// <summary>
	/// Доразметка по именам: заполняет ТОЛЬКО пустые слоты. Именно только пустые - топология уже
	/// сказала своё слово, и переписывать её именем значит проиграть ровно там, где имена и врут.
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
					// Кратчайшее подходящее имя: у «neck» и «neck_twist_01» первое почти всегда и
					// есть настоящая кость, а второе - вспомогательная.
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
	/// Сторона по имени. Проверяются и слова целиком, и односимвольные маркеры в РАЗДЕЛИТЕЛЯХ
	/// (<c>_l</c>, <c>.r</c>, <c>l_</c>) - но именно в разделителях: голая буква «l» встречается в
	/// половине имён костей, и искать её где попало значит объявить левыми все кости рига.
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

	/// <summary>Префиксы экспортёров, которые снимаются ЦЕЛИКОМ, вместе со своим разделителем. Именно
	/// с разделителем: снимать голую букву «b» из <c>b_Head</c> и заодно из <c>ball</c> - это
	/// превратить «ball» в «all» и потерять носок.</summary>
	private static readonly string[] RawPrefixes =
		["mixamorig:", "mixamorig", "b_", "bone_", "def-", "def_", "org-", "org_", "bip01_", "bip_"];

	/// <summary>Имя без регистра, разделителей, цифр и префиксов экспортёров. Цифры выбрасываются
	/// намеренно: <c>Spine01</c>, <c>spine_1</c> и <c>Spine</c> - одна и та же кость, а нумерация
	/// звеньев всё равно восстанавливается топологией, а не именем.</summary>
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
