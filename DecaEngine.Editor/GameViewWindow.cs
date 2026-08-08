using System.Numerics;
using DecaEngine.Core;
using Hexa.NET.ImGui;

namespace DecaEngine.Editor
{
	public class GameViewWindow : ImGuiDockingWindow
	{
		private const float IconShapeRounding = 0.5f;
		private const float ToolbarButtonRounding = 2f;
		private static readonly Vector2 ToolbarButtonSizeBase = new(48f, 22f);
		private const float ToolbarSpacingBase = 6f;

		// Единая палитра стиля подсказок (DrawStateOverlay), которой также следуют кнопки тулбара GameView.
		// Фон оверлея/тулбара выводится из EditorPalette.Background (а не захардкожен), чтобы тема из
		// Preferences → Appearance применялась и здесь. Play/Pause/Stop - семантические цвета состояния
		// (зелёный/жёлтый/красный), намеренно не привязаны к теме - как и цвета уровней лога в ConsoleWindow.
		private static Vector4 OverlayBackground => EditorPalette.WithAlpha(EditorPalette.Darken(EditorPalette.Background, 0.02f), 0.78f);
		private static readonly Vector4 PlayAccent = new(0.35f, 0.85f, 0.45f, 1f);
		private static readonly Vector4 PauseAccent = new(1f, 0.75f, 0.2f, 1f);
		private static readonly Vector4 StopAccent = new(0.85f, 0.35f, 0.30f, 1f);

		private readonly IRenderHandle _renderHandle;
		private readonly ProjectSession _projectSession;
		private ImTextureRef _textureRef;
		private bool _textureBound;

		public GameViewWindow(string name, IRenderHandle renderHandle, ProjectSession projectSession, ImGuiRender imGuiRender) : base(name, imGuiRender)
		{
			_renderHandle = renderHandle;
			_projectSession = projectSession;
		}

		protected override ImGuiWindowFlags AdditionalWindowFlags => ImGuiWindowFlags.MenuBar;

		private enum OverlayIcon
		{
			None,
			Info,
			Stop,
			Pause,
			Play
		}

		protected override void OnRender(uint dockId)
		{
			DrawHeader();

			var state = _projectSession.State;
			var areaMin = ImGui.GetCursorScreenPos();
			var available = ImGui.GetContentRegionAvail();

			if (state == AssemblyAppState.NotLoaded)
			{
				if (available.X > 0 && available.Y > 0)
				{
					DrawStateOverlay(areaMin, available, OverlayIcon.Info,
						EditorPalette.Darken(EditorPalette.Text, 0.45f),
						"No project loaded",
						"Open a project in the Project window.");
				}
				return;
			}

			if (state == AssemblyAppState.Stopped)
			{
				if (available.X > 0 && available.Y > 0)
				{
					DrawStateOverlay(areaMin, available, OverlayIcon.Stop,
						StopAccent,
						"Stopped",
						"Press Play to continue.");
				}
				return;
			}

			if (!_textureBound)
			{
				_textureRef = _imGuiRender.GetNewTexture();
				_imGuiRender.BindRenderTarget(_textureRef.GetTexID(), _renderHandle);
				_textureBound = true;
			}

			if (available.X > 0 && available.Y > 0)
			{
				ImGui.Image(_textureRef, available);
			}

			if (state == AssemblyAppState.Paused && available.X > 0 && available.Y > 0)
			{
				DrawStateOverlay(areaMin, available, OverlayIcon.Pause,
					PauseAccent,
					"Paused",
					"Press the Play to continue.");
			}
		}

