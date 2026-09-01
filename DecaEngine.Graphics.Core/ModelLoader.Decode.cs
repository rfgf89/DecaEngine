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

/// <summary>Декод и вспомогательное: чтение glTF-корня, декод текстур и лестниц качества, средний base color. Часть <see cref="ModelLoader"/> - файл на фазу; состояние,
/// точки входа загрузки и Release живут в основном файле.</summary>
public partial class ModelLoader
{
	/// <summary>Фоллбек для узлов, чья мировая матрица не раскладывается в TRS (shear от родительского
	/// поворота поверх неравномерного масштаба - Matrix4x4.Decompose возвращает false): матрица
	/// запекается прямо в копию вершин, инстанс получает identity-трансформ. Матрица приходит в
	/// RH-конвенции glTF и переводится в LH движка сопряжением M*W*M (M = diag(1,1,-1)) - вершины
	/// исходного меша уже отзеркалены по Z при чтении атрибутов.</summary>
	private static int BakeMeshWithMatrix(PreparedModel prepared, int meshId, Matrix4x4 worldRh)
	{
		var source = prepared.Meshes[meshId];
		var mirrorZ = Matrix4x4.CreateScale(1f, 1f, -1f);
		var world = mirrorZ * worldRh * mirrorZ;

		// Нормали - через inverse-transpose: под неравномерным масштабом/сдвигом прямое умножение
		// уводит их с перпендикуляра к поверхности.
		Matrix4x4.Invert(world, out var inverse);
		var normalMatrix = Matrix4x4.Transpose(inverse);

		var vertices = new Vertex[source.Vertices.Length];
		var min = new Vector3(float.MaxValue);
		var max = new Vector3(float.MinValue);
		for (int i = 0; i < vertices.Length; i++)
		{
			var vertex = source.Vertices[i];
			vertex.Position = Vector3.Transform(vertex.Position, world);
			vertex.Normal = SafeNormalize(Vector3.TransformNormal(vertex.Normal, normalMatrix));
			var tangent = SafeNormalize(Vector3.TransformNormal(
				new Vector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z), world));
			vertex.Tangent = new Vector4(tangent, vertex.Tangent.W);
			vertices[i] = vertex;
			min = Vector3.Min(min, vertex.Position);
			max = Vector3.Max(max, vertex.Position);
		}

		// Зеркалящая матрица (отрицательный детерминант) обращает обход треугольников - без
		// инверсии индексов culling выворачивает геометрию наизнанку. Свап покрывает и LOD-ы:
		// их LodLevel-ы - диапазоны в этом же индекс-буфере. Знак битангента флипается по той же
		// причине, что при базовом Z-зеркалировании (см. Vertex.Tangent).
		var indices = source.Indices;
		if (world.GetDeterminant() < 0f && source.Topology == MeshTopologyTriangles)
		{
			indices = (uint[])indices.Clone();
			for (int i = 0; i + 2 < indices.Length; i += 3)
			{
				(indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
			}
			for (int i = 0; i < vertices.Length; i++)
			{
				vertices[i].Tangent.W = -vertices[i].Tangent.W;
			}
		}

		prepared.Meshes.Add(new PreparedMesh
		{
			Name = source.Name + " (baked transform)",
			Vertices = vertices,
			Indices = indices,
			LodLevels = source.LodLevels,
			BoundsCenter = (min + max) * 0.5f,
			BoundsRadius = MathF.Max(0.0001f, (max - min).Length() * 0.5f),
			HasUv = source.HasUv,
			Topology = source.Topology,
		});
		return prepared.Meshes.Count - 1;
	}

	private static Vector3 SafeNormalize(Vector3 v)
	{
		return v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : v;
	}

	/// <summary>Собирает PreparedTexture из заранее декодированных пикселей image (параллельный декод
	/// в начале PrepareModel; кэш заодно дедуплицирует image, разделяемый несколькими
	/// материалами/каналами - пиксельный массив шарится, дальше он только читается) + настроек
	/// сэмплера. Сэмплер в glTF опционален (нет - значит wrap + linear по спеке): WaterBottle и
	/// другие Khronos-семплы без явных сэмплеров роняли загрузку NRE.</summary>
	private static PreparedTexture DecodeTexture(SharpGLTF.Schema2.Texture texture, int maxSize,
		Dictionary<SharpGLTF.Schema2.Image, (byte[] Pixels, int Width, int Height)> decodedImages,
		Dictionary<SharpGLTF.Schema2.Image, TextureStreamSource> streamSources,
		Dictionary<int, string> externalImagePaths = null)
	{
		var sampler = texture.Sampler;
		var prepared = new PreparedTexture
		{
			AddressMode = sampler != null ? ToAddressMode(sampler.WrapS) : TextureAddress.Wrap,
			FilterMode = sampler != null ? ToFilter(sampler.MinFilter, sampler.MagFilter) : TextureFilter.Linear,
			SourceImage = texture.PrimaryImage,
		};

		if (streamSources != null)
		{
			// Стриминг: пикселей на этой фазе нет вовсе - слот получит 1x1-филлер, а первая ступень
			// приедет из ModelStreamer. Страховка на канал, не учтённый пре-сбором usedImages.
			if (!streamSources.TryGetValue(texture.PrimaryImage, out var streamSource))
			{
				streamSource = CreateStreamSource(texture.PrimaryImage, externalImagePaths);
				streamSources[texture.PrimaryImage] = streamSource;
			}

			prepared.StreamSource = streamSource;
			return prepared;
		}

		if (!decodedImages.TryGetValue(texture.PrimaryImage, out var decoded))
		{
			// Страховка: канал, не учтённый пре-сбором usedImages, декодируется на месте.
			decoded = DecodeImagePixels(texture.PrimaryImage, maxSize);
			decodedImages[texture.PrimaryImage] = decoded;
		}

		prepared.Pixels = decoded.Pixels;
		prepared.Width = decoded.Width;
		prepared.Height = decoded.Height;
		return prepared;
	}

