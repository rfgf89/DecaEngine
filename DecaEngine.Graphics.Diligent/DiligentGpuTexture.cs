using System;
using System.Collections.Generic;
using DecaEngine.Core;
using DecaEngine.Graphics.Core;
using Diligent;

namespace DecaEngine.Graphics.Diligent
{
	public class DiligentGpuTexture : IGpuTexture
	{
		public string Name { get; }
		public TextureInfo Info { get; }
		
		public ITexture Texture { get; }

		private readonly Dictionary<(TextureViewType, uint), ITextureView> _views = new();

		public DiligentGpuTexture(string name, TextureInfo info, ITexture texture)
		{
			Name = name;
			Info = info;
			Texture = texture;
		}

		public ITextureView GetView(TextureViewType type, uint slice = 0)
		{
			if (_views.TryGetValue((type, slice), out var view))
				return view;

			ITextureView newView;
			if (slice == 0 && Info.arraySize <= 1)
			{
				newView = Texture.GetDefaultView(type);
			}
			else
			{
				newView = Texture.CreateView(new TextureViewDesc
				{
					Name = $"{Name} View {type} Slice {slice}",
					ViewType = type,
					FirstSlice = slice,
					NumSlices = 1,
					TextureDim = ResourceDimension.Tex2dArray,
				});
			}

			_views[(type, slice)] = newView;
			return newView;
		}

		public void Release()
		{
			foreach (var view in _views.Values)
				view.Dispose();
			_views.Clear();
			Texture?.Dispose();
		}
	}
}