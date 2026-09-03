using System.Reflection;
using System.Runtime;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DecaEngine;

public enum AssemblyAppState
{
	NotLoaded,
	Stopped,
	Playing,
	Paused
}

public class AssemblyApp
{
	private readonly string _basePath;
	private readonly List<string> _assemblies;
	private Thread? _executableThread;
	private AssemblyLoadContext? _assemblyLoadContext;

	private MethodInfo? _runMethod;
	private MethodInfo? _playMethod;
	private MethodInfo? _pauseMethod;
	private MethodInfo? _quitMethod;

	private object? _instanceApp;

	public AssemblyAppState State { get; private set; } = AssemblyAppState.NotLoaded;

	/// <summary>Fires right after the project's entry-point thread starts, with its ManagedThreadId.</summary>
	public event Action<int>? ThreadStarted;

	/// <summary>Fires after the project thread is stopped/unloaded.</summary>
	public event Action? ThreadStopped;

	/// <summary>Convenience constructor for the "load an already built project" path (LoadFromPath).</summary>
	public AssemblyApp() : this(AppContext.BaseDirectory)
	{
	}

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

			State = AssemblyAppState.Stopped;
		}
	}

	/// <summary>Loads a built project assembly into a collectible AssemblyLoadContext; the entry
	/// point is found via Assembly.EntryPoint, so it may live in any namespace.</summary>
	public void LoadFromPath(string assemblyPath)
	{
		var fullPath = Path.GetFullPath(assemblyPath);
		var assemblyDir = Path.GetDirectoryName(fullPath) ?? _basePath;

		_assemblyLoadContext = new AssemblyLoadContext(Path.GetFileNameWithoutExtension(fullPath), true);
		_assemblyLoadContext.Resolving += (context, name) => ResolveDependency(context, name, assemblyDir);

		var asm = _assemblyLoadContext.LoadFromAssemblyPath(fullPath);

		var entryPoint = asm.EntryPoint ?? throw new InvalidOperationException(
			$"Assembly '{fullPath}' has no entry point (Main). Make sure OutputType is Exe.");
		var type = entryPoint.DeclaringType ?? throw new InvalidOperationException(
			$"Could not resolve declaring type of entry point in '{fullPath}'.");

		_instanceApp = type.IsAbstract && type.IsSealed ? null /* static class */ : asm.CreateInstance(type.FullName!);

		_runMethod = entryPoint;
		_playMethod = type.GetMethod("Play", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
		_pauseMethod = type.GetMethod("Pause", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
		_quitMethod = type.GetMethod("Quit", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);

		State = AssemblyAppState.Stopped;
	}

	private static System.Reflection.Assembly? ResolveDependency(AssemblyLoadContext context, AssemblyName name, string assemblyDir)
	{
		var alreadyLoaded = AssemblyLoadContext.Default.Assemblies
			.FirstOrDefault(a => string.Equals(a.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase));

		if (alreadyLoaded != null)
		{
			return alreadyLoaded;
		}

		var candidate = Path.Combine(assemblyDir, name.Name + ".dll");
		return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
	}

	/// <summary>Runs the loaded project's entry point on a separate thread of this process.</summary>
	public void Run()
	{
		if (_runMethod is null)
		{
			throw new InvalidOperationException("Nothing loaded yet. Call LoadFromPath first.");
		}

		_executableThread = new Thread(() =>
		{
			try
			{
				_runMethod.Invoke(_instanceApp, BindingFlags.InvokeMethod, Type.DefaultBinder, [new[] { string.Empty }], null);
			}
			catch (Exception ex)
			{
				Console.WriteLine(ex);
			}
		})
		{
			IsBackground = true
		};

		State = AssemblyAppState.Playing;
		_executableThread.Start();
		ThreadStarted?.Invoke(_executableThread.ManagedThreadId);
	}

	public void Play()
	{
		InvokeGuarded(_playMethod, "Play");
		if (State != AssemblyAppState.NotLoaded)
		{
			State = AssemblyAppState.Playing;
		}
	}

	public void Pause()
	{
		InvokeGuarded(_pauseMethod, "Pause");
		if (State != AssemblyAppState.NotLoaded)
		{
			State = AssemblyAppState.Paused;
		}
	}

	// Calls into project code must never crash the editor: foreign dlls can throw as early as
	// the cctor (e.g. TypeLoadException from a stale build). Errors are logged and swallowed.
	private void InvokeGuarded(MethodInfo? method, string name)
	{
		if (method is null)
		{
			return;
		}

		try
		{
			method.Invoke(_instanceApp, null);
		}
		catch (Exception ex)
		{
			var inner = ex is TargetInvocationException { InnerException: { } cause } ? cause : ex;
			Console.Error.WriteLine($"[project] {name} failed: {inner.GetType().Name}: {inner.Message}");
		}
	}

	/// <summary>Stops the project and unloads its assembly from the process.</summary>
	public void Quit()
	{
		InvokeGuarded(_quitMethod, "Quit");
		_executableThread?.Join(TimeSpan.FromSeconds(2));

		_assemblyLoadContext?.Unload();
		_assemblyLoadContext = null;
		_instanceApp = null;
		_runMethod = _playMethod = _pauseMethod = _quitMethod = null;
		_executableThread = null;

		State = AssemblyAppState.NotLoaded;
		ThreadStopped?.Invoke();
	}
}