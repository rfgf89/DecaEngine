using DecaEngine.Graphics.Diligent;
using UnsafeCollections.Collections.Native;
using UnsafeCollections.Collections.Unsafe;

namespace DecaEngine.Graphics;

public unsafe struct RenderCamerasData
{
    public NativeList<CullData> cullData;
    public NativeList<ViewData> viewData;
    public NativeList<LightData> lightData;

    /// <summary>Per-frame pool of visible punctual lights; each camera owns a segment bounded by
    /// its LightData.ClusterParams. UnsafeArray, not NativeList: frozen graph commands capture the
    /// raw pointer and re-read it on every replay, so the memory must never reallocate.</summary>
    public UnsafeArray* punctualLights;

    /// <summary>Punctual shadow slice data, exactly <see cref="LightClusters.MaxShadowSlices"/>
    /// entries every frame (frozen ForwardPass loops over all slices; dead slices draw nothing).
    /// lightData.CascadeMatrix0 holds the slice viewProj used by ShadowVS.</summary>
    public NativeList<CullData> punctualShadowCullData;
    public NativeList<LightData> punctualShadowLightData;

    /// <summary>Per-slice viewProj for pixel-shader sampling (PunctualShadowMatrices buffer).
    /// Stable unmanaged memory for the same reason as punctualLights.</summary>
    public UnsafeArray* punctualShadowMatrices;

    public RenderCamerasData(int capacity)
    {
        capacity = Math.Max(1, capacity);
        cullData = new NativeList<CullData>(capacity);
        viewData = new NativeList<ViewData>(capacity);
        lightData = new NativeList<LightData>(capacity);
        punctualLights = UnsafeArray.Allocate<PunctualLight>(LightClusters.MaxLights);
        punctualShadowCullData = new NativeList<CullData>(LightClusters.MaxShadowSlices);
        punctualShadowLightData = new NativeList<LightData>(LightClusters.MaxShadowSlices);
        punctualShadowMatrices = UnsafeArray.Allocate<System.Numerics.Matrix4x4>(LightClusters.MaxShadowSlices);
    }

    public bool IsCreated => cullData.IsCreated && viewData.IsCreated && lightData.IsCreated;
    public int Length => viewData.Count;
    public int Capacity => viewData.Capacity;

    public void Dispose()
    {
        cullData.Dispose();
        viewData.Dispose();
        lightData.Dispose();
        punctualShadowCullData.Dispose();
        punctualShadowLightData.Dispose();
        if (punctualLights != null)
        {
            UnsafeArray.Free(punctualLights);
            punctualLights = null;
        }
        if (punctualShadowMatrices != null)
        {
            UnsafeArray.Free(punctualShadowMatrices);
            punctualShadowMatrices = null;
        }
    }

    public void Clear()
    {
        cullData.Clear();
        viewData.Clear();
        lightData.Clear();
        punctualShadowCullData.Clear();
        punctualShadowLightData.Clear();
        // punctualLights/punctualShadowMatrices are rewritten each frame; occupancy is conveyed
        // by LightData.ClusterParams and PunctualLight.ShadowParams, so no clear is needed.
    }
}

public struct DirectionalLightCascadeData
{
    public NativeList<CullData> cullData;
    public NativeList<ViewData> viewData;
    public NativeList<LightData> lightData;

    public DirectionalLightCascadeData(int capacity)
    {
        capacity = Math.Max(1, capacity);
        cullData = new NativeList<CullData>(capacity);
        viewData = new NativeList<ViewData>(capacity);
        lightData = new NativeList<LightData>(capacity);
    }

    public bool IsCreated => cullData.IsCreated && viewData.IsCreated && lightData.IsCreated;
    public int Length => viewData.Count;
    public int Capacity => viewData.Capacity;

    public void Dispose()
    {
        cullData.Dispose();
        viewData.Dispose();
        lightData.Dispose();
    }

    public void Clear()
    {
        cullData.Clear();
        viewData.Clear();
        lightData.Clear();
    }
}