	/// <summary>Источник ре-декодов для стриминга: путь к ВНЕШНЕМУ файлу картинки, если он известен
	/// (типовая .gltf-сцена - папка с PNG рядом), иначе копия встроенных байт (.glb / data-URI).
	/// Путь предпочтительнее ровно по памяти: у Sponza сотни 4K-исходников, и держать их все в
	/// managed-куче всю сессию - гигабайты на ровном месте.</summary>
	private static TextureStreamSource CreateStreamSource(SharpGLTF.Schema2.Image image,
		Dictionary<int, string> externalImagePaths)
	{
		// Внешний файл, чьё чтение мы подменили заглушкой при парсинге (см. LoadModelRoot): в
		// памяти его нет вовсе, читаем с диска в момент апгрейда.
		if (externalImagePaths != null && externalImagePaths.TryGetValue(image.LogicalIndex, out var path))
		{
			return new TextureStreamSource { FilePath = path };
		}

		var sourcePath = image.Content.SourcePath;
		if (!string.IsNullOrEmpty(sourcePath) && File.Exists(sourcePath))
		{
			return new TextureStreamSource { FilePath = sourcePath };
		}

		// Встроенная картинка (.glb / data-URI / bufferView) - её байты и так уже в памяти модели.
		return new TextureStreamSource { EncodedBytes = image.Content.Content.ToArray() };
	}

	/// <summary>Минимальный валидный PNG 1x1 - заглушка вместо реального содержимого внешних
	/// картинок при стриминге (см. <see cref="LoadModelRoot"/>).</summary>
	private static readonly byte[] StubPng = Convert.FromBase64String(
		"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

	/// <summary>
	/// Парсит glTF. В обычном режиме - как раньше. В режиме стриминга внешние файлы картинок НЕ
	/// ЧИТАЮТСЯ ВОВСЕ: их содержимое подменяется 1x1-заглушкой, а на выход отдаётся карта
	/// «логический индекс image -> путь к файлу», по которой стример читает нужную картинку с диска
	/// в момент, когда она реально понадобилась материалу.
	///
	/// Это и была главная причина «сцена пустая, редактор висит две минуты»: SharpGLTF грузит
	/// содержимое КАЖДОГО image при разборе документа, то есть Sponza затягивала в managed-кучу все
	/// свои сотни мегабайт (а с Intel-версией - гигабайты) PNG ещё до того, как появлялась хоть
	/// одна вершина, - и всё это до единого байта тут же становилось мусором, потому что декод
	/// текстур в этой фазе уже не делается.
	/// </summary>
	private static ModelRoot LoadModelRoot(string modelPath, ModelLoadOptions options,
		out Dictionary<int, string> externalImagePaths)
	{
		externalImagePaths = null;

		var settings = new ReadSettings { Validation = SharpGLTF.Validation.ValidationMode.TryFix };

		// Только для текстового .gltf: у .glb картинки лежат внутри самого файла, подменять нечего.
		if (!options.StreamTextures ||
			!string.Equals(Path.GetExtension(modelPath), ".gltf", StringComparison.OrdinalIgnoreCase))
		{
			return ModelRoot.Load(modelPath, settings);
		}

		// URI картинок берём из JSON напрямую: порядок элементов "images" совпадает с
		// ModelRoot.LogicalImages, а разбирать их через SharpGLTF мы как раз и не хотим.
		var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(modelPath)) ?? Environment.CurrentDirectory;
		var pathsByIndex = new Dictionary<int, string>();
		var stubbedUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		try
		{
			using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(modelPath));
			if (json.RootElement.TryGetProperty("images", out var images) &&
				images.ValueKind == System.Text.Json.JsonValueKind.Array)
			{
				int index = 0;
				foreach (var image in images.EnumerateArray())
				{
					if (image.TryGetProperty("uri", out var uriElement) &&
						uriElement.GetString() is { Length: > 0 } uri &&
						!uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
					{
						var relative = Uri.UnescapeDataString(uri).Replace('/', Path.DirectorySeparatorChar);
						var fullPath = Path.Combine(baseDirectory, relative);
						if (File.Exists(fullPath))
						{
							pathsByIndex[index] = fullPath;
							stubbedUris.Add(uri);
							stubbedUris.Add(Uri.UnescapeDataString(uri));
						}
					}

					index++;
				}
			}
		}
		catch (Exception)
		{
			// Не разобрали JSON сами - просто грузим обычным путём (медленно, но верно).
			return ModelRoot.Load(modelPath, settings);
		}

		if (pathsByIndex.Count == 0)
		{
			return ModelRoot.Load(modelPath, settings);
		}

		var context = ReadContext
			.Create(uri =>
			{
				if (stubbedUris.Contains(uri))
				{
					return new ArraySegment<byte>(StubPng);
				}

				var candidate = Path.Combine(baseDirectory, Uri.UnescapeDataString(uri)
					.Replace('/', Path.DirectorySeparatorChar));
				if (!File.Exists(candidate))
				{
					candidate = Path.Combine(baseDirectory, uri);
				}

				return new ArraySegment<byte>(File.ReadAllBytes(candidate));
			})
			.WithSettingsFrom(settings);

		externalImagePaths = pathsByIndex;
		return context.ReadSchema2(Path.GetFileName(modelPath));
	}

