using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics.Diligent;
using DecaEngine.Graphics.ProbeGi;
using Engine.ImGui.Core;
using Friflo.Engine.ECS;
using Hexa.NET.ImGui;
using DecaEngine.Scene;
using DecaEngine.Animation;
using DecaEngine.Graphics;

namespace DecaEngine.Editor;

/// <summary>Post-effect sections of the Graphics window: grade, bloom, fog, volumetrics, exposure.</summary>
public partial class GraphicsSettingsWindow
{
	private void DrawColorGradeSection()
	{
		ImGui.Spacing();

		var grade = _settings.PreviewColorGrade;
		if (ImGui.Checkbox("Color grading", ref grade))
		{
			_settings.PreviewColorGrade = grade;
			_changed = true;
		}
		Tooltip("Final pass over the finished frame: saturation, contrast, white balance,\nshadow and highlight tinting, vignette.\nAt default values the frame is UNCHANGED - the grade is yours to dial in.\nWorks in both HDR and LDR.");

		if (!grade)
		{
			return;
		}

		ImGui.Spacing();
		ImGui.TextDisabled("Tone (live):");

		var saturation = _settings.GradeSaturation;
		if (Slider("Saturation", ref saturation, 0f, 2f, "%.2f"))
		{
			_settings.GradeSaturation = saturation;
		}
		Tooltip("1 - as is, 0 - greyscale frame, above 1 - more vivid colours.\nThe main tool against oversaturated materials: one muted envelope with one or two\naccents reads richer than a dozen pure colours.");

		var contrast = _settings.GradeContrast;
		if (Slider("Contrast", ref contrast, 0f, 2f, "%.2f"))
		{
			_settings.GradeContrast = contrast;
		}
		Tooltip("Spreads darks and lights around the mid tone. 1 - no change.");

		var gamma = _settings.GradeGamma;
		if (Slider("Gamma", ref gamma, 0.2f, 3f, "%.2f"))
		{
			_settings.GradeGamma = gamma;
		}
		Tooltip("Mid tones: higher lifts the midpoint while keeping black and white in place.");

		var temperature = _settings.GradeTemperature;
		if (Slider("Temperature", ref temperature, -1f, 1f, "%.2f"))
		{
			_settings.GradeTemperature = temperature;
		}
		Tooltip("Negative - cooler (toward blue), positive - warmer (toward amber).\nLuminance-normalised: exposure is untouched, no compensation needed.");

		var tint = _settings.GradeTint;
		if (Slider("Tint", ref tint, -1f, 1f, "%.2f"))
		{
			_settings.GradeTint = tint;
		}
		Tooltip("Negative - toward green, positive - toward magenta. The second white balance axis.");

		ImGui.Spacing();
		ImGui.TextDisabled("Tinting (live):");

		var shadows = new Vector3(_settings.GradeShadowR, _settings.GradeShadowG, _settings.GradeShadowB);
		ImGui.SetNextItemWidth(180 * _scale);
		if (ImGui.ColorEdit3("Shadows", ref shadows))
		{
			_settings.GradeShadowR = shadows.X;
			_settings.GradeShadowG = shadows.Y;
			_settings.GradeShadowB = shadows.Z;
			_changed = true;
		}
		Tooltip("ADDITIVE tint: lifts the blacks only, leaves highlights alone.\nNeutral is black. Classic move: push shadows toward cool.");

		var highlights = new Vector3(_settings.GradeHighlightR, _settings.GradeHighlightG, _settings.GradeHighlightB);
		ImGui.SetNextItemWidth(180 * _scale);
		if (ImGui.ColorEdit3("Highlights", ref highlights))
		{
			_settings.GradeHighlightR = highlights.X;
			_settings.GradeHighlightG = highlights.Y;
			_settings.GradeHighlightB = highlights.Z;
			_changed = true;
		}
		Tooltip("MULTIPLICATIVE tint: colours the lights, black stays black.\nNeutral is white. Paired with cool shadows it gives a warm key.");

		ImGui.Spacing();
		ImGui.TextDisabled("Vignette (live):");

		var vignette = _settings.VignetteIntensity;
		if (Slider("Strength", ref vignette, 0f, 1f, "%.2f"))
		{
			_settings.VignetteIntensity = vignette;
		}
		Tooltip("Darkening toward the frame edges. 0 - off.\nPart of why a frame reads as COMPOSED rather than as a screenshot.");

		var vignetteRadius = _settings.VignetteRadius;
		if (Slider("Radius", ref vignetteRadius, 0.1f, 1.5f, "%.2f"))
		{
			_settings.VignetteRadius = vignetteRadius;
		}
		Tooltip("Size of the clean area in the centre. Larger pushes the vignette out to the edges.");

		var vignetteSmooth = _settings.VignetteSmoothness;
		if (Slider("Smoothness", ref vignetteSmooth, 0.01f, 1f, "%.2f"))
		{
			_settings.VignetteSmoothness = vignetteSmooth;
		}
		Tooltip("Width of the transition. Small values show a visible ring - usually you want it soft.");

		var vignetteRound = _settings.VignetteRoundness;
		if (Slider("Roundness", ref vignetteRound, 0f, 1f, "%.2f"))
		{
			_settings.VignetteRoundness = vignetteRound;
		}
		Tooltip("1 - a circle corrected for the frame's aspect, 0 - an oval stretched across the frame.");
	}

