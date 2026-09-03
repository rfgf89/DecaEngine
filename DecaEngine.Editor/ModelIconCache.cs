using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DecaEngine.Core;
using DecaEngine.Core.Diagnostics;
using Hexa.NET.ImGui;
using StbImageSharp;
using DecaEngine.Graphics;

namespace DecaEngine.Editor;

/// <summary>Disk plus runtime cache of model preview icons for <see cref="AssetBrowserWindow"/>.</summary>
public class ModelIconCache
{
	/// <summary>Icon of the whole model, as opposed to a 0..N-1 sub-mesh icon.</summary>
	public const int WholeModelIndex = -1;

	// Bump whenever bake logic or sub-mesh naming changes; otherwise stale manifests stay "valid",
	// since the only other staleness check is the source model's write time.
	private const int BakeVersion = 3;

	public sealed class Manifest
	{
		public int BakeVersion { get; set; }
		public long SourceWriteTimeUtcTicks { get; set; }
		public List<string> SubMeshNames { get; set; } = new();
	}

	private sealed class RuntimeEntry
	{
		public Manifest? Manifest;
		public bool ManifestChecked;
		public readonly Dictionary<int, ImTextureRef> Icons = new();
		public readonly HashSet<int> FailedIcons = new();
		public readonly Dictionary<int, IGpuTexture> Textures = new();
	}

	private readonly IGraphicsApi _graphicsApi;
	private readonly ImGuiRender _imGuiRender;
	private readonly Dictionary<string, RuntimeEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

	public ModelIconCache(IGraphicsApi graphicsApi, ImGuiRender imGuiRender)
	{
		_graphicsApi = graphicsApi;
		_imGuiRender = imGuiRender;
	}

	public static string GetCacheDirectory(string projectDirectory) =>
		Path.Combine(projectDirectory, "EditorCache", "AssetPreviews");

	/// <summary>Icon manifest if present on disk and not stale; null otherwise. The result, including
	/// the negative one, is memoized until <see cref="Invalidate"/> to keep drawing code off the disk.</summary>
	public Manifest? GetManifest(string projectDirectory, string modelPath)
	{
		var entry = GetOrCreateEntry(modelPath);
		if (entry.ManifestChecked)
		{
			return entry.Manifest;
		}

		entry.ManifestChecked = true;
		entry.Manifest = null;

		try
		{
			var manifestPath = GetManifestPath(projectDirectory, modelPath);
			if (!File.Exists(manifestPath))
			{
				return null;
			}

			var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath));
			if (manifest is null)
			{
				return null;
			}

			if (manifest.BakeVersion != BakeVersion)
			{
				// Written by an older baker: stale regardless of the source file's write time.
				return null;
			}

			if (File.GetLastWriteTimeUtc(modelPath).Ticks != manifest.SourceWriteTimeUtcTicks)
			{
				return null;
			}

