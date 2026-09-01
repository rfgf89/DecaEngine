using System.Numerics;
using Hexa.NET.ImGui;
using Hexa.NET.ImGui.Widgets.Dialogs;
using Engine.ImGui.Core;

namespace DecaEngine.Editor;

public class ModalNewProjectWindow : ImGuiModalWindow
{
	private string _projectName = "";
	private string _projectPath = "";
	private string _projectDescription = "";
	private readonly EditorBuilder _editorBuilder;
	private int _selectedTemplateIndex = 0;
	private bool _createGitRepository = false;
	private bool _initializeGit = false;
	private float _validationAlpha = 1.0f;
	private string _validationMessage = "";
	private OpenFolderDialog? _saveFileDialog;

	/// <summary>
	/// Идущее создание проекта. В ФОНЕ, потому что оно запускает dotnet десяток раз и занимает
	/// секунды: синхронный вызов прямо из отрисовки останавливал не модалку, а ВЕСЬ редактор -
	/// кадры не шли, окно переставало отвечать, и снаружи это неотличимо от зависания.
	///
	/// Фоновый поток здесь безопасен: сборщик не трогает ни ImGui, ни сцену, а журнал редактора
	/// потокобезопасен (см. EditorConsoleLog.AddInternal - запись под замком).
	/// </summary>
	private Task<string>? _buildTask;

	/// <summary>Имя проекта, который сейчас создаётся, - для сообщения о ходе: поля к этому моменту
	/// уже могут быть сброшены.</summary>
	private string _buildingName = "";

	/// <summary>
	/// Шаблоны, КОТОРЫЕ ЕСТЬ. Раньше список был декоративным: «2D Game» и «UI Framework» создавали
	/// ровно тот же пустой проект, что и «Empty», потому что сборщик про выбор не знал вовсе.
	/// Пункт, который ничего не делает, хуже его отсутствия - он обещает содержимое, которого нет, и
	/// разбираться, почему проект пустой, идут в ассеты, а не в список шаблонов.
	/// </summary>
	private readonly (ProjectTemplate Template, string Title, string Description, string Details)[] _templates =
	{
		(ProjectTemplate.Empty, "Empty Project",
			"Только код: игровой класс и хост. Ассетов нет.",
			"Пустая сцена"),
		(ProjectTemplate.AnimationSample, "Animation Sample",
			"Демо-сцена анимации и физики: площадка со ступенями и пандусом, четыре персонажа " +
			"(клип со spring bones и look-at, foot IK на ступенях, тряпичный и active рэгдолл), " +
			"spot и point со тенями.",
			"Ground.glb + Fox.glb + префаб сцены"),
	};

	public ModalNewProjectWindow(string title, ImGuiRender imGuiRender) : base(title, imGuiRender)
	{
		_editorBuilder = new EditorBuilder();
	}

