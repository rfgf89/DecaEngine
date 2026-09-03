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
	/// <summary>Scene SSR: ray scene, reflection G-buffer and settings/environment push. Part of
	/// <see cref="PrefabSceneViewport"/>; state and per-frame Update/Render live in the main file.</summary>
	public partial class PrefabSceneViewport
	{
		/// <summary>RT fallback availability: settings flag + inline tracing + an EXISTING scene accel
		/// (probe GI's TLAS and attribute tables feed reflection rays). Without one, SSR stays screen-space.</summary>
		private bool SsrRayTracedEnabled() =>
			_editorSettings.SsrRayTraced &&
			_graphicsApi.RayTracing >= RayTracingSupport.Inline &&
			(_sceneAccel != null || _ssrOwnAccel != null);

		/// <summary>Binds the scene TLAS and attribute tables to the RT SSR trace. Must be re-called
		/// after every accel recreation: the descriptor points at the TLAS object, and recreating
		/// the object stales the binding.</summary>
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

		/// <summary>Pushes the RT-hit texture set of the SAME accel passed to SetRayScene (its
		/// instance table indexes into this set). Called with every SetRayScene and on streaming
		/// upgrades (see PollSsrOwnRayScene).</summary>
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

		/// <summary>Maintains SSR's own accel, needed only when the RT fallback is on but the probe
		/// accel is absent. Rebuild is synchronous and expensive (whole-scene BLAS), so it is
		/// debounced on composition/pose changes. Called every frame from Update.</summary>
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

			// Streaming grew a texture: the bindless binding still points at the old one, re-push.
			if (_ssrOwnHitTextures?.RefreshStreams() == true ||
				_sceneAccelHitTextures?.RefreshStreams() == true)
			{
				PushSsrHitTextures();
			}

			if (!wanted || sceneModels.Count == 0)
			{
				if (_ssrOwnAccel != null)
				{
					// The trace material must not hold a dying TLAS (or reflect the old scene).
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

			// Debounce: gizmo drags change poses every frame, and a rebuild is a whole-scene BLAS.
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
					// No geometry (CPU mesh copies unavailable): silent skip with backoff.
					_ssrOwnRebuildDelay = 5f;
					return;
				}

				_env.DilApi.ImmediateContext.Flush();
				_env.DilApi.ImmediateContext.WaitForIdle();
				_ssrOwnAccel?.Dispose();
				_ssrOwnAccel = new ProbeSceneAccel(_env.DilApi, geometry);
				_ssrOwnBuiltFor = sceneModels;

				// Hit texture set is tied to THIS geometry's indices - rebuilt with it.
				_ssrOwnHitTextures?.Dispose();
				var hitModels = new List<ModelLoader>(sceneModels.Count);
				foreach (var (m, _) in sceneModels)
				{
					hitModels.Add(m);
				}
				_ssrOwnHitTextures = SsrHitTextures.Build(_graphicsApi, geometry, hitModels);

				// Features may have been waiting for the accel; binding happens inside.
				ApplyPipelineFeatures();
			}
			catch (Exception ex)
			{
				EngineLog.Add(LogLevel.Warning,
					$"SSR: the scene's own accel failed to build: {ex.Message}");
				_ssrOwnAccel?.Dispose();
				_ssrOwnAccel = null;
				_ssrOwnBuiltFor = null;

				// Backoff: the cause will not vanish next frame.
				_ssrOwnRebuildDelay = 5f;
			}
		}

		/// <summary>Live SSR knobs. Separate method because it is called from TWO places: settings
		/// apply and <see cref="ApplyPipelineFeatures"/> - toggling the RT fallback recreates SSR
		/// resources (shader variant is baked in), which would reset the knobs to defaults.</summary>
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

		/// <summary>Per-frame SSR data: env-map yaw (the composite subtracts exactly the env color
		/// forward added) and the RT-fallback sun. Sun color is a daylight constant: exact key
		/// contribution to RT hits is not reproducible without full shading.</summary>
		private void PushSsrEnvironment()
		{
			var shadowSettings = _env.ShadowSettings;
			if (shadowSettings == null)
			{
				return;
			}

			// Key color matches the preview direct light (SimpleCullingAndRenderSystem); ambient
			// weight matches the forward pass ambientLevel under world light (0.55, UnlitInstancedPS)
			// so RT hits and screen pixels share one lighting model. Sun angular size (same knob as
			// PCSS) softens RT-hit shadow edges - a binary shadow ray tears to black otherwise.
			float sunTanHalfAngle = MathF.Tan(
				Math.Clamp(_editorSettings.SunAngularSize, 0.01f, 20f) * 0.5f * MathF.PI / 180f);

			_env.SetSsrEnvironment(shadowSettings.EnvYawRadians,
				-Vector3.Normalize(shadowSettings.LightDirection),
				new Vector3(1f, 0.97f, 0.9f), 0.55f, sunTanHalfAngle);
		}

	}
}
