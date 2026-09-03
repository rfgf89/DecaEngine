using System.Numerics;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
using DecaEngine.Core;

namespace DecaEngine.Graphics;

public class GraphicsPipeline : IGraphicsPipeline
{
	private readonly IGraphicsApi _api;
	private readonly IBatchRenderer _batchRenderer;
	private readonly IRenderGraph _renderGraph;
	private readonly IGpuTexture? _colorTarget;
	private readonly IGpuTexture? _depthTarget;
	private readonly Vector4 _clearColor;

	private Ref<Vector2> _viewPortRef;

	// Outlives the graph: ShadowPass is recreated on every SignalGraph.
	private readonly ShadowCascadeSchedule _cascadeSchedule = new();

	/// <summary>Shadow cascade redraw schedule; written by the culling system, read by ShadowPass.</summary>
	public ShadowCascadeSchedule CascadeSchedule => _cascadeSchedule;

	public GraphicsPipeline(IGraphicsApi api, IBatchRenderer batchRenderer, string? debugName = null)
		: this(api, batchRenderer, null, null, new Vector4(0.1f, 0.1f, 0.1f, 1f), debugName)
	{
	}

	/// <summary>Off-screen overload: renders into its own targets instead of the swap chain.</summary>
	public GraphicsPipeline(IGraphicsApi api, IBatchRenderer batchRenderer, IGpuTexture? colorTarget, IGpuTexture? depthTarget, Vector4 clearColor, string? debugName = null)
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

		// The registry holds a weak reference and does not extend the pipeline's lifetime.
		GraphicsPipelineRegistry.Register(this,
			debugName ?? (_colorTarget is null ? "Main Scene (swap chain)" : "Offscreen (GraphicsPipeline)"));
	}

	/// <summary>Removes the pipeline from <see cref="GraphicsPipelineRegistry"/> and frees the graph.</summary>
	public void Release()
	{
		GraphicsPipelineRegistry.Unregister(this);
		_renderGraph.Release();
	}

	public void OnViewportChange()
	{
		_viewPortRef.Set(_api.WindowHandle.Size);
	}

	/// <summary>Updates the viewport for off-screen consumers; callers must resize the targets first.
	/// Invalidates the graph, whose frozen commands would otherwise touch disposed textures.</summary>
	public void SetOffscreenViewportSize(Vector2 size)
	{
		var change = _viewPortRef.Value != size;
		_viewPortRef.Set(size);
		if (change)
		{
			_renderGraph.Invalidate();
		}
	}

	/// <summary>Forces the graph to re-record frozen commands; mandatory after the batch set changes.
	/// There is no frame-in-flight fence, so callers must wait for GPU idle before calling this.</summary>
	public void InvalidateGraph()
	{
		_renderGraph.Invalidate();
	}

	public void Initialize()
	{
	}

	public void SignalGraph(DirectionalLightCascadeData renderScene, RenderCamerasData renderViews)
	{
		// ResetPasses, not Release: passes are rebuilt but the graph's native resources are reused.
		_renderGraph.ResetPasses();

		_renderGraph.AddPass(new ShadowPass(_batchRenderer, renderScene, _cascadeSchedule));
		_renderGraph.AddPass(new ForwardPass(_batchRenderer, renderViews, _viewPortRef, _colorTarget, _depthTarget, _clearColor));
	}

	public void Execute()
	{
		_renderGraph.Execute();
	}

	public RenderGraphDebugSnapshot DebugSnapshot => _renderGraph.DebugSnapshot;
	public RenderGraphDebugHistory DebugHistory => _renderGraph.DebugHistory;
}