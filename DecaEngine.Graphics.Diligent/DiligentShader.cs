using System;
using DecaEngine.Core;
using Diligent;

namespace DecaEngine.Graphics.Diligent;

public class DiligentShader : IShaderObject
{
	private readonly DiligentGraphicsApi _api;
	private IShader? _nativeShader;

	public ShaderObjectType Type { get; }
	public string Name { get; }
	public string FilePath { get; }
	public string FactoryPath { get; }
	public string EntryPoint { get; }

	/// <summary>Variant keywords; each is passed to the compiler as NAME=1.</summary>
	public IReadOnlyList<string> Keywords { get; }

	public IShader NativeShader => _nativeShader ?? throw new NullReferenceException(
		$"Shader is not compiled: '{Name}' ({FactoryPath}/{FilePath}).");

	public DiligentShader(DiligentGraphicsApi api, string name, string factoryPath, string file, ShaderObjectType type, string entryPoint = "Main",
		IReadOnlyList<string> keywords = null)
	{
		_api = api ?? throw new ArgumentNullException(nameof(api));
		Name = name;
		FilePath = file;
		FactoryPath = factoryPath;
		Type = type;
		EntryPoint = entryPoint;
		Keywords = keywords ?? Array.Empty<string>();
	}

	// DECA_VULKAN gates backend-specific syntax: vk:: attributes are an X3000 error under FXC.
	private ShaderMacro[] BuildMacros()
	{
		var macros = new ShaderMacro[Keywords.Count + 1];
		macros[0] = new ShaderMacro("DECA_VULKAN",
			_api.Device.GetDeviceInfo().Type == RenderDeviceType.Vulkan ? "1" : "0");

		for (int i = 0; i < Keywords.Count; i++)
		{
			macros[i + 1] = new ShaderMacro(Keywords[i], "1");
		}

		return macros;
	}

	// Calls vs actual compiles: the gap shows how well shaders are reused between materials.
	public static long DiagCompileMs;
	public static int DiagCompileCalls;
	public static int DiagCompileActual;

	// Compile runs from both loader threads and the main thread on one shared instance.
	// IRenderDevice resource creation is thread-safe, so distinct instances may compile at once.
	private readonly object _compileLock = new();

	public void Compile()
	{
		DiagCompileCalls++;

		if (_nativeShader != null)
		{
			return;
		}

		lock (_compileLock)
		{
			if (_nativeShader != null)
			{
				return;
			}

			DiagCompileActual++;
			var swDiag = System.Diagnostics.Stopwatch.StartNew();
			try
			{
				CompileCore();
			}
			finally
			{
				DiagCompileMs += swDiag.ElapsedMilliseconds;
			}
		}
	}

	private void CompileCore()
	{

		using var shaderSourceFactory = _api.EngineFactory.CreateDefaultShaderSourceStreamFactory(Path.Combine(Environment.CurrentDirectory, FactoryPath));

		var diligentType = Type switch
		{
			ShaderObjectType.Vertex => global::Diligent.ShaderType.Vertex,
			ShaderObjectType.Pixel => global::Diligent.ShaderType.Pixel,
			ShaderObjectType.Compute => global::Diligent.ShaderType.Compute,
			ShaderObjectType.Geometry => global::Diligent.ShaderType.Geometry,
			ShaderObjectType.Domain => global::Diligent.ShaderType.Domain,
			ShaderObjectType.Hull => global::Diligent.ShaderType.Hull,
			_ => throw new ArgumentOutOfRangeException()
		};

		// Inline ray tracing needs DXC and SM 6.5; FXC does not know RaytracingAccelerationStructure.
		bool needsRayQuery = Keywords != null &&
			(Keywords.Contains("FEATURE_RT_SHADOWS") || Keywords.Contains("FEATURE_RT_REFLECTIONS"));

		var shaderCi = new ShaderCreateInfo
		{
			SourceLanguage = ShaderSourceLanguage.Hlsl,
			Desc = new ShaderDesc
			{
				Name = Name,
				UseCombinedTextureSamplers = true,
				ShaderType = diligentType,
			},
			EntryPoint = EntryPoint,
			CompileFlags = ShaderCompileFlags.PackMatrixRowMajor,
			FilePath = FilePath,
			ShaderSourceStreamFactory = shaderSourceFactory,
			Macros = new ShaderMacroArray { Elements = BuildMacros() },
			ShaderCompiler = needsRayQuery ? ShaderCompiler.Dxc : ShaderCompiler.Default,
			HLSLVersion = needsRayQuery ? new global::Diligent.Version(6, 5) : default,
		};

		// Shader paths resolve against the process CWD, so an unexpected CWD must fail loudly.
		var expectedPath = Path.Combine(Environment.CurrentDirectory, FactoryPath, FilePath);
		if (!File.Exists(expectedPath))
		{
			throw new FileNotFoundException(
				$"Shader source not found: '{expectedPath}' (shader '{Name}', CWD '{Environment.CurrentDirectory}'). " +
				"Check that EditorAssets are copied into the process working directory.", expectedPath);
		}

		// Disk bytecode cache; the key hashes the source with all includes, so edits self-invalidate.
		var bytecodeCache = _api.ShaderBytecodeCache;
		var cacheKey = bytecodeCache?.ComputeKey(Path.Combine(Environment.CurrentDirectory, FactoryPath),
			FilePath, Type, EntryPoint, shaderCi.Macros.Elements, shaderCi.CompileFlags);
		if (bytecodeCache != null && cacheKey != null)
		{
			var cachedBytecode = bytecodeCache.TryLoad(cacheKey);
			if (cachedBytecode != null)
			{
				_nativeShader = DiligentShaderBytecodeInterop.CreateShader(_api.Device, Name, diligentType,
					EntryPoint, cachedBytecode);
				if (_nativeShader != null)
				{
					return;
				}

				// Stale entry, e.g. after a Diligent or driver update: drop it and recompile.
				bytecodeCache.Invalidate(cacheKey);
			}
		}

		_nativeShader = _api.Device.CreateShader(shaderCi, out var compilerOutput);

		// Diligent signals a compile failure with a null shader and the log in compilerOutput.
		if (_nativeShader == null)
		{
			string log = ReadCompilerOutput(compilerOutput);
			compilerOutput?.Dispose();
			throw new InvalidOperationException($"Shader '{Name}' ({FilePath}) failed to compile:\n{log}");
		}

		compilerOutput?.Dispose();

		if (bytecodeCache != null && cacheKey != null)
		{
			var compiledBytecode = _nativeShader.GetBytecode();
			if (compiledBytecode.Length > 0)
			{
				bytecodeCache.Store(cacheKey, compiledBytecode.ToArray());
			}
		}
	}

	private static unsafe string ReadCompilerOutput(IDataBlob? blob)
	{
		if (blob == null)
		{
			return "<no compiler output>";
		}

		try
		{
			var size = (int)blob.GetSize();
			if (size <= 0)
			{
				return "<empty compiler output>";
			}

			// The blob holds null-terminated compiler log text.
			return System.Runtime.InteropServices.Marshal.PtrToStringAnsi(blob.GetDataPtr(), size).TrimEnd('\0', '\n', ' ');
		}
		catch
		{
			return "<failed to read compiler output>";
		}
	}

	/// <summary>Shared cache instance: Release is a no-op, only the owning api may free it.</summary>
	public bool IsShared { get; init; }

	public void Release()
	{
		if (IsShared)
		{
			return;
		}

		_nativeShader?.Dispose();
		_nativeShader = null;
	}

	internal void ReleaseShared()
	{
		_nativeShader?.Dispose();
		_nativeShader = null;
	}
}