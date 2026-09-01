using System.Numerics;
using DecaEngine.Core.Diagnostics;
using Engine.ImGui.Core;
using Hexa.NET.ImGui;

namespace DecaEngine.Editor
{
	public class ConsoleWindow : ImGuiDockingWindow
	{
		private string _filter = string.Empty;
		private bool _autoScroll = true;
		private bool _showInfo = true;
		private bool _showWarning = true;
		private bool _showError = true;
		private bool _showNative; // Off by default: native (e.g. Diligent Engine) logs are noisy.
		private int _sourceFilter; // 0 = All, 1 = Editor, 2 = Project
		private LogEntry? _selectedEntry;
		private float _detailsHeight = 150f;

		private static readonly string[] SourceFilterLabels = { "All", "Editor", "Project" };

		// ????????? ImGuiCol.ChildBg ??????????? ?????? ?? 95% - ?????? ??? 5% ???????????? ?????
		// ???? ??????? ?????????, ??-?? ???? ?????? ??????? (?????? ????????? ? Details) ?????????
		// ? ??????? ????????. ??? ?? ????, ??? ? ? AssetBrowserWindow/InspectorWindow. ??????? ??
		// EditorPalette.Background (? ?? ???????????), ????? ???? ?? Preferences ??????????? ? ???.
		private static Vector4 PanelBackground => EditorPalette.Background;

		public ConsoleWindow(string name, ImGuiRender imGuiRender) : base(name, imGuiRender)
		{
			EngineLog.Install();
		}

		protected override void OnRender(uint dockId)
		{
			// ????????? ???????? ????????? ???? ??????? (?????? ?????????????? ????????? ????? ImGui).
			// ??? ???????? ???? ????????? ?????????: ??????, ?????????, ?????? "Details:" ? ?.?.
			ImGui.PushStyleColor(ImGuiCol.Text, EditorPalette.Text);

			if (ImGui.Button("Clear"))
			{
				EngineLog.Clear();
				_selectedEntry = null;
			}

			ImGui.SameLine();
			ImGui.SetNextItemWidth(150 * _scale);
			int sourceFilter = _sourceFilter;
			if (ImGui.Combo("##ConsoleSource", ref sourceFilter, SourceFilterLabels, SourceFilterLabels.Length))
			{
				_sourceFilter = sourceFilter;
			}

			ImGui.SameLine();
			ImGui.Checkbox("Info", ref _showInfo);
			ImGui.SameLine();
			ImGui.Checkbox("Warning", ref _showWarning);
			ImGui.SameLine();
			ImGui.Checkbox("Error", ref _showError);
			ImGui.SameLine();
			ImGui.Checkbox("Native", ref _showNative);
			ImGui.SameLine();
			ImGui.Checkbox("Auto Scroll", ref _autoScroll);

			ImGui.SetNextItemWidth(-1);
			ImGui.InputTextWithHint("##ConsoleFilter", String.Empty, ref _filter, 256);

			ImGui.Separator();

			var entries = EngineLog.Snapshot();
			var filtered = new List<LogEntry>(entries.Count);
			foreach (var entry in entries)
			{
				if (entry.Level == LogLevel.Info && !_showInfo)
				{
					continue;
				}

				if (entry.Level == LogLevel.Warning && !_showWarning)
				{
					continue;
				}

				if (entry.Level == LogLevel.Error && !_showError)
				{
					continue;
				}

				if (_sourceFilter == 1 && entry.Source != LogSource.Editor)
				{
					continue;
				}

				if (_sourceFilter == 2 && entry.Source != LogSource.Project)
				{
					continue;
				}

				// Native logs are hidden unless explicitly enabled, regardless of the source dropdown.
				if (entry.Source == LogSource.Native && !_showNative)
				{
					continue;
				}

				if (!string.IsNullOrEmpty(_filter) && entry.Message.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0)
				{
					continue;
				}

				filtered.Add(entry);
			}

			// The details panel height is user-resizable via the splitter below, but is
			// clamped so the list above always keeps a minimum visible height.
			float totalAvail = ImGui.GetContentRegionAvail().Y;
			float splitterThickness = 6f * _scale;
			float minListHeight = 100f * _scale;
			float minDetailsHeight = 40f * _scale;

			float maxDetailsHeight = MathF.Max(minDetailsHeight, totalAvail - minListHeight - splitterThickness);
			_detailsHeight = Math.Clamp(_detailsHeight, minDetailsHeight, maxDetailsHeight);

			float listHeight = MathF.Max(minListHeight, totalAvail - _detailsHeight - splitterThickness);

		ImGui.PushStyleColor(ImGuiCol.ChildBg, PanelBackground);

		if (ImGui.BeginChild("##ConsoleScrollRegion", new Vector2(0, listHeight)))
			{
				var clipper = new ImGuiListClipper();
				clipper.Begin(filtered.Count);

				while (clipper.Step())
				{
					for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
					{
						var entry = filtered[i];
						var text = entry.Level switch
						{
							LogLevel.Error => new Vector4(1f, 0.35f, 0.35f, 1f),
							LogLevel.Warning => new Vector4(1f, 0.85f, 0.3f, 1f),
							_ => EditorPalette.Text
						};

						var sourceTag = GetSourceTag(entry.Source);
						var singleLine = ToSingleLine(entry.Message);
						var label = $"[{entry.Time:HH:mm:ss}] [{sourceTag}] {singleLine}";

						bool isSelected = _selectedEntry.HasValue &&
							_selectedEntry.Value.Time == entry.Time &&
							_selectedEntry.Value.Message == entry.Message;

						ImGui.PushStyleColor(ImGuiCol.Text, text);
						ImGui.PushID(i);
						if (ImGui.Selectable(label, isSelected, ImGuiSelectableFlags.None))
						{
							_selectedEntry = entry;
						}
						ImGui.PopID();
						ImGui.PopStyleColor();
					}
				}

				if (_autoScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY())
				{
					ImGui.SetScrollHereY(1.0f);
				}
			}

			ImGui.EndChild();
			ImGui.PopStyleColor();

			// Draggable splitter to resize the details panel without letting it cover the whole
			// list. ??????? ???????? ?? ??? ?????? ?????? (???????????? ? ??????? ??????? - ????
			// ? ??? ?? ?????????????), ?? ?? ???????????? ?????? ?? ???? ???????, ??? ? ?
			// AssetBrowserWindow.RenderSplitter - ??? ?????? ????? ????? ? ???? ????????????
			// ????????? ????????.
			var cursorPos = ImGui.GetCursorScreenPos();
			var splitterFullWidth = ImGui.GetContentRegionAvail().X;

			ImGui.InvisibleButton("##ConsoleSplitter", new Vector2(splitterFullWidth, splitterThickness));

			var draggingSplitter = ImGui.IsItemActive();
			if (draggingSplitter)
			{
				_detailsHeight -= ImGui.GetIO().MouseDelta.Y;
				_detailsHeight = Math.Clamp(_detailsHeight, minDetailsHeight, maxDetailsHeight);
			}

			var hovered = ImGui.IsItemHovered();
			if (hovered || draggingSplitter)
			{
				ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNs);
			}

