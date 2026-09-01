using System.Numerics;

namespace DecaEngine.Graphics;

/// <summary>
/// Скиннинг-атрибуты одной вершины: до четырёх джойнтов с весами. Лежит ОТДЕЛЬНЫМ стримом,
/// параллельным <see cref="Vertex"/>, а не полями внутри неё - скиннед-мешей в сцене единицы, а
/// вершин у статики миллионы, и +16 байт на каждую вершину Sponza стоили бы десятки мегабайт
/// впустую. Стрим существует только у мешей, у которых glTF-примитив нёс JOINTS_0/WEIGHTS_0.
///
/// Упаковка (16 байт): индексы - ushort (скелеты за 65535 костей не бывают, а byte упирается в
/// 256 и ломает крупные персонажные риги), веса - unorm16. Веса НОРМАЛИЗОВАНЫ при импорте: сумма
/// ровно <see cref="WeightScale"/>, иначе скиннинг тихо масштабирует вершины - экспортёры
/// регулярно отдают сумму 0.999 или 1.001.
/// </summary>
public struct SkinVertex
{
	/// <summary>Знаменатель весов: вес w во float = W? / <see cref="WeightScale"/>.</summary>
	public const float WeightScale = 65535f;

	/// <summary>Максимум влияний на вершину. Зеркалится в SkinningCS.hlsl - менять только парой.</summary>
	public const int MaxInfluences = 4;

	public ushort J0, J1, J2, J3;
	public ushort W0, W1, W2, W3;

	public readonly bool IsUnskinned => W0 == 0 && W1 == 0 && W2 == 0 && W3 == 0;
}

/// <summary>
/// Скелет модели: плоский массив джойнтов, отсортированный ТОПОЛОГИЧЕСКИ (родитель всегда раньше
/// ребёнка). Порядок - не деталь реализации, а контракт: и расчёт модельных матриц одним проходом
/// по массиву, и ozz-скелет требуют именно его; без него пришлось бы обходить дерево рекурсивно
/// каждый кадр.
///
/// Пространство - уже ЛЕВОСТОРОННЕЕ движка (см. <see cref="SkinningImport.MirrorZ"/>): и bind-поза,
/// и обратные bind-матрицы, и все клипы конвертируются на импорте, чтобы в рантайме не осталось ни
/// одного места, где нужно помнить про исходную правостороннюю систему glTF.
/// </summary>
public sealed class PreparedSkeleton
{
	/// <summary>Имена джойнтов - по ним ищутся кости для IK, рэгдолла и spring bones.</summary>
	public string[] JointNames = [];

	/// <summary>Индекс родителя каждого джойнта, -1 у корня. Всегда меньше индекса самого джойнта.</summary>
	public int[] Parents = [];

	/// <summary>Локальная TRS каждого джойнта в bind-позе (она же поза по умолчанию, если клип не
	/// анимирует канал).</summary>
	public Transform[] BindLocals = [];

	/// <summary>Обратная bind-матрица: модельное пространство -> пространство джойнта. Источник -
	/// glTF inverseBindMatrices скина; для джойнтов, в скин не входящих (они всё равно нужны в
	/// иерархии как промежуточные узлы), считается из bind-позы.</summary>
	public Matrix4x4[] InverseBind = [];

	public int JointCount => Parents.Length;

	/// <summary>Индекс джойнта по имени, -1 если нет. Линейный поиск осознанно: зовётся на настройке
	/// (привязка IK-целей, сборка рэгдолла), а не в кадре, и словарь на 50-200 костей не окупается.</summary>
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

/// <summary>
/// Один анимационный клип: по дорожке на джойнт скелета. Дорожки хранят СЫРЫЕ ключи glTF с их
/// собственными временами - без ресемплинга в фиксированную частоту. Ресемплинг удобнее для
/// семплера, но он же необратимо портит редкие ключи (кадр-в-кадр анимация камеры) и раздувает
/// клипы с длинными статичными участками; ozz на своей стороне всё равно перепакует клип в
/// собственный сжатый формат, и подавать ему уже испорченные данные незачем.
/// </summary>
public sealed class PreparedAnimation
{
	public string Name = string.Empty;

	/// <summary>Длительность клипа в секундах = максимальное время ключа по всем дорожкам.</summary>
	public float Duration;

	/// <summary>По дорожке на джойнт скелета, индексация совпадает с <see cref="PreparedSkeleton"/>.
	/// Дорожка джойнта, которого клип не трогает, пустая - семплер берёт bind-позу.</summary>
	public JointTrack[] Tracks = [];
}

/// <summary>Ключи одного джойнта в клипе. Каналы независимы: glTF позволяет анимировать только
/// поворот, оставив трансляцию и масштаб из bind-позы, и это самый частый случай.</summary>
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
