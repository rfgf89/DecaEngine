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
/// привязанный render target, с депт-тестом сцены. Ни вершинного буфера, ни
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

		/// <summary>xyz - размер сетки проб; по нему VS разворачивает номер шарика в узел.</summary>
		public Vector4 GridCounts;

		public Vector4 Tint;
	}

	private readonly IMaterialObject _material;
	private readonly uint _vertexCount;
	private readonly float _radius;
	private readonly Vector3 _tint;

	/// <summary>Положение, под которое залит кбуфер. Прокрутка его меняет (см.
	/// ProbeGiBakeSession.LayoutGeneration), и без сверки шарики остались бы стоять там, откуда
	/// объём уже уехал.</summary>
	private int _layoutGeneration = -1;

	/// <param name="tint">Цветовая метка объёма (аддитивная подкраска шариков): каскады получают
	/// свой цвет, чтобы отличаться от базовых проб - их шарики мельче и стоят среди базовых.</param>
	public ProbeDebugOverlay(DiligentGraphicsApi dilApi, IGraphicsApi api,
		IBatchRenderer batchRenderer, ProbeGiBakeSession session, ProbeGiTextures textures,
		TextureObjectFormat colorFormat, Vector3 tint = default)
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
		}));

		batchRenderer.BindViewConstants(_material);

		// Буфера углов кирпичей здесь больше нет: у плотной сетки координаты узла ЕСТЬ номер шарика,
		// и VS разворачивает его делением с остатком (см. ProbeDebugVS.hlsl).
		_material.SetTexture("_ProbeOffset", textures.Offset, HandleAccess.Vertex);
		_material.SetTexture("_ProbeSh0", textures.Sh0, HandleAccess.Vertex);
		_material.SetTexture("_ProbeSh1", textures.Sh1, HandleAccess.Vertex);

		// Радиус шарика - доля минимального шага сетки: на плотной сетке шарики мельче и не
		// сливаются в кашу, на редкой - крупнее и видны издалека.
		_radius = textures.MinCellSize * 0.12f;
		_tint = tint;
		Refresh(session);
	}

	/// <summary>Догоняет положение объёма: угол сетки в мире и тороидальное смещение. Зовётся по
	/// номеру раскладки, то есть фактически только после прокрутки - без этого шарики остались бы
	/// стоять там, откуда объём уехал.
	///
	/// Заливки буфера здесь больше нет вовсе: раньше на каждую прокрутку сюда ехали углы всех
	/// кирпичей, теперь всё состояние - пять векторов кбуфера.</summary>
	public void Refresh(ProbeGiBakeSession session)
	{
		if (_layoutGeneration == session.LayoutGeneration)
		{
			return;
		}

		_layoutGeneration = session.LayoutGeneration;

		var constants = new ProbeDebugParams
		{
			GridOriginRadius = new Vector4(session.Origin, _radius),
			GridCellCount = new Vector4(session.Cell, session.ProbeCount),
			GridCounts = new Vector4(session.CountX, session.CountY, session.CountZ, 0f),
			Tint = new Vector4(_tint, 0f),
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
	}
}
