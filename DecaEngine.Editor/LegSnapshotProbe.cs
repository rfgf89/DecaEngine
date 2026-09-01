using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics;
using Friflo.Engine.ECS;
using UnsafeCollections.Collections.Unsafe;

namespace DecaEngine.Editor;

/// <summary>
/// Боковые стик-кадры ног персонажа на рельефе (DECA_PROBE_LEGSHOT=&lt;папка&gt;, работает внутри
/// DECA_PROBE_SCENE=1). Числовые метрики пробников отвечают «в теле ли лапа» и «вывернуто ли
/// колено», но не отвечают на жалобы вида «нога стоит не так»: форма позы - вещь визуальная, и
/// проверять её надо глазами. Кадр - ортопроекция X-Y (лестница демо-сцены растёт вдоль X, скелет
/// лисы лежит вдоль X): профиль пола щупается ТЕМИ ЖЕ физическими лучами, что и foot IK, скелет
/// рисуется целиком серым, задние цепочки бедро-колено-скакательный-носок подсвечены, передние -
/// плечо-предплечье-кисть.
/// </summary>
public static class LegSnapshotProbe
{
	private const int Width = 960;
	private const int Height = 600;

	/// <summary>Фазы съёмки, секунды сцены: шаг лисы ~1 с, десяток кадров через 0.25 покрывает
	/// больше двух циклов - и опору, и замах обеих ног.</summary>
	private static readonly float[] Times =
		{ 0.75f, 1.00f, 1.25f, 1.50f, 1.75f, 2.00f, 2.25f, 2.50f, 2.75f, 3.00f };

	public static void Poll(ScenePhysics physics, AnimationDriver animation,
		IReadOnlyList<Entity> skinnedEntities, Dictionary<int, ModelLoader> models,
		float time, float step)
	{
		string outDir = Environment.GetEnvironmentVariable("DECA_PROBE_LEGSHOT") ?? string.Empty;
		if (string.IsNullOrEmpty(outDir))
		{
			return;
		}

		// A/B-ветка: DECA_PROBE_LEGSHOT_NOIK=1 выключает foot IK у субъекта - кадры тогда
		// показывают ЧИСТЫЙ КЛИП. Разница двух прогонов отвечает на главный вопрос любой жалобы
		// на позу ноги: это солвер согнул или так нарисовано в клипе.
		// A/B-ветки: NOIK - чистый клип, NOLOCK - IK без локинга. Тройка прогонов раскладывает
		// вину между клипом, каналом высоты и локингом.
		if (!_ikToggleDone)
		{
			bool noIk = Environment.GetEnvironmentVariable("DECA_PROBE_LEGSHOT_NOIK") == "1";
			bool noLock = Environment.GetEnvironmentVariable("DECA_PROBE_LEGSHOT_NOLOCK") == "1";

			if (noIk || noLock)
			{
				foreach (var entity in skinnedEntities)
				{
					if (entity.GetComponent<EntityName>().value.Contains("foot IK", StringComparison.Ordinal) &&
						entity.HasComponent<FootIkComponent>())
					{
						if (noIk)
						{
							entity.GetComponent<FootIkComponent>().Enabled = false;
						}
						else
						{
							entity.GetComponent<FootIkComponent>().LockFeet = false;
						}
					}
				}
			}

			_ikToggleDone = true;
		}

		AccumulateJitter(animation, skinnedEntities, models);

		foreach (float target in Times)
		{
			if (MathF.Abs(time - target) < 0.5f * step)
			{
				Snapshot(physics, animation, skinnedEntities, models, time, outDir);
				return;
			}
		}
	}

	private static bool _ikToggleDone;

	// --- Дребезг лап -------------------------------------------------------------------------------
	//
	// Вторая разность мировой позиции точек опоры по кадрам: гладкий мах даёт миллиметры на кадр
	// (ускорение маха), дребезг захвата/отпуска локинга или скачки цели - всплеск на порядок.
	// Число само по себе не вердикт: та же метрика на прогоне без IK (DECA_PROBE_LEGSHOT_NOIK=1) -
	// планка клипа, и судить надо ПАРУ.

