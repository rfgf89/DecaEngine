using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DecaEngine.Core;
using DecaEngine.Graphics.Diligent;
using Diligent;
using Hexa.NET.ImGui;
// Renders through the native context rather than ICommandBuffer, so the flags are Diligent's.
using SetVertexBuffersFlags = Diligent.SetVertexBuffersFlags;
using TextureFormat = Diligent.TextureFormat;
using ValueType = Diligent.ValueType;
using DecaEngine.Graphics;

public class ImGuiDiligentRender : ImGuiRender
{
	private readonly Dictionary<ImTextureID, (ITexture, IShaderResourceBinding, IShaderResourceVariable)> _textures = new ();

	private IBuffer _projMatrixBuffer;
	private IBuffer _vertexBuffer;
	private IBuffer _indexBuffer;

	private IPipelineState _pipelineState;

	private IEngineFactory _engineFactory;
	private ISwapChain _swapChain;
	private IDeviceContext _deviceContext;
	private IRenderDevice _device;

	private IRenderHandle _backBufferTexture;
	private Func<ITextureView> _backBufferTextureView;
	private Func<ITextureView?> _backBufferTextureViewDepthStencil;
	private bool isSwapChainOutput;

	private static int _ids = 1;

	private readonly DiligentGraphicsApi _graphicsApi;

	public ImGuiDiligentRender(DiligentGraphicsApi graphicsApi) : base(graphicsApi)
	{
		_graphicsApi = graphicsApi;
	}

	public override void Initialize(DevicePull devicePull)
	{
		_engineFactory = _graphicsApi.EngineFactory;
		_swapChain = _graphicsApi.SwapChain;
		_deviceContext = _graphicsApi.ImmediateContext;
		_device = _graphicsApi.Device;

		BindBackTarget(null);

		base.Initialize(devicePull);
	}

	private void GetTextureBinding(ITexture texture, out IShaderResourceBinding shaderResourceBinding, out IShaderResourceVariable shaderResourceTexture)
	{
		shaderResourceBinding = _pipelineState.CreateShaderResourceBinding(true);
		shaderResourceTexture = shaderResourceBinding.GetVariableByName(ShaderType.Pixel, "FontTexture");
		shaderResourceTexture.Set(texture.GetDefaultView(TextureViewType.ShaderResource), SetShaderResourceFlags.AllowOverwrite);
	}

	public override void BindBackTarget(IRenderHandle? renderHandle)
	{
		if (renderHandle == null)
		{
			_backBufferTextureView = _swapChain.GetCurrentBackBufferRTV;
			_backBufferTextureViewDepthStencil = _swapChain.GetDepthBufferDSV;
			isSwapChainOutput = true;
			return;
		}

		if (renderHandle is not DiligentRenderHandle dilHandle)
		{
			throw new InvalidOperationException("Invalid render handle type");
		}

		_backBufferTexture = dilHandle;

		_backBufferTextureView = () => dilHandle.RTV;
		_backBufferTextureViewDepthStencil = () => null;
		isSwapChainOutput = false;
	}

	/// <summary>Releases the cached SRB for a texture id; the texture itself is not disposed.</summary>
	// Must be called BEFORE the underlying texture is disposed or resized: releasing an SRB whose
	// view is already gone touches freed native memory.
	public override void ReleaseRenderTargetBinding(ImTextureID textureId)
	{
		if (_textures.Remove(textureId, out var previous))
		{
			// Only the SRB: the IShaderResourceVariable is a query into its table, not ref-counted,
			// and disposing it too double-releases the same native pointer.
			previous.Item2.Dispose();
		}
	}

	public override void BindRenderTarget(ImTextureID textureId, IRenderHandle renderHandle)
	{
		if (renderHandle is not DiligentRenderHandle dilHandle)
		{
			throw new InvalidOperationException("Invalid render handle type");
		}

		ReleaseRenderTargetBinding(textureId);
		GetTextureBinding(dilHandle.Texture, out var shaderResourceBinding, out var shaderResourceTexture);
		_textures[textureId] = (dilHandle.Texture, shaderResourceBinding, shaderResourceTexture);
	}

