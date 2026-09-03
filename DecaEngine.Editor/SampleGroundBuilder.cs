using System;
using System.Collections.Generic;
using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;

namespace DecaEngine.Editor;

/// <summary>Generates the demo scene geometry: a platform with steps, a ramp and coloured walls.
/// Generated rather than shipped as a .glb so the whole path (export, import, .dmdl cook, probe
/// BVH, physics statics) is exercised. Each feature targets one engine capability: steps for foot
/// IK, the ramp for normal-aligned feet and ragdoll sliding, coloured walls for probe GI bounce,
/// and the smooth floor for SSR.</summary>
public static class SampleGroundBuilder
{
	/// <summary>Metres: the demo project works in metres, not model units.</summary>
	private const float PlatformSize = 14f;

	private const int StepCount = 5;
	private const float StepHeight = 0.16f;
	private const float StepDepth = 0.5f;
	private const float StepWidth = 4f;

	private const float RampLength = 5f;
	private const float RampWidth = 4f;
	private const float RampRise = 0.9f;

	// The mound sits on the gameplay circle path (must track SamplePrefabBuilder's CircleCenter).
	// Z is authored with the OPPOSITE sign to the engine's: the importer mirrors Z (RH glTF ->
	// LH engine), so this lands at z=-2.3 in world. Height/radius keep the max slope near 13
	// degrees - a slope, not a step, walkable by a capsule with no step-up.
	private static readonly Vector3 MoundCenter = new(0f, 0f, 2.3f);
	private const float MoundRadius = 1.5f;
	private const float MoundHeight = 0.12f;
	private const int MoundRings = 6;
	private const int MoundSegments = 24;

	private const float WallHeight = 3f;
	private const float WallThickness = 0.25f;

	private sealed class Surface
	{
		public MaterialBuilder Material = null!;
		public readonly List<(Vector3 A, Vector3 B, Vector3 C)> Triangles = new();
	}

	public static void Write(string path)
	{
		var floor = new Surface { Material = Material("Floor", new Vector4(0.62f, 0.62f, 0.64f, 1f), 0.22f) };
		var steps = new Surface { Material = Material("Steps", new Vector4(0.75f, 0.72f, 0.66f, 1f), 0.65f) };
		var leftWall = new Surface { Material = Material("WallRed", new Vector4(0.72f, 0.09f, 0.07f, 1f), 0.8f) };
		var rightWall = new Surface { Material = Material("WallGreen", new Vector4(0.10f, 0.62f, 0.16f, 1f), 0.8f) };
		var backWall = new Surface { Material = Material("WallGrey", new Vector4(0.70f, 0.70f, 0.70f, 1f), 0.8f) };

		float half = PlatformSize * 0.5f;

		AddQuad(floor,
			new Vector3(-half, 0f, -half), new Vector3(-half, 0f, half),
			new Vector3(half, 0f, half), new Vector3(half, 0f, -half));

		AddStairs(steps);
		AddRamp(steps);
		AddMound(steps);

		// Walls face inward with no ceiling: probe GI needs both direct sun and bounced light.
		AddBox(leftWall, new Vector3(-half, 0f, -half), new Vector3(-half + WallThickness, WallHeight, half));
		AddBox(rightWall, new Vector3(half - WallThickness, 0f, -half), new Vector3(half, WallHeight, half));
		AddBox(backWall, new Vector3(-half, 0f, half - WallThickness), new Vector3(half, WallHeight, half));

		var scene = new SceneBuilder();

		foreach (var surface in new[] { floor, steps, leftWall, rightWall, backWall })
		{
			if (surface.Triangles.Count == 0)
			{
				continue;
			}

			scene.AddRigidMesh(BuildMesh(surface), Matrix4x4.Identity);
		}

		scene.ToGltf2().SaveGLB(path);
	}

	private static MaterialBuilder Material(string name, Vector4 baseColor, float roughness) =>
		new MaterialBuilder(name)
			.WithMetallicRoughnessShader()
			.WithBaseColor(baseColor)
			.WithMetallicRoughness(0f, roughness)
			.WithDoubleSide(false);

	private static MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty> BuildMesh(Surface surface)
	{
		var mesh = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(surface.Material.Name);
		var primitive = mesh.UsePrimitive(surface.Material);

		foreach (var (a, b, c) in surface.Triangles)
		{
			// Flat per-triangle normals: averaging would round the step/riser corners.
			var normal = Vector3.Cross(b - a, c - a);
			normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY;

			primitive.AddTriangle(
				Vertex(a, normal),
				Vertex(b, normal),
				Vertex(c, normal));
		}

		return mesh;
	}

