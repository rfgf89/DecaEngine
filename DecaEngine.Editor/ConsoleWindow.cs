using Engine.ImGui.Core;

namespace DecaEngine.Editor
{
	public class ConsoleWindow : ImGuiDockingWindow
	{
		public ConsoleWindow(string name, ImGuiRender imGuiRender) : base(name, imGuiRender)
		{
		}

		protected override void OnRender(uint dockId)
		{
			// TODO: Implement console window UI
		}
	}
}