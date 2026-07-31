using DecaEngine.Core;
using DecaEngine.Graphics.Core;
using Diligent;

namespace DecaEngine.Graphics.Diligent;

public class DiligentRenderGraphBuilder : IRenderGraphBuilder
{
	public RenderGraphNode<ITextureView, TextureViewDesc, TextureDesc, ITexture> renderContainer { get; }
	public RenderGraphNode<IBufferView, BufferViewDesc, BufferDesc, IBuffer> bufferContainer { get; }

	private readonly Dictionary<string, TextureInfo> _textureInfos = new();
	private readonly Dictionary<string, BufferInfo> _bufferInfos = new();

	private readonly DiligentGraphicsApi _graphicsApi;

	public DiligentRenderGraphBuilder(DiligentGraphicsApi graphicsApi)
	{
		_graphicsApi = graphicsApi;

		renderContainer = new RenderGraphNode<ITextureView, TextureViewDesc, TextureDesc, ITexture>(
			FindTextureDescIndex, GetTextureViewDescName, GetTextureDescName, CreateTarget, CreateView
#if DEBUG
			, GetTextureDescSizeInBytes
#endif
			);
		bufferContainer = new RenderGraphNode<IBufferView, BufferViewDesc, BufferDesc, IBuffer>(
			FindBufferDescIndex, GetBufferViewDescName, GetBufferDescName, CreateBuffer, CreateBufferView
#if DEBUG
			, GetBufferDescSizeInBytes
#endif
			);
	}

#if DEBUG
	private static ulong GetTextureDescSizeInBytes(TextureDesc desc)
	{
		// Rough VRAM footprint estimate: width * height * depth/array * approx bytes-per-pixel * mip count.
		// Good enough for debug display purposes, not meant to be byte-exact.
		var bpp = DiligentResourceFormats.GetApproxBytesPerPixel(desc.Format);
		ulong texels = (ulong)desc.Width * desc.Height * Math.Max(1u, desc.ArraySizeOrDepth);
		return texels * (ulong)bpp * Math.Max(1u, desc.MipLevels);
	}

