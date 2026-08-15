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

		// Углы кирпичей: тот же формат, что у compute-раунда (int4: угол в координатах виртуальной
		// сетки + уровень) - VS восстанавливает по нему мировую позицию узла.
		var brickData = new int[session.BrickTotal * 4];
		for (int i = 0; i < session.BrickTotal; i++)
		{
			brickData[i * 4 + 0] = session.BrickCellOrigin[i * 3 + 0];
			brickData[i * 4 + 1] = session.BrickCellOrigin[i * 3 + 1];
			brickData[i * 4 + 2] = session.BrickCellOrigin[i * 3 + 2];
			brickData[i * 4 + 3] = session.BrickLevel[i];
		}

		_brickOrigins = dilApi.CreateBuffer<int>(brickData.Length, new BufferInfo
		{
			name = "ProbeDebugBrickOrigin",
			type = BufferHandleType.Structured,
			access = HandleAccess.Vertex,
		});

		// Заливка один раз: углы кирпичей геометричны и живут, пока жива сессия. Stride буфера -
		// int, а шейдер читает int4: размер совпадает, раскладка плоская.
		dilApi.ImmediateContext.UpdateBuffer<int>(((DiligentBufferHandle)_brickOrigins).Buffer, 0,
			brickData.AsSpan(), global::Diligent.ResourceStateTransitionMode.Transition);

		_material.SetBuffer("_ProbeBrickOrigin", _brickOrigins, HandleAccess.Vertex);
		_material.SetTexture("_ProbeOffset", textures.Offset, HandleAccess.Vertex);
		_material.SetTexture("_ProbeSh0", textures.Sh0, HandleAccess.Vertex);
		_material.SetTexture("_ProbeSh1", textures.Sh1, HandleAccess.Vertex);

		// Радиус шарика - доля минимального шага сетки: на плотной сетке шарики мельче и не
		// сливаются в кашу, на редкой - крупнее и видны издалека.
		float radius = textures.MinCellSize * 0.12f;
		var constants = new ProbeDebugParams
		{
			GridOriginRadius = new Vector4(session.Origin, radius),
			GridCellCount = new Vector4(session.Cell, session.ProbeCount),
			Pool = new Vector4(session.PoolColumns, tint.X, tint.Y, tint.Z),
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
