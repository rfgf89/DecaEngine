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

/// <summary>Light and shadow sections: world sun, cascades, shadow map slice capture.</summary>
public partial class GraphicsSettingsWindow
{
	private void DrawLightSection()
	{
		ImGui.Spacing();

		var shadows = _settings.PreviewShadows;
		if (ImGui.Checkbox("Shadows (world sun)", ref shadows))
		{
			_settings.PreviewShadows = shadows;
			_changed = true;
		}
		Tooltip("Shadows from the world key light (cascaded shadow map). Turning them off falls back\nto the camera light rig and hides probe GI (probes need a sun direction).");

		// Range top mirrors the clamp applied on the way to the cbuffer and the bake.
		var sun = _settings.ProbeGiSunIntensity;
		if (Slider("Sun intensity", ref sun, 0.1f, 16f, "%.2f"))
		{
			_settings.ProbeGiSunIntensity = sun;
		}
		Tooltip("Sun intensity - both the analytic key light and the bounce in probes (rebakes them).\nAbove the tonemap knee (~0.76 on bright albedo) contrast flattens out - tune together with Ambient boost.");

		// The stored value is the SHADER mode (0 must stay PCSS), so combo indices go through a
		// table. Ray-traced is offered only where inline ray tracing exists.
		bool rtAvailable = _viewport?.RayTracingSupported ?? false;
		int[] shadowModeValues = rtAvailable ? [1, 2, 0, 3, 4] : [1, 2, 0, 3];
		var shadowModeLabels = rtAvailable
			? new[]
			{
				"Hard (1 tap)",
				"PCF 3x3",
				"PCSS (soft edge)",
				"PCSS HQ (32 taps)",
				"Ray-traced (reload)",
			}
			: new[]
			{
				"Hard (1 tap)",
				"PCF 3x3",
				"PCSS (soft edge)",
				"PCSS HQ (32 taps)",
			};
		var shadowModeIndex = Array.IndexOf(shadowModeValues, _settings.ShadowFilterMode);
		if (shadowModeIndex < 0)
		{
			shadowModeIndex = 2;
		}

		ImGui.SetNextItemWidth(200 * _scale);
		if (ImGui.Combo("Shadow filtering", ref shadowModeIndex, shadowModeLabels, shadowModeLabels.Length))
		{
			_settings.ShadowFilterMode = shadowModeValues[shadowModeIndex];
			_changed = true;
		}
		Tooltip("Shadow filter for the sun AND punctual lights, in order of cost:\n" +
			"  Hard - one hardware tap, edge within a texel. Cheapest.\n" +
			"  PCF 3x3 - constant one-texel softness, 9 taps.\n" +
			"  PCSS - penumbra from the source's angular size (sharp at contact, softer with\n" +
			"    distance), 16+16 taps on a Vogel disk; TAAU averages out the noise.\n" +
			"  PCSS HQ - the same PCSS with a doubled tap count (32+32) and a wider penumbra -\n" +
			"    for stills and for working without TAAU.\n" +
			"  Ray-traced - sun shadow cast by SHADOW RAYS against the TLAS (8 rays in the disk\n" +
			"    cone): physical penumbra with no cascades and no bias. Switching RELOADS the\n" +
			"    model (the shader variant is compiled by DXC). Alpha-tested foliage shadows as\n" +
			"    solid geometry; punctual lights stay on PCSS.\n" +
			"Penumbra width comes from Sun angular size (sun) and the light's SourceRadius (lamps).");

		var sunSize = _settings.SunAngularSize;
		if (Slider("Sun angular size", ref sunSize, 0.25f, 8f, "%.2f°",
			ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.Logarithmic))
		{
			_settings.SunAngularSize = sunSize;
		}
		Tooltip("Apparent DIAMETER of the sun disk, in degrees - the PCSS penumbra width: the larger\n" +
			"the disk, the softer the shadow gets with distance from the caster (contact stays sharp).\n" +
			"The real sun is ~0.53°; the default 1° keeps softness visible even on short shadows.");

		ImGui.Spacing();
		DrawShadowCascadesDebug();
	}

