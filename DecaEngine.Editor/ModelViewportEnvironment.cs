using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
using DecaEngine.Graphics.ProbeGi;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using DecaEngine.Scene;
using DecaEngine.Animation;

namespace DecaEngine.Editor
{
	/// <summary>Preview world-light/shadow state; BoundsRadius 0 skips the cascade and the shader falls back to the camera key light.</summary>
	public sealed class PreviewShadowSettings
	{
		/// <summary>Live toggle: false makes ShadowPass a no-op (zero LightDirection) without rebuilding the graph.</summary>
		public bool Enabled = true;

		public Vector3 LightDirection = new(0.45f, -0.72f, -0.35f);
		public Vector3 BoundsCenter;
		public float BoundsRadius;

		/// <summary>Shadow cascade count (1..ShadowRenderer.MaxCascades); creation-time option - ShadowPass commands are frozen per count.</summary>
		public int CascadeCount = 1;

		/// <summary>Camera focus point centering the fine cascades; without HasFocus they center on scene bounds.</summary>
		public Vector3 FocusPoint;
		public bool HasFocus;

		/// <summary>Environment sun azimuth/elevation in degrees, before user sliders.</summary>
		public float BaseYawDegrees;
		public float BaseElevationDegrees;

		/// <summary>Current absolute sun azimuth/elevation in degrees (see <see cref="SetAngles"/>).</summary>
		public float YawDegrees;
		public float ElevationDegrees;

		/// <summary>User environment yaw around Y in radians, pushed to sky/IBL shaders so reflections rotate with the key light; elevation is not applied to the equirect map (Y rotation is the only cheap one for a panorama).</summary>
		public float EnvYawRadians => (YawDegrees - BaseYawDegrees) * (MathF.PI / 180f);

		/// <summary>Sets absolute sun azimuth (yaw around Y, from +Z toward +X) and elevation; BuildLightData reads it every frame, so the change is live.</summary>
		public void SetAngles(float yawDegrees, float elevationDegrees)
		{
			YawDegrees = yawDegrees;
			ElevationDegrees = elevationDegrees;

			var yaw = yawDegrees * (MathF.PI / 180f);
			var elevation = elevationDegrees * (MathF.PI / 180f);

			// Direction toward the sun; the light shines the opposite way.
			var sun = new Vector3(
				MathF.Cos(elevation) * MathF.Sin(yaw),
				MathF.Sin(elevation),
				MathF.Cos(elevation) * MathF.Cos(yaw));
			LightDirection = -sun;
		}
	}

	/// <summary>Toggleable Lighting-preview features (PbrFeatureFlags bits in the PreviewSettings cbuffer, see UnlitInstancedPS.hlsl); each feature must work correctly when off.</summary>
	[Flags]
	public enum PreviewFeatureFlags
	{
		None = 0,

		/// <summary>Tangent-space normal maps (_NormalTex).</summary>
		NormalMaps = 1 << 0,

		/// <summary>Ambient occlusion texture (_OcclusionTex).</summary>
		Occlusion = 1 << 1,

		/// <summary>World key-light shadows (ShadowMaps + PCF); harmless without the shadow pass (LightDirection is zero).</summary>
		Shadows = 1 << 2,

		/// <summary>HDR pipeline: the model shader writes linear radiance; TonemapPass applies the curve and sRGB encode at end of frame.</summary>
		HdrOutput = 1 << 3,

		All = NormalMaps | Occlusion | Shadows,
	}

	/// <summary>Layout of the "PreviewSettings" cbuffer in UnlitInstancedPS.hlsl; Mode 0 = Textured, 1 = Highlight, 2 = Channel debug, 3 = Lighting (PBR). The main editor scene never pushes it, so zero-init (Textured) must stay valid.</summary>
	public struct PreviewSettingsData
	{
		public int Mode;
		public int Channel;
		public float Metallic;
		public float Roughness;
		public Vector4 BaseColor;
		public int HasBaseColorTexture;
		public float AlphaCutoff;
		public int HasMetallicRoughnessTexture;
		public float Transmission;
		public float Dispersion;
		public float Ior;

		/// <summary>Glass thickness in world units (thicknessFactor x node scale) for the geometric refraction offset.</summary>
		public float ThicknessWorld;

		/// <summary><see cref="PreviewFeatureFlags"/> bit mask.</summary>
		public int FeatureFlags;

		/// <summary>KHR_materials_volume, precomputed: rgb = attenuationColor, w = thickness / attenuationDistance (Beer-Lambert exponent, 0 = off).</summary>
		public Vector4 VolumeAttenuation;

		/// <summary>glTF normalScale (normal-map xy channels).</summary>
		public float NormalScale;

		/// <summary>glTF occlusionStrength (baked AO weight).</summary>
		public float OcclusionStrength;

		/// <summary>KHR_texture_transform offset.</summary>
		public Vector2 UvOffset;

		/// <summary>KHR_texture_transform precomputed 2x2 UV matrix.</summary>
		public Vector4 UvTransform;

		/// <summary>1 = apply <see cref="UvTransform"/>/<see cref="UvOffset"/>; 0 (zero-init) = identity.</summary>
		public int UvHasTransform;

		/// <summary>UV channel index of occlusionTexture (glTF texCoord 0/1).</summary>
		public int OcclusionUvSet;

		/// <summary>Environment yaw around Y in radians; shifts sky/IBL equirect UVs so reflections rotate with the key light. 0 = none.</summary>
		public float EnvYawRadians;

		// Occupies the old padding slot so the float4s below stay 16-byte aligned with the HLSL
		// cbuffer (SetConstant uploads Marshal.SizeOf rounded UP to 16).
		/// <summary>Shadow filter mode (SHADOW_MODE_*): 0 = PCSS (must stay zero - zero-init outside the preview must give default quality), 1 = Hard, 2 = PCF 3x3, 3 = PCSS HQ.</summary>
		public int ShadowMode;

		/// <summary>KHR_materials_sheen: rgb = sheenColorFactor (zero = off), w = sheenRoughnessFactor.</summary>
		public Vector4 SheenColorRoughness;

