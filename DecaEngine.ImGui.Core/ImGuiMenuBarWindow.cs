using System.Numerics;
using Hexa.NET.ImGui;

using ImGui = Hexa.NET.ImGui.ImGui;

namespace Engine.ImGui.Core;

public class DockLayoutElement
{
	public string name;
	public float ratio;

	public ImGuiDir imGuiDir;

	public Vector2 position;
	public Vector2 size;

	public uint dockId;
}

public abstract class ImGuiMenuBarWindow : ImGuiWindow
{
	protected ImGuiMenuBarWindow(string title, ImGuiRender imGuiRender) : base(title, imGuiRender)
	{

	}

	public override void EndFirstFrame(uint dockId)
	{
		base.EndFirstFrame(dockId);
	}

	public override void Render(uint dockId)
	{
		base.Render(dockId);

		Hexa.NET.ImGui.ImGui.SetNextWindowPos(Hexa.NET.ImGui.ImGui.GetMainViewport().Pos);
		Hexa.NET.ImGui.ImGui.SetNextWindowSize(new Vector2(Hexa.NET.ImGui.ImGui.GetMainViewport().Size.X, Hexa.NET.ImGui.ImGui.GetMainViewport().Size.Y));
		Hexa.NET.ImGui.ImGui.GetIO().ConfigWindowsResizeFromEdges = false;

		Hexa.NET.ImGui.ImGui.Begin(_title, ImGuiWindowFlags.MenuBar |
		                                   ImGuiWindowFlags.NoTitleBar |
		                                   ImGuiWindowFlags.NoMove |
		                                   ImGuiWindowFlags.NoResize |
		                                   ImGuiWindowFlags.NoCollapse |
		                                   ImGuiWindowFlags.NoBringToFrontOnFocus |
		                                   ImGuiWindowFlags.NoNavFocus |
		                                   ImGuiWindowFlags.NoDocking);

		OnRender(dockId);
		Hexa.NET.ImGui.ImGui.End();
	}
}