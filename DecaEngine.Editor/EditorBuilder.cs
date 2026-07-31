using System.Diagnostics;
using System.Text;
using Microsoft.Build.Construction;

namespace DecaEngine.Editor;

/// <summary>
/// Отвечает за создание нового пользовательского проекта (.sln + .csproj) и за
/// синхронизацию ссылок на модули движка (все .csproj из главного DecaEngine.sln,
/// кроме DecaEngine.Editor) с этим проектом.
/// </summary>
public class EditorBuilder
{
	/// <summary>
	/// Создаёт новый .sln с одним консольным .csproj по указанному пути,
	/// генерирует стартовый Program.cs и подключает к проекту ссылки на все
	/// модули движка (кроме DecaEngine.Editor).
	/// </summary>
	public string Build(string projectName, string outputPath)
	{
		var projectDir = Path.Combine(outputPath, projectName);
		var slnPath = Path.Combine(projectDir, $"{projectName}.sln");
		var csprojPath = Path.Combine(projectDir, $"{projectName}.csproj");

		ExecuteCommand($"dotnet new sln --name \"{projectName}\" --format sln --output \"{projectDir}\"");
		ExecuteCommand($"dotnet new console --output \"{projectDir}\" --force -f net10.0");
		ExecuteCommand($"dotnet sln \"{slnPath}\" add \"{csprojPath}\"");

		WriteProgramTemplate(projectDir, projectName);
		EnableProjectFeatures(csprojPath);

		// Добавляем остальные модули движка в новый .sln (чтобы всё можно было
		// открыть и собрать вместе в IDE), и сразу же подключаем реальные
		// ProjectReference, иначе типы движка не будут видны из кода проекта.
		AddEngineProjectsToSolution(slnPath, csprojPath);
		AttachEngineReferences(csprojPath);

		return slnPath;
	}

	/// <summary>
	/// Синхронизирует ProjectReference указанного .csproj со всеми модулями
	/// движка (все .csproj из главного DecaEngine.sln), кроме DecaEngine.Editor.
	/// Безопасно вызывать многократно (например, каждый раз при открытии
	/// проекта в редакторе), уже подключённые ссылки просто игнорируются.
	/// </summary>
	public static void AttachEngineReferences(string csprojPath)
	{
		foreach (var enginePath in GetEngineProjectPaths(csprojPath))
		{
			ExecuteCommand($"dotnet add \"{csprojPath}\" reference \"{enginePath}\"");
		}
	}

	private static void AddEngineProjectsToSolution(string slnPath, string csprojPath)
	{
		var enginePaths = GetEngineProjectPaths(csprojPath);
		if (enginePaths.Count == 0)
		{
			return;
		}

		var addCsProjToSlnCommand = new StringBuilder($"dotnet sln \"{slnPath}\" add");
		foreach (var enginePath in enginePaths)
		{
			addCsProjToSlnCommand.Append(" \"").Append(enginePath).Append('"');
		}

		ExecuteCommand($"{addCsProjToSlnCommand} --in-root");
	}

	/// <summary>Все .csproj из главного движкового .sln, кроме DecaEngine.Editor и самого проекта.</summary>
	private static List<string> GetEngineProjectPaths(string csprojPath)
	{
		var engineSlnPath = FindSolutionFolder(AppContext.BaseDirectory);
		if (engineSlnPath is null)
		{
			return new List<string>();
		}

		var fullCsprojPath = Path.GetFullPath(csprojPath);
		var solutionFile = SolutionFile.Parse(engineSlnPath);

		return solutionFile.ProjectsInOrder
			.Where(p => p.ProjectType == SolutionProjectType.KnownToBeMSBuildFormat)
			.Select(p => p.AbsolutePath)
			.Where(path => !path.Contains(".Editor", StringComparison.OrdinalIgnoreCase))
			.Where(path => !string.Equals(Path.GetFullPath(path), fullCsprojPath, StringComparison.OrdinalIgnoreCase))
			.ToList();
	}