		/// <summary>KHR_materials_specular: rgb = specularColorFactor, w = specularFactor. Every Lighting push must fill it ((1,1,1,1) = identity) - zero w mutes specular to black.</summary>
		public Vector4 SpecularColorFactor;

		/// <summary>Probe-GI grid: xyz = world position of probe (0,0,0), w = 1 when probes are baked and atlases bound (0 = off).</summary>
		public Vector4 ProbeGridOrigin;

		/// <summary>xyz = probe grid step in world units, w = sample-point normal bias.</summary>
		public Vector4 ProbeGridCell;

		/// <summary>xyz = probe counts per axis; the grid is dense (a probe at every node).</summary>
		public Vector4 ProbeGridCounts;

		/// <summary>xyz = toroidal grid offset in probes: node c lives at texel (c + scroll) mod counts.</summary>
		public Vector4 ProbeGridScroll;

		/// <summary>Probe-GI knobs: x = shadow floor for the sun ambient share, y = sky-visibility floor for env specular, z = sun intensity, w = probe-irradiance multiplier.</summary>
		public Vector4 ProbeGiParams;

		/// <summary>x = shadow floor for the sky ambient share (1 = none); y = probe visibility octahedral map side (0 = shader default 8, so zero-init outside the preview keeps sampling valid); zw reserved.</summary>
		public Vector4 ProbeGiParams2;

		/// <summary>Fine-cascade probe-GI grids, same semantics as the base ProbeGrid*; Origin.w = 1 when the cascade's _C1/_C2 atlases are bound, zero-init = fall back to the base volume.</summary>
		public Vector4 ProbeGridOrigin1;
		public Vector4 ProbeGridCell1;
		public Vector4 ProbeGridCounts1;
		public Vector4 ProbeGridScroll1;
		public Vector4 ProbeGridOrigin2;
		public Vector4 ProbeGridCell2;
		public Vector4 ProbeGridCounts2;
		public Vector4 ProbeGridScroll2;

		/// <summary>Linear emissive color; MUST open a 16-byte register BEFORE <see cref="ToneCurve"/>: SPIR-V std140 requires vec3 16-byte alignment (Vulkan spirv-opt rejected a misaligned float3), and the struct tail must stay byte-identical to the PreviewSettings cbuffer.</summary>
		public Vector3 Emissive;

		/// <summary>Tone-curve mode (Tonemap.hlsl); LDR only - in HDR TonemapPass applies the curve. Pushed independently by the viewport AND the CLI probe: a field added in only one silently arrives as zero in the other.</summary>
		public int ToneCurve;

		/// <summary>1 = material draws with the blending PSO (author alpha into the frame); starts a new HLSL register. Zero-init = cutout.</summary>
		public int AlphaBlend;
	}

	/// <summary>Off-screen ECS render environment for showing/baking a model; shared scaffolding for <see cref="ModelPreviewViewport"/> and <see cref="ModelIconBaker"/>.</summary>
	public sealed class ModelViewportEnvironment
	{
		public const float CameraFovDegrees = 45f;

		public IGraphicsApi GraphicsApi { get; }
		public DiligentGraphicsApi DilApi { get; }
		public DiligentBatchRenderer BatchRenderer { get; }
		public GraphicsPipelineSimple Pipeline { get; }
		public EntityStore Store { get; }
		public RenderResourceManager ResourceManager { get; }
		public SystemRoot Root { get; }
		public Entity CameraEntity { get; }

		/// <summary>Pipeline creates and owns all off-screen targets; these properties are thin proxies for callers.</summary>
		public IRenderTarget ColorTarget => Pipeline.Targets!.ColorTarget;
		public IRenderTarget DepthTarget => Pipeline.Targets!.DepthTarget;

		/// <summary>Sampleable copy of <see cref="ColorTarget"/> after opaque draws - refraction source for transmissive materials.</summary>
		public IRenderTarget SceneCopyTarget => Pipeline.Targets!.SceneCopyTarget;

		/// <summary>Roughness-prefiltered equirect environment; owned by <see cref="SharedResources"/>, never released here.</summary>
		public IGpuTexture EnvironmentMap { get; }

		/// <summary>Process-wide environment/sampler container, supplied by the caller rather than created here.</summary>
		public SharedViewportResources SharedResources { get; }

		/// <summary>CPU radiance lookup of the environment by direction - sky source for the CPU probe bake.</summary>
		public Func<Vector3, Vector3> EnvironmentRadiance { get; }

		/// <summary>SSAO AO target (null = SSAO off); owned and recreated on Resize by <see cref="Pipeline"/>.</summary>
		public IRenderTarget? AoTarget => Pipeline.SsaoResources?.AoTarget;

		/// <summary>SSGI GI target (null = SSGI off); owned and recreated on Resize by <see cref="Pipeline"/>.</summary>
		public IRenderTarget? GiTarget => Pipeline.SsgiResources?.GiTarget;

		/// <summary>Linear HDR frame target that all geometry and post render into; <see cref="ColorTarget"/> is what gets shown, post-tonemap.</summary>
		public IRenderTarget? HdrColorTarget => Pipeline.Targets?.HdrColorTarget;

		/// <summary>Whether the HDR preview pipeline is on (always true for off-screen environments); drives the HdrOutput feature bit.</summary>
		public bool HdrOutput => Pipeline.Targets?.HdrColorTarget is not null;

		/// <summary>Current pipeline feature set - see <see cref="SetFeatures"/>.</summary>
		public PipelineFeatures Features => Pipeline.Features;

		/// <summary>Changes pipeline features live, without recreating the environment; Shadows is deliberately kept as created - the preview toggles shadows via the cheaper <see cref="PreviewShadowSettings.Enabled"/> path.</summary>
		public void SetFeatures(PipelineFeatures features)
		{
			features.Shadows = Pipeline.Features.Shadows;
			Pipeline.SetFeatures(features);
		}

		/// <summary>World light/shadow state (null = shadows off); the viewport updates bounds after model load.</summary>
		public PreviewShadowSettings ShadowSettings { get; }

		private SimpleCullingAndRenderSystem? _cullingSystem;
		private CullingAndRenderSystem? _mainCullingSystem;

