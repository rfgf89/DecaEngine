using DecaEngine.Core;
using DecaEngine.Core.Diagnostics;
using DecaEngine.Core.Build;
using DecaEngine.Graphics.Assets;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using DecaEngine.Animation;
using DecaEngine.Graphics;

namespace DecaEngine.Editor
{
	/// <summary>Owns the user project's lifecycle (build, load, play, stop) via <see cref="AssemblyApp"/>.</summary>
	public class ProjectSession
	{
		private AssemblyApp? _assemblyApp;

		private readonly IGraphicsApi _graphicsApi;
		private readonly IRenderHandle _renderHandle;
		private readonly EntityStore _ecsWorld;
		private readonly SystemRoot _root;

		private string? _projectSlnPath;
		private string? _projectCsprojPath;

		public AssemblyAppState State => _assemblyApp?.State ?? AssemblyAppState.NotLoaded;

		public string StatusMessage { get; private set; } = "?????? ?? ????????";

		public bool IsBusy { get; private set; }

		public string? ProjectSlnPath => _projectSlnPath;

		public string? ProjectDirectory => _projectCsprojPath is not null
			? Path.GetDirectoryName(_projectCsprojPath)
			: null;

		public string? AssetsPath => ProjectDirectory is not null
			? Path.Combine(ProjectDirectory, "Assets")
			: null;

		public string DisplayName => _projectSlnPath is not null
			? Path.GetFileNameWithoutExtension(_projectSlnPath)
			: "Project";

		public event Action? ProjectChanged;

		public ProjectSession(IGraphicsApi graphicsApi, IRenderHandle renderHandle, EntityStore ecsWorld, SystemRoot root)
		{
			_graphicsApi = graphicsApi;
			_renderHandle = renderHandle;
			_ecsWorld = ecsWorld;
			_root = root;
		}

		/// <summary>Build platform for the user project: must match this process's bitness (the assembly loads in-process), and DiligentEngine refuses to build as AnyCPU.</summary>
		public static string EditorPlatform =>
			System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
			{
				System.Runtime.InteropServices.Architecture.X86 => "x86",
				System.Runtime.InteropServices.Architecture.Arm64 => "ARM64",
				_ => "x64",
			};

		private readonly record struct LoadedPaths(string SlnPath, string CsprojPath, string DllPath);

		private Task<LoadedPaths>? _loadTask;

		/// <summary>Starts opening a project; the full build runs in the background and completes in <see cref="PollLoad"/> on the UI thread.</summary>
		public void BeginLoadProject(string slnPath)
		{
			if (_loadTask is not null)
			{
				return;
			}

			// Stop the previous app here on the UI thread: it touches editor state.
			if (_assemblyApp is not null && _assemblyApp.State != AssemblyAppState.NotLoaded)
			{
				_assemblyApp.Quit();
			}

			IsBusy = true;
			StatusMessage = "Opening project: building...";

			_loadTask = Task.Run(() => PrepareProject(slnPath));
		}

		/// <summary>Finishes a started load if ready; call every frame on the UI thread (editor windows depend on it). Returns true when a load completed.</summary>
		public bool PollLoad()
		{
			if (_loadTask is not { IsCompleted: true })
			{
				return false;
			}

			var task = _loadTask;
			_loadTask = null;

			try
			{
				if (task.IsFaulted)
				{
					var error = task.Exception?.GetBaseException();
					StatusMessage = $"Failed to open project: {error?.Message}";
					EngineLog.Add(LogLevel.Error, error?.ToString() ?? "Project load failed");
					return true;
				}

				var paths = task.Result;

				if (string.IsNullOrEmpty(paths.DllPath))
				{
					StatusMessage = "Failed to build project (see the Console window)";
					return true;
				}

				_assemblyApp = new AssemblyApp();
				_assemblyApp.ThreadStarted += threadId => EngineLog.SetProjectThreadId(threadId);
				_assemblyApp.ThreadStopped += () => EngineLog.SetProjectThreadId(null);
				_assemblyApp.LoadFromPath(paths.DllPath);

				_projectSlnPath = paths.SlnPath;
				_projectCsprojPath = paths.CsprojPath;

				// Asset cache root follows the opened project (see AssetCache.DefaultRoot).
				AssetCache.SetProjectRoot(ProjectDirectory);

				StatusMessage = "Project opened, ready to run";
			}
			catch (Exception ex)
			{
				StatusMessage = $"Failed to open project: {ex.Message}";
				EngineLog.Add(LogLevel.Error, ex.ToString());
			}
			finally
			{
				IsBusy = false;
				ProjectChanged?.Invoke();
			}

			return true;
		}

