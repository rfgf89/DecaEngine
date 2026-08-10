using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Graphics.Core;
using DecaEngine.Graphics.Diligent;
using Diligent;

namespace DecaEngine;

// IGpuTexture - чтобы хэндл можно было передавать в backend-независимые перегрузки ICommandBuffer
// (SetRenderTarget/ClearRenderTarget) вместо сырого Diligent-ITextureView.
public class DiligentRenderHandle : IRenderHandle, IGpuTexture
{
	public Vector2 Size { get; private set; }

	private readonly IRenderDevice _device;
	private ITexture _texture;
	private ITextureView _rtv;
	private TextureInfo _info;

	public string Name => _info.name;

	public TextureInfo Info => _info;

	public ITextureView RTV => _rtv;

	public ITexture Texture => _texture;

	/// <summary>Хэндл держит одну не-массивную RT-текстуру, поэтому slice всегда 0, а
	/// единственный интересный вид - RenderTarget (см. <see cref="RTV"/>).</summary>
	public ITextureView GetView(TextureViewType type, uint slice = 0)
	{
		return type == TextureViewType.RenderTarget ? _rtv : _texture.GetDefaultView(type);
	}

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