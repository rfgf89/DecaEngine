using System.Text.Json;

namespace DecaEngine.Editor;

public class RecentProjectEntry
{
	public string Name { get; set; } = "";
	public string SlnPath { get; set; } = "";
	public DateTime LastOpened { get; set; }
}

/// <summary>Хранит и персистит список недавно открытых проектов.</summary>
public class RecentProjectsManager
{
	private const int MaxEntries = 10;

	private static readonly string FilePath = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
		"DecaEngine",
		"recent_projects.json");

	public List<RecentProjectEntry> Entries { get; private set; } = new();

	public RecentProjectsManager()
	{
		Load();
	}

	public void Add(string slnPath)
	{
		var name = Path.GetFileNameWithoutExtension(slnPath);

		Entries.RemoveAll(e => string.Equals(e.SlnPath, slnPath, StringComparison.OrdinalIgnoreCase));
		Entries.Insert(0, new RecentProjectEntry { Name = name, SlnPath = slnPath, LastOpened = DateTime.Now });

		if (Entries.Count > MaxEntries)
		{
			Entries.RemoveRange(MaxEntries, Entries.Count - MaxEntries);
		}

		Save();
	}

	/// <summary>Выкидывает записи, чьего .sln больше нет на диске: удалённый проект в списке - это
	/// пункт меню, единственное действие которого - ошибка загрузки. Сохраняет файл, только если
	/// список реально изменился (вызывается при каждом открытии меню).</summary>
	public void Prune()
	{
		if (Entries.RemoveAll(e => !File.Exists(e.SlnPath)) > 0)
		{
			Save();
		}
	}

	private void Load()
	{
		try
		{
			if (!File.Exists(FilePath))
			{
				return;
			}

			var json = File.ReadAllText(FilePath);
			var entries = JsonSerializer.Deserialize<List<RecentProjectEntry>>(json);
			if (entries != null)
			{
				Entries = entries.OrderByDescending(e => e.LastOpened).ToList();
			}

			Prune();
		}
		catch
		{
			Entries = new List<RecentProjectEntry>();
		}
	}

	private void Save()
	{
		try
		{
			var directory = Path.GetDirectoryName(FilePath);
			if (!string.IsNullOrEmpty(directory))
			{
				Directory.CreateDirectory(directory);
			}

			var json = JsonSerializer.Serialize(Entries, new JsonSerializerOptions { WriteIndented = true });
			File.WriteAllText(FilePath, json);
		}
		catch
		{
			// Недоступный файл настроек - не повод ронять редактор.
		}
	}
}

