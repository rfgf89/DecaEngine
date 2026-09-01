using System.Numerics;
using DecaEngine.Graphics.ProbeGi;

namespace DecaEngine.Tests.ProbeGi;

/// <summary>
/// Веер лучей раунда probe GI. Проверять его тестами имеет смысл ровно потому, что глазами эти
/// ошибки не ловятся: неверный веер даёт не чёрный экран, а чуть более шумное поле - его спишут на
/// недосходимость. При этом CPU-бейкер и ProbeRoundCS обязаны строить веер ЛУЧ В ЛУЧ одинаково,
/// иначе сверка GPU-пути с CPU-эталоном перестаёт что-либо значить.
/// </summary>
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

	/// <summary>Веер Фибоначчи - равномерная сферическая выборка: сумма направлений должна гаснуть.
	/// Смещённая выборка тихо перекашивает оценку освещённости в сторону перепредставленной
	/// полусферы.</summary>
	[Fact]
	public void RoundRayDirections_FibonacciFan_IsCentredOnTheSphere()
	{
		var dirs = ProbeGiBaker.RoundRayDirections(1024, sequence: 0);

		var mean = dirs.Aggregate(Vector3.Zero, (a, d) => a + d) / dirs.Length;

		Assert.True(mean.Length() < 0.01f, $"веер смещён: средний вектор {mean}");
	}

	[Fact]
	public void RoundRayDirections_DifferentSequence_RotatesTheFan()
	{
		var first = ProbeGiBaker.RoundRayDirections(128, sequence: 0);
		var second = ProbeGiBaker.RoundRayDirections(128, sequence: 1);

		Assert.NotEqual(first, second);
		Assert.All(second, d => Assert.Equal(1f, d.Length(), Tolerance));
	}

	/// <summary>
	/// Главный контракт фиксированных лучей: по ним принимаются решения о переезде и отключении
	/// пробы, поэтому от номера раунда они зависеть НЕ должны. Стоит им начать вращаться - у пробы
	/// на кромке геометрии доля задних граней загуляет от раунда к раунду, и проба начнёт ездить
	/// туда-сюда, каждый раз сбрасывая накопители.
	/// </summary>
	[Fact]
	public void RoundRayDirections_FixedPrefix_IsIdenticalAcrossRounds()
	{
		const int rays = 288;
		int fixedRays = ProbeGiBaker.FixedRayCount(rays, realtime: true);

		var round1 = ProbeGiBaker.RoundRayDirections(rays, sequence: 1, fixedRays);
		var round9 = ProbeGiBaker.RoundRayDirections(rays, sequence: 9, fixedRays);

		Assert.True(fixedRays > 0, "у веера на 288 лучей в реальном времени должна быть фиксированная часть");
		Assert.Equal(round1[..fixedRays], round9[..fixedRays]);

		// А вращаемая часть обязана как раз отличаться, иначе вращения нет вовсе.
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

	/// <summary>В запечке веер вращается целиком: CPU и GPU обязаны совпасть луч в луч, а делить
	/// веер значило бы зеркалить раскладку ещё и в CPU-бейкере.</summary>
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
	[InlineData(16, 0)]   // короче 64 - деления нет вовсе, шум радианса дороже устойчивости
	[InlineData(63, 0)]
	[InlineData(64, 16)]  // пол: по этим лучам ищется ближайшая передняя грань
	[InlineData(128, 16)]
	[InlineData(192, 24)]
	[InlineData(256, 32)] // потолок: сверх 32 устойчивость не растёт, а радианс теряет выборку
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
