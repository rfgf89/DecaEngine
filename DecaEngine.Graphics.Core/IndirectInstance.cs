using System.Runtime.InteropServices;

namespace DecaEngine.Graphics;

[StructLayout(LayoutKind.Sequential)]
public struct IndirectInstance
{
	public BatchId batchId;
	public int objectId;
}