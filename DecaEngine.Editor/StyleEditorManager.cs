using System.Numerics;
using Hexa.NET.ImGui;

namespace DecaEngine.Editor;

internal static class StyleEditorManager
{
	internal static void SetDarkThemeColors(float scale)
	{
		ImGuiStylePtr style = ImGui.GetStyle();
		var colors = style.Colors;

		// Все цвета выводятся из главной палитры EditorPalette (см. Preferences → Appearance).
		var accent = EditorPalette.Accent;
		var background = EditorPalette.Background;
		var surface = EditorPalette.Surface;
		var text = EditorPalette.Text;
		var selection = EditorPalette.Selection;

		// Основные цвета
		colors[(int)ImGuiCol.Text] = text;
		// Direction (светлее/темнее) берётся от самого text, а не от фона: иначе на светлой теме
		// (тёмный text на светлом фоне) Darken(text) давал бы ещё более тёмный, то есть более
		// контрастный текст - визуально противоположное "disabled" ощущение.
		colors[(int)ImGuiCol.TextDisabled] = EditorPalette.Tint(text, 0.45f, text);
		colors[(int)ImGuiCol.WindowBg] = EditorPalette.WithAlpha(background, 1.0f);
		colors[(int)ImGuiCol.ChildBg] = EditorPalette.WithAlpha(surface, 0.95f);
		// Раньше PopupBg был лишь слегка (0.05) тонирован от Background - на светлых Base-пресетах
		// это давало попапу/контекстному меню почти тот же цвет, что и окно позади него ("всё белое").
		// Surface уже настроен с заметным контрастом к Background (см. BasePresets), поэтому попапы
		// используют его напрямую, как и обычные дочерние панели (ChildBg).
		colors[(int)ImGuiCol.PopupBg] = EditorPalette.WithAlpha(surface, 1.00f);

		// Заголовки окон
		// TitleBg раньше буквально совпадал с WindowBg (виден только контур), из-за чего заголовки
		// докнутых окон визуально сливались с их содержимым - особенно на светлых Base-пресетах.
		colors[(int)ImGuiCol.TitleBg] = EditorPalette.WithAlpha(EditorPalette.Tint(background, 0.06f, background), 1.00f);
		colors[(int)ImGuiCol.TitleBgCollapsed] = EditorPalette.WithAlpha(background, 0.75f);
		// TitleBgActive - это заголовок окна, находящегося в фокусе (то есть "выделенного" в
		// докинге, ImGui буквально называет его "Active"). Раньше он брался из Surface, никак не
		// связанного с Selection, из-за чего выделенное окно не подсвечивалось цветом выделения из
		// палитры. Теперь смешиваем Surface с Selection (Lerp, а не простой Tint по яркости - иначе
		// не переносился бы сам оттенок цвета выделения), сохраняя полную непрозрачность, чтобы
		// заголовок оставался читаемым фоном, а не полупрозрачной наложенной подсветкой.
		colors[(int)ImGuiCol.TitleBgActive] = EditorPalette.WithAlpha(Vector4.Lerp(surface, selection, 0.55f), 1.00f);

		// Меню-бар (раньше не переопределялся вообще и оставался дефолтным серо-синим цветом ImGui,
		// никак не связанным с палитрой) - продолжение фона окна/заголовка, поэтому берём Background.
		colors[(int)ImGuiCol.MenuBarBg] = EditorPalette.WithAlpha(background, 1.00f);

		// Разделители (ImGui.Separator() и любые кастомные сплиттеры, читающие эти цвета напрямую -
		// см. например сплиттер высоты панели Details в ConsoleWindow). Раньше тоже не
		// переопределялись и оставались дефолтными, не связанными с палитрой.
		colors[(int)ImGuiCol.Separator] = EditorPalette.WithAlpha(accent, 0.60f);
		colors[(int)ImGuiCol.SeparatorHovered] = EditorPalette.WithAlpha(selection, 0.60f);
		colors[(int)ImGuiCol.SeparatorActive] = EditorPalette.WithAlpha(selection, 1.00f);

		// Вкладки
		colors[(int)ImGuiCol.Tab] = EditorPalette.WithAlpha(surface, 0.75f);
		colors[(int)ImGuiCol.TabHovered] = EditorPalette.WithAlpha(accent, 0.30f);
		colors[(int)ImGuiCol.TabSelected] = EditorPalette.WithAlpha(accent, 0.35f);
		colors[(int)ImGuiCol.TabSelectedOverline] = EditorPalette.WithAlpha(selection, 1.00f);
		colors[(int)ImGuiCol.TabDimmed] = EditorPalette.WithAlpha(background, 0.75f);
		colors[(int)ImGuiCol.TabDimmedSelected] = EditorPalette.WithAlpha(surface, 0.75f);
		colors[(int)ImGuiCol.TabDimmedSelectedOverline] = EditorPalette.WithAlpha(background, 0.75f);

		// Докинг
		colors[(int)ImGuiCol.DockingPreview] = EditorSelectionStyle.Fill;

		// Заголовки элементов
		colors[(int)ImGuiCol.Header] = EditorSelectionStyle.Fill;
		colors[(int)ImGuiCol.HeaderHovered] = EditorSelectionStyle.Hover;
		colors[(int)ImGuiCol.HeaderActive] = EditorSelectionStyle.Active;

		// Поля ввода и слайдеры
		// Раньше фон полей ввода отличался от Background лишь на 0.02 (почти незаметно) и был
		// полупрозрачным (alpha 0.75) - визуально поле выглядело одинаково "серым" при любой теме,
		// будто вообще не подстраивается под палитру. Теперь берём Surface (уже настроен с заметным
		// контрастом к Background - см. BasePresets) с полной непрозрачностью - поле ввода явно
		// отличается от фона окна и корректно следует за текущим Base-пресетом.
		colors[(int)ImGuiCol.FrameBg] = EditorPalette.WithAlpha(surface, 1.00f);
		colors[(int)ImGuiCol.FrameBgHovered] = EditorPalette.WithAlpha(EditorPalette.Tint(surface, 0.12f, background), 1.00f);
		colors[(int)ImGuiCol.FrameBgActive] = EditorPalette.WithAlpha(EditorPalette.Tint(surface, 0.20f, background), 1.00f);

		// Цвета для захвата слайдера
		colors[(int)ImGuiCol.SliderGrab] = EditorPalette.WithAlpha(accent, 0.60f);
		colors[(int)ImGuiCol.SliderGrabActive] = EditorPalette.WithAlpha(accent, 1.00f);

		// Цвет выделенного текста
		colors[(int)ImGuiCol.TextSelectedBg] = EditorSelectionStyle.Hover;

		// Кнопки
		colors[(int)ImGuiCol.Button] = EditorPalette.WithAlpha(EditorPalette.Tint(background, 0.05f, background), 0.90f);
		colors[(int)ImGuiCol.ButtonHovered] = EditorPalette.WithAlpha(EditorPalette.Tint(background, 0.15f, background), 0.90f);
		colors[(int)ImGuiCol.ButtonActive] = EditorPalette.WithAlpha(EditorPalette.Tint(background, 0.20f, background), 0.90f);

		colors[(int)ImGuiCol.CheckMark] = EditorPalette.WithAlpha(accent, 1.00f);

		colors[(int)ImGuiCol.ResizeGrip] = EditorPalette.WithAlpha(Vector4.Lerp(surface, selection, 0.55f), 0.40f);
		colors[(int)ImGuiCol.ResizeGripHovered] = EditorPalette.WithAlpha(Vector4.Lerp(surface, selection, 0.55f), 0.60f);
		colors[(int)ImGuiCol.ResizeGripActive] = EditorPalette.WithAlpha(Vector4.Lerp(surface, selection, 0.55f), 1.00f);
		// Раньше Border был буквально равен Background - то есть сливался с ним и был практически
		// невидим (особенно заметно на светлых Base-пресетах и в попапах, где рамка окна вообще не
		// читалась). Теперь берём его через Tint от Surface - видимая, но не кричащая обводка.
		colors[(int)ImGuiCol.Border] = EditorPalette.WithAlpha(background, 0.75f);

		// Скроллбар (раньше вообще не переопределялся и оставался дефолтным серо-синим цветом ImGui,
		// никак не связанным с палитрой - особенно заметно в длинных попапах/списках со скроллом).
		colors[(int)ImGuiCol.ScrollbarBg] = EditorPalette.WithAlpha(background, 0.60f);
		colors[(int)ImGuiCol.ScrollbarGrab] = EditorPalette.WithAlpha(EditorPalette.Tint(surface, 0.15f, background), 0.80f);
		colors[(int)ImGuiCol.ScrollbarGrabHovered] = EditorPalette.WithAlpha(selection, 0.60f);
		colors[(int)ImGuiCol.ScrollbarGrabActive] = EditorPalette.WithAlpha(selection, 1.00f);

		// Затемнение фона позади модальных окон должно быть именно тёмным независимо от темы -
		// раньше это был Darken(background, 0.05f), который на светлых Base-пресетах (Background
		// близок к белому) почти не темнел и не давал эффекта затемнения вообще.
		var dimAmount = MathF.Max(0.05f, EditorPalette.Luminance(background) - 0.05f);
		colors[(int)ImGuiCol.ModalWindowDimBg] = EditorPalette.WithAlpha(EditorPalette.Darken(background, dimAmount), 0.40f);
		colors[(int)ImGuiCol.DockingEmptyBg] = EditorPalette.WithAlpha(background, 1.0f);


		style.WindowBorderHoverPadding = 2.0f * scale;
		// Скругления, отступы и все размерные свойства (установлены в значения по умолчанию ImGui, масштабируемые)
		style.Alpha = 1.0f; // общий альфа (оставляем ваш множитель)
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