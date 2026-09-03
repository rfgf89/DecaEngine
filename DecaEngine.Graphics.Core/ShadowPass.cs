using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;

namespace DecaEngine.Graphics;

/// <summary>Per-frame schedule of which shadow cascades are redrawn, shared by the view-building
/// system and <see cref="ShadowPass"/>.</summary>
// Invariant: a cascade's sampling matrix must stay the one it was last rendered with, so redraw
// and refit are decided by this single mask. Default is every cascade every frame.
public sealed class ShadowCascadeSchedule
{
	public const int AllCascades = ~0;

	/// <summary>DECA_SHADOW_STAGGER=0 forces every cascade to be redrawn every frame.</summary>
	public static readonly bool StaggerEnabled =
		Environment.GetEnvironmentVariable("DECA_SHADOW_STAGGER") != "0";

	private int _renderMask = AllCascades;

	/// <summary>Bit i marks cascade i for redraw; must be set before the graph executes.</summary>
	public void SetRenderMask(int mask) => _renderMask = mask;

	/// <summary>Makes the next replay redraw every cascade.</summary>
	public void ForceAll() => _renderMask = AllCascades;

	public bool ShouldRender(int cascadeIndex) => (_renderMask & (1 << cascadeIndex)) != 0;
}

/// <summary>
/// Render-graph pass that culls and renders every shadow-map cascade (shared by all cameras).
/// Also owns the once-per-frame indirect-draw-buffer bookkeeping that both this pass and
/// <see cref="ForwardPass"/> depend on.
/// </summary>
// With a schedule, each cascade records into its own frozen sub-buffer; a skipped cascade is
// neither cleared nor drawn, so its slice keeps the last render it matches.
public sealed class ShadowPass : RenderGraphPass<ShadowPass.PassData>
{
	public override string Name => "Shadow Pass";

	private readonly IBatchRenderer _batchRenderer;
	private readonly DirectionalLightCascadeData _directionalLightCascadeData;
	private readonly ShadowCascadeSchedule? _schedule;
	private ICommandBuffer?[]? _cascadeCmds;

	public struct PassData
	{
	}

	public ShadowPass(IBatchRenderer batchRenderer, DirectionalLightCascadeData directionalLightCascadeData,
		ShadowCascadeSchedule? schedule = null)
	{
		_batchRenderer = batchRenderer;
		_directionalLightCascadeData = directionalLightCascadeData;

		_schedule = ShadowCascadeSchedule.StaggerEnabled ? schedule : null;
	}

	public override PassData Setup(IRenderGraphBuilder builder)
	{
		return default;
	}

	public override void WriteCommands(in PassData value, in IRenderGraphContext context)
	{
		var cmd = context.cmd;

		_batchRenderer.CheckAndReallocateBuffers();

		var lights = _directionalLightCascadeData;
		if (!lights.IsCreated)
		{
			return;
		}

		if (_schedule is null)
		{
			for (int i = 0; i < lights.viewData.Capacity; i++)
			{
				WriteCascadeCommands(cmd, i);
			}

			return;
		}

		// Rewriting graph commands is a structural change, so the first replay must redraw all
		// cascades; a skipped one would still show content drawn before the rebuild.
		_schedule.ForceAll();

		int cascadeCount = lights.viewData.Capacity;
		if (_cascadeCmds is null || _cascadeCmds.Length != cascadeCount)
		{
			_cascadeCmds = new ICommandBuffer[cascadeCount];
		}

		for (int i = 0; i < cascadeCount; i++)
		{
			// Sub-buffers outlive graph recompiles; their UpdateBuffer commands hold raw pointers
			// into lights.* native memory and re-read it on every replay.
			var sub = _cascadeCmds[i] ??= context.Api.CreateCommandBuffer();
			sub.BeginRecording();
			WriteCascadeCommands(sub, i);
			sub.Freeze();

			// An explicit command rather than a callback closure: the args live as command data and
			// so follow the same rewrite discipline as every other recorded command.
			cmd.ExecuteNested(sub, _schedule, i);
		}
	}

	private void WriteCascadeCommands(ICommandBuffer cmd, int cascadeIndex)
	{
		var lights = _directionalLightCascadeData;

		_batchRenderer.ClearIndirectDrawBuffers(cmd);

		_batchRenderer.SetupViewData(cmd, ref lights.viewData.GetRef(cascadeIndex, false));
		_batchRenderer.SetupCullData(cmd, ref lights.cullData.GetRef(cascadeIndex, false));
		_batchRenderer.SetupLightData(cmd, ref lights.lightData.GetRef(cascadeIndex, false));

		var shadowCullResult = _batchRenderer.ExecuteComputeCulling(cmd, cascadeIndex);
		_batchRenderer.ExecuteDrawShadows(cmd, shadowCullResult, cascadeIndex);
	}
}
