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

/// <summary>Screen-space effects sections (AO, SSR, SSGI) of <see cref="GraphicsSettingsWindow"/>; fields and apply logic live in the main file.</summary>
public partial class GraphicsSettingsWindow
{
	private void DrawAoSection()
	{
		ImGui.Spacing();

		var ssao = _settings.PreviewSsao;
		if (ImGui.Checkbox("Ambient occlusion (screen-space)", ref ssao))
		{
			_settings.PreviewSsao = ssao;
			_changed = true;
		}
		Tooltip("Darkening in creases and cavities, derived from the depth buffer. Applied live.");

		if (ssao)
		{
			var aoModeLabels = new[] { "SSAO", "GTAO" };
			var aoModeIndex = _settings.PreviewAoMode == AmbientOcclusionMode.Gtao ? 1 : 0;
			ImGui.SetNextItemWidth(120 * _scale);
			if (ImGui.Combo("AO technique", ref aoModeIndex, aoModeLabels, aoModeLabels.Length))
			{
				_settings.PreviewAoMode = aoModeIndex == 1 ? AmbientOcclusionMode.Gtao : AmbientOcclusionMode.Ssao;
				_changed = true;
			}
			Tooltip("SSAO - classic spiral-sample occlusion.\nGTAO - horizon search plus a visibility integral: cleaner on flat surfaces, slightly costlier.");

			// Sliders below push the AoConstants cbuffer per frame, so no _changed = true.
			var aoStrength = _settings.AoStrength;
			if (Slider("AO strength", ref aoStrength, 0.25f, 4f, "%.2f"))
			{
				_settings.AoStrength = aoStrength;
			}
			Tooltip("Occlusion contrast (visibility exponent for GTAO, intensity multiplier for SSAO).");

			var aoFloor = _settings.AoFloor;
			if (Slider("AO floor", ref aoFloor, 0f, 0.5f, "%.2f"))
			{
				_settings.AoFloor = aoFloor;
			}
			Tooltip("Lower bound on visibility: screen-space AO is an approximation and should not kill light entirely.\n0 = allow full occlusion.");

			var aoRadiusWorld = _settings.AoRadiusWorld;
			if (Slider("AO radius (world)", ref aoRadiusWorld, 0f, 5f, "%.2f"))
			{
				_settings.AoRadiusWorld = aoRadiusWorld;
			}
			Tooltip("Search radius in WORLD units. 0 - derive it from the model bounds with the slider below.\nOn a full level (Sponza: bounds radius ~50) a fraction of the bounds means metres, and thin\ngeometry - curtains, flags, foliage - casts a wide blob instead of a contact shadow.\nUse 0.2-0.5 for such scenes.");

			var aoRadius = _settings.AoRadiusFraction;
			if (Slider("AO radius (bounds)", ref aoRadius, 0.02f, 0.6f, "%.3f"))
			{
				_settings.AoRadiusFraction = aoRadius;
			}
			Tooltip("Search radius as a fraction of the model's bounding radius - for previewing a SINGLE object\n(scale invariant). Ignored when the slider above is set.\nLarger reaches further from creases (big cavities), smaller keeps only contact shading.");

			var aoDebug = _settings.AoDebugView;
			if (ImGui.Checkbox("AO debug view", ref aoDebug))
			{
				_settings.AoDebugView = aoDebug;
				_changed = true;
			}
			Tooltip("AO debug view: the composite outputs raw visibility in greyscale instead of shading the frame\n(white - open, black - occluded). It shows exactly what the AO pass uses to damp ambient,\nso strength/floor/radius and SSAO vs GTAO can be compared directly.\nTransparent geometry is drawn AFTER the composite and stays normal on top of the debug view.");
		}
	}

