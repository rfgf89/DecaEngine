using System.Numerics;
using DecaEngine.Graphics.ProbeGi;

namespace DecaEngine.Tests.ProbeGi;

// Probe GI ray fan: the CPU baker and ProbeRoundCS must build it ray for ray alike.
public class ProbeRayDirectionTests
{
	private const float Tolerance = 1e-4f;

	[Theory]
	[InlineData(16, 0)]
	[InlineData(64, 0)]
	[InlineData(288, 7)]
	public void RoundRayDirections_ReturnsRequestedCountOfUnitVectors(int rays, int sequence)
	{
		var dirs = ProbeGiBaker.RoundRayDirections(rays, sequence);

		Assert.Equal(rays, dirs.Length);
		Assert.All(dirs, d => Assert.Equal(1f, d.Length(), Tolerance));
	}

	[Fact]
	public void RoundRayDirections_FibonacciFan_IsCentredOnTheSphere()
	{
		var dirs = ProbeGiBaker.RoundRayDirections(1024, sequence: 0);

		var mean = dirs.Aggregate(Vector3.Zero, (a, d) => a + d) / dirs.Length;

		Assert.True(mean.Length() < 0.01f, $"fan is biased: mean vector {mean}");
	}

	[Fact]
	public void RoundRayDirections_DifferentSequence_RotatesTheFan()
	{
		var first = ProbeGiBaker.RoundRayDirections(128, sequence: 0);
		var second = ProbeGiBaker.RoundRayDirections(128, sequence: 1);

		Assert.NotEqual(first, second);
		Assert.All(second, d => Assert.Equal(1f, d.Length(), Tolerance));
	}

	// Relocation decisions ride on the fixed rays, so they must not vary per round.
	[Fact]
	public void RoundRayDirections_FixedPrefix_IsIdenticalAcrossRounds()
	{
		const int rays = 288;
		int fixedRays = ProbeGiBaker.FixedRayCount(rays, realtime: true);

		var round1 = ProbeGiBaker.RoundRayDirections(rays, sequence: 1, fixedRays);
		var round9 = ProbeGiBaker.RoundRayDirections(rays, sequence: 9, fixedRays);

		Assert.True(fixedRays > 0, "a realtime 288-ray fan must have a fixed part");
		Assert.Equal(round1[..fixedRays], round9[..fixedRays]);

		Assert.NotEqual(round1[fixedRays..], round9[fixedRays..]);
	}

	[Fact]
	public void RoundRayDirections_WithFixedPrefix_StillReturnsUnitVectorsAndFullCount()
	{
		var dirs = ProbeGiBaker.RoundRayDirections(288, sequence: 3, fixedRays: 32);

		Assert.Equal(288, dirs.Length);
		Assert.All(dirs, d => Assert.Equal(1f, d.Length(), Tolerance));
	}

	[Fact]
	public void RoundRayDirections_ZeroFixedRays_MatchesThePlainOverload()
	{
		var withoutPrefix = ProbeGiBaker.RoundRayDirections(64, sequence: 5);
		var explicitlyZero = ProbeGiBaker.RoundRayDirections(64, sequence: 5, fixedRays: 0);

		Assert.Equal(withoutPrefix, explicitlyZero);
	}

	// Offline bakes rotate the whole fan, so there is no fixed prefix to keep in sync.
	[Theory]
	[InlineData(16)]
	[InlineData(64)]
	[InlineData(288)]
	[InlineData(1024)]
	public void FixedRayCount_OutsideRealtime_IsAlwaysZero(int rays)
	{
		Assert.Equal(0, ProbeGiBaker.FixedRayCount(rays, realtime: false));
	}

	[Theory]
	[InlineData(16, 0)]   // below 64 rays there is no split: radiance noise costs more
	[InlineData(63, 0)]
	[InlineData(64, 16)]
	[InlineData(128, 16)]
	[InlineData(192, 24)]
	[InlineData(256, 32)] // above 32 fixed rays stability stops improving, radiance loses samples
	[InlineData(1024, 32)]
	public void FixedRayCount_Realtime_ClampsBetweenFloorAndCeiling(int rays, int expected)
	{
		Assert.Equal(expected, ProbeGiBaker.FixedRayCount(rays, realtime: true));
	}

	[Theory]
	[InlineData(64)]
	[InlineData(96)]
	[InlineData(288)]
	public void FixedRayCount_NeverExceedsTheFanItself(int rays)
	{
		Assert.InRange(ProbeGiBaker.FixedRayCount(rays, realtime: true), 0, rays);
	}
}
