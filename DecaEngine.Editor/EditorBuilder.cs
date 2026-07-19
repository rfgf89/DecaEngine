using System.Diagnostics;
using System.Reflection;
using System.Text;
using Microsoft.Build.Construction;

namespace DecaEngine.Editor;

public class EditorBuilder
{
	public void Build(string projectName, string outputPath)
	{
		ExecuteCommand($"dotnet new sln --name NewProject33 --format sln --output {outputPath}/{projectName}");
		ExecuteCommand($"dotnet new console --output {outputPath}/{projectName} --force");
		ExecuteCommand($"dotnet sln {outputPath}/{projectName}/{projectName}.sln add {outputPath}/{projectName}/{projectName}.csproj");

		var solutionFile = SolutionFile.Parse(FindSolutionFolder(Directory.GetCurrentDirectory()));
		var pathCsProjects = solutionFile.ProjectsInOrder.Select(p => p.AbsolutePath).Where(path => !path.Contains(".Editor")).ToList();
		StringBuilder addCsProjToSlnCommand = new StringBuilder($"dotnet sln {outputPath}/{projectName}/{projectName}.sln add");

		for (int i = 0; i < pathCsProjects.Count; i++)
		{
			addCsProjToSlnCommand.Append(" " + pathCsProjects[i]);
		}

		ExecuteCommand($"{addCsProjToSlnCommand} --in-root");
	}

	static string FindSolutionFolder(string startDirectory)
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