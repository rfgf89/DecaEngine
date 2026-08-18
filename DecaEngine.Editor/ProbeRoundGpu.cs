using System.Numerics;
using System.Runtime.InteropServices;
using DecaEngine;
using DecaEngine.Core;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Core;
using DecaEngine.Graphics.Diligent;
using Diligent;

namespace DecaEngine.Editor;

/// <summary>
/// Раунд обновления probe-GI на GPU (см. ProbeRoundCS.hlsl) - перенос
/// <see cref="ProbeGiBaker.RunRound"/> в compute. Ради этого и строился общий интерфейс
/// трассировки: раунд на CPU стоит десятки миллисекунд, здесь - доли, и пробы могут обновляться
/// каждый кадр, то есть свет и геометрия становятся динамическими.
///
/// Поле держится в GPU-буфере в той же раскладке, что и SH-атласы (четыре float4 на пробу), чтобы
/// в дальнейшем шейдер писал прямо в атласы как в UAV, без упаковки на CPU.
///
/// Сверяется с CPU-раундом (см. <see cref="Verify"/>) на упрощённом освещении: без неба и без
/// переотскока. Это не лень, а изоляция - сверять сэмплинг энвайронмент-карты, которая на CPU
/// считается функцией, а на GPU текстурой, значит мерить не то, что тестируешь.
/// </summary>
public sealed class ProbeRoundGpu : IDisposable
{
	[StructLayout(LayoutKind.Sequential)]
	private struct RoundParams
	{
		public Vector4 GridOrigin;    // xyz - угол сетки, w - вес раунда
		public Vector4 GridCell;      // xyz - шаг сетки, w - предельная дальность луча
		public Vector4 SunDirection;  // xyz - на солнце, w - лучей за раунд
		public Vector4 SunColor;      // xyz - цвет солнца, w - число проб
		public Vector4 Round;         // x - эпсилон, y - кламп видимости, z - отступ сбора, w - обратная связь
		public Vector4 Chunk;         // x - первый элемент порции, y - за последним, z - потолок луча,
		                              // w - предел изменения пробы за раунд
		public Vector4 Relocation;    // x - предел релокации пробы в мировых единицах
		public Vector4 Scroll;        // x - предел релокации СВЕЖЕЙ пробы (см. ProbeGiBakeSession.BrickFresh)
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct GridParams
	{
		public Vector4 GridCounts;      // xyz - размер плотной сетки проб, w - насыщенность отскока

		// xyz - тороидальное смещение сетки в пробах: узел c лежит по индексу (c + scroll) mod count
		// (см. ProbeGiBakeResult.ScrollOffsetX).
		public Vector4 GridScroll;

		// xyz - ПЕРВАЯ въехавшая прокруткой плоскость по каждой оси, в координатах ХРАНЕНИЯ: -1 - по
		// этой оси не двигались, ProbeGiBakeSession.ClearWholeAxis - чистить ось целиком (камеру
		// телепортировали дальше размера объёма, пересечения со старым положением нет).
		public Vector4 GridClear;

		// xyz - сколько плоскостей подряд въехало по каждой оси, начиная с GridClear.
		public Vector4 GridClearSpan;
		public Vector4 SurfaceVoxel;    // xyz - размер вокселя кэша, w - число живых вокселей
		public Vector4 SurfaceCounts;   // xyz - размер воксельной сетки кэша
		public Vector4 SkyParams;       // x - поворот энвайронмента, y - яркость неба, z - сторона окто-карты видимости, w - кирпичей в ряду пула
	}

	/// <summary>Зеркало <see cref="ProbeGiBakeResult.VisRes"/> (ручка «Visibility res») и
	/// PROBE_VIS_RES, который шейдер получает кбуфером: расходиться этим трём нельзя - буфер
	/// видимости размеряется здесь, пишется шейдером и распаковывается по раскладке
	/// ProbeGiBakeResult. Свойство, а не const: значение фиксируется на время жизни комплекта
	/// (сессия пересоздаётся при смене ручки).</summary>
	private static int VisRes => ProbeGiBakeResult.VisRes;
	// 1024: §7.1 статьи Majercik 2021 - бюджет, освобождённый сном/Off-пробами, правильно тратить
	// на лучи («more rays ... allows for a lower global hysteresis»). Цена большого веера - не
	// кадр, а ТЕМП раундов: бюджет порций на кадр держит GPU-время кадра постоянным (см.
	// ChunksPerFrame), поэтому 1024 луча просто растягивают раунд на больше кадров.
	private const int MaxRaysPerRound = 1024;

	/// <summary>Сколько проб обсчитывается за ОДИН диспатч. Раунд режется на такие порции и
	/// растягивается на несколько кадров: при крупном бюджете проб один диспатч на всю сетку - это
	/// миллионы лучей, GPU занят им сотни миллисекунд, презентация всё это время голодает, и
	/// кадровый объект уходит в таймаут (в логе редактора - «Timeout elapsed while waiting for the
	/// frame waitable object» подряд), а следом снимается устройство. Порция подобрана так, чтобы
	/// диспатч укладывался в единицы миллисекунд даже на программном обходе BVH.</summary>
	private static readonly int ProbesPerDispatch =
		int.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_CHUNK"), out var chunk) && chunk > 0
			? chunk
			: 2048;

	/// <summary>То же для прохода кэша поверхностей: воксель дешевле пробы (один теневой луч против
	/// шестнадцати первичных), поэтому порция крупнее.</summary>
	private const int VoxelsPerDispatch = 16384;

	private readonly DiligentGraphicsApi _api;
	private readonly int _probeCount;

	private readonly IBuffer _bvhNodes, _bvhOrder, _bvhTriangles;
	private readonly IBuffer _rayDirections;

	/// <summary>Атласы объёма - нужны, чтобы прокрутка доехала до материалов одним движением
	/// (см. <see cref="SyncScrollState"/>): кбуфер раунда и угол объёма в материалах описывают ОДНО
	/// положение сетки, и разъехаться им нельзя.</summary>
	private readonly ProbeGiTextures? _atlases;
	private readonly IBuffer[] _field = new IBuffer[2];
	private readonly IBuffer _counters;
	private readonly IBuffer _offsets;
	private readonly IBuffer _visibility;

	// Изменчивость: на пробу (пишет раунд), свёрнутая по группам (пишет ProbeVariabilityCS) и её
	// копия под чтение с CPU. Подробности приёма - в шапке ProbeVariabilityCS.hlsl.
	private readonly IBuffer _variability;
	private readonly IBuffer _variabilitySum;
	private readonly IBuffer _variabilityStaging;
	private readonly IBuffer _variabilityParams;
	private readonly IPipelineState _variabilityPso;
	private IShaderResourceBinding? _variabilitySrb;

	/// <summary>Сколько групп сворачивает <c>ProbeVariabilityCS</c> - обязано совпадать с
	/// PROBE_VARIABILITY_GROUPS в шейдере.</summary>
	private const int VariabilityGroups = 64;

	/// <summary>Раундов между свёрткой изменчивости и попыткой её вычитать. Читать сразу нельзя:
	/// это стойло конвейера на кадр (Flush + WaitForIdle), а ради числа, которое меняется медленно,
	/// платить кадром нелепо. Два раунда - запас, которого хватает, чтобы копия уже доехала, и
	/// карта ниже брала её без ожидания.</summary>
	private const int VariabilityReadLag = 2;

	private int _variabilityPending = -1;
	private float _averageVariability = float.PositiveInfinity;

	/// <summary>Снимок <see cref="ProbeGiBakeSession.GeometryVersion"/> - по расхождению снимается
	/// остановка сошедшегося объёма (см. IsConverged).</summary>
	private int _geometryVersion = -1;

	/// <summary>Сколько раундов пропущено как сошедшиеся - для диагностики (см. IsConverged).
	/// </summary>
	public int SkippedRounds { get; private set; }

