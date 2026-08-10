using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DecaEngine;
using DecaEngine.Core;
using Diligent;
using DiligentEngineNET.Samples.Utils;
// Сэмпл работает с нативным контекстом напрямую - состояния Diligent-овские, не из ICommandBuffer.
using ClearDepthStencilFlags = Diligent.ClearDepthStencilFlags;
using ResourceState = Diligent.ResourceState;
using SetVertexBuffersFlags = Diligent.SetVertexBuffersFlags;
using StbImageSharp;
using ValueType = Diligent.ValueType;

namespace DiligentEngineNET.Samples;

public class MultiThreadSample(GraphicsBackend backend, uint gridSize) : Application(backend)
{
    private struct Vertex(Vector3 pos, Vector2 uv)
    {
        public Vector3 Pos = pos;
        public Vector2 Uv = uv;
    }

    private struct UniformBuffer
    {
        public Matrix4x4 ViewProjectionMatrix;
        public Matrix4x4 Rotation;
    }

    private float _angle;
    private UniformBuffer _uniformBufferData;
    private Matrix4x4[] _instanceTransforms = new Matrix4x4[gridSize * gridSize * gridSize];

    private IBuffer? _vertexBuffer;
    private IBuffer? _indexBuffer;
    private IBuffer? _vertexShaderConstants;
    private IBuffer? _instancingUniformBuffer;
    private IPipelineState? _pipelineState;
    private IShaderResourceBinding? _srb;

    private CancellationTokenSource _cancellationTokenSource = new();

    private StateTransitionDesc[] _barriers = [];
    private List<Thread> _threads = new();
    private ICommandList[] _commandLists = [];
    
    private Synchro? _synchro;

    private IShader CreateShader(ShaderType shaderType)
    {
        using var shaderSourceFactory =
            EngineFactory.CreateDefaultShaderSourceStreamFactory(Path.Combine(Environment.CurrentDirectory, "Assets"));
        var shaderCi = new ShaderCreateInfo()
        {
            SourceLanguage = ShaderSourceLanguage.Hlsl,
            Desc = new ShaderDesc()
            {
                Name = $"Cube MultiThread {shaderType}",
                UseCombinedTextureSamplers = true,
                ShaderType = shaderType,
            },
            EntryPoint = "main",
            CompileFlags = ShaderCompileFlags.PackMatrixRowMajor,
            FilePath = shaderType == ShaderType.Vertex ? "Shaders/CubeMultiThread.hlsl" : "Shaders/CubeTexturePS.hlsl",
            ShaderSourceStreamFactory = shaderSourceFactory,
        };

        return Device.CreateShader(shaderCi, out var blob);
    }

