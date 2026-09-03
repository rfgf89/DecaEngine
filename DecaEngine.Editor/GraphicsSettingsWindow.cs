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

/// <summary>Docked "Graphics" window: all preview-graphics knobs, applied live.</summary>
public partial class GraphicsSettingsWindow : ImGuiDockingWindow
{
	private readonly EditorSettings _settings;
	private readonly ModelPreviewViewport _viewport;
	private readonly PrefabSceneViewport _sceneViewport;

	private bool _changed;
	private bool _savePending;

	private bool _vsyncSynced;

	// Staged: these knobs recreate the environment and reload the model from disk.
	private bool _pendingAniso;
	private string _pendingHdr = "";
	private int _pendingMaxTextureSize;

	// Snapshot the buffer was filled from; detects the Settings modal editing the same fields.
	private (bool Aniso, string Hdr, int MaxTextureSize) _pendingSource;

	private const int ShadowDebugSize = 512;
	private int _shadowDebugSource;
	private bool _shadowDebugRaw;
	private string _shadowDebugInfo = "";
	private float[][] _shadowDebugSlices;
	private (float Min, float Max, float Coverage)[] _shadowDebugStats;
	private (float WorldSize, float WorldDepthRange)[] _shadowDebugWorld;
	private IGpuTexture[] _shadowDebugTextures;
	private ImTextureRef[] _shadowDebugTexRefs;

	public GraphicsSettingsWindow(string name, EditorSettings settings, ModelPreviewViewport viewport,
		PrefabSceneViewport sceneViewport, ImGuiRender imGuiRender) : base(name, imGuiRender)
	{
		_settings = settings;
		_viewport = viewport;
		_sceneViewport = sceneViewport;
	}

	protected override void OnRender(uint dockId)
	{
		_changed = false;
		SyncPendingFromSettings(force: false);

		if (ImGui.CollapsingHeader("Display", ImGuiTreeNodeFlags.DefaultOpen))
		{
			DrawDisplaySection();
		}

		if (ImGui.CollapsingHeader("Sun & Shadows", ImGuiTreeNodeFlags.DefaultOpen))
		{
			DrawLightSection();
		}

		if (ImGui.CollapsingHeader("Ambient occlusion", ImGuiTreeNodeFlags.DefaultOpen))
		{
			DrawAoSection();
		}

		if (ImGui.CollapsingHeader("SSGI", ImGuiTreeNodeFlags.DefaultOpen))
		{
			DrawSsgiSection();
		}

		if (ImGui.CollapsingHeader("Reflections (SSR)", ImGuiTreeNodeFlags.DefaultOpen))
		{
			DrawSsrSection();
		}

		if (ImGui.CollapsingHeader("Fog"))
		{
			DrawFogSection();
		}

		if (ImGui.CollapsingHeader("Volumetric light"))
		{
			DrawVolumetricSection();
		}

		if (ImGui.CollapsingHeader("Bloom"))
		{
			DrawBloomSection();
		}

		if (ImGui.CollapsingHeader("Color grading"))
		{
			DrawColorGradeSection();
		}

		if (ImGui.CollapsingHeader("Exposure"))
		{
			DrawExposureSection();
		}

		if (ImGui.CollapsingHeader("Materials"))
		{
			DrawMaterialSection();
		}

		if (ImGui.CollapsingHeader("Sky / Probe GI", ImGuiTreeNodeFlags.DefaultOpen))
		{
			DrawProbeGiSection();
		}

		if (ImGui.CollapsingHeader("Motion vectors"))
		{
			DrawMotionVectorSection();
		}

		if (ImGui.CollapsingHeader("Render graph (debug)"))
		{
			DrawRenderGraphSection();
		}

		// Grouped by cost, not by meaning: everything here needs the Apply button.
		if (ImGui.CollapsingHeader("Reload (environment and loading)"))
		{
			DrawReloadSection();
		}

		DrawApplyBar();

		if (_changed)
		{
			SettingsWindow.RaisePreviewGraphicsApplied();
			_savePending = true;
		}

		// Save once the control is released; writing json every drag tick hammers the disk.
		if (_savePending && !ImGui.IsAnyItemActive())
		{
			_savePending = false;
			_settings.Save();
		}
	}

	// DECA_VSYNC at startup outranks the saved setting: the checkbox follows the API, not vice versa.
	private void DrawDisplaySection()
	{
		ImGui.Spacing();

		var api = _viewport?.Environment?.GraphicsApi ?? _sceneViewport?.Environment?.GraphicsApi;
		if (api == null)
		{
			ImGui.TextDisabled("Environment not created yet.");
			return;
		}

		if (!_vsyncSynced)
		{
			_vsyncSynced = true;
			if (System.Environment.GetEnvironmentVariable("DECA_VSYNC") != null)
			{
				_settings.VSync = api.PresentInterval > 0;
			}
			else
			{
				api.PresentInterval = _settings.VSync ? 1 : 0;
			}
		}

		var vsync = _settings.VSync;
		if (ImGui.Checkbox("VSync", ref vsync))
		{
			_settings.VSync = vsync;
			api.PresentInterval = vsync ? 1 : 0;
			_changed = true;
		}
		Tooltip("Vertical sync on present (IGraphicsApi.PresentInterval).\n" +
			"Turning it off removes the frame rate cap - useful for performance measurements;\n" +
			"frames in flight are still limited by the fence (see Present).\n" +
			"Applied live; at startup it is overridden by DECA_VSYNC (1/0).");
	}

