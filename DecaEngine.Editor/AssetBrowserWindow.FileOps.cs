using System.Linq;
using System.Numerics;
using DecaEngine.Core.Prefabs;
using Engine.ImGui.Core;
using Friflo.Engine.ECS;
using Hexa.NET.ImGui;

namespace DecaEngine.Editor
{
	/// <summary>File operations: toolbar, breadcrumbs, context menu, rename, delete, paste.</summary>
	public partial class AssetBrowserWindow
	{
		private void RenderToolbar()
		{
			RenderBreadcrumbs();
		}

		private void RenderBreadcrumbs()
		{
			if (_assetsRoot is null || _currentDirectory is null)
			{
				return;
			}

			var relative = Path.GetRelativePath(_assetsRoot, _currentDirectory);
			var path = relative == "." ? "Assets" : "Assets/" + relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

			ImGui.TextUnformatted(path);
		}

		private void NavigateTo(string directory)
		{
			_currentDirectory = directory;
			_selectedPath = null;
			RefreshEntries();
		}

		// Result is forward-slash relative to the Assets root, as AssetRef expects.
		private string GetAssetRelativePath(string fullPath)
		{
			if (_assetsRoot is null)
			{
				return fullPath.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
			}

			var relative = Path.GetRelativePath(_assetsRoot, fullPath);
			return relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
		}


		private void RenderContextMenuContent()
		{
			var path = _contextPath;
			if (string.IsNullOrEmpty(path))
			{
				return;
			}

			var isRoot = string.Equals(path, _assetsRoot, StringComparison.OrdinalIgnoreCase);

			if (_contextPathIsDirectory)
			{
				ImGui.TextDisabled(isRoot ? "Assets" : Path.GetFileName(path));
				if (!string.Equals(path, _currentDirectory, StringComparison.OrdinalIgnoreCase) && ImGui.MenuItem("Open"))
				{
					NavigateTo(path);
				}

				if (ImGui.MenuItem("New Prefab"))
				{
					CreatePrefab(path);
				}

				ImGui.Separator();

				ImGui.BeginDisabled(_clipboardPath is null);
				if (ImGui.MenuItem("Paste"))
				{
					PasteClipboard(path);
				}
				ImGui.EndDisabled();

				if (!isRoot)
				{
					if (ImGui.MenuItem("Cut"))
					{
						_clipboardPath = path;
						_clipboardIsCut = true;
					}

					if (ImGui.MenuItem("Copy"))
					{
						_clipboardPath = path;
						_clipboardIsCut = false;
					}

					if (ImGui.MenuItem("Rename"))
					{
						BeginRenameAsset(path);
					}
				}
			}
			else
			{
				ImGui.TextDisabled(Path.GetFileName(path));
				if (IsPrefabJson(path))
				{
					if (ImGui.MenuItem("Cook to .bin"))
					{
						PrefabAsset.Cook(path, Path.ChangeExtension(path, ".bin"));
						RefreshEntries();
					}
				}
				else if (IsGltfModel(path) && ImGui.MenuItem("Open Preview"))
				{
					_inspectorWindow.ShowModel(path);
				}


				ImGui.Separator();

				if (ImGui.MenuItem("Cut"))
				{
					_clipboardPath = path;
					_clipboardIsCut = true;
				}

				if (ImGui.MenuItem("Copy"))
				{
					_clipboardPath = path;
					_clipboardIsCut = false;
				}

				if (ImGui.MenuItem("Rename"))
				{
					BeginRenameAsset(path);
				}
			}

			ImGui.Separator();
			if (ImGui.MenuItem("Copy Path"))
			{
				ImGui.SetClipboardText(path);
			}

			if (!isRoot)
			{
				var deleteColor = new Vector4(1f, 0.45f, 0.45f, 1f);
				ImGui.PushStyleColor(ImGuiCol.Text, deleteColor);
				if (ImGui.MenuItem("Delete"))
				{
					_deleteConfirmPath = path;
				}
				ImGui.PopStyleColor();
			}
		}

		private void BeginRenameAsset(string path)
		{
			_renamingPath = path;
			_renameBuffer = Path.GetFileName(path);
			_renameFocusPending = false;
		}

		private void RenderRenamePopup()
		{
			const string popupId = "AssetRenamePopup";

			if (_renamingPath is null)
			{
				return;
			}

			if (!ImGui.IsPopupOpen(popupId))
			{
				ImGui.OpenPopup(popupId);
			}

			var viewport = ImGui.GetMainViewport();
			ImGui.SetNextWindowPos(viewport.Pos + viewport.Size / 2, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));

			if (!PopupContextMenu.BeginPopup(popupId, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove))
			{
				_renamingPath = null;
				return;
			}

			ImGui.TextDisabled("Rename");
			ImGui.Separator();

			if (!_renameFocusPending)
			{
				ImGui.SetKeyboardFocusHere();
				_renameFocusPending = true;
			}

			ImGui.SetNextItemWidth(280f * _scale);
			bool submitted = ImGui.InputText("##AssetRenameInput", ref _renameBuffer, 260,
				ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);

			ImGui.Spacing();

			if (ImGui.Button("Rename") || submitted)
			{
				CommitRenameAsset();
				ImGui.CloseCurrentPopup();
			}

			ImGui.SameLine();
			if (ImGui.Button("Cancel"))
			{
				_renamingPath = null;
				ImGui.CloseCurrentPopup();
			}

			ImGui.EndPopup();
		}

