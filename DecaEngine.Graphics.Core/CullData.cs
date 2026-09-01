using System.Numerics;
using System.Runtime.InteropServices;

namespace DecaEngine.Graphics;

[StructLayout(LayoutKind.Sequential)]
public struct CullData
{
	public Matrix4x4 view;

	public float P00, P11, znear, zfar; // symmetric projection parameters
	public Vector4 frustum; // data for left/right/top/bottom frustum planes
	public float lodTarget; // lod target error at z=1

	public int drawCount;
	public int cullFrustum;
};