	private static readonly string[] JitterJoints =
		["b_LeftFoot02_018", "b_RightFoot02_022", "b_LeftHand_011", "b_RightHand_08"];

	private static readonly Vector3[] _jitterPrev = new Vector3[4];
	private static readonly Vector3[] _jitterPrev2 = new Vector3[4];
	private static int _jitterFrames;
	private static float _jitterWorstHind;
	private static float _jitterWorstFront;

	private static void AccumulateJitter(AnimationDriver animation,
		IReadOnlyList<Entity> skinnedEntities, Dictionary<int, ModelLoader> models)
	{
		Entity subject = default;
		foreach (var entity in skinnedEntities)
		{
			if (entity.GetComponent<EntityName>().value.Contains("foot IK", StringComparison.Ordinal))
			{
				subject = entity;
			}
		}

		if (subject.IsNull || !models.TryGetValue(subject.Id, out var model) ||
			model.Skeleton == null || !animation.TryGetPose(subject.Id, out var pose, out _))
		{
			return;
		}

		var world = PrefabSceneViewport.ComputeWorldMatrix(subject);

		for (int i = 0; i < JitterJoints.Length; i++)
		{
			int joint = model.Skeleton.FindJoint(JitterJoints[i]);
			if (joint < 0)
			{
				continue;
			}

			var position = Vector3.Transform(pose[joint].Translation, world);

			if (_jitterFrames >= 2)
			{
				float kick = (position - 2f * _jitterPrev[i] + _jitterPrev2[i]).Length();
				if (i < 2)
				{
					_jitterWorstHind = MathF.Max(_jitterWorstHind, kick);
				}
				else
				{
					_jitterWorstFront = MathF.Max(_jitterWorstFront, kick);
				}
			}

			_jitterPrev2[i] = _jitterPrev[i];
			_jitterPrev[i] = position;
		}

		_jitterFrames++;
	}

