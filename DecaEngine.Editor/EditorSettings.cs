using System.Text.Json;
using System.Text.Json.Serialization;
using DecaEngine.Core;
using DecaEngine.Core.Assets;
using DecaEngine.Graphics.ProbeGi;
using DecaEngine.Graphics;

namespace DecaEngine.Editor;

/// <summary>Per-user editor settings stored in %AppData%/DecaEngine/editor_settings.json.</summary>
public class EditorSettings
{
	private static readonly string FilePath = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
		"DecaEngine",
		"editor_settings.json");

	/// <summary>Allowed UI scale values, percent.</summary>
	public static readonly int[] AllowedUiScalePercents = { 50, 75, 100, 125, 150 };

	// --- General ---

	public bool AutoLoadLastProject { get; set; } = false;

	// --- Editor ---

	/// <summary>UI scale percent; must be one of <see cref="AllowedUiScalePercents"/>.</summary>
	public int UiScalePercent { get; set; } = 100;

	public float UiScaleMultiplier => UiScalePercent / 100f;

	// --- Appearance ---

	public SerializableColor AccentColor { get; set; } = new(EditorPalette.DefaultAccent);
	public SerializableColor BackgroundColor { get; set; } = new(EditorPalette.DefaultBackground);
	public SerializableColor SurfaceColor { get; set; } = new(EditorPalette.DefaultSurface);
	public SerializableColor TextColor { get; set; } = new(EditorPalette.DefaultText);
	/// <summary>Selection color; deliberately separate from Accent so both stay customizable.</summary>
	public SerializableColor SelectionColor { get; set; } = new(EditorPalette.DefaultSelection);
	/// <summary>Asset Browser icon accent; deliberately separate from Accent and Selection.</summary>
	public SerializableColor IconAccentColor { get; set; } = new(EditorPalette.DefaultIconAccent);

	// --- Graphics Pipeline ---

	/// <summary>Editor window vsync; DECA_VSYNC env var overrides the saved value at startup.</summary>
	public bool VSync { get; set; } = true;

	/// <summary>Vertex shader used by ModelLoader to render loaded glTF models, relative to "EditorAssets/".</summary>
	public EditorRef DefaultVertexShader { get; set; } = new("shader/UnlitInstancedVS.hlsl");

	/// <summary>Pixel shader used by ModelLoader to render loaded glTF models, relative to "EditorAssets/".</summary>
	public EditorRef DefaultPixelShader { get; set; } = new("shader/UnlitInstancedPS.hlsl");

	/// <summary>Equirect .hdr for preview IBL (absolute or EditorAssets-relative); empty = procedural sky.</summary>
	public string PreviewEnvironmentHdr { get; set; } = "";

	// --- Scene physics and animation debug (see AnimationPhysicsDebugWindow) ---

	/// <summary>Master switch for prefab-scene physics; the world itself is still created lazily.</summary>
	public bool ScenePhysicsEnabled { get; set; } = true;

	/// <summary>Gravity along Y in world units; tunable because model scale is arbitrary.</summary>
	public float SceneGravity { get; set; } = -9.81f;

	/// <summary>Pauses the whole simulation; not the same as a zero timestep.</summary>
	public bool ScenePhysicsPaused { get; set; } = false;

	/// <summary>Simulation time scale.</summary>
	public float ScenePhysicsTimeScale { get; set; } = 1f;

	/// <summary>Debug-line brightness; lines draw into the HDR target before tonemap, so exposure is unknown.</summary>
	public float DebugLineIntensity { get; set; } = 4f;

	public AnimationDebugOptions AnimationDebug { get; set; } = new();

	public PhysicsDebugOptions PhysicsDebug { get; set; } = new();

	// --- Preview Graphics (see SettingsWindow.DrawGraphicsSection) ---

	/// <summary>Normal maps in the lighting preview (live toggle).</summary>
	public bool PreviewNormalMaps { get; set; } = true;

	/// <summary>Baked AO (occlusionTexture) in the lighting preview (live toggle).</summary>
	public bool PreviewBakedOcclusion { get; set; } = true;

	/// <summary>Sun shadows in preview; disabling removes both the cascade and the sampling.</summary>
	public bool PreviewShadows { get; set; } = true;

	/// <summary>SSAO pass in preview; applied live via SetFeatures, no model reload.</summary>
	public bool PreviewSsao { get; set; } = true;

	/// <summary>AO technique when <see cref="PreviewSsao"/> is on; stored as a string for readable JSON.</summary>
	[JsonConverter(typeof(JsonStringEnumConverter))]
	public AmbientOcclusionMode PreviewAoMode { get; set; } = AmbientOcclusionMode.Ssao;

	/// <summary>SSGI pass in preview (one screen-space light bounce); live pipeline feature.</summary>
	public bool PreviewSsgi { get; set; } = true;

	/// <summary>Tonemap curve: 0 = PBR Neutral (identity below ~0.76, intentionally flat), 1 = ACES, 2 = AgX.</summary>
	public int ToneCurve { get; set; } = 0;

	// --- Motion vectors (see MotionVectorPass) ---

	/// <summary>Compute screen motion vectors (RG16F buffer); frame is unchanged until a consumer reads it.</summary>
	public bool PreviewMotionVectors { get; set; } = false;

	/// <summary>Debug view of motion vectors: R = X offset, G = Y offset, flat grey = zero.</summary>
	public bool MotionVectorDebugView { get; set; } = false;

	/// <summary>Pixel offset at which the debug scale saturates; beyond it the area turns yellow.</summary>
	public float MotionVectorDebugRange { get; set; } = MotionVectorDebugPassResources.DefaultRangePixels;

	/// <summary>Sub-pixel projection jitter (Halton 2/3, 16 phases); visibly shimmers without a TAA/upscaler consumer.</summary>
	public bool TemporalJitter { get; set; } = false;

	/// <summary>Scene render scale (0.25..1); tonemap upsamples to display resolution. 1 = off.</summary>
	public float RenderScale { get; set; } = 1f;

	/// <summary>Temporal upscale (TAAU); requires motion vectors and silently turns off without them.</summary>
	public bool TemporalUpscale { get; set; } = false;

	/// <summary>Upscaler backend: 0 = built-in TAAU, 1 = native FSR, 2 = native DLSS; falls back to TAAU if unavailable.</summary>
	public int UpscalerBackend { get; set; } = 0;

	/// <summary>Current-frame weight in the TAAU accumulator: lower = more stable, higher = more responsive.</summary>
	public float TaauBlendAlpha { get; set; } = TemporalUpscalePassResources.DefaultBlendAlpha;

	/// <summary>FSR built-in sharpen (RCAS) strength, 0..1; 0 = off.</summary>
	public float FsrSharpness { get; set; } = 0f;

	/// <summary>FSR provider: 0 = auto (FSR 4 > FSR 2, skipping 3.x), 1 = force FSR 2, 2 = force FSR 3.1 (degraded on current SDK).</summary>
	public int FsrProvider { get; set; } = 0;

	/// <summary>DLSS quality combo index: 0 = Performance, 1 = Balanced, 2 = Quality, 3 = DLAA; change recreates the NGX feature.</summary>
	public int DlssQuality { get; set; } = 1;

	// --- Color grading and vignette (see ColorGradePass) ---

	/// <summary>Enables the final color grade pass; works in both HDR and LDR modes.</summary>
	public bool PreviewColorGrade { get; set; } = false;

	/// <summary>Saturation: 1 = unchanged, 0 = greyscale.</summary>
	public float GradeSaturation { get; set; } = ColorGradePassResources.DefaultSaturation;

	/// <summary>Contrast around mid-grey: 1 = unchanged.</summary>
	public float GradeContrast { get; set; } = ColorGradePassResources.DefaultContrast;

	/// <summary>Midtone gamma: 1 = unchanged, higher = brighter midtones.</summary>
	public float GradeGamma { get; set; } = ColorGradePassResources.DefaultGamma;

	/// <summary>Temperature: -1 cooler, +1 warmer; luminance-normalized so exposure is unchanged.</summary>
	public float GradeTemperature { get; set; } = ColorGradePassResources.DefaultTemperature;

	/// <summary>Tint: -1 green, +1 magenta.</summary>
	public float GradeTint { get; set; } = ColorGradePassResources.DefaultTint;

	/// <summary>Shadow tint, additive; neutral is black.</summary>
	public float GradeShadowR { get; set; }
	public float GradeShadowG { get; set; }
	public float GradeShadowB { get; set; }

	/// <summary>Highlight tint, multiplicative; neutral is white.</summary>
	public float GradeHighlightR { get; set; } = 1f;
	public float GradeHighlightG { get; set; } = 1f;
	public float GradeHighlightB { get; set; } = 1f;

	/// <summary>Vignette intensity; 0 = none.</summary>
	public float VignetteIntensity { get; set; } = ColorGradePassResources.DefaultVignetteIntensity;

	/// <summary>Clean-zone radius, in fractions of the half-frame.</summary>
	public float VignetteRadius { get; set; } = ColorGradePassResources.DefaultVignetteRadius;

	/// <summary>Vignette edge softness.</summary>
	public float VignetteSmoothness { get; set; } = ColorGradePassResources.DefaultVignetteSmoothness;

	/// <summary>Roundness: 1 = aspect-corrected circle, 0 = full-frame oval.</summary>
	public float VignetteRoundness { get; set; } = ColorGradePassResources.DefaultVignetteRoundness;

	// --- Bloom (see BloomPass) ---

	/// <summary>Enables the bloom pass; live pipeline feature.</summary>
	public bool PreviewBloom { get; set; } = false;

	/// <summary>Brightness threshold in display units, tied to auto-exposure so it is scene-brightness independent.</summary>
	public float BloomThreshold { get; set; } = BloomPassResources.DefaultThreshold;

	/// <summary>Soft-knee width around the threshold; without it gradients show a step.</summary>
	public float BloomKnee { get; set; } = BloomPassResources.DefaultKnee;

	/// <summary>Upsample tent radius in texels; wider = softer, wider halo.</summary>
	public float BloomRadius { get; set; } = BloomPassResources.DefaultRadius;

	/// <summary>Overall bloom intensity.</summary>
	public float BloomIntensity { get; set; } = BloomPassResources.DefaultIntensity;

	// --- Atmospheric fog (see FogPass) ---

	/// <summary>Enables the fog pass; live pipeline feature.</summary>
	public bool PreviewFog { get; set; } = false;

	/// <summary>Medium density at the reference height, 1/world unit.</summary>
	public float FogDensity { get; set; } = FogPassResources.DefaultDensity;

	/// <summary>Density falloff with height; 0 = uniform haze.</summary>
	public float FogHeightFalloff { get; set; } = FogPassResources.DefaultHeightFalloff;

	/// <summary>Height where density equals <see cref="FogDensity"/>, world units.</summary>
	public float FogHeightRef { get; set; } = FogPassResources.DefaultHeightRef;

	/// <summary>Fog-free distance; keeps haze off nearby objects.</summary>
	public float FogStartDistance { get; set; } = FogPassResources.DefaultStartDistance;

	/// <summary>Maximum fog distance; the sky uses the same value since it has no depth.</summary>
	public float FogMaxDistance { get; set; } = FogPassResources.DefaultMaxDistance;

	/// <summary>Opacity ceiling 0..1; below 1 something always shows through the haze.</summary>
	public float FogMaxOpacity { get; set; } = FogPassResources.DefaultMaxOpacity;

	/// <summary>Medium color (linear RGB).</summary>
	public float FogColorR { get; set; } = FogPassResources.DefaultColorR;
	public float FogColorG { get; set; } = FogPassResources.DefaultColorG;
	public float FogColorB { get; set; } = FogPassResources.DefaultColorB;

	/// <summary>Sun-lit medium color (linear RGB).</summary>
	public float FogSunColorR { get; set; } = FogPassResources.DefaultSunColorR;
	public float FogSunColorG { get; set; } = FogPassResources.DefaultSunColorG;
	public float FogSunColorB { get; set; } = FogPassResources.DefaultSunColorB;

	/// <summary>Sun-glow strength 0..1.</summary>
	public float FogSunStrength { get; set; } = FogPassResources.DefaultSunStrength;

	/// <summary>Sun-spot sharpness: small = wide glow, large = compact halo.</summary>
	public float FogSunSharpness { get; set; } = FogPassResources.DefaultSunSharpness;

	// --- Volumetric light: god rays and volumetric fog (see VolumetricLightPass) ---
	// Separate from analytic fog on purpose: that pass cannot see shadows, and light
	// shafts are shadows in the medium. The passes are independent and combine well.

	/// <summary>Enables the volumetric light pass; live pipeline feature.</summary>
	public bool PreviewVolumetric { get; set; } = false;

	/// <summary>Medium density at the reference height, 1/world unit.</summary>
	public float VolumetricDensity { get; set; } = VolumetricLightPassResources.DefaultDensity;

	/// <summary>Density falloff with height; 0 = uniform medium.</summary>
	public float VolumetricHeightFalloff { get; set; } = VolumetricLightPassResources.DefaultHeightFalloff;

	/// <summary>Height where density equals <see cref="VolumetricDensity"/>, world units.</summary>
	public float VolumetricHeightRef { get; set; } = VolumetricLightPassResources.DefaultHeightRef;

	/// <summary>March start distance; near the camera the medium only produces noise.</summary>
	public float VolumetricStartDistance { get; set; } = VolumetricLightPassResources.DefaultStartDistance;

	/// <summary>March end distance; directly sets pass cost and step size.</summary>
	public float VolumetricMaxDistance { get; set; } = VolumetricLightPassResources.DefaultMaxDistance;

	/// <summary>March step count; affects shaft edge smoothness only, not brightness.</summary>
	public int VolumetricSteps { get; set; } = VolumetricLightPassResources.DefaultSteps;

	/// <summary>Medium opacity ceiling 0..1.</summary>
	public float VolumetricMaxOpacity { get; set; } = VolumetricLightPassResources.DefaultMaxOpacity;

	/// <summary>How much shadow cuts sun in-scattering: 1 = real shafts, 0 = flat fog.</summary>
	public float VolumetricShadowStrength { get; set; } = VolumetricLightPassResources.DefaultShadowStrength;

	/// <summary>Scattering coefficient: density-to-light conversion.</summary>
	public float VolumetricScattering { get; set; } = VolumetricLightPassResources.DefaultScattering;

	/// <summary>Extinction coefficient; separate from scattering so the medium can glow yet stay near-transparent.</summary>
	public float VolumetricExtinction { get; set; } = VolumetricLightPassResources.DefaultExtinction;

	/// <summary>Phase anisotropy -1..1; &gt;0 = forward scattering, shafts flare when looking toward the sun.</summary>
	public float VolumetricAnisotropy { get; set; } = VolumetricLightPassResources.DefaultAnisotropy;

	/// <summary>Sun in-scatter color (linear RGB).</summary>
	public float VolumetricSunColorR { get; set; } = VolumetricLightPassResources.DefaultSunColorR;
	public float VolumetricSunColorG { get; set; } = VolumetricLightPassResources.DefaultSunColorG;
	public float VolumetricSunColorB { get; set; } = VolumetricLightPassResources.DefaultSunColorB;

	/// <summary>Sun in-scatter strength; main god-ray brightness knob.</summary>
	public float VolumetricSunIntensity { get; set; } = VolumetricLightPassResources.DefaultSunIntensity;

	/// <summary>Sky in-scatter color (linear RGB); lights the medium in shadow, unaffected by shadows.</summary>
	public float VolumetricAmbientColorR { get; set; } = VolumetricLightPassResources.DefaultAmbientColorR;
	public float VolumetricAmbientColorG { get; set; } = VolumetricLightPassResources.DefaultAmbientColorG;
	public float VolumetricAmbientColorB { get; set; } = VolumetricLightPassResources.DefaultAmbientColorB;

	/// <summary>Sky in-scatter strength; without it shadowed medium is black and shafts look cut out.</summary>
	public float VolumetricAmbientIntensity { get; set; } = VolumetricLightPassResources.DefaultAmbientIntensity;

	/// <summary>Sky in-scatter attenuation inside shadowed volume; prevents a uniform milky veil.</summary>
	public float VolumetricAmbientShadowFloor { get; set; } = VolumetricLightPassResources.DefaultAmbientShadowFloor;

	/// <summary>Punctual-light in-scatter multiplier: 0 = sun/sky only, 1 = physical share.</summary>
	public float VolumetricPunctualScatter { get; set; } = 1f;

	/// <summary>Sun angular DIAMETER in degrees (PCSS penumbra width); real sun is ~0.53.</summary>
	public float SunAngularSize { get; set; } = 1f;

	/// <summary>Shadow filter mode, shader values (SHADOW_MODE_*): 0 must stay PCSS since non-preview scenes leave the cbuffer empty.</summary>
	public int ShadowFilterMode { get; set; } = 0;

	/// <summary>Max texture side on model load. Loader decodes ALL textures before upload, so peak RAM
	/// scales with the square of this value (LOH pressure); halving it cuts the peak 4x. Reload knob.</summary>
	public int PreviewMaxTextureSize { get; set; } = 2048;

	/// <summary>Distance streaming of scene models; off = everything permanently resident, not unloaded.
	/// Also the only editor path where batch sets change after the first frame, so the toggle
	/// separates scene bugs from streaming bugs.</summary>
	public bool SceneStreaming { get; set; } = true;

	/// <summary>GPU skinning of scene models; off draws bind pose via the static path.
	/// Applies at instancing time, so already-shown models must be reopened.</summary>
	public bool SceneSkinning { get; set; } = true;

	/// <summary>Streaming radius in world units; unload adds hysteresis (see ModelStreamer.StreamOutHysteresis).</summary>
	public float SceneStreamingRadius { get; set; } = 200f;

	/// <summary>OBSOLETE: MSAA was removed; field kept only so old settings JSON still parses. Ignored.</summary>
	public int PreviewMsaaSamples { get; set; } = 4;

	/// <summary>Sky background (environment as frame background); live pipeline feature.</summary>
	public bool PreviewSkyBackground { get; set; } = true;

	/// <summary>Anisotropic texture filtering (8x); applies on next model load.</summary>
	public bool PreviewAnisotropicFiltering { get; set; } = true;

	// --- AO (SSAO/GTAO, see SsaoPass.cs / GraphicsSettingsWindow) ---

	/// <summary>AO strength: visibility contrast exponent for GTAO, intensity multiplier for SSAO. Live.</summary>
	public float AoStrength { get; set; } = 1.5f;

	/// <summary>AO visibility floor; the screen-space estimate may not darken to zero. Live.</summary>
	public float AoFloor { get; set; } = 0.12f;

	/// <summary>AO world radius as a fraction of the model bounds radius. Live.</summary>
	public float AoRadiusFraction { get; set; } = ModelViewportEnvironment.AoRangeOfBoundsRadius;

	/// <summary>AO radius in world units; 0 = derive from model bounds via <see cref="AoRadiusFraction"/>. Live.</summary>
	public float AoRadiusWorld { get; set; } = 0f;

	/// <summary>AO debug view: composite outputs visibility in grayscale instead of multiplying the frame. Live.</summary>
	public bool AoDebugView { get; set; } = false;

	// --- SSGI (screen-space light bounce, see SsgiPass.cs / GraphicsSettingsWindow) ---

	/// <summary>Bounce intensity multiplier. Live.</summary>
	public float SsgiIntensity { get; set; } = SsgiPassResources.DefaultIntensity;

	/// <summary>Taps per pixel (4..<see cref="SsgiPassResources.MaxSampleCount"/>): noise vs cost. Live.</summary>
	public int SsgiSamples { get; set; } = SsgiPassResources.DefaultSampleCount;

	/// <summary>Per-tap luminance ceiling (firefly clamp); 0 = unclamped. Live.</summary>
	public float SsgiMaxLuminance { get; set; } = SsgiPassResources.DefaultMaxLuminance;

	/// <summary>Bounce saturation: 1 = sender color as-is, 0 = grey bounce. Live.</summary>
	public float SsgiSaturation { get; set; } = SsgiPassResources.DefaultSaturation;

	/// <summary>Bilateral blur radius of the bounce in composite, pixels. Live.</summary>
	public int SsgiBlurRadius { get; set; } = SsgiPassResources.DefaultBlurRadius;

	/// <summary>GI gather radius in world units; 0 = derive from model bounds via <see cref="SsgiRadiusFraction"/>. Live.</summary>
	public float SsgiRadiusWorld { get; set; } = 0f;

	/// <summary>GI gather radius as a fraction of the model bounds radius. Live.</summary>
	public float SsgiRadiusFraction { get; set; } = ModelViewportEnvironment.GiRangeOfBoundsRadius;

	/// <summary>SSGI debug view: composite outputs only the bounce. Live.</summary>
	public bool SsgiDebugView { get; set; } = false;

	// --- SSR (stochastic screen-space reflections, see SsrPass.cs / GraphicsSettingsWindow) ---

	/// <summary>SSR pass; result REPLACES env specular. Live feature; pulls in the motion vector buffer.</summary>
	public bool PreviewSsr { get; set; } = false;

	/// <summary>RT fallback for off-screen rays via inline RayQuery; needs device inline RT and hardware probe GI.</summary>
	public bool SsrRayTraced { get; set; } = false;

	/// <summary>Off-screen RT hit albedo: 0 = per-triangle average, 1 = 128^2 tile atlas, 2 = bindless
	/// full-size array (silently falls back to atlas without array indexing support).</summary>
	public int SsrHitTextures { get; set; } = 0;

	/// <summary>Reflection intensity multiplier (1 = energy-correct). Live.</summary>
	public float SsrIntensity { get; set; } = SsrPassResources.DefaultIntensity;

	/// <summary>Perceptual roughness ceiling; reflections fade out above it. Live.</summary>
	public float SsrMaxRoughness { get; set; } = SsrPassResources.DefaultMaxRoughness;

	/// <summary>Surface thickness for ray-depth intersection, world units. Live.</summary>
	public float SsrThickness { get; set; } = SsrPassResources.DefaultThickness;

	/// <summary>Reflection ray range, world units. Live.</summary>
	public float SsrMaxDistance { get; set; } = SsrPassResources.DefaultMaxDistance;

	/// <summary>Temporal history weight (0..0.97): higher = smoother but more laggy. Live.</summary>
	public float SsrHistoryWeight { get; set; } = SsrPassResources.DefaultHistoryWeight;

	/// <summary>Resolve quality (1..4): neighbor rays reused per pixel. Live.</summary>
	public int SsrRaysPerPixel { get; set; } = SsrPassResources.DefaultRaysPerPixel;

	/// <summary>SSR debug view: 0 frame, 1 reflections only, 2 confidence, 3 G-buffer normals, 4 RT hit albedo. Live.</summary>
	public int SsrDebugView { get; set; } = 0;

	/// <summary>Total RT reflection bounces (1..4); 2+ adds mirror continuations off metallic hits. Live.</summary>
	public int SsrRtBounces { get; set; } = SsrPassResources.DefaultRtBounces;

	/// <summary>Trace mode (RT only): 0 = screen march then RT for misses, 1 = RT immediately
	/// (hit radiance still reprojected from screen). Live.</summary>
	public int SsrTraceMode { get; set; } = 0;

	// --- Auto-exposure (see EyeAdaptationPass.cs / TonemapPass.cs / GraphicsSettingsWindow) ---

	/// <summary>Preview auto-exposure; enabling switches the preview pipeline to HDR (RGBA16F + separate tonemap).</summary>
	public bool PreviewEyeAdaptation { get; set; } = false;

	/// <summary>Scene View HDR mode; applies live, resident models migrate without disk reload.</summary>
	public bool SceneViewHdr { get; set; } = false;

	/// <summary>Scene View camera fly speed, units/sec; persisted so per-project tuning survives restarts.</summary>
	public float SceneCameraSpeed { get; set; } = SceneCamera.DefaultFlySpeed;

	/// <summary>Key value: target average luminance; 0.18 is photographic middle grey. Live.</summary>
	public float EyeAdaptationKey { get; set; } = 0.18f;

	/// <summary>Lower bound of measured luminance; keeps near-black frames from being pulled to white noise. Live.</summary>
	public float EyeAdaptationMinLuminance { get; set; } = 0.03f;

	/// <summary>Upper bound of measured luminance; keeps sun-in-lens frames from crushing to black. Live.</summary>
	public float EyeAdaptationMaxLuminance { get; set; } = 8f;

	/// <summary>Adaptation speed toward brighter frames, 1/sec. Live.</summary>
	public float EyeAdaptationSpeedUp { get; set; } = 3f;

	/// <summary>Adaptation speed toward darker frames, 1/sec; slower on purpose, like the eye. Live.</summary>
	public float EyeAdaptationSpeedDown { get; set; } = 1f;

	/// <summary>Exposure compensation in stops on top of auto-exposure. Live.</summary>
	public float EyeAdaptationExposureCompensation { get; set; } = 0f;

	// --- Probe GI (DDGI-lite, see ProbeGi.cs / GraphicsSettingsWindow) ---

	/// <summary>Probe GI in preview; disabling falls back to constant ambient, enabling starts a bake.</summary>
	public bool PreviewProbeGi { get; set; } = true;

	/// <summary>Sun intensity, shared by the analytic key light and the bounce bake. Rebake.</summary>
	public float ProbeGiSunIntensity { get; set; } = 2f;

	/// <summary>Sky brightness multiplier in the probe bake. Rebake.</summary>
	public float ProbeGiSkyIntensity { get; set; } = 1f;

	/// <summary>Multiplier on baked probe irradiance in the shader. Live.</summary>
	public float ProbeGiAmbientBoost { get; set; } = 1f;

	/// <summary>Floor for shadowing the SUN share of probe ambient (0 = fully shadowed). Live.</summary>
	public float ProbeGiShadowFloor { get; set; } = 0.3f;

	/// <summary>Floor for shadowing the SKY share of probe ambient; 1 = sky untouched by shadow. Live.</summary>
	public float ProbeGiSkyShadowFloor { get; set; } = 1f;

	/// <summary>Probe GI debug view: R = sun share, G = sky visibility, B = key screen shadow. Live.</summary>
	public bool ProbeGiDebugView { get; set; } = false;

	/// <summary>Probe visibility octahedral map side (DDGI depth), 8..24; doubling needs matching
	/// Rays per probe growth or quality drops. Rebake.</summary>
	public int ProbeGiVisRes { get; set; } = 8;

	/// <summary>Probe BVH debug: wireframe node boxes over the scene. Live.</summary>
	public bool ProbeGiBvhDebug { get; set; } = false;

	/// <summary>BVH debug descent depth (0 = root only); beyond ~10 the view is unreadable.</summary>
	public int ProbeGiBvhDebugDepth { get; set; } = 6;

	/// <summary>Show only BVH LEAVES instead of all nodes down to the depth.</summary>
	public bool ProbeGiBvhDebugLeaves { get; set; } = false;

	/// <summary>Probe placement debug: green = on grid node, yellow-red = relocated, blue = invalid. Live.</summary>
	public bool ProbeGiDebugProbes { get; set; } = false;

	/// <summary>Env-specular attenuation floor from baked sky visibility; 0 = no reflections indoors. Live.</summary>
	public float ProbeGiSpecularFloor { get; set; } = 0.2f;

	/// <summary>Probe sample normal bias in fractions of the smallest cell; fights leaks through thin walls. Live.</summary>
	public float ProbeGiNormalBias { get; set; } = 0.3f;

	/// <summary>View share of the sample bias direction, 0..1 (rest is normal). 1 = no faceting but
	/// view-dependent lighting; 0 = view-independent but triangle edges show. Paper uses 0.8. Live.</summary>
	public float ProbeGiViewBias { get; set; } = 1f;

	/// <summary>Rays per probe (16-512); more = smoother field, linearly longer bake. Rebake.</summary>
	public int ProbeGiRaysPerProbe { get; set; } = ProbeGiBaker.DefaultRaysPerProbe;

	/// <summary>Gather iterations (1-6); each after the first adds a bounce. Rebake.</summary>
	public int ProbeGiBounces { get; set; } = 2;

	/// <summary>Bounce color saturation in the bake (0-1); attenuates chroma only, not brightness. Rebake.</summary>
	public float ProbeGiBounceSaturation { get; set; } = 0.5f;

	/// <summary>Probe grid density: cells along the largest scene extent (4-64). Rebake.</summary>
	public float ProbeGiGridDensity { get; set; } = 22f;

	/// <summary>Probe count budget (512..2M); cell size grows until the grid fits. Rebake.</summary>
	public int ProbeGiMaxProbes { get; set; } = 8192;

	/// <summary>Use hardware ray tracing (RayQuery over BLAS/TLAS) for GPU rounds; silently ignored without inline RT.</summary>
	public bool ProbeGiHardwareRayTracing { get; set; } = false;

	/// <summary>Realtime mode: rounds keep running, field accumulates via exponential average
	/// so it tracks lighting changes instead of converging. Live; keeps the accumulated field.</summary>
	public bool ProbeGiRealtime { get; set; } = false;

	/// <summary>Rays per probe per realtime round (8-1024); the ray fan is shared, so estimate error
	/// shows as whole-scene brightness breathing, fixable only by more rays. Live.</summary>
	public int ProbeGiRealtimeRays { get; set; } = 64;

	/// <summary>Realtime round weight (exponential-average alpha); lower = calmer field, slower response. Live.</summary>
	public float ProbeGiRealtimeBlend { get; set; } = ProbeGiBaker.RealtimeBlend;

	/// <summary>Per-round probe change limit as a fraction of its brightness; 0 = unlimited.
	/// Selective anti-flicker: quiet probes unaffected, noisy ones crawl instead of flashing. Live.</summary>
	public float ProbeGiRealtimeMaxStep { get; set; } = 0.03f;

	/// <summary>Draw probes as spheres: color = SH L0, red = inside geometry, cyan rim = relocated. Live.</summary>
	public bool ProbeGiShowProbes { get; set; } = false;

	/// <summary>Perceptual accumulation gamma in realtime, 1 = linear; suppresses fireflies at the
	/// cost of a slight downward bias on noisy estimates. Live.</summary>
	public float ProbeGiRealtimeGamma { get; set; } = 5f;

	/// <summary>Mean variability threshold below which realtime rounds stop entirely; 0 = disabled. Live.</summary>
	public float ProbeGiVariabilityThreshold { get; set; } = 0.08f;

	/// <summary>Probe relocation range in fractions of the grid step (0 = off); moves probes out of walls. Live.</summary>
	public float ProbeGiRealtimeRelocation { get; set; } = 0.45f;


	// IncludeFields is required: AnimationDebug/PhysicsDebug are structs of public FIELDS,
	// which System.Text.Json skips by default, silently serializing them as "{}".
	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		WriteIndented = true,
		IncludeFields = true,
	};

	public static EditorSettings Load()
	{
		try
		{
			if (File.Exists(FilePath))
			{
				var json = File.ReadAllText(FilePath);
				var loaded = JsonSerializer.Deserialize<EditorSettings>(json, SerializerOptions);
				if (loaded is not null)
				{
					loaded.UiScalePercent = ClampToAllowedScale(loaded.UiScalePercent);
					return loaded;
				}
			}
		}
		catch
		{
			// Corrupt or unreadable settings fall back to defaults.
		}

		return new EditorSettings();
	}

	public void Save()
	{
		try
		{
			var directory = Path.GetDirectoryName(FilePath);
			if (!string.IsNullOrEmpty(directory))
			{
				Directory.CreateDirectory(directory);
			}

			var json = JsonSerializer.Serialize(this, SerializerOptions);
			File.WriteAllText(FilePath, json);
		}
		catch
		{
			// Failing to persist settings must not crash the editor.
		}
	}

	/// <summary>Snaps an arbitrary percent to the nearest allowed UI scale.</summary>
	public static int ClampToAllowedScale(int percent)
	{
		var closest = AllowedUiScalePercents[0];
		var closestDistance = Math.Abs(percent - closest);

		foreach (var allowed in AllowedUiScalePercents)
		{
			var distance = Math.Abs(percent - allowed);
			if (distance < closestDistance)
			{
				closest = allowed;
				closestDistance = distance;
			}
		}

		return closest;
	}
}

