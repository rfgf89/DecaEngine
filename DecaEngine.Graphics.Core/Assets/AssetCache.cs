using System.Security.Cryptography;
using System.Text;

namespace DecaEngine.Graphics.Assets;

/// <summary>
/// Раскладка дискового кеша ассет-пайплайна и правила вычисления ключей.
///
/// Кеш живёт в папке проекта - "&lt;project&gt;/EditorCache/Assets" (там же, где кеш иконок ассетов,
/// см. DecaEngine.Editor.ModelIconCache), и целиком удаляем: любой файл здесь выводим из исходников
/// и настроек импорта. Это не «данные», а материализованный результат чистой функции.
///
/// Текстуры адресуются ПО СОДЕРЖИМОМУ: ключ - хеш от байт исходной картинки плюс настройки импорта
/// плюс версия пайплайна. Отсюда два свойства, которых не даёт адресация по пути:
/// одна и та же картинка, встречающаяся в десятке моделей (типовой атлас, ORM-текстура), пекётся и
/// хранится РАЗ; а переименование или перемещение файла модели не обесценивает кеш её текстур.
///
/// Cooked-модели, наоборот, адресуются по исходнику (путь + время изменения + размер): их содержимое
/// зависит от опций загрузки (оптимизация меша, набор LOD-уровней), а не только от байт файла, и
/// хешировать сотни мегабайт .glb ради ключа было бы дороже, чем весь выигрыш.
/// </summary>
public sealed class AssetCache
{
	/// <summary>Бампается при изменении раскладки кеша или формата любого из контейнеров. Входит в
	/// каждый ключ, поэтому несовместимые файлы просто перестают находиться и перебейкиваются - без
	/// миграций и без «сотрите папку вручную».</summary>
	private const int LayoutVersion = 1;

	/// <summary>
	/// Корень кеша ОТКРЫТОГО проекта, который <see cref="ModelLoadOptions.CacheDirectory"/> берёт по
	/// умолчанию. Ставится один раз при открытии проекта (см. DecaEngine.Editor.ProjectSession) и
	/// снимается при закрытии; null - пайплайн выключен, и загрузка идёт как раньше.
	///
	/// Статика здесь, а не параметр конструктора опций, ровно потому, что редактор держит открытым
	/// РОВНО ОДИН проект, а опции загрузки собираются в десятке мест (превью ассетов, сцена, бейкер
	/// иконок, пробники). Протаскивать через все эти места один и тот же неизменный путь - значит
	/// гарантированно забыть его в одном из них и получить модель, которая необъяснимо грузится
	/// медленнее остальных.
	/// </summary>
	/// <remarks>DECA_ASSET_CACHE=&lt;путь&gt; задаёт корень на старте процесса - для CLI-пробников и
	/// прочих запусков без открытого проекта, где ProjectSession не выполняется вовсе.</remarks>
	public static string DefaultRoot { get; set; } = Environment.GetEnvironmentVariable("DECA_ASSET_CACHE");

	/// <summary>Ставит <see cref="DefaultRoot"/> по папке проекта. null/пусто - выключает кеш.</summary>
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

	/// <summary>Кеш проекта: "&lt;project&gt;/EditorCache/Assets".</summary>
	public static AssetCache ForProject(string projectDirectory) =>
		new(Path.Combine(projectDirectory, "EditorCache", "Assets"));

	public string TexturePath(string key) => Path.Combine(_textureDirectory, key + DtexFile.Extension);

	public string ModelPath(string key) => Path.Combine(_modelDirectory, key + CookedModelFile.Extension);

	/// <summary>Создаёт подпапки. Вызывается перед записью; чтение прекрасно живёт и без них.</summary>
	public void EnsureDirectories()
	{
		Directory.CreateDirectory(_textureDirectory);
		Directory.CreateDirectory(_modelDirectory);
	}

	/// <summary>
	/// Ключ запечённой текстуры: содержимое исходной картинки + настройки импорта. Хешируются именно
	/// СЖАТЫЕ байты файла (PNG/JPG), а не декодированные пиксели - декод стоит на порядок дороже
	/// хеширования, и в промахе кеша его всё равно придётся сделать, а в попадании не хочется делать
	/// вовсе.
	/// </summary>
	public static string TextureKey(ReadOnlySpan<byte> encodedImage, TextureImportSettings settings)
	{
		var hash = SHA256.HashData(encodedImage);

		// Настройки подмешиваем отдельным проходом, а не конкатенацией буферов: картинка бывает в
		// сотни мегабайт, и копировать её ради приписывания двух десятков байт незачем.
		return Combine(hash, $"tex-{LayoutVersion}-{TextureBaker.PipelineVersion}-{settings.CacheKey()}");
	}

	/// <summary>
	/// Ключ cooked-модели: исходный файл (путь + время изменения + размер) плюс подпись опций
	/// загрузки. Подпись обязательна - в cooked-данные запечены и оптимизация меша, и набор
	/// LOD-уровней, и предел размера текстур, то есть одна и та же модель с разными опциями даёт
	/// РАЗНОЕ содержимое (см. ModelLoadOptions.Signature).
	/// </summary>
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
			// Недоступный файл даст ключ по одному пути. Промах кеша - худшее, что может случиться.
		}

		var payload = $"mdl-{LayoutVersion}-{TextureBaker.PipelineVersion}-{CookedModelFile.FormatVersion}\n" +
			$"{Path.GetFullPath(modelPath)}\n{writeTicks}\n{length}\n{optionsSignature}";

		return Combine(SHA256.HashData(Encoding.UTF8.GetBytes(payload)), string.Empty);
	}

	/// <summary>Ключ для картинки, лежащей отдельным файлом: хеш её содержимого. Отдельный путь от
	/// <see cref="TextureKey(ReadOnlySpan{byte}, TextureImportSettings)"/> нужен, чтобы не тянуть
	/// в память 4K-PNG целиком ради одного лишь ключа - файл хешируется потоково.</summary>
	public static string TextureKeyFromFile(string imagePath, TextureImportSettings settings)
	{
		using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
		var hash = SHA256.HashData(stream);
		return Combine(hash, $"tex-{LayoutVersion}-{TextureBaker.PipelineVersion}-{settings.CacheKey()}");
	}

	/// <summary>Складывает хеш содержимого и текстовый суффикс настроек в одно шестнадцатеричное
	/// имя файла. 32 hex-символа (128 бит) - с запасом против коллизий на любом мыслимом проекте и
	/// заметно короче полного SHA-256 в путях, которые на Windows и так упираются в лимит длины.</summary>
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
