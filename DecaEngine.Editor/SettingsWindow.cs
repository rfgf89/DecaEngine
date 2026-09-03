using System.Numerics;
using System.Linq;
using DecaEngine.Core;
using DecaEngine.Core.Assets;
using Engine.ImGui.Core;
using Hexa.NET.ImGui;
using DecaEngine.Graphics;

namespace DecaEngine.Editor;

public class SettingsWindow : ImGuiModalWindow
{
	private enum SettingsCategory
	{
		General,
		Editor,
		Graphics
	}

	/// <summary>Raised after "OK" when live preview graphics settings changed.</summary>
	public static event Action PreviewGraphicsApplied;

	private readonly EditorSettings _settings;
	private readonly ImGuiRender _imGuiRenderRef;

	private SettingsCategory _selectedCategory = SettingsCategory.General;

	private int _uiScaleIndex;

	private Vector4 _paletteAccentBackup;
	private Vector4 _paletteBackgroundBackup;
	private Vector4 _paletteSurfaceBackup;
	private Vector4 _paletteTextBackup;
	private Vector4 _paletteSelectionBackup;
	private Vector4 _paletteIconAccentBackup;

	public SettingsWindow(string title, EditorSettings settings, ImGuiRender imGuiRender) : base(title, imGuiRender)
	{
		_settings = settings;
		_imGuiRenderRef = imGuiRender;
		_uiScaleIndex = Array.IndexOf(EditorSettings.AllowedUiScalePercents, _settings.UiScalePercent);
		if (_uiScaleIndex < 0)
		{
			_uiScaleIndex = Array.IndexOf(EditorSettings.AllowedUiScalePercents, 100);
		}
	}

	protected override void OnRender(uint dockId)
	{
		var availableSpace = ImGui.GetContentRegionAvail();
		float buttonAreaHeight = 35 * _scale;
		var contentHeight = availableSpace.Y - buttonAreaHeight - 5 * _scale;

		var categoryListWidth = MathF.Max(140 * _scale, availableSpace.X * 0.22f);

		ImGui.BeginChild("SettingsCategories", new Vector2(categoryListWidth, contentHeight), ImGuiChildFlags.Borders);
		{
			DrawCategoryItem(SettingsCategory.General, "General");
			DrawCategoryItem(SettingsCategory.Editor, "Editor");
			DrawCategoryItem(SettingsCategory.Graphics, "Graphics");
		}
		ImGui.EndChild();

		ImGui.SameLine();
		ImGui.BeginChild("SettingsContent", new Vector2(0, contentHeight), ImGuiChildFlags.Borders);
		{
			switch (_selectedCategory)
			{
				case SettingsCategory.General:
					DrawGeneralSection();
					break;
				case SettingsCategory.Editor:
					DrawEditorContent();
					break;
				case SettingsCategory.Graphics:
					DrawGraphicsSection();
					break;
			}
		}
		ImGui.EndChild();

		ImGui.Spacing();

		var buttonWidth = (ImGui.GetContentRegionAvail().X - 5 * _scale) / 2f;

		if (ImGui.Button("OK", new Vector2(buttonWidth, 0)))
		{
			ApplySettings();
			_settings.Save();
			Close();
		}

		ImGui.SameLine(0, 5 * _scale);

		if (ImGui.Button("Cancel", new Vector2(buttonWidth, 0)))
		{
			_imGuiRenderRef.UiScaleMultiplier = _settings.UiScaleMultiplier;
			EditorPalette.Accent = _paletteAccentBackup;
			EditorPalette.Background = _paletteBackgroundBackup;
			EditorPalette.Surface = _paletteSurfaceBackup;
			EditorPalette.Text = _paletteTextBackup;
			EditorPalette.Selection = _paletteSelectionBackup;
			EditorPalette.IconAccent = _paletteIconAccentBackup;
			EditorPalette.ActiveBasePresetName = EditorPalette.FindMatchingBasePreset(EditorPalette.Accent, EditorPalette.Background, EditorPalette.Surface)?.Name ?? EditorPalette.CustomPresetName;
			EditorPalette.ActiveTextPresetName = EditorPalette.FindMatchingTextPreset(EditorPalette.Text)?.Name ?? EditorPalette.CustomPresetName;
			EditorPalette.ActiveSelectionPresetName = EditorPalette.FindMatchingSelectionPreset(EditorPalette.Selection)?.Name ?? EditorPalette.CustomPresetName;
			EditorPalette.ActiveIconPresetName = EditorPalette.FindMatchingIconPreset(EditorPalette.IconAccent)?.Name ?? EditorPalette.CustomPresetName;
			Close();
		}
	}

