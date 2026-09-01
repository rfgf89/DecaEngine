using System.Numerics;
using DecaEngine.Graphics;

namespace DecaEngine.Tests.Graphics;

/// <summary>
/// Чистая математика мешей из <see cref="MeshUtility"/>: массивы на входе, массивы на выходе, ни
/// графического API, ни файлов. Именно её предстоит вынести из ModelLoader.cs (3700 строк) в
/// отдельный файл, и эти тесты - страховка на время переезда.
/// </summary>
public class MeshUtilityTests
{
	private const float Tolerance = 1e-5f;

	/// <summary>Единичный квадрат в плоскости XY, нормаль +Z. UV задаёт вызывающий: развёртка -
	/// единственное, что отличает обычный случай от зеркального.</summary>
	private static (Vertex[] Vertices, uint[] Indices) Quad(
		Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector2 uv3)
	{
		var normal = new Vector3(0f, 0f, 1f);
		var vertices = new[]
		{
			new Vertex { Position = new Vector3(0f, 0f, 0f), TexCoord = uv0, Normal = normal },
			new Vertex { Position = new Vector3(1f, 0f, 0f), TexCoord = uv1, Normal = normal },
			new Vertex { Position = new Vector3(1f, 1f, 0f), TexCoord = uv2, Normal = normal },
			new Vertex { Position = new Vector3(0f, 1f, 0f), TexCoord = uv3, Normal = normal },
		};

		return (vertices, [0, 1, 2, 0, 2, 3]);
	}

	[Fact]
	public void GenerateTangents_UvGrowingAlongX_PointsTangentAlongX()
	{
		var (vertices, indices) = Quad(
			new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f));

		MeshUtility.GenerateTangents(vertices, indices);

		foreach (var vertex in vertices)
		{
			Assert.Equal(1f, vertex.Tangent.X, Tolerance);
			Assert.Equal(0f, vertex.Tangent.Y, Tolerance);
			Assert.Equal(0f, vertex.Tangent.Z, Tolerance);
		}
	}

	/// <summary>
	/// Знак битангента на зеркальной развёртке. Ради него битангент и копится отдельным массивом:
	/// направление тангента у зеркального квадрата ТО ЖЕ САМОЕ (+X), и по нему одному зеркало не
	/// отличить. Ошибка здесь не роняет ничего - она переворачивает Y нормал-мапы, то есть
	/// инвертирует рельеф на симметричных моделях и атласах.
	/// </summary>
	[Fact]
	public void GenerateTangents_MirroredUv_FlipsBitangentSign()
	{
		var (straight, indices) = Quad(
			new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f));
		var (mirrored, _) = Quad(
			new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f), new Vector2(0f, 0f));

		MeshUtility.GenerateTangents(straight, indices);
		MeshUtility.GenerateTangents(mirrored, indices);

		foreach (var vertex in straight)
		{
			Assert.Equal(1f, vertex.Tangent.W);
		}

		foreach (var vertex in mirrored)
		{
			Assert.Equal(-1f, vertex.Tangent.W);

			// Направление осталось прежним - зеркало видно ТОЛЬКО по знаку.
			Assert.Equal(1f, vertex.Tangent.X, Tolerance);
		}
	}

	/// <summary>Меш без UV вовсе: тангент не определён, но остаться нулевым не может - шейдер
	/// строит по нему базис.</summary>
	[Fact]
	public void GenerateTangents_DegenerateUv_FallsBackToVectorPerpendicularToNormal()
	{
		var (vertices, indices) = Quad(Vector2.Zero, Vector2.Zero, Vector2.Zero, Vector2.Zero);

		MeshUtility.GenerateTangents(vertices, indices);

		foreach (var vertex in vertices)
		{
			var tangent = new Vector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z);
			Assert.Equal(1f, tangent.Length(), Tolerance);
			Assert.Equal(0f, Vector3.Dot(tangent, vertex.Normal), Tolerance);
		}
	}

	[Fact]
	public void GenerateTangents_NonTriangleIndexList_LeavesVerticesUntouched()
	{
		var (vertices, _) = Quad(
			new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f));

		MeshUtility.GenerateTangents(vertices, [0, 1, 2, 3]);

		Assert.All(vertices, v => Assert.Equal(Vector4.Zero, v.Tangent));
	}

	[Fact]
	public void GenerateTangents_EmptyIndexList_DoesNotThrow()
	{
		var (vertices, _) = Quad(
			new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f));

		MeshUtility.GenerateTangents(vertices, []);

		Assert.All(vertices, v => Assert.Equal(Vector4.Zero, v.Tangent));
	}

	[Fact]
	public void PackSkinned_ThenUnpack_RoundTripsBothStreams()
	{
		var vertices = new[]
		{
			new Vertex { Position = new Vector3(1f, 2f, 3f), TexCoord = new Vector2(0.25f, 0.5f) },
			new Vertex { Position = new Vector3(4f, 5f, 6f), TexCoord = new Vector2(0.75f, 1f) },
		};
		var skin = new[]
		{
			new SkinVertex { J0 = 3, J1 = 7, W0 = 40000, W1 = 25535 },
			new SkinVertex { J0 = 1, W0 = 65535 },
		};

		var (roundTripped, roundTrippedSkin) = MeshUtility.UnpackSkinned(
			MeshUtility.PackSkinned(vertices, skin));

		Assert.Equal(vertices, roundTripped);
		Assert.Equal(skin, roundTrippedSkin);
	}

	/// <summary>
	/// Скин-стрим короче геометрии. Так бывает после разваривания вершин под плоские нормали, и
	/// тихо покорёженный персонаж диагностируется куда хуже, чем краш: недостающие вершины
	/// прибиваются к корню с полным весом.
	/// </summary>
	[Fact]
	public void PackSkinned_SkinStreamShorterThanGeometry_PinsMissingVerticesToRoot()
	{
		var vertices = new Vertex[3];
		var skin = new[] { new SkinVertex { J0 = 5, W0 = 65535 } };

		var packed = MeshUtility.PackSkinned(vertices, skin);

		Assert.Equal(3, packed.Length);
		Assert.Equal((ushort)5, packed[0].Skin.J0);

		foreach (var missing in packed[1..])
		{
			Assert.Equal((ushort)0, missing.Skin.J0);
			Assert.Equal((ushort)SkinVertex.WeightScale, missing.Skin.W0);
			Assert.False(missing.Skin.IsUnskinned);
		}
	}

	[Fact]
	public void ComputeBoundsData_EmptyMesh_ReturnsZeroSphere()
	{
		var (center, radius) = MeshUtility.ComputeBoundsData([]);

		Assert.Equal(Vector3.Zero, center);
		Assert.Equal(0f, radius);
	}

	/// <summary>Через нативный meshopt: заодно проверяет, что его библиотека вообще доезжает до
	/// тестового хоста.</summary>
	[Fact]
	public void ComputeBoundsData_UnitCubeCorners_EnclosesEveryVertex()
	{
		var vertices = new List<Vertex>();
		foreach (var x in new[] { -1f, 1f })
		{
			foreach (var y in new[] { -1f, 1f })
			{
				foreach (var z in new[] { -1f, 1f })
				{
					vertices.Add(new Vertex { Position = new Vector3(x, y, z) });
				}
			}
		}

		var (center, radius) = MeshUtility.ComputeBoundsData([.. vertices]);

		Assert.All(vertices, v =>
			Assert.True((v.Position - center).Length() <= radius + Tolerance,
				$"вершина {v.Position} вне сферы (центр {center}, радиус {radius})"));
	}
}