	/// <summary>Средняя изменчивость объёма (коэффициент вариации яркости проб, см.
	/// ProbeVariabilityCS.hlsl). Бесконечность - «ещё не мерено»: до первой удачной вычитки объём
	/// обязан считаться несошедшимся, иначе трассировка встала бы, не начавшись.</summary>
	public float AverageVariability => _averageVariability;
	// Один буфер параметров: привязывается к SRB один раз, обновляется UpdateBuffer-ом перед каждым
	// диспатчем. Кольцо из нескольких буферов с переустановкой переменной перед диспатчем я пробовал -
	// выигрыша нет (простой был не из-за кбуфера, а из-за барьеров UAV, см. Dispatch), зато Vulkan
	// справедливо ругался: менять дескрипторный набор, по которому уже идёт запись команд, нельзя
	// (VUID-vkCmdDispatch-commandBuffer-recording).
	// Буфер параметров держит НЕСКОЛЬКО блоков, диспатч выбирает свой динамическим смещением (см.
	// Dispatch). 256 байт - выравнивание константных буферов у D3D12.
	/// <summary>DECA_PROBE_FORCETRANS=1 - всегда полные переходы состояний вместо Verify внутри
	/// прохода. Диагностический тумблер: если с ним отказ пропадает, значит пропущенный переход и
	/// есть причина.</summary>
	private static readonly bool ForceTransitions =
		Environment.GetEnvironmentVariable("DECA_PROBE_FORCETRANS") == "1";

	private const int ParamsStride = 256;
	private const int ParamsSlots = 64;
	private readonly IBuffer _params;
	private int _paramsSlot;

	// Состояния ресурсов уже выставлены первой порцией - дальше переходы не нужны (см. Dispatch).
	// Сбрасывается после возврата атласов в ShaderResource в конце раунда.
	private readonly IBuffer _gridParams;

	/// <summary>CPU-копия содержимого <see cref="_gridParams"/>: прокрутка правит в нём три вектора
	/// из восьми, а UpdateBuffer заливает структуру целиком - остальные надо откуда-то взять.</summary>
	private GridParams _gridParamsValue;

	// Кэш поверхностей: геометрия захвачена один раз, радианс пересчитывается своим проходом перед
	// каждым раундом проб (см. mainSurface в ProbeRoundCS.hlsl).
	private readonly IBuffer _surfaceIndex;
	private readonly IBuffer _surfacePosition;
	private readonly IBuffer _surfaceNormal;
	private readonly IBuffer _surfaceAlbedo;
	private readonly IBuffer _surfaceRadiance;
	private readonly int _surfaceVoxelCount;
	private readonly IPipelineState? _surfacePso;
	private readonly IShaderResourceBinding[] _surfaceSrb = new IShaderResourceBinding[2];

	private readonly ISamplerObject _environmentSampler;

	// Забор дросселирования. Раунд - это десятки миллисекунд работы GPU; если выпускать по раунду
	// на кадр не дожидаясь предыдущего, очередь растёт быстрее, чем GPU её разбирает, и кончается
	// это таймаутом кадрового объекта и снятием устройства (ровно то, что видно в редакторе на
	// Sponza). CPU-путь дросселировался сам собой - там раунд ждали как Task.
	private readonly IFence _roundFence;
	private ulong _roundFenceValue;

	// Докуда дошёл текущий раунд по своим двум проходам (см. RunRound).
	private int _surfaceChunkStart;
	private int _probeChunkStart;
	private RoundParams _roundParams;

	// Потолок яркости луча текущего раунда: живая ручка, приезжает из сессии на старте раунда и
	// обязана быть одинаковой у всех его порций (см. RunRound).
	private float _maxRayLuminance;
	private float _maxStep;

	/// <summary>Атласы, которые пишет раунд - после диспатча их надо вернуть в ShaderResource
	/// (см. RunRound).</summary>
	private readonly ITexture[] _atlasTextures = Array.Empty<ITexture>();

	private readonly IPipelineState _pso;

	// Две привязки под пинг-понг поля: в одной буфер A читается и B пишется, в другой наоборот.
	// Перепривязывать ресурсы каждый раунд дороже и шумнее, чем держать пару готовых SRB.
	private readonly IShaderResourceBinding[] _srb = new IShaderResourceBinding[2];
	private int _writeIndex;
	private readonly List<IDeviceObject> _views = new();

