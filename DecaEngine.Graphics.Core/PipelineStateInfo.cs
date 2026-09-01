
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

	/// <summary>Наклонозависимый депт-байас (D3D SlopeScaledDepthBias): смещение растёт с
	/// крутизной полигона к камере/свету. Нужен shadow map-у с CullMode None - у поверхностей
	/// под скользящим углом к свету константного байаса не хватает (акне полосами).</summary>
	public float SlopeScaledDepthBias;

	/// <summary>Выключить отсечение по БЛИЖНЕЙ/дальней плоскости (D3D DepthClipEnable = false).
	/// Флаг инвертирован относительно нативного намеренно: значение по умолчанию у структуры - ноль,
	/// то есть отсечение включено, как и было.
	///
	/// Нужен shadow map-е. Кастер, оказавшийся МЕЖДУ солнцем и объёмом каскада дальше, чем оттянут
	/// глаз, отсекается ближней плоскостью и не отбрасывает тень вовсе - в тени появляются дыры.
	/// Оттяжка глаза считается от РАДИУСА КАСКАДА (см. UpdateCascades / BuildLightData), а расстояние
	/// до реальных окклюдеров - свойство СЦЕНЫ, а не каскада, поэтому первому (самому мелкому)
	/// каскаду её всегда не хватает первым. Растянуть под сцену диапазон глубины нельзя - у мелкого
	/// каскада это съело бы точность и константный байас, а вот отсечение выключить можно: без него
	/// глубина перед ближней плоскостью просто кламмится к ней («pancaking»), и кастер остаётся
	/// окклюдером - что для карты теней и требуется, он всё равно заслоняет всё, что за ним.</summary>
	public bool DepthClipDisable;
}

public struct DepthStencilStateInfo()
{
	public bool DepthEnable = false;
	public ComparisonFunctionType DepthFunc = default;

	/// <summary>Пишет ли дроу глубину. По умолчанию ДА - это поведение по умолчанию и у D3D12/Vulkan,
	/// и у всего, что было в движке до появления этого поля; конструктор без параметров существует
	/// ровно затем, чтобы инициализаторы объекта (<c>new DepthStencilStateInfo { DepthEnable = true }</c>)
	/// не получали молча false и не оставляли геометрию без глубины.
	///
	/// Выключается там, где дроу обязан ЧИТАТЬ глубину сцены, но не менять её: дебаг-линии (см.
	/// DebugLineOverlay) рисуются после геометрии, а глубину дальше читают векторы движения, SSR и
	/// туман - каркас скелета в депт-буфере испортил бы всем троим кадр.</summary>
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
