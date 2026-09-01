using System;
using System.Numerics;
using DecaEngine.Core;

namespace DecaEngine.Animation;

/// <summary>Насколько поза похожа на T: по каждой конечности - отклонение её направления от
/// ожидаемого, в градусах. Числа, а не «да/нет»: поза почти никогда не идеальна, и решать, годится
/// ли она, должен человек, глядя на величину промаха.</summary>
public readonly record struct HumanoidPoseReport(
	float LeftArmDegrees,
	float RightArmDegrees,
	float LeftLegDegrees,
	float RightLegDegrees,
	bool Complete)
{
	public float Worst => MathF.Max(
		MathF.Max(LeftArmDegrees, RightArmDegrees),
		MathF.Max(LeftLegDegrees, RightLegDegrees));

	/// <summary>Порог «это T-поза». 25 градусов - не строгость ради строгости: A-поза отличается от
	/// T примерно на 45, и порог обязан их различать, оставляя запас на риги, у которых руки чуть
	/// опущены по замыслу.</summary>
	public bool LooksLikeTPose => Complete && Worst <= 25f;
}

/// <summary>
/// Снятие и проверка референсной позы аватара (см. <see cref="HumanoidAvatar.ReferenceLocals"/>).
///
/// Референсная поза - это НЕ bind-поза модели. Совпадать они могут, но требовать этого нельзя:
/// экспортируют модели в чём угодно, включая A-позу и позу с согнутыми руками, и bind-поза в таком
/// риге - произвольное состояние, а не общая точка отсчёта. Поэтому её СНИМАЮТ явно: автор ставит
/// персонажа в T и нажимает кнопку.
/// </summary>
public static class HumanoidReferencePose
{
	/// <summary>Снимает референсную позу из текущих локальных TRS. Ключ - ИМЯ кости: индексы
	/// разъезжаются при переэкспорте, а разметка обязана переживать его.</summary>
	public static void Capture(HumanoidAvatar avatar, PreparedSkeleton skeleton, Transform[] locals)
	{
		if (avatar == null || skeleton == null || locals == null)
		{
			return;
		}

		avatar.ReferenceLocals.Clear();

		int count = Math.Min(skeleton.JointCount, locals.Length);
		for (int i = 0; i < count; i++)
		{
			avatar.ReferenceLocals[skeleton.JointNames[i]] = locals[i];
		}
	}

	/// <summary>Снимает референсную позу из BIND-позы скелета - разумная отправная точка, когда
	/// модель уже экспортирована в T.</summary>
	public static void CaptureFromBind(HumanoidAvatar avatar, PreparedSkeleton skeleton) =>
		Capture(avatar, skeleton, skeleton?.BindLocals ?? []);

	/// <summary>
	/// Оценивает референсную позу: насколько руки лежат вдоль X, а ноги вдоль -Y.
	///
	/// Проверяется НАПРАВЛЕНИЕ КОНЕЧНОСТИ (от плеча к кисти, от бедра к стопе), а не повороты
	/// отдельных костей: именно оно определяет позу, и именно оно не зависит от того, как автор рига
	/// сориентировал локальные оси. Проверка по осям костей ловила бы не позу, а соглашение
	/// экспортёра.
	/// </summary>
	public static HumanoidPoseReport Evaluate(HumanoidAvatar avatar, PreparedSkeleton skeleton)
	{
		if (avatar == null || skeleton == null || !avatar.HasReferencePose)
		{
			return new HumanoidPoseReport(0f, 0f, 0f, 0f, Complete: false);
		}

		var models = BuildModelMatrices(avatar, skeleton);
		if (models == null)
		{
			return new HumanoidPoseReport(0f, 0f, 0f, 0f, Complete: false);
		}

		// Левая сторона - вдоль +X, правая - вдоль -X (соглашение движка, см.
		// HumanoidAutoMap.AssignSides), ноги - вниз.
		bool complete =
			TryDirection(avatar, skeleton, models, HumanoidBone.LeftUpperArm, HumanoidBone.LeftHand, out var leftArm) &
			TryDirection(avatar, skeleton, models, HumanoidBone.RightUpperArm, HumanoidBone.RightHand, out var rightArm) &
			TryDirection(avatar, skeleton, models, HumanoidBone.LeftUpperLeg, HumanoidBone.LeftFoot, out var leftLeg) &
			TryDirection(avatar, skeleton, models, HumanoidBone.RightUpperLeg, HumanoidBone.RightFoot, out var rightLeg);

		return new HumanoidPoseReport(
			Angle(leftArm, Vector3.UnitX),
			Angle(rightArm, -Vector3.UnitX),
			Angle(leftLeg, -Vector3.UnitY),
			Angle(rightLeg, -Vector3.UnitY),
			complete);
	}

	/// <summary>Модельные матрицы референсной позы. Кость, которой в позе нет, берётся из bind-позы:
	/// референс мог быть снят с рига до переэкспорта, и обрывать из-за одной новой кости всю оценку
	/// незачем.</summary>
	public static Matrix4x4[]? BuildModelMatrices(HumanoidAvatar avatar, PreparedSkeleton skeleton)
	{
		if (avatar == null || skeleton == null || skeleton.JointCount == 0)
		{
			return null;
		}

		var models = new Matrix4x4[skeleton.JointCount];

		for (int i = 0; i < skeleton.JointCount; i++)
		{
			if (!avatar.ReferenceLocals.TryGetValue(skeleton.JointNames[i], out var local))
			{
				local = skeleton.BindLocals[i];
			}

			var matrix = Matrix4x4.CreateScale(local.scale) *
				Matrix4x4.CreateFromQuaternion(local.rotation) *
				Matrix4x4.CreateTranslation(local.position);

			int parent = skeleton.Parents[i];
			models[i] = parent >= 0 ? matrix * models[parent] : matrix;
		}

		return models;
	}

	private static bool TryDirection(HumanoidAvatar avatar, PreparedSkeleton skeleton, Matrix4x4[] models,
		HumanoidBone from, HumanoidBone to, out Vector3 direction)
	{
		direction = Vector3.Zero;

		if (!avatar.IsAssigned(from) || !avatar.IsAssigned(to))
		{
			return false;
		}

		int fromJoint = skeleton.FindJoint(avatar[from]);
		int toJoint = skeleton.FindJoint(avatar[to]);

		if (fromJoint < 0 || toJoint < 0)
		{
			return false;
		}

		var delta = models[toJoint].Translation - models[fromJoint].Translation;
		if (delta.LengthSquared() < 1e-10f)
		{
			return false;
		}

		direction = Vector3.Normalize(delta);
		return true;
	}

	private static float Angle(Vector3 direction, Vector3 expected)
	{
		if (direction.LengthSquared() < 1e-10f)
		{
			return 180f;
		}

		float dot = Math.Clamp(Vector3.Dot(direction, expected), -1f, 1f);
		return MathF.Acos(dot) * (180f / MathF.PI);
	}
}
