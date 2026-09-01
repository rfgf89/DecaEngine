using System;
using System.Collections.Generic;
using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;

namespace DecaEngine.Editor;

/// <summary>
/// Генератор геометрии демо-сцены: площадка со ступенями, пандусом и цветными стенами, которую
/// кладут рядом с демо-префабом (см. <see cref="SamplePrefabBuilder"/>).
///
/// Геометрия ГЕНЕРИРУЕТСЯ, а не лежит готовым файлом, по той же причине, по которой генерируется сам
/// префаб: и то, и другое - проверка живого кода движка. Готовый .glb в репозитории проверял бы
/// только, что загрузчик умеет читать этот конкретный файл, а сгенерированный проходит весь путь -
/// экспорт, импорт, кук в .dmdl, BVH проб, статику физики.
///
/// Форма выбрана не для красоты. Каждый её элемент отвечает конкретному вопросу к движку:
/// - СТУПЕНИ - foot IK: на ровном полу он неотличим от его отсутствия, а на ступенях видно и работу
///   стоп, и опускание таза;
/// - ПАНДУС - доворот стопы по нормали и рэгдолл, который по нему съезжает, а не залипает;
/// - ЦВЕТНЫЕ СТЕНЫ - probe GI: подкраска отражённым светом видна только от насыщенных поверхностей;
/// - ГЛАДКИЙ ПОЛ - SSR: на шероховатости 0.8 отражения не видно вовсе, и проверять было бы нечего.
///
/// Пишется через SharpGLTF, который в движке уже есть (им же читаются модели), а не собственным
/// писателем glb: формат бинарный, и рукописный экспорт - это второе место, где можно разойтись с
/// импортёром, причём молча.
/// </summary>
public static class SampleGroundBuilder
{
	/// <summary>Метры. Сцена демо-проекта живёт в метрах (лиса приводится к ним масштабом 0.02),
	/// поэтому и площадка - человеческого размера, а не «в единицах модели».</summary>
	private const float PlatformSize = 14f;

	private const int StepCount = 5;
	private const float StepHeight = 0.16f;
	private const float StepDepth = 0.5f;
	private const float StepWidth = 4f;

	private const float RampLength = 5f;
	private const float RampWidth = 4f;
	private const float RampRise = 0.9f;

	// Кочка лежит на ПУТИ КРУГА геймплейной лисы: центр - ближняя к лестнице точка круга
	// (CircleCenter + R по Z в SamplePrefabBuilder; констант общих нет, разойдясь с кругом, кочка
	// молча превратится в декорацию, которую никто не топчет). Высота и радиус подобраны под
	// капсулу БЕЗ step-up: максимальный уклон косинусного профиля H*pi/(2R) ~ 13 градусов - это
	// склон, а не ступень.
	//
	// Z задан С ОБРАТНЫМ ЗНАКОМ к сценическому: импортёр зеркалит Z (RH glTF → LH движка), и в
	// движке кочка оказывается на z=-2.3. Вся остальная площадка z-симметрична, поэтому зеркало в
	// ней НЕ ВИДНО вовсе - кочка первая деталь, на которой конвенция проявилась: заданная «как в
	// сцене», она молча уехала на другую сторону круга (ловится лучами в ScenePhysicsProbe).
	// Высота и радиус подобраны ПОД ДЛИНУ ЛИСЫ, а не только под капсулу: зверь длиной 1.5 м на
	// бугре радиусом 1.2 м получает под парами лап перепад в 10-15 см при ноге в 35 см - foot IK
	// честно уводит такой перепад в сгиб коленей, и задние ноги протыкают корпус. Пологая широкая
	// кочка держит перепады под лапами в единицах сантиметров - естественном диапазоне шага.
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

		// Стены стоят по краям площадки и смотрят ВНУТРЬ. Это не «коробка» - потолка нет, солнце и
		// небо должны попадать в сцену: probe GI проверяется на смеси прямого света и отражённого,
		// а в закрытой коробке от солнца остаётся только вход через дверь.
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
			// Нормаль ПЛОСКАЯ, по самому треугольнику: у площадки со ступенями сглаживать нечего, а
			// усреднённая нормаль на стыке ступени и подступёнка дала бы скруглённый угол там, где
			// на самом деле прямой, - и тень с probe GI сели бы не туда.
			var normal = Vector3.Cross(b - a, c - a);
			normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY;