	/// <summary>Декодирование картинки (PNG/JPG) + даунскейл до <paramref name="maxSize"/> (см.
	/// ModelLoadOptions.MaxTextureSize). Чистый CPU без разделяемого состояния - зовётся из
	/// Parallel.For в PrepareModel.</summary>
	private static (byte[] Pixels, int Width, int Height) DecodeImagePixels(SharpGLTF.Schema2.Image image, int maxSize)
		=> DecodeEncodedImage(image.Content.Content.ToArray(), maxSize);

	/// <summary>Декод сжатой картинки (PNG/JPG) с даунскейлом до <paramref name="maxSize"/> (0 = без
	/// лимита). Публичный - им же фоновые апгрейды стрим-текстур ре-декодируют сохранённые исходники
	/// (см. <see cref="StreamedTextures"/>). Чистый CPU без разделяемого состояния - безопасен из
	/// любого потока; учти, что декод идёт в ПОЛНОМ разрешении файла и только потом ужимается (stb
	/// иначе не умеет) - пиковая память по одной задаче на 4K-исходнике ~64 МБ.</summary>
	public static (byte[] Pixels, int Width, int Height) DecodeEncodedImage(byte[] encodedBytes, int maxSize)
	{
		var decoded = ImageResult.FromMemory(encodedBytes, ColorComponents.RedGreenBlueAlpha);

		var pixels = decoded.Data;
		int width = decoded.Width;
		int height = decoded.Height;
		while (maxSize > 0 && (width > maxSize || height > maxSize))
		{
			(pixels, width, height) = DownscaleHalf(pixels, width, height);
		}

		return (pixels, width, height);
	}

	/// <summary>
	/// Декод сжатой картинки СРАЗУ ВСЕЙ ЛЕСТНИЦЕЙ качества - от <paramref name="firstSize"/> до
	/// <paramref name="maxSize"/> с шагом <paramref name="stepFactor"/> (степени двойки), в порядке
	/// ВОЗРАСТАНИЯ. Существует ради прогрессивного стриминга (см. DecaEngine.Editor.ECS.ModelStore):
	/// stb декодирует файл только в полном разрешении, поэтому ступень "64px" стоит ровно столько же,
	/// сколько полный декод - и лестница из четырёх ступеней раньше означала ЧЕТЫРЕ полных декода
	/// одного и того же файла. Здесь файл декодируется РОВНО ОДИН РАЗ, а ступени снимаются с той же
	/// цепочки половинных даунскейлов, которую даунскейл до целевого размера и так проходит: младшие
	/// ступени достаются практически даром.
	///
	/// Пустой список - декодировать нечего. Уровни отдаются отдельными массивами: потребитель заливает
	/// их по одному, начиная с самого маленького (модель появляется в кадре сразу), и держит остаток в
	/// памяти до заливки - см. ModelStore.PendingDecodeBytesBudget про потолок этого остатка.
	/// </summary>
	public static List<(byte[] Pixels, int Width, int Height)> DecodeEncodedImageLadder(
		byte[] encodedBytes, int maxSize, int firstSize, int stepFactor)
	{
		var decoded = ImageResult.FromMemory(encodedBytes, ColorComponents.RedGreenBlueAlpha);

		var pixels = decoded.Data;
		int width = decoded.Width;
		int height = decoded.Height;
		while (maxSize > 0 && (width > maxSize || height > maxSize))
		{
			(pixels, width, height) = DownscaleHalf(pixels, width, height);
		}

		// Верхняя ступень - то, что получилось после даунскейла до потолка; ниже неё идут ступени,
		// каждая в stepFactor раз мельче, пока не пройдена firstSize. Порядок в списке - по
		// возрастанию, поэтому собираем с конца.
		var levels = new List<(byte[] Pixels, int Width, int Height)> { (pixels, width, height) };
		var halvings = 1;
		for (int step = Math.Max(2, stepFactor); step > 2; step >>= 1)
		{
			halvings++;
		}

		while (firstSize > 0 && Math.Max(width, height) > firstSize)
		{
			for (int i = 0; i < halvings && Math.Max(width, height) > 1; i++)
			{
				(pixels, width, height) = DownscaleHalf(pixels, width, height);
			}

			levels.Add((pixels, width, height));

			if (Math.Max(width, height) <= 1)
			{
				break;
			}
		}

		levels.Reverse();
		return levels;
	}

