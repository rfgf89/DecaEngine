using System.Numerics;
using Engine.ImGui.Core;
using Hexa.NET.ImGui;
using Hexa.NET.ImGuizmo;

namespace DecaEngine.Editor
{
	/// <summary>Dockable "Scene View" window rendering the prefab edited in the Inspector.</summary>
	public class SceneViewWindow : ImGuiDockingWindow
	{
		// Order must match PrefabSceneViewport.ShadingMode: the combo index is cast to it directly.
		private static readonly string[] ShadingLabels =
		{
			"Lighting", "Textured", "Normal", "UV", "Tangent", "Punctual Shadow Debug",
			"Cluster Depth Slices", "Cluster Screen Tiles", "Cluster Light Count",
			"Light Depth: Receiver", "Light Depth: Occluder", "Light Depth: Gap",
			"Sun Shadow Cascades",
		};

		private readonly InspectorWindow _inspectorWindow;
		private readonly PrefabSceneViewport _sceneViewport;
		private string? _lastFramedPrefabPath;

		protected override ImGuiWindowFlags AdditionalWindowFlags => ImGuiWindowFlags.MenuBar;

		public SceneViewWindow(string name, InspectorWindow inspectorWindow, PrefabSceneViewport sceneViewport,
			ImGuiRender imGuiRender) : base(name, imGuiRender)
		{
			_inspectorWindow = inspectorWindow;
			_sceneViewport = sceneViewport;
		}

		protected override void OnRender(uint dockId)
		{
			var root = _inspectorWindow.Root;
			if (root is null)
			{
				_lastFramedPrefabPath = null;
			}
			else if (_inspectorWindow.PrefabPath != _lastFramedPrefabPath)
			{
				_sceneViewport.FrameAll();
				_lastFramedPrefabPath = _inspectorWindow.PrefabPath;
			}

			DrawHeader();

			var canvasSize = ImGui.GetContentRegionAvail();
			if (canvasSize.X <= 0 || canvasSize.Y <= 0)
			{
				return;
			}

			// The viewport renders the environment sky even with no prefab open, so always show it.
			var cursor = ImGui.GetCursorScreenPos();
			if (_sceneViewport.Render(_imGuiRender, root ?? default, _inspectorWindow.Selected, canvasSize, out var pick))
			{
				_inspectorWindow.NotifyTransformChangedExternally();
			}

			var hint = !_sceneViewport.IsActive
				? "Scene paused - Inspector is showing a model preview"
				: root is null ? "No prefab loaded" : null;
			if (hint != null)
			{
				var center = cursor + canvasSize * 0.5f;
				var textSize = ImGui.CalcTextSize(hint);
				ImGui.GetWindowDrawList().AddText(center - textSize * 0.5f,
					ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.6f)), hint);
			}

			if (pick.Clicked)
			{
				if (pick.Entity.HasValue)
				{
					_inspectorWindow.SetSelected(pick.Entity.Value);
				}
				else
				{
					_inspectorWindow.ClearSelection();
				}
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
				_sceneViewport.FrameAll();
			}

			ImGui.SameLine();
			int shadingIndex = (int)_sceneViewport.Shading;
			ImGui.SetNextItemWidth(110f * _scale);
			if (ImGui.Combo("##SceneShading", ref shadingIndex, ShadingLabels, ShadingLabels.Length))
			{
				_sceneViewport.SetShading((PrefabSceneViewport.ShadingMode)shadingIndex);
			}
			var hoveredShading = (PrefabSceneViewport.ShadingMode)shadingIndex;
			if (ImGui.IsItemHovered() && hoveredShading >= PrefabSceneViewport.ShadingMode.PunctualShadowDebug)
			{
				ImGui.SetTooltip(PrefabSceneViewport.ClusterLegend(hoveredShading));
			}

			// One checkbox drives both auto-exposure and probe GI; applied by rebuilding the env.
			ImGui.SameLine();
			bool hdr = _sceneViewport.HdrEnabled;
			if (ImGui.Checkbox("HDR+GI", ref hdr))
			{
				_sceneViewport.SetHdrEnabled(hdr);
			}

			float lightYaw = _sceneViewport.LightYawDegrees;
			float lightElevation = _sceneViewport.LightElevationDegrees;
			bool lightChanged = false;

			ImGui.SameLine();
			ImGui.SetNextItemWidth(120f * _scale);
			lightChanged |= ImGui.SliderFloat("Sun Yaw", ref lightYaw, -180f, 180f, "%.0f deg");

			ImGui.SameLine();
			ImGui.SetNextItemWidth(120f * _scale);
			lightChanged |= ImGui.SliderFloat("Height", ref lightElevation, -60f, 60f, "%.0f deg");

			if (lightChanged)
			{
				_sceneViewport.SetLightRotation(lightYaw, lightElevation);
			}

			ImGui.SameLine();
			ImGui.TextDisabled("RMB fly (WASD/QE, Shift/Ctrl) / Alt+LMB orbit / MMB pan / wheel dolly / F focus");

			ImGui.EndMenuBar();
		}
	}
}
