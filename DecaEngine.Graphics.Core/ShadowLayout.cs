namespace DecaEngine.Graphics;

/// <summary>Cascaded shadow layout shared by the frame builder and the backend.</summary>
public static class ShadowLayout
{
	public const int MaxCascades = 4;

	public const int ShadowMapSize = 4096;

	/// <summary>Cascade edge margin in texels for the PCF/PCSS kernel; without it the filter reads the neighboring slice.</summary>
	public const float CascadeMarginTexels = 8f;
}
