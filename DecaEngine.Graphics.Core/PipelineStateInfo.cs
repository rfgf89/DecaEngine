using DecaEngine.Graphics.Core;

namespace DecaEngine.Core;

public enum PrimitiveTopologyType
{
	Undefined = 0,
	TriangleList,
	TriangleStrip,
	PointList,
	LineList,
	LineStrip,
}

public enum CullModeType
{
	None = 0,
	Front,
	Back,
}

public enum FillModeType
{
	Solid = 0,
	Wireframe,
}

public enum ComparisonFunctionType
{
	Unknown = 0,
	Never,
	Less,
	Equal,
	LessEqual,
	Greater,
	NotEqual,
	GreaterEqual,
	Always,
}

public enum InputElementValueType
{
	Float32 = 0,
	Int32,
	UInt32,
}

public enum InputElementFrequencyType
{
	PerVertex = 0,
	PerInstance,
}

public struct InputLayoutElementInfo
{
	public uint InputIndex;
	public uint BufferSlot;
	public uint NumComponents;
	public InputElementValueType ValueType;
	public bool IsNormalized;
	public InputElementFrequencyType Frequency;
}

public struct RasterizerStateInfo
{
	public CullModeType CullMode;
	public FillModeType FillMode;
	public int DepthBias;

	/// <summary>Наклонозависимый депт-байас (D3D SlopeScaledDepthBias): смещение растёт с
	/// крутизной полигона к камере/свету. Нужен shadow map-у с CullMode None - у поверхностей
	/// под скользящим углом к свету константного байаса не хватает (акне полосами).</summary>
	public float SlopeScaledDepthBias;
}

public struct DepthStencilStateInfo
{
	public bool DepthEnable;
	public ComparisonFunctionType DepthFunc;
}

public struct GraphicsStateInfo
{
	public string Name;

	public TextureObjectFormat[] RenderTargetFormats;
	public TextureObjectFormat DepthStencilFormat;
	public InputLayoutElementInfo[] InputLayout;

	public PrimitiveTopologyType PrimitiveTopology;
	public RasterizerStateInfo RasterizerState;
	public DepthStencilStateInfo DepthStencilState;

	/// <summary>MSAA sample count PSO (обязан совпадать с sampleCount привязанных таргетов); 1 = без MSAA.</summary>
	public uint SampleCount;

	public GraphicsStateInfo()
	{
		SampleCount = 1;
		PrimitiveTopology = PrimitiveTopologyType.TriangleList;
		RasterizerState = new RasterizerStateInfo()
		{
			DepthBias = 0
		};
		DepthStencilState = new DepthStencilStateInfo()
		{
			DepthEnable = false,
		};
	}
}

public struct ComputeStateInfo
{
	public string Name;
}
