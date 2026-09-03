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
	/// <summary>Applies the Graphics window to the scene: pipeline features, live knobs, env rebuild.</summary>
	public partial class PrefabSceneViewport
	{
		private void ApplyPendingUpscalerSettings()
		{
			if (!_pendingUpscalerApply || _env is null)
			{
				return;
			}

			_pendingUpscalerApply = false;
			ViewportSettingsPush.Upscaler(_env, _editorSettings);
		}

		// Camera basis from the last Update; Render reuses it so the gizmo lands pixel-exact.
		private Vector3 _lastEye;

		public ImGuizmoOperation Operation { get; set; } = ImGuizmoOperation.Translate;

		/// <summary>Current shading mode.</summary>
		public ShadingMode Shading => _shading;

		/// <summary>Light slider offsets from the environment's base sun position, in degrees.</summary>
		public float LightYawDegrees => _lightYawOffsetDegrees;
		public float LightElevationDegrees => _lightElevationOffsetDegrees;

		/// <summary>Whether the scene has at least one rendered model instance.</summary>
		public bool HasContent
		{
			get
			{
				foreach (var record in _rendered.Values)
				{
					if (record.EnvEntities.Count > 0)
					{
						return true;
					}
				}
				return false;
			}
		}

		private void ApplyPipelineFeatures()
		{
			_appliedSsao = _editorSettings.PreviewSsao;
			_appliedAoMode = _editorSettings.PreviewAoMode;
			_appliedSsgi = _editorSettings.PreviewSsgi;
			_appliedSky = _editorSettings.PreviewSkyBackground;
			_appliedSceneHdr = _editorSettings.SceneViewHdr;
			_appliedFog = _editorSettings.PreviewFog;
			_appliedVolumetric = _editorSettings.PreviewVolumetric;
			_appliedBloom = _editorSettings.PreviewBloom;
			_appliedColorGrade = _editorSettings.PreviewColorGrade;
			_appliedMotionVectors = _editorSettings.PreviewMotionVectors;

			_env.SetFeatures(new PipelineFeatures
			{
				SkyBackground = _appliedSky,
				Ssao = _appliedSsao,
				AoMode = _appliedAoMode,
				Ssgi = _appliedSsgi,
				EyeAdaptation = _appliedSceneHdr && _editorSettings.PreviewEyeAdaptation,
				Fog = _appliedFog,
				Volumetric = _appliedVolumetric,
				Bloom = _appliedBloom,
				ColorGrade = _appliedColorGrade,
				// SSR requires motion vectors.
				MotionVectors = _appliedMotionVectors || _editorSettings.PreviewSsr,
				TemporalUpscale = _appliedMotionVectors && _editorSettings.TemporalUpscale,
				Ssr = _editorSettings.PreviewSsr,
				SsrRayTraced = SsrRayTracedEnabled(),
				SsrHitTextures = _editorSettings.SsrHitTextures,
			});

			// The RT trace variant must get the scene TLAS before its first frame.
			UpdateSsrRayScene();
			_env.SetSsrProbeField(_probeTextures);

			// Switching the RT fallback recreated SSR resources, resetting live knobs to defaults.
			PushSsrSettings();
		}


		/// <summary>null when RT SSR is live, else why it silently stayed screen-space.</summary>
		public string? SsrRayTracedBlockReason
		{
			get
			{
				if (_graphicsApi.RayTracing < RayTracingSupport.Inline)
				{
					return "no inline tracing (D3D12 required)";
				}
				if (_sceneAccel == null && _ssrOwnAccel == null)
				{
					return "the scene's accel is not built yet (scene is empty or still loading)";
				}
				if (_env.Pipeline.SsrResources is not { RayTraced: true })
				{
					return "SSR resources have not been rebuilt for the RT variant yet";
				}
				return null;
			}
		}

		// Env-level changes rebuild the environment lazily at the start of Update: mid-ImGui-frame
		// the old target may still sit in a draw list.
		private void OnGraphicsSettingsChanged()
		{
			// Only options baked outside the pipeline force a rebuild; the rest are live features.
			bool needsRecreate =
				_appliedHdrPath != (ResolveEnvironmentHdrPath(_editorSettings.PreviewEnvironmentHdr) ?? "") ||
				_appliedAniso != _editorSettings.PreviewAnisotropicFiltering ||
				// Texture ceiling is baked at load time, so models must be re-read, not just the
				// pipeline rebuilt.
				_appliedMaxTextureSize != ClampedMaxTextureSize() ||
				// RT shadows are a shader keyword, so crossing the mode boundary re-reads the scene.
				_appliedRtShadows != RtShadowsEnabled();

			_pendingEnvironmentRecreate |= needsRecreate;

			if (!needsRecreate)
			{
				ApplyPipelineFeatures();
			}

			// Bake knobs restart the probe session (debounced); real-time knobs are not listed here.
			var wantedBake = (_editorSettings.PreviewProbeGi,
				_editorSettings.ProbeGiSkyIntensity,
				_editorSettings.ProbeGiRaysPerProbe,
				_editorSettings.ProbeGiBounces,
				_editorSettings.ProbeGiBounceSaturation,
				_editorSettings.ProbeGiGridDensity,
				_editorSettings.ProbeGiMaxProbes,
				// The trace path is chosen once when the GPU set comes up, so this must live here
				// or the toggle would do nothing until some other knob forces a rebake.
				_editorSettings.ProbeGiHardwareRayTracing,
				// Visibility octahedral map side - drives atlas layout.
				_editorSettings.ProbeGiVisRes);
			if (wantedBake != _appliedProbeBake)
			{
				_appliedProbeBake = wantedBake;
				RequestProbeSession(0.25f);
			}

			ApplyGraphicsSettings();
		}

		// Snapshot of the bake knobs the current scene probe session was started with.
		private (bool On, float Sky, int Rays, int Bounces, float Sat, float Density, int Max,
			bool HardwareTrace, int VisRes) _appliedProbeBake;

		// Rebuilds the environment without reloading the scene: resident models migrate by
		// re-registration. Anisotropy changes are the exception - they bake into samplers at load.
		private void RecreateEnvironment()
		{
			// Frames holding old-environment resources may be in flight; releasing without a GPU
			// wait crashes the driver.
			_env.DilApi.ImmediateContext.Flush();
			_env.DilApi.ImmediateContext.WaitForIdle();

			if (_textureBound && _lastImGuiRender != null)
			{
				_lastImGuiRender.ReleaseRenderTargetBinding(_textureRef.GetTexID());
				_textureBound = false;
			}

			// The selection overlay held targets and PSOs of the old environment.
			_selectionOverlay?.Dispose();
			_selectionOverlay = null;
			_highlightedId = -1;

			// Probe atlases are bound into materials and must not outlive the environment.
			ResetProbeGi();

			// Records pointed at the old EntityStore, which is freed wholesale - drop them without
			// Unregister; SyncScene rebuilds. The camera stays: this is not a scene change.
			_rendered.Clear();
			_lightMirrors.Clear();
			_transformsDirty = false;
			_structuralDirtySelection = false;
			_physicsStaticsDirty = true;

			bool dropModels = _appliedAniso != _editorSettings.PreviewAnisotropicFiltering ||
				_appliedMaxTextureSize != ClampedMaxTextureSize() ||
				// RT shadows are a shader keyword: resident models are compiled for another set,
				// so re-registration will not do - re-read them.
				_appliedRtShadows != RtShadowsEnabled();

			// The RT shadow TLAS holds BLASes over the dying batch renderer's meshes.
			_rtShadowScene?.Release();
			_rtShadowScene = null;

			// SSR's own accel is bound to the dying environment's materials.
			_ssrOwnAccel?.Dispose();
			_ssrOwnAccel = null;
			_ssrOwnBuiltFor = null;

			// The debug overlay holds buffers and PSOs of the dying pipeline.
			ReleaseDebugOverlay();

			_env.Release();
			_env = CreateEnvironment();
			_env.Root.Add(new ModelStreamingSystem(_streamer));
			ApplyLightRotation();

			_streamer.MigrateEnvironment(_env, dropModels);

			ApplyGraphicsSettings();
		}

		// World-space AO/SSGI radii from scene bounds. Call only after a GPU barrier.
		private void PushPostProcessRanges()
		{
			float radius = 0f;
			if (TryComputeSceneBounds(out var min, out var max))
			{
				radius = MathF.Max(0.05f, (max - min).Length() * 0.5f);
			}

			var aoWorld = _editorSettings.AoRadiusWorld;
			var aoRange = aoWorld > 0f
				? Math.Clamp(aoWorld, 0.01f, 1000f)
				: radius * Math.Clamp(_editorSettings.AoRadiusFraction, 0.01f, 1f);
			if (aoRange > 0f)
			{
				_env.SetAoWorldRange(aoRange);
			}

			var giWorld = _editorSettings.SsgiRadiusWorld;
			var giRange = giWorld > 0f
				? Math.Clamp(giWorld, 0.01f, 1000f)
				: radius * Math.Clamp(_editorSettings.SsgiRadiusFraction, 0.01f, 2f);
			if (giRange > 0f)
			{
				_env.SetGiWorldRange(giRange);
			}
		}

		// --- Graphics/shading settings --------------------------------------------------------------

		private void ApplyBloomSettings() => ViewportSettingsPush.Bloom(_env, _editorSettings);

		private void ApplyColorGradeSettings() => ViewportSettingsPush.ColorGrade(_env, _editorSettings);

		private void ApplyFogSettings() => ViewportSettingsPush.Fog(_env, _editorSettings);

		private void ApplyVolumetricSettings() => ViewportSettingsPush.Volumetric(_env, _editorSettings);

		private void ApplyGraphicsSettings()
		{
			PushPostProcessRanges();

			// Streaming off means an infinite radius: every scene model stays resident.
			_streamer.StreamRadius = _editorSettings.SceneStreaming
				? MathF.Max(1f, _editorSettings.SceneStreamingRadius)
				: float.PositiveInfinity;

			// Skinning is read at model instantiation, so already-shown models keep their setting
			// until the prefab is reopened. DECA_SKINNING=0 overrides the setting as an escape hatch.
			if (System.Environment.GetEnvironmentVariable("DECA_SKINNING") != "0")
			{
				ModelViewportGeometry.SkinningEnabled = _editorSettings.SceneSkinning;
			}

			// UAV on the vertex mega-buffer only while skinning is on, so turning it off really
			// restores the original buffer description.
			DiligentBatchRenderer.SkinningUav = ModelViewportGeometry.SkinningEnabled;

			ApplyFogSettings();
			ApplyVolumetricSettings();
			ApplyBloomSettings();
			ApplyColorGradeSettings();
			_env.SetToneCurve(_editorSettings.ToneCurve);

			var flags = PreviewFeatureFlags.None;
			if (_editorSettings.PreviewNormalMaps)
			{
				flags |= PreviewFeatureFlags.NormalMaps;
			}
			if (_editorSettings.PreviewBakedOcclusion)
			{
				flags |= PreviewFeatureFlags.Occlusion;
			}
			if (_editorSettings.PreviewShadows)
			{
				flags |= PreviewFeatureFlags.Shadows;
			}

			// From the environment actually created, not the setting: HDR is restart-level, and
			// until the rebuild the shader must keep writing display space.
			if (_env.HdrOutput)
			{
				flags |= PreviewFeatureFlags.HdrOutput;
			}
			_featureFlags = flags;

			if (_env.ShadowSettings != null)
			{
				_env.ShadowSettings.Enabled = _editorSettings.PreviewShadows;
			}

			// Keep the luminance bounds ordered: an inverted range locks exposure solid.
			var eaMin = Math.Clamp(_editorSettings.EyeAdaptationMinLuminance, 0.0001f, 100f);
			var eaMax = Math.Max(Math.Clamp(_editorSettings.EyeAdaptationMaxLuminance, 0.0001f, 100f), eaMin);
			_env.SetEyeAdaptationParams(
				Math.Clamp(_editorSettings.EyeAdaptationKey, 0.01f, 2f),
				eaMin,
				eaMax,
				Math.Clamp(_editorSettings.EyeAdaptationExposureCompensation, -8f, 8f));
			_env.SetEyeAdaptationSpeed(
				Math.Clamp(_editorSettings.EyeAdaptationSpeedUp, 0.05f, 20f),
				Math.Clamp(_editorSettings.EyeAdaptationSpeedDown, 0.05f, 20f));

			_env.SetAoStrength(Math.Clamp(_editorSettings.AoStrength, 0.1f, 4f),
				Math.Clamp(_editorSettings.AoFloor, 0f, 1f));
			_env.SetAoDebugView(_editorSettings.AoDebugView);

			_env.SetMotionVectorDebug(_editorSettings.MotionVectorDebugView,
				Math.Clamp(_editorSettings.MotionVectorDebugRange, 0.25f, 256f));
			_env.SetTemporalJitter(_editorSettings.TemporalJitter);

			// Deferred to the start of Update: the switch waits on the GPU and issues NGX init
			// commands, which crashes mid-ImGui-frame. Render scale is applied in TrackAndApplyResize
			// for the same reason.
			_pendingUpscalerApply = true;

			_env.SetGiParams(
				Math.Clamp(_editorSettings.SsgiIntensity, 0f, 4f),
				Math.Clamp(_editorSettings.SsgiSamples, 4, SsgiPassResources.MaxSampleCount),
				Math.Max(0f, _editorSettings.SsgiMaxLuminance),
				Math.Clamp(_editorSettings.SsgiSaturation, 0f, 1f));
			_env.SetGiCompositeParams(
				Math.Clamp(_editorSettings.SsgiBlurRadius, 0, SsgiPassResources.MaxBlurRadius),
				_editorSettings.SsgiDebugView);

			PushSsrSettings();

			ApplyMaterialSettings();

			// The shadow toggle changes the cascade record count in ShadowPass data, and its loop
			// is frozen into the graph commands - a rebuild is mandatory.
			_env.Pipeline.InvalidateGraph();
		}

		private void ApplyLightRotation()
		{
			var shadowSettings = _env.ShadowSettings;
			if (shadowSettings == null)
			{
				return;
			}

			shadowSettings.SetAngles(
				shadowSettings.BaseYawDegrees + _lightYawOffsetDegrees,
				Math.Clamp(shadowSettings.BaseElevationDegrees + _lightElevationOffsetDegrees,
					LightElevationMinDegrees, LightElevationMaxDegrees));

			_env.Pipeline.SkyResources?.SetEnvironmentYaw(shadowSettings.EnvYawRadians);
			PushSsrEnvironment();
			ApplyMaterialSettings();
		}

		private void ApplyMaterialSettings()
		{
			int mode = _shading switch
			{
				ShadingMode.Textured => 0,
				ShadingMode.Normal => 2,
				ShadingMode.Uv => 2,
				ShadingMode.Tangent => 2,
				_ => 3,
			};
			int channel = _shading switch
			{
				ShadingMode.Uv => 1,
				ShadingMode.Tangent => 2,
				// All debug channels below ride on top of Mode == 3 (the default arm above).
				ShadingMode.PunctualShadowDebug => PunctualDebugChannel,
				ShadingMode.ClusterDepthSlices => 20,
				ShadingMode.ClusterScreenTiles => 21,
				ShadingMode.ClusterLightCount => 14,
				ShadingMode.LightDepthReceiver => 22,
				ShadingMode.LightDepthOccluder => 23,
				ShadingMode.LightDepthGap => 24,
				ShadingMode.SunShadowCascades => 28,
				_ => 0,
			};

			// Priority: an explicit shading-combo channel wins, then probe placement (10), then
			// the field view (9).
			if (channel == 0)
			{
				if (_editorSettings.ProbeGiDebugProbes)
				{
					channel = 10;
				}
				else if (_editorSettings.ProbeGiDebugView)
				{
					channel = 9;
				}
			}

			// Debug views write display-ready values, so HDR must route them past exposure and the
			// tone curve. Tested per channel, not just mode: channels 11..21 ride on mode == 3, and
			// AoDebugView bypasses PreviewSettings entirely.
			_env.SetTonemapPassthrough(mode != 3 || channel != 0 || _editorSettings.AoDebugView);

			foreach (var state in _models.Values)
			{
				var model = state.Model;
				if (model == null)
				{
					continue;
				}

				var data = new PreviewSettingsData
				{
					// LDR only; in HDR the curve is applied by TonemapPass.
					ToneCurve = _editorSettings.ToneCurve,
					Mode = mode,
					Channel = channel,
					EnvYawRadians = _env.ShadowSettings?.EnvYawRadians ?? 0f,
					ShadowMode = _editorSettings.ShadowFilterMode,
					// The shader reads these even without probe GI (z is sun intensity).
					ProbeGiParams = new Vector4(
						Math.Clamp(_editorSettings.ProbeGiShadowFloor, 0f, 1f),
						Math.Clamp(_editorSettings.ProbeGiSpecularFloor, 0f, 1f),
						Math.Clamp(_editorSettings.ProbeGiSunIntensity, 0.1f, 16f),
						Math.Clamp(_editorSettings.ProbeGiAmbientBoost, 0.1f, 128f)),
					// y is the visibility octahedral map side; it must match the bake session exactly.
					ProbeGiParams2 = new Vector4(
						Math.Clamp(_editorSettings.ProbeGiSkyShadowFloor, 0.01f, 1f),
						ProbeGiBakeResult.VisRes, 0f, 0f),
				};

				// Origin.w == 1 is the shader-side toggle; all zeros means probe GI is off.
				if (_probeTextures != null && ProbesEnabled)
				{
					ProbeGiViewportShared.PushGrid(ref data, _probeTextures,
						_editorSettings.ProbeGiNormalBias, _editorSettings.ProbeGiViewBias);
				}

				// This environment's own material set: model.materialObjects may belong to another
				// environment (preview, icon baker), and pushing there would clobber its settings.
				var materials = state.Materials ?? model.materialObjects;
				for (int i = 0; i < materials.Count; i++)
				{
					var kvp = materials.GetAt(i);

					if (!model.MaterialPbr.TryGetValue(kvp.Key, out var pbr))
					{
						pbr = new MaterialPbrFactors
						{
							BaseColorFactor = Vector4.One,
							MetallicFactor = 0f,
							RoughnessFactor = 0.6f,
							HasBaseColorTexture = false,
							Ior = 1.5f,
							VolumeAttenuation = new Vector4(1f, 1f, 1f, 0f),
							NormalScale = 1f,
							OcclusionStrength = 1f,
							SpecularColorFactor = Vector4.One
						};
					}

					data.Metallic = pbr.MetallicFactor;
					data.Roughness = pbr.RoughnessFactor;
					data.BaseColor = pbr.BaseColorFactor;
					data.HasBaseColorTexture = pbr.HasBaseColorTexture ? 1 : 0;
					data.AlphaCutoff = pbr.AlphaCutoff;
					data.HasMetallicRoughnessTexture = pbr.HasMetallicRoughnessTexture ? 1 : 0;
					data.Transmission = pbr.TransmissionFactor;
					data.Dispersion = pbr.Dispersion;
					data.Ior = pbr.Ior;
					data.VolumeAttenuation = pbr.VolumeAttenuation;
					data.ThicknessWorld = pbr.ThicknessWorld;
					data.FeatureFlags = (int)_featureFlags;
					data.NormalScale = pbr.NormalScale;
					data.OcclusionStrength = pbr.OcclusionStrength;
					data.UvOffset = pbr.UvOffset;
					data.UvTransform = pbr.UvTransform;
					data.UvHasTransform = pbr.HasUvTransform ? 1 : 0;
					data.OcclusionUvSet = pbr.OcclusionUvSet;
					data.SheenColorRoughness = pbr.SheenColorRoughness;
					data.SpecularColorFactor = pbr.SpecularColorFactor;
					data.Emissive = pbr.EmissiveFactor;
					data.AlphaBlend = pbr.IsSoftBlend ? 1 : 0;

					kvp.Value.SetConstant("PreviewSettings", ref data, HandleAccess.Pixel);
				}
			}
		}

	}
}
