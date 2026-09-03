using System.Linq;
using System.Numerics;
using DecaEngine.Core.Prefabs;
using Engine.ImGui.Core;
using Friflo.Engine.ECS;
using Hexa.NET.ImGui;

namespace DecaEngine.Editor
{
	/// <summary>Browser over the loaded project's "Assets" folder.</summary>
	public partial class AssetBrowserWindow : ImGuiDockingWindow
	{
		internal enum FileIconKind
		{
			Generic,
			Image,
			Code,
			Json,
			Shader,
			Audio,
			Model,
			Material,
			Scene,
			Prefab
		}

		private readonly struct AssetEntry
		{
			public readonly string FullPath;
			public readonly string Name;
			public readonly bool IsDirectory;
			public readonly bool IsEmptyDirectory;

			// -1 for a plain asset, otherwise the sub-mesh index inside an expanded model.
			public readonly int SubMeshIndex;

			public readonly string? ParentModelPath;

			public bool IsSubMesh => SubMeshIndex >= 0;

			public AssetEntry(string fullPath, string name, bool isDirectory, bool isEmptyDirectory)
			{
				FullPath = fullPath;
				Name = name;
				IsDirectory = isDirectory;
				IsEmptyDirectory = isEmptyDirectory;
				SubMeshIndex = -1;
				ParentModelPath = null;
			}

			// FullPath here is the pseudo-path "model::subN": no such file exists, it is a unique key.
			public AssetEntry(string parentModelPath, string subMeshName, int subMeshIndex)
			{
				FullPath = $"{parentModelPath}::sub{subMeshIndex}";
				Name = subMeshName;
				IsDirectory = false;
				IsEmptyDirectory = false;
				SubMeshIndex = subMeshIndex;
				ParentModelPath = parentModelPath;
			}
		}

		private string? _assetsRoot;
		private string? _currentDirectory;
		private string? _selectedPath;
		private string? _contextPath;
		private bool _contextPathIsDirectory;
		private readonly List<AssetEntry> _entries = new();

		private string? _clipboardPath;
		private bool _clipboardIsCut;

		private string? _renamingPath;
		private string _renameBuffer = string.Empty;
		private bool _renameFocusPending;

		private string? _deleteConfirmPath;

		private const float CellSize = 64f;
		private const float IconPadding = 6f;
		private const float CellSpacing = 8f;
		private const float LabelHeight = 32f;

		private float _treeWidth = 220f;
		private const float MinTreeWidth = 120f;
		private const float MinGridWidth = 200f;
		private const float SplitterWidth = 2f;
		private bool _draggingSplitter;

		private static Vector4 PanelBackground => EditorPalette.Background;

		private readonly ProjectSession _projectSession;
		private readonly InspectorWindow _inspectorWindow;
		private readonly ModelIconCache _iconCache;
		private readonly ModelIconBaker _iconBaker;

		private readonly HashSet<string> _expandedModels = new(StringComparer.OrdinalIgnoreCase);

		// Rebuild is deferred to after the draw pass: _entries must not change while iterated.
		private bool _entriesDirty;

		public AssetBrowserWindow(string name, ProjectSession projectSession, InspectorWindow inspectorWindow,
			ModelIconCache iconCache, ModelIconBaker iconBaker, ImGuiRender imGuiRender) : base(name, imGuiRender)
		{
			_projectSession = projectSession;
			_inspectorWindow = inspectorWindow;
			_iconCache = iconCache;
			_iconBaker = iconBaker;
		}

		protected override void OnRender(uint dockId)
		{
			var assetsPath = _projectSession.AssetsPath;

			if (string.IsNullOrEmpty(assetsPath))
			{
				_assetsRoot = null;
				_currentDirectory = null;
				_entries.Clear();
				ImGui.TextDisabled("Load a project to browse its assets.");
				return;
			}

			if (!string.Equals(_assetsRoot, assetsPath, StringComparison.OrdinalIgnoreCase))
			{
				_assetsRoot = assetsPath;
				_currentDirectory = assetsPath;
				_selectedPath = null;
				RefreshEntries();
			}

			try
			{
				Directory.CreateDirectory(_assetsRoot!);
			}
			catch
			{
			}

			RenderToolbar();
			ImGui.Separator();

			var avail = ImGui.GetContentRegionAvail();
			var splitterWidth = SplitterWidth * _scale;
			var minTreeWidth = MinTreeWidth * _scale;
			var minGridWidth = MinGridWidth * _scale;
			var maxTreeWidth = MathF.Max(minTreeWidth, avail.X - minGridWidth - splitterWidth);
			_treeWidth = Math.Clamp(_treeWidth, minTreeWidth, maxTreeWidth);

			ImGui.PushStyleColor(ImGuiCol.ChildBg, PanelBackground);

			if (ImGui.BeginChild("##AssetBrowserTree", new Vector2(_treeWidth, avail.Y), ImGuiChildFlags.Borders, ImGuiWindowFlags.HorizontalScrollbar))
			{
				RenderDirectoryTree();
			}

			ImGui.EndChild();

			ImGui.SameLine(0, 0);
			RenderSplitter(minTreeWidth, maxTreeWidth, avail.Y, splitterWidth);
			ImGui.SameLine(0, 0);

			if (ImGui.BeginChild("##AssetBrowserGrid", new Vector2(0, 0), 0, ImGuiWindowFlags.None))
			{
				RenderGrid();
			}

			ImGui.EndChild();
			ImGui.PopStyleColor();

			// Popups are opened after the children, at window scope, not inside a child region.
			RenderRenamePopup();
			RenderDeleteConfirmPopup();
		}

