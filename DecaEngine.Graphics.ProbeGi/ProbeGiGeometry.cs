using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Core;
using DecaEngine.Graphics.Diligent;
using UnsafeCollections.Collections.Native;
using UnsafeCollections.Collections.Unsafe;

namespace DecaEngine.Graphics.ProbeGi;


/// <summary>Узел BVH в раскладке под StructuredBuffer - обязан совпадать байт-в-байт с BvhNode в
/// SceneTrace.hlsl. Паддинг явный: полагаться на то, как компилятор шейдеров разложит float3 в
/// структурированном буфере, нельзя.</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct BvhNodeGpu
{
	public Vector3 BoundsMin;

	/// <summary>&lt; 0 - лист (Start/Count задают срез в порядке треугольников), иначе индекс левого
	/// ребёнка; правый лежит в Start (см. ProbeGiBaker.Node).</summary>
	public int Left;

	public Vector3 BoundsMax;
	public int Start;
	public int Count;
	public int Pad0, Pad1, Pad2;
}

/// <summary>Треугольник сцены под StructuredBuffer - зеркало BvhTriangle в SceneTrace.hlsl.</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct BvhTriangleGpu
{
	public Vector3 A;

	/// <summary>UV вершины A, два half-а в битах float (см. <see cref="PackUv"/>). Бывшие паддинги
	/// float3-полей: размер структуры не изменился, старые потребители читают их как мусорные
	/// биты паддинга. Заполняется ТОЛЬКО у объектной геометрии аппаратного пути (текстуры в хите
	/// RT-отражений); мировая похлёбка программного пути оставляет нули.</summary>
	public float UvA;
	public Vector3 E1;

	/// <summary>UV вершины A+E1 (упаковка как <see cref="UvA"/>).</summary>
	public float UvB;
	public Vector3 E2;

	/// <summary>UV вершины A+E2 (упаковка как <see cref="UvA"/>).</summary>
	public float UvC;

	/// <summary>Линейное альбедо для отскока - трассировка на GPU возвращает его сразу, чтобы
	/// вызывающему не пришлось лезть за материалом.</summary>
	public Vector3 Albedo;

	/// <summary>Металличность (B-канал MR-текстуры в центроиде UV x MetallicFactor, см.
	/// ModelLoader.TriangleMetalness) - детект металла у RT-хита для «зеркала в зеркале»:
	/// светлый хром по одному альбедо неотличим от штукатурки. Бывший паддинг; мировая
	/// похлёбка программного пути оставляет ноль.</summary>
	public float Metalness;

	/// <summary>Вершинные нормали (A, A+E1, A+E2) окто-кодированными парами half-ов - сглаженный
	/// шейдинг RT-хитов (геометрическая нормаль cross(e1,e2) давала фасетки на плотных сферах:
	/// «нет смешивания между вершинами»). Объектное пространство; перенос в мир - той же
	/// матрицей рёбер (равномерность масштаба у отражений приемлема). Заполняются только у
	/// объектной геометрии аппаратного пути.</summary>
	public float NormalA;
	public float NormalB;
	public float NormalC;

	/// <summary>Шероховатость (G-канал MR-текстуры в центроиде UV x RoughnessFactor, см.
	/// ModelLoader.TriangleRoughness) - насколько РЕЗКО металлический хит отражает дальше:
	/// без неё зеркальный хром и матовое железо в цепочке отскоков шейдились одинаково
	/// размыто (фиксированное 0.35 у env-заглушки - «шероховатость перемножается»).</summary>
	public float Roughness;

	/// <summary>Пара UV в half-ах, уложенная в биты float-поля. Half на UV хватает только возле
	/// нуля (на u=8 шаг сетки уже 1/128), поэтому вызывающий обязан заранее свернуть заворот
	/// (вычесть общий floor по треугольнику) - внутри одного треугольника размах UV мал.</summary>
	public static float PackUv(Vector2 uv)
	{
		uint bits = System.BitConverter.HalfToUInt16Bits((Half)uv.X)
			| ((uint)System.BitConverter.HalfToUInt16Bits((Half)uv.Y) << 16);
		return System.BitConverter.UInt32BitsToSingle(bits);
	}

	/// <summary>Окто-кодировка единичной нормали в пару half-ов (битами float-поля) - зеркало
	/// SceneUnpackOctNormal в SceneTrace.hlsl.</summary>
	public static float PackOctNormal(Vector3 n)
	{
		float sum = MathF.Abs(n.X) + MathF.Abs(n.Y) + MathF.Abs(n.Z);
		if (sum < 1e-12f)
		{
			return PackUv(Vector2.Zero);
		}

		var p = new Vector2(n.X / sum, n.Y / sum);
		if (n.Z < 0f)
		{
			p = new Vector2(
				(1f - MathF.Abs(p.Y)) * (p.X >= 0f ? 1f : -1f),
				(1f - MathF.Abs(p.X)) * (p.Y >= 0f ? 1f : -1f));
		}

		return PackUv(p);
	}
}