		// Background half of the load: files and processes only, no editor state.
		private static LoadedPaths PrepareProject(string slnPath)
		{
			var slnDir = Path.GetDirectoryName(slnPath)!;
			var slnName = Path.GetFileNameWithoutExtension(slnPath);

			var csprojPath = Path.Combine(slnDir, slnName, $"{slnName}.csproj");

			if (!File.Exists(csprojPath))
			{
				csprojPath = Path.Combine(slnDir, $"{slnName}.csproj");
				if (!File.Exists(csprojPath))
				{
					csprojPath = Directory.GetFiles(slnDir, "*.csproj", SearchOption.AllDirectories)
						.FirstOrDefault(p => !Path.GetFileNameWithoutExtension(p).EndsWith(".Assembly", StringComparison.OrdinalIgnoreCase))
						?? throw new FileNotFoundException("Game project .csproj not found next to the .sln", slnPath);
				}
			}

			var assemblyProjectName = $"{slnName}.Assembly";
			var assemblyCsprojPath = Path.Combine(slnDir, assemblyProjectName, $"{assemblyProjectName}.csproj");
			var runnableCsprojPath = File.Exists(assemblyCsprojPath) ? assemblyCsprojPath : csprojPath;

			EditorBuilder.AttachEngineReferences(csprojPath);
			if (runnableCsprojPath != csprojPath)
			{
				EditorBuilder.AttachEngineReferences(runnableCsprojPath);
			}

			// rebuild: a stale dll from an older engine loads fine, then crashes on first call in.
			var outputs = CsprojOutputResolver.GetBuildOutputs(runnableCsprojPath,
				buildIfMissing: true, platform: EditorPlatform, rebuild: true);
			var assemblyName = Path.GetFileNameWithoutExtension(runnableCsprojPath);
			var dllPath = outputs.FirstOrDefault(p =>
				string.Equals(Path.GetFileNameWithoutExtension(p), assemblyName, StringComparison.OrdinalIgnoreCase) &&
				Path.GetExtension(p).Equals(".dll", StringComparison.OrdinalIgnoreCase));

			return new LoadedPaths(slnPath, csprojPath, dllPath ?? string.Empty);
		}

		/// <summary>Synchronous load for non-frame-loop callers only; in the editor it would freeze rendering for the whole build.</summary>
		public void LoadProject(string slnPath)
		{
			BeginLoadProject(slnPath);

			_loadTask?.Wait();

			while (!PollLoad() && _loadTask is not null)
			{
			}
		}

		public void Play()
		{
			if (_assemblyApp is null)
			{
				return;
			}

			GameHostBridge.GraphicsApi = _graphicsApi;
			GameHostBridge.RenderHandle = _renderHandle;
			GameHostBridge.EntityStore = _ecsWorld;
			GameHostBridge.SystemRoot = _root;

			if (_assemblyApp.State == AssemblyAppState.Stopped)
			{
				_assemblyApp.Run();
				StatusMessage = "???????????";
			}
			else if (_assemblyApp.State == AssemblyAppState.Paused)
			{
				_assemblyApp.Play();
				StatusMessage = "???????????";
			}
		}

		public void Pause() => _assemblyApp?.Pause();

		public void Stop()
		{
			_assemblyApp?.Quit();
			GameHostBridge.Reset();
			StatusMessage = "?????? ??????????";

			if (_projectCsprojPath != null)
			{
				LoadProject(_projectSlnPath!);
			}
		}
	}
}
