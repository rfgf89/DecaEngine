using System.Linq;
using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Core.Diagnostics;
using DecaEngine.Core.Prefabs;
using DecaEngine.Editor.ECS;
using Engine.ImGui.Core;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Serialize;
using Friflo.Engine.ECS.Systems;
using Friflo.Engine.ECS.Utils;
using Hexa.NET.ImGui;
using DecaEngine.Scene;

namespace DecaEngine.Editor
{
	/// <summary>Prefab/model inspector: edits a prefab instantiated into its own <see cref="EntityStore"/>, saves back to the same JSON file.</summary>
	public partial class InspectorWindow : ImGuiDockingWindow
	{
		private enum InspectorMode
		{
			None,
			Prefab,
			Model
		}

		private readonly Dictionary<int, bool> _expandedState = new();

		private readonly ModelPreviewViewport? _modelPreview;
		private InspectorMode _mode = InspectorMode.None;

		// null = whole-model view (no sub-mesh isolated).
		private string? _modelSubMeshLabel;

		private string? _prefabPath;
		private EntityStore? _store;
		private Entity _root;
		private Entity? _selected;

		private string _componentsBuffer = string.Empty;
		private int _componentsBufferEntityId = -1;
		private string? _applyError;

		private Vector3 _tempEuler;
		private int _eulerEntityId = -1;

		private string _addComponentFilter = string.Empty;

		private int _renamingEntityId = -1;
		private string _renameBuffer = string.Empty;
		private bool _renameFocusPending;
		private bool _dirty;
		private bool _debugMode;

		// Play Mode snapshot/restore is pure ECS-side DataEntity JSON; no file I/O either way.
		private SystemRoot? _playSystemRoot;
		private List<DataEntity>? _playSnapshot;
		private bool _isPlaying;

		// Width/height ratio the model preview viewport is fit to (1 = square).
		private float _previewAspectRatio = 1f;

		private static Vector4 PanelBackground => EditorPalette.Lighten(EditorPalette.Background, 0.04f);

		private static Vector4 HierarchyPanelBackground => EditorPalette.Background;

		public InspectorWindow(string name, ImGuiRender imGuiRender) : base(name, imGuiRender)
		{
		}

		public InspectorWindow(string name, ModelPreviewViewport modelPreview, ImGuiRender imGuiRender) : base(name, imGuiRender)
		{
			_modelPreview = modelPreview;
		}


		public string? PrefabPath => _prefabPath;

		public Entity? Root => _prefabPath is null ? (Entity?)null : _root;

		public Entity? Selected => _selected;

		public void SetSelected(Entity entity)
		{
			_selected = entity;
		}

		/// <summary>True while Play Mode is running (see <see cref="Play"/>/<see cref="Stop"/>).</summary>
		public bool IsPlaying => _isPlaying;

		/// <summary>True while showing a model preview; only one viewport may hold the model (see <see cref="ModelPreviewViewport.SetActive"/>).</summary>
		public bool IsModelPreviewMode => _mode == InspectorMode.Model;

		/// <summary>Starts Play Mode: snapshots the prefab subtree in memory and ticks Play-only systems each frame.</summary>
		public void Play()
		{
			if (_isPlaying || _store is null || _prefabPath is null)
			{
				return;
			}

			var jsonEntities = TreeUtils.EntitiesToJsonArray(new[] { _root });
			var snapshot = new List<DataEntity>();
			var error = TreeUtils.JsonArrayToDataEntities(jsonEntities.entities, snapshot);
			if (error != null)
			{
				EngineLog.Add(LogLevel.Error, $"Play Mode: failed to snapshot prefab: {error}");
				return;
			}

			_playSnapshot = snapshot;
			_playSystemRoot = new SystemRoot { new RotateSystem(), new CircleMoveSystem() };
			_playSystemRoot.AddStore(_store);
			_isPlaying = true;
		}

