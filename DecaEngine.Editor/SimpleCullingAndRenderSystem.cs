using System;
using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Graphics;
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
    private readonly PreviewShadowSettings _shadowSettings;
    private RenderCamerasData _renderCamerasData;
    private DirectionalLightCascadeData _shadowData;

    /// <param name="shadowSettings">Мировой свет превью (null = теней нет, свет остаётся камерным):
    /// система строит из него один каскад для ShadowPass и LightData для форвард-шейдера.</param>
    public SimpleCullingAndRenderSystem(RenderResourceManager resourceManager, IGraphicsPipeline pipeline,
        PreviewShadowSettings shadowSettings = null)
    {
        _resourceManager = resourceManager;
        _graphicsPipeline = pipeline;
        _shadowSettings = shadowSettings;
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
        // количества превью сабмеши уезжали в пустоту после бейка/показа целой модели.
        int drawCount = _resourceManager.DrawInstanceCount;

        if (!_renderCamerasData.IsCreated || _renderCamerasData.Capacity != cameraCount)
        {
            if (_renderCamerasData.IsCreated)
            {
                _renderCamerasData.Dispose();
            }

            // Один каскад под мировой свет превью; без _shadowSettings список остаётся пустым
            // (Count 0), и ShadowPass - если он вообще добавлен в граф - корректно no-op-ится.
            if (!_shadowData.IsCreated)
            {
                _shadowData = new DirectionalLightCascadeData(1);
            }

            _renderCamerasData = new RenderCamerasData(cameraCount);
            _graphicsPipeline.SignalGraph(_shadowData, _renderCamerasData);
        }

        _renderCamerasData.Clear();
        _shadowData.Clear();

        var lightData = BuildLightData(drawCount);

        cameras.ForEachEntity((ref CameraComponent camera, Entity entity) =>
        {
            camera.SetPositionAndRotation(entity.Position.value, entity.Rotation.value);

            var viewData = camera.CreateViewData();
            var cullData = camera.CreateCullData();
            cullData.drawCount = drawCount;

            _renderCamerasData.viewData.Add(viewData);
            _renderCamerasData.cullData.Add(cullData);
            _renderCamerasData.lightData.Add(lightData);
        });
    }

    /// <summary>Строит LightData мирового света и (при валидных баундах) каскад для ShadowPass:
    /// ортокамера вдоль направления света, накрывающая bounding-сферу модели. Depth-конвенция
    /// shadow map - ОБЫЧНЫЙ Z (ShadowRenderer: clear 1.0 + DepthFunc Less), поэтому орто-матрица
    /// мапит near->0, far->1, а сравнение в шейдере - LessEqual.</summary>
    private LightData BuildLightData(int drawCount)
    {
        if (_shadowSettings == null || !_shadowSettings.Enabled || _shadowSettings.BoundsRadius <= 0f)
        {
            return default;
        }

        var lightDir = Vector3.Normalize(_shadowSettings.LightDirection);
        var center = _shadowSettings.BoundsCenter;
        float radius = _shadowSettings.BoundsRadius * 1.15f;

        var eye = center - lightDir * radius * 2f;
        var up = MathF.Abs(lightDir.Y) > 0.95f ? Vector3.UnitZ : Vector3.UnitY;
        var view = Matrix4x4.CreateLookAtLeftHanded(eye, center, up);

        // Геометрия лежит в [radius .. 3*radius] от глаза света.
        float near = radius * 0.5f;
        float far = radius * 3.5f;
        var proj = new Matrix4x4(
            1f / radius, 0, 0, 0,
            0, 1f / radius, 0, 0,
            0, 0, 1f / (far - near), 0,
            0, 0, -near / (far - near), 1f);

        var lightViewProj = view * proj;

        var lightData = new LightData
        {
            LightPos = new Vector4(eye, 0f),
            LightDirection = new Vector4(lightDir, 0f),
            LightColor = new Vector4(1f, 0.97f, 0.9f, 1f),
            CascadeMatrix0 = lightViewProj,
            CascadeSplits = new Vector4(float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue),
            // x = мировая ширина орто-каскада (NDC [-1..1] при проекции 1/radius). Шейдер делит на
            // разрешение shadow map и получает мировой размер текселя для normal-offset bias.
            CascadeSizes = new Vector4(2f * radius, 0f, 0f, 0f),
        };

        var shadowView = new ViewData
        {
            view = view,
            viewProj = lightViewProj,
            viewport = new Vector4(0, 0, ShadowRenderer.ShadowMapSize, ShadowRenderer.ShadowMapSize),
            CameraWorldPos = eye,
        };

        var shadowCull = new CullData
        {
            view = view,
            cullFrustum = 0,
            drawCount = drawCount,
        };

        _shadowData.viewData.Add(shadowView);
        _shadowData.cullData.Add(shadowCull);
        _shadowData.lightData.Add(lightData);

        return lightData;
    }

    public void Dispose()
    {
        if (_renderCamerasData.IsCreated)
        {
            _renderCamerasData.Dispose();
        }
        if (_shadowData.IsCreated)
        {
            _shadowData.Dispose();
        }
    }
}
