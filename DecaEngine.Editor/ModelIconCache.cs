using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DecaEngine.Core;
using DecaEngine.Graphics.Core;
using Hexa.NET.ImGui;
using StbImageSharp;

namespace DecaEngine.Editor;

/// <summary>
/// Дисковый + рантайм кеш превью-иконок моделей для <see cref="AssetBrowserWindow"/>.
/// На диске живёт в папке открытого проекта: "&lt;project&gt;/EditorCache/AssetPreviews" -
/// PNG-иконка целой модели, PNG на каждый SubMesh и JSON-манифест (имена сабмешей + время
/// изменения исходника, чтобы понимать, когда кеш протух). Рантайм-часть лениво поднимает
/// PNG в GPU-текстуры и биндит их к ImGui-текстурам (см. <see cref="TryGetIcon"/>), чтобы
/// сетка ассетов могла рисовать их через ImDrawList.AddImage.
///
/// Сам бейк иконок делает <see cref="ModelIconBaker"/> - он вызывает
/// <see cref="SaveIcon"/>/<see cref="SaveManifest"/> и затем <see cref="Invalidate"/>, чтобы
/// уже показанные фолбэк-состояния перечитались с диска.
/// </summary>
public class ModelIconCache
{
	/// <summary>Индекс "иконка целой модели" (в отличие от 0..N-1 - иконок сабмешей).</summary>
	public const int WholeModelIndex = -1;

	/// <summary>
	/// Бампается при изменении логики бейка/имён сабмешей (см. ModelLoader.PrepareModel,
	/// ModelIconBaker) - манифест на диске, записанный старой версией, иначе остаётся "валидным"
	/// сколько угодно (единственная проверка протухания - время изменения ИСХОДНОГО файла модели,
	/// см. GetManifest), так что баг вроде "у всех сабмешей одно и то же имя" пережил бы фикс кода
	/// и требовал бы вручную стирать EditorCache. Проверка версии заставляет такие манифесты
	/// перебейкаться автоматически при следующем обращении.
	/// </summary>
	/// v2: до фикса RenderResourceManager.DrawInstanceCount бейк сабмешей, шедший после бейка целой
	/// модели, обходил пустой префикс массива инстансов и писал на диск ПУСТЫЕ (цвета фона) PNG -
	/// они валидны по времени изменения исходника и без бампа версии остались бы навсегда.
	private const int BakeVersion = 2;

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

	/// <summary>
	/// Манифест иконок для модели, если он существует на диске и не протух (время изменения
	/// исходного файла совпадает с записанным при бейке). Иначе null - вызывающий может
	/// поставить модель в очередь на бейк (см. <see cref="ModelIconBaker.Enqueue"/>).
	/// Результат (в т.ч. отрицательный) кешируется в памяти до <see cref="Invalidate"/> -
	/// чтобы не делать файловые stat/чтения каждый кадр из кода отрисовки.
	/// </summary>
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
				// Записан старой версией бейкера/именования сабмешей - протух независимо от того,
				// менялся ли сам исходник модели на диске.
				return null;
			}

			if (File.GetLastWriteTimeUtc(modelPath).Ticks != manifest.SourceWriteTimeUtcTicks)
			{
				// Исходник изменился после бейка - иконки устарели.
				return null;
			}

			entry.Manifest = manifest;
		}
		catch
		{
			// Битый JSON, гонка с записью и т.п. - ведём себя как "кеша нет".
		}

		return entry.Manifest;
	}

	/// <summary>
	/// ImGui-текстура закешированной иконки (лениво загружается с диска при первом запросе).
	/// <paramref name="subMeshIndex"/>: <see cref="WholeModelIndex"/> - иконка целой модели,
	/// иначе индекс сабмеша. false - если иконки на диске нет (или не смогли прочитать).
	/// </summary>
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

		// Иконки грузим только если манифест валиден - иначе на диске могут лежать протухшие PNG.
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
			EditorConsoleLog.Add(LogLevel.Warning, $"Asset preview: failed to load cached icon for '{modelPath}': {ex.Message}");
			return false;
		}
	}

	/// <summary>
	/// Сбрасывает рантайм-состояние ОДНОЙ иконки (см. <paramref name="subMeshIndex"/>), освобождая
	/// её GPU-текстуру и ImGui-биндинг. Диск не трогает. Вызывается после перебейка этой иконки -
	/// остальные уже загруженные иконки той же модели (другие сабмеши) не трогаются, иначе каждый
	/// ленивый добейк одного сабмеша (см. ModelIconBaker.EnqueueSubMeshIcon) заставлял бы AssetBrowser
	/// заново поднимать с диска и перебиндить ВСЕ уже показанные иконки этой модели.
	///
	/// Для <see cref="WholeModelIndex"/> дополнительно сбрасывает закешированный манифест - он
	/// перезаписывается на диске тем же бейком (см. ModelIconBaker.BakeNextStage), так что рантайм-копию
	/// нужно перечитать, иначе <see cref="GetManifest"/> продолжит отдавать старые данные до перезапуска.
	/// </summary>
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

	/// <summary>
	/// Имя файлов кеша: читаемое имя модели + короткий хеш её пути относительно проекта + версия
	/// бейкера. BakeVersion - часть самого ИМЕНИ файла (а не только поля внутри Manifest), чтобы
	/// протухшие PNG отдельных сабмешей автоматически переставали находиться на диске при бампе
	/// версии - GetManifest уже отбраковывает старый Manifest.json по полю BakeVersion, но
	/// TryGetIcon грузит PNG по пути by-index НАПРЯМУЮ, не читая Manifest повторно, так что без
	/// версии в самом пути старые PNG (запечённые багнутым бейкером) продолжали бы находиться и
	/// показываться даже после того, как манифест уже перебейкался с фиксом.
	/// </summary>
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