	private void DrawSsrSection()
	{
		ImGui.Spacing();

		var ssr = _settings.PreviewSsr;
		if (ImGui.Checkbox("SSR (stochastic reflections)", ref ssr))
		{
			_settings.PreviewSsr = ssr;
			_changed = true;
		}
		Tooltip("Screen-space reflections: one stochastic GGX ray per pixel marched through the depth buffer,\ntemporally accumulated along motion vectors (enabled automatically).\nThe result REPLACES the prefiltered environment specular rather than adding on top.\nApplied live.");

		if (ssr)
		{
			var rt = _settings.SsrRayTraced;
			var rtAvailable = _viewport?.RayTracingSupported ?? false;
			if (!rtAvailable)
			{
				ImGui.BeginDisabled();
			}
			if (ImGui.Checkbox("Ray-traced fallback", ref rt))
			{
				_settings.SsrRayTraced = rt;
				_changed = true;
			}
			if (!rtAvailable)
			{
				ImGui.EndDisabled();
			}
			Tooltip("Rays that miss the screen are resolved with an inline RayQuery against the scene TLAS\n(the same geometry as hardware probe GI - it must be enabled, otherwise the fallback\nsilently stays off). Hits visible on screen reuse the shaded pixel; off-screen hits get\nsimplified shading (sun + probe field + punctual lights).\nRequires D3D12 with inline ray tracing.");

			if (rt && rtAvailable)
			{
				var hitTex = _settings.SsrHitTextures;
				string[] hitTexModes = ["Off (per-triangle albedo)", "Atlas 128² (cheap)", "Bindless (full textures)"];
				ImGui.SetNextItemWidth(220 * _scale);
				if (ImGui.Combo("RT hit textures", ref hitTex, hitTexModes, hitTexModes.Length))
				{
					_settings.SsrHitTextures = hitTex;
					_changed = true;
				}
				Tooltip("How to shade an off-screen RT hit:\nOff - one averaged colour per triangle (previous behaviour);\nAtlas - 128² downsampled tiles of every base color texture in a single Texture2DArray\n(for streamed/cooked models the tile collapses to the material's average colour);\nBindless - an array of full-size textures, real UV detail in reflections\n(costlier in descriptors; falls back to the atlas silently when the device lacks support).\nSwitching rebuilds the SSR materials.");

				var traceMode = _settings.SsrTraceMode;
				string[] traceModes = ["Screen march → RT", "RT only (no march)"];
				ImGui.SetNextItemWidth(220 * _scale);
				if (ImGui.Combo("Trace mode", ref traceMode, traceModes, traceModes.Length))
				{
					_settings.SsrTraceMode = traceMode;
					_changed = true;
				}
				Tooltip("How the reflection point is found:\nScreen march → RT - 48 steps through the depth buffer first, RT picks up the misses (default);\nRT only - the march is skipped, the ray goes straight to the TLAS. Screen DATA is not lost:\nradiance at the hit point is still reprojected from the screen.\nThis removes marching artifacts (false hits behind thin geometry, SSR thickness errors,\nfade-out at frame edges); the cost is a BVH traversal instead of depth samples. Live.");

				var rtBounces = _settings.SsrRtBounces;
				if (SliderInt("RT bounces", ref rtBounces, 1, 4))
				{
					_settings.SsrRtBounces = rtBounces;
				}
				Tooltip("TOTAL RT ray bounces: 1 - primary ray only (a mirror inside a mirror is black),\n2 - plus one specular bounce off metallic hits (default),\n3-4 - longer chains of mutual chrome reflections. Cost is one trace per extra\nbounce, only on mirror-like pixels with a dark hit. Live.");
			}

			// Status reflects actually-built resources: RT can silently downgrade to screen-only.
			if (rt)
			{
				var sceneReason = _sceneViewport?.SsrRayTracedBlockReason;
				ImGui.TextColored(sceneReason == null
						? new Vector4(0.45f, 0.85f, 0.45f, 1f)
						: new Vector4(1f, 0.6f, 0.2f, 1f),
					sceneReason == null ? "Scene View: RT fallback active" : $"Scene View: {sceneReason}");

				var previewReason = _viewport?.SsrRayTracedBlockReason;
				ImGui.TextColored(previewReason == null
						? new Vector4(0.45f, 0.85f, 0.45f, 1f)
						: new Vector4(1f, 0.6f, 0.2f, 1f),
					previewReason == null ? "Preview: RT fallback active" : $"Preview: {previewReason}");
			}

			var intensity = _settings.SsrIntensity;
			if (Slider("SSR intensity", ref intensity, 0f, 4f, "%.2f"))
			{
				_settings.SsrIntensity = intensity;
			}
			Tooltip("Multiplier for the replacing reflection. 1 is energy-correct\n(as much traced light goes in as environment specular was taken out).");

			var maxRough = _settings.SsrMaxRoughness;
			if (Slider("SSR max roughness", ref maxRough, 0.05f, 1f, "%.2f"))
			{
				_settings.SsrMaxRoughness = maxRough;
			}
			Tooltip("Roughness ceiling: above it reflections fade out smoothly (the prefiltered environment remains).\nThere is one ray per pixel, and on rough surfaces the leftover noise costs more\nthan the missing specular.");

			var thickness = _settings.SsrThickness;
			if (Slider("SSR thickness", ref thickness, 0.01f, 5f, "%.2f",
				ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.Logarithmic))
			{
				_settings.SsrThickness = thickness;
			}
			Tooltip("Assumed surface thickness during the intersection test, in world units.\nToo small and rays slip through thin geometry (holes in the reflection);\ntoo large and reflections smear onto foreground silhouettes.");

			var maxDist = _settings.SsrMaxDistance;
			if (Slider("SSR max distance", ref maxDist, 1f, 500f, "%.0f",
				ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.Logarithmic))
			{
				_settings.SsrMaxDistance = maxDist;
			}
			Tooltip("Reflection ray range, in world units.");

			var rays = _settings.SsrRaysPerPixel;
			if (SliderInt("SSR ray reuse", ref rays, 1, 4))
			{
				_settings.SsrRaysPerPixel = rays;
			}
			Tooltip("Resolve quality: how many neighbouring rays each pixel reuses (x2 taps).\nEach is weighted by BRDF/PDF (ratio estimator, as in Frostbite's Stochastic SSR):\nmirror sharpness and rough-lobe width follow from the physics,\nthis knob only changes residual noise and cost.");

			var history = _settings.SsrHistoryWeight;
			if (Slider("SSR history weight", ref history, 0f, 0.97f, "%.2f"))
			{
				_settings.SsrHistoryWeight = history;
			}
			Tooltip("History weight of the temporal accumulation: higher is smoother and more sluggish\n(ghosting during motion is limited by a neighbourhood clamp), 0 is the raw single-ray noise.");

			var debug = _settings.SsrDebugView;
			string[] debugModes = ["Off", "Reflection only", "Confidence", "G-buffer normals", "RT hit albedo", "RT bounce chain"];
			if (ImGui.Combo("SSR debug view", ref debug, debugModes, debugModes.Length))
			{
				_settings.SsrDebugView = debug;
				_changed = true;
			}
			Tooltip("Debug views: reflection only (exactly what is blended in), confidence\n(where rays hit and with what weight), G-buffer normals (the trace input).");
		}
	}

