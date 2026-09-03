using Engine.ImGui.Core;

namespace DecaEngine.Editor
{
	public class HierarchyWindow : ImGuiDockingWindow
	{
		public HierarchyWindow(string name, ImGuiRender imGuiRender) : base(name, imGuiRender)
		{
		}

		protected override void OnRender(uint dockId)
		{
		}
	}
}