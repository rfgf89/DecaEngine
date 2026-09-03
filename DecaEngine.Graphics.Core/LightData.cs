using System.Numerics;
using System.Runtime.InteropServices;

namespace DecaEngine.Graphics;

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

	/// <summary>x - offset into the shared PunctualLight pool, y - count (0 disables clustering),
	/// z/w - zNear/zFar of the cluster grid's exponential depth slices.</summary>
	public Vector4 ClusterParams;
}

/// <summary>Froxel grid constants; mirrors the CLUSTER_* defines in Instancing.hlsl.</summary>
public static class LightClusters
{
	public const int GridX = 16;
	public const int GridY = 8;
	public const int GridZ = 24;
	public const int ClusterCount = GridX * GridY * GridZ;

	/// <summary>Fixed index stride per cluster, so the compute pass needs no global compaction.</summary>
	public const int MaxLightsPerCluster = 32;

	/// <summary>Must match numthreads and the groupshared light batch in LightClusterCS.hlsl.</summary>
	public const int CullGroupSize = 64;

	/// <summary>Capacity of the shared per-frame visible light pool, summed over all cameras.</summary>
	public const int MaxLights = 256;

	/// <summary>Shadow slices per frame: a spot takes one, a point light six cube faces.</summary>
	public const int MaxShadowSlices = 16;

	/// <summary>Resolution of one punctual shadow slice; sun cascades use their own (4096).</summary>
	public const int ShadowMapSize = 1024;
}

/// <summary>GPU record of one point/spot light; mirrors PunctualLight in Instancing.hlsl.</summary>
// Position and direction are world space; LightClusterCS transforms them to view itself.
[StructLayout(LayoutKind.Sequential)]
public struct PunctualLight
{
	/// <summary>xyz - world position, w - range beyond which the contribution is exactly zero.</summary>
	public Vector4 PositionRange;

	/// <summary>rgb - linear color, w - intensity kept separate so it never bakes into the color.</summary>
	public Vector4 ColorIntensity;

	/// <summary>xyz - world cone direction (spot only), w - type: 0 = point, 1 = spot.</summary>
	public Vector4 DirectionType;

	/// <summary>x - cos of outer HALF-angle, y - 1/(cosInner - cosOuter), z - sin of outer half-angle.</summary>
	public Vector4 SpotAngles;

	/// <summary>x - first shadow slice index (-1 = none), y - strength, z - slice near plane
	/// (far = PositionRange.w), w - source radius in world units for PCSS penumbra.</summary>
	public Vector4 ShadowParams;
}

