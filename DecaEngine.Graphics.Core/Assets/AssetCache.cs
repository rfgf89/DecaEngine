using System.Security.Cryptography;
using System.Text;

namespace DecaEngine.Graphics.Assets;

/// <summary>Asset-pipeline disk cache: textures are content-addressed, cooked models are
/// source-addressed (path + mtime + size). Fully deletable - everything rebakes.</summary>
public sealed class AssetCache
{
	// Part of every key: incompatible files just miss and rebake, so no migrations are needed.
	private const int LayoutVersion = 1;

	/// <summary>Cache root of the open project; null disables the pipeline. DECA_ASSET_CACHE
	/// sets it at process start for CLI probes with no ProjectSession.</summary>
	public static string DefaultRoot { get; set; } = Environment.GetEnvironmentVariable("DECA_ASSET_CACHE");

	/// <summary>Sets DefaultRoot from the project directory; null/empty disables the cache.</summary>
	public static void SetProjectRoot(string projectDirectory) =>
		DefaultRoot = string.IsNullOrEmpty(projectDirectory)
			? null
			: Path.Combine(projectDirectory, "EditorCache", "Assets");

	public string Root { get; }

	private readonly string _textureDirectory;
	private readonly string _modelDirectory;

	public AssetCache(string root)
	{
		ArgumentException.ThrowIfNullOrEmpty(root);

		Root = root;
		_textureDirectory = Path.Combine(root, "textures");
		_modelDirectory = Path.Combine(root, "models");
	}

	/// <summary>Project cache: "&lt;project&gt;/EditorCache/Assets".</summary>
	public static AssetCache ForProject(string projectDirectory) =>
		new(Path.Combine(projectDirectory, "EditorCache", "Assets"));

	public string TexturePath(string key) => Path.Combine(_textureDirectory, key + DtexFile.Extension);

	public string ModelPath(string key) => Path.Combine(_modelDirectory, key + CookedModelFile.Extension);

	/// <summary>Creates the subfolders; call before writing (reads work without them).</summary>
	public void EnsureDirectories()
	{
		Directory.CreateDirectory(_textureDirectory);
		Directory.CreateDirectory(_modelDirectory);
	}

	/// <summary>Baked texture key: hashes the ENCODED file bytes, never the decoded pixels.</summary>
	public static string TextureKey(ReadOnlySpan<byte> encodedImage, TextureImportSettings settings)
	{
		var hash = SHA256.HashData(encodedImage);

		// Settings mixed in a second pass to avoid copying a possibly huge image buffer.
		return Combine(hash, $"tex-{LayoutVersion}-{TextureBaker.PipelineVersion}-{settings.CacheKey()}");
	}

	/// <summary>Cooked-model key: source path + mtime + size, plus the load-options signature
	/// (mesh optimization, LOD set and texture limits are baked into the output).</summary>
	public static string ModelKey(string modelPath, string optionsSignature)
	{
		long writeTicks = 0;
		long length = 0;

		try
		{
			var info = new FileInfo(modelPath);
			if (info.Exists)
			{
				writeTicks = info.LastWriteTimeUtc.Ticks;
				length = info.Length;
			}
		}
		catch (IOException)
		{
			// Unreadable file keys on path alone; worst case is a cache miss.
		}

		var payload = $"mdl-{LayoutVersion}-{TextureBaker.PipelineVersion}-{CookedModelFile.FormatVersion}\n" +
			$"{Path.GetFullPath(modelPath)}\n{writeTicks}\n{length}\n{optionsSignature}";

		return Combine(SHA256.HashData(Encoding.UTF8.GetBytes(payload)), string.Empty);
	}

	/// <summary>Texture key for a standalone image file, hashed streaming to avoid loading it.</summary>
	public static string TextureKeyFromFile(string imagePath, TextureImportSettings settings)
	{
		using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
		var hash = SHA256.HashData(stream);
		return Combine(hash, $"tex-{LayoutVersion}-{TextureBaker.PipelineVersion}-{settings.CacheKey()}");
	}

	// Truncated to 128 bits: full SHA-256 hex pushes paths into the Windows length limit.
	private static string Combine(byte[] contentHash, string settingsSuffix)
	{
		if (settingsSuffix.Length == 0)
		{
			return Convert.ToHexStringLower(contentHash.AsSpan(0, 16));
		}

		var combined = SHA256.HashData(
			[.. contentHash, .. Encoding.UTF8.GetBytes(settingsSuffix)]);

		return Convert.ToHexStringLower(combined.AsSpan(0, 16));
	}
}
