using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DecaEngine;
using DecaEngine.Core;
using Diligent;
using StbImageSharp;
using ValueType = Diligent.ValueType;

namespace DiligentEngineNET.Samples;

public class CubeTextureSample(GraphicsBackend backend) : Application(backend)
{
    private struct Vertex(Vector3 pos, Vector2 uv)
    {
        public Vector3 Pos = pos;
        public Vector2 Uv = uv;
    }

    private float _angle;
    private Matrix4x4 _worldViewProj = Matrix4x4.Identity;

    private IBuffer? _vertexBuffer;
    private IBuffer? _indexBuffer;
    private IBuffer? _vertexShaderConstants;
    private IPipelineState? _pipelineState;
    private IShaderResourceBinding? _srb;

    private IShader CreateShader(ShaderType shaderType)
    {
        using var shaderSourceFactory = EngineFactory.CreateDefaultShaderSourceStreamFactory(Path.Combine(Environment.CurrentDirectory, "Assets"));
        var shaderCi = new ShaderCreateInfo()
        {
            SourceLanguage = ShaderSourceLanguage.Hlsl,
            Desc = new ShaderDesc()
            {
                Name = $"Cube Texture {shaderType}",
                UseCombinedTextureSamplers = true,
                ShaderType = shaderType,
            },
            EntryPoint = "main",
            CompileFlags = ShaderCompileFlags.PackMatrixRowMajor,
            FilePath = shaderType == ShaderType.Vertex ? "Shaders/CubeTextureVS.hlsl" : "Shaders/CubeTexturePS.hlsl",
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
                Name = "Cube PSO",
                PipelineType = PipelineType.Graphics,
                ResourceLayout = new PipelineResourceLayoutDesc()
                {
                    DefaultVariableType = ShaderResourceVariableType.Static,
                    Variables =
                    [
                        new ShaderResourceVariableDesc()
                        {
                            ShaderStages = ShaderType.Pixel,
                            Name = "g_texture",
                            Type = ShaderResourceVariableType.Mutable
                        }
                    ],
                    ImmutableSamplers =
                    [
                        new ImmutableSamplerDesc()
                        {
                            ShaderStages = ShaderType.Pixel,
                            SamplerOrTextureName = "g_texture",
                            Desc = new SamplerDesc()
                            {
                                MagFilter = FilterType.Linear,
                                MinFilter = FilterType.Linear,
                                MipFilter = FilterType.Linear,
                                AddressU = Diligent.TextureAddressMode.Clamp,
                                AddressV = Diligent.TextureAddressMode.Clamp,
                                AddressW = Diligent.TextureAddressMode.Clamp,
                            }
                        }
                    ]
                }
            },
            GraphicsPipeline = new GraphicsPipelineDesc()
            {
                NumRenderTargets = 1,
                RTVFormats = [SwapChain.GetDesc().ColorBufferFormat],
                DSVFormat = SwapChain.GetDesc().DepthBufferFormat,
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                RasterizerDesc = new RasterizerStateDesc()
                {
                    CullMode = CullMode.Front,
                },
                DepthStencilDesc = new DepthStencilStateDesc()
                {
                    DepthEnable = true
                },
                InputLayout = new InputLayoutDesc()
                {
                    LayoutElements =
                    [
                        new LayoutElement
                            { InputIndex = 0, NumComponents = 3, ValueType = ValueType.Float32, IsNormalized = false },
                        new LayoutElement
                            { InputIndex = 1, NumComponents = 2, ValueType = ValueType.Float32, IsNormalized = false }
                    ]
                },
            },
            Vs = vertexShader,
            Ps = pixelShader,
        };

