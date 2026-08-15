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

	/// <summary>Кластеризованные punctual-света ЭТОЙ камеры: x - офсет сегмента камеры в общем пуле
	/// <see cref="PunctualLight"/> (см. RenderCamerasData.punctualLights), y - число светов сегмента,
	/// z/w - zNear/zFar экспоненциальных срезов глубины кластерной сетки. y == 0 (дефолт) - punctual-
	/// светов нет, и кластеризация с шейдингом вырождаются в no-op (превью-конвейеры, теневые
	/// каскады).</summary>
	public Vector4 ClusterParams;
}

/// <summary>
/// Константы кластерной сетки punctual-светов (froxel grid: тайлы экрана x экспоненциальные срезы
/// глубины). Зеркалят #define-ы CLUSTER_* в Instancing.hlsl - менять только парой.
/// </summary>
public static class LightClusters
{
	public const int GridX = 16;
	public const int GridY = 8;
	public const int GridZ = 24;
	public const int ClusterCount = GridX * GridY * GridZ;

	/// <summary>Слотов индексов на кластер в ClusterIndices - фиксированный страйд, чтобы компьют
	/// писал без глобальной компакции атомиками (каждый кластер владеет своим отрезком).</summary>
	public const int MaxLightsPerCluster = 32;

	/// <summary>Ёмкость общего пула видимых punctual-светов кадра (сумма по всем камерам).</summary>
	public const int MaxLights = 256;

	/// <summary>Слайсов в texture array теней punctual-светов на кадр: спот занимает один слайс,
	/// точечный - шесть (грани куба). Бюджет раздаётся ближайшим к камере светам с
	/// ShadowStrength > 0 (см. PunctualShadowScheduler); не влезшие светят без тени.</summary>
	public const int MaxShadowSlices = 16;

	/// <summary>Разрешение одного слайса теней punctual-светов (у каскадов солнца своё - 4096).</summary>
	public const int ShadowMapSize = 1024;
}

/// <summary>
/// GPU-запись одного punctual-света (point/spot) в структурном буфере PunctualLights.
/// Зеркалит struct PunctualLight в Instancing.hlsl - менять только парой. Позиция и направление
/// МИРОВЫЕ: шейдинг (UnlitInstancedPS) работает в мировом пространстве, а кластеризация
/// (LightClusterCS) переводит их во view сама через CullData.view текущей камеры.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct PunctualLight
{
	/// <summary>xyz - мировая позиция, w - радиус действия (за ним вклад света ровно ноль).</summary>
	public Vector4 PositionRange;

	/// <summary>rgb - линейный цвет, w - интенсивность (множитель, не запечён в rgb - так live-ручка
	/// интенсивности не теряет исходный цвет).</summary>
	public Vector4 ColorIntensity;

	/// <summary>xyz - мировое направление конуса (только спот), w - тип: 0 = point, 1 = spot.</summary>
	public Vector4 DirectionType;

	/// <summary>x - cos внешнего ПОЛУугла конуса, y - 1/(cosInner - cosOuter) (масштаб угловой
	/// кромки), z - sin внешнего полуугла (для теста конус-против-сферы в кластеризации), w - пад.</summary>
	public Vector4 SpotAngles;

	/// <summary>Тень света: x - индекс ПЕРВОГО слайса в texture array теней punctual-светов
	/// (спот - один слайс, точечный - шесть граней куба подряд; -1 = тени нет), y - сила тени
	/// (ShadowStrength, 0..1), z/w - пад. Матрицы слайсов - в структурном буфере
	/// PunctualShadowMatrices по тому же индексу.</summary>
	public Vector4 ShadowParams;
}

