using System.Linq;
using System.Numerics;
using DecaEngine.Core.Prefabs;
using Engine.ImGui.Core;
using Friflo.Engine.ECS;
using Hexa.NET.ImGui;

namespace DecaEngine.Editor
{
	/// <summary>Vector file-type icons for <see cref="AssetBrowserWindow"/>; state and OnRender live in the main file.</summary>
	public partial class AssetBrowserWindow
	{
		private static bool IsPrefabJson(string fileName) =>
			fileName.EndsWith(".prefab.json", StringComparison.OrdinalIgnoreCase);

		private static bool IsPrefabBinary(string fileName) =>
			fileName.EndsWith(".prefab.bin", StringComparison.OrdinalIgnoreCase);

		private static bool IsGltfModel(string fileName) =>
			fileName.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase) ||
			fileName.EndsWith(".glb", StringComparison.OrdinalIgnoreCase);

		// internal: reused by ComponentFieldEditor.DrawAssetRef for AssetRef field icons.
		internal static FileIconKind GetFileIconKind(string fileName)
		{
			if (IsPrefabJson(fileName) || IsPrefabBinary(fileName))
			{
				return FileIconKind.Prefab;
			}

			var ext = Path.GetExtension(fileName).ToLowerInvariant();

			return ext switch
			{
				".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga" or ".gif" or ".dds" or ".ktx" or ".hdr" => FileIconKind.Image,
				".cs" or ".xaml" => FileIconKind.Code,
				".json" => FileIconKind.Json,
				".hlsl" or ".glsl" or ".fx" or ".shader" or ".vert" or ".frag" or ".comp" => FileIconKind.Shader,
				".wav" or ".mp3" or ".ogg" or ".flac" => FileIconKind.Audio,
				".obj" or ".fbx" or ".gltf" or ".glb" or ".dae" => FileIconKind.Model,
				".mat" => FileIconKind.Material,
				".scene" or ".prefab" => FileIconKind.Scene,
				_ => FileIconKind.Generic
			};
		}

		// Accents derive from EditorPalette.IconAccent so icons follow the active theme.
		private static Vector4 FolderAccent => IconColor(saturationMul: 0.85f, valueOffset: 0.10f);
		private static Vector4 PageFill => AdaptIconNeutral(new(0.30f, 0.30f, 0.32f, 1f));
		private static Vector4 GenericAccent => IconColor(saturationMul: 0.55f, valueOffset: -0.06f);
		private static Vector4 ImageAccent => IconColor(saturationMul: 1.00f, valueOffset: 0.08f);
		private static Vector4 CodeAccent => IconColor(saturationMul: 0.80f, valueOffset: -0.02f);
		private static Vector4 JsonAccent => IconColor(saturationMul: 0.85f, valueOffset: 0.05f);
		private static Vector4 ShaderAccent => IconColor(saturationMul: 1.05f, valueOffset: 0.04f);
		private static Vector4 AudioAccent => IconColor(saturationMul: 0.90f, valueOffset: -0.10f);
		private static Vector4 ModelAccent => IconColor(saturationMul: 0.65f, valueOffset: 0.02f);
		private static Vector4 MaterialAccent => IconColor(saturationMul: 1.10f, valueOffset: 0.12f);
		private static Vector4 SceneAccent => IconColor(saturationMul: 0.95f, valueOffset: -0.04f);
		private static Vector4 PrefabAccent => IconColor(saturationMul: 0.75f, valueOffset: 0.06f);

