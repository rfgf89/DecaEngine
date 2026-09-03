using System.Numerics;
using System.Runtime.InteropServices;
using DecaEngine.Core;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
using Diligent;

namespace DecaEngine.Graphics.ProbeGi;

/// <summary>GPU compute port of ProbeGiBaker.RunRound (see ProbeRoundCS.hlsl); the field buffer
/// uses the SH-atlas layout (four float4 per probe) so the shader writes atlases as UAVs.</summary>
public sealed class ProbeRoundGpu : IDisposable
{
	[StructLayout(LayoutKind.Sequential)]
	private struct RoundParams
	{
		public Vector4 GridOrigin;    // xyz grid corner, w round blend weight
		public Vector4 GridCell;      // xyz grid step, w max ray distance
		public Vector4 SunDirection;  // xyz toward sun, w rays per round
		public Vector4 SunColor;      // xyz sun color, w probe count
		public Vector4 Round;         // x epsilon, y visibility clamp, z gather offset, w feedback
		public Vector4 Chunk;         // x chunk first element, y one past last, z ray luminance cap,
		                              // w per-round probe change limit
		public Vector4 Relocation;    // x probe relocation limit in world units
		public Vector4 Rays;          // x count of fixed leading rays in the fan
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct GridParams
	{
		public Vector4 GridCounts;      // xyz dense probe grid size, w bounce saturation
		public Vector4 SurfaceVoxel;    // xyz cache voxel size, w live voxel count
		public Vector4 SurfaceCounts;   // xyz cache voxel grid size
		public Vector4 SkyParams;       // x env yaw, y sky intensity, z visibility octo-map side, w bricks per pool row
	}

	// Must stay in sync with ProbeGiBakeResult.VisRes and the shader's PROBE_VIS_RES cbuffer value.
	private static int VisRes => ProbeGiBakeResult.VisRes;
	// 1024 per Majercik 2021 §7.1; big fans stretch the round over more frames, not the frame time.
	private const int MaxRaysPerRound = 1024;

	// Probes per dispatch; a single full-grid dispatch starves presentation and removes the device.
	private static readonly int ProbesPerDispatch =
		int.TryParse(Environment.GetEnvironmentVariable("DECA_PROBE_CHUNK"), out var chunk) && chunk > 0
			? chunk
			: 2048;

	// Surface-cache chunk; voxels are cheaper than probes (one shadow ray vs sixteen primaries).
	private const int VoxelsPerDispatch = 16384;

	private readonly DiligentGraphicsApi _api;
	private readonly int _probeCount;

	private readonly IBuffer _bvhNodes, _bvhOrder, _bvhTriangles;
	private readonly IBuffer _rayDirections;

	// Buffer size cap; lights beyond this are silently dropped (no priority sort).
	private const int MaxBakeLights = 64;
	private readonly IBuffer _bakeLights;

	private readonly ProbeGiTextures? _atlases;
	private readonly IBuffer[] _field = new IBuffer[2];
	private readonly IBuffer _counters;
	private readonly IBuffer _offsets;
	private readonly IBuffer _visibility;

	// Variability: per-probe (round writes), group-reduced (ProbeVariabilityCS), CPU staging copy.
	private readonly IBuffer _variability;
	private readonly IBuffer _variabilitySum;
	private readonly IBuffer _variabilityStaging;
	private readonly IBuffer _variabilityParams;
	private readonly IPipelineState _variabilityPso;
	private IShaderResourceBinding? _variabilitySrb;

	// Must match PROBE_VARIABILITY_GROUPS in the shader.
	private const int VariabilityGroups = 64;

	// Rounds between reduction and readback; reading immediately stalls the frame (Flush+WaitForIdle).
	private const int VariabilityReadLag = 2;

	private int _variabilityPending = -1;
	private float _averageVariability = float.PositiveInfinity;

	// Snapshot of ProbeGiBakeSession.GeometryVersion; mismatch lifts the converged-volume skip.
	private int _geometryVersion = -1;

	/// <summary>Rounds skipped as converged (see IsConverged).</summary>
	public int SkippedRounds { get; private set; }

	/// <summary>Mean volume variability; infinity means not yet measured (treated as unconverged).</summary>
	public float AverageVariability => _averageVariability;
	// DECA_PROBE_FORCETRANS=1: full state transitions instead of Verify, to diagnose missed ones.
	private static readonly bool ForceTransitions =
		Environment.GetEnvironmentVariable("DECA_PROBE_FORCETRANS") == "1";

	// One buffer of 256-byte blocks (D3D12 cbuffer alignment) picked per dispatch by dynamic
	// offset: rebinding an SRB mid-recording is illegal on Vulkan.
	private const int ParamsStride = 256;
	private const int ParamsSlots = 64;
	private readonly IBuffer _params;
	private int _paramsSlot;

	private readonly IBuffer _gridParams;

	// CPU copy: scrolling edits three of eight vectors, but UpdateBuffer uploads the whole struct.
	private GridParams _gridParamsValue;

