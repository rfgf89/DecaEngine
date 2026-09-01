using System;
using System.Collections.Generic;
using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Core.Diagnostics;
using DecaEngine.Core.Assets;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
using DecaEngine.Graphics.ProbeGi;
using Engine.ImGui.Core;
using System.Threading.Tasks;
using Friflo.Engine.ECS;
using Hexa.NET.ImGui;
using Hexa.NET.ImGuizmo;
using UnsafeCollections.Collections.Unsafe;
using DecaEngine.Scene;
using DecaEngine.Animation;

namespace DecaEngine.Editor
{
	/// <summary>Пикинг: луч из курсора, сферы и треугольники. Часть <see cref="PrefabSceneViewport"/> - файл на тему;
	/// состояние, конструктор и кадровые Update/Render живут в основном файле.</summary>
	public partial class PrefabSceneViewport
	{
		/// <summary>Пикинг кликом: луч из камеры через пиксель, сферный broadphase по баундам мешей,
		/// затем точное пересечение с треугольниками (CPU-копии вершин). Возвращает сущность префаба
		/// ближайшего попадания, null - клик в пустоту.</summary>
		private unsafe Entity? Pick(Vector2 cursor, Vector2 size, Vector2 mouse)
		{
			// Луч в мировом пространстве - те же камера/проекция, под которыми рендерился кадр.
			float u = (mouse.X - cursor.X) / size.X * 2f - 1f;
			float v = 1f - (mouse.Y - cursor.Y) / size.Y * 2f;
			float tanHalf = MathF.Tan(CameraFovDegrees * (MathF.PI / 180f) * 0.5f);

			var view = Matrix4x4.CreateLookAtLeftHanded(_lastEye, _camera.Target, Vector3.UnitY);
			if (!Matrix4x4.Invert(view, out var invView))
			{
				return null;
			}

			var dirView = new Vector3(u * tanHalf * (size.X / size.Y), v * tanHalf, 1f);
			var dir = Vector3.Normalize(Vector3.TransformNormal(dirView, invView));
			var origin = _lastEye;

			float bestT = float.PositiveInfinity;
			int bestId = -1;

			foreach (var kvp in _rendered)
			{
				var record = kvp.Value;
				if (!record.Instantiated || string.IsNullOrEmpty(record.ResolvedPath) ||
					!_models.TryGetValue(record.ResolvedPath, out var state) || state.Model == null)
				{
					continue;
				}

				var model = state.Model;
				for (int i = 0; i < record.InstanceIndices.Count; i++)
				{
					var instance = model.instances[record.InstanceIndices[i]];
					if (instance.meshId < 0 || instance.meshId >= model.Meshes.Count)
					{
						continue;
					}

					var mesh = model.Meshes[instance.meshId];
					var t = ComposeInstanceTransform(instance.transform, record.LastWorld);
					var matrix = MathUtils.CreateTrs(t.position, t.rotation, t.scale);

					// Broadphase: сфера баундов меша в мире.
					var center = Vector3.Transform(mesh.Center, matrix);
					var maxScale = MathF.Max(MathF.Abs(t.scale.X), MathF.Max(MathF.Abs(t.scale.Y), MathF.Abs(t.scale.Z)));
					var radius = mesh.Radius * maxScale;
					if (!RayIntersectsSphere(origin, dir, center, radius, out var sphereT) || sphereT >= bestT)
					{
						continue;
					}

					// Точная фаза: луч в локальном пространстве инстанса против CPU-треугольников.
					// Без CPU-копий (не должно случаться для моделей превью-лоадера) - берём сферу.
					if (mesh.VertexData == null || mesh.IndexData == null || mesh.IndexCount < 3 ||
						!Matrix4x4.Invert(matrix, out var invMatrix))
					{
						if (sphereT < bestT)
						{
							bestT = sphereT;
							bestId = kvp.Key;
						}
						continue;
					}

					var lo = Vector3.Transform(origin, invMatrix);
					var ld = Vector3.TransformNormal(dir, invMatrix);

					int vertexCount = UnsafeArray.GetLength(mesh.VertexData);
					var vertices = new ReadOnlySpan<Vertex>(UnsafeArray.GetPtr<Vertex>(mesh.VertexData, 0), vertexCount);
					var indices = new ReadOnlySpan<uint>(UnsafeArray.GetPtr<uint>(mesh.IndexData, 0), mesh.IndexCount);

					for (int j = 0; j + 2 < indices.Length; j += 3)
					{
						uint j0 = indices[j], j1 = indices[j + 1], j2 = indices[j + 2];
						if (j0 >= vertexCount || j1 >= vertexCount || j2 >= vertexCount)
						{
							continue;
						}

						if (!RayIntersectsTriangle(lo, ld, vertices[(int)j0].Position,
								vertices[(int)j1].Position, vertices[(int)j2].Position, out var localT))
						{
							continue;
						}

						// t локального луча не сравним между инстансами (масштаб) - переводим точку
						// попадания обратно в мир и меряем вдоль мирового луча.
						var worldHit = Vector3.Transform(lo + ld * localT, matrix);
						var worldT = Vector3.Dot(worldHit - origin, dir);
						if (worldT > 0f && worldT < bestT)
						{
							bestT = worldT;
							bestId = kvp.Key;
						}
					}
				}
			}

			if (bestId < 0 || _lastStore == null)
			{
				return null;
			}

			var picked = _lastStore.GetEntityById(bestId);
			return picked.IsNull ? null : picked;
		}

