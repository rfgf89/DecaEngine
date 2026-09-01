using System;
using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;

namespace DecaEngine.Editor;

/// <summary>
/// GPU-сторона дебаг-линий: заливает то, что за кадр накопил <see cref="DebugDraw"/>, и рисует двумя
/// дроу - с депт-тестом сцены и поверх всего.
///
/// Рисуется ИНЛАЙН в конце ForwardPass (см. GraphicsPipelineSimple.InlineOverlay), потому что только
/// там доступен депт-буфер сцены: без него «кость внутри меша» и «кость снаружи» выглядят
/// одинаково. Плата за это - HDR-таргет ДО тонемапа: чистый белый после экспозиции и тонемапа
/// приезжает серым. Поэтому у линий есть множитель яркости (см. <see cref="Intensity"/>) - не
/// украшение, а способ вытащить дебаг из-под тонемапа сцены, экспозиция которой заранее неизвестна.
///
/// ЧИСЛО ВЕРШИН В ДРОУ ЗАШИТО В ЗАМОРОЖЕННУЮ КОМАНДУ ГРАФА, а дебаг-геометрия меняется каждый кадр.
/// Поэтому дроу всегда идёт на ПОЛНУЮ ёмкость буфера, а лишние вершины гасятся нулевой альфой и
/// отсекаются в вершиннике (см. DebugLineVS.hlsl). Пересборки графа требует только РОСТ ёмкости -
/// событие редкое, а не покадровое.
/// </summary>
public sealed class DebugLineOverlay : IDisposable
{
	/// <summary>Один буфер линий: депт-тестируемые и «поверх всего» отличаются только состоянием
	/// глубины в PSO, поэтому всё остальное у них общее и живёт здесь.</summary>
	private sealed class Bucket
	{
		public IMaterialObject Material = null!;
		public IBufferHandle? Buffer;

		/// <summary>Ёмкость в вершинах. Она же - число вершин в дроу (см. шапку класса).</summary>
		public int Capacity;

		/// <summary>Сколько вершин буфера залито «живыми» в прошлом кадре: ровно этот хвост нужно
		/// погасить, если в этом кадре линий стало меньше. Гасить весь буфер незачем - за пределами
		/// прошлого кадра он уже нулевой.</summary>
		public int LiveLastFrame;

		public DebugLineVertex[] Scratch = [];
	}

	private struct DebugLineParams
	{
		public Vector4 Params;
	}

	private const int MinCapacity = 4096;

	private readonly DiligentGraphicsApi _dilApi;
	private readonly Bucket _depthTested = new();
	private readonly Bucket _onTop = new();

	private float _appliedIntensity = -1f;

	/// <summary>Множитель яркости линий - см. шапку про HDR. Значение по умолчанию подобрано так,
	/// чтобы линия читалась на средне-экспонированной сцене; на очень яркой или очень тёмной его
	/// правит ползунок в окне дебага.</summary>
	public float Intensity { get; set; } = 4f;

	public int DepthTestedCapacity => _depthTested.Capacity;
	public int OnTopCapacity => _onTop.Capacity;

	public DebugLineOverlay(DiligentGraphicsApi dilApi, IGraphicsApi api, IBatchRenderer batchRenderer,
		TextureObjectFormat colorFormat)
	{
		_dilApi = dilApi;

		var vs = api.CreateShader("Debug Line VS", "EditorAssets/shader", "DebugLineVS.hlsl",
			ShaderObjectType.Vertex);
		var ps = api.CreateShader("Debug Line PS", "EditorAssets/shader", "DebugLinePS.hlsl",
			ShaderObjectType.Pixel);

		_depthTested.Material = CreateMaterial(api, batchRenderer, vs, ps, colorFormat,
			"Debug Line Depth", depthTest: true);
		_onTop.Material = CreateMaterial(api, batchRenderer, vs, ps, colorFormat,
			"Debug Line OnTop", depthTest: false);

		ApplyIntensity();
	}

