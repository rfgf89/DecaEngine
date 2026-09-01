using System.Numerics;
using System.Runtime.InteropServices;

namespace DecaEngine.Graphics;

[StructLayout(LayoutKind.Sequential)]
public struct ViewData
{
	public Matrix4x4 view;
	public Matrix4x4 viewProj;
	public Vector4 viewport;
	public Vector3 CameraWorldPos;
	private float _pad;
}