using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Graphics.Diligent.RenderGraph;
using Diligent;

namespace DecaEngine.Graphics.Diligent;

public class DiligentRenderGraphContext : IRenderGraphContext
{
	public IGraphicsApi Api => _diligentGraphicsApi;
	public ICommandBuffer cmd => _commandBuffer;

	public IDeviceContext Context { get; private set; }

	private DiligentRenderGraphBuilder _builder;
	private DiligentGraphicsApi _diligentGraphicsApi;
	private DiligentCommandBuffer _commandBuffer;

	public void BeginRecording(DiligentGraphicsApi api, IDeviceContext context, DiligentRenderGraphBuilder builder)
	{
		_diligentGraphicsApi = api;
		Context = context;
		_builder = builder;

		if (_commandBuffer == null)
		{
			_commandBuffer = new DiligentCommandBuffer(context);
		}
		else
		{
			_commandBuffer.Retarget(context);
		}

		_commandBuffer.BeginRecording();
	}

	public void Freeze()
	{
		_commandBuffer.Freeze();
	}

	public void Execute(IDeviceContext context)
	{
		_commandBuffer.Retarget(context);
		_commandBuffer.Execute();
	}

	public IGpuTexture GetTexture(TextureResource textureResource)
	{
		var nativeTexture = _builder.GetTexture(textureResource.Id);
		var textureInfo = _builder.GetTextureInfo(textureResource.Id);

		// Wrapper creates its own lazy views, independent of the views the graph already made.
		return new DiligentGpuTexture(textureInfo.name, textureInfo, nativeTexture);
	}

	public IBufferHandle GetBuffer(BufferResource bufferResource)
	{
		var nativeBuffer = _builder.GetBuffer(bufferResource.Id);
		var pinnedInfo = _builder.GetBufferInfo(bufferResource.Id);

		// Wraps the graph-owned native buffer without allocating a new one.
		return new DiligentBufferHandle(nativeBuffer, pinnedInfo);
	}

#if DEBUG
	/// <summary>Debug-only pass-through to the underlying command buffer's recorded stats.</summary>
	public (int drawCalls, int dispatchCalls, int transitionCount, long triangles) GetDebugStats()
	{
		return _commandBuffer?.GetDebugStats() ?? (0, 0, 0, 0);
	}
#endif
}