	// Surface cache: geometry captured once, radiance recomputed per round (mainSurface pass).
	private readonly IBuffer _surfaceIndex;
	private readonly IBuffer _surfacePosition;
	private readonly IBuffer _surfaceNormal;
	private readonly IBuffer _surfaceAlbedo;
	private readonly IBuffer _surfaceRadiance;
	private readonly int _surfaceVoxelCount;
	private readonly IPipelineState? _surfacePso;
	private readonly IShaderResourceBinding[] _surfaceSrb = new IShaderResourceBinding[2];

	private readonly ISamplerObject _environmentSampler;

	// Throttle fence: unthrottled round-per-frame outruns the GPU and ends in device removal.
	private readonly IFence _roundFence;
	private ulong _roundFenceValue;

	// Progress of the current round through its two passes (see RunRound).
	private int _surfaceChunkStart;
	private int _probeChunkStart;
	private RoundParams _roundParams;

	// Ray luminance cap for the current round; must be identical across all its chunks.
	private float _maxRayLuminance;
	private float _maxStep;

	// Atlases written by the round; must return to ShaderResource after dispatch (see RunRound).
	private readonly ITexture[] _atlasTextures = Array.Empty<ITexture>();

	private readonly IPipelineState _pso;

	// Two SRBs for field ping-pong; cheaper than rebinding resources every round.
	private readonly IShaderResourceBinding[] _srb = new IShaderResourceBinding[2];
	private int _writeIndex;
	private readonly List<IDeviceObject> _views = new();

