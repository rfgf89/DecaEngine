using System.Numerics;
using Hexa.NET.ImGui;

public abstract class ImGuiModalWindow : ImGuiWindow
{
	private bool windowEnded;
	private bool signalShow;
	protected bool signalClose;
	protected bool shown;

	public ImGuiModalWindow(string title, ImGuiRender imGuiRender) : base(title, imGuiRender)
	{
	}

	public override void Render(uint dockId)
	{
		base.Render(dockId);

		bool open = true;

		if (!shown)
		{
			return;
		}

		Vector2 size = ImGui.GetMainViewport().Size * 0.75f;
		ImGui.SetNextWindowPos(ImGui.GetWindowPos() + ImGui.GetMainViewport().Size / 2 - (size / 2));
		ImGui.SetNextWindowSize(size);

		if (signalShow)
		{
			shown = true;
			ImGui.OpenPopup(_title, ImGuiPopupFlags.None);
			signalShow = false;
		}

		if (!ImGui.BeginPopupModal(_title, ref shown, ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize))
		{
			return;
		}

		if (signalClose)
		{
			ImGui.CloseCurrentPopup();
			signalClose = false;
			shown = false;
			ImGui.EndPopup();
			return;
		}

		OnRender(dockId);

		ImGui.EndPopup();
	}

	public virtual void Close()
	{
		signalClose = true;
	}

	public virtual void Show()
	{
		signalShow = shown = true;
	}
}