		/// <summary>Stops Play Mode: restores the pre-Play snapshot ECS-side and deletes entities created while playing.</summary>
		public void Stop()
		{
			if (!_isPlaying || _playSnapshot is null || _store is null)
			{
				return;
			}

			var snapshotPids = new HashSet<long>(_playSnapshot.Select(de => de.pid));
			DeleteEntitiesNotInSnapshot(_root, snapshotPids);

			foreach (var de in _playSnapshot)
			{
				if (!_store.TryGetEntityByPid(de.pid, out var entity))
				{
					continue; // Entity itself was deleted during Play - nothing left to restore.
				}
				var componentsJson = de.components.IsNull() ? "{}" : de.components.AsString();
				if (!PrefabAsset.TryApplyComponentsJson(entity, componentsJson, out var applyError))
				{
					EngineLog.Add(LogLevel.Error, $"Play Mode: failed to restore entity {de.pid}: {applyError}");
				}
			}

			_playSystemRoot!.RemoveStore(_store);
			_playSystemRoot = null;
			_playSnapshot = null;
			_isPlaying = false;
		}

		// Delete cascades to the whole subtree, so a deleted match is never recursed into.
		private static void DeleteEntitiesNotInSnapshot(Entity entity, HashSet<long> snapshotPids)
		{
			if (!snapshotPids.Contains(entity.Pid))
			{
				entity.DeleteEntity();
				return;
			}
			foreach (var child in entity.ChildEntities.ToArray())
			{
				DeleteEntitiesNotInSnapshot(child, snapshotPids);
			}
		}

		/// <summary>Called every frame by EditorManager while <see cref="IsPlaying"/> to tick Play-Mode-only systems.</summary>
		public void UpdatePlayMode(float deltaTime, float time)
		{
			if (!_isPlaying)
			{
				return;
			}
			_playSystemRoot!.Update(new UpdateTick(deltaTime, time));
		}

		public void ClearSelection()
		{
			_selected = null;
		}

		public void NotifyTransformChangedExternally()
		{
			MarkChanged();
			_eulerEntityId = -1;
		}

		public void ShowPrefab(string prefabPath)
		{
			_mode = InspectorMode.Prefab;
			_prefabPath = prefabPath;
			Reload();
			Show();
		}

		public void ShowModel(string modelPath, int subMeshIndex = -1, string? subMeshLabel = null)
		{
			if (_modelPreview is null)
			{
				return;
			}

			_mode = InspectorMode.Model;
			_modelSubMeshLabel = subMeshIndex >= 0 ? subMeshLabel : null;
			_modelPreview.LoadModel(modelPath, subMeshIndex);
			Show();
		}


		private void Reload()
		{
			if (_prefabPath is null)
			{
				return;
			}

			if (_isPlaying)
			{
				Stop();
			}

			_store = new EntityStore();
			_root = PrefabAsset.Instantiate(_store, _prefabPath);
			_selected = _root;
			_componentsBufferEntityId = -1;
			_eulerEntityId = -1;
			_applyError = null;
			_dirty = false;
			_renamingEntityId = -1;
			_renameFocusPending = false;
			_expandedState.Clear();
		}

		protected override void OnRender(uint dockId)
		{
			if (_mode == InspectorMode.Model)
			{
				RenderModelPreview();
				return;
			}

			if (_prefabPath is null || _store is null)
			{
				return;
			}

			// Frame snapshot must be captured before any edits this frame (see InspectorWindow.EditOps).
			CaptureFrameSnapshot();
			HandleEditShortcuts();

			RenderToolbar();
			ImGui.Separator();

			var avail = ImGui.GetContentRegionAvail();
			if (avail.X <= 0 || avail.Y <= 0)
			{
				return;
			}

			var treeWidth = MathF.Max(160f * _scale, MathF.Min(260f * _scale, avail.X * 0.4f));

			ImGui.PushStyleColor(ImGuiCol.ChildBg, HierarchyPanelBackground);

			if (ImGui.BeginChild("##InspectorPrefabTree", new Vector2(treeWidth, avail.Y), ImGuiChildFlags.Borders))
			{
				RenderNode(_root);
			}
			ImGui.EndChild();
			ImGui.PopStyleColor();

			ImGui.SameLine();

			ImGui.PushStyleColor(ImGuiCol.ChildBg, PanelBackground);

			if (ImGui.BeginChild("##InspectorPrefabInspector", new Vector2(0, 0), ImGuiChildFlags.Borders))
			{
				RenderInspector();
			}
			ImGui.EndChild();

			ImGui.PopStyleColor();
		}

