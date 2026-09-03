using System.Numerics;
using System.Runtime.InteropServices;
using DecaEngine.Graphics.Diligent;
using DecaEngine.Graphics.ProbeGi;
using Diligent;
using DecaEngine.Graphics;

namespace DecaEngine.Probes;

/// <summary>Cross-checks the GPU BVH traversal against the CPU tracer in <see cref="ProbeGiBaker"/>.</summary>
public static class SceneTraceVerifier
{
	// Relative, because absolute error grows with ray length; exact equality is impossible since
	// the CPU works in extended-precision registers and the GPU strictly in float.
	private const float RelativeTolerance = 1e-3f;

	public readonly record struct Report(int RayCount, int Mismatches, float WorstRelativeError,
		int CpuHits, int GpuHits, int ShaderNodeCount, int UploadedNodeCount);

	// Mirrors cbuffer TraceTestParams in SceneTraceTestCS.hlsl - uint, not float.
	[StructLayout(LayoutKind.Sequential)]
	private struct TraceTestParams
	{
		public uint Count;
		public uint Pad0, Pad1, Pad2;
	}

	public static unsafe Report Run(DiligentGraphicsApi api, ProbeGiBaker baker,
		Vector3 boundsMin, Vector3 boundsMax, int rayCount = 4096)
	{
		var (nodes, order, triangles) = baker.ExportBvh();

		// Deterministic low-discrepancy rays: the test must be reproducible to be debuggable.
		var origins = new Vector4[rayCount];
		var directions = new Vector4[rayCount];
		var size = boundsMax - boundsMin;
		float tMax = baker.RayTMax;
		for (int i = 0; i < rayCount; i++)
		{
			float u1 = Frac(i * 0.7548776662f);
			float u2 = Frac(i * 0.5698402909f);
			float u3 = Frac(i * 0.6180339887f);
			float u4 = Frac(i * 0.8191725134f);
			float u5 = Frac(i * 0.3819660112f);

			origins[i] = new Vector4(boundsMin + new Vector3(size.X * u1, size.Y * u2, size.Z * u3), tMax);

			float z = u4 * 2f - 1f;
			float r = MathF.Sqrt(MathF.Max(1f - z * z, 0f));
			float phi = u5 * 2f * MathF.PI;
			directions[i] = new Vector4(
				Vector3.Normalize(new Vector3(MathF.Cos(phi) * r, z, MathF.Sin(phi) * r)), 0f);
		}

		var device = api.Device;
		var context = api.ImmediateContext;

		using var nodeBuffer = CreateStructured(device, "SceneBvhNodes", nodes, sizeof(BvhNodeGpu));
		using var orderBuffer = CreateStructured(device, "SceneBvhOrder", order, sizeof(uint));
		using var triBuffer = CreateStructured(device, "SceneBvhTriangles", triangles, sizeof(BvhTriangleGpu));
		using var originBuffer = CreateStructured(device, "TestRayOrigin", origins, sizeof(Vector4));
		using var dirBuffer = CreateStructured(device, "TestRayDirection", directions, sizeof(Vector4));

		using var resultBuffer = device.CreateBuffer(new BufferDesc
		{
			Name = "TestResult",
			Usage = Usage.Default,
			BindFlags = BindFlags.UnorderedAccess,
			Mode = BufferMode.Structured,
			ElementByteStride = (uint)sizeof(Vector4),
			Size = (ulong)(rayCount * sizeof(Vector4)),
		});

		using var paramsBuffer = device.CreateBuffer(new BufferDesc
		{
			Name = "TraceTestParams",
			Usage = Usage.Dynamic,
			BindFlags = BindFlags.UniformBuffer,
			CPUAccessFlags = CpuAccessFlags.Write,
			Size = 16,
		});
		// Discard, not DoNotWait: DoNotWait may silently skip the write, leaving a zero cbuffer.
		context.UploadBufferExt(paramsBuffer, new TraceTestParams { Count = (uint)rayCount },
			MapFlags.Discard);

		using var factory = api.EngineFactory.CreateDefaultShaderSourceStreamFactory("EditorAssets/shader");
		using var shader = device.CreateShader(new ShaderCreateInfo
		{
			SourceLanguage = ShaderSourceLanguage.Hlsl,
			Desc = new ShaderDesc
			{
				Name = "SceneTraceTestCS",
				ShaderType = ShaderType.Compute,
				UseCombinedTextureSamplers = false,
			},
			EntryPoint = "main",
			FilePath = "SceneTraceTestCS.hlsl",
			ShaderSourceStreamFactory = factory,
		}, out _);

		using var pso = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
		{
			PSODesc = new PipelineStateDesc
			{
				Name = "SceneTraceTest",
				PipelineType = PipelineType.Compute,
				ResourceLayout = new PipelineResourceLayoutDesc
				{
					DefaultVariableType = ShaderResourceVariableType.Mutable,
				},
			},
			Cs = shader,
		});

