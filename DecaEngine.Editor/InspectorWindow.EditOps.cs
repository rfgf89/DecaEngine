using System.Text;
using DecaEngine.Core.Diagnostics;
using DecaEngine.Core.Prefabs;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Serialize;
using Friflo.Engine.ECS.Utils;
using Friflo.Json.Fliox;
using Hexa.NET.ImGui;

namespace DecaEngine.Editor
{
	/// <summary>
	/// Prefab tree edit operations: Undo/Redo and Copy/Paste/Duplicate/Delete of entities.
	/// Part of <see cref="InspectorWindow"/>.
	///
	/// Undo is built on SNAPSHOTS of the whole subtree - the same DataEntity primitives Play Mode
	/// uses to roll changes back (see Play/Stop): before the first mutation of a "gesture" the state
	/// is serialized to JSON, and Ctrl+Z rebuilds the entire EntityStore from that snapshot the way
	/// Reload rebuilds it from the file. There is no command system: the "gesture = one undo step"
	/// granularity comes from per-frame coalescing (see <see cref="MarkChanged"/>), and correctness
	/// comes from restore never having to guess an inverse operation.
	///
	/// The entity clipboard is the SYSTEM clipboard, holding the same JSON array as .prefab.json:
	/// a copied entity can be pasted into another prefab or into a text editor, and a fragment
	/// copied by hand out of a prefab file pastes back in.
	/// </summary>
	public partial class InspectorWindow
	{
		/// <summary>Undo stack cap. A snapshot is JSON of the whole subtree (a few KB for typical
		/// prefabs), so 64 steps cost almost nothing; steps pushed off the bottom are simply lost.</summary>
		private const int MaxUndoDepth = 64;

		private readonly List<byte[]> _undoStack = new();
		private readonly List<byte[]> _redoStack = new();

		/// <summary>State snapshot taken at the START of the frame - what goes onto the undo stack if
		/// the first mutation of a gesture happens this frame. Re-taken every frame (see
		/// <see cref="CaptureFrameSnapshot"/>), so at any mutation this is guaranteed to hold the
		/// PRE-mutation state. null = already consumed this frame, or no prefab is open.</summary>
		private byte[]? _frameStartSnapshot;

		/// <summary>Frame of the last mutation, used for coalescing: changes in adjacent frames
		/// (slider drag, gizmo) fold into ONE undo step.</summary>
		private int _lastChangeFrame = -10;

		/// <summary>A gesture with the left mouse button held is still in progress: pausing mid-drag
		/// (frames with no value change) must not split it into several undo steps.</summary>
		private bool _dragGestureActive;

		public bool CanUndo => _mode == InspectorMode.Prefab && !_isPlaying && _undoStack.Count > 0;
		public bool CanRedo => _mode == InspectorMode.Prefab && !_isPlaying && _redoStack.Count > 0;

		/// <summary>Whether there is anything to copy/duplicate (for the Edit menu items).</summary>
		public bool HasPrefabSelection => _mode == InspectorMode.Prefab && !_isPlaying && _selected.HasValue;

		/// <summary>Whether pasting is possible (a prefab is open and not playing). Clipboard contents
		/// are not checked - reading the system clipboard every menu frame is not worth it.</summary>
		public bool CanPasteEntity => _mode == InspectorMode.Prefab && !_isPlaying && _store is not null;

		// ------------------------------------------------------------------
		// Undo/Redo
		// ------------------------------------------------------------------

		private byte[] CaptureSnapshot() =>
			TreeUtils.EntitiesToJsonArray(new[] { _root }).entities.AsByteArray();

		/// <summary>Called at the top of OnRender every frame. Serializing the subtree once per frame is
		/// a deliberate price for simplicity: typical prefabs hold dozens of entities, which is cheaper
		/// than capturing the "state before the change" in each of the dozen places that can mutate the
		/// tree. If prefabs ever grow to thousands of entities, capture lazily on the frame's first input.</summary>
		private void CaptureFrameSnapshot()
		{
			if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
			{
				_dragGestureActive = false;
			}

			if (_store is null || _isPlaying)
			{
				_frameStartSnapshot = null;
				return;
			}

			_frameStartSnapshot = CaptureSnapshot();
		}

