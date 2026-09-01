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

/// <summary>GPU-фаза: инкрементальная финализация PreparedModel в буферы, текстуры и материалы по чанкам между кадрами. Часть <see cref="ModelLoader"/> - файл на фазу; состояние,
/// точки входа загрузки и Release живут в основном файле.</summary>
public partial class ModelLoader
{
	/// <summary>Пошаговое создание GPU-ресурсов готовой <see cref="PreparedModel"/>: итератор
	/// возвращает ОЦЕНКУ байт, залитых в GPU на очередном шаге (текстуры материала / вершины+индексы
	/// меша). Diligent освобождает страницы upload-хипа только на FinishFrame (Present), поэтому
	/// финализация всей модели одним кадром раздувала host-visible память до гигабайт («Space in
	/// dynamic heap is almost exhausted», peak 2.5+ GB). Вызывающий (<see
	/// cref="ModelLoadRequest.FinalizeChunk"/>) двигает итератор, пока не выберет байтовый бюджет
	/// кадра, и продолжает на следующем кадре - <paramref name="result"/> наполняется по мере
	/// движения и валиден только после того, как MoveNext вернул false.</summary>
	private static IEnumerator<long> BuildFromPreparedIncremental(IGraphicsApi graphicsApi, ModelLoadOptions options,
		PreparedModel prepared, ModelLoader result)
	{
		var (vsFactoryPath, vsFileName) = options.VertexShader.ToShaderFactoryParts();
		var (psFactoryPath, psFileName) = options.PixelShader.ToShaderFactoryParts();
		// Шейдеры модели берутся из ОБЩЕГО кэша бэкенда: варианты у разных моделей практически
		// всегда одни и те же, а компиляция идёт синхронно на потоке рендера (см. CreateSharedShader).
		// Материалы модели помечены OwnsShaders=false, так что шарёный экземпляр никто не убьёт.
		// FEATURE_RT_SHADOWS и на ВЕРШИННИКЕ: сам вершинник кейворд не читает, но он переключает
		// компилятор на DXC/SM6.5 (см. DiligentShader) - D3D12 запрещает смешивать DXBC и DXIL в
		// одном PSO, и FXC-вершинник с DXC-пикселем ломал создание пайплайна.
		var vsKeywords = options.RtShadows ? new[] { "FEATURE_RT_SHADOWS" } : null;
		var modelShaderVs = graphicsApi.CreateSharedShader("Model Vertex Shader", vsFactoryPath, vsFileName,
			ShaderObjectType.Vertex, keywords: vsKeywords);
		result._ownedShaders.Add(modelShaderVs);

		// Пиксельные ВАРИАНТЫ по shader keywords (см. шапку UnlitInstancedPS.hlsl): эффекты,
		// статически известные по материалу (текстуры, transmission, dispersion, alpha clip),
		// вырезаются из кода компиляцией вместо рантайм-веток по cbuffer-флагам. Кэш - материалы
		// с одинаковым набором ключей делят один скомпилированный шейдер.
		var pixelShaderVariants = new Dictionary<string, IShaderObject>();

		IShaderObject GetPixelShaderVariant(List<string> keywords)
		{
			keywords.Sort(StringComparer.Ordinal);
			string cacheKey = string.Join(";", keywords);

			if (!pixelShaderVariants.TryGetValue(cacheKey, out var shader))
			{
				var swShader = System.Diagnostics.Stopwatch.StartNew();
				shader = graphicsApi.CreateSharedShader(
					cacheKey.Length == 0 ? "Model Pixel Shader" : $"Model Pixel Shader [{cacheKey}]",
					psFactoryPath, psFileName, ShaderObjectType.Pixel, "Main", keywords.ToArray());
				pixelShaderVariants[cacheKey] = shader;
				result._ownedShaders.Add(shader);

				result._shaderMs += swShader.ElapsedMilliseconds;
				result._shaderVariants++;
			}

			return shader;
		}

		// pm == null - встроенный дефолтный материал (без текстур/расширений).
		List<string> BuildMaterialKeywords(PreparedMaterial pm) => BuildKeywordsFromPrepared(options, pm);

		var defaultMaterial = graphicsApi.CreateMaterial("Default Material");

		// Шейдеры шареные - см. IMaterialObject.OwnsShaders. Этот материал вдобавок раздаётся
		// НЕСКОЛЬКИМ логическим индексам (все null-материалы модели ссылаются на один объект),
		// так что его Release зовётся из ModelLoader.Release столько же раз - ещё одна причина не
		// давать ему трогать шейдеры.
		defaultMaterial.OwnsShaders = false;
		defaultMaterial.SetShader(GetPixelShaderVariant(BuildMaterialKeywords(null)), modelShaderVs);

		// Белый 1x1-филлер для _MainTex/_MetallicRoughnessTex у материалов без соответствующей
		// текстуры: пиксельный шейдер статически ссылается на оба слота (ветвление по
		// PbrHas*Texture - динамическое), поэтому непривязанный дескриптор - это undefined
		// behavior на Vulkan (validation VUID-vkCmdDrawIndexedIndirect-None-08114), а не
		// безобидный «нулевой» сэмпл. Один общий на модель, создаётся лениво.
		Texture fallbackTexture = null;
		ISamplerObject fallbackSampler = null;

		// Отдельный филлер для _NormalTex: белый пиксель распаковался бы в наклонённую нормаль
		// (1,1,1)->(1,1,1), а "плоский" (128,128,255) -> (0,0,1) оставляет геометрическую.
		Texture flatNormalTexture = null;

		// Создаёт (лениво) оба 1x1-филлера, не привязывая их ни к какому слоту: стриминг ставит их
		// сам, со СВОИМ (авторским) сэмплером - см. BindPreparedTexture.
		void EnsureFallbackTextures()
		{
			if (fallbackTexture == null)
			{
				fallbackTexture = new Texture("Model Fallback White", new CpuTextureData
				{
					Name = "Model Fallback White",
					DecodedPixels = new byte[] { 255, 255, 255, 255 },
					DecodedWidth = 1,
					DecodedHeight = 1,
				});
				fallbackTexture.Upload(graphicsApi, true);
				result._ownedTextures.Add(fallbackTexture.GpuHandle);

				fallbackSampler = graphicsApi.CreateSampler(
					name: "Model Fallback Sampler",
					filter: TextureFilter.Point,
					address: TextureAddress.Wrap,
					comparisonFunction: CompFunction.Always,
					border: Vector4.Zero);
			}

			if (flatNormalTexture == null)
			{
				flatNormalTexture = new Texture("Model Fallback Flat Normal", new CpuTextureData
				{
					Name = "Model Fallback Flat Normal",
					DecodedPixels = new byte[] { 128, 128, 255, 255 },
					DecodedWidth = 1,
					DecodedHeight = 1,
				});
				flatNormalTexture.Upload(graphicsApi, true);
				result._ownedTextures.Add(flatNormalTexture.GpuHandle);
			}
		}

		void BindFallbackTexture(IMaterialObject material, string slot)
		{
			if (fallbackTexture == null)
			{
				fallbackTexture = new Texture("Model Fallback White", new CpuTextureData
				{
					Name = "Model Fallback White",
					DecodedPixels = new byte[] { 255, 255, 255, 255 },
					DecodedWidth = 1,
					DecodedHeight = 1,
				});
				fallbackTexture.Upload(graphicsApi, true);
				result._ownedTextures.Add(fallbackTexture.GpuHandle);

				fallbackSampler = graphicsApi.CreateSampler(
					name: "Model Fallback Sampler",
					filter: TextureFilter.Point,
					address: TextureAddress.Wrap,
					comparisonFunction: CompFunction.Always,
					border: Vector4.Zero);
			}

			material.SetTexture(slot, fallbackTexture.GpuHandle);
			material.SetImmutableSampler(slot, fallbackSampler);
		}

		void BindFlatNormalFallback(IMaterialObject material)
		{
			// Белый филлер создаёт общий сэмплер - гарантируем его наличие.
			if (fallbackSampler == null)
			{
				BindFallbackTexture(material, "_NormalTex");
			}

			if (flatNormalTexture == null)
			{
				flatNormalTexture = new Texture("Model Fallback Flat Normal", new CpuTextureData
				{
					Name = "Model Fallback Flat Normal",
					DecodedPixels = new byte[] { 128, 128, 255, 255 },
					DecodedWidth = 1,
					DecodedHeight = 1,
				});
				flatNormalTexture.Upload(graphicsApi, true);
				result._ownedTextures.Add(flatNormalTexture.GpuHandle);
			}

			material.SetTexture("_NormalTex", flatNormalTexture.GpuHandle);
			material.SetImmutableSampler("_NormalTex", fallbackSampler);
		}

		BindFallbackTexture(defaultMaterial, "_MainTex");
		BindFallbackTexture(defaultMaterial, "_OcclusionTex");
		BindFlatNormalFallback(defaultMaterial);

		// Все три филлера гарантированно созданы к этой точке (вызовы выше) - публикуем их на модели
		// для BuildAdditionalMaterialSet (см. поле-комментарии у FallbackWhiteTexture/FallbackSampler/
		// FallbackFlatNormalTexture).
		result.FallbackWhiteTexture = fallbackTexture.GpuHandle;
		result.FallbackSampler = fallbackSampler;
		result.FallbackFlatNormalTexture = flatNormalTexture.GpuHandle;

		result.materialObjects.Add(-1, defaultMaterial);

		// The built-in default material is not a glTF material, so the spec's metallic=1 default
		// would make it preview as a dark mirror in Lighting mode - neutral dielectric gray instead.
		var defaultPbr = new MaterialPbrFactors
		{
			BaseColorFactor = Vector4.One,
			MetallicFactor = 0f,
			RoughnessFactor = 0.6f,
			HasBaseColorTexture = false,
			AlphaCutoff = 0f,
			Ior = 1.5f,
			VolumeAttenuation = new Vector4(1f, 1f, 1f, 0f),
			NormalScale = 1f,
			OcclusionStrength = 1f,
			SpecularColorFactor = Vector4.One
		};
		result.MaterialPbr[-1] = defaultPbr;

		// Шейдеры + дефолтный материал с 1x1-филлерами - копейки, но это удобная точка отсечки
		// перед первым «тяжёлым» материалом.
		yield return 4096;

		// Оценка залитых в GPU байт при материализации материала: сумма несжатых RGBA-пикселей
		// его текстур (каждый Bind* делает отдельный Upload, так что считаем по слотам).
		static long EstimateMaterialBytes(PreparedMaterial pm)
		{
			if (pm == null)
			{
				return 4096;
			}

			// В режиме стриминга Pixels у всех каналов null (заливки на этой фазе нет вовсе) - оценка
			// честно выходит в «почти ноль», и финализация материалов не тратит кадровый бюджет.
			long bytes = 4096;
			bytes += SlotBytes(pm.BaseColorTexture);
			bytes += SlotBytes(pm.MetallicRoughnessTexture);
			bytes += SlotBytes(pm.NormalTexture);
			bytes += SlotBytes(pm.OcclusionTexture);
			bytes += pm.TransmissionFactor > 0f ? SlotBytes(pm.ThicknessTexture) : 0;
			return bytes;

			// Запечённый слот пикселей не несёт, но заливка в VRAM всё равно стоит времени, и
			// пропорциональна она объёму данных: BC7/BC5 - байт на тексель, плюс треть на хвост
			// мип-цепочки. Считать такие слоты бесплатными значило бы финализировать всю сцену
			// одним куском в одном кадре.
			static long SlotBytes(PreparedTexture texture)
			{
				if (texture == null)
				{
					return 0;
				}

				if (texture.Pixels != null)
				{
					return texture.Pixels.Length;
				}

				return texture.CacheKey != null
					? (long)texture.Width * texture.Height * 4 / 3
					: 0;
			}
		}

		// KHR_materials_volume: толщина задана в ЛОКАЛЬНЫХ координатах меша и по спеке умножается
		// на масштаб узла (у Khronos-семплов DragonAttenuation/DragonDispersion узел дракона имеет
		// scale 0.25 - без учёта масштаба экспонента Beer-Lambert завышается в 4 раза, и янтарное
		// стекло глушится в тёмно-красное, а слегка голубоватое - в тёмно-синее). Толщина -
		// per-material, масштаб - per-instance; для превью берём масштаб первого инстанса,
		// использующего материал (модели с volume-стеклом практически всегда один узел на меш).
		var materialScales = new Dictionary<int, float>();
		foreach (var instance in prepared.Instances)
		{
			var s = instance.transform.scale;
			materialScales.TryAdd(instance.materialId, (s.X + s.Y + s.Z) / 3f);
		}

		// Реестр стрим-текстур по исходнику: один image шарится несколькими слотами/материалами
		// (типовая ORM-текстура), апгрейд декодируется один раз и раскладывается по всем привязкам.
		var streamEntries = new Dictionary<TextureStreamSource, StreamedTexture>();

		// Кеш ассетов этой загрузки: из него берутся запечённые .dtex, когда модель пришла из .dmdl.
		// Один экземпляр на всю финализацию - он всего лишь держит пути, но создавать его на каждый
		// из сотен слотов незачем.
		var assetCache = options.Cache;

		// Уже созданные GPU-текстуры по ключу кеша. Одна запечённая картинка (типовая ORM) шарится
		// несколькими слотами и материалами; без этой карты один и тот же .dtex читался бы с диска и
		// заливался в VRAM столько раз, сколько на него ссылок, - то есть кеш экономил бы время
		// загрузки и при этом РАЗДУВАЛ бы видеопамять против некешированного пути.
		var bakedTextures = new Dictionary<string, IGpuTexture>(StringComparer.Ordinal);

		// Записи стриминга запечённых текстур по ключу кеша - тот же приём, что и streamEntries выше:
		// одна .dtex, на которую ссылаются несколько слотов, обязана стримиться ОДНОЙ записью, иначе
		// её ступени читались бы и заливались по разу на ссылку.
		var bakedStreamEntries = new Dictionary<string, StreamedTexture>(StringComparer.Ordinal);

		// Читает .dtex и создаёт GPU-текстуру, разделяя результат между всеми слотами с тем же
		// ключом. null - файла нет (кеш чистили прямо во время загрузки).
		IGpuTexture GetOrCreateBakedTexture(string cacheKey, string slot)
		{
			if (bakedTextures.TryGetValue(cacheKey, out var existing))
			{
				return existing;
			}

			if (assetCache == null)
			{
				return null;
			}

			var payload = DtexFile.TryRead(assetCache.TexturePath(cacheKey));
			if (payload == null)
			{
				return null;
			}

			// Тот же замер, что и у обычного пути: именно по нему видно, что кеш действительно
			// убирает время из финализации, а не переносит его в другое место.
			var swBaked = System.Diagnostics.Stopwatch.StartNew();
			var texture = new Texture(slot, payload.ToCpuTextureData(slot));
			texture.Upload(graphicsApi, true);
			result._textureMs += swBaked.ElapsedMilliseconds;
			result._textureCount++;

			result._ownedTextures.Add(texture.GpuHandle);
			bakedTextures[cacheKey] = texture.GpuHandle;
			return texture.GpuHandle;
		}

		// Возвращает привязку (текстура + сэмплер + запись стриминга) - её переиспользует теневой
		// материал с альфа-тестом (см. ModelLoader.MaterialBaseColor). null - слот получил филлер.
		BaseColorBinding BindPreparedTexture(IMaterialObject materialObj, string slot, PreparedTexture preparedTexture)
		{
			if (preparedTexture == null)
			{
				// Белый филлер (для _ThicknessTex G=1 -> толщина остаётся чистым factor-ом).
				BindFallbackTexture(materialObj, slot);
				return null;
			}

			// Режим стриминга: пикселей ещё нет вовсе - слот получает общий 1x1-филлер (белый, для
			// _NormalTex - плоская нормаль), а первая ступень приедет из ModelStreamer. Заливать
			// здесь нечего, поэтому финализация материалов стоит копейки и геометрия появляется
			// почти сразу. Кейворды шейдера при этом ТЕ ЖЕ (ставятся по наличию текстуры в glTF),
			// так что апгрейд не трогает PSO.
			if (preparedTexture.StreamSource != null)
			{
				if (!streamEntries.TryGetValue(preparedTexture.StreamSource, out var streamEntry))
				{
					streamEntry = new StreamedTexture
					{
						FilePath = preparedTexture.StreamSource.FilePath,
						EncodedPixels = preparedTexture.StreamSource.EncodedBytes,
						CurrentSize = 0,
						TargetSize = options.MaxTextureSize,
						Texture = null,
						AddressMode = preparedTexture.AddressMode,
						FilterMode = preparedTexture.FilterMode,
					};

					streamEntries[preparedTexture.StreamSource] = streamEntry;
					result.StreamedTextures.Add(streamEntry);
				}

				// Текстура-филлер - общая 1x1 (белая; для нормалей плоская), а вот СЭМПЛЕР ставится
				// сразу авторский: он immutable и печётся в layout PSO, то есть подменить его при
				// апгрейде уже нельзя - фоллбечный Point/Wrap остался бы с текстурой навсегда.
				EnsureFallbackTextures();
				materialObj.SetTexture(slot, slot == "_NormalTex"
					? flatNormalTexture.GpuHandle
					: fallbackTexture.GpuHandle);

				var streamFilter = preparedTexture.FilterMode == TextureFilter.Linear && options.AnisotropicFiltering
					? TextureFilter.Anisotropic
					: preparedTexture.FilterMode;

				var streamSampler = graphicsApi.CreateSampler(
					name: slot + "_Sampler",
					filter: streamFilter,
					address: preparedTexture.AddressMode,
					comparisonFunction: CompFunction.Always,
					border: Vector4.Zero,
					mipLodBias: options.MipLodBias);

				// Динамический сэмплер (на texture view), а не immutable, - как в прямом пути ниже:
				// immutable для батч-материалов был мёртв из-за PSO-кэша (см. там же), а стримингу
				// динамический ещё и роднее - при горячей замене текстуры SetTexture сам перевесит
				// его на новый view (см. DiligentMaterial.SetTexture).
				materialObj.SetSampler(slot + "_sampler", streamSampler);
				result._samplerCount++;

				streamEntry.Bindings.Add((materialObj, slot));

				// Текстура здесь - общий 1x1-филлер; теневому материалу важна не она, а ЗАПИСЬ
				// стриминга: он подпишется на неё и получит те же ступени качества.
				return new BaseColorBinding
				{
					Texture = slot == "_NormalTex" ? flatNormalTexture.GpuHandle : fallbackTexture.GpuHandle,
					Sampler = streamSampler,
					Stream = streamEntry,
				};
			}

			// Запечённая текстура: мип-цепочка лежит на диске готовой к заливке. Ни декода, ни
			// RGBA8-буфера, ни GenerateMips на GPU.
			if (preparedTexture.CacheKey != null)
			{
				var bakedFilter = preparedTexture.FilterMode == TextureFilter.Linear && options.AnisotropicFiltering
					? TextureFilter.Anisotropic
					: preparedTexture.FilterMode;

				var bakedSampler = graphicsApi.CreateSampler(
					name: slot + "_Sampler",
					filter: bakedFilter,
					address: preparedTexture.AddressMode,
					comparisonFunction: CompFunction.Always,
					border: Vector4.Zero,
					mipLodBias: options.MipLodBias);

				result._samplerCount++;

				// Стриминг поверх кеша: слот получает 1x1-филлер и запись стриминга, а ступени
				// приезжают ХВОСТАМИ мип-цепочки прямо из .dtex (см. ModelStore). Верхние - самые
				// тяжёлые - уровни при этом не читаются с диска вовсе, пока качество до них не дошло,
				// и ни одна ступень не стоит ни декода, ни пересжатия.
				if (options.StreamTextures && assetCache != null)
				{
					if (!bakedStreamEntries.TryGetValue(preparedTexture.CacheKey, out var bakedStream))
					{
						bakedStream = new StreamedTexture
						{
							DtexPath = assetCache.TexturePath(preparedTexture.CacheKey),
							DtexWidth = preparedTexture.Width,
							DtexHeight = preparedTexture.Height,
							IsBlockCompressed = true,
							CurrentSize = 0,

							// Потолок качества - СОБСТВЕННЫЙ верхний уровень .dtex, а не предел
							// импорта: файл уже запечён с этим пределом, и мелкий исходник (256px при
							// пределе 2048) иначе вечно считался бы «недогруженным» - стример гонялся
							// бы за качеством, которого в файле нет.
							TargetSize = Math.Max(preparedTexture.Width, preparedTexture.Height),
							Texture = null,
							AddressMode = preparedTexture.AddressMode,
							FilterMode = preparedTexture.FilterMode,
						};

						bakedStreamEntries[preparedTexture.CacheKey] = bakedStream;
						result.StreamedTextures.Add(bakedStream);
					}

					EnsureFallbackTextures();
					var filler = slot == "_NormalTex" ? flatNormalTexture.GpuHandle : fallbackTexture.GpuHandle;

					materialObj.SetTexture(slot, filler);
					materialObj.SetSampler(slot + "_sampler", bakedSampler);
					bakedStream.Bindings.Add((materialObj, slot));

					return new BaseColorBinding
					{
						Texture = filler,
						Sampler = bakedSampler,
						Stream = bakedStream,
					};
				}

				var bakedTexture = GetOrCreateBakedTexture(preparedTexture.CacheKey, slot);
				if (bakedTexture == null)
				{
					// .dtex исчез между проверкой кеша и заливкой (кто-то чистил папку прямо во время
					// загрузки). Пикселей в cooked-модели нет и взять их неоткуда, поэтому слот
					// получает филлер - следующая загрузка увидит промах и перепечёт.
					BindFallbackTexture(materialObj, slot);
					return null;
				}

				materialObj.SetTexture(slot, bakedTexture);
				materialObj.SetSampler(slot + "_sampler", bakedSampler);

				return new BaseColorBinding
				{
					Texture = bakedTexture,
					Sampler = bakedSampler,
				};
			}

			IGpuTexture gpuTexture;
			{
				var cpuData = new CpuTextureData
				{
					Name = slot,
					DecodedPixels = preparedTexture.Pixels,
					DecodedWidth = preparedTexture.Width,
					DecodedHeight = preparedTexture.Height,
				};

				var texture = new Texture(cpuData.Name, cpuData);

				// Замер отдельно от остальной финализации: она оказалась 80% времени загрузки и при этом
				// почти не зависит от ОБЪЁМА текстур - значит цена не в байтах, а в вызовах, и надо
				// знать, в каких именно.
				var swUpload = System.Diagnostics.Stopwatch.StartNew();
				texture.Upload(graphicsApi, true);
				result._textureMs += swUpload.ElapsedMilliseconds;
				result._textureCount++;

				gpuTexture = texture.GpuHandle;
				result._ownedTextures.Add(gpuTexture);
			}

			// Линейные текстуры апгрейдятся до анизотропных (тумблер в ModelLoadOptions) - без
			// этого доска/пол мылятся под острым углом; авторский point-фильтр сохраняется.
			var filterMode = preparedTexture.FilterMode == TextureFilter.Linear && options.AnisotropicFiltering
				? TextureFilter.Anisotropic
				: preparedTexture.FilterMode;

			var swSampler = System.Diagnostics.Stopwatch.StartNew();
			var samplerObject = graphicsApi.CreateSampler(
				name: slot + "_Sampler",
				filter: filterMode,
				address: preparedTexture.AddressMode,
				comparisonFunction: CompFunction.Always,
				border: Vector4.Zero,
				mipLodBias: options.MipLodBias
			);
			result._samplerMs += swSampler.ElapsedMilliseconds;
			result._samplerCount++;

			materialObj.SetTexture(slot, gpuTexture);

			// ДИНАМИЧЕСКАЯ привязка (сэмплер вешается на texture view), а не SetImmutableSampler:
			// immutable-путь для батч-материалов молча не срабатывает - Diligent подставляет дефолтный
			// сэмплер (linear wrap), и все ручки (анизотропия, mip bias) оказываются мёртвыми.
			// Замерено пробником: кадры с ANISO=0/1 и MIPBIAS=+4 были БИТ-В-БИТ одинаковыми.
			materialObj.SetSampler(slot + "_sampler", samplerObject);

			return new BaseColorBinding { Texture = gpuTexture, Sampler = samplerObject, Stream = null };
		}

		// Записывает РЕАЛЬНУЮ (не филлер) привязку слота в result.MaterialTextureBindings под ключом
		// материала - см. поле-комментарий. Единственный писатель этого словаря.
		void TrackBinding(int materialKey, string slot, BaseColorBinding binding)
		{
			if (binding == null)
			{
				return;
			}

			if (!result.MaterialTextureBindings.TryGetValue(materialKey, out var slots))
			{
				slots = new Dictionary<string, BaseColorBinding>();
				result.MaterialTextureBindings[materialKey] = slots;
			}

			slots[slot] = binding;
		}

		// vs передаётся параметром (а не правится повторным SetShader): DiligentMaterial.SetShader
		// release-ит ранее установленные шейдеры, а они шарятся между материалами - повторный вызов
		// на живом наборе роняет процесс двойным освобождением.
		//
		// materialKey - ключ, под которым будет зарегистрирован ИТОГОВЫЙ материал в
		// result.materialObjects (обычный логический индекс или синтетический ключ клона топологии,
		// см. MakeTopologyMaterialKey) - нужен только чтобы разложить реальные привязки текстур в
		// result.MaterialTextureBindings (см. TrackBinding) для BuildAdditionalMaterialSet.
		IMaterialObject BuildMaterialObject(PreparedMaterial pm, string name, IShaderObject vs, int materialKey,
			out BaseColorBinding baseColor)
		{
			var swCreate = System.Diagnostics.Stopwatch.StartNew();
			var materialObj = graphicsApi.CreateMaterial(name);

			// Шейдеры ШАРЕНЫЕ между материалами модели (вариантный кэш + один VS): освобождать их
			// материалу нельзя - это декремент чужого счётчика ссылок и падение на следующем
			// материале. См. IMaterialObject.OwnsShaders и ModelLoader.Release.
			materialObj.OwnsShaders = false;
			result._matCreateMs += swCreate.ElapsedMilliseconds;

			var swSetShader = System.Diagnostics.Stopwatch.StartNew();
			materialObj.SetShader(GetPixelShaderVariant(BuildMaterialKeywords(pm)), vs);
			result._matShaderMs += swSetShader.ElapsedMilliseconds;

			baseColor = BindPreparedTexture(materialObj, "_MainTex", pm.BaseColorTexture);
			TrackBinding(materialKey, "_MainTex", baseColor);

			// Слот объявлен в шейдере только под HAS_MR_TEXTURE (см. UnlitInstancedPS.hlsl) - этот
			// кейворд ставится только когда у материала реально есть MR-текстура, так что фоллбек
			// тут не нужен и не должен биндиться (иначе immutable sampler без ресурса в шейдере).
			if (pm.MetallicRoughnessTexture != null)
			{
				TrackBinding(materialKey, "_MetallicRoughnessTex",
					BindPreparedTexture(materialObj, "_MetallicRoughnessTex", pm.MetallicRoughnessTexture));
			}

			// Слот объявлен в шейдере только под MATERIAL_TRANSMISSION (см. UnlitInstancedPS.hlsl) -
			// у остальных материалов кейворд выключен, и биндить нечего.
			if (pm.TransmissionFactor > 0f)
			{
				TrackBinding(materialKey, "_ThicknessTex",
					BindPreparedTexture(materialObj, "_ThicknessTex", pm.ThicknessTexture));
			}

			if (pm.NormalTexture != null)
			{
				TrackBinding(materialKey, "_NormalTex",
					BindPreparedTexture(materialObj, "_NormalTex", pm.NormalTexture));
			}
			else
			{
				BindFlatNormalFallback(materialObj);
			}

			// Белый филлер (R=1) = "ничего не заслонено" - has-флаг не нужен.
			TrackBinding(materialKey, "_OcclusionTex",
				BindPreparedTexture(materialObj, "_OcclusionTex", pm.OcclusionTexture));

			return materialObj;
		}

		// scaleKey - ключ, под которым ИНСТАНСЫ ссылаются на материал (для клонов топологий это
		// синтетический ключ, см. MakeTopologyMaterialKey), т.к. materialScales собран по инстансам.
		MaterialPbrFactors BuildFactors(PreparedMaterial pm, int scaleKey)
		{
			var averageBaseColor = ModelImporter.ComputeAverageBaseColor(pm);
			return new MaterialPbrFactors
			{
			BaseColorFactor = pm.BaseColorFactor,
			AverageBaseColor = new Vector3(averageBaseColor.X, averageBaseColor.Y, averageBaseColor.Z),
			AverageAlpha = averageBaseColor.W,
			MetallicFactor = pm.MetallicFactor,
			RoughnessFactor = pm.RoughnessFactor,
			HasBaseColorTexture = pm.BaseColorTexture != null,
			HasMetallicRoughnessTexture = pm.MetallicRoughnessTexture != null,
			NormalScale = pm.NormalScale,
			OcclusionStrength = pm.OcclusionStrength,
			OcclusionUvSet = pm.OcclusionUvSet,
			UvTransform = pm.UvTransform,
			UvOffset = pm.UvOffset,
			HasUvTransform = pm.HasUvTransform,
			AlphaCutoff = pm.AlphaCutoff,
			AlphaMode = pm.AlphaMode,
			SoftAlphaFraction = pm.SoftAlphaFraction,
			TransmissionFactor = pm.TransmissionFactor,
			Ior = pm.Ior,
			Dispersion = pm.Dispersion,
			SheenColorRoughness = new Vector4(pm.SheenColorFactor, pm.SheenRoughnessFactor),
			SpecularColorFactor = new Vector4(pm.SpecularColorFactor, pm.SpecularFactor),
			VolumeAttenuation = ScaleVolumeAttenuation(pm, materialScales, scaleKey),
			ThicknessWorld = pm.ThicknessFactor *
				(materialScales.TryGetValue(scaleKey, out var nodeScale) && nodeScale > 0f ? nodeScale : 1f)
			};
		}

		foreach (var preparedMaterial in prepared.Materials)
		{
			if (preparedMaterial.IsNull)
			{
				result.materialObjects.Add(preparedMaterial.LogicalIndex, defaultMaterial);
				result.MaterialPbr[preparedMaterial.LogicalIndex] = defaultPbr;
				continue;
			}

			var swMat = System.Diagnostics.Stopwatch.StartNew();
			var builtMaterial = BuildMaterialObject(preparedMaterial, preparedMaterial.Name, modelShaderVs,
				preparedMaterial.LogicalIndex, out var builtBaseColor);
			result._materialMs += swMat.ElapsedMilliseconds;
			result._materialCount++;

			if (builtBaseColor != null)
			{
				result.MaterialBaseColor[preparedMaterial.LogicalIndex] = builtBaseColor;
			}

			result.materialObjects.Add(preparedMaterial.LogicalIndex, builtMaterial);
			result.MaterialPbr[preparedMaterial.LogicalIndex] =
				BuildFactors(preparedMaterial, preparedMaterial.LogicalIndex);

			yield return EstimateMaterialBytes(preparedMaterial);
		}

		// Материалы-клоны под не-треугольные топологии (см. PrepareModel): тот же шейдинг и
		// текстуры, но отдельный объект материала - RegisterModelResources назначит ему PSO с
		// нужной PrimitiveTopology, а батч-рендерер и так группирует индирект-дроу по материалу,
		// так что смешение топологий в одной модели больше ничего не требует.
		IShaderObject pointShaderVs = null;

		foreach (var (synthKey, clone) in prepared.TopologyMaterialClones)
		{
			PreparedMaterial source = null;
			if (clone.SourceMaterial >= 0)
			{
				source = prepared.Materials.Find(m => m.LogicalIndex == clone.SourceMaterial && !m.IsNull);
			}

			// PSO с POINT_LIST обязан писать builtin PointSize из VS (Vulkan
			// VUID-VkGraphicsPipelineCreateInfo-topology-08773) - точечным клонам достаётся
			// *PointVS-вариант, лежащий рядом со штатным (конвенция имени; для нестандартного VS
			// из опций остаётся обычный - валидация ругнётся, но на большинстве драйверов рендер
			// работает).
			var cloneVs = modelShaderVs;
			if (clone.Topology == MeshTopologyPoints)
			{
				if (pointShaderVs == null && vsFileName == "UnlitInstancedVS.hlsl")
				{
					// Добавляется в _ownedShaders сразу после создания - см. ниже. Кейворды - как у
					// основного вершинника (DXC-паритет с RT-вариантом пикселя).
					pointShaderVs = graphicsApi.CreateSharedShader("Model Point Vertex Shader", vsFactoryPath,
						"UnlitInstancedPointVS.hlsl", ShaderObjectType.Vertex, keywords: vsKeywords);
					result._ownedShaders.Add(pointShaderVs);
				}

				cloneVs = pointShaderVs ?? modelShaderVs;
			}

			IMaterialObject materialObj;
			MaterialPbrFactors factors;
			if (source == null)
			{
				materialObj = graphicsApi.CreateMaterial($"Default Material (topology {clone.Topology})");

				// Шейдеры здесь ШАРЕНЫЕ (вариантный кэш + один VS на модель) - освобождает их
				// ModelLoader.Release, по разу на каждый. См. IMaterialObject.OwnsShaders.
				materialObj.OwnsShaders = false;
				materialObj.SetShader(GetPixelShaderVariant(BuildMaterialKeywords(null)), cloneVs);
				BindFallbackTexture(materialObj, "_MainTex");
				BindFallbackTexture(materialObj, "_OcclusionTex");
				BindFlatNormalFallback(materialObj);
				factors = defaultPbr;
			}
			else
			{
				materialObj = BuildMaterialObject(source, $"{source.Name} (topology {clone.Topology})", cloneVs,
					synthKey, out var cloneBaseColor);
				factors = BuildFactors(source, synthKey);

				if (cloneBaseColor != null)
				{
					result.MaterialBaseColor[synthKey] = cloneBaseColor;
				}
			}

			factors.Topology = clone.Topology;
			result.materialObjects.Add(synthKey, materialObj);
			result.MaterialPbr[synthKey] = factors;

			yield return EstimateMaterialBytes(source);
		}

		foreach (var preparedMesh in prepared.Meshes)
		{
			var swMesh = System.Diagnostics.Stopwatch.StartNew();
			var meshObj = graphicsApi.CreateMesh(preparedMesh.Name);
			meshObj.SetVertices(preparedMesh.Vertices);
			meshObj.SetIndices(preparedMesh.Indices);
			result._meshMs += swMesh.ElapsedMilliseconds;
			result._meshCount++;
			meshObj.SetBounds(preparedMesh.BoundsCenter, preparedMesh.BoundsRadius);

			if (preparedMesh.LodLevels != null)
			{
				UploadLodGroup(meshObj, preparedMesh.LodLevels);
			}

			result.Meshes.Add(meshObj);
			result.MeshHasUv.Add(preparedMesh.HasUv);
			result.MeshSkin.Add(preparedMesh.SkinVertices);

			yield return (long)preparedMesh.Vertices.Length * VertexSizeBytes + (long)preparedMesh.Indices.Length * sizeof(uint);
		}

		result.Skeleton = prepared.Skeleton;
		result.Animations.AddRange(prepared.Animations);
		result.instances.AddRange(prepared.Instances);

		// Потриугольное альбедо из текстур - пока CPU-пиксели base color ещё живы (после
		// финализации они освобождаются). Потребитель - probe-GI бейкер: цвет отскока и
		// RT-отражений в разрешении треугольников вместо одного среднего на материал.
		ModelImporter.ComputeTriangleAlbedoFromTextures(result, prepared);
	}

}