		using var srb = pso.CreateShaderResourceBinding(true);
		Bind(srb, "_SceneBvhNodes", nodeBuffer);
		Bind(srb, "_SceneBvhOrder", orderBuffer);
		Bind(srb, "_SceneBvhTriangles", triBuffer);
		Bind(srb, "_TestRayOrigin", originBuffer);
		Bind(srb, "_TestRayDirection", dirBuffer);
		Require(srb, "_TestResult").Set(
			resultBuffer.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.AllowOverwrite);
		Require(srb, "TraceTestParams").Set(paramsBuffer, SetShaderResourceFlags.AllowOverwrite);

		context.SetPipelineState(pso);
		context.CommitShaderResources(srb, ResourceStateTransitionMode.Transition);
		context.DispatchCompute(new DispatchComputeAttribs
		{
			ThreadGroupCountX = (uint)((rayCount + 63) / 64),
			ThreadGroupCountY = 1,
			ThreadGroupCountZ = 1,
		});

		var gpu = new Vector4[rayCount];
		fixed (Vector4* dst = gpu)
		{
			context.ReadBufferExt<Vector4>(device, resultBuffer, dst, (uint)(rayCount * sizeof(Vector4)));
		}

		// Shader marker: distinguishes a traversal mismatch from an unwritten result buffer.
		if (gpu[0].Z != 777f)
		{
			throw new InvalidOperationException(
				$"Compute shader did not write results (marker={gpu[0].Z}, expected 777) - " +
				"dispatch or UAV binding is broken, not the traversal");
		}

		// Informational only: GetDimensions is unreliable on some backends (see Bind).
		int shaderNodeCount = (int)gpu[0].W;

		int mismatches = 0, cpuHits = 0, gpuHits = 0;
		float worst = 0f;
		for (int i = 0; i < rayCount; i++)
		{
			bool cpuHit = baker.TraceRay(
				new Vector3(origins[i].X, origins[i].Y, origins[i].Z),
				new Vector3(directions[i].X, directions[i].Y, directions[i].Z),
				tMax, out float cpuT, out _, out _);

			bool gpuHit = gpu[i].X >= 0f;
			if (cpuHit) cpuHits++;
			if (gpuHit) gpuHits++;

			if (cpuHit != gpuHit)
			{
				mismatches++;
				continue;
			}

			if (!cpuHit)
			{
				continue;
			}

			float error = MathF.Abs(gpu[i].X - cpuT) / MathF.Max(cpuT, 1e-4f);
			worst = MathF.Max(worst, error);
			if (error > RelativeTolerance)
			{
				mismatches++;
			}
		}

