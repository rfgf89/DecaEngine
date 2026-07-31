using System.Numerics;
using DecaEngine.Graphics.Diligent;

namespace DecaEngine.Core;

public class GraphicsPipeline : IGraphicsPipeline
{
	private readonly IGraphicsApi _api;
	private readonly IBatchRenderer _batchRenderer;
	private readonly IRenderGraph _renderGraph;

	private Ref<Vector2> _viewPortRef;

	public GraphicsPipeline(IGraphicsApi api, IBatchRenderer batchRenderer)
	{
		_api = api;
		_batchRenderer = batchRenderer;
		_renderGraph = _api.CreateRenderGraph();
		_viewPortRef = new Ref<Vector2>(_api.WindowHandle.Size);

		_api.WindowHandle.OnWindowResize += OnViewportChange;
	}

	public void OnViewportChange()
	{
		_viewPortRef.Set(_api.WindowHandle.Size);
	}

	public void Initialize()
	{
	}

	public void SignalGraph(DirectionalLightCascadeData renderScene, RenderCamerasData renderViews)
	{
		_renderGraph.Release();

		// NOTE: each ClearRenderTargetPass must get a *unique* pinned-texture name. PinTexture
		// dedups by name, so reusing the same literal (as this used to do) silently collapses all
		// 4 instances into writes against a single shared texture instead of 4 independent ones.
		_renderGraph.AddPass(new ClearRenderTargetPass("ClearRenderTarget_0"));
		_renderGraph.AddPass(new ClearRenderTargetPass("ClearRenderTarget_1"));
		_renderGraph.AddPass(new ClearRenderTargetPass("ClearRenderTarget_2"));
		_renderGraph.AddPass(new ClearRenderTargetPass("ClearRenderTarget_3"));

		// Demo consumer: without this, ClearRenderTarget_0's texture is written once and never
		// read again, so it correctly (and expectedly) shows a single-pass lifetime in the render
		// graph debugger - that's not a deallocation bug, there's simply no reader. Declaring a
		// read here lets its lifetime genuinely span from this pass to the read below.
		_renderGraph.AddPass(new ReadRenderTargetPass("ReadClearRenderTarget_0", "ClearRenderTarget_0"));

		_renderGraph.AddPass(new ShadowPass(_batchRenderer, renderScene));
		_renderGraph.AddPass(new ForwardPass(_batchRenderer, renderViews, _viewPortRef));
	}

	public void Execute()
	{
		_renderGraph.Execute();
	}

	public RenderGraphDebugSnapshot DebugSnapshot => _renderGraph.DebugSnapshot;
	public RenderGraphDebugHistory DebugHistory => _renderGraph.DebugHistory;
}