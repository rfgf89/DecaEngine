using System;
using System.Collections.Generic;
using System.Numerics;
using SharpGLTF.Schema2;
using DecaEngine.Animation;
using DecaEngine.Core;

namespace DecaEngine.Graphics;

/// <summary>
/// Разбор скелета, скин-весов и анимационных клипов glTF (см. <see cref="SkinningData"/>-типы).
/// Вынесен из <see cref="ModelLoader"/> отдельным файлом сознательно: это единственное место, где
/// правосторонняя система glTF переводится в левостороннюю для ИЕРАРХИИ, и держать конверсию
/// кучкой проще, чем искать её среди трёх тысяч строк загрузчика.
///
/// SharpGLTF НЕ потокобезопасен, поэтому всё здесь обязано звучать на том же потоке, что и
/// остальное чтение документа (фоновая фаза PrepareModel до Parallel.For).
/// </summary>
/// <remarks>Публичный, а не internal: скелет модели нужен и СНАРУЖИ графического слоя - генератору
/// демо-сцены, чтобы разметить humanoid-аватар без загрузки модели на GPU (см.
/// SamplePrefabBuilder.WriteFoxAvatar). Полная загрузка ради одного скелета тянула бы за собой
/// устройство, которого у генератора нет вовсе.</remarks>
public static class SkinningImport
{
	/// <summary>
	/// Сопряжение матрицы отражением по Z: M' = S * M * S, где S = diag(1,1,-1,1). Ровно та же
	/// конверсия правой системы в левую, что <c>Position.Z = -Z</c> для вершин, только для полной
	/// трансформации - в результате меняют знак ТОЛЬКО элементы, у которых ровно один из индексов
	/// (строка/столбец) равен Z. Без сопряжения обратные bind-матрицы остаются в правой системе, и
	/// скиннинг зеркалит персонажа относительно зеркалированной же геометрии - руки и ноги
	/// меняются местами.
	/// </summary>
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

	/// <summary>Та же конверсия для кватерниона: отражение по Z сопрягает поворот в (-x,-y,z,w)
	/// (зеркалятся компоненты оси, перпендикулярные плоскости отражения). Совпадает с тем, что
	/// делает ModelLoader с поворотами узлов.</summary>
	public static Quaternion MirrorZ(Quaternion q) => new(-q.X, -q.Y, q.Z, q.W);

	public static Vector3 MirrorZ(Vector3 v) => new(v.X, v.Y, -v.Z);

	/// <summary>
	/// Собирает ЕДИНЫЙ скелет модели: объединение узлов-джойнтов всех скинов документа плюс все их
	/// предки до корня сцены. Предки обязательны, даже если сами джойнтами не являются: их локальные
	/// трансформации входят в модельную матрицу джойнта, и без них скелет разъезжается по сцене.
	/// Один скелет на модель, а не по скелету на скин, - потому что скины персонажа (тело, одежда,
	/// волосы) почти всегда сидят на одной иерархии, и разводить их значило бы считать одну и ту же
	/// позу несколько раз.
	///
	/// Возвращает null, если в документе нет ни одного скина - модель статическая.
	/// </summary>
	/// <param name="nodeToJoint">Заполняется отображением LogicalIndex узла glTF -> индекс джойнта.</param>
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
					// Цепочка предков уже собрана - выше по ней идти незачем (типичный риг: 200
					// джойнтов с общим корнем, без выхода из цикла это 200 полных подъёмов).
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

		// Топологический порядок через глубину: родитель всегда строго мельче ребёнка, поэтому
		// сортировка по глубине гарантирует контракт PreparedSkeleton.Parents (родитель раньше).
		// Вторичный ключ - LogicalIndex, чтобы порядок был детерминированным между запусками:
		// иначе .dmdl-кеш и индексы костей в настройках IK/рэгдолла плыли бы от сборки к сборке.
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

			// GetDecomposed обязателен: узел glTF может задавать трансформ МАТРИЦЕЙ, а не TRS
			// (CesiumMan), и у такого AffineTransform прямое чтение Rotation бросает
			// InvalidOperationException. Для TRS-узлов разложение тождественно.
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

