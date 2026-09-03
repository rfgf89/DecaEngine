using System.ComponentModel;
using System.Numerics;
using System.Text.RegularExpressions;
using Hexa.NET.ImGui;

public abstract class ImGuiDockingWindow : ImGuiWindow
{
	private bool _undock;

	private static Dictionary<string, HashSet<ImGuiDockingWindow>> _instanceIds = new ();

	public ImGuiDockingWindow(string title, ImGuiRender imGuiRender) : base(title, imGuiRender)
	{

	}

	protected override void OnAdd()
	{
		base.OnAdd();

		var output = Regex.Replace(_title, @"[\d-]", string.Empty).Trim();
		if (!_instanceIds.ContainsKey(output))
		{
			_instanceIds[output] = new HashSet<ImGuiDockingWindow>();
		}

		_instanceIds[output].Add(this);

		for (int i = 0; i < _instanceIds[output].Count; i++)
		{
			if (i == 0)
			{
				_title = output;
				continue;
			}

			_title = $"{output} {i}";
		}
	}

	protected override void OnRemove()
	{
		base.OnRemove();

		var output = Regex.Replace(_title, @"[\d-]", string.Empty).Trim();
		_instanceIds[output].Remove(this);

		for (int i = 0; i < _instanceIds[output].Count; i++)
		{
			if (i == 0)
			{
				_title = output;
				continue;
			}

			_title = $"{output} {i}";
		}
	}

	protected virtual ImGuiWindowFlags AdditionalWindowFlags => ImGuiWindowFlags.None;

	/// <summary>Whether this window (or a child) was focused on the LAST draw. Systems that read
	/// input outside ImGui must use this: io.WantCaptureKeyboard is true for any focused window.</summary>
	public bool IsFocused { get; private set; }

	public override void Render(uint dockId)
	{
		if (!IsOpen && !_forceDraw)
		{
			return;
		}

		base.Render(dockId);

		bool open = IsOpen || _forceDraw;
		ImGui.Begin(_title, ref open, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings | AdditionalWindowFlags);

		IsFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
		if (IsFocused)
		{
			DrawFocusHighlight();
		}

		OnRender(dockId);

		ImGui.End();

		IsOpen = open;
	}

	private static void DrawFocusHighlight()
	{
		var drawList = ImGui.GetWindowDrawList();
		var min = ImGui.GetWindowPos();
		var max = min + ImGui.GetWindowSize();
		var style = ImGui.GetStyle();

		// GetColorU32 returns packed ARGB; lerping the packed ints would blend channels into
		// each other, so unpack to Vector4 first.
		var overline = ImGui.ColorConvertU32ToFloat4(ImGui.GetColorU32(ImGuiCol.TabSelectedOverline));
		var windowBg = ImGui.ColorConvertU32ToFloat4(ImGui.GetColorU32(ImGuiCol.WindowBg));

		// Keep WindowBg's alpha: the overline color is usually opaque and would repaint the window.
		var blended = Vector4.Lerp(windowBg, overline, 0.05f);
		blended.W = windowBg.W;
		var color = ImGui.GetColorU32(blended);

		drawList.AddRectFilled(min, max, color, style.WindowRounding);
	}
}