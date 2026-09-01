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
	/// <summary>SSR сцены: RT-сцена лучей, G-buffer отражений и пуш настроек/энвайронмента. Часть <see cref="PrefabSceneViewport"/> - файл на тему;
	/// состояние, конструктор и кадровые Update/Render живут в основном файле.</summary>
	public partial class PrefabSceneViewport
	{
		/// <summary>Доступен ли RT-фолбэк SSR прямо сейчас: галка настроек + inline-трассировка +
		/// СУЩЕСТВУЮЩИЙ accel сцены (ProbeSceneAccel живёт при аппаратном probe GI - его TLAS и
		/// таблицы атрибутов и питают лучи отражений). Без accel-а фича молча остаётся экранной.</summary>
		private bool SsrRayTracedEnabled() =>
			_editorSettings.SsrRayTraced &&
			_graphicsApi.RayTracing >= RayTracingSupport.Inline &&
			(_sceneAccel != null || _ssrOwnAccel != null);

		/// <summary>Привязывает TLAS и таблицы атрибутов сцены RT-варианту SSR-трейса. Зовётся после
		/// каждого создания/пересоздания <see cref="_sceneAccel"/> и после смены фич: дескриптор
		/// указывает на сам объект TLAS, но ПЕРЕСОЗДАНИЕ объекта привязку протухает.</summary>
		private void UpdateSsrRayScene()
		{
			var accel = _sceneAccel ?? _ssrOwnAccel;
			if (accel != null)
			{
				_env.Pipeline.SsrResources?.SetRayScene(accel.Tlas, accel.MeshTriangles,
					accel.Instances);
				PushSsrHitTextures();
			}
		}

		/// <summary>Привязывает трейсу набор текстур RT-хитов ТОГО accel-а, что ушёл в SetRayScene
		/// (индексы текстур в его таблице инстансов указывают именно в этот набор). Зовётся вместе
		/// с каждым SetRayScene и при апгрейдах стриминга (см. PollSsrOwnRayScene).</summary>
		private void PushSsrHitTextures()
		{
			var ssr = _env.Pipeline.SsrResources;
			if (ssr is not { RayTraced: true } || ssr.HitTextureMode == 0)
			{
				return;
			}

			var set = _sceneAccel != null ? _sceneAccelHitTextures : _ssrOwnHitTextures;
			if (set == null)
			{
				ssr.SetHitTextures(null, null);
			}
			else if (ssr.HitTextureMode == 1)
			{
				ssr.SetHitTextures(set.GetAtlas(), null);
			}
			else
			{
				ssr.SetHitTextures(null, set.GetFullTextures());
			}
		}

		/// <summary>Ведёт собственный accel SSR (см. поля у _ssrOwnAccel): нужен, только когда
		/// RT-фолбэк включён, а accel-а проб нет. Пересборка синхронная и дорогая (BLAS всей
		/// сцены) - с дебаунсом по сменам состава/поз. Зовётся каждый кадр из Update.</summary>
		private void PollSsrOwnRayScene(float deltaTime)
		{
			bool wanted = _editorSettings.PreviewSsr && _editorSettings.SsrRayTraced
				&& _graphicsApi.RayTracing >= RayTracingSupport.Inline
				&& _sceneAccel == null;

			var sceneModels = new List<(ModelLoader Model, Matrix4x4 World)>();
			if (wanted)
			{
				foreach (var record in _rendered.Values)
				{
					if (record.Instantiated && !string.IsNullOrEmpty(record.ResolvedPath) &&
						_models.TryGetValue(record.ResolvedPath, out var state) && state.Model != null)
					{
						sceneModels.Add((state.Model, record.LastWorld));
					}
				}
			}

			// Стриминг дорастил текстуру - bindless-привязка указывает на старую, перепушиваем
			// (наборы обоих accel-ов; проверка - сравнение счётчиков, копейки).
			if (_ssrOwnHitTextures?.RefreshStreams() == true ||
				_sceneAccelHitTextures?.RefreshStreams() == true)
			{
				PushSsrHitTextures();
			}

			if (!wanted || sceneModels.Count == 0)
			{
				if (_ssrOwnAccel != null)
				{
					// Возврат на accel проб / выключение / опустевшая сцена: трейс-материал не
					// должен держать умирающий TLAS (и отражать призрак старой сцены).
					_env.Pipeline.SsrResources?.SetHitTextures(null, null);
					_env.DilApi.ImmediateContext.Flush();
					_env.DilApi.ImmediateContext.WaitForIdle();
					_ssrOwnAccel.Dispose();
					_ssrOwnAccel = null;
					_ssrOwnBuiltFor = null;
					_ssrOwnHitTextures?.Dispose();
					_ssrOwnHitTextures = null;
					ApplyPipelineFeatures();
				}

				_ssrOwnRebuildDelay = -1f;
				return;
			}

			if (_ssrOwnAccel != null && SameScenePoses(_ssrOwnBuiltFor, sceneModels))
			{
				_ssrOwnRebuildDelay = -1f;
				return;
			}

			// Дебаунс: драг гизмо меняет позы каждый кадр, а пересборка - BLAS всей сцены.
			if (_ssrOwnRebuildDelay < 0f)
			{
				_ssrOwnRebuildDelay = 0.4f;
				return;
			}

			_ssrOwnRebuildDelay -= deltaTime;
			if (_ssrOwnRebuildDelay > 0f)
			{
				return;
			}

			_ssrOwnRebuildDelay = -1f;

			try
			{
				var geometry = new ProbeGiBaker(sceneModels).InstancedGeometry;
				if (geometry.Instances.Length == 0)
				{
					// Геометрии нет (CPU-копии мешей недоступны) - тихий пропуск с бэкоффом.
					_ssrOwnRebuildDelay = 5f;
					return;
				}

				_env.DilApi.ImmediateContext.Flush();
				_env.DilApi.ImmediateContext.WaitForIdle();
				_ssrOwnAccel?.Dispose();
				_ssrOwnAccel = new ProbeSceneAccel(_env.DilApi, geometry);
				_ssrOwnBuiltFor = sceneModels;

				// Набор текстур хитов сшит с индексами ЭТОЙ геометрии - пересобирается вместе с ней.
				_ssrOwnHitTextures?.Dispose();
				var hitModels = new List<ModelLoader>(sceneModels.Count);
				foreach (var (m, _) in sceneModels)
				{
					hitModels.Add(m);
				}
				_ssrOwnHitTextures = SsrHitTextures.Build(_graphicsApi, geometry, hitModels);

				// Фича могла ждать появления accel-а (SsrRayTracedEnabled), ресурсы -
				// пересборки под RT-вариант; привязка внутри ApplyPipelineFeatures.
				ApplyPipelineFeatures();
			}
			catch (Exception ex)
			{
				EngineLog.Add(LogLevel.Warning,
					$"SSR: собственный accel сцены не собрался: {ex.Message}");
				_ssrOwnAccel?.Dispose();
				_ssrOwnAccel = null;
				_ssrOwnBuiltFor = null;

				// Бэкофф: причина не исчезнет через кадр - не молотим пересборкой.
				_ssrOwnRebuildDelay = 5f;
			}
		}

		/// <summary>Живые ручки SSR из настроек. Отдельным методом, потому что зовётся из ДВУХ мест:
		/// применения настроек и <see cref="ApplyPipelineFeatures"/> - смена RT-фолбэка пересоздаёт
		/// SSR-ресурсы (в трейс запечён вариант шейдера), и без повторного пуша ручки откатывались бы
		/// в дефолты.</summary>
		private void PushSsrSettings()
		{
			_env.SetSsrParams(
				Math.Clamp(_editorSettings.SsrIntensity, 0f, 4f),
				Math.Clamp(_editorSettings.SsrMaxRoughness, 0.05f, 1f),
				Math.Clamp(_editorSettings.SsrThickness, 0.01f, 5f),
				Math.Clamp(_editorSettings.SsrMaxDistance, 1f, 500f),
				Math.Clamp(_editorSettings.SsrHistoryWeight, 0f, 0.97f),
				Math.Clamp(_editorSettings.SsrRaysPerPixel, 1, 4),
				_editorSettings.SsrDebugView,
				Math.Clamp(_editorSettings.SsrRtBounces, 1, 4),
				Math.Clamp(_editorSettings.SsrTraceMode, 0, 1));
			PushSsrEnvironment();
		}

		/// <summary>Покадровые данные SSR: поворот env-карты (композит вычитает ровно тот env-цвет,
		/// что вложил форвард) и солнце RT-фолбэка. Цвет солнца - константа дневного света: точный
		/// вклад ключа в RT-хиты не воспроизвести без полного шейдинга, а для отражений вне экрана
		/// достаточно правдоподобной яркости.</summary>
		private void PushSsrEnvironment()
		{
			var shadowSettings = _env.ShadowSettings;
			if (shadowSettings == null)
			{
				return;
			}

			// Цвет ключа - тот же, что у прямого света превью (SimpleCullingAndRenderSystem,
			// LightColor (1, 0.97, 0.9) x intensity 1); ambient-вес - ambientLevel форвард-пасса
			// при мировом свете (0.55, см. UnlitInstancedPS) - RT-хиты и экранные пиксели теперь
			// освещаются одной и той же моделью.
			// Угловой размер солнца (та же ручка, что у PCSS прямого вида) - мягкость края тени
			// у RT-хитов: без неё теневой луч бинарный, и в отражении край тени рвался в чёрное.
			float sunTanHalfAngle = MathF.Tan(
				Math.Clamp(_editorSettings.SunAngularSize, 0.01f, 20f) * 0.5f * MathF.PI / 180f);

			_env.SetSsrEnvironment(shadowSettings.EnvYawRadians,
				-Vector3.Normalize(shadowSettings.LightDirection),
				new Vector3(1f, 0.97f, 0.9f), 0.55f, sunTanHalfAngle);
		}

	}
}
