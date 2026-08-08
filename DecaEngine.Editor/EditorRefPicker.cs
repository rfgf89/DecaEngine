using System.Numerics;
using DecaEngine.Core.Assets;
using Hexa.NET.ImGui;

namespace DecaEngine.Editor;

/// <summary>
/// Reusable picker for an <see cref="EditorRef"/>, populated from <see cref="EditorAssetDatabase"/>
/// and filtered by file extension (e.g. ".hlsl"). Mirrors ComponentFieldEditor's AssetRef slot
/// widget (filled background, file-type icon, current file name, clear button) so an editor-bundled
/// asset reads the same way a project asset does; clicking the slot opens a popup list instead of
/// accepting a drag&amp;drop, since EditorAssets isn't browsable from the Asset Browser.
/// </summary>
public static class EditorRefPicker
{
	/// <summary>Draws the picker for <paramref name="value"/>. Returns true if the selection changed.</summary>
	public static bool Draw(string label, ref EditorRef value, string extension)
	{
		var options = EditorAssetDatabase.GetByExtension(extension);
		bool changed = false;
		bool hasValue = !value.IsEmpty;

		var frameHeight = ImGui.GetFrameHeight();
		var clearWidth = hasValue ? frameHeight + ImGui.GetStyle().ItemSpacing.X : 0f;
		var slotWidth = MathF.Max(80f, ImGui.GetContentRegionAvail().X - clearWidth);
		var slotSize = new Vector2(slotWidth, frameHeight);

		ImGui.AlignTextToFramePadding();
		ImGui.TextUnformatted(label);
		ImGui.SameLine(0f, ImGui.GetStyle().ItemInnerSpacing.X);

		var slotMin = ImGui.GetCursorScreenPos();
		var slotMax = slotMin + slotSize;

		var clicked = ImGui.InvisibleButton($"##EditorRefSlot_{label}", slotSize);
		bool hovered = ImGui.IsItemHovered();

		var drawList = ImGui.GetWindowDrawList();
		var rounding = ImGui.GetStyle().FrameRounding;
		drawList.AddRectFilled(slotMin, slotMax, ImGui.GetColorU32(hovered ? ImGuiCol.FrameBgHovered : ImGuiCol.FrameBg), rounding);

		// Same file-type icon AssetBrowserWindow/ComponentFieldEditor's AssetRef slot draws, so the
		// slot reads as "an asset reference" at a glance instead of a plain text field.
		const float iconPadding = 2f;
		var iconMin = slotMin + new Vector2(iconPadding, iconPadding);
		var iconMax = new Vector2(iconMin.X + frameHeight - iconPadding * 2f, slotMax.Y - iconPadding);
		var iconKind = AssetBrowserWindow.GetFileIconKind(hasValue ? value.Path : extension);
		AssetBrowserWindow.DrawFileIcon(drawList, iconMin, iconMax, iconKind, 1f);

		var displayText = hasValue ? Path.GetFileName(value.Path) : "Select...";
		var textColor = ImGui.GetColorU32(hasValue ? ImGuiCol.Text : ImGuiCol.TextDisabled);
		var textMinX = iconMax.X + iconPadding * 2f;
		var textPos = new Vector2(textMinX, slotMin.Y + (frameHeight - ImGui.GetTextLineHeight()) * 0.5f);
		drawList.PushClipRect(new Vector2(textMinX, slotMin.Y), slotMax, true);
		drawList.AddText(textPos, textColor, displayText);
		drawList.PopClipRect();

		var borderColor = ImGui.GetColorU32(EditorPalette.WithAlpha(EditorPalette.Selection, hovered ? 0.9f : 0.55f));
		drawList.AddRect(slotMin, slotMax, borderColor, rounding, ImDrawFlags.None, hovered ? 2f : 1.5f);

		if (hovered && hasValue)
		{
			ImGui.SetTooltip(value.Path);
		}

		var popupId = $"##EditorRefPopup_{label}";
		if (clicked)
		{
			ImGui.OpenPopup(popupId);
		}

		if (ImGui.BeginPopup(popupId))
		{
			for (var i = 0; i < options.Count; i++)
			{
				var option = options[i];
				var isSelected = string.Equals(option, value.Path, StringComparison.OrdinalIgnoreCase);

				var lineHeight = ImGui.GetFrameHeight();
				var cursor = ImGui.GetCursorScreenPos();
				var optionIconMin = cursor + new Vector2(iconPadding, iconPadding);
				var optionIconMax = new Vector2(optionIconMin.X + lineHeight - iconPadding * 2f, cursor.Y + lineHeight - iconPadding);
				AssetBrowserWindow.DrawFileIcon(ImGui.GetWindowDrawList(), optionIconMin, optionIconMax, AssetBrowserWindow.GetFileIconKind(option), 1f);

				ImGui.Indent(lineHeight + iconPadding * 2f);
				if (ImGui.Selectable(option, isSelected))
				{
					value = option;
					changed = true;
				}
				ImGui.Unindent(lineHeight + iconPadding * 2f);

				if (isSelected)
				{
					ImGui.SetItemDefaultFocus();
				}
			}

			ImGui.EndPopup();
		}

		if (hasValue)
		{
			ImGui.SameLine(0f, ImGui.GetStyle().ItemSpacing.X);
			if (ImGui.Button($"x##EditorRefClear_{label}"))
			{
				value = default;
				changed = true;
			}
		}

		return changed;
	}
}
