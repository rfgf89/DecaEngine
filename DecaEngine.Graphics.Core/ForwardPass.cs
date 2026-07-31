using System.Numerics;
using DecaEngine.Graphics.Diligent;

namespace DecaEngine.Core;

/// <summary>
/// Render-graph pass that clears/binds the back buffer and culls + draws every camera view.
/// Runs after <see cref="ShadowPass"/> so shadow maps are already populated.
/// </summary>
public sealed class ForwardPass : RenderGraphPass<ForwardPass.PassData>
{
	public override string Name => "Forward Pass";

	private readonly IBatchRenderer _batchRenderer;
	private readonly RenderCamerasData _renderScene;
	private readonly Ref<Vector2> _viewPortRef;

	public struct PassData
	{
	}

	public ForwardPass(IBatchRenderer batchRenderer, RenderCamerasData renderScene, Ref<Vector2> viewPortRef)
	{
		_batchRenderer = batchRenderer;
		_renderScene = renderScene;
		_viewPortRef = viewPortRef;
	}

	public override PassData Setup(IRenderGraphBuilder builder)
	{
		return default;
	}

	public override void WriteCommands(in PassData value, in IRenderGraphContext context)
	{
		var cmd = context.cmd;

		// Must run every frame regardless of whether ShadowPass is enabled: this is what actually
		// computes _totalCommands and (re)allocates/clears the indirect-draw buffers. Without it,
		// ExecuteComputeCulling/ExecuteDrawBatching silently no-op (their early-out guard on
		// _totalCommands == 0 is never satisfied).
		_batchRenderer.CheckAndReallocateBuffers();
		_batchRenderer.ClearIndirectDrawBuffers(cmd);

		cmd.SetBackBufferTarget(context.Api);
		cmd.ClearBackBufferTarget(context.Api, new Vector4(0.1f, 0.1f, 0.1f, 1f));
		cmd.SetViewport(_viewPortRef);

		var views = _renderScene;
		if (!views.IsCreated)
		{
			return;
		}

		for (int i = 0; i < views.viewData.Capacity; i++)
		{
			_batchRenderer.SetupViewData(cmd, ref views.viewData.GetRef(i, false));
			_batchRenderer.SetupCullData(cmd, ref views.cullData.GetRef(i, false));
			_batchRenderer.SetupLightData(cmd, ref views.lightData.GetRef(i, false));

			var cullResult = _batchRenderer.ExecuteComputeCulling(cmd);
			_batchRenderer.ExecuteDrawBatching(cmd, cullResult);
		}
	}
}