		/// <summary>Sun entity, only in mainCascades mode; light direction = +Z of its rotation, synced from <see cref="ShadowSettings"/> every frame.</summary>
		public Entity SunEntity { get; private set; }

		/// <summary>Releases GPU resources for live recreation; the caller MUST Flush + WaitForIdle and unbind ImGui target bindings first.</summary>
		public void Release()
		{
			// Pipeline owns the off-screen targets and sky/SSAO resources; releasing it frees them too.
			Pipeline.Release();
			_cullingSystem?.Dispose();
			_mainCullingSystem?.Dispose();
			BatchRenderer.Release();

			// EnvironmentMap is owned by SharedResources (shared between environments) - not released here.
		}

		/// <summary>Rebinds resizable targets to SSAO materials AFTER Resize - Resize recreates native textures and SRBs would hold destroyed ones. No-op when SSAO is off.</summary>
		public void RebindPostProcessTargets()
		{
			Pipeline.RebindSsaoTargets();
		}

		/// <summary>Fraction of the model bounds radius giving the AO world range; unlike a screen-space radius it does not collapse when the camera closes in.</summary>
		public const float AoRangeOfBoundsRadius = 0.15f;

		/// <summary>Fraction of the model bounds radius giving the GI gather range - wider than AO: bounce light reaches farther than contact shadow.</summary>
		public const float GiRangeOfBoundsRadius = 0.5f;

		/// <summary>World-space AO range, pushed after framing the model. No-op when AO is off.</summary>
		public void SetAoWorldRange(float worldRange)
		{
			Pipeline.SsaoResources?.SetWorldRange(worldRange);
		}

		/// <summary>Live AO strength/floor knobs. No-op when AO is off.</summary>
		public void SetAoStrength(float power, float floor)
		{
			Pipeline.SsaoResources?.SetStrength(power, floor);
		}

		/// <summary>AO debug view. No-op when AO is off.</summary>
		public void SetAoDebugView(bool enabled)
		{
			Pipeline.SsaoResources?.SetDebugView(enabled);
		}

		/// <summary>Motion-vector debug view; rangePixels = displacement at which the scale saturates. No-op when vectors are off.</summary>
		public void SetMotionVectorDebug(bool enabled, float rangePixels)
		{
			Pipeline.MotionVectorDebugResources?.SetDebugView(enabled, rangePixels);
		}

		/// <summary>Whether the pipeline actually has a motion-vector buffer.</summary>
		public bool MotionVectorsAvailable => Pipeline.MotionVectorResources is not null;

		/// <summary>Sub-pixel projection jitter; a CPU matrix mutation, independent of motion vectors.</summary>
		public void SetTemporalJitter(bool enabled)
		{
			Pipeline.SetTemporalJitter(enabled);
		}

		/// <summary>Whether a native upscaler backend (FSR/DLSS) is active right now.</summary>
		public bool NativeUpscalerActive => Pipeline.NativeUpscaler is not null;

		/// <summary>Active native backend index (0 = none/TAAU, 1 = FSR, 2 = DLSS), same as the setting.</summary>
		public int ActiveUpscalerKind { get; private set; }

		/// <summary>Active native backend label with library version; null when built-in TAAU runs.</summary>
		public string? ActiveUpscalerName => Pipeline.NativeUpscaler?.DebugName;

		/// <summary>Live upscaler tuning; a DLSS preset change recreates the NGX feature, so it is preceded by a GPU barrier like a resize.</summary>
		public void SetUpscalerTuning(float taauBlendAlpha, float fsrSharpness, int dlssQuality,
			int fsrProviderMajor = 0)
		{
			Pipeline.TemporalUpscaleResources?.SetBlendAlpha(taauBlendAlpha);

			switch (Pipeline.NativeUpscaler)
			{
				case FsrUpscalerBackend fsr:
					fsr.SetSharpness(fsrSharpness);

					// Provider switch recreates the native context AND its textures: GPU barrier plus
					// graph invalidation are mandatory - the frozen graph holds recorded native
					// pointers and replay would copy into a destroyed texture (AV in CopyTexture).
					if (fsr.ProviderMajor != fsrProviderMajor)
					{
						DilApi.ImmediateContext.Flush();
						DilApi.ImmediateContext.WaitForIdle();
						fsr.SetProvider(fsrProviderMajor);
						Pipeline.InvalidateGraph();
						Console.WriteLine($"[upscaler] FSR provider: {fsr.DebugName}");
					}

					break;
				case DlssUpscalerBackend dlss when dlss.Quality != dlssQuality:
					// Same contract as the FSR provider switch above.
					DilApi.ImmediateContext.Flush();
					DilApi.ImmediateContext.WaitForIdle();
					dlss.SetQuality(dlssQuality);
					Pipeline.InvalidateGraph();
					break;
			}
		}

