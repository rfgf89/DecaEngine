using Diligent;
using Microsoft.Build.Locator;

namespace DecaEngine.Editor;

public static class EditorMain
{
	private static EditorManager EditorManager;

	private static void Main(string[] args)
	{
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

		EditorConsoleLog.AddNative(level, formatted);
	}
}