	public unsafe ProbeRoundGpu(DiligentGraphicsApi api, ProbeRoundPipelines pipelines,
		ProbeGiBakeSession session, ProbeGiBaker baker, ProbeGiTextures? atlases = null,
		IGpuTexture? environmentMap = null, float envYaw = 0f, ProbeSceneAccel? accel = null)
	{
		_api = api;
		_probeCount = session.ProbeCount;
		Hardware = accel != null;

		var device = api.Device;
		var swPhase = System.Diagnostics.Stopwatch.StartNew();
		long msSurface = 0, msExport = 0, msBuffers = 0, msShaders = 0;

		msSurface = swPhase.ElapsedMilliseconds;
		swPhase.Restart();
		var (nodes, order, triangles) = baker.ExportBvh();
		msExport = swPhase.ElapsedMilliseconds;
		swPhase.Restart();
		var bvhNodes = CreateImmutable(device, "SceneBvhNodes", nodes, sizeof(BvhNodeGpu));
		var bvhOrder = CreateImmutable(device, "SceneBvhOrder", order, sizeof(uint));
		var bvhTriangles = CreateImmutable(device, "SceneBvhTriangles", triangles, sizeof(BvhTriangleGpu));
		_bvhNodes = bvhNodes;
		_bvhOrder = bvhOrder;
		_bvhTriangles = bvhTriangles;

		if (atlases is { GpuWritable: true })
		{
			_atlasTextures = new[]
				{ atlases.Sh0, atlases.Sh1, atlases.Sh2, atlases.Sh3, atlases.Vis, atlases.Offset }
				.OfType<DiligentGpuTexture>()
				.Select(t => t.Texture)
				.ToArray();
		}

		_roundFence = device.CreateFence(new FenceDesc { Name = "ProbeRoundFence" });

		// Linear Wrap to match the pixel shader; Clamp would band at the equirect horizontal seam.
		_environmentSampler = api.CreateSampler("_ProbeEnvMap_Sampler", TextureFilter.Linear,
			TextureAddress.Wrap, CompFunction.Always, Vector4.Zero);

		_atlases = atlases;

		// Accumulators must start at zero (running average); zeroed by command, not CPU upload.
		_field[0] = CreateRw<Vector4>(device, "ProbeFieldA", _probeCount * 4, sizeof(Vector4));
		_field[1] = CreateRw<Vector4>(device, "ProbeFieldB", _probeCount * 4, sizeof(Vector4));
		_counters = CreateRw<int>(device, "ProbeCounters", _probeCount * 4, sizeof(int) * 4);

		_offsets = CreateRw<Vector4>(device, "ProbeOffsets", _probeCount, sizeof(Vector4));
		_visibility = CreateRw<Vector4>(device, "ProbeVisibility",
			_probeCount * VisRes * VisRes, sizeof(Vector4));

		_variability = CreateRw<Vector2>(device, "ProbeVariability", _probeCount, sizeof(Vector2));
		_variabilitySum = CreateRw<Vector2>(device, "ProbeVariabilitySum", VariabilityGroups,
			sizeof(Vector2));

		// Persistent staging buffer: creating one per readback allocates driver memory every round.
		_variabilityStaging = device.CreateBuffer(new BufferDesc
		{
			Name = "ProbeVariabilityStaging",
			Usage = Usage.Staging,
			CPUAccessFlags = CpuAccessFlags.Read,
			BindFlags = BindFlags.None,
			Size = (ulong)(VariabilityGroups * sizeof(Vector2)),
		});

		_variabilityParams = device.CreateBuffer(new BufferDesc
		{
			Name = "ProbeVariabilityParams",
			Usage = Usage.Default,
			BindFlags = BindFlags.UniformBuffer,
			Size = 16,
		});

		// Default, not Dynamic - same reason as the ray-direction buffer below.
		_bakeLights = device.CreateBuffer(new BufferDesc
		{
			Name = "ProbeBakeLights",
			Usage = Usage.Default,
			BindFlags = BindFlags.ShaderResource,
			Mode = BufferMode.Structured,
			ElementByteStride = (uint)sizeof(PunctualLight),
			Size = (ulong)(MaxBakeLights * sizeof(PunctualLight)),
		});

		// Default, not Dynamic: dynamic storage moves on map and the view here would go stale.
		_rayDirections = device.CreateBuffer(new BufferDesc
		{
			Name = "ProbeRayDirections",
			Usage = Usage.Default,
			BindFlags = BindFlags.ShaderResource,
			Mode = BufferMode.Structured,
			ElementByteStride = (uint)sizeof(Vector4),
			Size = (ulong)(MaxRaysPerRound * sizeof(Vector4)),
		});

		// Default, not Dynamic: a dynamic buffer would hand every chunk dispatch the last write.
		_params = device.CreateBuffer(new BufferDesc
		{
			Name = "ProbeRoundParams",
			Usage = Usage.Default,
			BindFlags = BindFlags.UniformBuffer,
			Size = (ulong)(ParamsStride * ParamsSlots),
		});

		_gridParams = device.CreateBuffer(new BufferDesc
		{
			Name = "ProbeGridParams",
			Usage = Usage.Default,
			BindFlags = BindFlags.UniformBuffer,
			Size = (ulong)sizeof(GridParams),
		});

		// Surface-cache buffers always exist; with cache off, SurfaceVoxel.w = 0 and lookup returns -1.
		var surface = session.Surface;
		_surfaceVoxelCount = surface?.VoxelCount ?? 0;
		int surfaceSlots = Math.Max(_surfaceVoxelCount, 1);

		_surfaceIndex = CreateImmutable(device, "SurfaceIndex",
			surface != null ? surface.ExportIndex() : new int[1], sizeof(int));
		_surfacePosition = CreateImmutable(device, "SurfacePosition",
			ToVector4(surface?.Position, surfaceSlots), sizeof(Vector4));
		_surfaceNormal = CreateImmutable(device, "SurfaceNormal",
			ToVector4(surface?.Normal, surfaceSlots), sizeof(Vector4));
		_surfaceAlbedo = CreateImmutable(device, "SurfaceAlbedo",
			ToVector4(surface?.Albedo, surfaceSlots), sizeof(Vector4));
		_surfaceRadiance = CreateRw<Vector4>(device, "SurfaceRadiance", surfaceSlots, sizeof(Vector4));

		_gridParamsValue = new GridParams
		{
			GridCounts = new Vector4(session.CountX, session.CountY, session.CountZ,
				session.BounceSaturation),
			SurfaceVoxel = surface != null
				? new Vector4(surface.Voxel, _surfaceVoxelCount)
				: Vector4.Zero,
			SurfaceCounts = surface != null
				? new Vector4(surface.CountX, surface.CountY, surface.CountZ, 0f)
				: Vector4.Zero,
			// z: visibility octahedral-map side (see ProbeGiBakeResult.VisRes).
			SkyParams = new Vector4(envYaw, session.SkyIntensity, VisRes, 0f),
		};

		api.ImmediateContext.UpdateBuffer<GridParams>(_gridParams, 0,
			new ReadOnlySpan<GridParams>(in _gridParamsValue), ResourceStateTransitionMode.Transition);

		ClearBuffers(api.ImmediateContext);

		msBuffers = swPhase.ElapsedMilliseconds;
		swPhase.Restart();

		// Pipelines outlive the session: compiling costs ~650 ms, sessions recreate per slider tick.
		_pso = pipelines.Round;
		_surfacePso = _surfaceVoxelCount > 0 ? pipelines.Surface : null;

		_variabilityPso = pipelines.Variability;
		_variabilitySrb = _variabilityPso.CreateShaderResourceBinding(true);
		TryBind(_variabilitySrb, "_ProbeVariability", _variability, BufferViewType.ShaderResource);
		TryBind(_variabilitySrb, "_ProbeVariabilitySum", _variabilitySum,
			BufferViewType.UnorderedAccess);
		_variabilitySrb.GetVariableByName(ShaderType.Compute, "ProbeVariabilityParams")
			?.Set(_variabilityParams, SetShaderResourceFlags.None);

		for (int i = 0; i < 2; i++)
		{
			// Surface pass also reads the probe field, so it gets its own ping-pong SRB pair.
			if (_surfacePso != null)
			{
				var surfaceSrb = _surfacePso.CreateShaderResourceBinding(true);
				_surfaceSrb[i] = surfaceSrb;
				BindSceneAndSurface(surfaceSrb, bvhNodes, bvhOrder, bvhTriangles, i);
				TryBind(surfaceSrb, "_SurfaceRadiance", _surfaceRadiance, BufferViewType.UnorderedAccess);
				TryBind(surfaceSrb, "_ProbeBakeLights", _bakeLights, BufferViewType.ShaderResource);

				// Surface pass traces rays too (shadow ray per voxel), so it needs the TLAS as well.
				BindAccel(surfaceSrb, accel);
			}

			var srb = _pso.CreateShaderResourceBinding(true);
			_srb[i] = srb;

			// Software and hardware shader variants use disjoint scene bindings: all optional.
			TryBind(srb, "_SceneBvhNodes", bvhNodes, BufferViewType.ShaderResource);
			TryBind(srb, "_SceneBvhOrder", bvhOrder, BufferViewType.ShaderResource);
			TryBind(srb, "_SceneBvhTriangles", bvhTriangles, BufferViewType.ShaderResource);
			BindSrv(srb, "_ProbeRayDirections", _rayDirections);
			TryBind(srb, "_ProbeBakeLights", _bakeLights, BufferViewType.ShaderResource);

			// Ping-pong: write _field[i], read the other.
			BindSrv(srb, "_ProbeFieldRead", _field[1 - i]);
			BindUav(srb, "_ProbeField", _field[i]);
			BindUav(srb, "_ProbeCounters", _counters);
			BindUav(srb, "_ProbeVisibility", _visibility);
			BindUav(srb, "_ProbeOffsets", _offsets);
			BindUav(srb, "_ProbeVariability", _variability);

			// Probe round only reads the cache; unused voxel geometry is compiled out, so optional.
			TryBind(srb, "_SurfaceIndex", _surfaceIndex, BufferViewType.ShaderResource);
			TryBind(srb, "_SurfacePosition", _surfacePosition, BufferViewType.ShaderResource);
			TryBind(srb, "_SurfaceNormal", _surfaceNormal, BufferViewType.ShaderResource);
			TryBind(srb, "_SurfaceAlbedo", _surfaceAlbedo, BufferViewType.ShaderResource);
			TryBind(srb, "_SurfaceRadiance", _surfaceRadiance, BufferViewType.UnorderedAccess);

			if (atlases is { GpuWritable: true })
			{
				TryBindTexture(srb, "_ProbeAtlasSh0", atlases.Sh0);
				TryBindTexture(srb, "_ProbeAtlasSh1", atlases.Sh1);
				TryBindTexture(srb, "_ProbeAtlasSh2", atlases.Sh2);
				TryBindTexture(srb, "_ProbeAtlasSh3", atlases.Sh3);
				TryBindTexture(srb, "_ProbeAtlasVis", atlases.Vis);
				TryBindTexture(srb, "_ProbeAtlasOffset", atlases.Offset);
			}

			BindAccel(srb, accel);
			BindEnvironment(srb, environmentMap, _environmentSampler);
			// Range is ONE block: the dynamic offset is added to it and would run past the buffer end.
		Require(srb, "ProbeRoundParams")
			.SetBufferRange(_params, 0, ParamsStride, 0, SetShaderResourceFlags.AllowOverwrite);
			Require(srb, "ProbeGridParams").Set(_gridParams, SetShaderResourceFlags.AllowOverwrite);
		}

		msShaders = swPhase.ElapsedMilliseconds;
		SetupTiming = (msSurface, msExport, msBuffers, msShaders);
	}

