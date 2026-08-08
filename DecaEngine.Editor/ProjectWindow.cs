using System.Numerics;
using Engine.ImGui.Core;
using Hexa.NET.ImGui;

namespace DecaEngine.Editor
{
	/// <summary>
	/// Окно "Project" отвечает за конфигурацию текущего загруженного проекта: имя, версия,
	/// иконка и т.д. — то, что и положено проектному окну. Загрузка/сборка/запуск сборки
	/// проекта вынесены в <see cref="ProjectSession"/> (см. также <see cref="MenuBarWindow"/>,
	/// который инициирует загрузку проекта через сессию).
	/// </summary>
	public class ProjectWindow : ImGuiDockingWindow
	{
		private readonly ProjectSession _session;

		private ProjectConfig? _config;
		private string? _loadedProjectDirectory;

		private string _nameBuffer = string.Empty;
		private string _versionBuffer = string.Empty;
		private string _iconPathBuffer = string.Empty;
		private bool _isDirty;

		public ProjectWindow(string name, ProjectSession session, ImGuiRender imGuiRender) : base(name, imGuiRender)
		{
			_session = session;
		}

		protected override void OnRender(uint dockId)
		{
			var projectDirectory = _session.ProjectDirectory;

			if (string.IsNullOrEmpty(projectDirectory))
			{
				ImGui.TextDisabled("Load project (File → Import Project) to configure it.");
				return;
			}

			if (!string.Equals(_loadedProjectDirectory, projectDirectory, StringComparison.OrdinalIgnoreCase))
			{
				_config = ProjectConfig.Load(projectDirectory);
				_nameBuffer = _config.Name;
				_versionBuffer = _config.Version;
				_iconPathBuffer = _config.IconPath ?? string.Empty;
				_loadedProjectDirectory = projectDirectory;
				_isDirty = false;
			}

			ImGui.TextUnformatted("Configuration");
			ImGui.Spacing();

			if (ImGui.InputText("Name", ref _nameBuffer, 128))
			{
				_isDirty = true;
			}

			if (ImGui.InputText("Version", ref _versionBuffer, 32))
			{
				_isDirty = true;
			}

			if (ImGui.InputText("Icon", ref _iconPathBuffer, 260))
			{
				_isDirty = true;
			}

			ImGui.SameLine();
			if (ImGui.Button("..."))
			{
				// TODO: открыть диалог выбора файла иконки (см. OpenFolderDialog в MenuBarWindow
				// как пример существующей интеграции с Hexa.NET.ImGui.Widgets.Dialogs).
			}

			DrawIconPreview(projectDirectory);

			ImGui.Spacing();
			ImGui.BeginDisabled(!_isDirty);
			if (ImGui.Button("Save"))
			{
				SaveConfig(projectDirectory);
			}
			ImGui.EndDisabled();
		}

		private void DrawIconPreview(string projectDirectory)
		{
			if (string.IsNullOrWhiteSpace(_iconPathBuffer))
			{
				return;
			}

			var fullPath = Path.IsPathRooted(_iconPathBuffer)
				? _iconPathBuffer
				: Path.Combine(projectDirectory, _iconPathBuffer);

			if (!File.Exists(fullPath))
			{
				ImGui.TextColored(new Vector4(0.9f, 0.5f, 0.4f, 1f), "Icon file not found");
			}
		}

		private void SaveConfig(string projectDirectory)
		{
			_config ??= ProjectConfig.Load(projectDirectory);
			_config.Name = _nameBuffer;
			_config.Version = _versionBuffer;
			_config.IconPath = string.IsNullOrWhiteSpace(_iconPathBuffer) ? null : _iconPathBuffer;

			if (_config.Save())
			{
				_isDirty = false;
			}
		}
	}
}