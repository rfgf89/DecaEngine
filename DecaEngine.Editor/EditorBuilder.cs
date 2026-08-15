using System.Diagnostics;
using System.Text;
using Microsoft.Build.Construction;

namespace DecaEngine.Editor;

public class EditorBuilder
{
	public string Build(string projectName, string outputPath)
	{
		var projectDir = Path.Combine(outputPath, projectName);
		var slnPath = Path.Combine(projectDir, $"{projectName}.sln");

		var gameProjectDir = Path.Combine(projectDir, projectName);
		var csprojPath = Path.Combine(gameProjectDir, $"{projectName}.csproj");

		var assemblyProjectName = $"{projectName}.Assembly";
		var assemblyProjectDir = Path.Combine(projectDir, assemblyProjectName);
		var assemblyCsprojPath = Path.Combine(assemblyProjectDir, $"{assemblyProjectName}.csproj");

		ExecuteCommand($"dotnet new sln --name \"{projectName}\" --format sln --output \"{projectDir}\"");

		ExecuteCommand($"dotnet new classlib --output \"{gameProjectDir}\" --force -f net10.0");
		ExecuteCommand($"dotnet sln \"{slnPath}\" add \"{csprojPath}\"");

		var defaultClassFile = Path.Combine(gameProjectDir, "Class1.cs");
		if (File.Exists(defaultClassFile))
		{
			File.Delete(defaultClassFile);
		}

		ExecuteCommand($"dotnet new console --output \"{assemblyProjectDir}\" --force -f net10.0");
		ExecuteCommand($"dotnet sln \"{slnPath}\" add \"{assemblyCsprojPath}\"");

		WriteGameProjectTemplate(gameProjectDir, projectName);
		WriteAssemblyProjectTemplate(assemblyProjectDir, projectName);

		EnableProjectFeatures(csprojPath);
		EnableProjectFeatures(assemblyCsprojPath);

		ExecuteCommand($"dotnet add \"{assemblyCsprojPath}\" reference \"{csprojPath}\"");

		AddEngineProjectsToSolution(slnPath, csprojPath);
		AttachEngineReferences(csprojPath);
		AttachEngineReferences(assemblyCsprojPath);

		return slnPath;
	}

	public static void AttachEngineReferences(string csprojPath)
	{
		var missingPaths = GetEngineProjectPaths(csprojPath)
			.Select(Path.GetFullPath)
			.Except(GetExistingReferencePaths(csprojPath), StringComparer.OrdinalIgnoreCase)
			.ToList();

		if (missingPaths.Count == 0)
		{
			return;
		}

		foreach (var enginePath in missingPaths)
		{
			ExecuteCommand($"dotnet add \"{csprojPath}\" reference \"{enginePath}\"");
		}
	}

	private static HashSet<string> GetExistingReferencePaths(string csprojPath)
	{
		var projectDir = Path.GetDirectoryName(Path.GetFullPath(csprojPath))!;
		var project = ProjectRootElement.Open(csprojPath);

		var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var item in project.Items.Where(i => i.ItemType == "ProjectReference"))
		{
			var referencePath = item.Include;
			if (string.IsNullOrWhiteSpace(referencePath))
			{
				continue;
			}

			var fullReferencePath = Path.IsPathRooted(referencePath)
				? referencePath
				: Path.Combine(projectDir, referencePath);

			result.Add(Path.GetFullPath(fullReferencePath));
		}

		return result;
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

	private static void WriteGameProjectTemplate(string projectDir, string projectName)
	{
		var rootNamespace = ToValidIdentifier(projectName);
		var programPath = Path.Combine(projectDir, "GameApplication.cs");

		var content = $$"""
		                using System.Numerics;
		                using DecaEngine;
		                using DecaEngine.Core;
		                using DecaEngine.Graphics.Core;

		                namespace {{rootNamespace}};

		                public class GameApplication : GameBehaviour
		                {
		                	private float _time;

		                	protected override void OnInitialize()
		                	{
		                		_time = 0f;
		                	}

		                	protected override void OnUpdate(float deltaTime)
		                	{
		                		_time += deltaTime;

		                		var hue = (_time * 0.15f) % 1.0f;
		                		var color = HsvToRgb(hue, 0.65f, 0.9f);

		                		lock (GameHostBridge.GpuSync)
		                		{
		                			var cmd = Context.GraphicsApi.CreateCommandBuffer();
		                			cmd.BeginRecording();

		                			if (Context.RenderHandle is IGpuTexture handleTexture)
		                			{
		                				cmd.ClearRenderTarget(handleTexture, new Vector4(color, 1.0f));
		                			}
		                			else
		                			{
		                				cmd.SetBackBufferTarget(Context.GraphicsApi);
		                				cmd.ClearBackBufferTarget(Context.GraphicsApi, new Vector4(color, 1.0f));
		                			}

		                			cmd.EndRecording();
		                			cmd.Execute();
		                		}
		                	}

		                	protected override void OnShutdown()
		                	{
		                	}

		                	private static Vector3 HsvToRgb(float h, float s, float v)
		                	{
		                		int i = (int)(h * 6.0f);
		                		float f = h * 6.0f - i;
		                		float p = v * (1.0f - s);
		                		float q = v * (1.0f - f * s);
		                		float t = v * (1.0f - (1.0f - f) * s);

		                		return (i % 6) switch
		                		{
		                			0 => new Vector3(v, t, p),
		                			1 => new Vector3(q, v, p),
		                			2 => new Vector3(p, v, t),
		                			3 => new Vector3(p, q, v),
		                			4 => new Vector3(t, p, v),
		                			_ => new Vector3(v, p, q),
		                		};
		                	}
		                }
		                """;

		File.WriteAllText(programPath, content);
	}