	/// <summary>Setup cost per phase (ms); all of it runs synchronously on the render thread.</summary>
	public (long SurfaceCapture, long BvhExport, long Buffers, long Shaders) SetupTiming { get; }

	// Binding survives external TLAS rebuilds: the descriptor points at the TLAS, not its contents.
	private void BindAccel(IShaderResourceBinding srb, ProbeSceneAccel? accel)
	{
		if (accel == null)
		{
			return;
		}

		srb.GetVariableByName(ShaderType.Compute, "_SceneTlas")
			?.Set(accel.Tlas, SetShaderResourceFlags.AllowOverwrite);
		TryBind(srb, "_SceneMeshTriangles", accel.MeshTriangles, BufferViewType.ShaderResource);
		TryBind(srb, "_SceneInstances", accel.Instances, BufferViewType.ShaderResource);
	}

	// Env map is optional: without it the shader still compiles but miss rays get a black sky.
	private static void BindEnvironment(IShaderResourceBinding srb, IGpuTexture? map,
		ISamplerObject? sampler)
	{
		if (map is not DiligentGpuTexture texture)
		{
			return;
		}

		var variable = srb.GetVariableByName(ShaderType.Compute, "_EnvMap");
		variable?.Set(texture.GetView(TextureViewType.ShaderResource),
			SetShaderResourceFlags.AllowOverwrite);

		if (sampler is DiligentSamplerObject diligentSampler)
		{
			srb.GetVariableByName(ShaderType.Compute, "_EnvMap_sampler")
				?.Set(diligentSampler.Sampler, SetShaderResourceFlags.AllowOverwrite);
		}
	}

