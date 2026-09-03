using System.Numerics;
using Hexa.NET.ImGui;

namespace DecaEngine.Editor;

internal static class StyleEditorManager
{
	internal static void SetDarkThemeColors(float scale)
	{
		ImGuiStylePtr style = ImGui.GetStyle();
		var colors = style.Colors;

		// Every colour is derived from EditorPalette (Preferences -> Appearance).
		var accent = EditorPalette.Accent;
		var background = EditorPalette.Background;
		var surface = EditorPalette.Surface;
		var text = EditorPalette.Text;
		var selection = EditorPalette.Selection;

		colors[(int)ImGuiCol.Text] = text;
		// Tint direction comes from text itself: on a light theme, darkening would raise contrast.
		colors[(int)ImGuiCol.TextDisabled] = EditorPalette.Tint(text, 0.45f, text);
		colors[(int)ImGuiCol.WindowBg] = EditorPalette.WithAlpha(background, 1.0f);
		colors[(int)ImGuiCol.ChildBg] = EditorPalette.WithAlpha(surface, 0.95f);
		colors[(int)ImGuiCol.PopupBg] = EditorPalette.WithAlpha(surface, 1.00f);

		colors[(int)ImGuiCol.TitleBg] = EditorPalette.WithAlpha(EditorPalette.Tint(background, 0.06f, background), 1.00f);
		colors[(int)ImGuiCol.TitleBgCollapsed] = EditorPalette.WithAlpha(background, 0.75f);
		// Lerp rather than Tint so the selection hue itself carries over into the focused title.
		colors[(int)ImGuiCol.TitleBgActive] = EditorPalette.WithAlpha(Vector4.Lerp(surface, selection, 0.55f), 1.00f);

		colors[(int)ImGuiCol.MenuBarBg] = EditorPalette.WithAlpha(background, 1.00f);

		colors[(int)ImGuiCol.Separator] = EditorPalette.WithAlpha(accent, 0.60f);
		colors[(int)ImGuiCol.SeparatorHovered] = EditorPalette.WithAlpha(selection, 0.60f);
		colors[(int)ImGuiCol.SeparatorActive] = EditorPalette.WithAlpha(selection, 1.00f);

		colors[(int)ImGuiCol.Tab] = EditorPalette.WithAlpha(surface, 0.75f);
		colors[(int)ImGuiCol.TabHovered] = EditorPalette.WithAlpha(accent, 0.30f);
		colors[(int)ImGuiCol.TabSelected] = EditorPalette.WithAlpha(accent, 0.35f);
		colors[(int)ImGuiCol.TabSelectedOverline] = EditorPalette.WithAlpha(selection, 1.00f);
		colors[(int)ImGuiCol.TabDimmed] = EditorPalette.WithAlpha(background, 0.75f);
		colors[(int)ImGuiCol.TabDimmedSelected] = EditorPalette.WithAlpha(surface, 0.75f);
		colors[(int)ImGuiCol.TabDimmedSelectedOverline] = EditorPalette.WithAlpha(background, 0.75f);

		colors[(int)ImGuiCol.DockingPreview] = EditorSelectionStyle.Fill;

		colors[(int)ImGuiCol.Header] = EditorSelectionStyle.Fill;
		colors[(int)ImGuiCol.HeaderHovered] = EditorSelectionStyle.Hover;
		colors[(int)ImGuiCol.HeaderActive] = EditorSelectionStyle.Active;

		colors[(int)ImGuiCol.FrameBg] = EditorPalette.WithAlpha(surface, 1.00f);
		colors[(int)ImGuiCol.FrameBgHovered] = EditorPalette.WithAlpha(EditorPalette.Tint(surface, 0.12f, background), 1.00f);
		colors[(int)ImGuiCol.FrameBgActive] = EditorPalette.WithAlpha(EditorPalette.Tint(surface, 0.20f, background), 1.00f);

		colors[(int)ImGuiCol.SliderGrab] = EditorPalette.WithAlpha(accent, 0.60f);
		colors[(int)ImGuiCol.SliderGrabActive] = EditorPalette.WithAlpha(accent, 1.00f);

		colors[(int)ImGuiCol.TextSelectedBg] = EditorSelectionStyle.Hover;

		colors[(int)ImGuiCol.Button] = EditorPalette.WithAlpha(EditorPalette.Tint(background, 0.05f, background), 0.90f);
		colors[(int)ImGuiCol.ButtonHovered] = EditorPalette.WithAlpha(EditorPalette.Tint(background, 0.15f, background), 0.90f);
		colors[(int)ImGuiCol.ButtonActive] = EditorPalette.WithAlpha(EditorPalette.Tint(background, 0.20f, background), 0.90f);

		colors[(int)ImGuiCol.CheckMark] = EditorPalette.WithAlpha(accent, 1.00f);

		colors[(int)ImGuiCol.ResizeGrip] = EditorPalette.WithAlpha(Vector4.Lerp(surface, selection, 0.55f), 0.40f);
		colors[(int)ImGuiCol.ResizeGripHovered] = EditorPalette.WithAlpha(Vector4.Lerp(surface, selection, 0.55f), 0.60f);
		colors[(int)ImGuiCol.ResizeGripActive] = EditorPalette.WithAlpha(Vector4.Lerp(surface, selection, 0.55f), 1.00f);
		colors[(int)ImGuiCol.Border] = EditorPalette.WithAlpha(background, 0.75f);

		colors[(int)ImGuiCol.ScrollbarBg] = EditorPalette.WithAlpha(background, 0.60f);
		colors[(int)ImGuiCol.ScrollbarGrab] = EditorPalette.WithAlpha(EditorPalette.Tint(surface, 0.15f, background), 0.80f);
		colors[(int)ImGuiCol.ScrollbarGrabHovered] = EditorPalette.WithAlpha(selection, 0.60f);
		colors[(int)ImGuiCol.ScrollbarGrabActive] = EditorPalette.WithAlpha(selection, 1.00f);

		// The modal dim must darken on light presets too, hence the luminance-driven amount.
		var dimAmount = MathF.Max(0.05f, EditorPalette.Luminance(background) - 0.05f);
		colors[(int)ImGuiCol.ModalWindowDimBg] = EditorPalette.WithAlpha(EditorPalette.Darken(background, dimAmount), 0.40f);
		colors[(int)ImGuiCol.DockingEmptyBg] = EditorPalette.WithAlpha(background, 1.0f);


		style.WindowBorderHoverPadding = 2.0f * scale;
		// Metrics below are the ImGui defaults, scaled by the DPI factor.
		style.Alpha = 1.0f;
		style.WindowPadding = new Vector2(8f, 8f) * scale;
		style.WindowRounding = 7.0f * scale;
		style.WindowBorderSize = 1.0f * scale;
		style.WindowMinSize = new Vector2(32f, 32f) * scale;

		style.ChildRounding = 4.0f * scale;
		style.ChildBorderSize = 0.5f * scale;

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
		style.DockingSeparatorSize = 2.0f * scale;

		style.FontSizeBase = scale * 18f;
	}
}