		// Derives an icon color from EditorPalette.IconAccent hue; hue is untrustworthy when
		// the accent is near-grey, so saturation/value blending is weighted by hueTrust.
		private static Vector4 IconColor(float saturationMul, float valueOffset)
		{
			var accent = EditorPalette.IconAccent;
			RgbToHsv(accent, out var accentHue, out var accentSaturation, out var accentValue);

			var hue = accentHue;

			// Near-grey accents make RgbToHsv hue numerically meaningless; fade it out.
			const float hueTrustThreshold = 0.10f;
			var hueTrust = Math.Clamp(accentSaturation / hueTrustThreshold, 0f, 1f);

			var baseSaturation = 0.30f + 0.35f * Math.Clamp(accentSaturation / 0.15f, 0f, 1f);
			var finalSaturation = Math.Clamp(baseSaturation * saturationMul * hueTrust, 0f, 1f);

			var isDarkTheme = EditorPalette.Luminance(EditorPalette.Background) < 0.5f;
			var baseValue = isDarkTheme ? 0.88f : 0.62f;
			// Grey accents lean harder on the accent's own Value so themes still differentiate.
			var accentValueWeight = 0.25f + 0.35f * (1f - hueTrust);
			var value = Math.Clamp(baseValue * (1f - accentValueWeight) + accentValue * accentValueWeight + valueOffset, isDarkTheme ? 0.45f : 0.35f, 0.98f);

			return HsvToRgb(hue, finalSaturation, value, 1f);
		}

		// Neutral icon fill adapted to theme: takes Surface hue, and on light themes derives
		// value from Background (offset, clamped) instead of a fixed multiplier.
		private static Vector4 AdaptIconNeutral(Vector4 baseColor)
		{
			RgbToHsv(EditorPalette.Surface, out var surfaceHue, out _, out _);
			RgbToHsv(baseColor, out _, out var baseSaturation, out var baseValue);

			var saturation = MathF.Max(baseSaturation, 0.08f);

			if (EditorPalette.Luminance(EditorPalette.Background) < 0.5f)
			{
				return HsvToRgb(surfaceHue, saturation, baseValue, baseColor.W);
			}

			RgbToHsv(EditorPalette.Background, out _, out _, out var backgroundValue);
			var value = Math.Clamp(backgroundValue - 0.20f, 0.55f, 0.85f);
			return HsvToRgb(surfaceHue, MathF.Min(saturation, 0.05f), value, baseColor.W);
		}


		private static void RgbToHsv(Vector4 color, out float h, out float s, out float v)
		{
			var r = color.X;
			var g = color.Y;
			var b = color.Z;
			var max = MathF.Max(r, MathF.Max(g, b));
			var min = MathF.Min(r, MathF.Min(g, b));
			var delta = max - min;

			v = max;
			s = max <= 0.0001f ? 0f : delta / max;

			if (delta <= 0.0001f)
			{
				h = 0f;
			}
			else if (max == r)
			{
				h = 60f * (((g - b) / delta) % 6f);
			}
			else if (max == g)
			{
				h = 60f * (((b - r) / delta) + 2f);
			}
			else
			{
				h = 60f * (((r - g) / delta) + 4f);
			}

			if (h < 0f)
			{
				h += 360f;
			}
		}

		private static Vector4 HsvToRgb(float h, float s, float v, float a)
		{
			var c = v * s;
			var hPrime = (h % 360f) / 60f;
			var x = c * (1f - MathF.Abs(hPrime % 2f - 1f));
			var m = v - c;

			var (r, g, b) = hPrime switch
			{
				< 1f => (c, x, 0f),
				< 2f => (x, c, 0f),
				< 3f => (0f, c, x),
				< 4f => (0f, x, c),
				< 5f => (x, 0f, c),
				_ => (c, 0f, x)
			};

			return new Vector4(r + m, g + m, b + m, a);
		}

		// Rounding scales with the smaller icon dimension so shapes stay proportional.
		private const float IconRoundingFactor = 0.1f;

		private static float GetIconRounding(float width, float height)
		{
			return MathF.Max(1f, MathF.Min(width, height) * IconRoundingFactor);
		}

