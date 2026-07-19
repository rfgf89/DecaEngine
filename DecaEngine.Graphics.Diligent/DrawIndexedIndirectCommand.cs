using System.Runtime.InteropServices;

namespace DecaEngine.Graphics.Diligent;

[StructLayout(LayoutKind.Sequential)]
public struct DrawIndexedIndirectCommand
{
	public uint NumIndices;
	public uint NumInstances;
	public uint FirstIndexLocation;
	public int BaseVertex;
	public uint FirstInstanceLocation;
}