	public unsafe ProbeRoundGpu(DiligentGraphicsApi api, ProbeRoundPipelines pipelines,
		ProbeGiBakeSession session, ProbeGiBaker baker, ProbeGiTextures? atlases = null,
		IGpuTexture? environmentMap = null, float envYaw = 0f, ProbeSceneAccel? accel = null)
	{
		_api = api;
		_probeCount = session.ProbeCount;
		Hardware = accel != null;

		var device = api.Device;
		var swPhase = System.Diagnostics.Stopwatch.StartNew();
		long msSurface = 0, msExport = 0, msBuffers = 0, msShaders = 0;

		// BVH выгружается и живёт здесь же: вызывающему (вьюпорту) незачем знать про буферы
		// графического API - он оперирует сессией и бейкером.
		msSurface = swPhase.ElapsedMilliseconds;
		swPhase.Restart();
		var (nodes, order, triangles) = baker.ExportBvh();
		msExport = swPhase.ElapsedMilliseconds;
		swPhase.Restart();
		var bvhNodes = CreateImmutable(device, "SceneBvhNodes", nodes, sizeof(BvhNodeGpu));
		var bvhOrder = CreateImmutable(device, "SceneBvhOrder", order, sizeof(uint));
		var bvhTriangles = CreateImmutable(device, "SceneBvhTriangles", triangles, sizeof(BvhTriangleGpu));
		_bvhNodes = bvhNodes;
		_bvhOrder = bvhOrder;
		_bvhTriangles = bvhTriangles;

		if (atlases is { GpuWritable: true })
		{
			_atlasTextures = new[]
				{ atlases.Sh0, atlases.Sh1, atlases.Sh2, atlases.Sh3, atlases.Vis, atlases.Offset }
				.OfType<DiligentGpuTexture>()
				.Select(t => t.Texture)
				.ToArray();
		}

		_roundFence = device.CreateFence(new FenceDesc { Name = "ProbeRoundFence" });

		// Тот же сэмплер, что у пиксельного шейдера (см. ModelViewportEnvironment): линейный Wrap,
		// иначе equirect-шов по горизонтали дал бы полосу в небе бейка.
		_environmentSampler = api.CreateSampler("_ProbeEnvMap_Sampler", TextureFilter.Linear,
			TextureAddress.Wrap, CompFunction.Always, Vector4.Zero);

		_atlases = atlases;

		// Аккумуляторы обязаны стартовать с нуля (поле копится бегущим средним - мусор остался бы
		// навсегда), но обнуляются КОМАНДОЙ, а не загрузкой нулей с CPU (см. CreateRw/ClearBuffers).
		_field[0] = CreateRw<Vector4>(device, "ProbeFieldA", _probeCount * 4, sizeof(Vector4));
		_field[1] = CreateRw<Vector4>(device, "ProbeFieldB", _probeCount * 4, sizeof(Vector4));
		_counters = CreateRw<int>(device, "ProbeCounters", _probeCount * 4, sizeof(int) * 4);

		// Смещения релокации. Стартуют нулями - проба сидит в своём узле, пока раунды не выяснят,
		// что она замурована.
		_offsets = CreateRw<Vector4>(device, "ProbeOffsets", _probeCount, sizeof(Vector4));
		_visibility = CreateRw<Vector4>(device, "ProbeVisibility",
			_probeCount * VisRes * VisRes, sizeof(Vector4));

		// Изменчивость: по числу проб и по числу групп свёртки.
		_variability = CreateRw<Vector2>(device, "ProbeVariability", _probeCount, sizeof(Vector2));
		_variabilitySum = CreateRw<Vector2>(device, "ProbeVariabilitySum", VariabilityGroups,
			sizeof(Vector2));

		// Копия под чтение с CPU. Держится ПОСТОЯННО, а не заводится на каждую вычитку: создание
		// staging-буфера в кадре - это выделение памяти у драйвера, а нам его читать каждый раунд.
		_variabilityStaging = device.CreateBuffer(new BufferDesc
		{
			Name = "ProbeVariabilityStaging",
			Usage = Usage.Staging,
			CPUAccessFlags = CpuAccessFlags.Read,
			BindFlags = BindFlags.None,
			Size = (ulong)(VariabilityGroups * sizeof(Vector2)),
		});

		_variabilityParams = device.CreateBuffer(new BufferDesc
		{
			Name = "ProbeVariabilityParams",
			Usage = Usage.Default,
			BindFlags = BindFlags.UniformBuffer,
			Size = 16,
		});

		// Default, а не Dynamic: у динамического буфера подложка переезжает при каждом маппинге, а
		// представление под него создаётся здесь ОДИН раз - шейдер читал бы протухший вид (поймано
		// сверкой: поле выходило пустым, потому что все направления лучей приезжали нулями).
		// Заливается UpdateBuffer-ом прямо в кадре.
		_rayDirections = device.CreateBuffer(new BufferDesc
		{
			Name = "ProbeRayDirections",
			Usage = Usage.Default,
			BindFlags = BindFlags.ShaderResource,
			Mode = BufferMode.Structured,
			ElementByteStride = (uint)sizeof(Vector4),
			Size = (ulong)(MaxRaysPerRound * sizeof(Vector4)),
		});

		// Default, а НЕ Dynamic - по той же причине, что и буфер направлений выше, только здесь она
		// кусается сильнее. Раунд режется на порции, и каждый диспатч обязан увидеть СВОЙ диапазон;
		// динамический буфер, маппящийся несколько раз за кадр, этого не даёт - все диспатчи
		// прочитали бы последнее записанное значение. Ловится это не падением, а тем, что поле
		// выходит ровно во столько раз меньше, сколько порций в раунде (замерено сверкой с CPU:
		// 0.359 против 0.960 при трёх порциях).
		_params = device.CreateBuffer(new BufferDesc
		{
			Name = "ProbeRoundParams",
			Usage = Usage.Default,
			BindFlags = BindFlags.UniformBuffer,
			Size = (ulong)(ParamsStride * ParamsSlots),
		});

		// Размеры сеток от раунда к раунду не меняются, а вот смещение прокрутки и пометки чистки -
		// меняются (см. SyncScrollState), поэтому кбуфер переливается на каждой прокрутке.
		_gridParams = device.CreateBuffer(new BufferDesc
		{
			Name = "ProbeGridParams",
			Usage = Usage.Default,
			BindFlags = BindFlags.UniformBuffer,
			Size = (ulong)sizeof(GridParams),
		});

		// Кэш поверхностей. Буферы заводятся всегда (пустые слоты в шейдере всё равно объявлены),
		// но при выключенном кэше SurfaceVoxel.w = 0 и SurfaceLookup сразу возвращает -1.
		var surface = session.Surface;
		_surfaceVoxelCount = surface?.VoxelCount ?? 0;
		int surfaceSlots = Math.Max(_surfaceVoxelCount, 1);

		_surfaceIndex = CreateImmutable(device, "SurfaceIndex",
			surface != null ? surface.ExportIndex() : new int[1], sizeof(int));
		_surfacePosition = CreateImmutable(device, "SurfacePosition",
			ToVector4(surface?.Position, surfaceSlots), sizeof(Vector4));
		_surfaceNormal = CreateImmutable(device, "SurfaceNormal",
			ToVector4(surface?.Normal, surfaceSlots), sizeof(Vector4));
		_surfaceAlbedo = CreateImmutable(device, "SurfaceAlbedo",
			ToVector4(surface?.Albedo, surfaceSlots), sizeof(Vector4));
		_surfaceRadiance = CreateRw<Vector4>(device, "SurfaceRadiance", surfaceSlots, sizeof(Vector4));

		_gridParamsValue = new GridParams
		{
			GridCounts = new Vector4(session.CountX, session.CountY, session.CountZ,
				session.BounceSaturation),
			GridScroll = new Vector4(session.ScrollX, session.ScrollY, session.ScrollZ, 0f),
			GridClear = new Vector4(session.ClearPlane[0], session.ClearPlane[1],
				session.ClearPlane[2], 0f),
			GridClearSpan = new Vector4(session.ClearPlaneSpan[0], session.ClearPlaneSpan[1],
				session.ClearPlaneSpan[2], 0f),
			SurfaceVoxel = surface != null
				? new Vector4(surface.Voxel, _surfaceVoxelCount)
				: Vector4.Zero,
			SurfaceCounts = surface != null
				? new Vector4(surface.CountX, surface.CountY, surface.CountZ, 0f)
				: Vector4.Zero,
			// z - сторона окто-карты видимости (ручка «Visibility res», см. ProbeGiBakeResult.VisRes):
			// по ней шейдер раскладывает буфер глубин и пишет атлас, а пиксельный - читает его.
			SkyParams = new Vector4(envYaw, session.SkyIntensity, VisRes, 0f),
		};

		api.ImmediateContext.UpdateBuffer<GridParams>(_gridParams, 0,
			new ReadOnlySpan<GridParams>(in _gridParamsValue), ResourceStateTransitionMode.Transition);

		// Аккумуляторы - в ноль командой заполнения (см. ClearBuffers): дёшево и без динамической
		// кучи, в отличие от прежней загрузки десятков мегабайт нулей с CPU.
		ClearBuffers(api.ImmediateContext);

		msBuffers = swPhase.ElapsedMilliseconds;
		swPhase.Restart();

		// Конвейеры приходят готовыми и переживают пересоздание сессии - см. ProbeRoundPipelines:
		// их компиляция стоит ~650 мс, а сессия пересоздаётся на каждое движение ползунка.
		_pso = pipelines.Round;
		_surfacePso = _surfaceVoxelCount > 0 ? pipelines.Surface : null;

		// Свёртка изменчивости: одна привязка на весь срок жизни - пинг-понга у неё нет, она читает
		// собственный буфер, который раунд пишет одинаково в обеих фазах.
		_variabilityPso = pipelines.Variability;
		_variabilitySrb = _variabilityPso.CreateShaderResourceBinding(true);
		TryBind(_variabilitySrb, "_ProbeVariability", _variability, BufferViewType.ShaderResource);
		TryBind(_variabilitySrb, "_ProbeVariabilitySum", _variabilitySum,
			BufferViewType.UnorderedAccess);
		_variabilitySrb.GetVariableByName(ShaderType.Compute, "ProbeVariabilityParams")
			?.Set(_variabilityParams, SetShaderResourceFlags.None);

		for (int i = 0; i < 2; i++)
		{
			// Проход кэша тоже читает поле проб (гладкая часть освещения), поэтому у него своя
			// пара привязок под тот же пинг-понг.
			if (_surfacePso != null)
			{
				var surfaceSrb = _surfacePso.CreateShaderResourceBinding(true);
				_surfaceSrb[i] = surfaceSrb;
				BindSceneAndSurface(surfaceSrb, bvhNodes, bvhOrder, bvhTriangles, i);
				TryBind(surfaceSrb, "_SurfaceRadiance", _surfaceRadiance, BufferViewType.UnorderedAccess);

				// Проход кэша тоже ПУСКАЕТ ЛУЧИ (теневой луч на воксель, см. mainSurface), поэтому
				// аппаратные ресурсы ему нужны ровно так же, как раунду проб. Раньше TLAS сюда не
				// привязывался вовсе - при включённых одновременно аппаратной трассировке и кэше
				// поверхностей проход трассировал по пустой структуре.
				BindAccel(surfaceSrb, accel);
			}

			var srb = _pso.CreateShaderResourceBinding(true);
			_srb[i] = srb;

			// Мировой BVH живёт только в ПРОГРАММНОМ варианте шейдера, объектная геометрия и таблица
			// инстансов - только в аппаратном (см. SceneTrace.hlsl). Общих буферов сцены у путей
			// больше нет, поэтому все привязки необязательные: чего вариант не объявил, того
			// TryBind просто не найдёт.
			TryBind(srb, "_SceneBvhNodes", bvhNodes, BufferViewType.ShaderResource);
			TryBind(srb, "_SceneBvhOrder", bvhOrder, BufferViewType.ShaderResource);
			TryBind(srb, "_SceneBvhTriangles", bvhTriangles, BufferViewType.ShaderResource);
			BindSrv(srb, "_ProbeRayDirections", _rayDirections);

			// Пинг-понг: пишем в _field[i], читаем из другого.
			BindSrv(srb, "_ProbeFieldRead", _field[1 - i]);
			BindUav(srb, "_ProbeField", _field[i]);
			BindUav(srb, "_ProbeCounters", _counters);
			BindUav(srb, "_ProbeVisibility", _visibility);
			BindUav(srb, "_ProbeOffsets", _offsets);
			BindUav(srb, "_ProbeVariability", _variability);

			// В раунде проб кэш только читается - радианс пишет предыдущий проход. Геометрия
			// вокселей (позиции/нормали/альбедо) тут не нужна и компилятором вырезается, поэтому
			// привязки кэша необязательные.
			TryBind(srb, "_SurfaceIndex", _surfaceIndex, BufferViewType.ShaderResource);
			TryBind(srb, "_SurfacePosition", _surfacePosition, BufferViewType.ShaderResource);
			TryBind(srb, "_SurfaceNormal", _surfaceNormal, BufferViewType.ShaderResource);
			TryBind(srb, "_SurfaceAlbedo", _surfaceAlbedo, BufferViewType.ShaderResource);
			TryBind(srb, "_SurfaceRadiance", _surfaceRadiance, BufferViewType.UnorderedAccess);

			// Атласы пишутся прямо из раунда - шага упаковки на CPU в этом пути нет.
			if (atlases is { GpuWritable: true })
			{
				TryBindTexture(srb, "_ProbeAtlasSh0", atlases.Sh0);
				TryBindTexture(srb, "_ProbeAtlasSh1", atlases.Sh1);
				TryBindTexture(srb, "_ProbeAtlasSh2", atlases.Sh2);
				TryBindTexture(srb, "_ProbeAtlasSh3", atlases.Sh3);
				TryBindTexture(srb, "_ProbeAtlasVis", atlases.Vis);
				TryBindTexture(srb, "_ProbeAtlasOffset", atlases.Offset);
			}

			BindAccel(srb, accel);
			BindEnvironment(srb, environmentMap, _environmentSampler);
			// Диапазон дескриптора - ОДИН блок, а не весь буфер: динамическое смещение прибавляется к
		// диапазону, и при диапазоне во весь буфер оно немедленно вылезает за его конец
		// (VUID-vkCmdBindDescriptorSets-pDescriptorSets-01979).
		Require(srb, "ProbeRoundParams")
			.SetBufferRange(_params, 0, ParamsStride, 0, SetShaderResourceFlags.AllowOverwrite);
			Require(srb, "ProbeGridParams").Set(_gridParams, SetShaderResourceFlags.AllowOverwrite);
		}

		msShaders = swPhase.ElapsedMilliseconds;
		SetupTiming = (msSurface, msExport, msBuffers, msShaders);
	}

