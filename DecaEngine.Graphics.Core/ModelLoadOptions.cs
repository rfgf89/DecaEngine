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

	public ModelLoadOptions()
	{
	}
}
