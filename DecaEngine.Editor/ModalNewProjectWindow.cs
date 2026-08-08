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

	private readonly string[] _templates =
	{
		"Empty Project",
		"3D Scene",
		"2D Game",
		"UI Framework",
		"Custom"
	};

	private readonly Dictionary<string, string> _templateDescriptions = new()
	{
		{ "Empty Project", "Start with a blank project" },
		{ "3D Scene", "Pre-configured 3D project" },
		{ "2D Game", "Template for 2D game development" },
		{ "UI Framework", "Project with UI components" },
		{ "Custom", "Fully customizable configuration" }
	};

	public ModalNewProjectWindow(string title, ImGuiRender imGuiRender) : base(title, imGuiRender)
	{
		_editorBuilder = new EditorBuilder();
	}

	protected override void OnRender(uint dockId)
	{
		var viewportSize = ImGui.GetMainViewport().Size;

		ImGui.BeginDisabled(_saveFileDialog != null);

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

						if (ImGui.Button(_templates[i], new Vector2(-1, 64 * _scale)))
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
				ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1.0f), _templates[_selectedTemplateIndex]);

				ImGui.Spacing();
				ImGui.Separator();
				ImGui.Spacing();

				// Description with better formatting
				ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "Description:");
				ImGui.TextWrapped(_templateDescriptions[_templates[_selectedTemplateIndex]]);

				ImGui.Spacing();
				ImGui.Separator();
				ImGui.Spacing();

				// Template info
				ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "Details:");
				ImGui.BulletText($"Template: {_templates[_selectedTemplateIndex]}");
				ImGui.BulletText($"Type: {GetTemplateType(_selectedTemplateIndex)}");
				ImGui.BulletText($"Complexity: {GetTemplateComplexity(_selectedTemplateIndex)}");
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

		ImGui.Spacing();

		// Action buttons
		var buttonWidth = ((ImGui.GetContentRegionAvail().X - 10) / 3);

		if (ImGui.Button("Create Project", new Vector2(buttonWidth, 0)))
		{
			if (ValidateForm())
			{
				_editorBuilder.Build(_projectName, _projectPath);
				Close();
				ResetFields();
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

	private string GetTemplateType(int index)
	{
		return _templates[index] switch
		{
			"Empty Project" => "Minimal",
			"3D Scene" => "Advanced",
			"2D Game" => "Intermediate",
			"UI Framework" => "Intermediate",
			"Custom" => "Custom",
			_ => "Unknown"
		};
	}

	private string GetTemplateComplexity(int index)
	{
		return _templates[index] switch
		{
			"Empty Project" => "Low",
			"3D Scene" => "High",
			"2D Game" => "Medium",
			"UI Framework" => "Medium",
			"Custom" => "Variable",
			_ => "Unknown"
		};
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