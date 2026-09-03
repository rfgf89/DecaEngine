using System;
using System.Collections.Generic;
using DecaEngine.Core;
using Diligent;

namespace DecaEngine.Graphics.Diligent
{
	public class DiligentGpuTexture : IGpuTexture
	{
		public string Name { get; }
		public TextureInfo Info { get; }
		
		public ITexture Texture { get; }

		private readonly Dictionary<(TextureViewType, uint), ITextureView> _views = new();

		/// <summary>Views created here; default views are owned by the texture itself.</summary>
		private readonly List<ITextureView> _ownedViews = new();

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
				// Owned by the texture: releasing it separately is a double free and kills the driver.
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

				_ownedViews.Add(newView);
			}

			_views[(type, slice)] = newView;
			return newView;
		}

		public void Release()
		{
			foreach (var view in _ownedViews)
				view.Dispose();
			_ownedViews.Clear();
			_views.Clear();
			Texture?.Dispose();
		}
	}
}