using Engine.ImGui.Core;

namespace DecaEngine.Editor
{
	public class InspectorWindow : ImGuiDockingWindow
	{
		public InspectorWindow(string name, ImGuiRender imGuiRender) : base(name, imGuiRender)
		{
		}

		protected override void OnRender(uint dockId)
		{
			// TODO: Implement inspector window UI
		}
	}
}