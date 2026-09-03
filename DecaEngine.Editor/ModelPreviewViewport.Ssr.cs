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
	/// <summary>Preview SSR: its own ray-tracing scene and hit textures.</summary>
	public partial class ModelPreviewViewport
	{
		// The RT fallback needs ANY live accel: the probe one (preferred) or its own.
		private bool SsrRayTracedEnabled() =>
			_editorSettings.SsrRayTraced &&
			_graphicsApi.RayTracing >= RayTracingSupport.Inline &&
			(_probeAccel != null || _ssrOwnAccel != null);

		// Preview's own accel: RT reflection fallback when probe GI is off.
		private ProbeSceneAccel? _ssrOwnAccel;
		private ModelLoader? _ssrOwnBuiltFor;
		private float _ssrOwnRetryDelay;

		// One hit-texture set per accel, living as long as it does.
		private SsrHitTextures? _probeAccelHitTextures;
		private SsrHitTextures? _ssrOwnHitTextures;

		// Polled rather than hooked to load events: the hooks missed the cook-cache and sub-mesh paths.
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

			// Streaming grew a texture: the bindless binding still points at the old one.
			if (_ssrOwnHitTextures?.RefreshStreams() == true ||
				_probeAccelHitTextures?.RefreshStreams() == true)
			{
				PushSsrHitTextures();
			}
		}

		// Must be called BEFORE SetFeatures: the RT feature predicate reads the accel.
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
					// No geometry: skip quietly instead of throwing every 0.3 s.
					return;
				}

				_ssrOwnAccel = new ProbeSceneAccel(_env.DilApi, geometry);
				_ssrOwnBuiltFor = _residentModel;

				// The hit-texture set is indexed against THIS geometry.
				_ssrOwnHitTextures?.Dispose();
				_ssrOwnHitTextures = SsrHitTextures.Build(_graphicsApi, geometry,
					new[] { _residentModel! });
			}
			catch (Exception ex)
			{
				EngineLog.Add(LogLevel.Warning,
					$"SSR: the model's own accel failed to build: {ex.Message}");
				_ssrOwnAccel?.Dispose();
				_ssrOwnAccel = null;
				_ssrOwnBuiltFor = null;

				// Back off: the cause will not disappear within a frame.
				_ssrOwnRetryDelay = 5f;
			}
		}

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

		// Pushes the hit-texture set of the accel that was handed to SetRayScene.
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