        return Device.CreateGraphicsPipelineState(pipelineCreateInfo);
    }

    private IBuffer CreateUniformBuffer(int size, string name)
    {
        var bufferDesc = new BufferDesc()
        {
            Name = name,
            Size = (ulong)size,
            Usage = Usage.Dynamic,
            BindFlags = BindFlags.UniformBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
        };
        return Device.CreateBuffer(bufferDesc);
    }

    private IBuffer CreateVertexBuffer()
    {
        var cubeVertices = new Vertex[]
        {
            new(new Vector3(-1, -1, -1), new Vector2(0, 1)),
            new(new Vector3(-1, +1, -1), new Vector2(0, 0)),
            new(new Vector3(+1, +1, -1), new Vector2(1, 0)),
            new(new Vector3(+1, -1, -1), new Vector2(1, 1)),

            new(new Vector3(-1, -1, -1), new Vector2(0, 1)),
            new(new Vector3(-1, -1, +1), new Vector2(0, 0)),
            new(new Vector3(+1, -1, +1), new Vector2(1, 0)),
            new(new Vector3(+1, -1, -1), new Vector2(1, 1)),

            new(new Vector3(+1, -1, -1), new Vector2(0, 1)),
            new(new Vector3(+1, -1, +1), new Vector2(1, 1)),
            new(new Vector3(+1, +1, +1), new Vector2(1, 0)),
            new(new Vector3(+1, +1, -1), new Vector2(0, 0)),

            new(new Vector3(+1, +1, -1), new Vector2(0, 1)),
            new(new Vector3(+1, +1, +1), new Vector2(0, 0)),
            new(new Vector3(-1, +1, +1), new Vector2(1, 0)),
            new(new Vector3(-1, +1, -1), new Vector2(1, 1)),

            new(new Vector3(-1, +1, -1), new Vector2(1, 0)),
            new(new Vector3(-1, +1, +1), new Vector2(0, 0)),
            new(new Vector3(-1, -1, +1), new Vector2(0, 1)),
            new(new Vector3(-1, -1, -1), new Vector2(1, 1)),

            new(new Vector3(-1, -1, +1), new Vector2(1, 1)),
            new(new Vector3(+1, -1, +1), new Vector2(0, 1)),
            new(new Vector3(+1, +1, +1), new Vector2(0, 0)),
            new(new Vector3(-1, +1, +1), new Vector2(1, 0)),
        };

        var bufferDesc = new BufferDesc()
        {
            Name = "Cube vertex buffer",
            Usage = Usage.Immutable,
            BindFlags = BindFlags.VertexBuffer,
            Size = (ulong)(cubeVertices.Length * Unsafe.SizeOf<Vertex>()),
        };
        return Device.CreateBuffer(bufferDesc, cubeVertices);
    }

    private IBuffer CreateIndexBuffer()
    {
        var indices = new uint[]
        {
            2,0,1,    2,3,0,
            4,6,5,    4,7,6,
            8,10,9,   8,11,10,
            12,14,13, 12,15,14,
            16,18,17, 16,19,18,
            20,21,22, 20,22,23
        };

        var bufferDesc = new BufferDesc()
        {
            Name = "Cube index buffer",
            Usage = Usage.Immutable,
            BindFlags = BindFlags.IndexBuffer,
            Size = (ulong)(indices.Length * Unsafe.SizeOf<uint>()),
        };
        return Device.CreateBuffer(bufferDesc, indices);
    }

    private unsafe ITexture LoadTexture()
    {
        using var stream = File.OpenRead(Path.Combine(Environment.CurrentDirectory, "Assets/Textures", "diligent-icon.png"));
        var image = ImageResult.FromStream(stream);
        var data = image.Data.AsSpan();

        var textureDesc = new TextureDesc()
        {
            Name = "Cube texture",
            Width = (uint)image.Width,
            Height = (uint)image.Height,
            Type = ResourceDimension.Tex2d,
            BindFlags = BindFlags.ShaderResource,
            Usage = Usage.Immutable,
            Format = TextureFormat.RGBA8_SNorm,
        };

        fixed (void* dataPtr = data)
            return Device.CreateTexture(textureDesc, new TextureData()
            {
                SubResources =
                [
                    new TextureSubResData()
                    {
                        Data = new IntPtr(dataPtr),
                        Stride = (ulong)(image.Width * 4),
                    }
                ]
            });
    }

    private void UpdateTransform(double dt)
    {
        var rotationSpeed = 2.0;
        _angle += (float)(rotationSpeed * dt);
        var transform = Matrix4x4.CreateRotationY(_angle)
                        * Matrix4x4.CreateRotationX((float)Math.Cos(_angle));
        var view = Matrix4x4.CreateTranslation(0, (float)Math.Sin(_angle * 0.5f), -5.0f);

        var wndSize = WindowSize;
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(
            90f * (float)(Math.PI / 180), // 90º of FoV
            wndSize.Width / (float)wndSize.Height,
            0.01f,
            100.0f);

        _worldViewProj = transform * view * proj;
    }

    private unsafe void Render()
    {
        var rtv = SwapChain.GetCurrentBackBufferRTV();
        var dsv = SwapChain.GetDepthBufferDSV();

        Vector4 clearColor = new Vector4( .350f, .350f, .350f, 1.0f );

        ImmediateContext.SetRenderTargets([rtv], dsv, ResourceStateTransitionMode.Transition);
        ImmediateContext.ClearRenderTarget(rtv, clearColor, ResourceStateTransitionMode.Transition);
        ImmediateContext.ClearDepthStencil(dsv,
            ClearDepthStencilFlags.Depth,
            1.0f, 0,
            ResourceStateTransitionMode.Transition);

        var uniformBuffer = _vertexShaderConstants ?? throw new NullReferenceException();
        var mapPtr = ImmediateContext.MapBuffer(uniformBuffer, MapType.Write, MapFlags.Discard);
        Unsafe.Copy(mapPtr.ToPointer(), ref _worldViewProj);

        ImmediateContext.UnmapBuffer(uniformBuffer, MapType.Write);

        ImmediateContext.SetVertexBuffers(0,
            [_vertexBuffer ?? throw new NullReferenceException()],
            [0],
            ResourceStateTransitionMode.Transition,
            SetVertexBuffersFlags.Reset);
        ImmediateContext.SetIndexBuffer(_indexBuffer ?? throw new NullReferenceException(), 0,
            ResourceStateTransitionMode.Transition);

        ImmediateContext.SetPipelineState(_pipelineState ?? throw new NullReferenceException());
        ImmediateContext.CommitShaderResources(_srb ?? throw new NullReferenceException(),
            ResourceStateTransitionMode.Transition);

        var drawAttribs = new DrawIndexedAttribs()
        {
            IndexType = ValueType.UInt32,
            NumIndices = 36,
            Flags = DrawFlags.VerifyAll
        };
        ImmediateContext.DrawIndexed(drawAttribs);
    }

    protected override void OnSetup()
    {
        _vertexBuffer = CreateVertexBuffer();
        _indexBuffer = CreateIndexBuffer();
        using var texture = LoadTexture();

        _vertexShaderConstants = CreateUniformBuffer(Unsafe.SizeOf<Matrix4x4>(), "VS constants CB");

        _pipelineState = CreatePipelineState();
        _pipelineState.GetStaticVariableByName(ShaderType.Vertex, "Constants")?.Set(_vertexShaderConstants, SetShaderResourceFlags.AllowOverwrite);

        _srb = _pipelineState.CreateShaderResourceBinding(true);
        _srb.GetVariableByName(ShaderType.Pixel, "g_texture")?.Set(texture.GetDefaultView(TextureViewType.ShaderResource), SetShaderResourceFlags.AllowOverwrite);
    }

    protected override void OnUpdate(double dt)
    {
        UpdateTransform(dt);
        Render();
        //RenderUI(Math.Max(dt, 0.0000001f));
    }

    protected override void OnPresent()
    {
        SwapChain.Present(1);
    }

    protected override void OnExit()
    {
        var disposableList = new List<IDisposable?>()
        {
            _vertexBuffer,
            _indexBuffer,
            _vertexShaderConstants,
            _srb,
            _pipelineState
        };
        disposableList.ForEach(disposable => disposable?.Dispose());
    }
}