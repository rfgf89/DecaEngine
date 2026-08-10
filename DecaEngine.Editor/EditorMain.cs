using Diligent;
using Microsoft.Build.Locator;

namespace DecaEngine.Editor;

public static class EditorMain
{
	private static EditorManager EditorManager;

	private static void Main(string[] args)
	{
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