		private void DrawHeader()
		{
			if (!ImGui.BeginMenuBar())
			{
				return;
			}

			var toolbarButtonSize = ToolbarButtonSizeBase * _scale;
			var spacing = ToolbarSpacingBase * _scale;

			DrawProjectLabel();

			var totalWidth = toolbarButtonSize.X * 3 + spacing * 2;
			var windowWidth = ImGui.GetWindowWidth();
			var centerX = (windowWidth - totalWidth) * 0.5f;

			ImGui.SameLine();
			if (centerX > ImGui.GetCursorPosX())
			{
				ImGui.SetCursorPosX(centerX);
			}

			var state = _projectSession.State;
			var hasProject = state != AssemblyAppState.NotLoaded;

			if (DrawToolbarIconButton("##GameViewPlay", toolbarButtonSize, OverlayIcon.Play, PlayAccent,
				    !hasProject || state == AssemblyAppState.Playing))
			{
				_projectSession.Play();
			}

			ImGui.SameLine(0, spacing);
			if (DrawToolbarIconButton("##GameViewPause", toolbarButtonSize, OverlayIcon.Pause, PauseAccent,
				    state is not AssemblyAppState.Playing))
			{
				_projectSession.Pause();
			}

			ImGui.SameLine(0, spacing);
			if (DrawToolbarIconButton("##GameViewStop", toolbarButtonSize, OverlayIcon.Stop, StopAccent,
				    state is not (AssemblyAppState.Playing or AssemblyAppState.Paused)))
			{
				_projectSession.Stop();
			}

			ImGui.EndMenuBar();
		}