	/// <summary>Догоняет прокрутку сессии: смещение и пометки чистки в кбуфере, угол объёма в атласах.
	///
	/// Двух буферов раскладки, которые здесь заливались раньше (углы кирпичей и карта индирекции),
	/// больше нет вовсе. У плотной сетки координаты пробы ЕСТЬ её индекс, а состояние - свежая ли
	/// она, разрешена ли ей релокация - выводится из трёх чисел, уже лежащих в кбуфере, а не читается
	/// из таблицы на слот. Тем самым с раунда снялись две заливки буферов на каждую прокрутку.
	///
	/// Зовётся ТОЛЬКО из начала раунда (см. RunRound), сразу за применением отложенной прокрутки, и
	/// это не перестраховка вдвойне. Смещение описывает, где какая проба стоит в мире: поменяй его
	/// посреди раунда - первые порции досчитают пробы по старым позициям, остальные по новым, и
	/// раунд запишет в поле шов ровно по границе порции (та же причина, по которой на границе раунда
	/// пересобирается TLAS, см. AtRoundStart); а разведи его с применением прокрутки - разъедутся уже
	/// не порции, а сами потребители позиций (см. ProbeGiBakeSession.Scroll).</summary>
	private void SyncScrollState(ProbeGiBakeSession session)
	{
		if (!session.ScrollStateDirty)
		{
			return;
		}

		session.ScrollStateDirty = false;
		_gridParamsValue.GridScroll = new Vector4(session.ScrollX, session.ScrollY, session.ScrollZ, 0f);
		_gridParamsValue.GridClear = new Vector4(session.ClearPlane[0], session.ClearPlane[1],
			session.ClearPlane[2], 0f);
		_gridParamsValue.GridClearSpan = new Vector4(session.ClearPlaneSpan[0], session.ClearPlaneSpan[1],
			session.ClearPlaneSpan[2], 0f);

		_api.ImmediateContext.UpdateBuffer<GridParams>(_gridParams, 0,
			new ReadOnlySpan<GridParams>(in _gridParamsValue), ResourceStateTransitionMode.Transition);
		_atlases?.ApplyScroll(session.Result);
	}

	/// <summary>Во что обошёлся подъём пути, по фазам (мс). Всё это идёт СИНХРОННО на потоке
	/// рендера, поэтому знать раскладку важно: длинный стопор между кадрами ломает кадровый цикл
	/// swap chain.</summary>
	public (long SurfaceCapture, long BvhExport, long Buffers, long Shaders) SetupTiming { get; }

	/// <summary>Аппаратный путь: TLAS вместо буферов BVH плюс индирекция атрибутов - объектная
	/// геометрия и таблица инстансов (см. SceneTrace.hlsl). Структура ускорения переживает этот
	/// объект и пересобирается снаружи при движении сцены, но привязка от пересборки не протухает:
	/// дескриптор указывает на сам TLAS, а не на его содержимое.</summary>
	private void BindAccel(IShaderResourceBinding srb, ProbeSceneAccel? accel)
	{
		if (accel == null)
		{
			return;
		}

		srb.GetVariableByName(ShaderType.Compute, "_SceneTlas")
			?.Set(accel.Tlas, SetShaderResourceFlags.AllowOverwrite);
		TryBind(srb, "_SceneMeshTriangles", accel.MeshTriangles, BufferViewType.ShaderResource);
		TryBind(srb, "_SceneInstances", accel.Instances, BufferViewType.ShaderResource);
	}

	/// <summary>Панорама окружения - источник неба для лучей-промахов. Необязательна: без неё
	/// шейдер компилируется тем же, но небо приедет чёрным, поэтому вызывающий обязан её дать,
	/// если хочет тот же результат, что у CPU-бейка.</summary>
	private static void BindEnvironment(IShaderResourceBinding srb, IGpuTexture? map,
		ISamplerObject? sampler)
	{
		if (map is not DiligentGpuTexture texture)
		{
			return;
		}

		var variable = srb.GetVariableByName(ShaderType.Compute, "_EnvMap");
		variable?.Set(texture.GetView(TextureViewType.ShaderResource),
			SetShaderResourceFlags.AllowOverwrite);

		if (sampler is DiligentSamplerObject diligentSampler)
		{
			srb.GetVariableByName(ShaderType.Compute, "_EnvMap_sampler")
				?.Set(diligentSampler.Sampler, SetShaderResourceFlags.AllowOverwrite);
		}
	}

	private static void TryBindTexture(IShaderResourceBinding srb, string name, IGpuTexture texture)
	{
		if (texture is not DiligentGpuTexture diligentTexture)
		{
			return;
		}

		srb.GetVariableByName(ShaderType.Compute, name)
			?.Set(diligentTexture.GetView(TextureViewType.UnorderedAccess),
				SetShaderResourceFlags.AllowOverwrite);
	}

	/// <summary>Привязки прохода кэша поверхностей. Все НЕОБЯЗАТЕЛЬНЫЕ: два входа одного файла
	/// законно используют разные подмножества ресурсов (проходу кэша не нужны ни направления лучей,
	/// ни счётчики, ни окто-карта), а неиспользованное компилятор вырезает - требовать их значило
	/// бы падать на здоровом шейдере.</summary>
	private void BindSceneAndSurface(IShaderResourceBinding srb, IBuffer bvhNodes, IBuffer bvhOrder,
		IBuffer bvhTriangles, int writeIndex)
	{
		TryBind(srb, "_SceneBvhNodes", bvhNodes, BufferViewType.ShaderResource);
		TryBind(srb, "_SceneBvhOrder", bvhOrder, BufferViewType.ShaderResource);
		TryBind(srb, "_SceneBvhTriangles", bvhTriangles, BufferViewType.ShaderResource);
		TryBind(srb, "_ProbeRayDirections", _rayDirections, BufferViewType.ShaderResource);
		TryBind(srb, "_ProbeFieldRead", _field[1 - writeIndex], BufferViewType.ShaderResource);
		TryBind(srb, "_ProbeField", _field[writeIndex], BufferViewType.UnorderedAccess);
		TryBind(srb, "_ProbeCounters", _counters, BufferViewType.UnorderedAccess);
		TryBind(srb, "_ProbeVisibility", _visibility, BufferViewType.UnorderedAccess);
		// Проход кэша поверхностей собирает переотскок тем же ProbeGatherIrradiance, а тот учитывает
		// смещения соседей - буфер нужен и здесь.
		TryBind(srb, "_ProbeOffsets", _offsets, BufferViewType.UnorderedAccess);
		TryBind(srb, "_SurfaceIndex", _surfaceIndex, BufferViewType.ShaderResource);
		TryBind(srb, "_SurfacePosition", _surfacePosition, BufferViewType.ShaderResource);
		TryBind(srb, "_SurfaceNormal", _surfaceNormal, BufferViewType.ShaderResource);
		TryBind(srb, "_SurfaceAlbedo", _surfaceAlbedo, BufferViewType.ShaderResource);
		// Конкретный буфер кольца выбирается перед каждым диспатчем (см. Dispatch) - здесь только
		// чтобы привязка не осталась пустой.
		srb.GetVariableByName(ShaderType.Compute, "ProbeRoundParams")
			?.SetBufferRange(_params, 0, ParamsStride, 0, SetShaderResourceFlags.AllowOverwrite);
		srb.GetVariableByName(ShaderType.Compute, "ProbeGridParams")
			?.Set(_gridParams, SetShaderResourceFlags.AllowOverwrite);
	}

