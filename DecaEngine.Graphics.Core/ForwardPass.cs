using System.Numerics;
using DecaEngine.Graphics;
using UnsafeCollections.Collections.Unsafe;
using DecaEngine.Core;

namespace DecaEngine.Graphics;

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
	private readonly IGpuTexture? _sceneCopy;
	private readonly SkyPassResources? _sky;
	private readonly SsaoPassResources? _ssao;
	private readonly IGpuTexture? _normalRoughTarget;
	private readonly IGpuTexture? _envFactorTarget;
	private readonly Vector4 _clearColor;

	// A getter, not a value: lets the caller toggle the overlay without recreating the pass.
	private readonly Func<Action<ICommandBuffer>?>? _overlay;

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
		IGpuTexture? colorTarget, IGpuTexture? depthTarget, Vector4 clearColor, IGpuTexture? sceneCopy = null,
		SkyPassResources? sky = null,
		SsaoPassResources? ssao = null, Func<Action<ICommandBuffer>?>? overlay = null,
		IGpuTexture? normalRoughTarget = null, IGpuTexture? envFactorTarget = null)
	{
		_sky = sky;
		_overlay = overlay;

		// Both reflection G-buffer targets must arrive together: geometry PSOs are built for three
		// MRT slots, and Vulkan forbids binding fewer.
		if (colorTarget is not null && normalRoughTarget is not null && envFactorTarget is not null)
		{
			_normalRoughTarget = normalRoughTarget;
			_envFactorTarget = envFactorTarget;
		}

		// AO is drawn inline between the opaque and transmissive draws, so it needs the refraction
		// path; without a scene copy there is nothing for its composite to read.
		_ssao = colorTarget is not null && sceneCopy is not null ? ssao : null;

		_batchRenderer = batchRenderer;
		_renderScene = renderScene;
		_viewPortRef = viewPortRef;
		_colorTarget = colorTarget;
		_depthTarget = depthTarget;
		_clearColor = clearColor;

		// Refraction needs an explicit offscreen target; the swap-chain back buffer can't be copied.
		_sceneCopy = colorTarget is not null ? sceneCopy : null;
	}

	// The AO/GTAO targets are deliberately not declared: they live entirely inside this pass.
	public override PassData Setup(IRenderGraphBuilder builder)
	{
		if (_colorTarget is not null)
		{
			builder.WriteTarget(builder.ImportTexture(_colorTarget));
		}

		if (_depthTarget is not null)
		{
			builder.WriteTarget(builder.ImportTexture(_depthTarget));
		}

		if (_sceneCopy is not null)
		{
			// Both written (opaque snapshot) and read: transmissive materials sample it as _SceneColor.
			var sceneCopy = builder.ImportTexture(_sceneCopy);
			builder.WriteTarget(sceneCopy);
			builder.ReadTarget(sceneCopy);
		}

		if (_normalRoughTarget is not null)
		{
			builder.WriteTarget(builder.ImportTexture(_normalRoughTarget));
			builder.WriteTarget(builder.ImportTexture(_envFactorTarget!));
		}

		return default;
	}

	public override unsafe void WriteCommands(in PassData value, in IRenderGraphContext context)
	{
		var cmd = context.cmd;

		_batchRenderer.CheckAndReallocateBuffers();

		var punctualViews = _renderScene;

		// Punctual shadows before any color target is bound: each slice binds its own depth slice.
		// The loop covers all slices because commands are frozen; unused ones draw nothing.
		if (punctualViews.IsCreated)
		{
			_batchRenderer.SetupPunctualShadowMatrices(cmd, punctualViews.punctualShadowMatrices);

			for (int s = 0; s < punctualViews.punctualShadowCullData.Capacity; s++)
			{
				_batchRenderer.ClearIndirectDrawBuffers(cmd);
				_batchRenderer.SetupCullData(cmd, ref punctualViews.punctualShadowCullData.GetRef(s, false));
				_batchRenderer.SetupLightData(cmd, ref punctualViews.punctualShadowLightData.GetRef(s, false));

				var sliceCull = _batchRenderer.ExecuteComputeCulling(cmd);
				_batchRenderer.ExecuteDrawPunctualShadow(cmd, sliceCull, s);
			}

			// Unconditional: the PS declares the texture always, so its layout must be valid.
			_batchRenderer.TransitionPunctualShadowsForRead(cmd);
		}

		var renderColor = _colorTarget;
		var renderDepth = _depthTarget;

		if (renderColor is not null)
		{
			// Bind the G-buffer before clearing it: on Vulkan an unbound clear goes through
			// vkCmdClearColorImage, which wants TRANSFER_DST and trips VUID-...-imageLayout-00004.
			if (_normalRoughTarget is not null)
			{
				cmd.SetRenderTargets([renderColor, _normalRoughTarget, _envFactorTarget!], renderDepth);
			}
			else
			{
				cmd.SetRenderTarget(renderColor, renderDepth);
			}

			cmd.ClearRenderTarget(renderColor, _clearColor);
			if (renderDepth is not null)
			{
				cmd.ClearDepthStencil(renderDepth, ClearDepthStencilFlags.Depth, 0.0f, 0);
			}

			// Cleared to zero: w == 0 in EnvFactor means "no lit path", and SSR skips those pixels.
			if (_normalRoughTarget is not null)
			{
				cmd.ClearRenderTarget(_normalRoughTarget, Vector4.Zero);
				cmd.ClearRenderTarget(_envFactorTarget!, Vector4.Zero);

				// Sky/AO/overlays use single-target PSOs; the MRT triple is rebound before batch draws.
				cmd.SetRenderTarget(renderColor, renderDepth);
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
			// One light pool for all cameras; must be uploaded before the per-camera cluster dispatches.
			_batchRenderer.SetupPunctualLights(cmd, views.punctualLights);

			for (int i = 0; i < views.viewData.Capacity; i++)
			{
				// Per camera: culling allocates instance slots by atomic increment, and without a
				// reset the counts would accumulate across cameras.
				_batchRenderer.ClearIndirectDrawBuffers(cmd);

				_batchRenderer.SetupViewData(cmd, ref views.viewData.GetRef(i, false));
				_batchRenderer.SetupCullData(cmd, ref views.cullData.GetRef(i, false));
				_batchRenderer.SetupLightData(cmd, ref views.lightData.GetRef(i, false));

				// Reads ClusterParams from the Light cbuffer, so strictly after SetupLightData.
				_batchRenderer.ExecuteLightClustering(cmd);

				_sky?.Draw(cmd);

				var cullResult = _batchRenderer.ExecuteComputeCulling(cmd);

				// Batch draws need the MRT triple bound, single-target PSOs need it unbound; the
				// rebinds below sit exactly on those boundaries.
				if (_sceneCopy is null)
				{
					if (_normalRoughTarget is not null)
					{
						cmd.SetRenderTargets([renderColor!, _normalRoughTarget, _envFactorTarget!], renderDepth);
					}

					_batchRenderer.ExecuteDrawBatching(cmd, cullResult);

					if (_normalRoughTarget is not null)
					{
						cmd.SetRenderTarget(renderColor, renderDepth);
					}

					_overlay?.Invoke()?.Invoke(cmd);
					continue;
				}

				// Refraction order: opaque draws, snapshot into _sceneCopy, then transmissive draws
				// sampling that snapshot. A bound render target cannot be copied, so it is unbound.
				//
				// This transition must precede the opaque draws: _SceneColor is statically bound in
				// every material's SRB, and on frame one the texture is still UNDEFINED.
				cmd.TransitionResource(_sceneCopy, ResourceState.ShaderResource);

				if (_normalRoughTarget is not null)
				{
					cmd.SetRenderTargets([renderColor!, _normalRoughTarget, _envFactorTarget!], renderDepth);
				}

				_batchRenderer.ExecuteDrawBatching(cmd, cullResult, BatchDrawFilter.OpaqueOnly);

				cmd.SetRenderTarget(null, null);
				cmd.CopyTexture(_colorTarget, _sceneCopy);
				cmd.TransitionResource(_sceneCopy, ResourceState.ShaderResource);

				// AO runs inline off the opaque depth. The snapshot is deliberately NOT retaken, so
				// transmissive materials refract the pre-AO frame: screen-space AO approximates
				// ambient occlusion at a surface and does not apply to light passing through one.
				if (_ssao is not null)
				{
					_ssao.WriteInlineCommands(cmd, renderColor!, renderDepth!, _viewPortRef);
				}

				if (_normalRoughTarget is not null)
				{
					cmd.SetRenderTargets([renderColor!, _normalRoughTarget, _envFactorTarget!], renderDepth);
				}
				else
				{
					cmd.SetRenderTarget(renderColor, renderDepth);
				}

				cmd.SetViewport(_viewPortRef);
				_batchRenderer.ExecuteDrawBatching(cmd, cullResult, BatchDrawFilter.TransparentOnly);

				if (_normalRoughTarget is not null)
				{
					cmd.SetRenderTarget(renderColor, renderDepth);
				}

				_overlay?.Invoke()?.Invoke(cmd);
			}
		}
	}
}
