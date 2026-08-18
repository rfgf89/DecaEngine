using DecaEngine.Core.Assets;
using DecaEngine.Graphics.Assets;

namespace DecaEngine.Graphics;

/// <summary>
/// Controls how <see cref="ModelLoader"/> builds materials/meshes for a loaded glTF scene: which
/// shaders to use, and whether to run mesh optimization / LOD generation. Lets a lightweight editor
/// preview (see DecaEngine.Editor.ModelPreviewViewport) skip the same per-primitive optimization work
/// the main scene wants, instead of both paths paying for it unconditionally.
/// </summary>
public readonly struct ModelLoadOptions
{
	public static readonly float[] DefaultLodRatios = [0.5f, 0.25f, 0.1f, 0.05f, 0.0025f];

	public required EditorRef VertexShader { get; init; }
	public required EditorRef PixelShader { get; init; }
	public bool OptimizeMesh { get; init; }
	public bool GenerateLods { get; init; }

	/// <summary>Анизотропная фильтрация (8x) для линейно-фильтруемых текстур модели. Тумблер уровня
	/// ЗАГРУЗКИ: сэмплеры immutable и пекутся в материалы, поэтому смена настройки применяется при
	/// следующей загрузке модели. Текстуры с авторским point-фильтром не трогает.</summary>
	public bool AnisotropicFiltering { get; init; } = true;

	/// <summary>Смещение выбора мип-уровня для сэмплеров текстур модели - log2 масштаба рендера при
	/// темпоральном апскейле (см. IGraphicsApi.CreateSampler). Уровня ЗАГРУЗКИ, как и анизотропия:
	/// сэмплеры immutable, смена масштаба подхватится при следующей загрузке модели.</summary>
	public float MipLodBias { get; init; } = 0f;

	/// <summary>Компилировать в пиксельные варианты фичи Lighting-превью (кейворды
	/// FEATURE_NORMAL_MAPS/OCCLUSION/SHADOWS - см. UnlitInstancedPS.hlsl): live-тумблеры настроек
	/// работают битами ВНУТРИ скомпилированной фичи. false - варианты без кода фич вовсе (для
	/// потребителей без Lighting-превью).</summary>
	public bool PreviewLightingFeatures { get; init; } = true;
	public float[] LodRatios { get; init; } = DefaultLodRatios;

	/// <summary>Максимальная сторона текстур материалов в пикселях; бо́льшие даунскейлятся (бокс 2x)
	/// при фоновом декодировании. Текстуры хранятся несжатым RGBA8 с полной мип-цепочкой (~5.3
	/// байта/пиксель), так что ассеты с сотнями 4K-текстур (Intel Sponza) без лимита кладут VRAM:
	/// одна 4096x4096 - это ~89 МБ, с лимитом 2048 - ~22 МБ. 0 = без лимита. Потребителям с
	/// маленьким выходом (бейкер иконок) имеет смысл ставить сильно меньше.</summary>
	public int MaxTextureSize { get; init; } = 2048;

	/// <summary>Стриминг текстур: фоновая фаза загрузки не декодирует их вовсе (материалы строятся с
	/// 1x1-филлерами), а сжатые исходники (PNG/JPG) сохраняются в
	/// <see cref="ModelLoader.StreamedTextures"/> для фоновых декодов с горячей заменой в живых
	/// материалах (см. DecaEngine.Editor.ECS.ModelStore). Самая дорогая CPU-фаза загрузки уходит из
	/// критического пути: геометрия и материалы готовы почти сразу, а текстуры догружаются по
	/// приоритету от камеры - причём модель, которую ещё никто не показывает, декодируется сразу в
	/// целевой размер и показывается только целиком готовой (ModelStore.ModelTexturesReady).</summary>
	public bool StreamTextures { get; init; }

	/// <summary>Сторона текстур первого декода при <see cref="StreamTextures"/> (дальше качество
	/// поднимается ступенями до <see cref="MaxTextureSize"/>).</summary>
	public int StreamInitialTextureSize { get; init; } = 64;

	/// <summary>
	/// Корень дискового кеша ассет-пайплайна ("&lt;project&gt;/EditorCache/Assets", см.
	/// <see cref="DecaEngine.Graphics.Assets.AssetCache"/>). null/пусто - пайплайн выключен целиком
	/// и загрузка идёт как раньше.
	///
	/// При ПОПАДАНИИ модель не парсится из glTF вовсе: читается .dmdl с уже подготовленной
	/// геометрией и .dtex с блочно-сжатыми текстурами, готовыми к заливке в VRAM как есть. При
	/// ПРОМАХЕ загрузка идёт обычным путём без единого изменения, а бейк ставится в фоновую очередь
	/// (см. <see cref="DecaEngine.Graphics.Assets.AssetBakeQueue"/>) - то есть первое открытие
	/// модели не становится медленнее, чем было, а все последующие становятся быстрее.
	/// </summary>
	public string CacheDirectory { get; init; } = AssetCache.DefaultRoot;

	/// <summary>Качество BC-кодирования при бейке. Кодировщик managed, и BC7 на максимуме считается
	/// ощутимо дольше - на больших сценах разница измеряется минутами фонового бейка.</summary>
	public TextureBakeQuality BakeQuality { get; init; } = TextureBakeQuality.Balanced;

	public ModelLoadOptions()
	{
	}

	/// <summary>Кеш ассетов этой загрузки, либо null, если <see cref="CacheDirectory"/> не задан.</summary>
	public AssetCache Cache => string.IsNullOrEmpty(CacheDirectory) ? null : new AssetCache(CacheDirectory);

	/// <summary>
	/// Stable key capturing every field that changes the SHARED (device-level) load output - the
	/// geometry/textures/material CPU-data a <see cref="DecaEngine.Editor.ECS.ModelStore"/> entry
	/// produces for a given file path. Two loads of the same path with equal <see cref="Signature"/>
	/// are safe to share as ONE ModelLoader; anisotropy/MipLodBias/MaxTextureSize/etc. are baked into
	/// immutable samplers and the texture decoder (see the field docs above), so a mismatch on any of
	/// them means the models are NOT interchangeable and must load (and stay) separate.
	/// </summary>
	public string Signature()
	{
		var ratios = LodRatios ?? DefaultLodRatios;
		var ratioParts = new string[ratios.Length];
		for (int i = 0; i < ratios.Length; i++)
		{
			ratioParts[i] = ratios[i].ToString("R");
		}

		return string.Join('|',
			VertexShader.Path, PixelShader.Path,
			OptimizeMesh.ToString(), GenerateLods.ToString(), AnisotropicFiltering.ToString(),
			MipLodBias.ToString("R"), PreviewLightingFeatures.ToString(), string.Join(',', ratioParts),
			MaxTextureSize.ToString(), StreamTextures.ToString(), StreamInitialTextureSize.ToString(),
			BakeQuality.ToString(), CacheDirectory ?? string.Empty);
	}

	/// <summary>
	/// Подпись только тех полей, от которых зависит СОДЕРЖИМОЕ cooked-модели - ключ кеша (см.
	/// <see cref="DecaEngine.Graphics.Assets.AssetCache.ModelKey"/>).
	///
	/// Отдельная от <see cref="Signature"/> намеренно. Та отвечает на вопрос «можно ли двум
	/// загрузкам делить один ModelLoader» и потому включает и шейдеры, и сэмплеры, и режим
	/// стриминга - ничего из чего в .dmdl не попадает. Считай кеш по ней - и один и тот же файл
	/// пёкся бы заново на каждую комбинацию шейдеров превью, давая побайтово одинаковые .dmdl.
	/// </summary>
	public string CookSignature()
	{
		var ratios = LodRatios ?? DefaultLodRatios;
		var ratioParts = new string[ratios.Length];
		for (int i = 0; i < ratios.Length; i++)
		{
			ratioParts[i] = ratios[i].ToString("R");
		}

		return string.Join('|',
			OptimizeMesh.ToString(), GenerateLods.ToString(), string.Join(',', ratioParts),
			MaxTextureSize.ToString(), BakeQuality.ToString());
	}
}
