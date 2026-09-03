using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
using UnsafeCollections.Collections.Native;
using UnsafeCollections.Collections.Unsafe;

namespace DecaEngine.Graphics.ProbeGi;


/// <summary>Lightweight DDGI bake: an SH L1 irradiance probe grid + sky visibility, CPU-traced over
/// the loaded model and packed into four RGBA16F 2D atlases (Z slices stacked vertically) that the
/// shader samples manually trilinearly (see UnlitInstancedPS.hlsl, SampleProbeGi).</summary>
public sealed class ProbeGiBakeResult
{
	/// <summary>Probe grid size. The grid is DENSE: a probe exists at every node, including
	/// inside walls and in open sky (measured cheaper than the sparse brick pool it replaced).</summary>
	public int CountX, CountY, CountZ;
	public Vector3 Origin;
	public Vector3 Cell;

	/// <summary>SH atlas layout: width = grid X axis, height = Z planes stacked vertically with
	/// rows running along Y (a column, not Texture2DArray, so consumers keep the texture type).</summary>
	public int ShWidth => CountX;
	public int ShHeight => CountZ * CountY;

	/// <summary>Probe count of the grid; also the SH atlas texel count.</summary>
	public int ProbeCount => CountX * CountY * CountZ;

	/// <summary>RGBA16F atlases. Sh0: rgb = SH L0 radiance, a = sky visibility. Sh1..3: rgb = SH L1
	/// x/y/z, a(Sh1) = probe validity (0 = inside geometry, do not interpolate).</summary>
	public byte[] Sh0, Sh1, Sh2, Sh3;

	/// <summary>Relocation atlas, RGBA16F: rgb = probe offset from its grid node in WORLD units,
	/// a = 1 for active probes. The shader must see the offset: trilinear weights and the Chebyshev
	/// test measure distance to the PROBE, not the node. Zeros in baked mode.</summary>
	public byte[] Offset;

	/// <summary>Octahedral resolution of the per-probe visibility map (see <see cref="Vis"/>).
	/// A knob, not a const: raising it only helps together with more rays per probe, and atlases
	/// plus both shaders read it, so it may change only when the session is recreated.</summary>
	public static int VisRes { get; set; } = DefaultVisRes;

	public const int DefaultVisRes = 8;
	public const int MinVisRes = 8;
	public const int MaxVisRes = 24;

	/// <summary>DDGI visibility atlas, RGBA16F, VisRes x VisRes octahedral texels per probe:
	/// r = mean distance to geometry, g = mean squared distance (Chebyshev leak test).</summary>
	public byte[] Vis;
}