	/// <summary>Бокс-фильтр 2x2 в один шаг вдвое - то же усреднение, что GPU GenerateMips, поэтому
	/// картинка после даунскейла совпадает с тем, что сэмплер и так показал бы на этом мипе.
	/// Нечётные размеры клампятся к краю (последние строка/столбец усредняются сами с собой).</summary>
	private static (byte[] pixels, int width, int height) DownscaleHalf(byte[] pixels, int width, int height)
	{
		int newWidth = Math.Max(1, width / 2);
		int newHeight = Math.Max(1, height / 2);
		var result = new byte[newWidth * newHeight * 4];

		for (int y = 0; y < newHeight; y++)
		{
			int srcY0 = Math.Min(height - 1, y * 2);
			int srcY1 = Math.Min(height - 1, y * 2 + 1);
			for (int x = 0; x < newWidth; x++)
			{
				int srcX0 = Math.Min(width - 1, x * 2);
				int srcX1 = Math.Min(width - 1, x * 2 + 1);
				int p00 = (srcY0 * width + srcX0) * 4;
				int p01 = (srcY0 * width + srcX1) * 4;
				int p10 = (srcY1 * width + srcX0) * 4;
				int p11 = (srcY1 * width + srcX1) * 4;
				int dst = (y * newWidth + x) * 4;
				for (int c = 0; c < 4; c++)
				{
					result[dst + c] = (byte)((pixels[p00 + c] + pixels[p01 + c] + pixels[p10 + c] + pixels[p11 + c] + 2) >> 2);
				}
			}
		}

		return (result, newWidth, newHeight);
	}

	/// <summary>Среднее линейное альбедо материала для <see cref="MaterialPbrFactors.AverageBaseColor"/>:
	/// разреженное среднее по base color текстуре (sRGB → linear), умноженное на линейный фактор.
	/// Без текстуры - просто фактор. Альфа (линейная, без sRGB) уходит в
	/// <see cref="MaterialPbrFactors.AverageAlpha"/> - по ней probe-GI бейкер отличает реально
	/// «дырявые» материалы (листва/трава/решётки, средняя альфа мала) от сплошных, которые
	/// экспортер зачем-то пометил MASK/BLEND (камень с альфой ~1) - см. ProbeGiBaker.</summary>
	private static Vector4 ComputeAverageBaseColor(PreparedMaterial pm)
	{
		EnsureAverageBaseColor(pm);
		return pm.AverageBaseColorRgba.Value;
	}

	/// <summary>Считает <see cref="PreparedMaterial.AverageBaseColorRgba"/>, если он ещё не посчитан.
	/// Вызывать ОБЯЗАТЕЛЬНО пока живы пиксели base color: и при обычной загрузке (лениво, из
	/// BuildFactors), и перед записью .dmdl - у печки свой экземпляр PreparedModel, который через
	/// финализацию не проходит, так что лениво он бы остался пустым и в кеш уехал бы фактор.</summary>
	internal static void EnsureAverageBaseColor(PreparedMaterial pm)
	{
		if (pm.AverageBaseColorRgba.HasValue)
		{
			return;
		}

		pm.AverageBaseColorRgba = ComputeAverageBaseColorCore(pm);
		pm.SoftAlphaFraction = ComputeSoftAlphaFraction(pm);
	}

	/// <summary>Доля текселей base color с «промежуточной» альфой (0.1..0.9) - насколько альфа-канал
	/// БИНАРЕН.
	///
	/// Отвечает на вопрос, который alphaMode не решает: у экспортов сплошь и листва, и накладные
	/// декали помечены одним и тем же BLEND (Intel Sponza: LeafSpring, dirt_decal - все BLEND), а
	/// вести себя в тени они обязаны противоположно. Листва - вырезка: альфа почти везде 0 или 1,
	/// бинарная тень по ней осмысленна и нужна. Декаль грязи - мягкая размазка по всему диапазону,
	/// бинарной тени у неё быть не может в принципе, и любая попытка её отбросить даёт тёмную кляксу
	/// формы своей же текстуры на стене, к которой декаль приклеена.
	///
	/// -1 = не считалось (пикселей не было).</summary>
	private static float ComputeSoftAlphaFraction(PreparedMaterial pm)
	{
		var texture = pm.BaseColorTexture;
		if (texture?.Pixels == null || texture.Width <= 0 || texture.Height <= 0)
		{
			return -1f;
		}

		int pixelCount = texture.Width * texture.Height;
		int stride = Math.Max(1, pixelCount / 4096);
		int soft = 0;
		int count = 0;
		for (int i = 0; i < pixelCount; i += stride)
		{
			int idx = i * 4;
			if (idx + 3 >= texture.Pixels.Length)
			{
				break;
			}

			float a = texture.Pixels[idx + 3] / 255f;
			if (a > 0.1f && a < 0.9f)
			{
				soft++;
			}

			count++;
		}

		return count > 0 ? (float)soft / count : -1f;
	}

