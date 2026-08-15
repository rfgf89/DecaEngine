using Diligent;
using Microsoft.Build.Locator;

namespace DecaEngine.Editor;

public static class EditorMain
{
	private static EditorManager EditorManager;

	private static void Main(string[] args)
	{
		// До ЛЮБОЙ ветки: нативные либы апскейлеров нужны и редактору, и CLI-пробам, а P/Invoke
		// грузит их лениво - на этот момент ничего ещё не загружено и копирование безопасно.
		NativeLibraryDeployer.Deploy();

		// DirectX Agility SDK - СТРОГО до создания D3D12-устройства (Diligent создаёт его при
		// инициализации графики). Требует режима разработчика Windows; при неудаче остаёмся на
		// встроенном рантайме - это штатно. DECA_AGILITY=0 - выключить.
		NativeLibraryDeployer.TryEnableAgilitySdk();

		if (args.Length > 0 && args[0] == "--preview-probe")
		{
			DiligentGraphicsApi.DebugMessage += (severity, message, function, file, line) =>
			{
				var text = severity.ToString();
				if (text.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
				    text.Contains("Fatal", StringComparison.OrdinalIgnoreCase))
				{
					Console.WriteLine($"[diligent-{severity}] {message}");
				}
			};
			PreviewProbe.Run(args);
			return;
		}

		if (args.Length > 0 && args[0] == "--preview-loop")
		{
			DiligentGraphicsApi.DebugMessage += (severity, message, function, file, line) =>
			{
				Console.WriteLine($"[diligent-{severity}] {message} ({function}: {file}, {line})");
			};
			PreviewLoopProbe.Run(args);
			return;
		}

		if (args.Length > 0 && args[0] == "--full-loop")
		{
			DiligentGraphicsApi.DebugMessage += (severity, message, function, file, line) =>
			{
				Console.WriteLine($"[diligent-{severity}] {message} ({function}: {file}, {line})");
			};
			FullLoopProbe.Run(args);
			return;
		}

		EditorConsoleLog.Install();

		DiligentGraphicsApi.DebugMessage += OnDiligentDebugMessage;

		if (!MSBuildLocator.IsRegistered)
		{
			MSBuildLocator.RegisterDefaults();
		}

		EditorManager = new EditorManager();
		EditorManager.Initialize();
	}

	private static void OnDiligentDebugMessage(DebugMessageSeverity severity, string message, string function, string file, int line)
	{
		var severityText = severity.ToString();
		var level = severityText.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
			severityText.Contains("Fatal", StringComparison.OrdinalIgnoreCase)
				? LogLevel.Error
				: severityText.Contains("Warn", StringComparison.OrdinalIgnoreCase)
					? LogLevel.Warning
					: LogLevel.Info;

		var formatted = string.IsNullOrEmpty(function)
			? message
			: $"{message} ({function}: {file}, {line})";

		Console.WriteLine(formatted);
		EditorConsoleLog.AddNative(level, formatted);
	}
}