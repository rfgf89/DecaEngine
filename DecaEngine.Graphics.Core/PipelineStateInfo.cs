
namespace DecaEngine.Graphics;

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

	/// <summary>D3D SlopeScaledDepthBias; shadow maps with CullMode None need it, constant
	/// bias alone leaves striped acne on surfaces at grazing angles to the light.</summary>
	public float SlopeScaledDepthBias;

	/// <summary>Disables depth clip (D3D DepthClipEnable = false). Inverted vs native on purpose
	/// so zero-init keeps clipping on. Shadow maps need it: casters between the sun and the
	/// cascade volume get pancaked to the near plane instead of clipped away.</summary>
	public bool DepthClipDisable;
}

public enum BlendFactorType
{
	Zero = 0,
	One,
	SrcColor,
	InvSrcColor,
	SrcAlpha,
	InvSrcAlpha,
	DestColor,
	InvDestColor,
	DestAlpha,
	InvDestAlpha,
}

public enum BlendOperationType
{
	Add = 0,
	Subtract,
	RevSubtract,
	Min,
	Max,
}

/// <summary>PSO blend state. When enabled it applies to render target 0 only; the backend sets
/// ColorMask.None on the other MRT slots so blended draws cannot corrupt the SSR G-buffer.
/// Per-target blending would require extending this - see DiligentStateObject.ToNativeBlendDesc.
/// Parameterless ctor sets identity One/Zero + Add factors; zero-init is also safe.</summary>
public struct BlendStateInfo()
{
	public bool BlendEnable = false;
	public BlendFactorType SrcBlend = BlendFactorType.One;
	public BlendFactorType DestBlend = BlendFactorType.Zero;
	public BlendOperationType BlendOp = BlendOperationType.Add;
	public BlendFactorType SrcBlendAlpha = BlendFactorType.One;
	public BlendFactorType DestBlendAlpha = BlendFactorType.Zero;
	public BlendOperationType BlendOpAlpha = BlendOperationType.Add;

	/// <summary>No blending - the default of every existing PSO.</summary>
	public static BlendStateInfo Opaque => new();

	/// <summary>Straight-alpha transparency: src*a + dst*(1-a). Alpha accumulates coverage so
	/// alpha-composited targets (_SceneColor.a in MATERIAL_TRANSMISSION) stay meaningful.</summary>
	public static BlendStateInfo AlphaBlend => new()
	{
		BlendEnable = true,
		SrcBlend = BlendFactorType.SrcAlpha,
		DestBlend = BlendFactorType.InvSrcAlpha,
		SrcBlendAlpha = BlendFactorType.One,
		DestBlendAlpha = BlendFactorType.InvSrcAlpha,
	};

	/// <summary>Premultiplied alpha: src + dst*(1-a), for RGB already multiplied by alpha in the shader.</summary>
	public static BlendStateInfo Premultiplied => new()
	{
		BlendEnable = true,
		SrcBlend = BlendFactorType.One,
		DestBlend = BlendFactorType.InvSrcAlpha,
		SrcBlendAlpha = BlendFactorType.One,
		DestBlendAlpha = BlendFactorType.InvSrcAlpha,
	};

	/// <summary>Additive: src + dst (glow, sparks). Leaves target alpha untouched.</summary>
	public static BlendStateInfo Additive => new()
	{
		BlendEnable = true,
		SrcBlend = BlendFactorType.One,
		DestBlend = BlendFactorType.One,
		SrcBlendAlpha = BlendFactorType.Zero,
		DestBlendAlpha = BlendFactorType.One,
	};
}

public struct DepthStencilStateInfo()
{
	public bool DepthEnable = false;
	public ComparisonFunctionType DepthFunc = default;

	/// <summary>Depth writes; defaults to true (D3D12/Vulkan default). Disable for draws that must
	/// read scene depth but not change it (debug lines) - motion vectors, SSR and fog read
	/// depth afterwards.</summary>
	public bool DepthWriteEnable = true;
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

	/// <summary>Blend state (one for all targets). Participates by identity in the shared-PSO
	/// cache key - see DiligentMaterial.RebuildPipelineIfNeeded.</summary>
	public BlendStateInfo BlendState;

	/// <summary>MSAA sample count; must match the bound targets' sampleCount. 1 = no MSAA.</summary>
	public uint SampleCount;

	public GraphicsStateInfo()
	{
		SampleCount = 1;
		PrimitiveTopology = PrimitiveTopologyType.TriangleList;
		BlendState = new BlendStateInfo();
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
