using System.Numerics;
using DecaEngine.Graphics;
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
	private readonly PipelineRenderTargets? _targets;
	private readonly SkyPassResources? _skyResources;
	private readonly SsaoPassResources? _ssaoResources;
	private readonly SsgiPassResources? _ssgiResources;
	private readonly bool _enableShadowPass;
	private readonly Vector4 _clearColor;

	private Ref<Vector2> _viewPortRef;

	/// <summary>Non-null only in off-screen mode (<paramref name="colorTargetName"/> given to the
	/// constructor) - the pipeline owns and creates these itself, the same way a swap chain would own
	/// the back buffer, so off-screen consumers (see <see cref="DecaEngine.Editor.ModelViewportEnvironment"/>)
	/// resize/bind them through here rather than creating their own.</summary>
	public PipelineRenderTargets? Targets => _targets;

	/// <summary>Non-null only when a sky background was enabled (see <see cref="SkyPassResources"/>).
	/// Exposed so the preview viewport can push the environment yaw (light-rotation slider) into the
	/// sky shader - см. <see cref="SkyPassResources.SetEnvironmentYaw"/>.</summary>
	public SkyPassResources? SkyResources => _skyResources;

	/// <summary>Non-null only when SSAO was enabled and resources were actually created (requires an
	/// off-screen color target + scene copy) - see <see cref="SsaoPassResources"/>. Exposed so
	/// off-screen consumers (see <see cref="DecaEngine.Editor.ModelViewportEnvironment"/>) can resize
	/// the AO target alongside their other render targets.</summary>
	public SsaoPassResources? SsaoResources => _ssaoResources;

	/// <summary>Non-null only when SSGI was enabled and resources were actually created (requires an
	/// off-screen color target + scene copy) - see <see cref="SsgiPassResources"/>. Exposed so
	/// off-screen consumers can resize the GI target alongside their other render targets.</summary>
	public SsgiPassResources? SsgiResources => _ssgiResources;

	public GraphicsPipelineSimple(IGraphicsApi api, IBatchRenderer batchRenderer)
		: this(api, batchRenderer, null, null, 0, 0, new Vector4(0.1f, 0.1f, 0.1f, 1f))
	{
	}

	/// <param name="colorTargetName">Non-null selects off-screen mode: the pipeline creates and owns
	/// its own color/depth/scene-copy(/MSAA) targets instead of drawing to the swap chain's back
	/// buffer (see <see cref="DecaEngine.Editor.ModelViewportEnvironment"/>). Null draws straight to
	/// the back buffer and every other creation parameter below is ignored.</param>
	public GraphicsPipelineSimple(IGraphicsApi api, IBatchRenderer batchRenderer, string? colorTargetName,
		string? depthTargetName, uint width, uint height, Vector4 clearColor, uint msaaSamples = 1,
		bool skyBackground = false, IGpuTexture? environmentMap = null, bool ssao = false, bool enableShadowPass = false,
		AmbientOcclusionMode aoMode = AmbientOcclusionMode.Ssao, bool ssgi = false)
	{
		_enableShadowPass = enableShadowPass;
		_api = api;
		_batchRenderer = batchRenderer;
		_clearColor = clearColor;
		_renderGraph = _api.CreateRenderGraph();

		if (skyBackground && environmentMap is not null)
		{
			_skyResources = new SkyPassResources(api, batchRenderer, environmentMap, msaaSamples);
		}

		if (colorTargetName is not null)
		{
			_targets = new PipelineRenderTargets(api, colorTargetName, depthTargetName!, width, height, msaaSamples);

			// SSAO требует офскрин-режима - см. ForwardPass, чей refraction-пасс по той же причине
			// игнорирует sceneCopy для swap-chain-пути.
			if (ssao)
			{
				var renderDepth = _targets.MsaaDepthTarget ?? _targets.DepthTarget;
				_ssaoResources = new SsaoPassResources(api, batchRenderer, colorTargetName, width, height,
					renderDepth, _targets.SceneCopyTarget, msaaSamples, aoMode);
			}

			// SSGI требует офскрин-режима по той же причине, что SSAO (нужны scene-copy + депт).
			if (ssgi)
			{
				var renderDepth = _targets.MsaaDepthTarget ?? _targets.DepthTarget;
				_ssgiResources = new SsgiPassResources(api, batchRenderer, colorTargetName, width, height,
					renderDepth, _targets.SceneCopyTarget, _targets.MsaaDepthTarget is not null);
			}

			_viewPortRef = new Ref<Vector2>(new Vector2(width, height));
		}
		else
		{
			_viewPortRef = new Ref<Vector2>(_api.WindowHandle.Size);
			_api.WindowHandle.OnWindowResize += OnViewportChange;
		}
	}

	/// <summary>Перепривязывает SSAO/SSGI-материалы к текущим depth/scene-copy таргетам ПОСЛЕ их
	/// Resize (см. ModelPreviewViewport.ResizeTargets) - no-op когда оба пасса выключены.</summary>
	public void RebindSsaoTargets()
	{
		_ssaoResources?.RebindTargets(_targets!.MsaaDepthTarget ?? _targets.DepthTarget, _targets.SceneCopyTarget);
		_ssgiResources?.RebindTargets(_targets!.MsaaDepthTarget ?? _targets.DepthTarget, _targets.SceneCopyTarget);
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

		// Тени превью: пасс глубины с точки зрения света ПЕРЕД ForwardPass-ом (см.
		// SimpleCullingAndRenderSystem.BuildLightData - он заполняет renderScene одним каскадом).
		if (_enableShadowPass)
		{
			_renderGraph.AddPass(new ShadowPass(_batchRenderer, renderScene));
		}

		// AO рисуется инлайн внутри ForwardPass - между opaque- и transmissive-дроу, чтобы стекло
		// преломляло затенённый фон, но само экранным AO не глушилось (см.
		// SsaoPassResources.WriteInlineCommands).
		_renderGraph.AddPass(new ForwardPass(_batchRenderer, renderViews, _viewPortRef, _targets?.ColorTarget,
			_targets?.DepthTarget, _clearColor, _targets?.SceneCopyTarget, _skyResources,
			_targets?.MsaaColorTarget, _targets?.MsaaDepthTarget, _ssaoResources));

		// SSGI собирает bounce из уже отрисованного кадра, поэтому идёт последним - после AO
		// в источнике света уже есть контактные тени, и bounce их корректно учитывает.
		if (_ssgiResources is not null)
		{
			var renderDepth = _targets!.MsaaDepthTarget ?? _targets.DepthTarget;
			_renderGraph.AddPass(new SsgiPass(_ssgiResources, _targets.ColorTarget, _targets.SceneCopyTarget, renderDepth, _viewPortRef));
		}
	}

	public void Execute()
	{
		_renderGraph.Execute();
	}

	/// <summary>Освобождает рендер-граф (заморожённые команды и пины ресурсов) - для пересоздания
	/// превью-окружения на лету (см. ModelPreviewViewport.RecreateEnvironment). Вызывающий обязан
	/// сперва дождаться GPU (Flush + WaitForIdle).</summary>
	public void Release()
	{
		_renderGraph.Release();
		_ssaoResources?.Release();
		_ssgiResources?.Release();
		_skyResources?.Release();
		_targets?.Release();
	}

	public RenderGraphDebugSnapshot DebugSnapshot => _renderGraph.DebugSnapshot;
	public RenderGraphDebugHistory DebugHistory => _renderGraph.DebugHistory;
}
