using DecaEngine.Graphics.Diligent;
using UnsafeCollections.Collections.Native;
using UnsafeCollections.Collections.Unsafe;

namespace DecaEngine.Graphics;

public unsafe struct RenderCamerasData
{
    public NativeList<CullData> cullData;
    public NativeList<ViewData> viewData;
    public NativeList<LightData> lightData;

    /// <summary>Общий пул видимых punctual-светов кадра (PunctualLight, фиксированная ёмкость
    /// <see cref="LightClusters.MaxLights"/>): каждая камера владеет своим отрезком, границы - в её
    /// LightData.ClusterParams. Именно UnsafeArray, а не NativeList: замороженные команды графа
    /// (см. ForwardPass -> IBatchRenderer.SetupPunctualLights) запоминают сырой указатель и
    /// перечитывают память на каждом реплее - память обязана быть стабильной, без переаллокаций.</summary>
    public UnsafeArray* punctualLights;

    /// <summary>Данные слайсов теней punctual-светов кадра - РОВНО <see cref="LightClusters.MaxShadowSlices"/>
    /// записей каждый кадр (замороженный ForwardPass пишет фиксированную петлю по всем слайсам;
    /// мёртвый слайс несёт drawCount = 0 и рисует пусто). cullData - фрустум света для GPU-кулинга,
    /// lightData - CascadeMatrix0 = viewProj слайса (ShadowVS трансформирует по ней).</summary>
    public NativeList<CullData> punctualShadowCullData;
    public NativeList<LightData> punctualShadowLightData;

    /// <summary>viewProj каждого слайса теней для СЭМПЛИНГА в пиксельном шейдере (структурный буфер
    /// PunctualShadowMatrices). Стабильная unmanaged-память - по той же причине, что punctualLights.</summary>
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
        // punctualLights/punctualShadowMatrices не чистятся: пул перезаписывается каждый кадр, а
        // занятость сегментов/слайсов сообщают LightData.ClusterParams и PunctualLight.ShadowParams.
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