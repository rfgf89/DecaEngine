using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
using UnsafeCollections.Collections.Native;
using UnsafeCollections.Collections.Unsafe;

namespace DecaEngine.Graphics.ProbeGi;

public sealed class ProbeGiBaker
{
	// --- Сцена: мировые треугольники + BVH ---------------------------------------------------

	private struct Tri
	{
		public Vector3 A, E1, E2;

		/// <summary>Линейное альбедо для отскока (среднее по base color текстуре × фактор).</summary>
		public Vector3 Albedo;
	}

	private struct Node
	{
		public Vector3 Min, Max;

		/// <summary>Лист (Left &lt; 0): Start/Count - срез в _order. Внутренний узел: Left/Start -
		/// индексы левого/правого детей (правый НЕ Left+1: между ними всё левое поддерево -
		/// депth-first нумерация BuildNode).</summary>
		public int Left, Start, Count;
	}

	private Tri[] _tris = Array.Empty<Tri>();

	/// <summary>Объектная геометрия для аппаратного пути - собирается тем же проходом по модели, что
	/// и мировая похлёбка (см. конструктор).</summary>
	private ProbeInstancedGeometry _instanced = new()
	{
		Triangles = Array.Empty<BvhTriangleGpu>(),
		Meshes = Array.Empty<(int, int)>(),
		Instances = Array.Empty<ProbeGeometryInstance>(),
		HitTextureKeys = Array.Empty<(int, int)>(),
	};

	/// <summary>Геометрия сцены в объектном пространстве плюс таблица инстансов - основа BLAS/TLAS
	/// аппаратного пути (см. <see cref="ProbeInstancedGeometry"/>). Программному пути не нужна: он
	/// ходит по мировому BVH из <see cref="ExportBvh"/>.</summary>
	public ProbeInstancedGeometry InstancedGeometry => _instanced;

	/// <summary>Нормаль с гардом от нулевой длины (вырожденные/несглаженные вершины импорта):
	/// нулевая окто-кодируется в мусор, а (0,0,1) хотя бы валидна - шейдер её нормализует.</summary>
	private static Vector3 SafeNormalize(Vector3 n)
	{
		float lenSq = n.LengthSquared();
		return lenSq > 1e-12f ? n / MathF.Sqrt(lenSq) : Vector3.UnitZ;
	}

	/// <summary>Мировая матрица инстанса модели. Публичная и одна на всех нарочно: по ней строится
	/// и запечённая геометрия, и пересборка TLAS на движение объекта - разъехавшись, эти две
	/// сдвинули бы лучи относительно самой сцены.</summary>
	public static Matrix4x4 InstanceMatrix(Transform t) =>
		Matrix4x4.CreateScale(t.scale.X, t.scale.Y, t.scale.Z) *
		Matrix4x4.CreateFromQuaternion(t.rotation) *
		Matrix4x4.CreateTranslation(t.position);
	private int[] _order = Array.Empty<int>();
	private Node[] _nodes = Array.Empty<Node>();
	private int _nodeCount;
	private float _sceneEpsilon = 1e-3f;
	private float _rayTMax = 1e4f;

	public bool HasGeometry => _tris.Length > 0;

	/// <summary>Число треугольников в BVH - диагностика «бейк ничего не видит» (см. PreviewProbe).</summary>
	public int TriangleCount => _tris.Length;

	// --- Дисковый кеш BVH (см. ProbeGiBvhCache) ------------------------------------------------

	/// <summary>Треугольник мировой похлёбки в сериализуемом виде (зеркало приватного Tri).</summary>
	public struct CachedTri
	{
		public Vector3 A, E1, E2, Albedo;
	}

	/// <summary>Узел BVH в сериализуемом виде (зеркало приватного Node).</summary>
	public struct CachedNode
	{
		public Vector3 Min, Max;
		public int Left, Start, Count;
	}

	/// <summary>Полный слепок построенного BVH - всё, что нужно, чтобы поднять бейкер без обхода
	/// геометрии модели.</summary>
	public sealed class BvhCacheData
	{
		public required CachedTri[] Triangles { get; init; }
		public required CachedNode[] Nodes { get; init; }
		public required int[] Order { get; init; }
		public required int NodeCount { get; init; }
		public required float SceneEpsilon { get; init; }
		public required float RayTMax { get; init; }
		public required BvhTriangleGpu[] ObjectTriangles { get; init; }
		public required (int First, int Count)[] MeshSlots { get; init; }
		public required ProbeGeometryInstance[] Instances { get; init; }
		public required (int Model, int Material)[] HitTextureKeys { get; init; }
	}

	/// <summary>Восстановление из кеша - конструктор без единого обращения к геометрии модели.</summary>
	private ProbeGiBaker(BvhCacheData data)
	{
		_tris = new Tri[data.Triangles.Length];
		for (int i = 0; i < _tris.Length; i++)
		{
			var t = data.Triangles[i];
			_tris[i] = new Tri { A = t.A, E1 = t.E1, E2 = t.E2, Albedo = t.Albedo };
		}

		_nodes = new Node[data.Nodes.Length];
		for (int i = 0; i < _nodes.Length; i++)
		{
			var n = data.Nodes[i];
			_nodes[i] = new Node { Min = n.Min, Max = n.Max, Left = n.Left, Start = n.Start, Count = n.Count };
		}

		_order = data.Order;
		_nodeCount = data.NodeCount;
		_sceneEpsilon = data.SceneEpsilon;
		_rayTMax = data.RayTMax;

		_instanced = new ProbeInstancedGeometry
		{
			Triangles = data.ObjectTriangles,
			Meshes = data.MeshSlots,
			Instances = data.Instances,
			HitTextureKeys = data.HitTextureKeys,
		};
	}

	/// <summary>Слепок текущего BVH для записи в кеш.</summary>
	public BvhCacheData ExportCache()
	{
		var triangles = new CachedTri[_tris.Length];
		for (int i = 0; i < triangles.Length; i++)
		{
			ref var t = ref _tris[i];
			triangles[i] = new CachedTri { A = t.A, E1 = t.E1, E2 = t.E2, Albedo = t.Albedo };
		}

		var nodes = new CachedNode[_nodeCount];
		for (int i = 0; i < nodes.Length; i++)
		{
			ref var n = ref _nodes[i];
			nodes[i] = new CachedNode { Min = n.Min, Max = n.Max, Left = n.Left, Start = n.Start, Count = n.Count };
		}

		return new BvhCacheData
		{
			Triangles = triangles,
			Nodes = nodes,
			Order = _order,
			NodeCount = _nodeCount,
			SceneEpsilon = _sceneEpsilon,
			RayTMax = _rayTMax,
			ObjectTriangles = _instanced.Triangles,
			MeshSlots = _instanced.Meshes,
			Instances = _instanced.Instances,
			HitTextureKeys = _instanced.HitTextureKeys,
		};
	}

	/// <summary>
	/// Бейкер по одной модели: сперва пробуем кеш &lt;модель&gt;.bhv.bin рядом с ней, и только если
	/// его нет (или он от другой версии файла) - строим BVH и кладём результат в кеш. Сборка стоит
	/// десятки секунд на тяжёлом ассете, а геометрия между запусками не меняется.
	/// </summary>
	public static ProbeGiBaker LoadOrBuild(ModelLoader model, string modelPath, out bool fromCache)
	{
		fromCache = false;

		if (!string.IsNullOrEmpty(modelPath))
		{
			var cached = ProbeGiBvhCache.TryRead(modelPath);
			if (cached != null)
			{
				fromCache = true;
				return new ProbeGiBaker(cached);
			}
		}

		var baker = new ProbeGiBaker(model);

		if (!string.IsNullOrEmpty(modelPath) && baker.HasGeometry)
		{
			ProbeGiBvhCache.Write(modelPath, baker.ExportCache());
		}

		return baker;
	}

	// --- Диагностика BVH -----------------------------------------------------------------------

	/// <summary>Сводка по построенному дереву - для отладочного вывода и оверлея.</summary>
	public readonly record struct BvhStats(int Triangles, int Nodes, int Leaves, int MaxDepth,
		float AvgLeafTriangles, Vector3 Min, Vector3 Max);

	public BvhStats GetStats()
	{
		if (_nodeCount == 0)
		{
			return new BvhStats(0, 0, 0, 0, 0f, Vector3.Zero, Vector3.Zero);
		}

		int leaves = 0, maxDepth = 0;
		long leafTris = 0;
		CountStats(0, 1, ref leaves, ref maxDepth, ref leafTris);

		return new BvhStats(_tris.Length, _nodeCount, leaves, maxDepth,
			leaves > 0 ? (float)leafTris / leaves : 0f, _nodes[0].Min, _nodes[0].Max);
	}

	private void CountStats(int nodeIndex, int depth, ref int leaves, ref int maxDepth, ref long leafTris)
	{
		ref var node = ref _nodes[nodeIndex];
		if (depth > maxDepth)
		{
			maxDepth = depth;
		}

		if (node.Left < 0)
		{
			leaves++;
			leafTris += node.Count;
			return;
		}

		CountStats(node.Left, depth + 1, ref leaves, ref maxDepth, ref leafTris);
		CountStats(node.Start, depth + 1, ref leaves, ref maxDepth, ref leafTris);
	}

	/// <summary>
	/// Боксы узлов дерева для отладочной отрисовки. <paramref name="maxDepth"/> - до какой глубины
	/// спускаться (0 = только корень); <paramref name="leavesOnly"/> - брать только листья, то есть
	/// показывать фактическую гранулярность разбиения, а не вложенные объёмы.
	/// </summary>
	public List<(Vector3 Min, Vector3 Max, int Depth)> CollectDebugBoxes(int maxDepth, bool leavesOnly)
	{
		var boxes = new List<(Vector3, Vector3, int)>();
		if (_nodeCount > 0)
		{
			CollectBoxes(0, 0, maxDepth, leavesOnly, boxes);
		}

		return boxes;
	}

	private void CollectBoxes(int nodeIndex, int depth, int maxDepth, bool leavesOnly,
		List<(Vector3, Vector3, int)> boxes)
	{
		ref var node = ref _nodes[nodeIndex];
		bool isLeaf = node.Left < 0;

		if (!leavesOnly || isLeaf)
		{
			if (depth <= maxDepth || (leavesOnly && isLeaf))
			{
				boxes.Add((node.Min, node.Max, depth));
			}
		}

		if (isLeaf || depth >= maxDepth)
		{
			return;
		}

		CollectBoxes(node.Left, depth + 1, maxDepth, leavesOnly, boxes);
		CollectBoxes(node.Start, depth + 1, maxDepth, leavesOnly, boxes);
	}

	/// <summary>Порог луча по дальности, за которым попадание считается промахом - тот же, что
	/// использует CPU-трассировщик; GPU-обход обязан брать его отсюда, иначе пути разойдутся.</summary>
	public float RayTMax => _rayTMax;

	/// <summary>Отступ теневого луча от поверхности - GPU-путь обязан брать его отсюда, иначе
	/// самозатенение разойдётся с CPU-эталоном.</summary>
	public float SceneEpsilon => _sceneEpsilon;

	/// <summary>Направления лучей конкретного раунда. GPU-путь берёт их отсюда, а не пересчитывает
	/// у себя: расхождение в последнем бите синуса увело бы луч на соседний треугольник у силуэта,
	/// и сверка с CPU-эталоном перестала бы что-либо значить (см. ProbeRoundCS.hlsl).</summary>
	public static Vector3[] RoundRayDirections(int rays, int sequence) =>
		BuildRotatedFibonacciSphere(rays, sequence);