	private static void TryBindTexture(IShaderResourceBinding srb, string name, IGpuTexture texture)
	{
		if (texture is not DiligentGpuTexture diligentTexture)
		{
			return;
		}

		srb.GetVariableByName(ShaderType.Compute, name)
			?.Set(diligentTexture.GetView(TextureViewType.UnorderedAccess),
				SetShaderResourceFlags.AllowOverwrite);
	}

	// All bindings optional: the two entry points of one file use different resource subsets.
	private void BindSceneAndSurface(IShaderResourceBinding srb, IBuffer bvhNodes, IBuffer bvhOrder,
		IBuffer bvhTriangles, int writeIndex)
	{
		TryBind(srb, "_SceneBvhNodes", bvhNodes, BufferViewType.ShaderResource);
		TryBind(srb, "_SceneBvhOrder", bvhOrder, BufferViewType.ShaderResource);
		TryBind(srb, "_SceneBvhTriangles", bvhTriangles, BufferViewType.ShaderResource);
		TryBind(srb, "_ProbeRayDirections", _rayDirections, BufferViewType.ShaderResource);
		TryBind(srb, "_ProbeFieldRead", _field[1 - writeIndex], BufferViewType.ShaderResource);
		TryBind(srb, "_ProbeField", _field[writeIndex], BufferViewType.UnorderedAccess);
		TryBind(srb, "_ProbeCounters", _counters, BufferViewType.UnorderedAccess);
		TryBind(srb, "_ProbeVisibility", _visibility, BufferViewType.UnorderedAccess);
		// ProbeGatherIrradiance uses neighbour offsets, so the buffer is needed here too.
		TryBind(srb, "_ProbeOffsets", _offsets, BufferViewType.UnorderedAccess);
		TryBind(srb, "_SurfaceIndex", _surfaceIndex, BufferViewType.ShaderResource);
		TryBind(srb, "_SurfacePosition", _surfacePosition, BufferViewType.ShaderResource);
		TryBind(srb, "_SurfaceNormal", _surfaceNormal, BufferViewType.ShaderResource);
		TryBind(srb, "_SurfaceAlbedo", _surfaceAlbedo, BufferViewType.ShaderResource);
		// Actual offset is set per dispatch (see Dispatch); this just keeps the binding non-empty.
		srb.GetVariableByName(ShaderType.Compute, "ProbeRoundParams")
			?.SetBufferRange(_params, 0, ParamsStride, 0, SetShaderResourceFlags.AllowOverwrite);
		srb.GetVariableByName(ShaderType.Compute, "ProbeGridParams")
			?.Set(_gridParams, SetShaderResourceFlags.AllowOverwrite);
	}

	private void TryBind(IShaderResourceBinding srb, string name, IBuffer buffer, BufferViewType type)
	{
		var variable = srb.GetVariableByName(ShaderType.Compute, name);
		if (variable == null)
		{
			return;
		}

		var view = buffer.CreateView(new BufferViewDesc
		{
			Name = $"{buffer.GetDesc().Name} {type}",
			ViewType = type,
			ByteOffset = 0,
			ByteWidth = buffer.GetDesc().Size,
		});

		_views.Add(view);
		variable.Set(view, SetShaderResourceFlags.AllowOverwrite);
	}

	private static Vector4[] ToVector4(Vector3[]? source, int slots)
	{
		var result = new Vector4[slots];
		if (source == null)
		{
			return result;
		}

		for (int i = 0; i < source.Length && i < slots; i++)
		{
			result[i] = new Vector4(source[i], 0f);
		}

		return result;
	}

	/// <summary>True when built on hardware ray tracing (TLAS); ~2 orders cheaper per chunk.</summary>
	public bool Hardware { get; private set; }

	// Per-frame ray budget shared by all volumes: ~1M rays/ms hardware, ~50x slower software.
	private const int HardwareRayBudgetPerFrame = 4_000_000;
	private const int SoftwareRayBudgetPerFrame = 200_000;

	/// <summary>Chunks of this volume to issue per frame for the given fan and volume count (min 1).</summary>
	public int ChunksPerFrame(int raysPerRound, int volumeCount = 1)
	{
		long budget = (Hardware ? HardwareRayBudgetPerFrame : SoftwareRayBudgetPerFrame)
			/ Math.Max(volumeCount, 1);
		long raysPerChunk = (long)ProbesPerDispatch * Math.Max(raysPerRound, 8);
		return (int)Math.Clamp(budget / Math.Max(raysPerChunk, 1), 1, 32);
	}

	// Caps queued GPU work; 6 in flight showed frame-object timeouts, change only with measurements.
	private ulong MaxRoundsInFlight => Hardware ? 4UL : 2UL;

	/// <summary>False once <see cref="MaxRoundsInFlight"/> unfinished rounds are queued.</summary>
	public bool IsReady
	{
		get
		{
			// Subtraction, not "completed >= value - N": unsigned values underflow on early rounds.
			var completed = _roundFence.GetCompletedValue();
			return completed >= _roundFenceValue || _roundFenceValue - completed < MaxRoundsInFlight;
		}
	}

	/// <summary>No chunk issued yet - the only safe moment to rebuild the TLAS.</summary>
	public bool AtRoundStart => _surfaceChunkStart == 0 && _probeChunkStart == 0;