	private void DrawSsgiSection()
	{
		ImGui.Spacing();

		var ssgi = _settings.PreviewSsgi;
		if (ImGui.Checkbox("SSGI (screen-space bounce)", ref ssgi))
		{
			_settings.PreviewSsgi = ssgi;
			_changed = true;
		}
		Tooltip("Screen-space light bounce from the frame (color bleeding). Complements probe GI\nwith close-range colour transfer where the probe grid is too sparse.\nApplied live.");

		if (ssgi)
		{
			var giIntensity = _settings.SsgiIntensity;
			if (Slider("GI intensity", ref giIntensity, 0f, 4f, "%.2f"))
			{
				_settings.SsgiIntensity = giIntensity;
			}
			Tooltip("Multiplier for the gathered bounce. 0 - the pass still runs but contributes nothing.");

			var giSamples = _settings.SsgiSamples;
			if (SliderInt("GI samples", ref giSamples, 4, SsgiPassResources.MaxSampleCount))
			{
				_settings.SsgiSamples = giSamples;
			}
			Tooltip("Taps per pixel - the main noise/cost lever. 8 and below gives the familiar colour snow;\n16-24 with the blur below already reads as a soft bounce.");

			var giMaxLum = _settings.SsgiMaxLuminance;
			if (Slider("GI firefly clamp", ref giMaxLum, 0f, 32f, "%.2f",
				ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.Logarithmic))
			{
				_settings.SsgiMaxLuminance = giMaxLum;
			}
			Tooltip("Luminance ceiling for a SINGLE tap. In an HDR frame a sunlit spot next to shadow can be\ntens of units bright, and one such tap in the sample set becomes a white/coloured firefly.\nLower is cleaner and dimmer in high-contrast areas; 0 removes the limit.");

			var giSaturation = _settings.SsgiSaturation;
			if (Slider("GI saturation", ref giSaturation, 0f, 1f, "%.2f"))
			{
				_settings.SsgiSaturation = giSaturation;
			}
			Tooltip("Bounce saturation: 1 - the emitter's colour as is, 0 - a grey bounce.\nThe counterpart of probe GI's Bounce saturation: coloured fabrics otherwise glow like neon.");

			var giBlur = _settings.SsgiBlurRadius;
			if (SliderInt("GI blur radius", ref giBlur, 0, SsgiPassResources.MaxBlurRadius))
			{
				_settings.SsgiBlurRadius = giBlur;
			}
			Tooltip("Radius of the depth-bilateral blur applied to the bounce in the composite, in pixels.\nWider is smoother and costlier; silhouettes stay crisp - weights drop across depth discontinuities.");

			var giRadiusWorld = _settings.SsgiRadiusWorld;
			if (Slider("GI radius (world)", ref giRadiusWorld, 0f, 20f, "%.2f"))
			{
				_settings.SsgiRadiusWorld = giRadiusWorld;
			}
			Tooltip("Gather radius in WORLD units. 0 - derive it from the model bounds with the slider below.\nOn a full level (Sponza) a fraction of the bounds means metres: the bounce is gathered from\nhalf the screen and degenerates into a coloured haze - use 1-3.");

			var giRadiusFraction = _settings.SsgiRadiusFraction;
			if (Slider("GI radius (bounds)", ref giRadiusFraction, 0.02f, 2f, "%.3f"))
			{
				_settings.SsgiRadiusFraction = giRadiusFraction;
			}
			Tooltip("Gather radius as a fraction of the model's bounding radius - for previewing a SINGLE object\n(scale invariant). Ignored when the slider above is set.");

			var giDebug = _settings.SsgiDebugView;
			if (ImGui.Checkbox("GI debug view", ref giDebug))
			{
				_settings.SsgiDebugView = giDebug;
				_changed = true;
			}
			Tooltip("SSGI debug view: the composite outputs the bounce ALONE instead of the frame with it -\nshowing exactly what the pass contributes and how the sliders above affect it.");
		}
	}

}