		private void RenderModelPreview()
		{
			if (_modelPreview is null)
			{
				return;
			}

			var loadedPath = _modelPreview.LoadedPath;
			var header = loadedPath is not null ? Path.GetFileName(loadedPath) : "No model loaded";
			if (loadedPath is not null && _modelSubMeshLabel is not null)
			{
				header += $"  >  {_modelSubMeshLabel}";
			}
			ImGui.TextDisabled(header);

			if (_modelPreview.LoadError is not null)
			{
				ImGui.SameLine();
				ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), _modelPreview.LoadError);
			}

			if (_modelPreview.IsSubMeshView)
			{
				RenderModelPreviewModeToolbar();
			}

			// Yaw/height are offsets from the environment's base sun; height moves the light only
			// (an equirect panorama cannot be rotated in elevation).
			if (_modelPreview.HasModel)
			{
				float lightYaw = _modelPreview.LightYawDegrees;
				float lightElevation = _modelPreview.LightElevationDegrees;
				bool lightChanged = false;

				ImGui.SetNextItemWidth(160f * _scale);
				lightChanged |= ImGui.SliderFloat("Light Yaw", ref lightYaw, -180f, 180f, "%.0f deg");

				ImGui.SameLine();

				ImGui.SetNextItemWidth(160f * _scale);
				lightChanged |= ImGui.SliderFloat("Height", ref lightElevation, -60f, 60f, "%.0f deg");

				if (lightChanged)
				{
					_modelPreview.SetLightRotation(lightYaw, lightElevation);
				}
			}

			ImGui.Separator();

