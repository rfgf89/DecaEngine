using System.Numerics;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Core;
using DecaEngine.Graphics.Diligent;

namespace DecaEngine.Core;

public class GraphicsPipeline : IGraphicsPipeline
{
	private readonly IGraphicsApi _api;
	private readonly IBatchRenderer _batchRenderer;
	private readonly IRenderGraph _renderGraph;
	private readonly IGpuTexture? _colorTarget;
	private readonly IGpuTexture? _depthTarget;
	private readonly Vector4 _clearColor;

	private Ref<Vector2> _viewPortRef;

	public GraphicsPipeline(IGraphicsApi api, IBatchRenderer batchRenderer, string? debugName = null)
		: this(api, batchRenderer, null, null, new Vector4(0.1f, 0.1f, 0.1f, 1f), debugName)
	{
	}

	/// <summary>
	/// Overload for a self-contained, off-screen render-graph instance (e.g.
	/// <see cref="DecaEngine.Editor.ModelPreviewViewport"/>'s isolated .gltf/.glb preview render
	/// graph used by the Inspector) that writes into its own persistent <paramref name="colorTarget"/>
	/// / <paramref name="depthTarget"/> instead of the swap chain, and doesn't need the swap-chain
	/// resize hookup or the render-graph-debugger demo passes.
	/// </summary>
	/// <param name="debugName">Имя конвейера в окне отладки рендер-графа (см.
	/// <see cref="GraphicsPipelineRegistry"/>). Null - имя по умолчанию, по режиму конвейера.</param>
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

		// Конвейер сам встаёт в реестр - окно отладки рендер-графа набирает свой список именно так
		// (см. GraphicsPipelineRegistry). Реестр держит слабую ссылку и жизнь конвейера не продлевает.
		GraphicsPipelineRegistry.Register(this,
			debugName ?? (_colorTarget is null ? "Main Scene (swap chain)" : "Offscreen (GraphicsPipeline)"));
	}

	/// <summary>Убирает конвейер из <see cref="GraphicsPipelineRegistry"/>, чтобы он пропал из окна
	/// отладки сразу, как только перестал использоваться. Не обязателен - реестр держит слабую
	/// ссылку, - но избавляет UI от мёртвой строки до ближайшей сборки мусора.</summary>
	public void Release()
	{
		GraphicsPipelineRegistry.Unregister(this);
		_renderGraph.Release();
	}

	public void OnViewportChange()
	{
		_viewPortRef.Set(_api.WindowHandle.Size);
	}

	/// <summary>
	/// Updates the render viewport for off-screen consumers (<see cref="_colorTarget"/> not null),
	/// which don't get <see cref="OnViewportChange"/> from <see cref="IWindowHandle.OnWindowResize"/>
	/// - callers must resize <see cref="_colorTarget"/>/<see cref="_depthTarget"/> themselves and then
	/// call this so <see cref="ForwardPass"/>'s viewport (<see cref="_viewPortRef"/>) matches.
	///
	/// Also invalidates the render graph (<see cref="IRenderGraph.Invalidate"/>): resizing disposes
	/// and recreates the native textures behind <see cref="_colorTarget"/>/<see cref="_depthTarget"/>,
	/// but the graph's compiled/frozen commands still reference the old native objects until it
	/// recompiles - without this, replaying those frozen commands touches disposed GPU resources.
	/// </summary>
	public void SetOffscreenViewportSize(Vector2 size)
	{
		var change = _viewPortRef.Value != size;
		_viewPortRef.Set(size);
		if (change)
		{
			_renderGraph.Invalidate();
		}
	}

	/// <summary>
	/// Forces the render graph to re-record its frozen command buffers on the next
	/// <see cref="Execute"/>. Passes record their draw/dispatch commands only during
	/// <see cref="IRenderGraph.Compile"/> and are then merely replayed (see
	/// <see cref="IRenderGraph.Invalidate"/>) - so after the batch set changes (new meshes/batches
	/// registered in the <see cref="IBatchRenderer"/>, e.g. a different model or sub-mesh loaded
	/// into an off-screen preview) the frozen commands silently keep drawing the OLD batch set;
	/// callers that mutate batches must invalidate, or the new geometry never appears.
	///
	/// Recompiling disposes every native resource pinned by a pass (e.g. <see cref="ShadowPass"/>'s
	/// shadow maps - see <c>RenderGraphNode.Clean</c>) and immediately recreates them. Just like
	/// <see cref="SetOffscreenViewportSize"/>'s render-target resize, doing that while the GPU may
	/// still be reading/writing those resources from a prior frame (this engine has no
	/// frame-in-flight fence) races the GPU and can crash the driver - so callers must flush and wait
	/// for the GPU to go idle (on their backend-specific device context) themselves before calling
	/// this, exactly as <see cref="DecaEngine.Editor.ModelPreviewViewport"/>'s resize path already
	/// does. This class only sees the backend through <see cref="IGraphicsApi"/>, so it cannot do
	/// that synchronization itself.
	/// </summary>
	public void InvalidateGraph()
	{
		_renderGraph.Invalidate();
	}

	public void Initialize()
	{
	}

	public void SignalGraph(DirectionalLightCascadeData renderScene, RenderCamerasData renderViews)
	{
		// ResetPasses, а не Release: пассы пересоздаются, а нативные ресурсы графа переиспользуются
		// (см. IRenderGraph.ResetPasses).
		_renderGraph.ResetPasses();

		/*if (_colorTarget is null)
		{
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
		}*/

		_renderGraph.AddPass(new ShadowPass(_batchRenderer, renderScene));
		_renderGraph.AddPass(new ForwardPass(_batchRenderer, renderViews, _viewPortRef, _colorTarget, _depthTarget, _clearColor));
	}

	public void Execute()
	{
		_renderGraph.Execute();
	}

	public RenderGraphDebugSnapshot DebugSnapshot => _renderGraph.DebugSnapshot;
	public RenderGraphDebugHistory DebugHistory => _renderGraph.DebugHistory;
}