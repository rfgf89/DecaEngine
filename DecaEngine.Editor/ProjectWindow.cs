using DecaEngine.Generic;
using Hexa.NET.ImGui;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace DecaEngine.Editor;

public class ProjectWindow : ImGuiDockingWindow, IFactoryObject
{
	public ProjectWindow(string title, ImGuiRender imGuiRender) : base(title, imGuiRender)
	{

	}

	protected override void OnRender(uint dockId)
	{

	}
}

public class InspectorWindow : ImGuiDockingWindow, IFactoryObject
{
	public InspectorWindow(string title, ImGuiRender imGuiRender) : base(title, imGuiRender)
	{

	}

	protected override void OnRender(uint dockId)
	{

	}
}

public class HierarchyWindow : ImGuiDockingWindow, IFactoryObject
{
	public HierarchyWindow(string title, ImGuiRender imGuiRender) : base(title, imGuiRender)
	{

	}

	protected override void OnRender(uint dockId)
	{

	}
}

[Flags]
public enum LogLevelFlags
{
	None = 0,
	Info = 1 << 0,
	Warning = 1 << 1,
	Error = 1 << 2,
	Debug = 1 << 3,
	All = Info | Warning | Error | Debug
}

public class ConsoleWindow : ImGuiDockingWindow, IFactoryObject
{
	public struct LogMessage
	{
		public LogLevelFlags Level;
		public string Text;
		public string Timestamp;
	}

	private readonly List<LogMessage> _logs = new List<LogMessage>();
	private bool _autoScroll = true;
	private LogLevelFlags _levelFilters = LogLevelFlags.All;
	
	private string _searchFilter = string.Empty;
	private string _commandInput = string.Empty;

	public ConsoleWindow(string title, ImGuiRender imGuiRender) : base(title, imGuiRender)
	{
		// Test logs
		Log(LogLevelFlags.Info, "Engine initialized.");
		Log(LogLevelFlags.Debug, "Memory allocated for new scene.");
		Log(LogLevelFlags.Warning, "Some minor configuration is missing. Using defaults.");
		Log(LogLevelFlags.Error, "Failed to load a texture.");
	}

	public void Log(LogLevelFlags level, string text)
	{
		_logs.Add(new LogMessage
		{
			Level = level,
			Text = text,
			Timestamp = DateTime.Now.ToString("HH:mm:ss")
		});
	}

	public void Clear()
	{
		_logs.Clear();
	}

	protected override void OnRender(uint dockId)
	{
		if (ImGui.Button("Clear"))
		{
			Clear();
		}

		ImGui.SameLine();

		if (ImGui.Button("Copy"))
		{
			StringBuilder sb = new StringBuilder();
			foreach (var log in _logs)
			{
				if ((_levelFilters & log.Level) == 0) continue;
				
				if (!string.IsNullOrEmpty(_searchFilter) && !log.Text.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase))
					continue;

				sb.AppendLine($"[{log.Timestamp}] [{log.Level}] {log.Text}");
			}
			ImGui.SetClipboardText(sb.ToString());
		}

		ImGui.SameLine();

		bool autoScroll = _autoScroll;
		if (ImGui.Checkbox("Auto-scroll", ref autoScroll))
		{
			_autoScroll = autoScroll;
		}

		ImGui.SameLine();
		
		ImGui.SetNextItemWidth(150);
		ImGui.InputText("Filter", ref _searchFilter, 256);

		ImGui.SameLine();