	private static void Snapshot(ScenePhysics physics, AnimationDriver animation,
		IReadOnlyList<Entity> skinnedEntities, Dictionary<int, ModelLoader> models,
		float time, string outDir)
	{
		// Субъект - лестничная лиса: единственный персонаж демо-сцены, у которого foot IK работает
		// на перепадах каждый кадр, а не изредка на кочке.
		Entity subject = default;
		foreach (var entity in skinnedEntities)
		{
			if (entity.GetComponent<EntityName>().value.Contains("foot IK", StringComparison.Ordinal))
			{
				subject = entity;
			}
		}

		if (subject.IsNull || !models.TryGetValue(subject.Id, out var model) ||
			model.Skeleton == null || !animation.TryGetPose(subject.Id, out var pose, out _))
		{
			return;
		}

		var world = PrefabSceneViewport.ComputeWorldMatrix(subject);
		var skeleton = model.Skeleton;

		// Мировые позиции суставов - один раз, дальше только 2D.
		var joints = new Vector3[skeleton.JointCount];
		for (int i = 0; i < joints.Length; i++)
		{
			joints[i] = Vector3.Transform(pose[i].Translation, world);
		}

		// Деформированный МЕШ - каркасом по рёбрам: скелет отвечает «куда согнуло кости», а жалобы
		// пользователя приходят про то, как выглядит ТЕЛО; облако вершин низкополигональной лисы
		// слишком редкое. Скиннинг - той же свёрткой палитры, что ушла бы в GPU (см.
		// ScenePhysicsProbe.ReportDeformedExtents).
		var cloud = SkinnedMesh(animation, model, subject, world);

		// Два ракурса: тело лисы в мире лежит вдоль Z, лестница растёт вдоль X - вид вдоль X
		// (горизонталь кадра = Z) показывает сгиб коленей сбоку, вид вдоль Z (горизонталь = X) -
		// перепад ступеней под левой и правой сторонами.
		Draw(physics, subject, skeleton, joints, cloud, time, outDir, "side", Vector3.UnitZ);
		Draw(physics, subject, skeleton, joints, cloud, time, outDir, "front", Vector3.UnitX);
		ReportGaps(physics, skeleton, joints, time);

		Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
			$"[probe] legshot: t={time:0.00} дребезг за окно - задние {_jitterWorstHind * 1000f:0.0} мм/кадр², " +
			$"передние {_jitterWorstFront * 1000f:0.0} мм/кадр²"));
		_jitterWorstHind = 0f;
		_jitterWorstFront = 0f;
	}

	/// <summary>Зазор «сустав опоры - поверхность под ним» по каждой лапе, метры мира. Числа к
	/// картинкам: картинка отвечает «как выглядит», зазор - «на сколько именно висит»; сравнение
	/// прогонов с IK и без (DECA_PROBE_LEGSHOT_NOIK=1) отделяет отклонения самого клипа от того,
	/// что добавил или снял солвер.</summary>
	private static void ReportGaps(ScenePhysics physics, PreparedSkeleton skeleton, Vector3[] joints,
		float time)
	{
		(string Label, string Joint)[] contacts =
		[
			("задняя L", "b_LeftFoot02_018"),
			("задняя R", "b_RightFoot02_022"),
			("передняя L", "b_LeftHand_011"),
			("передняя R", "b_RightHand_08"),
		];

		var line = new System.Text.StringBuilder();
		line.Append(string.Create(System.Globalization.CultureInfo.InvariantCulture,
			$"[probe] legshot: t={time:0.00} зазоры"));

		foreach (var contact in contacts)
		{
			int joint = skeleton.FindJoint(contact.Joint);
			if (joint < 0)
			{
				continue;
			}

			var position = joints[joint];
			var hit = physics.SampleGround(position + Vector3.UnitY * 3f, -Vector3.UnitY, 6f);
			line.Append(string.Create(System.Globalization.CultureInfo.InvariantCulture,
				$" | {contact.Label} {(hit.Hit ? position.Y - hit.Position.Y : float.NaN):+0.000;-0.000} м"));
		}

		Console.WriteLine(line.ToString());
	}

	private static void DrawWireframe(byte[] rgba, (List<Vector3> Vertices, List<int> Indices) cloud,
		Vector3 horizontal, float viewX0, float viewY0, float scale)
	{
		for (int k = 0; k + 2 < cloud.Indices.Count; k += 3)
		{
			var a = Project(cloud.Vertices[cloud.Indices[k]], horizontal, viewX0, viewY0, scale);
			var b = Project(cloud.Vertices[cloud.Indices[k + 1]], horizontal, viewX0, viewY0, scale);
			var c = Project(cloud.Vertices[cloud.Indices[k + 2]], horizontal, viewX0, viewY0, scale);

			DrawLine(rgba, a, b, 52, 52, 64);
			DrawLine(rgba, b, c, 52, 52, 64);
			DrawLine(rgba, c, a, 52, 52, 64);
		}
	}

	/// <summary>Меш, деформированный текущей палитрой: мировые вершины плюс индексы треугольников
	/// (все меши модели одним списком, LOD-уровни в IndexData просто перерисовывают ту же форму
	/// грубее - для каркаса это безвредно).</summary>
	private static unsafe (List<Vector3> Vertices, List<int> Indices) SkinnedMesh(
		AnimationDriver animation, ModelLoader model, Entity subject, in Matrix4x4 world)
	{
		var resultVertices = new List<Vector3>();
		var resultIndices = new List<int>();

		if (!animation.TryGetPose(subject.Id, out _, out var skin))
		{
			return (resultVertices, resultIndices);
		}

		for (int meshIndex = 0; meshIndex < model.Meshes.Count; meshIndex++)
		{
			var skinStream = meshIndex < model.MeshSkin.Count ? model.MeshSkin[meshIndex] : null;
			var mesh = model.Meshes[meshIndex];

			if (skinStream == null || mesh.VertexData == null || mesh.IndexData == null)
			{
				continue;
			}

			int baseVertex = resultVertices.Count;
			int vertexCount = Math.Min(UnsafeArray.GetLength(mesh.VertexData), skinStream.Length);
			var vertices = new ReadOnlySpan<Vertex>(UnsafeArray.GetPtr<Vertex>(mesh.VertexData, 0), vertexCount);

			for (int v = 0; v < vertexCount; v++)
			{
				var bind = vertices[v].Position;
				var s = skinStream[v];

				var deformed = s.IsUnskinned
					? bind
					: Vector3.Transform(bind, skin[s.J0]) * (s.W0 / SkinVertex.WeightScale) +
						Vector3.Transform(bind, skin[s.J1]) * (s.W1 / SkinVertex.WeightScale) +
						Vector3.Transform(bind, skin[s.J2]) * (s.W2 / SkinVertex.WeightScale) +
						Vector3.Transform(bind, skin[s.J3]) * (s.W3 / SkinVertex.WeightScale);

				resultVertices.Add(Vector3.Transform(deformed, world));
			}

			var indices = new ReadOnlySpan<uint>(UnsafeArray.GetPtr<uint>(mesh.IndexData, 0), mesh.IndexCount);
			for (int k = 0; k + 2 < indices.Length; k += 3)
			{
				if (indices[k] < vertexCount && indices[k + 1] < vertexCount && indices[k + 2] < vertexCount)
				{
					resultIndices.Add(baseVertex + (int)indices[k]);
					resultIndices.Add(baseVertex + (int)indices[k + 1]);
					resultIndices.Add(baseVertex + (int)indices[k + 2]);
				}
			}
		}

		return (resultVertices, resultIndices);
	}

	private static void Draw(ScenePhysics physics, Entity subject, PreparedSkeleton skeleton,
		Vector3[] joints, (List<Vector3> Vertices, List<int> Indices) cloud, float time,
		string outDir, string label, Vector3 horizontal)
	{

		// Окно кадра - вокруг персонажа, с постоянным масштабом между кадрами (привязка к целым
		// ступеням лестницы дала бы прыгающую камеру).
		var subjectPosition = subject.Position.value;
		float viewX0 = Vector3.Dot(subjectPosition, horizontal) - 1.1f;
		float scale = Width / 2.2f;
		float viewY0 = -0.08f;

		var rgba = new byte[Width * Height * 4];
		for (int i = 0; i < rgba.Length; i += 4)
		{
			rgba[i] = 16; rgba[i + 1] = 16; rgba[i + 2] = 20; rgba[i + 3] = 255;
		}

		// Профиль пола - лучами по столбцам кадра, в вертикальной плоскости через персонажа:
		// именно этот рельеф щупает foot IK, и рисовать надо его, а не авторские числа лестницы.
		for (int px = 0; px < Width; px++)
		{
			float x = viewX0 + px / scale;
			var origin = subjectPosition + horizontal * (x - Vector3.Dot(subjectPosition, horizontal));
			origin.Y = 3f;

			var hit = physics.SampleGround(origin, -Vector3.UnitY, 6f);
			if (hit.Hit)
			{
				int py = ToPixelY(hit.Position.Y, viewY0, scale);
				for (int t = 0; t < 2; t++)
				{
					PutPixel(rgba, px, py + t, 235, 235, 235);
				}
			}
		}

		// Тело - каркасом, тусклым: поверх него читаются и скелет, и подсветка ног.
		DrawWireframe(rgba, cloud, horizontal, viewX0, viewY0, scale);

		// Весь скелет - серым: контекст позы (корпус, шея, хвост) без него не читается.
		for (int i = 0; i < joints.Length; i++)
		{
			int parent = skeleton.Parents[i];
			if (parent >= 0)
			{
				DrawLine(rgba, Project(joints[parent], horizontal, viewX0, viewY0, scale),
					Project(joints[i], horizontal, viewX0, viewY0, scale), 110, 110, 118);
			}
		}

		// Подсветка ног - по именам рига Khronos Fox, как в CharacterPlaneProbe: пробник
		// воспроизводит конкретного персонажа. Ненайденное имя просто не подсвечивается.
		DrawChain(rgba, skeleton, joints, horizontal, viewX0, viewY0, scale, 255, 210, 60,
			"b_LeftLeg01_015", "b_LeftLeg02_016", "b_LeftFoot01_017", "b_LeftFoot02_018");
		DrawChain(rgba, skeleton, joints, horizontal, viewX0, viewY0, scale, 255, 140, 40,
			"b_RightLeg01_019", "b_RightLeg02_020", "b_RightFoot01_021", "b_RightFoot02_022");
		DrawChain(rgba, skeleton, joints, horizontal, viewX0, viewY0, scale, 90, 200, 255,
			"b_LeftUpperArm_09", "b_LeftForeArm_010", "b_LeftHand_011");
		DrawChain(rgba, skeleton, joints, horizontal, viewX0, viewY0, scale, 60, 150, 230,
			"b_RightUpperArm_06", "b_RightForeArm_07", "b_RightHand_08");

		Directory.CreateDirectory(outDir);
		string path = Path.Combine(outDir, string.Create(
			System.Globalization.CultureInfo.InvariantCulture, $"legshot_{label}_t{time:0.00}.png"));
		PngWriter.Write(path, rgba, Width, Height);
		Console.WriteLine($"[probe] legshot: {path}");
	}

	private static void DrawChain(byte[] rgba, PreparedSkeleton skeleton, Vector3[] joints,
		Vector3 horizontal, float viewX0, float viewY0, float scale, byte r, byte g, byte b,
		params string[] names)
	{
		int previous = -1;
		foreach (string name in names)
		{
			int joint = skeleton.FindJoint(name);
			if (joint < 0)
			{
				previous = -1;
				continue;
			}

			if (previous >= 0)
			{
				var from = Project(joints[previous], horizontal, viewX0, viewY0, scale);
				var to = Project(joints[joint], horizontal, viewX0, viewY0, scale);

				// Жирная линия: три параллельных прохода читаются на фоне серого скелета.
				for (int offset = -1; offset <= 1; offset++)
				{
					DrawLine(rgba, (from.X, from.Y + offset), (to.X, to.Y + offset), r, g, b);
				}
			}

			DrawDot(rgba, Project(joints[joint], horizontal, viewX0, viewY0, scale), r, g, b);
			previous = joint;
		}
	}

	private static (int X, int Y) Project(Vector3 position, Vector3 horizontal, float viewX0,
		float viewY0, float scale) =>
		((int)MathF.Round((Vector3.Dot(position, horizontal) - viewX0) * scale),
			ToPixelY(position.Y, viewY0, scale));

	private static int ToPixelY(float y, float viewY0, float scale) =>
		Height - 1 - (int)MathF.Round((y - viewY0) * scale);

	private static void DrawDot(byte[] rgba, (int X, int Y) center, byte r, byte g, byte b)
	{
		for (int dy = -2; dy <= 2; dy++)
		{
			for (int dx = -2; dx <= 2; dx++)
			{
				PutPixel(rgba, center.X + dx, center.Y + dy, r, g, b);
			}
		}
	}

	private static void DrawLine(byte[] rgba, (int X, int Y) from, (int X, int Y) to,
		byte r, byte g, byte b)
	{
		int dx = Math.Abs(to.X - from.X), sx = from.X < to.X ? 1 : -1;
		int dy = -Math.Abs(to.Y - from.Y), sy = from.Y < to.Y ? 1 : -1;
		int error = dx + dy;
		int x = from.X, y = from.Y;

		while (true)
		{
			PutPixel(rgba, x, y, r, g, b);

			if (x == to.X && y == to.Y)
			{
				break;
			}

			int doubled = 2 * error;
			if (doubled >= dy) { error += dy; x += sx; }
			if (doubled <= dx) { error += dx; y += sy; }
		}
	}

	private static void PutPixel(byte[] rgba, int x, int y, byte r, byte g, byte b)
	{
		if (x < 0 || y < 0 || x >= Width || y >= Height)
		{
			return;
		}

		int offset = (y * Width + x) * 4;
		rgba[offset] = r;
		rgba[offset + 1] = g;
		rgba[offset + 2] = b;
		rgba[offset + 3] = 255;
	}
}
