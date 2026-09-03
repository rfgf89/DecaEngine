using System.Numerics;
using DecaEngine.Core.Diagnostics;
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

	// Runs off the render thread: the build shells out to dotnet and takes seconds. Safe because
	// the builder touches neither ImGui nor the scene, and EngineLog is thread-safe.
	private Task<string>? _buildTask;

	// Kept separately: the form fields may already be reset while the build runs.
	private string _buildingName = "";

	private readonly (ProjectTemplate Template, string Title, string Description, string Details)[] _templates =
	{
		(ProjectTemplate.Empty, "Empty Project",
			"Code only: a game class and a host. No assets.",
			"Empty scene"),
		(ProjectTemplate.AnimationSample, "Animation Sample",
			"Animation and physics demo scene: a ground with steps and a ramp, four characters " +
			"(a clip with spring bones and look-at, foot IK on steps, a limp and an active ragdoll), " +
			"a spot and a point light with shadows.",
			"Ground.glb + Fox.glb + scene prefab"),
	};

	public ModalNewProjectWindow(string title, ImGuiRender imGuiRender) : base(title, imGuiRender)
	{
		_editorBuilder = new EditorBuilder();
	}

	protected override void OnRender(uint dockId)
	{
		var viewportSize = ImGui.GetMainViewport().Size;

		PollBuild();

		// The whole form is disabled while building: a second Create would run dotnet over the first.
		ImGui.BeginDisabled(_saveFileDialog != null || _buildTask != null);

		ImGui.Text("Create a new project by filling in the details below:");
		ImGui.Separator();

		var availableSpace = ImGui.GetContentRegionAvail();
		float buttonAreaHeight = 35 * _scale;
		var contentHeight = (availableSpace.Y - buttonAreaHeight - 5 * _scale);
		var columnWidth = availableSpace.X * 0.4f;
		ImGui.BeginChild("LeftPanel", new Vector2(columnWidth, contentHeight), ImGuiChildFlags.Borders);
		{
			ImGui.TextColored(new Vector4(0.7f, 0.7f, 1.0f, 1.0f), "Project Settings");
			ImGui.Spacing();

			ImGui.Text("Project Name:");
			ImGui.SetNextItemWidth(-1);
			if (ImGui.InputText("##ProjectName", ref _projectName, 256)) ValidateProjectName();

			ImGui.Spacing();

			ImGui.Text("Description:");
			ImGui.SetNextItemWidth(-1);
			ImGui.InputTextMultiline("##ProjectDescription", ref _projectDescription, 512, new Vector2(-1, 196 * _scale), ImGuiInputTextFlags.WordWrap);

			ImGui.Spacing();
			ImGui.Separator();
			ImGui.Spacing();

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

		ImGui.SameLine();
		ImGui.BeginChild("RightPanel", new Vector2(0, contentHeight), ImGuiChildFlags.Borders);
		{
			ImGui.TextColored(new Vector4(0.7f, 0.7f, 1.0f, 1.0f), "Project Templates");
			ImGui.Spacing();

			if (ImGui.BeginListBox("##TemplatesCards", new Vector2(-1, 300 * _scale)))
			{
				ImGui.Spacing();
				for (var i = 0; i < _templates.Length; i++)
				{
					var isSelected = _selectedTemplateIndex == i;

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

			ImGui.TextColored(EditorSelectionStyle.Accent, "Template Details");
			ImGui.Spacing();

			ImGui.BeginChild("TemplatePreview", new Vector2(-1, -1), ImGuiChildFlags.Borders);
			{
				var selected = _templates[_selectedTemplateIndex];

				ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1.0f), selected.Title);

				ImGui.Spacing();
				ImGui.Separator();
				ImGui.Spacing();

				ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "Description:");
				ImGui.TextWrapped(selected.Description);

				ImGui.Spacing();
				ImGui.Separator();
				ImGui.Spacing();

				ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "Details:");
				ImGui.BulletText($"Assets: {selected.Details}");
			}
			ImGui.EndChild();
		}
		ImGui.EndChild();

		ImGui.Spacing();

		if (!string.IsNullOrEmpty(_validationMessage))
		{
			ImGui.TextColored(new Vector4(1.0f, 0.7f, 0.7f, _validationAlpha), _validationMessage);
		}

		if (_buildTask != null)
		{
			ImGui.TextColored(EditorSelectionStyle.Accent,
				$"Creating project '{_buildingName}' - restoring packages and references...");
			ImGui.TextDisabled("Details are in the Console window.");
		}

		ImGui.Spacing();

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

	// Field values are snapshotted here rather than read from the closure while the build runs.
	private void StartBuild()
	{
		string name = _projectName;
		string path = _projectPath;
		var template = _templates[_selectedTemplateIndex].Template;

		_buildingName = name;
		_validationMessage = "";

		EngineLog.Add(LogLevel.Info, $"Creating project '{name}' in {path} ...");

		// Progress goes to the editor console, not stdout: that is where it gets read.
		_buildTask = Task.Run(() => _editorBuilder.Build(name, path, template,
			message => EngineLog.Add(LogLevel.Info, message)));
	}

	// The result is handled on the render thread: closing the modal and editing fields is UI work.
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
			// The modal stays open so the name and path can be fixed and retried.
			var error = task.Exception?.GetBaseException();
			_validationMessage = $"Failed to create project: {error?.Message}";
			EngineLog.Add(LogLevel.Error, error?.ToString() ?? "Project creation failed");
			return;
		}

		EngineLog.Add(LogLevel.Info, $"Project created: {task.Result}");

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