	public override void BindRenderTarget(ImTextureID textureId, IGpuTexture texture)
	{
		ITexture native = texture switch
		{
			DiligentRenderTarget dilRenderTarget => dilRenderTarget.Texture,
			DiligentGpuTexture dilGpuTexture => dilGpuTexture.Texture,
			_ => throw new InvalidOperationException("Unsupported texture type '" + texture.GetType() + "' for ImGui binding")
		};

		ReleaseRenderTargetBinding(textureId);
		GetTextureBinding(native, out var shaderResourceBinding, out var shaderResourceTexture);
		_textures[textureId] = (native, shaderResourceBinding, shaderResourceTexture);
	}

	public override unsafe ImTextureRef GetNewTexture()
	{
		ImTextureID textureId = new ImTextureID(_ids++);
		return new ImTextureRef(null, textureId);
	}

	public override void GarbageTexture(ImTextureRef textureRef)
	{
		if (_textures.Remove(textureRef.TexID, out (ITexture Texture, IShaderResourceBinding ShaderResourceBinding, IShaderResourceVariable ShaderResourceVariable) texture))
		{
			texture.Texture.Dispose();
			texture.ShaderResourceBinding.Dispose();
			texture.ShaderResourceVariable.Dispose();
		}
	}

	private IShader CreateShader(string shaderName, ShaderType shaderType)
	{
		using var shaderSourceFactory = _engineFactory.CreateDefaultShaderSourceStreamFactory(Path.Combine(Environment.CurrentDirectory, "Assets"));
		var shaderCi = new ShaderCreateInfo
		{
			SourceLanguage = ShaderSourceLanguage.Hlsl,
			Desc = new ShaderDesc
			{
				Name = $"Font {shaderType}",
				UseCombinedTextureSamplers = true,
				ShaderType = shaderType,
			},
			EntryPoint = "main",
			CompileFlags = ShaderCompileFlags.PackMatrixRowMajor,
			FilePath = $"Shaders/{shaderName}.hlsl",
			ShaderSourceStreamFactory = shaderSourceFactory,
		};

		return _device.CreateShader(shaderCi, out var blob);
	}

	protected override void CreateDeviceResources()
	{
		var verticesBufferDesc = new BufferDesc
		{
			BindFlags = BindFlags.VertexBuffer,
			Size = 1024 * 64, // Start with a reasonable size
			Usage = Usage.Dynamic,
			CPUAccessFlags = CpuAccessFlags.Write,
			Name = "ImGui Vertex Buffer",
		};

		_vertexBuffer = _device.CreateBuffer(verticesBufferDesc);

		var indexBufferDesc = new BufferDesc
		{
			BindFlags = BindFlags.IndexBuffer,
			Size = 1024 * 64, // Start with a reasonable size
			Usage = Usage.Dynamic,
			CPUAccessFlags = CpuAccessFlags.Write,
			Name = "ImGui Index Buffer"
		};

		_indexBuffer = _device.CreateBuffer(indexBufferDesc);

		var projMatrixBufferDesc = new BufferDesc
		{
			BindFlags = BindFlags.UniformBuffer,
			Size = 64,
			Usage = Usage.Dynamic,
			CPUAccessFlags = CpuAccessFlags.Write,
			Name = "ImGui Projection Buffer"
		};

		_projMatrixBuffer = _device.CreateBuffer(projMatrixBufferDesc);

		_pipelineState = CreatePipelineState();

		_pipelineState.GetStaticVariableByName(ShaderType.Vertex, "Constants").Set(_projMatrixBuffer, SetShaderResourceFlags.AllowOverwrite);
	}

