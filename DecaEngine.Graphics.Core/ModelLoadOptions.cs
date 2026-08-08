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
	public float[] LodRatios { get; init; } = DefaultLodRatios;

	public ModelLoadOptions()
	{
	}
}