		// Builds the folder outline (tab + body) as ONE closed path; two separate rects would
		// double-stroke the shared edge. Caller closes it via PathStroke(ImDrawFlags.Closed).
		private static void AddFolderOutlinePath(ImDrawListPtr drawList, Vector2 min, Vector2 max, float tabBottomY, float tabRight, float rounding)
		{
			var tabHeight = tabBottomY - min.Y;
			var tabRounding = MathF.Max(0f, MathF.Min(rounding, MathF.Min(tabHeight, (tabRight - min.X) * 0.5f)));
			var bodyRounding = MathF.Max(0f, MathF.Min(rounding, MathF.Min((max.X - min.X) * 0.5f, (max.Y - tabBottomY) * 0.5f)));

			drawList.PathClear();

			drawList.PathArcTo(new Vector2(min.X + tabRounding, min.Y + tabRounding), tabRounding, MathF.PI, 1.5f * MathF.PI);
			drawList.PathArcTo(new Vector2(tabRight - tabRounding, min.Y + tabRounding), tabRounding, 1.5f * MathF.PI, 2f * MathF.PI);

			drawList.PathLineTo(new Vector2(tabRight, tabBottomY));


			drawList.PathArcTo(new Vector2(max.X - bodyRounding, tabBottomY + bodyRounding), bodyRounding, 1.5f * MathF.PI, 2f * MathF.PI);
			drawList.PathArcTo(new Vector2(max.X - bodyRounding, max.Y - bodyRounding), bodyRounding, 0f, 0.5f * MathF.PI);
			drawList.PathArcTo(new Vector2(min.X + bodyRounding, max.Y - bodyRounding), bodyRounding, 0.5f * MathF.PI, MathF.PI);
		}

		internal static void DrawFolderIcon(ImDrawListPtr drawList, Vector2 min, Vector2 max, float scale, bool isEmpty = false)
		{
			var width = max.X - min.X;
			var height = max.Y - min.Y;
			var rounding = GetIconRounding(width, height);
			var tabHeight = height * 0.16f;
			var tabWidth = width * 0.5f;

			var bodyMin = new Vector2(min.X, min.Y + tabHeight);
			var bodyMax = max;
			var tabRight = min.X + tabWidth;

			if (isEmpty)
			{
				// Empty folder: same silhouette, stroked outline only (see AddFolderOutlinePath).
				var emptyColor = ImGui.GetColorU32(new Vector4(FolderAccent.X, FolderAccent.Y, FolderAccent.Z, 0.55f));
				var strokeThickness = MathF.Max(1.2f, 1.4f * scale);

				AddFolderOutlinePath(drawList, min, max, bodyMin.Y, tabRight, rounding);
				drawList.PathStroke(emptyColor, ImDrawFlags.Closed, strokeThickness);
				return;
			}

			var bodyColor = ImGui.GetColorU32(FolderAccent);

			// Tab rect overlaps the body by 1px so the seam never shows at fractional scales.
			drawList.AddRectFilled(min, new Vector2(tabRight, bodyMin.Y + 1f * scale), bodyColor, rounding,
				ImDrawFlags.RoundCornersTop);
			drawList.AddRectFilled(bodyMin, bodyMax, bodyColor, rounding, ImDrawFlags.RoundCornersBottom | ImDrawFlags.RoundCornersTopRight);
		}