		/// <summary>
		/// The single "the tree changed" entry point: sets the dirty marker and pushes the pre-mutation
		/// snapshot onto the undo stack. Coalescing: changes in adjacent frames OR within one hold of the
		/// left mouse button (slider drag, gizmo, a pause mid-drag) form one undo step; discrete clicks
		/// (checkbox, Delete, Paste) get a step each.
		/// </summary>
		private void MarkChanged()
		{
			_dirty = true;

			if (_store is null || _isPlaying)
			{
				return;
			}

			int frame = ImGui.GetFrameCount();
			bool mouseHeld = ImGui.IsMouseDown(ImGuiMouseButton.Left);
			bool continuation = frame - _lastChangeFrame <= 1 || (mouseHeld && _dragGestureActive);
			_lastChangeFrame = frame;
			_dragGestureActive = mouseHeld;

			if (continuation || _frameStartSnapshot is null)
			{
				return;
			}

			_undoStack.Add(_frameStartSnapshot);
			if (_undoStack.Count > MaxUndoDepth)
			{
				_undoStack.RemoveAt(0);
			}

			_redoStack.Clear();

			// Snapshot consumed: a second discrete mutation in the SAME frame must not push the same
			// array as another step (two Ctrl+Z for one action).
			_frameStartSnapshot = null;
		}

		public void Undo()
		{
			if (!CanUndo)
			{
				return;
			}

			var current = CaptureSnapshot();
			var snapshot = _undoStack[^1];
			_undoStack.RemoveAt(_undoStack.Count - 1);
			_redoStack.Add(current);
			RestoreSnapshot(snapshot);
		}

		public void Redo()
		{
			if (!CanRedo)
			{
				return;
			}

			var current = CaptureSnapshot();
			var snapshot = _redoStack[^1];
			_redoStack.RemoveAt(_redoStack.Count - 1);
			_undoStack.Add(current);
			RestoreSnapshot(snapshot);
		}

		/// <summary>
		/// Rebuilds the EntityStore from a snapshot the same way Reload rebuilds it from the file
		/// (viewports resync with the new store on their own - already proven by the Reload button).
		/// The fresh store keeps the pids from the snapshot, so the selection is restored by pid and
		/// EntityRef component fields survive the undo.
		/// </summary>
		private void RestoreSnapshot(byte[] snapshotJson)
		{
			long selectedPid = _selected?.Pid ?? -1;
			var baseDir = Path.GetDirectoryName(Path.GetFullPath(_prefabPath!)) ?? ".";

			_store = new EntityStore();
			_root = PrefabAsset.InstantiateFromJson(_store, snapshotJson, baseDir);
			_selected = _store.TryGetEntityByPid(selectedPid, out var selected) ? selected : _root;

			_componentsBufferEntityId = -1;
			_eulerEntityId = -1;
			_applyError = null;
			_renamingEntityId = -1;
			_renameFocusPending = false;
			_expandedState.Clear();

			_dirty = true;
			_frameStartSnapshot = null;
			_lastChangeFrame = -10;
			_dragGestureActive = false;
		}

		// ------------------------------------------------------------------
		// Copy / Paste / Duplicate / Delete
		// ------------------------------------------------------------------

		public void CopySelected()
		{
			if (HasPrefabSelection)
			{
				CopyEntity(_selected!.Value);
			}
		}

		public void CutSelected()
		{
			if (HasPrefabSelection)
			{
				CopyEntity(_selected!.Value);
				DeleteEntityWithUndo(_selected!.Value);
			}
		}

		public void PasteIntoSelected()
		{
			if (CanPasteEntity)
			{
				PasteInto(_selected ?? _root);
			}
		}

		public void DuplicateSelected()
		{
			if (HasPrefabSelection)
			{
				DuplicateEntity(_selected!.Value);
			}
		}

		public void DeleteSelected()
		{
			if (HasPrefabSelection)
			{
				DeleteEntityWithUndo(_selected!.Value);
			}
		}

		/// <summary>Puts the entity subtree on the SYSTEM clipboard as the same JSON array used by
		/// .prefab.json - it pastes into another prefab and into a text editor alike.</summary>
		private void CopyEntity(Entity entity)
		{
			var json = TreeUtils.EntitiesToJsonArray(new[] { entity }).entities.AsString();
			ImGui.SetClipboardText(json);
		}

		private void CopyEntityName(Entity entity)
		{
			ImGui.SetClipboardText(GetEntityLabel(entity));
		}

		/// <summary>
		/// Pastes the JSON entity array from the system clipboard as a child subtree of
		/// <paramref name="parent"/>. AddDataEntitiesToEntity reindexes the pids to free ones itself, so
		/// pasting a copy next to the original does not overwrite the original. Known limitation:
		/// EntityRef fields INSIDE the pasted subtree keep pointing at entities with the original pids
		/// (for a duplicate, at the originals) - they are not retargeted into the copy.
		/// </summary>
		private void PasteInto(Entity parent)
		{
			var text = ImGui.GetClipboardTextS();
			if (string.IsNullOrWhiteSpace(text))
			{
				return;
			}

			var list = new List<DataEntity>();
			var error = TreeUtils.JsonArrayToDataEntities(new JsonValue(Encoding.UTF8.GetBytes(text)), list);
			if (error != null || list.Count == 0)
			{
				EngineLog.Add(LogLevel.Warning, $"Paste: clipboard is not a prefab entity JSON ({error ?? "empty"})");
				return;
			}

			// Find the fragment root BEFORE pasting: afterwards the pids are reindexed, but the list is
			// mutated in place, so the root index stays the same.
			int rootIndex = FindFragmentRootIndex(list);

			MarkChanged();

			var result = TreeUtils.AddDataEntitiesToEntity(parent, list);
			if (result.errors is { Count: > 0 })
			{
				EngineLog.Add(LogLevel.Error, "Paste errors: " + string.Join("; ", result.errors));
			}

			if (_store!.TryGetEntityByPid(list[rootIndex].pid, out var pastedRoot))
			{
				_selected = pastedRoot;
			}
		}