	private void TryBind(IShaderResourceBinding srb, string name, IBuffer buffer, BufferViewType type)
	{
		var variable = srb.GetVariableByName(ShaderType.Compute, name);
		if (variable == null)
		{
			return;
		}

		var view = buffer.CreateView(new BufferViewDesc
		{
			Name = $"{buffer.GetDesc().Name} {type}",
			ViewType = type,
			ByteOffset = 0,
			ByteWidth = buffer.GetDesc().Size,
		});

		_views.Add(view);
		variable.Set(view, SetShaderResourceFlags.AllowOverwrite);
	}

	private static Vector4[] ToVector4(Vector3[]? source, int slots)
	{
		var result = new Vector4[slots];
		if (source == null)
		{
			return result;
		}

		for (int i = 0; i < source.Length && i < slots; i++)
		{
			result[i] = new Vector4(source[i], 0f);
		}

		return result;
	}

	/// <summary>Собран ли путь на аппаратной трассировке (TLAS). Порция аппаратного пути на два
	/// порядка дешевле программной (замерено: 0.25 мс против 13.8) - бюджет порций на кадр обязан
	/// это учитывать, иначе темп раундов упирается в число кадров, и ускорение невидимо.</summary>
	public bool Hardware { get; private set; }

	/// <summary>Бюджет порций на кадр под ЭТОТ путь и ЭТОТ веер: цель - единицы миллисекунд GPU на
	/// кадр. Аппаратная порция стоит ~0.002 мс на луч веера (2048 проб), программная - в ~50 раз
	/// дороже; отсюда обратная пропорция от числа лучей.</summary>
	/// <summary>Бюджет ЛУЧЕЙ на кадр - общий на все объёмы. Считать в порциях было ошибкой: порция
	/// это 2048 проб, но её цена зависит от веера, а объёмов несколько (база + каскады), и GPU
	/// видит их СУММУ. На сцене в 100k проб три объёма по 16 порций при 1024 лучах давали под сотню
	/// миллионов лучей в кадр - отсюда 5 кадров в секунду и петля «кадр не завершился → страницы
	/// динамической кучи не переработаны → выделяются новые» (см. лог: страница на каждый таймаут).
	///
	/// Числа - от порядка, замеренного на этом же пути: аппаратная трассировка ~0.25 мс на порцию
	/// в 2048 проб при 128 лучах, то есть примерно миллион лучей на миллисекунду; программная в
	/// полсотни раз медленнее. Целимся в единицы миллисекунд GPU на пробы за кадр.</summary>
	private const int HardwareRayBudgetPerFrame = 4_000_000;
	private const int SoftwareRayBudgetPerFrame = 200_000;

	/// <summary>Сколько порций этого объёма выпускать в кадре при заданном веере и числе объёмов,
	/// делящих бюджет. Минимум одна - иначе на гигантской сетке поле не двигалось бы вовсе.</summary>
	public int ChunksPerFrame(int raysPerRound, int volumeCount = 1)
	{
		long budget = (Hardware ? HardwareRayBudgetPerFrame : SoftwareRayBudgetPerFrame)
			/ Math.Max(volumeCount, 1);
		long raysPerChunk = (long)ProbesPerDispatch * Math.Max(raysPerRound, 8);
		return (int)Math.Clamp(budget / Math.Max(raysPerChunk, 1), 1, 32);
	}

	/// <summary>Сколько РАУНДОВ разрешено держать в полёте одновременно.
	///
	/// Единица (полная сериализация) была здесь ошибкой, и ровно там, где трассировка быстрая: CPU
	/// ждал, пока GPU ДОСЧИТАЕТ раунд, прежде чем записать команды следующего, поэтому между
	/// раундами GPU простаивал целую задержку кадра. Замерено на Sponza (D3D12, 6144 пробы):
	/// аппаратный раунд - 0.34 мс на диспатч против 7.89 мс у программного обхода, то есть в 23 раза
	/// быстрее, - а поле сходилось всего в полтора раза быстрее (152 раунда за 300 кадров против
	/// 102), потому что 148 кадров из 300 не делали ВООБЩЕ НИЧЕГО, дожидаясь забора. Программный
	/// путь этого не замечал: там раунд - десятки миллисекунд собственной работы, и простой на её
	/// фоне теряется.
	///
	/// Логически раунды по-прежнему последовательны (N+1 читает поле, записанное N) - но соблюдает
	/// это сам GPU: команды упорядочены, а чтение-после-записи между проходами закрыто переходом
	/// состояний на границе прохода (см. Dispatch). Забор нужен только чтобы очередь не росла
	/// быстрее, чем GPU её разбирает, а для этого достаточно ОГРАНИЧИТЬ её глубину, а не обнулять.
	///
	/// Глубина РАЗНАЯ у путей, и по той же причине, что и лучевой бюджет ниже: она обязана
	/// ограничивать не число раундов, а КОЛИЧЕСТВО ЗАПАСЁННОЙ GPU-РАБОТЫ. Аппаратный раунд на Sponza -
	/// около 1.7 мс, программный - около 40; шесть аппаратных раундов в очереди это те же ~10 мс, а
	/// шесть программных - четверть секунды невыполненных команд, то есть ровно та растущая очередь,
	/// которая заканчивалась таймаутом кадрового объекта и снятием устройства.
	/// Шесть давали ещё больше (888 раундов против 604), но на одном из семи прогонов с тремя
	/// каскадами при них всплыл тот самый «Timeout elapsed while waiting for the frame waitable
	/// object» - предвестник снятия устройства. Четыре берёт большую часть выигрыша (вчетверо к
	/// исходным 151) с запасом до этой границы; менять - только с этим же замером на руках.</summary>
	private ulong MaxRoundsInFlight => Hardware ? 4UL : 2UL;

	/// <summary>Готов ли GPU принять следующую ПОРЦИЮ - то есть не набралось ли уже
	/// <see cref="MaxRoundsInFlight"/> невыполненных раундов.</summary>
	public bool IsReady
	{
		get
		{
			// Сравнение через вычитание, а не «completed >= value - N»: значения беззнаковые, и на
			// первых раундах правая часть ушла бы в переполнение вниз.
			var completed = _roundFence.GetCompletedValue();
			return completed >= _roundFenceValue || _roundFenceValue - completed < MaxRoundsInFlight;
		}
	}

	/// <summary>Раунд ещё не начат - ни одной порции не выпущено. Момент, в который безопасно менять
	/// сцену под лучами: пересобрать TLAS СЕРЕДИНЕ раунда команды позволяют (они упорядочены, и
	/// драйвер расставит барьеры), но тогда одна половина проб отследит лучи по старой сцене, а
	/// вторая по новой, и раунд запишет в поле шов.</summary>
	public bool AtRoundStart => _surfaceChunkStart == 0 && _probeChunkStart == 0;

	/// <summary>Доводит отложенную прокрутку до GPU НЕЗАВИСИМО от того, дойдут ли до этого объёма
	/// раунды в этом кадре.
	///
	/// Это не дубль того, что делает начало раунда, а лечение застоя. Раунды каскада гасятся тремя
	/// внешними условиями разом - сходимостью БАЗОВОГО объёма, его же забором и бюджетом порций, -
	/// и пока применение прокрутки жило только внутри RunRound, любое из них морозило каскад на
	/// месте: камера уезжает, заявки копятся и перезаписывают друг друга, объём стоит. Сам переезд
	/// забора не требует: это обновление буферов и карты индирекции, оно упорядочено в потоке
	/// команд и не занимает GPU счётом.
	///
	/// Граница раунда всё же обязательна - раскладку нельзя менять между порциями одного раунда
	/// (см. <see cref="SyncScrollState"/>), - но ждать её долго не приходится: порции идут внутри
	/// кадра, а не через кадр.</summary>
	public void SettlePendingScroll(ProbeGiBakeSession session, ProbeGiBaker baker)
	{
		if (!AtRoundStart)
		{
			return;
		}

		session.ApplyPendingScroll(baker);
		SyncScrollState(session);
	}