		internal static void DrawFileIcon(ImDrawListPtr drawList, Vector2 min, Vector2 max, FileIconKind kind, float scale)
		{
			var pageColor = ImGui.GetColorU32(PageFill);

			var width = max.X - min.X;
			var height = max.Y - min.Y;
			var rounding = GetIconRounding(width, height);

			drawList.AddRectFilled(min, max, pageColor, rounding);


			var contentMin = new Vector2(min.X + width * 0.20f, min.Y + height * 0.32f);
			var contentMax = new Vector2(max.X - width * 0.20f, max.Y - height * 0.16f);

			switch (kind)
			{
				case FileIconKind.Image:
					DrawImageGlyph(drawList, contentMin, contentMax, scale);
					break;
				case FileIconKind.Code:
					DrawCodeGlyph(drawList, contentMin, contentMax, scale);
					break;
				case FileIconKind.Json:
					// JSON glyph needs wider margins than the shared content rect.
					var jsonMin = new Vector2(min.X + width * 0.08f, min.Y + height * 0.10f);
					var jsonMax = new Vector2(max.X - width * 0.08f, max.Y - height * 0.12f);
					DrawJsonGlyph(drawList, jsonMin, jsonMax, scale, min, max);
					break;
				case FileIconKind.Shader:
					DrawShaderGlyph(drawList, contentMin, contentMax, scale);
					break;
				case FileIconKind.Audio:
					DrawAudioGlyph(drawList, contentMin, contentMax, scale);
					break;
				case FileIconKind.Model:
					DrawModelGlyph(drawList, contentMin, contentMax, scale);
					break;
				case FileIconKind.Material:
					DrawMaterialGlyph(drawList, contentMin, contentMax, scale);
					break;
				case FileIconKind.Scene:
					DrawSceneGlyph(drawList, contentMin, contentMax, scale);
					break;
				case FileIconKind.Prefab:
					DrawPrefabGlyph(drawList, contentMin, contentMax, scale);
					break;
				default:
					DrawGenericGlyph(drawList, contentMin, contentMax, scale);
					break;
			}
		}

		private static void DrawGenericGlyph(ImDrawListPtr drawList, Vector2 min, Vector2 max, float scale)
		{
			var color = ImGui.GetColorU32(new Vector4(GenericAccent.X, GenericAccent.Y, GenericAccent.Z, 0.85f));
			var thickness = MathF.Max(1.4f, 1.6f * scale);
			var height = max.Y - min.Y;
			const int lineCount = 3;

			for (int i = 0; i < lineCount; i++)
			{
				var y = min.Y + height * (i + 0.5f) / (lineCount + 0.5f);
				var lineEnd = i == lineCount - 1 ? max.X - (max.X - min.X) * 0.35f : max.X;
				drawList.AddLine(new Vector2(min.X, y), new Vector2(lineEnd, y), color, thickness);
			}
		}

		private static void DrawImageGlyph(ImDrawListPtr drawList, Vector2 min, Vector2 max, float scale)
		{
			var width = max.X - min.X;
			var height = max.Y - min.Y;
			var rounding = GetIconRounding(width, height) * 0.7f;

			var accentColor = ImGui.GetColorU32(ImageAccent);
			var cardColor = ImGui.GetColorU32(new Vector4(ImageAccent.X, ImageAccent.Y, ImageAccent.Z, 0.22f));

			drawList.AddRectFilled(min, max, cardColor, rounding);

			drawList.PushClipRect(min, max, true);

			var sunCenter = new Vector2(min.X + width * 0.30f, min.Y + height * 0.32f);
			drawList.AddCircleFilled(sunCenter, width * 0.14f, accentColor);

			DrawRoundedHill(drawList, new Vector2(min.X - width * 0.05f, max.Y), new Vector2(min.X + width * 0.62f, min.Y + height * 0.34f), new Vector2(min.X + width * 0.98f, max.Y), accentColor);
			var backHillColor = ImGui.GetColorU32(new Vector4(ImageAccent.X, ImageAccent.Y, ImageAccent.Z, 0.7f));
			DrawRoundedHill(drawList, new Vector2(min.X + width * 0.32f, max.Y), new Vector2(min.X + width * 0.85f, min.Y + height * 0.52f), new Vector2(max.X + width * 0.05f, max.Y), backHillColor);

			drawList.PopClipRect();
		}

		// Hill built from two bezier halves instead of a triangle: smooth silhouette.
		private static void DrawRoundedHill(ImDrawListPtr drawList, Vector2 baseLeft, Vector2 peak, Vector2 baseRight, uint color)
		{
			const int segments = 12;
			var points = new List<Vector2>(segments * 2 + 1) { baseLeft };

			var ctrlUp1 = Vector2.Lerp(baseLeft, peak, 0.5f);
			AddBezierPoints(points, baseLeft, ctrlUp1, peak, peak, segments);

			var ctrlDown2 = Vector2.Lerp(peak, baseRight, 0.5f);
			AddBezierPoints(points, peak, peak, ctrlDown2, baseRight, segments);

			var arr = points.ToArray();
			drawList.AddConvexPolyFilled(ref arr[0], arr.Length, color);
		}

