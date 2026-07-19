using System.Reflection;
using System.Runtime;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DecaEngine.Core;

public class DecaAssemblyLoadContext
{
	private SyntaxTree _syntaxTree;
	private Thread _appMainThread;

	public DecaAssemblyLoadContext(string pluginPath)
	{
	}

	private List<PortableExecutableReference> GetListExecutableReferences(string code)
	{
		var basePath = Path.GetDirectoryName(typeof(GCSettings).GetTypeInfo().Assembly.Location);

		_syntaxTree = CSharpSyntaxTree.ParseText(code, new CSharpParseOptions(LanguageVersion.CSharp10));
		var root = _syntaxTree.GetRoot() as CompilationUnitSyntax;
		var referencePaths = GetDllPaths(basePath);

		referencePaths.AddRange(root.Usings.Select(x => Path.Combine(basePath, $"{x.Name}.dll")));

		var executableReferences = new List<PortableExecutableReference>();

		foreach (var reference in referencePaths)
		{
			if (File.Exists(reference))
			{
				executableReferences.Add(MetadataReference.CreateFromFile(reference));
			}
		}

		return executableReferences;
	}

	private List<string> GetDllPaths(string basePath)
	{
		return
		[
			typeof(object).GetTypeInfo().Assembly.Location,
			typeof(Console).GetTypeInfo().Assembly.Location,
			Path.Combine(basePath, "System.Runtime.dll"),
			Path.Combine(basePath, "System.Runtime.Extensions.dll"),
			Path.Combine(basePath, "mscorlib.dll")
		];
	}

	public void Load()
	{
		var code = $$"""
		             using System;
		             using DecaEngine.Core;

		             namespace AppCore;

		             class Program
		             {
		             	private static IEngineRun _engineRun = new GameCore().GetRun();

		             	private static void Main(string[] args)
		             	{
		             		_engineRun.Run();
		             	}

		             	public static void Play()
		             	{
		             		_engineRun.Play();
		             	}

		             	public static void Pause()
		             	{
		             		_engineRun.Pause();
		             	}

		             	public static void Quit()
		             	{
		             		_engineRun.Quit();
		             	}
		             }
		             """;
		var executableReferences = GetListExecutableReferences(code);

		var compilation = CSharpCompilation.Create(Path.GetRandomFileName(), [_syntaxTree], executableReferences, new CSharpCompilationOptions(OutputKind.ConsoleApplication));
		using var memoryStream = new MemoryStream();

		var compilationResult = compilation.Emit(memoryStream);
		if (!compilationResult.Success)
		{
			var errors = compilationResult.Diagnostics.Where(diagnostic => diagnostic.IsWarningAsError || diagnostic.Severity == DiagnosticSeverity.Error)?.ToList() ?? new List<Diagnostic>();
			foreach (Diagnostic diagnostic in errors)
			{
				Console.WriteLine(diagnostic);
			}
		}
		else
		{
			memoryStream.Seek(0, SeekOrigin.Begin);

			var assemblyContext = new AssemblyLoadContext(Path.GetRandomFileName(), true);
			var assembly = assemblyContext.LoadFromStream(memoryStream);

			var entryPoint = compilation.GetEntryPoint(CancellationToken.None);
			var type = assembly.GetType($"{entryPoint?.ContainingNamespace.MetadataName}.{entryPoint?.ContainingType.MetadataName}");
			var instance = assembly.CreateInstance(type.FullName);

			var main = type.GetMethod(entryPoint.MetadataName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
			var play = type.GetMethod("Play", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
			var pause = type.GetMethod("Pause", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
			var quit = type.GetMethod("Quit", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);

			_appMainThread = new Thread(() =>
			{
				main?.Invoke(instance, BindingFlags.InvokeMethod, Type.DefaultBinder, [new[] { string.Empty }], null);
			});

			_appMainThread.Start();

			play?.Invoke(instance, null);
			pause?.Invoke(instance, null);
			quit?.Invoke(instance, null);
			assemblyContext.Unload();

			string directory = Directory.GetCurrentDirectory();
			//string solutionFolder = FindSolutionFolder(directory);

			//ExecuteCommand($"dotnet build {Path.Combine(solutionFolder, "DecaEngine.Generator")}");
		}
	}
}