	/// <summary>Advances the round by one chunk; returns true only when the round completes.
	/// Ray directions and blend weight must stay constant within a round.</summary>
	public unsafe bool RunRound(ProbeGiBakeSession session, ProbeGiBaker baker,
		Vector3[] rayDirections, float alpha)
	{
		var context = _api.ImmediateContext;

		// Convergence check only at a round boundary: a started round must finish or probes mix fans.
		if (_surfaceChunkStart == 0 && _probeChunkStart == 0 && IsConverged(session))
		{
			SkippedRounds++;
			return true;
		}

		// Directions and params upload once per round: all chunks must see the same fan.
		if (_surfaceChunkStart == 0 && _probeChunkStart == 0)
		{
			var dirs = new Vector4[MaxRaysPerRound];
			for (int i = 0; i < rayDirections.Length; i++)
			{
				dirs[i] = new Vector4(rayDirections[i], 0f);
			}

			context.UpdateBuffer<Vector4>(_rayDirections, 0, dirs.AsSpan(),
				ResourceStateTransitionMode.Transition);

			// Uploaded every round without diffing (tiny buffer); tail beyond MaxBakeLights dropped.
			int lightCount = Math.Min(session.BakeLights.Length, MaxBakeLights);
			if (lightCount > 0)
			{
				context.UpdateBuffer<PunctualLight>(_bakeLights, 0,
					session.BakeLights.AsSpan(0, lightCount), ResourceStateTransitionMode.Transition);
			}

			_maxRayLuminance = session.MaxRayLuminance;
			_maxStep = session.MaxStep;
			_roundParams = new RoundParams
			{
				GridOrigin = new Vector4(session.Origin, alpha),
				GridCell = new Vector4(session.Cell, baker.RayTMax),
				SunDirection = new Vector4(session.SunDirection, rayDirections.Length),
				SunColor = new Vector4(session.SunColor, _probeCount),
				Round = new Vector4(baker.SceneEpsilon, session.Cell.Length() * 1.5f,
					session.Cell.Length() * 0.05f, session.Feedback),
				Relocation = new Vector4(session.RelocationLimit, session.AccumulationGamma,
					session.Realtime ? 1f : 0f,
					// Probes sleep only in settled realtime; otherwise it reads as popcorn.
					session.Realtime && session.RelocationRoundsLeft == 0
						&& alpha <= session.MinBlend * 1.001f && alpha <= 0.05f
						? 1f + (session.Sequence & 3)
						: 0f),
				// x = fixed leading rays (must match the uploaded fan), y = punctual light count.
				Rays = new Vector4(session.FixedRays, lightCount, 0f, 0f),
			};
		}

		// Surface cache must finish before the first probe chunk: probe rays read its radiance.
		if (_surfacePso != null && !session.Realtime && _surfaceChunkStart < _surfaceVoxelCount)
		{
			int end = Math.Min(_surfaceChunkStart + VoxelsPerDispatch, _surfaceVoxelCount);
			Dispatch(context, _surfacePso, _surfaceSrb[_writeIndex], _surfaceChunkStart, end,
				firstOfPass: _surfaceChunkStart == 0);
			_surfaceChunkStart = end;
			return false;
		}

		int probeEnd = Math.Min(_probeChunkStart + ProbesPerDispatch, _probeCount);
		Dispatch(context, _pso, _srb[_writeIndex], _probeChunkStart, probeEnd,
			firstOfPass: _probeChunkStart == 0);
		_probeChunkStart = probeEnd;

		if (_probeChunkStart < _probeCount)
		{
			return false;
		}

		// Reduction strictly after the last chunk: it reads the whole buffer at once.
		if (session.Realtime)
		{
			RunVariability(context, session);
		}

		// Atlases stay UnorderedAccess after dispatch; materials need them back in ShaderResource.
		if (_atlasTextures.Length > 0)
		{
			var transitions = new StateTransitionDesc[_atlasTextures.Length];
			for (int i = 0; i < _atlasTextures.Length; i++)
			{
				transitions[i] = new StateTransitionDesc
				{
					Resource = _atlasTextures[i],
					// global:: required: DecaEngine.Graphics.Diligent shadows the SDK namespace here.
					OldState = global::Diligent.ResourceState.UnorderedAccess,
					NewState = global::Diligent.ResourceState.ShaderResource,
					Flags = StateTransitionFlags.UpdateState,
				};
			}

			context.TransitionResourceStates(transitions);
		}

		SignalRound(context);

		// Round done: swap read/write field (mirrors ProbeGiBakeSession.Swap), reset chunk cursors.
		_writeIndex = 1 - _writeIndex;
		_surfaceChunkStart = 0;
		_probeChunkStart = 0;
		return true;
	}