	// Knobs baked into IBL, samplers and the texture decoder: staged, applied by the button below.
	private void DrawReloadSection()
	{
		ImGui.Spacing();
		ImGui.TextDisabled("Applied by the button at the bottom of the window: they recreate\n" +
			"the environment and reload the model from disk.");
		ImGui.Spacing();

		var aniso = _pendingAniso;
		if (ImGui.Checkbox("Anisotropic filtering", ref aniso))
		{
			_pendingAniso = aniso;
		}
		PendingMark(_pendingAniso != _settings.PreviewAnisotropicFiltering);

		var hdrBuffer = _pendingHdr;
		ImGui.SetNextItemWidth(240 * _scale);
		if (ImGui.InputText("Environment HDR", ref hdrBuffer, 512))
		{
			_pendingHdr = hdrBuffer;
		}
		PendingMark(_pendingHdr != (_settings.PreviewEnvironmentHdr ?? string.Empty));
		Tooltip("Equirect .hdr: absolute path, or relative to EditorAssets/.\nEmpty - procedural sky.\nApplied by the button at the bottom of the window (recreates the environment and rebakes probes).");

		// Not a render knob, but baked into the loader's decoder: same model-reload cost.
		var sizes = new[] { 512, 1024, 2048, 4096 };
		var labels = new[] { "512", "1024", "2048", "4096" };
		int index = Array.IndexOf(sizes, _pendingMaxTextureSize);
		if (index < 0)
		{
			index = 2;
			_pendingMaxTextureSize = sizes[index];
		}

		ImGui.SetNextItemWidth(120 * _scale);
		if (ImGui.Combo("Texture size limit", ref index, labels, labels.Length))
		{
			_pendingMaxTextureSize = sizes[index];
		}
		PendingMark(_pendingMaxTextureSize != _settings.PreviewMaxTextureSize);
		Tooltip("Maximum texture side when loading a model.\n\n" +
			"Directly sets PEAK load memory: the loader decodes ALL of the model's textures\n" +
			"at once and only then uploads them to the GPU, so an uncompressed RGBA copy of\n" +
			"every texture is resident at the same time. At 2048 one texture is 16 MB; on an\n" +
			"asset like Intel Sponza with hundreds of textures that is gigabytes.\n\n" +
			"Each step down cuts the peak FOURFOLD. 1024 is nearly indistinguishable in the\n" +
			"preview, 512 visibly blurs close-ups.");

		DrawStreamingSettings();
	}

	// Live knobs: the streamer re-reads the radius every Tick, no reload needed.
	private void DrawStreamingSettings()
	{
		ImGui.Separator();
		ImGui.TextDisabled("Scene");

		bool skinning = _settings.SceneSkinning;
		if (ImGui.Checkbox("GPU skinning", ref skinning))
		{
			_settings.SceneSkinning = skinning;
			_changed = true;
		}
		Tooltip("Deform skinned models on the GPU.\n\n" +
			"Turning it off does NOT hide the model - it is drawn in bind pose through the\n" +
			"regular static path: no per-instance slots in the mega vertex buffer, no batches\n" +
			"for them, no compute pass. Animation components still show in the inspector.\n\n" +
			"This is an INSTANTIATION knob: skinned instances are registered when a model\n" +
			"enters the scene, so toggling only affects later instantiations - reopen a model\n" +
			"that is already shown.\n\n" +
			"DECA_SKINNING=0 outranks this checkbox: it exists as an escape hatch for when\n" +
			"the editor does not survive long enough to reach this window.");

		bool streaming = _settings.SceneStreaming;
		if (ImGui.Checkbox("Distance streaming", ref streaming))
		{
			_settings.SceneStreaming = streaming;
			_changed = true;
		}
		Tooltip("Scene models are acquired and released by distance to the camera.\n\n" +
			"Turning it off does NOT disable loading - it makes every scene model permanently\n" +
			"resident: the radius goes to infinity and nothing is ever released. Memory then\n" +
			"grows to the whole scene at once.\n\n" +
			"Practical use of turning it off: streaming is the only path in the editor where\n" +
			"the set of meshes and batches changes DURING the frame sequence rather than on\n" +
			"the first frame. The toggle separates scene bugs from streaming bugs without a\n" +
			"rebuild.");

		if (!streaming)
		{
			return;
		}

		float radius = _settings.SceneStreamingRadius;
		ImGui.SetNextItemWidth(160 * _scale);
		if (ImGui.SliderFloat("Streaming radius", ref radius, 10f, 5000f, "%.0f"))
		{
			_settings.SceneStreamingRadius = MathF.Max(1f, radius);
			_changed = true;
		}
		Tooltip("World units. Beyond the radius a model is released, inside it is acquired.\n\n" +
			"Unloading has slack (x1.15 hysteresis): without it a model sitting exactly on the\n" +
			"boundary would load and unload on every camera step.\n\n" +
			"Too small a radius shows visible pop-in around the camera; too large makes\n" +
			"streaming pointless - the whole scene ends up resident anyway.");
	}

