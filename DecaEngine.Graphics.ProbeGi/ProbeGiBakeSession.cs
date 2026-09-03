using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
using UnsafeCollections.Collections.Native;
using UnsafeCollections.Collections.Unsafe;

namespace DecaEngine.Graphics.ProbeGi;

/// <summary>Progressive probe-GI bake state; not thread-safe: run rounds one at a time.</summary>
public sealed class ProbeGiBakeSession
{
	/// <summary>Dense probe grid size: a probe exists at every node.</summary>
	public int CountX { get; }
	public int CountY { get; }
	public int CountZ { get; }

	internal int LayoutGeneration;

	public int ProbeCount { get; }
	public Vector3 Origin { get; internal set; }
	public Vector3 Cell { get; }

	/// <summary>Rounds accumulated since the last lighting change; sets round weight 1/(Round+1).</summary>
	public int Round { get; internal set; }

	/// <summary>Rounds until convergence; callers stop running rounds after it is reached.</summary>
	public int TargetRounds { get; }

	/// <summary>Realtime mode; togglable on a live session, accumulated field survives the switch.</summary>
	public bool Realtime { get; set; }

	/// <summary>Nothing to bake (empty BVH); rounds are pointless even in realtime.</summary>
	public bool NoGeometry { get; internal set; }

	/// <summary>Realtime exponential-average alpha; live knob.</summary>
	public float RealtimeBlend { get; set; } = ProbeGiBaker.RealtimeBlend;

	// Round-weight floor: near zero in bake (average must converge), fixed alpha in realtime.
	internal float MinBlend => Realtime
		? Math.Clamp(RealtimeBlend, 0.005f, 1f)
		: ProbeGiBaker.MinRoundBlend;

	/// <summary>Whether the field has converged; never true in realtime (except empty scenes).</summary>
	public bool Converged => NoGeometry || (!Realtime && Round >= TargetRounds);

	/// <summary>Convergence progress 0..1; always 1 in realtime.</summary>
	public float Progress => Realtime || TargetRounds <= 0
		? 1f
		: Math.Clamp(Round / (float)TargetRounds, 0f, 1f);

	private readonly int _bakeRaysPerRound;

	/// <summary>Realtime rays per round; live knob, direction buffer is sized for the cap.</summary>
	public int RealtimeRaysPerRound { get; set; }

	/// <summary>Rays for the current round; depends on mode.</summary>
	public int RaysPerRound => Realtime ? RealtimeRaysPerRound : _bakeRaysPerRound;

	/// <summary>How many leading fan rays are fixed (see ProbeGiBaker.FixedRayCount).</summary>
	public int FixedRays => ProbeGiBaker.FixedRayCount(RaysPerRound, Realtime);

	/// <summary>Realtime per-ray luminance cap; live knob.</summary>
	public float RealtimeMaxRayLuminance { get; set; }

	// Bake mode never clamps: it would lose energy and break the bitwise GPU/CPU parity check.
	internal float MaxRayLuminance => Realtime ? MathF.Max(RealtimeMaxRayLuminance, 0f) : 0f;

	/// <summary>Realtime per-round probe change limit; live knob.</summary>
	public float RealtimeMaxStep { get; set; }

	// Disabled in bake mode: it would only slow convergence.
	internal float MaxStep => Realtime ? MathF.Max(RealtimeMaxStep, 0f) : 0f;

	/// <summary>Relocation limit; live knob.</summary>
	public float RealtimeRelocation { get; set; }

	/// <summary>Perceptual accumulation gamma; live knob.</summary>
	public float RealtimeGamma { get; set; }

	// Bake mode is always linear: the average must converge to the true integral (and match CPU ref).
	internal float AccumulationGamma => Realtime ? Math.Clamp(RealtimeGamma, 1f, 8f) : 1f;

	// Relocation window opens once, at session init (Majercik 2021 §5).
	internal int RelocationRoundsLeft;

	// World-space relocation limit; disabled in bake, it would invalidate the accumulated field.
	internal float RelocationLimit => Realtime && RelocationRoundsLeft > 0
		? MathF.Max(RealtimeRelocation, 0f) * MathF.Min(Cell.X, MathF.Min(Cell.Y, Cell.Z))
		: 0f;

	/// <summary>Scene geometry moved; realtime must not reset round weight, or the field boils.</summary>
	public void InvalidateGeometry()
	{
		if (!Realtime)
		{
			Round = Math.Min(Round, ProbeGiBaker.RestartRound);
		}

		// Bump so ProbeRoundGpu lifts the converged-volume stop.
		GeometryVersion++;
	}

	// Incremented per InvalidateGeometry; ProbeRoundGpu compares against its snapshot.
	internal int GeometryVersion { get; private set; }

	internal void ConsumeRelocationRound()
	{
		if (RelocationRoundsLeft > 0)
		{
			RelocationRoundsLeft--;
		}
	}

	// Probe offsets from grid nodes, world units; CPU path accumulates here, GPU in its own buffer.
	internal readonly Vector3[] ProbeOffset;

	internal readonly float SkyIntensity, BounceSaturation, Feedback;

