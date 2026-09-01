namespace DecaEngine.Graphics;

/// <summary>
/// Раскладка каскадных теней - контракт между СБОРЩИКОМ кадра и бэкендом.
///
/// Эти три числа читает не только ShadowRenderer (который владеет самим атласом): сборщик каскадов
/// в сцене (CullingAndRenderSystem) считает по ним снап текселя и запас фильтра. Пока они жили
/// константами бэкенда, сцена была вынуждена ссылаться на DecaEngine.Graphics.Diligent целиком -
/// ради трёх чисел.
/// </summary>
public static class ShadowLayout
{
	public const int MaxCascades = 4;

	public const int ShadowMapSize = 4096;

	/// <summary>Запас по краю каскада в текселях - под ядро PCF/PCSS: без него фильтр у границы
	/// каскада читает соседний слайс.</summary>
	public const float CascadeMarginTexels = 8f;
}
