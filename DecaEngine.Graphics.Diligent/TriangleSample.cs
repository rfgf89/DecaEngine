using System.Numerics;
using DecaEngine.Core;
using Diligent;

namespace DecaEngine;

public class Sample : TimeLoopCore
{
	private readonly DiligentGraphicsApi _graphicsApi;
	private IPipelineState? _pipelineState;
	private readonly IRenderHandle _renderHandle;
	private readonly ICommandList[] _commandLists;

	public Sample(ICommandList[] commandLists, IRenderHandle renderHandle, DiligentGraphicsApi graphicsApi)
	{
		_commandLists = commandLists;
		_graphicsApi = graphicsApi;
		_renderHandle = renderHandle;
	}

	private IShader CreateShader(ShaderType shaderType)
	{
		using var shaderSourceFactory = _graphicsApi.EngineFactory.CreateDefaultShaderSourceStreamFactory(Path.Combine(Environment.CurrentDirectory, "Assets"));;
		var shaderCi = new ShaderCreateInfo()
		{
			SourceLanguage = ShaderSourceLanguage.Hlsl,
			Desc = new ShaderDesc()
			{
				Name = $"Cube {shaderType}",
				UseCombinedTextureSamplers = true,
				ShaderType = shaderType,
			},
			EntryPoint = "main",
			CompileFlags = ShaderCompileFlags.PackMatrixRowMajor,
			FilePath = shaderType == ShaderType.Vertex ? "Shaders/TriangleVS.hlsl" : "Shaders/TrianglePS.hlsl",
			ShaderSourceStreamFactory = shaderSourceFactory,
		};

		return _graphicsApi.Device.CreateShader(shaderCi, out var blob);
	}

	private IPipelineState CreatePipelineState()
	{
		using var vertexShader = CreateShader(ShaderType.Vertex);
		using var pixelShader = CreateShader(ShaderType.Pixel);

		var pipelineCreateInfo = new GraphicsPipelineStateCreateInfo()
		{
			PSODesc = new PipelineStateDesc()
			{
				Name = "Triangle PSO",
				PipelineType = PipelineType.Graphics,
			},
			GraphicsPipeline = new GraphicsPipelineDesc()
			{
				NumRenderTargets = 1,
				RTVFormats = [_graphicsApi.SwapChain.GetDesc().ColorBufferFormat],
				DSVFormat = _graphicsApi.SwapChain.GetDesc().DepthBufferFormat,
				PrimitiveTopology = PrimitiveTopology.TriangleList,
				RasterizerDesc = new RasterizerStateDesc()
				{
					CullMode = CullMode.None,
				},
				DepthStencilDesc = new DepthStencilStateDesc()
				{
					DepthEnable = true
				},
			},
			Vs = vertexShader,
			Ps = pixelShader,
		};

		return _graphicsApi.Device.CreateGraphicsPipelineState(pipelineCreateInfo);
	}

	protected override void OnStart()
	{
		_pipelineState = CreatePipelineState();
	}

	protected override void OnUpdate(float deltaTime)
	{
		_graphicsApi.DeferredContexts[0].Begin(0);
		//_graphicsPipeline.SetRenderTarget(_renderHandle);

		_graphicsApi.DeferredContexts[0].SetPipelineState(_pipelineState ?? throw new NullReferenceException());

		var drawAttribs = new DrawAttribs()
		{
			NumVertices = 3
		};
		_graphicsApi.DeferredContexts[0].Draw(drawAttribs);

		_commandLists[0] = _graphicsApi.DeferredContexts[0].FinishCommandList();

		_graphicsApi.DeferredContexts[0].InvalidateState();
		_graphicsApi.DeferredContexts[0].FinishFrame();
	}

	protected override void OnQuit()
	{
		_pipelineState?.Dispose();
	}
}