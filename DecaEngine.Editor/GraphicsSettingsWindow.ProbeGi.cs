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

/// <summary>Probe GI and sky controls of the graphics settings window.</summary>
public partial class GraphicsSettingsWindow
{
	private void DrawProbeGiSection()
	{
		ImGui.Spacing();

		// Before the early return on !enabled: the sky draws independently of probe GI.
		var sky = _settings.PreviewSkyBackground;
		if (ImGui.Checkbox("Sky background", ref sky))
		{
			_settings.PreviewSkyBackground = sky;
			_changed = true;
		}
		Tooltip("Draw the environment as the background instead of a flat clear colour.\nToggles on the live pipeline.");

		ImGui.Spacing();

		var enabled = _settings.PreviewProbeGi;
		if (ImGui.Checkbox("Probe GI (DDGI-lite)", ref enabled))
		{
			_settings.PreviewProbeGi = enabled;
			_changed = true;
		}
		Tooltip("CPU bake of an irradiance probe grid (SH L1) plus sky visibility against the model geometry:\nsky and light bounces instead of constant ambient. Requires shadows to be enabled.");

		ImGui.SameLine();
		ImGui.TextDisabled($"[preview: {_viewport.ProbeGiStatus}]");
		if (_sceneViewport != null)
		{
			ImGui.SameLine();
			ImGui.TextDisabled($"[scene: {_sceneViewport.ProbeGiStatus}]");
		}

		if (!enabled)
		{
			return;
		}

		var realtime = _settings.ProbeGiRealtime;
		if (ImGui.Checkbox("Real-time (no convergence)", ref realtime))
		{
			_settings.ProbeGiRealtime = realtime;
			_changed = true;
		}
		Tooltip("Baked versus dynamic. The normal mode accumulates the field with a running average\n" +
			"and stops once it converges: the longer it runs, the less noise, but also the weaker\n" +
			"the reaction to changes - exactly what a static scene needs.\n" +
			"In real-time mode rounds never stop and the average becomes exponential with a fixed\n" +
			"alpha: the field tracks light indefinitely at the cost of residual noise.\n" +
			"Toggles on the live session, no rebake.");

		if (realtime)
		{
			ImGui.Indent();
			var realtimeRays = _settings.ProbeGiRealtimeRays;
			if (SliderInt("Rays per round", ref realtimeRays, 8, 1024))
			{
				_settings.ProbeGiRealtimeRays = realtimeRays;
			}
			Tooltip("Fixes whole-scene BRIGHTNESS BREATHING. All probes in a round share the same ray\n" +
				"fan, so the estimation error is correlated across the grid and shows up not as\n" +
				"per-probe noise but as a global pulsation; more rays damp it (1/sqrt(N)).\n" +
				"Measured on Sponza (swing of mean brightness per round, alpha 0.15):\n" +
				"  16 rays - 5.1% (clearly visible), 32 - 1.5%, 64 - 1.1%, 128 - 0.5%.\n" +
				"Round cost is linear in ray count. Live, no rebake needed.");

			var blend = _settings.ProbeGiRealtimeBlend;
			if (Slider("Round weight", ref blend, 0.01f, 0.5f, "%.3f"))
			{
				_settings.ProbeGiRealtimeBlend = blend;
				_changed = true;
			}
			Tooltip("Fixes flicker of INDIVIDUAL probes - use this knob, not the ray count.\n" +
				"Alpha of the exponential average: a disturbance decays as (1-alpha)^n while probe\n" +
				"jitter goes as sqrt(alpha/(2-alpha)). The editor issues at most one round per frame,\n" +
				"so 0.04 at 60 fps settles in about 1.2 s.\n" +
				"Measured on Sponza at 64 rays (p99 and max probe change per round):\n" +
				"  0.15 - p99 6.3%, max 79%   0.08 - p99 3.4%, max 48%\n" +
				"  0.04 - p99 1.8%, max 24%   0.02 - p99 1.0%, max 12% (2.5 s response)\n" +
				"Lower is calmer but slower to follow a light change. Live.");

			var maxStep = _settings.ProbeGiRealtimeMaxStep;
			if (Slider("Step limit", ref maxStep, 0f, 0.2f, "%.3f"))
			{
				_settings.ProbeGiRealtimeMaxStep = maxStep;
				_changed = true;
			}
			Tooltip("The main anti-disco knob: how much a probe may change in one round.\n" +
				"Round weight is a filter applied EQUALLY to every probe, so it has to be tuned\n" +
				"for the worst one: calm probes get needless sluggishness, wild ones still get too\n" +
				"little. The step limit leaves calm probes alone and only kicks in where the estimate\n" +
				"jumps - a probe stops flashing and starts creeping. Insufficient rays then degrade\n" +
				"into LATENCY rather than into flashes.\n" +
				"No brightness is lost: it clamps the derivative, not the value.\n" +
				"Measured on Sponza at 8 rays, weight 0.5, grid 64, sky 12\n" +
				"(share of probes jumping more than 10% in a round):\n" +
				"  off - 61.2%   0.10 - 22.0%   0.03 - 0.6%   0.01 - 0.1%\n" +
				"Mean field brightness stays at 5.61 / 5.56 / 5.60 / 5.70 - no drift.\n" +
				"0 = off. Lower is calmer and slower to respond. Live.");

			var relocation = _settings.ProbeGiRealtimeRelocation;
			if (Slider("Probe relocation", ref relocation, 0f, 0.45f, "%.2f"))
			{
				_settings.ProbeGiRealtimeRelocation = relocation;
				_changed = true;
			}
			Tooltip("Treats the CAUSE of dense-grid flicker rather than the symptom. The finer the cell,\n" +
				"the more probes end up INSIDE walls and columns - such a probe both flickers (its\n" +
				"rays bounce between back faces and the sky beyond an edge) and leaks light through\n" +
				"the wall. Here such a probe is pushed outward every round through the nearest back\n" +
				"face - its own rays show where the exit is.\n" +
				"The value is the offset limit as a fraction of the grid step. Above 0.45 is not\n" +
				"allowed: the probe would leave its cell and trilinear interpolation would lie more\n" +
				"than the fix gains. 0 = off. Live.");

			var gamma = _settings.ProbeGiRealtimeGamma;
			if (Slider("Accumulation gamma", ref gamma, 1f, 8f, "%.1f"))
			{
				_settings.ProbeGiRealtimeGamma = gamma;
				_changed = true;
			}
			Tooltip("Perceptual accumulation (Majercik 2021, §4.2, adapted to SH): probe brightness\n" +
				"accumulates along a perceptual curve rather than linearly - a rare firefly is\n" +
				"suppressed by roughly (round weight)^(gamma-1), while a light→shadow transition\n" +
				"speeds up and reads as an even darkening with no endless tail.\n" +
				"Measured on Sponza (grid 64, sky 12, 64 rays, weight 0.04): worst probe jump per\n" +
				"round 84% - 38% (gamma 3) - 29% (gamma 5); combined with a 0.03 step limit:\n" +
				"7% - 3%. Mean field brightness loses about 0.1%.\n" +
				"1 = linear (off). The paper's value is 5. Live.");

			var variability = _settings.ProbeGiVariabilityThreshold;
			if (Slider("Convergence threshold", ref variability, 0f, 0.3f, "%.3f"))
			{
				_settings.ProbeGiVariabilityThreshold = variability;
				_changed = true;
			}
			Tooltip("Probe Variability from RTXGI-DDGI: a probe's variability is the coefficient of\n" +
				"variation of its brightness (spread divided by mean), averaged over the volume.\n" +
				"It is dimensionless, so a dark interior and a sunlit courtyard are comparable.\n" +
				"Once it drops below the threshold the volume has converged and rounds stop being\n" +
				"issued ENTIRELY until light or geometry moves; one full pass still runs every 32\n" +
				"rounds as insurance against an unnoticed change.\n" +
				"This is the next step after probe sleeping: sleeping saves three quarters of the\n" +
				"rays, here there is no dispatch at all.\n" +
				"Measured on Sponza (128 rays): steady-state variability 0.058; at a threshold of\n" +
				"0.08, 94% of rounds are skipped and the mean field brightness is unchanged.\n" +
				"The value depends on the scene and the ray count (estimation noise goes as\n" +
				"1/sqrt(N)): set it below the steady state and the stop never engages, set it too\n" +
				"high and the volume freezes before it converges. 0 = off. Live.");
			ImGui.Unindent();
		}


		// No CPU/GPU toggle: rounds run on compute only, the CPU round is a CLI reference.
		{
			var hardwareSupported = _viewport.RayTracingSupported;
			if (!hardwareSupported)
			{
				ImGui.BeginDisabled();
			}

			var hardware = _settings.ProbeGiHardwareRayTracing;
			if (ImGui.Checkbox("Hardware ray tracing", ref hardware))
			{
				_settings.ProbeGiHardwareRayTracing = hardware;
				_changed = true;
			}

			if (!hardwareSupported)
			{
				ImGui.EndDisabled();
				ImGui.SameLine();
				ImGui.TextDisabled("(device does not support it)");
			}

			Tooltip("Trace rays with RayQuery against hardware acceleration structures (BLAS/TLAS)\n" +
				"instead of walking the engine's own BVH in software. It speeds up the TRAVERSAL\n" +
				"itself, i.e. the dispatch body; per-dispatch overhead is untouched.\n" +
				"Requires inline ray tracing support (DXR 1.1 / VK_KHR_ray_query).\n" +
				"Unavailable or failed to build - falls back silently to the software path.");
		}

		ImGui.Spacing();
		ImGui.TextDisabled("Live (no rebake):");

		// Logarithmic: on a linear 0.25-128 scale the useful 1-4 range is a few pixels wide.
		var boost = _settings.ProbeGiAmbientBoost;
		if (Slider("Ambient boost", ref boost, 0.25f, 128f, "%.2f",
			ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.Logarithmic))
		{
			_settings.ProbeGiAmbientBoost = boost;
		}
		Tooltip("Multiplier on the baked probe irradiance - ambient exposure.\nUseful range 1-4; values in the tens blow the scene out to white (clamped to 128 on push).");

		var shadowFloor = _settings.ProbeGiShadowFloor;
		if (Slider("Sun bounce in shadow", ref shadowFloor, 0f, 1f, "%.2f"))
		{
			_settings.ProbeGiShadowFloor = shadowFloor;
		}
		Tooltip("How much of the SUN part of ambient survives inside the key light's screen shadow\n(0 - harder contact shadows, 1 - softer). It acts where the probe field is sun-driven -\nsee the red channel of Probe debug view; in sky-dominated scenes (a shaded courtyard)\nthe effect is negligible - use Sky ambient in shadow there.");

		var skyShadowFloor = _settings.ProbeGiSkyShadowFloor;
		if (Slider("Sky ambient in shadow", ref skyShadowFloor, 0.05f, 1f, "%.2f"))
		{
			_settings.ProbeGiSkyShadowFloor = skyShadowFloor;
		}
		Tooltip("How much of the SKY part of ambient survives inside the key light's screen shadow.\n1 (default) is physically correct: a shaded courtyard is filled by the sky (Intel Sponza).\nLower darkens shadows overall, for a more contrasty mood.");

		var specFloor = _settings.ProbeGiSpecularFloor;
		if (Slider("Env specular occlusion", ref specFloor, 0f, 1f, "%.2f"))
		{
			_settings.ProbeGiSpecularFloor = specFloor;
		}
		Tooltip("Floor for damping environment reflections by the baked sky visibility\n(0 - reflections vanish completely indoors).");

		var bias = _settings.ProbeGiNormalBias;
		if (Slider("Normal bias", ref bias, 0f, 1f, "%.2f"))
		{
			_settings.ProbeGiNormalBias = bias;
		}
		Tooltip("Offset of the sample point along the normal, in grid cells -\nguards against light/dark leaking through thin walls.");

		var viewBias = _settings.ProbeGiViewBias;
		if (Slider("View bias", ref viewBias, 0f, 1f, "%.2f"))
		{
			_settings.ProbeGiViewBias = viewBias;
		}
		Tooltip("Which way the sample point is offset: 1 - toward the camera, 0 - along the normal.\n" +
			"1 (default): no faceting, but as the camera moves probes appear to slide and change\n" +
			"brightness - lighting becomes view dependent.\n" +
			"0: fully view independent, but triangle facets show through the ambient on hard edges.\n" +
			"The DDGI paper uses 0.8.");

		ImGui.Spacing();
		ImGui.TextDisabled("Bake (changes rebake the probes):");

		var skyIntensity = _settings.ProbeGiSkyIntensity;
		if (Slider("Sky intensity", ref skyIntensity, 0.25f, 12f, "%.2f"))
		{
			_settings.ProbeGiSkyIntensity = skyIntensity;
		}
		Tooltip("Sky brightness during the bake - the sky part of ambient (shaded courtyards, niches, sky visibility).");

		// Range must equal the bake-side clamp in ProbeGi.cs: Clamp(..., 16, 512).
		var rays = _settings.ProbeGiRaysPerProbe;
		if (SliderInt("Rays per probe", ref rays, 16, 512))
		{
			_settings.ProbeGiRaysPerProbe = rays;
		}
		Tooltip("Rays per probe: more gives a smoother field and more accurate sky visibility, with a linearly longer bake.");

		var visRes = _settings.ProbeGiVisRes;
		if (SliderInt("Visibility res", ref visRes, ProbeGiBakeResult.MinVisRes, ProbeGiBakeResult.MaxVisRes))
		{
			_settings.ProbeGiVisRes = visRes;
		}
		Tooltip("Side of the per-probe octahedral DEPTH map (DDGI visibility): the Chebyshev test uses it\n" +
			"to decide whether a wall stands between the probe and the shaded point - the main guard\n" +
			"against light leaking through thin geometry (curtains, column edges).\n" +
			"8 = ~25° per texel, 16 = ~12° (the value from Majercik 2021).\n" +
			"NOTE: the atlas grows quadratically while rays per texel drop by the same factor - at a\n" +
			"fixed Rays per probe quality gets worse (measured: at 16, the spurious lighting of the\n" +
			"Sponza interior rose from 14.1 to 15.9). Raise it together with Rays per probe.\n" +
			"Rebake: the atlas layout is fixed when the session is created.");

		// Upper bound must equal ProbeGi.cs: Clamp(options.Bounces, 1, 6).
		var bounces = _settings.ProbeGiBounces;
		if (SliderInt("Bounces", ref bounces, 1, 6))
		{
			_settings.ProbeGiBounces = bounces;
		}
		Tooltip("Gather iterations: 1 - sky plus the direct sun bounce only,\neach further one adds a re-bounce (deep courtyards need 2-3).");

		var bounceSat = _settings.ProbeGiBounceSaturation;
		if (Slider("Bounce saturation", ref bounceSat, 0f, 1f, "%.2f"))
		{
			_settings.ProbeGiBounceSaturation = bounceSat;
		}
		Tooltip("Saturation of the coloured bounce: 0 - grey bounce, 1 - full albedo colour.\nBounce brightness is unchanged (the sun scatters just as much) -\nthe lower the value, the less bright coloured fabrics glow like lamps.");

		// Low end = ProbeGi.cs clamp of 4; high end 44 because the bake multiplies by 1.45 and
		// clamps the product to 64.
		var density = _settings.ProbeGiGridDensity;
		if (Slider("Grid density", ref density, 4f, 44f, "%.0f"))
		{
			_settings.ProbeGiGridDensity = density;
		}
		Tooltip("Grid cells across the scene's largest dimension: denser means fewer leaks and better\nlight localisation at a costlier bake (probe count grows cubically).");

		// Top entry must stay equal to ProbeGiBaker.MaxProbeBudget.
		int[] probeCaps = [2048, 4096, 8192, 16384, 32768, 131072, 524288, ProbeGiBaker.MaxProbeBudget];
		var capLabels = new[] { "2k", "4k", "8k", "16k", "32k", "128k", "512k", "2M" };
		var capIndex = Array.IndexOf(probeCaps, _settings.ProbeGiMaxProbes);
		if (capIndex < 0)
		{
			capIndex = Array.IndexOf(probeCaps, 8192);
			_settings.ProbeGiMaxProbes = probeCaps[capIndex];
			_changed = true;
		}

		ImGui.SetNextItemWidth(120 * _scale);
		if (ImGui.Combo("Max probes", ref capIndex, capLabels, capLabels.Length))
		{
			_settings.ProbeGiMaxProbes = probeCaps[capIndex];
			_changed = true;
		}
		Tooltip("Probe count cap: the cell grows until the grid fits.\nBake time grows linearly with probe count (32k ~ 1.3 s on Sponza); the grid is further\nlimited by the per-axis cap (128) and the visibility atlas size limit.");

		// The x3 budget is inherited from the three former cascades, now spent on one volume.
		var effectiveProbes = Math.Min(_settings.ProbeGiMaxProbes * 3, ProbeGiBaker.MaxProbeBudget);
		ImGui.SameLine();
		ImGui.TextDisabled($"= {effectiveProbes:N0} effective (x3 from the removed cascades)");

		ImGui.Spacing();
		if (ImGui.Button("Rebake now", new Vector2(120 * _scale, 0)))
		{
			_viewport.RequestProbeRebake();
			_sceneViewport?.RequestProbeRebake();
		}
		Tooltip("Force a rebake of both the preview AND the scene (e.g. after editing the HDR file on disk).");

		ImGui.SameLine();
		var debugView = _settings.ProbeGiDebugView;
		if (ImGui.Checkbox("Probe debug view", ref debugView))
		{
			_settings.ProbeGiDebugView = debugView;
			_changed = true;
		}
		Tooltip("Debug view of the probe field: R = sun share (where Sun bounce in shadow acts),\nG = sky visibility (where Env specular occlusion acts), B = the key light's screen shadow.");

		var debugProbes = _settings.ProbeGiDebugProbes;
		if (ImGui.Checkbox("Probe placement", ref debugProbes))
		{
			_settings.ProbeGiDebugProbes = debugProbes;
			_changed = true;
		}
		Tooltip("Where each probe sits and what relocation did to it:\n" +
			"  green spot - probe on its grid node, all fine;\n" +
			"  yellow to red - pushed out of geometry (redder = further);\n" +
			"  blue - marked invalid (walled in, excluded from interpolation);\n" +
			"  background - validity of the surrounding field.\n" +
			"Only probes with a surface nearby are marked - exactly the ones that make a dense\n" +
			"grid flicker. Probes in open air are invisible: this view has no geometry pass of\n" +
			"its own, the marks are drawn onto surfaces.\n" +
			"Takes precedence over Probe debug view when both are enabled.");

		var showProbes = _settings.ProbeGiShowProbes;
		if (ImGui.Checkbox("Probe spheres", ref showProbes))
		{
			_settings.ProbeGiShowProbes = showProbes;
			_changed = true;
		}
		Tooltip("A sphere per probe AT ITS ACTUAL position (after relocation) - unlike Probe placement\n" +
			"this is real depth-tested geometry, so probes in open air are visible too:\n" +
			"  colour - the light accumulated by the probe (SH L0): dark in shadow, bright near light;\n" +
			"  red - the probe considers itself inside a wall (rare after relocation);\n" +
			"  cyan rim - the probe was moved by relocation (shows which ones were pulled out).\n" +
			"Drawn over the scene using its depth. Live, no rebake needed.");

		ImGui.Separator();

		var bvhDebug = _settings.ProbeGiBvhDebug;
		if (ImGui.Checkbox("BVH boxes", ref bvhDebug))
		{
			_settings.ProbeGiBvhDebug = bvhDebug;
			_changed = true;
		}
		Tooltip("Wireframe boxes of the BVH nodes - the tree the probe rays traverse.\n" +
			"Shows what the scene geometry actually collapsed into: a bloated node (one box over\n" +
			"half the scene) means rays wading through extra triangles.\n" +
			"The tree itself is cached next to the model as a .bhv.bin file: building it on a heavy\n" +
			"asset takes tens of seconds and happens once per model file version.\n" +
			"Tree statistics are printed to the console when enabled.");

		if (bvhDebug)
		{
			var bvhLeaves = _settings.ProbeGiBvhDebugLeaves;
			if (ImGui.Checkbox("BVH leaves only", ref bvhLeaves))
			{
				_settings.ProbeGiBvhDebugLeaves = bvhLeaves;
				_changed = true;
			}
			Tooltip("Leaf nodes only - the real granularity of the split\n" +
				"(<= 4 triangles per leaf). The list is cut off at 20000 boxes.");

			if (!bvhLeaves)
			{
				var bvhDepth = _settings.ProbeGiBvhDebugDepth;
				if (SliderInt("BVH depth", ref bvhDepth, 0, 16))
				{
					_settings.ProbeGiBvhDebugDepth = bvhDepth;
				}
				Tooltip("How deep to show nodes (0 = just the root box of the whole scene).\n" +
					"Each level has twice as many boxes as the previous one.");
			}
		}
	}

}
