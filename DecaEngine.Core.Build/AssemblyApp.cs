using System.Reflection;
using System.Runtime;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DecaEngine;

public class AssemblyApp
{
	private readonly string _basePath;
	private readonly List<string> _assemblies;
	private Thread _executableThread;
	private AssemblyLoadContext _assemblyLoadContext;

	private MethodInfo? _runMethod;
	private MethodInfo? _playMethod;
	private MethodInfo? _pauseMethod;
	private MethodInfo? _quitMethod;

	private object? _instanceApp;

	public AssemblyApp(string basePath, params string[] assembliesLocation)
	{
		_basePath = Path.GetDirectoryName(typeof(GCSettings).GetTypeInfo().Assembly.Location);
		_assemblies = new List<string>();

		foreach (var assembly in assembliesLocation)
		{
			_assemblies.Add(assembly);
		}

		_assemblies.Add(typeof(object).GetTypeInfo().Assembly.Location);
		_assemblies.Add(Path.Combine(_basePath, "System.Console.dll"));
		_assemblies.Add(Path.Combine(_basePath, "System.Runtime.dll"));
		_assemblies.Add(Path.Combine(_basePath, "System.Runtime.Extensions.dll"));
		_assemblies.Add(Path.Combine(_basePath, "mscorlib.dll"));
	}

	private List<PortableExecutableReference> GetListExecutableReferences(string code)
	{
		var executableReferences = new List<PortableExecutableReference>();

		foreach (var reference in _assemblies)
		{
			if (File.Exists(reference))
			{
				executableReferences.Add(MetadataReference.CreateFromFile(reference));
			}
		}

		return executableReferences;
	}

	public void Load(string code)
	{
		var syntaxTree = CSharpSyntaxTree.ParseText(code, new CSharpParseOptions(LanguageVersion.CSharp10));
		var root = syntaxTree.GetRoot() as CompilationUnitSyntax;
		var executableReferences = GetListExecutableReferences(code);

		_assemblies.AddRange(root.Usings.Select(x => Path.Combine(_basePath, $"{x.Name}.dll")));

		var compilation = CSharpCompilation.Create(Path.GetRandomFileName(), [syntaxTree], executableReferences, new CSharpCompilationOptions(OutputKind.ConsoleApplication));
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

			_assemblyLoadContext = new AssemblyLoadContext(Path.GetRandomFileName(), true);
			var assembly = _assemblyLoadContext.LoadFromStream(memoryStream);

			var entryPoint = compilation.GetEntryPoint(CancellationToken.None);
			var type = assembly.GetType($"{entryPoint?.ContainingNamespace.MetadataName}.{entryPoint?.ContainingType.MetadataName}");
			_instanceApp = assembly.CreateInstance(type.FullName);

			_runMethod = type.GetMethod(entryPoint.MetadataName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
			_playMethod = type.GetMethod("Play", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
			_pauseMethod = type.GetMethod("Pause", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
			_quitMethod = type.GetMethod("Quit", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
		}
	}

	public void LoadFromPath()
	{
		string path = @"C:\Users\rfgf89\Desktop\NewProject33\bin\Debug\net10.0\NewProject33.dll";
		var alc = AssemblyLoadContext.Default;
		var asm = alc.LoadFromAssemblyPath(Path.GetFullPath(path));

		var type = asm.GetType("Program");
		_instanceApp = asm.CreateInstance(type.FullName);

		_runMethod = asm.EntryPoint;
		_playMethod = type.GetMethod("Play", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
		_pauseMethod = type.GetMethod("Pause", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
		_quitMethod = type.GetMethod("Quit", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
	}

	public void Run()
	{
		_executableThread = new Thread(() =>
		{
			_runMethod?.Invoke(_instanceApp, BindingFlags.InvokeMethod, Type.DefaultBinder, [new[] { string.Empty }], null);
		});

		_executableThread.Start();
	}

	public void Play()
	{
		_playMethod?.Invoke(_instanceApp, null);
	}

	public void Pause()
	{
		_pauseMethod?.Invoke(_instanceApp, null);
	}

	public void Quit()
	{
		_quitMethod?.Invoke(_instanceApp, null);
		_assemblyLoadContext.Unload();
	}
}