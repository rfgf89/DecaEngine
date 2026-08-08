using System.Numerics;
using Engine.ImGui.Core;
using Hexa.NET.ImGui;
using Hexa.NET.ImGuizmo;

namespace DecaEngine.Editor
{
	/// <summary>
	/// ????????? ???-???? "Scene View" ? 3D-?????????????? ???????? ?????????????? ?
	/// <see cref="InspectorWindow"/> ???????: ?????? ??? ?? ??????? TRS-??????? ?????? ????????
	/// ????? ?????????? ????????? ImGuizmo, ??????????? ?????? ????? ? gizmo
	/// ???????????/????????/??????????????? ????????? ????????, ????????? ???????? ????????????
	/// ??????? ? ?????????? ???????? ????? <see cref="InspectorWindow"/>.
	/// </summary>
	public class SceneViewWindow : ImGuiDockingWindow
	{
		private readonly InspectorWindow _inspectorWindow;
		private readonly PrefabSceneViewport _sceneViewport = new();
		private string? _lastFramedPrefabPath;

		protected override ImGuiWindowFlags AdditionalWindowFlags => ImGuiWindowFlags.MenuBar;

		public SceneViewWindow(string name, InspectorWindow inspectorWindow, ImGuiRender imGuiRender) : base(name, imGuiRender)
		{
			_inspectorWindow = inspectorWindow;
		}

		protected override void OnRender(uint dockId)
		{
			var root = _inspectorWindow.Root;
			if (root is null)
			{
				_lastFramedPrefabPath = null;
				var avail = ImGui.GetContentRegionAvail();
				if (avail.X > 0 && avail.Y > 0)
				{
					var center = ImGui.GetCursorScreenPos() + avail * 0.5f;
					var text = "No prefab loaded";
					var textSize = ImGui.CalcTextSize(text);
					ImGui.SetCursorScreenPos(center - textSize * 0.5f);
					ImGui.TextDisabled(text);
				}
				return;
			}

			if (_inspectorWindow.PrefabPath != _lastFramedPrefabPath)
			{
				_sceneViewport.FrameAll(root.Value);
				_lastFramedPrefabPath = _inspectorWindow.PrefabPath;
			}

			DrawHeader();

			var canvasSize = ImGui.GetContentRegionAvail();
			if (canvasSize.X <= 0 || canvasSize.Y <= 0)
			{
				return;
			}

			if (_sceneViewport.Render(root.Value, _inspectorWindow.Selected, canvasSize))
			{
				_inspectorWindow.NotifyTransformChangedExternally();
			}
		}

		private void DrawHeader()
		{
			if (!ImGui.BeginMenuBar())
			{
				return;
			}

			var op = _sceneViewport.Operation;

			if (ImGui.RadioButton("Move", op == ImGuizmoOperation.Translate))
			{
				_sceneViewport.Operation = ImGuizmoOperation.Translate;
			}
			ImGui.SameLine();
			if (ImGui.RadioButton("Rotate", op == ImGuizmoOperation.Rotate))
			{
				_sceneViewport.Operation = ImGuizmoOperation.Rotate;
			}
			ImGui.SameLine();
			if (ImGui.RadioButton("Scale", op == ImGuizmoOperation.Scale))
			{
				_sceneViewport.Operation = ImGuizmoOperation.Scale;
			}
			ImGui.SameLine();
			if (ImGui.SmallButton("Frame All"))
			{
				var root = _inspectorWindow.Root;
				if (root.HasValue)
				{
					_sceneViewport.FrameAll(root.Value);
				}
			}
			ImGui.SameLine();
			ImGui.TextDisabled("RMB orbit / MMB pan / wheel zoom");

			ImGui.EndMenuBar();
		}
	}
}

