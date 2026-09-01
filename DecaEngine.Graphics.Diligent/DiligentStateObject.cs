using System;
using System.Linq;
using DecaEngine.Core;
using DecaEngine.Graphics.Core;
using Diligent;
using ValueType = Diligent.ValueType;

namespace DecaEngine.Graphics.Diligent;

/// <summary>
/// Diligent-реализация Graphics Pipeline State. Инициализируется backend-независимым
/// описанием <see cref="GraphicsStateInfo"/> (топология примитивов, растеризация,
/// depth/stencil, input layout, форматы render target'ов), которое конвертируется
/// в нативную <see cref="GraphicsPipelineStateCreateInfo"/> Diligent'а внутри этого класса.
/// Взаимодействие с другими Diligent-объектами (например, <see cref="DiligentMaterial"/>)
/// происходит через приведение типов <c>IStateObject</c> -&gt; <see cref="DiligentGraphicsStateObject"/>.
/// </summary>
internal sealed class DiligentGraphicsStateObject : IStateObject
{
	public string Name { get; }
	public PipelineStateType StateType => PipelineStateType.Graphics;

	/// <summary>Нативное Diligent-описание PSO, собранное из <see cref="GraphicsStateInfo"/>.</summary>
	internal GraphicsPipelineStateCreateInfo CreateInfo { get; }

	public DiligentGraphicsStateObject(string name, GraphicsStateInfo info)
	{
		Name = name ?? throw new ArgumentNullException(nameof(name));
		CreateInfo = ToNativeCreateInfo(name, info);
	}

	/// <summary>
	/// Прямая инициализация нативным Diligent-описанием, минуя абстракцию. Используется
	/// только внутри backend'а (например, менеджерами, которым нужен полный контроль над PSO).
	/// </summary>
	internal DiligentGraphicsStateObject(string name, GraphicsPipelineStateCreateInfo createInfo)
	{
		Name = name ?? throw new ArgumentNullException(nameof(name));
		CreateInfo = createInfo;
	}

	private static GraphicsPipelineStateCreateInfo ToNativeCreateInfo(string name, GraphicsStateInfo info)
	{
		var rtFormats = info.RenderTargetFormats ?? Array.Empty<TextureObjectFormat>();

		return new GraphicsPipelineStateCreateInfo
		{
			PSODesc = new PipelineStateDesc
			{
				Name = name,
				PipelineType = PipelineType.Graphics,
				ResourceLayout = new PipelineResourceLayoutDesc
				{
					DefaultVariableType = ShaderResourceVariableType.Mutable
				}
			},
			GraphicsPipeline = new GraphicsPipelineDesc
			{
				NumRenderTargets = (byte)rtFormats.Length,
				RTVFormats = rtFormats.Select(DiligentResourceFormats.ToNativeFormat).ToArray(),
				DSVFormat = DiligentResourceFormats.ToNativeFormat(info.DepthStencilFormat),
				PrimitiveTopology = ToNativeTopology(info.PrimitiveTopology),
				RasterizerDesc = ToNativeRasterizerDesc(info.RasterizerState),
				DepthStencilDesc = ToNativeDepthStencilDesc(info.DepthStencilState),
				InputLayout = ToNativeInputLayout(info.InputLayout),
				SmplDesc = new SampleDesc { Count = (byte)Math.Max(1, info.SampleCount), Quality = 0 },
			}
		};
	}

	private static PrimitiveTopology ToNativeTopology(PrimitiveTopologyType topology) => topology switch
	{
		PrimitiveTopologyType.TriangleList => PrimitiveTopology.TriangleList,
		PrimitiveTopologyType.TriangleStrip => PrimitiveTopology.TriangleStrip,
		PrimitiveTopologyType.PointList => PrimitiveTopology.PointList,
		PrimitiveTopologyType.LineList => PrimitiveTopology.LineList,
		PrimitiveTopologyType.LineStrip => PrimitiveTopology.LineStrip,
		_ => PrimitiveTopology.TriangleList
	};

	private static CullMode ToNativeCullMode(CullModeType cullMode) => cullMode switch
	{
		CullModeType.None => CullMode.None,
		CullModeType.Front => CullMode.Front,
		CullModeType.Back => CullMode.Back,
		_ => CullMode.None
	};

