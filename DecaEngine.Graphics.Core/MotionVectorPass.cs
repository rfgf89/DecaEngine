using System.Numerics;
using System.Runtime.InteropServices;
using DecaEngine.Graphics;
using DecaEngine.Core;

namespace DecaEngine.Graphics;

/// <summary>Owns the GPU resources for the screen-space motion vector buffer. Vectors are rebuilt
/// from depth alone, so they carry camera motion only: self-moving geometry reads as static.</summary>
public sealed unsafe class MotionVectorPassResources : IReleaseObject
{
	internal IMaterialObject Material { get; }

	/// <summary>RG16F target; units are screen UV, pointing current -> previous (prevUV = curUV + motion).</summary>
	public IRenderTarget MotionTarget { get; }

	// Own unmanaged memory + UpdateBuffer: SetConstant would re-bind the SRB mid-flight on Vulkan.
	private readonly IBufferHandle _constantBuffer;
	private readonly MotionVectorConstantsData* _constants;

	private Matrix4x4 _prevViewProj;

	private bool _hasPrev;

	public MotionVectorPassResources(IGraphicsApi graphicsApi, IBatchRenderer batchRenderer,
		string colorTargetName, IGpuTexture depthTarget, uint width, uint height)
	{
		// Own VS instance: a shared shader would be released twice when the environment is rebuilt.
		var vs = graphicsApi.CreateShader("Motion Vector Fullscreen VS", "EditorAssets/shader",
			"SkyBackgroundVS.hlsl", ShaderObjectType.Vertex);
		var ps = graphicsApi.CreateShader("Motion Vector PS", "EditorAssets/shader",
			"MotionVectorPS.hlsl", ShaderObjectType.Pixel);

		MotionTarget = graphicsApi.CreateRenderTarget(new TextureInfo
		{
			name = colorTargetName + " Motion Vectors",
			width = width,
			height = height,
			format = TextureObjectFormat.R16G16Float,
		});

		var state = graphicsApi.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "Motion Vector PSO",
			RenderTargetFormats = [TextureObjectFormat.R16G16Float],
			DepthStencilFormat = TextureObjectFormat.Unknown,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = false },
			InputLayout = [],
		});

		Material = graphicsApi.CreateMaterial("Motion Vector Material");
		Material.SetShader(vs, ps);
		Material.SetState(state);
		batchRenderer.BindViewConstants(Material);

		// No sampler on purpose: depth is only Load-ed, and a blend of two depths lies on neither.
		Material.SetTexture("_DepthTex", depthTarget);

		// dynamic = false: Diligent updates dynamic buffers via Map; we need UpdateBuffer.
		_constantBuffer = graphicsApi.CreateBuffer(new BufferInfo
		{
			name = "MotionVectorConstants",
			dynamic = false,
			type = BufferHandleType.Constant,
			access = HandleAccess.Pixel,
			sizeInBytes = (uint)sizeof(MotionVectorConstantsData),
		});

		Material.SetBuffer("MotionVectorConstants", _constantBuffer, HandleAccess.Pixel);

		_constants = (MotionVectorConstantsData*)NativeMemory.AllocZeroed(1,
			(nuint)sizeof(MotionVectorConstantsData));

		// Identity, not zeros: a zero matrix gives prevClip.w == 0 and clips the whole frame.
		_constants->Reprojection = Matrix4x4.Identity;
	}

	// Shaders compile with PackMatrixRowMajor, so the matrix goes out untransposed.
	private struct MotionVectorConstantsData
	{
		public Matrix4x4 Reprojection;
	}

	/// <summary>Latches the frame's reprojection matrix; must be called exactly once per frame, from
	/// outside the graph - recorded commands are frozen and would freeze the matrix with them.</summary>
	public void UpdateFromView(in Matrix4x4 viewProj)
	{
		if (!_hasPrev)
		{
			_constants->Reprojection = Matrix4x4.Identity;
			_prevViewProj = viewProj;
			_hasPrev = true;
			return;
		}

		// Row-vector order (v * M), as everywhere in the engine: unproject now, project with the past.
		if (Matrix4x4.Invert(viewProj, out var invViewProj))
		{
			_constants->Reprojection = invViewProj * _prevViewProj;
		}
		else
		{
			// Degenerate camera (collapsed viewport on minimize): identity yields zero vectors.
			_constants->Reprojection = Matrix4x4.Identity;
		}

		_prevViewProj = viewProj;
	}

	/// <summary>Drops history: call whenever frame continuity breaks (teleport, scene swap, resize).</summary>
	public void ResetHistory()
	{
		_hasPrev = false;
		_constants->Reprojection = Matrix4x4.Identity;
	}

	/// <summary>Resizes the motion target and resets history.</summary>
	public void Resize(uint width, uint height)
	{
		MotionTarget.Resize(new Vector2(width, height));
		ResetHistory();
	}

	/// <summary>Rebind after the depth target's Resize: it recreates the native texture.</summary>
	public void RebindTargets(IGpuTexture depthTarget, uint width, uint height)
	{
		Material.SetTexture("_DepthTex", depthTarget);
		Resize(width, height);
	}

	// The command re-reads this CPU memory on every replay of a frozen buffer.
	internal void WriteConstants(ICommandBuffer cmd) => cmd.UpdateBuffer(_constantBuffer, 0, _constants);

	public void Release()
	{
		Material.Release();
		MotionTarget.Release();
		_constantBuffer.Release();
		NativeMemory.Free(_constants);
	}
}

/// <summary>Render-graph pass that fills the screen-space motion vector buffer from scene depth;
/// must run after <see cref="ForwardPass"/> and before post-processing.</summary>
public sealed class MotionVectorPass : RenderGraphPass<MotionVectorPass.PassData>
{
	public override string Name => "Motion Vector Pass";

	private readonly MotionVectorPassResources _resources;
	private readonly IGpuTexture _depthTarget;
	private readonly Ref<Vector2> _viewPortRef;

	public struct PassData
	{
	}

	public MotionVectorPass(MotionVectorPassResources resources, IGpuTexture depthTarget,
		Ref<Vector2> viewPortRef)
	{
		_resources = resources;
		_depthTarget = depthTarget;
		_viewPortRef = viewPortRef;
	}

	public override PassData Setup(IRenderGraphBuilder builder)
	{
		builder.ReadTarget(builder.ImportTexture(_depthTarget));
		builder.WriteTarget(builder.ImportTexture(_resources.MotionTarget));
		return default;
	}

	public override void WriteCommands(in PassData value, in IRenderGraphContext context)
	{
		var cmd = context.cmd;

		_resources.WriteConstants(cmd);

		// DepthRead, not ShaderResource: Vulkan needs DEPTH_STENCIL_READ_ONLY_OPTIMAL here.
		cmd.SetRenderTarget(null, null);
		cmd.TransitionResource(_depthTarget, ResourceState.DepthRead);

		cmd.SetRenderTarget(_resources.MotionTarget, null);
		cmd.SetViewport(_viewPortRef);
		cmd.SetPipelineState(_resources.Material);
		cmd.CommitShaderResources(_resources.Material);
		cmd.Draw(3);
	}
}
