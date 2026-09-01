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

/// <summary>CPU-фаза: разбор glTF в PreparedModel - меши, LOD-ы, скелет, клипы, материалы. Часть <see cref="ModelLoader"/> - файл на фазу; состояние,
/// точки входа загрузки и Release живут в основном файле.</summary>
public partial class ModelLoader
{
	private static PreparedModel PrepareModel(string modelPath, ModelLoadOptions options,
		IProgress<float> progress, CancellationToken cancellationToken)
	{
		// Ассет-пайплайн. При ПОПАДАНИИ всё, что ниже, не выполняется вовсе: ни разбора glTF, ни
		// декода картинок, ни meshopt, ни упрощения под LOD - только чтение линейного .dmdl. Именно
		// эти четыре фазы и составляют почти всё время загрузки, и все они - чистые функции от
		// исходника и опций, то есть считать их заново при каждом открытии сцены незачем.
		var cache = options.Cache;
		if (cache != null)
		{
			var modelKey = AssetCache.ModelKey(modelPath, options.CookSignature());
			var cooked = CookedModelFile.TryRead(cache.ModelPath(modelKey));

			if (cooked != null && ModelAssetBaker.AllTexturesPresent(cooked, cache))
			{
				progress?.Report(1f);
				return cooked;
			}

			// Промах. Загрузка НЕ ждёт печку и идёт дальше обычным путём - включение пайплайна не
			// имеет права сделать первое открытие модели медленнее, чем оно было без него.
			AssetBakeQueue.Enqueue(modelPath, options, modelKey);
		}

		// Строгая валидация SharpGLTF на больших сценах заметно небесплатна; TryFix заодно чинит
		// мелкие огрехи экспортёров вместо жёсткого отказа.
		var swPhase = System.Diagnostics.Stopwatch.StartNew();
		var model = LoadModelRoot(modelPath, options, out var externalImagePaths);
		cancellationToken.ThrowIfCancellationRequested();

		var prepared = new PreparedModel();
		prepared.MsParse = swPhase.ElapsedMilliseconds;
		swPhase.Restart();

		// Картинки, на которые реально ссылаются декодируемые ниже каналы материалов. Декод (PNG/JPG +
		// даунскейл) - самая дорогая CPU-фаза загрузки: параллелится по уникальным image, материалы
		// ниже берут готовые пиксели из кэша. Кэш заодно убирает повторный декод одного image,
		// разделяемого несколькими материалами/каналами (типовая ORM-текстура: у MetallicRoughness и
		// Occlusion один и тот же image - раньше он декодировался дважды).
		var usedImages = new List<SharpGLTF.Schema2.Image>();
		{
			var seenImages = new HashSet<SharpGLTF.Schema2.Image>();
			void AddImage(SharpGLTF.Schema2.Texture texture)
			{
				if (texture?.PrimaryImage != null && seenImages.Add(texture.PrimaryImage))
				{
					usedImages.Add(texture.PrimaryImage);
				}
			}

			foreach (var logicalMaterial in model.LogicalMaterials)
			{
				if (logicalMaterial == null)
				{
					continue;
				}

				AddImage(logicalMaterial.GetDiffuseTexture());
				AddImage(logicalMaterial.FindChannel("MetallicRoughness")?.Texture);
				AddImage(logicalMaterial.FindChannel("VolumeThickness")?.Texture);
				AddImage(logicalMaterial.FindChannel("Occlusion")?.Texture);
				AddImage(logicalMaterial.FindChannel("Normal")?.Texture);
			}
		}

		// Стриминг текстур: в фоновой фазе НЕ ДЕКОДИРУЕТСЯ НИ ОДНА картинка. Декод (PNG/JPG +
		// даунскейл) - самая дорогая CPU-фаза загрузки и главный вкладчик в пиковую память, и именно
		// он раньше держал сцену пустой всё время загрузки. Материалы строятся сразу с 1x1-филлерами
		// (кейворды шейдера при этом ТЕ ЖЕ - они ставятся по наличию текстуры в glTF, а не по
		// наличию пикселей, так что апгрейд не трогает PSO), геометрия появляется почти сразу, а
		// пиксели приезжают ступенями из ModelStreamer.
		//
		// Источник ре-декода - ПУТЬ к файлу картинки, если она внешняя (типовая .gltf-сцена вроде
		// Sponza: папка с PNG рядом), и только для встроенных (.glb / data-URI) копируются байты.
		// Иначе сотни 4K-исходников Sponza жили бы в managed-памяти всё время сессии.
		int decodeMaxSize = options.MaxTextureSize;
		Dictionary<SharpGLTF.Schema2.Image, TextureStreamSource> streamSources = null;
		if (options.StreamTextures)
		{
			decodeMaxSize = 0; // ничего не декодируем в этой фазе
			streamSources = new Dictionary<SharpGLTF.Schema2.Image, TextureStreamSource>();
			foreach (var image in usedImages)
			{
				streamSources[image] = CreateStreamSource(image, externalImagePaths);
			}
		}

		var decodedImages = new Dictionary<SharpGLTF.Schema2.Image, (byte[] Pixels, int Width, int Height)>();
		if (usedImages.Count > 0 && !options.StreamTextures)
		{
			var decodedResults = new (byte[] Pixels, int Width, int Height)[usedImages.Count];
			int imagesDone = 0;

			// Параллелизм ОГРАНИЧЕН, и это не про загрузку CPU, а про ПАМЯТЬ. Декод идёт в полном
			// разрешении файла и только потом ужимается до MaxTextureSize (stb иначе не умеет), то
			// есть каждый поток держит в пике полноразмерную RGBA-копию: для 4K это 64 МБ. Без
			// ограничения Parallel.For берёт по потоку на ядро, и на 16-32-поточной машине это
			// 1-2 ГБ ОДНИХ ТОЛЬКО промежуточных буферов - поверх того, что уже накоплено
			// декодированным (см. ниже: decodedResults держит ВСЕ картинки до конца фазы).
			//
			// Четыре потока сохраняют почти всю выгоду распараллеливания (декод упирается в память,
			// а не в ALU) и срезают этот пик до сотен мегабайт.
			var decodeOptions = new ParallelOptions
			{
				CancellationToken = cancellationToken,
				MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount),
			};

			Parallel.For(0, usedImages.Count, decodeOptions, i =>
			{
				decodedResults[i] = DecodeImagePixels(usedImages[i], decodeMaxSize);
				progress?.Report(0.05f + 0.30f * (Interlocked.Increment(ref imagesDone) / (float)usedImages.Count));
			});

			for (int i = 0; i < usedImages.Count; i++)
			{
				decodedImages[usedImages[i]] = decodedResults[i];
				prepared.DecodedBytes += decodedResults[i].Pixels?.LongLength ?? 0;
			}

			prepared.DecodedImages = usedImages.Count;
		}

