using System.Numerics;
using DecaEngine.Core;
using Diligent;
using ValueType = Diligent.ValueType;

namespace DecaEngine.Graphics.Diligent;

public class DiligentRenderGraphContext : IRenderGraphContext
{
	public IGraphicsPipeline Pipeline => _diligentGraphicsPipeline;

	public IDeviceContext Context { get; private set; }

	private DiligentRenderGraphBuilder _builder;
	private DiligentGraphicsPipeline _diligentGraphicsPipeline;
	private int _contextIdx;

	public void Initialize(int contextIdx, DiligentGraphicsPipeline pipeline, IDeviceContext context, DiligentRenderGraphBuilder builder)
	{
		_contextIdx = contextIdx;
		_diligentGraphicsPipeline = pipeline;
		Context = context;
		_builder = builder;
	}

	public void SetRenderTargets(TextureResource textureResource)
	{
		var resource = _builder.renderContainer.BindWrittenTargets[textureResource.bindId];

		Context.SetRenderTargets([resource], null, _contextIdx == 0 ? ResourceStateTransitionMode.Transition : ResourceStateTransitionMode.Verify);
	}

	public void ClearRenderTarget(TextureResource textureResource, float r, float g, float b, float a)
	{
		var resource = _builder.renderContainer.BindWrittenTargets[textureResource.bindId];

		Context.ClearRenderTarget(resource, new Vector4(r, g, b, a), _contextIdx == 0 ? ResourceStateTransitionMode.Transition : ResourceStateTransitionMode.Verify);
	}



	public void SetPipelineState(PsoResource psoResource)
	{
		//var resource = _builder.PsoTargets[0];
		//Context.SetPipelineState(resource.value);
	}

	public void DrawIndexed(uint indicesStart, uint indicesCount, uint vertexStart, uint instanceStart, uint instanceCount, IndexType indexType)
	{
		var drawAttr = new DrawIndexedAttribs()
		{
			IndexType = (ValueType)indexType,
			BaseVertex = vertexStart,
			FirstIndexLocation = indicesStart,
			FirstInstanceLocation = instanceStart,
			Flags = DrawFlags.VerifyAll,
			NumIndices = indicesCount,
			NumInstances = instanceCount
		};

		Context.DrawIndexed(drawAttr);
	}
}