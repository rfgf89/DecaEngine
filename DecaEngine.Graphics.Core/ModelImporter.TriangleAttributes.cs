using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using SharpGLTF.Schema2;
using DecaEngine.Core;
using DecaEngine.Graphics.Assets;
using System.Runtime.InteropServices;
using Diligent;
using MeshOptimizer;
using StbImageSharp;
using UnsafeCollections.Collections.Unsafe;
using DecaEngine.Animation;

namespace DecaEngine.Graphics;

/// <summary>Потриугольные атрибуты для probe GI: альбедо из текстур, тайлы, упаковка единичных векторов. Часть <see cref="ModelImporter"/> - CPU-стороны импорта; ФАЗА потребления (GPU-финализация) и
/// точки входа загрузки живут в <see cref="ModelLoader"/>.</summary>
public static partial class ModelImporter
{
	/// <summary>Линейное альбедо КАЖДОГО треугольника меша: base color текстуры в центроиде UV
	/// (точечная выборка с заворотом) x линейный фактор. Ключ - meshId; меши без текстуры/UV или
	/// без CPU-пикселей (стриминг, cooked-модель) пропускаются - потребитель падает на средний
	/// цвет материала (<see cref="MaterialPbrFactors.AverageBaseColor"/>). Стоимость - единицы
	/// миллисекунд на Sponza (одна выборка на треугольник) на фоне декода текстур.</summary>
	internal static void ComputeTriangleAlbedoFromTextures(ModelLoader result, PreparedModel prepared)
	{
		var materialByLogical = new Dictionary<int, PreparedMaterial>();
		foreach (var pm in prepared.Materials)
		{
			materialByLogical[pm.LogicalIndex] = pm;

			// Плитка альбедо материала - тем же проходом, пока CPU-пиксели живы.
			var tileSource = pm.BaseColorTexture;
			if (tileSource?.Pixels != null && tileSource.Width > 0 && tileSource.Height > 0 &&
				!result.MaterialAlbedoTile.ContainsKey(pm.LogicalIndex))
			{
				result.MaterialAlbedoTile[pm.LogicalIndex] = BuildAlbedoTile(tileSource);
			}
		}

		// COOKED-путь: пикселей нет, но атрибуты приехали из .dmdl готовыми - распаковываем и
		// выходим (см. PreparedModel.TriangleAttributes / EnsureTriangleAttributes).
		if (prepared.TriangleAttributes.Count > 0)
		{
			foreach (var (meshId, packed) in prepared.TriangleAttributes)
			{
				int count = packed.Length / 5;
				var albedoOut = new Vector3[count];
				var metalOut = new float[count];
				var roughOut = new float[count];
				for (int t = 0; t < count; t++)
				{
					int b = t * 5;
					albedoOut[t] = new Vector3(
						MathF.Pow(packed[b] / 255f, 2.2f),
						MathF.Pow(packed[b + 1] / 255f, 2.2f),
						MathF.Pow(packed[b + 2] / 255f, 2.2f));
					metalOut[t] = packed[b + 3] / 255f;
					roughOut[t] = packed[b + 4] / 255f;
				}

				result.TriangleAlbedo[meshId] = albedoOut;
				result.TriangleMetalness[meshId] = metalOut;
				result.TriangleRoughness[meshId] = roughOut;
			}

			return;
		}

		foreach (var inst in prepared.Instances)
		{
			if (inst.meshId < 0 || inst.meshId >= prepared.Meshes.Count ||
				result.TriangleAlbedo.ContainsKey(inst.meshId))
			{
				continue;
			}

			if (!materialByLogical.TryGetValue(inst.materialId, out var pm))
			{
				continue;
			}

			// Пикселей base color может не быть (стриминг/cooked) - это НЕ повод пропускать меш
			// целиком: потриугольная металличность/шероховатость берётся из СВОЕЙ текстуры (ниже),
			// а альбедо тогда честно падает на средний цвет материала.
			var texture = pm.BaseColorTexture;
			bool hasBasePixels = texture?.Pixels != null && texture.Width > 0 && texture.Height > 0;

			var mesh = prepared.Meshes[inst.meshId];
			if (!mesh.HasUv || mesh.Vertices == null || mesh.Indices == null || mesh.Indices.Length < 3)
			{
				continue;
			}

			// Средний цвет материала - фолбэк альбедо, когда пикселей нет (тот же источник, что у
			// потребителя: MaterialPbrFactors.AverageBaseColor).
			var factor = hasBasePixels
				? new Vector3(pm.BaseColorFactor.X, pm.BaseColorFactor.Y, pm.BaseColorFactor.Z)
				: new Vector3(ComputeAverageBaseColor(pm).X, ComputeAverageBaseColor(pm).Y,
					ComputeAverageBaseColor(pm).Z);
			int triCount = mesh.Indices.Length / 3;
			var albedo = new Vector3[triCount];

			// Буфер выборок - ОДИН на меш: stackalloc внутри цикла по треугольникам копит стек
			// (кадр метода не освобождается до выхода) и на модели уровня Sponza его срывает.
			Span<Vector2> taps = stackalloc Vector2[7];

			// Металличность - тем же проходом (те же центроиды UV), из B-канала MR-текстуры
			// (glTF: G - roughness, B - metallic; данные ЛИНЕЙНЫЕ, без sRGB-декода).
			var mrTexture = pm.MetallicRoughnessTexture;

			// Пикселей MR-текстуры нет (стриминг/cooked), а материал ПОТЕНЦИАЛЬНО металлический
			// (фактор > 0.5 - у glTF-материалов с MR-текстурой он по умолчанию 1): декодируем её
			// МЕЛКО, только ради потриугольных метал/шероховатости. Без этого сцена со стримингом
			// получала фолбэк «фактор = 1» по обоим каналам, то есть «весь материал - шершавый
			// металл»: цепочка отскоков RT-отражений не запускалась НИКОГДА (диагностика -
			// отладочный вид «RT bounce chain»: сплошь зелёный). Стоимость ограничена: декод
			// идёт только у металлических материалов и в 256px.
			// Пиксели - в ЛОКАЛЬНЫХ переменных, а не в PreparedTexture: тот же экземпляр может уйти
			// в печку ассетов, и подмена его пикселей мелким декодом запекла бы в .dtex 256px.
			var mrPixels = mrTexture?.Pixels;
			int mrWidth = mrTexture?.Width ?? 0;
			int mrHeight = mrTexture?.Height ?? 0;

			if (mrPixels == null && mrTexture?.StreamSource != null && pm.MetallicFactor > 0.5f)
			{
				try
				{
					var encoded = mrTexture.StreamSource.EncodedBytes
						?? (mrTexture.StreamSource.FilePath != null && File.Exists(mrTexture.StreamSource.FilePath)
							? File.ReadAllBytes(mrTexture.StreamSource.FilePath)
							: null);
					if (encoded != null)
					{
						var levels = DecodeEncodedImageLadder(encoded, 256, 256, 2);
						if (levels.Count > 0)
						{
							var top = levels[levels.Count - 1];
							mrPixels = top.Pixels;
							mrWidth = top.Width;
							mrHeight = top.Height;
						}
					}
				}
				catch (Exception)
				{
					// Декод - оптимизация качества отражений, а не источник правды: не вышло -
					// молча падаем на факторы материала.
				}
			}

			bool hasMrPixels = mrPixels != null && mrWidth > 0 && mrHeight > 0;
			var metalness = hasMrPixels ? new float[triCount] : null;
			var roughness = hasMrPixels ? new float[triCount] : null;

			for (int t = 0; t < triCount; t++)
			{
				uint i0 = mesh.Indices[t * 3], i1 = mesh.Indices[t * 3 + 1], i2 = mesh.Indices[t * 3 + 2];
				if (i0 >= mesh.Vertices.Length || i1 >= mesh.Vertices.Length || i2 >= mesh.Vertices.Length)
				{
					albedo[t] = factor;
					if (metalness != null)
					{
						metalness[t] = pm.MetallicFactor;
						roughness![t] = pm.RoughnessFactor;
					}
					continue;
				}

				// СЕМЬ точек по треугольнику вместо одного центроида: центр, вершины (поджатые
				// внутрь) и середины рёбер. Одна выборка ловит шум текстуры - в MR-картах
				// реальных ассетов канал металличности «крапчатый», и у отдельных треугольников
				// неметаллической ткани центроид попадал в тексель 0.6+, что в RT-отражениях
				// читалось выбросами по треугольникам. Усреднение убирает крапинки, не размывая
				// крупные детали (внутри треугольника цвет всё равно один).
				var uvA = mesh.Vertices[i0].TexCoord;
				var uvB = mesh.Vertices[i1].TexCoord;
				var uvC = mesh.Vertices[i2].TexCoord;
				var uvCentroid = (uvA + uvB + uvC) / 3f;
				taps[0] = uvCentroid;
				taps[1] = Vector2.Lerp(uvA, uvCentroid, 0.25f);
				taps[2] = Vector2.Lerp(uvB, uvCentroid, 0.25f);
				taps[3] = Vector2.Lerp(uvC, uvCentroid, 0.25f);
				taps[4] = Vector2.Lerp((uvA + uvB) * 0.5f, uvCentroid, 0.25f);
				taps[5] = Vector2.Lerp((uvB + uvC) * 0.5f, uvCentroid, 0.25f);
				taps[6] = Vector2.Lerp((uvC + uvA) * 0.5f, uvCentroid, 0.25f);

				var albedoSum = Vector3.Zero;
				float metalSum = 0f, roughSum = 0f;
				int albedoTaps = 0, mrTaps = 0;

				foreach (var tap in taps)
				{
					// Заворот UV как у Wrap-сэмплера (отрицательные тоже).
					float u = tap.X - MathF.Floor(tap.X);
					float v = tap.Y - MathF.Floor(tap.Y);

					if (hasBasePixels)
					{
						int px = Math.Clamp((int)(u * texture!.Width), 0, texture.Width - 1);
						int py = Math.Clamp((int)(v * texture.Height), 0, texture.Height - 1);
						int idx = (py * texture.Width + px) * 4;
						if (idx + 2 < texture.Pixels!.Length)
						{
							// sRGB -> linear тем же pow(2.2), что и шейдер (см. UnlitInstancedPS.hlsl).
							albedoSum += new Vector3(
								MathF.Pow(texture.Pixels[idx] / 255f, 2.2f),
								MathF.Pow(texture.Pixels[idx + 1] / 255f, 2.2f),
								MathF.Pow(texture.Pixels[idx + 2] / 255f, 2.2f));
							albedoTaps++;
						}
					}

					if (metalness != null)
					{
						int mx = Math.Clamp((int)(u * mrWidth), 0, mrWidth - 1);
						int my = Math.Clamp((int)(v * mrHeight), 0, mrHeight - 1);
						int mBase = (my * mrWidth + mx) * 4;
						if (mBase + 2 < mrPixels!.Length)
						{
							// glTF-упаковка: G - roughness, B - metallic; данные линейные.
							metalSum += mrPixels[mBase + 2] / 255f;
							roughSum += mrPixels[mBase + 1] / 255f;
							mrTaps++;
						}
					}
				}

				albedo[t] = albedoTaps > 0 ? albedoSum / albedoTaps * factor : factor;

				if (metalness != null)
				{
					metalness[t] = mrTaps > 0 ? metalSum / mrTaps * pm.MetallicFactor : pm.MetallicFactor;
					roughness![t] = mrTaps > 0 ? roughSum / mrTaps * pm.RoughnessFactor : pm.RoughnessFactor;
				}
			}

			result.TriangleAlbedo[inst.meshId] = albedo;
			if (metalness != null)
			{
				result.TriangleMetalness[inst.meshId] = metalness;
				result.TriangleRoughness[inst.meshId] = roughness!;
			}
		}
	}