		prepared.MsDecode = swPhase.ElapsedMilliseconds;
		swPhase.Restart();

		// Weight the big background phases (texture decode above, then materials and meshes) roughly
		// by count so the progress bar moves at a believable pace instead of jumping straight to 50%.
		int materialCount = Math.Max(1, model.LogicalMaterials.Count);

		for (var index = 0; index < model.LogicalMaterials.Count; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var logicalMaterial = model.LogicalMaterials[index];

			if (logicalMaterial == null)
			{
				prepared.Materials.Add(new PreparedMaterial { LogicalIndex = index, IsNull = true });
				progress?.Report(0.35f + 0.05f * ((index + 1) / (float)materialCount));
				continue;
			}

			var preparedMaterial = new PreparedMaterial
			{
				LogicalIndex = index,
				Name = logicalMaterial.Name ?? $"Material_{index}"
			};

			var baseColorTexture = logicalMaterial.GetDiffuseTexture();
			if (baseColorTexture?.PrimaryImage != null)
			{
				preparedMaterial.BaseColorTexture = DecodeTexture(baseColorTexture, decodeMaxSize, decodedImages, streamSources, externalImagePaths);
			}

			// PBR metallic-roughness scalars for the editor's Lighting preview (see MaterialPbr).
			// PreparedMaterial's field initializers already hold the glTF spec defaults (white/1/1),
			// so only explicitly-authored channel parameters are read here.
			var baseColorChannel = logicalMaterial.FindChannel("BaseColor");
			if (baseColorChannel.HasValue)
			{
				foreach (var parameter in baseColorChannel.Value.Parameters)
				{
					if (parameter.Name == "RGBA" && parameter.Value is Vector4 rgba)
					{
						preparedMaterial.BaseColorFactor = rgba;
					}
				}
			}

			preparedMaterial.AlphaCutoff = logicalMaterial.Alpha switch
			{
				AlphaMode.MASK => logicalMaterial.AlphaCutoff,
				AlphaMode.BLEND => 0.5f,
				_ => 0f,
			};

			// Сам режим - ОТДЕЛЬНЫМ полем: порог выше его теряет (см. PreparedMaterial.AlphaMode).
			preparedMaterial.AlphaMode = logicalMaterial.Alpha switch
			{
				AlphaMode.MASK => MaterialAlphaMode.Mask,
				AlphaMode.BLEND => MaterialAlphaMode.Blend,
				_ => MaterialAlphaMode.Opaque,
			};

			bool metallicAuthored = false;
			bool roughnessAuthored = false;

			var metallicRoughnessChannel = logicalMaterial.FindChannel("MetallicRoughness");
			if (metallicRoughnessChannel.HasValue)
			{
				var channel = metallicRoughnessChannel.Value;

				foreach (var parameter in channel.Parameters)
				{
					if (parameter.Name == "MetallicFactor")
					{
						preparedMaterial.MetallicFactor = Convert.ToSingle(parameter.Value);
						metallicAuthored = !parameter.IsDefault;
					}
					else if (parameter.Name == "RoughnessFactor")
					{
						preparedMaterial.RoughnessFactor = Convert.ToSingle(parameter.Value);
						roughnessAuthored = !parameter.IsDefault;
					}
				}

				// Game-ready assets typically keep the factors at 1 and put the real per-texel values
				// into the metallic-roughness texture (G = roughness, B = metallic) - without sampling
				// it the preview would treat everything as polished-then-fully-rough metal.
				var mrTexture = channel.Texture;
				if (mrTexture?.PrimaryImage != null)
				{
					preparedMaterial.MetallicRoughnessTexture = DecodeTexture(mrTexture, decodeMaxSize, decodedImages, streamSources, externalImagePaths);
				}
			}

			// KHR_materials_ior / KHR_materials_dispersion - SharpGLTF мапит их прямо в свойства
			// материала (IndexOfRefraction по умолчанию 1.5, Dispersion 0 = выключена).
			preparedMaterial.Ior = logicalMaterial.IndexOfRefraction;
			preparedMaterial.Dispersion = logicalMaterial.Dispersion;

			// KHR_materials_transmission: только скалярный factor - текстуру трансмиссии превью не
			// сэмплирует, а полноценной рефракции у него нет (см. UnlitInstancedPS.hlsl, там
			// аппроксимация "фон сквозь тонированное стекло").
			var transmissionChannel = logicalMaterial.FindChannel("Transmission");
			if (transmissionChannel.HasValue)
			{
				foreach (var parameter in transmissionChannel.Value.Parameters)
				{
					if (parameter.Name == "TransmissionFactor")
					{
						preparedMaterial.TransmissionFactor = Convert.ToSingle(parameter.Value);
					}
				}
			}

			// KHR_materials_sheen: велюровый "световой ворс" (Charlie-лоб в шейдере). Цвет и своя
			// шероховатость - двумя каналами SharpGLTF. Параметры матчатся по ТИПУ значения (в каждом
			// канале ровно один нетекстурный параметр) - имена ключей у SharpGLTF внутренние.
			var sheenColorChannel = logicalMaterial.FindChannel("SheenColor");
			if (sheenColorChannel.HasValue)
			{
				foreach (var parameter in sheenColorChannel.Value.Parameters)
				{
					if (parameter.Value is Vector3 sheenRgb)
					{
						preparedMaterial.SheenColorFactor = sheenRgb;
					}
				}
			}

			var sheenRoughnessChannel = logicalMaterial.FindChannel("SheenRoughness");
			if (sheenRoughnessChannel.HasValue)
			{
				foreach (var parameter in sheenRoughnessChannel.Value.Parameters)
				{
					if (parameter.Value is float || parameter.Value is double)
					{
						preparedMaterial.SheenRoughnessFactor = Convert.ToSingle(parameter.Value);
					}
				}
			}

			// KHR_materials_specular: перекраска/ослабление диэлектрического F0 (сатин и прочие ткани
			// с цветным бликом). specularColorFactor может быть >1 (ChairDamaskPurplegold: [1,0.25,2]) -
			// кламп произойдёт в шейдере ПОСЛЕ умножения на F0 от IOR, как велит спека.
			var specularColorChannel = logicalMaterial.FindChannel("SpecularColor");
			if (specularColorChannel.HasValue)
			{
				foreach (var parameter in specularColorChannel.Value.Parameters)
				{
					if (parameter.Value is Vector3 specularRgb)
					{
						preparedMaterial.SpecularColorFactor = specularRgb;
					}
				}
			}

			var specularFactorChannel = logicalMaterial.FindChannel("SpecularFactor");
			if (specularFactorChannel.HasValue)
			{
				foreach (var parameter in specularFactorChannel.Value.Parameters)
				{
					if (parameter.Value is float || parameter.Value is double)
					{
						preparedMaterial.SpecularFactor = Convert.ToSingle(parameter.Value);
					}
				}
			}

			// KHR_materials_volume: Beer-Lambert затухание сквозь толщу стекла. Толщину берём только
			// фактором (thicknessTexture не сэмплируется), показатель степени thickness/attenuationDistance
			// предвычисляем здесь - шейдеру нужен один float4 (rgb цвет, w показатель).
			float volumeThickness = 0f;
			float attenuationDistance = 0f;
			var attenuationColor = Vector3.One;

			var thicknessChannel = logicalMaterial.FindChannel("VolumeThickness");
			if (thicknessChannel.HasValue)
			{
				foreach (var parameter in thicknessChannel.Value.Parameters)
				{
					if (parameter.Name == "ThicknessFactor")
					{
						volumeThickness = Convert.ToSingle(parameter.Value);
						preparedMaterial.ThicknessFactor = volumeThickness;
					}
				}

				// Толщина в текстуре (G-канал по спеке) - множитель поверх factor-а; без неё
				// плотное стекло глушит просвет равномерно, и тонкие детали (гребни, шипы)
				// теряют характерную "светящуюся" прозрачность.
				var thicknessTexture = thicknessChannel.Value.Texture;
				if (thicknessTexture?.PrimaryImage != null)
				{
					preparedMaterial.ThicknessTexture = DecodeTexture(thicknessTexture, decodeMaxSize, decodedImages, streamSources, externalImagePaths);
				}
			}

			var attenuationChannel = logicalMaterial.FindChannel("VolumeAttenuation");
			if (attenuationChannel.HasValue)
			{
				foreach (var parameter in attenuationChannel.Value.Parameters)
				{
					if (parameter.Name == "RGB" && parameter.Value is Vector3 rgb)
					{
						attenuationColor = rgb;
					}
					else if (parameter.Name == "AttenuationDistance")
					{
						attenuationDistance = Convert.ToSingle(parameter.Value);
					}
				}
			}

			preparedMaterial.VolumeAttenuation = volumeThickness > 0f && attenuationDistance > 0f
				? new Vector4(attenuationColor, volumeThickness / attenuationDistance)
				: new Vector4(1f, 1f, 1f, 0f);

			// Запечённый ambient occlusion (R-канал по спеке, часто общая ORM-текстура с MR) +
			// occlusionStrength. Глушит ambient/env-термы в порах и складках - без него фигуры
			// выглядят "пластиково чистыми". Прямой свет по спеке AO не трогает.
			var occlusionChannel = logicalMaterial.FindChannel("Occlusion");
			if (occlusionChannel.HasValue)
			{
				foreach (var parameter in occlusionChannel.Value.Parameters)
				{
					if (parameter.Name == "OcclusionStrength")
					{
						preparedMaterial.OcclusionStrength = Convert.ToSingle(parameter.Value);
					}
				}

				// AO часто запечён под уникальную развёртку ВТОРОГО UV-канала (texCoord 1, см.
				// ChairDamaskPurplegold) - сэмпл по UV0 кладёт затемнения в случайные места.
				// Каналы выше 1 в вершине не хранятся - клампятся в TEXCOORD_1.
				preparedMaterial.OcclusionUvSet = Math.Clamp(occlusionChannel.Value.TextureCoordinate, 0, 1);

				var occlusionTexture = occlusionChannel.Value.Texture;
				if (occlusionTexture?.PrimaryImage != null)
				{
					preparedMaterial.OcclusionTexture = DecodeTexture(occlusionTexture, decodeMaxSize, decodedImages, streamSources, externalImagePaths);
				}
			}

			// Нормал-мапа (tangent-space, линейная - без sRGB-декода) + normalScale. Без неё весь
			// авторский микрорельеф (кладка, резьба, прожилки) теряется - поверхность шейдится
			// только геометрической нормалью.
			var normalChannel = logicalMaterial.FindChannel("Normal");
			if (normalChannel.HasValue)
			{
				foreach (var parameter in normalChannel.Value.Parameters)
				{
					if (parameter.Name == "NormalScale")
					{
						preparedMaterial.NormalScale = Convert.ToSingle(parameter.Value);
					}
				}

				var normalTexture = normalChannel.Value.Texture;
				if (normalTexture?.PrimaryImage != null)
				{
					preparedMaterial.NormalTexture = DecodeTexture(normalTexture, decodeMaxSize, decodedImages, streamSources, externalImagePaths);
				}
			}

			// KHR_texture_transform: смещение/масштаб/поворот UV, заданные материалом (Khronos-семпл
			// ChairDamaskPurplegold: scale 3x3 + rotation 0.1 на дереве/ткани - без учёта текстуры
			// тайлятся втрое крупнее и без поворота волокон). Одна трансформация на материал: с
			// baseColor-канала, фоллбек normal/MR. Предвычисляется в 2x2-матрицу + offset по формуле
			// спеки M = Translation * Rotation * Scale.
			foreach (var channelName in new[] { "BaseColor", "Normal", "MetallicRoughness" })
			{
				var transform = logicalMaterial.FindChannel(channelName)?.TextureTransform;
				if (transform == null)
				{
					continue;
				}

				float sin = MathF.Sin(transform.Rotation);
				float cos = MathF.Cos(transform.Rotation);
				preparedMaterial.UvTransform = new Vector4(
					cos * transform.Scale.X, -sin * transform.Scale.Y,
					sin * transform.Scale.X, cos * transform.Scale.Y);
				preparedMaterial.UvOffset = transform.Offset;
				preparedMaterial.HasUvTransform = true;
				break;
			}

			// Preview-friendly fallback: a material with neither a metallic-roughness texture nor
			// authored factors lands on the glTF spec defaults (metallic 1, roughness 1), i.e. a metal
			// with no diffuse and a lobe-less specular - it renders as if unlit (ambient only). A
			// neutral dielectric reads far closer to what the author meant.
			//
			// ВАЖНО: только когда НЕ авторский НИ ОДИН фактор. IsDefault у SharpGLTF означает
			// "значение равно дефолту", а не "не записан в JSON" - материал с явным metallic=1 +
			// roughness=0 (зеркало, см. PrimitiveModeNormalsTest) выглядит как "metallic не авторский",
			// но авторский roughness выдаёт осознанный metal-workflow, и глушить его в диэлектрик нельзя.
			if (preparedMaterial.MetallicRoughnessTexture == null && !metallicAuthored && !roughnessAuthored)
			{
				preparedMaterial.MetallicFactor = 0f;
				preparedMaterial.RoughnessFactor = 0.6f;
			}

			prepared.Materials.Add(preparedMaterial);
			progress?.Report(0.35f + 0.05f * ((index + 1) / (float)materialCount));
		}