	/// <summary>Продвигает раунд на одну порцию работы. Возвращает true, когда раунд ЗАВЕРШЁН -
	/// только тогда вызывающий двигает счётчики сессии.
	///
	/// Раунд не выпускается одним диспатчем нарочно: при крупном бюджете проб это миллионы лучей в
	/// одном вызове, GPU занят им сотни миллисекунд, презентация всё это время голодает, кадровый
	/// объект уходит в таймаут и следом снимается устройство. Порциями та же работа растягивается
	/// на несколько кадров, и между ними GPU успевает отдать кадр на экран.
	///
	/// Направления лучей и вес раунда приходят снаружи - их считает та же логика, что и на CPU
	/// (см. ProbeGiBaker.RoundRayDirections / RoundBlendWeight), чтобы оба пути можно было сверять.
	/// Внутри раунда они обязаны быть одинаковыми для всех порций, поэтому запоминаются на первой.</summary>
	public unsafe bool RunRound(ProbeGiBakeSession session, ProbeGiBaker baker,
		Vector3[] rayDirections, float alpha)
	{
		var context = _api.ImmediateContext;

		// ОСТАНОВКА СОШЕДШЕГОСЯ ОБЪЁМА («Probe Variability», см. ProbeVariabilityCS.hlsl). Проверка
		// стоит на ГРАНИЦЕ раунда: начатый раунд надо доводить до конца, иначе часть проб останется
		// с полем от одного веера, а часть - от другого.
		if (_surfaceChunkStart == 0 && _probeChunkStart == 0 && IsConverged(session))
		{
			SkippedRounds++;
			return true;
		}

		// Направления и параметры заливаются один раз на раунд: порции обязаны видеть одно и то же,
		// иначе пробы одной сетки посчитаются по разным веерам.
		if (_surfaceChunkStart == 0 && _probeChunkStart == 0)
		{
			// ЕДИНСТВЕННОЕ место, где объём переезжает, - и сразу за ним выгрузка раскладки на GPU.
			// Порядок этих двух строк и есть лечение расхождения: мировые позиции проб читают трое
			// (материалы, этот раунд, дебаг-оверлей), и стоит применить прокрутку в стороне от
			// выгрузки - они разъедутся по поколениям раскладки на всё время до следующего раунда
			// (см. ProbeGiBakeSession.Scroll). Ниже в этом же блоке session.Origin уходит в кбуфер
			// раунда, так что заявка обязана исполниться именно ЗДЕСЬ, а не после.
			session.ApplyPendingScroll(baker);
			SyncScrollState(session);

			var dirs = new Vector4[MaxRaysPerRound];
			for (int i = 0; i < rayDirections.Length; i++)
			{
				dirs[i] = new Vector4(rayDirections[i], 0f);
			}

			context.UpdateBuffer<Vector4>(_rayDirections, 0, dirs.AsSpan(),
				ResourceStateTransitionMode.Transition);

			_maxRayLuminance = session.MaxRayLuminance;
			_maxStep = session.MaxStep;
			_roundParams = new RoundParams
			{
				GridOrigin = new Vector4(session.Origin, alpha),
				GridCell = new Vector4(session.Cell, baker.RayTMax),
				SunDirection = new Vector4(session.SunDirection, rayDirections.Length),
				SunColor = new Vector4(session.SunColor, _probeCount),
				Round = new Vector4(baker.SceneEpsilon, session.Cell.Length() * 1.5f,
					session.Cell.Length() * 0.05f, session.Feedback),
				Relocation = new Vector4(session.RelocationLimit, session.AccumulationGamma,
					session.Realtime ? 1f : 0f,
					// Сон проб - только в УСТОЯВШЕМСЯ реальном времени: вес раунда на полу (свет не
					// менялся) и окно релокации закрыто (сцена не двигалась). Любое событие снимает
					// условие, и весь следующий отрезок все пробы бодрствуют.
					// Абсолютный порог веса обязателен: при крупном весе (0.1+) каждый шаг пробы
					// заметен глазу, и шахматное прореживание сна превращается в попкорн - соседние
					// пробы обновляются в разные кадры видимыми скачками. Спать можно только когда
					// шаги мелкие настолько, что их рассинхрон неразличим.
					session.Realtime && session.RelocationRoundsLeft == 0
						&& alpha <= session.MinBlend * 1.001f && alpha <= 0.05f
						? 1f + (session.Sequence & 3)
						: 0f),
				// Свежим пробам (слот только что заселён прокруткой) релокация разрешена независимо
				// от общесеточного окна: они появились там, где поля ещё не было, и половина из них
				// стоит в стенах - а открывать окно всему объёму на каждый сдвиг камеры значило бы
				// расшатывать ровно то поле, которое прокрутка и бережёт (см. FreshRelocationLimit).
				// y - сколько первых лучей веера ФИКСИРОВАННЫЕ (см. ProbeGiBaker.FixedRayCount):
				// они не вращаются по номеру раунда, не идут ни в радианс, ни в карту глубин, и
				// кормят только геометрические решения - релокацию и классификацию. Значение
				// обязано совпадать с раскладкой, по которой построен _ProbeRayDirections, поэтому
				// берётся из ТОЙ ЖЕ сессии и в том же месте, где заливается сам веер.
				Scroll = new Vector4(session.FreshRelocationLimit, session.FixedRays, 0f, 0f),
			};

			// Прогревающего диспатча здесь больше нет. Он существовал, чтобы снять «холод» с
			// въехавших кирпичей отдельным заходом, не дожидаясь конца раунда: слот доставался
			// новому кирпичу вместе с ЧУЖИМ полем, показывать такое было нельзя, и индирекция
			// прятала его до собственных значений. Тороидальной прокрутке прятать нечего - въехавшая
			// плоскость обнуляется на месте, в том же диспатче, что и обычная работа (см. GridClear).
		}

		// Сначала до конца прогоняется кэш поверхностей: лучи раунда забирают из него радианс, так
		// что он обязан быть готов раньше первой порции проб. В РЕАЛЬНОМ ВРЕМЕНИ проход пропущен
		// целиком: кэш там не читается (см. ProbeRelocation.z в шейдере), и обновлять его - значит
		// жечь по теневому лучу на воксель впустую. Ветка по СЕССИИ, а не по снятому в начале
		// раунда флагу, сознательно: переключение режима между порциями раунда просто досчитает
		// кэш или бросит его - обе судьбы безобидны, кэш никем не читается.
		if (_surfacePso != null && !session.Realtime && _surfaceChunkStart < _surfaceVoxelCount)
		{
			int end = Math.Min(_surfaceChunkStart + VoxelsPerDispatch, _surfaceVoxelCount);
			Dispatch(context, _surfacePso, _surfaceSrb[_writeIndex], _surfaceChunkStart, end,
				firstOfPass: _surfaceChunkStart == 0);
			_surfaceChunkStart = end;
			return false;
		}

		int probeEnd = Math.Min(_probeChunkStart + ProbesPerDispatch, _probeCount);
		Dispatch(context, _pso, _srb[_writeIndex], _probeChunkStart, probeEnd,
			firstOfPass: _probeChunkStart == 0);
		_probeChunkStart = probeEnd;

		if (_probeChunkStart < _probeCount)
		{
			return false;
		}

		// Свёртка изменчивости - строго ЗДЕСЬ, после последней порции: она читает весь буфер разом,
		// а порции пишут его по кусочкам, растянутым на несколько кадров.
		//
		// Только в реальном времени: в запечке раунды и так конечны, останавливать нечего, а свёртка
		// с копией в staging - это лишний диспатч и лишний барьер на каждый раунд ради числа,
		// которое там никто не прочитает.
		// Числом цену не подкрепляю намеренно: «gpu converge» в headless-прогоне гуляет от 16 до 66
		// мс на НЕИЗМЕННОМ коде, и любая разница меньше этого разброса ничего не значит.
		if (session.Realtime)
		{
			RunVariability(context, session);
		}

		// Атласы остаются после диспатча в UnorderedAccess (у Vulkan это layout GENERAL), а
		// материалы ждут их в ShaderResource - без явного возврата валидация ловит несоответствие
		// layout-а на отрисовке (VUID-vkCmdDraw-None-09600). Автоматический переход на месте
		// использования не спасает: дескрипторы материалов уже записаны.
		if (_atlasTextures.Length > 0)
		{
			var transitions = new StateTransitionDesc[_atlasTextures.Length];
			for (int i = 0; i < _atlasTextures.Length; i++)
			{
				transitions[i] = new StateTransitionDesc
				{
					Resource = _atlasTextures[i],
					OldState = Diligent.ResourceState.UnorderedAccess,
					NewState = Diligent.ResourceState.ShaderResource,
					Flags = StateTransitionFlags.UpdateState,
				};
			}

			context.TransitionResourceStates(transitions);

			// Атласы ушли в ShaderResource - на следующем раунде их снова надо перевести в UAV.
		}

		SignalRound(context);

		// Раунд завершён: свежезаписанный буфер становится читаемым для следующего - зеркало
		// ProbeGiBakeSession.Swap. Порции сбрасываются под новый раунд.
		_writeIndex = 1 - _writeIndex;
		_surfaceChunkStart = 0;
		_probeChunkStart = 0;
		return true;
	}

