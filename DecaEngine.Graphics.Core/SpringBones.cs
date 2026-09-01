using System;
using System.Collections.Generic;
using System.Numerics;

namespace DecaEngine.Graphics;

/// <summary>
/// Цепочка костей вторичного движения: хвост, коса, полы плаща, подвески. Кости идут ОТ КОРНЯ К
/// КОНЧИКУ, каждая следующая - непосредственный ребёнок предыдущей; солвер проходит цепочку в этом
/// порядке и пересчитывает модельные матрицы по ходу, поэтому разрыв в родстве тихо испортил бы
/// всё, что ниже него.
/// </summary>
public sealed class SpringBoneChain
{
	public int[] Joints = [];

	/// <summary>Насколько сильно кость тянет обратно к позе анимации за шаг, 0..1. Ноль - кость
	/// живёт только инерцией и никогда не возвращается; единица - вторичного движения нет вовсе.</summary>
	public float Stiffness = 0.08f;

	/// <summary>Потеря скорости за шаг, 0..1. Без неё цепочка колеблется вечно.</summary>
	public float Drag = 0.2f;

	/// <summary>Внешняя сила в пространстве МОДЕЛИ (обычно гравитация). Задаётся в пространстве
	/// модели, а не мира, потому что и вся поза считается в нём: переводить силу в мир значило бы
	/// тащить сюда трансформ сущности ради одного вектора.</summary>
	public Vector3 Gravity = Vector3.Zero;

	/// <summary>Длина «хвостовой» кости - у последнего джойнта цепочки нет ребёнка, задающего
	/// направление и длину. Ноль означает, что последний джойнт не колышется.</summary>
	public float TailLength;

	// Положения кончиков костей в пространстве модели: текущее и прошлого шага. Верле, а не пара
	// (позиция, скорость): при рывке корня (телепорт персонажа) верле сам гасит вылет, потому что
	// скорость в нём не хранится отдельно и не переживает подмену позиции.
	internal Vector3[] Tips = [];
	internal Vector3[] PreviousTips = [];
	internal bool Initialized;

	/// <summary>Сбрасывает симуляцию к текущей позе. Обязателен при телепорте персонажа: иначе
	/// цепочка «догоняет» его через полкарты, растягиваясь в струну.</summary>
	public void Reset() => Initialized = false;
}

/// <summary>
/// Солвер вторичного движения. Работает ПОСЛЕ всего остального (анимация, блендинг, IK) и правит
/// только локальные повороты костей цепочек - именно поэтому он последний: любой шаг, пересчитывающий
/// позу из клипа, стёр бы его результат.
/// </summary>
public static class SpringBones
{
	/// <summary>
	/// Считает шаг симуляции для всех цепочек и правит <paramref name="locals"/>.
	///
	/// <paramref name="models"/> ОБЯЗАН быть актуален на входе и пересчитывается по ходу для костей
	/// цепочек: каждая следующая кость висит на уже поправленной предыдущей, и брать для неё
	/// доанимационную матрицу родителя значило бы считать колебание вокруг положения, в котором
	/// кость уже не находится.
	/// </summary>
	public static void Solve(PreparedSkeleton skeleton, IReadOnlyList<SpringBoneChain> chains,
		Transform[] locals, Matrix4x4[] models, float deltaSeconds)
	{
		if (skeleton == null || chains == null || locals == null || models == null || deltaSeconds <= 0f)
		{
			return;
		}

		foreach (var chain in chains)
		{
			SolveChain(skeleton, chain, locals, models, deltaSeconds);
		}
	}

	private static void SolveChain(PreparedSkeleton skeleton, SpringBoneChain chain, Transform[] locals,
		Matrix4x4[] models, float deltaSeconds)
	{
		int count = chain.Joints.Length;
		if (count == 0)
		{
			return;
		}

		if (!chain.Initialized || chain.Tips.Length != count)
		{
			chain.Tips = new Vector3[count];
			chain.PreviousTips = new Vector3[count];

			for (int i = 0; i < count; i++)
			{
				chain.Tips[i] = AnimatedTip(skeleton, chain, models, i);
				chain.PreviousTips[i] = chain.Tips[i];
			}

			chain.Initialized = true;
			return;
		}

		for (int i = 0; i < count; i++)
		{
			int joint = chain.Joints[i];
			var head = models[joint].Translation;
			var animatedTip = AnimatedTip(skeleton, chain, models, i);

			float length = Vector3.Distance(head, animatedTip);
			if (length < 1e-5f)
			{
				// Кость нулевой длины направления не задаёт - крутить вокруг неё нечего. Такое
				// бывает у служебных узлов, случайно попавших в цепочку.
				chain.Tips[i] = animatedTip;
				chain.PreviousTips[i] = animatedTip;
				continue;
			}

			// Верле: инерция + внешняя сила. Шаг в квадрате у силы - не педантизм, а условие того,
			// что при смене частоты симуляции траектория остаётся той же.
			var inertia = (chain.Tips[i] - chain.PreviousTips[i]) * (1f - chain.Drag);
			var next = chain.Tips[i] + inertia + chain.Gravity * (deltaSeconds * deltaSeconds);

			// Возврат к позе анимации.
			next = Vector3.Lerp(next, animatedTip, Math.Clamp(chain.Stiffness, 0f, 1f));

			// Жёсткая связь: кончик обязан остаться на своём расстоянии от головы кости, иначе
			// цепочка растягивается и рвётся визуально.
			var direction = next - head;
			float distance = direction.Length();
			next = distance > 1e-5f ? head + direction * (length / distance) : animatedTip;

			chain.PreviousTips[i] = chain.Tips[i];
			chain.Tips[i] = next;

			ApplyRotation(skeleton, locals, models, joint, head, animatedTip, next);

			// Модельные матрицы ниже по цепочке пересчитываются от поправленной кости - см. шапку.
			RefreshDescendants(skeleton, chain, locals, models, i);
		}
	}