		prepared.MsMaterials = swPhase.ElapsedMilliseconds;
		swPhase.Restart();

		var primitiveToMeshIdMap = new Dictionary<MeshPrimitive, int>();
		var meshWork = new List<MeshWorkItem>();

		// Скелет и клипы - ДО обхода примитивов: скин-стрим вершин переводит локальные индексы скина
		// в индексы джойнтов скелета, значит скелет к этому моменту обязан существовать.
		prepared.Skeleton = SkinningImport.BuildSkeleton(model, out var nodeToJoint);
		prepared.Animations.AddRange(SkinningImport.BuildAnimations(model, prepared.Skeleton, nodeToJoint));

		// Скин висит на УЗЛЕ, а не на примитиве, но скин-стрим нужен именно примитиву - отсюда
		// предпроход. Один и тот же примитив под двумя узлами с разными скинами разрешается в пользу
		// первого: glTF такое допускает, живые ассеты - нет, а тащить в PreparedMesh вариант на скин
		// значило бы дублировать всю геометрию ради несуществующего случая.
		var primitiveToSkin = new Dictionary<MeshPrimitive, Skin>();
		foreach (var node in model.LogicalNodes)
		{
			if (node.Mesh == null || node.Skin == null)
			{
				continue;
			}

			foreach (var primitive in node.Mesh.Primitives)
			{
				primitiveToSkin.TryAdd(primitive, node.Skin);
			}
		}