	protected override unsafe void RenderImDrawData(ImDrawDataPtr drawData)
	{
		if (drawData.CmdListsCount == 0)
		{
			return;
		}

		var rtv = _backBufferTextureView();
		var dsv = _backBufferTextureViewDepthStencil();
		_deviceContext.SetRenderTargets([rtv], dsv, ResourceStateTransitionMode.Transition);
		_deviceContext.SetPipelineState(_pipelineState);

		var totalVbSize = (uint)(drawData.TotalVtxCount * Unsafe.SizeOf<ImDrawVert>());
		if (totalVbSize > _vertexBuffer.GetDesc().Size)
		{
			var vertBufferDesc = new BufferDesc
			{
				BindFlags = BindFlags.VertexBuffer,
				Size = (ulong)(totalVbSize * 1.5f),
				Usage = Usage.Dynamic,
				CPUAccessFlags = CpuAccessFlags.Write,
				Name = "ImGui Vertex Buffer",
			};

			_vertexBuffer.Dispose();
			_vertexBuffer = _device.CreateBuffer(vertBufferDesc);
		}

		var totalIbSize = (uint)(drawData.TotalIdxCount * sizeof(ushort));
		if (totalIbSize > _indexBuffer.GetDesc().Size)
		{
			var indexBufferDesc = new BufferDesc
			{
				BindFlags = BindFlags.IndexBuffer,
				Size = (ulong)(totalIbSize * 1.5f),
				Usage = Usage.Dynamic,
				CPUAccessFlags = CpuAccessFlags.Write,
				Name = "ImGui Index Buffer"
			};

			_indexBuffer.Dispose();
			_indexBuffer = _device.CreateBuffer(indexBufferDesc);
		}

		// Map buffers once per frame and copy all command list data
		IntPtr vbPtr = _deviceContext.MapBuffer(_vertexBuffer, MapType.Write, MapFlags.Discard);
		IntPtr ibPtr = _deviceContext.MapBuffer(_indexBuffer, MapType.Write, MapFlags.Discard);

		uint currentVtxOffset = 0;
		uint currentIdxOffset = 0;

		for (var n = 0; n < drawData.CmdListsCount; n++)
		{
			var cmdList = drawData.CmdLists[n];

			Unsafe.CopyBlock((byte*)vbPtr.ToPointer() + currentVtxOffset * (uint)Unsafe.SizeOf<ImDrawVert>(), cmdList.VtxBuffer.Data, (uint)(cmdList.VtxBuffer.Size * Unsafe.SizeOf<ImDrawVert>()));
			Unsafe.CopyBlock((byte*)ibPtr.ToPointer() + currentIdxOffset * sizeof(ushort), cmdList.IdxBuffer.Data, (uint)(cmdList.IdxBuffer.Size * sizeof(ushort)));

			currentVtxOffset += (uint)cmdList.VtxBuffer.Size;
			currentIdxOffset += (uint)cmdList.IdxBuffer.Size;
		}

		_deviceContext.UnmapBuffer(_vertexBuffer, MapType.Write);
		_deviceContext.UnmapBuffer(_indexBuffer, MapType.Write);

		// Bind buffers once after data is uploaded
		_deviceContext.SetVertexBuffers(0, [_vertexBuffer], [0], ResourceStateTransitionMode.Transition, SetVertexBuffersFlags.Reset);
		_deviceContext.SetIndexBuffer(_indexBuffer, 0, ResourceStateTransitionMode.Transition);

		var io = ImGui.GetIO();
		var mvp = Matrix4x4.CreateOrthographicOffCenter(
			0f,
			io.DisplaySize.X,
			io.DisplaySize.Y,
			0.0f,
			-1.0f,
			1.0f);

		var mapPtr = _deviceContext.MapBuffer(_projMatrixBuffer, MapType.Write, MapFlags.Discard);
		Unsafe.Copy(mapPtr.ToPointer(), ref mvp);
		_deviceContext.UnmapBuffer(_projMatrixBuffer, MapType.Write);

		drawData.ScaleClipRects(io.DisplayFramebufferScale);

		ImTextureID? lastTexId = null;

		uint globalVtxOffset = 0;
		uint globalIdxOffset = 0;

		for (var n = 0; n < drawData.CmdListsCount; n++)
		{
			var cmdList = drawData.CmdLists[n];

			for (var cmdI = 0; cmdI < cmdList.CmdBuffer.Size; cmdI++)
			{
				var cmd = cmdList.CmdBuffer[cmdI];

				if (cmd.ElemCount == 0)
				{
					continue;
				}

				ImTextureRef textureRef = cmd.TexRef;
				ImTextureID texId = textureRef.GetTexID();

				if (lastTexId == null || lastTexId.Value.Handle != texId.Handle)
				{
					if (_textures.TryGetValue(texId, out var texture))
					{
						_deviceContext.CommitShaderResources(texture.Item2, ResourceStateTransitionMode.Transition);
						lastTexId = texId;
					}
					else
					{
						// Texture not registered yet. Skip the draw: without a rebind the GPU still has
						// the previously committed SRB bound and would stamp an unrelated texture here.
						lastTexId = null;
						continue;
					}
				}

				var rect = new Rect
				{
					Left = (int)cmd.ClipRect.X,
					Top = (int)cmd.ClipRect.Y,
					Right = (int)cmd.ClipRect.Z,
					Bottom = (int)cmd.ClipRect.W
				};

				_deviceContext.SetScissorRects([rect], 0, 0);

				var drawIndexedAttribs = new DrawIndexedAttribs
				{
					IndexType = ValueType.UInt16,
					BaseVertex = (uint)(globalVtxOffset + cmd.VtxOffset),
					FirstIndexLocation = (uint)(globalIdxOffset + cmd.IdxOffset),
					NumIndices = cmd.ElemCount,
					NumInstances = 1,
					Flags = DrawFlags.VerifyAll,
				};

				_deviceContext.DrawIndexed(drawIndexedAttribs);
			}

			globalVtxOffset += (uint)cmdList.VtxBuffer.Size;
			globalIdxOffset += (uint)cmdList.IdxBuffer.Size;
		}
	}

