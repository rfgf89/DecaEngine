using DecaEngine.Graphics.Core;

namespace DecaEngine.Graphics.Assets;

/// <summary>
/// Печёт текстуры подготовленной модели в .dtex и проставляет слотам ключи кеша. Работает по
/// <c>ModelLoader.PreparedModel</c>, то есть после того, как фоновая фаза загрузки уже декодировала
/// картинки, - собственного разбора glTF здесь нет.
/// </summary>
internal static class ModelAssetBaker
{
	/// <summary>
	/// Печёт все текстурные слоты модели и заполняет им <c>PreparedTexture.CacheKey</c>.
	///
	/// Дедупликация идёт по паре (исходная картинка, настройки импорта), а не по слоту: типовая
	/// ORM-текстура glTF - это ОДИН image, на который ссылаются и MetallicRoughness, и Occlusion, и
	/// без дедупликации BC7-кодирование 4K-картинки честно выполнялось бы дважды на каждый такой
	/// материал. На сценах уровня Sponza это разница между минутами и десятками минут.
	/// </summary>
	public static void BakeTextures(ModelLoader.PreparedModel prepared, AssetCache cache,
		ModelLoadOptions options, CancellationToken cancellationToken)
	{
		cache.EnsureDirectories();

		// Ключ считается по байтам исходника, поэтому один и тот же image в разных материалах даёт
		// одну и ту же строку - словарь лишь избавляет от повторного хеширования и от повторной
		// проверки файла на диске.
		var bakedByImage = new Dictionary<(SharpGLTF.Schema2.Image, string), string>();

		foreach (var material in prepared.Materials)
		{
			if (material.IsNull)
			{
				continue;
			}

			cancellationToken.ThrowIfCancellationRequested();

			BakeSlot(material.BaseColorTexture, TextureSlotKind.BaseColor);
			BakeSlot(material.MetallicRoughnessTexture, TextureSlotKind.MetallicRoughness);
			BakeSlot(material.NormalTexture, TextureSlotKind.Normal);
			BakeSlot(material.OcclusionTexture, TextureSlotKind.Occlusion);
			BakeSlot(material.ThicknessTexture, TextureSlotKind.Thickness);
		}

		void BakeSlot(ModelLoader.PreparedTexture texture, TextureSlotKind kind)
		{
			if (texture?.Pixels == null || texture.SourceImage == null)
			{
				// Слот пуст, или пикселей нет (режим стриминга). Печь нечего - слот останется без
				// ключа, и при загрузке из кеша получит филлер, как и раньше.
				return;
			}

			var settings = TextureImportSettings.AutoFor(kind, options.MaxTextureSize, options.BakeQuality);
			var dedupeKey = (texture.SourceImage, settings.CacheKey());

			if (bakedByImage.TryGetValue(dedupeKey, out var existingKey))
			{
				texture.CacheKey = existingKey;
				return;
			}

			var encoded = texture.SourceImage.Content.Content;
			if (encoded.IsEmpty)
			{
				return;
			}

			var key = AssetCache.TextureKey(encoded.Span, settings);
			var path = cache.TexturePath(key);

			// Файл уже есть - это попадание по СОДЕРЖИМОМУ: ту же картинку с теми же настройками уже
			// пекла другая модель. Именно ради этого текстуры адресуются контентно (см. AssetCache).
			// Ключ проставляется слоту ВСЕГДА, а не только на ветке «файл писали сейчас»: cooked-модель
			// ссылается на текстуру исключительно по нему (CookedModelFile.WriteTexture пишет слот как
			// пустой, если ключа нет). Без этого .dtex честно печётся и ложится на диск, .dmdl пишется
			// без единой текстуры, и модель из кеша приезжает целиком на белых филлерах - причём молча:
			// геометрия и габариты сходятся один в один. Заодно ломался и отбор «дырявых» материалов
			// (HasBaseColorTexture=false), то есть листва теряла альфа-тест в тенях.
			texture.CacheKey = key;
			bakedByImage[dedupeKey] = key;

			if (!File.Exists(path))
			{
				// Параллелизм отдан кодировщику (он режет картинку на блоки), а картинки идут строго
				// по одной. Обратная раскладка - поток на картинку - выглядит соблазнительно, но
				// упирается в память: декод и кодирование держат по полноразмерной копии на поток, и
				// на 4K-текстурах это гигабайты (ровно тем же ограничен декод в PrepareModel).
				var payload = TextureBaker.Bake(texture.Pixels, texture.Width, texture.Height, settings);
				DtexFile.Write(path, payload);

				// Размеры - от ФАКТИЧЕСКИ запечённого нулевого уровня. Повторять здесь арифметику
				// клампа нельзя: он ужимает обе стороны пропорционально, и «обрезать каждую по
				// пределу» разошлось бы с бейком на неквадратных текстурах (4096x2048 при пределе
				// 2048 - это 2048x1024, а не 2048x2048). Разъехавшиеся размеры потом дают неверный
				// номер мип-уровня при стриминге, то есть текстуру не того размера.
				texture.Width = payload.Width;
				texture.Height = payload.Height;
			}
			else if (DtexFile.TryReadHeader(path, out var header))
			{
				// Попадание по содержимому - размеры берём из ЗАГОЛОВКА готового файла, по той же
				// причине, что и веткой выше: у слота сейчас размеры исходника, а в файле лежит
				// прикламленный уровень 0. Разойдясь, они дают стримеру неверный номер мипа.
				texture.Width = header.Width;
				texture.Height = header.Height;
			}
		}
	}

	/// <summary>
	/// Проверяет, что все .dtex, на которые ссылается прочитанная cooked-модель, лежат на месте.
	///
	/// Без этой проверки частично вычищенный кеш (кто-то удалил папку textures, антивирус съел файл)
	/// давал бы модель, у которой .dmdl валиден, а половина материалов молча получает белые филлеры.
	/// Ошибка такого рода не диагностируется вообще - модель просто «выцветает», - поэтому дешевле
	/// объявить промах и перепечь.
	/// </summary>
	public static bool AllTexturesPresent(ModelLoader.PreparedModel prepared, AssetCache cache)
	{
		foreach (var material in prepared.Materials)
		{
			if (material.IsNull)
			{
				continue;
			}

			if (!SlotPresent(material.BaseColorTexture) ||
				!SlotPresent(material.MetallicRoughnessTexture) ||
				!SlotPresent(material.NormalTexture) ||
				!SlotPresent(material.OcclusionTexture) ||
				!SlotPresent(material.ThicknessTexture))
			{
				return false;
			}
		}

		return true;

		bool SlotPresent(ModelLoader.PreparedTexture texture) =>
			texture?.CacheKey == null || File.Exists(cache.TexturePath(texture.CacheKey));
	}
}