		private static void AddBezierPoints(List<Vector2> points, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, int segments)
		{
			for (int i = 1; i <= segments; i++)
			{
				var t = (float)i / segments;
				var u = 1f - t;
				var point = u * u * u * p0 + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * p3;
				points.Add(point);
			}
		}

		private static void DrawCodeGlyph(ImDrawListPtr drawList, Vector2 min, Vector2 max, float scale)
		{
			var color = ImGui.GetColorU32(CodeAccent);
			var thickness = MathF.Max(1.6f, 1.8f * scale);
			var width = max.X - min.X;
			var height = max.Y - min.Y;
			var midY = min.Y + height * 0.5f;
			var chevronW = width * 0.30f;
			var chevronH = height * 0.5f;

			drawList.AddLine(new Vector2(min.X + chevronW, midY - chevronH * 0.5f), new Vector2(min.X, midY), color, thickness);
			drawList.AddLine(new Vector2(min.X, midY), new Vector2(min.X + chevronW, midY + chevronH * 0.5f), color, thickness);

			drawList.AddLine(new Vector2(max.X - chevronW, midY - chevronH * 0.5f), new Vector2(max.X, midY), color, thickness);
			drawList.AddLine(new Vector2(max.X, midY), new Vector2(max.X - chevronW, midY + chevronH * 0.5f), color, thickness);
		}

		private static void DrawJsonGlyph(ImDrawListPtr drawList, Vector2 min, Vector2 max, float scale, Vector2 iconMin, Vector2 iconMax)
		{
			// Label uses PushFont(font, size)/PopFont: plain AddText renders at the fixed UI font
			// size, which is illegible at small icon sizes.
			var width = max.X - min.X;
			var height = max.Y - min.Y;

			var labelAreaHeight = height * 0.8f;
			var labelFontSize = MathF.Max(8f, labelAreaHeight * 0.50f);
			var labelColor = ImGui.GetColorU32(new Vector4(PanelBackground.X, PanelBackground.Y, PanelBackground.Z, 0.95f));

			ImGui.PushFont(ImGui.GetFont(), labelFontSize);
			const string label = "Json";
			// CalcTextSize must run while the pushed font is active or the metrics are wrong.
			var labelSize = ImGui.CalcTextSize(label);
			var labelPos = new Vector2(min.X + (width - labelSize.X) * 0.5f, min.Y + (labelFontSize - labelSize.Y) * 1.5f);

			// Ribbon spans the full icon rect (iconMin/iconMax), not the glyph content rect.
			var iconWidth = iconMax.X - iconMin.X;
			var ribbonOverhang = iconWidth * 0.08f;
			var ribbonThickness = MathF.Max(2f, 16f * scale);
			var ribbonGap = MathF.Max(1f, 2f * scale);
			var ribbonTop = labelPos.Y - ribbonGap;
			var ribbonMin = new Vector2(iconMin.X - ribbonOverhang, ribbonTop);
			var ribbonMax = new Vector2(iconMax.X + ribbonOverhang, ribbonTop + ribbonThickness);
			var ribbonColor = ImGui.GetColorU32(new Vector4(GenericAccent.X, GenericAccent.Y, GenericAccent.Z, 1f));
			drawList.AddRectFilled(ribbonMin, ribbonMax, ribbonColor, 2f);

			drawList.AddText(labelPos, labelColor, label);
			ImGui.PopFont();

			var color = ImGui.GetColorU32(JsonAccent);
			var thickness = MathF.Max(1.5f, 1.7f * scale);
			var braceWidth = width * 0.1f;
			var bracesTop = ribbonMax.Y + ribbonGap;
			var bracesHeight = MathF.Max(1f, max.Y - bracesTop);
			var bracesCenterY = bracesTop + bracesHeight * 0.5f;

			// "center" is the brace spine; the shape extends braceWidth toward the nub, so
			// centers are inset by braceWidth to keep nubs inside the glyph rect.
			DrawCurlyBrace(drawList, new Vector2(min.X + braceWidth, bracesCenterY), braceWidth, bracesHeight, color, thickness, mirrored: false);
			DrawCurlyBrace(drawList, new Vector2(max.X - braceWidth, bracesCenterY), braceWidth, bracesHeight, color, thickness, mirrored: true);

			var dotColor = ImGui.GetColorU32(new Vector4(JsonAccent.X, JsonAccent.Y, JsonAccent.Z, 0.9f));
			drawList.AddCircleFilled(new Vector2(min.X + width * 0.5f, bracesCenterY), MathF.Max(1.1f, 1.3f * scale), dotColor);
		}