	private static Vector4 ComputeAverageBaseColorCore(PreparedMaterial pm)
	{
		var factor = new Vector3(pm.BaseColorFactor.X, pm.BaseColorFactor.Y, pm.BaseColorFactor.Z);
		var texture = pm.BaseColorTexture;
		if (texture?.Pixels == null || texture.Width <= 0 || texture.Height <= 0)
		{
			// Пикселей нет. Два разных случая, и путать их нельзя:
			//
			// 1. Текстуры у слота нет вовсе - материал целиком описан фактором, среднее и есть фактор.
			//
			// 2. Текстура ЕСТЬ, но пикселей нет: режим стриминга (см. ModelLoadOptions.StreamTextures -
			//    им грузит Scene View) при ПРОМАХЕ кеша, то есть пока фоновый бейк не положил .dmdl со
			//    средним. Здесь фактор - не ответ, а тихая ложь: у glTF-материалов он почти всегда
			//    (1,1,1,1), то есть альфа выходит единицей, и по ней отбор «дырявой» геометрии
			//    (AverageAlpha < 0.6, см. ModelViewportEnvironment и ProbeGi) молча выключается. Плата
			//    за это - альфа-тест в тени пропадает у ВСЕЙ MASK/BLEND-геометрии: занавеси и накладные
			//    планки грязи/потёков Intel Sponza начинают отбрасывать тень СПЛОШНЫМ квадратом, что на
			//    стене читается крупными гладкими кляксами.
			//
			//    Поэтому неизвестная альфа объявляется НУЛЁМ - то есть «считать дырявым», - и только у
			//    материалов, которые glTF пометил MASK/BLEND (AlphaCutoff > 0). Цена ошибки в эту
			//    сторону - лишний дроу-колл на каскад у пары материалов, пока не приехал бейк; в
			//    обратную - тот самый сплошной квад в тени. RGB при этом остаётся фактором: его
			//    альфа-режим не касается, а по нему красит баунс probe-GI.
			float unknownAlpha = texture != null && pm.AlphaCutoff > 0f ? 0f : pm.BaseColorFactor.W;
			return new Vector4(factor, unknownAlpha);
		}

		// Каждый ~16-й пиксель: среднему хватает, а гигантские атласы не тормозят загрузку.
		int pixelCount = texture.Width * texture.Height;
		int stride = Math.Max(1, pixelCount / 4096);
		var sum = Vector3.Zero;
		float alphaSum = 0f;
		int count = 0;
		for (int i = 0; i < pixelCount; i += stride)
		{
			int idx = i * 4;
			if (idx + 3 >= texture.Pixels.Length)
			{
				break;
			}

			// sRGB → linear тем же pow(2.2), что и шейдер (см. UnlitInstancedPS.hlsl).
			sum += new Vector3(
				MathF.Pow(texture.Pixels[idx] / 255f, 2.2f),
				MathF.Pow(texture.Pixels[idx + 1] / 255f, 2.2f),
				MathF.Pow(texture.Pixels[idx + 2] / 255f, 2.2f));
			alphaSum += texture.Pixels[idx + 3] / 255f;
			count++;
		}

		return count > 0
			? new Vector4(sum / count * factor, alphaSum / count * pm.BaseColorFactor.W)
			: new Vector4(factor, pm.BaseColorFactor.W);
	}

	/// <summary>Сжатый исходник одной glTF-картинки для стриминга качества - один на image, шарится
	/// всеми PreparedTexture его каналов/материалов; в финализации по нему группируются привязки в
	/// один <see cref="StreamedTexture"/>.</summary>
	internal sealed class TextureStreamSource
	{
		/// <summary>Внешний файл картинки (предпочтительно - ничего не держим в памяти).</summary>
		public string FilePath;

		/// <summary>Встроенные байты (.glb / data-URI), когда файла на диске нет.</summary>
		public byte[] EncodedBytes;
	}

	internal sealed class PreparedTexture
	{
		public byte[] Pixels;
		public int Width;
		public int Height;
		public TextureAddress AddressMode;
		public TextureFilter FilterMode;

		/// <summary>null = стриминг выключен (обычный полноразмерный декод).</summary>
		public TextureStreamSource StreamSource;