	private void Dispatch(IDeviceContext context, IPipelineState pso, IShaderResourceBinding srb,
		int start, int end, bool firstOfPass)
	{
		_roundParams.Chunk = new Vector4(start, end, _maxRayLuminance, _maxStep);
		var chunkParams = _roundParams;

		// Dynamic offset, not overwriting one block: overwriting serializes dispatches.
		Require(srb, "ProbeRoundParams").SetBufferOffset((uint)(_paramsSlot * ParamsStride), 0);
		context.UpdateBuffer<RoundParams>(_params, (ulong)(_paramsSlot * ParamsStride),
			new ReadOnlySpan<RoundParams>(in chunkParams), ResourceStateTransitionMode.Transition);
		_paramsSlot = (_paramsSlot + 1) % ParamsSlots;

		context.SetPipelineState(pso);

		// Transitions only at pass boundaries: per-dispatch UAV barriers serialize dispatches.
		context.CommitShaderResources(srb, firstOfPass || ForceTransitions
			? ResourceStateTransitionMode.Transition
			: ResourceStateTransitionMode.Verify);
		context.DispatchCompute(new DispatchComputeAttribs
		{
			ThreadGroupCountX = (uint)((end - start + 63) / 64),
			ThreadGroupCountY = 1,
			ThreadGroupCountZ = 1,
		});
	}

	// Fence per ROUND, not per chunk: per-chunk fences would kill CPU/GPU overlap.
	private void SignalRound(IDeviceContext context) =>
		context.EnqueueSignal(_roundFence, ++_roundFenceValue);

	/// <summary>Reads the probe field back for CPU-reference verification.</summary>
	public unsafe Vector4[] ReadField()
	{
		// After the swap, the last-written buffer is the readable one (see RunRound).
		var field = new Vector4[_probeCount * 4];
		fixed (Vector4* dst = field)
		{
			_api.ImmediateContext.ReadBufferExt<Vector4>(_api.Device, _field[1 - _writeIndex], dst,
				(uint)(field.Length * sizeof(Vector4)));
		}

		return field;
	}

	private void BindSrv(IShaderResourceBinding srb, string name, IBuffer buffer)
	{
		var view = buffer.CreateView(new BufferViewDesc
		{
			Name = $"{buffer.GetDesc().Name} SRV",
			ViewType = BufferViewType.ShaderResource,
			ByteOffset = 0,
			ByteWidth = buffer.GetDesc().Size,
		});

		_views.Add(view);
		Require(srb, name).Set(view, SetShaderResourceFlags.AllowOverwrite);
	}

	private void BindUav(IShaderResourceBinding srb, string name, IBuffer buffer)
	{
		var view = buffer.CreateView(new BufferViewDesc
		{
			Name = $"{buffer.GetDesc().Name} UAV",
			ViewType = BufferViewType.UnorderedAccess,
			ByteOffset = 0,
			ByteWidth = buffer.GetDesc().Size,
		});

		_views.Add(view);
		Require(srb, name).Set(view, SetShaderResourceFlags.AllowOverwrite);
	}

	private static IShaderResourceVariable Require(IShaderResourceBinding srb, string name) =>
		srb.GetVariableByName(ShaderType.Compute, name)
		?? throw new InvalidOperationException(
			$"Shader variable '{name}' not found in ProbeRoundCS - renamed or optimised away");

	private static unsafe IBuffer CreateImmutable<T>(IRenderDevice device, string name, T[] data, int stride)
		where T : unmanaged
	{
		fixed (T* ptr = data)
		{
			return device.CreateBuffer(new BufferDesc
			{
				Name = name,
				Usage = Usage.Immutable,
				BindFlags = BindFlags.ShaderResource,
				Mode = BufferMode.Structured,
				ElementByteStride = (uint)stride,
				Size = (ulong)((long)data.Length * sizeof(T)),
			}, new BufferData
			{
				Data = new IntPtr(ptr),
				DataSize = (ulong)((long)data.Length * sizeof(T)),
			});
		}
	}

	// Updatable structured buffer: a scrolling volume's brick layout changes (see SyncScrollState).
	private static unsafe IBuffer CreateUpdatable<T>(IRenderDevice device, string name, T[] data,
		int stride)
		where T : unmanaged
	{
		fixed (T* ptr = data)
		{
			return device.CreateBuffer(new BufferDesc
			{
				Name = name,
				Usage = Usage.Default,
				BindFlags = BindFlags.ShaderResource,
				Mode = BufferMode.Structured,
				ElementByteStride = (uint)stride,
				Size = (ulong)((long)data.Length * sizeof(T)),
			}, new BufferData
			{
				Data = new IntPtr(ptr),
				DataSize = (ulong)((long)data.Length * sizeof(T)),
			});
		}
	}

	// Forced full recompute every N rounds: caps stale-stop latency (~0.5 s) for ~3% extra work.
	private const int VariabilityRefreshPeriod = 32;

	private bool IsConverged(ProbeGiBakeSession session)
	{
		// Geometry changes do not raise blend weight in realtime, so reset variability here.
		if (_geometryVersion != session.GeometryVersion)
		{
			_geometryVersion = session.GeometryVersion;
			_averageVariability = float.PositiveInfinity;
			return false;
		}

		if (!session.Realtime || session.VariabilityThreshold <= 0f)
		{
			return false;
		}

		if (session.RelocationRoundsLeft > 0
			|| ProbeGiBaker.RoundBlendWeight(session) > session.MinBlend * 1.001f)
		{
			return false;
		}

		if (session.Sequence % VariabilityRefreshPeriod == 0)
		{
			return false;
		}

		return _averageVariability < session.VariabilityThreshold;
	}

