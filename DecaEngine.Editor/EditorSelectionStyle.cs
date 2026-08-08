using System.Numerics;
using Hexa.NET.ImGui;

namespace DecaEngine.Editor
{
	public static class EditorSelectionStyle
	{
		public static Vector4 Accent => EditorPalette.Selection;

		public static Vector4 Fill => EditorPalette.WithAlpha(Accent, 0.35f);
		public static Vector4 Hover => EditorPalette.WithAlpha(Accent, 0.22f);
		public static Vector4 Active => EditorPalette.WithAlpha(Accent, 0.45f);

		public static void PushColors()
		{
			ImGui.PushStyleColor(ImGuiCol.Header, Fill);
			ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Hover);
			ImGui.PushStyleColor(ImGuiCol.HeaderActive, Active);
		}

		public static void PopColors() => ImGui.PopStyleColor(3);
	}
}

