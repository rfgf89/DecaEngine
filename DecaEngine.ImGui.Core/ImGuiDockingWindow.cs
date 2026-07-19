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

	public override void Render(uint dockId)
	{
		if (!IsOpen)
		{
			return;
		}

		base.Render(dockId);

		bool open = IsOpen;
		ImGui.Begin(_title, ref open, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings);

		OnRender(dockId);

		ImGui.End();

		IsOpen = open;
	}
}