	// May change between rounds via SetLighting: sun rotation does not restart the bake.
	internal Vector3 SunDirection, SunColor;
	internal float EnvYaw;
	internal Func<Vector3, Vector3> SkyRadiance;

	// ShadowParams must be zero here: slice layout is per-frame and must not affect comparison.
	internal PunctualLight[] BakeLights = Array.Empty<PunctualLight>();

	/// <summary>Lifetime round counter, never reset; rotates the Fibonacci ray fan.</summary>
	public int Sequence { get; internal set; }

	// Double-buffered probe field: a round reads the previous field and writes the new one.
	internal Vector3[] L0R, L1XR, L1YR, L1ZR, L0W, L1XW, L1YW, L1ZW;
	internal float[] ValidityR, ValidityW, SunFracR, SunFracW;

	/// <summary>Read-side constant term (L0) of the probe field, as sampled right now.</summary>
	public ReadOnlySpan<Vector3> IrradianceRead => L0R;

	// Sky visibility: pure geometry, read only by the owning probe - single buffer.
	internal readonly float[] SkyVis;

	// Geometry accumulators: lighting-independent exact sums over ALL rounds; survive sun rotation.
	internal readonly int[] RayTotal, MissTotal, BackTotal;

	// VisWeight sums weights, not rays: depth splats over a cone lobe (Majercik 2019 §4.4).
	internal readonly float[] VisSumT, VisSumT2, VisWeight;

	/// <summary>Atlas buffers are reused across snapshots (tens of MB per round).</summary>
	public readonly ProbeGiBakeResult Result;

	/// <summary>Surface radiance cache feeding bounce rays; null until the first round builds it.</summary>
	public SurfaceCache? Surface { get; internal set; }

	// Surface cache requested but not yet built; capture costs hundreds of ms, deferred to a round.
	internal bool WantsSurfaceCache;

	internal ProbeGiBakeSession(Vector3 origin, Vector3 cell, int cx, int cy, int cz,
		ProbeGiBakeOptions options, Vector3 sunDirection, Vector3 sunColor,
		float envYawRadians, Func<Vector3, Vector3> skyRadiance, int targetRounds)
	{
		CountX = cx;
		CountY = cy;
		CountZ = cz;
		ProbeCount = cx * cy * cz;
		Origin = origin;
		Cell = cell;
		TargetRounds = targetRounds;

		_bakeRaysPerRound = Math.Clamp(options.RaysPerRound, 4, 128);
		RealtimeRaysPerRound = Math.Clamp(options.RealtimeRaysPerRound, 4, 1024);
		RealtimeMaxRayLuminance = MathF.Max(options.RealtimeMaxRayLuminance, 0f);
		RealtimeBlend = options.RealtimeBlend;
		RealtimeMaxStep = MathF.Max(options.RealtimeMaxStep, 0f);
		RealtimeRelocation = Math.Clamp(options.RealtimeRelocation, 0f, 0.45f);
		RealtimeGamma = Math.Clamp(options.RealtimeGamma, 1f, 8f);
		VariabilityThreshold = MathF.Max(options.RealtimeVariabilityThreshold, 0f);
		// Fresh session: probes sit at grid nodes, some inside walls - relocation window opens.
		RelocationRoundsLeft = ProbeGiBaker.RelocationRounds;
		Realtime = options.Realtime;
		SkyIntensity = Math.Clamp(options.SkyIntensity, 0f, 16f);
		BounceSaturation = Math.Clamp(options.BounceSaturation, 0f, 1f);
		Feedback = ProbeGiBaker.BounceFeedback(Math.Clamp(options.Bounces, 1, 6));

		SunDirection = Vector3.Normalize(sunDirection);
		SunColor = sunColor;
		EnvYaw = envYawRadians;
		SkyRadiance = skyRadiance;

		int n = ProbeCount;
		L0R = new Vector3[n]; L1XR = new Vector3[n]; L1YR = new Vector3[n]; L1ZR = new Vector3[n];
		L0W = new Vector3[n]; L1XW = new Vector3[n]; L1YW = new Vector3[n]; L1ZW = new Vector3[n];
		ValidityR = new float[n]; ValidityW = new float[n];
		SunFracR = new float[n]; SunFracW = new float[n];
		SkyVis = new float[n];
		ProbeOffset = new Vector3[n];
		RayTotal = new int[n]; MissTotal = new int[n]; BackTotal = new int[n];

		int visCells = n * ProbeGiBakeResult.VisRes * ProbeGiBakeResult.VisRes;
		VisSumT = new float[visCells];
		VisSumT2 = new float[visCells];
		VisWeight = new float[visCells];

		Result = new ProbeGiBakeResult
		{
			CountX = cx,
			CountY = cy,
			CountZ = cz,
			Origin = origin,
			Cell = cell,
		};

		Result.Sh0 = new byte[n * 8];
		Result.Sh1 = new byte[n * 8];
		Result.Sh2 = new byte[n * 8];
		Result.Sh3 = new byte[n * 8];
		Result.Offset = new byte[n * 8];
		Result.Vis = new byte[n * ProbeGiBakeResult.VisRes * ProbeGiBakeResult.VisRes * 8];
	}