		foreach (var logicalMesh in model.LogicalMeshes)
		{
			var baseMeshName = logicalMesh.Name ?? $"Mesh_{logicalMesh.LogicalIndex}";

			for (var primitiveIndex = 0; primitiveIndex < logicalMesh.Primitives.Count; primitiveIndex++)
			{
				var primitive = logicalMesh.Primitives[primitiveIndex];
				cancellationToken.ThrowIfCancellationRequested();

				var positionsAccessor = primitive.GetVertexAccessor("POSITION");
				var uvsAccessor = primitive.GetVertexAccessor("TEXCOORD_0");
				var uvs1Accessor = primitive.GetVertexAccessor("TEXCOORD_1");
				var normalsAccessor = primitive.GetVertexAccessor("NORMAL");
				var tangentsAccessor = primitive.GetVertexAccessor("TANGENT");
				var colorsAccessor = primitive.GetVertexAccessor("COLOR_0");
				var indexAccessor = primitive.GetIndexAccessor();

				if (positionsAccessor == null)
				{
					continue;
				}

				// Топология примитива (см. MeshTopology*-константы): точки/линии рисуются клонами
				// материала с PSO соответствующей топологии (см. BuildFromPrepared /
				// ModelViewportGeometry.RegisterModelResources) - батч-рендерер группирует дроу по
				// материалу, так что отдельный материал на топологию не требует его переделки.
				int topology = primitive.DrawPrimitiveType switch
				{
					PrimitiveType.TRIANGLES => MeshTopologyTriangles,
					PrimitiveType.LINES => MeshTopologyLineList,
					PrimitiveType.LINE_STRIP => MeshTopologyLineStrip,
					PrimitiveType.LINE_LOOP => MeshTopologyLineStrip,
					PrimitiveType.POINTS => MeshTopologyPoints,
					_ => -1,
				};
				if (topology < 0)
				{
					// TRIANGLE_STRIP/FAN не поддержаны - раньше такие примитивы рисовались как
					// triangle list (мусор), теперь честно пропускаются.
					continue;
				}

				var positions = positionsAccessor.AsVector3Array();
				if (positions.Count == 0)
				{
					continue;
				}

				var uvs = uvsAccessor?.AsVector2Array();
				var uvs1 = uvs1Accessor?.AsVector2Array();
				var normals = normalsAccessor?.AsVector3Array();
				var tangents = tangentsAccessor?.AsVector4Array();
				var colors = colorsAccessor?.AsColorArray();
				var indices = indexAccessor?.AsIndicesArray();

				// glTF - правосторонняя система (+Z на зрителя), движок - левосторонняя: без
				// зеркалирования Z вся геометрия рендерится отражённой (текст задом наперёд, см.
				// PrimitiveModeNormalsTest). Вместе с инверсией Z у треугольников меняется winding -
				// он разворачивается ниже, чтобы фронт-фейсы остались фронт-фейсами.
				var sourceVertices = new Vertex[positions.Count];
				for (int i = 0; i < positions.Count; i++)
				{
					var uv = uvs != null && i < uvs.Count ? uvs[i] : Vector2.Zero;
					var uv1 = uvs1 != null && i < uvs1.Count ? uvs1[i] : Vector2.Zero;
					var normal = normals != null && i < normals.Count ? normals[i] : Vector3.UnitY;
					var color = colors != null && i < colors.Count ? colors[i] : Vector4.One;

					// Авторский glTF TANGENT (vec4, w = знак битангента). Направление зеркалируется
					// по Z вместе с позициями/нормалями, а w ИНВЕРТИРУЕТСЯ: зеркало меняет
					// ориентацию базиса (det = -1), и cross(N, T) в пространстве движка смотрит
					// против зеркалированного битангента. Без авторских тангентов w временно 1 -
					// GenerateTangents ниже перезапишет и направление, и знак.
					var tangent = tangents != null && i < tangents.Count
						? new Vector4(tangents[i].X, tangents[i].Y, -tangents[i].Z, -tangents[i].W)
						: new Vector4(1f, 0f, 0f, 1f);

					sourceVertices[i] = new Vertex
					{
						Position = new Vector3(positions[i].X, positions[i].Y, -positions[i].Z),
						TexCoord = new Vector2(uv.X, uv.Y),
						TexCoord1 = new Vector2(uv1.X, uv1.Y),
						Normal = new Vector3(normal.X, normal.Y, -normal.Z),
						Tangent = tangent,
						Color = color
					};
				}

				// Точки/линии в glTF почти всегда неиндексированные (см. PrimitiveModeNormalsTest) -
				// батч-рендерер рисует только DrawIndexedIndirect, поэтому синтезируем 0..N-1.
				uint[] sourceIndices;
				if (indices != null)
				{
					sourceIndices = indices.ToArray();
				}
				else
				{
					sourceIndices = new uint[positions.Count];
					for (uint i = 0; i < sourceIndices.Length; i++)
					{
						sourceIndices[i] = i;
					}
				}

				// A glTF logical mesh with multiple primitives (e.g. one node using several materials)
				// becomes multiple sub-meshes here, one per primitive - without a per-primitive suffix
				// they'd all inherit the same logicalMesh.Name and be indistinguishable in the sub-mesh
				// list (same label for every entry, even though each is a distinct piece of geometry).
				var meshName = logicalMesh.Primitives.Count > 1 ? $"{baseMeshName}.{primitiveIndex}" : baseMeshName;

				// Тяжёлая чистая CPU-обработка (winding/нормали/тангенты/meshopt/LOD) вынесена в
				// параллельную фазу ниже - здесь только чтение SharpGLTF (не потокобезопасно) и
				// сбор сырья по примитивам. meshId примитива = индекс work-item-а.
				primitiveToMeshIdMap[primitive] = meshWork.Count;
				meshWork.Add(new MeshWorkItem
				{
					Name = meshName,
					SourceVertices = sourceVertices,
					SourceIndices = sourceIndices,
					Topology = topology,
					HasUv = uvsAccessor != null,
					HasNormals = normalsAccessor != null,
					HasTangents = tangents != null,
					// Читается ЗДЕСЬ, а не в параллельной фазе ниже: SharpGLTF не потокобезопасен.
					SourceSkin = primitiveToSkin.TryGetValue(primitive, out var primitiveSkin)
						? SkinningImport.ReadSkinVertices(primitive, primitiveSkin, nodeToJoint, sourceVertices.Length)
						: null,
				});
			}
		}