	private static ulong GetBufferDescSizeInBytes(BufferDesc desc)
	{
		return desc.Size;
	}
#endif

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
		_textureInfos.Clear();
		_bufferInfos.Clear();
	}

	public void PostSetup()
	{
		for (int i = 0; i < renderContainer.RenderTargetsDesc.Count; i++)
		{
			var desc = renderContainer.RenderTargetsDesc[i];
			if (renderContainer.ReadViewsDesc.Any(view => view.Name == desc.Name))
			{
				desc.BindFlags |= BindFlags.ShaderResource;
				renderContainer.RenderTargetsDesc[i] = desc;
			}
		}

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
		var targetDesc = renderContainer.RenderTargetsDesc.FirstOrDefault(desc => desc.Name == textureResource.Id.name);
		if (targetDesc.Name == null)
		{
			throw new InvalidOperationException($"Texture '{textureResource.Id.name}' must be pinned before it is written.");
		}

		var bindId = renderContainer.WriteViewsDesc.Count;
		renderContainer.WriteViewsDesc.Add(new TextureViewDesc
		{
			AccessFlags = UavAccessFlag.FlagWrite,
			ViewType = (targetDesc.BindFlags & BindFlags.DepthStencil) != 0
				? TextureViewType.DepthStencil
				: TextureViewType.RenderTarget,
			Name = textureResource.Id.name,
			NumMipLevels = 1,
			TextureDim = targetDesc.Type
		});

		return new TextureResource(textureResource.Id, bindId);
	}

	public TextureResource ReadTarget(TextureResource textureResource)
	{
		var targetDesc = renderContainer.RenderTargetsDesc.FirstOrDefault(desc => desc.Name == textureResource.Id.name);
		if (targetDesc.Name == null)
		{
			throw new InvalidOperationException($"Texture '{textureResource.Id.name}' must be pinned before it is read.");
		}

		var bindId = renderContainer.ReadViewsDesc.Count;
		renderContainer.ReadViewsDesc.Add(new TextureViewDesc
		{
			AccessFlags = UavAccessFlag.FlagRead,
			ViewType = TextureViewType.ShaderResource,
			Name = textureResource.Id.name,
			NumMipLevels = 1,
			TextureDim = targetDesc.Type
		});

		return new TextureResource(textureResource.Id, bindId);
	}

	public BufferResource WriteBuffer(BufferResource bufferResource)
	{
		bufferContainer.WriteViewsDesc.Add(new BufferViewDesc()
		{
			Name = bufferResource.Id.name,
			ByteOffset = 0,
			ByteWidth = 0,
			ViewType = BufferViewType.UnorderedAccess
		});

		return bufferResource;
	}

	public BufferResource ReadBuffer(BufferResource bufferResource)
	{
		bufferContainer.ReadViewsDesc.Add(new BufferViewDesc
		{
			Name = bufferResource.Id.name,
			ByteOffset = 0,
			ByteWidth = 0,
			ViewType = BufferViewType.ShaderResource
		});

		return bufferResource;
	}

	public TextureResource PinTexture(TextureInfo info)
	{
		var (dilFormat, bindFlags) = DiligentResourceFormats.ToRenderTargetFormat(info.format);

		// Unlike a persistent IRenderTarget, the graph only knows whether a color target will be
		// sampled *after* every pass has been set up (see PostSetup), so ShaderResource is added
		// lazily for color formats. Depth formats always keep it since they are commonly sampled
		// for shadow mapping etc. and DepthStencil-only textures are otherwise rare in this engine.
		if ((bindFlags & BindFlags.DepthStencil) == 0)
		{
			bindFlags &= ~BindFlags.ShaderResource;
		}

		var desc = new TextureDesc
		{
			Name = info.name,
			Type = info.arraySize > 1 ? ResourceDimension.Tex2dArray : ResourceDimension.Tex2d,
			Width = info.width,
			Height = info.height,
			ArraySizeOrDepth = Math.Max(1u, info.arraySize),
			Format = dilFormat,
			BindFlags = bindFlags,
			Usage = Usage.Default,
			CPUAccessFlags = CpuAccessFlags.None,
			MipLevels = 1,
		};

		var existingIndex = renderContainer.RenderTargetsDesc.FindIndex(existing => existing.Name == info.name);
		if (existingIndex >= 0)
		{
			var existing = renderContainer.RenderTargetsDesc[existingIndex];
			if (existing.Width != desc.Width ||
			    existing.Height != desc.Height ||
			    existing.ArraySizeOrDepth != desc.ArraySizeOrDepth ||
			    existing.Format != desc.Format)
			{
				throw new InvalidOperationException($"Texture '{info.name}' was pinned more than once with incompatible descriptions.");
			}

			return new TextureResource(info.name, -1);
		}

		renderContainer.RenderTargetsDesc.Add(desc);
		info.arraySize = desc.ArraySizeOrDepth;
		info.type = info.arraySize > 1 ? TextureType.Texture2DArray : TextureType.Texture2D;
		_textureInfos[info.name] = info;
		return new TextureResource(info.name, -1);
	}

	public BufferResource PinBuffer(BufferInfo info)
	{
		var bindFlags = DiligentResourceFormats.ToBufferBindFlags(info.type, info.access);

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
			desc.ElementByteStride = info.stride;
		}

		var existingIndex = bufferContainer.RenderTargetsDesc.FindIndex(existing => existing.Name == info.name);
		if (existingIndex >= 0)
		{
			var existing = bufferContainer.RenderTargetsDesc[existingIndex];
			if (existing.Size != desc.Size ||
			    existing.BindFlags != desc.BindFlags ||
			    existing.Mode != desc.Mode ||
			    existing.ElementByteStride != desc.ElementByteStride)
			{
				throw new InvalidOperationException(
					$"Buffer '{info.name}' was pinned more than once with incompatible descriptions.");
			}

			return new BufferResource(info.name);
		}

		bufferContainer.RenderTargetsDesc.Add(desc);
		_bufferInfos[info.name] = info;
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

	/// <summary>
	/// Returns the native texture allocated for a pinned resource, so it can be wrapped as an
	/// <see cref="IGpuTexture"/> (see <see cref="DiligentRenderGraphContext.GetTexture"/>) and bound
	/// into materials/command buffer calls the same way any other engine texture is.
	/// Must be called after <see cref="Allocate"/> ran for the owning pass (i.e. from a pass's Execute).
	/// </summary>
	public ITexture GetTexture(GraphId id) => renderContainer.GetTarget(id.name);

	/// <summary>
	/// Returns the native buffer allocated for a pinned resource, so it can be wrapped as an
	/// <see cref="IBufferHandle"/> (see <see cref="DiligentRenderGraphContext.GetBuffer"/>).
	/// Must be called after <see cref="Allocate"/> ran for the owning pass (i.e. from a pass's Execute).
	/// </summary>
	public IBuffer GetBuffer(GraphId id) => bufferContainer.GetTarget(id.name);

	public TextureInfo GetTextureInfo(GraphId id)
	{
		if (!_textureInfos.TryGetValue(id.name, out var info))
		{
			throw new InvalidOperationException($"Texture '{id.name}' was never pinned.");
		}

		return info;
	}

	public BufferInfo GetBufferInfo(GraphId id)
	{
		if (!_bufferInfos.TryGetValue(id.name, out var info))
		{
			throw new InvalidOperationException($"Buffer '{id.name}' was never pinned.");
		}

		return info;
	}

	private ITextureView CreateView(ITexture arg1, TextureViewDesc arg2)
	{
		return arg1.CreateView(arg2);
	}

	private ITexture CreateTarget(TextureDesc arg)
	{
		return _graphicsApi.Device.CreateTexture(arg);
	}

	private IBufferView CreateBufferView(IBuffer arg1, BufferViewDesc arg2)
	{
		return arg1.CreateView(arg2);
	}

	private IBuffer CreateBuffer(BufferDesc arg)
	{
		return _graphicsApi.Device.CreateBuffer(arg);
	}

#if DEBUG
	/// <summary>
	/// Debug-only export of resource lifetime info for every pinned texture and buffer.
	/// Must be called after <see cref="PostSetup"/> and all <see cref="SetupPass"/> calls.
	/// </summary>
	public List<ResourceDebugInfo> ExportResourceDebugInfo()
	{
		var list = new List<ResourceDebugInfo>();
		list.AddRange(renderContainer.ExportLifetimes(isBuffer: false));
		list.AddRange(bufferContainer.ExportLifetimes(isBuffer: true));
		return list;
	}

	/// <summary>Debug-only: names of all resources (textures + buffers) read/written by a given pass.</summary>
	public (string[] reads, string[] writes) GetPassResourceNames(int pass)
	{
		var reads = renderContainer.GetPassReadNames(pass).Concat(bufferContainer.GetPassReadNames(pass)).ToArray();
		var writes = renderContainer.GetPassWriteNames(pass).Concat(bufferContainer.GetPassWriteNames(pass)).ToArray();
		return (reads, writes);
	}
#endif
}