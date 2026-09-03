using System.Linq;
using System.Numerics;
using DecaEngine.Core.Prefabs;
using Engine.ImGui.Core;
using Friflo.Engine.ECS;
using Hexa.NET.ImGui;

namespace DecaEngine.Editor
{
	/// <summary>Asset grid: directory walk, cell and label drawing.</summary>
	public partial class AssetBrowserWindow
	{
		private void RefreshEntries()
		{
			_entries.Clear();

			if (_currentDirectory is null || !Directory.Exists(_currentDirectory))
			{
				return;
			}

			try
			{
				var directories = Directory.GetDirectories(_currentDirectory)
					.OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase);

				foreach (var dir in directories)
				{
					_entries.Add(new AssetEntry(dir, Path.GetFileName(dir), true, IsDirectoryEmpty(dir)));
				}

				var files = Directory.GetFiles(_currentDirectory)
					.OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);

				var projectDirectory = _projectSession.ProjectDirectory;

				foreach (var file in files)
				{
					// Compiled prefabs are an implementation detail, not browsable assets.
					if (IsPrefabBinary(Path.GetFileName(file)))
					{
						continue;
					}

					var fileName = Path.GetFileName(file);
					_entries.Add(new AssetEntry(file, fileName, false, false));

					// Expanded model: inline its sub-meshes as entries, per the baked icon manifest.
					if (projectDirectory is null || !IsGltfModel(fileName) || !_expandedModels.Contains(file))
					{
						continue;
					}

					var manifest = _iconCache.GetManifest(projectDirectory, file);
					if (manifest is null)
					{
						continue;
					}

					for (int i = 0; i < manifest.SubMeshNames.Count; i++)
					{
						_entries.Add(new AssetEntry(file, manifest.SubMeshNames[i], i));
					}
				}
			}
			catch
			{
			}
		}

		private static bool IsDirectoryEmpty(string path)
		{
			try
			{
				return !Directory.EnumerateFileSystemEntries(path).Any();
			}
			catch
			{
				return true;
			}
		}

		private void RenderGrid()
		{
			// Sampled once at frame start: the click that closes a popup must not also reopen it.
			bool popupOpenAtFrameStart = PopupContextMenu.IsAnyPopupOpen();

			bool gridHovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByPopup);
			bool leftClicked = ImGui.IsMouseClicked(ImGuiMouseButton.Left);
			bool rightClicked = ImGui.IsMouseClicked(ImGuiMouseButton.Right);

			if (gridHovered && (leftClicked || rightClicked) && !popupOpenAtFrameStart)
			{
				RefreshEntries();
			}

			bool itemConsumedRightClick = false;
			bool itemConsumedLeftClick = false;

			if (_entries.Count != 0)
			{
				var cellSize = CellSize * _scale;
				var cellSpacing = CellSpacing * _scale;
				var panelWidth = ImGui.GetContentRegionAvail().X;
				var columns = Math.Max(1, (int)((panelWidth + cellSpacing) / (cellSize + cellSpacing)));

				for (int i = 0; i < _entries.Count; i++)
				{
					RenderEntry(_entries[i], i, cellSize, rightClicked, !popupOpenAtFrameStart, out var consumedRight, out var consumedLeft);
					itemConsumedRightClick |= consumedRight;
					itemConsumedLeftClick |= consumedLeft;

					if ((i + 1) % columns != 0 && i != _entries.Count - 1)
					{
						ImGui.SameLine(0, cellSpacing);
					}
				}
			}

			if (_entriesDirty)
			{
				_entriesDirty = false;
				RefreshEntries();
			}

			if (leftClicked && !itemConsumedLeftClick && gridHovered && !ImGui.IsAnyItemHovered())
			{
				_selectedPath = null;
			}

			if (rightClicked && !itemConsumedRightClick && gridHovered && !ImGui.IsAnyItemHovered())
			{
				_contextPath = _currentDirectory;
				_contextPathIsDirectory = true;
				PopupContextMenu.TryOpen("AssetContextWindowBg", !popupOpenAtFrameStart);
			}

			if (PopupContextMenu.BeginPopup("AssetContextWindowBg"))
			{
				RenderContextMenuContent();
				ImGui.EndPopup();
			}
		}

		private void RenderEntry(AssetEntry entry, int index, float cellSize, bool rightClicked, bool allowOpenPopup, out bool consumedRightClick, out bool consumedLeftClick)
		{
			consumedRightClick = false;
			consumedLeftClick = false;

			bool isSelected = _selectedPath == entry.FullPath;

			// Only the selected cell wraps its label; others truncate to keep the grid rows even.
			var maxLabelWidth = cellSize - 4f * _scale;
			var labelLines = isSelected
				? WrapTextToLines(entry.Name, maxLabelWidth)
				: new List<string> { TruncateToWidth(entry.Name, maxLabelWidth) };

			var lineHeight = ImGui.GetTextLineHeight();
			var labelAreaHeight = MathF.Max(LabelHeight * _scale, labelLines.Count * lineHeight + 8f * _scale);
			var itemSize = new Vector2(cellSize, cellSize + labelAreaHeight);
			var screenPos = ImGui.GetCursorScreenPos();

			// Drives sub-mesh icon bake scheduling below, not just widget visibility.
			bool isVisible = ImGui.IsRectVisible(screenPos, screenPos + itemSize);

			ImGui.PushID(index);

			// Hand-drawn rounded card: ImGui hardcodes Selectable's highlight rounding to zero, so
			// the Selectable keeps transparent Header colors and only provides interaction.
			var drawList = ImGui.GetWindowDrawList();
			float cardRounding = 6f * _scale;
			var cardMax = screenPos + itemSize;
			drawList.AddRectFilled(screenPos, cardMax,
				ImGui.GetColorU32(EditorPalette.Tint(EditorPalette.Background, 0.045f, EditorPalette.Background)),
				cardRounding);

			ImGui.PushStyleColor(ImGuiCol.Header, Vector4.Zero);
			ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Vector4.Zero);
			ImGui.PushStyleColor(ImGuiCol.HeaderActive, Vector4.Zero);
			bool clicked = ImGui.Selectable("##asset-item", isSelected, ImGuiSelectableFlags.AllowDoubleClick, itemSize);
			ImGui.PopStyleColor(3);

			if (isSelected)
			{
				drawList.AddRectFilled(screenPos, cardMax,
					ImGui.GetColorU32(EditorPalette.WithAlpha(EditorPalette.Selection, 0.20f)), cardRounding);
			}
			else if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByPopup))
			{
				drawList.AddRectFilled(screenPos, cardMax,
					ImGui.GetColorU32(EditorPalette.WithAlpha(EditorPalette.Selection, 0.09f)), cardRounding);
			}

			// Drag payload for AssetRef fields: path relative to the Assets root, forward slashes.
			// Sub-mesh entries have no real file behind them, so they are not draggable.
			if (!entry.IsDirectory && !entry.IsSubMesh && ImGui.BeginDragDropSource())
			{
				var relativePath = GetAssetRelativePath(entry.FullPath);
				var bytes = System.Text.Encoding.UTF8.GetBytes(relativePath);

				// SetDragDropPayload copies the data, so the array need not outlive this call.
				unsafe
				{
					fixed (byte* p = bytes)
					{
						ImGui.SetDragDropPayload(
							DecaEngine.Core.Assets.AssetRef.DragDropPayloadType,
							p,
							(nuint)bytes.Length
						);
					}
				}

				ImGui.TextUnformatted(entry.Name);
				ImGui.EndDragDropSource();
			}

			bool doubleClicked = clicked && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);

			// Sub-mesh foldout arrow; only available once the icon manifest has been baked.
			var projectDirectory = _projectSession.ProjectDirectory;
			bool hasExpandArrow = !entry.IsDirectory && !entry.IsSubMesh && projectDirectory is not null &&
				IsGltfModel(entry.Name) &&
				_iconCache.GetManifest(projectDirectory, entry.FullPath) is { SubMeshNames.Count: > 0 };

			var arrowSize = 13f * _scale;
			var arrowMin = new Vector2(screenPos.X + cellSize - arrowSize - 2f * _scale,
				screenPos.Y + (cellSize - arrowSize) * 0.5f);
			var arrowMax = arrowMin + new Vector2(arrowSize, arrowSize);

			bool arrowClicked = false;
			if (hasExpandArrow && clicked)
			{
				var mouse = ImGui.GetMousePos();
				arrowClicked = mouse.X >= arrowMin.X && mouse.X <= arrowMax.X &&
					mouse.Y >= arrowMin.Y && mouse.Y <= arrowMax.Y;
			}

			if (arrowClicked)
			{
				if (!_expandedModels.Remove(entry.FullPath))
				{
					_expandedModels.Add(entry.FullPath);
				}

				// Cannot rebuild here: the caller is iterating _entries.
				_entriesDirty = true;
				clicked = false;
				consumedLeftClick = true;
			}

			if (clicked)
			{
				_selectedPath = entry.FullPath;
				consumedLeftClick = true;
			}

			// Uses the click sampled once per frame so this stays consistent with RenderGrid.
			if (rightClicked && ImGui.IsItemHovered())
			{
				_selectedPath = entry.FullPath;
				consumedRightClick = true;

				// Sub-mesh entries have no file, so the file context menu must not open for them.
				if (!entry.IsSubMesh)
				{
					_contextPath = entry.FullPath;
					_contextPathIsDirectory = entry.IsDirectory;
					PopupContextMenu.TryOpen("AssetContextItem", allowOpenPopup);
				}
			}

			if (PopupContextMenu.BeginPopup("AssetContextItem"))
			{
				RenderContextMenuContent();
				ImGui.EndPopup();
			}

			if (doubleClicked && entry.IsDirectory)
			{
				NavigateTo(entry.FullPath);
			}
			else if (clicked && entry.IsSubMesh)
			{
				_inspectorWindow.ShowModel(entry.ParentModelPath!, entry.SubMeshIndex, entry.Name);
			}
			else if (clicked && !entry.IsDirectory && IsPrefabJson(entry.Name))
			{
				_inspectorWindow.ShowPrefab(entry.FullPath);
			}
			else if (clicked && !entry.IsDirectory && IsGltfModel(entry.Name))
			{
				// Previewed through an isolated render graph, not the editor scene.
				_inspectorWindow.ShowModel(entry.FullPath);

				if (projectDirectory is not null &&
				    _iconCache.GetManifest(projectDirectory, entry.FullPath) is null &&
				    !_iconBaker.IsBakingOrQueued(entry.FullPath))
				{
					_iconBaker.Enqueue(entry.FullPath, projectDirectory);
				}
			}

			var iconPadding = IconPadding * _scale;
			var iconTop = screenPos.Y + iconPadding;
			var iconBottom = screenPos.Y + cellSize * 0.9f - iconPadding;

			if (entry.IsDirectory)
			{
				var iconMin = screenPos + new Vector2(iconPadding, iconPadding);
				var iconMax = new Vector2(screenPos.X + cellSize - iconPadding, iconBottom);
				DrawFolderIcon(drawList, iconMin, iconMax, _scale, entry.IsEmptyDirectory);
			}
			else
			{
				var fileIconHeight = iconBottom - iconTop;
				var fileIconWidth = fileIconHeight * 0.9f;
				var iconCenterX = screenPos.X + cellSize * 0.5f;
				var iconMin = new Vector2(iconCenterX - fileIconWidth * 0.5f, iconTop);
				var iconMax = new Vector2(iconCenterX + fileIconWidth * 0.5f, iconBottom);

				// Cached preview when baked; the vector glyph is the fallback.
				bool drewCachedIcon = false;
				if (projectDirectory is not null && (entry.IsSubMesh || IsGltfModel(entry.Name)))
				{
					var modelPath = entry.IsSubMesh ? entry.ParentModelPath! : entry.FullPath;
					var iconIndex = entry.IsSubMesh ? entry.SubMeshIndex : ModelIconCache.WholeModelIndex;
					if (_iconCache.TryGetIcon(projectDirectory, modelPath, iconIndex, out var iconTexture))
					{
						var previewMin = screenPos + new Vector2(iconPadding, iconPadding);
						var previewMax = new Vector2(screenPos.X + cellSize - iconPadding, iconBottom);
						drawList.AddImageRounded(iconTexture, previewMin, previewMax,
							Vector2.Zero, Vector2.One, 0xFFFFFFFF, 4f * _scale);
						drewCachedIcon = true;
					}
					else if (entry.IsSubMesh)
					{
						if (isVisible)
						{
							// Baked lazily per visible row: baking every sub-mesh at once on model
							// selection freezes the editor on high-sub-mesh models.
							if (!_iconBaker.IsSubMeshIconBakingOrQueued(modelPath, iconIndex))
							{
								_iconBaker.EnqueueSubMeshIcon(modelPath, projectDirectory, iconIndex);
							}
						}
						else
						{
							// Scrolled out of view: drop the not-yet-started bake to save GPU time.
							_iconBaker.CancelSubMeshIcon(modelPath, iconIndex);
						}
					}
				}

				if (!drewCachedIcon)
				{
					DrawFileIcon(drawList, iconMin, iconMax,
						entry.IsSubMesh ? FileIconKind.Model : GetFileIconKind(entry.Name), _scale);
				}
			}

			if (hasExpandArrow)
			{
				var expanded = _expandedModels.Contains(entry.FullPath);
				var arrowColor = ImGui.GetColorU32(ImGuiCol.Text);
				var center = (arrowMin + arrowMax) * 0.5f;
				var half = arrowSize * 0.35f;

				// Backing disc keeps the arrow readable on top of a cached preview.
				drawList.AddCircleFilled(center, arrowSize * 0.62f,
					ImGui.GetColorU32(EditorPalette.WithAlpha(EditorPalette.Background, 0.75f)));

				if (expanded)
				{
					drawList.AddTriangleFilled(
						new Vector2(center.X - half, center.Y - half * 0.6f),
						new Vector2(center.X + half, center.Y - half * 0.6f),
						new Vector2(center.X, center.Y + half),
						arrowColor);
				}
				else
				{
					drawList.AddTriangleFilled(
						new Vector2(center.X - half * 0.6f, center.Y - half),
						new Vector2(center.X + half, center.Y),
						new Vector2(center.X - half * 0.6f, center.Y + half),
						arrowColor);
				}
			}

			DrawLabel(drawList, labelLines, screenPos, cellSize);

			// Drawn last so the icon cannot cover it, and after this frame's click may have moved it.
			if (_selectedPath == entry.FullPath)
			{
				drawList.AddRect(screenPos, cardMax,
					ImGui.GetColorU32(EditorPalette.WithAlpha(EditorPalette.Selection, 0.9f)),
					cardRounding, 0, 2f * _scale);
			}

			ImGui.PopID();
		}

		private void DrawLabel(ImDrawListPtr drawList, IReadOnlyList<string> lines, Vector2 screenPos, float cellSize)
		{
			var textColor = ImGui.GetColorU32(ImGuiCol.Text);
			var lineHeight = ImGui.GetTextLineHeight();
			var y = screenPos.Y + cellSize;

			foreach (var line in lines)
			{
				var textSize = ImGui.CalcTextSize(line);
				var textPos = new Vector2(screenPos.X + (cellSize - textSize.X) * 0.5f, y);
				drawList.AddText(textPos, textColor, line);
				y += lineHeight;
			}
		}

		private static string TruncateToWidth(string text, float maxWidth)
		{
			if (ImGui.CalcTextSize(text).X <= maxWidth)
			{
				return text;
			}

			const string ellipsis = "...";
			var lo = 0;
			var hi = text.Length;

			while (lo < hi)
			{
				var mid = (lo + hi + 1) / 2;
				var candidate = text[..mid] + ellipsis;
				if (ImGui.CalcTextSize(candidate).X <= maxWidth)
				{
					lo = mid;
				}
				else
				{
					hi = mid - 1;
				}
			}

			return lo <= 0 ? ellipsis : text[..lo] + ellipsis;
		}

		private static List<string> WrapTextToLines(string text, float maxWidth)
		{
			var lines = new List<string>();

			if (ImGui.CalcTextSize(text).X <= maxWidth)
			{
				lines.Add(text);
				return lines;
			}

			var words = text.Split(' ');
			var current = string.Empty;

			foreach (var word in words)
			{
				var candidate = current.Length == 0 ? word : current + " " + word;
				if (ImGui.CalcTextSize(candidate).X <= maxWidth)
				{
					current = candidate;
					continue;
				}

				if (current.Length > 0)
				{
					lines.Add(current);
				}

				var remaining = word;
				while (ImGui.CalcTextSize(remaining).X > maxWidth && remaining.Length > 1)
				{
					var lo = 1;
					var hi = remaining.Length;
					while (lo < hi)
					{
						var mid = (lo + hi + 1) / 2;
						if (ImGui.CalcTextSize(remaining[..mid]).X <= maxWidth)
						{
							lo = mid;
						}
						else
						{
							hi = mid - 1;
						}
					}

					lines.Add(remaining[..lo]);
					remaining = remaining[lo..];
				}

				current = remaining;
			}

			if (current.Length > 0)
			{
				lines.Add(current);
			}

			return lines;
		}

	}
}