    private IPipelineState CreatePipelineState()
    {
        using var pixelShader = CreateShader(ShaderType.Pixel);
        using var vertexShader = CreateShader(ShaderType.Vertex);

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
                            { InputIndex = 1, NumComponents = 2, ValueType = ValueType.Float32, IsNormalized = false },
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
            2, 0, 1, 2, 3, 0,
            4, 6, 5, 4, 7, 6,
            8, 10, 9, 8, 11, 10,
            12, 14, 13, 12, 15, 14,
            16, 18, 17, 16, 19, 18,
            20, 21, 22, 20, 22, 23
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
        using var stream =
            File.OpenRead(Path.Combine(Environment.CurrentDirectory, "Assets/Textures", "diligent-icon.png"));
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
            Format = TextureFormat.RGBA8_UNorm,
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

    private void PopulateInstanceBuffer()
    {
        var random = new Random();
        var baseScale = 0.6f / gridSize;

        var instanceId = 0;

        for (var x = 0; x < gridSize; ++x)
        {
            for (var y = 0; y < gridSize; ++y)
            {
                for (var z = 0; z < gridSize; ++z)
                {
                    var xOffset = 2.0f * (x + 0.5f + RandomOffset()) / gridSize - 1.0f;
                    var yOffset = 2.0f * (y + 0.5f + RandomOffset()) / gridSize - 1.0f;
                    var zOffset = 2.0f * (z + 0.5f + RandomOffset()) / gridSize - 1.0f;

                    var scale = baseScale * RandomScale();

                    var rotation = Matrix4x4.CreateRotationX(RandomRotation());
                    rotation *= Matrix4x4.CreateRotationY(RandomRotation());
                    rotation *= Matrix4x4.CreateRotationZ(RandomRotation());

                    _instanceTransforms[instanceId++] = rotation * Matrix4x4.CreateScale(scale) *
                                                        Matrix4x4.CreateTranslation(xOffset, yOffset, zOffset);
                }
            }
        }


        return;

        float RandomScale()
        {
            return RandomRange(0.3f, 1.0f);
        }

        float RandomOffset()
        {
            return RandomRange(-.15f, .15f);
        }

        float RandomRotation()
        {
            return RandomRange((float)-Math.PI, (float)Math.PI);
        }

        float RandomRange(float min, float max)
        {
            var val = random.NextDouble();
            return min + ((float)val * max);
        }
    }

    private void UpdateTransform(double dt)
    {
        _angle += 2.0f * (float)dt;

        var wndSize = WindowSize;
        var view = Matrix4x4.CreateRotationX(-1.0f) * Matrix4x4.CreateTranslation(0, 0, -2);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(
            90f * (float)(Math.PI / 180),
            wndSize.Width / (float)wndSize.Height,
            0.01f,
            100.0f);

        _uniformBufferData.ViewProjectionMatrix = view * proj;
        _uniformBufferData.Rotation =
            Matrix4x4.CreateRotationY(_angle * 1.0f) * Matrix4x4.CreateRotationX(_angle * .25f);
    }

    private void StartWorkerThreads(int numThreads)
    {
        _synchro = new Synchro(numThreads, _cancellationTokenSource.Token);
        _commandLists = new ICommandList[numThreads];
        for (var i = 0; i < numThreads; ++i)
        {
            var t = new Thread(WorkerThreadFunc);
            t.Priority = ThreadPriority.Lowest;
            t.Start(i);

            _threads.Add(t);
        }

        Thread.Sleep(500);
    }

    private void StopWorkerThreads()
    {
        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();

        foreach (var t in _threads)
            t.Join();
    }

    int mainThreadId = Thread.CurrentThread.ManagedThreadId;

    private void WorkerThreadFunc(object? obj)
    {
        if (obj is null)
            return;


        var threadIdx = (int)obj;
        var token = _cancellationTokenSource.Token;
        var context = DeferredContexts[threadIdx];
        var synchro = _synchro;
        
        if (synchro is null)
            return;

        try
        {
            while (!token.IsCancellationRequested)
                DoThreadRender();
        }
        catch
        {
            // ignored
        }


        void DoThreadRender()
        {
            synchro.WaitSignal();
            context.Begin(0);

            // Signal that this thread is ready and wait for
            // the next main thread signal
            RenderSubset(context, threadIdx + 1);
            _commandLists[threadIdx] = context.FinishCommandList();
            synchro.WaitSignal();

            if (Thread.CurrentThread.ManagedThreadId == mainThreadId)
            {
                synchro.WaitForThreads();
            }


            context.InvalidateState();
            context.FinishFrame();
        }
    }

    private unsafe void RenderSubset(IDeviceContext context, int subset)
    {
        if (_vertexBuffer is null ||
            _vertexShaderConstants is null ||
            _instancingUniformBuffer is null ||
            _indexBuffer is null ||
            _pipelineState is null ||
            _srb is null)
            return;

        context.SetRenderTargets([SwapChain.GetCurrentBackBufferRTV()], SwapChain.GetDepthBufferDSV(),
            ResourceStateTransitionMode.Verify);

        var mapPtr = context.MapBuffer(_vertexShaderConstants, MapType.Write, MapFlags.Discard);
        Unsafe.Copy(mapPtr.ToPointer(), ref _uniformBufferData);
        context.UnmapBuffer(_vertexShaderConstants, MapType.Write);

        context.SetVertexBuffers(0, [_vertexBuffer], null, ResourceStateTransitionMode.Verify,
            SetVertexBuffersFlags.Reset);
        context.SetIndexBuffer(_indexBuffer, 0, ResourceStateTransitionMode.Verify);
        context.SetPipelineState(_pipelineState);

        var drawAttrs = new DrawIndexedAttribs()
        {
            IndexType = ValueType.UInt32,
            Flags = DrawFlags.VerifyAll,
            NumIndices = 36,
        };

        var numSubsets = _threads.Count + 1;
        var numInstances = _instanceTransforms.Length;
        var subsetSize = numInstances / numSubsets;
        var startInst = subsetSize * subset;
        var endInst = (subset < numSubsets - 1) ? subsetSize * (subset + 1) : numInstances;

        context.CommitShaderResources(_srb, ResourceStateTransitionMode.Verify);

        for (var inst = startInst; inst < endInst; ++inst)
        {
            var currInstData = _instanceTransforms[inst];

            mapPtr = context.MapBuffer(_instancingUniformBuffer, MapType.Write, MapFlags.Discard);
            Unsafe.Copy(mapPtr.ToPointer(), ref currInstData);
            context.UnmapBuffer(_instancingUniformBuffer, MapType.Write);

            context.DrawIndexed(drawAttrs);
        }
    }

    private void Render()
    {
        var rtv = SwapChain.GetCurrentBackBufferRTV();
        var dsv = SwapChain.GetDepthBufferDSV();

        Vector4 clearColor = new Vector4(.350f, .350f, .350f, 1.0f );
        //ImmediateContext.ClearStats();
        ImmediateContext.Begin(0);
        ImmediateContext.SetRenderTargets([rtv], dsv, ResourceStateTransitionMode.Transition);
        ImmediateContext.ClearRenderTarget(rtv, clearColor, ResourceStateTransitionMode.Transition);
        ImmediateContext.ClearDepthStencil(dsv,
            ClearDepthStencilFlags.Depth,
            1.0f, 0,
            ResourceStateTransitionMode.Transition);

        // let workers free to execute, so all threads(including main thread)
        // will render a piece of subset at same time

        _synchro?.Signal();
        RenderSubset(ImmediateContext, 0);
        _synchro?.WaitForThreads();
        ImmediateContext.ExecuteCommandLists(_commandLists);
        _synchro?.Signal();

        foreach (var cmdList in _commandLists)
            cmdList.Dispose();

        ImmediateContext.Flush();
        ImmediateContext.InvalidateState();
        ImmediateContext.FinishFrame();
    }

    protected override void OnSetupEngineCreateInfo(EngineCreateInfo createInfo)
    {
        var currProcessorCount = Environment.ProcessorCount * 0.8f; // run at 80% of cores
        createInfo.NumDeferredContexts = (uint)Math.Max(Math.Floor(currProcessorCount), 4);

        if (createInfo is EngineD3D12CreateInfo d3d12Ci)
        {
            d3d12Ci.DynamicHeapPageSize = 26 << 20;
        }
        else if (createInfo is EngineVkCreateInfo vkCi)
        {
            vkCi.DynamicHeapSize = 26 << 20;
        }
    }

    protected override void OnSetup()
    {
        _vertexBuffer = CreateVertexBuffer();
        _indexBuffer = CreateIndexBuffer();
        _instancingUniformBuffer = CreateUniformBuffer(Unsafe.SizeOf<Matrix4x4>(), "Instance constants CB");
        _vertexShaderConstants = CreateUniformBuffer(Unsafe.SizeOf<Matrix4x4>() * 2, "VS constants CB");

        using var texture = LoadTexture();
        var textureView = texture.GetDefaultView(TextureViewType.ShaderResource);

        _pipelineState = CreatePipelineState();
        _pipelineState.GetStaticVariableByName(ShaderType.Vertex, "Constants")?.Set(_vertexShaderConstants, SetShaderResourceFlags.AllowOverwrite);
        _pipelineState.GetStaticVariableByName(ShaderType.Vertex, "InstanceData")?.Set(_instancingUniformBuffer, SetShaderResourceFlags.AllowOverwrite);

        _srb = _pipelineState.CreateShaderResourceBinding(true);
        _srb.GetVariableByName(ShaderType.Pixel, "g_texture")
            ?.Set(textureView, SetShaderResourceFlags.AllowOverwrite);

        _barriers =
        [
            new StateTransitionDesc()
            {
                Resource = texture,
                OldState = ResourceState.Unknown,
                NewState = ResourceState.ShaderResource,
                Flags = StateTransitionFlags.UpdateState,
            },
            new StateTransitionDesc()
            {
                Resource = _vertexBuffer,
                OldState = ResourceState.Unknown,
                NewState = ResourceState.VertexBuffer,
                Flags = StateTransitionFlags.UpdateState,
            },
            new StateTransitionDesc()
            {
                Resource = _indexBuffer,
                OldState = ResourceState.Unknown,
                NewState = ResourceState.IndexBuffer,
                Flags = StateTransitionFlags.UpdateState,
            }
        ];

        ImmediateContext.TransitionResourceStates(_barriers);
        PopulateInstanceBuffer();
        StartWorkerThreads(DeferredContexts.Length);
    }

    protected override void OnUpdate(double dt)
    {
        UpdateTransform(dt);
        Render();
    }

    protected override void OnPresent()
    {
        SwapChain.Present(1);
    }

    protected override void OnExit()
    {
        StopWorkerThreads();
        
        foreach (var cmdList in _commandLists)
        {
            if(cmdList.NativePointer != IntPtr.Zero)
                continue;
            cmdList.Dispose();
        }
        
        var disposableList = new List<IDisposable?>()
        {
            _vertexBuffer,
            _indexBuffer,
            _vertexShaderConstants,
            _instancingUniformBuffer,
            _srb,
            _pipelineState,
        };
        disposableList.ForEach(disposable => disposable?.Dispose());
    }
}