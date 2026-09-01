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

	/// <summary>Ключевые слова варианта (см. IGraphicsApi.CreateShader с keywords): каждый уходит
	/// в компиляцию макросом NAME=1. Пусто - базовый вариант.</summary>
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

	/// <summary>Макросы компиляции: бэкенд-дефайн + ключевые слова варианта (каждое = 1).
	/// DECA_VULKAN нужен для бэкенд-специфичных веток (например, [[vk::builtin("PointSize")]] в
	/// UnlitInstancedPointVS.hlsl обязателен на Vulkan, но для FXC на D3D это синтаксическая
	/// ошибка X3000 - шейдер оборачивает атрибут в #if DECA_VULKAN).</summary>
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

	/// <summary>Диагностика: сколько раз Compile ВЫЗВАН и сколько раз он реально компилировал
	/// (то есть ранний возврат не сработал). Расхождение этих чисел и есть ответ на вопрос,
	/// переиспользуется ли скомпилированный шейдер между материалами.</summary>
	public static long DiagCompileMs;
	public static int DiagCompileCalls;
	public static int DiagCompileActual;

	/// <summary>Компиляции зовутся и с фоновых потоков загрузки (см.
	/// ModelLoader.PrecompileShaderVariants), и с главного (SetShader) - в том числе на ОДНОМ
	/// экземпляре (шарёный кэш DiligentGraphicsApi): без замка два потока компилировали бы один
	/// шейдер дважды с гонкой на _nativeShader. Создание ресурсов у IRenderDevice потокобезопасно
	/// (в отличие от контекстов) - параллельные компиляции РАЗНЫХ экземпляров легальны.</summary>
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

		// Inline-трассировка (RayQuery) в варианте шейдера требует DXC и SM 6.5 - штатный FXC не
		// знает даже идентификатора RaytracingAccelerationStructure (тот же выбор, что у
		// ProbeRoundPipelines для SCENE_TRACE_HARDWARE). Признак - кейворд варианта: компилятор
		// решается ЗДЕСЬ, потому что общий путь CreateSharedShader ничего не знает о содержимом
		// шейдера. Ключ дискового кеша байткода различает варианты по макросам, так что DXC- и
		// FXC-байткод не перепутаются.
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

		// Файл шейдера резолвится относительно ТЕКУЩЕЙ директории процесса - при запуске с "чужим"
		// CWD (IDE с рабочей папкой проекта и т.п.) файл не находится, и без этой проверки наружу
		// уходило только безликое "Shader is not compiled" из NativeShader.
		var expectedPath = Path.Combine(Environment.CurrentDirectory, FactoryPath, FilePath);
		if (!File.Exists(expectedPath))
		{
			throw new FileNotFoundException(
				$"Shader source not found: '{expectedPath}' (shader '{Name}', CWD '{Environment.CurrentDirectory}'). " +
				"Проверь, что EditorAssets скопированы в рабочую директорию процесса.", expectedPath);
		}

		// Дисковый кеш байткода: попадание минует компилятор совсем (большие варианты
		// UnlitInstancedPS стоят секунды КАЖДЫМ запуском). Ключ включает содержимое исходника со
		// всеми инклудами, так что правка шейдера инвалидирует запись сама.
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

				// Битая/несовместимая запись (например, после апдейта Diligent или драйвера) -
				// убрать и скомпилировать из исходника обычным путём.
				bytecodeCache.Invalidate(cacheKey);
			}
		}

		_nativeShader = _api.Device.CreateShader(shaderCi, out var compilerOutput);

		// Diligent при ошибке компиляции возвращает null и кладёт лог в compilerOutput - раньше он
		// молча уничтожался, а наружу уходило только "Shader is not compiled" без причины.
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

			// Diligent кладёт в blob нуль-терминированный текст лога компилятора.
			return System.Runtime.InteropServices.Marshal.PtrToStringAnsi(blob.GetDataPtr(), size).TrimEnd('\0', '\n', ' ');
		}
		catch
		{
			return "<failed to read compiler output>";
		}
	}

	/// <summary>Шейдер выдан из кэша <see cref="DiligentGraphicsApi"/> и шарится между МОДЕЛЯМИ -
	/// его <see cref="Release"/> игнорируется: освобождение одной модели иначе убило бы нативный
	/// объект, которым пользуются живые материалы другой. Освобождается только вместе с api
	/// (см. ReleaseShared).</summary>
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

	/// <summary>Настоящее освобождение шарёного шейдера - только из владельца кэша.</summary>
	internal void ReleaseShared()
	{
		_nativeShader?.Dispose();
		_nativeShader = null;
	}
}