	// The toggle is environment-level (the pass owns its target chain); the rest is live.
	private void DrawBloomSection()
	{
		ImGui.Spacing();

		var bloom = _settings.PreviewBloom;
		if (ImGui.Checkbox("Bloom", ref bloom))
		{
			_settings.PreviewBloom = bloom;
			_changed = true;
		}
		Tooltip("Glow around bright areas. It does not \"make things brighter\" - it makes a source READ AS LIGHT:\na display cannot show a lamp brighter than white paper, and it is the scattering in the optics\nthat conveys the difference to the eye.");

		if (!bloom)
		{
			return;
		}

		ImGui.Spacing();

		var threshold = _settings.BloomThreshold;
		if (Slider("Threshold", ref threshold, 0f, 4f, "%.2f"))
		{
			_settings.BloomThreshold = threshold;
		}
		Tooltip("Brightness above which the glow starts, in DISPLAY units.\n1.0 - only true overexposure glows (what the display cannot show any brighter).\nTied to auto exposure, so it does not depend on the scene's absolute brightness.\nBelow 1.0 things that are not overexposed start to glow too.");

		var knee = _settings.BloomKnee;
		if (Slider("Threshold knee", ref knee, 0.0001f, 1f, "%.3f"))
		{
			_settings.BloomKnee = knee;
		}
		Tooltip("Width of the soft transition around the threshold.\nWithout it a gradient shows a step: a surface brightens and exactly at the threshold\na halo suddenly switches on.");

		var radius = _settings.BloomRadius;
		if (Slider("Radius", ref radius, 0f, 4f, "%.2f"))
		{
			_settings.BloomRadius = radius;
		}
		Tooltip("Tent filter width while combining the chain upward.\nLarger spreads the halo softer and further; 0 uses plain bilinear sampling\nand rings appear between levels.");

		var intensity = _settings.BloomIntensity;
		if (Slider("Intensity", ref intensity, 0f, 3f, "%.2f"))
		{
			_settings.BloomIntensity = intensity;
		}
		Tooltip("How much halo to blend into the frame. Normalised by the number of chain levels,\nso it does not jump when the viewport resolution changes.");
	}