	/// <summary>Положение кончика кости в позе анимации: голова следующей кости цепочки, а для
	/// последней - точка на её собственной оси в <see cref="SpringBoneChain.TailLength"/>.</summary>
	private static Vector3 AnimatedTip(PreparedSkeleton skeleton, SpringBoneChain chain, Matrix4x4[] models, int index)
	{
		int joint = chain.Joints[index];

		if (index + 1 < chain.Joints.Length)
		{
			return models[chain.Joints[index + 1]].Translation;
		}

		// Ось кости - направление, в котором от неё рос бы ребёнок. Берём локальную трансляцию
		// самой кости как приближение направления роста: у костей цепочки (хвост, коса) она
		// сонаправлена с самой цепочкой.
		var axis = skeleton.BindLocals[joint].position;
		if (axis.LengthSquared() < 1e-10f)
		{
			axis = Vector3.UnitY;
		}

		axis = Vector3.Normalize(axis);
		var model = models[joint];
		var direction = Vector3.TransformNormal(axis, model);

		return model.Translation + Vector3.Normalize(direction) * chain.TailLength;
	}

	/// <summary>Доворачивает кость так, чтобы её кончик смотрел из головы в новую точку. Коррекция
	/// считается в модельном пространстве и переводится в локальное через поворот РОДИТЕЛЯ - иначе
	/// она применилась бы поверх собственного поворота кости дважды.</summary>
	private static void ApplyRotation(PreparedSkeleton skeleton, Transform[] locals, Matrix4x4[] models,
		int joint, Vector3 head, Vector3 animatedTip, Vector3 newTip)
	{
		var from = animatedTip - head;
		var to = newTip - head;

		if (from.LengthSquared() < 1e-10f || to.LengthSquared() < 1e-10f)
		{
			return;
		}

		var correction = FromToRotation(Vector3.Normalize(from), Vector3.Normalize(to));

		int parent = skeleton.Parents[joint];
		var parentRotation = parent >= 0 ? RotationOf(models[parent]) : Quaternion.Identity;

		// Модельный поворот кости = локальный, домноженный на родительский; коррекция накладывается
		// в модельном пространстве СЛЕВА от него, а обратно в локальное снимается родительским.
		var modelRotation = correction * (locals[joint].rotation * parentRotation);
		locals[joint].rotation = Quaternion.Normalize(modelRotation * Quaternion.Inverse(parentRotation));

		models[joint] = Compose(locals[joint], parent >= 0 ? models[parent] : Matrix4x4.Identity);
	}

	/// <summary>Пересчитывает модельные матрицы костей цепочки НИЖЕ поправленной. Только их, а не
	/// всего скелета: цепочки вторичного движения - листья, и полный пересчёт был бы работой впустую
	/// на каждой кости каждой цепочки.</summary>
	private static void RefreshDescendants(PreparedSkeleton skeleton, SpringBoneChain chain, Transform[] locals,
		Matrix4x4[] models, int index)
	{
		for (int i = index + 1; i < chain.Joints.Length; i++)
		{
			int joint = chain.Joints[i];
			int parent = skeleton.Parents[joint];
			models[joint] = Compose(locals[joint], parent >= 0 ? models[parent] : Matrix4x4.Identity);
		}
	}

	private static Matrix4x4 Compose(in Transform local, in Matrix4x4 parent) =>
		Matrix4x4.CreateScale(local.scale)
		* Matrix4x4.CreateFromQuaternion(local.rotation)
		* Matrix4x4.CreateTranslation(local.position)
		* parent;

	/// <summary>Поворот матрицы без масштаба. Строки нормализуются: у костей с масштабом
	/// CreateFromRotationMatrix на ненормализованной матрице даёт кватернион с мусорной нормой.</summary>
	private static Quaternion RotationOf(in Matrix4x4 matrix)
	{
		var normalized = matrix;
		normalized.Translation = Vector3.Zero;

		var x = Vector3.Normalize(new Vector3(normalized.M11, normalized.M12, normalized.M13));
		var y = Vector3.Normalize(new Vector3(normalized.M21, normalized.M22, normalized.M23));
		var z = Vector3.Normalize(new Vector3(normalized.M31, normalized.M32, normalized.M33));

		normalized.M11 = x.X; normalized.M12 = x.Y; normalized.M13 = x.Z;
		normalized.M21 = y.X; normalized.M22 = y.Y; normalized.M23 = y.Z;
		normalized.M31 = z.X; normalized.M32 = z.Y; normalized.M33 = z.Z;

		return Quaternion.CreateFromRotationMatrix(normalized);
	}

	/// <summary>Кратчайший поворот из одного единичного вектора в другой. Отдельно обрабатывается
	/// противоположное направление: ось поворота там вырождается в ноль, и наивная формула даёт
	/// нулевой кватернион вместо разворота на 180°.</summary>
	private static Quaternion FromToRotation(Vector3 from, Vector3 to)
	{
		float dot = Vector3.Dot(from, to);

		if (dot > 0.999999f)
		{
			return Quaternion.Identity;
		}

		if (dot < -0.999999f)
		{
			var axis = Vector3.Cross(Vector3.UnitX, from);
			if (axis.LengthSquared() < 1e-8f)
			{
				axis = Vector3.Cross(Vector3.UnitY, from);
			}

			return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
		}

		var cross = Vector3.Cross(from, to);
		return Quaternion.Normalize(new Quaternion(cross, 1f + dot));
	}
}
