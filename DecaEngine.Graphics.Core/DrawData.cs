using System.Numerics;
using System.Runtime.InteropServices;

namespace DecaEngine.Graphics;

[StructLayout(LayoutKind.Explicit, Size = 48)]
public struct DrawData
{
	[FieldOffset(0)]
	public Vector4 positionScale;
	[FieldOffset(16)]
	public Vector4 orientation;

	/// <summary>xyz = per-component instance scale. Culling in BatchingInstancingCS must scale the
	/// mesh bounds center with this, not the max scale (positionScale.w, kept for radius/LOD):
	/// the max shifts the center off non-uniform-scaled instances and breaks shadow culling.</summary>
	[FieldOffset(32)]
	public Vector4 scale3;
};