	/// <summary>Сколько первых лучей веера не вращать (RTXGI-DDGI, RTXGI_DDGI_NUM_FIXED_RAYS).
	///
	/// Смысл приёма (ProbeRayCommon.hlsl: «Don't rotate fixed rays so relocation/classification are
	/// temporally stable»): решения о ПЕРЕЕЗДЕ пробы и о её отключении принимаются по геометрии -
	/// доле задних граней, ближайшему выходу наружу, запасу свободного места. Считать их по вееру,
	/// который каждый раунд повёрнут заново, значит мерить дрожащей линейкой: у пробы на кромке
	/// геометрии доля задних граней гуляет от раунда к раунду просто из-за смены направлений, и
	/// проба то уезжает, то возвращается, каждый раз сбрасывая накопители. Небольшой набор лучей,
	/// НЕ зависящий от номера раунда, даёт этим решениям устойчивую опору.
	///
	/// Фиксированные лучи не участвуют в оценке радианса и в карте глубин (см. ProbeRoundCS): они
	/// не вращаются, поэтому их направления представлены в среднем вдвое чаще остальных, и подмешать
	/// их значило бы внести в оценку постоянное смещение по этим направлениям. Трассируются они не
	/// впустую - именно они и делают всю геометрическую работу.
	///
	/// ТОЛЬКО в реальном времени. В запечке веер вращается по номеру раунда, оба пути (CPU и GPU)
	/// обязаны совпасть луч в луч ради сверки, и делить веер значило бы зеркалить всю раскладку ещё
	/// и в CPU-бейкере; выгоды при этом нет - в запечке проба переезжает один раз на инициализации.
	/// Доля - восьмая часть веера (у эталона 32 из 288, тот же порядок) с полом 16 и потолком 32,
	/// и только начиная с 64 лучей. Пол не занижен сознательно: по этим лучам ищется БЛИЖАЙШАЯ
	/// передняя грань, и на слишком редком веере проба рискует не заметить рядом стоящую
	/// поверхность и решить, что вокруг просторно (ветка возврата к узлу в ProbeRoundCS). Потолок
	/// держит цену: сверх 32 устойчивость решений уже не растёт, а лучи из оценки радианса
	/// вычитаются. На коротком веере (меньше 64) деления нет вовсе - отдать четверть выборки ради
	/// устойчивости релокации невыгодно, шум радианса дороже.</summary>
	public static int FixedRayCount(int rays, bool realtime) =>
		realtime && rays >= 64 ? Math.Min(32, Math.Max(rays / 8, 16)) : 0;

	/// <summary>Направления раунда с учётом фиксированных лучей: [0, FixedRays) - НЕвращаемый веер
	/// Фибоначчи, [FixedRays, rays) - обычный, повёрнутый по номеру раунда. Оба - равномерные
	/// сферические выборки, поэтому каждая часть остаётся корректной сама по себе.</summary>
	public static Vector3[] RoundRayDirections(ProbeGiBakeSession session) =>
		RoundRayDirections(session.RaysPerRound, session.Sequence, session.FixedRays);

	/// <inheritdoc cref="RoundRayDirections(ProbeGiBakeSession)"/>
	public static Vector3[] RoundRayDirections(int rays, int sequence, int fixedRays)
	{
		if (fixedRays <= 0)
		{
			return BuildRotatedFibonacciSphere(rays, sequence);
		}

		var dirs = new Vector3[rays];
		Array.Copy(BuildFibonacciSphere(fixedRays), dirs, fixedRays);
		Array.Copy(BuildRotatedFibonacciSphere(rays - fixedRays, sequence), 0,
			dirs, fixedRays, rays - fixedRays);
		return dirs;
	}

	/// <summary>Вес раунда в бегущем среднем - GPU-путь считает его тем же способом, что и
	/// <see cref="RunRound"/>, иначе поля разойдутся по яркости.
	///
	/// Сессия, а не просто номер раунда: пол веса зависит от режима (см.
	/// <see cref="ProbeGiBakeSession.MinBlend"/>), и в реальном времени именно он превращает бегущее
	/// среднее в экспоненциальное - формула та же, разъезжаются только асимптоты.</summary>
	public static float RoundBlendWeight(ProbeGiBakeSession session)
	{
		int averaged = session.Round - BootstrapRounds;
		return averaged < 0 ? 1f : MathF.Max(1f / (averaged + 1), session.MinBlend);
	}

	/// <summary>Трассирует один луч CPU-обходом. Это тот же код, которым идёт бейк, вынесенный
	/// наружу как ЭТАЛОН для сверки GPU-путей (см. SceneTrace.hlsl и сверочный прогон в
	/// PreviewProbe): CPU-трассировщик уже рабочий, и расхождение с ним - это баг GPU-обхода.</summary>
	public bool TraceRay(Vector3 origin, Vector3 direction, float tMax,
		out float t, out Vector3 normal, out Vector3 albedo)
	{
		normal = Vector3.UnitY;
		albedo = Vector3.Zero;

		if (!TraceClosest(origin, direction, out t, out int triIndex) || t > tMax)
		{
			t = 0f;
			return false;
		}

		ref var tri = ref _tris[triIndex];
		normal = Vector3.Normalize(Vector3.Cross(tri.E1, tri.E2));
		albedo = tri.Albedo;
		return true;
	}

	/// <summary>Выгружает BVH в раскладке под StructuredBuffer для compute-обхода на GPU (см.
	/// SceneTrace.hlsl) - путь для железа без аппаратной трассировки. Структура ровно та же, по
	/// которой ходит CPU-трассировщик, поэтому compute-путь можно сверять с ним луч в луч.</summary>
	public (BvhNodeGpu[] Nodes, uint[] Order, BvhTriangleGpu[] Triangles) ExportBvh()
	{
		var nodes = new BvhNodeGpu[Math.Max(_nodeCount, 1)];
		for (int i = 0; i < _nodeCount; i++)
		{
			ref var node = ref _nodes[i];
			nodes[i] = new BvhNodeGpu
			{
				BoundsMin = node.Min,
				BoundsMax = node.Max,
				Left = node.Left,
				Start = node.Start,
				Count = node.Count,
			};
		}

		var order = new uint[Math.Max(_order.Length, 1)];
		for (int i = 0; i < _order.Length; i++)
		{
			order[i] = (uint)_order[i];
		}

		var triangles = new BvhTriangleGpu[Math.Max(_tris.Length, 1)];
		for (int i = 0; i < _tris.Length; i++)
		{
			ref var tri = ref _tris[i];
			triangles[i] = new BvhTriangleGpu
			{
				A = tri.A,
				E1 = tri.E1,
				E2 = tri.E2,
				Albedo = tri.Albedo,
			};
		}

		return (nodes, order, triangles);
	}

	/// <summary>Число лучей на пробу по умолчанию (сферический Фибоначчи, фиксированный веер -
	/// при L1-проекции регулярность паттерна не полосит). Реальное берётся из
	/// <see cref="ProbeGiBakeOptions.RaysPerProbe"/>.</summary>
	public const int DefaultRaysPerProbe = 96;

	/// <summary>Потолок бюджета проб (верхний кламп <see cref="ProbeGiBakeOptions.MaxProbes"/>) -
	/// им же размечен комбо "Max probes" в окне Graphics. Ограничивает бейк, а не раскладку
	/// атласов: ячейка укрупняется, пока сетка не влезет в бюджет.</summary>
	public const int MaxProbeBudget = 2097152;

	/// <summary>Нижний кламп бюджета проб: сетка меньше 2x2x2 по осям бессмысленна, а совсем
	/// мелкий бюджет схлопывает ячейку в габарит сцены.</summary>
	public const int MinProbeBudget = 512;

	/// <summary>Потолок числа ПРОБ по одной оси. Это не про стоимость бейка (её держит
	/// <see cref="MaxProbeBudget"/>), а про размер атласа: высота равна CountZ*CountY, умноженная на
	/// <see cref="ProbeGiBakeResult.VisRes"/> у карты видимости, и обязана влезть в
	/// <see cref="MaxAtlasDimension"/>. Сама эта проверка стоит в BeginBake и точнее потолка по оси;
	/// потолок здесь - грубая страховка от вырожденно вытянутого баунда.</summary>
	public const int MaxProbesPerAxis = 512;

	/// <summary>Предел стороны текстуры, в который обязаны влезть атласы проб (гарантия D3D12 и
	/// Vulkan-реализаций - 16384). Проверяется по САМОМУ большому измерению окто-атласа видимости
	/// (высота = CountZ*CountY*<see cref="ProbeGiBakeResult.VisRes"/>).</summary>
	public const int MaxAtlasDimension = 16384;

	/// <summary>Читает CPU-копии мешей (<see cref="IMeshObject.VertexData"/>) - вызывать на потоке,
	/// владеющем моделью (главном); дальше Bake можно уносить в фон.</summary>
	public unsafe ProbeGiBaker(ModelLoader model)
		: this(new[] { (model, Matrix4x4.Identity) }, trackSourceInstances: true)
	{
	}

