using System.Numerics;
using DecaEngine.Graphics.Core;
using DecaEngine.Graphics.Diligent;

namespace DecaEngine.Core;

/// <summary>
/// <see cref="IGraphicsPipeline"/> without <see cref="ShadowPass"/> - for off-screen consumers
/// (see <see cref="DecaEngine.Editor.ModelViewportEnvironment"/>) that only ever draw unlit
/// geometry through <see cref="SimpleCullingAndRenderSystem"/> and never need shadow-cascade
/// culling/rendering. The <see cref="DirectionalLightCascadeData"/> passed to
/// <see cref="SignalGraph"/> is ignored - callers no longer need to feed it an empty
/// <see cref="DirectionalLightCascadeData"/> just to keep <see cref="ShadowPass"/> a no-op.
/// </summary>
public class GraphicsPipelineSimple : IGraphicsPipeline
{
	private readonly IGraphicsApi _api;
	private readonly IBatchRenderer _batchRenderer;
	private readonly IRenderGraph _renderGraph;
	private readonly IGpuTexture? _colorTarget;
	private readonly IGpuTexture? _depthTarget;
	private readonly Vector4 _clearColor;

	private Ref<Vector2> _viewPortRef;

	public GraphicsPipelineSimple(IGraphicsApi api, IBatchRenderer batchRenderer)
		: this(api, batchRenderer, null, null, new Vector4(0.1f, 0.1f, 0.1f, 1f))
	{
	}

	public GraphicsPipelineSimple(IGraphicsApi api, IBatchRenderer batchRenderer, IGpuTexture? colorTarget, IGpuTexture? depthTarget, Vector4 clearColor)
	{
		_api = api;
		_batchRenderer = batchRenderer;
		_colorTarget = colorTarget;
		_depthTarget = depthTarget;
		_clearColor = clearColor;
		_renderGraph = _api.CreateRenderGraph();

		if (_colorTarget is not null)
		{
			_viewPortRef = new Ref<Vector2>(new Vector2(_colorTarget.Info.width, _colorTarget.Info.height));
		}
		else
		{
			_viewPortRef = new Ref<Vector2>(_api.WindowHandle.Size);
			_api.WindowHandle.OnWindowResize += OnViewportChange;
		}
	}

	public void OnViewportChange()
	{
		_viewPortRef.Set(_api.WindowHandle.Size);
	}

	/// <summary>See <see cref="GraphicsPipeline.SetOffscreenViewportSize"/>.</summary>
	public void SetOffscreenViewportSize(Vector2 size)
	{
		var change = _viewPortRef.Value != size;
		_viewPortRef.Set(size);
		if (change)
		{
			_renderGraph.Invalidate();
		}
	}

	/// <summary>See <see cref="GraphicsPipeline.InvalidateGraph"/>.</summary>
	public void InvalidateGraph()
	{
		_renderGraph.Invalidate();
	}

	public void Initialize()
	{
	}

	public void SignalGraph(DirectionalLightCascadeData renderScene, RenderCamerasData renderViews)
	{
		_renderGraph.Release();
		_renderGraph.AddPass(new ForwardPass(_batchRenderer, renderViews, _viewPortRef, _colorTarget, _depthTarget, _clearColor));
	}

	public void Execute()
	{
		_renderGraph.Execute();
	}

	public RenderGraphDebugSnapshot DebugSnapshot => _renderGraph.DebugSnapshot;
	public RenderGraphDebugHistory DebugHistory => _renderGraph.DebugHistory;
}
