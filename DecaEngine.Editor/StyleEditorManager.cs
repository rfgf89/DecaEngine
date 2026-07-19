using System.Numerics;
using Hexa.NET.ImGui;

namespace DecaEngine.Editor;

internal static class StyleEditorManager
{
	internal static void SetDarkThemeColors(float scale)
	{
		ImGuiStylePtr style = ImGui.GetStyle();
		var colors = style.Colors;

		// Основные цвета
		colors[(int)ImGuiCol.Text] = new Vector4(0.95f, 0.95f, 0.95f, 1.00f);
		colors[(int)ImGuiCol.TextDisabled] = new Vector4(0.50f, 0.50f, 0.50f, 1.00f);
		colors[(int)ImGuiCol.WindowBg] = new Vector4(0.1f, 0.1f, 0.1f, 0.8f);
		colors[(int)ImGuiCol.ChildBg] = new Vector4(0.20f, 0.20f, 0.20f, 0.95f);
		colors[(int)ImGuiCol.PopupBg] = new Vector4(0.15f, 0.15f, 0.15f, 1.00f);

		// Заголовки окон
		colors[(int)ImGuiCol.TitleBg] = new Vector4(0.10f, 0.10f, 0.10f, 1.00f);
		colors[(int)ImGuiCol.TitleBgCollapsed] = new Vector4(0.10f, 0.10f, 0.10f, 0.75f);
		colors[(int)ImGuiCol.TitleBgActive] = new Vector4(0.20f, 0.20f, 0.20f, 1.00f);

		// Вкладки
		colors[(int)ImGuiCol.Tab] = new Vector4(0.20f, 0.20f, 0.20f, 0.75f); // Светлее серый
		colors[(int)ImGuiCol.TabHovered] = new Vector4(0.40f, 0.40f, 0.40f, 0.75f); // Еще светлее серый
		colors[(int)ImGuiCol.TabSelected] = new Vector4(0.30f, 0.30f, 0.30f, 0.75f); // Тусклый темно-синий с серым оттенком
		colors[(int)ImGuiCol.TabSelectedOverline] = new Vector4(0.20f, 0.20f, 0.20f, 0.75f);
		colors[(int)ImGuiCol.TabDimmed] = new Vector4(0.10f, 0.10f, 0.10f, 0.75f); // Очень темный серый
		colors[(int)ImGuiCol.TabDimmedSelected] = new Vector4(0.20f, 0.20f, 0.20f, 0.75f);
		colors[(int)ImGuiCol.TabDimmedSelectedOverline] = new Vector4(0.10f, 0.10f, 0.10f, 0.75f);

		// Докинг
		colors[(int)ImGuiCol.DockingPreview] = new Vector4(0.10f, 0.10f, 0.10f, 0.75f); // Нейтральный темный серо-голубой

		// Заголовки элементов
		colors[(int)ImGuiCol.Header] = new Vector4(0.20f, 0.20f, 0.20f, 0.75f);
		colors[(int)ImGuiCol.HeaderHovered] = new Vector4(0.20f, 0.20f, 0.20f, 0.75f);
		colors[(int)ImGuiCol.HeaderActive] = new Vector4(0.20f, 0.20f, 0.20f, 0.75f);

		// Поля ввода и слайдеры
		colors[(int)ImGuiCol.FrameBg] = new Vector4(0.12f, 0.12f, 0.12f, 0.75f);
		colors[(int)ImGuiCol.FrameBgHovered] = new Vector4(0.25f, 0.25f, 0.25f, 0.75f);
		colors[(int)ImGuiCol.FrameBgActive] = new Vector4(0.30f, 0.30f, 0.30f, 0.75f);

		// Цвета для захвата слайдера
		colors[(int)ImGuiCol.SliderGrab] = new Vector4(0.20f, 0.20f, 0.20f, 0.75f);
		colors[(int)ImGuiCol.SliderGrabActive] = new Vector4(0.20f, 0.20f, 0.20f, 0.75f);

		// Цвет выделенного текста
		colors[(int)ImGuiCol.TextSelectedBg] = new Vector4(0.30f, 0.30f, 0.30f, 0.35f);

		// Кнопки
		colors[(int)ImGuiCol.Button] = new Vector4(0.15f, 0.15f, 0.15f, 0.90f);
		colors[(int)ImGuiCol.ButtonHovered] = new Vector4(0.25f, 0.25f, 0.25f, 0.90f);
		colors[(int)ImGuiCol.ButtonActive] = new Vector4(0.30f, 0.30f, 0.30f, 0.90f);

		colors[(int)ImGuiCol.CheckMark] = new Vector4(0.60f, 0.60f, 0.60f, 1.00f);

		colors[(int)ImGuiCol.ResizeGrip] = new Vector4(0.60f, 0.60f, 0.60f, 0.70f);
		colors[(int)ImGuiCol.ResizeGripHovered] = new Vector4(0.60f, 0.60f, 0.60f, 0.70f);
		colors[(int)ImGuiCol.ResizeGripActive] = new Vector4(0.60f, 0.60f, 0.60f, 0.70f);
		colors[(int)ImGuiCol.Border] = new Vector4(0.10f, 0.10f, 0.10f, 0.75f);
		colors[(int)ImGuiCol.ModalWindowDimBg] = new Vector4(0.05f, 0.05f, 0.05f, 0.80f);
		colors[(int)ImGuiCol.DockingEmptyBg] = new Vector4(0.10f, 0.10f, 0.10f, 0.75f);

		style.WindowBorderHoverPadding = 2.0f * scale;
		// Скругления, отступы и все размерные свойства (установлены в значения по умолчанию ImGui, масштабируемые)
		style.Alpha = 0.9f; // общий альфа (оставляем ваш множитель)
		style.WindowPadding = new Vector2(8f, 8f) * scale;
		style.WindowRounding = 7.0f * scale;
		style.WindowBorderSize = 1.0f * scale;
		style.WindowMinSize = new Vector2(32f, 32f) * scale;

		style.ChildRounding = 4.0f * scale;
		style.ChildBorderSize = 1.0f * scale;

		style.PopupRounding = 4.0f * scale;

		style.FramePadding = new Vector2(4f, 3f) * scale;
		style.FrameRounding = 3.0f * scale;
		style.FrameBorderSize = 0.0f * scale;

		style.ItemSpacing = new Vector2(8f, 4f) * scale;
		style.ItemInnerSpacing = new Vector2(4f, 4f) * scale;

		style.IndentSpacing = 21.0f * scale;
		style.ColumnsMinSpacing = 6.0f * scale;

		style.ScrollbarSize = 14.0f * scale;
		style.ScrollbarRounding = 9.0f * scale;

		style.GrabMinSize = 10.0f * scale;
		style.GrabRounding = 3.0f * scale;

		style.LogSliderDeadzone = 4.0f * scale;

		style.TabRounding = 4.0f * scale;
		style.TabBorderSize = 0.0f * scale;

		style.ButtonTextAlign = new Vector2(0.5f, 0.5f);
		style.SelectableTextAlign = new Vector2(0.0f, 0.0f);

		style.DisplayWindowPadding = new Vector2(22f, 22f) * scale;
		style.DisplaySafeAreaPadding = new Vector2(4f, 4f) * scale;

		style.MouseCursorScale = 1.0f * scale;

		style.AntiAliasedLines = true;
		style.AntiAliasedFill = true;
		style.CurveTessellationTol = 1.25f;

		style.SeparatorTextPadding = new Vector2(5f, 5f) * scale;
		style.SeparatorTextBorderSize = 1.0f * scale;
		style.DockingSeparatorSize = 4.0f * scale;

		style.FontSizeBase = scale * 18f;
	}
}