		private static bool RayIntersectsSphere(Vector3 origin, Vector3 dir, Vector3 center, float radius, out float t)
		{
			t = 0f;
			var oc = center - origin;
			float proj = Vector3.Dot(oc, dir);
			float distSq = oc.LengthSquared() - proj * proj;
			float radiusSq = radius * radius;
			if (distSq > radiusSq)
			{
				return false;
			}

			float half = MathF.Sqrt(radiusSq - distSq);
			t = proj - half;
			if (t < 0f)
			{
				t = proj + half;
			}
			return t >= 0f;
		}

		/// <summary>Möller-Trumbore; ld может быть ненормированным (локальное пространство инстанса) -
		/// t возвращается в его единицах.</summary>
		private static bool RayIntersectsTriangle(Vector3 lo, Vector3 ld, Vector3 a, Vector3 b, Vector3 c, out float t)
		{
			t = 0f;
			var e1 = b - a;
			var e2 = c - a;
			var p = Vector3.Cross(ld, e2);
			float det = Vector3.Dot(e1, p);
			if (MathF.Abs(det) < 1e-12f)
			{
				return false;
			}

			float invDet = 1f / det;
			var s = lo - a;
			float bu = Vector3.Dot(s, p) * invDet;
			if (bu < 0f || bu > 1f)
			{
				return false;
			}

			var q = Vector3.Cross(s, e1);
			float bv = Vector3.Dot(ld, q) * invDet;
			if (bv < 0f || bu + bv > 1f)
			{
				return false;
			}

			t = Vector3.Dot(e2, q) * invDet;
			return t > 0f;
		}

		// --- Probe GI сцены -------------------------------------------------------------------------

		/// <summary>Гейт проб сцены: галка «Probe GI» окна Graphics - ТА ЖЕ, что у превью, - плюс
		/// HDR-тумблер тулбара (поле проб питает HDR-путь освещения, в LDR ему некуда светить).
		/// Раньше галка окна здесь игнорировалась вовсе: пробы сцены пеклись при снятой «Probe GI»,
		/// и выключить их можно было только выключив весь HDR.</summary>
		private bool ProbesEnabled => _editorSettings.PreviewProbeGi && _editorSettings.SceneViewHdr;

		/// <summary>Статус проб СЦЕНЫ для окна Graphics - раньше окно показывало только превью
		/// модели, и при работающем в сцене GI писало «нет проб».</summary>
		public string ProbeGiStatus
		{
			get
			{
				if (!ProbesEnabled)
				{
					return "выключен (нужен Scene View HDR)";
				}

				var s = _probeSession;
				if (s == null)
				{
					return "нет проб";
				}

				var grid = $"{s.CountX}x{s.CountY}x{s.CountZ}";
				if (_sceneGpu == null)
				{
					return $"{grid}, GPU-путь не поднялся (см. консоль)";
				}

				// Какой путь трассировки ЖИВОЙ. Снаружи это было невидимо: галка в окне Graphics
				// показывает ЖЕЛАНИЕ, а путь выбирается при подъёме сессии и законно может с ним не
				// совпадать: устройство не умеет inline-трассировки либо сессию ещё не перезавели.
				// Без этой строки одно от другого отличалось только под профайлером.
				grid += _sceneGpu.Hardware ? ", трассировка аппаратная"
					: ", трассировка программная";

				return s.Realtime
					? $"{grid}, реальное время"
					: s.Converged
						? $"{grid}, готово"
						: $"{grid}, раунд {s.Round}/{s.TargetRounds}";
			}
		}

		/// <summary>Цвет/интенсивность солнца для бейка - тот же keyIntensity, что у аналитического
		/// света (ProbeGiParams.z), иначе баунс разойдётся по яркости с прямым светом.</summary>
		private Vector3 ProbeSunColor() => ViewportSettingsPush.ProbeSunColor(_editorSettings);

		/// <summary>Принудительный ребейк проб сцены - кнопка «Rebake now» окна Graphics (зеркало
		/// ModelPreviewViewport.RequestProbeRebake; раньше кнопка перепекала только превью, а сессия
		/// сцены жила старым полем до первого структурного изменения).</summary>
		public void RequestProbeRebake() => RequestProbeSession(0f);

	}
}