	private static FillMode ToNativeFillMode(FillModeType fillMode) => fillMode switch
	{
		FillModeType.Wireframe => FillMode.Wireframe,
		_ => FillMode.Solid
	};

	private static ComparisonFunction ToNativeComparisonFunction(ComparisonFunctionType func) => func switch
	{
		ComparisonFunctionType.Never => ComparisonFunction.Never,
		ComparisonFunctionType.Less => ComparisonFunction.Less,
		ComparisonFunctionType.Equal => ComparisonFunction.Equal,
		ComparisonFunctionType.LessEqual => ComparisonFunction.LessEqual,
		ComparisonFunctionType.Greater => ComparisonFunction.Greater,
		ComparisonFunctionType.NotEqual => ComparisonFunction.NotEqual,
		ComparisonFunctionType.GreaterEqual => ComparisonFunction.GreaterEqual,
		ComparisonFunctionType.Always => ComparisonFunction.Always,
		_ => ComparisonFunction.Unknown
	};

	private static ValueType ToNativeValueType(InputElementValueType valueType) => valueType switch
	{
		InputElementValueType.Float32 => ValueType.Float32,
		InputElementValueType.Int32 => ValueType.Int32,
		InputElementValueType.UInt32 => ValueType.UInt32,
		_ => ValueType.Float32
	};

	private static InputElementFrequency ToNativeFrequency(InputElementFrequencyType frequency) => frequency switch
	{
		InputElementFrequencyType.PerInstance => InputElementFrequency.PerInstance,
		_ => InputElementFrequency.PerVertex
	};

	private static RasterizerStateDesc ToNativeRasterizerDesc(RasterizerStateInfo info) => new()
	{
		CullMode = ToNativeCullMode(info.CullMode),
		FillMode = ToNativeFillMode(info.FillMode),
		DepthBias = info.DepthBias,
		SlopeScaledDepthBias = info.SlopeScaledDepthBias,
		DepthClipEnable = !info.DepthClipDisable
	};

	private static DepthStencilStateDesc ToNativeDepthStencilDesc(DepthStencilStateInfo info) => new()
	{
		DepthEnable = info.DepthEnable,
		DepthWriteEnable = info.DepthWriteEnable,
		DepthFunc = ToNativeComparisonFunction(info.DepthFunc)
	};

	private static InputLayoutDesc ToNativeInputLayout(InputLayoutElementInfo[] elements)
	{
		if (elements == null || elements.Length == 0)
		{
			return new InputLayoutDesc();
		}

		return new InputLayoutDesc
		{
			LayoutElements = elements.Select(e => new LayoutElement
			{
				InputIndex = e.InputIndex,
				BufferSlot = e.BufferSlot,
				NumComponents = e.NumComponents,
				ValueType = ToNativeValueType(e.ValueType),
				IsNormalized = e.IsNormalized,
				Frequency = ToNativeFrequency(e.Frequency)
			}).ToArray()
		};
	}

	public void Release()
	{
	}
}

/// <summary>
/// Diligent-реализация Compute Pipeline State. Инициализируется backend-независимым
/// описанием <see cref="ComputeStateInfo"/>, конвертируемым в нативное
/// <see cref="ComputePipelineStateCreateInfo"/> внутри этого класса.
/// </summary>
internal sealed class DiligentComputeStateObject : IStateObject
{
	public string Name { get; }
	public PipelineStateType StateType => PipelineStateType.Compute;
	internal ComputePipelineStateCreateInfo CreateInfo { get; }

	public DiligentComputeStateObject(string name, ComputeStateInfo info)
	{
		Name = name ?? throw new ArgumentNullException(nameof(name));
		CreateInfo = new ComputePipelineStateCreateInfo
		{
			PSODesc = new PipelineStateDesc
			{
				Name = name,
				PipelineType = PipelineType.Compute,
				ResourceLayout = new PipelineResourceLayoutDesc
				{
					DefaultVariableType = ShaderResourceVariableType.Mutable
				}
			}
		};
	}

	/// <summary>
	/// Прямая инициализация нативным Diligent-описанием, минуя абстракцию. Используется
	/// только внутри backend'а.
	/// </summary>
	internal DiligentComputeStateObject(string name, ComputePipelineStateCreateInfo createInfo)
	{
		Name = name ?? throw new ArgumentNullException(nameof(name));
		CreateInfo = createInfo;
	}

	public void Release()
	{
	}
}
