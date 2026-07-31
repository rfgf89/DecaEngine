using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Graphics.Diligent.RenderGraph;
using Diligent;
using UnsafeCollections.Collections.Native;

namespace DecaEngine.Graphics.Diligent;

public sealed class DrawGreenSquarePass : RenderGraphPass<DrawGreenSquarePass.PassData>, IDisposable
{
	private const uint SquareVertexCount = 6; // two triangles forming a unit square
	private readonly IGraphicsApi _api;

	public override string Name { get; }
	private readonly string _psoName;
	private IMaterialObject? _material;

	private readonly Ref<Vector2> _viewportSize;

	public struct PassData
	{
	}

	public DrawGreenSquarePass(IGraphicsApi api, string name, string psoName)
	{
		_api = api;
		Name = name;
		_psoName = psoName;

		_viewportSize = new Ref<Vector2>(_api.WindowHandle.Size);

		_api.WindowHandle.OnWindowResize += OnViewportChange;
	}

	public void OnViewportChange()
	{
		_viewportSize.Set(_api.WindowHandle.Size);
	}

	public override PassData Setup(IRenderGraphBuilder builder)
	{
		return default;
	}

	public override void WriteCommands(in PassData value, in IRenderGraphContext context)
	{
		_material ??= CreateSquareMaterial(context.Api);
		var cmd = context.cmd;

		cmd.SetBackBufferTarget(context.Api);
		cmd.ClearBackBufferTarget(context.Api, new Vector4(0.0f, 0.0f, 0.0f, 1.0f));
		cmd.SetViewport(_viewportSize);
		cmd.SetPipelineState(_material);
		cmd.CommitShaderResources(_material);
		cmd.Draw(SquareVertexCount);
	}

	private IMaterialObject CreateSquareMaterial(IGraphicsApi api)
	{
		var material = api.CreateMaterial(_psoName);
		var diligentPipeline = (DiligentGraphicsApi)api;

		material.SetState(api.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = $"{_psoName} Base State",
			RenderTargetFormats = [diligentPipeline.SwapChainColorFormat],
			DepthStencilFormat = diligentPipeline.SwapChainDepthFormat,
		}));

		material.SetShader(
			api.CreateShader($"{_psoName} VS", "Assets",
				"Shaders/GreenSquareVS.hlsl", ShaderObjectType.Vertex, "main"),
			api.CreateShader($"{_psoName} PS", "Assets",
				"Shaders/GreenSquarePS.hlsl", ShaderObjectType.Pixel, "main"));

		return material;
	}

	public void Dispose()
	{
		_viewportSize.Release();
		_material?.Release();
		_material = null;
	}
}