using System;
using System.Linq;
using DecaEngine.Core;
using Diligent;
using ValueType = Diligent.ValueType;

namespace DecaEngine.Graphics.Diligent;

/// <summary>Diligent implementation of a graphics PSO built from backend-independent <see cref="GraphicsStateInfo"/>.</summary>
internal sealed class DiligentGraphicsStateObject : IStateObject
{
	public string Name { get; }
	public PipelineStateType StateType => PipelineStateType.Graphics;

	internal GraphicsPipelineStateCreateInfo CreateInfo { get; }

	public DiligentGraphicsStateObject(string name, GraphicsStateInfo info)
	{
		Name = name ?? throw new ArgumentNullException(nameof(name));
		CreateInfo = ToNativeCreateInfo(name, info);
	}

	/// <summary>Backend-internal: initialize directly from a native Diligent description.</summary>
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
				BlendDesc = ToNativeBlendDesc(info.BlendState),
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

	private static BlendFactor ToNativeBlendFactor(BlendFactorType factor) => factor switch
	{
		BlendFactorType.Zero => BlendFactor.Zero,
		BlendFactorType.One => BlendFactor.One,
		BlendFactorType.SrcColor => BlendFactor.SrcColor,
		BlendFactorType.InvSrcColor => BlendFactor.InvSrcColor,
		BlendFactorType.SrcAlpha => BlendFactor.SrcAlpha,
		BlendFactorType.InvSrcAlpha => BlendFactor.InvSrcAlpha,
		BlendFactorType.DestColor => BlendFactor.DestColor,
		BlendFactorType.InvDestColor => BlendFactor.InvDestColor,
		BlendFactorType.DestAlpha => BlendFactor.DestAlpha,
		BlendFactorType.InvDestAlpha => BlendFactor.InvDestAlpha,
		_ => BlendFactor.One
	};

	private static BlendOperation ToNativeBlendOperation(BlendOperationType op) => op switch
	{
		BlendOperationType.Add => BlendOperation.Add,
		BlendOperationType.Subtract => BlendOperation.Subtract,
		BlendOperationType.RevSubtract => BlendOperation.RevSubtract,
		BlendOperationType.Min => BlendOperation.Min,
		BlendOperationType.Max => BlendOperation.Max,
		_ => BlendOperation.Add
	};

	// Disabled blending maps to one default RenderTargetBlendDesc, byte-identical to pre-field
	// PSOs so existing disk caches stay valid. Enabled blending affects slot 0 only; slots 1+
	// get ColorMask.None so transparent draws under the reflection MRT don't corrupt G-buffer normals.
	private static BlendStateDesc ToNativeBlendDesc(BlendStateInfo info)
	{
		var rt0 = new RenderTargetBlendDesc
		{
			BlendEnable = info.BlendEnable,
			SrcBlend = ToNativeBlendFactor(info.SrcBlend),
			DestBlend = ToNativeBlendFactor(info.DestBlend),
			BlendOp = ToNativeBlendOperation(info.BlendOp),
			SrcBlendAlpha = ToNativeBlendFactor(info.SrcBlendAlpha),
			DestBlendAlpha = ToNativeBlendFactor(info.DestBlendAlpha),
			BlendOpAlpha = ToNativeBlendOperation(info.BlendOpAlpha),
			RenderTargetWriteMask = ColorMask.All
		};

		if (!info.BlendEnable)
		{
			return new BlendStateDesc { RenderTargets = [rt0] };
		}

		var targets = new RenderTargetBlendDesc[8];
		targets[0] = rt0;
		for (int i = 1; i < targets.Length; i++)
		{
			targets[i] = new RenderTargetBlendDesc { RenderTargetWriteMask = ColorMask.None };
		}

		return new BlendStateDesc
		{
			IndependentBlendEnable = true,
			RenderTargets = targets
		};
	}

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

/// <summary>Diligent implementation of a compute PSO built from backend-independent <see cref="ComputeStateInfo"/>.</summary>
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

	/// <summary>Backend-internal: initialize directly from a native Diligent description.</summary>
	internal DiligentComputeStateObject(string name, ComputePipelineStateCreateInfo createInfo)
	{
		Name = name ?? throw new ArgumentNullException(nameof(name));
		CreateInfo = createInfo;
	}

	public void Release()
	{
	}
}
