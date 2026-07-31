using System.Numerics;
using System.Runtime.InteropServices;

namespace DecaEngine.Graphics.Diligent;

[StructLayout(LayoutKind.Sequential)]
public struct CascadeAttribs
{
	public Vector4 f4LightSpaceScale;
	public Vector4 f4LightSpaceScaledBias;
	public Vector4 f4StartEndZ;
	public Vector4 f4MarginProjSpace;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct ShadowMapAttribs
{
	public Vector4 f4CascadeCamSpaceZEnd0;
	public Vector4 f4CascadeCamSpaceZEnd1;
	public Vector4 f4CascadeCamSpaceZEnd2;
	public Vector4 f4CascadeCamSpaceZEnd3;

	public CascadeAttribs Cascades0;
	public CascadeAttribs Cascades1;
	public CascadeAttribs Cascades2;
	public CascadeAttribs Cascades3;
	public CascadeAttribs Cascades4;
	public CascadeAttribs Cascades5;
	public CascadeAttribs Cascades6;
	public CascadeAttribs Cascades7;

	public Vector4 f4ShadowMapDim;
	public float fCascadeTransitionRegion;
	public int iNumCascades;
	public float fReceiverPlaneDepthBiasClamp;
	public float fFixedDepthBias;

	public float fVSMBias;
	public float fVSMLightBleedingReduction;
	public float fEVSMPositiveExponent;
	public float fEVSMNegativeExponent;
	public int bIs32BitEVSM;
	public float fFilterWorldSize;
	public Vector2 _padding;
}

/// <summary>
/// Per-view lighting data (currently supports a single directional/sun light with up to
/// <see cref="DecaEngine.Core.IBatchRenderer.ShadowCascadeCount"/> cascades).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct LightData
{
	public Vector4 LightPos;
	public Vector4 LightColor;
	public Vector4 LightDirection;
	public Vector4 SpotAngles;

	public Matrix4x4 CascadeMatrix0;
	public Matrix4x4 CascadeMatrix1;
	public Matrix4x4 CascadeMatrix2;
	public Matrix4x4 CascadeMatrix3;
	public Vector4 CascadeSplits;
	public Vector4 CascadeSizes;
	public Vector4 CascadeNearPlanes;
}