	protected override unsafe void CreateTexture(ImTextureDataPtr textureData)
	{
		if (!textureData.IsNull && textureData.Pixels != null)
		{
			int bytesPerPixel = textureData.Format == ImTextureFormat.Rgba32 ? 4 : 1;


			var textureDesc = new TextureDesc
			{
				Name = "TextureSampler " + textureData.UniqueID,
				Width = (uint)textureData.Width,
				Height = (uint)textureData.Height,
				Type = ResourceDimension.Tex2d,
				BindFlags = BindFlags.ShaderResource,
				Usage = Usage.Dynamic,
				CPUAccessFlags = CpuAccessFlags.Write,
				MipLevels = 1,
				Format = textureData.Format == ImTextureFormat.Rgba32
					? TextureFormat.RGBA8_UNorm_sRGB
					: TextureFormat.A8_UNorm,
			};

			var texture = _device.CreateTexture(textureDesc, new TextureData
			{
				SubResources =
				[
					new TextureSubResData()
					{
						Data = new IntPtr(textureData.Pixels),
						Stride = (ulong)(textureDesc.Width * bytesPerPixel),
					}
				]
			});

			GetTextureBinding(texture, out var shaderResourceBinding, out var shaderResourceTexture);
			_textures[textureData.TexID] = (texture, shaderResourceBinding, shaderResourceTexture);
		}

		textureData.SetStatus(ImTextureStatus.Ok);
	}