		// Curly brace ("{" or, mirrored, "}") as two bezier halves meeting at a side nub.
		private static void DrawCurlyBrace(ImDrawListPtr drawList, Vector2 center, float width, float height, uint color, float thickness, bool mirrored)
		{
			var sign = mirrored ? -1f : 1f;
			var halfHeight = height * 0.5f;
			var top = new Vector2(center.X, center.Y - halfHeight);
			var bottom = new Vector2(center.X, center.Y + halfHeight);
			var nub = new Vector2(center.X - width * sign, center.Y);

			var kinkSpread = width * 0.05f;
			var approachDeltaY = halfHeight * 0.15f;

			var upperCtrl1 = new Vector2(top.X, top.Y + halfHeight);
			var upperCtrl2 = new Vector2(nub.X + kinkSpread * sign, nub.Y - approachDeltaY);

			var lowerCtrl1 = new Vector2(nub.X + kinkSpread * sign, nub.Y + approachDeltaY);
			var lowerCtrl2 = new Vector2(bottom.X, bottom.Y - halfHeight);

			var points = new List<Vector2> { top };
			AddBezierPoints(points, top, upperCtrl1, upperCtrl2, nub, 20);
			AddBezierPoints(points, nub, lowerCtrl1, lowerCtrl2, bottom, 20);

			var arr = points.ToArray();
			drawList.AddPolyline(ref arr[0], arr.Length, color, ImDrawFlags.None, thickness);
		}

		private static void DrawShaderGlyph(ImDrawListPtr drawList, Vector2 min, Vector2 max, float scale)
		{
			var color = ImGui.GetColorU32(ShaderAccent);
			var width = max.X - min.X;
			var height = max.Y - min.Y;

			var points = new[]
			{
				new Vector2(min.X + width * 0.58f, min.Y),
				new Vector2(min.X + width * 0.18f, min.Y + height * 0.58f),
				new Vector2(min.X + width * 0.46f, min.Y + height * 0.58f),
				new Vector2(min.X + width * 0.34f, min.Y + height),
				new Vector2(min.X + width * 0.82f, min.Y + height * 0.40f),
				new Vector2(min.X + width * 0.54f, min.Y + height * 0.40f),
			};

			drawList.AddConvexPolyFilled(ref points[0], points.Length, color);
		}

		private static void DrawAudioGlyph(ImDrawListPtr drawList, Vector2 min, Vector2 max, float scale)
		{
			var color = ImGui.GetColorU32(AudioAccent);
			var thickness = MathF.Max(1.6f, 1.8f * scale);
			var width = max.X - min.X;
			var height = max.Y - min.Y;

			var noteRadius = width * 0.14f;
			var noteCenter = new Vector2(min.X + noteRadius + 1f * scale, max.Y - noteRadius - 1f * scale);
			drawList.AddCircleFilled(noteCenter, noteRadius, color);

			var stemTop = new Vector2(noteCenter.X + noteRadius * 0.9f, min.Y);
			var stemBottom = new Vector2(noteCenter.X + noteRadius * 0.9f, noteCenter.Y);
			drawList.AddLine(stemBottom, stemTop, color, thickness);

			var flag = new[]
			{
				stemTop,
				new Vector2(stemTop.X + width * 0.28f, stemTop.Y + height * 0.16f),
				new Vector2(stemTop.X, stemTop.Y + height * 0.3f),
			};
			drawList.AddConvexPolyFilled(ref flag[0], flag.Length, color);
		}

