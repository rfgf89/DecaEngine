using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DecaEngine.Core;
using Diligent;

namespace DecaEngine.Graphics.Diligent;

/// <summary>Disk cache of compiled shader bytecode (HLSL -> DXBC/DXIL/SPIR-V), one file per variant.
/// Key is SHA-256 over format version, backend, shader type, entry point, flags, macros and the
/// source text with all transitive #includes, so no manual version bump is needed. The source tree
/// hash is memoized per process: editing a .hlsl mid-session does not affect this run's compiles.
/// DECA_SHADER_CACHE=0 disables it, =clear wipes the directory at startup. Thread-safe.</summary>
public sealed class DiligentShaderBytecodeCache
{
	// Bump when the entry layout or the key composition changes.
	private const int FormatVersion = 1;

	private readonly string _cacheDir;
	private readonly string _backendTag;

	private readonly object _lock = new();
	private readonly Dictionary<string, string> _sourceTreeHashByRoot = new(StringComparer.OrdinalIgnoreCase);

	public static int DiagHits;
	public static int DiagMisses;
	public static int DiagStores;

	private DiligentShaderBytecodeCache(string cacheDir, string backendTag)
	{
		_cacheDir = cacheDir;
		_backendTag = backendTag;
	}

	/// <summary>Returns null when the cache is disabled or its directory cannot be created.</summary>
	public static DiligentShaderBytecodeCache? Create(string cacheDir, string backendTag)
	{
		var mode = Environment.GetEnvironmentVariable("DECA_SHADER_CACHE");
		if (mode == "0")
		{
			return null;
		}

		try
		{
			if (string.Equals(mode, "clear", StringComparison.OrdinalIgnoreCase) && Directory.Exists(cacheDir))
			{
				Directory.Delete(cacheDir, recursive: true);
			}

			Directory.CreateDirectory(cacheDir);
		}
		catch (Exception)
		{
			return null;
		}

		return new DiligentShaderBytecodeCache(cacheDir, backendTag);
	}

	/// <summary>Cache key for a variant; null when the source could not be read.</summary>
	public string? ComputeKey(string factoryRoot, string filePath, ShaderObjectType type, string entryPoint,
		ShaderMacro[] macros, ShaderCompileFlags compileFlags)
	{
		var rootPath = Path.GetFullPath(Path.Combine(factoryRoot, filePath));

		string treeHash;
		try
		{
			treeHash = GetSourceTreeHash(rootPath, factoryRoot);
		}
		catch (Exception)
		{
			return null;
		}

		var sb = new StringBuilder(256);
		sb.Append(FormatVersion).Append('|').Append(_backendTag).Append('|').Append((int)type).Append('|')
			.Append(entryPoint).Append('|').Append((int)compileFlags).Append('|');
		foreach (var macro in macros)
		{
			sb.Append(macro.Name).Append('=').Append(macro.Definition).Append(';');
		}
		sb.Append('|').Append(treeHash);

		var keyBytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
		return Convert.ToHexString(keyBytes);
	}

	public byte[]? TryLoad(string key)
	{
		try
		{
			var path = EntryPath(key);
			if (!File.Exists(path))
			{
				DiagMisses++;
				return null;
			}

			var bytes = File.ReadAllBytes(path);
			if (bytes.Length == 0)
			{
				DiagMisses++;
				return null;
			}

			DiagHits++;
			return bytes;
		}
		catch (Exception)
		{
			DiagMisses++;
			return null;
		}
	}

	public void Store(string key, byte[] bytecode)
	{
		if (bytecode.Length == 0)
		{
			return;
		}

		try
		{
			// Temp file + move: two processes can write the same key, a truncated entry must not hit.
			var path = EntryPath(key);
			var tmp = path + "." + Environment.ProcessId + ".tmp";
			File.WriteAllBytes(tmp, bytecode);
			File.Move(tmp, path, overwrite: true);
			DiagStores++;
		}
		catch (Exception)
		{
		}
	}

	/// <summary>Drops a stale or corrupt entry so the next run recompiles instead of retrying it.</summary>
	public void Invalidate(string key)
	{
		try
		{
			File.Delete(EntryPath(key));
		}
		catch (Exception)
		{
		}
	}

	private string EntryPath(string key) => Path.Combine(_cacheDir, key + ".shbc");

	// Commented-out includes also match; that only makes the key stricter, never wrong.
	private static readonly Regex IncludeRegex = new("^\\s*#\\s*include\\s+[\"<]([^\">]+)[\">]",
		RegexOptions.Multiline | RegexOptions.Compiled);

	// Includes resolve relative to the including file, then to the factory root, as Diligent does.
	private string GetSourceTreeHash(string rootPath, string factoryRoot)
	{
		lock (_lock)
		{
			if (_sourceTreeHashByRoot.TryGetValue(rootPath, out var cached))
			{
				return cached;
			}
		}

		using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		AppendFile(sha, rootPath, factoryRoot, visited);
		var hash = Convert.ToHexString(sha.GetHashAndReset());

		lock (_lock)
		{
			_sourceTreeHashByRoot[rootPath] = hash;
		}

		return hash;
	}

	private static void AppendFile(IncrementalHash sha, string path, string factoryRoot, HashSet<string> visited)
	{
		if (!visited.Add(path))
		{
			return;
		}

		var content = File.ReadAllBytes(path);
		sha.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(path)));
		sha.AppendData(content);

		var text = Encoding.UTF8.GetString(content);
		foreach (Match match in IncludeRegex.Matches(text))
		{
			var include = match.Groups[1].Value;
			var candidate = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, include));
			if (!File.Exists(candidate))
			{
				candidate = Path.GetFullPath(Path.Combine(factoryRoot, include));
			}

			if (File.Exists(candidate))
			{
				AppendFile(sha, candidate, factoryRoot, visited);
			}
			else
			{
				sha.AppendData(Encoding.UTF8.GetBytes("missing:" + include));
			}
		}
	}
}
