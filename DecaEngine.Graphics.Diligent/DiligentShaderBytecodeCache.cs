using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DecaEngine.Core;
using Diligent;

namespace DecaEngine.Graphics.Diligent;

/// <summary>
/// Дисковый кеш БАЙТКОДА шейдеров (HLSL -> DXBC/DXIL/SPIR-V), по файлу на вариант. Существующий
/// <see cref="DiligentPsoManager"/> кеширует только драйверную часть PSO - компиляция исходника
/// компилятором (DXC/FXC) в него не входит и на больших вариантах UnlitInstancedPS стоит секунды
/// НА КАЖДЫЙ запуск редактора. Этот кеш закрывает именно её: попадание создаёт шейдер из готового
/// байткода, минуя компилятор совсем.
///
/// Ключ - SHA-256 от: версии формата, бэкенда, типа шейдера, точки входа, флагов компиляции,
/// макросов И СОДЕРЖИМОГО исходника со всеми транзитивными #include-ами. Правка любого инклуда
/// меняет ключ, так что бампать руками ничего не нужно (в отличие от версий ассет-кеша) - протухшие
/// записи просто перестают находиться и остаются мусором на диске.
///
/// Хеш дерева исходников мемоизируется на процесс по корневому файлу: варианты одного шейдера
/// (десятки) читают дерево с диска один раз. Следствие то же, что у процессного кеша шейдеров
/// (<see cref="DecaEngine.DiligentGraphicsApi"/>._shaderCache): правка .hlsl при живом редакторе
/// новых компиляций этого запуска не затронет - поведение не хуже прежнего.
///
/// Переменные окружения (зеркало DECA_PSO_CACHE):
///   DECA_SHADER_CACHE=0     - кеш выключен полностью;
///   DECA_SHADER_CACHE=clear - очистить директорию кеша при старте.
///
/// Потокобезопасен: и мемоизация, и файловый I/O зовутся из фоновой прекомпиляции
/// (см. ModelLoader.PrecompileShaderVariants) параллельно с компиляциями главного потока.
/// </summary>
public sealed class DiligentShaderBytecodeCache
{
	/// <summary>Бамп при изменении раскладки записи или состава ключа.</summary>
	private const int FormatVersion = 1;

	private readonly string _cacheDir;
	private readonly string _backendTag;

	private readonly object _lock = new();
	private readonly Dictionary<string, string> _sourceTreeHashByRoot = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Диагностика на процесс: попадания/промахи/отказы (ключ не посчитался - исходник
	/// не прочитался) - чтобы вопрос «работает ли кеш» решался счётчиком, а не гаданием.</summary>
	public static int DiagHits;
	public static int DiagMisses;
	public static int DiagStores;

	private DiligentShaderBytecodeCache(string cacheDir, string backendTag)
	{
		_cacheDir = cacheDir;
		_backendTag = backendTag;
	}

	/// <summary>null - кеш выключен (DECA_SHADER_CACHE=0). Ошибки создания директории глушатся:
	/// лучше без кеша, чем упавший запуск.</summary>
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

	/// <summary>Ключ кеша для варианта, null - исходник не прочитался (кеш для этой компиляции
	/// молча выключается, компилятор отработает как раньше и сам скажет, что не так).</summary>
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
			// Через временный файл: два процесса (редактор + CLI-проба) могут писать один ключ
			// одновременно, и обрезанная запись не должна стать «валидным» попаданием.
			var path = EntryPath(key);
			var tmp = path + "." + Environment.ProcessId + ".tmp";
			File.WriteAllBytes(tmp, bytecode);
			File.Move(tmp, path, overwrite: true);
			DiagStores++;
		}
		catch (Exception)
		{
			// Некритично: следующий запуск просто скомпилирует заново.
		}
	}

	/// <summary>Запись протухла или битая (создание из байткода не удалось) - убрать, чтобы не
	/// спотыкаться о неё каждым запуском.</summary>
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

	// #include "x" / #include <x> в начале строки (пробелы допустимы). Закомментированный инклуд
	// иногда попадёт в хеш лишним файлом - это лишь чуть строже нужного, ключ остаётся корректным.
	private static readonly Regex IncludeRegex = new("^\\s*#\\s*include\\s+[\"<]([^\">]+)[\">]",
		RegexOptions.Multiline | RegexOptions.Compiled);

	/// <summary>Хеш содержимого файла со всеми транзитивными #include-ами (DFS в порядке
	/// упоминания). Инклуды резолвятся относительно включающего файла, затем от корня фабрики -
	/// как у дефолтной стрим-фабрики Diligent. Неразрешившийся инклуд входит в хеш именем:
	/// компиляция такого исходника всё равно упадёт сама.</summary>
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