	// Planar world-XZ UVs, one metre per unit: a material without TEXCOORD_0 takes a different
	// import path than real models do.
	private static VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty> Vertex(
		Vector3 position, Vector3 normal) =>
		new((position, normal), new Vector2(position.X, position.Z));

	private static void AddQuad(Surface surface, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
	{
		surface.Triangles.Add((a, b, c));
		surface.Triangles.Add((a, c, d));
	}

	// Stairs along +X.
	private static void AddStairs(Surface surface)
	{
		float halfWidth = StepWidth * 0.5f;
		float x = 1.5f;

		// Each step is a CLOSED box, not a tread/riser quad pair: the physics mesh is one-sided,
		// so any missing face is a hole a capsule walks straight into.
		for (int i = 0; i < StepCount; i++)
		{
			float height = (i + 1) * StepHeight;
			float nextX = x + StepDepth;

			AddBox(surface, new Vector3(x, 0f, -halfWidth), new Vector3(nextX, height, halfWidth));

			x = nextX;
		}

		float top = StepCount * StepHeight;
		AddBox(surface, new Vector3(x, 0f, -halfWidth), new Vector3(x + 2f, top, halfWidth));
	}

	// Ramp along -X: the only place normal-aligned feet are visible.
	private static void AddRamp(Surface surface)
	{
		float halfWidth = RampWidth * 0.5f;
		float near = -1.5f;
		float far = near - RampLength;

		// Winding must match the floor quad: cross(ab, ac) up, or physics sees no surface at all.
		AddQuad(surface,
			new Vector3(near, 0f, halfWidth), new Vector3(near, 0f, -halfWidth),
			new Vector3(far, RampRise, -halfWidth), new Vector3(far, RampRise, halfWidth));

		// End cap facing outward (-X); the mesh is one-sided, so a missing face is a hole.
		AddQuad(surface,
			new Vector3(far, 0f, halfWidth), new Vector3(far, RampRise, halfWidth),
			new Vector3(far, RampRise, -halfWidth), new Vector3(far, 0f, -halfWidth));

		// Wedge sides facing outward along +/-Z.
		surface.Triangles.Add((
			new Vector3(near, 0f, -halfWidth), new Vector3(far, 0f, -halfWidth), new Vector3(far, RampRise, -halfWidth)));
		surface.Triangles.Add((
			new Vector3(near, 0f, halfWidth), new Vector3(far, RampRise, halfWidth), new Vector3(far, 0f, halfWidth)));
	}

	// Built from rings, not a grid: a grid's outer cells lie in the floor plane and z-fight.
	private static void AddMound(Surface surface)
	{
		float Height(float r) =>
			MoundHeight * (0.5f + 0.5f * MathF.Cos(MathF.PI * Math.Clamp(r / MoundRadius, 0f, 1f)));

		Vector3 P(int ring, int segment)
		{
			float r = MoundRadius * ring / MoundRings;
			float angle = MathF.Tau * segment / MoundSegments;
			return MoundCenter + new Vector3(r * MathF.Cos(angle), Height(r), r * MathF.Sin(angle));
		}

		for (int j = 0; j < MoundSegments; j++)
		{
			// Apex fan, wound AGAINST increasing angle: fan edges run outward from the apex, so
			// the same vertex order as the rings would give the opposite normal.
			surface.Triangles.Add((P(0, 0), P(1, j + 1), P(1, j)));

			for (int i = 1; i < MoundRings; i++)
			{
				AddQuad(surface, P(i, j), P(i, j + 1), P(i + 1, j + 1), P(i + 1, j));
			}
		}
	}

	// All six faces: a one-sided wall behaves as a hole in probe GI and in tracing.
	private static void AddBox(Surface surface, Vector3 min, Vector3 max)
	{
		Vector3 P(float x, float y, float z) => new(
			x < 0.5f ? min.X : max.X,
			y < 0.5f ? min.Y : max.Y,
			z < 0.5f ? min.Z : max.Z);

		// -Z and +Z
		AddQuad(surface, P(0, 0, 0), P(0, 1, 0), P(1, 1, 0), P(1, 0, 0));
		AddQuad(surface, P(1, 0, 1), P(1, 1, 1), P(0, 1, 1), P(0, 0, 1));

		// -X and +X
		AddQuad(surface, P(0, 0, 1), P(0, 1, 1), P(0, 1, 0), P(0, 0, 0));
		AddQuad(surface, P(1, 0, 0), P(1, 1, 0), P(1, 1, 1), P(1, 0, 1));

		// -Y and +Y
		AddQuad(surface, P(0, 0, 0), P(1, 0, 0), P(1, 0, 1), P(0, 0, 1));
		AddQuad(surface, P(0, 1, 1), P(1, 1, 1), P(1, 1, 0), P(0, 1, 0));
	}
}