	/// <summary>Сцена из нескольких моделей с мировыми матрицами (окно Scene View, см.
	/// PrefabSceneViewport): треугольники каждого инстанса каждой модели попадают в мировой BVH
	/// через InstanceMatrix(инстанс) * World. При trackSourceInstances=false SourceInstance пишется
	/// -1 - слежение за движением инстансов по исходной модели (PollProbeAccel превью) для
	/// мульти-модельной сцены не имеет смысла: её позы задают сущности префаба, и на их изменение
	/// сцена просто пересоздаёт сессию.</summary>
	public unsafe ProbeGiBaker(IReadOnlyList<(ModelLoader Model, Matrix4x4 World)> models,
		bool trackSourceInstances = false)
	{
		var tris = new List<Tri>();

		// Параллельно мировой похлёбке собирается ОБЪЕКТНАЯ геометрия для аппаратной трассировки
		// (см. ProbeInstancedGeometry): те же меши, но по разу на меш и без матрицы инстанса.
		// Дедуп - по паре (модель, меш): меш, инстанцированный многими сущностями сцены, получает
		// один срез треугольников/BLAS.
		var objectTris = new List<BvhTriangleGpu>();
		var meshSlots = new List<(int First, int Count)>();
		var meshSlotByMeshId = new Dictionary<(ModelLoader, int), int>();
		var geometryInstances = new List<ProbeGeometryInstance>();

		// Уникальные base color текстуры сцены (текстурное альбедо RT-хитов) - ключами
		// (модель, материал), дедуп по словарю; -1 в словаре - материал не влез в потолок.
		var hitTextureKeys = new List<(int Model, int Material)>();
		var hitTextureIndexByKey = new Dictionary<(int, int), int>();

		for (int modelIndex = 0; modelIndex < models.Count; modelIndex++)
		{
		var (model, world) = models[modelIndex];
		for (int sourceIndex = 0; sourceIndex < model.instances.Count; sourceIndex++)
		{
			var instance = model.instances[sourceIndex];
			if (instance.meshId < 0 || instance.meshId >= model.Meshes.Count)
			{
				continue;
			}

			// Стекло свет не блокирует, линии/точки не геометрия. Реально «дырявые» материалы
			// (листва/трава/решётки: средняя альфа base color текстуры мала) тоже пропускаем
			// ЦЕЛИКОМ: трассировщик не сэмплирует текстуры, и ажурные квады стали бы сплошными
			// стенами - крона дерева наглухо гасила бы солнце и небо во всём дворе (пропадал
			// солнечный баунс от пола - галереи чернели, см. Sponza). Критерий - именно средняя
			// альфа, а НЕ AlphaCutoff: экспортеры сплошь метят камень/ткань как MASK/BLEND
			// (альфа ~1), и фильтр по режиму выкидывал из BVH всю сцену. Цена - листва не даёт
			// GI-тени; экранная тень от неё остаётся за shadow map-ой.
			Vector3 albedo = new(0.5f);
			bool pbrFound = model.MaterialPbr.TryGetValue(instance.materialId, out var pbr);
			if (pbrFound)
			{
				bool sparse = pbr.AlphaCutoff > 0f && pbr.HasBaseColorTexture && pbr.AverageAlpha < 0.6f;
				if (pbr.Topology != ModelLoader.MeshTopologyTriangles || pbr.TransmissionFactor > 0.5f ||
					sparse)
				{
					continue;
				}

				albedo = pbr.AverageBaseColor.LengthSquared() > 1e-6f
					? pbr.AverageBaseColor
					: new Vector3(pbr.BaseColorFactor.X, pbr.BaseColorFactor.Y, pbr.BaseColorFactor.Z);
			}

			// Кламп сверху: альбедо ~1 в замкнутом дворе раскачивает мультибаунс до пересвета.
			albedo = Vector3.Min(albedo, new Vector3(0.85f));

			var mesh = model.Meshes[instance.meshId];
			if (mesh.IndexCount < 3 || mesh.VertexData == null || mesh.IndexData == null)
			{
				continue;
			}

			var matrix = InstanceMatrix(instance.transform) * world;

			// Потриугольное альбедо из текстур (см. ModelLoader.TriangleAlbedo): отскок GI и
			// RT-отражения получают цвет в разрешении треугольников; без него - средний цвет
			// материала (albedo выше).
			model.TriangleAlbedo.TryGetValue(instance.meshId, out var triAlbedo);
			var albedoCap = new Vector3(0.85f);

			// Потриугольные металличность и шероховатость (детект металла у RT-хита и резкость
			// его продолжения).
			//
			// Фолбэк, когда попиксельных данных нет: факторы материала - но ТОЛЬКО если у него
			// нет MR-текстуры. С текстурой факторы по спецификации glTF всего лишь МНОЖИТЕЛИ и
			// по умолчанию равны 1: принять их за истину означало объявить весь материал
			// «шершавым металлом» - диффуз хита обнулялся множителем (1 - metalness), оставалась
			// одна env-заглушка с шероховатостью 1 (плоский цвет, «roughness улетел в максимум»).
			// Неизвестная металличность = НЕ металл: диффуз сохраняется, фантомных зеркал нет.
			model.TriangleMetalness.TryGetValue(instance.meshId, out var triMetalness);
			model.TriangleRoughness.TryGetValue(instance.meshId, out var triRoughness);
			bool factorsAuthoritative = pbrFound && !pbr.HasMetallicRoughnessTexture;
			float materialMetalness = factorsAuthoritative ? pbr.MetallicFactor : 0f;
			float materialRoughness = factorsAuthoritative ? pbr.RoughnessFactor : 1f;

			// Текстурное альбедо хита (RT-отражения): индекс уникальной base color текстуры сцены.
			// Только материалам с настоящей текстурой на мешах с настоящими UV (синтезированные
			// нули дали бы одну точку текстуры на весь меш); переполнение потолка честно остаётся
			// на потриугольном альбедо.
			int hitTextureIndex = -1;
			bool meshHasUv = instance.meshId < model.MeshHasUv.Count && model.MeshHasUv[instance.meshId];
			if (meshHasUv && pbrFound && pbr.HasBaseColorTexture &&
				model.MaterialBaseColor.ContainsKey(instance.materialId))
			{
				var textureKey = (modelIndex, instance.materialId);
				if (!hitTextureIndexByKey.TryGetValue(textureKey, out hitTextureIndex))
				{
					hitTextureIndex = hitTextureKeys.Count < ProbeInstancedGeometry.MaxHitTextures
						? hitTextureKeys.Count
						: -1;
					if (hitTextureIndex >= 0)
					{
						hitTextureKeys.Add(textureKey);
					}

					hitTextureIndexByKey[textureKey] = hitTextureIndex;
				}
			}

			var baseColorFactor = pbrFound
				? new Vector3(pbr.BaseColorFactor.X, pbr.BaseColorFactor.Y, pbr.BaseColorFactor.Z)
				: Vector3.One;

			int vertexCount = UnsafeArray.GetLength(mesh.VertexData);
			var vertices = new ReadOnlySpan<Vertex>(UnsafeArray.GetPtr<Vertex>(mesh.VertexData, 0), vertexCount);

			// ModelLoader всегда строит 32-битные индексы (см. PreparedMesh.Indices: uint[]).
			var indices = new ReadOnlySpan<uint>(UnsafeArray.GetPtr<uint>(mesh.IndexData, 0), mesh.IndexCount);

			// Объектная копия меша - только для первого встреченного его инстанса: остальные
			// переиспользуют и срез треугольников, и построенный по нему BLAS.
			if (!meshSlotByMeshId.TryGetValue((model, instance.meshId), out int meshSlot))
			{
				int firstObjectTri = objectTris.Count;
				for (int i = 0; i + 2 < indices.Length; i += 3)
				{
					uint j0 = indices[i], j1 = indices[i + 1], j2 = indices[i + 2];
					if (j0 >= vertexCount || j1 >= vertexCount || j2 >= vertexCount)
					{
						continue;
					}

					var oa = vertices[(int)j0].Position;
					var oe1 = vertices[(int)j1].Position - oa;
					var oe2 = vertices[(int)j2].Position - oa;
					if (Vector3.Cross(oe1, oe2).LengthSquared() < 1e-16f)
					{
						continue;
					}

					// UV вершин для текстурного альбедо хита: KHR_texture_transform применяется
					// ЗДЕСЬ (та же формула, что в UnlitInstancedPS: 2x2-матрица + offset), чтобы
					// шейдеру трассы не таскать трансформ per-инстанс. Как и у triAlbedo,
					// трансформ берётся у ПЕРВОГО инстанса меша - у прочих инстансов с другим
					// материалом UV останутся его.
					var uv0 = vertices[(int)j0].TexCoord;
					var uv1 = vertices[(int)j1].TexCoord;
					var uv2 = vertices[(int)j2].TexCoord;
					if (pbrFound && pbr.HasUvTransform)
					{
						var t = pbr.UvTransform;
						uv0 = new Vector2(uv0.X * t.X + uv0.Y * t.Y, uv0.X * t.Z + uv0.Y * t.W) + pbr.UvOffset;
						uv1 = new Vector2(uv1.X * t.X + uv1.Y * t.Y, uv1.X * t.Z + uv1.Y * t.W) + pbr.UvOffset;
						uv2 = new Vector2(uv2.X * t.X + uv2.Y * t.Y, uv2.X * t.Z + uv2.Y * t.W) + pbr.UvOffset;
					}

					// Свернуть заворот к нулю ДО упаковки в half: у half на u=8 шаг сетки уже
					// 1/128 (пиксели на текстуре 1К), а Wrap-сэмплер общий сдвиг на целое не
					// заметит. Внутри одного треугольника размах UV мал - точности хватает.
					var uvShift = new Vector2(
						MathF.Floor(MathF.Min(uv0.X, MathF.Min(uv1.X, uv2.X))),
						MathF.Floor(MathF.Min(uv0.Y, MathF.Min(uv1.Y, uv2.Y))));

					// Потриугольное альбедо, фолбэк - материал инстанса. Раньше здесь не писалось
					// ничего («оно у инстанса»), но HW-путь SceneTrace читает ИМЕННО tri.albedo -
					// нулевое альбедо делало все RT-хиты чёрными (и включало метал-эвристику
					// второго отскока на всей геометрии).
					objectTris.Add(new BvhTriangleGpu
					{
						A = oa, E1 = oe1, E2 = oe2,
						UvA = BvhTriangleGpu.PackUv(uv0 - uvShift),
						UvB = BvhTriangleGpu.PackUv(uv1 - uvShift),
						UvC = BvhTriangleGpu.PackUv(uv2 - uvShift),
						Albedo = triAlbedo != null
							? Vector3.Min(triAlbedo[i / 3], albedoCap)
							: albedo,
						Metalness = triMetalness != null ? triMetalness[i / 3] : materialMetalness,
						Roughness = triRoughness != null ? triRoughness[i / 3] : materialRoughness,
						NormalA = BvhTriangleGpu.PackOctNormal(SafeNormalize(vertices[(int)j0].Normal)),
						NormalB = BvhTriangleGpu.PackOctNormal(SafeNormalize(vertices[(int)j1].Normal)),
						NormalC = BvhTriangleGpu.PackOctNormal(SafeNormalize(vertices[(int)j2].Normal)),
					});
				}

				// Меш целиком выродился (нулевой масштаб в самих вершинах, склеенные точки) -
				// инстансу не на что ссылаться, BLAS строить не из чего.
				meshSlot = objectTris.Count > firstObjectTri ? meshSlots.Count : -1;
				if (meshSlot >= 0)
				{
					meshSlots.Add((firstObjectTri, objectTris.Count - firstObjectTri));
				}

				meshSlotByMeshId[(model, instance.meshId)] = meshSlot;
			}

			if (meshSlot >= 0)
			{
				geometryInstances.Add(new ProbeGeometryInstance(meshSlot,
					trackSourceInstances ? sourceIndex : -1, albedo, matrix,
					modelIndex, InstanceMatrix(instance.transform),
					hitTextureIndex, baseColorFactor));
			}

			for (int i = 0; i + 2 < indices.Length; i += 3)
			{
				uint i0 = indices[i], i1 = indices[i + 1], i2 = indices[i + 2];
				if (i0 >= vertexCount || i1 >= vertexCount || i2 >= vertexCount)
				{
					continue;
				}

				var a = Vector3.Transform(vertices[(int)i0].Position, matrix);
				var b = Vector3.Transform(vertices[(int)i1].Position, matrix);
				var c = Vector3.Transform(vertices[(int)i2].Position, matrix);

				var e1 = b - a;
				var e2 = c - a;
				if (Vector3.Cross(e1, e2).LengthSquared() < 1e-16f)
				{
					continue;
				}

				tris.Add(new Tri
				{
					A = a, E1 = e1, E2 = e2,
					Albedo = triAlbedo != null ? Vector3.Min(triAlbedo[i / 3], albedoCap) : albedo,
				});
			}
		}
		}

		_tris = tris.ToArray();

		// Вырожденность проверяется в СВОЁМ пространстве у каждой похлёбки, поэтому счётчики
		// треугольников могут разойтись на единицы на патологических матрицах (нулевой масштаб
		// схлопывает мировой треугольник, оставляя объектный живым). Пути от этого не разъезжаются:
		// каждый читает атрибуты из своего массива, а сверяются они по попаданиям луча.
		_instanced = new ProbeInstancedGeometry
		{
			Triangles = objectTris.ToArray(),
			Meshes = meshSlots.ToArray(),
			Instances = geometryInstances.ToArray(),
			HitTextureKeys = hitTextureKeys.ToArray(),
		};

		if (_tris.Length == 0)
		{
			return;
		}

		BuildBvh();
	}

	// --- BVH (медианный сплит по крупнейшей оси, лист ≤ 4 треугольников) ----------------------

	private void BuildBvh()
	{
		int n = _tris.Length;
		_order = new int[n];
		var centroids = new Vector3[n];
		for (int i = 0; i < n; i++)
		{
			_order[i] = i;
			centroids[i] = _tris[i].A + (_tris[i].E1 + _tris[i].E2) * (1f / 3f);
		}

		_nodes = new Node[2 * n];
		_nodeCount = 0;
		var sceneMin = new Vector3(float.MaxValue);
		var sceneMax = new Vector3(float.MinValue);

		BuildNode(0, n, centroids, ref sceneMin, ref sceneMax);

		var size = sceneMax - sceneMin;
		float maxDim = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
		_sceneEpsilon = MathF.Max(maxDim * 5e-4f, 1e-5f);
		_rayTMax = MathF.Max(maxDim * 4f, 1f);
	}

