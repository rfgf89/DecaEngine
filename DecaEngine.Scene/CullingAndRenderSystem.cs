using System;
using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Graphics;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using UnsafeCollections.Collections.Unsafe;

namespace DecaEngine.Scene;

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

    // Per-frame punctual shadow slice layout: light entity id -> first slice.
    private readonly System.Collections.Generic.Dictionary<int, int> _punctualShadowSlices = new();

    // Cascade staggering state: a skipped cascade must keep the matrix its map was rendered with.
    private readonly ShadowCascadeSchedule _cascadeSchedule;
    private uint _cascadeFrameIndex;
    private bool _hasCascadeHistory;
    private Vector3 _lastSunDirection;
    private Vector3 _lastCascadeCameraPos;
    private int _lastCascadeDrawCount;
    private Vector4 _lastCascadeSplits;
    private float _lastCascadeFov;
    private float _lastCascadeAspect;
    private Vector4 _cachedCascadeSizes;
    private Vector4 _cachedCascadeNearPlanes;

    public CullingAndRenderSystem(RenderResourceManager resourceManager, IGraphicsApi api, IGraphicsPipeline pipeline)
    {
        _resourceManager = resourceManager;
        _graphicsPipeline = pipeline;

        // A pipeline without a schedule redraws every cascade every frame.
        _cascadeSchedule = pipeline switch
        {
            GraphicsPipelineSimple simple => simple.CascadeSchedule,
            GraphicsPipeline classic => classic.CascadeSchedule,
            _ => null,
        };
    }

    protected override unsafe void OnUpdate()
    {
        var mainCameras = Query.Store.Query<CameraComponent>().WithoutAllComponents(ComponentTypes.Get<CascadedShadowComponent>());
        var lights = Query.Store.Query<LightComponent, SunComponent, CascadedShadowComponent>();

        // Punctual lights: everything but the sun, which goes through the cascade path above.
        var punctualLightsQuery = Query.Store.Query<LightComponent>().WithoutAllComponents(ComponentTypes.Get<SunComponent>());

        int cameraCount = mainCameras.Count;
        if (Environment.GetEnvironmentVariable("DECA_DEBUG_CAMERACOUNT") == "1")
        {
            int lightCount = punctualLightsQuery.Count;
            Console.WriteLine($"[debug] CullingAndRenderSystem.OnUpdate: cameras={cameraCount} punctualLights={lightCount}");
        }
        if (cameraCount == 0)
        {
            return;
        }

        // Upper index bound, not a count: draw slots come from a free stack and are sparse.
        int drawCount = _resourceManager.DrawInstanceCount;
        int shadowViewCount = lights.Count > 0 ? ShadowLayout.MaxCascades : 0;

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
        bool hasReferenceCamera = false;
        Vector3 referenceCameraPos = default;
        mainCameras.ForEachEntity((ref CameraComponent camera, Entity entity) =>
        {
            camera.SetPositionAndRotation(entity.Position.value, entity.Rotation.value);
            if (!hasReferenceCamera)
            {
                hasReferenceCamera = true;
                referenceCamera = camera;
                referenceCameraPos = entity.Position.value;
            }
        });

        LightData sharedLightData = default;

        if (shadowViewCount > 0)
        {
            _cascadeFrameIndex++;
            lights.ForEachEntity((ref LightComponent light, ref SunComponent sun, ref CascadedShadowComponent cascadedShadow, Entity lightEntity) =>
            {
                var lightDirection = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, lightEntity.Rotation.value));

                // Mask must be computed before the refit: UpdateCascades only refits masked ones.
                int updateMask = ComputeCascadeUpdateMask(in referenceCamera, lightDirection, referenceCameraPos, drawCount, ref cascadedShadow);
                _cascadeSchedule?.SetRenderMask(updateMask);

                var (cascadeSizes, cascadeNearPlanes, cascadeSplits) = UpdateCascades(referenceCamera, lightDirection, ref cascadedShadow, updateMask);

                fixed (CameraComponent* ptr = &cascadedShadow.Cascade0)
                {
                    for (int i = 0; i < ShadowLayout.MaxCascades; i++)
                    {
                        var viewData = (ptr + i)->CreateViewData();
                        var cullData = (ptr + i)->CreateCullData();
                        cullData.drawCount = drawCount;

                        // All four CascadeMatrix slots hold this cascade's viewProj.
                        var cascadeLightData = new LightData
                        {
                            LightPos = lightEntity.Position.value.AsVector4(),
                            LightColor = new Vector4(light.Color, light.Intensity),
                            LightDirection = new Vector4(-lightDirection, 1.0f),
                            SpotAngles = new Vector4(0, 0, light.ShadowStrength, SunTanHalfAngle(in light)),
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

                sharedLightData = BuildLightData(ref cascadedShadow, ref light, lightEntity, lightDirection, cascadeSizes, cascadeNearPlanes, cascadeSplits);
            });
        }

        // Must precede the per-camera pools: TryBuildPunctualLight reads this layout.
        PunctualShadowScheduler.BuildShadowSlices(punctualLightsQuery, referenceCameraPos,
            drawCount, ref _renderCamerasData, _punctualShadowSlices);

        int punctualLightTotal = 0;
        mainCameras.ForEachEntity((ref CameraComponent camera, Entity entity) =>
        {
            var viewData = camera.CreateViewData();
            var cullData = camera.CreateCullData();
            cullData.drawCount = drawCount;

            // One pool shared by all cameras; this camera's segment starts at the current end.
            int segmentOffset = punctualLightTotal;
            var cullDataCopy = cullData;
            // View-depth bounds of the segment, used to size the cluster grid's slice range.
            float minLightZ = float.MaxValue;
            float maxLightZ = float.MinValue;
            punctualLightsQuery.ForEachEntity((ref LightComponent light, Entity lightEntity) =>
            {
                if (punctualLightTotal >= LightClusters.MaxLights)
                {
                    return;
                }

                if (LightCulling.TryBuildPunctualLight(ref light, lightEntity, in cullDataCopy, _punctualShadowSlices, out var punctualLight))
                {
                    UnsafeArray.Set(_renderCamerasData.punctualLights, punctualLightTotal, punctualLight);
                    punctualLightTotal++;

                    float lightViewZ = Vector3.Transform(
                        new Vector3(punctualLight.PositionRange.X, punctualLight.PositionRange.Y,
                            punctualLight.PositionRange.Z), cullDataCopy.view).Z;
                    minLightZ = MathF.Min(minLightZ, lightViewZ - punctualLight.PositionRange.W);
                    maxLightZ = MathF.Max(maxLightZ, lightViewZ + punctualLight.PositionRange.W);
                }
            });

            var clusterDepth = LightCulling.ClusterDepthRange(in cullData, minLightZ, maxLightZ);
            var cameraLightData = sharedLightData;
            cameraLightData.ClusterParams = new Vector4(
                segmentOffset,
                punctualLightTotal - segmentOffset,
                clusterDepth.X,
                clusterDepth.Y);
            if (Environment.GetEnvironmentVariable("DECA_DEBUG_CAMERACOUNT") == "1")
            {
                Console.WriteLine($"[debug]   camera entity={entity.Id} segmentOffset={segmentOffset} segCount={punctualLightTotal - segmentOffset}");
            }

            _renderCamerasData.viewData.Add(viewData);
            _renderCamerasData.cullData.Add(cullData);
            _renderCamerasData.lightData.Add(cameraLightData);
        });
    }

    // Tangent of the sun disc's half angle; goes to SpotAngles.w, where PCSS reads penumbra width.
    private static float SunTanHalfAngle(in LightComponent light)
    {
        float diameterDeg = light.SunAngularSize > 0f ? light.SunAngularSize : 1.0f;
        return MathF.Tan(diameterDeg * 0.5f * MathF.PI / 180f);
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
                SpotAngles = new Vector4(0, 0, light.ShadowStrength, SunTanHalfAngle(in light)),
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

    // Cascade 3 lands on frames 4k+1, deliberately out of phase with cascade 2.
    private int ComputeCascadeUpdateMask(in CameraComponent camera, Vector3 lightDirection, Vector3 cameraPos,
        int drawCount, ref CascadedShadowComponent cascadedShadow)
    {
        if (_cascadeSchedule is null || !ShadowCascadeSchedule.StaggerEnabled)
        {
            return ShadowCascadeSchedule.AllCascades;
        }

        var distances = cascadedShadow.CascadeDistances;
        var splits = new Vector4(distances[1], distances[2], distances[3], distances[4]);

        // CascadeSizes holds sphere diameters; the teleport threshold is a quarter of the far radius.
        float teleportThreshold = MathF.Max(0.5f, _cachedCascadeSizes.W * 0.125f);

        bool force = !_hasCascadeHistory
            || Vector3.Dot(lightDirection, _lastSunDirection) < 1f - 1e-4f
            || drawCount != _lastCascadeDrawCount
            || Vector3.DistanceSquared(cameraPos, _lastCascadeCameraPos) > teleportThreshold * teleportThreshold
            || camera.data.fovRad != _lastCascadeFov
            || camera.data.aspect != _lastCascadeAspect
            || SplitsChanged(splits, _lastCascadeSplits);

        _hasCascadeHistory = true;
        _lastSunDirection = lightDirection;
        _lastCascadeDrawCount = drawCount;
        _lastCascadeCameraPos = cameraPos;
        _lastCascadeFov = camera.data.fovRad;
        _lastCascadeAspect = camera.data.aspect;
        _lastCascadeSplits = splits;

        if (force)
        {
            return ShadowCascadeSchedule.AllCascades;
        }

        // The bit layout below assumes four cascades (ShadowLayout.MaxCascades).
        int mask = 0b0011;
        if ((_cascadeFrameIndex & 1u) == 0u) mask |= 0b0100;
        if ((_cascadeFrameIndex & 3u) == 1u) mask |= 0b1000;
        return mask;
    }

    // Relative tolerance: split distances drift with zoom and must not disable staggering.
    private static bool SplitsChanged(Vector4 a, Vector4 b)
    {
        float scale = MathF.Max(1f, MathF.Max(MathF.Abs(a.W), MathF.Abs(b.W)));
        var d = Vector4.Abs(a - b);
        return MathF.Max(MathF.Max(d.X, d.Y), MathF.Max(d.Z, d.W)) > scale * 1e-3f;
    }

    private unsafe (Vector4 cascadeSizes, Vector4 cascadeNearPlanes, Vector4 cascadeSplits) UpdateCascades(CameraComponent camera, Vector3 lightDirection, ref CascadedShadowComponent cascadedShadow, int updateMask)
    {
        var cascadeSplits = cascadedShadow.CascadeDistances;

        // Start from the last render: skipped cascades must keep their rendered size and near.
        var cascadeSizes = _cachedCascadeSizes;
        var cascadeNearPlanes = _cachedCascadeNearPlanes;

        Vector3 lightUp = Vector3.UnitY;
        if (Math.Abs(Vector3.Dot(lightDirection, lightUp)) > 0.99f) lightUp = Vector3.UnitX;

        Matrix4x4.Invert(camera.renderCamera.view, out Matrix4x4 cameraWorld);

        // Hoisted out of the loop: a per-cascade array would allocate every frame.
        Span<Vector3> cornersViewSpace = stackalloc Vector3[8];

        for (int i = 0; i < ShadowLayout.MaxCascades; i++)
        {
            if ((updateMask & (1 << i)) == 0)
            {
                // Not redrawn this frame: refitting without rendering desyncs shadow from map.
                continue;
            }

            float n = cascadeSplits[i];
            float f = cascadeSplits[i + 1];

            float tanHalfFov = MathF.Tan(camera.data.fovRad * 0.5f);
            float nearY = n * tanHalfFov;
            float nearX = nearY * camera.data.aspect;
            float farY = f * tanHalfFov;
            float farX = farY * camera.data.aspect;

            // Left-handed camera, forward = +Z: slice depths are positive, not negated.
            cornersViewSpace[0] = new Vector3(-nearX, -nearY, n);
            cornersViewSpace[1] = new Vector3(nearX, -nearY, n);
            cornersViewSpace[2] = new Vector3(nearX, nearY, n);
            cornersViewSpace[3] = new Vector3(-nearX, nearY, n);
            cornersViewSpace[4] = new Vector3(-farX, -farY, f);
            cornersViewSpace[5] = new Vector3(farX, -farY, f);
            cornersViewSpace[6] = new Vector3(farX, farY, f);
            cornersViewSpace[7] = new Vector3(-farX, farY, f);

            Vector3 center = Vector3.Zero;
            for (int j = 0; j < 8; j++)
            {
                cornersViewSpace[j] = Vector3.Transform(cornersViewSpace[j], cameraWorld);
                center += cornersViewSpace[j];
            }
            center /= 8.0f;

            float radius = 0.0f;
            for (int j = 0; j < 8; j++)
            {
                radius = Math.Max(radius, Vector3.Distance(cornersViewSpace[j], center));
            }

            // Grow the sphere by the shader's border margin; texel snapping must use the new size.
            radius /= 1f - 2f * ShadowLayout.CascadeMarginTexels / ShadowLayout.ShadowMapSize;

            float worldUnitsPerTexel = (radius * 2.0f) / ShadowLayout.ShadowMapSize;
            Matrix4x4 tempLightView = Matrix4x4.CreateLookAt(Vector3.Zero, lightDirection, lightUp);
            Vector3 lightSpaceCenter = Vector3.Transform(center, tempLightView);
            lightSpaceCenter.X = MathF.Floor(lightSpaceCenter.X / worldUnitsPerTexel) * worldUnitsPerTexel;
            lightSpaceCenter.Y = MathF.Floor(lightSpaceCenter.Y / worldUnitsPerTexel) * worldUnitsPerTexel;
            Matrix4x4.Invert(tempLightView, out Matrix4x4 tempLightViewInv);
            center = Vector3.Transform(lightSpaceCenter, tempLightViewInv);

            // Eye pulled back toward the sun: casters above the slice would else be near-clipped.
            float casterExtension = radius * 2.0f;
            Vector3 lightPos = center - lightDirection * (radius + casterExtension);
            float znear = 0.01f;

            // Far margin past the sphere: the shader drops receivers whose lightNdc.z reaches 1.
            float receiverExtension = radius * 0.5f;
            float zfar = radius * 2.0f + casterExtension + receiverExtension;

            (&cascadeSizes.X)[i] = radius * 2.0f;
            (&cascadeNearPlanes.X)[i] = znear;

            var camData = new CameraData(radius * 2.0f, radius * 2.0f, znear, zfar, new Vector4(0, 0, ShadowLayout.ShadowMapSize, ShadowLayout.ShadowMapSize));

            fixed (CameraComponent* ptr = &cascadedShadow.Cascade0)
            {
                (ptr + i)->data = camData;
                (ptr + i)->SetLookAt(lightPos, center, lightUp);
                (ptr + i)->RecalculateProjection();
            }
        }

        // Cache the refit lanes so the next frame's skipped cascades keep these values.
        _cachedCascadeSizes = cascadeSizes;
        _cachedCascadeNearPlanes = cascadeNearPlanes;

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
