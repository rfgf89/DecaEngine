using System;
using System.Collections.Generic;
using System.Numerics;
using DecaEngine.Animation;
using DecaEngine.Core;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
using DecaEngine.Physics;
using DecaEngine.Scene;
using Friflo.Engine.ECS;

// В Friflo есть свой Transform-компонент, а поза скелета оперирует TRS движка - без явного алиаса
// имя разрешается неоднозначно.
using Transform = DecaEngine.Core.Transform;

namespace DecaEngine.Editor;

/// <summary>Процедурные добавки: foot IK по ступеням и пружинные кости. Часть <see cref="AnimationDriver"/> - файл на тему; состояние
/// персонажа (Character) и кадровый Update живут в основном файле.</summary>
public sealed partial class AnimationDriver
{
	/// <summary>
	/// Привязка стоп к полу. Идёт ПОСЛЕ look-at и ДО spring bones: look-at правит верх скелета и с
	/// ногами не пересекается, а вторичное движение обязано считаться по уже окончательной позе -
	/// иначе цепочка начинает каждый кадр от позы, которой в кадре не будет.
	///
	/// Молча выходит без физики или без нативного ozz: two-bone IK живёт в шиме, а луч - в мире.
	/// Это штатная деградация, и её видно в окне дебага счётчиком применённых ног.
	/// </summary>
	private void ApplyFootIk(Entity entity, Character character, float deltaSeconds)
	{
		if (character.Pose == null || Physics == null || !entity.HasComponent<FootIkComponent>())
		{
			character.LegsBuilt = false;
			character.Legs.Clear();
			return;
		}

		var settings = entity.GetComponent<FootIkComponent>();
		if (!settings.Enabled || settings.Weight <= 0f)
		{
			return;
		}

		if (!character.LegsBuilt || !SameLegSource(character.LegSource, settings))
		{
			BuildLegs(character, settings);
			character.LegSource = settings;
			character.LegsBuilt = true;
		}

		if (character.Legs.Count == 0)
		{
			return;
		}

		// Числовые ручки обновляются каждый кадр (их крутят ползунками во время проигрывания), а
		// состав ног - только при пересборке выше: пересборка роняет сглаживание, и стопа поехала бы
		// к земле заново на каждом кадре.
		character.IkSettings.Weight = Math.Clamp(settings.Weight, 0f, 1f);
		character.IkSettings.MaxPelvisDrop = settings.MaxPelvisDrop;
		character.IkSettings.Smoothing = settings.Smoothing;
		character.IkSettings.AlignToNormal = settings.AlignToNormal;
		character.IkSettings.LockFeet = settings.LockFeet;
		character.IkSettings.AlignBodyToSlope = settings.AlignBodyToSlope;

		foreach (var leg in character.Legs)
		{
			leg.AnkleHeight = settings.AnkleHeight;
		}

		var physics = Physics;

		character.IkApplied = FootIk.Solve(character.Pose, character.Skeleton, character.Legs,
			character.IkSettings, character.ModelToWorld, character.Locals, character.Models,
			(origin, direction, maximumT) => physics.SampleGround(origin, direction, maximumT),
			deltaSeconds);

		if (character.IkApplied)
		{
			character.Models.CopyTo(character.Managed.ModelMatrices, 0);
		}
	}

	/// <summary>
	/// Имя кости: заданное автором, а если оно пусто - из humanoid-разметки модели.
	///
	/// Приоритет у АВТОРА, и это не мелочь: разметка автоматическая, и на нестандартном риге она
	/// может ошибиться. Пустое поле означает «возьми из аватара», непустое - «я знаю лучше», и
	/// перебивать второе первым значило бы, что ручную настройку нельзя сделать в принципе.
	/// </summary>
	private static string JointOf(Character character, string authored, HumanoidBone slot) =>
		!string.IsNullOrEmpty(authored) ? authored : character.Avatar?[slot] ?? string.Empty;

