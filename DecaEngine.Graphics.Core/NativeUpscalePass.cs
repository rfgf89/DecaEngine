using System;
using System.Numerics;
using DecaEngine.Graphics;
using DecaEngine.Core;

namespace DecaEngine.Graphics;

/// <summary>Upscaler slot backed by a native library (FSR via ffx-api, DLSS). Input contract matches
/// the built-in TAAU: HDR scene, depth and motion at render resolution, output display RGBA16F.</summary>
public interface INativeUpscalerBackend : IReleaseObject
{
	/// <summary>Name for logs, e.g. "FSR 3.1.4".</summary>
	string DebugName { get; }

	/// <summary>Display-resolution RGBA16F with UAV; the tonemap input while this backend is active.</summary>
	IRenderTarget OutputTarget { get; }

	/// <summary>Called from the frozen buffer replay; must leave the context consistent afterwards.</summary>
	void Dispatch();

	/// <summary>Per-frame parameters; must be called AFTER this frame's jitter is applied.</summary>
	void SetFrameParams(Vector2 jitterPixels);

	/// <summary>Frame duration in seconds.</summary>
	void SetDeltaTime(float seconds);

	/// <summary>Resizes inputs and output, recreating the native context. Breaks history.</summary>
	void Resize(IGpuTexture sceneHdr, IGpuTexture depth, IGpuTexture motion,
		uint renderWidth, uint renderHeight, uint displayWidth, uint displayHeight);

	/// <summary>Marks the next frame with the reset flag.</summary>
	void ResetHistory();

	/// <summary>Non-null when the backend needs a TYPED depth copy: Diligent creates depth as
	/// R32_TYPELESS and the native runtime would read it as zeros.</summary>
	IGpuTexture? DepthProxy => null;

	/// <summary>Non-null reactive/transparency masks the pass clears every frame. It must be the
	/// pass, not backend creation: out-of-band immediate-context commands crash mid-frame.</summary>
	IGpuTexture? ReactiveMask => null;

	/// <summary>See <see cref="ReactiveMask"/>.</summary>
	IGpuTexture? TransparencyMask => null;
}

/// <summary>Wraps a native upscaler: declares inputs/output to the graph, transitions resources to
/// the states ffx-api expects, and injects the native dispatch as a callback command. Runs after
/// scene post-processing and before tonemap, so accumulation happens on linear light.</summary>
public sealed class NativeUpscalePass : RenderGraphPass<NativeUpscalePass.PassData>
{
	public override string Name => "Native Upscale Pass";

	private readonly INativeUpscalerBackend _backend;
	private readonly IGpuTexture _sceneHdrTarget;
	private readonly IGpuTexture _depthTarget;
	private readonly IGpuTexture _motionTarget;

	public struct PassData
	{
	}

	public NativeUpscalePass(INativeUpscalerBackend backend, IGpuTexture sceneHdrTarget,
		IGpuTexture depthTarget, IGpuTexture motionTarget)
	{
		_backend = backend;
		_sceneHdrTarget = sceneHdrTarget;
		_depthTarget = depthTarget;
		_motionTarget = motionTarget;
	}

	public override PassData Setup(IRenderGraphBuilder builder)
	{
		builder.ReadTarget(builder.ImportTexture(_sceneHdrTarget));
		builder.ReadTarget(builder.ImportTexture(_depthTarget));
		builder.ReadTarget(builder.ImportTexture(_motionTarget));
		builder.WriteTarget(builder.ImportTexture(_backend.OutputTarget));

		if (_backend.DepthProxy is { } proxy)
		{
			builder.WriteTarget(builder.ImportTexture(proxy));
		}

		if (_backend.ReactiveMask is { } reactive)
		{
			builder.WriteTarget(builder.ImportTexture(reactive));
		}

		if (_backend.TransparencyMask is { } transparency)
		{
			builder.WriteTarget(builder.ImportTexture(transparency));
		}

		return default;
	}

	public override void WriteCommands(in PassData value, in IRenderGraphContext context)
	{
		var cmd = context.cmd;

		// Depth goes to ShaderResource, not DepthRead: this pass is D3D12-only, so Vulkan's
		// DEPTH_STENCIL_READ_ONLY restriction does not apply.
		cmd.SetRenderTarget(null, null);

		// The typed depth copy must precede the other transitions: CopyTexture moves depth through
		// CopySource, while the runtime expects inputs in ShaderResource.
		if (_backend.DepthProxy is { } proxy)
		{
			cmd.CopyTexture(_depthTarget, proxy);
			cmd.TransitionResource(proxy, ResourceState.ShaderResource);
		}

		cmd.TransitionResource(_sceneHdrTarget, ResourceState.ShaderResource);
		cmd.TransitionResource(_depthTarget, ResourceState.ShaderResource);
		cmd.TransitionResource(_motionTarget, ResourceState.ShaderResource);
		cmd.TransitionResource(_backend.OutputTarget, ResourceState.UnorderedAccess);

		if (_backend.ReactiveMask is { } reactiveMask)
		{
			cmd.ClearRenderTarget(reactiveMask, Vector4.Zero);
			cmd.TransitionResource(reactiveMask, ResourceState.ShaderResource);
		}

		if (_backend.TransparencyMask is { } transparencyMask)
		{
			cmd.ClearRenderTarget(transparencyMask, Vector4.Zero);
			cmd.TransitionResource(transparencyMask, ResourceState.ShaderResource);
		}

		cmd.Callback(_backend.Dispatch);
	}
}