	private void DrawCategoryItem(SettingsCategory category, string label)
	{
		var isSelected = _selectedCategory == category;
		if (ImGui.Selectable(label, isSelected))
		{
			_selectedCategory = category;
		}
	}


	private void DrawGeneralSection()
	{
		ImGui.TextColored(EditorSelectionStyle.Accent, "General");
		ImGui.Separator();
		ImGui.Spacing();

		var autoLoad = _settings.AutoLoadLastProject;
		if (ImGui.Checkbox("Automatically load last project on startup", ref autoLoad))
		{
			_settings.AutoLoadLastProject = autoLoad;
		}
	}

	private void DrawGraphicsSection()
	{
		ImGui.TextColored(EditorSelectionStyle.Accent, "Graphics Pipeline");
		ImGui.Separator();
		ImGui.Spacing();

		ImGui.TextUnformatted("Vertex/pixel shaders used to load and render glTF models in the editor.");
		ImGui.Spacing();

		var vertexShader = _settings.DefaultVertexShader;
		if (EditorRefPicker.Draw("Vertex Shader", ref vertexShader, ".hlsl"))
		{
			_settings.DefaultVertexShader = vertexShader;
		}

		var pixelShader = _settings.DefaultPixelShader;
		if (EditorRefPicker.Draw("Pixel Shader", ref pixelShader, ".hlsl"))
		{
			_settings.DefaultPixelShader = pixelShader;
		}

		ImGui.Spacing();
		ImGui.Separator();
		ImGui.Spacing();

		DrawPreviewGraphicsSection();
	}

	// Pass/target settings only take effect after an editor restart, anisotropy on the next load.
	private void DrawPreviewGraphicsSection()
	{
		ImGui.TextColored(EditorSelectionStyle.Accent, "Model Preview");
		ImGui.Separator();
		ImGui.Spacing();

		var normalMaps = _settings.PreviewNormalMaps;
		if (ImGui.Checkbox("Normal maps", ref normalMaps))
		{
			_settings.PreviewNormalMaps = normalMaps;
		}

		var bakedAo = _settings.PreviewBakedOcclusion;
		if (ImGui.Checkbox("Baked ambient occlusion", ref bakedAo))
		{
			_settings.PreviewBakedOcclusion = bakedAo;
		}

		var shadows = _settings.PreviewShadows;
		if (ImGui.Checkbox("Shadows (sun light)", ref shadows))
		{
			_settings.PreviewShadows = shadows;
		}

		ImGui.Spacing();
		ImGui.TextDisabled("Applied immediately (preview model reloads):");
		ImGui.Spacing();

		var ssao = _settings.PreviewSsao;
		if (ImGui.Checkbox("Ambient occlusion (contact shading)", ref ssao))
		{
			_settings.PreviewSsao = ssao;
		}

		if (ssao)
		{
			var aoModeLabels = new[] { "SSAO", "GTAO" };
			var aoModeIndex = _settings.PreviewAoMode == AmbientOcclusionMode.Gtao ? 1 : 0;
			ImGui.SetNextItemWidth(120 * _scale);
			if (ImGui.Combo("AO technique", ref aoModeIndex, aoModeLabels, aoModeLabels.Length))
			{
				_settings.PreviewAoMode = aoModeIndex == 1 ? AmbientOcclusionMode.Gtao : AmbientOcclusionMode.Ssao;
			}
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("SSAO - classic spiral occlusion.\nGTAO - horizons + visibility integral: cleaner on flat surfaces, slightly more expensive.");
			}
		}