		/// <summary>Live upscaler backend switch (0 TAAU, 1 FSR, 2 DLSS); waits for the GPU because the tonemap input is rebound on a live material. No-op without a motion-vector buffer; TryCreate failure keeps TAAU.</summary>
		public void SetUpscalerBackend(int kind)
		{
			if (kind == ActiveUpscalerKind && (kind == 0) == (Pipeline.NativeUpscaler is null))
			{
				return;
			}

			DilApi.ImmediateContext.Flush();
			DilApi.ImmediateContext.WaitForIdle();

			if (kind == 0)
			{
				Pipeline.SetNativeUpscaler(null);
				ActiveUpscalerKind = 0;
				return;
			}

			if (Pipeline.MotionVectorResources is null || Pipeline.Targets?.HdrColorTarget is null)
			{
				return;
			}

			// The native shim (DecaFfxShim) is built for D3D12 only; on Vulkan DecaDlss_Create
			// crashes inside the environment constructor (editor never starts), so fall back to TAAU.
			if (DilApi.Device.GetDeviceInfo().Type != global::Diligent.RenderDeviceType.D3D12)
			{
				Console.WriteLine($"[upscaler] backend {kind} is only available on D3D12 - staying on the built-in TAAU");
				Pipeline.SetNativeUpscaler(null);
				ActiveUpscalerKind = 0;
				return;
			}

			// Sizes are the actual current ones: render size from depth (render scale may already
			// apply), display size from ColorTarget; later resizes reach the backend via RebindSsaoTargets.
			var renderSize = Pipeline.Targets.DepthTarget.Size;
			var displaySize = Pipeline.Targets.ColorTarget.Size;
			INativeUpscalerBackend? backend = kind == 2
				? DlssUpscalerBackend.TryCreate(GraphicsApi, "Preview",
					Pipeline.Targets.HdrColorTarget, Pipeline.Targets.DepthTarget,
					Pipeline.MotionVectorResources.MotionTarget,
					(uint)renderSize.X, (uint)renderSize.Y, (uint)displaySize.X, (uint)displaySize.Y)
				: FsrUpscalerBackend.TryCreate(GraphicsApi, "Preview",
					Pipeline.Targets.HdrColorTarget, Pipeline.Targets.DepthTarget,
					Pipeline.MotionVectorResources.MotionTarget,
					(uint)renderSize.X, (uint)renderSize.Y, (uint)displaySize.X, (uint)displaySize.Y,
					cameraNear: 0.05f, fovYRad: CameraFovDegrees * MathF.PI / 180f);

			// Creation failure leaves the current backend (or TAAU) untouched.
			if (backend is not null)
			{
				Pipeline.SetNativeUpscaler(backend);
				ActiveUpscalerKind = kind;
				Console.WriteLine($"[upscaler] native backend active: {backend.DebugName}");
			}
			else if (Pipeline.NativeUpscaler is null)
			{
				ActiveUpscalerKind = 0;
			}
		}

		/// <summary>Stores the scene render scale; true = changed and the caller MUST run the resize path before it takes effect.</summary>
		public bool SetRenderScale(float scale)
		{
			return Pipeline.SetRenderScale(scale);
		}

		/// <summary>World-space GI gather range, pushed with the AO range after framing. No-op when SSGI is off.</summary>
		public void SetGiWorldRange(float worldRange)
		{
			Pipeline.SsgiResources?.SetWorldRange(worldRange);
		}

		/// <summary>Live SSGI knobs. No-op when SSGI is off.</summary>
		public void SetGiParams(float intensity, int sampleCount, float maxLuminance, float saturation)
		{
			Pipeline.SsgiResources?.SetParams(intensity, sampleCount, maxLuminance, saturation);
		}

		/// <summary>Whether the batch renderer was built with the thin reflection G-buffer (MRT slots in geometry PSOs).</summary>
		public bool ReflectionGbuffer => BatchRenderer.ReflectionGbuffer;

		/// <summary>Live SSR knobs. No-op until SSR has been enabled.</summary>
		public void SetSsrParams(float intensity, float maxRoughness, float thickness, float maxDistance,
			float historyWeight, int raysPerPixel, int debugView,
			int rtBounces = SsrPassResources.DefaultRtBounces, int traceMode = 0)
		{
			Pipeline.SsrResources?.SetParams(intensity, maxRoughness, thickness, maxDistance,
				historyWeight, raysPerPixel, debugView, rtBounces, traceMode);
		}

		/// <summary>Env-map yaw and RT-fallback sun for SSR, pushed per frame; sunTanHalfAngle = tangent of the sun's half angular size.</summary>
		public void SetSsrEnvironment(float envYawRadians, Vector3 dirTowardSun, Vector3 sunColor,
			float ambient, float sunTanHalfAngle = 0f)
		{
			Pipeline.SsrResources?.SetEnvironmentYaw(envYawRadians);
			Pipeline.SsrResources?.SetSun(dirTowardSun, sunColor, ambient, sunTanHalfAngle);
		}

		/// <summary>SSR RT-hit probe field; MUST be called with null BEFORE releasing the atlases, or the trace SRB holds destroyed textures.</summary>
		public void SetSsrProbeField(ProbeGiTextures? textures)
		{
			Pipeline.SsrResources?.SetProbeField(
				textures?.Sh0, textures?.Sh1, textures?.Sh2, textures?.Sh3,
				textures?.GridOrigin ?? Vector4.Zero,
				textures?.GridCell ?? Vector4.Zero,
				textures?.GridCounts ?? Vector4.Zero);
		}

		/// <summary>Live fog knobs; the pass itself is created with the pipeline, so the on/off toggle needs a recreate.</summary>
		public void SetFogParams(float density, float heightFalloff, float heightRef, float startDistance,
			float maxDistance, float maxOpacity)
		{
			Pipeline.FogResources?.SetParams(density, heightFalloff, heightRef, startDistance, maxDistance,
				maxOpacity);
		}

		/// <summary>Fog and fog sun-glow colors, linear. No-op when fog is off.</summary>
		public void SetFogColors(Vector3 color, Vector3 sunColor, float sunStrength, float sunSharpness)
		{
			Pipeline.FogResources?.SetColors(color, sunColor, sunStrength, sunSharpness);
		}

		/// <summary>Direction TOWARD the sun; LightDirection points away, so callers must negate it.</summary>
		public void SetFogSun(Vector3 sunDirection)
		{
			Pipeline.FogResources?.SetSun(sunDirection);
		}

		/// <summary>Live volumetric-light knobs; like fog, the on/off toggle needs an environment recreate.</summary>
		public void SetVolumetricParams(float density, float heightFalloff, float heightRef,
			float startDistance, float maxDistance, int steps, float maxOpacity, float shadowStrength)
		{
			Pipeline.VolumetricResources?.SetParams(density, heightFalloff, heightRef, startDistance,
				maxDistance, steps, maxOpacity, shadowStrength);
		}

		/// <summary>Volumetric medium optics: scattering, extinction, anisotropy.</summary>
		public void SetVolumetricScattering(float scattering, float extinction, float anisotropy)
		{
			Pipeline.VolumetricResources?.SetScattering(scattering, extinction, anisotropy);
		}