	/// <summary>Считает <see cref="PreparedModel.TriangleAttributes"/> - упакованные потриугольные
	/// альбедо/металличность/шероховатость - ПОКА ЖИВЫ ПИКСЕЛИ текстур. Зовётся печкой ассетов
	/// перед записью .dmdl: у cooked-модели пикселей нет, и без этого блока RT-отражения теряли и
	/// текстурный цвет хитов, и материал (цепочка отскоков не запускалась - «металла в сцене
	/// нет»). Побочный эффект осознан: на модель уходит 5 байт на треугольник в кеше.</summary>
	internal static void EnsureTriangleAttributes(PreparedModel prepared)
	{
		if (prepared.TriangleAttributes.Count > 0)
		{
			return;
		}

		// Считаем тем же кодом, что и на обычной загрузке, - через временный контейнер.
		var scratch = new ModelLoader();
		ComputeTriangleAlbedoFromTextures(scratch, prepared);

		foreach (var (meshId, albedo) in scratch.TriangleAlbedo)
		{
			scratch.TriangleMetalness.TryGetValue(meshId, out var metal);
			scratch.TriangleRoughness.TryGetValue(meshId, out var rough);

			var packed = new byte[albedo.Length * 5];
			for (int t = 0; t < albedo.Length; t++)
			{
				int b = t * 5;
				packed[b] = EncodeUnitSrgb(albedo[t].X);
				packed[b + 1] = EncodeUnitSrgb(albedo[t].Y);
				packed[b + 2] = EncodeUnitSrgb(albedo[t].Z);
				packed[b + 3] = EncodeUnit(metal != null && t < metal.Length ? metal[t] : 0f);
				packed[b + 4] = EncodeUnit(rough != null && t < rough.Length ? rough[t] : 1f);
			}

			prepared.TriangleAttributes[meshId] = packed;
		}
	}