	protected override void OnRender(uint dockId)
	{
		var viewportSize = ImGui.GetMainViewport().Size;

		PollBuild();

		// Пока идёт создание, форма заблокирована целиком: правка полей на него уже не влияет, а
		// повторное нажатие «Create Project» запустило бы второй dotnet поверх первого - в тот же
		// каталог.
		ImGui.BeginDisabled(_saveFileDialog != null || _buildTask != null);

		ImGui.Text("Create a new project by filling in the details below:");
		ImGui.Separator();

		// Calculate available space for content (excluding buttons at bottom)
		var availableSpace = ImGui.GetContentRegionAvail();
		float buttonAreaHeight = 35 * _scale; // Height for buttons and spacing
		var contentHeight = (availableSpace.Y - buttonAreaHeight - 5 * _scale);
		// Left column - main settings
		var columnWidth = availableSpace.X * 0.4f;
		ImGui.BeginChild("LeftPanel", new Vector2(columnWidth, contentHeight), ImGuiChildFlags.Borders);
		{
			ImGui.TextColored(new Vector4(0.7f, 0.7f, 1.0f, 1.0f), "Project Settings");
			ImGui.Spacing();

			// Project name
			ImGui.Text("Project Name:");
			ImGui.SetNextItemWidth(-1);
			if (ImGui.InputText("##ProjectName", ref _projectName, 256)) ValidateProjectName();

			ImGui.Spacing();

			// Project description
			ImGui.Text("Description:");
			ImGui.SetNextItemWidth(-1);
			ImGui.InputTextMultiline("##ProjectDescription", ref _projectDescription, 512, new Vector2(-1, 196 * _scale), ImGuiInputTextFlags.WordWrap);

			ImGui.Spacing();
			ImGui.Separator();
			ImGui.Spacing();

			// Save location
			ImGui.Text("Save Location:");
			ImGui.SetNextItemWidth((-1 - 164) * _scale);
			ImGui.InputText("##ProjectPath", ref _projectPath, 512);
			ImGui.SameLine();
			if (ImGui.Button("Browse...", new Vector2(148 * _scale, 0)))
			{
				_saveFileDialog = new OpenFolderDialog();
				_saveFileDialog.Show(SaveProjectCallback);
			}

			ImGui.Spacing();
			ImGui.Separator();
			ImGui.Spacing();

			// Options
			ImGui.TextColored(EditorSelectionStyle.Accent, "Options");
			ImGui.Spacing();

			ImGui.Checkbox("Initialize Git Repository", ref _createGitRepository);
			if (_createGitRepository)
			{
				ImGui.Indent();
				ImGui.Checkbox("Create .gitignore", ref _initializeGit);
				ImGui.Unindent();
			}

			ImGui.Spacing();
		}
		ImGui.EndChild();

		// Right column - template selection
		ImGui.SameLine();
		ImGui.BeginChild("RightPanel", new Vector2(0, contentHeight), ImGuiChildFlags.Borders);
		{
			ImGui.TextColored(new Vector4(0.7f, 0.7f, 1.0f, 1.0f), "Project Templates");
			ImGui.Spacing();

			// Template cards layout
			if (ImGui.BeginListBox("##TemplatesCards", new Vector2(-1, 300 * _scale)))
			{
				ImGui.Spacing();
				for (var i = 0; i < _templates.Length; i++)
				{
					var isSelected = _selectedTemplateIndex == i;

					// Template card
					ImGui.BeginGroup();
					{
						ImGui.PushStyleColor(ImGuiCol.Button, isSelected ? new Vector4(0.3f, 0.5f, 0.9f, 0.8f) : new Vector4(0.2f, 0.2f, 0.2f, 0.6f));

						if (ImGui.Button(_templates[i].Title, new Vector2(-1, 64 * _scale)))
						{
							_selectedTemplateIndex = i;
						}

						ImGui.PopStyleColor();
					}
					ImGui.EndGroup();

					ImGui.Spacing();
				}

				ImGui.EndListBox();
			}

			ImGui.Spacing();
			ImGui.Separator();
			ImGui.Spacing();

			// Template preview panel
			ImGui.TextColored(EditorSelectionStyle.Accent, "Template Details");
			ImGui.Spacing();

			ImGui.BeginChild("TemplatePreview", new Vector2(-1, -1), ImGuiChildFlags.Borders);
			{
				var selected = _templates[_selectedTemplateIndex];

				ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1.0f), selected.Title);

				ImGui.Spacing();
				ImGui.Separator();
				ImGui.Spacing();

				// Description with better formatting
				ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "Description:");
				ImGui.TextWrapped(selected.Description);

				ImGui.Spacing();
				ImGui.Separator();
				ImGui.Spacing();

