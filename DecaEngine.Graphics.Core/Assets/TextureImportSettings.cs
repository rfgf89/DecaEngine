
namespace DecaEngine.Graphics.Assets;

/// <summary>Роль текстуры в материале - по ней ассет-пайплайн выбирает формат сжатия автоматически
/// (см. <see cref="TextureImportSettings.AutoFor"/>). glTF не даёт текстурам ни имён, ни отдельных
/// файлов (в .glb картинки лежат внутри контейнера), поэтому вешать настройки на «файл текстуры»
/// негде - единственный устойчивый источник смысла здесь именно слот материала.</summary>
public enum TextureSlotKind
{
	/// <summary>Базовый цвет (sRGB-данные + альфа для cutout/blend).</summary>
	BaseColor,

	/// <summary>glTF metallicRoughness: G = roughness, B = metallic (линейные).</summary>
	MetallicRoughness,

	/// <summary>Карта нормалей в тангенциальном пространстве (линейная).</summary>
	Normal,

	/// <summary>Ambient occlusion (линейная).</summary>
	Occlusion,

	/// <summary>KHR_materials_volume thickness, канал G (линейная).</summary>
	Thickness,
}

/// <summary>Ползунок «время бейка против качества». Кодировщик managed (BCnEncoder.Net), и BC7 на
/// максимуме считается ощутимо дольше - поэтому уровень вынесен наружу, а не зашит.</summary>
public enum TextureBakeQuality
{
	Fast,
	Balanced,
	Best,
}

/// <summary>
/// Что именно ассет-пайплайн делает с одной текстурой: в какой формат жать, до какой стороны
/// ужимать и с каким качеством кодировать. Участвует в ключе кеша целиком (см.
/// <see cref="CacheKey"/>) - смена любого поля обязана приводить к перебейку, иначе на диске
/// останется файл, чьё имя обещает одно, а содержимое другое.
/// </summary>
public readonly record struct TextureImportSettings
{
	public required TextureObjectFormat Format { get; init; }

	/// <summary>Максимальная сторона в текселях; большее ужимается боксом 2x. 0 = без лимита.</summary>
	public required int MaxSize { get; init; }

	public required TextureBakeQuality Quality { get; init; }

	/// <summary>Перенормировать вектор после каждого уменьшения вдвое. Только для карт нормалей:
	/// покомпонентное усреднение четвёрки единичных векторов даёт вектор КОРОЧЕ единицы, и без
	/// перенормировки дальние мипы систематически «сплющивают» рельеф к плоскости.</summary>
	public bool RenormalizeMips { get; init; }

	/// <summary>
	/// Настройки по умолчанию для роли текстуры.
	///
	/// BC7 для всего цветного: 8 бит/тексель (вдвое меньше RGBA8) при качестве, которое на глаз не
	/// отличается от исходника, - в отличие от BC1/BC3, которые заметно рвут градиенты и особенно
	/// плохи на резких границах базового цвета.
	///
	/// BC5 для нормалей: два канала на тех же 8 битах/тексель, то есть на канал приходится вдвое
	/// больше данных, чем у любого трёхканального BC. Z не хранится вовсе - он восстанавливается в
	/// шейдере из XY (тангенциальная нормаль всегда смотрит наружу, z &gt; 0), см.
	/// UnlitInstancedPS.hlsl. Это ровно тот случай, где знание о смысле данных даёт качество
	/// бесплатно, и ради него слот и заведён.
	/// </summary>
	public static TextureImportSettings AutoFor(TextureSlotKind kind, int maxSize, TextureBakeQuality quality)
	{
		var format = kind switch
		{
			TextureSlotKind.Normal => TextureObjectFormat.BC5UNorm,
			_ => TextureObjectFormat.BC7UNorm,
		};

		return new TextureImportSettings
		{
			Format = format,
			MaxSize = maxSize,
			Quality = quality,
			RenormalizeMips = kind == TextureSlotKind.Normal,
		};
	}

	/// <summary>Стабильная строка для ключа кеша. Меняется вместе с любым полем - см. класс-док.</summary>
	public string CacheKey() =>
		$"{(int)Format}-{MaxSize}-{(int)Quality}-{(RenormalizeMips ? 1 : 0)}";
}
