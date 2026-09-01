using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using DecaEngine.Core;
using DecaEngine.Core.Diagnostics;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
using DecaEngine.Graphics.ProbeGi;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using Hexa.NET.ImGui;
using DecaEngine.Scene;
using DecaEngine.Animation;

namespace DecaEngine.Editor
{
	/// <summary>SSR превью: собственная RT-сцена лучей и текстуры хитов. Часть <see cref="ModelPreviewViewport"/> - файл на тему;
	/// состояние, конструктор и кадровые Update/Render живут в основном файле.</summary>
	public partial class ModelPreviewViewport
	{
		/// <summary>Зеркало PrefabSceneViewport.SsrRayTracedEnabled: RT-фолбэку нужен ЛЮБОЙ живой
		/// accel - проб (предпочтителен) или собственный (см. EnsureSsrOwnRayScene).</summary>
		private bool SsrRayTracedEnabled() =>
			_editorSettings.SsrRayTraced &&
			_graphicsApi.RayTracing >= RayTracingSupport.Inline &&
			(_probeAccel != null || _ssrOwnAccel != null);

		// Собственный accel SSR превью - RT-фолбэк отражений без probe GI (зеркало
		// PrefabSceneViewport._ssrOwnAccel, но проще: модель одна и статичная).
		private ProbeSceneAccel? _ssrOwnAccel;
		private ModelLoader? _ssrOwnBuiltFor;
		private float _ssrOwnRetryDelay;

		// Наборы текстур RT-хитов (текстурное альбедо отражений) - по одному на accel, живут
		// вместе с ним (зеркало PrefabSceneViewport).
		private SsrHitTextures? _probeAccelHitTextures;
		private SsrHitTextures? _ssrOwnHitTextures;

		/// <summary>Покадровый привод собственного accel-а SSR: хуки на события загрузки ловили не
		/// все пути (кук-кеш, смена суб-меша), и статус «accel не собран» висел вечно. Опрос дешёвый
		/// (сравнение ссылок), сборка - с дебаунсом; смена доступности accel-а перечитывает фичи.</summary>
		private void PollSsrOwnRayScene(float deltaTime)
		{
			_ssrOwnRetryDelay -= deltaTime;
			if (_ssrOwnRetryDelay > 0f)
			{
				return;
			}

			_ssrOwnRetryDelay = 0.3f;

			bool hadAccel = _ssrOwnAccel != null;
			EnsureSsrOwnRayScene();
			if ((_ssrOwnAccel != null) != hadAccel)
			{
				ApplyPipelineFeatures();
			}

			// Стриминг дорастил текстуру - bindless-привязка указывает на старую, перепушиваем.
			if (_ssrOwnHitTextures?.RefreshStreams() == true ||
				_probeAccelHitTextures?.RefreshStreams() == true)
			{
				PushSsrHitTextures();
			}
		}

		/// <summary>Собирает/освобождает собственный accel SSR под текущую модель. Зовётся из
		/// ApplyPipelineFeatures ДО SetFeatures (предикат RT смотрит на accel) и после загрузки
		/// модели.</summary>
		private void EnsureSsrOwnRayScene()
		{
			bool wanted = _editorSettings.PreviewSsr && _editorSettings.SsrRayTraced
				&& _graphicsApi.RayTracing >= RayTracingSupport.Inline
				&& _probeAccel == null && _residentModel != null;

			if (!wanted || !ReferenceEquals(_ssrOwnBuiltFor, _residentModel))
			{
				if (_ssrOwnAccel != null)
				{
					_env.Pipeline.SsrResources?.SetHitTextures(null, null);
					_env.DilApi.ImmediateContext.Flush();
					_env.DilApi.ImmediateContext.WaitForIdle();
					_ssrOwnAccel.Dispose();
					_ssrOwnAccel = null;
					_ssrOwnBuiltFor = null;
					_ssrOwnHitTextures?.Dispose();
					_ssrOwnHitTextures = null;
				}

				if (!wanted)
				{
					return;
				}
			}

			if (_ssrOwnAccel != null)
			{
				return;
			}

			try
			{
				var geometry = new ProbeGiBaker(_residentModel!).InstancedGeometry;
				if (geometry.Instances.Length == 0)
				{
					// Геометрии нет (CPU-копии мешей недоступны/модель пустая) - тихий пропуск,
					// а не исключение каждые 0.3 с.
					return;
				}

				_ssrOwnAccel = new ProbeSceneAccel(_env.DilApi, geometry);
				_ssrOwnBuiltFor = _residentModel;

				// Набор текстур хитов сшит с индексами ЭТОЙ геометрии.
				_ssrOwnHitTextures?.Dispose();
				_ssrOwnHitTextures = SsrHitTextures.Build(_graphicsApi, geometry,
					new[] { _residentModel! });
			}
			catch (Exception ex)
			{
				EngineLog.Add(LogLevel.Warning,
					$"SSR: собственный accel модели не собрался: {ex.Message}");
				_ssrOwnAccel?.Dispose();
				_ssrOwnAccel = null;
				_ssrOwnBuiltFor = null;

				// Бэкофф: причина (нет CPU-копий и т.п.) не исчезнет через кадр - не молотим.
				_ssrOwnRetryDelay = 5f;
			}
		}

		/// <summary>Зеркало PrefabSceneViewport.UpdateSsrRayScene.</summary>
		private void UpdateSsrRayScene()
		{
			var accel = _probeAccel ?? _ssrOwnAccel;
			if (accel != null)
			{
				_env.Pipeline.SsrResources?.SetRayScene(accel.Tlas, accel.MeshTriangles,
					accel.Instances);
				PushSsrHitTextures();
			}
		}

		/// <summary>Зеркало PrefabSceneViewport.PushSsrHitTextures: набор ТОГО accel-а, что ушёл в
		/// SetRayScene.</summary>
		private void PushSsrHitTextures()
		{
			var ssr = _env.Pipeline.SsrResources;
			if (ssr is not { RayTraced: true } || ssr.HitTextureMode == 0)
			{
				return;
			}

			var set = _probeAccel != null ? _probeAccelHitTextures : _ssrOwnHitTextures;
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

	}
}