		/// <summary>Ключ запечённой BC-текстуры в кеше ассетов (см. DecaEngine.Graphics.Assets.AssetCache).
		/// Когда не null, пиксели брать неоткуда и не нужно: слот заливается прямо из .dtex готовой
		/// мип-цепочкой. Это и есть штатный путь при попадании в кеш - именно он убирает из загрузки
		/// и декод PNG, и RGBA8-буферы, и генерацию мипов на GPU.</summary>
		public string CacheKey;

		/// <summary>Картинка glTF, из которой декодирован слот. Нужна только фазе бейка - по её
		/// СЖАТЫМ байтам считается ключ кеша (см. AssetCache.TextureKey). В .dmdl не попадает и при
		/// загрузке из кеша всегда null: там glTF не открывается вовсе.</summary>
		public SharpGLTF.Schema2.Image SourceImage;
	}

	internal sealed class PreparedMaterial
	{
		public int LogicalIndex;
		public bool IsNull;
		public string Name;
		public PreparedTexture BaseColorTexture;
		public PreparedTexture MetallicRoughnessTexture;
		public PreparedTexture NormalTexture;
		public float NormalScale = 1f;
		public PreparedTexture OcclusionTexture;
		public float OcclusionStrength = 1f;

		/// <summary>glTF texCoord occlusion-канала (0/1, см. MaterialPbrFactors.OcclusionUvSet).</summary>
		public int OcclusionUvSet;
		public PreparedTexture ThicknessTexture;

		// KHR_texture_transform (см. MaterialPbrFactors.UvTransform/UvOffset/HasUvTransform).
		public Vector4 UvTransform;
		public Vector2 UvOffset;
		public bool HasUvTransform;

		// glTF spec defaults - overwritten in PrepareModel only when the material authored them.
		public Vector4 BaseColorFactor = Vector4.One;
		public float MetallicFactor = 1f;
		public float RoughnessFactor = 1f;
		public float AlphaCutoff;
		public MaterialAlphaMode AlphaMode;

		/// <summary>См. <see cref="ComputeSoftAlphaFraction"/>. Считается по ПИКСЕЛЯМ, поэтому обязана
		/// попадать в .dmdl - в cooked-модели пикселей нет. -1 = не считалось.</summary>
		public float SoftAlphaFraction = -1f;
		public float TransmissionFactor;
		public float Ior = 1.5f;
		public float Dispersion;
		public Vector4 VolumeAttenuation = new(1f, 1f, 1f, 0f);
		public float ThicknessFactor;

		// KHR_materials_sheen (нулевой цвет = выключено; roughness-дефолт спеки 0).
		public Vector3 SheenColorFactor;
		public float SheenRoughnessFactor;

		// KHR_materials_specular (дефолты спеки: белый цвет, вес 1 = тождественно).
		public Vector3 SpecularColorFactor = Vector3.One;
		public float SpecularFactor = 1f;

		/// <summary>Среднее base color: rgb - линейное альбедо, w - средняя альфа (см.
		/// <see cref="EnsureAverageBaseColor"/>). Считается ПО ПИКСЕЛЯМ текстуры, поэтому обязано
		/// попадать в .dmdl: в cooked-модели пикселей нет вовсе (CookedModelFile.WriteTexture), и
		/// пересчитать это при загрузке из кеша не из чего. Пока поле не сохранялось, у всей
		/// cooked-сцены альфа выходила равной фактору (=1), а по ней отбираются «дырявые» материалы -
		/// листва теряла и альфа-тест в тенях (ModelViewportEnvironment), и исключение из BVH
		/// probe-GI (ProbeGi), то есть кроны отбрасывали тень сплошными квадратами.
		/// null = ещё не считалось.</summary>
		public Vector4? AverageBaseColorRgba;
	}

	/// <summary>Сырьё одного glTF-примитива, собранное последовательной фазой PrepareModel (чтение
	/// SharpGLTF не потокобезопасно) для параллельной CPU-обработки (winding/нормали/тангенты/
	/// meshopt/LOD). Индекс в списке work-item-ов = будущий meshId.</summary>
	private sealed class MeshWorkItem
	{
		public string Name;
		public Vertex[] SourceVertices;
		public uint[] SourceIndices;
		public int Topology;
		public bool HasUv;
		public bool HasNormals;
		public bool HasTangents;

		/// <summary>Скин-стрим примитива, null у статической геометрии (см. <see cref="SkinVertex"/>).</summary>
		public SkinVertex[] SourceSkin;
	}

	internal sealed class PreparedMesh
	{
		public string Name;
		public Vertex[] Vertices;
		public uint[] Indices;
		public LodLevel[] LodLevels;

		/// <summary>Скиннинг-атрибуты, параллельные <see cref="Vertices"/>; null - меш статический и
		/// рисуется прежним путём без compute-скиннинга.</summary>
		public SkinVertex[] SkinVertices;
		public Vector3 BoundsCenter;
		public float BoundsRadius;
		public bool HasUv;

		/// <summary>Код топологии (MeshTopology*-константы).</summary>
		public int Topology;
	}