		/// <summary>Linear colors and strengths of sun (god-ray) and sky scattering.</summary>
		public void SetVolumetricColors(Vector3 sunColor, float sunIntensity, Vector3 ambientColor,
			float ambientIntensity, float ambientShadowFloor)
		{
			Pipeline.VolumetricResources?.SetColors(sunColor, sunIntensity, ambientColor, ambientIntensity,
				ambientShadowFloor);
		}

		/// <summary>Scatter multiplier for punctual lights in the volumetric medium.</summary>
		public void SetVolumetricPunctualScatter(float intensity)
		{
			Pipeline.VolumetricResources?.SetPunctualScatter(intensity);
		}

		/// <summary>Whether the pipeline has a shadow pass; without it god rays degrade to flat volumetric fog.</summary>
		public bool VolumetricShadowsAvailable => Pipeline.VolumetricResources?.ShadowsAvailable ?? false;

		/// <summary>Live color-grading knobs. No-op when grading is off.</summary>
		public void SetColorGrade(float saturation, float contrast, float gamma, float temperature,
			float tint, Vector3 shadowTint, Vector3 highlightTint)
		{
			Pipeline.ColorGradeResources?.SetGrade(saturation, contrast, gamma, temperature, tint);
			Pipeline.ColorGradeResources?.SetTints(shadowTint, highlightTint);
		}

		/// <summary>Live vignette knobs.</summary>
		public void SetVignette(float intensity, float radius, float smoothness, float roundness)
		{
			Pipeline.ColorGradeResources?.SetVignette(intensity, radius, smoothness, roundness);
		}

		/// <summary>HDR tone curve; no-op in LDR, where UnlitInstancedPS applies the curve itself.</summary>
		public void SetToneCurve(int curve)
		{
			Pipeline.TonemapResources?.SetCurve(curve);
		}

		/// <summary>Live bloom knobs. No-op when bloom is off.</summary>
		public void SetBloomParams(float threshold, float knee, float radius, float intensity)
		{
			Pipeline.BloomResources?.SetParams(threshold, knee, radius, intensity);
		}

		/// <summary>Live SSGI composite knobs: bilateral blur radius and debug view. No-op when SSGI is off.</summary>
		public void SetGiCompositeParams(int blurRadius, bool debugView)
		{
			Pipeline.SsgiResources?.SetCompositeParams(blurRadius, debugView);
		}

		/// <summary>Live auto-exposure knobs; metering and tonemap must see the same key/compensation.</summary>
		public void SetEyeAdaptationParams(float key, float minLuminance, float maxLuminance,
			float exposureCompensation)
		{
			Pipeline.EyeAdaptationResources?.SetParams(key, minLuminance, maxLuminance, exposureCompensation);
			Pipeline.TonemapResources?.SetParams(key, exposureCompensation);

			// Fog/bloom/volumetrics need the same key: their colors are display-space and are
			// converted to linear through adapted/key.
			Pipeline.FogResources?.SetExposure(Pipeline.AutoExposure, key);
			Pipeline.BloomResources?.SetExposure(Pipeline.AutoExposure, key);
			Pipeline.VolumetricResources?.SetExposure(Pipeline.AutoExposure, key);
		}

		/// <summary>Adaptation speeds in 1/sec (toward bright / toward dark). No-op when auto-exposure is off.</summary>
		public void SetEyeAdaptationSpeed(float speedUp, float speedDown)
		{
			Pipeline.EyeAdaptationResources?.SetSpeed(speedUp, speedDown);
		}

		/// <summary>Frame delta for temporal adaptation; must be pushed every frame.</summary>
		public void SetEyeAdaptationDeltaTime(float deltaTime)
		{
			Pipeline.EyeAdaptationResources?.SetDeltaTime(deltaTime);

			// The native upscaler accumulates temporally and needs the same frame delta.
			Pipeline.NativeUpscaler?.SetDeltaTime(deltaTime);
		}

		/// <summary>Skip tonemapping for debug views that already write display-space values.</summary>
		public void SetTonemapPassthrough(bool passthrough)
		{
			Pipeline.TonemapResources?.SetPassthrough(passthrough);

			// Sky is drawn outside the model shader, so it must also write display-space during
			// passthrough or the debug-view background goes dark (linear values, no encode).
			if (HdrOutput)
			{
				Pipeline.SkyResources?.SetHdrOutput(!passthrough);
			}
		}

