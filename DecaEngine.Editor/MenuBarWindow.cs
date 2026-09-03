using Hexa.NET.ImGui;
using Hexa.NET.ImGui.Widgets.Dialogs;
using System.Linq;
using System.Numerics;
using DecaEngine.Core;
using Engine.ImGui.Core;

namespace DecaEngine.Editor;

public unsafe class MenuBarWindow : ImGuiMenuBarWindow
{
	private readonly EditorBuilder _editorBuilder;
	private readonly ModalNewProjectWindow _newProjectWindow;
	private readonly SettingsWindow _settingsWindow;
	private readonly DockLayout _dockLayout;
	private readonly IWindowHandle _windowHandle;
	private readonly RecentProjectsManager _recentProjects;
	private readonly ProjectSession _projectSession;
	private readonly EditorSettings _editorSettings;

	private OpenFolderDialog? _openFolderDialog;

	// Project currently loading in the background; recorded on completion.
	private string? _pendingProjectSln;

	private bool _autoLoadAttempted;
	private bool _settingsAutoOpened;
	private int _autoOpenFrame;

	/// <summary>Target of the Edit menu. A property, not a ctor argument: the menu bar is created
	/// before the inspector; null keeps the menu items disabled.</summary>
	public InspectorWindow? Inspector { get; set; }

	public MenuBarWindow(string title, DockLayout dockLayout, IWindowHandle windowHandle, ProjectSession projectSession, EditorSettings editorSettings, ImGuiRender imGuiRender) : base(title, imGuiRender)
	{
		_dockLayout = dockLayout;
		_windowHandle = windowHandle;
		_projectSession = projectSession;
		_editorSettings = editorSettings;
		_editorBuilder = new EditorBuilder();
		_newProjectWindow = new ModalNewProjectWindow("New Project", imGuiRender);
		_settingsWindow = new SettingsWindow("Settings", editorSettings, imGuiRender);
		_recentProjects = new RecentProjectsManager();
	}

	public override void EndFirstFrame(uint dockId)
	{
		base.EndFirstFrame(dockId);

		_dockLayout.FirstFrame(dockId);
	}