	private static void BuildLegs(Character character, in FootIkComponent settings)
	{
		character.Legs.Clear();

		string pelvis = JointOf(character, settings.PelvisJoint, HumanoidBone.Hips);

		character.IkSettings.PelvisJoint = string.IsNullOrEmpty(pelvis)
			? -1
			: character.Skeleton.FindJoint(pelvis);

		// Дальность луча - в масштабе СКЕЛЕТА, а не в метрах: у лисы длина кости ~10 единиц, и луч
		// «полтора метра вниз» не дотянулся бы до пола, под которым она стоит. Это ровно тот случай,
		// когда абсолютная константа выглядит как «IK не работает».
		//
		// Старт - ВЫСОКО над стопой (3 длины кости; канонический сэмпл ozz берёт полметра): лапа на
		// ступенях и склонах уходит ВНУТРЬ рельефа, и луч, рождённый под поверхностью, пролетает
		// односторонний меш насквозь и находит «пол» этажом ниже - IK тянет ногу туда.
		character.IkSettings.ProbeUp = character.Scale * 3f;
		character.IkSettings.ProbeDown = character.Scale * 2f;

		// Носок - точка ОПОРЫ дигитиграда: у лисы «стопа» разметки - скакательный сустав, и без
		// носка IK щупал пол под суставом, висящим над землёй в стороне от места контакта.
		AddLeg(character,
			JointOf(character, settings.LeftUpperJoint, HumanoidBone.LeftUpperLeg),
			JointOf(character, settings.LeftLowerJoint, HumanoidBone.LeftLowerLeg),
			JointOf(character, settings.LeftFootJoint, HumanoidBone.LeftFoot),
			JointOf(character, settings.LeftToeJoint, HumanoidBone.LeftToes));

		AddLeg(character,
			JointOf(character, settings.RightUpperJoint, HumanoidBone.RightUpperLeg),
			JointOf(character, settings.RightLowerJoint, HumanoidBone.RightLowerLeg),
			JointOf(character, settings.RightFootJoint, HumanoidBone.RightFoot),
			JointOf(character, settings.RightToeJoint, HumanoidBone.RightToes),
			right: true);

		// Передние ноги четвероногого - из ARM-слотов разметки (лисе автомаппинг кладёт передние
		// ноги именно туда). Только по явной галочке: у двуногого те же слоты - его руки. Носок
		// передней ноги - только авторский: слота «пальцы кисти» в разметке нет.
		if (settings.FrontLegs)
		{
			AddLeg(character,
				JointOf(character, settings.FrontLeftUpperJoint, HumanoidBone.LeftUpperArm),
				JointOf(character, settings.FrontLeftLowerJoint, HumanoidBone.LeftLowerArm),
				JointOf(character, settings.FrontLeftFootJoint, HumanoidBone.LeftHand),
				settings.FrontLeftToeJoint,
				front: true);

			AddLeg(character,
				JointOf(character, settings.FrontRightUpperJoint, HumanoidBone.RightUpperArm),
				JointOf(character, settings.FrontRightLowerJoint, HumanoidBone.RightLowerArm),
				JointOf(character, settings.FrontRightFootJoint, HumanoidBone.RightHand),
				settings.FrontRightToeJoint,
				front: true, right: true);
		}
	}

	/// <summary>Добавляет ногу, если ВСЕ три кости нашлись. Частично настроенная нога не добавляется
	/// вовсе: two-bone IK с отсутствующим суставом - это не «чуть хуже», а обращение по индексу -1.
	/// Носок - ОПЦИОНАЛЬНЫЙ: без него точкой опоры служит сама стопа (двуногие).</summary>
	private static void AddLeg(Character character, string upper, string lower, string foot,
		string toe = "", bool front = false, bool right = false)
	{
		int upperJoint = character.Skeleton.FindJoint(upper ?? string.Empty);
		int lowerJoint = character.Skeleton.FindJoint(lower ?? string.Empty);
		int footJoint = character.Skeleton.FindJoint(foot ?? string.Empty);

		if (upperJoint < 0 || lowerJoint < 0 || footJoint < 0)
		{
			return;
		}

		character.Legs.Add(new FootIkLeg
		{
			UpperJoint = upperJoint,
			LowerJoint = lowerJoint,
			FootJoint = footJoint,
			ToeJoint = character.Skeleton.FindJoint(toe ?? string.Empty),
			Front = front,
			Right = right,
		});
	}