	internal sealed class PreparedModel
	{
		public List<PreparedMaterial> Materials = new();
		public List<PreparedMesh> Meshes = new();
		public List<InstanceData> Instances = new();

		/// <summary>Скелет модели, null у статической. Один на модель, даже если скинов несколько
		/// (см. <see cref="SkinningImport.BuildSkeleton"/>).</summary>
		public PreparedSkeleton Skeleton;

		/// <summary>Клипы, разложенные по джойнтам <see cref="Skeleton"/>. Пусто, если скелета нет
		/// или ни один клип его не задевает.</summary>
		public List<PreparedAnimation> Animations = new();

		/// <summary>Реестр материалов-клонов для не-треугольных топологий: синтетический ключ ->
		/// (исходный glTF-материал, код топологии). Заполняется в PrepareModel, материализуется в
		/// BuildFromPrepared.</summary>
		public Dictionary<int, (int SourceMaterial, int Topology)> TopologyMaterialClones = new();

		/// <summary>Тайминги фоновых фаз, мс - для диагностики (см. ModelLoader.Timings). Без них
		/// оптимизация загрузки превращается в гадание: фазы стоят очень по-разному на разных
		/// ассетах, и «очевидный» виновник обычно не тот.</summary>
		public long MsParse, MsDecode, MsMaterials, MsMeshes;

		/// <summary>Сколько уникальных картинок декодировано и сколько мегабайт они заняли
		/// несжатыми - главный вкладчик в пиковую память загрузки.</summary>
		public int DecodedImages;
		public long DecodedBytes;

		/// <summary>Потриугольные атрибуты материала (ключ - meshId), по 5 байт на треугольник:
		/// альбедо RGB в sRGB-кодировке + металличность + шероховатость. Считаются ПО ПИКСЕЛЯМ
		/// текстур, поэтому обязаны попадать в .dmdl: в cooked-модели пикселей нет вовсе, и
		/// пересчитать это на загрузке не из чего - без них у RT-отражений оставались плоский
		/// средний цвет и «неизвестный» материал (цепочка отскоков не запускалась никогда).
		/// Пусто = ещё не считалось (см. <see cref="EnsureTriangleAttributes"/>).</summary>
		public Dictionary<int, byte[]> TriangleAttributes = new();
	}

	/// <summary>
	/// Handle to an in-flight background <see cref="ModelLoader"/> load (see <see cref="BeginLoadAsync"/>).
	/// Poll <see cref="PrepareTask"/>/<see cref="Progress"/> from the render loop and, once the task
	/// completes successfully, call <see cref="FinalizeOnMainThread"/> on the graphics thread to create
	/// the actual GPU resources and obtain the ready <see cref="ModelLoader"/>.
	/// </summary>
	public sealed class ModelLoadRequest
	{
		private readonly IGraphicsApi _graphicsApi;
		private readonly ModelLoadOptions _options;
		private readonly ProgressTracker _progressTracker = new();

		public string ModelPath { get; }
		public Task PrepareTask { get; }
		public float Progress => _progressTracker.Value;

		private PreparedModel _prepared;

		// Состояние пошаговой финализации (см. FinalizeChunk): наполовину построенный ModelLoader и
		// текущая позиция итератора BuildFromPreparedIncremental. Живёт между кадрами.
		private ModelLoader _finalizing;
		private long _finalizeMs;
		private IEnumerator<long> _finalizeSteps;

		internal ModelLoadRequest(IGraphicsApi graphicsApi, string modelPath, ModelLoadOptions options,
			IProgress<float> externalProgress, CancellationToken cancellationToken)
		{
			_graphicsApi = graphicsApi;
			_options = options;
			ModelPath = modelPath;

			var combinedProgress = new Progress<float>(p =>
			{
				_progressTracker.Value = p;
				externalProgress?.Report(p);
			});

			PrepareTask = Task.Run(() =>
			{
				_prepared = PrepareModel(modelPath, options, combinedProgress, cancellationToken);
				// Прогрев шейдер-вариантов ЗДЕСЬ, в фоне - иначе они компилируются лениво из
				// SetShader во время финализации, синхронно на GPU-потоке (см. PrecompileShaderVariants).
				PrecompileShaderVariants(graphicsApi, options, _prepared, cancellationToken);
			}, cancellationToken);
		}

		/// <summary>Дефолтный байтовый бюджет одного вызова <see cref="FinalizeChunk"/>. Подобран под
		/// дефолтный dynamicHeapSize Diligent-а: страницы upload-хипа возвращаются в пул только на
		/// FinishFrame (Present), поэтому бюджет кадра и определяет пиковый расход host-visible
		/// памяти при загрузке (у Sponza одним махом выходило 2.5+ GB и «Space in dynamic heap is
		/// almost exhausted» с принудительным idle GPU).</summary>
		public const long DefaultFinalizeBudgetBytes = 96L << 20;