	// Synchronous D32 slice readback, normalised per cascade: raw depth of a large cascade looks
	// almost white because the scene occupies a narrow band of its Z range.
	private unsafe void DrawShadowCascadesDebug()
	{
		if (!ImGui.TreeNode("Shadow cascades (debug)"))
		{
			return;
		}

		var sourceLabels = new[] { "Scene View", "Model Preview" };
		ImGui.SetNextItemWidth(140 * _scale);
		ImGui.Combo("Source", ref _shadowDebugSource, sourceLabels, sourceLabels.Length);

		if (ImGui.Button("Capture", new Vector2(100 * _scale, 0)))
		{
			CaptureShadowCascades();
		}
		Tooltip("Synchronous readback of every shadow map slice of the selected viewport (the frame\nstalls for a moment). A snapshot, not live - capture again after moving the camera or light.");

		ImGui.SameLine();
		if (ImGui.Checkbox("Raw depth", ref _shadowDebugRaw))
		{
			RefreshShadowDebugTextures();
		}
		Tooltip("Depth exactly as stored (0..1 from cascade near to far) instead of stretched over the\nactual geometry range. In large cascades the scene occupies a narrow band, so the image\nis expectedly almost white - that is not a write bug.");

		if (_shadowDebugInfo.Length > 0)
		{
			ImGui.TextDisabled(_shadowDebugInfo);
		}

		if (_shadowDebugSlices != null)
		{
			float imageSize = 220 * _scale;
			for (int i = 0; i < _shadowDebugSlices.Length; i++)
			{
				ImGui.Image(_shadowDebugTexRefs[i], new Vector2(imageSize, imageSize));
				ImGui.SameLine();
				ImGui.BeginGroup();
				ImGui.Text($"Cascade {i}");
				var stats = _shadowDebugStats[i];
				var world = _shadowDebugWorld[i];
				if (stats.Coverage <= 0f)
				{
					ImGui.TextDisabled("empty (no geometry, or the cascade is not rendered)");
				}
				else
				{
					ImGui.Text($"geometry: {stats.Coverage * 100f:F1}% of texels");
					ImGui.Text($"depth: {stats.Min:F4} .. {stats.Max:F4}");
					if (world.WorldDepthRange > 0f)
					{
						ImGui.Text($"world: {stats.Min * world.WorldDepthRange:F1} .. {stats.Max * world.WorldDepthRange:F1} units (range {world.WorldDepthRange:F1})");
					}
				}
				if (world.WorldSize > 0f)
				{
					ImGui.Text($"extent: {world.WorldSize:F1} x {world.WorldSize:F1} units " +
						$"(texel {world.WorldSize / ShadowRenderer.ShadowMapSize:F3} units)");
				}
				ImGui.EndGroup();
				ImGui.Spacing();
			}
		}

		ImGui.TreePop();
	}