			entry.Manifest = manifest;
		}
		catch
		{
			// Corrupt JSON or a race with the writer: behave as if there were no cache.
		}

		return entry.Manifest;
	}

	/// <summary>ImGui texture of a cached icon, loaded from disk on first request.</summary>
	public bool TryGetIcon(string projectDirectory, string modelPath, int subMeshIndex, out ImTextureRef textureRef)
	{
		textureRef = default;

		var entry = GetOrCreateEntry(modelPath);
		if (entry.Icons.TryGetValue(subMeshIndex, out textureRef))
		{
			return true;
		}

		if (entry.FailedIcons.Contains(subMeshIndex))
		{
			return false;
		}

		// Only load icons behind a valid manifest: stale PNGs may still sit on disk.
		if (GetManifest(projectDirectory, modelPath) is null)
		{
			return false;
		}

		try
		{
			var iconPath = GetIconPath(projectDirectory, modelPath, subMeshIndex);
			if (!File.Exists(iconPath))
			{
				entry.FailedIcons.Add(subMeshIndex);
				return false;
			}

			var image = ImageResult.FromMemory(File.ReadAllBytes(iconPath), ColorComponents.RedGreenBlueAlpha);

			var texture = _graphicsApi.CreateTexture(new CpuTextureData
			{
				Name = $"AssetPreview {Path.GetFileName(iconPath)}",
				DecodedPixels = image.Data,
				DecodedWidth = image.Width,
				DecodedHeight = image.Height,
			});

			textureRef = _imGuiRender.GetNewTexture();
			_imGuiRender.BindRenderTarget(textureRef.GetTexID(), texture);

			entry.Textures[subMeshIndex] = texture;
			entry.Icons[subMeshIndex] = textureRef;
			return true;
		}
		catch (Exception ex)
		{
			entry.FailedIcons.Add(subMeshIndex);
			EngineLog.Add(LogLevel.Warning, $"Asset preview: failed to load cached icon for '{modelPath}': {ex.Message}");
			return false;
		}
	}

	/// <summary>Drops the runtime state of one icon, leaving the other icons of the model alone;
	/// for <see cref="WholeModelIndex"/> the cached manifest is dropped too.</summary>
	public void Invalidate(string modelPath, int subMeshIndex)
	{
		if (!_entries.TryGetValue(modelPath, out var entry))
		{
			return;
		}

		if (entry.Icons.Remove(subMeshIndex, out var textureRef))
		{
			_imGuiRender.ReleaseRenderTargetBinding(textureRef.GetTexID());
		}

		if (entry.Textures.Remove(subMeshIndex, out var texture))
		{
			texture.Release();
		}

		entry.FailedIcons.Remove(subMeshIndex);

		if (subMeshIndex == WholeModelIndex)
		{
			entry.ManifestChecked = false;
			entry.Manifest = null;
		}
	}

	public void SaveIcon(string projectDirectory, string modelPath, int subMeshIndex, byte[] rgba, int width, int height)
	{
		var iconPath = GetIconPath(projectDirectory, modelPath, subMeshIndex);
		Directory.CreateDirectory(Path.GetDirectoryName(iconPath)!);
		PngWriter.Write(iconPath, rgba, width, height);
	}

	public void SaveManifest(string projectDirectory, string modelPath, IReadOnlyList<string> subMeshNames)
	{
		var manifest = new Manifest
		{
			BakeVersion = BakeVersion,
			SourceWriteTimeUtcTicks = File.GetLastWriteTimeUtc(modelPath).Ticks,
			SubMeshNames = subMeshNames.ToList(),
		};

		var manifestPath = GetManifestPath(projectDirectory, modelPath);
		Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
		File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));
	}

	private RuntimeEntry GetOrCreateEntry(string modelPath)
	{
		if (!_entries.TryGetValue(modelPath, out var entry))
		{
			entry = new RuntimeEntry();
			_entries[modelPath] = entry;
		}

		return entry;
	}

	private static string GetIconPath(string projectDirectory, string modelPath, int subMeshIndex)
	{
		var suffix = subMeshIndex == WholeModelIndex ? string.Empty : $".sub{subMeshIndex}";
		return Path.Combine(GetCacheDirectory(projectDirectory), GetCacheKey(projectDirectory, modelPath) + suffix + ".png");
	}

	private static string GetManifestPath(string projectDirectory, string modelPath) =>
		Path.Combine(GetCacheDirectory(projectDirectory), GetCacheKey(projectDirectory, modelPath) + ".json");

	// BakeVersion is part of the file NAME, not just the manifest: TryGetIcon loads sub-mesh PNGs
	// by path without re-reading the manifest, so stale ones must stop resolving on a bump.
	private static string GetCacheKey(string projectDirectory, string modelPath)
	{
		string relative;
		try
		{
			relative = Path.GetRelativePath(projectDirectory, modelPath);
		}
		catch
		{
			relative = modelPath;
		}

		relative = relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/').ToLowerInvariant();

		var hashBytes = SHA1.HashData(Encoding.UTF8.GetBytes(relative));
		var hash = Convert.ToHexString(hashBytes.AsSpan(0, 5)).ToLowerInvariant();

		var safeName = Path.GetFileNameWithoutExtension(modelPath);
		foreach (var invalid in Path.GetInvalidFileNameChars())
		{
			safeName = safeName.Replace(invalid, '_');
		}

		return $"{safeName}_{hash}_v{BakeVersion}";
	}
}
