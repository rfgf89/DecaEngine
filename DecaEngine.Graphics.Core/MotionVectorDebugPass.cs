using System;
using System.Numerics;
using System.Runtime.InteropServices;
using DecaEngine.Graphics;
using DecaEngine.Core;

namespace DecaEngine.Graphics;

/// <summary>Resources for the motion vector debug overlay. The toggle lives in a cbuffer rather than
/// <see cref="PipelineFeatures"/>, so flipping it costs one discarded fullscreen draw, not a graph
/// rebuild.</summary>
public sealed unsafe class MotionVectorDebugPassResources : IReleaseObject
{
	/// <summary>Displacement in PIXELS that saturates the scale; beyond it the shader kills blue.</summary>
	public const float DefaultRangePixels = 16f;

	internal IMaterialObject Material { get; }

	private readonly IBufferHandle _constantBuffer;
	private readonly MotionVectorDebugConstantsData* _constants;

	public MotionVectorDebugPassResources(IGraphicsApi graphicsApi, IBatchRenderer batchRenderer,
		IGpuTexture motionTarget, uint renderWidth, uint renderHeight, uint displayWidth, uint displayHeight)
	{
		// Own VS instance: a shared shader would be released twice when the environment is rebuilt.
		var vs = graphicsApi.CreateShader("Motion Vector Debug VS", "EditorAssets/shader",
			"SkyBackgroundVS.hlsl", ShaderObjectType.Vertex);
		var ps = graphicsApi.CreateShader("Motion Vector Debug PS", "EditorAssets/shader",
			"MotionVectorDebugPS.hlsl", ShaderObjectType.Pixel);

		// Format is fixed RGBA8: this pass writes to the DISPLAYED target, after tonemap.
		var state = graphicsApi.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "Motion Vector Debug PSO",
			RenderTargetFormats = [TextureObjectFormat.R8G8B8A8UNorm],
			DepthStencilFormat = TextureObjectFormat.Unknown,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = false },
			InputLayout = [],
		});

		Material = graphicsApi.CreateMaterial("Motion Vector Debug Material");
		Material.SetShader(vs, ps);
		Material.SetState(state);
		batchRenderer.BindViewConstants(Material);

		// No sampler: the motion buffer is Load'ed 1:1, filtering would average across silhouettes.
		Material.SetTexture("_MotionTex", motionTarget);

		// dynamic = false: this needs UpdateBuffer from the command buffer (USAGE_DEFAULT).
		_constantBuffer = graphicsApi.CreateBuffer(new BufferInfo
		{
			name = "MotionVectorDebugConstants",
			dynamic = false,
			type = BufferHandleType.Constant,
			access = HandleAccess.Pixel,
			sizeInBytes = (uint)sizeof(MotionVectorDebugConstantsData),
		});

		Material.SetBuffer("MotionVectorDebugConstants", _constantBuffer, HandleAccess.Pixel);
		_constants = (MotionVectorDebugConstantsData*)NativeMemory.AllocZeroed(
			1, (nuint)sizeof(MotionVectorDebugConstantsData));

		SetDebugView(false, DefaultRangePixels);
		Resize(renderWidth, renderHeight, displayWidth, displayHeight);
	}

	// Layout of the "MotionVectorDebugConstants" cbuffer in MotionVectorDebugPS.hlsl: two float4.
	private struct MotionVectorDebugConstantsData
	{
		// xy - motion buffer size in pixels (render resolution), z - 1/range, w - enabled.
		public Vector4 Params;

		// xy - render-to-display size ratio: the pass draws into the display frame but Loads
		// from the motion buffer, which needs render-space coordinates.
		public Vector4 Params2;
	}

	public void SetDebugView(bool enabled, float rangePixels)
	{
		// Range is inverted here; a zero range would otherwise divide by zero in the shader.
		_constants->Params.Z = 1f / MathF.Max(rangePixels, 1e-3f);
		_constants->Params.W = enabled ? 1f : 0f;
	}

	/// <summary>Accepts new sizes; the scale is in RENDER-resolution pixels.</summary>
	public void Resize(uint renderWidth, uint renderHeight, uint displayWidth, uint displayHeight)
	{
		_constants->Params.X = renderWidth;
		_constants->Params.Y = renderHeight;
		_constants->Params2.X = renderWidth / (float)Math.Max(1u, displayWidth);
		_constants->Params2.Y = renderHeight / (float)Math.Max(1u, displayHeight);
	}

	/// <summary>Must be called AFTER a resize: it recreates the native texture the SRB points at.</summary>
	public void RebindTargets(IGpuTexture motionTarget, uint renderWidth, uint renderHeight,
		uint displayWidth, uint displayHeight)
	{
		Material.SetTexture("_MotionTex", motionTarget);
		Resize(renderWidth, renderHeight, displayWidth, displayHeight);
	}

	internal void WriteConstants(ICommandBuffer cmd) => cmd.UpdateBuffer(_constantBuffer, 0, _constants);

	public void Release()
	{
		Material.Release();
		_constantBuffer.Release();
		NativeMemory.Free(_constants);
	}
}

/// <summary>Render-graph pass that paints the motion vector buffer over the displayed frame. Runs
/// after tonemap and grading but before overlays, so gizmos stay visible on top.</summary>
public sealed class MotionVectorDebugPass : RenderGraphPass<MotionVectorDebugPass.PassData>
{
	public override string Name => "Motion Vector Debug Pass";

	private readonly MotionVectorDebugPassResources _resources;
	private readonly IGpuTexture _motionTarget;
	private readonly IGpuTexture _colorTarget;
	private readonly Ref<Vector2> _viewPortRef;

	public struct PassData
	{
	}

	public MotionVectorDebugPass(MotionVectorDebugPassResources resources, IGpuTexture motionTarget,
		IGpuTexture colorTarget, Ref<Vector2> viewPortRef)
	{
		_resources = resources;
		_motionTarget = motionTarget;
		_colorTarget = colorTarget;
		_viewPortRef = viewPortRef;
	}

	public override PassData Setup(IRenderGraphBuilder builder)
	{
		builder.ReadTarget(builder.ImportTexture(_motionTarget));
		builder.WriteTarget(builder.ImportTexture(_colorTarget));
		return default;
	}

	public override void WriteCommands(in PassData value, in IRenderGraphContext context)
	{
		var cmd = context.cmd;

		_resources.WriteConstants(cmd);

		cmd.SetRenderTarget(null, null);
		cmd.TransitionResource(_motionTarget, ResourceState.ShaderResource);

		cmd.SetRenderTarget(_colorTarget, null);
		cmd.SetViewport(_viewPortRef);
		cmd.SetPipelineState(_resources.Material);
		cmd.CommitShaderResources(_resources.Material);
		cmd.Draw(3);
	}
}