	private unsafe void CaptureShadowCascades()
	{
		var env = _shadowDebugSource == 0 ? _sceneViewport?.Environment : _viewport?.Environment;
		if (env?.BatchRenderer == null)
		{
			_shadowDebugInfo = "environment not created yet";
			return;
		}

		var shadowTarget = env.BatchRenderer.WorldShadowRenderer?.ShadowMapsTarget as DiligentRenderTarget;
		if (shadowTarget == null)
		{
			_shadowDebugInfo = "shadow map unavailable";
			return;
		}

		var fullSlices = DiligentTextureReadback.ReadFloatSlices(env.DilApi, shadowTarget,
			out int width, out int height);
		int step = Math.Max(1, width / ShadowDebugSize);

		_shadowDebugSlices = new float[fullSlices.Length][];
		_shadowDebugStats = new (float, float, float)[fullSlices.Length];
		_shadowDebugWorld = new (float, float)[fullSlices.Length];

		for (int slice = 0; slice < fullSlices.Length; slice++)
		{
			var data = new float[ShadowDebugSize * ShadowDebugSize];
			float min = float.MaxValue, max = float.MinValue;
			long geomCount = 0;
			for (int y = 0; y < ShadowDebugSize; y++)
			{
				for (int x = 0; x < ShadowDebugSize; x++)
				{
					float v = fullSlices[slice][(y * step) * width + x * step];
					data[y * ShadowDebugSize + x] = v;
					if (v < 1.0f)
					{
						geomCount++;
						min = Math.Min(min, v);
						max = Math.Max(max, v);
					}
				}
			}

			_shadowDebugSlices[slice] = data;
			_shadowDebugStats[slice] = geomCount > 0
				? (min, max, (float)geomCount / data.Length)
				: (0f, 0f, 0f);
		}

		// Cascade extents live in the sun cascade cameras: ortho width in viewport.Z, depth
		// range in far-near. The preview path has no cascade cameras, so these stay zero.
		var sun = env.SunEntity;
		if (!sun.IsNull && sun.HasComponent<CascadedShadowComponent>())
		{
			ref var cascaded = ref sun.GetComponent<CascadedShadowComponent>();
			fixed (CameraComponent* ptr = &cascaded.Cascade0)
			{
				for (int i = 0; i < Math.Min(_shadowDebugWorld.Length, ShadowRenderer.MaxCascades); i++)
				{
					var camData = (ptr + i)->data;
					_shadowDebugWorld[i] = (camData.viewport.Z, Math.Abs(camData.far - camData.near));
				}
			}
		}

		_shadowDebugInfo = $"{sourceName(_shadowDebugSource)}: {width}x{height} x{fullSlices.Length}, downsample {step}x";
		RefreshShadowDebugTextures();

		static string sourceName(int source) => source == 0 ? "Scene View" : "Model Preview";
	}

	private void RefreshShadowDebugTextures()
	{
		if (_shadowDebugSlices == null)
		{
			return;
		}

		var env = _shadowDebugSource == 0 ? _sceneViewport?.Environment : _viewport?.Environment;
		if (env == null)
		{
			return;
		}

		_shadowDebugTextures ??= new IGpuTexture[_shadowDebugSlices.Length];
		_shadowDebugTexRefs ??= new ImTextureRef[_shadowDebugSlices.Length];

		var pixels = new byte[ShadowDebugSize * ShadowDebugSize * 4];
		for (int slice = 0; slice < _shadowDebugSlices.Length; slice++)
		{
			var data = _shadowDebugSlices[slice];
			var stats = _shadowDebugStats[slice];
			float range = MathF.Max(stats.Max - stats.Min, 1e-6f);

			for (int i = 0; i < data.Length; i++)
			{
				float v = data[i];
				byte b;
				if (_shadowDebugRaw)
				{
					b = (byte)Math.Clamp((int)(v * 255f), 0, 255);
				}
				else
				{
					// Cleared texels (1.0) stay white; geometry is stretched into 0..230.
					b = v >= 1.0f ? (byte)255 : (byte)Math.Clamp((int)((v - stats.Min) / range * 230f), 0, 230);
				}

				int o = i * 4;
				pixels[o] = pixels[o + 1] = pixels[o + 2] = b;
				pixels[o + 3] = 255;
			}

			if (_shadowDebugTextures[slice] == null)
			{
				_shadowDebugTextures[slice] = env.DilApi.CreateTexture2DMutable(
					$"Shadow Cascade Debug {slice}", ShadowDebugSize, ShadowDebugSize);
				_shadowDebugTexRefs[slice] = _imGuiRender.GetNewTexture();
				_imGuiRender.BindRenderTarget(_shadowDebugTexRefs[slice].GetTexID(), _shadowDebugTextures[slice]);
			}

			env.DilApi.UpdateTexture2D(_shadowDebugTextures[slice], pixels);
		}
	}

}