	// The toggle is environment-level (the pass needs depth and a scene copy); the rest is live.
	private void DrawFogSection()
	{
		ImGui.Spacing();

		var fog = _settings.PreviewFog;
		if (ImGui.Checkbox("Atmospheric fog", ref fog))
		{
			_settings.PreviewFog = fog;
			_changed = true;
		}
		Tooltip("Aerial perspective: distant planes lose contrast and fade into haze.\nThe main source of the sense of DEPTH in a frame - no amount of GI replaces it.");

		if (!fog)
		{
			return;
		}

		ImGui.Spacing();

		// Logarithmic: the useful density range is thousandths, invisible on a linear slider.
		var density = _settings.FogDensity;
		if (Slider("Density", ref density, 0.0002f, 0.5f, "%.4f",
			ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.Logarithmic))
		{
			_settings.FogDensity = density;
		}
		Tooltip("The main knob. Density at the reference height, per world unit.\nScale dependent: a scene tens of units across needs hundredths, a single model tenths.");

		var heightFalloff = _settings.FogHeightFalloff;
		if (Slider("Height falloff", ref heightFalloff, 0f, 1f, "%.3f"))
		{
			_settings.FogHeightFalloff = heightFalloff;
		}
		Tooltip("How fast the haze thins out with height.\n0 - uniform fog with no height profile;\nhigher - a low ground layer with the tops of geometry sticking out.");

		var heightRef = _settings.FogHeightRef;
		if (Slider("Reference height", ref heightRef, -50f, 50f, "%.1f"))
		{
			_settings.FogHeightRef = heightRef;
		}
		Tooltip("Height (world Y) at which the density equals the value set above.\nUsually the scene's floor level.");

		var start = _settings.FogStartDistance;
		if (Slider("Start distance", ref start, 0f, 50f, "%.1f"))
		{
			_settings.FogStartDistance = start;
		}
		Tooltip("Distance up to which there is no fog at all.\nWithout it the haze settles onto the nearest objects and blurs them.");

		var maxDistance = _settings.FogMaxDistance;
		if (Slider("Max distance", ref maxDistance, 10f, 5000f, "%.0f",
			ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.Logarithmic))
		{
			_settings.FogMaxDistance = maxDistance;
		}
		Tooltip("Distance cap. The SKY gets the same value - the background has no depth,\nand without this the horizon would be the only place with no haze.");

		var maxOpacity = _settings.FogMaxOpacity;
		if (Slider("Max opacity", ref maxOpacity, 0f, 1f, "%.2f"))
		{
			_settings.FogMaxOpacity = maxOpacity;
		}
		Tooltip("How far the haze may hide the distance.\n1 - completely, less - something is always visible through the fog.");

		ImGui.Spacing();
		ImGui.TextDisabled("Color:");

		var color = new Vector3(_settings.FogColorR, _settings.FogColorG, _settings.FogColorB);
		ImGui.SetNextItemWidth(180 * _scale);
		if (ImGui.ColorEdit3("Medium color", ref color))
		{
			_settings.FogColorR = color.X;
			_settings.FogColorG = color.Y;
			_settings.FogColorB = color.Z;
			_changed = true;
		}
		Tooltip("The shadow side of the haze - what the distance fades into AWAY from the sun.\nSlate blue reads as distance, warm reads as dust or smog.");

		var sunColor = new Vector3(_settings.FogSunColorR, _settings.FogSunColorG, _settings.FogSunColorB);
		ImGui.SetNextItemWidth(180 * _scale);
		if (ImGui.ColorEdit3("Inscatter color", ref sunColor))
		{
			_settings.FogSunColorR = sunColor.X;
			_settings.FogSunColorG = sunColor.Y;
			_settings.FogSunColorB = sunColor.Z;
			_changed = true;
		}
		Tooltip("Haze colour toward the sun. Usually warmer and brighter than the medium colour.");

		var sunStrength = _settings.FogSunStrength;
		if (Slider("Inscatter strength", ref sunStrength, 0f, 1f, "%.2f"))
		{
			_settings.FogSunStrength = sunStrength;
		}
		Tooltip("This is why fog is used at all: the haze stops being a grey veil\nand starts to glow on the light's side. 0 - single-colour fog.");

		var sunSharpness = _settings.FogSunSharpness;
		if (Slider("Inscatter sharpness", ref sunSharpness, 1f, 64f, "%.1f",
			ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.Logarithmic))
		{
			_settings.FogSunSharpness = sunSharpness;
		}
		Tooltip("Low values give a wide soft glow across half the sky,\nhigh values a compact halo around the disk.");
	}