			var avail = ImGui.GetContentRegionAvail();
			if (avail.X > 1f && avail.Y > 1f)
			{
				var fitSize = avail.X / avail.Y > _previewAspectRatio
					? new Vector2(avail.Y * _previewAspectRatio, avail.Y)
					: new Vector2(avail.X, avail.X / _previewAspectRatio);
				var cursor = ImGui.GetCursorPos();
				ImGui.SetCursorPos(cursor + (avail - fitSize) * 0.5f);
				_modelPreview.Render(_imGuiRender, fitSize);
			}
		}

		private static readonly string[] ModelPreviewViewModeLabels = { "Highlight", "Channel", "Lighting" };
		private static readonly string[] ModelPreviewChannelLabels = { "Normal", "UV", "Tangent" };

		// Shown only while a single sub-mesh is isolated; the whole-model view is always Lighting (PBR).
		private void RenderModelPreviewModeToolbar()
		{
			int modeIndex = (int)_modelPreview!.ViewMode;
			ImGui.SetNextItemWidth(140f * _scale);
			if (ImGui.Combo("View Mode", ref modeIndex, ModelPreviewViewModeLabels, ModelPreviewViewModeLabels.Length))
			{
				_modelPreview.SetSubMeshViewMode((ModelPreviewViewport.SubMeshPreviewMode)modeIndex);
			}

			ImGui.SameLine();

			bool wireframe = _modelPreview.WireframeEnabled;
			if (ImGui.Checkbox("Wireframe", ref wireframe))
			{
				_modelPreview.SetWireframeEnabled(wireframe);
			}

			if (_modelPreview.ViewMode != ModelPreviewViewport.SubMeshPreviewMode.Channel)
			{
				return;
			}

			ImGui.SameLine();

			int channelIndex = (int)_modelPreview.Channel;
			bool hasUv = _modelPreview.CurrentSubMeshHasUv;

			ImGui.SetNextItemWidth(120f * _scale);
			if (ImGui.BeginCombo("Channel", ModelPreviewChannelLabels[channelIndex]))
			{
				for (int i = 0; i < ModelPreviewChannelLabels.Length; i++)
				{
					bool isTangent = i == (int)ModelPreviewViewport.PreviewChannel.Tangent;
					bool enabled = !isTangent || hasUv;

					ImGui.BeginDisabled(!enabled);
					if (ImGui.Selectable(ModelPreviewChannelLabels[i], i == channelIndex) && enabled)
					{
						_modelPreview.SetPreviewChannel((ModelPreviewViewport.PreviewChannel)i);
					}
					ImGui.EndDisabled();
				}
				ImGui.EndCombo();
			}

			if (!hasUv)
			{
				ImGui.SameLine();
				ImGui.TextDisabled("(no UVs - Tangent unavailable)");
			}
		}

		private void RenderToolbar()
		{
			ImGui.TextDisabled(Path.GetFileName(_prefabPath));
			if (_dirty)
			{
				ImGui.SameLine();
				ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), "*");
			}

			ImGui.BeginDisabled(_isPlaying);
			if (ImGui.Button("Save"))
			{
				PrefabAsset.SaveJson(_root, _prefabPath!);
				var binPath = Path.ChangeExtension(_prefabPath, ".bin");
				PrefabAsset.Cook(_prefabPath!, binPath);
				_dirty = false;
			}

			ImGui.SameLine();
			if (ImGui.Button("Reload"))
			{
				Reload();
			}
			ImGui.EndDisabled();

			ImGui.SameLine();
			// Edits made while playing do not persist - Stop() reverts everything ECS-side.
			if (_isPlaying)
			{
				ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.48f, 0.20f, 0.17f, 1f));
				ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.58f, 0.26f, 0.22f, 1f));
				ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.68f, 0.30f, 0.25f, 1f));
				if (ImGui.Button("Stop"))
				{
					Stop();
				}
				ImGui.PopStyleColor(3);
			}
			else
			{
				ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.20f, 0.42f, 0.24f, 1f));
				ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.25f, 0.52f, 0.30f, 1f));
				ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.30f, 0.60f, 0.35f, 1f));
				if (ImGui.Button("Play"))
				{
					Play();
				}
				ImGui.PopStyleColor(3);
			}

			ImGui.SameLine();
			ImGui.Checkbox("Debug", ref _debugMode);
		}

		private void RenderNode(Entity entity)
		{
			bool hadChildrenAtRenderStart = entity.ChildCount > 0;
			bool isRenaming = _renamingEntityId == entity.Id;

			ImGui.PushID(entity.Id);

			bool opened;
			if (isRenaming)
			{
				opened = !_expandedState.TryGetValue(entity.Id, out var storedOpened) || storedOpened;
				RenderRenameInput(entity);
			}
			else
			{
				var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.DefaultOpen;
				if (!hadChildrenAtRenderStart)
				{
					flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;
				}
				if (_selected.HasValue && _selected.Value.Pid == entity.Pid)
				{
					flags |= ImGuiTreeNodeFlags.Selected;
				}

				var label = GetEntityLabel(entity);

				EditorSelectionStyle.PushColors();
				// Three leading spaces reserve room for the marker dot drawn below via the drawlist.
				opened = ImGui.TreeNodeEx("   " + label, flags);
				EditorSelectionStyle.PopColors();
				if (hadChildrenAtRenderStart)
				{
					_expandedState[entity.Id] = opened;
				}

				var nodeDrawList = ImGui.GetWindowDrawList();
				var nodeMin = ImGui.GetItemRectMin();
				var nodeMax = ImGui.GetItemRectMax();
				float markerX = nodeMin.X + ImGui.GetTreeNodeToLabelSpacing() + 5f * _scale;
				float markerY = (nodeMin.Y + nodeMax.Y) * 0.5f;
				nodeDrawList.AddCircleFilled(new Vector2(markerX, markerY), 3.2f * _scale,
					ImGui.GetColorU32(GetEntityMarkerColor(entity)));

				if (hadChildrenAtRenderStart)
				{
					var countText = entity.ChildCount.ToString();
					var countSize = ImGui.CalcTextSize(countText);
					nodeDrawList.AddText(
						new Vector2(nodeMax.X - countSize.X - 6f * _scale, (nodeMin.Y + nodeMax.Y - countSize.Y) * 0.5f),
						ImGui.GetColorU32(EditorPalette.WithAlpha(EditorPalette.Text, 0.35f)), countText);
				}

				if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen())
				{
					_selected = entity;
				}

				// Drag payload is the persistent id - the value EntityRef fields store.
				if (ImGui.BeginDragDropSource())
				{
					long pid = entity.Pid;
					unsafe
					{
						ImGui.SetDragDropPayload(DecaEngine.Core.Entities.EntityRef.DragDropPayloadType, &pid, (nuint)sizeof(long));
					}
					ImGui.TextUnformatted(label);
					ImGui.EndDragDropSource();
				}
			}

			bool entityDeleted = RenderNodeContextMenu(entity);

			if (!entityDeleted && opened && hadChildrenAtRenderStart)
			{
				if (isRenaming)
				{
					ImGui.Indent();
				}

				foreach (var child in entity.ChildEntities)
				{
					RenderNode(child);
				}

				if (isRenaming)
				{
					ImGui.Unindent();
				}
				else
				{
					ImGui.TreePop();
				}
			}
			ImGui.PopID();
		}

		private void RenderRenameInput(Entity entity)
		{
			float spacing = ImGui.GetTreeNodeToLabelSpacing();
			var cursor = ImGui.GetCursorScreenPos();
			ImGui.SetCursorScreenPos(new Vector2(cursor.X + spacing, cursor.Y));
			ImGui.SetNextItemWidth(MathF.Max(40f * _scale, ImGui.GetContentRegionAvail().X - spacing));

			if (!_renameFocusPending)
			{
				ImGui.SetKeyboardFocusHere();
				_renameFocusPending = true;
			}

			bool submitted = ImGui.InputText("##rename", ref _renameBuffer, 256,
				ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
			bool cancelled = ImGui.IsItemActive() && ImGui.IsKeyPressed(ImGuiKey.Escape);
			bool lostFocus = ImGui.IsItemDeactivated();

			if (cancelled)
			{
				CancelRename();
			}
			else if (submitted || lostFocus)
			{
				CommitRename(entity);
			}
		}

		private static string GetEntityLabel(Entity entity) =>
			entity.HasName && !string.IsNullOrEmpty(entity.Name.value) ? entity.Name.value : $"Entity {entity.Pid}";

		// Branch order is by role rarity: light/camera outrank merely having a mesh.
		private static Vector4 GetEntityMarkerColor(Entity entity)
		{
			if (entity.HasComponent<LightComponent>())
			{
				return new Vector4(1.0f, 0.78f, 0.35f, 1f);
			}
			if (entity.HasComponent<CameraComponent>())
			{
				return new Vector4(0.45f, 0.70f, 1.0f, 1f);
			}
			if (entity.HasComponent<ModelRenderer>())
			{
				return EditorPalette.IconAccent;
			}
			if (entity.Scripts.Length > 0)
			{
				return new Vector4(0.55f, 0.85f, 0.55f, 1f);
			}

			return EditorPalette.WithAlpha(EditorPalette.Text, 0.35f);
		}

		private void BeginRename(Entity entity)
		{
			_renamingEntityId = entity.Id;
			_renameBuffer = entity.HasName ? entity.Name.value : string.Empty;
			_renameFocusPending = false;
		}

		private void CancelRename()
		{
			_renamingEntityId = -1;
			_renameFocusPending = false;
		}

		private void CommitRename(Entity entity)
		{
			if (!entity.HasName)
			{
				entity.AddComponent<EntityName>();
			}
			entity.Name.value = _renameBuffer;
			MarkChanged();
			_renamingEntityId = -1;
			_renameFocusPending = false;
		}

		private bool RenderNodeContextMenu(Entity entity)
		{
			bool deleted = false;

			if (!ImGui.BeginPopupContextItem("##NodeContext"))
			{
				return false;
			}

			PopupContextMenu.DrawBackdrop();

			if (ImGui.MenuItem("Rename", "F2"))
			{
				BeginRename(entity);
			}

			if (ImGui.MenuItem("Add Child"))
			{
				MarkChanged();
				var child = _store!.CreateEntity(new Position(0, 0, 0));
				entity.AddChild(child);
				_selected = child;
			}

			ImGui.Separator();

			// These act on the clicked entity, not _selected: right-click must not change selection.
			bool notRoot = entity.Pid != _root.Pid;

			ImGui.BeginDisabled(!notRoot);
			if (ImGui.MenuItem("Duplicate", "Ctrl+D"))
			{
				DuplicateEntity(entity);
			}
			ImGui.EndDisabled();

			if (ImGui.MenuItem("Copy", "Ctrl+C"))
			{
				CopyEntity(entity);
			}

			if (ImGui.MenuItem("Paste As Child", "Ctrl+V"))
			{
				PasteInto(entity);
			}

			if (ImGui.MenuItem("Copy Name"))
			{
				CopyEntityName(entity);
			}

			ImGui.Separator();

			ImGui.BeginDisabled(!notRoot);
			if (ImGui.MenuItem("Delete", "Del"))
			{
				deleted = DeleteEntityWithUndo(entity);
			}
			ImGui.EndDisabled();

			ImGui.EndPopup();
			return deleted;
		}

		private void RenderInspector()
		{
			if (!_selected.HasValue)
			{
				ImGui.TextDisabled("No entity selected.");
				return;
			}

			var entity = _selected.Value;

			ImGui.TextUnformatted(GetEntityLabel(entity));
			ImGui.SameLine();
			ImGui.TextDisabled($"pid {entity.Pid}");
			if (entity.Pid == _root.Pid)
			{
				ImGui.SameLine();
				ImGui.TextColored(EditorPalette.WithAlpha(EditorPalette.Selection, 0.8f), "root");
			}
			ImGui.Separator();


			RenderTransform(entity);
			ImGui.Spacing();
			RenderComponentsSection(entity);

			if (_debugMode)
			{
				ImGui.Separator();
				RenderRawComponentsJson(entity);
			}

			RenderAddComponentContextMenu(entity);
		}


		private void RenderTransform(Entity entity)
		{
			if (!entity.HasPosition && !entity.HasRotation && !entity.HasScale3)
			{
				return;
			}

			ImGui.PushStyleColor(ImGuiCol.Header, EditorPalette.WithAlpha(EditorPalette.Selection, 0.30f));
			ImGui.PushStyleColor(ImGuiCol.HeaderHovered, EditorPalette.WithAlpha(EditorPalette.Selection, 0.42f));
			ImGui.PushStyleColor(ImGuiCol.HeaderActive, EditorPalette.WithAlpha(EditorPalette.Selection, 0.55f));
			bool open = ImGui.CollapsingHeader("Transform", ImGuiTreeNodeFlags.DefaultOpen);
			ImGui.PopStyleColor(3);

			if (!open)
			{
				return;
			}

			var drawList = ImGui.GetWindowDrawList();
			var spineTop = ImGui.GetCursorScreenPos();

			ImGui.Indent();

			if (entity.HasPosition)
			{
				var pos = entity.Position.value;
				if (ImGui.DragFloat3("Position", ref pos, 0.05f))
				{
					entity.Position = new Position(pos.X, pos.Y, pos.Z);
					MarkChanged();
				}
			}

			if (entity.HasRotation)
			{
				if (_eulerEntityId != entity.Id)
				{
					_tempEuler = MathUtils.ToEulerAngles(entity.Rotation.value) * (180f / MathF.PI);
					_eulerEntityId = entity.Id;
				}
				if (ImGui.DragFloat3("Rotation (Euler)", ref _tempEuler, 0.5f))
				{
					var rad = _tempEuler * (MathF.PI / 180f);
					entity.Rotation = new Rotation { value = Quaternion.CreateFromYawPitchRoll(rad.Y, rad.X, rad.Z) };
					MarkChanged();
				}
			}

			if (entity.HasScale3)
			{
				var scale = entity.Scale3.value;
				if (ImGui.DragFloat3("Scale", ref scale, 0.05f))
				{
					entity.Scale3 = new Scale3(scale.X, scale.Y, scale.Z);
					MarkChanged();
				}
			}

			ImGui.Unindent();

			var spineBottom = ImGui.GetCursorScreenPos();
			var spineX = spineTop.X + 4f * _scale;
			drawList.AddLine(new Vector2(spineX, spineTop.Y), new Vector2(spineX, spineBottom.Y), ImGui.GetColorU32(EditorPalette.Selection), MathF.Max(1.5f, 2f * _scale));
		}

		private static readonly HashSet<Type> TransformComponentTypes = new()
		{
			typeof(Position),
			typeof(Rotation),
			typeof(Scale3),
			typeof(EntityName),
			typeof(TreeNode)
		};

		private void RenderComponentsSection(Entity entity)
		{
			if (ComponentFieldEditor.DrawComponents(entity, TransformComponentTypes))
			{
				MarkChanged();
			}
		}

		private void RenderAddComponentContextMenu(Entity entity)
		{
			bool popupOpenAtFrameStart = PopupContextMenu.IsAnyPopupOpen();
			bool rightClicked = ImGui.IsMouseClicked(ImGuiMouseButton.Right);

			if (rightClicked &&
			    ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByPopup) &&
			    !ImGui.IsAnyItemHovered())
			{
				_addComponentFilter = string.Empty;
				PopupContextMenu.TryOpen("InspectorAddComponentMenu", !popupOpenAtFrameStart);
			}

			if (!PopupContextMenu.BeginPopup("InspectorAddComponentMenu"))
			{
				return;
			}

			ImGui.SetNextItemWidth(280f * _scale);
			ImGui.InputTextWithHint("##AddComponentSearch", "Search...", ref _addComponentFilter, 128);

			// Paste is offered here too: a fresh entity has no component headers, hence no Paste menu.
			if (ComponentFieldEditor.ComponentClipboardKey is { } clipboardKey &&
				ImGui.MenuItem($"Paste Component ({clipboardKey})"))
			{
				if (ComponentFieldEditor.TryPasteComponent(entity))
				{
					MarkChanged();
				}
				ImGui.CloseCurrentPopup();
			}

			ImGui.Separator();

			if (ImGui.BeginTabBar("##AddComponentTabs"))
			{
				RenderAddComponentTab("Game", entity, isEngineAssembly: true);
				RenderAddComponentTab("Behaviour", entity, isEngineAssembly: false);
				ImGui.EndTabBar();
			}

			ImGui.EndPopup();
		}


		private void RenderAddComponentTab(string tabName, Entity entity, bool isEngineAssembly)
		{
			if (!ImGui.BeginTabItem(tabName))
			{
				return;
			}

			if (ImGui.BeginChild("##AddComponentList_" + tabName, new Vector2(300f * _scale, 260f * _scale)))
			{
				if (isEngineAssembly)
				{
					RenderRegisteredComponents(entity);
				}
				else
				{
					RenderBehaviourScripts(entity);
				}
			}
			ImGui.EndChild();
			ImGui.EndTabItem();
		}

		private void RenderRegisteredComponents(Entity entity)
		{
			var root = ComponentRegistry.BuildTree();
			if (root.IsEmpty)
			{
				ImGui.TextDisabled("Nothing registered.\nUse ComponentRegistry.RegisterComponent<T>().");
				return;
			}

			bool any = RenderComponentMenuNode(root, entity);
			if (!any)
			{
				ImGui.TextDisabled("Nothing found.");
			}
		}

		private bool RenderComponentMenuNode(ComponentRegistry.MenuNode node, Entity entity)
		{
			bool any = false;

			foreach (var leaf in node.Leaves)
			{
				if (!MatchesFilter(leaf.DisplayName))
				{
					continue;
				}
				any = true;
				if (ImGui.Selectable(leaf.DisplayName))
				{
					AddRegisteredComponent(entity, leaf);
				}
			}

			foreach (var child in node.Children.Values)
			{
				if (!NodeMatchesFilter(child))
				{
					continue;
				}
				any = true;
				if (ImGui.BeginMenu(child.Name))
				{
					RenderComponentMenuNode(child, entity);
					ImGui.EndMenu();
				}
			}

			return any;
		}

		private bool NodeMatchesFilter(ComponentRegistry.MenuNode node)
		{
			foreach (var leaf in node.Leaves)
			{
				if (MatchesFilter(leaf.DisplayName))
				{
					return true;
				}
			}
			foreach (var child in node.Children.Values)
			{
				if (NodeMatchesFilter(child))
				{
					return true;
				}
			}
			return false;
		}

		private void AddRegisteredComponent(Entity entity, ComponentRegistryEntry entry)
		{
			var schema = EntityStore.GetEntitySchema();
			if (entry.Kind == ComponentRegistryKind.Component)
			{
				var componentType = ComponentRegistry.ResolveComponentType(schema, entry.ClrType);
				if (componentType != null)
				{
					EntityUtils.AddEntityComponent(entity, componentType);
				}
			}
			else
			{
				var scriptType = ComponentRegistry.ResolveScriptType(schema, entry.ClrType);
				if (scriptType != null)
				{
					EntityUtils.AddNewEntityScript(entity, scriptType);
				}
			}
			MarkChanged();
			ImGui.CloseCurrentPopup();
		}

		private void RenderBehaviourScripts(Entity entity)
		{
			var schema = EntityStore.GetEntitySchema();
			bool any = false;

			foreach (var scriptType in schema.Scripts)
			{
				if (scriptType is null)
				{
					continue;
				}
				if (!MatchesTab(scriptType.Type, isEngineAssembly: false) || !MatchesFilter(scriptType.Name))
				{
					continue;
				}
				any = true;
				if (ImGui.Selectable($"{scriptType.Name}  (script)"))
				{
					EntityUtils.AddNewEntityScript(entity, scriptType);
					MarkChanged();
					ImGui.CloseCurrentPopup();
				}
			}

			if (!any)
			{
				ImGui.TextDisabled("Nothing found.");
			}
		}

		private bool MatchesFilter(string? name) =>
			string.IsNullOrEmpty(_addComponentFilter) || (!string.IsNullOrEmpty(name) && name.Contains(_addComponentFilter, StringComparison.OrdinalIgnoreCase));

		private static bool MatchesTab(Type? type, bool isEngineAssembly)
		{
			if (type is null)
			{
				return false;
			}
			var assemblyName = type.Assembly.GetName().Name ?? string.Empty;
			bool isEngine = assemblyName.StartsWith("Friflo", StringComparison.OrdinalIgnoreCase)
				|| assemblyName.StartsWith("DecaEngine", StringComparison.OrdinalIgnoreCase);
			return isEngine == isEngineAssembly;
		}

		private void RenderRawComponentsJson(Entity entity)
		{
			if (!ImGui.CollapsingHeader("Raw components JSON", ImGuiTreeNodeFlags.DefaultOpen))
			{
				return;
			}

			ImGui.TextDisabled("Fallback editor for any component/tag/nested-prefab marker ($prefab).");

			if (_componentsBufferEntityId != entity.Id)
			{
				_componentsBuffer = PrefabAsset.GetComponentsJson(entity);
				_componentsBufferEntityId = entity.Id;
				_applyError = null;
			}

			ImGui.InputTextMultiline("##ComponentsJson", ref _componentsBuffer, (nuint)16384, new Vector2(-1, 220f * _scale));

			if (ImGui.Button("Apply JSON"))
			{
				if (PrefabAsset.TryApplyComponentsJson(entity, _componentsBuffer, out var error))
				{
					_applyError = null;
					MarkChanged();
					_componentsBuffer = PrefabAsset.GetComponentsJson(entity);
				}
				else
				{
					_applyError = error;
				}
			}

			ImGui.SameLine();
			if (ImGui.Button("Refresh"))
			{
				_componentsBuffer = PrefabAsset.GetComponentsJson(entity);
				_applyError = null;
			}

			if (_applyError != null)
			{
				ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), _applyError);
			}
		}
	}
}