	private static void WriteAssemblyProjectTemplate(string assemblyProjectDir, string projectName)
	{
		var rootNamespace = ToValidIdentifier(projectName);
		var programPath = Path.Combine(assemblyProjectDir, "Program.cs");

		var content = $$"""
		                using System.Numerics;
		                using DecaEngine;
		                using DecaEngine.Core;
		                using DecaEngine.Sdl;
		                using Friflo.Engine.ECS;
		                using {{rootNamespace}};

		                namespace {{rootNamespace}}.Assembly;

		                internal sealed class GameHost : TimeLoopCore
		                {
		                	private readonly GameApplication _game = new();
		                	private IWindowHandle? _ownedWindowHandle;
		                	private IGraphicsApi? _ownedGraphicsApi;
		                	private SdlDevicePull? _ownedDevicePull;
		                	private IInputEventPull? _ownedInputEventPull;
		                	private bool _ownsGraphicsApi;

		                	protected override void OnStart()
		                	{
		                		IGraphicsApi graphicsApi;
		                		IRenderHandle? renderHandle;
		                		EntityStore entityStore;

		                		if (GameHostBridge.IsHosted)
		                		{
		                			graphicsApi = GameHostBridge.GraphicsApi!;
		                			renderHandle = GameHostBridge.RenderHandle;
		                			entityStore = GameHostBridge.EntityStore ?? new EntityStore();
		                			_ownsGraphicsApi = false;
		                		}
		                		else
		                		{
		                			_ownedWindowHandle = new SdlWindowHandle();
		                			_ownedWindowHandle.Initialize("{{projectName}}", 0, new Vector2(1280, 720));

		                			_ownedGraphicsApi = new DiligentGraphicsApi(_ownedWindowHandle);
		                			_ownedGraphicsApi.Initialize(GraphicsBackend.Vulkan);

		                			_ownedDevicePull = new SdlDevicePull();
		                			_ownedInputEventPull = new SdlEventPull(_ownedWindowHandle, _ownedDevicePull);

		                			graphicsApi = _ownedGraphicsApi;
		                			renderHandle = null;
		                			entityStore = new EntityStore();
		                			_ownsGraphicsApi = true;
		                		}

		                		_game.InternalInitialize(new GameContext(graphicsApi, renderHandle, entityStore, GameHostBridge.SystemRoot, GameHostBridge.IsHosted));

		                		GameHostBridge.NotifyGameReady();
		                	}

		                	protected override void OnUpdate(float deltaTime)
		                	{
		                		if (_ownedInputEventPull != null && _ownedInputEventPull.PullEvent())
		                		{
		                			Quit();
		                			return;
		                		}

		                		_game.InternalUpdate(deltaTime);

		                		if (_ownsGraphicsApi)
		                		{
		                			_ownedGraphicsApi!.Present();
		                		}
		                	}

		                	protected override void OnFixedUpdate(float fixedDeltaTime)
		                	{
		                		_game.InternalFixedUpdate(fixedDeltaTime);
		                	}

		                	protected override void OnQuit()
		                	{
		                		_game.InternalShutdown();

		                		_ownedGraphicsApi?.Release();
		                		_ownedWindowHandle?.Release();
		                	}
		                }

		                public static class Program
		                {
		                	private static readonly GameHost _host = new();

		                	public static void Main(string[] args) => _host.Run();

		                	public static void Play() => _host.Play();

		                	public static void Pause() => _host.Pause();

		                	public static void Quit() => _host.Quit();
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
			var slnFiles = dir.GetFiles("*.sln");
			if (slnFiles.Length > 0)
			{
				return slnFiles[0].FullName;
			}

			dir = dir.Parent;
		}

		return null;
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
		processInfo.RedirectStandardError = true;
		processInfo.RedirectStandardOutput = true;

		process = Process.Start(processInfo);

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