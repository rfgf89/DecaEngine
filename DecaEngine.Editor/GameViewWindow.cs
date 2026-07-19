using DecaEngine.Core;
using DecaEngine.Generic;
using Hexa.NET.ImGui;

namespace DecaEngine.Editor;

public class GameViewWindow : ImGuiDockingWindow, IFactoryObject
{
	private readonly IRenderHandle _renderHandle;
	private ImTextureRef _textureRef;

	public unsafe GameViewWindow(string title, IRenderHandle renderHandle, ImGuiRender imGuiRender) : base(title, imGuiRender)
	{
		_renderHandle = renderHandle;
	}

	public void StartScene()
	{

	}

	public void ChangeState(LoopCore.State state)
	{

	}

	public void EndScene()
	{
		_textureRef.Destroy();
	}

	public override unsafe void FirstFrame(uint dockId)
	{
		base.FirstFrame(dockId);

		_textureRef = _imGuiRender.GetNewTexture();
		_imGuiRender.BindRenderTarget(_textureRef.TexID, _renderHandle);
	}

	protected override void OnRender(uint dockId)
	{
		var workSize = ImGui.GetContentRegionAvail();
		ImGui.Image(_textureRef, workSize);
	}
}