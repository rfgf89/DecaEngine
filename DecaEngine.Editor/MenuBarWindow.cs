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

	/// <summary>Проект, открытие которого сейчас идёт в фоне (см. LoadProjectFromSln): нужен, чтобы
	/// по завершении дописать его в недавние и в заголовок окна.</summary>
	private string? _pendingProjectSln;

	private bool _autoLoadAttempted;
	private bool _settingsAutoOpened;
	private int _autoOpenFrame;

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

		// Диагностический авто-опен модалок (DECA_AUTO_OPEN_SETTINGS=1 - Settings, =2 - New Project)
		// для headless-проверки UI скриншотом без кликов по меню. Задержка в кадрах - первые кадры
		// редактора создают/фокусируют док-окна, и ImGui закрывает открытую в этот момент модалку.
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

		// Открытие проекта идёт в фоне - его завершение подхватывается здесь, каждый кадр.
		PollProjectLoad();

		_newProjectWindow.Render(0);
		_settingsWindow.Render(0);

		if (ImGui.BeginMenuBar())
		{
			// Меню "File"
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
					// Чистка от удалённых с диска проектов - В МОМЕНТ ОТКРЫТИЯ меню, а не каждый
					// кадр: File.Exists на каждый пункт при отвалившемся сетевом диске - это
					// подвисание меню на каждом кадре, пока оно открыто.
					if (ImGui.IsWindowAppearing())
					{
						_recentProjects.Prune();
					}

					if (_recentProjects.Entries.Count == 0)
					{
						ImGui.TextDisabled("Нет недавних проектов");
					}
					else
					{
						var currentSlnPath = _projectSession.ProjectSlnPath;

						// Копия списка — LoadProjectFromSln может изменить _recentProjects.Entries
						// (переместить/добавить запись), поэтому нельзя итерировать исходную коллекцию.
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
								var tooltip = $"{entry.SlnPath}\nОткрыт: {entry.LastOpened:g}";
								if (isCurrent)
								{
									tooltip += "\n(уже загружен)";
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
					// Обработка выхода из приложения
				}

				ImGui.EndMenu();
			}

			// Меню "Edit"
			if (ImGui.BeginMenu("Edit"))
			{
				if (ImGui.MenuItem("Undo", "Ctrl+Z"))
				{
					// Обработка отмены
				}

				if (ImGui.MenuItem("Redo", "Ctrl+Y"))
				{
					// Обработка повторения
				}

				ImGui.Separator();
				if (ImGui.MenuItem("Cut"))
				{
					// Обработка вырезания
				}

				if (ImGui.MenuItem("Copy"))
				{
					// Обработка копирования
				}

				if (ImGui.MenuItem("Paste"))
				{
					// Обработка вставки
				}

				ImGui.Separator();
				if (ImGui.MenuItem("Preferences..."))
				{
					_settingsWindow.Show();
				}

				ImGui.EndMenu();
			}

			// Меню "View"
			if (ImGui.BeginMenu("View"))
			{
				if (ImGui.MenuItem("Copy"))
				{
					// Обработка копирования
				}

				if (ImGui.MenuItem("Paste"))
				{
					// Обработка вставки
				}

				ImGui.EndMenu();
			}

			// Меню "Window" — собирается из реестра DockLayout: каждое
			// зарегистрированное окно становится пунктом с галочкой.
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

			// Меню "Help"
			if (ImGui.BeginMenu("Help"))
			{
				if (ImGui.MenuItem("Documentation"))
				{
					// Открыть документацию
				}

				if (ImGui.MenuItem("About"))
				{
					// Открыть окно "О программе"
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

	/// <summary>Начинает открытие проекта. Дожидается его <see cref="PollProjectLoad"/> - открытие
	/// идёт в фоне (сборка проекта занимает минуты, см. ProjectSession.BeginLoadProject), и держать
	/// на нём кадр нельзя.</summary>
	private void LoadProjectFromSln(string slnPath)
	{
		_pendingProjectSln = slnPath;
		_projectSession.BeginLoadProject(slnPath);
	}

	/// <summary>Доводит открытие до конца и делает то, что зависит от результата: список недавних и
	/// заголовок окна. Звать каждый кадр.</summary>
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