	private static IMaterialObject CreateMaterial(IGraphicsApi api, IBatchRenderer batchRenderer,
		IShaderObject vs, IShaderObject ps, TextureObjectFormat colorFormat, string name, bool depthTest)
	{
		var material = api.CreateMaterial($"{name} Material");
		material.SetShader(vs, ps);
		material.SetState(api.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = $"{name} PSO",
			RenderTargetFormats = [colorFormat],
			DepthStencilFormat = TextureObjectFormat.D32Float,
			PrimitiveTopology = PrimitiveTopologyType.LineList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo
			{
				DepthEnable = depthTest,

				// Reversed-Z, как у всей сцены (ForwardPass чистит глубину нулём). Записи глубины
				// нет намеренно: дебаг-линия не должна закрывать собой ни сцену, ни другую линию -
				// пересечение двух каркасов обязано быть видно целиком.
				DepthFunc = ComparisonFunctionType.GreaterEqual,
				DepthWriteEnable = false,
			},
			InputLayout =
			[
				new InputLayoutElementInfo
				{
					InputIndex = 0,
					BufferSlot = 0,
					NumComponents = 3,
					ValueType = InputElementValueType.Float32,
				},
				new InputLayoutElementInfo
				{
					InputIndex = 1,
					BufferSlot = 0,
					NumComponents = 4,
					ValueType = InputElementValueType.Float32,
				},
			],
		}));

		batchRenderer.BindViewConstants(material);
		return material;
	}

	/// <summary>
	/// Заливает кадр дебаг-геометрии в GPU. Возвращает true, если замороженные команды графа устарели
	/// (выросла ёмкость буфера) - вызывающий обязан позвать InvalidateGraph.
	///
	/// Звать ДО исполнения графа: заливка идёт немедленным контекстом, как и у контура выделения.
	/// </summary>
	public bool Upload(DebugDraw draw)
	{
		if (_appliedIntensity != Intensity)
		{
			ApplyIntensity();
		}

		bool commandsDirty = UploadBucket(_depthTested, draw.DepthTestedVertices());
		commandsDirty |= UploadBucket(_onTop, draw.OnTopVertices());

		return commandsDirty;
	}

	private bool UploadBucket(Bucket bucket, ReadOnlySpan<DebugLineVertex> vertices)
	{
		bool commandsDirty = false;

		if (bucket.Buffer == null || bucket.Capacity < vertices.Length)
		{
			// Пересоздание ждёт GPU: старый буфер мог читаться ещё летящим кадром (та же причина,
			// что в SelectionOutlineOverlay).
			_dilApi.ImmediateContext.Flush();
			_dilApi.ImmediateContext.WaitForIdle();

			bucket.Buffer?.Release();

			int capacity = Math.Max(MinCapacity, bucket.Capacity == 0 ? MinCapacity : bucket.Capacity);
			while (capacity < vertices.Length)
			{
				capacity *= 2;
			}

			bucket.Capacity = capacity;
			bucket.Scratch = new DebugLineVertex[capacity];
			bucket.Buffer = _dilApi.CreateBuffer<DebugLineVertex>(capacity, new BufferInfo
			{
				name = "Debug Line VB",
				type = BufferHandleType.Vertex,
			});

			// Свежий буфер содержит мусор, а дроу идёт на всю ёмкость: без начального обнуления в
			// кадре появились бы линии из неинициализированной памяти - с координатами порядка 1e38,
			// то есть на весь экран.
			bucket.LiveLastFrame = capacity;
			commandsDirty = true;
		}

		var scratch = bucket.Scratch;
		vertices.CopyTo(scratch);

		// Хвост прошлого кадра гасится нулевой альфой (см. DebugLineVS.hlsl). Чистится ровно он, а не
		// весь буфер: дальше него уже нули.
		int tail = Math.Min(bucket.LiveLastFrame, bucket.Capacity);
		for (int i = vertices.Length; i < tail; i++)
		{
			scratch[i] = default;
		}

		int upload = Math.Max(vertices.Length, tail);
		bucket.LiveLastFrame = vertices.Length;

		if (upload > 0)
		{
			var buffer = ((DiligentBufferHandle)bucket.Buffer!).Buffer;
			if (buffer != null)
			{
				_dilApi.ImmediateContext.UpdateBuffer<DebugLineVertex>(buffer, 0,
					scratch.AsSpan(0, upload), global::Diligent.ResourceStateTransitionMode.Transition);
			}
		}

		return commandsDirty;
	}

	/// <summary>Тело инлайн-оверлея: рисуется в УЖЕ привязанный render target ForwardPass-а.</summary>
	public void Draw(ICommandBuffer cmd)
	{
		DrawBucket(cmd, _depthTested);
		DrawBucket(cmd, _onTop);
	}

	private static void DrawBucket(ICommandBuffer cmd, Bucket bucket)
	{
		if (bucket.Buffer == null || bucket.Capacity == 0)
		{
			return;
		}

		cmd.SetPipelineState(bucket.Material);
		cmd.CommitShaderResources(bucket.Material);
		cmd.SetVertexBuffers(0, [bucket.Buffer], [0ul], SetVertexBuffersFlags.Reset);
		cmd.Draw((uint)bucket.Capacity);
	}

	private void ApplyIntensity()
	{
		_appliedIntensity = Intensity;

		var constants = new DebugLineParams { Params = new Vector4(Intensity, 0f, 0f, 0f) };
		_depthTested.Material.SetConstant("DebugLineParams", ref constants, HandleAccess.Vertex);
		_onTop.Material.SetConstant("DebugLineParams", ref constants, HandleAccess.Vertex);
	}

	public void Dispose()
	{
		_depthTested.Material.Release();
		_onTop.Material.Release();
		_depthTested.Buffer?.Release();
		_onTop.Buffer?.Release();
	}
}