		// Обработка примитивов независима и не трогает SharpGLTF - параллелится целиком.
		var preparedMeshes = new PreparedMesh[meshWork.Count];
		int primitivesDone = 0;
		Parallel.For(0, meshWork.Count, new ParallelOptions { CancellationToken = cancellationToken }, workIndex =>
		{
			var work = meshWork[workIndex];
			var sourceVertices = work.SourceVertices;
			var sourceIndices = work.SourceIndices;
			var sourceSkin = work.SourceSkin;

			if (work.Topology == MeshTopologyTriangles)
			{
				for (int t = 0; t + 2 < sourceIndices.Length; t += 3)
				{
					(sourceIndices[t + 1], sourceIndices[t + 2]) = (sourceIndices[t + 2], sourceIndices[t + 1]);
				}

				// Примитив без NORMAL-аксессора: по спеке glTF шейдится FLAT (per-face). Вершины
				// развариваются по треугольникам, каждая получает нормаль своей грани - ровно
				// гранёный "диско-шар" эталонного вьювера. Усреднение по вершинам (прошлая
				// версия) давало гладкую сферу, но швы дублированных вершин расходились
				// полосами в отражениях.
				if (!work.HasNormals)
				{
					var flatVertices = new Vertex[sourceIndices.Length];
					// Скин разваривается ВМЕСТЕ с геометрией: индексы вершин переписываются на
					// 0..N-1, и стрим, оставшийся в старой индексации, раздал бы вершинам чужие кости.
					var flatSkin = sourceSkin != null ? new SkinVertex[sourceIndices.Length] : null;

					for (int t = 0; t + 2 < sourceIndices.Length; t += 3)
					{
						if (flatSkin != null)
						{
							flatSkin[t] = sourceSkin[sourceIndices[t]];
							flatSkin[t + 1] = sourceSkin[sourceIndices[t + 1]];
							flatSkin[t + 2] = sourceSkin[sourceIndices[t + 2]];
						}

						var v0 = sourceVertices[sourceIndices[t]];
						var v1 = sourceVertices[sourceIndices[t + 1]];
						var v2 = sourceVertices[sourceIndices[t + 2]];

						var faceNormal = Vector3.Cross(v2.Position - v0.Position, v1.Position - v0.Position);
						faceNormal = faceNormal.LengthSquared() > 1e-16f
							? Vector3.Normalize(faceNormal)
							: Vector3.UnitY;

						v0.Normal = faceNormal;
						v1.Normal = faceNormal;
						v2.Normal = faceNormal;

						flatVertices[t] = v0;
						flatVertices[t + 1] = v1;
						flatVertices[t + 2] = v2;
						sourceIndices[t] = (uint)t;
						sourceIndices[t + 1] = (uint)(t + 1);
						sourceIndices[t + 2] = (uint)(t + 2);
					}

					sourceVertices = flatVertices;
					sourceSkin = flatSkin;
				}
			}
			var (boundsCenter, boundsRadius) = MeshUtility.ComputeBoundsData(sourceVertices);

			var finalVertices = sourceVertices;
			var finalIndices = sourceIndices;
			var finalSkin = sourceSkin;
			LodLevel[] lodLevels = null;

			if (work.Topology == MeshTopologyTriangles)
			{
				// Must run before Optimize/GenerateLods reorder/remap vertices - it needs the
				// pristine per-triangle winding to compute per-triangle tangents, but the resulting
				// per-vertex Tangent then rides along automatically through any later remap (it's
				// just another Vertex field, opaque to Meshopt's vertex-remap/simplify passes).
				// Только фоллбек: авторский glTF TANGENT (уже в вершинах, со знаком w) точнее
				// генерации - он согласован с запечкой нормал-мапы (MikkTSpace и пр.).
				if (!work.HasTangents)
				{
					MeshUtility.GenerateTangents(sourceVertices, sourceIndices);
				}

				if (finalSkin == null)
				{
					if (options.OptimizeMesh)
					{
						(finalVertices, finalIndices) = MeshUtility.OptimizeMeshData(finalVertices, finalIndices);
					}

					if (options.GenerateLods)
					{
						(finalVertices, finalIndices, lodLevels) =
							MeshUtility.GenerateLodGroupData(finalVertices, finalIndices, options.LodRatios);
					}
				}
				else
				{
					// Скиннед-меш проходит те же проходы, но СШИТОЙ вершиной: meshopt переставляет,
					// склеивает и выбрасывает вершины, не отдавая наружу полную таблицу перестановки,
					// и параллельный скин-стрим после этого разъезжается с геометрией (см. IMeshVertex).
					var packed = MeshUtility.PackSkinned(finalVertices, finalSkin);

					if (options.OptimizeMesh)
					{
						(packed, finalIndices) = MeshUtility.OptimizeMeshData(packed, finalIndices);
					}

					if (options.GenerateLods)
					{
						(packed, finalIndices, lodLevels) =
							MeshUtility.GenerateLodGroupData(packed, finalIndices, options.LodRatios);
					}

					(finalVertices, finalSkin) = MeshUtility.UnpackSkinned(packed);
				}
			}

			preparedMeshes[workIndex] = new PreparedMesh
			{
				Name = work.Name,
				Vertices = finalVertices,
				Indices = finalIndices,
				SkinVertices = finalSkin,
				LodLevels = lodLevels,
				BoundsCenter = boundsCenter,
				BoundsRadius = boundsRadius,
				HasUv = work.HasUv,
				Topology = work.Topology,
			};

			progress?.Report(0.4f + 0.55f * (Interlocked.Increment(ref primitivesDone) / (float)meshWork.Count));
		});

