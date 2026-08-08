using System.Text.Json;

namespace DecaEngine.Editor;

public class RecentProjectEntry
{
	public string Name { get; set; } = "";
	public string SlnPath { get; set; } = "";
	public DateTime LastOpened { get; set; }
}

/// <summary>?????? ? ???????????? ?????? ??????? ???????? ????????.</summary>
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
			// ?????????? ?????? ?????????? ? ??? ?? ????????? ??????????.
		}
	}
}