		/// <summary>Feature flags are creation-time: mainCascades requires shadows, and PSOs bake the target format.</summary>
		public ModelViewportEnvironment(IGraphicsApi graphicsApi, uint width, uint height,
			string colorTargetName, string depthTargetName, SharedViewportResources sharedResources,
			bool skyBackground = false,
			string environmentHdrPath = null, bool ssao = false, bool shadows = false,
			AmbientOcclusionMode aoMode = AmbientOcclusionMode.Ssao, bool ssgi = false, bool eyeAdaptation = false,
			bool mainCascades = false, bool fog = false, bool bloom = false, bool colorGrade = false,
			bool volumetric = false, bool motionVectors = false, bool temporalUpscale = false,
			int upscalerBackend = 0, bool ssr = false, bool ssrRayTraced = false)
		{
			GraphicsApi = graphicsApi;
			DilApi = (DiligentGraphicsApi)graphicsApi;
			SharedResources = sharedResources;

			// Off-screen is unconditionally HDR + reflection G-buffer so geometry PSOs bake one
			// format; that is what keeps post-process toggles live instead of a recreate. Models
			// must then load with ModelLoadOptions.ReflectionGbuffer = true.
			BatchRenderer = new DiligentBatchRenderer(DilApi,
				TextureObjectFormat.R16G16B16A16Float, reflectionGbuffer: true);

			// Resolved from the shared container: environments with the same HDRI share one texture.
			var environmentResult = sharedResources.GetEnvironment(environmentHdrPath);
			EnvironmentMap = environmentResult.Texture;
			EnvironmentRadiance = environmentResult.Radiance;

			if (shadows)
			{
				// SunDirection points TOWARD the sun; light direction is its negation.
				var sun = Vector3.Normalize(environmentResult.SunDirection);
				var baseYaw = MathF.Atan2(sun.X, sun.Z) * (180f / MathF.PI);
				var baseElevation = MathF.Asin(Math.Clamp(sun.Y, -1f, 1f)) * (180f / MathF.PI);

				ShadowSettings = new PreviewShadowSettings
				{
					LightDirection = -sun,
					BaseYawDegrees = baseYaw,
					BaseElevationDegrees = baseElevation,
					YawDegrees = baseYaw,
					ElevationDegrees = baseElevation,
				};
			}

			// Clear alpha 0 keeps the background transparent for ImGui.Image; the clear RGB is mid
			// grey because bilinear filtering bleeds it into silhouette edges.
			Pipeline = new GraphicsPipelineSimple(graphicsApi, BatchRenderer, colorTargetName, depthTargetName,
				width, height, new Vector4(0.4f, 0.4f, 0.4f, 0f),
				skyBackground: skyBackground, environmentMap: EnvironmentMap,
				ssao: ssao, enableShadowPass: shadows, aoMode: aoMode, ssgi: ssgi, eyeAdaptation: eyeAdaptation,
				fog: fog, bloom: bloom, colorGrade: colorGrade, volumetric: volumetric,
				motionVectors: motionVectors, temporalUpscale: temporalUpscale,
				ssr: ssr, ssrRayTraced: ssrRayTraced);

			if (upscalerBackend != 0 && temporalUpscale)
			{
				SetUpscalerBackend(upscalerBackend);
			}

			Store = new EntityStore();
			ResourceManager = new RenderResourceManager(16, 16, Store, BatchRenderer);

			var cameraComponent = new CameraComponent(new CameraData(CameraFovDegrees, 0.05f, 2000f,
				new Vector4(0, 0, width, height)));
			cameraComponent.data.cullFlags = CullFlags.None;

			CameraEntity = Store.CreateEntity(
				new Position(0, 0, -4f),
				new Rotation { value = Quaternion.Identity },
				new Scale3(1, 1, 1),
				cameraComponent);

			if (shadows && mainCascades)
			{
				// Light direction is +Z of the sun entity's rotation. CascadeDistances must be valid
				// from the start: zeros give degenerate slices (n == f) and NaN cascade matrices
				// before the viewport's first sync.
				var sunDirection = ShadowSettings != null
					? Vector3.Normalize(ShadowSettings.LightDirection)
					: new Vector3(0.45f, -0.72f, -0.35f);
				var sunUp = MathF.Abs(sunDirection.Y) > 0.95f ? Vector3.UnitX : Vector3.UnitY;
				var sunView = Matrix4x4.CreateLookAtLeftHanded(Vector3.Zero, sunDirection, sunUp);

				SunEntity = Store.CreateEntity(
					new Position(0, 50, 0),
					new Rotation { value = Quaternion.CreateFromRotationMatrix(Matrix4x4.Transpose(sunView)) },
					new LightComponent
					{
						Type = LightType.Directional,
						Color = new Vector3(1f, 0.97f, 0.9f),
						Intensity = 1f,
						ShadowStrength = 1f,
					},
					new SunComponent()
					{

					},
					new CascadedShadowComponent
					{
						CascadeDistances = [0.01f, 10f, 30f, 100f, 300f]
					});

				_mainCullingSystem = new CullingAndRenderSystem(ResourceManager, graphicsApi, Pipeline);
				Root = new SystemRoot()
				{
					// Must run before culling: it composes WorldMatrix for hierarchy children, which
					// LightCulling needs instead of raw local Position/Rotation.
					new DecaEngine.Core.Entities.TransformSystem(),
					new GpuInstanceBufferSystem(),
					_mainCullingSystem
				};
			}
			else
			{
				_cullingSystem = new SimpleCullingAndRenderSystem(ResourceManager, Pipeline, ShadowSettings);
				Root = new SystemRoot()
				{
					// Must run before culling: composes WorldMatrix for hierarchy children.
					new DecaEngine.Core.Entities.TransformSystem(),
					new GpuInstanceBufferSystem(),
					_cullingSystem
				};
			}
			Root.AddStore(Store);
		}

		public void SetCameraTransform(Vector3 eye, Vector3 target)
		{
			var viewMatrix = Matrix4x4.CreateLookAtLeftHanded(eye, target, Vector3.UnitY);
			var rotation = Quaternion.CreateFromRotationMatrix(Matrix4x4.Transpose(viewMatrix));

			CameraEntity.Position = new Position(eye.X, eye.Y, eye.Z);
			CameraEntity.Rotation = new Rotation { value = rotation };

			// World camera basis for fog/volumetrics, rebuilt from eye/target rather than decomposed
			// from viewMatrix, so there is no row/column transpose to get wrong.
			var fog = Pipeline.FogResources;
			var volumetric = Pipeline.VolumetricResources;
			if (fog is not null || volumetric is not null)
			{
				var forward = target - eye;
				forward = forward.LengthSquared() > 1e-8f ? Vector3.Normalize(forward) : Vector3.UnitZ;

				// Same order as left-handed LookAt: x = up X z, y = z X x.
				var right = Vector3.Cross(Vector3.UnitY, forward);
				right = right.LengthSquared() > 1e-8f
					? Vector3.Normalize(right)
					// Straight up/down: up degenerates, so pick any perpendicular or fog gets NaN.
					: Vector3.UnitX;

				var up = Vector3.Cross(forward, right);
				fog?.SetCamera(right, up, forward);

				volumetric?.SetCamera(right, up, forward);

				// Sun is pushed here, not from ApplyGraphicsSettings: the Scene View gizmo rotates it
				// without raising a settings event. LightDirection points AWAY from the sun.
				if (ShadowSettings is not null)
				{
					fog?.SetSun(-ShadowSettings.LightDirection);
					volumetric?.SetSun(-ShadowSettings.LightDirection);
				}
			}
		}
	}

