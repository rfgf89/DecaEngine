using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;

namespace DecaEngine.Core;

/// <summary>
/// Render-graph pass that culls and renders every shadow-map cascade (shared by all cameras).
/// Also owns the once-per-frame indirect-draw-buffer bookkeeping that both this pass and
/// <see cref="ForwardPass"/> depend on.
/// </summary>
public sealed class ShadowPass : RenderGraphPass<ShadowPass.PassData>
{
	public override string Name => "Shadow Pass";

	private readonly IBatchRenderer _batchRenderer;
	private readonly DirectionalLightCascadeData _directionalLightCascadeData;

	public struct PassData
	{
	}

	public ShadowPass(IBatchRenderer batchRenderer, DirectionalLightCascadeData directionalLightCascadeData)
	{
		_batchRenderer = batchRenderer;
		_directionalLightCascadeData = directionalLightCascadeData;
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

		var lights = _directionalLightCascadeData;
		if (!lights.IsCreated)
		{
			return;
		}

		for (int i = 0; i < lights.viewData.Count; i++)
		{
			_batchRenderer.SetupViewData(cmd, ref lights.viewData.GetRef(i));
			_batchRenderer.SetupCullData(cmd, ref lights.cullData.GetRef(i));
			_batchRenderer.SetupLightData(cmd, ref lights.lightData.GetRef(i));

			var shadowCullResult = _batchRenderer.ExecuteComputeCulling(cmd, i);
			_batchRenderer.ExecuteDrawShadows(cmd, shadowCullResult, i);
		}
	}
}

