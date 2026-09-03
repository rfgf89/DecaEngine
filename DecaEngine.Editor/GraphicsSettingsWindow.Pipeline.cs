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

/// <summary>Motion-vector and render-graph sections of <see cref="GraphicsSettingsWindow"/>; state and apply logic live in the main file.</summary>
public partial class GraphicsSettingsWindow
{
	private void DrawMotionVectorSection()
	{
		ImGui.Spacing();

		// Label must differ from the section header: equal labels in one ImGui ID stack collide
		// and checkbox clicks fight the header.
		var motion = _settings.PreviewMotionVectors;
		if (ImGui.Checkbox("Motion vectors (upscaler input)", ref motion))
		{
			_settings.PreviewMotionVectors = motion;
			_changed = true;
		}
		Tooltip("Screen-space motion vectors into a separate RG16F buffer - input for upscalers (DLSS/FSR) and TAA.\n" +
			"Does not change the frame itself: nothing reads the buffer yet, the checkbox costs one fullscreen draw.\n" +
			"Applied live.");

		// Scale and jitter stay above the early-out: they must remain usable with vectors off.
		var renderScale = _settings.RenderScale;
		if (Slider("Render scale", ref renderScale, 0.25f, 1f, "%.2f"))
		{
			_settings.RenderScale = renderScale;
			_changed = true;
		}
		Tooltip("The scene and its post-processing are rendered at this fraction of the window resolution;\n" +
			"tonemapping lifts the frame to full size (bilinear for now - the upscaler goes here). 1 = off.\n" +
			"Applied live; at half resolution the scene costs about a quarter of full price.");

		var jitter = _settings.TemporalJitter;
		if (ImGui.Checkbox("Temporal jitter", ref jitter))
		{
			_settings.TemporalJitter = jitter;
			_changed = true;
		}
		Tooltip("Sub-pixel projection jitter (Halton 2/3, 16 phases) - the other half of a temporal\n" +
			"upscaler's input. Applied live.\n" +
			"WITHOUT a consumer (TAA/upscaler) the image shimmers - that is the raw input of a\n" +
			"technique that is not there yet, not a bug. The motion vector debug view MUST stay\n" +
			"flat grey: vectors are computed from unjittered matrices, the upscaler cancels the shake.");

		if (!motion)
		{
			return;
		}

		var taau = _settings.TemporalUpscale;
		if (ImGui.Checkbox("Temporal upscale", ref taau))
		{
			_settings.TemporalUpscale = taau;
			_changed = true;
		}
		Tooltip("Temporal upscaling: the scene at render resolution (see Render scale) plus jitter plus\n" +
			"motion vectors is accumulated into a full-size frame from history. Enables jitter itself.\n" +
			"At Render scale 1 it acts as plain TAA (antialiasing with no loss of resolution).");

		if (taau)
		{
			var backend = _settings.UpscalerBackend;
			ImGui.SetNextItemWidth(220);
			if (ImGui.Combo("Upscaler backend", ref backend, "TAAU (built-in)\0FSR (native)\0DLSS (native)\0"))
			{
				_settings.UpscalerBackend = backend;
				_changed = true;
			}
			Tooltip("TAAU - managed reference backend (engine shader). FSR - native ffx-api\n" +
				"(needs DecaFfxShim.dll + amd_fidelityfx_upscaler_dx12.dll). DLSS - NVIDIA NGX\n" +
				"(needs DecaFfxShim.dll + nvngx_dlss.dll and an RTX card). Both are D3D12 only.\n" +
				"Applied live; falls back silently to TAAU when unavailable.");

			var activeName = _viewport?.Environment?.ActiveUpscalerName;
			if (backend != 0 && activeName is null)
			{
				ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f),
					(backend == 2 ? "DLSS" : "FSR") + " is not active - TAAU is running.");
				Tooltip("DecaFfxShim.dll or the native DLL is missing next to the executable, the backend\n" +
					"is not D3D12, the hardware is wrong (DLSS is NVIDIA RTX only), or the motion\n" +
					"vector buffer was not created. Details are in the console ([fsr]/[dlss] ...).");
			}
			else if (activeName is not null)
			{
				ImGui.TextDisabled($"Active: {activeName}");
			}

