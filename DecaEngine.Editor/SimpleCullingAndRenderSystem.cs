using System;
using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using UnsafeCollections.Collections.Unsafe;

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

    // Кадровая раскладка теневых слайсов punctual-светов: id сущности света -> первый слайс
    // (см. PunctualShadowScheduler). Пересобирается каждый кадр, читается при сборке пула светов.
    private readonly System.Collections.Generic.Dictionary<int, int> _punctualShadowSlices = new();

    /// <param name="shadowSettings">Мировой свет превью (null = теней нет, свет остаётся камерным):
    /// система строит из него один каскад для ShadowPass и LightData для форвард-шейдера.</param>
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

        // Punctual-света (point/spot) Scene View/превью: всё с LightComponent, кроме солнца.
        // Та же схема, что в CullingAndRenderSystem: по-типовый кулинг против фрустума каждой
        // камеры (LightCulling), выжившие - в общий пул кадра, сегмент камеры - в её
        // LightData.ClusterParams.
        var punctualLightsQuery = Query.Store.Query<LightComponent>()
            .WithoutAllComponents(ComponentTypes.Get<SunComponent>());

        int cameraCount = cameras.Count;
        // ВРЕМЕННЫЙ отладочный принт (расследование "punctual shadow слайс не совпадает") -
        // сколько камер реально видит этот QuerySystem: подозрение, что оффскрин-баунсы
        // (probe-GI/иконки) оставляют лишние CameraComponent-сущности в общем сторе.
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

            // Каскады мирового света (число - опция окружения, см. PreviewShadowSettings.CascadeCount);
            // без _shadowSettings список остаётся пустым (Count 0), и ShadowPass - если он вообще
            // добавлен в граф - корректно no-op-ится.
            if (!_shadowData.IsCreated)
            {
                _shadowData = new DirectionalLightCascadeData(CascadeCount());
            }

            _renderCamerasData = new RenderCamerasData(cameraCount);
            _graphicsPipeline.SignalGraph(_shadowData, _renderCamerasData);
        }

        _renderCamerasData.Clear();
        _shadowData.Clear();

        // Опорная камера каскадов: срезы её фрустума задают объёмы каскадов (классический CSM;
        // тот же приём, что referenceCamera в CullingAndRenderSystem). Берётся первая - у
        // офскрин-окружений камера одна.
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

        // Теневые слайсы punctual-светов - ДО сборки пер-камерных пулов: TryBuildPunctualLight
        // читает раскладку, собирая ShadowParams.
        PunctualShadowScheduler.BuildShadowSlices(punctualLightsQuery, cameraPos, drawCount,
            ref _renderCamerasData, _punctualShadowSlices);

        int punctualLightTotal = 0;
        cameras.ForEachEntity((ref CameraComponent camera, Entity entity) =>
        {
            camera.SetPositionAndRotation(entity.Position.value, entity.Rotation.value);

            var viewData = camera.CreateViewData();
            var cullData = camera.CreateCullData();
            cullData.drawCount = drawCount;

            // Пер-камерный кулинг punctual-светов: сегмент камеры в общем пуле кадра начинается с
            // текущего конца - пул один на все камеры, границы уходят в ClusterParams.
            int segmentOffset = punctualLightTotal;
            var cullDataCopy = cullData;
            // Границы влияния светов сегмента по view-глубине - из них строится диапазон срезов
            // кластерной сетки (см. LightCulling.ClusterDepthRange).
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

    /// <summary>Строит LightData мирового света и (при валидных баундах) каскады для ShadowPass -
    /// той же схемой, что UpdateCascades основного пайплайна: каждый каскад - сфера, описанная
    /// вокруг СРЕЗА ФРУСТУМА опорной камеры, со снапом центра к текселю shadow map и оттяжкой
    /// глаза света под кастеры над объёмом. Отличие одно: дистанции срезов не абсолютные метры из
    /// конфига, а прогрессия по диапазону [дистанция до сцены - габарит .. дистанция + габарит] -
    /// орбитальная камера редактора может стоять и вплотную, и очень далеко от геометрии. Шейдер
    /// берёт первый каскад, чей объём содержит точку (см. SampleWorldLightShadow в
    /// UnlitInstancedPS.hlsl); точка глубже far-плоскости каскада проваливается в следующий.
    /// Depth-конвенция shadow map - ОБЫЧНЫЙ Z (ShadowRenderer: clear 1.0 + DepthFunc Less):
    /// орто-матрица мапит near->0, far->1, сравнение в шейдере - LessEqual.</summary>
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

        // Дальности срезов - по диапазону, где РЕАЛЬНО лежит геометрия: [дистанция до сцены -
        // габарит .. дистанция + габарит]. Орбитальная камера редактора обычно стоит далеко от
        // объекта, и срезы «от нуля» ложились бы в пустой воздух перед ней - все камерные каскады
        // выходили почти одинаковыми и без пользы. Внутри сцены (rangeStart -> 0) схема
        // вырождается в классическую «от камеры». Прогрессия ~2.6x (0.38^k): ближний к геометрии
        // срез самый плотный. Камера LH - forward это +Z её поворота.
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
            // Конвенция основного пайплайна: LightDirection указывает НА солнце (шейдер берёт
            // keyDir без инверсии, см. UnlitInstancedPS).
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
                // Точно как UpdateCascades основного пайплайна: сфера, описанная вокруг восьми
                // углов среза фрустума камеры [sliceNear .. sliceFar].
                float sliceNear = i == 0 ? rangeStart : splitEnds[i - 1];
                (center, radius) = FitFrustumSlice(cameraPos, cameraForward, cameraRight, cameraUp,
                    tanHalfFov, cameraAspect, sliceNear, splitEnds[i]);
                radius = MathF.Max(radius, sceneRadius * 0.005f);
            }
            else
            {
                // Фолбэк без камеры (headless-пробы) - сцена целиком.
                center = sceneCenter;
                radius = sceneRadius;
            }

            // Кайма ВОКРУГ сферы каскада - см. ShadowRenderer.CascadeMarginTexels: шейдер не берёт
            // каскад, пока точка не ушла от края карты внутрь, и без каймы этот отступ съедал край
            // самого объёма.
            radius /= 1f - 2f * ShadowRenderer.CascadeMarginTexels / ShadowRenderer.ShadowMapSize;

            // Снап центра к сетке текселей shadow map в осях света (Floor, как в UpdateCascades) -
            // без него тень мерцает при движении камеры: центр каскада двигается субтексельными
            // шагами.
            float texelWorld = 2f * radius / ShadowRenderer.ShadowMapSize;
            float rSnap = MathF.Floor(Vector3.Dot(center, right) / texelWorld) * texelWorld - Vector3.Dot(center, right);
            float uSnap = MathF.Floor(Vector3.Dot(center, upAxis) / texelWorld) * texelWorld - Vector3.Dot(center, upAxis);
            center += right * rSnap + upAxis * uSnap;

            // Глаз и глубина - как в UpdateCascades (после фикса near-клиппинга): глаз оттянут от
            // сферы каскада к свету на её диаметр, чтобы кастеры между солнцем и объёмом (высокая
            // геометрия над срезом) не резались near-плоскостью.
            float casterExtension = radius * 2f;
            var eye = center - lightDir * (radius + casterExtension);
            var view = Matrix4x4.CreateLookAtLeftHanded(eye, center, up);

            float near = 0.01f;

            // Запас ЗА сферой каскада - см. подробный разбор в UpdateCascades: без него приёмники на
            // задней полусфере получают ndc.z >= 1 (normal-offset + снап центра + float) и
            // UnlitInstancedPS отбрасывает каскад, оставляя просветы.
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

        // x..w = мировые ширины ортокаскадов (0 = каскада нет - шейдер по этому и отличает
        // валидные). Шейдер делит на разрешение shadow map и получает мировой размер текселя
        // для normal-offset bias.
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

            // ВАЖНО: ShadowVS.hlsl трансформирует КАЖДЫЙ слайс по lightData.CascadeMatrix[0] своего
            // пасса - в запись каскада обязана уйти ЕГО матрица, а не общий набор (одна матрица на
            // все четыре слайса рисовала бы их одинаково, а форвард-шейдер сэмплировал бы четырьмя
            // разными - ложные силуэтные тени по всей сцене).
            var cascadeLight = lightData;
            cascadeLight.CascadeMatrix0 = viewProjs[i];
            _shadowData.lightData.Add(cascadeLight);
        }

        return lightData;
    }

    /// <summary>Сфера, описанная вокруг восьми углов среза камерного фрустума [near .. far] -
    /// объём каскада (тот же приём, что UpdateCascades основного пайплайна).</summary>
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