		private void DrawProjectLabel()
		{
			var label = _projectSession.State != AssemblyAppState.NotLoaded ? _projectSession.DisplayName : string.Empty;

			ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetColorU32(OverlayBackground));
			ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ImGui.GetColorU32(EditorPalette.WithAlpha(EditorPalette.Lighten(EditorPalette.Background, 0.08f), 0.9f)));
			ImGui.PushStyleColor(ImGuiCol.ButtonActive, ImGui.GetColorU32(EditorPalette.WithAlpha(EditorPalette.Lighten(EditorPalette.Background, 0.12f), 0.95f)));
			ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, ToolbarButtonRounding * _scale);

			ImGui.Text($"{label}");

			ImGui.PopStyleVar();
			ImGui.PopStyleColor(3);
		}

		private static bool DrawToolbarIconButton(string id, Vector2 size, OverlayIcon icon, Vector4 accent, bool disabled)
		{
			ImGui.BeginDisabled(disabled);

			var cursor = ImGui.GetCursorScreenPos();
			var clicked = ImGui.InvisibleButton(id, size);
			var hovered = !disabled && ImGui.IsItemHovered();
			var active = !disabled && ImGui.IsItemActive();

			var drawList = ImGui.GetWindowDrawList();

			var bg = OverlayBackground;
			Vector4 displayAccent;

			if (disabled)
			{
				var gray = (accent.X + accent.Y + accent.Z) / 3f;
				displayAccent = new Vector4(gray, gray, gray, 0.35f);
				bg.W *= 0.55f;
			}
			else
			{
				displayAccent = accent;
				bg.W = active ? 0.95f : hovered ? 0.88f : bg.W;
			}

			var bgColor = ImGui.GetColorU32(bg);
			var accentColor = ImGui.GetColorU32(displayAccent);
			var rounding = ToolbarButtonRounding * (size.Y / ToolbarButtonSizeBase.Y);

			drawList.AddRectFilled(cursor, cursor + size, bgColor, rounding);

			if (!disabled)
			{
				drawList.AddRect(cursor, cursor + size, accentColor, rounding, ImDrawFlags.None, hovered || active ? 1.75f : 1.25f);
			}

			var iconRadius = MathF.Min(size.X, size.Y) * 0.28f;
			var center = cursor + size * 0.5f;
			DrawIcon(drawList, icon, center, iconRadius, accentColor);

			ImGui.EndDisabled();

			return clicked;
		}


		private static void DrawStateOverlay(Vector2 areaMin, Vector2 areaSize, OverlayIcon icon, Vector4 accent, string title, string? subtitle)
		{
			var drawList = ImGui.GetWindowDrawList();

			const float iconRadius = 7f;
			const float iconTextGap = 10f;
			var padding = new Vector2(18f, 12f);
			const float titleSubtitleGap = 4f;

			var titleSize = ImGui.CalcTextSize(title);
			var subtitleSize = subtitle is null ? Vector2.Zero : ImGui.CalcTextSize(subtitle);

			var textBlockWidth = MathF.Max(titleSize.X, subtitleSize.X);
			var textBlockHeight = titleSize.Y + (subtitle is null ? 0f : titleSubtitleGap + subtitleSize.Y);

			var hasIcon = icon != OverlayIcon.None;
			var iconColumnWidth = hasIcon ? iconRadius * 2f + iconTextGap : 0f;

			var contentWidth = iconColumnWidth + textBlockWidth;
			var contentHeight = MathF.Max(textBlockHeight, hasIcon ? iconRadius * 2f : 0f);
			var boxSize = new Vector2(contentWidth, contentHeight) + padding * 2f;

			var center = areaMin + areaSize * 0.5f;
			var boxMin = center - boxSize * 0.5f;
			var boxMax = boxMin + boxSize;

			var bgColor = ImGui.GetColorU32(OverlayBackground);
			var accentColor = ImGui.GetColorU32(accent);
			var titleColor = ImGui.GetColorU32(EditorPalette.Text);
			var subtitleColor = ImGui.GetColorU32(EditorPalette.WithAlpha(EditorPalette.Darken(EditorPalette.Text, 0.45f), 0.85f));

			drawList.AddRectFilled(boxMin, boxMax, bgColor, 12f);
			drawList.AddRect(boxMin, boxMax, accentColor, 12f, ImDrawFlags.None, 1.5f);

			var textBlockMinX = boxMin.X + padding.X + iconColumnWidth;
			var textBlockCenterY = boxMin.Y + boxSize.Y * 0.5f;

			if (hasIcon)
			{
				var iconCenter = new Vector2(boxMin.X + padding.X + iconRadius, textBlockCenterY);
				DrawIcon(drawList, icon, iconCenter, iconRadius, accentColor);
			}

			var titlePos = new Vector2(textBlockMinX, textBlockCenterY - textBlockHeight * 0.5f);
			drawList.AddText(titlePos, titleColor, title);

			if (subtitle is not null)
			{
				var subtitlePos = new Vector2(textBlockMinX, titlePos.Y + titleSize.Y + titleSubtitleGap);
				drawList.AddText(subtitlePos, subtitleColor, subtitle);
			}
		}

		private static void DrawIcon(ImDrawListPtr drawList, OverlayIcon icon, Vector2 center, float radius, uint color)
		{
			switch (icon)
			{
				case OverlayIcon.Play:
				{
					var half = radius * 0.95f;
					var p1 = center + new Vector2(-half * 0.55f, -half);
					var p2 = center + new Vector2(-half * 0.55f, half);
					var p3 = center + new Vector2(half, 0f);
					drawList.AddTriangleFilled(p1, p2, p3, color);
					break;
				}
				case OverlayIcon.Pause:
				{
					var barWidth = radius * 0.6f;
					var gap = radius * 0.4f;
					var barHalfHeight = radius * 0.9f;

					drawList.AddRectFilled(
						new Vector2(center.X - gap * 0.5f - barWidth, center.Y - barHalfHeight),
						new Vector2(center.X - gap * 0.5f, center.Y + barHalfHeight),
						color, IconShapeRounding);
					drawList.AddRectFilled(
						new Vector2(center.X + gap * 0.5f, center.Y - barHalfHeight),
						new Vector2(center.X + gap * 0.5f + barWidth, center.Y + barHalfHeight),
						color, IconShapeRounding);
					break;
				}
				case OverlayIcon.Stop:
				{
					var half = radius * 0.9f;
					drawList.AddRectFilled(center - new Vector2(half, half), center + new Vector2(half, half), color, IconShapeRounding);
					break;
				}
				case OverlayIcon.Info:
				{
					drawList.AddCircle(center, radius, color, 0, 1.5f);
					var dotRadius = radius * 0.15f;
					drawList.AddCircleFilled(center - new Vector2(0f, radius * 0.45f), dotRadius, color);
					drawList.AddRectFilled(
						center + new Vector2(-dotRadius, -radius * 0.15f),
						center + new Vector2(dotRadius, radius * 0.55f),
						color);
					break;
				}
			}
		}
	}
}