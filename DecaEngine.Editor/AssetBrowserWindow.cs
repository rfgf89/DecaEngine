using Engine.ImGui.Core;

namespace DecaEngine.Editor
{
	public class AssetBrowserWindow : ImGuiDockingWindow
	{
		public AssetBrowserWindow(string name, ImGuiRender imGuiRender) : base(name, imGuiRender)
		{
		}

		protected override void OnRender(uint dockId)
		{
			// TODO: Implement asset browser window UI
		}
	}
}