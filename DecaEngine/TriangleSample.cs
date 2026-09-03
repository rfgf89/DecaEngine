using System.Numerics;
using System.Runtime.CompilerServices;
using DecaEngine;
using DecaEngine.Core;
using Diligent;
// Sample drives the native Diligent context directly, so states come from Diligent.
using ClearDepthStencilFlags = Diligent.ClearDepthStencilFlags;
using ResourceState = Diligent.ResourceState;
using ValueType = Diligent.ValueType;
using DecaEngine.Graphics;

namespace DiligentEngineNET.Samples;

public class TriangleSample(GraphicsBackend backend) : Application(backend)
{
    private IPipelineState? _pipelineState;
    private IShader CreateShader(ShaderType shaderType)
    {
        using var shaderSourceFactory = EngineFactory.CreateDefaultShaderSourceStreamFactory(Path.Combine(Environment.CurrentDirectory, "Assets"));
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

        return Device.CreateShader(shaderCi, out var blob);
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
                RTVFormats = [SwapChain.GetDesc().ColorBufferFormat],
                DSVFormat = SwapChain.GetDesc().DepthBufferFormat,
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

        return Device.CreateGraphicsPipelineState(pipelineCreateInfo);
    }

    private void Render()
    {
        var rtv = SwapChain.GetCurrentBackBufferRTV();
        var dsv = SwapChain.GetDepthBufferDSV();

        var clearColor = new Vector4( .350f, .350f, .350f, 1.0f );

        ImmediateContext.SetRenderTargets([rtv], dsv, ResourceStateTransitionMode.Transition);
        ImmediateContext.ClearRenderTarget(rtv, clearColor, ResourceStateTransitionMode.Transition);
        ImmediateContext.ClearDepthStencil(dsv, 
            ClearDepthStencilFlags.Depth,
            1.0f, 0,
            ResourceStateTransitionMode.Transition);
        
        ImmediateContext.SetPipelineState(_pipelineState ?? throw new NullReferenceException());

        var drawAttribs = new DrawAttribs()
        {
            NumVertices = 3
        };
        ImmediateContext.Draw(drawAttribs);
    }

    protected override void OnSetup()
    {
        _pipelineState = CreatePipelineState();
    }

    protected override void OnUpdate(double dt)
    {
        Render();
    }

    protected override void OnPresent()
    {
        SwapChain.Present(1);
    }

    protected override void OnExit()
    {
        _pipelineState?.Dispose();
    }
}