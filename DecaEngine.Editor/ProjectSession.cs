using DecaEngine.Core;
using DecaEngine.Core.Build;
using DecaEngine.Graphics.Assets;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;

namespace DecaEngine.Editor
{
	/// <summary>
	/// ??????? ????????? ?????? ??????????? ?????? ??????? (??????, ??????, ?????, ?????????)
	/// ????? <see cref="AssemblyApp"/>. ??? ?? ???? ? ??? ????? ?????? ????????? ????????
	/// ???????????? ???????, ???????????? ??????????? ?????? ????????? (<see cref="ProjectWindow"/>,
	/// <see cref="GameViewWindow"/>, <see cref="MenuBarWindow"/>, <see cref="AssetBrowserWindow"/>).
	/// ????? ???? ???? ?????????? ??? ?????????? ????? ? ProjectWindow, ??-?? ???? ????
	/// "Project" ???????? ???????????? ? ?? ????????/?????? ???????, ? ?? ??? ???????????,
	/// ???? ?? ?????? ???? ??????? ?????? ???????? ?????? ?? ???????????? ???????.
	/// </summary>
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

		/// <summary>???????, ?????????? .csproj ???????????? ??????? (????????, .../MyGame).</summary>
		public string? ProjectDirectory => _projectCsprojPath is not null
			? Path.GetDirectoryName(_projectCsprojPath)
			: null;

		/// <summary>???? ? ????? "Assets" ?????? ???????? ???????????? ???????.</summary>
		public string? AssetsPath => ProjectDirectory is not null
			? Path.Combine(ProjectDirectory, "Assets")
			: null;

		/// <summary>???????????? ??? ???????????? ??????? ??? ??????? ???????????? (????????, GameView).</summary>
		public string DisplayName => _projectSlnPath is not null
			? Path.GetFileNameWithoutExtension(_projectSlnPath)
			: "Project";

		/// <summary>?????????? ????? ????????/?????????? ??????? ???????? ?????? ???????.</summary>
		public event Action? ProjectChanged;

		public ProjectSession(IGraphicsApi graphicsApi, IRenderHandle renderHandle, EntityStore ecsWorld, SystemRoot root)
		{
			_graphicsApi = graphicsApi;
			_renderHandle = renderHandle;
			_ecsWorld = ecsWorld;
			_root = root;
		}

		/// <summary>????????? ?????? ?? ?????????? ???? ? .sln (?????????? ?????, ???????? ?? MenuBarWindow).</summary>
		/// <summary>
		/// Платформа, под которую собирается проект пользователя. Не настройка, а СЛЕДСТВИЕ: сборка
		/// проекта грузится В ЭТОТ ЖЕ процесс, значит обязана совпадать с ним по разрядности. Плюс
		/// движок тянет DiligentEngine, который на AnyCPU отказывается собираться вовсе - без явной
		/// платформы открытие проекта заканчивалось сообщением «не удалось собрать проект».
		/// </summary>
		public static string EditorPlatform =>
			System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
			{
				System.Runtime.InteropServices.Architecture.X86 => "x86",
				System.Runtime.InteropServices.Architecture.Arm64 => "ARM64",
				_ => "x64",
			};

		/// <summary>Что подготовка нашла: пути проекта и собранная сборка, которую остаётся
		/// загрузить.</summary>
		private readonly record struct LoadedPaths(string SlnPath, string CsprojPath, string DllPath);

		private Task<LoadedPaths>? _loadTask;

		/// <summary>
		/// Начинает открытие проекта. Тяжёлая часть - разбор csproj и ПОЛНАЯ СБОРКА проекта (он
		/// тянет за собой два десятка проектов движка, на холодную это минуты) - уходит в фон;
		/// завершается открытие в <see cref="PollLoad"/>, в потоке UI.
		///
		/// Раньше всё это шло синхронно из обработчика меню: редактор переставал рисовать кадры на
		/// всё время сборки, и открытие проекта было неотличимо от зависания.
		/// </summary>
		public void BeginLoadProject(string slnPath)
		{
			if (_loadTask is not null)
			{
				return;
			}

			// Остановка прежнего приложения - здесь, в UI-потоке: она трогает состояние редактора.
			if (_assemblyApp is not null && _assemblyApp.State != AssemblyAppState.NotLoaded)
			{
				_assemblyApp.Quit();
			}

			IsBusy = true;
			StatusMessage = "Открытие проекта: сборка...";

			_loadTask = Task.Run(() => PrepareProject(slnPath));
		}

		/// <summary>
		/// Доводит начатое открытие до конца, если подготовка закончилась. Звать каждый кадр.
		/// Возвращает true, если в этот раз открытие завершилось (успешно или нет).
		///
		/// Загрузка сборки, публикация путей и событие <see cref="ProjectChanged"/> живут ЗДЕСЬ, а не
		/// в фоне: на них завязаны окна редактора, и трогать их из чужого потока нельзя.
		/// </summary>
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
					StatusMessage = $"Ошибка открытия проекта: {error?.Message}";
					EditorConsoleLog.Add(LogLevel.Error, error?.ToString() ?? "Project load failed");
					return true;
				}

				var paths = task.Result;

				if (string.IsNullOrEmpty(paths.DllPath))
				{
					StatusMessage = "Не удалось собрать проект (см. окно Console)";
					return true;
				}

				_assemblyApp = new AssemblyApp();
				_assemblyApp.ThreadStarted += threadId => EditorConsoleLog.SetProjectThreadId(threadId);
				_assemblyApp.ThreadStopped += () => EditorConsoleLog.SetProjectThreadId(null);
				_assemblyApp.LoadFromPath(paths.DllPath);

				_projectSlnPath = paths.SlnPath;
				_projectCsprojPath = paths.CsprojPath;

				// Asset pipeline cache lives inside the opened project (see AssetCache.DefaultRoot):
				// every ModelLoadOptions built from here on picks it up by default, so baked textures
				// and cooked models follow the project rather than the editor install.
				AssetCache.SetProjectRoot(ProjectDirectory);

				StatusMessage = "Проект открыт, готов к запуску";
			}
			catch (Exception ex)
			{
				StatusMessage = $"Ошибка открытия проекта: {ex.Message}";
				EditorConsoleLog.Add(LogLevel.Error, ex.ToString());
			}
			finally
			{
				IsBusy = false;
				ProjectChanged?.Invoke();
			}

			return true;
		}

		/// <summary>Фоновая часть открытия: только файлы и процессы, ничего от редактора. Именно
		/// поэтому её можно унести с UI-потока целиком.</summary>
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
						?? throw new FileNotFoundException("Не найден .csproj игрового проекта рядом с .sln", slnPath);
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

			var outputs = CsprojOutputResolver.GetBuildOutputs(runnableCsprojPath,
				buildIfMissing: true, platform: EditorPlatform);
			var assemblyName = Path.GetFileNameWithoutExtension(runnableCsprojPath);
			var dllPath = outputs.FirstOrDefault(p =>
				string.Equals(Path.GetFileNameWithoutExtension(p), assemblyName, StringComparison.OrdinalIgnoreCase) &&
				Path.GetExtension(p).Equals(".dll", StringComparison.OrdinalIgnoreCase));

			return new LoadedPaths(slnPath, csprojPath, dllPath ?? string.Empty);
		}

		/// <summary>Синхронное открытие - для кода без кадрового цикла (командная строка, проверки).
		/// В редакторе пользоваться им НЕЛЬЗЯ: он снова заморозит кадры на всё время сборки.</summary>
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