	/// <summary>
	/// Mesh/material registration, instance-entity creation and camera-framing math shared between
	/// <see cref="ModelPreviewViewport"/> and <see cref="ModelIconBaker"/> - both populate a
	/// <see cref="ModelViewportEnvironment"/> from a loaded <see cref="ModelLoader"/> and frame a camera
	/// around either the whole model or a single sub-mesh.
	/// </summary>
	public static class ModelViewportGeometry
	{
		/// <summary>Skinning kill switch; DECA_SKINNING=0 registers skinned meshes as static bind-pose.</summary>
		/// <remarks>The env var only seeds the value, before settings load; EditorSettings.SceneSkinning drives it afterwards.</remarks>
		public static bool SkinningEnabled { get; set; } =
			Environment.GetEnvironmentVariable("DECA_SKINNING") != "0";

		/// <summary>Registration mutates materials: a shared ModelLoader must pass its own material set per renderer.</summary>
		public static void RegisterModelResources(DiligentBatchRenderer batchRenderer, ModelLoader modelLoader,
			Dictionary<int, MeshId> meshIdMap, Dictionary<int, MaterialId> materialIdMap,
			ISamplerObject? envMapSampler = null, IGpuTexture? sceneCopy = null, IGpuTexture? environmentMap = null,
			OrderedDictionary<int, IMaterialObject>? materials = null, ISamplerObject? sceneCopySampler = null,
			Dictionary<int, int>? skinBaseMap = null)
		{
			var materialSet = materials ?? modelLoader.materialObjects;

			var environmentSampler = environmentMap != null ? envMapSampler : null;

			var baseMaterialState = batchRenderer.GetBaseState();
			IStateObject? lineListState = null, lineStripState = null, pointState = null;
			IStateObject? blendedMaterialState = null;

			for (int i = 0; i < materialSet.Count; i++)
			{
				var kvp = materialSet.GetAt(i);

				// Non-triangle material clones get a PSO with the matching primitive topology.
				int topology = modelLoader.MaterialPbr.TryGetValue(kvp.Key, out var pbrTopology) ? pbrTopology.Topology : 0;
				kvp.Value.SetState(topology switch
				{
					ModelLoader.MeshTopologyLineList => lineListState ??= batchRenderer.GetTopologyState(PrimitiveTopologyType.LineList),
					ModelLoader.MeshTopologyLineStrip => lineStripState ??= batchRenderer.GetTopologyState(PrimitiveTopologyType.LineStrip),
					ModelLoader.MeshTopologyPoints => pointState ??= batchRenderer.GetTopologyState(PrimitiveTopologyType.PointList),
					_ => baseMaterialState,
				});
				materialIdMap[kvp.Key] = batchRenderer.Register(kvp.Value);

				if (environmentSampler != null)
				{
					kvp.Value.SetTexture("_EnvMap", environmentMap);
					kvp.Value.SetImmutableSampler("_EnvMap", environmentSampler);

					// Probe-GI slots are declared unconditionally: unbound descriptors are invalid
					// (Vulkan VUID-08114) even when the shader never reads them. Load-only, no sampler.
					kvp.Value.SetTexture("_ProbeSh0", environmentMap);
					kvp.Value.SetTexture("_ProbeSh1", environmentMap);
					kvp.Value.SetTexture("_ProbeSh2", environmentMap);
					kvp.Value.SetTexture("_ProbeSh3", environmentMap);
					kvp.Value.SetTexture("_ProbeVis", environmentMap);
					kvp.Value.SetTexture("_ProbeOffset", environmentMap);

					// Cascade slots need the same unconditional placeholder binding.
					foreach (var suffix in new[] { "_C1", "_C2" })
					{
						kvp.Value.SetTexture($"_ProbeSh0{suffix}", environmentMap);
						kvp.Value.SetTexture($"_ProbeSh1{suffix}", environmentMap);
						kvp.Value.SetTexture($"_ProbeSh2{suffix}", environmentMap);
						kvp.Value.SetTexture($"_ProbeSh3{suffix}", environmentMap);
						kvp.Value.SetTexture($"_ProbeVis{suffix}", environmentMap);
						kvp.Value.SetTexture($"_ProbeOffset{suffix}", environmentMap);
					}
				}

				// _SceneColor exists only under MATERIAL_TRANSMISSION; binding it elsewhere leaves an
				// immutable sampler with no matching shader resource (Diligent warns).
				if (sceneCopySampler != null && sceneCopy != null &&
					modelLoader.MaterialPbr.TryGetValue(kvp.Key, out var pbr) && pbr.TransmissionFactor > 0f)
				{
					kvp.Value.SetTexture("_SceneColor", sceneCopy);
					kvp.Value.SetImmutableSampler("_SceneColor", sceneCopySampler);

					batchRenderer.SetMaterialTransparent(materialIdMap[kvp.Key], true);
				}

				// Soft BLEND overlays get a real blending PSO and draw in the transparent loop.
				// Transparents are ordered by materialId, not back-to-front: interpenetrating
				// transparent volumes would need sorting. Gated on sceneCopy because environments
				// without an opaque/transparent split must keep these on alpha cutout.
				if (sceneCopySampler != null && sceneCopy != null && topology == 0 &&
					modelLoader.MaterialPbr.TryGetValue(kvp.Key, out var blendPbr) && blendPbr.IsSoftBlend)
				{
					kvp.Value.SetState(blendedMaterialState ??= batchRenderer.GetBlendedState());
					batchRenderer.SetMaterialTransparent(materialIdMap[kvp.Key], true);
				}

				// Alpha-tested shadows only for genuinely perforated geometry: alphaMode MASK alone
				// is not enough (exporters tag opaque stone with it), so average alpha gates it.
				// Each such material adds a draw call to every shadow cascade.
				if (modelLoader.MaterialPbr.TryGetValue(kvp.Key, out var maskPbr))
				{
					// Soft overlays (dirt decals) cast no shadow at all: they sit millimetres from
					// the wall they decorate and would duplicate themselves as dark blobs. alphaMode
					// cannot separate them from foliage - both are BLEND - so alpha binarity does:
					// cutouts are ~0 or 1, soft smears spread across the range. Unknown binarity
					// (-1, cache not baked) counts as cutout, so the test can only remove shadows.
					if (maskPbr.AlphaMode == MaterialAlphaMode.Blend && maskPbr.SoftAlphaFraction > 0.25f)
					{
						batchRenderer.SetMaterialShadowCasting(materialIdMap[kvp.Key].materialId, false);
					}
					else if (maskPbr.AlphaCutoff > 0f && maskPbr.HasBaseColorTexture && maskPbr.AverageAlpha < 0.6f &&
						modelLoader.MaterialBaseColor.TryGetValue(kvp.Key, out var maskBaseColor))
					{
						batchRenderer.SetMaterialAlphaTestedShadow(materialIdMap[kvp.Key].materialId, maskBaseColor,
							maskPbr.AlphaCutoff);
					}
				}
			}

			for (int i = 0; i < modelLoader.Meshes.Count; i++)
			{
				// Index-less glTF meshes are skipped: a zero-count batch breaks native indirect draw.
				if (modelLoader.Meshes[i].IndexCount == 0)
				{
					continue;
				}

				meshIdMap[i] = batchRenderer.Register(modelLoader.Meshes[i]);

				// Registered once per mesh, not per instance: weights are shared, only palettes differ.
				if (SkinningEnabled && skinBaseMap != null &&
					i < modelLoader.MeshSkin.Count && modelLoader.MeshSkin[i] != null)
				{
					skinBaseMap[i] = batchRenderer.Skinning.RegisterSkinStream(modelLoader.MeshSkin[i]);
				}
			}
		}

