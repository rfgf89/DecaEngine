using System.Numerics;
using DecaEngine.Core.Build;
using Engine.ImGui.Core;
using Hexa.NET.ImGui;
using Hexa.NET.ImGui.Widgets.Dialogs;

namespace DecaEngine.Editor
{
	public class ProjectWindow : ImGuiDockingWindow
	{
		private OpenFolderDialog? _openFolderDialog;
		private AssemblyApp? _assemblyApp;

		private string? _projectSlnPath;
		private string? _projectCsprojPath;
		private string _statusMessage = "Проект не загружен";
		private bool _isBusy;

		public ProjectWindow(string name, ImGuiRender imGuiRender) : base(name, imGuiRender)
		{
		}

		protected override void OnRender(uint dockId)
		{
			ImGui.BeginDisabled(_isBusy);

			if (ImGui.Button("Открыть проект..."))
			{
				_openFolderDialog = new OpenFolderDialog();
				_openFolderDialog.Show(OnProjectFolderSelected);
			}

			ImGui.SameLine();

			var state = _assemblyApp?.State ?? AssemblyAppState.NotLoaded;

			ImGui.BeginDisabled(state is AssemblyAppState.NotLoaded);
			if (ImGui.Button("Play"))
			{
				OnPlay();
			}

			ImGui.SameLine();
			ImGui.BeginDisabled(state is not (AssemblyAppState.Playing or AssemblyAppState.Paused));
			if (ImGui.Button("Pause"))
			{
				_assemblyApp?.Pause();
			}
			ImGui.EndDisabled();

			ImGui.SameLine();
			ImGui.BeginDisabled(state is not (AssemblyAppState.Playing or AssemblyAppState.Paused));
			if (ImGui.Button("Stop"))
			{
				OnStop();
			}
			ImGui.EndDisabled();
			ImGui.EndDisabled();

			ImGui.Separator();

			ImGui.TextWrapped(_projectSlnPath is null ? "Проект: (не выбран)" : $"Проект: {_projectSlnPath}");
			ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), _statusMessage);

			ImGui.EndDisabled();

			if (_openFolderDialog != null)
			{
				var viewportSize = ImGui.GetMainViewport().Size;
				ImGui.SetNextWindowSize(viewportSize * 0.33f);
				ImGui.SetNextWindowPos(viewportSize / 2 - _openFolderDialog.Size / 2);
				ImGui.SetNextWindowFocus();
				_openFolderDialog.Draw(ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoTitleBar);
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
				_statusMessage = "Папка проекта не найдена";
				return;
			}

			var slnFile = Directory.GetFiles(folder, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault()
				?? Directory.GetFiles(folder, "*.sln", SearchOption.AllDirectories).FirstOrDefault();

			if (slnFile is null)
			{
				_statusMessage = "В выбранной папке не найден .sln";
				return;
			}

			LoadProject(slnFile);
		}

		private void LoadProject(string slnPath)
		{
			// Останавливаем и выгружаем предыдущий проект, если он был загружен.
			if (_assemblyApp is not null && _assemblyApp.State != AssemblyAppState.NotLoaded)
			{
				_assemblyApp.Quit();
			}

			_isBusy = true;
			_statusMessage = "Загрузка проекта...";

			try
			{
				var slnDir = Path.GetDirectoryName(slnPath)!;
				var slnName = Path.GetFileNameWithoutExtension(slnPath);
				var csprojPath = Path.Combine(slnDir, $"{slnName}.csproj");

				if (!File.Exists(csprojPath))
				{
					// Резервный вариант: берём первый .csproj с тем же именем, что и папка проекта.
					csprojPath = Directory.GetFiles(slnDir, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault()
						?? throw new FileNotFoundException("Не найден .csproj проекта рядом с .sln", slnPath);
				}

				// Требование 2: при открытии проекта в редакторе синхронизируем
				// ссылки на все модули движка (кроме DecaEngine.Editor).
				EditorBuilder.AttachEngineReferences(csprojPath);

				var outputs = CsprojOutputResolver.GetBuildOutputs(csprojPath, buildIfMissing: true);
				var assemblyName = Path.GetFileNameWithoutExtension(csprojPath);
				var dllPath = outputs.FirstOrDefault(p =>
					string.Equals(Path.GetFileNameWithoutExtension(p), assemblyName, StringComparison.OrdinalIgnoreCase) &&
					Path.GetExtension(p).Equals(".dll", StringComparison.OrdinalIgnoreCase));

				if (dllPath is null)
				{
					_statusMessage = "Не удалось собрать проект (см. вывод консоли)";
					return;
				}

				_assemblyApp = new AssemblyApp();
				_assemblyApp.LoadFromPath(dllPath);

				_projectSlnPath = slnPath;
				_projectCsprojPath = csprojPath;
				_statusMessage = "Проект загружен, готов к запуску";
			}
			catch (Exception ex)
			{
				_statusMessage = $"Ошибка загрузки проекта: {ex.Message}";
			}
			finally
			{
				_isBusy = false;
			}
		}

		private void OnPlay()
		{
			if (_assemblyApp is null)
			{
				return;
			}

			if (_assemblyApp.State == AssemblyAppState.Stopped)
			{
				_assemblyApp.Run();
				_statusMessage = "Выполняется";
			}
			else if (_assemblyApp.State == AssemblyAppState.Paused)
			{
				_assemblyApp.Play();
				_statusMessage = "Выполняется";
			}
		}

		private void OnStop()
		{
			_assemblyApp?.Quit();
			_statusMessage = "Проект остановлен";

			// После Stop сборка выгружена из процесса — для повторного запуска
			// нужно загрузить проект заново.
			if (_projectCsprojPath != null)
			{
				LoadProject(_projectSlnPath!);
			}
		}
	}
}