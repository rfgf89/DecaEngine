using System.Numerics;
using DecaEngine.Graphics.Core;
using DecaEngine.Graphics.Diligent;
using Diligent;

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
	private readonly IGpuTexture? _colorTarget;
	private readonly IGpuTexture? _depthTarget;
	private readonly Vector4 _clearColor;

	public struct PassData
	{
	}

	public ForwardPass(IBatchRenderer batchRenderer, RenderCamerasData renderScene, Ref<Vector2> viewPortRef)
		: this(batchRenderer, renderScene, viewPortRef, null, null, new Vector4(0.1f, 0.1f, 0.1f, 1f))
	{
	}

	/// <summary>
	/// Overload used by off-screen consumers (see <see cref="DecaEngine.Editor.ModelPreviewViewport"/>)
	/// that need to draw into their own persistent color/depth targets instead of the swap chain -
	/// e.g. a separate, isolated render-graph instance rendering a .gltf/.glb preview for the
	/// Inspector, independent from the main Game View. When <paramref name="colorTarget"/> is null
	/// this behaves exactly like the swap-chain-writing constructor above.
	/// </summary>
	public ForwardPass(IBatchRenderer batchRenderer, RenderCamerasData renderScene, Ref<Vector2> viewPortRef,
		IGpuTexture? colorTarget, IGpuTexture? depthTarget, Vector4 clearColor)
	{
		_batchRenderer = batchRenderer;
		_renderScene = renderScene;
		_viewPortRef = viewPortRef;
		_colorTarget = colorTarget;
		_depthTarget = depthTarget;
		_clearColor = clearColor;
	}

	public override PassData Setup(IRenderGraphBuilder builder)
	{
		return default;
	}

	public override void WriteCommands(in PassData value, in IRenderGraphContext context)
	{
		var cmd = context.cmd;

		_batchRenderer.CheckAndReallocateBuffers();
		_batchRenderer.ClearIndirectDrawBuffers(cmd);

		if (_colorTarget is not null)
		{
			cmd.SetRenderTarget(_colorTarget, _depthTarget);
			cmd.ClearRenderTarget(_colorTarget, _clearColor);
			if (_depthTarget is not null)
			{
				cmd.ClearDepthStencil(_depthTarget, ClearDepthStencilFlags.Depth, 0.0f, 0);
			}
		}
		else
		{
			cmd.SetBackBufferTarget(context.Api);
			cmd.ClearBackBufferTarget(context.Api, _clearColor);
		}

		cmd.SetViewport(_viewPortRef);

		var views = _renderScene;
		if (views.IsCreated)
		{
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
}
