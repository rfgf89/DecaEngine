using System.Numerics;
using Hexa.NET.ImGui;

public abstract class ImGuiModalWindow : ImGuiWindow
{
	private bool windowEnded;
	private bool signalShow;
	private bool _lastBeganLogged;
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

		// Center on the main viewport, not the host window: mixing window screen coords with
		// viewport size pushes the modal off-screen on moved/multi-monitor setups.
		var viewport = ImGui.GetMainViewport();
		Vector2 size = viewport.Size * 0.75f;
		ImGui.SetNextWindowPos(viewport.Pos + (viewport.Size - size) * 0.5f);
		ImGui.SetNextWindowSize(size);

		if (signalShow)
		{
			shown = true;
			ImGui.OpenPopup(_title, ImGuiPopupFlags.None);
			signalShow = false;
			Console.WriteLine($"[modal] OpenPopup('{_title}') issued, IsPopupOpen={ImGui.IsPopupOpen(_title)}");
		}

		bool began = ImGui.BeginPopupModal(_title, ref shown, ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize);
		if (began != _lastBeganLogged)
		{
			_lastBeganLogged = began;
			Console.WriteLine($"[modal] '{_title}' BeginPopupModal={began}, shown={shown}, IsPopupOpen={ImGui.IsPopupOpen(_title)}");
		}

		if (!began)
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