	private static bool SameLegSource(in FootIkComponent a, in FootIkComponent b) =>
		a.FrontLegs == b.FrontLegs &&
		string.Equals(a.PelvisJoint, b.PelvisJoint, StringComparison.Ordinal) &&
		string.Equals(a.LeftUpperJoint, b.LeftUpperJoint, StringComparison.Ordinal) &&
		string.Equals(a.LeftLowerJoint, b.LeftLowerJoint, StringComparison.Ordinal) &&
		string.Equals(a.LeftFootJoint, b.LeftFootJoint, StringComparison.Ordinal) &&
		string.Equals(a.RightUpperJoint, b.RightUpperJoint, StringComparison.Ordinal) &&
		string.Equals(a.RightLowerJoint, b.RightLowerJoint, StringComparison.Ordinal) &&
		string.Equals(a.RightFootJoint, b.RightFootJoint, StringComparison.Ordinal) &&
		string.Equals(a.FrontLeftUpperJoint, b.FrontLeftUpperJoint, StringComparison.Ordinal) &&
		string.Equals(a.FrontLeftLowerJoint, b.FrontLeftLowerJoint, StringComparison.Ordinal) &&
		string.Equals(a.FrontLeftFootJoint, b.FrontLeftFootJoint, StringComparison.Ordinal) &&
		string.Equals(a.FrontRightUpperJoint, b.FrontRightUpperJoint, StringComparison.Ordinal) &&
		string.Equals(a.FrontRightLowerJoint, b.FrontRightLowerJoint, StringComparison.Ordinal) &&
		string.Equals(a.FrontRightFootJoint, b.FrontRightFootJoint, StringComparison.Ordinal) &&
		string.Equals(a.LeftToeJoint, b.LeftToeJoint, StringComparison.Ordinal) &&
		string.Equals(a.RightToeJoint, b.RightToeJoint, StringComparison.Ordinal) &&
		string.Equals(a.FrontLeftToeJoint, b.FrontLeftToeJoint, StringComparison.Ordinal) &&
		string.Equals(a.FrontRightToeJoint, b.FrontRightToeJoint, StringComparison.Ordinal);

	// --- Spring bones ------------------------------------------------------------------------------

	private static void ApplySpringBones(Entity entity, Character character, float deltaSeconds)
	{
		if (!entity.HasComponent<SpringBoneComponent>())
		{
			character.ChainsBuilt = false;
			character.Chains.Clear();
			return;
		}

		var settings = entity.GetComponent<SpringBoneComponent>();
		if (!settings.Enabled || string.IsNullOrEmpty(settings.RootJoint) || settings.Length < 2)
		{
			return;
		}

		if (!character.ChainsBuilt || !SameChainSource(character.ChainSource, settings))
		{
			character.Chains.Clear();

			var joints = BuildChain(character.Skeleton, settings.RootJoint, settings.Length);
			if (joints.Length >= 2)
			{
				character.Chains.Add(new SpringBoneChain { Joints = joints });
			}

			character.ChainSource = settings;
			character.ChainsBuilt = true;
		}

		foreach (var chain in character.Chains)
		{
			// Числовые параметры обновляются каждый кадр (их крутят ползунками прямо во время
			// проигрывания), а вот СОСТАВ цепочки - только при пересборке выше, иначе инерция
			// сбрасывалась бы на каждом кадре.
			chain.Stiffness = settings.Stiffness;
			chain.Drag = settings.Drag;
			chain.TailLength = settings.TailLength;
			chain.Gravity = settings.Gravity;
		}

		SpringBones.Solve(character.Skeleton, character.Chains, character.Locals, character.Models, deltaSeconds);
		character.Models.CopyTo(character.Managed.ModelMatrices, 0);
	}

	/// <summary>Совпадает ли СОСТАВ цепочки (корень и длина). Числовые параметры сюда не входят
	/// намеренно - их правка не должна ронять инерцию.</summary>
	private static bool SameChainSource(in SpringBoneComponent a, in SpringBoneComponent b) =>
		string.Equals(a.RootJoint, b.RootJoint, StringComparison.Ordinal) && a.Length == b.Length;

	/// <summary>
	/// Собирает цепочку от корневой кости вниз ПО ПЕРВОМУ РЕБЁНКУ. Первый ребёнок, а не «все дети»:
	/// цепочка вторичного движения по определению линейна, а у кости с развилкой (основание хвоста,
	/// от которого отходят ещё и ноги) взять всех детей значило бы утащить в цепочку пол-скелета.
	/// </summary>
	private static int[] BuildChain(PreparedSkeleton skeleton, string rootName, int length)
	{
		int root = skeleton.FindJoint(rootName);
		if (root < 0)
		{
			return [];
		}

		var chain = new List<int> { root };
		int current = root;

		while (chain.Count < length)
		{
			int child = FirstChild(skeleton, current);
			if (child < 0)
			{
				break;
			}

			chain.Add(child);
			current = child;
		}

		return chain.ToArray();
	}

	/// <summary>Первый ребёнок джойнта, -1 если лист. Джойнты топологически упорядочены, поэтому
	/// дети лежат ПОСЛЕ родителя, и первый найденный - он и есть первый ребёнок.</summary>
	private static int FirstChild(PreparedSkeleton skeleton, int joint)
	{
		for (int i = joint + 1; i < skeleton.JointCount; i++)
		{
			if (skeleton.Parents[i] == joint)
			{
				return i;
			}
		}

		return -1;
	}

	// --- Рэгдолл -----------------------------------------------------------------------------------

}
