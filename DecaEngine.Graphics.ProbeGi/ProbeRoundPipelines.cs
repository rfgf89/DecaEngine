using DecaEngine;
using Diligent;
using DecaEngine.Graphics.Diligent;

namespace DecaEngine.Graphics.ProbeGi;

/// <summary>Compiled compute pipelines for the probe-GI GPU round.</summary>
// Kept outside the session: these shaders cost ~650 ms to compile, and recompiling them on the
// render thread every time a probe-GI slider moves stalls the swap chain into device removal.
public sealed class ProbeRoundPipelines : IDisposable
{
	/// <summary>Probe update round (entry point main).</summary>
	public IPipelineState Round { get; }

	/// <summary>Surface cache update (entry point mainSurface).</summary>
	public IPipelineState Surface { get; }

	/// <summary>Probe variability reduction (entry point mainVariability).</summary>
	public IPipelineState Variability { get; }

	/// <summary>Compile cost in milliseconds.</summary>
	public long CompileMs { get; }

	/// <summary>Built for hardware ray tracing: needs a TLAS, ignores the BVH buffers.</summary>
	public bool Hardware { get; }

	public ProbeRoundPipelines(DiligentGraphicsApi api, bool hardware)
	{
		var sw = System.Diagnostics.Stopwatch.StartNew();
		var device = api.Device;
		Hardware = hardware;

		using var factory = api.EngineFactory.CreateDefaultShaderSourceStreamFactory("EditorAssets/shader");

		Round = CreatePso(device, factory, "ProbeRoundCS", "main", "ProbeRound", hardware);
		Surface = CreatePso(device, factory, "ProbeSurfaceCacheCS", "mainSurface", "ProbeSurfaceCache", hardware);

		// Variability does not trace, so it builds without the hardware keyword. Its PSO name
		// must stay distinct: the D3D12 disk cache is keyed by name and would poison variants.
		Variability = CreatePso(device, factory, "ProbeVariabilityCS", "mainVariability",
			"ProbeVariability", hardware: false, fileName: "ProbeVariabilityCS.hlsl");

		CompileMs = sw.ElapsedMilliseconds;
	}

	private static IPipelineState CreatePso(IRenderDevice device, IShaderSourceInputStreamFactory factory,
		string shaderName, string entryPoint, string psoName, bool hardware,
		string fileName = "ProbeRoundCS.hlsl")
	{
		// Trace path is a compile-time keyword: the unused variant is absent from the bytecode,
		// including its BVH buffer bindings.
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
			// RayQuery needs SM 6.5, which only DXC understands; FXC serves the software path.
			ShaderCompiler = hardware ? ShaderCompiler.Dxc : ShaderCompiler.Default,
			// global:: is required: inside this namespace "Diligent" resolves to the sibling
			// DecaEngine.Graphics.Diligent and shadows the SDK.
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
