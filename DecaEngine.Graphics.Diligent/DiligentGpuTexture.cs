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

		/// <summary>Представления, СОЗДАННЫЕ здесь - только их и освобождаем. Дефолтные принадлежат
		/// текстуре и освобождаются вместе с ней.</summary>
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
				// Дефолтным представлением владеет САМА текстура - его нельзя освобождать отдельно
				// (двойное освобождение роняет драйвер), поэтому в список владения оно не идёт.
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
			// Только созданные нами представления - см. комментарий в GetView.
			foreach (var view in _ownedViews)
				view.Dispose();
			_ownedViews.Clear();
			_views.Clear();
			Texture?.Dispose();
		}
	}
}