using System.Numerics;
using System.Runtime.InteropServices;

namespace DecaEngine.Graphics.Diligent;

[StructLayout(LayoutKind.Explicit, Size = 32)]
public struct DrawData
{
	[FieldOffset(0)]
	public Vector4 positionScale;
	[FieldOffset(16)]
	public Vector4 orientation;
};