		// Dropdown for LogLevel filtering
		ImGui.SetNextItemWidth(120);
		if (ImGui.BeginCombo("Levels", "Select..."))
		{
			bool allSelected = _levelFilters == LogLevelFlags.All;
			if (ImGui.Checkbox("All", ref allSelected))
			{
				_levelFilters = allSelected ? LogLevelFlags.All : LogLevelFlags.None;
			}
			ImGui.Separator();

			bool showInfo = (_levelFilters & LogLevelFlags.Info) != 0;
			ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.8f, 0.8f, 0.8f, 1.0f));
			if (ImGui.Checkbox("Info", ref showInfo))
			{
				if (showInfo) _levelFilters |= LogLevelFlags.Info;
				else _levelFilters &= ~LogLevelFlags.Info;
			}
			ImGui.PopStyleColor();

			bool showWarning = (_levelFilters & LogLevelFlags.Warning) != 0;
			ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 1.0f, 0.0f, 1.0f));
			if (ImGui.Checkbox("Warning", ref showWarning))
			{
				if (showWarning) _levelFilters |= LogLevelFlags.Warning;
				else _levelFilters &= ~LogLevelFlags.Warning;
			}
			ImGui.PopStyleColor();

			bool showError = (_levelFilters & LogLevelFlags.Error) != 0;
			ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.3f, 0.3f, 1.0f));
			if (ImGui.Checkbox("Error", ref showError))
			{
				if (showError) _levelFilters |= LogLevelFlags.Error;
				else _levelFilters &= ~LogLevelFlags.Error;
			}
			ImGui.PopStyleColor();

			bool showDebug = (_levelFilters & LogLevelFlags.Debug) != 0;
			ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.5f, 0.5f, 1.0f, 1.0f));
			if (ImGui.Checkbox("Debug", ref showDebug))
			{
				if (showDebug) _levelFilters |= LogLevelFlags.Debug;
				else _levelFilters &= ~LogLevelFlags.Debug;
			}
			ImGui.PopStyleColor();

			ImGui.EndCombo();
		}

		ImGui.Separator();

		float footerHeightToReserve = ImGui.GetStyle().ItemSpacing.Y + ImGui.GetFrameHeightWithSpacing();
		if (ImGui.BeginChild("ScrollingRegion", new Vector2(0, -footerHeightToReserve)))
		{
			foreach (var log in _logs)
			{
				if ((_levelFilters & log.Level) == 0) continue;
				
				if (!string.IsNullOrEmpty(_searchFilter) && !log.Text.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase))
					continue;

				Vector4 color = log.Level switch
				{
					LogLevelFlags.Info => new Vector4(0.8f, 0.8f, 0.8f, 1.0f),
					LogLevelFlags.Warning => new Vector4(1.0f, 1.0f, 0.0f, 1.0f),
					LogLevelFlags.Error => new Vector4(1.0f, 0.3f, 0.3f, 1.0f),
					LogLevelFlags.Debug => new Vector4(0.5f, 0.5f, 1.0f, 1.0f),
					_ => new Vector4(1.0f, 1.0f, 1.0f, 1.0f)
				};

				ImGui.PushStyleColor(ImGuiCol.Text, color);
				ImGui.TextUnformatted($"[{log.Timestamp}] [{log.Level}] {log.Text}");
				ImGui.PopStyleColor();
			}

			if (_autoScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY())
			{
				ImGui.SetScrollHereY(1.0f);
			}
		}
		ImGui.EndChild();
		
		ImGui.Separator();
		
		bool commandEntered = ImGui.InputText("Command", ref _commandInput, 1024, ImGuiInputTextFlags.EnterReturnsTrue);
		ImGui.SameLine();
		if (ImGui.Button("Execute") || commandEntered)
		{
			ExecuteCommand(_commandInput);
			_commandInput = string.Empty;
			ImGui.SetKeyboardFocusHere(-1);
		}
	}
	
	private void ExecuteCommand(string command)
	{
		if (string.IsNullOrWhiteSpace(command)) return;
		
		Log(LogLevelFlags.Info, $"> {command}");
		
		string cmd = command.Trim().ToLower();
		
		if (cmd == "clear")
		{
			Clear();
		}
		else if (cmd == "help")
		{
			Log(LogLevelFlags.Info, "Available commands: clear, help");
		}
		else
		{
			Log(LogLevelFlags.Error, $"Unknown command: '{command}'");
		}
	}
}

public class AssetBrowserWindow : ImGuiDockingWindow, IFactoryObject
{
	private string _currentDirectory;
	private readonly string _baseDirectory;
	private float _thumbnailSize = 96.0f;
	private float _padding = 16.0f;

	// In a real engine, you'd cache these and not check every frame.
	public AssetBrowserWindow(string title, ImGuiRender imGuiRender) : base(title, imGuiRender)
	{
		// Defaulting to an "Assets" folder relative to the project/executable
		_baseDirectory = Path.Combine(Environment.CurrentDirectory, "Assets");
		if (!Directory.Exists(_baseDirectory))
		{
			try
			{
				Directory.CreateDirectory(_baseDirectory);
			}
			catch { }
		}
		_currentDirectory = _baseDirectory;
	}

