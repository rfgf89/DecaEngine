using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Graphics.Core;
using DecaEngine.Graphics.Diligent;
using Diligent;

namespace DecaEngine;

public class DiligentRenderHandle : IRenderHandle
{
	public Vector2 Size { get; private set; }

	private readonly IRenderDevice _device;
	private ITexture _texture;
	private ITextureView _rtv;
	private TextureInfo _info;

	public ITextureView RTV => _rtv;

	public ITexture Texture => _texture;

	public DiligentRenderHandle(IRenderDevice device)
	{
		_device = device;
	}

	public void Alloc(TextureInfo info)
	{
		_info = info;
		Size = new Vector2(info.width, info.height);
		CreateTexture();
	}

	public void Resize(Vector2 size)
	{
		if (Size == size)
		{
			return;
		}

		Size = size;
		_info.width = (uint)size.X;
		_info.height = (uint)size.Y;

		ReleaseResources();
		CreateTexture();
	}

	private void CreateTexture()
	{
		var dilFormat = DiligentResourceFormats.ToNativeFormat(_info.format);
		if (dilFormat == Diligent.TextureFormat.Unknown)
		{
			dilFormat = Diligent.TextureFormat.RGBA8_UNorm;
		}

		var desc = new TextureDesc
		{
			Name = _info.name,
			Type = ResourceDimension.Tex2d,
			Width = _info.width,
			Height = _info.height,
			Format = dilFormat,
			BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
			Usage = Usage.Default,
			MipLevels = 1,
		};

		_texture = _device.CreateTexture(desc);
		_rtv = _texture.GetDefaultView(TextureViewType.RenderTarget);
	}

	private void ReleaseResources()
	{
		_rtv.Dispose();
		_texture?.Dispose();
	}

	public void Release()
	{
		ReleaseResources();
	}
}