		/// <summary>Copies the subtree next to the original (same parent) and selects the copy. Done via
		/// serialization rather than element-by-element cloning: the DataEntity path carries absolutely
		/// everything - components, tags, scripts - and remains the single definition of "a whole
		/// entity".</summary>
		private void DuplicateEntity(Entity entity)
		{
			if (entity.Pid == _root.Pid)
			{
				return;
			}

			var parent = entity.Parent;
			var attach = parent.IsNull ? _root : parent;

			var json = TreeUtils.EntitiesToJsonArray(new[] { entity }).entities;
			var list = new List<DataEntity>();
			var error = TreeUtils.JsonArrayToDataEntities(json, list);
			if (error != null || list.Count == 0)
			{
				EngineLog.Add(LogLevel.Error, $"Duplicate failed: {error ?? "no entities"}");
				return;
			}

			int rootIndex = FindFragmentRootIndex(list);

			MarkChanged();

			var result = TreeUtils.AddDataEntitiesToEntity(attach, list);
			if (result.errors is { Count: > 0 })
			{
				EngineLog.Add(LogLevel.Error, "Duplicate errors: " + string.Join("; ", result.errors));
			}

			if (_store!.TryGetEntityByPid(list[rootIndex].pid, out var copy))
			{
				_selected = copy;
			}
		}

		/// <summary>Delete with an undo step - the shared path for the context menu, the Delete key and
		/// Cut. The prefab root is never deleted (without it the document is meaningless).</summary>
		private bool DeleteEntityWithUndo(Entity entity)
		{
			if (entity.Pid == _root.Pid)
			{
				return false;
			}

			MarkChanged();

			var parent = entity.Parent;
			_selected = parent.IsNull ? _root : parent;
			if (_renamingEntityId == entity.Id)
			{
				CancelRename();
			}

			entity.DeleteEntity();
			return true;
		}

		/// <summary>Root of a JSON fragment: the entity whose pid appears in nobody's children - the same
		/// rule PrefabAsset uses when loading a document.</summary>
		private static int FindFragmentRootIndex(List<DataEntity> list)
		{
			var childPids = new HashSet<long>();
			foreach (var de in list)
			{
				if (de.children == null)
				{
					continue;
				}
				foreach (var child in de.children)
				{
					childPids.Add(child);
				}
			}

			for (int i = 0; i < list.Count; i++)
			{
				if (!childPids.Contains(list[i].pid))
				{
					return i;
				}
			}

			return 0;
		}

		// ------------------------------------------------------------------
		// Keyboard shortcuts
		// ------------------------------------------------------------------

		/// <summary>Called from OnRender in prefab mode. Active only while the window (or its child
		/// panels) has focus and input is not going into a text field - otherwise Ctrl+C in the name
		/// field would delete entities instead of copying text.</summary>
		private void HandleEditShortcuts()
		{
			if (_isPlaying)
			{
				return;
			}

			var io = ImGui.GetIO();
			if (io.WantTextInput || !ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
			{
				return;
			}

			bool ctrl = io.KeyCtrl;
			if (ctrl && ImGui.IsKeyPressed(ImGuiKey.Z, false))
			{
				Undo();
			}
			else if (ctrl && ImGui.IsKeyPressed(ImGuiKey.Y, false))
			{
				Redo();
			}
			else if (ctrl && ImGui.IsKeyPressed(ImGuiKey.C, false))
			{
				CopySelected();
			}
			else if (ctrl && ImGui.IsKeyPressed(ImGuiKey.X, false))
			{
				CutSelected();
			}
			else if (ctrl && ImGui.IsKeyPressed(ImGuiKey.V, false))
			{
				PasteIntoSelected();
			}
			else if (ctrl && ImGui.IsKeyPressed(ImGuiKey.D, false))
			{
				DuplicateSelected();
			}
			else if (ImGui.IsKeyPressed(ImGuiKey.Delete, false))
			{
				DeleteSelected();
			}
			else if (ImGui.IsKeyPressed(ImGuiKey.F2, false) && _selected.HasValue)
			{
				BeginRename(_selected.Value);
			}
		}
	}
}