/// <summary>Инстанс сцены для аппаратной трассировки: во что попал луч и где это стоит. Зеркало
/// SceneInstance в SceneTrace.hlsl в части, которую видит шейдер (первый треугольник меша и
/// альбедо); матрица шейдеру не нужна - её знает TLAS.
///
/// SourceInstance - индекс в ModelLoader.instances, откуда инстанс пришёл. Он нужен, чтобы
/// вызывающий мог забрать СВЕЖУЮ позу для пересборки TLAS: часть инстансов модели в геометрию не
/// попадает (стекло, листва, вырожденные меши), поэтому нумерация здесь своя.</summary>
/// <summary>SourceModel/LocalTransform - происхождение инстанса для слежения за позами
/// МУЛЬТИМОДЕЛЬНОЙ сцены (см. PrefabSceneViewport): индекс модели в списке, отданном бейкеру, и
/// локальная матрица glTF-инстанса. Мировая поза = LocalTransform * мир записи сцены, и когда
/// запись двигают гизмо, пересобрать TLAS можно без пересбора бейкера.</summary>
/// <summary>TextureIndex/BaseColorFactor - текстурное альбедо хита для RT-отражений: индекс в
/// <see cref="ProbeInstancedGeometry.HitTextureKeys"/> (-1 - у материала нет base color текстуры,
/// хит остаётся на потриугольном альбедо) и линейный множитель BaseColorFactor материала (сама
/// текстура его не содержит - шейдер умножает после выборки).</summary>
public readonly record struct ProbeGeometryInstance(int MeshSlot, int SourceInstance, Vector3 Albedo,
	Matrix4x4 Transform, int SourceModel = 0, Matrix4x4 LocalTransform = default,
	int TextureIndex = -1, Vector3 BaseColorFactor = default);

/// <summary>
/// Геометрия сцены для АППАРАТНОЙ трассировки: треугольники в ОБЪЕКТНОМ пространстве, по одному
/// экземпляру на меш, плюс таблица инстансов с матрицами.
///
/// Почему не мировая похлёбка <see cref="ProbeGiBaker.ExportBvh"/>, которой пользуется программный
/// путь: она приколочена к позам объектов намертво - сдвинули инстанс, и надо перестраивать и
/// треугольники, и BVH целиком. Здесь же геометрия от позы не зависит вовсе, BLAS на меш строится
/// один раз, а движение мира стоит пересборки одного TLAS (см. ProbeSceneAccel).
///
/// Треугольники того же меша, использованного несколькими инстансами, лежат в единственном
/// экземпляре: BLAS и атрибуты для них общие, разъезжаются только матрица и альбедо.
/// </summary>
public sealed class ProbeInstancedGeometry
{
	/// <summary>Треугольники всех мешей подряд, в объектном пространстве. Поле альбедо не
	/// заполняется: оно свойство ИНСТАНСА (один меш может стоять в сцене с разными материалами),
	/// поэтому шейдер берёт его из <see cref="Instances"/>.</summary>
	public required BvhTriangleGpu[] Triangles { get; init; }

	/// <summary>Срез <see cref="Triangles"/> на каждый меш - по нему строится его BLAS, и он же
	/// даёт базу для CommittedPrimitiveIndex.</summary>
	public required (int First, int Count)[] Meshes { get; init; }

	/// <summary>Инстансы в порядке, в котором они уедут в TLAS: индекс здесь и есть InstanceID() в
	/// шейдере.</summary>
	public required ProbeGeometryInstance[] Instances { get; init; }

	/// <summary>Уникальные base color текстуры сцены для текстурного альбедо RT-хитов: пара
	/// (индекс модели в списке бейкера, materialId). Хранятся КЛЮЧАМИ, а не GPU-объектами, чтобы
	/// пережить дисковый кеш BVH: сами текстуры пересобираются из живых ModelLoader-ов при сборке
	/// SSR-ресурсов (см. SsrHitTextures). Индексы сюда пишет
	/// <see cref="ProbeGeometryInstance.TextureIndex"/>; размер ограничен
	/// <see cref="MaxHitTextures"/>.</summary>
	public required (int Model, int Material)[] HitTextureKeys { get; init; }

	/// <summary>Потолок числа уникальных текстур хитов - размер массива Texture2D в шейдере
	/// (bindless-режим) и слоёв атласа, сшит с SsrPassResources.MaxHitTextures. Не влезшие
	/// материалы честно падают на потриугольное альбедо (TextureIndex = -1).</summary>
	public const int MaxHitTextures = DecaEngine.Core.SsrPassResources.MaxHitTextures;

	public int TriangleCount => Triangles.Length;
}
