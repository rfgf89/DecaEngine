using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
using UnsafeCollections.Collections.Native;
using UnsafeCollections.Collections.Unsafe;

namespace DecaEngine.Graphics.ProbeGi;

/// <summary>Probe-GI bake quality options, edited in the Graphics window and persisted in EditorSettings; any change requires a rebake.</summary>
public sealed class ProbeGiBakeOptions
{
	/// <summary>Total rays per probe for session convergence; sets how many rounds count as unconverged, not the per-round cost.</summary>
	public int RaysPerProbe = ProbeGiBaker.DefaultRaysPerProbe;

	/// <summary>Rays per probe per round - the unit of background bake work; a round must fit in ~10 ms or progressiveness is lost.</summary>
	public int RaysPerRound = 16;

	/// <summary>Multi-bounce depth. The progressive bake gathers bounce from the CURRENT probe field (infinite feedback, as in DDGI); this damps it so total energy matches an N-bounce bake. 1 = sky + direct sun bounce only.</summary>
	public int Bounces = 2;

	/// <summary>Sky radiance multiplier in the bake (sky ambient brightness).</summary>
	public float SkyIntensity = 1f;

	/// <summary>Gather bounce from the surface radiance cache instead of re-evaluating the probe field at each hit; gives geometry-level detail at the cost of one shadow ray per voxel per round.</summary>
	public bool SurfaceCache = true;

	/// <summary>Fraction of albedo CHROMA fed into the bounce (0 = grey bounce, 1 = full color). Luma is never touched, so bounce strength off grey surfaces stays; compounds per bounce, since red-to-red feedback would amplify chroma to neon.</summary>
	public float BounceSaturation = 0.5f;

	/// <summary>Grid density: cells per largest scene extent (~22 by default).</summary>
	public float GridDensity = 22f;

	/// <summary>Probe count cap - guards against bake explosion on large scenes (cells grow until the grid fits). Clamped to [MinProbeBudget, MaxProbeBudget]; also cut by MaxProbesPerAxis and MaxAtlasDimension.</summary>
	public int MaxProbes = 8192;

	/// <summary>REALTIME mode: rounds never converge or stop, and the field accumulates with a constant-alpha exponential average instead of a running 1/(Round+1) mean. A running mean stops reacting by construction (round weight tends to zero) - right for baking, wrong for a dynamic scene, which needs constant responsiveness at the cost of residual noise. Toggling on a live session keeps the accumulated field.</summary>
	public bool Realtime = false;

	/// <summary>Rays per probe per round in realtime - separate from RaysPerRound because the error cost differs. All probes fire ONE fan per round (rotated between rounds), so estimate error is correlated grid-wide and reads as whole-scene brightness breathing; error goes as 1/sqrt(N). Measured on Sponza flicker: 16 rays 5.1%, 32 1.5%, 64 1.1% (visibility threshold), 128 0.5%.</summary>
	public int RealtimeRaysPerRound = 64;

	/// <summary>Mean volume-variability threshold below which rounds stop entirely (0 = off); port of RTXGI-DDGI "Probe Variability" (ProbeVariabilityCS.hlsl). Light changes and geometry motion reset the round weight and forbid stopping; a full round still runs every VariabilityRefreshPeriod rounds as insurance. Measured on Sponza at 128 rays: settled variability 0.058, threshold 0.08 skips 94% of rounds with unchanged field brightness.</summary>
	public float RealtimeVariabilityThreshold = 0.08f;

	/// <summary>Per-ray luminance cap in realtime (0 = off) - outlier suppression, same idea as EditorSettings.SsgiMaxLuminance. Probe variance comes from rare very bright hits (sun disk in an HDR panorama), which do not average out; direct sunlight is unaffected (it arrives as an analytic term, not a panorama sample). NOT applied to baking. Off by default per measurement: on Sponza with procedural sky, caps 4/2/1 change nothing and 0.5 costs 19% field brightness for p99 6.3%->6.0%.</summary>
	public float RealtimeMaxRayLuminance = 0f;

	/// <summary>Round weight in realtime - the exponential average alpha. Direct response-vs-stability tradeoff: disturbances decay as (1-alpha)^n, residual jitter goes as sqrt(alpha/(2-alpha)) of one round's variance.</summary>
	public float RealtimeBlend = ProbeGiBaker.RealtimeBlend;

	/// <summary>Per-round probe CHANGE limit as a fraction of its own brightness (0 = off). Unlike the round weight (a uniform time filter tuned to the worst probe), this only engages where the estimate jumps: a flickering probe degrades into lag instead of flashes. Cuts the derivative, not the value, so no energy loss. Scale uses the mean of old and new values - scaling by old alone would pin a zero-lit probe at zero forever.</summary>
	public float RealtimeMaxStep = 0.03f;

	/// <summary>Probe RELOCATION: how far a probe may leave its grid node, in fractions of the minimum grid step (0 = off). Standard DDGI cure for probes inside walls (they flicker and leak). A probe with many backface hits moves toward the largest free space its own rays measured; it returns to its node only after verifying there is room (see mainProbe in ProbeRoundCS.hlsl) to avoid oscillation. 0.45 max: past half a step the probe leaves its cell and trilinear interpolation lies more than it gains.</summary>
	public float RealtimeRelocation = 0.45f;

	/// <summary>PERCEPTUAL accumulation gamma (1 = linear, off); adaptation of Majercik 2021 section 4.2 (gamma 5.0) to SH. Irradiance cannot be stored as pow(E, 1/5) in SH (reconstruction is linear in coefficients), so the field stays linear and only the brightness TRAJECTORY follows the perceptual curve - one scalar over all four SH bands to preserve directionality. Linear averaging is additive while the eye is logarithmic: a 100x flash at alpha=0.04 moves a linear mean +396% but a perceptual one +34%; light-to-shadow transitions speed up. Cost: settled level biases slightly low on noisy estimates.</summary>
	public float RealtimeGamma = 5f;
}
