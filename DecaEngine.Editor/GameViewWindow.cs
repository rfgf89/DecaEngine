using DecaEngine.Core;
using DecaEngine.Graphics;
using Engine.ImGui.Core;

namespace DecaEngine.Editor
{
	public class GameViewWindow : ImGuiDockingWindow
	{
		private IRenderHandle _renderHandle;

		public GameViewWindow(string name, IRenderHandle renderHandle, ImGuiRender imGuiRender) : base(name, imGuiRender)
		{
			_renderHandle = renderHandle;
		}

		protected override void OnRender(uint dockId)
		{
			// TODO: Implement game view window UI
		}
	}
}