			var styleColors = ImGui.GetStyle().Colors;
			var color = draggingSplitter
				? styleColors[(int)ImGuiCol.SeparatorActive]
				: hovered
					? styleColors[(int)ImGuiCol.SeparatorHovered]
					: styleColors[(int)ImGuiCol.Separator];

			var rounding = splitterThickness * 0.5f;
			ImGui.GetWindowDrawList().AddRectFilled(cursorPos, new Vector2(cursorPos.X + splitterFullWidth, cursorPos.Y + splitterThickness), ImGui.GetColorU32(color), rounding);

			ImGui.TextUnformatted("Details:");

			ImGui.PushStyleColor(ImGuiCol.ChildBg, PanelBackground);

			if (ImGui.BeginChild("##ConsoleDetails", new Vector2(0, 0)))
			{
				if (_selectedEntry.HasValue)
				{
					var entry = _selectedEntry.Value;
					var sourceTag = GetSourceTag(entry.Source);
					ImGui.TextUnformatted($"[{entry.Time:yyyy-MM-dd HH:mm:ss.fff}] [{entry.Level}] [{sourceTag}]");
					ImGui.Separator();
					ImGui.TextWrapped(entry.Message);
				}
				else
				{
					ImGui.TextDisabled("Select a message to see its details.");
				}
			}

			ImGui.EndChild();
			ImGui.PopStyleColor();

			ImGui.PopStyleColor(); // ??????? Text, ??????????? ? ?????? OnRender
		}

		private static string ToSingleLine(string message)
		{
			if (message.IndexOf('\n') < 0 && message.IndexOf('\r') < 0)
			{
				return message;
			}

			return message.Replace("\r\n", " \u21b5 ").Replace('\n', ' ').Replace('\r', ' ');
		}

		private static string GetSourceTag(LogSource source) => source switch
		{
			LogSource.Project => "Project",
			LogSource.Native => "Native",
			_ => "Editor"
		};
	}
}