		private static void DrawModelGlyph(ImDrawListPtr drawList, Vector2 min, Vector2 max, float scale)
		{
			// Wireframe pyramid; hidden back edges drawn dashed and slightly dimmed.
			var edgeColor = ImGui.GetColorU32(ModelAccent);
			var hiddenEdgeColor = ImGui.GetColorU32(ModelAccent * new Vector4(0.96f, 0.96f, 0.96f, 1.0f));
			var thickness = MathF.Max(1.2f, 1.3f * scale);

			var width = max.X - min.X;
			var height = max.Y - min.Y;
			var baseInset = width * 0.12f;

			var top = new Vector2(min.X + width * 0.5f, min.Y);
			var left = new Vector2(min.X + baseInset, min.Y + height * 0.75f);
			var right = new Vector2(max.X - baseInset, min.Y + height * 0.75f);
			var front = new Vector2(min.X + width * 0.5f, max.Y);
			var sideMid = new Vector2((left.X + right.X) * 0.5f, (left.Y + right.Y) * 0.5f);
			var back = Vector2.Lerp(sideMid, top, 0.2f);

			DrawDashedLine(drawList, back, top, hiddenEdgeColor, thickness, 2f * scale, 1f * scale);
			DrawDashedLine(drawList, back, left, hiddenEdgeColor, thickness, 2f * scale, 1f * scale);
			DrawDashedLine(drawList, back, right, hiddenEdgeColor, thickness, 2f * scale, 1f * scale);

			var outline = new[] { top, right, front, left };
			drawList.AddPolyline(ref outline[0], outline.Length, edgeColor, ImDrawFlags.Closed, thickness);
		}

		private static void DrawDashedLine(ImDrawListPtr drawList, Vector2 from, Vector2 to, uint color, float thickness, float dashLength, float gapLength)
		{
			var delta = to - from;
			var totalLength = delta.Length();
			if (totalLength < 0.001f)
			{
				return;
			}

			var direction = delta / totalLength;
			var step = dashLength + gapLength;
			var traveled = 0f;

			while (traveled < totalLength)
			{
				var dashEnd = MathF.Min(traveled + dashLength, totalLength);
				drawList.AddLine(from + direction * traveled, from + direction * dashEnd, color, thickness);
				traveled += step;
			}
		}


		private static void DrawMaterialGlyph(ImDrawListPtr drawList, Vector2 min, Vector2 max, float scale)
		{
			var color = ImGui.GetColorU32(MaterialAccent);
			// Highlight uses EditorPalette.Text so it contrasts on light themes too.
			var highlight = ImGui.GetColorU32(EditorPalette.WithAlpha(EditorPalette.Text, 0.5f));

			var width = max.X - min.X;
			var height = max.Y - min.Y;
			var center = new Vector2(min.X + width * 0.5f, min.Y + height * 0.5f);
			var radius = MathF.Min(width, height) * 0.42f;

			drawList.AddCircleFilled(center, radius, color);
			drawList.AddCircleFilled(center - new Vector2(radius * 0.35f, radius * 0.35f), radius * 0.28f, highlight);
		}

		private static void DrawSceneGlyph(ImDrawListPtr drawList, Vector2 min, Vector2 max, float scale)
		{
			var color = ImGui.GetColorU32(SceneAccent);
			var faint = ImGui.GetColorU32(new Vector4(SceneAccent.X, SceneAccent.Y, SceneAccent.Z, 0.45f));
			var height = max.Y - min.Y;

			var backOffset = new Vector2(0f, height * 0.18f);
			DrawDiamond(drawList, min + backOffset, max, faint);
			DrawDiamond(drawList, min, max - backOffset, color);

			static void DrawDiamond(ImDrawListPtr dl, Vector2 dMin, Vector2 dMax, uint fillColor)
			{
				var w = dMax.X - dMin.X;
				var h = dMax.Y - dMin.Y;
				var c = new Vector2(dMin.X + w * 0.5f, dMin.Y + h * 0.5f);
				var points = new[]
				{
					new Vector2(c.X, dMin.Y),
					new Vector2(dMax.X, c.Y),
					new Vector2(c.X, dMax.Y),
					new Vector2(dMin.X, c.Y),
				};
				dl.AddConvexPolyFilled(ref points[0], points.Length, fillColor);
			}
		}

