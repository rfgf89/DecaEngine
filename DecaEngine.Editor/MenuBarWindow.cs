using Hexa.NET.ImGui;
using Hexa.NET.ImGui.Widgets.Dialogs;
using SDL;
using System.Numerics;
using Engine.ImGui.Core;

namespace DecaEngine.Editor;

public unsafe class MenuBarWindow : ImGuiMenuBarWindow
{
	private readonly EditorBuilder _editorBuilder;
	private readonly ModalNewProjectWindow _newProjectWindow;
	private readonly DockLayout _dockLayout;

	public MenuBarWindow(string title, DockLayout dockLayout, ImGuiRender imGuiRender) : base(title, imGuiRender)
	{
		_dockLayout = dockLayout;
		_editorBuilder = new EditorBuilder();
		_newProjectWindow = new ModalNewProjectWindow("New Project", imGuiRender);
	}

	public override void EndFirstFrame(uint dockId)
	{
		base.EndFirstFrame(dockId);

		_dockLayout.FirstFrame(dockId);
	}

	protected override void OnRender(uint dockId)
	{
		_newProjectWindow.Render(0);

		if (ImGui.BeginMenuBar())
		{
			// Меню "File"
			if (ImGui.BeginMenu("File"))
			{
				if (ImGui.MenuItem("New Scene", "Ctrl+N"))
				{
					// Обработка создания новой сцены
				}

				if (ImGui.MenuItem("Open Scene...", "Ctrl+O"))
				{
					// Обработка открытия сцены
				}

				if (ImGui.MenuItem("Save Scene", "Ctrl+S"))
				{
					_newProjectWindow.Show();
				}

				if (ImGui.MenuItem("Save Scene As..."))
				{
					// Обработка сохранения сцены с новым именем
				}

				ImGui.Separator();
				if (ImGui.MenuItem("Import Models"))
				{
					// Обработка импорта моделей
				}

				if (ImGui.MenuItem("Export Scene"))
				{
					// Обработка экспорта сцены
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
	}
}