		return new Report(rayCount, mismatches, worst, cpuHits, gpuHits, shaderNodeCount, nodes.Length);
	}

	public readonly record struct RoundReport(int Probes, int Rounds, float WorstRelativeError,
		int Mismatches, float CpuMeanLuminance, float GpuMeanLuminance,
		double CpuMsPerRound, double GpuMsPerRound,
		int SignificantProbes, float WorstAbsoluteError, float MeanMagnitude);

	public static RoundReport VerifyRound(DiligentGraphicsApi api, ProbeGiBaker baker,
		Vector3 boundsMin, Vector3 boundsMax, Vector3 sunDirection, Vector3 sunColor, int rounds = 4,
		IGpuTexture? environmentMap = null, Func<Vector3, Vector3>? skyRadiance = null,
		float envYaw = 0f)
	{
		var (nodes, order, triangles) = baker.ExportBvh();

		// With sky the check is order-of-magnitude only: a CPU function cannot match a GPU sampler.
		bool withSky = environmentMap != null && skyRadiance != null;

		var options = new ProbeGiBakeOptions
		{
			SkyIntensity = withSky ? 1f : 0f,
			Bounces = 2,
			SurfaceCache = true,
		};

		var session = baker.BeginBake(boundsMin, boundsMax, sunDirection, sunColor,
			withSky ? envYaw : 0f, skyRadiance ?? (_ => Vector3.Zero), options);

		// The cache is built lazily by the first round, but the GPU side needs it at buffer
		// creation; running a round here would put the CPU one round ahead.
		baker.EnsureSurfaceCache(session);

		// Atlases must be bound even though this compares the field buffer: the shader writes them
		// unconditionally, and an unbound UAV is a validation error.
		var atlases = new ProbeGiTextures(api, session.Result, "_probeGiVerify", gpuWritable: true);
		// Deliberately not `using`: gpu holds views on the atlases and must be released first.
		using var pipelines = new ProbeRoundPipelines(api, hardware: false);
		var gpu = new ProbeRoundGpu(api, pipelines, session, baker, atlases, environmentMap,
			withSky ? envYaw : 0f);

		// Both paths must see the SAME ray directions and blend weight: the field is a running
		// average, so comparing after differing round counts is meaningless.
		for (int i = 0; i < rounds; i++)
		{
			var directions = ProbeGiBaker.RoundRayDirections(session);
			float alpha = ProbeGiBaker.RoundBlendWeight(session);
			// A GPU round runs in chunks; drain it fully before stepping the CPU.
			while (!gpu.RunRound(session, baker, directions, alpha))
			{
			}

			baker.RunRound(session);
		}

		var gpuField = gpu.ReadField();

		int mismatches = 0, significant = 0;
		float worst = 0f, worstAbs = 0f;
		double cpuSum = 0, gpuSum = 0, magnitudeSum = 0;
		for (int p = 0; p < session.ProbeCount; p++)
		{
			var cpu = session.IrradianceRead[p];
			var got = gpuField[p * 4];
			var gotRgb = new Vector3(got.X, got.Y, got.Z);

			cpuSum += Luminance(cpu);
			gpuSum += Luminance(gotRgb);

			float absError = (gotRgb - cpu).Length();
			worstAbs = MathF.Max(worstAbs, absError);
			magnitudeSum += cpu.Length();

			// Relative error only for probes bright enough to have one: near zero, a last-bit
			// difference produces an arbitrarily large ratio.
			float scale = cpu.Length();
			if (scale < 1e-3f)
			{
				continue;
			}

			significant++;
			float error = absError / scale;
			worst = MathF.Max(worst, error);
			if (error > 2e-2f)
			{
				mismatches++;
			}
		}

		// GPU timing needs Flush+WaitForIdle: a dispatch alone only records commands. The sync
		// adds overhead, so the GPU figure is pessimistic.
		const int timedRounds = 4;
		var context = api.ImmediateContext;

		var swGpu = System.Diagnostics.Stopwatch.StartNew();
		for (int i = 0; i < timedRounds; i++)
		{
			while (!gpu.RunRound(session, baker,
				ProbeGiBaker.RoundRayDirections(session.RaysPerRound, session.Sequence + i, session.FixedRays),
				ProbeGiBaker.RoundBlendWeight(session)))
			{
			}
		}

		context.Flush();
		context.WaitForIdle();
		swGpu.Stop();

		var swCpu = System.Diagnostics.Stopwatch.StartNew();
		for (int i = 0; i < timedRounds; i++)
		{
			baker.RunRound(session);
		}

		swCpu.Stop();
		gpu.Dispose();
		atlases.Release();

		return new RoundReport(session.ProbeCount, rounds, worst, mismatches,
			(float)(cpuSum / Math.Max(session.ProbeCount, 1)),
			(float)(gpuSum / Math.Max(session.ProbeCount, 1)),
			swCpu.Elapsed.TotalMilliseconds / timedRounds,
			swGpu.Elapsed.TotalMilliseconds / timedRounds,
			significant, worstAbs, (float)(magnitudeSum / Math.Max(session.ProbeCount, 1)));
	}

	/// <summary>Realtime field flicker: per-probe boiling (MeanRelativeDelta) vs whole-scene
	/// breathing from the multibounce loop (MeanLuminance*), which need opposite fixes.</summary>
	public readonly record struct FlickerReport(int Probes, int Rays, float Alpha, int Rounds,
		float MeanRelativeDelta, float MaxRelativeDelta,
		float MeanLuminanceMin, float MeanLuminanceMax, float MeanLuminanceAvg,
		float P50, float P90, float P99, float ShareAbove10,
		float Variability, float SkippedRoundShare)
	{
		public float GlobalSwing => MeanLuminanceAvg > 1e-6f
			? (MeanLuminanceMax - MeanLuminanceMin) / MeanLuminanceAvg
			: 0f;
	}

	public static FlickerReport MeasureFlicker(DiligentGraphicsApi api, ProbeGiBaker baker,
		Vector3 boundsMin, Vector3 boundsMax, Vector3 sunDirection, Vector3 sunColor,
		int raysPerRound, int settleRounds, int measureRounds, float maxRayLuminance = 0f,
		float blend = 0f, float skyIntensity = 1f, float gridDensity = 0f, float maxStep = -1f,
		float relocation = -1f, float gamma = -1f, float variabilityThreshold = 0f,
		bool hardware = false,
		IGpuTexture? environmentMap = null, Func<Vector3, Vector3>? skyRadiance = null,
		float envYaw = 0f)
	{
		bool withSky = environmentMap != null && skyRadiance != null;
		var options = new ProbeGiBakeOptions
		{
			// Sets sky-vs-surface CONTRAST, which is the dominant source of per-probe variance.
			SkyIntensity = withSky ? skyIntensity : 0f,
			Bounces = 2,
			SurfaceCache = true,
			// In realtime mode the session reads this, not RaysPerRound.
			RealtimeRaysPerRound = raysPerRound,
			RealtimeMaxRayLuminance = maxRayLuminance,
			// Negative means "keep the default"; 0 is a meaningful value here.
			RealtimeMaxStep = maxStep < 0f ? new ProbeGiBakeOptions().RealtimeMaxStep : maxStep,
			RealtimeRelocation = relocation < 0f
				? new ProbeGiBakeOptions().RealtimeRelocation
				: relocation,
			RealtimeGamma = gamma < 0f ? new ProbeGiBakeOptions().RealtimeGamma : gamma,
			// Off by default: a skipped round reports zero delta and would dilute the metric.
			RealtimeVariabilityThreshold = variabilityThreshold,
			RealtimeBlend = blend > 0f ? blend : ProbeGiBaker.RealtimeBlend,
			// Flicker only matters at constant blend weight; a bake's weight decays to zero.
			Realtime = true,
		};

		if (gridDensity > 0f)
		{
			options.GridDensity = gridDensity;
			options.MaxProbes = ProbeGiBaker.MaxProbeBudget;
		}

		var session = baker.BeginBake(boundsMin, boundsMax, sunDirection, sunColor,
			withSky ? envYaw : 0f, skyRadiance ?? (_ => Vector3.Zero), options);
		baker.EnsureSurfaceCache(session);

		var atlases = new ProbeGiTextures(api, session.Result, "_probeGiFlicker", gpuWritable: true);

		// On a dense grid (hundreds of thousands of probes) software traversal takes seconds/round.
		using var pipelines = new ProbeRoundPipelines(api, hardware);
		using var accel = hardware ? new ProbeSceneAccel(api, baker.InstancedGeometry) : null;
		var gpu = new ProbeRoundGpu(api, pipelines, session, baker, atlases, environmentMap,
			withSky ? envYaw : 0f, accel);

		void RunOne()
		{
			while (!gpu.RunRound(session, baker,
				ProbeGiBaker.RoundRayDirections(session),
				ProbeGiBaker.RoundBlendWeight(session)))
			{
			}

			session.AdvanceRound();
		}

		// Settle: early rounds run at full weight and are still building the field.
		for (int i = 0; i < settleRounds; i++)
		{
			RunOne();
		}

		int skippedBefore = gpu.SkippedRounds;
		var previous = gpu.ReadField();
		double deltaSum = 0;
		float lumMin = float.MaxValue, lumMax = 0f;
		double lumSum = 0;
		int probes = session.ProbeCount;

		// Kept per probe and round: the max alone lies, the distribution is what the eye sees.
		var deltas = new List<float>(probes * measureRounds);

		for (int round = 0; round < measureRounds; round++)
		{
			RunOne();
			var current = gpu.ReadField();

			double roundDelta = 0, roundMagnitude = 0, roundLum = 0;
			for (int p = 0; p < probes; p++)
			{
				var before = previous[p * 4];
				var after = current[p * 4];
				var d = new Vector3(after.X - before.X, after.Y - before.Y, after.Z - before.Z);
				var magnitude = new Vector3(after.X, after.Y, after.Z);

				roundDelta += d.Length();
				roundMagnitude += magnitude.Length();
				roundLum += Luminance(magnitude);

				// Near-zero probes are excluded: a last-bit jitter there dominates the ratio.
				if (magnitude.Length() > 1e-2f)
				{
					deltas.Add(d.Length() / magnitude.Length());
				}
			}

			deltaSum += roundMagnitude > 1e-9 ? roundDelta / roundMagnitude : 0.0;
			float meanLum = (float)(roundLum / Math.Max(probes, 1));
			lumMin = MathF.Min(lumMin, meanLum);
			lumMax = MathF.Max(lumMax, meanLum);
			lumSum += meanLum;
			previous = current;
		}

		float variability = gpu.AverageVariability;
		int skippedRounds = gpu.SkippedRounds - skippedBefore;

		deltas.Sort();
		float Percentile(double q) => deltas.Count == 0
			? 0f
			: deltas[Math.Clamp((int)(q * (deltas.Count - 1)), 0, deltas.Count - 1)];

		float worst = deltas.Count > 0 ? deltas[^1] : 0f;
		float shareAbove10 = deltas.Count > 0
			? deltas.Count(d => d > 0.1f) / (float)deltas.Count
			: 0f;

		gpu.Dispose();
		atlases.Release();

		return new FlickerReport(probes, session.RaysPerRound,
			ProbeGiBaker.RoundBlendWeight(session), measureRounds,
			(float)(deltaSum / Math.Max(measureRounds, 1)), worst,
			lumMin, lumMax, (float)(lumSum / Math.Max(measureRounds, 1)),
			Percentile(0.50), Percentile(0.90), Percentile(0.99), shareAbove10,
			variability, measureRounds > 0 ? skippedRounds / (float)measureRounds : 0f);
	}

	private static float Luminance(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

	private static void Bind(IShaderResourceBinding srb, string name, IBuffer buffer)
	{
		// Explicit view rather than GetDefaultView; both work, but this one is less ambiguous.
		// Note: on D3D12, GetDimensions reports zero even for a perfectly valid binding.
		var desc = buffer.GetDesc();
		using var view = buffer.CreateView(new BufferViewDesc
		{
			Name = $"{desc.Name} SRV",
			ViewType = BufferViewType.ShaderResource,
			ByteOffset = 0,
			ByteWidth = desc.Size,
		});

		Require(srb, name).Set(view, SetShaderResourceFlags.AllowOverwrite);
	}

	// A swallowed name miss would leave a zero buffer and look like a traversal mismatch.
	private static IShaderResourceVariable Require(IShaderResourceBinding srb, string name) =>
		srb.GetVariableByName(ShaderType.Compute, name)
		?? throw new InvalidOperationException(
			$"Compute shader has no resource variable '{name}' - it was likely optimised out or renamed");

	private static unsafe IBuffer CreateStructured<T>(IRenderDevice device, string name, T[] data, int stride)
		where T : unmanaged
	{
		var desc = new BufferDesc
		{
			Name = name,
			// Default, not Immutable: on D3D12 an immutable structured buffer with initial data
			// reaches the shader as zero elements.
			Usage = Usage.Default,
			BindFlags = BindFlags.ShaderResource,
			Mode = BufferMode.Structured,
			ElementByteStride = (uint)stride,
			Size = (ulong)(data.Length * stride),
		};

		fixed (T* ptr = data)
		{
			return device.CreateBuffer(desc, new BufferData
			{
				Data = new IntPtr(ptr),
				DataSize = (ulong)(data.Length * stride),
			});
		}
	}

	private static float Frac(float v) => v - MathF.Floor(v);
}