			primitive.AddTriangle(
				Vertex(a, normal),
				Vertex(b, normal),
				Vertex(c, normal));
		}

		return mesh;
	}

	/// <summary>UV - планарные по мировым XZ, с шагом в метр. Развёртка здесь никому не нужна по
	/// существу, но материал без TEXCOORD_0 - это отдельная ветка в импортёре, и гонять демо-сцену
	/// по ней значило бы проверять не тот путь, которым приходят настоящие модели.</summary>
	private static VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty> Vertex(
		Vector3 position, Vector3 normal) =>
		new((position, normal), new Vector2(position.X, position.Z));

	private static void AddQuad(Surface surface, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
	{
		surface.Triangles.Add((a, b, c));
		surface.Triangles.Add((a, c, d));
	}

	/// <summary>Лестница вдоль +X: ступень за ступенью, проступь и подступёнок. Именно по ней
	/// проверяется foot IK - на ней ноги персонажа стоят на РАЗНОЙ высоте, а это и есть случай, ради
	/// которого солвер опускает таз.</summary>
	private static void AddStairs(Surface surface)
	{
		float halfWidth = StepWidth * 0.5f;
		float x = 1.5f;

		for (int i = 0; i < StepCount; i++)
		{
			float height = (i + 1) * StepHeight;
			float nextX = x + StepDepth;

			// Проступь.
			AddQuad(surface,
				new Vector3(x, height, -halfWidth), new Vector3(x, height, halfWidth),
				new Vector3(nextX, height, halfWidth), new Vector3(nextX, height, -halfWidth));

			// Подступёнок - лицом к -X, откуда на лестницу и заходят.
			AddQuad(surface,
				new Vector3(x, height - StepHeight, -halfWidth), new Vector3(x, height, -halfWidth),
				new Vector3(x, height, halfWidth), new Vector3(x, height - StepHeight, halfWidth));

			x = nextX;
		}

		// Верхняя площадка - чтобы персонаж, поднявшийся по лестнице, стоял на чём-то, а не
		// заканчивался обрывом ровно на последней ступени.
		float top = StepCount * StepHeight;
		AddQuad(surface,
			new Vector3(x, top, -halfWidth), new Vector3(x, top, halfWidth),
			new Vector3(x + 2f, top, halfWidth), new Vector3(x + 2f, top, -halfWidth));
	}

	/// <summary>Пандус вдоль -X. Наклонная поверхность - единственное, на чём видно доворот стопы по
	/// нормали: на горизонтальном полу нормаль совпадает с вертикалью, и коррекция вырождается в
	/// единичную сама собой.</summary>
	private static void AddRamp(Surface surface)
	{
		float halfWidth = RampWidth * 0.5f;
		float near = -1.5f;
		float far = near - RampLength;

		AddQuad(surface,
			new Vector3(near, 0f, -halfWidth), new Vector3(near, 0f, halfWidth),
			new Vector3(far, RampRise, halfWidth), new Vector3(far, RampRise, -halfWidth));

		// Торец пандуса: без него в него видно «насквозь» с обратной стороны - меш односторонний.
		AddQuad(surface,
			new Vector3(far, 0f, -halfWidth), new Vector3(far, RampRise, -halfWidth),
			new Vector3(far, RampRise, halfWidth), new Vector3(far, 0f, halfWidth));
	}

	/// <summary>
	/// Пологая кочка на пути круга. Плавный рельеф - единственное, на чём видна подстройка лап
	/// ИДУЩЕГО персонажа: ступени капсула без step-up не берёт, а на ровном полу foot IK вырождается
	/// в ничто. Диск набран КОЛЬЦАМИ, а не сеткой над квадратом: у сетки внешние ячейки лежат ровно в
	/// плоскости пола и мерцают с ним z-файтингом, у колец с полом совпадает только внешняя ОКРУЖНОСТЬ.
	/// </summary>
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
			// Вершина - веером: внутреннее кольцо стянуто в точку, и квадами оно дало бы
			// вырожденные треугольники с нулевой нормалью. Обход - ПРОТИВ роста угла, не как у
			// колец: ребро веера идёт от вершины НАРУЖУ, а у кольца - вдоль окружности, и «тот же»
			// порядок вершин даёт противоположную нормаль. Проверено пробником: с веером по росту
			// угла шапка гребня смотрела изнанкой вверх, и капсула проходила под неё, как под навес.
			surface.Triangles.Add((P(0, 0), P(1, j + 1), P(1, j)));

			for (int i = 1; i < MoundRings; i++)
			{
				AddQuad(surface, P(i, j), P(i, j + 1), P(i + 1, j + 1), P(i + 1, j));
			}
		}
	}

	/// <summary>Коробка по двум углам. Все шесть граней: стена, видимая только с одной стороны,
	/// в probe GI и в трассировке ведёт себя не как стена, а как дыра.</summary>
	private static void AddBox(Surface surface, Vector3 min, Vector3 max)
	{
		Vector3 P(float x, float y, float z) => new(
			x < 0.5f ? min.X : max.X,
			y < 0.5f ? min.Y : max.Y,
			z < 0.5f ? min.Z : max.Z);

		// -Z и +Z
		AddQuad(surface, P(0, 0, 0), P(0, 1, 0), P(1, 1, 0), P(1, 0, 0));
		AddQuad(surface, P(1, 0, 1), P(1, 1, 1), P(0, 1, 1), P(0, 0, 1));

		// -X и +X
		AddQuad(surface, P(0, 0, 1), P(0, 1, 1), P(0, 1, 0), P(0, 0, 0));
		AddQuad(surface, P(1, 0, 0), P(1, 1, 0), P(1, 1, 1), P(1, 0, 1));

		// -Y и +Y
		AddQuad(surface, P(0, 0, 0), P(1, 0, 0), P(1, 0, 1), P(0, 0, 1));
		AddQuad(surface, P(0, 1, 1), P(1, 1, 1), P(1, 1, 0), P(0, 1, 0));
	}
}