				// Template info
				ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "Details:");
				ImGui.BulletText($"Assets: {selected.Details}");
			}
			ImGui.EndChild();
		}
		ImGui.EndChild();

		ImGui.Spacing();

		// Validation message
		if (!string.IsNullOrEmpty(_validationMessage))
		{
			ImGui.TextColored(new Vector4(1.0f, 0.7f, 0.7f, _validationAlpha), _validationMessage);
		}

		if (_buildTask != null)
		{
			// Явное сообщение о ходе - не украшение: создание идёт секунды, и без него единственный
			// признак работы - это то, что окно не закрылось, а он читается как «кнопка не нажалась».
			ImGui.TextColored(EditorSelectionStyle.Accent,
				$"Создаётся проект '{_buildingName}' - идёт восстановление пакетов и ссылок...");
			ImGui.TextDisabled("Подробности - в окне Console.");
		}

		ImGui.Spacing();

		// Action buttons
		var buttonWidth = ((ImGui.GetContentRegionAvail().X - 10) / 3);

		if (ImGui.Button("Create Project", new Vector2(buttonWidth, 0)))
		{
			if (ValidateForm())
			{
				StartBuild();
			}
		}

		ImGui.SameLine(0, 5 * _scale);

		if (ImGui.Button("Load Existing", new Vector2(buttonWidth, 0)))
		{
			// Open dialog to load existing project
		}

		ImGui.SameLine(0, 5 * _scale);

		if (ImGui.Button("Cancel", new Vector2(buttonWidth, 0)))
		{
			Close();
			ResetFields();
		}

		ImGui.EndDisabled();

		if (_saveFileDialog != null)
		{
			var style = ImGui.GetStyle();
			_imGuiRender.PushFont(FontType.MaterialSymbols, 15 * _scale);
			ImGui.SetNextWindowSize(viewportSize * 0.33f);
			ImGui.SetNextWindowPos(viewportSize / 2 - _saveFileDialog.Size / 2);
			ImGui.SetNextWindowFocus();
			_saveFileDialog.Draw(ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoTitleBar);
			_imGuiRender.PopFont();
		}
	}

	/// <summary>Запускает создание проекта в фоне. Значения полей СНИМАЮТСЯ здесь, до старта: форма
	/// заблокирована, но копировать её состояние в замыкание надёжнее, чем полагаться на
	/// блокировку.</summary>
	private void StartBuild()
	{
		string name = _projectName;
		string path = _projectPath;
		var template = _templates[_selectedTemplateIndex].Template;

		_buildingName = name;
		_validationMessage = "";

		EditorConsoleLog.Add(LogLevel.Info, $"Создание проекта '{name}' в {path} ...");

		// Ход генерации - в консоль РЕДАКТОРА: сборщик умеет сообщать о проблемах («модель не
		// найдена», «площадка не собралась»), и в редакторе их читают там, а не в стандартном
		// выводе процесса.
		_buildTask = Task.Run(() => _editorBuilder.Build(name, path, template,
			message => EditorConsoleLog.Add(LogLevel.Info, message)));
	}

	/// <summary>Проверяет, не закончилось ли создание. Результат разбирается ЗДЕСЬ, в потоке
	/// отрисовки: закрытие модалки и правка полей - работа UI, и делать её из фонового потока
	/// нельзя.</summary>
	private void PollBuild()
	{
		if (_buildTask is not { IsCompleted: true })
		{
			return;
		}

		var task = _buildTask;
		_buildTask = null;

		if (task.IsFaulted)
		{
			// Модалка НЕ закрывается: пути и имя остаются на месте, чтобы можно было поправить и
			// повторить, не набирая всё заново.
			var error = task.Exception?.GetBaseException();
			_validationMessage = $"Не удалось создать проект: {error?.Message}";
			EditorConsoleLog.Add(LogLevel.Error, error?.ToString() ?? "Project creation failed");
			return;
		}

		EditorConsoleLog.Add(LogLevel.Info, $"Проект создан: {task.Result}");

		Close();
		ResetFields();
	}

	private void ValidateProjectName()
	{
		if (string.IsNullOrWhiteSpace(_projectName))
		{
			_validationMessage = "";
			return;
		}

		char[] invalidChars = Path.GetInvalidFileNameChars();
		if (_projectName.Any(c => invalidChars.Contains(c)))
		{
			_validationMessage = "⚠ Project name contains invalid characters";
		}
		else
		{
			_validationMessage = "";
		}
	}

	private bool ValidateForm()
	{
		if (string.IsNullOrWhiteSpace(_projectName))
		{
			_validationMessage = "Project name is required";
			return false;
		}

		if (string.IsNullOrWhiteSpace(_projectPath))
		{
			_validationMessage = "Save path is required";
			return false;
		}

		if (!Directory.Exists(_projectPath))
		{
			_validationMessage = "Save path does not exist";
			return false;
		}

		char[] invalidChars = Path.GetInvalidFileNameChars();
		if (_projectName.Any(c => invalidChars.Contains(c)))
		{
			_validationMessage = "Project name contains invalid characters";
			return false;
		}

		string fullPath = Path.Combine(_projectPath, _projectName);
		if (Directory.Exists(fullPath))
		{
			_validationMessage = "Project already exists at this location";
			return false;
		}

		_validationMessage = "";
		return true;
	}

	private void ResetFields()
	{
		_projectName = "";
		_projectPath = "";
		_projectDescription = "";
		_selectedTemplateIndex = 0;
		_createGitRepository = false;
		_initializeGit = false;
		_validationMessage = "";
	}

	private void SaveProjectCallback(object? sender, DialogResult result)
	{
		_saveFileDialog = null;
		if (result != DialogResult.Ok || sender is not OpenFolderDialog dialog)
		{
			return;
		}

		_projectPath = dialog.SelectedFolder ?? _projectPath;
	}
}