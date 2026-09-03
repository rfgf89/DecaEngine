using System;
using System.Collections.Generic;
using System.Numerics;
using DecaEngine.Core;
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
		private TextureInfo _info;
		private ITexture _texture;
		private readonly Dictionary<(TextureViewType, uint), ITextureView> _views = new();

		// Only CreateView() results are ours to dispose: GetDefaultView() adds no reference and
		// releasing it corrupts the native heap.
		private readonly HashSet<(TextureViewType, uint)> _ownedViews = new();

		public DiligentRenderTarget(IRenderDevice device, TextureInfo info)
		{
			_device = device;
			_info = info;
			if (_info.arraySize == 0) _info.arraySize = 1;
			_info.type = _info.arraySize > 1 ? TextureType.Texture2DArray : TextureType.Texture2D;
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
			var (dilFormat, bindFlags) = DiligentResourceFormats.ToRenderTargetFormat(_info.format, _info.access);

			var desc = new TextureDesc
			{
				Name = _info.name,
				Type = _info.arraySize > 1 ? ResourceDimension.Tex2dArray : ResourceDimension.Tex2d,
				Width = _info.width,
				Height = _info.height,
				ArraySizeOrDepth = _info.arraySize,
				Format = dilFormat,
				// MSAA targets keep ShaderResource: SSAO reads depth via Texture2DMS.Load.
				BindFlags = bindFlags,
				Usage = Usage.Default,
				MipLevels = 1,
				SampleCount = Math.Max(1, _info.sampleCount),
			};

			_texture = _device.CreateTexture(desc);

			Info = _info;
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
				// The view dimension must match the texture dimension.
				var viewDim = _info.arraySize > 1 ? ResourceDimension.Tex2dArray : ResourceDimension.Tex2d;
				
				newView = _texture.CreateView(new TextureViewDesc
				{
					Name = $"{Name} View {type} Slice {slice}",
					ViewType = type,
					TextureDim = viewDim,
					FirstSlice = slice,
					NumSlices = 1,
				});
				_ownedViews.Add((type, slice));
			}

			_views[(type, slice)] = newView;
			return newView;
		}

		private void ReleaseResources()
		{
			foreach (var kvp in _views)
			{
				if (_ownedViews.Contains(kvp.Key))
					kvp.Value.Dispose();
			}
			_views.Clear();
			_ownedViews.Clear();
			_texture?.Dispose();
		}

		public void Release()
		{
			ReleaseResources();
		}
	}
}