		private void CommitRenameAsset()
		{
			var path = _renamingPath;
			_renamingPath = null;

			if (path is null || string.IsNullOrWhiteSpace(_renameBuffer))
			{
				return;
			}

			var directory = Path.GetDirectoryName(path);
			if (directory is null)
			{
				return;
			}

			var newPath = Path.Combine(directory, _renameBuffer);
			if (string.Equals(newPath, path, StringComparison.Ordinal))
			{
				return;
			}

			try
			{
				if (Directory.Exists(path))
				{
					Directory.Move(path, newPath);
				}
				else if (File.Exists(path))
				{
					File.Move(path, newPath);
				}

				if (string.Equals(_selectedPath, path, StringComparison.OrdinalIgnoreCase))
				{
					_selectedPath = newPath;
				}

				if (string.Equals(_currentDirectory, path, StringComparison.OrdinalIgnoreCase))
				{
					_currentDirectory = newPath;
				}
			}
			catch
			{
			}

			RefreshEntries();
		}

		private void RenderDeleteConfirmPopup()
		{
			const string popupId = "AssetDeleteConfirmPopup";

			if (_deleteConfirmPath is null)
			{
				return;
			}

			if (!ImGui.IsPopupOpen(popupId))
			{
				ImGui.OpenPopup(popupId);
			}

			var viewport = ImGui.GetMainViewport();
			ImGui.SetNextWindowPos(viewport.Pos + viewport.Size / 2, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));

			if (!PopupContextMenu.BeginPopup(popupId, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove))
			{
				_deleteConfirmPath = null;
				return;
			}

			ImGui.Text($"Delete \"{Path.GetFileName(_deleteConfirmPath)}\"?");
			ImGui.TextDisabled("This cannot be undone.");
			ImGui.Spacing();

			ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.55f, 0.18f, 0.18f, 1f));
			ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.7f, 0.2f, 0.2f, 1f));
			ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.8f, 0.25f, 0.25f, 1f));
			if (ImGui.Button("Delete"))
			{
				CommitDeleteAsset();
				ImGui.CloseCurrentPopup();
			}
			ImGui.PopStyleColor(3);

			ImGui.SameLine();
			if (ImGui.Button("Cancel"))
			{
				_deleteConfirmPath = null;
				ImGui.CloseCurrentPopup();
			}

			ImGui.EndPopup();
		}

		private void CommitDeleteAsset()
		{
			var path = _deleteConfirmPath;
			_deleteConfirmPath = null;

			if (path is null)
			{
				return;
			}

			try
			{
				if (Directory.Exists(path))
				{
					Directory.Delete(path, true);
				}
				else if (File.Exists(path))
				{
					File.Delete(path);
				}

				if (string.Equals(_selectedPath, path, StringComparison.OrdinalIgnoreCase))
				{
					_selectedPath = null;
				}

				if (string.Equals(_clipboardPath, path, StringComparison.OrdinalIgnoreCase))
				{
					_clipboardPath = null;
				}
			}
			catch
			{
			}

			RefreshEntries();
		}

		private void PasteClipboard(string targetDirectory)
		{
			if (_clipboardPath is null)
			{
				return;
			}

			var sourcePath = _clipboardPath;
			var name = Path.GetFileName(sourcePath);
			var destination = MakeUniqueDestination(Path.Combine(targetDirectory, name));

			try
			{
				if (Directory.Exists(sourcePath))
				{
					if (_clipboardIsCut)
					{
						Directory.Move(sourcePath, destination);
					}
					else
					{
						CopyDirectoryRecursive(sourcePath, destination);
					}
				}
				else if (File.Exists(sourcePath))
				{
					if (_clipboardIsCut)
					{
						File.Move(sourcePath, destination);
					}
					else
					{
						File.Copy(sourcePath, destination);
					}
				}

				_selectedPath = destination;
			}
			catch
			{
			}

			if (_clipboardIsCut)
			{
				_clipboardPath = null;
			}

			RefreshEntries();
		}

		private static string MakeUniqueDestination(string destination)
		{
			if (!File.Exists(destination) && !Directory.Exists(destination))
			{
				return destination;
			}

			var directory = Path.GetDirectoryName(destination) ?? string.Empty;
			var extension = Path.GetExtension(destination);
			var nameWithoutExt = Path.GetFileNameWithoutExtension(destination);

			var index = 1;
			string candidate;
			do
			{
				candidate = Path.Combine(directory, $"{nameWithoutExt} ({index++}){extension}");
			}
			while (File.Exists(candidate) || Directory.Exists(candidate));

			return candidate;
		}

		private static void CopyDirectoryRecursive(string sourceDir, string destDir)
		{
			Directory.CreateDirectory(destDir);

			foreach (var file in Directory.GetFiles(sourceDir))
			{
				File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)));
			}

			foreach (var subDir in Directory.GetDirectories(sourceDir))
			{
				CopyDirectoryRecursive(subDir, Path.Combine(destDir, Path.GetFileName(subDir)));
			}
		}

		private void CreatePrefab(string directory)
		{
			var path = Path.Combine(directory, "New Prefab.prefab.json");
			int index = 1;
			while (File.Exists(path))
			{
				path = Path.Combine(directory, $"New Prefab {index++}.prefab.json");
			}

			var store = new EntityStore();
			PrefabAsset.SaveJson(store.CreateEntity(), path);
			RefreshEntries();
			_inspectorWindow.ShowPrefab(path);
		}

	}
}