	// Toggle is environment-level (needs depth, scene copy and shadow map); the rest is live.
	private void DrawVolumetricSection()
	{
		ImGui.Spacing();

		var volumetric = _settings.PreviewVolumetric;
		if (ImGui.Checkbox("Volumetric light", ref volumetric))
		{
			_settings.PreviewVolumetric = volumetric;
			_changed = true;
		}
		Tooltip("Light shafts (god rays) and glowing volumetric fog.\n" +
			"A ray march that samples the cascaded shadow map at every step -\n" +
			"so the shafts follow the shadow-casting geometry exactly.\n" +
			"It neither replaces nor conflicts with atmospheric fog: that one handles\n" +
			"distant haze, this one scattered light.");

		if (!volumetric)
		{
			return;
		}

		// Without a shadow pass the CPU forces shadow strength to zero, so the sliders do nothing.
		if (!_viewport.VolumetricShadowsAvailable && _sceneViewport?.VolumetricShadowsAvailable != true)
		{
			ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f), "No shadows - no light shafts");
			Tooltip("The march reads shadows from the cascaded shadow map. Without the shadow pass\n" +
				"only flat volumetric fog remains - enable shadows in the Sun & Shadows section.");
		}

		ImGui.Spacing();

		// Logarithmic: the useful range is hundredths, as with fog density.
		var density = _settings.VolumetricDensity;
		if (Slider("Medium density", ref density, 0.0005f, 1f, "%.4f",
			ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.Logarithmic))
		{
			_settings.VolumetricDensity = density;
		}
		Tooltip("The main knob. How much matter is in the air, per world unit.\n" +
			"Higher gives denser shafts and a hazier frame.");

		var sunIntensity = _settings.VolumetricSunIntensity;
		if (Slider("Shaft strength", ref sunIntensity, 0f, 8f, "%.2f"))
		{
			_settings.VolumetricSunIntensity = sunIntensity;
		}
		Tooltip("Brightness of SUN scattering - this is what god rays are.\n" +
			"It is what shadow cuts away: where there is no shadow, the shaft glows.");

		var anisotropy = _settings.VolumetricAnisotropy;
		if (Slider("Anisotropy", ref anisotropy, -0.95f, 0.95f, "%.2f"))
		{
			_settings.VolumetricAnisotropy = anisotropy;
		}
		Tooltip("Directionality of the scattering (Henyey-Greenstein phase function).\n" +
			"0.6..0.85 behaves like real haze: shafts flare when looking TOWARD the sun.\n" +
			"0 - even glow from every direction. Negative - back-scattering (rarely needed).\n" +
			"It does not change total brightness, only how it is distributed by direction.");

		var shadowStrength = _settings.VolumetricShadowStrength;
		if (Slider("Shadow strength", ref shadowStrength, 0f, 1f, "%.2f"))
		{
			_settings.VolumetricShadowStrength = shadowStrength;
		}
		Tooltip("How strongly shadow cuts the shafts. 1 - real shafts with crisp edges,\n" +
			"0 - shadow is ignored and only uniform volumetric fog remains.");

		var punctualScatter = _settings.VolumetricPunctualScatter;
		if (Slider("Punctual light scatter", ref punctualScatter, 0f, 4f, "%.2f"))
		{
			_settings.VolumetricPunctualScatter = punctualScatter;
		}
		Tooltip("Scattering from point/spot lights: a spot's cone and a lamp's halo in the haze.\n" +
			"1 - the physical amount (brightness comes from the lights themselves), 0 - the medium\n" +
			"sees only sun and sky. Lamp shadows cut the cone using the same Shadow strength.");

		ImGui.Spacing();
		ImGui.TextDisabled("March quality:");

		// Upper bound must match the pass clamp, Clamp(4, 256).
		var steps = _settings.VolumetricSteps;
		if (SliderInt("Steps", ref steps, 8, 256))
		{
			_settings.VolumetricSteps = steps;
		}
		Tooltip("The main COST knob of the pass - the steps run per pixel.\n" +
			"It does NOT affect brightness (the integral is analytic over each segment),\n" +
			"only edge smoothness: too few steps give grainy shaft edges.\n" +
			"32-64 is usually enough, above 96 the difference is barely visible.");

		var maxDistance = _settings.VolumetricMaxDistance;
		if (Slider("March distance", ref maxDistance, 10f, 2000f, "%.0f",
			ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.Logarithmic))
		{
			_settings.VolumetricMaxDistance = maxDistance;
		}
		Tooltip("How far the ray travels. The step count is fixed, so twice the distance\n" +
			"means twice the step size and more ragged shafts.\n" +
			"Keep it within the last shadow cascade: beyond it the shafts switch off\n" +
			"all at once (everything past the cascades counts as lit).");

		var start = _settings.VolumetricStartDistance;
		if (Slider("Start distance", ref start, 0f, 20f, "%.2f"))
		{
			_settings.VolumetricStartDistance = start;
		}
		Tooltip("Distance at which the march begins.\nRight at the camera the medium only adds noise and eats steps.");

		ImGui.Spacing();
		ImGui.TextDisabled("Medium optics:");

		var scattering = _settings.VolumetricScattering;
		if (Slider("Scattering", ref scattering, 0f, 4f, "%.2f"))
		{
			_settings.VolumetricScattering = scattering;
		}
		Tooltip("How strongly density turns into LIGHT.\nA global brightness multiplier for the whole effect.");

		var extinction = _settings.VolumetricExtinction;
		if (Slider("Extinction", ref extinction, 0.01f, 4f, "%.2f",
			ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.Logarithmic))
		{
			_settings.VolumetricExtinction = extinction;
		}
		Tooltip("How strongly the medium ABSORBS light passing through it.\n" +
			"Deliberately decoupled from scattering: low extinction with high scattering\n" +
			"gives glowing shafts without hazing up the frame - no real substance behaves\n" +
			"that way, but \"shafts yes, milky distance no\" is the most common request.");

		var maxOpacity = _settings.VolumetricMaxOpacity;
		if (Slider("Max opacity", ref maxOpacity, 0f, 1f, "%.2f"))
		{
			_settings.VolumetricMaxOpacity = maxOpacity;
		}
		Tooltip("How much of the original frame the medium may consume.\n1 - all the way to full milk, less - something is always visible through it.");

		var heightFalloff = _settings.VolumetricHeightFalloff;
		if (Slider("Height falloff", ref heightFalloff, 0f, 1f, "%.3f"))
		{
			_settings.VolumetricHeightFalloff = heightFalloff;
		}
		Tooltip("How fast the medium thins out with height.\n0 - uniform volume;\nhigher - a low ground layer where shafts are only visible near the floor.");

		var heightRef = _settings.VolumetricHeightRef;
		if (Slider("Reference height", ref heightRef, -50f, 50f, "%.1f"))
		{
			_settings.VolumetricHeightRef = heightRef;
		}
		Tooltip("Height (world Y) at which the density equals the value set above.\nUsually the scene's floor level.");

		ImGui.Spacing();
		ImGui.TextDisabled("Color:");

		var sunColor = new Vector3(_settings.VolumetricSunColorR, _settings.VolumetricSunColorG,
			_settings.VolumetricSunColorB);
		ImGui.SetNextItemWidth(180 * _scale);
		if (ImGui.ColorEdit3("Shaft color", ref sunColor))
		{
			_settings.VolumetricSunColorR = sunColor.X;
			_settings.VolumetricSunColorG = sunColor.Y;
			_settings.VolumetricSunColorB = sunColor.Z;
			_changed = true;
		}
		Tooltip("Colour of the shafts themselves. Usually taken from the sun - warm at sunset.");

		var ambientColor = new Vector3(_settings.VolumetricAmbientColorR,
			_settings.VolumetricAmbientColorG, _settings.VolumetricAmbientColorB);
		ImGui.SetNextItemWidth(180 * _scale);
		if (ImGui.ColorEdit3("Shadow color", ref ambientColor))
		{
			_settings.VolumetricAmbientColorR = ambientColor.X;
			_settings.VolumetricAmbientColorG = ambientColor.Y;
			_settings.VolumetricAmbientColorB = ambientColor.Z;
			_changed = true;
		}
		Tooltip("Colour of the medium WHERE THE SUN DOES NOT REACH - sky light, unaffected by shadow.\nUsually cooler than the shaft colour.");

		var ambientIntensity = _settings.VolumetricAmbientIntensity;
		if (Slider("Shadow intensity", ref ambientIntensity, 0f, 3f, "%.2f"))
		{
			_settings.VolumetricAmbientIntensity = ambientIntensity;
		}
		Tooltip("Without it the medium is pitch black in shadow\nand the shafts read as cut out with scissors instead of light in haze.");

		var ambientFloor = _settings.VolumetricAmbientShadowFloor;
		if (Slider("Sky in shadow", ref ambientFloor, 0f, 1f, "%.2f"))
		{
			_settings.VolumetricAmbientShadowFloor = ambientFloor;
		}
		Tooltip("How much dimmer the glow is where the sun does not reach.\n" +
			"The MAIN knob against milkiness: at 1 a covered interior glows as much as a\n" +
			"sunlit courtyard and the whole frame loses contrast and saturation.\n" +
			"0.1..0.2 keeps the glow near openings only and the interior stays dense.");
	}

	private void DrawExposureSection()
	{
		// Outside the auto-exposure branch: the curve applies in both LDR and HDR modes.
		var curveLabels = new[] { "PBR Neutral", "ACES (filmic)", "AgX (filmic)" };
		var curve = Math.Clamp(_settings.ToneCurve, 0, curveLabels.Length - 1);
		if (curve != _settings.ToneCurve)
		{
			// Write the clamp back: the shader branches on the stored index, not on the combo.
			_settings.ToneCurve = curve;
			_changed = true;
		}

		ImGui.SetNextItemWidth(180 * _scale);
		if (ImGui.Combo("Tonemap curve", ref curve, curveLabels, curveLabels.Length))
		{
			_settings.ToneCurve = curve;
			_changed = true;
		}
		Tooltip("PBR Neutral - the glTF reference: identity below ~0.76, i.e. it DELIBERATELY adds\n" +
			"neither contrast nor shadow depth. Correct for judging materials, and exactly why\n" +
			"the frame reads flat with it.\n\n" +
			"ACES - the classic filmic curve: mid-tone contrast, a deep toe, rolled-off highlights.\n" +
			"It shifts the hue of saturated bright colours (orange toward yellow).\n\n" +
			"AgX - the same filmic contrast but WITHOUT the hue shift: overexposure goes to white\n" +
			"through desaturation rather than a hue change. Usually the best \"make it pretty\" choice.");

		ImGui.Spacing();

		var eyeAdaptation = _settings.PreviewEyeAdaptation;
		if (ImGui.Checkbox("Auto exposure (eye adaptation)", ref eyeAdaptation))
		{
			_settings.PreviewEyeAdaptation = eyeAdaptation;
			_changed = true;
		}
		Tooltip("Measures the mean brightness of the finished frame and smooths it over time: exposure brings\nthe scene to Key value the way an eye adapts to light. Switches the preview to the HDR pipeline\n(linear frame, tonemap as a separate pass) - the pipeline is rebuilt in place, no model reload.");

		if (!eyeAdaptation)
		{
			return;
		}

		ImGui.Spacing();

		var key = _settings.EyeAdaptationKey;
		if (Slider("Key value", ref key, 0.02f, 1f, "%.3f"))
		{
			_settings.EyeAdaptationKey = key;
		}
		Tooltip("Mean brightness the frame is exposed to. 0.18 is photographic middle grey;\nhigher brightens the whole image.");

		var ev = _settings.EyeAdaptationExposureCompensation;
		if (Slider("Exposure compensation (EV)", ref ev, -4f, 4f, "%.2f"))
		{
			_settings.EyeAdaptationExposureCompensation = ev;
		}
		Tooltip("Artistic offset in stops on top of auto exposure: +1 EV is twice as bright.");

		var minLum = _settings.EyeAdaptationMinLuminance;
		if (Slider("Min luminance", ref minLum, 0.001f, 1f, "%.3f", ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.Logarithmic))
		{
			_settings.EyeAdaptationMinLuminance = minLum;
		}
		Tooltip("Lower bound on the MEASURED luminance: without it a nearly black frame\n(the camera pressed against a wall) is stretched into noise.");

		var maxLum = _settings.EyeAdaptationMaxLuminance;
		if (Slider("Max luminance", ref maxLum, 0.1f, 64f, "%.2f", ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.Logarithmic))
		{
			_settings.EyeAdaptationMaxLuminance = maxLum;
		}
		Tooltip("Upper bound on the measured luminance - keeps the scene from crushing to black\nwhen the sun enters the frame.");

		var speedUp = _settings.EyeAdaptationSpeedUp;
		if (Slider("Adapt speed (to light)", ref speedUp, 0.1f, 10f, "%.2f"))
		{
			_settings.EyeAdaptationSpeedUp = speedUp;
		}
		Tooltip("Adaptation speed toward a BRIGHTER frame, per second (\"squinting\").");

		var speedDown = _settings.EyeAdaptationSpeedDown;
		if (Slider("Adapt speed (to dark)", ref speedDown, 0.1f, 10f, "%.2f"))
		{
			_settings.EyeAdaptationSpeedDown = speedDown;
		}
		Tooltip("Adaptation speed toward a DARKER frame, per second.\nUsually slower than the way up - that is how the eye behaves.");
	}

}