		prepared.Meshes.AddRange(preparedMeshes);

		// Кэш запечённых мешей для нераскладываемых матриц (см. ниже): один меш под несколькими
		// узлами с ОДИНАКОВОЙ мировой матрицей пекётся однажды.
		var bakedMeshCache = new Dictionary<(int MeshId, Matrix4x4 World), int>();

		foreach (var node in model.LogicalNodes)
		{
			if (node.Mesh == null)
			{
				continue;
			}

			// Decompose ПРОВЕРЯЕТСЯ: мировая матрица глубокой иерархии (родительский поворот поверх
			// неравномерного масштаба - Intel Sponza) содержит shear, в TRS не представимый.
			// Decompose тогда возвращает false и МУСОР в out-параметрах - геометрия таких узлов
			// съезжала и перекашивалась. Фоллбек - запечь матрицу прямо в вершины (см. BakeMeshWithMatrix).
			bool trsValid = Matrix4x4.Decompose(node.WorldMatrix, out var scale, out var rotation, out var translation);

			// Та же RH->LH конвертация, что и для вершин выше: зеркалим Z трансляции, а поворот
			// сопрягаем отражением M*R*M (M = diag(1,1,-1)), что для кватерниона даёт (-x,-y,z,w).
			translation.Z = -translation.Z;
			rotation = new Quaternion(-rotation.X, -rotation.Y, rotation.Z, rotation.W);

			foreach (var primitive in node.Mesh.Primitives)
			{
				if (primitiveToMeshIdMap.TryGetValue(primitive, out int meshId))
				{
					// Скиннед-примитив: по спеке glTF трансформация узла с мешом ИГНОРИРУЕТСЯ - меш
					// живёт в пространстве скина, и всё положение задают джойнты. Запечь сюда
					// WorldMatrix значило бы применить трансформацию узла дважды (второй раз - через
					// матрицы джойнтов), и персонаж уезжал бы вдвое дальше от начала координат.
					// Инстанс остаётся единичным: мировое размещение задаёт трансформ ENTITY, а поза -
					// палитра скиннинг-матриц.
					if (prepared.Meshes[meshId].SkinVertices != null)
					{
						prepared.Instances.Add(new InstanceData
						{
							transform = new Transform
							{
								position = Vector3.Zero,
								rotation = Quaternion.Identity,
								scale = Vector3.One,
							},
							meshId = meshId,
							materialId = primitive.Material?.LogicalIndex ?? -1,
						});
						continue;
					}

					if (!trsValid)
					{
						var cacheKey = (meshId, node.WorldMatrix);
						if (!bakedMeshCache.TryGetValue(cacheKey, out int bakedId))
						{
							bakedId = BakeMeshWithMatrix(prepared, meshId, node.WorldMatrix);
							bakedMeshCache[cacheKey] = bakedId;
						}
						meshId = bakedId;
						translation = Vector3.Zero;
						rotation = Quaternion.Identity;
						scale = Vector3.One;
					}

					var material = primitive.Material;
					int materialId = material?.LogicalIndex ?? -1;

					// Не-треугольная топология: инстанс ссылается на материал-клон с подходящим PSO
					// (создаётся в BuildFromPrepared по этому реестру).
					int topology = prepared.Meshes[meshId].Topology;
					if (topology != MeshTopologyTriangles)
					{
						int synthKey = MakeTopologyMaterialKey(topology, materialId);
						prepared.TopologyMaterialClones[synthKey] = (materialId, topology);
						materialId = synthKey;
					}

					prepared.Instances.Add(new InstanceData
					{
						transform = new Transform { position = translation, rotation = rotation, scale = scale },
						meshId = meshId,
						materialId = materialId
					});
				}
			}
		}

		progress?.Report(1f);
		prepared.MsMeshes = swPhase.ElapsedMilliseconds;
		return prepared;
	}

}