	private int BuildNode(int start, int count, Vector3[] centroids, ref Vector3 outMin, ref Vector3 outMax)
	{
		int nodeIndex = _nodeCount++;
		var min = new Vector3(float.MaxValue);
		var max = new Vector3(float.MinValue);
		for (int i = start; i < start + count; i++)
		{
			ref var tri = ref _tris[_order[i]];
			var b = tri.A + tri.E1;
			var c = tri.A + tri.E2;
			min = Vector3.Min(min, Vector3.Min(tri.A, Vector3.Min(b, c)));
			max = Vector3.Max(max, Vector3.Max(tri.A, Vector3.Max(b, c)));
		}

		outMin = Vector3.Min(outMin, min);
		outMax = Vector3.Max(outMax, max);

		if (count <= 4)
		{
			_nodes[nodeIndex] = new Node { Min = min, Max = max, Left = -1, Start = start, Count = count };
			return nodeIndex;
		}

		var size = max - min;
		int axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;

		// Медиана по центроидам: сортировка среза _order компаратором по оси.
		Array.Sort(_order, start, count, Comparer<int>.Create((x, y) =>
			GetAxis(centroids[x], axis).CompareTo(GetAxis(centroids[y], axis))));

		int half = count / 2;
		var dummyMin = new Vector3(float.MaxValue);
		var dummyMax = new Vector3(float.MinValue);
		int left = BuildNode(start, half, centroids, ref dummyMin, ref dummyMax);
		int right = BuildNode(start + half, count - half, centroids, ref dummyMin, ref dummyMax);

		_nodes[nodeIndex] = new Node { Min = min, Max = max, Left = left, Start = right, Count = 0 };
		return nodeIndex;
	}