		var ssgi = _settings.PreviewSsgi;
		if (ImGui.Checkbox("Global illumination (SSGI)", ref ssgi))
		{
			_settings.PreviewSsgi = ssgi;
		}
		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip("Screen-space global illumination: one light bounce from the frame\n(color bleeding - nearby geometry lit by reflected color).");
		}

		var sky = _settings.PreviewSkyBackground;
		if (ImGui.Checkbox("Environment sky background", ref sky))
		{
			_settings.PreviewSkyBackground = sky;
		}

		var aniso = _settings.PreviewAnisotropicFiltering;
		if (ImGui.Checkbox("Anisotropic filtering", ref aniso))
		{
			_settings.PreviewAnisotropicFiltering = aniso;
		}

		var hdrBuffer = _settings.PreviewEnvironmentHdr ?? string.Empty;
		ImGui.SetNextItemWidth(280 * _scale);
		if (ImGui.InputText("Environment HDR (.hdr path)", ref hdrBuffer, 512))
		{
			_settings.PreviewEnvironmentHdr = hdrBuffer;
		}
		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip("Absolute path, or relative to EditorAssets/ (e.g. env/studio.hdr).\nEmpty - procedural sky.");
		}
	}

	private void DrawEditorContent()
	{
		ImGui.TextColored(EditorSelectionStyle.Accent, "Editor");
		ImGui.Separator();
		ImGui.Spacing();

		ImGui.TextColored(EditorSelectionStyle.Accent, "UI Scale");
		DrawUIScaleSection();
		ImGui.Spacing();
		ImGui.Separator();
		ImGui.Spacing();

		ImGui.TextColored(EditorSelectionStyle.Accent, "Text");
		DrawTextSection();
		ImGui.Spacing();
		ImGui.Separator();
		ImGui.Spacing();

		ImGui.TextColored(EditorSelectionStyle.Accent, "Selection");
		DrawSelectionSection();
		ImGui.Spacing();
		ImGui.Separator();
		ImGui.Spacing();

		ImGui.TextColored(EditorSelectionStyle.Accent, "Icons");
		DrawIconsSection();
		ImGui.Spacing();
		ImGui.Separator();
		ImGui.Spacing();

		ImGui.TextColored(EditorSelectionStyle.Accent, "Dark Themes");
		DrawThemesSection(dark: true);
		ImGui.Spacing();
		ImGui.Separator();
		ImGui.Spacing();

		ImGui.TextColored(EditorSelectionStyle.Accent, "Light Themes");
		DrawThemesSection(dark: false);
	}

	private void DrawUIScaleSection()
	{
		ImGui.Spacing();

		var labels = EditorSettings.AllowedUiScalePercents.Select(p => $"{p}%").ToArray();
		_uiScaleIndex = Math.Clamp(_uiScaleIndex, 0, labels.Length - 1);

		var canDecrease = _uiScaleIndex > 0;
		var canIncrease = _uiScaleIndex < labels.Length - 1;
		var valueWidth = 60 * _scale;

		ImGui.BeginDisabled(!canDecrease);
		if (ImGui.ArrowButton("##UiScaleLeft", ImGuiDir.Left))
		{
			_uiScaleIndex--;
			_settings.UiScalePercent = EditorSettings.AllowedUiScalePercents[_uiScaleIndex];
			_imGuiRenderRef.UiScaleMultiplier = _settings.UiScaleMultiplier;
		}
		ImGui.EndDisabled();

		ImGui.SameLine();
		var label = labels[_uiScaleIndex];
		var textSize = ImGui.CalcTextSize(label);
		var cursorX = ImGui.GetCursorPosX();
		ImGui.SetCursorPosX(cursorX + MathF.Max(0, (valueWidth - textSize.X) * 0.5f));
		ImGui.TextUnformatted(label);

		ImGui.SameLine();
		ImGui.SetCursorPosX(cursorX + valueWidth);
		ImGui.BeginDisabled(!canIncrease);
		if (ImGui.ArrowButton("##UiScaleRight", ImGuiDir.Right))
		{
			_uiScaleIndex++;
			_settings.UiScalePercent = EditorSettings.AllowedUiScalePercents[_uiScaleIndex];
			_imGuiRenderRef.UiScaleMultiplier = _settings.UiScaleMultiplier;
		}
		ImGui.EndDisabled();
	}

	private void DrawTextSection()
	{
		DrawTextPresetGrid();
	}

	private void DrawSelectionSection()
	{
		DrawSelectionPresetGrid();
	}

	private void DrawIconsSection()
	{
		ImGui.TextUnformatted("Icon accent color is independent from Text/Selection - pick a preset just for file-type icons.");
		ImGui.Spacing();

		DrawIconPresetGrid();

		ImGui.Spacing();
		ImGui.Separator();
		ImGui.Spacing();

		ImGui.TextUnformatted("Preview of the Asset Browser file-type icons for the currently selected palette.");
		ImGui.Spacing();

		var iconSize = 48f * _scale;
		var iconSpacing = ImGui.GetStyle().ItemSpacing.X;
		var iconWindowVisibleX2 = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
		var iconDrawList = ImGui.GetWindowDrawList();

		var kinds = Enum.GetValues<AssetBrowserWindow.FileIconKind>();
		var total = kinds.Length + 1;

		for (var i = 0; i < total; i++)
		{
			var isFolder = i == 0;
			var name = isFolder ? "Folder" : kinds[i - 1].ToString();

			ImGui.PushID(i);
			ImGui.Dummy(new Vector2(iconSize, iconSize));
			var min = ImGui.GetItemRectMin();
			var max = ImGui.GetItemRectMax();

			if (isFolder)
			{
				AssetBrowserWindow.DrawFolderIcon(iconDrawList, min, max, _scale);
			}
			else
			{
				AssetBrowserWindow.DrawFileIcon(iconDrawList, min, max, kinds[i - 1], _scale);
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip(name);
			}
			ImGui.PopID();

			var lastItemX2 = ImGui.GetItemRectMax().X;
			var nextItemX2 = lastItemX2 + iconSpacing + iconSize;
			if (i + 1 < total && nextItemX2 < iconWindowVisibleX2)
			{
				ImGui.SameLine();
			}
		}
	}

	private void DrawThemesSection(bool dark)
	{
		DrawBasePresetGrid(dark);
	}

	private void DrawIconPresetGrid()
	{
		var presets = EditorPalette.IconPresets;
		ImGui.PushID("IconPresetGrid");
		DrawSwatchGrid(presets.Length,
			i => presets[i].Name,
			isActive: name => EditorPalette.ActiveIconPresetName == name,
			onClick: i => EditorPalette.ApplyIconPreset(presets[i]),
			drawPreview: null,
			buttonBackground: i => presets[i].IconAccent);
		ImGui.PopID();
	}

	private void DrawBasePresetGrid(bool dark)
	{
		var presets = EditorPalette.BasePresets
			.Where(p => (EditorPalette.Luminance(p.Background) < 0.5f) == dark)
			.ToArray();
		// Distinct PushID per grid: all grids index from 0, so "##swatch" alone would collide.
		ImGui.PushID(dark ? "DarkBasePresetGrid" : "LightBasePresetGrid");
		DrawSwatchGrid(presets.Length,
			i => presets[i].Name,
			isActive: name => EditorPalette.ActiveBasePresetName == name,
			onClick: i => EditorPalette.ApplyBasePreset(presets[i]),
			drawPreview: (drawList, min, max, i) =>
			{
				var preset = presets[i];
				drawList.AddRectFilled(new Vector2(min.X, min.Y + (max.Y - min.Y) * 0.6f), max, ImGui.GetColorU32(preset.Surface));
				drawList.AddRectFilled(new Vector2(min.X, max.Y - 6f * _scale), max, ImGui.GetColorU32(preset.Accent));
			},
			buttonBackground: i => presets[i].Background);
		ImGui.PopID();
	}

	private void DrawTextPresetGrid()
	{
		var presets = EditorPalette.TextPresets;
		ImGui.PushID("TextPresetGrid");
		DrawSwatchGrid(presets.Length,
			i => presets[i].Name,
			isActive: name => EditorPalette.ActiveTextPresetName == name,
			onClick: i => EditorPalette.ApplyTextPreset(presets[i]),
			drawPreview: null,
			buttonBackground: i => presets[i].Text,
			isEnabled: i => EditorPalette.HasSufficientContrast(presets[i].Text, EditorPalette.Background));
		ImGui.PopID();
	}

	private void DrawSelectionPresetGrid()
	{
		var presets = EditorPalette.SelectionPresets;
		ImGui.PushID("SelectionPresetGrid");
		DrawSwatchGrid(presets.Length,
			i => presets[i].Name,
			isActive: name => EditorPalette.ActiveSelectionPresetName == name,
			onClick: i => EditorPalette.ApplySelectionPreset(presets[i]),
			drawPreview: null,
			buttonBackground: i => presets[i].Selection,
			isEnabled: i => EditorPalette.HasSufficientContrast(presets[i].Selection, EditorPalette.Background));
		ImGui.PopID();
	}

	private void DrawSwatchGrid(int count, Func<int, string> nameOf, Func<string, bool> isActive, Action<int> onClick,
		Action<ImDrawListPtr, Vector2, Vector2, int>? drawPreview, Func<int, Vector4> buttonBackground,
		Func<int, bool>? isEnabled = null)
	{
		const float swatchSize = 56f;
		var spacing = ImGui.GetStyle().ItemSpacing.X;
		var scaledSwatch = swatchSize * _scale;

		// Right edge (in screen space) that a swatch must not cross, otherwise it wraps to the next line.
		var windowVisibleX2 = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;

		int visibleIndex = 0;
		for (var i = 0; i < count; i++)
		{
			var name = nameOf(i);
			var active = isActive(name);
			var enabled = isEnabled?.Invoke(i) ?? true;

			if (!enabled)
			{
				continue;
			}

			if (active)
			{
				ImGui.PushStyleColor(ImGuiCol.Border, EditorPalette.Selection);
				ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 2f * _scale);
			}

			ImGui.PushID(visibleIndex);

			var bg = buttonBackground(i);
			ImGui.PushStyleColor(ImGuiCol.Button, bg);
			ImGui.PushStyleColor(ImGuiCol.ButtonHovered, bg);
			ImGui.PushStyleColor(ImGuiCol.ButtonActive, bg);
			if (ImGui.Button("##swatch", new Vector2(scaledSwatch, scaledSwatch)))
			{
				onClick(i);
			}

			if (drawPreview != null)
			{
				var min = ImGui.GetItemRectMin();
				var max = ImGui.GetItemRectMax();
				drawPreview(ImGui.GetWindowDrawList(), min, max, i);
			}
			ImGui.PopStyleColor(3);

			// The preset name is shown only as a tooltip on hover.
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip(name);
			}

			ImGui.PopID();

			if (active)
			{
				ImGui.PopStyleVar();
				ImGui.PopStyleColor();
			}

			// Only continue on the same line if the next swatch would still fit within the available width.
			var lastButtonX2 = ImGui.GetItemRectMax().X;
			var nextButtonX2 = lastButtonX2 + spacing + scaledSwatch;
			if (visibleIndex + 1 < count && nextButtonX2 < windowVisibleX2)
			{
				ImGui.SameLine();
			}

			visibleIndex++;
		}
	}


	public override void Show()
	{
		_uiScaleIndex = Array.IndexOf(EditorSettings.AllowedUiScalePercents, _settings.UiScalePercent);
		if (_uiScaleIndex < 0)
		{
			_uiScaleIndex = Array.IndexOf(EditorSettings.AllowedUiScalePercents, 100);
		}

		_paletteAccentBackup = EditorPalette.Accent;
		_paletteBackgroundBackup = EditorPalette.Background;
		_paletteSurfaceBackup = EditorPalette.Surface;
		_paletteTextBackup = EditorPalette.Text;
		_paletteSelectionBackup = EditorPalette.Selection;
		_paletteIconAccentBackup = EditorPalette.IconAccent;

		base.Show();
	}

	private void ApplySettings()
	{
		_settings.UiScalePercent = EditorSettings.AllowedUiScalePercents[Math.Clamp(_uiScaleIndex, 0, EditorSettings.AllowedUiScalePercents.Length - 1)];
		_imGuiRenderRef.UiScaleMultiplier = _settings.UiScaleMultiplier;
		EditorPalette.SaveTo(_settings);

		PreviewGraphicsApplied?.Invoke();
	}

	/// <summary>Raises <see cref="PreviewGraphicsApplied"/> from outside the modal.</summary>
	public static void RaisePreviewGraphicsApplied() => PreviewGraphicsApplied?.Invoke();

	/// <summary>Raises <see cref="PreviewGraphicsApplied"/> for headless probes.</summary>
	public static void RaisePreviewGraphicsAppliedForTest() => PreviewGraphicsApplied?.Invoke();
}

