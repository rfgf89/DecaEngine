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
	public int DepthBias;
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

	public GraphicsStateInfo()
	{
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
