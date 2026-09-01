using DecaEngine;
using Diligent;
using DecaEngine.Graphics.Diligent;

namespace DecaEngine.Graphics.ProbeGi;

/// <summary>
/// Скомпилированные конвейеры GPU-раунда probe-GI. Вынесены из <see cref="ProbeRoundGpu"/> и живут
/// отдельно от сессии, потому что от неё не зависят: от сессии зависят только буферы и привязки.
///
/// Зачем отдельный объект: компиляция этих двух шейдеров стоит ~650 мс (в них целиком входит обход
/// BVH), а сессия пересоздаётся на каждое изменение настроек probe-GI - то есть на каждое движение
/// ползунка в окне Graphics. Пока компиляция сидела в конструкторе ProbeRoundGpu, каждая такая
/// правка означала более чем полусекундный стопор ПРЯМО НА ПОТОКЕ РЕНДЕРА между кадрами; кадровый
/// цикл swap chain этого не переживает - в логе редактора это выглядело как «Timeout elapsed while
/// waiting for the frame waitable object», а дальше устройство уезжало в отказ.
/// </summary>
public sealed class ProbeRoundPipelines : IDisposable
{
	/// <summary>Раунд обновления проб (entry point main).</summary>
	public IPipelineState Round { get; }

	/// <summary>Обновление кэша поверхностей (entry point mainSurface).</summary>
	public IPipelineState Surface { get; }

	/// <summary>Свёртка изменчивости проб в число на объём (entry point mainVariability).</summary>
	public IPipelineState Variability { get; }

	/// <summary>Во что обошлась компиляция, мс - для диагностики.</summary>
	public long CompileMs { get; }

	/// <summary>Скомпилированы под аппаратную трассировку (SCENE_TRACE_HARDWARE) - значит нужен
	/// TLAS, а буферы BVH этим вариантом не читаются.</summary>
	public bool Hardware { get; }

	public ProbeRoundPipelines(DiligentGraphicsApi api, bool hardware)
	{
		var sw = System.Diagnostics.Stopwatch.StartNew();
		var device = api.Device;
		Hardware = hardware;

		using var factory = api.EngineFactory.CreateDefaultShaderSourceStreamFactory("EditorAssets/shader");

		Round = CreatePso(device, factory, "ProbeRoundCS", "main", "ProbeRound", hardware);
		Surface = CreatePso(device, factory, "ProbeSurfaceCacheCS", "mainSurface", "ProbeSurfaceCache", hardware);

		// Свёртка изменчивости трассировкой не пользуется, поэтому компилируется БЕЗ кейворда
		// аппаратного пути (hardware: false) - и заодно штатным компилятором, без SM 6.5 и DXC.
		// Имя PSO своё и с вариантом трассировки не пересекается: дисковый кэш D3D12 ключуется по
		// имени, и одинаковые имена у разных вариантов травят кэш чужим кодом.
		Variability = CreatePso(device, factory, "ProbeVariabilityCS", "mainVariability",
			"ProbeVariability", hardware: false, fileName: "ProbeVariabilityCS.hlsl");

		CompileMs = sw.ElapsedMilliseconds;
	}

	/// <param name="fileName">Файл шейдера. Отдельно от <paramref name="shaderName"/>: раунд проб и
	/// проход кэша поверхностей - это два входа в ОДНОМ файле, а свёртка изменчивости живёт в
	/// своём.</param>
	private static IPipelineState CreatePso(IRenderDevice device, IShaderSourceInputStreamFactory factory,
		string shaderName, string entryPoint, string psoName, bool hardware,
		string fileName = "ProbeRoundCS.hlsl")
	{
		// Путь трассировки выбирается КЕЙВОРДОМ, а не ветвлением в рантайме: выключенный вариант не
		// существует в скомпилированном коде вовсе - ни обхода BVH, ни его буферов (см. SceneTrace.hlsl).
		var macros = new ShaderMacroArray
		{
			Elements = hardware ? [new ShaderMacro("SCENE_TRACE_HARDWARE", "1")] : [],
		};

		using var shader = device.CreateShader(new ShaderCreateInfo
		{
			SourceLanguage = ShaderSourceLanguage.Hlsl,
			Desc = new ShaderDesc
			{
				Name = shaderName,
				ShaderType = ShaderType.Compute,
				UseCombinedTextureSamplers = false,
			},
			EntryPoint = entryPoint,
			FilePath = fileName,
			Macros = macros,
			// Inline-трассировка (RayQuery) появилась в шейдерной модели 6.5, а её понимает только
			// DXC - штатный FXC не знает даже идентификатора RaytracingAccelerationStructure.
			// Программному пути всё это не нужно, поэтому переключаем компилятор только под аппаратный.
			ShaderCompiler = hardware ? ShaderCompiler.Dxc : ShaderCompiler.Default,
			// global:: обязателен: изнутри DecaEngine.Graphics.ProbeGi имя Diligent сначала
			// разрешается в соседний DecaEngine.Graphics.Diligent и заслоняет SDK.
			HLSLVersion = hardware ? new global::Diligent.Version(6, 5) : default,
			ShaderSourceStreamFactory = factory,
		}, out _);

		if (shader == null)
		{
			throw new InvalidOperationException(
				$"Failed to compile '{shaderName}'" + (hardware
					? " with hardware ray tracing (SM 6.5 / DXC) - see the compiler output above"
					: string.Empty));
		}

		return device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
		{
			PSODesc = new PipelineStateDesc
			{
				Name = psoName,
				PipelineType = PipelineType.Compute,
				ResourceLayout = new PipelineResourceLayoutDesc
				{
					DefaultVariableType = ShaderResourceVariableType.Mutable,
				},
			},
			Cs = shader,
		});
	}

	public void Dispose()
	{
		Variability.Dispose();
		Surface.Dispose();
		Round.Dispose();
	}
}