	private unsafe IPipelineState CreatePipelineState()
	{
		using var pixelShader = CreateShader("ImGuiFrag", ShaderType.Pixel);
		using var vertexShader = CreateShader("ImGuiVertex", ShaderType.Vertex);

		var rtvFormat = _backBufferTextureView().GetDesc().Format;
		var dsvFormat = _backBufferTextureViewDepthStencil()?.GetDesc().Format ?? TextureFormat.Unknown;

		var pipelineCreateInfo = new GraphicsPipelineStateCreateInfo
		{
			PSODesc = new PipelineStateDesc
			{
				Name = "Imgui PSO",
				PipelineType = PipelineType.Graphics,
				ResourceLayout = new PipelineResourceLayoutDesc
				{
					DefaultVariableMergeStages = ShaderType.Pixel,
					DefaultVariableType = ShaderResourceVariableType.Static,
					Variables =
					[
						new ShaderResourceVariableDesc
						{
							ShaderStages = ShaderType.Pixel,
							Name = "FontTexture",
							Type = ShaderResourceVariableType.Mutable,
						}
					],
					ImmutableSamplers =
					[
						new ImmutableSamplerDesc
						{
							ShaderStages = ShaderType.Pixel,
							SamplerOrTextureName = "FontTexture",
							Desc = new SamplerDesc
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
			GraphicsPipeline = new GraphicsPipelineDesc
			{
				DepthStencilDesc = new DepthStencilStateDesc()
				{
					DepthEnable = false,
					DepthWriteEnable = false,
					StencilEnable = false,
				},
				NumRenderTargets = 1,
				RTVFormats = [rtvFormat],
				DSVFormat = dsvFormat,
				PrimitiveTopology = PrimitiveTopology.TriangleList,
				BlendDesc = new BlendStateDesc()
				{
					RenderTargets =
					[
						new RenderTargetBlendDesc()
						{
							BlendEnable = true,
							BlendOp = BlendOperation.Add,
							BlendOpAlpha = BlendOperation.Add,
							DestBlend = BlendFactor.InvSrcAlpha,
							SrcBlend = BlendFactor.SrcAlpha,
							DestBlendAlpha = BlendFactor.One,
							SrcBlendAlpha = BlendFactor.SrcAlpha,
							RenderTargetWriteMask = ColorMask.All
						}
					]
				},
				RasterizerDesc = new RasterizerStateDesc
				{
					CullMode = CullMode.None,
					FillMode = FillMode.Solid,
					ScissorEnable = true,
				},
				InputLayout = new InputLayoutDesc
				{
					LayoutElements =
					[
						new LayoutElement { InputIndex = 0, NumComponents = 2, ValueType = ValueType.Float32, IsNormalized = false,
							BufferSlot = 0, Frequency = InputElementFrequency.PerVertex
						},
						new LayoutElement { InputIndex = 1, NumComponents = 2, ValueType = ValueType.Float32, IsNormalized = false,
							BufferSlot = 0, Frequency = InputElementFrequency.PerVertex
						},
						new LayoutElement { InputIndex = 2, NumComponents = 1, ValueType = ValueType.UInt32, IsNormalized = false,
							BufferSlot = 0, Frequency = InputElementFrequency.PerVertex
						},
					]
				}
			},
			Vs = vertexShader,
			Ps = pixelShader
		};

		return _graphicsApi.PsoManager.CreateGraphicsPipelineState(pipelineCreateInfo);
	}

	protected override void UpdateTextureData(ImTextureDataPtr textureData)
	{
		IntPtr texId = textureData.GetTexID();
		if (!_textures.ContainsKey(texId))
		{
			return;
		}

		CreateTexture(textureData);
	}

	protected override void DestroyTexture(ImTextureDataPtr textureData)
	{
		IntPtr texId = textureData.GetTexID();
		if (!_textures.TryGetValue(texId, out var texture))
		{
			return;
		}

		texture.Item1.Dispose();
		texture.Item2.Dispose();
		texture.Item3.Dispose();
		_textures.Remove(texId);
	}

	protected override void ReleaseDeviceResources()
	{
		_vertexBuffer.Dispose();
		_indexBuffer.Dispose();
		_projMatrixBuffer.Dispose();
		_pipelineState.Dispose();
	}
}