	private static byte EncodeUnit(float value) =>
		(byte)Math.Clamp((int)(Math.Clamp(value, 0f, 1f) * 255f + 0.5f), 0, 255);

	private static byte EncodeUnitSrgb(float linear) =>
		EncodeUnit(MathF.Pow(Math.Clamp(linear, 0f, 1f), 1f / 2.2f));

	/// <summary>Бокс-даунсемпл base color текстуры в плитку <see cref="ModelLoader.AlbedoTileSize"/>² (см.
	/// <see cref="MaterialAlbedoTile"/>). Усреднение в линейном пространстве, но по РАЗРЕЖЕННОЙ
	/// сетке (до 4x4 сэмплов на тексель плитки, как stride у ComputeAverageBaseColor): полный
	/// проход по 2К-текстуре стоил бы сотни миллионов выборок на модель, а плитке 128² больше
	/// точности и не нужно.</summary>
	private static byte[] BuildAlbedoTile(PreparedTexture texture)
	{
		const int size = ModelLoader.AlbedoTileSize;

		// sRGB -> linear через таблицу: pow на каждый сэмпл - главная цена всего прохода.
		Span<float> toLinear = stackalloc float[256];
		for (int i = 0; i < 256; i++)
		{
			toLinear[i] = MathF.Pow(i / 255f, 2.2f);
		}

		var tile = new byte[size * size * 4];
		var pixels = texture.Pixels!;
		for (int ty = 0; ty < size; ty++)
		{
			int y0 = (int)((long)ty * texture.Height / size);
			int y1 = Math.Max(y0 + 1, (int)((long)(ty + 1) * texture.Height / size));
			int strideY = Math.Max(1, (y1 - y0) / 4);
			for (int tx = 0; tx < size; tx++)
			{
				int x0 = (int)((long)tx * texture.Width / size);
				int x1 = Math.Max(x0 + 1, (int)((long)(tx + 1) * texture.Width / size));
				int strideX = Math.Max(1, (x1 - x0) / 4);

				float r = 0f, g = 0f, b = 0f;
				int count = 0;
				for (int y = y0; y < y1; y += strideY)
				{
					int row = y * texture.Width;
					for (int x = x0; x < x1; x += strideX)
					{
						int idx = (row + x) * 4;
						if (idx + 2 >= pixels.Length)
						{
							continue;
						}

						r += toLinear[pixels[idx]];
						g += toLinear[pixels[idx + 1]];
						b += toLinear[pixels[idx + 2]];
						count++;
					}
				}

				int outIdx = (ty * size + tx) * 4;
				if (count > 0)
				{
					float inv = 1f / count;
					tile[outIdx] = (byte)Math.Clamp((int)(MathF.Pow(r * inv, 1f / 2.2f) * 255f + 0.5f), 0, 255);
					tile[outIdx + 1] = (byte)Math.Clamp((int)(MathF.Pow(g * inv, 1f / 2.2f) * 255f + 0.5f), 0, 255);
					tile[outIdx + 2] = (byte)Math.Clamp((int)(MathF.Pow(b * inv, 1f / 2.2f) * 255f + 0.5f), 0, 255);
				}

				tile[outIdx + 3] = 255;
			}
		}

		return tile;
	}

}