	private static float GetAxis(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

	// --- Трассировка --------------------------------------------------------------------------

	private static bool RayBox(Vector3 origin, Vector3 invDir, float tMax, in Node node)
	{
		float tx1 = (node.Min.X - origin.X) * invDir.X;
		float tx2 = (node.Max.X - origin.X) * invDir.X;
		float tmin = MathF.Min(tx1, tx2);
		float tmax = MathF.Max(tx1, tx2);

		float ty1 = (node.Min.Y - origin.Y) * invDir.Y;
		float ty2 = (node.Max.Y - origin.Y) * invDir.Y;
		tmin = MathF.Max(tmin, MathF.Min(ty1, ty2));
		tmax = MathF.Min(tmax, MathF.Max(ty1, ty2));

		float tz1 = (node.Min.Z - origin.Z) * invDir.Z;
		float tz2 = (node.Max.Z - origin.Z) * invDir.Z;
		tmin = MathF.Max(tmin, MathF.Min(tz1, tz2));
		tmax = MathF.Min(tmax, MathF.Max(tz1, tz2));

		return tmax >= MathF.Max(tmin, 0f) && tmin <= tMax;
	}

	/// <summary>Möller–Trumbore, двусторонний. Возвращает t или -1.</summary>
	private static float RayTri(Vector3 origin, Vector3 dir, in Tri tri)
	{
		var p = Vector3.Cross(dir, tri.E2);
		float det = Vector3.Dot(tri.E1, p);
		if (MathF.Abs(det) < 1e-9f)
		{
			return -1f;
		}

		float invDet = 1f / det;
		var s = origin - tri.A;
		float u = Vector3.Dot(s, p) * invDet;
		if (u < 0f || u > 1f)
		{
			return -1f;
		}

		var q = Vector3.Cross(s, tri.E1);
		float v = Vector3.Dot(dir, q) * invDet;
		if (v < 0f || u + v > 1f)
		{
			return -1f;
		}

		float t = Vector3.Dot(tri.E2, q) * invDet;
		return t > 0f ? t : -1f;
	}

	private bool TraceClosest(Vector3 origin, Vector3 dir, out float hitT, out int hitTri)
	{
		hitT = _rayTMax;
		hitTri = -1;

		var invDir = new Vector3(
			1f / (MathF.Abs(dir.X) < 1e-12f ? MathF.CopySign(1e-12f, dir.X) : dir.X),
			1f / (MathF.Abs(dir.Y) < 1e-12f ? MathF.CopySign(1e-12f, dir.Y) : dir.Y),
			1f / (MathF.Abs(dir.Z) < 1e-12f ? MathF.CopySign(1e-12f, dir.Z) : dir.Z));

		Span<int> stack = stackalloc int[64];
		int sp = 0;
		stack[sp++] = 0;

		while (sp > 0)
		{
			ref var node = ref _nodes[stack[--sp]];
			if (!RayBox(origin, invDir, hitT, node))
			{
				continue;
			}

			if (node.Left < 0)
			{
				for (int i = node.Start; i < node.Start + node.Count; i++)
				{
					int triIndex = _order[i];
					float t = RayTri(origin, dir, _tris[triIndex]);
					if (t > 0f && t < hitT)
					{
						hitT = t;
						hitTri = triIndex;
					}
				}
			}
			else if (sp + 2 <= stack.Length)
			{
				stack[sp++] = node.Left;
				stack[sp++] = node.Start;
			}
		}

		return hitTri >= 0;
	}

	private bool TraceAnyHit(Vector3 origin, Vector3 dir, float tMax)
	{
		var invDir = new Vector3(
			1f / (MathF.Abs(dir.X) < 1e-12f ? MathF.CopySign(1e-12f, dir.X) : dir.X),
			1f / (MathF.Abs(dir.Y) < 1e-12f ? MathF.CopySign(1e-12f, dir.Y) : dir.Y),
			1f / (MathF.Abs(dir.Z) < 1e-12f ? MathF.CopySign(1e-12f, dir.Z) : dir.Z));

		Span<int> stack = stackalloc int[64];
		int sp = 0;
		stack[sp++] = 0;

		while (sp > 0)
		{
			ref var node = ref _nodes[stack[--sp]];
			if (!RayBox(origin, invDir, tMax, node))
			{
				continue;
			}

			if (node.Left < 0)
			{
				for (int i = node.Start; i < node.Start + node.Count; i++)
				{
					float t = RayTri(origin, dir, _tris[_order[i]]);
					if (t > 0f && t < tMax)
					{
						return true;
					}
				}
			}
			else if (sp + 2 <= stack.Length)
			{
				stack[sp++] = node.Left;
				stack[sp++] = node.Start;
			}
		}

		return false;
	}

	// --- Плотная сетка проб --------------------------------------------------------------------

	/// <summary>Линейный индекс ХРАНЕНИЯ пробы по её координатам в атласе: строки идут по Y внутри
	/// плоскости Z, плоскости - столбиком. Ровно этим индексом адресуются все CPU-буферы поля
	/// (L0R/L1XR/Validity/...), и он же делением на ширину даёт тексель атласа - см.
	/// <see cref="ProbeTexel"/>.</summary>
	internal static int StorageIndex(int sx, int sy, int sz, int cx, int cy) =>
		(sz * cy + sy) * cx + sx;

	/// <summary>Тексель пробы в SH-атласе по её линейному индексу хранения. Ширина атласа равна оси X
	/// сетки, поэтому деление с остатком и есть вся адресация (см.
	/// <see cref="ProbeGiBakeResult.ShWidth"/>).</summary>
	private static (int X, int Y) ProbeTexel(int storageIndex, int cx) =>
		(storageIndex % cx, storageIndex / cx);

	/// <summary>Захватывает поверхности сцены в разреженную воксельную сетку (см.
	/// <see cref="SurfaceCache"/>): для каждого вокселя, где есть геометрия, запоминает точку на
	/// поверхности, нормаль и альбедо - взвешенные площадью средние по попавшим треугольникам.
	/// Чистая геометрия, считается один раз на сессию.</summary>
	private SurfaceCache BuildSurfaceCache(Vector3 origin, Vector3 cell, int cx, int cy, int cz)
	{
		const int sub = SurfaceCache.Subdivision;
		var voxel = cell / sub;
		int vx = Math.Max(1, (cx - 1) * sub);
		int vy = Math.Max(1, (cy - 1) * sub);
		int vz = Math.Max(1, (cz - 1) * sub);

		var cache = new SurfaceCache(origin, voxel, vx, vy, vz);
		int total = vx * vy * vz;
		var dense = new int[total];
		var posSum = new Vector3[total];
		var normalSum = new Vector3[total];
		var albedoSum = new Vector3[total];
		var areaSum = new float[total];

		// Идём по треугольникам, а не по вокселям: сцена - это сотни тысяч треугольников против
		// миллионов вокселей, и почти все вокселя пустые. Треугольник раскладывается по вокселям
		// своего AABB (консервативно - AABB, а не точное пересечение).
		var lockObj = new object();
		Parallel.For(0, _tris.Length, () => (Voxels: new Dictionary<int, (Vector3 P, Vector3 N, Vector3 A, float W)>(), Dummy: 0),
			(t, _, local) =>
		{
			ref var tri = ref _tris[t];
			var b = tri.A + tri.E1;
			var c = tri.A + tri.E2;
			var cross = Vector3.Cross(tri.E1, tri.E2);
			float area = cross.Length() * 0.5f;
			if (area <= 1e-12f)
			{
				return local;
			}

			var normal = cross / (area * 2f);
			var centroid = (tri.A + b + c) / 3f;
			var triMin = Vector3.Min(tri.A, Vector3.Min(b, c));
			var triMax = Vector3.Max(tri.A, Vector3.Max(b, c));

			var lo = (triMin - origin) / voxel;
			var hi = (triMax - origin) / voxel;
			int x0 = Math.Clamp((int)MathF.Floor(lo.X), 0, vx - 1), x1 = Math.Clamp((int)MathF.Floor(hi.X), 0, vx - 1);
			int y0 = Math.Clamp((int)MathF.Floor(lo.Y), 0, vy - 1), y1 = Math.Clamp((int)MathF.Floor(hi.Y), 0, vy - 1);
			int z0 = Math.Clamp((int)MathF.Floor(lo.Z), 0, vz - 1), z1 = Math.Clamp((int)MathF.Floor(hi.Z), 0, vz - 1);

			// Крупный треугольник (пол, стена) накрывает много вокселей - в каждый кладём его точку,
			// зажатую в этот воксель, иначе центроид увёл бы позицию вокселя за его пределы.
			for (int z = z0; z <= z1; z++)
			for (int y = y0; y <= y1; y++)
			for (int x = x0; x <= x1; x++)
			{
				int v = (z * vy + y) * vx + x;
				var boxMin = origin + new Vector3(x * voxel.X, y * voxel.Y, z * voxel.Z);
				var point = Vector3.Clamp(centroid, boxMin, boxMin + voxel);
				var add = (point * area, normal * area, tri.Albedo * area, area);
				local.Voxels[v] = local.Voxels.TryGetValue(v, out var prev)
					? (prev.P + add.Item1, prev.N + add.Item2, prev.A + add.Item3, prev.W + area)
					: add;
			}

			return local;
		},
		local =>
		{
			lock (lockObj)
			{
				foreach (var (v, acc) in local.Voxels)
				{
					posSum[v] += acc.P;
					normalSum[v] += acc.N;
					albedoSum[v] += acc.A;
					areaSum[v] += acc.W;
				}
			}
		});

		int count = 0;
		for (int v = 0; v < total; v++)
		{
			dense[v] = areaSum[v] > 1e-12f ? count++ : -1;
		}

		cache.Allocate(dense, count);
		for (int v = 0; v < total; v++)
		{
			int slot = dense[v];
			if (slot < 0)
			{
				continue;
			}

			float inv = 1f / areaSum[v];
			cache.Position[slot] = posSum[v] * inv;
			var n = normalSum[v] * inv;
			float len = n.Length();
			// Нормали сошлись в ноль (воксель на ребре, где грани смотрят навстречу) - берём любую
			// осмысленную: такой воксель всё равно почти не виден.
			cache.Normal[slot] = len > 1e-4f ? n / len : Vector3.UnitY;
			cache.Albedo[slot] = albedoSum[v] * inv;
		}

		return cache;
	}

	/// <summary>Строит захват поверхностей, если он заказан и ещё не построен. Обычно это делает
	/// первый раунд (захват стоит сотни миллисекунд и не должен вставать на главном потоке), но
	/// GPU-пути кэш нужен уже при создании буферов - там он вызывается напрямую.</summary>
	public void EnsureSurfaceCache(ProbeGiBakeSession s)
	{
		// Реальному времени кэш не нужен ВООБЩЕ (см. RunRound): его статичная геометрия врёт на
		// движущейся сцене, отскок идёт из поля. Один гейт здесь закрывает все места вызова и
		// заодно экономит сотни миллисекунд захвата на главном потоке при создании сессии.
		// WantsSurfaceCache НЕ сбрасывается: живое переключение в запечку достроит кэш первым же
		// её раундом.
		if (!s.WantsSurfaceCache || s.Realtime)
		{
			return;
		}

		s.WantsSurfaceCache = false;
		s.Surface = BuildSurfaceCache(s.Origin, s.Cell, s.CountX, s.CountY, s.CountZ);
	}

	/// <summary>Прямой свет punctual-светов сцены в точке поверхности - подход RTXGI (теневой луч к
	/// свету с tmax до него) с формулами затухания из UnlitInstancedPS (Frostbite-окно + конус
	/// спота), чтобы отскок сходился с прямым светом шейдинга один в один. Вклад ламп идёт в
	/// СТАТИЧНУЮ долю освещения (знаменатель SunFraction, не числитель): реалтайм-модуляция
	/// солнечной тенью света ламп касаться не должна. Дистанция теневого луча укорочена на эпсилон
	/// с обеих сторон - от самозатенения поверхности и от «попадания» в геометрию источника.</summary>
	private Vector3 EvalPunctualLights(PunctualLight[] lights, Vector3 pos, Vector3 normal)
	{
		if (lights.Length == 0)
		{
			return Vector3.Zero;
		}

		var sum = Vector3.Zero;
		for (int i = 0; i < lights.Length; i++)
		{
			ref var l = ref lights[i];
			var lightPos = new Vector3(l.PositionRange.X, l.PositionRange.Y, l.PositionRange.Z);
			float range = l.PositionRange.W;
			var toLight = lightPos - pos;
			float distSq = toLight.LengthSquared();
			if (distSq > range * range)
			{
				continue;
			}

			float dist = MathF.Sqrt(MathF.Max(distSq, 1e-6f));
			var dir = toLight / dist;
			float ndotl = Vector3.Dot(normal, dir);
			if (ndotl <= 0f)
			{
				continue;
			}

			// Гладкое окно затухания - зеркало кластерного шейдинга UnlitInstancedPS.
			float distRatio = dist / range;
			float distRatio2 = distRatio * distRatio;
			float distFactor = Math.Clamp(1f - distRatio2 * distRatio2, 0f, 1f);
			float atten = distFactor * distFactor / (distSq + 1e-2f);

			if (l.DirectionType.W > 0.5f)
			{
				float cd = Vector3.Dot(-dir,
					new Vector3(l.DirectionType.X, l.DirectionType.Y, l.DirectionType.Z));
				float spotFactor = Math.Clamp((cd - l.SpotAngles.X) * l.SpotAngles.Y, 0f, 1f);
				atten *= spotFactor * spotFactor;
				if (atten <= 0f)
				{
					continue;
				}
			}

			float shadowStart = _sceneEpsilon * 4f;
			if (TraceAnyHit(pos + dir * shadowStart, dir, dist - shadowStart * 2f))
			{
				continue;
			}

			sum += new Vector3(l.ColorIntensity.X, l.ColorIntensity.Y, l.ColorIntensity.Z)
				* l.ColorIntensity.W * (ndotl * atten);
		}

		return sum;
	}

	/// <summary>Пересчитывает исходящий радианс кэша поверхностей. Резкая часть - солнце с теневым
	/// лучом на каждый воксель (это и даёт отскоку детализацию, недоступную сетке проб); гладкая -
	/// небо и переотскок, взятые из поля проб, которому такой детализации и не нужно. Один луч на
	/// воксель за раунд, поэтому кэш идёт в ногу с пробами, а не удваивает стоимость бейка.</summary>
	private void UpdateSurfaceCache(ProbeGiBakeSession s)
	{
		var cache = s.Surface;
		if (cache == null || cache.VoxelCount == 0)
		{
			return;
		}

		var sunDir = s.SunDirection;
		var sunColor = s.SunColor;
		var bakeLights = s.BakeLights;
		float feedback = s.Feedback;
		float bounceSaturation = s.BounceSaturation;
		var l0R = s.L0R; var l1xR = s.L1XR; var l1yR = s.L1YR; var l1zR = s.L1ZR;
		var validityR = s.ValidityR; var sunFracR = s.SunFracR;
		float offset = _sceneEpsilon * 4f;

		static float Lum(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

		Parallel.For(0, cache.VoxelCount, v =>
		{
			var normal = cache.Normal[v];
			var pos = cache.Position[v] + normal * offset;

			var sunIrradiance = Vector3.Zero;
			float ndotl = Vector3.Dot(normal, sunDir);
			if (ndotl > 0f && !TraceAnyHit(pos, sunDir, _rayTMax))
			{
				sunIrradiance = sunColor * ndotl;
			}

			// Прямой свет ламп - в «статичную» часть, вместе с небом: солнечная тень реального
			// времени модулирует только солнечную долю (см. EvalPunctualLights).
			var lampIrradiance = EvalPunctualLights(bakeLights, pos, normal);

			// Небо и переотскок - из поля проб. Оно грубое, но именно эта часть освещения меняется
			// в пространстве плавно, так что разрешения сетки проб ей хватает.
			var ambient = Vector3.Zero;
			float ambientFrac = 0f;
			if (feedback > 0f)
			{
				ambient = EvalIrradiance(s, l0R, l1xR, l1yR, l1zR, validityR, sunFracR,
					pos, normal, out ambientFrac) * feedback;
			}

			var irradiance = sunIrradiance + lampIrradiance + ambient;
			var albedo = Vector3.Lerp(new Vector3(Lum(cache.Albedo[v])), cache.Albedo[v], bounceSaturation);
			cache.Radiance[v] = albedo * irradiance * (1f / MathF.PI);

			float lumIrr = Lum(irradiance);
			cache.SunFraction[v] = lumIrr > 1e-6f
				? Math.Clamp((Lum(sunIrradiance) + Lum(ambient) * ambientFrac) / lumIrr, 0f, 1f)
				: 0f;
		});
	}

	// --- Бейк ---------------------------------------------------------------------------------

	/// <summary>Вес раунда снизу (см. <see cref="RunRound"/>): бегущее среднее 1/(Round+1) на очень
	/// длинных сессиях загнало бы вес в ноль и заморозило поле намертво.</summary>
	internal const float MinRoundBlend = 0.02f;

	/// <summary>Пол веса раунда в режиме реального времени (см.
	/// <see cref="ProbeGiBakeOptions.Realtime"/>) - он же альфа экспоненциального среднего, к которой
	/// сходится вес после первых раундов.
	///
	/// Размен прямой: возмущение затухает как (1-alpha)^n, а остаточное дрожание ОТДЕЛЬНОЙ пробы
	/// идёт как sqrt(alpha/(2-alpha)) от дисперсии одного раунда. Редактор выпускает не больше
	/// одного раунда за кадр, так что при 60 к/с 0.04 - это установление за ~1.2 секунды.
	///
	/// Замерено на Sponza при 64 лучах (см. SceneTraceVerifier.MeasureFlicker; p99 и максимум - по
	/// относительной смене пробы за раунд, доля - сколько проб дёрнулось больше чем на 10%):
	///
	///   alpha 0.15 - p99 6.3%, max 79%, доля 0.6%   (мигание видно отчётливо)
	///   alpha 0.08 - p99 3.4%, max 48%, доля 0.0%
	///   alpha 0.04 - p99 1.8%, max 24%, доля 0.0%   (выбрано)
	///   alpha 0.02 - p99 1.0%, max 12%, доля 0.0%   (отклик уже за 2.5 с)
	///
	/// Против ХВОСТА распределения альфа работает много лучше числа лучей: учетверение лучей при
	/// 0.15 убирает дыхание сцены, но отдельные пробы продолжают дёргаться, потому что их разброс
	/// делает не шум оценки, а смена того, во что попадает веер.</summary>
	public const float RealtimeBlend = 0.04f;

	/// <summary>Длина окна релокации в раундах - ПЯТЬ, как в Majercik 2021 (§5: «cap the number of
	/// iterations at five to prevent probes from moving back and forth (infinitely) through tangent
	/// backfaces»). Длинное окно (пробовали 32) не улучшает позиции, а даёт касательным задним
	/// граням качать пробу туда-обратно.</summary>
	internal const int RelocationRounds = 5;

	/// <summary>Разгонные раунды, НЕ попадающие в усреднение. Отскок собирается из текущего поля, и
	/// у самого первого раунда это поле пустое - его радианс занижен ровно на весь мультибаунс.
	/// Копить такие раунды в общее среднее нельзя: холодный старт остался бы в результате навсегда
	/// (замерено на Sponza - поле выходило процентов на восемь темнее прежнего бейка). Поэтому
	/// первые раунды идут с полным весом, только раскачивая отскок (сходится геометрически, трёх
	/// хватает), а усреднение стартует уже по прогретому полю.</summary>
	private const int BootstrapRounds = 3;

	/// <summary>К какому «возрасту» откатывается сходимость при смене освещения (см.
	/// <see cref="ProbeGiBakeSession.SetLighting"/>). Разгон заново не нужен - старое поле остаётся
	/// приличным стартовым приближением для отскока, - но вес первого раунда после смены должен
	/// быть большим, чтобы новое решение проступило за единицы раундов.</summary>
	internal const int RestartRound = BootstrapRounds + 1;

	/// <summary>Минимум УСРЕДНЯЕМЫХ раундов независимо от
	/// <see cref="ProbeGiBakeOptions.RaysPerProbe"/> - иначе на низком качестве поле остаётся
	/// откровенно шумным.</summary>
	private const int MinAveragedRounds = 4;

	/// <summary>Синхронный бейк до сходимости - обёртка над сессией для headless-путей вроде
	/// PreviewProbe, которым нечего ждать между кадрами. Редактор вместо этого крутит
	/// <see cref="RunRound"/> из PollProbeBake и показывает поле, не дожидаясь сходимости.</summary>
	public ProbeGiBakeResult Bake(Vector3 boundsMin, Vector3 boundsMax, Vector3 sunDirection,
		Vector3 sunColor, float envYawRadians, Func<Vector3, Vector3> skyRadiance,
		ProbeGiBakeOptions? options = null, PunctualLight[]? punctualLights = null)
	{
		var session = BeginBake(boundsMin, boundsMax, sunDirection, sunColor, envYawRadians,
			skyRadiance, options, punctualLights);

		// Условие явное, а не Converged: синхронный бейк обязан завершиться, даже если в настройках
		// стоит режим реального времени (там сходимости нет по определению).
		while (!session.NoGeometry && session.Round < session.TargetRounds)
		{
			RunRound(session);
		}

		return Snapshot(session);
	}

	/// <summary>Раскладывает сетку проб по баундам сцены и заводит аккумуляторы прогрессивного
	/// бейка - лучей не пускает, так что звать можно и с главного потока. Дальше крутите
	/// <see cref="RunRound"/> в фоне, пока <see cref="ProbeGiBakeSession.Converged"/> не станет
	/// true, а <see cref="Snapshot"/> - между раундами: поле пригодно к показу уже после первого.
	/// skyRadiance - линейный радианс неба по мировому направлению ДО пользовательского поворота
	/// (envYaw применяется внутри, той же конвенцией, что SampleEnvironment в шейдере).
	/// sunDirection - НА солнце.</summary>
	public ProbeGiBakeSession BeginBake(Vector3 boundsMin, Vector3 boundsMax, Vector3 sunDirection,
		Vector3 sunColor, float envYawRadians, Func<Vector3, Vector3> skyRadiance,
		ProbeGiBakeOptions? options = null, PunctualLight[]? punctualLights = null)
	{
		options ??= new ProbeGiBakeOptions();
		float density = Math.Clamp(options.GridDensity, 4f, 64f);
		int maxProbes = Math.Clamp(options.MaxProbes, MinProbeBudget, MaxProbeBudget);

		// Сетка: ячейка ~1/density крупнейшего измерения, не больше maxProbes, минимум 2 по оси.
		var size = Vector3.Max(boundsMax - boundsMin, new Vector3(1e-3f));
		float maxDim = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
		var margin = new Vector3(maxDim * 0.02f);
		var min = boundsMin - margin;
		var full = size + margin * 2f;

		// Сетка кирпичей: укрупняем ячейку, пока ЖИВЫЕ кирпичи не влезут в бюджет. Считать бюджет
		// по живым, а не по всему баунду - и есть весь смысл разреженности: на сцене-уровне пустое
		// пространство занимает большую часть коробки, и раньше оно съедало бюджет наравне с
		// геометрией, заставляя грубеть сетку у поверхностей.
		// Сетка ПЛОТНАЯ, поэтому подбор размера стал арифметикой: осматривать геометрию, чтобы
		// узнать цену раскладки, больше не нужно - цена есть произведение сторон. Вместе с осмотром
		// ушли ScrollHeadroom, поиск худшего размещения коробки и весь класс отказов «ran out of
		// pool slots»: слотов, которых может не хватить, больше нет.
		float cellTarget = MathF.Max(maxDim, 1e-3f) / density;
		int cx, cy, cz;
		while (true)
		{
			cx = ProbesPerAxis(full.X, cellTarget);
			cy = ProbesPerAxis(full.Y, cellTarget);
			cz = ProbesPerAxis(full.Z, cellTarget);

			// Атлас видимости крупнее SH-атласа в VisRes раз по обеим осям и в предел упирается
			// первым (см. MaxAtlasDimension).
			long probes = (long)cx * cy * cz;
			bool fitsBudget = probes <= maxProbes;
			bool fitsAtlas = (long)cx * ProbeGiBakeResult.VisRes <= MaxAtlasDimension
				&& (long)cz * cy * ProbeGiBakeResult.VisRes <= MaxAtlasDimension;
			if ((fitsBudget && fitsAtlas) || (cx <= 2 && cy <= 2 && cz <= 2))
			{
				break;
			}

			cellTarget *= 1.25f;
		}

		var cell = new Vector3(full.X / (cx - 1), full.Y / (cy - 1), full.Z / (cz - 1));

		// Усредняемых раундов ровно столько, чтобы набрать заказанные RaysPerProbe лучей; сверху -
		// разгон. Итоговое качество сошедшегося поля выходит тем же, что у прежнего бейка одним
		// куском, только приходит оно постепенно.
		int averagedRounds = Math.Max(MinAveragedRounds,
			(int)MathF.Ceiling(Math.Clamp(options.RaysPerProbe, 16, 512)
				/ (float)Math.Clamp(options.RaysPerRound, 4, 128)));

		var session = new ProbeGiBakeSession(min, cell, cx, cy, cz, options,
			Vector3.Normalize(sunDirection), sunColor, envYawRadians, skyRadiance,
			BootstrapRounds + averagedRounds);

		// Захват поверхностей (сотни миллисекунд на сцене-уровне) откладывается до первого раунда:
		// BeginBake зовётся с ГЛАВНОГО потока, и здесь он встал бы видимым фризом редактора.
		session.WantsSurfaceCache = options.SurfaceCache;
		if (punctualLights is { Length: > 0 })
		{
			session.BakeLights = punctualLights;
		}

		return session;
	}

	/// <summary>Число ПРОБ по оси под заданный шаг ячейки: ячеек столько, чтобы накрыть протяжённость,
	/// проб на одну больше. Минимум два - иначе не из чего интерполировать.</summary>
	private static int ProbesPerAxis(float extent, float cellTarget) =>
		Math.Clamp((int)MathF.Ceiling(extent / cellTarget) + 1, 2, MaxProbesPerAxis);

	/// <summary>Осматривает коробку: есть ли в ней геометрия и насколько сонаправлены нормали её
	/// треугольников (см. <see cref="FlatNormalCoherence"/>). Нормали взвешиваются площадью - иначе
	/// россыпь мелких треугольников декора перевесила бы плиту пола, на которой они лежат.</summary>
	private (bool HasGeometry, float Coherence) InspectBox(Vector3 boxMin, Vector3 boxMax)
	{
		if (_nodeCount == 0)
		{
			return (false, 0f);
		}

		var normalSum = Vector3.Zero;
		float areaSum = 0f;

		Span<int> stack = stackalloc int[64];
		int sp = 0;
		stack[sp++] = 0;

		while (sp > 0)
		{
			ref var node = ref _nodes[stack[--sp]];
			if (node.Min.X > boxMax.X || node.Max.X < boxMin.X ||
				node.Min.Y > boxMax.Y || node.Max.Y < boxMin.Y ||
				node.Min.Z > boxMax.Z || node.Max.Z < boxMin.Z)
			{
				continue;
			}

			if (node.Left < 0)
			{
				for (int i = node.Start; i < node.Start + node.Count; i++)
				{
					ref var tri = ref _tris[_order[i]];
					var b = tri.A + tri.E1;
					var c = tri.A + tri.E2;
					var triMin = Vector3.Min(tri.A, Vector3.Min(b, c));
					var triMax = Vector3.Max(tri.A, Vector3.Max(b, c));
					if (triMin.X > boxMax.X || triMax.X < boxMin.X ||
						triMin.Y > boxMax.Y || triMax.Y < boxMin.Y ||
						triMin.Z > boxMax.Z || triMax.Z < boxMin.Z)
					{
						continue;
					}

					// Векторное произведение рёбер - нормаль длиной в две площади: и направление, и
					// вес в одном значении.
					var cross = Vector3.Cross(tri.E1, tri.E2);
					normalSum += cross;
					areaSum += cross.Length();
				}
			}
			else if (sp + 2 <= stack.Length)
			{
				stack[sp++] = node.Left;
				stack[sp++] = node.Start;
			}
		}

		return areaSum > 1e-12f ? (true, normalSum.Length() / areaSum) : (false, 0f);
	}

	/// <summary>Один раунд прогрессивного бейка: пускает RaysPerRound лучей на пробу повёрнутым
	/// веером Фибоначчи, вливает радианс в поле бегущим средним и копит геометрические суммы
	/// (видимость неба, валидность, окто-карта глубин). Это тяжёлая часть - зовите в фоне; внутри
	/// раунд параллелится по пробам, но сами раунды обязаны идти строго по одному.</summary>
	public void RunRound(ProbeGiBakeSession s)
	{
		if (!HasGeometry)
		{
			// Печь нечего - помечаем сессию сошедшейся, чтобы вызывающий не крутил пустые раунды.
			// Номера раунда для этого мало: в реальном времени сходимости нет, и вызывающий гонял бы
			// пустые раунды вечно.
			s.NoGeometry = true;
			s.Round = s.TargetRounds;
			return;
		}

		// В реальном времени кэш поверхностей не захватывается, не обновляется и не читается -
		// зеркало GPU-раунда (см. ProbeRelocation.z в ProbeRoundCS.hlsl): его статичная геометрия
		// врёт на движущейся сцене, отскок идёт из поля проб в точке попадания.
		SurfaceCache? surface = null;
		if (!s.Realtime)
		{
			EnsureSurfaceCache(s);

			// Кэш обновляется ПЕРЕД лучами раунда: он собирает небо и переотскок из поля прошлого
			// раунда, а лучи этого раунда уже забирают из него свежий радианс. Так кэш и поле
			// сходятся вместе, не обгоняя друг друга.
			UpdateSurfaceCache(s);
			surface = s.Surface;
		}

		int rays = s.RaysPerRound;
		var dirs = BuildRotatedFibonacciSphere(rays, s.Sequence++);

		// Вес нового раунда. Разгонные раунды кладутся целиком (alpha = 1): они не усредняются, а
		// только раскачивают отскок, из которого будут собирать последующие. Дальше - честное
		// бегущее среднее по УСРЕДНЯЕМЫМ раундам; пол не даёт весу схлопнуться в ноль на длинных
		// сессиях, а в реальном времени он же и держит окно усреднения конечным.
		float alpha = RoundBlendWeight(s);

		int cx = s.CountX, cy = s.CountY, cz = s.CountZ;
		int probeCount = s.ProbeCount;
		var origin = s.Origin;
		var cell = s.Cell;
		var sunDir = s.SunDirection;
		var sunColor = s.SunColor;
		var bakeLights = s.BakeLights;
		float bounceSaturation = s.BounceSaturation;
		float feedback = s.Feedback;
		float maxRayLuminance = s.MaxRayLuminance;
		float maxStep = s.MaxStep;
		float accumGamma = s.AccumulationGamma;
		float relocLimit = s.RelocationLimit;
		var probeOffsets = s.ProbeOffset;
		float visMax = cell.Length() * 1.5f;
		float gatherOffset = cell.Length() * 0.05f;

		// Поворот энвайронмента: шейдер сдвигает equirect-U на +yaw (см. SampleEnvironment), т.е.
		// мировое направление d видит небо в направлении с азимутом φ+yaw. SkyIntensity - ручка
		// яркости небесного эмбиента (окно Graphics).
		var skyRadiance = s.SkyRadiance;
		float skyIntensity = s.SkyIntensity;
		float cosYaw = MathF.Cos(s.EnvYaw), sinYaw = MathF.Sin(s.EnvYaw);
		Vector3 RotatedSky(Vector3 d) => skyRadiance(new Vector3(
			d.X * cosYaw - d.Z * sinYaw, d.Y, d.X * sinYaw + d.Z * cosYaw)) * skyIntensity;

		const float y00 = 0.28209479f;
		const float y1 = 0.48860251f;
		int res = ProbeGiBakeResult.VisRes;
		float domega = 4f * MathF.PI / rays;

		static float Lum(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

		// Раунд ЧИТАЕТ прошлое поле (мультибаунс собирается по соседним пробам в точках попаданий)
		// и ПИШЕТ новое - см. двойной буфер в ProbeGiBakeSession.
		var l0R = s.L0R; var l1xR = s.L1XR; var l1yR = s.L1YR; var l1zR = s.L1ZR;
		var validityR = s.ValidityR; var sunFracR = s.SunFracR;
		var l0W = s.L0W; var l1xW = s.L1XW; var l1yW = s.L1YW; var l1zW = s.L1ZW;
		var validityW = s.ValidityW; var sunFracW = s.SunFracW;

		int gridX = s.CountX, gridY = s.CountY, gridZ = s.CountZ;

		// Обход идёт по индексу ХРАНЕНИЯ - тому же, которым адресованы все буферы поля и тексель
		// атласа. Объём неподвижен (прокрутки больше нет), так что координаты хранения и есть
		// координаты сетки.
		Parallel.For(0, probeCount, p =>
		{
			int px = p % gridX;
			int py = p / gridX % gridY;
			int pz = p / (gridX * gridY);

			// Трассируем из АКТУАЛЬНОЙ позиции - с учётом накопленной релокации (зеркало
			// ProbeRoundCS.hlsl): иначе статистика задних граней описывала бы узел сетки, а не то
			// место, где проба стоит, и релокация не сошлась бы.
			var probeOffset = probeOffsets[p];
			var probePos = origin + new Vector3(px * cell.X, py * cell.Y, pz * cell.Z) + probeOffset;

			// Шаг проб у плотной сетки один на весь объём - уровней подразделения, под которые этот
			// кламп раньше подстраивался, больше нет.
			float probeVisMax = visMax;

			var sum0 = Vector3.Zero;
			var sumX = Vector3.Zero;
			var sumY = Vector3.Zero;
			var sumZ = Vector3.Zero;
			float sunLum = 0f, totalLum = 0f;
			int missCount = 0, backCount = 0;
			int visBase = p * res * res;

			// Для релокации - зеркало ProbeRoundCS.hlsl: ближайшая ЗАДНЯЯ грань есть ближайший
			// выход наружу, ближайшая передняя - мера свободного места вокруг.
			float closestBackT = _rayTMax, closestFrontT = _rayTMax;
			var closestBackDir = Vector3.UnitY;

			for (int r = 0; r < rays; r++)
			{
				var dir = dirs[r];
				Vector3 radiance;
				float sunShare = 0f;
				float hitT;

				if (!TraceClosest(probePos, dir, out float t, out int triIndex))
				{
					radiance = RotatedSky(dir);
					missCount++;
					hitT = _rayTMax;
				}
				else
				{
					hitT = t;
					ref var tri = ref _tris[triIndex];
					var normal = Vector3.Normalize(Vector3.Cross(tri.E1, tri.E2));
					if (Vector3.Dot(normal, dir) > 0f)
					{
						// Задняя грань = луч вышел изнутри геометрии (проба в стене).
						radiance = Vector3.Zero;
						backCount++;

						// Порядок важен: релокации нужна ПОЛНАЯ дистанция (ближайший выход из
						// стены), укорачивание - только для записи в глубину.
						if (t < closestBackT)
						{
							closestBackT = t;
							closestBackDir = dir;
						}

						// Глубина задней грани укорачивается на 80% - зеркало ProbeRoundCS.hlsl
						// (Majercik 2021, §4.1): тест Чебышёва должен считать её заслоняющей, иначе
						// проба заявляет, что видит в эту сторону далеко, и свет течёт сквозь стену.
						hitT = t * 0.2f;
					}
					else
					{
						closestFrontT = MathF.Min(closestFrontT, t);
						var hitPos = probePos + dir * t;

						// Кэш поверхностей (см. SurfaceCache): у точки попадания уже есть готовый
						// исходящий радианс, посчитанный на СВОЁМ разрешении - вчетверо мельче шага
						// проб. Это и есть смысл surface GI: отскок берётся с детализацией
						// геометрии, а не размазывается по ячейке сетки проб.
						int voxel = surface?.Lookup(hitPos + normal * gatherOffset) ?? -1;
						if (voxel >= 0)
						{
							radiance = surface!.Radiance[voxel];
							sunShare = surface.SunFraction[voxel];
						}
						else
						{
							// Кэша тут нет (воксель не захвачен) - считаем отскок по-старому, из
							// поля проб.
							var sunIrradiance = Vector3.Zero;
							float ndotl = Vector3.Dot(normal, sunDir);
							if (ndotl > 0f &&
								!TraceAnyHit(hitPos + normal * (_sceneEpsilon * 4f), sunDir, _rayTMax))
							{
								sunIrradiance = sunColor * ndotl;
							}

							// Лампы - в статичную долю, как в UpdateSurfaceCache.
							var lampIrradiance = EvalPunctualLights(bakeLights,
								hitPos + normal * (_sceneEpsilon * 4f), normal);

							var prevIrradiance = Vector3.Zero;
							float prevFrac = 0f;
							if (feedback > 0f)
							{
								prevIrradiance = EvalIrradiance(s, l0R, l1xR, l1yR, l1zR, validityR,
									sunFracR, hitPos + normal * gatherOffset, normal, out prevFrac) * feedback;
							}

							var irradiance = sunIrradiance + lampIrradiance + prevIrradiance;

							// Хрома-кламп альбедо: тянем цвет к собственной люме, ЯРКОСТЬ не меняем
							// (lerp к Lum линеен - Lum(результата) == Lum(альбедо)). Поэтому
							// солнечный баунс от камня/пола по силе прежний, а насыщенная ткань
							// перестаёт быть цветной лампочкой.
							var albedo = Vector3.Lerp(new Vector3(Lum(tri.Albedo)), tri.Albedo, bounceSaturation);
							radiance = albedo * irradiance * (1f / MathF.PI);

							// Солнечная доля яркости этого луча: прямой вклад солнца + солнечная
							// часть собранного поля (переотскок наследует долю источника).
							float lumIrr = Lum(irradiance);
							sunShare = lumIrr > 1e-6f
								? (Lum(sunIrradiance) + Lum(prevIrradiance) * prevFrac) / lumIrr
								: 0f;
						}
					}
				}

				// Подавление выбросов - зеркало GPU-раунда (см. ProbeRoundCS.hlsl): редкий луч в
				// очень яркое двигает пробу целиком, и числом лучей это не лечится. В запечке
				// потолок нулевой, кламп не срабатывает вовсе.
				if (maxRayLuminance > 0f)
				{
					float rayLum = Lum(radiance);
					if (rayLum > maxRayLuminance)
					{
						// Масштаб, а не обрезание по каналам: обрезание увело бы цвет.
						radiance *= maxRayLuminance / rayLum;
					}
				}

				// Окто-карта глубин (DDGI depth), точные суммы по всем раундам. Кламп по масштабу
				// ячейки, как в оригинальном DDGI: без него луч-промах вносит в среднюю глубину
				// октанта дистанцию в несколько габаритов сцены, средняя «видимость» становится
				// огромной, и тест Чебышёва не срабатывает НИКОГДА - протечки у стыков остаются.
				// Луч размазывается по КОНУСУ текселей - зеркало ProbeRoundCS.hlsl и §4.4 статьи
				// Majercik 2019. При укладке в один ближайший тексель большинству из 64 октантов не
				// достаётся ни одного сэмпла за раунд, и тест Чебышёва работает по карте, которой
				// почти нет.
				float tv = MathF.Min(hitT, probeVisMax);
				for (int dt = 0; dt < res * res; dt++)
				{
					var texelUv = new Vector2((dt % res + 0.5f) / res, (dt / res + 0.5f) / res);
					float w = MathF.Max(0f, Vector3.Dot(OctDecode(texelUv), dir));
					for (int sq = 0; sq < DepthSharpnessSquarings; sq++)
					{
						w *= w;
					}

					if (w < DepthWeightEpsilon)
					{
						continue;
					}

					int visAt = visBase + dt;
					s.VisSumT[visAt] += tv * w;
					s.VisSumT2[visAt] += tv * tv * w;
					s.VisWeight[visAt] += w;
				}

				float lum = Lum(radiance);
				sunLum += lum * sunShare;
				totalLum += lum;

				sum0 += radiance * (y00 * domega);
				sumX += radiance * (y1 * dir.X * domega);
				sumY += radiance * (y1 * dir.Y * domega);
				sumZ += radiance * (y1 * dir.Z * domega);
			}

			var new0 = Vector3.Lerp(l0R[p], sum0, alpha);
			var new1 = Vector3.Lerp(l1xR[p], sumX, alpha);
			var new2 = Vector3.Lerp(l1yR[p], sumY, alpha);
			var new3 = Vector3.Lerp(l1zR[p], sumZ, alpha);

			// Перцептивное накопление - зеркало GPU-раунда (см. ProbeRoundCS.hlsl и
			// ProbeGiBakeOptions.RealtimeGamma): яркость движется по гамма-кривой, направленность
			// не трогается. В запечке accumGamma = 1 и блок мёртв.
			if (accumGamma > 1f && alpha < 1f)
			{
				float lumOld = Lum(l0R[p]);
				float lumNew = Lum(sum0);
				float lumLinear = Lum(new0);

				// Только на потемнение - зеркало ProbeRoundCS.hlsl (симметричная кривая душила
				// подъём из темноты).
				if (lumNew < lumOld && lumLinear > 1e-6f)
				{
					float invGamma = 1f / accumGamma;
					float lumPerceptual = MathF.Pow(
						MathF.Pow(MathF.Max(lumOld, 0f), invGamma) * (1f - alpha)
							+ MathF.Pow(MathF.Max(lumNew, 0f), invGamma) * alpha,
						accumGamma);
					float k = lumPerceptual / lumLinear;
					new0 *= k;
					new1 *= k;
					new2 *= k;
					new3 *= k;
				}
			}

			// Ограничитель скорости - зеркало GPU-раунда (см. ProbeRoundCS.hlsl): режем производную,
			// а не величину, поэтому установившееся значение не смещается.
			if (maxStep > 0f && alpha < 1f)
			{
				var delta = new0 - l0R[p];
				float deltaLen = delta.Length();
				float scale = 0.5f * (l0R[p].Length() + new0.Length()) + 1e-4f;
				float limit = maxStep * scale;
				if (deltaLen > limit)
				{
					// Один множитель на все полосы SH: порознь они изменили бы направленность поля.
					float k = limit / deltaLen;
					new0 = l0R[p] + (new0 - l0R[p]) * k;
					new1 = l1xR[p] + (new1 - l1xR[p]) * k;
					new2 = l1yR[p] + (new2 - l1yR[p]) * k;
					new3 = l1zR[p] + (new3 - l1zR[p]) * k;
				}
			}

			l0W[p] = new0;
			l1xW[p] = new1;
			l1yW[p] = new2;
			l1zW[p] = new3;

			// Релокация - зеркало ProbeRoundCS.hlsl: проба, стоящая внутри стены или колонны,
			// отодвигается наружу через ближайшую заднюю грань.
			bool relocated = false;
			if (relocLimit > 0f)
			{
				float backFrac = backCount / (float)rays;
				var newOffset = probeOffset;
				float offsetLen = probeOffset.Length();

				if (backFrac > 0.25f && closestBackT < _rayTMax)
				{
					newOffset = probeOffset + closestBackDir * (closestBackT + gatherOffset);
				}
				// Возврата к узлу нет - зеркало ProbeRoundCS.hlsl: у тонкой геометрии он качал пробу
				// туда-обратно с сбросом накопителей на каждый переезд.

				float newLen = newOffset.Length();
				if (newLen > relocLimit)
				{
					newOffset *= relocLimit / newLen;
				}

				// Порог, а не любое движение: возврат к узлу идёт долями (0.75 за раунд), и сброс
				// на каждый мелкий шаг держал бы пробу в вечном холодном старте.
				relocated = (newOffset - probeOffset).Length() > relocLimit * 0.1f;
				probeOffsets[p] = newOffset;
			}

			float roundSunFrac = totalLum > 1e-6f ? Math.Clamp(sunLum / totalLum, 0f, 1f) : 0f;
			sunFracW[p] = sunFracR[p] + (roundSunFrac - sunFracR[p]) * alpha;

			// Видимость неба и валидность зависят только от геометрии, не от света - копим их
			// точными долями по ВСЕМ раундам сессии. Поэтому поворот солнца их не обесценивает:
			// именно тут прогрессивный бейк выигрывает у прежнего полного ребейка.
			int rayTotal = s.RayTotal[p] + rays;
			int missTotal = s.MissTotal[p] + missCount;
			int backTotal = s.BackTotal[p] + backCount;
			s.RayTotal[p] = rayTotal;
			s.MissTotal[p] = missTotal;
			s.BackTotal[p] = backTotal;
			s.SkyVis[p] = missTotal / (float)rayTotal;

			// Проба в стене видит в основном задние грани - гасим её вес в интерполяции.
			validityW[p] = Math.Clamp(1f - backTotal / (float)rayTotal * 3f, 0f, 1f);

			// Сброс геометрии переехавшей пробы - ПОСЛЕ того, как она отдала этот раунд (лучи-то
			// пущены ещё с прежнего места), и обязательно после накопления счётчиков выше, иначе
			// они тут же затёрли бы обнуление. Копить с нуля начинает следующий раунд.
			//
			// Без этого сброса переезд был бы половинчатым: радианс проба считала бы уже с нового
			// места, а валидность осталась бы заниженной старой статистикой задних граней - то есть
			// выбравшаяся из стены проба продолжала бы числиться замурованной, - и тест Чебышёва
			// мерил бы глубины от точки, где пробы больше нет.
			if (relocated)
			{
				s.RayTotal[p] = 0;
				s.MissTotal[p] = 0;
				s.BackTotal[p] = 0;
				int visReset = p * res * res;
				for (int i = 0; i < res * res; i++)
				{
					s.VisSumT[visReset + i] = 0f;
					s.VisSumT2[visReset + i] = 0f;
					s.VisWeight[visReset + i] = 0f;
				}
			}
		});

		s.Swap();
		s.Round++;
		s.ConsumeRelocationRound();
	}

	/// <summary>Пакует ТЕКУЩЕЕ состояние сессии в атласы. Буферы результата переиспользуются между
	/// снимками (пересоздавать десятки мегабайт каждый раунд незачем), поэтому звать строго между
	/// раундами и отдавать результат потребителю до следующего <see cref="RunRound"/>.</summary>
	public ProbeGiBakeResult Snapshot(ProbeGiBakeSession s)
	{
		int res = ProbeGiBakeResult.VisRes;
		var result = s.Result;
		int shWidth = result.ShWidth;
		int visWidth = shWidth * res;

		Parallel.For(0, s.ProbeCount, p =>
		{
			// Индекс хранения и есть адрес в атласе: ширина атласа равна оси X сетки.
			var (px, py) = ProbeTexel(p, shWidth);
			int texel = (py * shWidth + px) * 8;
			WriteHalf4(result.Sh0, texel, s.L0R[p], s.SkyVis[p]);
			WriteHalf4(result.Sh1, texel, s.L1XR[p], s.ValidityR[p]);
			WriteHalf4(result.Sh2, texel, s.L1YR[p], s.SunFracR[p]);
			WriteHalf4(result.Sh3, texel, s.L1ZR[p], 1f);
			WriteHalf4(result.Offset, texel, s.ProbeOffset[p], 1f);

			// Среднее по всей пробе - заполнитель октантов, куда за все раунды не попал ни один луч.
			int visBase = p * res * res;
			float totalT = 0f;
			float totalWeight = 0f;
			for (int i = 0; i < res * res; i++)
			{
				totalT += s.VisSumT[visBase + i];
				totalWeight += s.VisWeight[visBase + i];
			}

			float meanAll = totalWeight > 0f ? totalT / totalWeight : 0f;

			// Окто-блок видимости пробы: res×res текселей начиная с (px*res, py*res).
			for (int ty = 0; ty < res; ty++)
			{
				for (int tx = 0; tx < res; tx++)
				{
					int src = visBase + ty * res + tx;
					float weight = s.VisWeight[src];
					float mean = weight > 0f ? s.VisSumT[src] / weight : meanAll;
					float mean2 = weight > 0f ? s.VisSumT2[src] / weight : meanAll * meanAll;
					int dst = ((py * res + ty) * visWidth + px * res + tx) * 8;
					WriteHalf4(result.Vis, dst, new Vector3(mean, mean2, 0f), 0f);
				}
			}
		});

		return result;
	}

	/// <summary>Множитель обратной связи поля под заданную глубину мультибаунса. Прогрессивный бейк
	/// собирает отскок из ТЕКУЩЕГО поля, то есть переотскок по построению бесконечный: суммарная
	/// энергия идёт как 1/(1-r*f) при средней отражательной способности сцены r, тогда как прежний
	/// N-итерационный бейк давал (1-r^N)/(1-r). Приравняв, получаем f = (1-r^(N-1))/(1-r^N). Берём
	/// r=0.5 (дефолтное альбедо трассировщика) - точность оценки тут не важна, важно, что при
	/// переходе на прогрессивный бейк сцены не поедут по яркости.</summary>
	internal static float BounceFeedback(int bounces)
	{
		if (bounces <= 1)
		{
			return 0f;
		}

		const float r = 0.5f;
		float rn = MathF.Pow(r, bounces);
		return (1f - rn / r) / (1f - rn);
	}

	/// <summary>Резкость лобы, которой луч размазывается по окто-карте глубин: степень косинуса
	/// берётся шестью возведениями в квадрат (то есть 64) - дешевле pow, а порог веса отсекает всё
	/// дальше 26 градусов от луча. Обязано совпадать с ProbeRoundCS.hlsl.</summary>
	private const int DepthSharpnessSquarings = 6;
	private const float DepthWeightEpsilon = 0.001f;

	/// <summary>Обратное окто-преобразование: направление по точке карты (зеркало ProbeOctDecode в
	/// ProbeRoundCS.hlsl).</summary>
	private static Vector3 OctDecode(Vector2 uv)
	{
		var p = uv * 2f - Vector2.One;
		var d = new Vector3(p.X, p.Y, 1f - MathF.Abs(p.X) - MathF.Abs(p.Y));
		if (d.Z < 0f)
		{
			d = new Vector3(
				(1f - MathF.Abs(d.Y)) * (d.X >= 0f ? 1f : -1f),
				(1f - MathF.Abs(d.X)) * (d.Y >= 0f ? 1f : -1f),
				d.Z);
		}

		return Vector3.Normalize(d);
	}

	/// <summary>Окто-кодирование направления в [0,1]² - обязано бит-в-бит совпадать с OctEncode в
	/// UnlitInstancedPS.hlsl (иначе шейдер читает чужие тексели видимости).</summary>
	private static Vector2 OctEncode(Vector3 d)
	{
		float sum = MathF.Abs(d.X) + MathF.Abs(d.Y) + MathF.Abs(d.Z);
		float px = d.X / sum, py = d.Y / sum;
		if (d.Z < 0f)
		{
			(px, py) = ((1f - MathF.Abs(py)) * (px >= 0f ? 1f : -1f),
						(1f - MathF.Abs(px)) * (py >= 0f ? 1f : -1f));
		}

		return new Vector2(px * 0.5f + 0.5f, py * 0.5f + 0.5f);
	}

	/// <summary>CPU-аналог шейдерного SampleProbeGi: трилинейная интерполяция 8 проб с весами
	/// валидности, затем SH L1 → irradiance по нормали. sunFrac/fracOut - интерполяция доли
	/// солнечного света теми же весами (см. Bake).</summary>
	private static Vector3 EvalIrradiance(ProbeGiBakeSession s,
		Vector3[] l0, Vector3[] l1x, Vector3[] l1y, Vector3[] l1z, float[] validity, float[] sunFrac,
		Vector3 pos, Vector3 normal, out float fracOut)
	{
		fracOut = 0f;

		var origin = s.Origin;
		var cell = s.Cell;
		var f = (pos - origin) / cell;
		f = Vector3.Clamp(f, Vector3.Zero,
			new Vector3(s.CountX - 1, s.CountY - 1, s.CountZ - 1));

		// Базовый узел ячейки - просто пол координат сетки: у плотной сетки проба есть в каждом узле,
		// искать её больше негде и не через что.
		int lx = Math.Clamp((int)MathF.Floor(f.X), 0, s.CountX - 2);
		int ly = Math.Clamp((int)MathF.Floor(f.Y), 0, s.CountY - 2);
		int lz = Math.Clamp((int)MathF.Floor(f.Z), 0, s.CountZ - 2);
		var t = Vector3.Clamp(f - new Vector3(lx, ly, lz), Vector3.Zero, Vector3.One);

		var sh0 = Vector3.Zero;
		var shX = Vector3.Zero;
		var shY = Vector3.Zero;
		var shZ = Vector3.Zero;
		float fracSum = 0f;
		float weightSum = 0f;

		for (int corner = 0; corner < 8; corner++)
		{
			int ox = corner & 1, oy = (corner >> 1) & 1, oz = (corner >> 2) & 1;
			int index = StorageIndex(lx + ox, ly + oy, lz + oz, s.CountX, s.CountY);
			float w = (ox == 1 ? t.X : 1f - t.X) * (oy == 1 ? t.Y : 1f - t.Y) * (oz == 1 ? t.Z : 1f - t.Z)
				* validity[index];

			// Мягкий backface-вес - зеркало wrap shading-а в SampleProbeGi (см. UnlitInstancedPS):
			// без него мультибаунс за несколько итераций протаскивает свет сквозь стены (проба за
			// стеной подмешивается в сбор на точке попадания) - поле внутри помещений засорялось
			// солнечным баунсом наружных стен.
			var probePos = origin + new Vector3(lx + ox, ly + oy, lz + oz) * cell;
			var toProbe = probePos - pos;
			float toProbeLen = toProbe.Length();
			float wrap = (Vector3.Dot(toProbe / MathF.Max(toProbeLen, 1e-4f), normal) + 1f) * 0.5f;
			w *= wrap * wrap + 0.05f;

			sh0 += l0[index] * w;
			shX += l1x[index] * w;
			shY += l1y[index] * w;
			shZ += l1z[index] * w;
			fracSum += sunFrac[index] * w;
			weightSum += w;
		}

		if (weightSum < 1e-4f)
		{
			return Vector3.Zero;
		}

		float inv = 1f / weightSum;
		fracOut = Math.Clamp(fracSum * inv, 0f, 1f);
		var e = (sh0 * inv) * 0.8862269f
			+ ((shX * inv) * normal.X + (shY * inv) * normal.Y + (shZ * inv) * normal.Z) * 1.0233267f;
		return Vector3.Max(e, Vector3.Zero);
	}

	private static void WriteHalf4(byte[] bytes, int offset, Vector3 rgb, float a)
	{
		WriteHalf(bytes, offset + 0, rgb.X);
		WriteHalf(bytes, offset + 2, rgb.Y);
		WriteHalf(bytes, offset + 4, rgb.Z);
		WriteHalf(bytes, offset + 6, a);
	}

	private static void WriteHalf(byte[] bytes, int offset, float value)
	{
		ushort bits = BitConverter.HalfToUInt16Bits((Half)value);
		bytes[offset] = (byte)bits;
		bytes[offset + 1] = (byte)(bits >> 8);
	}

	/// <summary>Веер Фибоначчи, повёрнутый детерминированной по номеру раунда ориентацией. Без
	/// поворота каждый раунд стрелял бы ровно в те же направления, и накопление раундов не давало
	/// бы ничего сверх первого - вся прогрессивность держится на этом повороте.</summary>
	private static Vector3[] BuildRotatedFibonacciSphere(int count, int sequence)
	{
		var dirs = BuildFibonacciSphere(count);
		if (sequence == 0)
		{
			return dirs;
		}

		// Равномерная ориентация по Шумейку из низкодискрепансной тройки (аддитивная рекуррента на
		// иррациональных константах: покрывает пространство ориентаций ровнее, чем ГПСЧ, - на
		// десятке раундов это заметно меньше пятен в поле).
		float u1 = Frac(sequence * 0.7548776662f);
		float u2 = Frac(sequence * 0.5698402909f);
		float u3 = Frac(sequence * 0.6180339887f);
		float r1 = MathF.Sqrt(1f - u1), r2 = MathF.Sqrt(u1);
		var rotation = new Quaternion(
			r1 * MathF.Sin(2f * MathF.PI * u2), r1 * MathF.Cos(2f * MathF.PI * u2),
			r2 * MathF.Sin(2f * MathF.PI * u3), r2 * MathF.Cos(2f * MathF.PI * u3));

		for (int i = 0; i < count; i++)
		{
			dirs[i] = Vector3.Transform(dirs[i], rotation);
		}

		return dirs;

		static float Frac(float v) => v - MathF.Floor(v);
	}

	private static Vector3[] BuildFibonacciSphere(int count)
	{
		var dirs = new Vector3[count];
		float golden = MathF.PI * (3f - MathF.Sqrt(5f));
		for (int i = 0; i < count; i++)
		{
			float y = 1f - (i + 0.5f) * 2f / count;
			float radius = MathF.Sqrt(MathF.Max(1f - y * y, 0f));
			float phi = golden * i;
			dirs[i] = new Vector3(MathF.Cos(phi) * radius, y, MathF.Sin(phi) * radius);
		}

		return dirs;
	}
}