		private void RenderSplitter(float minTreeWidth, float maxTreeWidth, float height, float splitterWidth)
		{
			var cursorPos = ImGui.GetCursorScreenPos();

			ImGui.InvisibleButton("##AssetTreeSplitter", new Vector2(splitterWidth, height));

			if (ImGui.IsItemActive())
			{
				_draggingSplitter = true;
			}
			else if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
			{
				_draggingSplitter = false;
			}

			if (_draggingSplitter)
			{
				_treeWidth = Math.Clamp(_treeWidth + ImGui.GetIO().MouseDelta.X, minTreeWidth, maxTreeWidth);
			}

			var hovered = ImGui.IsItemHovered();
			if (hovered || _draggingSplitter)
			{
				ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);
			}

			var styleColors = ImGui.GetStyle().Colors;
			var color = _draggingSplitter
				? styleColors[(int)ImGuiCol.SeparatorActive]
				: hovered
					? styleColors[(int)ImGuiCol.SeparatorHovered]
					: styleColors[(int)ImGuiCol.Separator];

			var rounding = splitterWidth * 0.5f;
			ImGui.GetWindowDrawList().AddRectFilled(cursorPos, new Vector2(cursorPos.X + splitterWidth, cursorPos.Y + height), ImGui.GetColorU32(color), rounding);
		}

		private void RenderDirectoryTree()
		{
			if (_assetsRoot is null)
			{
				return;
			}

			RenderDirectoryTreeNode(_assetsRoot, "Assets");
		}

		private void RenderDirectoryTreeNode(string path, string label)
		{
			string[] subDirectories;
			var hasFiles = false;
			try
			{
				subDirectories = Directory.GetDirectories(path)
					.OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
					.ToArray();
				hasFiles = Directory.EnumerateFiles(path).Any();
			}
			catch
			{
				subDirectories = Array.Empty<string>();
			}

			var isEmpty = subDirectories.Length == 0 && !hasFiles;

			var isSelected = string.Equals(_currentDirectory, path, StringComparison.OrdinalIgnoreCase);
			var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth;
			if (string.Equals(path, _assetsRoot, StringComparison.OrdinalIgnoreCase))
			{
				flags |= ImGuiTreeNodeFlags.DefaultOpen;
			}

			if (subDirectories.Length == 0)
			{
				flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;
			}

			if (isSelected)
			{
				flags |= ImGuiTreeNodeFlags.Selected;
			}

			ImGui.PushID(path);
			EditorSelectionStyle.PushColors();
			var opened = ImGui.TreeNodeEx("##node", flags);
			EditorSelectionStyle.PopColors();

			if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen())
			{
				NavigateTo(path);
			}

			var drawList = ImGui.GetWindowDrawList();
			var iconSize = ImGui.GetTextLineHeight() * 0.8f;
			var iconSpacing = 6f * _scale;

			ImGui.SameLine(0, 2f * _scale);
			var iconPos = ImGui.GetCursorScreenPos() + new Vector2(0, (ImGui.GetTextLineHeight() - iconSize) * 0.5f);
			DrawFolderIcon(drawList, iconPos, iconPos + new Vector2(iconSize, iconSize), _scale, isEmpty);
			ImGui.Dummy(new Vector2(iconSize + iconSpacing, iconSize));
			ImGui.SameLine(0, 0);
			ImGui.TextUnformatted(label);

			if (opened && subDirectories.Length > 0)
			{
				foreach (var subDirectory in subDirectories)
				{
					RenderDirectoryTreeNode(subDirectory, Path.GetFileName(subDirectory));
				}

				ImGui.TreePop();
			}

			ImGui.PopID();
		}

	}
}