	private static void WriteProgramTemplate(string projectDir, string projectName)
	{
		var rootNamespace = ToValidIdentifier(projectName);
		var programPath = Path.Combine(projectDir, "Program.cs");

		var content = $$"""
		                using DecaEngine.Core;

		                namespace {{rootNamespace}};

		                public class GameApplication : StateLoopCore
		                {
		                	protected override void Start(ref State state)
		                	{
		                		// TODO: инициализация игры (окно, графическое устройство, загрузка сцены и т.д.)
		                	}

		                	protected override void OnProcess(ref State state)
		                	{
		                		// TODO: логика обновления/рендера кадра
		                	}

		                	protected override void OnQuit(ref State state)
		                	{
		                		// TODO: освобождение ресурсов
		                	}
		                }

		                public static class Program
		                {
		                	private static readonly GameApplication _app = new();

		                	public static void Main(string[] args)
		                	{
		                		_app.Run();
		                	}

		                	public static void Play() => _app.Play();

		                	public static void Pause() => _app.Pause();

		                	public static void Quit() => _app.Quit();
		                }
		                """;

		File.WriteAllText(programPath, content);
	}

	private static void EnableProjectFeatures(string csprojPath)
	{
		var project = ProjectRootElement.Open(csprojPath);
		var propertyGroup = project.PropertyGroups.FirstOrDefault() ?? project.AddPropertyGroup();

		void SetIfMissing(string name, string value)
		{
			if (propertyGroup.Properties.All(p => p.Name != name))
			{
				propertyGroup.AddProperty(name, value);
			}
		}

		SetIfMissing("Nullable", "enable");
		SetIfMissing("ImplicitUsings", "enable");
		SetIfMissing("AllowUnsafeBlocks", "true");

		project.Save(csprojPath);
	}

	private static string ToValidIdentifier(string name)
	{
		var chars = name.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray();
		var result = new string(chars);

		if (string.IsNullOrEmpty(result) || char.IsDigit(result[0]))
		{
			result = "_" + result;
		}

		return result;
	}

	private static string FindSolutionFolder(string startDirectory)
	{
		DirectoryInfo dir = new DirectoryInfo(startDirectory);

		while (dir != null)
		{
			// Проверяем наличие файла *.sln
			var slnFiles = dir.GetFiles("*.sln");
			if (slnFiles.Length > 0)
			{
				return slnFiles[0].FullName;
			}

			dir = dir.Parent;
		}

		return null; // решение не найдено
	}

	public static void ExecuteCommand(string command)
	{
		int exitCode;
		ProcessStartInfo processInfo;
		Process process;
		Console.WriteLine(command);
		processInfo = new ProcessStartInfo("cmd.exe", "/c " + command);
		processInfo.CreateNoWindow = true;
		processInfo.UseShellExecute = false;
		// *** Redirect the output ***
		processInfo.RedirectStandardError = true;
		processInfo.RedirectStandardOutput = true;

		process = Process.Start(processInfo);

		// *** Read the streams asychronously to prevent deadlocks ***
		string output = "";
		string error = "";

		process.OutputDataReceived += (sender, e) => { if (e.Data != null) output += e.Data + Environment.NewLine; };
		process.ErrorDataReceived += (sender, e) => { if (e.Data != null) error += e.Data + Environment.NewLine; };

		process.BeginOutputReadLine();
		process.BeginErrorReadLine();

		process.WaitForExit();

		exitCode = process.ExitCode;

		Console.WriteLine("output>>" + (String.IsNullOrEmpty(output) ? "(none)" : output));
		Console.WriteLine("error>>" + (String.IsNullOrEmpty(error) ? "(none)" : error));
		Console.WriteLine("ExitCode: " + exitCode.ToString(), "ExecuteCommand");
		process.Close();
	}
}