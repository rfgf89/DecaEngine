using System.Numerics;
using Engine.ImGui.Core;
using Hexa.NET.ImGui;
using Hexa.NET.ImGuizmo;

namespace DecaEngine.Editor
{
	/// <summary>
	/// Докируемое окно "Scene View": GPU-рендер сцены редактируемого в <see cref="InspectorWindow"/>
	/// префаба через <see cref="PrefabSceneViewport"/> (объекты грузятся по AssetRef из компонентов
	/// ModelRenderer), с гизмо перемещения/поворота/масштаба выделенной сущности, переключателями
	/// шейдинга и вращением направленного света - те же ручки, что у превью модели.
	/// </summary>
	public class SceneViewWindow : ImGuiDockingWindow
	{
		// Порядок ОБЯЗАН совпадать с PrefabSceneViewport.ShadingMode - Combo отдаёт индекс, который
		// приводится к перечислению напрямую.
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

			// Вьюпорт рендерит лит-небо окружения ДАЖЕ без открытого префаба (см.
			// PrefabSceneViewport.Update: hasRoot) - кадр показываем всегда, а "No prefab loaded"
			// кладём поверх подсказкой, а не вместо картинки. root телом Render не используется
			// (см. его сигнатуру) - default передаётся, когда префаб не открыт.
			var cursor = ImGui.GetCursorScreenPos();
			if (_sceneViewport.Render(_imGuiRender, root ?? default, _inspectorWindow.Selected, canvasSize, out var pick))
			{
				_inspectorWindow.NotifyTransformChangedExternally();
			}

			// Сцена на паузе (Inspector показывает превью модели) снята с GPU целиком - модель
			// редактора грузится ровно в одном месте, см. PrefabSceneViewport.SetActive. Кадр при
			// этом пишется, но пустой, поэтому объясняем пустоту подсказкой, как и закрытый префаб.
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

			// Клик по вьюпорту: объект - выделяем его в Inspector-е (и гизмо переезжает на него),
			// пустота - снимаем выделение, как в обычных редакторах.
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

			// Режим шейдинга - тот же набор, что у превью модели (Lighting/Textured/каналы отладки).
			ImGui.SameLine();
			int shadingIndex = (int)_sceneViewport.Shading;
			ImGui.SetNextItemWidth(110f * _scale);
			if (ImGui.Combo("##SceneShading", ref shadingIndex, ShadingLabels, ShadingLabels.Length))
			{
				_sceneViewport.SetShading((PrefabSceneViewport.ShadingMode)shadingIndex);
			}
			// Легенда ВЫБРАННОГО отладочного режима - у каждого своя (см.
			// PrefabSceneViewport.ClusterLegend): у кластерных видов ценность целиком в том, что
			// ожидаемая картинка известна заранее, и держать её надо перед глазами, а не в статье.
			var hoveredShading = (PrefabSceneViewport.ShadingMode)shadingIndex;
			if (ImGui.IsItemHovered() && hoveredShading >= PrefabSceneViewport.ShadingMode.PunctualShadowDebug)
			{
				ImGui.SetTooltip(PrefabSceneViewport.ClusterLegend(hoveredShading));
			}

			// HDR+GI: одна галочка включает и авто-экспозицию (HDR-конвейер), и probe-GI сцены.
			// Применяется пересозданием окружения без перезагрузки моделей (см.
			// PrefabSceneViewport.SetHdrEnabled).
			ImGui.SameLine();
			bool hdr = _sceneViewport.HdrEnabled;
			if (ImGui.Checkbox("HDR+GI", ref hdr))
			{
				_sceneViewport.SetHdrEnabled(hdr);
			}

			// Поворот мирового направленного света - live, вместе с небом/IBL (см.
			// PrefabSceneViewport.SetLightRotation; та же семантика, что в превью модели).
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
