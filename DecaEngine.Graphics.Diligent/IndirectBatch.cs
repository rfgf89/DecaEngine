namespace DecaEngine.Graphics.Diligent;

public struct IndirectBatch
{
	public MeshId mesh;
	public MaterialId material;

	public IndirectBatch(MeshId mesh, MaterialId material)
	{
		this.mesh = mesh;
		this.material = material;
	}
}