	/// <summary>Один диспатч по диапазону элементов [start, end).</summary>
	private void Dispatch(IDeviceContext context, IPipelineState pso, IShaderResourceBinding srb,
		int start, int end, bool firstOfPass)
	{
		_roundParams.Chunk = new Vector4(start, end, _maxRayLuminance, _maxStep);
		var chunkParams = _roundParams;

		// Смещение внутрь буфера, а не перезапись общего блока: перезапись создаёт зависимость
		// «запись после чтения» и сериализует диспатчи (замерено 4086 мс против 695 мс), а
		// переустановка переменной в SRB перед диспатчем невозможна на Vulkan - нельзя менять
		// дескрипторный набор, по которому идёт запись команд. Динамическое смещение меняет только
		// смещение, дескриптор остаётся тем же.
		Require(srb, "ProbeRoundParams").SetBufferOffset((uint)(_paramsSlot * ParamsStride), 0);
		context.UpdateBuffer<RoundParams>(_params, (ulong)(_paramsSlot * ParamsStride),
			new ReadOnlySpan<RoundParams>(in chunkParams), ResourceStateTransitionMode.Transition);
		_paramsSlot = (_paramsSlot + 1) % ParamsSlots;

		context.SetPipelineState(pso);

		// Переходы состояний нужны на ГРАНИЦЕ ПРОХОДА, а внутри прохода - нет: его порции пишут
		// непересекающиеся диапазоны, а барьер UAV драйвер ставит консервативно и сериализует
		// диспатчи целиком (замерено: 12.6 мс на диспатч против 2.15 без барьеров).
		//
		// Именно на границе прохода они обязательны, и это не формальность: между проходом кэша и
		// проходом проб стоит чтение после записи (пробы читают радианс, который только что записал
		// кэш), а атласы к первой порции проб надо вернуть из ShaderResource в UAV. Попытка
		// пропустить переходы везде, кроме первой порции РАУНДА, дала и то, и другое: 480 расхождений
		// с CPU вместо 107 и 133 ошибки валидации на Vulkan.
		context.CommitShaderResources(srb, firstOfPass || ForceTransitions
			? ResourceStateTransitionMode.Transition
			: ResourceStateTransitionMode.Verify);
		context.DispatchCompute(new DispatchComputeAttribs
		{
			ThreadGroupCountX = (uint)((end - start + 63) / 64),
			ThreadGroupCountY = 1,
			ThreadGroupCountZ = 1,
		});
	}

	/// <summary>Метка завершения РАУНДА - по ней IsReady решает, можно ли начинать следующий.
	///
	/// Именно раунда, а не порции: ставить забор на каждую порцию значит ждать, пока GPU досчитает
	/// её, прежде чем выпустить следующую - никакого перекрытия CPU и GPU, одна порция за кадр.
	/// При 1.7 мс счёта на порцию мы ждали целый кадр, и весь выигрыш от переноса на GPU уходил в
	/// ожидание (замерено: 182 кадра из 300 не делали вообще ничего).</summary>
	private void SignalRound(IDeviceContext context) =>
		context.EnqueueSignal(_roundFence, ++_roundFenceValue);

	/// <summary>Считывает поле проб обратно - для сверки с CPU-эталоном.</summary>
	public unsafe Vector4[] ReadField()
	{
		// Последним писали в буфер, который после свопа стал читаемым (см. RunRound).
		var field = new Vector4[_probeCount * 4];
		fixed (Vector4* dst = field)
		{
			_api.ImmediateContext.ReadBufferExt<Vector4>(_api.Device, _field[1 - _writeIndex], dst,
				(uint)(field.Length * sizeof(Vector4)));
		}

		return field;
	}

	private void BindSrv(IShaderResourceBinding srb, string name, IBuffer buffer)
	{
		var view = buffer.CreateView(new BufferViewDesc
		{
			Name = $"{buffer.GetDesc().Name} SRV",
			ViewType = BufferViewType.ShaderResource,
			ByteOffset = 0,
			ByteWidth = buffer.GetDesc().Size,
		});

		_views.Add(view);
		Require(srb, name).Set(view, SetShaderResourceFlags.AllowOverwrite);
	}

	private void BindUav(IShaderResourceBinding srb, string name, IBuffer buffer)
	{
		var view = buffer.CreateView(new BufferViewDesc
		{
			Name = $"{buffer.GetDesc().Name} UAV",
			ViewType = BufferViewType.UnorderedAccess,
			ByteOffset = 0,
			ByteWidth = buffer.GetDesc().Size,
		});

		_views.Add(view);
		Require(srb, name).Set(view, SetShaderResourceFlags.AllowOverwrite);
	}

	private static IShaderResourceVariable Require(IShaderResourceBinding srb, string name) =>
		srb.GetVariableByName(ShaderType.Compute, name)
		?? throw new InvalidOperationException(
			$"Shader variable '{name}' not found in ProbeRoundCS - renamed or optimised away");

	private static unsafe IBuffer CreateImmutable<T>(IRenderDevice device, string name, T[] data, int stride)
		where T : unmanaged
	{
		fixed (T* ptr = data)
		{
			return device.CreateBuffer(new BufferDesc
			{
				Name = name,
				Usage = Usage.Immutable,
				BindFlags = BindFlags.ShaderResource,
				Mode = BufferMode.Structured,
				ElementByteStride = (uint)stride,
				Size = (ulong)((long)data.Length * sizeof(T)),
			}, new BufferData
			{
				Data = new IntPtr(ptr),
				DataSize = (ulong)((long)data.Length * sizeof(T)),
			});
		}
	}

	/// <summary>Структурированный буфер, который можно перезаливать: раскладка кирпичей у
	/// прокручиваемого объёма меняется (см. <see cref="SyncScrollState"/>), а Immutable по
	/// определению нет. Начальные данные передаются сразу - до первого сдвига перезаливок не будет
	/// вовсе, и лишнего UpdateBuffer в кадре создания не появляется.</summary>
	private static unsafe IBuffer CreateUpdatable<T>(IRenderDevice device, string name, T[] data,
		int stride)
		where T : unmanaged
	{
		fixed (T* ptr = data)
		{
			return device.CreateBuffer(new BufferDesc
			{
				Name = name,
				Usage = Usage.Default,
				BindFlags = BindFlags.ShaderResource,
				Mode = BufferMode.Structured,
				ElementByteStride = (uint)stride,
				Size = (ulong)((long)data.Length * sizeof(T)),
			}, new BufferData
			{
				Data = new IntPtr(ptr),
				DataSize = (ulong)((long)data.Length * sizeof(T)),
			});
		}
	}

	/// <summary>Раз во столько раундов сошедшийся объём всё равно считается целиком. Страховка от
	/// изменения, которое не подняло ни веса раунда, ни окна релокации: без неё остановка была бы
	/// ловушкой без выхода, а так задержка отклика ограничена сверху. 32 раунда - около половины
	/// секунды при раунде на кадр, и стоит эта страховка 3% работы.</summary>
	private const int VariabilityRefreshPeriod = 32;