		private static void DrawPrefabGlyph(ImDrawListPtr drawList, Vector2 min, Vector2 max, float scale)
		{
			// Hierarchy glyph (root + two children): a prefab is a saved entity hierarchy.
			var width = max.X - min.X;
			var height = max.Y - min.Y;

			var nodeSize = MathF.Min(width, height) * 0.36f;
			var rounding = MathF.Max(1f, nodeSize * 0.22f);
			var thickness = MathF.Max(1.1f, 1.2f * scale);

			var rootCenter = new Vector2(min.X + width * 0.5f, min.Y + nodeSize * 0.5f);
			var childCenterY = max.Y - nodeSize * 0.5f;
			var leftChildCenter = new Vector2(min.X + nodeSize * 0.5f, childCenterY);
			var rightChildCenter = new Vector2(max.X - nodeSize * 0.5f, childCenterY);
			var branchY = (rootCenter.Y + childCenterY) * 0.5f;

			var lineColor = ImGui.GetColorU32(Adjust(PrefabAccent, 0.7f));

			// Connectors first so node fills cover their ends.
			drawList.AddLine(new Vector2(rootCenter.X, rootCenter.Y + nodeSize * 0.5f), new Vector2(rootCenter.X, branchY), lineColor, thickness);
			drawList.AddLine(new Vector2(leftChildCenter.X, branchY), new Vector2(rightChildCenter.X, branchY), lineColor, thickness);
			drawList.AddLine(new Vector2(leftChildCenter.X, branchY), new Vector2(leftChildCenter.X, childCenterY - nodeSize * 0.5f), lineColor, thickness);
			drawList.AddLine(new Vector2(rightChildCenter.X, branchY), new Vector2(rightChildCenter.X, childCenterY - nodeSize * 0.5f), lineColor, thickness);

			var rootColor = ImGui.GetColorU32(Adjust(PrefabAccent, 1.2f));
			var childColor = ImGui.GetColorU32(Adjust(PrefabAccent, 0.78f));
			var borderColor = ImGui.GetColorU32(Adjust(PrefabAccent, 0.5f));
			var childSize = nodeSize * 0.8f;
			var childRounding = rounding * 0.85f;

			// Root drawn last so it sits on top.
			DrawNode(drawList, leftChildCenter, childSize, childRounding, childColor, borderColor, thickness * 0.85f);
			DrawNode(drawList, rightChildCenter, childSize, childRounding, childColor, borderColor, thickness * 0.85f);
			DrawNode(drawList, rootCenter, nodeSize, rounding, rootColor, borderColor, thickness * 0.9f);

			static void DrawNode(ImDrawListPtr dl, Vector2 center, float size, float rounding, uint fillColor, uint borderColor, float borderThickness)
			{
				var half = size * 0.5f;
				var nodeMin = center - new Vector2(half, half);
				var nodeMax = center + new Vector2(half, half);
				dl.AddRectFilled(nodeMin, nodeMax, fillColor, rounding);
				dl.AddRect(nodeMin, nodeMax, borderColor, rounding, ImDrawFlags.None, borderThickness);
			}

			static Vector4 Adjust(Vector4 color, float multiplier) => new(
				Math.Clamp(color.X * multiplier, 0f, 1f),
				Math.Clamp(color.Y * multiplier, 0f, 1f),
				Math.Clamp(color.Z * multiplier, 0f, 1f),
				color.W);
		}
	}
}