	/// <summary>
	/// Обратные bind-матрицы: для джойнтов, входящих в какой-нибудь скин, берутся АВТОРСКИЕ из glTF
	/// (mirror-Z-сопряжённые) - они точнее пересчёта из bind-позы и учитывают случаи, когда bind-поза
	/// узлов и та, под которую пекли веса, различаются. Для промежуточных узлов иерархии, ни в один
	/// скин не входящих, авторской матрицы нет вовсе, и она честно считается из bind-позы: скиннинг
	/// их не использует, но процедурный слой (IK, рэгдолл) работает с полным скелетом.
	/// </summary>
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

		// Модельные матрицы bind-позы - одним проходом: массив топологически упорядочен, поэтому
		// родитель к моменту обработки ребёнка уже посчитан.
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

	/// <summary>
	/// Скин-стрим примитива: JOINTS_0/WEIGHTS_0, переведённые из локальных индексов скина в индексы
	/// джойнтов скелета. Возвращает null, если примитив не скиннится, - это признак того, что меш
	/// остаётся на статическом пути (см. <see cref="SkinVertex"/> о том, почему стрим отдельный).
	///
	/// Второй набор влияний (JOINTS_1/WEIGHTS_1, до восьми костей на вершину) осознанно ОТБРАСЫВАЕТСЯ
	/// с оставлением четырёх самых весомых и перенормировкой: восьмивлиятельные риги встречаются
	/// редко, а стоят вдвое дороже и в памяти, и в compute-скиннинге. Вклад отброшенных костей у
	/// таких вершин почти всегда доли процента.
	/// </summary>
	public static SkinVertex[] ReadSkinVertices(MeshPrimitive primitive, Skin skin,
		Dictionary<int, int> nodeToJoint, int vertexCount)
	{
		var jointsAccessor = primitive.GetVertexAccessor("JOINTS_0");
		var weightsAccessor = primitive.GetVertexAccessor("WEIGHTS_0");

		if (skin == null || jointsAccessor == null || weightsAccessor == null)
		{
			return null;
		}

		// Локальный индекс скина -> индекс джойнта скелета. Своя таблица на скин: у второго скина
		// той же модели те же локальные индексы означают другие кости.
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

	/// <summary>Заносит четвёрку (индекс, вес) в набор четырёх сильнейших влияний, вытесняя самое
	/// слабое. Нулевые веса игнорируются: glTF заполняет неиспользуемые слоты нулём, а джойнт в них
	/// оставляет мусорный - без проверки мусор вытеснил бы настоящее влияние.</summary>
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

	/// <summary>
	/// Нормализует веса и пакует их в <see cref="SkinVertex"/>. Остаток от округления к unorm16
	/// сбрасывается в самый весомый слот, чтобы сумма была РОВНО <see cref="SkinVertex.WeightScale"/>:
	/// иначе накопленная ошибка округления масштабирует вершину, и на крупных планах персонаж
	/// заметно «дышит».
	///
	/// Вершина без единого влияния (экспортёр приложил скин не ко всем вершинам) прибивается к
	/// джойнту 0 с весом 1: нулевые веса в compute-скиннинге схлопнули бы её в начало координат
	/// длинным лучом через всю сцену.
	/// </summary>
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

	/// <summary>
	/// Клипы документа, разложенные по джойнтам скелета. Ключи берутся СЫРЫМИ (см.
	/// <see cref="PreparedAnimation"/> о том, почему без ресемплинга); CUBICSPLINE-каналы читаются
	/// как линейные по значениям в узлах - тангенсы отбрасываются. Это осознанная потеря: честная
	/// поддержка кубики требует своего пути и в семплере, и в ozz-конвертере, а встречается она
	/// почти только в физически-точных технических анимациях, не в персонажных.
	/// </summary>
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
					// Канал на узел вне скелета (анимация камеры, света, статического реквизита):
					// скелетному клипу он не принадлежит и молча пропускается.
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

			// Клип, не задевший скелет ни одним каналом (морфы, анимация света), в список не идёт -
			// иначе в UI аниматора висели бы пустые записи, ничего не делающие при выборе.
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
