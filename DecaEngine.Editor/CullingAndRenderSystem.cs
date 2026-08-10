using System;
using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;

namespace DecaEngine.Editor.ECS;

/// <summary>
/// Builds the per-frame list of views (every camera + every shadow-map cascade) and hands them
/// off to the <see cref="IGraphicsPipeline"/>, which actually culls and draws them.
/// </summary>
public class CullingAndRenderSystem : QuerySystem, IDisposable
{
    private readonly RenderResourceManager _resourceManager;
    private readonly IGraphicsPipeline _graphicsPipeline;
    private RenderCamerasData _renderCamerasData;
    private DirectionalLightCascadeData _directionalLightCascadeData;

    public CullingAndRenderSystem(RenderResourceManager resourceManager, IGraphicsApi api, IGraphicsPipeline pipeline)
    {
        _resourceManager = resourceManager;
        _graphicsPipeline = pipeline;
    }

    protected override unsafe void OnUpdate()
    {
        var mainCameras = Query.Store.Query<CameraComponent>().WithoutAllComponents(ComponentTypes.Get<CascadedShadowComponent>());
        var lights = Query.Store.Query<LightComponent, SunComponent, CascadedShadowComponent>();

        int cameraCount = mainCameras.Count;
        if (cameraCount == 0)
        {
            return;
        }

        // Граница ИНДЕКСА занятых слотов, а не их количество - слоты выдаются из стека свободных и
        // разрежены, см. RenderResourceManager.DrawInstanceCount.
        int drawCount = _resourceManager.DrawInstanceCount;
        int shadowViewCount = lights.Count > 0 ? ShadowRenderer.MaxCascades : 0;

        if (!_renderCamerasData.IsCreated || _renderCamerasData.Capacity != cameraCount || _directionalLightCascadeData.Capacity != Math.Max(1, shadowViewCount))
        {
            if (_renderCamerasData.IsCreated)
            {
                _renderCamerasData.Dispose();
                _directionalLightCascadeData.Dispose();
            }

            _renderCamerasData = new RenderCamerasData(cameraCount);
            _directionalLightCascadeData = new DirectionalLightCascadeData(shadowViewCount);
            _graphicsPipeline.SignalGraph(_directionalLightCascadeData, _renderCamerasData);
        }

        _renderCamerasData.Clear();
        _directionalLightCascadeData.Clear();

        // Every camera needs its own view/cull data, but the shadow map(s) are shared: the
        // cascades below are fit to the first camera found (typical single-viewer setup).
        CameraComponent referenceCamera = default;
        mainCameras.ForEachEntity((ref CameraComponent camera, Entity entity) =>
        {
            camera.SetPositionAndRotation(entity.Position.value, entity.Rotation.value);
            referenceCamera = camera;
        });

        LightData sharedLightData = default;

        if (shadowViewCount > 0)
        {
            lights.ForEachEntity((ref LightComponent light, ref SunComponent sun, ref CascadedShadowComponent cascadedShadow, Entity lightEntity) =>
            {
                var lightDirection = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, lightEntity.Rotation.value));
                var (cascadeSizes, cascadeNearPlanes, cascadeSplits) = UpdateCascades(referenceCamera, lightDirection, ref cascadedShadow);

                fixed (CameraComponent* ptr = &cascadedShadow.Cascade0)
                {
                    // Each cascade needs its own LightData with its own matrix
                    for (int i = 0; i < ShadowRenderer.MaxCascades; i++)
                    {
                        var viewData = (ptr + i)->CreateViewData();
                        var cullData = (ptr + i)->CreateCullData();
                        cullData.drawCount = drawCount;

                        // Each cascade's LightData: all CascadeMatrix entries point to this cascade's viewProj
                        var cascadeLightData = new LightData
                        {
                            LightPos = lightEntity.Position.value.AsVector4(),
                            LightColor = new Vector4(light.Color, light.Intensity),
                            LightDirection = new Vector4(-lightDirection, 1.0f),
                            SpotAngles = new Vector4(0, 0, light.ShadowStrength, 0),
                            CascadeMatrix0 = viewData.viewProj,
                            CascadeMatrix1 = viewData.viewProj,
                            CascadeMatrix2 = viewData.viewProj,
                            CascadeMatrix3 = viewData.viewProj,
                            CascadeSplits = cascadeSplits,
                            CascadeSizes = cascadeSizes,
                            CascadeNearPlanes = cascadeNearPlanes,
                        };

                        _directionalLightCascadeData.viewData.Add(viewData);
                        _directionalLightCascadeData.cullData.Add(cullData);
                        _directionalLightCascadeData.lightData.Add(cascadeLightData);
                    }
                }

                // Main camera gets light data with all four cascade matrices
                sharedLightData = BuildLightData(ref cascadedShadow, ref light, lightEntity, lightDirection, cascadeSizes, cascadeNearPlanes, cascadeSplits);
            });
        }

        mainCameras.ForEachEntity((ref CameraComponent camera, Entity entity) =>
        {
            var viewData = camera.CreateViewData();
            var cullData = camera.CreateCullData();
            cullData.drawCount = drawCount;

            _renderCamerasData.viewData.Add(viewData);
            _renderCamerasData.cullData.Add(cullData);
            _renderCamerasData.lightData.Add(sharedLightData);
        });
    }

    private static unsafe LightData BuildLightData(ref CascadedShadowComponent cascadedShadow, ref LightComponent light, Entity lightEntity,
        Vector3 lightDirection, Vector4 cascadeSizes, Vector4 cascadeNearPlanes, Vector4 cascadeSplits)
    {
        fixed (CameraComponent* ptr = &cascadedShadow.Cascade0)
        {
            return new LightData
            {
                LightPos = lightEntity.Position.value.AsVector4(),
                LightColor = new Vector4(light.Color, light.Intensity),
                LightDirection = new Vector4(-lightDirection, 1.0f),
                SpotAngles = new Vector4(0, 0, light.ShadowStrength, 0),
                CascadeMatrix0 = (ptr + 0)->CreateViewData().viewProj,
                CascadeMatrix1 = (ptr + 1)->CreateViewData().viewProj,
                CascadeMatrix2 = (ptr + 2)->CreateViewData().viewProj,
                CascadeMatrix3 = (ptr + 3)->CreateViewData().viewProj,
                CascadeSplits = cascadeSplits,
                CascadeSizes = cascadeSizes,
                CascadeNearPlanes = cascadeNearPlanes,
            };
        }
    }

    private unsafe (Vector4 cascadeSizes, Vector4 cascadeNearPlanes, Vector4 cascadeSplits) UpdateCascades(CameraComponent camera, Vector3 lightDirection, ref CascadedShadowComponent cascadedShadow)
    {
        var cascadeSplits = cascadedShadow.CascadeDistances;
        var cascadeSizes = new Vector4();
        var cascadeNearPlanes = new Vector4();

        Vector3 lightUp = Vector3.UnitY;
        if (Math.Abs(Vector3.Dot(lightDirection, lightUp)) > 0.99f) lightUp = Vector3.UnitX;

        Matrix4x4.Invert(camera.renderCamera.view, out Matrix4x4 cameraWorld);

        for (int i = 0; i < ShadowRenderer.MaxCascades; i++)
        {
            float n = cascadeSplits[i];
            float f = cascadeSplits[i + 1];

            float tanHalfFov = MathF.Tan(camera.data.fovRad * 0.5f);
            float nearY = n * tanHalfFov;
            float nearX = nearY * camera.data.aspect;
            float farY = f * tanHalfFov;
            float farX = farY * camera.data.aspect;

            Vector3[] cornersViewSpace = new Vector3[8];
            cornersViewSpace[0] = new Vector3(-nearX, -nearY, -n);
            cornersViewSpace[1] = new Vector3(nearX, -nearY, -n);
            cornersViewSpace[2] = new Vector3(nearX, nearY, -n);
            cornersViewSpace[3] = new Vector3(-nearX, nearY, -n);
            cornersViewSpace[4] = new Vector3(-farX, -farY, -f);
            cornersViewSpace[5] = new Vector3(farX, -farY, -f);
            cornersViewSpace[6] = new Vector3(farX, farY, -f);
            cornersViewSpace[7] = new Vector3(-farX, farY, -f);

            Vector3 center = Vector3.Zero;
            for (int j = 0; j < 8; j++)
            {
                cornersViewSpace[j] = Vector3.Transform(cornersViewSpace[j], cameraWorld);
                center += cornersViewSpace[j];
            }
            center /= 8.0f;

            float radius = 0.0f;
            foreach (var corner in cornersViewSpace)
            {
                radius = Math.Max(radius, Vector3.Distance(corner, center));
            }

            float worldUnitsPerTexel = (radius * 2.0f) / ShadowRenderer.ShadowMapSize;
            Matrix4x4 tempLightView = Matrix4x4.CreateLookAt(Vector3.Zero, lightDirection, lightUp);
            Vector3 lightSpaceCenter = Vector3.Transform(center, tempLightView);
            lightSpaceCenter.X = MathF.Floor(lightSpaceCenter.X / worldUnitsPerTexel) * worldUnitsPerTexel;
            lightSpaceCenter.Y = MathF.Floor(lightSpaceCenter.Y / worldUnitsPerTexel) * worldUnitsPerTexel;
            Matrix4x4.Invert(tempLightView, out Matrix4x4 tempLightViewInv);
            center = Vector3.Transform(lightSpaceCenter, tempLightViewInv);

            Vector3 lightPos = center - lightDirection * radius;
            float znear = 0.01f;
            float zfar = radius * 2.0f;

            (&cascadeSizes.X)[i] = radius * 2.0f;
            (&cascadeNearPlanes.X)[i] = znear;

            var camData = new CameraData(radius * 2.0f, radius * 2.0f, znear, zfar, new Vector4(0, 0, ShadowRenderer.ShadowMapSize, ShadowRenderer.ShadowMapSize));

            fixed (CameraComponent* ptr = &cascadedShadow.Cascade0)
            {
                (ptr + i)->data = camData;
                (ptr + i)->SetLookAt(lightPos, center, lightUp);
                (ptr + i)->RecalculateProjection();
            }
        }

        var cascadeSplitsVec = new Vector4(cascadeSplits[1], cascadeSplits[2], cascadeSplits[3], cascadeSplits[4]);
        return (cascadeSizes, cascadeNearPlanes, cascadeSplitsVec);
    }

    public void Dispose()
    {
        if (_directionalLightCascadeData.IsCreated)
        {
            _directionalLightCascadeData.Dispose();
        }
        if (_renderCamerasData.IsCreated)
        {
            _renderCamerasData.Dispose();
        }
    }
}