	/// <summary>Updates lighting between rounds; a change rolls convergence back to RestartRound.</summary>
	public bool SetLighting(Vector3 sunDirection, Vector3 sunColor, float envYawRadians,
		Func<Vector3, Vector3> skyRadiance)
	{
		var dir = Vector3.Normalize(sunDirection);
		bool changed = (dir - SunDirection).LengthSquared() > 1e-10f
			|| (sunColor - SunColor).LengthSquared() > 1e-10f
			|| MathF.Abs(envYawRadians - EnvYaw) > 1e-6f;

		SunDirection = dir;
		SunColor = sunColor;
		EnvYaw = envYawRadians;
		SkyRadiance = skyRadiance;

		if (changed)
		{
			Round = Math.Min(Round, ProbeGiBaker.RestartRound);
		}

		return changed;
	}

	/// <summary>Updates punctual lights between rounds; ShadowParams must be zero in entries.</summary>
	public bool SetPunctualLights(ReadOnlySpan<PunctualLight> lights)
	{
		bool changed = lights.Length != BakeLights.Length;
		if (!changed)
		{
			for (int i = 0; i < lights.Length; i++)
			{
				ref readonly var a = ref lights[i];
				ref var b = ref BakeLights[i];
				if ((a.PositionRange - b.PositionRange).LengthSquared() > 1e-10f
					|| (a.ColorIntensity - b.ColorIntensity).LengthSquared() > 1e-10f
					|| (a.DirectionType - b.DirectionType).LengthSquared() > 1e-10f
					|| (a.SpotAngles - b.SpotAngles).LengthSquared() > 1e-10f)
				{
					changed = true;
					break;
				}
			}
		}

		if (changed)
		{
			BakeLights = lights.ToArray();
			Round = Math.Min(Round, ProbeGiBaker.RestartRound);
		}

		return changed;
	}

	/// <summary>Advances round counters when the GPU did the work; both must track the GPU path.</summary>
	public void AdvanceRound()
	{
		Sequence++;
		Round++;
		ConsumeRelocationRound();
	}

	/// <summary>Mean-variability threshold below which a volume counts as converged; live knob.</summary>
	public float VariabilityThreshold { get; set; }

	internal void Swap()
	{
		(L0R, L0W) = (L0W, L0R);
		(L1XR, L1XW) = (L1XW, L1XR);
		(L1YR, L1YW) = (L1YW, L1YR);
		(L1ZR, L1ZW) = (L1ZW, L1ZR);
		(ValidityR, ValidityW) = (ValidityW, ValidityR);
		(SunFracR, SunFracW) = (SunFracW, SunFracR);
	}
}

/// <summary>World-space sparse voxel surface radiance cache read by probe bake rays.</summary>
public sealed class SurfaceCache
{
	/// <summary>Voxel step subdivision relative to the probe grid step.</summary>
	public const int Subdivision = 4;

	public int CountX { get; }
	public int CountY { get; }
	public int CountZ { get; }
	public Vector3 Origin { get; }
	public Vector3 Voxel { get; }

	// Voxel index in dense arrays by grid coordinates; -1 = no surface here.
	private readonly int[] _index;

	/// <summary>Captured surface geometry; computed once, lighting-independent.</summary>
	public Vector3[] Position = Array.Empty<Vector3>();
	public Vector3[] Normal = Array.Empty<Vector3>();
	public Vector3[] Albedo = Array.Empty<Vector3>();

	/// <summary>Outgoing voxel radiance and its sun fraction, consumed by probe bake rays.</summary>
	public Vector3[] Radiance = Array.Empty<Vector3>();
	public float[] SunFraction = Array.Empty<float>();

	public int VoxelCount { get; private set; }

	internal SurfaceCache(Vector3 origin, Vector3 voxel, int cx, int cy, int cz)
	{
		Origin = origin;
		Voxel = voxel;
		CountX = cx;
		CountY = cy;
		CountZ = cz;
		_index = new int[cx * cy * cz];
	}

	/// <summary>Voxel index covering a world point, or -1; offset hits along the normal first.</summary>
	public int Lookup(Vector3 worldPos)
	{
		var f = (worldPos - Origin) / Voxel;
		int x = (int)MathF.Floor(f.X), y = (int)MathF.Floor(f.Y), z = (int)MathF.Floor(f.Z);
		if (x < 0 || y < 0 || z < 0 || x >= CountX || y >= CountY || z >= CountZ)
		{
			return -1;
		}

		return _index[(z * CountY + y) * CountX + x];
	}

	/// <summary>Dense cell-to-voxel-index map (-1 = empty), used by the GPU cache pass lookup.</summary>
	public int[] ExportIndex() => _index;

	internal void Allocate(int[] denseIndex, int voxelCount)
	{
		Array.Copy(denseIndex, _index, denseIndex.Length);
		VoxelCount = voxelCount;
		Position = new Vector3[voxelCount];
		Normal = new Vector3[voxelCount];
		Albedo = new Vector3[voxelCount];
		Radiance = new Vector3[voxelCount];
		SunFraction = new float[voxelCount];
	}
}
