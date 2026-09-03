using System.Numerics;
using DecaEngine.Graphics;

namespace DecaEngine.Editor;

/// <summary>
/// Pushes Graphics-window knobs into a viewport environment; single copy shared by both viewports.
/// Only logic that depends purely on <see cref="EditorSettings"/> and the environment lives here;
/// anything reading per-viewport state (targets, deferred-apply flags, device RT support) stays
/// in the viewports.
/// </summary>
internal static class ViewportSettingsPush
{
	/// <summary>Live bloom knobs (no-op when bloom is off - see ModelViewportEnvironment.SetBloomParams).</summary>
	public static void Bloom(ModelViewportEnvironment env, EditorSettings s)
	{
		env.SetBloomParams(
			Math.Max(s.BloomThreshold, 0f),
			Math.Max(s.BloomKnee, 0.0001f),
			Math.Max(s.BloomRadius, 0f),
			Math.Max(s.BloomIntensity, 0f));
	}

	/// <summary>Live color grading and vignette knobs (no-op when grading is off).</summary>
	public static void ColorGrade(ModelViewportEnvironment env, EditorSettings s)
	{
		env.SetColorGrade(
			Math.Max(s.GradeSaturation, 0f),
			Math.Max(s.GradeContrast, 0f),
			Math.Max(s.GradeGamma, 0.001f),
			Math.Clamp(s.GradeTemperature, -1f, 1f),
			Math.Clamp(s.GradeTint, -1f, 1f),
			new Vector3(s.GradeShadowR, s.GradeShadowG, s.GradeShadowB),
			new Vector3(s.GradeHighlightR, s.GradeHighlightG, s.GradeHighlightB));

		env.SetVignette(
			Math.Clamp(s.VignetteIntensity, 0f, 1f),
			Math.Max(s.VignetteRadius, 0.001f),
			Math.Max(s.VignetteSmoothness, 0.001f),
			Math.Clamp(s.VignetteRoundness, 0f, 1f));
	}

	/// <summary>Live fog knobs. Sun direction is NOT pushed here: it goes per-frame with the camera
	/// basis (SetCameraTransform), since scene-view sun gizmos raise no settings event.</summary>
	public static void Fog(ModelViewportEnvironment env, EditorSettings s)
	{
		env.SetFogParams(
			Math.Max(s.FogDensity, 0f),
			Math.Max(s.FogHeightFalloff, 0f),
			s.FogHeightRef,
			Math.Max(s.FogStartDistance, 0f),
			Math.Max(s.FogMaxDistance, 1f),
			Math.Clamp(s.FogMaxOpacity, 0f, 1f));

		env.SetFogColors(
			new Vector3(s.FogColorR, s.FogColorG, s.FogColorB),
			new Vector3(s.FogSunColorR, s.FogSunColorG, s.FogSunColorB),
			Math.Clamp(s.FogSunStrength, 0f, 1f),
			Math.Max(s.FogSunSharpness, 0.001f));
	}

	/// <summary>Live volumetric light knobs; sun direction excluded for the same reason as fog.</summary>
	public static void Volumetric(ModelViewportEnvironment env, EditorSettings s)
	{
		env.SetVolumetricParams(
			Math.Max(s.VolumetricDensity, 0f),
			Math.Max(s.VolumetricHeightFalloff, 0f),
			s.VolumetricHeightRef,
			Math.Max(s.VolumetricStartDistance, 0f),
			Math.Max(s.VolumetricMaxDistance, 1f),
			Math.Clamp(s.VolumetricSteps, 4, 256),
			Math.Clamp(s.VolumetricMaxOpacity, 0f, 1f),
			Math.Clamp(s.VolumetricShadowStrength, 0f, 1f));

		env.SetVolumetricScattering(
			Math.Max(s.VolumetricScattering, 0f),
			Math.Max(s.VolumetricExtinction, 1e-4f),
			Math.Clamp(s.VolumetricAnisotropy, -0.95f, 0.95f));

		env.SetVolumetricColors(
			new Vector3(s.VolumetricSunColorR, s.VolumetricSunColorG, s.VolumetricSunColorB),
			Math.Max(s.VolumetricSunIntensity, 0f),
			new Vector3(s.VolumetricAmbientColorR, s.VolumetricAmbientColorG, s.VolumetricAmbientColorB),
			Math.Max(s.VolumetricAmbientIntensity, 0f),
			Math.Clamp(s.VolumetricAmbientShadowFloor, 0f, 1f));

		env.SetVolumetricPunctualScatter(Math.Max(s.VolumetricPunctualScatter, 0f));
	}

	/// <summary>Upscaler backend and tuning; the deferred-apply flag stays with the viewport.</summary>
	public static void Upscaler(ModelViewportEnvironment env, EditorSettings s)
	{
		env.SetUpscalerBackend(s.TemporalUpscale && s.PreviewMotionVectors
			? Math.Clamp(s.UpscalerBackend, 0, 2)
			: 0);

		env.SetUpscalerTuning(
			Math.Clamp(s.TaauBlendAlpha, 0.02f, 0.5f),
			Math.Clamp(s.FsrSharpness, 0f, 1f),
			new[] { 0, 1, 2, 5 }[Math.Clamp(s.DlssQuality, 0, 3)],
			// FSR provider combo index -> branch major: {Auto, FSR 2, FSR 3.1} = {0, 2, 3}.
			new[] { 0, 2, 3 }[Math.Clamp(s.FsrProvider, 0, 2)]);
	}

	/// <summary>Texture size cap as passed to the loader; comparing raw vs clamped values would keep
	/// re-loading the scene for settings outside [128, 8192].</summary>
	public static int ClampedMaxTextureSize(EditorSettings s) =>
		Math.Clamp(s.PreviewMaxTextureSize, 128, 8192);

	/// <summary>Sun color for probe baking.</summary>
	public static Vector3 ProbeSunColor(EditorSettings s) =>
		new Vector3(1f, 0.98f, 0.92f) * Math.Clamp(s.ProbeGiSunIntensity, 0.1f, 16f);

	/// <summary>Model load options. RT shadows come as a parameter: each viewport answers
	/// "is inline tracing available" differently.</summary>
	public static ModelLoadOptions BuildLoadOptions(EditorSettings s, bool rtShadows) => new()
	{
		VertexShader = s.DefaultVertexShader,
		PixelShader = s.DefaultPixelShader,
		OptimizeMesh = false,
		GenerateLods = false,
		AnisotropicFiltering = s.PreviewAnisotropicFiltering,

		// log2 of render scale: with upscaling, mips must be selected for FULL resolution or the
		// accumulator has nothing to reconstruct (see ModelLoadOptions.MipLodBias). Load-time,
		// like anisotropy: a scale change takes effect on the next model load.
		MipLodBias = MathF.Log2(Math.Clamp(s.RenderScale, 0.25f, 1f)),

		MaxTextureSize = ClampedMaxTextureSize(s),
		RtShadows = rtShadows,

		// Reflection G-buffer keyword is unconditional in the environment (ModelViewportEnvironment).
		ReflectionGbuffer = true,

		// Textures stream by camera priority (ModelStore); display waits for ModelTexturesReady so
		// the model does not appear on 1x1 fillers.
		StreamTextures = true
	};
}