	private static void PendingMark(bool pending)
	{
		if (!pending)
		{
			return;
		}

		ImGui.SameLine();
		ImGui.TextColored(new Vector4(1f, 0.8f, 0.35f, 1f), "*");
	}

	private void DrawMaterialSection()
	{
		ImGui.Spacing();

		var normalMaps = _settings.PreviewNormalMaps;
		if (ImGui.Checkbox("Normal maps", ref normalMaps))
		{
			_settings.PreviewNormalMaps = normalMaps;
			_changed = true;
		}

		var bakedAo = _settings.PreviewBakedOcclusion;
		if (ImGui.Checkbox("Baked occlusion (occlusionTexture)", ref bakedAo))
		{
			_settings.PreviewBakedOcclusion = bakedAo;
			_changed = true;
		}
	}

	// Without force, resync only on external edits; otherwise it overwrites in-progress input.
	private void SyncPendingFromSettings(bool force)
	{
		var current = (_settings.PreviewAnisotropicFiltering,
			_settings.PreviewEnvironmentHdr ?? string.Empty, _settings.PreviewMaxTextureSize);

		if (!force && current == _pendingSource)
		{
			return;
		}

		_pendingSource = current;
		_pendingAniso = current.Item1;
		_pendingHdr = current.Item2;
		_pendingMaxTextureSize = current.Item3;
	}

	private List<string> CollectPendingChanges()
	{
		var changes = new List<string>();


		if (_pendingAniso != _settings.PreviewAnisotropicFiltering)
		{
			changes.Add($"Anisotropic filtering: {OnOff(_settings.PreviewAnisotropicFiltering)} -> {OnOff(_pendingAniso)}");
		}

		if (_pendingHdr != (_settings.PreviewEnvironmentHdr ?? string.Empty))
		{
			changes.Add($"Environment HDR: {HdrLabel(_settings.PreviewEnvironmentHdr)} -> {HdrLabel(_pendingHdr)}");
		}

		if (_pendingMaxTextureSize != _settings.PreviewMaxTextureSize)
		{
			changes.Add($"Texture size limit: {_settings.PreviewMaxTextureSize} -> {_pendingMaxTextureSize}");
		}

		return changes;

		static string OnOff(bool value) => value ? "on" : "off";
		static string HdrLabel(string path) =>
			string.IsNullOrWhiteSpace(path) ? "procedural sky" : Path.GetFileName(path.Trim());
	}

	private void DrawApplyBar()
	{
		ImGui.Spacing();
		ImGui.Separator();

		var changes = CollectPendingChanges();
		if (changes.Count == 0)
		{
			ImGui.TextDisabled("No changes requiring a reload.");
			return;
		}

		ImGui.TextColored(new Vector4(1f, 0.8f, 0.35f, 1f), $"Pending ({changes.Count}):");
		foreach (var change in changes)
		{
			ImGui.BulletText(change);
		}

		ImGui.Spacing();

		if (ImGui.Button("Apply", new Vector2(140 * _scale, 0)))
		{
			ApplyPending();
		}
		Tooltip("Writes every staged reload knob at once and recreates the environment:\n" +
			"the model is reread from disk, probes are rebaked. Seconds on a heavy asset -\n" +
			"which is why the knobs are staged instead of applied one by one.");

		ImGui.SameLine();
		if (ImGui.Button("Revert", new Vector2(140 * _scale, 0)))
		{
			SyncPendingFromSettings(force: true);
		}
		Tooltip("Reset the controls to what the engine is currently using.");
	}

	private void ApplyPending()
	{
		_settings.PreviewAnisotropicFiltering = _pendingAniso;
		_settings.PreviewEnvironmentHdr = _pendingHdr;
		_settings.PreviewMaxTextureSize = _pendingMaxTextureSize;

		SyncPendingFromSettings(force: true);
		_changed = true;
	}

	// AlwaysClamp: ctrl+click typing otherwise stores out-of-range values in the json.
	private bool Slider(string label, ref float value, float min, float max, string format,
		ImGuiSliderFlags flags = ImGuiSliderFlags.AlwaysClamp)
	{
		ImGui.SetNextItemWidth(180 * _scale);
		if (ImGui.SliderFloat(label, ref value, min, max, format, flags))
		{
			_changed = true;
			return true;
		}

		return false;
	}

	private bool SliderInt(string label, ref int value, int min, int max)
	{
		ImGui.SetNextItemWidth(180 * _scale);
		if (ImGui.SliderInt(label, ref value, min, max, "%d", ImGuiSliderFlags.AlwaysClamp))
		{
			_changed = true;
			return true;
		}

		return false;
	}

	private static void Tooltip(string text)
	{
		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip(text);
		}
	}
}
