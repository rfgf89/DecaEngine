using DecaEngine.Core.Diagnostics;
using DecaEngine.Editor;
using DecaEngine.Graphics.Diligent;
using DecaEngine.Probes;
using Diligent;
using Microsoft.Build.Locator;

namespace DecaEngine.App;

public static class EditorMain
{
	private static EditorManager EditorManager;

	private static void Main(string[] args)
	{
		// Before ANY branch: upscaler native libs are needed by both the editor and CLI probes,
		// and P/Invoke loads lazily, so copying is still safe here.
		NativeLibraryDeployer.Deploy();

		// DirectX Agility SDK must be enabled STRICTLY before the D3D12 device is created.
		// Requires Windows developer mode; falling back to the inbox runtime is normal.
		// DECA_AGILITY=0 disables.
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

		// Sample prefab generator: no graphics needed, only ECS and serialization.
		if (args.Length > 0 && args[0] == "--make-sample-prefab")
		{
			SamplePrefabBuilder.Run(args);
			return;
		}

		// Same, but with a wrapper project the editor can open.
		if (args.Length > 0 && args[0] == "--make-sample-project")
		{
			SamplePrefabBuilder.RunProject(args);
			return;
		}

		// Attach engine references to a csproj, same as project open (ProjectSession); a separate
		// command because idempotence is only observable on a second GUI-less run.
		if (args.Length > 1 && args[0] == "--attach-references")
		{
			if (!MSBuildLocator.IsRegistered)
			{
				MSBuildLocator.RegisterDefaults();
			}

			EditorBuilder.AttachEngineReferences(args[1]);
			Console.WriteLine($"[refs] processed {args[1]}");
			return;
		}

		// Resolve (and build if needed) project outputs - the other half of project open;
		// separate command so it can be reproduced without the GUI.
		if (args.Length > 1 && args[0] == "--resolve-outputs")
		{
			if (!MSBuildLocator.IsRegistered)
			{
				MSBuildLocator.RegisterDefaults();
			}

			var outputs = DecaEngine.Core.Build.CsprojOutputResolver.GetBuildOutputs(args[1],
				buildIfMissing: true, platform: ProjectSession.EditorPlatform, rebuild: true);
			Console.WriteLine($"[outputs] found {outputs.Count} files for {args[1]}");
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

		EngineLog.Install();

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
		EngineLog.AddNative(level, formatted);
	}
}