using System.Numerics;
using System.Runtime.InteropServices;

namespace DecaEngine.Graphics;

[StructLayout(LayoutKind.Sequential)]
public struct GPURenderInstance
{
	public Matrix4x4 modelMatrix;
}