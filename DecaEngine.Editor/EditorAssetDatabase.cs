namespace DecaEngine.Editor;

/// <summary>
/// Index of every file bundled in the editor's "EditorAssets" folder, keyed by extension, so editor
/// UI (e.g. the Graphics Pipeline settings' shader pickers, see <see cref="EditorRefPicker"/>) can
/// offer a filtered dropdown instead of hand-typing an <see cref="DecaEngine.Core.Assets.EditorRef"/>
/// path. Built once via <see cref="Rescan"/> (called at editor startup) and lazily re-built on first
/// use if that never happened.
/// </summary>
public static class EditorAssetDatabase
{
	private static readonly Dictionary<string, List<string>> ByExtension = new(StringComparer.OrdinalIgnoreCase);
	private static bool _scanned;

	public static string RootPath => Path.Combine(Environment.CurrentDirectory, "EditorAssets");

	/// <summary>Walks "EditorAssets/" recursively and rebuilds the extension index from scratch.</summary>
	public static void Rescan()
	{
		ByExtension.Clear();

		var root = RootPath;
		if (Directory.Exists(root))
		{
			foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
			{
				var extension = Path.GetExtension(file);
				if (string.IsNullOrEmpty(extension))
				{
					continue;
				}

				if (!ByExtension.TryGetValue(extension, out var list))
				{
					list = new List<string>();
					ByExtension[extension] = list;
				}

				list.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
			}

			foreach (var list in ByExtension.Values)
			{
				list.Sort(StringComparer.OrdinalIgnoreCase);
			}
		}

		_scanned = true;
	}

	/// <summary>Paths (relative to "EditorAssets/") of every indexed file matching <paramref name="extension"/> (e.g. ".hlsl").</summary>
	public static IReadOnlyList<string> GetByExtension(string extension)
	{
		if (!_scanned)
		{
			Rescan();
		}

		return ByExtension.TryGetValue(extension, out var list) ? list : Array.Empty<string>();
	}
}
