using System;
using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using UnsafeCollections.Collections.Unsafe;
using DecaEngine.Scene;

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

    // Light entity id -> first shadow slice, rebuilt every frame.
    private readonly System.Collections.Generic.Dictionary<int, int> _punctualShadowSlices = new();

    public SimpleCullingAndRenderSystem(RenderResourceManager resourceManager, IGraphicsPipeline pipeline,
        PreviewShadowSettings shadowSettings = null)
    {
        _resourceManager = resourceManager;
        _graphicsPipeline = pipeline;
        _shadowSettings = shadowSettings;
    }

    protected override unsafe void OnUpdate()
    {
        var cameras = Query.Store.Query<CameraComponent>();

        // Punctual lights are everything with a LightComponent except the sun.
        var punctualLightsQuery = Query.Store.Query<LightComponent>()
            .WithoutAllComponents(ComponentTypes.Get<SunComponent>());

        int cameraCount = cameras.Count;
        if (Environment.GetEnvironmentVariable("DECA_DEBUG_CAMERACOUNT") == "1")
        {
            int lightCount = Query.Store.Query<LightComponent>()
                .WithoutAllComponents(ComponentTypes.Get<SunComponent>()).Count;
            Console.WriteLine($"[debug] SimpleCullingAndRenderSystem.OnUpdate: cameras={cameraCount} punctualLights={lightCount}");
        }
        if (cameraCount == 0)
        {
            return;
        }

        // Slot index bound, not a count: draw slots come from a free stack and stay sparse.
        int drawCount = _resourceManager.DrawInstanceCount;

        if (!_renderCamerasData.IsCreated || _renderCamerasData.Capacity != cameraCount)
        {
            if (_renderCamerasData.IsCreated)
            {
                _renderCamerasData.Dispose();
            }

            if (!_shadowData.IsCreated)
            {
                _shadowData = new DirectionalLightCascadeData(CascadeCount());
            }

            _renderCamerasData = new RenderCamerasData(cameraCount);
            _graphicsPipeline.SignalGraph(_shadowData, _renderCamerasData);
        }

        _renderCamerasData.Clear();
        _shadowData.Clear();

        // Cascade reference camera; off-screen environments only ever have one.
        bool hasCamera = false;
        Vector3 cameraPos = default;
        Quaternion cameraRot = Quaternion.Identity;
        float cameraFov = 1f;
        float cameraAspect = 1f;
        cameras.ForEachEntity((ref CameraComponent camera, Entity entity) =>
        {
            if (!hasCamera)
            {
                hasCamera = true;
                cameraPos = entity.Position.value;
                cameraRot = entity.Rotation.value;
                cameraFov = camera.data.fovRad;
                cameraAspect = camera.data.aspect;
            }
        });

        var lightData = BuildLightData(drawCount, hasCamera, cameraPos, cameraRot, cameraFov, cameraAspect);

        // Must run before the per-camera pools: TryBuildPunctualLight reads this layout.
        PunctualShadowScheduler.BuildShadowSlices(punctualLightsQuery, cameraPos, drawCount,
            ref _renderCamerasData, _punctualShadowSlices);

        int punctualLightTotal = 0;
        cameras.ForEachEntity((ref CameraComponent camera, Entity entity) =>
        {
            camera.SetPositionAndRotation(entity.Position.value, entity.Rotation.value);

            var viewData = camera.CreateViewData();
            var cullData = camera.CreateCullData();
            cullData.drawCount = drawCount;

            // One light pool shared by all cameras; this camera's segment starts at the current end.
            int segmentOffset = punctualLightTotal;
            var cullDataCopy = cullData;
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
                    LightCulling.DumpPunctualLight(lightEntity, in light, in punctualLight);
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
            var cameraLightData = lightData;
            cameraLightData.ClusterParams = new Vector4(
                segmentOffset,
                punctualLightTotal - segmentOffset,
                clusterDepth.X,
                clusterDepth.Y);

            _renderCamerasData.viewData.Add(viewData);
            _renderCamerasData.cullData.Add(cullData);
            _renderCamerasData.lightData.Add(cameraLightData);
        });
    }

    private int CascadeCount() =>
        Math.Clamp(_shadowSettings?.CascadeCount ?? 1, 1, ShadowRenderer.MaxCascades);

    // Mirrors the main pipeline's UpdateCascades, except splits are a progression over
    // [distance - extent .. distance + extent] since the orbit camera can sit at any distance.
    // Shadow maps use standard Z here (clear 1.0, DepthFunc Less), not reversed Z.
    private LightData BuildLightData(int drawCount, bool hasCamera, Vector3 cameraPos,
        Quaternion cameraRot, float cameraFov, float cameraAspect)
    {
        if (_shadowSettings == null || !_shadowSettings.Enabled || _shadowSettings.BoundsRadius <= 0f)
        {
            return default;
        }

        var lightDir = Vector3.Normalize(_shadowSettings.LightDirection);
        var sceneCenter = _shadowSettings.BoundsCenter;
        float sceneRadius = _shadowSettings.BoundsRadius * 1.15f;
        int cascadeCount = CascadeCount();

        var up = MathF.Abs(lightDir.Y) > 0.95f ? Vector3.UnitZ : Vector3.UnitY;
        var right = Vector3.Normalize(Vector3.Cross(up, lightDir));
        var upAxis = Vector3.Cross(lightDir, right);

        // Splits span only where geometry actually is; from-zero splits land in empty air when the
        // orbit camera is far away. Left-handed camera: forward is +Z of its rotation.
        float distanceToScene = hasCamera ? Vector3.Distance(cameraPos, sceneCenter) : 0f;
        float rangeStart = MathF.Max(distanceToScene - sceneRadius, 0f);
        float rangeSpan = MathF.Max(distanceToScene + sceneRadius - rangeStart, sceneRadius * 0.1f);
        Span<float> splitEnds = stackalloc float[ShadowRenderer.MaxCascades];
        for (int i = 0; i < cascadeCount; i++)
        {
            splitEnds[i] = rangeStart + rangeSpan * MathF.Pow(0.38f, cascadeCount - 1 - i);
        }

        var cameraForward = Vector3.Transform(Vector3.UnitZ, cameraRot);
        var cameraRight = Vector3.Transform(Vector3.UnitX, cameraRot);
        var cameraUp = Vector3.Transform(Vector3.UnitY, cameraRot);
        float tanHalfFov = MathF.Tan(cameraFov * 0.5f);

        var lightData = new LightData
        {
            LightPos = new Vector4(sceneCenter - lightDir * sceneRadius * 2f, 0f),
            // Convention: LightDirection points toward the sun; the shader does not invert it.
            LightDirection = new Vector4(-lightDir, 0f),
            LightColor = new Vector4(1f, 0.97f, 0.9f, 1f),
            CascadeSplits = new Vector4(float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue),
        };

        Span<Matrix4x4> views = stackalloc Matrix4x4[ShadowRenderer.MaxCascades];
        Span<Matrix4x4> viewProjs = stackalloc Matrix4x4[ShadowRenderer.MaxCascades];
        Span<Vector3> eyes = stackalloc Vector3[ShadowRenderer.MaxCascades];
        var sizes = Vector4.Zero;

        for (int i = 0; i < cascadeCount; i++)
        {
            Vector3 center;
            float radius;

            if (hasCamera)
            {
                float sliceNear = i == 0 ? rangeStart : splitEnds[i - 1];
                (center, radius) = FitFrustumSlice(cameraPos, cameraForward, cameraRight, cameraUp,
                    tanHalfFov, cameraAspect, sliceNear, splitEnds[i]);
                radius = MathF.Max(radius, sceneRadius * 0.005f);
            }
            else
            {
                // Headless fallback: cover the whole scene.
                center = sceneCenter;
                radius = sceneRadius;
            }

            // Grow past CascadeMarginTexels: the shader rejects points near the map edge, which
            // would otherwise eat the rim of the cascade volume itself.
            radius /= 1f - 2f * ShadowRenderer.CascadeMarginTexels / ShadowRenderer.ShadowMapSize;

            // Snap the center to the shadow texel grid in light axes, or shadows shimmer as the
            // camera moves.
            float texelWorld = 2f * radius / ShadowRenderer.ShadowMapSize;
            float rSnap = MathF.Floor(Vector3.Dot(center, right) / texelWorld) * texelWorld - Vector3.Dot(center, right);
            float uSnap = MathF.Floor(Vector3.Dot(center, upAxis) / texelWorld) * texelWorld - Vector3.Dot(center, upAxis);
            center += right * rSnap + upAxis * uSnap;

            // Pull the light eye back by a diameter so casters above the volume survive near clip.
            float casterExtension = radius * 2f;
            var eye = center - lightDir * (radius + casterExtension);
            var view = Matrix4x4.CreateLookAtLeftHanded(eye, center, up);

            float near = 0.01f;

            // Slack behind the sphere: receivers on the far hemisphere otherwise reach ndc.z >= 1
            // and the shader drops the cascade, leaving gaps.
            float receiverExtension = radius * 0.5f;
            float far = radius * 2f + casterExtension + receiverExtension;

            var proj = new Matrix4x4(
                1f / radius, 0, 0, 0,
                0, 1f / radius, 0, 0,
                0, 0, 1f / (far - near), 0,
                0, 0, -near / (far - near), 1f);

            views[i] = view;
            viewProjs[i] = view * proj;
            eyes[i] = eye;

            switch (i)
            {
                case 0: lightData.CascadeMatrix0 = viewProjs[i]; sizes.X = 2f * radius; break;
                case 1: lightData.CascadeMatrix1 = viewProjs[i]; sizes.Y = 2f * radius; break;
                case 2: lightData.CascadeMatrix2 = viewProjs[i]; sizes.Z = 2f * radius; break;
                default: lightData.CascadeMatrix3 = viewProjs[i]; sizes.W = 2f * radius; break;
            }
        }

        // World widths per cascade; 0 marks an absent cascade for the shader.
        lightData.CascadeSizes = sizes;

        for (int i = 0; i < cascadeCount; i++)
        {
            _shadowData.viewData.Add(new ViewData
            {
                view = views[i],
                viewProj = viewProjs[i],
                viewport = new Vector4(0, 0, ShadowRenderer.ShadowMapSize, ShadowRenderer.ShadowMapSize),
                CameraWorldPos = eyes[i],
            });
            _shadowData.cullData.Add(new CullData
            {
                view = views[i],
                cullFrustum = 0,
                drawCount = drawCount,
            });

            // ShadowVS.hlsl always transforms by CascadeMatrix0, so each slice record must carry
            // its own matrix rather than the shared set.
            var cascadeLight = lightData;
            cascadeLight.CascadeMatrix0 = viewProjs[i];
            _shadowData.lightData.Add(cascadeLight);
        }

        return lightData;
    }

    private static (Vector3 Center, float Radius) FitFrustumSlice(Vector3 cameraPos, Vector3 forward,
        Vector3 right, Vector3 up, float tanHalfFov, float aspect, float near, float far)
    {
        Span<Vector3> corners = stackalloc Vector3[8];
        int n = 0;
        for (int d = 0; d < 2; d++)
        {
            float dist = d == 0 ? near : far;
            float halfY = dist * tanHalfFov;
            float halfX = halfY * aspect;
            var basePoint = cameraPos + forward * dist;
            corners[n++] = basePoint - right * halfX - up * halfY;
            corners[n++] = basePoint + right * halfX - up * halfY;
            corners[n++] = basePoint + right * halfX + up * halfY;
            corners[n++] = basePoint - right * halfX + up * halfY;
        }

        var center = Vector3.Zero;
        foreach (var corner in corners)
        {
            center += corner;
        }
        center /= 8f;

        float radius = 0f;
        foreach (var corner in corners)
        {
            radius = MathF.Max(radius, Vector3.Distance(corner, center));
        }

        return (center, radius);
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
