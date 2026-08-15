using DecaEngine.Core.Assets;

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

	/// <summary>Прогрессивный стриминг качества текстур: первый декод (и первая видимая модель) - на
	/// <see cref="StreamInitialTextureSize"/>, а сжатые исходники (PNG/JPG) сохраняются в
	/// <see cref="ModelLoader.StreamedTextures"/> для последующих фоновых ре-декодов на бо́льших
	/// размерах с горячей заменой в живых материалах (см. DecaEngine.Editor.ECS.ModelStreamer).
	/// Меши при этом появляются почти сразу: декод крошечных текстур перестаёт быть самой дорогой
	/// CPU-фазой загрузки.</summary>
	public bool StreamTextures { get; init; }

	/// <summary>Сторона текстур первого декода при <see cref="StreamTextures"/> (дальше качество
	/// поднимается ступенями до <see cref="MaxTextureSize"/>).</summary>
	public int StreamInitialTextureSize { get; init; } = 64;

	public ModelLoadOptions()
	{
	}

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
			MaxTextureSize.ToString(), StreamTextures.ToString(), StreamInitialTextureSize.ToString());
	}
}