		/// <summary>
		/// Creates one instance entity for the given mesh/material, reusing (and lazily creating) the
		/// batch for that (meshIndex, materialIndex) pair. Returns null if meshIndex has no registered
		/// mesh (e.g. dead reference) - caller should skip it.
		/// </summary>
		public static Entity? CreateInstanceEntity(EntityStore store, RenderResourceManager resourceManager,
			DiligentBatchRenderer batchRenderer, Dictionary<int, MeshId> meshIdMap,
			Dictionary<int, MaterialId> materialIdMap, Dictionary<(int, int), BatchId> batchCache,
			int meshIndex, int materialIndex, DecaEngine.Core.Transform t,
			ModelLoader? skinnedModel = null, Dictionary<int, int>? skinBaseMap = null,
			Action<int>? onSkinnedPalette = null)
		{
			if (!meshIdMap.TryGetValue(meshIndex, out var meshId))
			{
				return null;
			}

			// Skinned instances get their own meshId and batch (deliberately bypassing batchCache):
			// a shared batch would make every character with this model draw the same pose.
			int paletteOffset = -1;
			bool skinned = SkinningEnabled &&
				skinnedModel?.Skeleton != null &&
				skinBaseMap != null &&
				onSkinnedPalette != null &&
				skinBaseMap.ContainsKey(meshIndex);

			if (skinned)
			{
				(meshId, paletteOffset) = batchRenderer.RegisterSkinnedInstance(
					meshId, skinnedModel!.Skeleton.JointCount, skinBaseMap![meshIndex]);
			}

			if (!materialIdMap.TryGetValue(materialIndex, out var matId))
			{
				if (materialIdMap.Count == 0)
				{
					// default(MaterialId) is never registered; drawing it is UB on the native side.
					return null;
				}

				foreach (var candidate in materialIdMap.Values)
				{
					matId = candidate;
					break;
				}
			}

			BatchId batchId;
			if (skinned)
			{
				batchId = batchRenderer.CreateBatch(meshId, matId);
			}
			else if (!batchCache.TryGetValue((meshIndex, materialIndex), out batchId))
			{
				batchId = batchRenderer.CreateBatch(meshId, matId);
				batchCache[(meshIndex, materialIndex)] = batchId;
			}

			var entity = store.CreateEntity(
				new Position(t.position.X, t.position.Y, t.position.Z),
				new Scale3(t.scale.X, t.scale.Y, t.scale.Z),
				new Rotation(t.rotation.X, t.rotation.Y, t.rotation.Z, t.rotation.W),
				Tags.Get<GpuUpdateTag>());

			resourceManager.RegisterRenderable(entity, batchId);

			if (skinned)
			{
				onSkinnedPalette!(paletteOffset);
			}

			return entity;
		}

		/// <summary>
		/// AABB of one sub-mesh across its instances (bounding-sphere of the mesh, transformed by each
		/// instance). If the sub-mesh has no instances, falls back to its local bounding sphere.
		/// </summary>
		public static (Vector3 Min, Vector3 Max) ComputeSubMeshBounds(ModelLoader model, int meshIndex)
		{
			var mesh = model.Meshes[meshIndex];
			var min = new Vector3(float.PositiveInfinity);
			var max = new Vector3(float.NegativeInfinity);
			var any = false;

			foreach (var instance in model.instances)
			{
				if (instance.meshId != meshIndex)
				{
					continue;
				}

				var t = instance.transform;
				var worldCenter = Vector3.Transform(mesh.Center * t.scale, t.rotation) + t.position;
				var maxScale = MathF.Max(MathF.Abs(t.scale.X), MathF.Max(MathF.Abs(t.scale.Y), MathF.Abs(t.scale.Z)));
				var radius = mesh.Radius * maxScale;

				min = Vector3.Min(min, worldCenter - new Vector3(radius));
				max = Vector3.Max(max, worldCenter + new Vector3(radius));
				any = true;
			}

			if (!any)
			{
				min = mesh.Center - new Vector3(mesh.Radius);
				max = mesh.Center + new Vector3(mesh.Radius);
			}

			return (min, max);
		}

		/// <summary>Distance at which a bounding sphere of the given radius exactly fills the vertical FOV, plus a margin.</summary>
		public static float ComputeFramingDistance(float radius, float fovDegrees)
		{
			var halfFovRad = fovDegrees * (MathF.PI / 180f) * 0.5f;
			return Math.Clamp(radius / MathF.Sin(halfFovRad) * 1.25f, 0.2f, 1500f);
		}

		public static Vector3 ComputeOrbitEye(Vector3 target, float distance, float yaw, float pitch)
		{
			return target + distance * new Vector3(
				MathF.Cos(pitch) * MathF.Sin(yaw),
				MathF.Sin(pitch),
				MathF.Cos(pitch) * MathF.Cos(yaw));
		}
	}
}
