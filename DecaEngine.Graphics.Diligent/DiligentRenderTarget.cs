using System;
using System.Collections.Generic;
using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Graphics.Core;
using Diligent;

namespace DecaEngine.Graphics.Diligent
{
	public class DiligentRenderTarget : IRenderTarget
	{
		public string Name => _info.name;
		public TextureInfo Info { get; private set; }

		public Vector2 Size { get; private set; }

		public ITexture Texture => _texture;

		private readonly IRenderDevice _device;
		private RenderTargetInfo _info;
		private ITexture _texture;
		private readonly Dictionary<(TextureViewType, uint), ITextureView> _views = new();

		public DiligentRenderTarget(IRenderDevice device, RenderTargetInfo info)
		{
			_device = device;
			_info = info;
			if (_info.arraySize == 0) _info.arraySize = 1;
			Size = new Vector2(info.width, info.height);
			CreateTexture();
		}

		public void Resize(Vector2 size)
		{
			if (Size == size) return;

			Size = size;
			_info.width = (uint)size.X;
			_info.height = (uint)size.Y;

			ReleaseResources();
			CreateTexture();
		}

		private void CreateTexture()
		{
			var (dilFormat, bindFlags) = _info.textureFormat switch
			{
				RenderTargetInfo.Format.R16G16B16A16_FLOAT => (TextureFormat.RGBA16_Float, BindFlags.RenderTarget | BindFlags.ShaderResource),
				RenderTargetInfo.Format.D32_FLOAT => (TextureFormat.D32_Float, BindFlags.DepthStencil | BindFlags.ShaderResource),
				RenderTargetInfo.Format.D32_Float_S8X24_UInt => (TextureFormat.D32_Float_S8X24_UInt, BindFlags.DepthStencil | BindFlags.ShaderResource),
				RenderTargetInfo.Format.D24_UNorm_S8_UInt => (TextureFormat.D24_UNorm_S8_UInt, BindFlags.DepthStencil | BindFlags.ShaderResource),
				_ => (TextureFormat.RGBA8_UNorm, BindFlags.RenderTarget | BindFlags.ShaderResource)
			};

			var desc = new TextureDesc
			{
				Name = _info.name,
				Type = _info.arraySize > 1 ? ResourceDimension.Tex2dArray : ResourceDimension.Tex2d,
				Width = _info.width,
				Height = _info.height,
				ArraySizeOrDepth = _info.arraySize,
				Format = dilFormat,
				BindFlags = bindFlags,
				Usage = Usage.Default,
				MipLevels = 1,
			};

			_texture = _device.CreateTexture(desc);
			
			Info = new TextureInfo
			{
				name = _info.name,
				width = _info.width,
				height = _info.height,
				arraySize = _info.arraySize,
				type = _info.arraySize > 1 ? TextureType.Texture2DArray : TextureType.Texture2D,
				format = _info.textureFormat switch
				{
					RenderTargetInfo.Format.R16G16B16A16_FLOAT => TextureObjectFormat.R16G16B16A16Float,
					RenderTargetInfo.Format.D32_FLOAT => TextureObjectFormat.D32Float,
					_ => TextureObjectFormat.R8G8B8A8UNorm
				}
			};
		}

		public ITextureView GetView(TextureViewType type, uint slice = 0)
		{
			if (_views.TryGetValue((type, slice), out var view))
				return view;

			ITextureView newView;
			if (slice == 0 && _info.arraySize <= 1)
			{
				newView = _texture.GetDefaultView(type);
			}
			else
			{
				newView = _texture.CreateView(new TextureViewDesc
				{
					Name = $"{Name} View {type} Slice {slice}",
					ViewType = type,
					FirstSlice = slice,
					NumSlices = 1,
					TextureDim = ResourceDimension.Tex2d,
				});
			}

			_views[(type, slice)] = newView;
			return newView;
		}

		private void ReleaseResources()
		{
			foreach (var view in _views.Values)
				view.Dispose();
			_views.Clear();
			_texture?.Dispose();
		}

		public void Release()
		{
			ReleaseResources();
		}
	}
}