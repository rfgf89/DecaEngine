using System;
using UnsafeCollections.Collections.Native;

namespace DecaEngine.Graphics.Diligent;

public struct RenderCamerasData
{
    public NativeList<CullData> cullData;
    public NativeList<ViewData> viewData;
    public NativeList<LightData> lightData;

    public RenderCamerasData(int capacity)
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