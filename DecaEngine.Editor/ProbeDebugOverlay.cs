using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Core;
using DecaEngine.Graphics.Diligent;

namespace DecaEngine.Editor;

/// <summary>
/// Дебаг-вид probe-GI: шарик-октаэдр на каждую пробу в её ФАКТИЧЕСКОЙ позиции - узел сетки плюс
/// смещение релокации из атласа (см. ProbeDebugVS.hlsl). Кодировка на шарике: цвет = накопленный
/// SH L0, красный = невалидная («в стене»), голубая кромка = переехала релокацией.
///
/// Рисуется инлайн в конце ForwardPass (см. GraphicsPipelineSimple.InlineOverlay) - в уже
/// привязанный render target, с депт-тестом сцены, до резолва MSAA. Ни вершинного буфера, ни
/// инстансинга: один Draw(24 * probeCount), всё восстанавливается из SV_VertexID.
///
/// Живёт по СЕССИИ (сетка и атласы меняются вместе с ней): пересоздаётся там же, где
/// ProbeGiTextures. Позиции читаются из тех же атласов, что пишет раунд, поэтому вид одинаково
/// честен для GPU- и CPU-пути и не требует ни снимков, ни синхронизации.
/// </summary>
public sealed class ProbeDebugOverlay : IDisposable
{
	private struct ProbeDebugParams
	{
		public Vector4 GridOriginRadius;
		public Vector4 GridCellCount;
		public Vector4 Pool;
	}

	private readonly IMaterialObject _material;
	private readonly IBufferHandle _brickOrigins;
	private readonly uint _vertexCount;
	private readonly DiligentGraphicsApi _dilApi;
	private readonly float _radius;
	private readonly Vector3 _tint;

	/// <summary>Раскладка, под которую залиты углы кирпичей и угол объёма. Прокрутка её меняет (см.
	/// ProbeGiBakeSession.LayoutGeneration), и без сверки шарики остались бы стоять там, откуда
	/// объём уже уехал.</summary>
	private int _layoutGeneration = -1;

	/// <param name="tint">Цветовая метка объёма (аддитивная подкраска шариков): каскады получают
	/// свой цвет, чтобы отличаться от базовых проб - их шарики мельче и стоят среди базовых.</param>
	public ProbeDebugOverlay(DiligentGraphicsApi dilApi, IGraphicsApi api,
		IBatchRenderer batchRenderer, ProbeGiBakeSession session, ProbeGiTextures textures,
		uint msaaSamples, TextureObjectFormat colorFormat, Vector3 tint = default)
	{
		_vertexCount = (uint)session.ProbeCount * 24u;

		var vs = api.CreateShader("Probe Debug VS", "EditorAssets/shader", "ProbeDebugVS.hlsl",
			ShaderObjectType.Vertex);
		var ps = api.CreateShader("Probe Debug PS", "EditorAssets/shader", "ProbeDebugPS.hlsl",
			ShaderObjectType.Pixel);

		_material = api.CreateMaterial("Probe Debug Material");
		_material.SetShader(vs, ps);
		_material.SetState(api.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "Probe Debug PSO",
			RenderTargetFormats = [colorFormat],
			DepthStencilFormat = TextureObjectFormat.D32Float,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			// Без отбраковки: октаэдр выпуклый и перерисовка мизерная, а от порядка обхода вершин
			// массива в шейдере PSO зависеть не должен.
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			// Reversed-Z, как у всей сцены (см. ForwardPass: клир глубины нулём): шарики честно
			// прячутся за геометрией.
			DepthStencilState = new DepthStencilStateInfo
			{
				DepthEnable = true,
				DepthFunc = ComparisonFunctionType.GreaterEqual,
			},
			InputLayout = [],
			SampleCount = msaaSamples,
		}));

		batchRenderer.BindViewConstants(_material);

		_brickOrigins = dilApi.CreateBuffer<int>(session.BrickTotal * 4, new BufferInfo
		{
			name = "ProbeDebugBrickOrigin",
			type = BufferHandleType.Structured,
			access = HandleAccess.Vertex,
		});

		_material.SetBuffer("_ProbeBrickOrigin", _brickOrigins, HandleAccess.Vertex);
		_material.SetTexture("_ProbeOffset", textures.Offset, HandleAccess.Vertex);
		_material.SetTexture("_ProbeSh0", textures.Sh0, HandleAccess.Vertex);
		_material.SetTexture("_ProbeSh1", textures.Sh1, HandleAccess.Vertex);

		// Радиус шарика - доля минимального шага сетки: на плотной сетке шарики мельче и не
		// сливаются в кашу, на редкой - крупнее и видны издалека.
		_dilApi = dilApi;
		_radius = textures.MinCellSize * 0.12f;
		_tint = tint;
		Refresh(session);
	}

	/// <summary>Догоняет раскладку объёма: углы кирпичей и угол сетки в мире. Дёшево (кирпичей
	/// тысячи, не проб) и зовётся по номеру раскладки, то есть фактически только после прокрутки -
	/// без этого шарики остались бы стоять там, откуда объём уехал.</summary>
	public void Refresh(ProbeGiBakeSession session)
	{
		if (_layoutGeneration == session.LayoutGeneration)
		{
			return;
		}

		_layoutGeneration = session.LayoutGeneration;

		// Углы кирпичей: тот же формат и то же СОСТОЯНИЕ слота, что у compute-раунда (см.
		// ProbeRoundGpu.BrickWord) - по нему VS восстанавливает мировую позицию узла и прячет
		// шарики пустых слотов пула.
		var brickData = new int[session.BrickTotal * 4];
		for (int slot = 0; slot < session.BrickTotal; slot++)
		{
			brickData[slot * 4 + 0] = session.BrickCellOrigin[slot * 3 + 0];
			brickData[slot * 4 + 1] = session.BrickCellOrigin[slot * 3 + 1];
			brickData[slot * 4 + 2] = session.BrickCellOrigin[slot * 3 + 2];
			brickData[slot * 4 + 3] = session.BrickAlive[slot]
				? session.BrickLevel[slot] & 15
				: 512;
		}

		// Stride буфера - int, а шейдер читает int4: размер совпадает, раскладка плоская.
		_dilApi.ImmediateContext.UpdateBuffer<int>(((DiligentBufferHandle)_brickOrigins).Buffer, 0,
			brickData.AsSpan(), global::Diligent.ResourceStateTransitionMode.Transition);

		var constants = new ProbeDebugParams
		{
			GridOriginRadius = new Vector4(session.Origin, _radius),
			GridCellCount = new Vector4(session.Cell, session.ProbeCount),
			Pool = new Vector4(session.PoolColumns, _tint.X, _tint.Y, _tint.Z),
		};
		_material.SetConstant("ProbeDebugParams", ref constants, HandleAccess.Vertex);
	}

	/// <summary>Рисует шарики в УЖЕ привязанный render target - зовётся из ForwardPass через
	/// GraphicsPipelineSimple.InlineOverlay.</summary>
	public void Draw(ICommandBuffer cmd)
	{
		cmd.SetPipelineState(_material);
		cmd.CommitShaderResources(_material);
		cmd.Draw(_vertexCount);
	}

	public void Dispose()
	{
		_material.Release();
		_brickOrigins.Release();
	}
}
