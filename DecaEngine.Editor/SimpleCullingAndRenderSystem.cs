using System;
using DecaEngine.Core;
using DecaEngine.Graphics.Diligent;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;

namespace DecaEngine.Editor.ECS;

/// <summary>
/// Minimal counterpart to <see cref="CullingAndRenderSystem"/> for off-screen model preview/icon
/// rendering (<see cref="DecaEngine.Editor.ModelViewportEnvironment"/>): those scenes only ever
/// draw geometry through a single camera with the default Unlit*Instanced shaders (see
/// EditorSettings), so there's no light/shadow-cascade query or math to run here - just cull and
/// draw batch-renderer geometry for every camera view.
/// </summary>
public sealed class SimpleCullingAndRenderSystem : QuerySystem, IDisposable
{
    private readonly RenderResourceManager _resourceManager;
    private readonly IGraphicsPipeline _graphicsPipeline;
    private RenderCamerasData _renderCamerasData;
    private DirectionalLightCascadeData _emptyShadowData;

    public SimpleCullingAndRenderSystem(RenderResourceManager resourceManager, IGraphicsPipeline pipeline)
    {
        _resourceManager = resourceManager;
        _graphicsPipeline = pipeline;
    }

    protected override void OnUpdate()
    {
        var cameras = Query.Store.Query<CameraComponent>();

        int cameraCount = cameras.Count;
        if (cameraCount == 0)
        {
            return;
        }

        // Граница ИНДЕКСА занятых слотов, а не их количество - слоты выдаются из стека свободных и
        // разрежены, см. RenderResourceManager.DrawInstanceCount. Именно из-за подстановки сюда
        // количества превью сабмешей уезжали в пустоту после бейка/показа целой модели.
        int drawCount = _resourceManager.DrawInstanceCount;

        if (!_renderCamerasData.IsCreated || _renderCamerasData.Capacity != cameraCount)
        {
            if (_renderCamerasData.IsCreated)
            {
                _renderCamerasData.Dispose();
            }

            // Always empty (Count stays 0 - nothing ever adds to it): ShadowPass no-ops on an
            // empty DirectionalLightCascadeData, which is exactly what these unlit-only scenes need.
            if (!_emptyShadowData.IsCreated)
            {
                _emptyShadowData = new DirectionalLightCascadeData(0);
            }

            _renderCamerasData = new RenderCamerasData(cameraCount);
            _graphicsPipeline.SignalGraph(_emptyShadowData, _renderCamerasData);
        }

        _renderCamerasData.Clear();

        cameras.ForEachEntity((ref CameraComponent camera, Entity entity) =>
        {
            camera.SetPositionAndRotation(entity.Position.value, entity.Rotation.value);

            var viewData = camera.CreateViewData();
            var cullData = camera.CreateCullData();
            cullData.drawCount = drawCount;

            _renderCamerasData.viewData.Add(viewData);
            _renderCamerasData.cullData.Add(cullData);
            _renderCamerasData.lightData.Add(default);
        });
    }

    public void Dispose()
    {
        if (_renderCamerasData.IsCreated)
        {
            _renderCamerasData.Dispose();
        }
        if (_emptyShadowData.IsCreated)
        {
            _emptyShadowData.Dispose();
        }
    }
}