	protected override void OnRender(uint dockId)
	{
		TryAutoLoadLastProject();

		// DECA_AUTO_OPEN_SETTINGS opens a modal for headless screenshot checks (1 - Settings,
		// 2 - New Project). The frame delay is required: early frames dock windows and ImGui
		// closes any modal opened during that.
		if (!_settingsAutoOpened && ++_autoOpenFrame == 150)
		{
			var autoOpen = Environment.GetEnvironmentVariable("DECA_AUTO_OPEN_SETTINGS");
			if (autoOpen == "1")
			{
				_settingsAutoOpened = true;
				_settingsWindow.Show();
			}
			else if (autoOpen == "2")
			{
				_settingsAutoOpened = true;
				_newProjectWindow.Show();
			}
		}

		PollProjectLoad();

		_newProjectWindow.Render(0);
		_settingsWindow.Render(0);

		if (ImGui.BeginMenuBar())
		{
			if (ImGui.BeginMenu("File"))
			{
				if (ImGui.MenuItem("New Project", "Ctrl+S"))
				{
					_newProjectWindow.Show();
				}

				if (ImGui.MenuItem("Import Project"))
				{
					_openFolderDialog = new OpenFolderDialog();
					_openFolderDialog.Show(OnProjectFolderSelected);
				}

				if (ImGui.BeginMenu("Recently Project"))
				{
					// Prune only when the menu opens: a per-frame File.Exists over every entry
					// stalls the UI on a dead network drive.
					if (ImGui.IsWindowAppearing())
					{
						_recentProjects.Prune();
					}

					if (_recentProjects.Entries.Count == 0)
					{
						ImGui.TextDisabled("No recent projects");
					}
					else
					{
						var currentSlnPath = _projectSession.ProjectSlnPath;

						// Iterate a copy: LoadProjectFromSln mutates _recentProjects.Entries.
						string? clickedSlnPath = null;
						foreach (var entry in _recentProjects.Entries.ToArray())
						{
							var isCurrent = _projectSession.State != AssemblyAppState.NotLoaded
								&& string.Equals(currentSlnPath, entry.SlnPath, StringComparison.OrdinalIgnoreCase);

							var label = $"{entry.Name}##{entry.SlnPath}";

							ImGui.BeginDisabled(isCurrent);
							if (ImGui.MenuItem(label))
							{
								clickedSlnPath = entry.SlnPath;
							}
							ImGui.EndDisabled();

							if (ImGui.IsItemHovered())
							{
								var tooltip = $"{entry.SlnPath}\nOpened: {entry.LastOpened:g}";
								if (isCurrent)
								{
									tooltip += "\n(already loaded)";
								}
								ImGui.SetTooltip(tooltip);
							}
						}

						if (clickedSlnPath != null)
						{
							LoadProjectFromSln(clickedSlnPath);
						}
					}

					ImGui.EndMenu();
				}

				ImGui.Separator();
				if (ImGui.MenuItem("Exit"))
				{
				}

				ImGui.EndMenu();
			}

			if (ImGui.BeginMenu("Edit"))
			{
				var inspector = Inspector;

				if (ImGui.MenuItem("Undo", "Ctrl+Z", false, inspector?.CanUndo == true))
				{
					inspector!.Undo();
				}

				if (ImGui.MenuItem("Redo", "Ctrl+Y", false, inspector?.CanRedo == true))
				{
					inspector!.Redo();
				}

				ImGui.Separator();

				bool hasSelection = inspector?.HasPrefabSelection == true;

				if (ImGui.MenuItem("Cut", "Ctrl+X", false, hasSelection))
				{
					inspector!.CutSelected();
				}

				if (ImGui.MenuItem("Copy", "Ctrl+C", false, hasSelection))
				{
					inspector!.CopySelected();
				}

				if (ImGui.MenuItem("Paste", "Ctrl+V", false, inspector?.CanPasteEntity == true))
				{
					inspector!.PasteIntoSelected();
				}

				if (ImGui.MenuItem("Duplicate", "Ctrl+D", false, hasSelection))
				{
					inspector!.DuplicateSelected();
				}

				ImGui.Separator();
				if (ImGui.MenuItem("Preferences..."))
				{
					_settingsWindow.Show();
				}

				ImGui.EndMenu();
			}

			// Built from the DockLayout registry: every registered window becomes an item.
			if (ImGui.BeginMenu("Window"))
			{
				foreach (var windowType in _imGuiRender.windowGetters.Keys)
				{
					if (ImGui.MenuItem(windowType.ToString()))
					{
						var window = _imGuiRender.InstanceWindowGetter(windowType);
						_dockLayout.AddDockLayoutElement(new DockLayoutElement { name = window.Title, imGuiDir = ImGuiDir.Left, ratio = 0.20f });
						window.Show();
					}
				}

				ImGui.EndMenu();
			}

			if (ImGui.BeginMenu("Help"))
			{
				if (ImGui.MenuItem("Documentation"))
				{
				}

				if (ImGui.MenuItem("About"))
				{
				}

				ImGui.EndMenu();
			}

			ImGui.EndMenuBar();
		}

		_dockLayout.Render(dockId);

		if (_openFolderDialog != null)
		{
			var viewportSize = ImGui.GetMainViewport().Size;
			ImGui.SetNextWindowSize(viewportSize * 0.33f);
			ImGui.SetNextWindowPos(viewportSize / 2 - _openFolderDialog.Size / 2);
			ImGui.SetNextWindowFocus();
			_openFolderDialog.Draw(ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoTitleBar);
		}
	}

	private void TryAutoLoadLastProject()
	{
		if (_autoLoadAttempted)
		{
			return;
		}

		_autoLoadAttempted = true;

		if (!_editorSettings.AutoLoadLastProject)
		{
			return;
		}

		var lastProject = _recentProjects.Entries.FirstOrDefault();
		if (lastProject is not null && File.Exists(lastProject.SlnPath))
		{
			LoadProjectFromSln(lastProject.SlnPath);
		}
	}

	private void OnProjectFolderSelected(object? sender, DialogResult result)
	{
		_openFolderDialog = null;
		if (result != DialogResult.Ok || sender is not OpenFolderDialog dialog)
		{
			return;
		}

		var folder = dialog.SelectedFolder;
		if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
		{
			return;
		}

		var slnFile = Directory.GetFiles(folder, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault()
			?? Directory.GetFiles(folder, "*.sln", SearchOption.AllDirectories).FirstOrDefault();

		if (slnFile is null)
		{
			return;
		}

		LoadProjectFromSln(slnFile);
	}

	// Loading runs in the background (a project build takes minutes); PollProjectLoad finishes it.
	private void LoadProjectFromSln(string slnPath)
	{
		_pendingProjectSln = slnPath;
		_projectSession.BeginLoadProject(slnPath);
	}

	// Must be called every frame.
	private void PollProjectLoad()
	{
		if (!_projectSession.PollLoad())
		{
			return;
		}

		var slnPath = _pendingProjectSln;
		_pendingProjectSln = null;

		if (slnPath is not null && _projectSession.State != AssemblyAppState.NotLoaded)
		{
			_recentProjects.Add(slnPath);
			_windowHandle.SetTitle($"{_projectSession.DisplayName} - DecaEngine Editor");
		}
	}
}