	protected override void OnRender(uint dockId)
	{
		if (!Directory.Exists(_baseDirectory))
		{
			ImGui.Text("Asset directory not found.");
			return;
		}

		// --- TOP BAR (Breadcrumbs & Zoom) ---
		ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.15f, 0.15f, 0.15f, 1.0f));
		if (ImGui.BeginChild("AssetTopBar", new Vector2(0, 40), ImGuiWindowFlags.NoScrollbar))
		{
			// Back Button
			if (_currentDirectory != _baseDirectory)
			{
				if (ImGui.Button("<-"))
				{
					_currentDirectory = Directory.GetParent(_currentDirectory)?.FullName ?? _baseDirectory;
				}
				ImGui.SameLine();
			}

			// Breadcrumbs
			string relativePath = _currentDirectory == _baseDirectory 
				? "Assets" 
				: "Assets" + _currentDirectory.Substring(_baseDirectory.Length).Replace('\\', '/');
			
			ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 4);
			ImGui.Text(relativePath);

			// Zoom Slider
			ImGui.SameLine();
			ImGui.SetCursorPosX(ImGui.GetWindowWidth() - 220);
			ImGui.SetCursorPosY(ImGui.GetCursorPosY() - 4);
			ImGui.PushItemWidth(200);
			ImGui.SliderFloat("##Zoom", ref _thumbnailSize, 32.0f, 256.0f, "Zoom");
			ImGui.PopItemWidth();
		}
		ImGui.EndChild();
		ImGui.PopStyleColor();

		// --- MAIN CONTENT AREA (Left: Tree, Right: Grid) ---
		if (ImGui.BeginTable("AssetBrowserLayout", 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV))
		{
			ImGui.TableSetupColumn("Tree", ImGuiTableColumnFlags.WidthFixed, 200.0f);
			ImGui.TableSetupColumn("Grid", ImGuiTableColumnFlags.WidthStretch);
			ImGui.TableNextRow();

			// --- LEFT PANEL: FOLDER TREE ---
			ImGui.TableSetColumnIndex(0);
			if (ImGui.BeginChild("FolderTree"))
			{
				RenderFolderTree(_baseDirectory);
			}
			ImGui.EndChild();

			// --- RIGHT PANEL: ASSET GRID ---
			ImGui.TableSetColumnIndex(1);
			if (ImGui.BeginChild("AssetGrid"))
			{
				RenderAssetGrid();
			}
			ImGui.EndChild();

			ImGui.EndTable();
		}
	}

	private void RenderFolderTree(string path)
	{
		DirectoryInfo dirInfo = new DirectoryInfo(path);
		
		ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth;
		
		if (path == _currentDirectory)
		{
			flags |= ImGuiTreeNodeFlags.Selected;
		}

		DirectoryInfo[] subDirs = Array.Empty<DirectoryInfo>();
		try 
		{
			if (dirInfo.Exists)
			{
				subDirs = dirInfo.GetDirectories();
			}
		}
		catch 
		{
			// Ignore UnauthorizedAccessException, etc.
		}

		if (subDirs.Length == 0)
		{
			flags |= ImGuiTreeNodeFlags.Leaf;
		}

		// Always require a TreePop if isNodeOpen is true
		bool isNodeOpen = ImGui.TreeNodeEx(dirInfo.Name == "Assets" ? "Assets" : dirInfo.Name, flags);
		
		if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen())
		{
			_currentDirectory = path;
		}

		if (isNodeOpen)
		{
			foreach (var subDir in subDirs)
			{
				RenderFolderTree(subDir.FullName);
			}
			ImGui.TreePop();
		}
	}

	private void RenderAssetGrid()
	{
		float cellSize = _thumbnailSize + _padding;
		float panelWidth = ImGui.GetContentRegionAvail().X;
		int columnCount = (int)(panelWidth / cellSize);
		if (columnCount < 1) columnCount = 1;

		if (ImGui.BeginTable("AssetTable", columnCount))
		{
			DirectoryInfo dirInfo = new DirectoryInfo(_currentDirectory);
			
			DirectoryInfo[] directories = Array.Empty<DirectoryInfo>();
			FileInfo[] files = Array.Empty<FileInfo>();

			try 
			{
				if (dirInfo.Exists)
				{
					directories = dirInfo.GetDirectories();
					files = dirInfo.GetFiles();
				}
			}
			catch 
			{
				// Ignore file access exceptions
			}

			int itemIndex = 0;

			// Render Directories first
			foreach (var dir in directories)
			{
				RenderGridItem(dir.Name, true, itemIndex, columnCount, () =>
				{
					_currentDirectory = dir.FullName;
				});
				itemIndex++;
			}

			// Render Files
			foreach (var file in files)
			{
				RenderGridItem(file.Name, false, itemIndex, columnCount, () =>
				{
					// Double click on file (e.g., open in inspector, open scene, etc.)
				});
				itemIndex++;
			}

			ImGui.EndTable();
		}
	}

	private void RenderGridItem(string name, bool isDirectory, int index, int columns, Action onDoubleClick)
	{
		ImGui.TableNextColumn();
		ImGui.PushID(name);
		
		Vector2 size = new Vector2(_thumbnailSize, _thumbnailSize);
		
		// Setup colors based on selection/hover state (simplified here)
		ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0, 0, 0, 0));
		
		// The thumbnail button
		string label = isDirectory ? "\n[DIR]" : "\n[FILE]";
		ImGui.Button(label, size);
		
		ImGui.PopStyleColor();

		// Drag & Drop Source example for files
		if (!isDirectory && ImGui.BeginDragDropSource())
		{
			// You would pass the actual file path or asset GUID here
			string payload = Path.Combine(_currentDirectory, name);
			unsafe 
			{
				fixed (char* ptr = payload)
				{
					ImGui.SetDragDropPayload("CONTENT_BROWSER_ITEM", ptr, (uint)(payload.Length * sizeof(char)));
				}
			}
			ImGui.Text(name);
			ImGui.EndDragDropSource();
		}

		if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
		{
			onDoubleClick?.Invoke();
		}

		// Centered text
		ImGui.TextWrapped(name);

		ImGui.PopID();
	}
}