	/// <summary>Объём сошёлся - раунд можно не пускать вовсе.
	///
	/// Условий много, и каждое закрывает свою дыру:
	///   - режим реального времени и включённый порог: в запечке останавливаться нечему, там раунды
	///     и так конечны;
	///   - вес раунда на полу и окно релокации закрыто (тот же признак устоявшегося состояния, по
	///     которому засыпают пробы): любое событие сцены поднимает вес, и остановка снимается сама;
	///   - нет неисполненной заявки на переезд: объём вот-вот получит пустые кирпичи с краю, и они
	///     обязаны сойтись;
	///   - изменчивость измерена и ниже порога. До первой вычитки она бесконечна, то есть на старте
	///     остановка невозможна по построению;
	///   - раунд не «страховочный» (см. VariabilityRefreshPeriod).</summary>
	private bool IsConverged(ProbeGiBakeSession session)
	{
		// Геометрия сцены изменилась (объект подвинули - см. ProbeGiBakeSession.InvalidateGeometry).
		// В реальном времени это НЕ поднимает вес раунда сознательно, поэтому остальные условия ниже
		// такое событие не заметили бы, и остановленный объём замер бы навсегда - объект уехал, а его
		// освещение осталось лежать следом на прежнем месте. Сбрасываем измеренную изменчивость: до
		// следующей удачной свёртки она бесконечна, то есть объём заведомо считается несошедшимся и
		// раунды идут.
		if (_geometryVersion != session.GeometryVersion)
		{
			_geometryVersion = session.GeometryVersion;
			_averageVariability = float.PositiveInfinity;
			return false;
		}

		if (!session.Realtime || session.VariabilityThreshold <= 0f || session.HasPendingScroll)
		{
			return false;
		}

		if (session.RelocationRoundsLeft > 0
			|| ProbeGiBaker.RoundBlendWeight(session) > session.MinBlend * 1.001f)
		{
			return false;
		}

		if (session.Sequence % VariabilityRefreshPeriod == 0)
		{
			return false;
		}

		return _averageVariability < session.VariabilityThreshold;
	}

	/// <summary>Сворачивает изменчивость проб и забирает результат ПРОШЛОЙ свёртки - без стойла.
	///
	/// Порядок именно такой: сначала читаем копию, сделанную несколько раундов назад (к этому
	/// моменту она заведомо доехала, и Map с DoNotWait отдаёт её сразу), и только потом ставим в
	/// очередь новую. Читай мы свежую - пришлось бы ждать GPU прямо в кадре, а ради числа, которое
	/// за раунд меняется на доли процента, платить кадром нелепо. Если копия почему-то ещё не
	/// готова, вычитка просто пропускается: прошлое значение остаётся в силе, а признак сходимости
	/// от одного раунда задержки не портится.</summary>
	private unsafe void RunVariability(IDeviceContext context, ProbeGiBakeSession session)
	{
		// Вычитка прошлой свёртки.
		if (_variabilityPending >= 0 && session.Sequence - _variabilityPending >= VariabilityReadLag)
		{
			var mapped = context.MapBuffer(_variabilityStaging, MapType.Read, MapFlags.DoNotWait);
			if (mapped != IntPtr.Zero)
			{
				try
				{
					var groups = new ReadOnlySpan<Vector2>(mapped.ToPointer(), VariabilityGroups);
					float sum = 0f, weight = 0f;
					for (int i = 0; i < groups.Length; i++)
					{
						sum += groups[i].X;
						weight += groups[i].Y;
					}

					// Нулевой вес - в объёме нет ни одной пробы, чьё мнение считается (все в стенах
					// либо объём пуст). Сходившимся такой объём называть нельзя: считать нечего, а
					// не «всё спокойно», поэтому оставляем бесконечность.
					_averageVariability = weight > 0f ? sum / weight : float.PositiveInfinity;
					_variabilityPending = -1;
				}
				finally
				{
					context.UnmapBuffer(_variabilityStaging, MapType.Read);
				}
			}
		}

		// Новая свёртка.
		if (_variabilitySrb == null)
		{
			return;
		}

		var varParams = new Vector4(_probeCount, 0f, 0f, 0f);
		context.UpdateBuffer<Vector4>(_variabilityParams, 0,
			new ReadOnlySpan<Vector4>(in varParams), ResourceStateTransitionMode.Transition);

		context.SetPipelineState(_variabilityPso);
		context.CommitShaderResources(_variabilitySrb, ResourceStateTransitionMode.Transition);
		context.DispatchCompute(new DispatchComputeAttribs
		{
			ThreadGroupCountX = VariabilityGroups,
			ThreadGroupCountY = 1,
			ThreadGroupCountZ = 1,
		});

		// Копия в staging ставится в ту же очередь команд и исполнится после свёртки - ждать здесь
		// нечего, читать её будем через VariabilityReadLag раундов.
		if (_variabilityPending < 0)
		{
			context.CopyBuffer(_variabilitySum, 0, ResourceStateTransitionMode.Transition,
				_variabilityStaging, 0, (ulong)(VariabilityGroups * sizeof(Vector2)),
				ResourceStateTransitionMode.Transition);
			_variabilityPending = session.Sequence;
		}
	}

	/// <summary>RW-буфер аккумулятора. Начальные данные НЕ передаются: заливка нулей через
	/// BufferData тащит десятки мегабайт по динамической куче ПРЯМО В КАДРЕ, а куча выделяет под
	/// это страницы синхронно - на большой сцене с каскадами это была пачка «Created dynamic memory
	/// page» вперемежку с «Timeout elapsed while waiting for the frame waitable object» при
	/// создании сессии. D3D12 и Vulkan отдают память ресурсов обнулённой, а нулевой старт
	/// аккумуляторов гарантирует ClearBuffers ниже - дешёвой командой, без загрузки с CPU.</summary>
	private static unsafe IBuffer CreateRw<T>(IRenderDevice device, string name, int count, int stride)
		where T : unmanaged
	{
		return device.CreateBuffer(new BufferDesc
		{
			Name = name,
			Usage = Usage.Default,
			BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
			Mode = BufferMode.Structured,
			ElementByteStride = (uint)stride,
			Size = (ulong)((long)count * sizeof(T)),
		});
	}

	/// <summary>Обнуляет аккумуляторы загрузкой нулей. Команды заполнения буфера в этой версии
	/// Diligent-биндингов нет (FillBufferRegion отсутствует), поэтому пока так - и это ИЗВЕСТНАЯ
	/// цена: на большой сцене с каскадами десятки мегабайт нулей едут через динамическую кучу прямо
	/// в кадре («Created dynamic memory page» вперемежку с таймаутами кадрового объекта при
	/// создании сессии). Правильное лечение - размазать создание объёмов по кадрам, см. долг в
	/// PollProbeBake.</summary>
	private static unsafe void ClearBuffer<T>(IDeviceContext context, IBuffer buffer, int count)
		where T : unmanaged
	{
		var zeros = new T[count];
		fixed (T* ptr = zeros)
		{
			context.UpdateBuffer(buffer, 0, (ulong)((long)count * sizeof(T)), new IntPtr(ptr),
				ResourceStateTransitionMode.Transition);
		}
	}

	private void ClearBuffers(IDeviceContext context)
	{
		ClearBuffer<Vector4>(context, _field[0], _probeCount * 4);
		ClearBuffer<Vector4>(context, _field[1], _probeCount * 4);
		ClearBuffer<int>(context, _counters, _probeCount * 4);
		ClearBuffer<Vector4>(context, _offsets, _probeCount);
		ClearBuffer<Vector4>(context, _visibility, _probeCount * VisRes * VisRes);
		ClearBuffer<Vector2>(context, _variability, _probeCount);
		ClearBuffer<Vector2>(context, _variabilitySum, VariabilityGroups);
	}

	public void Dispose()
	{
		foreach (var view in _views)
		{
			view.Dispose();
		}

		foreach (var srb in _srb)
		{
			srb.Dispose();
		}

		// _pso/_surfacePso принадлежат ProbeRoundPipelines и переживают этот объект - не трогаем.
		_gridParams.Dispose();
		_params.Dispose();

		_variabilitySrb?.Dispose();
		_variabilityParams.Dispose();
		_variabilityStaging.Dispose();
		_variabilitySum.Dispose();
		_variability.Dispose();
		_rayDirections.Dispose();
		_visibility.Dispose();
		_counters.Dispose();
		_field[0].Dispose();
		_field[1].Dispose();
		_bvhTriangles.Dispose();
		_bvhOrder.Dispose();
		_bvhNodes.Dispose();
		_roundFence.Dispose();
		_environmentSampler.Release();
	}
}