		/// <summary>
		/// Creates the GPU resources (shaders/materials/textures/meshes) for a completed background load
		/// and returns the ready <see cref="ModelLoader"/>. Must be called on the thread that owns the
		/// <see cref="IGraphicsApi"/> passed to <see cref="BeginLoadAsync"/> (i.e. the main/render
		/// thread), only after <see cref="PrepareTask"/> has completed successfully. Заливает ВСЮ
		/// модель одним вызовом - в интерактивном рендер-лупе предпочитайте покадровый
		/// <see cref="FinalizeChunk"/>, иначе upload-хип раздувается на весь размер модели.
		/// </summary>
		public ModelLoader FinalizeOnMainThread() => FinalizeChunk(long.MaxValue, long.MaxValue);

		/// <summary>
		/// Покадровая версия <see cref="FinalizeOnMainThread"/>: создаёт GPU-ресурсы, пока суммарная
		/// оценка залитых байт не превысит <paramref name="budgetBytes"/>, и возвращает null - «зайди
		/// на следующем кадре» (между вызовами обязан пройти Present, освобождающий страницы
		/// upload-хипа). Когда всё создано - возвращает готовый <see cref="ModelLoader"/>. Те же
		/// требования, что у FinalizeOnMainThread: главный поток, PrepareTask завершился успешно.
		/// Внимание: бросить запрос между вызовами (не дойдя до результата) - значит утечь уже
		/// созданными GPU-ресурсами: у ModelLoader нет Release, недостроенный экземпляр никому не
		/// возвращается.
		/// </summary>
		public ModelLoader FinalizeChunk(long budgetBytes = DefaultFinalizeBudgetBytes,
			long budgetMs = FinalizeBudgetMs)
		{
			if (!PrepareTask.IsCompletedSuccessfully)
			{
				throw new InvalidOperationException(
					"FinalizeChunk called before the background load finished successfully.");
			}

			if (_prepared == null)
			{
				throw new InvalidOperationException("FinalizeChunk called after finalization already completed.");
			}

			if (_finalizeSteps == null)
			{
				_finalizing = new ModelLoader();
				_finalizeSteps = BuildFromPreparedIncremental(_graphicsApi, _options, _prepared, _finalizing);
			}

			// Финализация размазана по кадрам, поэтому её время копится по кусочкам - иначе цифра
			// показывала бы длину последнего чанка, а не стоимость фазы.
			var swFinalize = System.Diagnostics.Stopwatch.StartNew();

			long uploadedBytes = 0;
			while (uploadedBytes < budgetBytes)
			{
				if (!_finalizeSteps.MoveNext())
				{
					var ready = _finalizing;
					_finalizeMs += swFinalize.ElapsedMilliseconds;
					ready.Timings = new LoadTimings(_prepared.MsParse, _prepared.MsDecode,
						_prepared.MsMaterials, _prepared.MsMeshes, _finalizeMs,
						_prepared.DecodedImages, _prepared.DecodedBytes,
						ready._shaderVariants, ready._shaderMs, ready._textureCount, ready._textureMs,
						ready._meshCount, ready._meshMs, ready._samplerCount, ready._samplerMs, ready._materialCount, ready._materialMs, ready._matCreateMs, ready._matShaderMs);

					_finalizeSteps.Dispose();
					_finalizeSteps = null;
					_finalizing = null;
					_prepared = null;

					// Кэш PSO - на диск ровно здесь: загрузка только что создала все конвейеры модели,
					// а следующий запуск иначе скомпилирует их заново (см. IGraphicsApi.SavePipelineCache).
					_graphicsApi.SavePipelineCache();
					return ready;
				}

				uploadedBytes += _finalizeSteps.Current;

				// Бюджет ВРЕМЕНИ, а не только байт. Байтовый бюджет считает ЗАЛИВКУ, а самое дорогое
				// в финализации байт не заливает вовсе: компиляция вариантов пиксельного шейдера
				// (секунда на вариант) и создание материалов. В режиме стриминга текстур оценка
				// материала - жалкие 4 КБ, так что все 50+ материалов Sponza со всеми компиляциями
				// проходили за ОДИН вызов, то есть за один кадр: окно редактора висело "Not
				// Responding" на всё время загрузки.
				if (swFinalize.ElapsedMilliseconds >= budgetMs)
				{
					break;
				}
			}

			_finalizeMs += swFinalize.ElapsedMilliseconds;

			return null;
		}

		/// <summary>Сколько миллисекунд одному вызову <see cref="FinalizeChunk"/> позволено занимать
		/// поток рендера. Один шаг итератора прервать нельзя (компиляция шейдера идёт целиком), так
		/// что реальный кадр может выйти длиннее - но следующий шаг уже уедет в следующий кадр, и UI
		/// остаётся живым.</summary>
		public const long FinalizeBudgetMs = 8;

		private sealed class ProgressTracker
		{
			private float _value;
			public float Value
			{
				get => Volatile.Read(ref _value);
				set => Volatile.Write(ref _value, value);
			}
		}
	}
}
