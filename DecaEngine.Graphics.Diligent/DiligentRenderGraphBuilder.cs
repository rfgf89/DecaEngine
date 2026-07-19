using DecaEngine.Core;
using Diligent;

namespace DecaEngine.Graphics.Diligent;

public class DiligentRenderGraphBuilder : IRenderGraphBuilder
{
	public RenderGraphNode<ITextureView, TextureViewDesc, TextureDesc, ITexture> renderContainer { get; }
	public RenderGraphNode<IBufferView, BufferViewDesc, BufferDesc, IBuffer> bufferContainer { get; }

	private readonly DiligentGraphicsPipeline _graphicsPipeline;

	public DiligentRenderGraphBuilder(DiligentGraphicsPipeline graphicsPipeline)
	{
		_graphicsPipeline = graphicsPipeline;

		renderContainer = new RenderGraphNode<ITextureView, TextureViewDesc, TextureDesc, ITexture>(
			FindTextureDescIndex, GetTextureViewDescName, GetTextureDescName, CreateTarget, CreateView);
		bufferContainer = new RenderGraphNode<IBufferView, BufferViewDesc, BufferDesc, IBuffer>(
			FindBufferDescIndex, GetBufferViewDescName, GetBufferDescName, CreateBuffer, CreateBufferView);
	}

	private int FindBufferDescIndex(List<BufferDesc> arg1, BufferViewDesc arg2)
	{
		return arg1.FindIndex(buf => buf.Name == arg2.Name);
	}

	private string GetBufferViewDescName(BufferViewDesc arg)
	{
		return arg.Name;
	}

	private string GetBufferDescName(BufferDesc arg)
	{
		return arg.Name;
	}

	private int FindTextureDescIndex(List<TextureDesc> arg1, TextureViewDesc arg2)
	{
		return arg1.FindIndex(tex => tex.Name == arg2.Name);
	}

	private string GetTextureViewDescName(TextureViewDesc arg)
	{
		return arg.Name;
	}

	private string GetTextureDescName(TextureDesc arg)
	{
		return arg.Name;
	}

	public void Clean()
	{
		renderContainer.Clean();
		bufferContainer.Clean();
	}

	public void PostSetup()
	{
		renderContainer.PostSetup();
		bufferContainer.PostSetup();
	}

	public void SetupPass(int pass)
	{
		renderContainer.SetupPass(pass);
		bufferContainer.SetupPass(pass);
	}

	public TextureResource WriteTarget(TextureResource textureResource)
	{
		renderContainer.WriteViewsDesc.Add(new TextureViewDesc
		{
			AccessFlags = UavAccessFlag.FlagWrite,
			ViewType = TextureViewType.RenderTarget,
			Name = textureResource.Id.name,
			NumMipLevels = 1,
			TextureDim = ResourceDimension.Tex2d
		});

		return textureResource;
	}

	public TextureResource ReadTarget(TextureResource textureResource)
	{
		renderContainer.ReadViewsDesc.Add(new TextureViewDesc
		{
			AccessFlags = UavAccessFlag.FlagRead,
			ViewType = TextureViewType.ShaderResource,
			Name = textureResource.Id.name,
			NumMipLevels = 1,
			TextureDim = ResourceDimension.Tex2d
		});

		return textureResource;
	}

	public BufferResource WriteBuffer(BufferResource bufferResource)
	{
		bufferContainer.ReadViewsDesc.Add(new BufferViewDesc()
		{
			Name = bufferResource.Id.name,
			ByteOffset = 0,
			ByteWidth = 1,
			Format = new BufferFormat()
			{

			},
			ViewType = BufferViewType.ShaderResource
		});

		return bufferResource;
	}

	public BufferResource ReadBuffer(BufferResource bufferResource)
	{
		return bufferResource;
	}

	public TextureResource PinTexture(RenderTargetInfo info)
	{
		var textureResource = new TextureResource(info.name, renderContainer.RenderTargetsDesc.Count);

		var dilFormat = info.textureFormat switch
		{
			RenderTargetInfo.Format.R16G16B16A16_FLOAT => TextureFormat.RGBA16_Float,
			_ => TextureFormat.RGBA8_UNorm
		};

		var desc = new TextureDesc
		{
			Name = info.name,
			Type = ResourceDimension.Tex2d,
			Width = info.width,
			Height = info.height,
			Format = dilFormat,
			BindFlags = BindFlags.RenderTarget,
			Usage = Usage.Default,
			CPUAccessFlags = CpuAccessFlags.None,
			MipLevels = 1,
		};

		renderContainer.RenderTargetsDesc.Add(desc);
		return textureResource;
	}

	public BufferResource PinBuffer(BufferInfo info)
	{
		var bindFlags = info.type switch
		{
			BufferHandleType.Constant => BindFlags.UniformBuffer,
			BufferHandleType.Vertex => BindFlags.VertexBuffer,
			BufferHandleType.Index => BindFlags.IndexBuffer,
			BufferHandleType.Structured => BindFlags.ShaderResource | BindFlags.UnorderedAccess,
			_ => BindFlags.None
		};

		var usage = info.dynamic ? Usage.Dynamic : Usage.Default;
		var cpuAccessFlags = info.dynamic ? CpuAccessFlags.Write : CpuAccessFlags.None;

		var desc = new BufferDesc
		{
			Name = info.name,
			Size = info.sizeInBytes,
			BindFlags = bindFlags,
			Usage = usage,
			CPUAccessFlags = cpuAccessFlags
		};

		if (info.type == BufferHandleType.Structured)
		{
			desc.Mode = BufferMode.Structured;
		}

		bufferContainer.RenderTargetsDesc.Add(desc);
		return new BufferResource(info.name);
	}

	public void Allocate(int pass)
	{
		renderContainer.Allocate(pass);
		bufferContainer.Allocate(pass);
	}

	public void Release(int pass)
	{
		renderContainer.Release(pass);
		bufferContainer.Release(pass);
	}

	private ITextureView CreateView(ITexture arg1, TextureViewDesc arg2)
	{
		return arg1.CreateView(arg2);
	}

	private ITexture CreateTarget(TextureDesc arg)
	{
		return _graphicsPipeline.Device.CreateTexture(arg);
	}

	private IBufferView CreateBufferView(IBuffer arg1, BufferViewDesc arg2)
	{
		return arg1.CreateView(arg2);
	}

	private IBuffer CreateBuffer(BufferDesc arg)
	{
		return _graphicsPipeline.Device.CreateBuffer(arg);
	}
}