	// Reads the reduction queued several rounds ago, then queues a new one; skips if not ready.
	private unsafe void RunVariability(IDeviceContext context, ProbeGiBakeSession session)
	{
		if (_variabilityPending >= 0 && session.Sequence - _variabilityPending >= VariabilityReadLag)
		{
			var mapped = context.MapBuffer(_variabilityStaging, MapType.Read, MapFlags.DoNotWait);
			if (mapped != IntPtr.Zero)
			{
				try
				{
					var groups = new ReadOnlySpan<Vector2>(mapped.ToPointer(), VariabilityGroups);
					float sum = 0f, weight = 0f;
					for (int i = 0; i < groups.Length; i++)
					{
						sum += groups[i].X;
						weight += groups[i].Y;
					}

					// Zero weight means "nothing to measure", not "converged": keep infinity.
					_averageVariability = weight > 0f ? sum / weight : float.PositiveInfinity;
					_variabilityPending = -1;
				}
				finally
				{
					context.UnmapBuffer(_variabilityStaging, MapType.Read);
				}
			}
		}

		if (_variabilitySrb == null)
		{
			return;
		}

		var varParams = new Vector4(_probeCount, 0f, 0f, 0f);
		context.UpdateBuffer<Vector4>(_variabilityParams, 0,
			new ReadOnlySpan<Vector4>(in varParams), ResourceStateTransitionMode.Transition);

		context.SetPipelineState(_variabilityPso);
		context.CommitShaderResources(_variabilitySrb, ResourceStateTransitionMode.Transition);
		context.DispatchCompute(new DispatchComputeAttribs
		{
			ThreadGroupCountX = VariabilityGroups,
			ThreadGroupCountY = 1,
			ThreadGroupCountZ = 1,
		});

		// Staging copy runs after the reduction in the same queue, read VariabilityReadLag later.
		if (_variabilityPending < 0)
		{
			context.CopyBuffer(_variabilitySum, 0, ResourceStateTransitionMode.Transition,
				_variabilityStaging, 0, (ulong)(VariabilityGroups * sizeof(Vector2)),
				ResourceStateTransitionMode.Transition);
			_variabilityPending = session.Sequence;
		}
	}

	// No initial data: BufferData zero-fill drags tens of MB in-frame; ClearBuffers does it instead.
	private static unsafe IBuffer CreateRw<T>(IRenderDevice device, string name, int count, int stride)
		where T : unmanaged
	{
		return device.CreateBuffer(new BufferDesc
		{
			Name = name,
			Usage = Usage.Default,
			BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
			Mode = BufferMode.Structured,
			ElementByteStride = (uint)stride,
			Size = (ulong)((long)count * sizeof(T)),
		});
	}

	// Zero upload because these Diligent bindings lack FillBufferRegion.
	private static unsafe void ClearBuffer<T>(IDeviceContext context, IBuffer buffer, int count)
		where T : unmanaged
	{
		var zeros = new T[count];
		fixed (T* ptr = zeros)
		{
			context.UpdateBuffer(buffer, 0, (ulong)((long)count * sizeof(T)), new IntPtr(ptr),
				ResourceStateTransitionMode.Transition);
		}
	}

	private void ClearBuffers(IDeviceContext context)
	{
		ClearBuffer<Vector4>(context, _field[0], _probeCount * 4);
		ClearBuffer<Vector4>(context, _field[1], _probeCount * 4);
		ClearBuffer<int>(context, _counters, _probeCount * 4);
		ClearBuffer<Vector4>(context, _offsets, _probeCount);
		ClearBuffer<Vector4>(context, _visibility, _probeCount * VisRes * VisRes);
		ClearBuffer<Vector2>(context, _variability, _probeCount);
		ClearBuffer<Vector2>(context, _variabilitySum, VariabilityGroups);
	}

	public void Dispose()
	{
		foreach (var view in _views)
		{
			view.Dispose();
		}

		foreach (var srb in _srb)
		{
			srb.Dispose();
		}

		// _pso/_surfacePso belong to ProbeRoundPipelines and outlive this object.
		_gridParams.Dispose();
		_bakeLights.Dispose();
		_params.Dispose();

		_variabilitySrb?.Dispose();
		_variabilityParams.Dispose();
		_variabilityStaging.Dispose();
		_variabilitySum.Dispose();
		_variability.Dispose();
		_rayDirections.Dispose();
		_visibility.Dispose();
		_counters.Dispose();
		_field[0].Dispose();
		_field[1].Dispose();
		_bvhTriangles.Dispose();
		_bvhOrder.Dispose();
		_bvhNodes.Dispose();
		_roundFence.Dispose();
		_environmentSampler.Release();
	}
}