			switch (backend)
			{
				case 0:
					var alpha = _settings.TaauBlendAlpha;
					if (Slider("TAAU: frame weight", ref alpha, 0.02f, 0.5f, "%.2f"))
					{
						_settings.TaauBlendAlpha = alpha;
					}
					Tooltip("Weight of the current frame in the history accumulator. Lower is more stable\n" +
						"and sharper on static shots but converges slower and ghosts more; higher is\n" +
						"more responsive to motion but damps jitter shimmer less. Classic TAA is 0.10.");
					break;

				case 1:
					var provider = _settings.FsrProvider;
					ImGui.SetNextItemWidth(220);
					if (ImGui.Combo("FSR: provider", ref provider, "Auto (newest working)\0FSR 2\0FSR 3.1\0"))
					{
						_settings.FsrProvider = provider;
						_changed = true;
					}
					Tooltip("Provider branch of the ffx runtime. Auto picks the newest WORKING generation\n" +
						"for the current hardware (FSR 4 on RDNA4, otherwise FSR 2). FSR 3.1 is kept for\n" +
						"visual comparison. Switching recreates the context - instant, history drops for a frame.");

					if (provider == 2)
					{
						ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f),
							"FSR 3.1 degrades the frame on the current SDK (known issue).");
						Tooltip("Provider 3.1.5 from SDK 2.3.0 washes the frame into a blurry mush in this\n" +
							"integration; every documented parameter has been tried and it works in AMD's\n" +
							"official sample - suspected read of a stale descriptor from the application\n" +
							"heap. Details are in the console log.");
					}

					var sharp = _settings.FsrSharpness;
					if (Slider("FSR: sharpness", ref sharp, 0f, 1f, "%.2f"))
					{
						_settings.FsrSharpness = sharp;
					}
					Tooltip("FSR's built-in sharpening (RCAS) applied after upscaling: 0 - off, 1 - maximum.\n" +
						"Applied live; sharpness is never free - on a noisy scene oversharpening also\n" +
						"amplifies the noise.");
					break;

				case 2:
					var quality = _settings.DlssQuality;
					ImGui.SetNextItemWidth(220);
					if (ImGui.Combo("DLSS: quality", ref quality, "Performance\0Balanced\0Quality\0DLAA\0"))
					{
						_settings.DlssQuality = quality;
						_changed = true;
					}
					Tooltip("Preset for DLSS's internal processing (model/weights). It does NOT control the\n" +
						"render resolution - that comes from Render scale above; DLAA makes sense at\n" +
						"Render scale 1 (pure antialiasing, no upscaling). Switching recreates the feature - instant.");
					break;
			}
		}

		var debug = _settings.MotionVectorDebugView;
		if (ImGui.Checkbox("Motion vector debug view", ref debug))
		{
			_settings.MotionVectorDebugView = debug;
			_changed = true;
		}
		Tooltip("Replaces the frame with a vector visualisation: R - X offset, G - Y offset, FLAT GREY - zero.\n" +
			"Key check: with a static camera the frame must be uniformly grey at any range setting.\n" +
			"Acid-yellow blobs mean a vector went out of range - raise the slider below.\n" +
			"Moving objects stay grey in stage 1 - they have no vectors yet, that is not a bug.");

		if (debug)
		{
			var range = _settings.MotionVectorDebugRange;
			if (Slider("Debug range (px)", ref range, 1f, 64f, "%.1f"))
			{
				_settings.MotionVectorDebugRange = range;
			}
			Tooltip("Offset in PIXELS at which the scale saturates. Smaller shows shimmer during slow\n" +
				"camera motion, larger keeps the view from clipping during fast turns.");
		}
	}

	/// <summary>Live per-pass toggles: disabling replays the graph without the pass (IRenderGraphPass.Enabled), no rebuild.</summary>
	private void DrawRenderGraphSection()
	{
		ImGui.Spacing();
		ImGui.TextDisabled("Debug list: disable ANY pass on the live graph and see what it contributes.\n" +
			"Toggle pipeline features above instead - that also skips preparing their resources.");
		ImGui.Spacing();

		var pipeline = _viewport.Environment?.Pipeline;
		if (pipeline == null)
		{
			ImGui.TextDisabled("Preview pipeline is not up yet.");
			return;
		}

		var names = pipeline.PassNames;
		if (names.Count == 0)
		{
			ImGui.TextDisabled("Graph is empty (no frame has been built yet).");
			return;
		}

		for (int i = 0; i < names.Count; i++)
		{
			var name = names[i];
			var enabled = pipeline.IsPassEnabled(name);

			// ID by index: pass names need not be unique, and equal ImGui IDs merge the checkboxes.
			if (ImGui.Checkbox($"{name}##pass{i}", ref enabled))
			{
				pipeline.SetPassEnabled(name, enabled);
				_sceneViewport?.Environment?.Pipeline?.SetPassEnabled(name, enabled);
			}
		}

		ImGui.Spacing();
		ImGui.TextDisabled("A disabled pass is dropped from the graph entirely, together with its resource\n" +
			"state transitions, so any pass can be disabled - including structural ones\n" +
			"(Forward, Tonemap): the frame is simply left undrawn, nothing breaks.\n" +
			"This checkbox OVERRIDES the settings above and survives a graph rebuild: a pass\n" +
			"disabled here will not come back when its feature is enabled - re-enable it here.");
	}

}
