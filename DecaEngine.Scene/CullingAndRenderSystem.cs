using System;
using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
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

    // Кадровая раскладка теневых слайсов punctual-светов: id сущности света -> первый слайс
    // (см. PunctualShadowScheduler). Пересобирается каждый кадр, читается при сборке пула светов.
    private readonly System.Collections.Generic.Dictionary<int, int> _punctualShadowSlices = new();

    // Стаггеринг теневых каскадов (см. ShadowCascadeSchedule): решение "каскад i перерисовывается
    // в ЭТОМ кадре" принимается здесь - вместе с рефитом его матрицы, - а исполняет его колбэк
    // ShadowPass при реплее замороженного графа. Кэши ниже - состояние последнего РЕНДЕРА каждого
    // каскада: пропущенный каскад обязан сэмплироваться той же матрицей (и с тем же CascadeSizes),
    // которой его карта нарисована; сами матрицы живут в CascadedShadowComponent.Cascade0..3 и для
    // пропущенных каскадов просто не трогаются.
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

        // Расписание живёт на конвейере (он передаёт его в каждый пересозданный ShadowPass);
        // конвейер без расписания = прежнее поведение, каскады каждый кадр.
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

        // Punctual-света (point/spot): всё с LightComponent, кроме солнца - оно идёт своим путём
        // через каскадные тени выше. Кулятся ПО-ТИПОВО против фрустума каждой камеры (см.
        // LightCulling), выжившие складываются в общий пул кадра, сегмент камеры - в её
        // LightData.ClusterParams.
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

                // Маска каскадов кадра - ДО рефита: UpdateCascades перефитит только их, остальные
                // сохранят матрицу последнего рендера (см. комментарий у _cascadeSchedule).
                int updateMask = ComputeCascadeUpdateMask(in referenceCamera, lightDirection, referenceCameraPos, drawCount, ref cascadedShadow);
                _cascadeSchedule?.SetRenderMask(updateMask);

                var (cascadeSizes, cascadeNearPlanes, cascadeSplits) = UpdateCascades(referenceCamera, lightDirection, ref cascadedShadow, updateMask);

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

                // Main camera gets light data with all four cascade matrices
                sharedLightData = BuildLightData(ref cascadedShadow, ref light, lightEntity, lightDirection, cascadeSizes, cascadeNearPlanes, cascadeSplits);
            });
        }

        // Теневые слайсы punctual-светов - ДО сборки пер-камерных пулов: TryBuildPunctualLight
        // читает раскладку, собирая ShadowParams. Приоритет бюджета - по дистанции до опорной камеры.
        PunctualShadowScheduler.BuildShadowSlices(punctualLightsQuery, referenceCameraPos,
            drawCount, ref _renderCamerasData, _punctualShadowSlices);

        int punctualLightTotal = 0;
        mainCameras.ForEachEntity((ref CameraComponent camera, Entity entity) =>
        {
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

    /// <summary>Тангенс полуугла видимого диска солнца - уходит в SpotAngles.w, оттуда PCSS в
    /// UnlitInstancedPS.SampleWorldLightShadow выводит ширину полутени. Дефолт нуля компонента -
    /// см. комментарий у LightComponent.SunAngularSize.</summary>
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

    /// <summary>Битовая маска каскадов, перерисовываемых (и перефичиваемых) в ЭТОМ кадре.
    /// Каскады 0/1 - каждый кадр; 2 - каждый второй (чётные кадры); 3 - каждый четвёртый (кадры
    /// 4k+1 - нарочно вразнос с каскадом 2, чтобы две тяжёлые перерисовки не совпадали). Полное
    /// обновление форсируется, когда стаггерить опасно: повернулось солнце, изменился набор
    /// инстансов (drawCount - см. RenderResourceManager.DrawInstanceCount; перестройки графа
    /// ловит сам ShadowPass через ForceAll при перезаписи команд), камера телепортировалась
    /// дальше четверти радиуса дальнего каскада, сменились fov/аспект или дистанции срезов.</summary>
    private int ComputeCascadeUpdateMask(in CameraComponent camera, Vector3 lightDirection, Vector3 cameraPos,
        int drawCount, ref CascadedShadowComponent cascadedShadow)
    {
        if (_cascadeSchedule is null || !ShadowCascadeSchedule.StaggerEnabled)
        {
            return ShadowCascadeSchedule.AllCascades;
        }

        var distances = cascadedShadow.CascadeDistances;
        var splits = new Vector4(distances[1], distances[2], distances[3], distances[4]);

        // CascadeSizes хранит ДИАМЕТРЫ сфер каскадов; порог телепорта - четверть радиуса дальнего.
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

        // Раскладка ниже рассчитана на четыре каскада (ShadowRenderer.MaxCascades).
        int mask = 0b0011;
        if ((_cascadeFrameIndex & 1u) == 0u) mask |= 0b0100;
        if ((_cascadeFrameIndex & 3u) == 1u) mask |= 0b1000;
        return mask;
    }

    /// <summary>Относительный допуск, а не точное сравнение: PrefabSceneViewport подгоняет
    /// дистанции срезов под камеру, и микросдвиги зума не должны выключать стаггеринг целиком.</summary>
    private static bool SplitsChanged(Vector4 a, Vector4 b)
    {
        float scale = MathF.Max(1f, MathF.Max(MathF.Abs(a.W), MathF.Abs(b.W)));
        var d = Vector4.Abs(a - b);
        return MathF.Max(MathF.Max(d.X, d.Y), MathF.Max(d.Z, d.W)) > scale * 1e-3f;
    }

    private unsafe (Vector4 cascadeSizes, Vector4 cascadeNearPlanes, Vector4 cascadeSplits) UpdateCascades(CameraComponent camera, Vector3 lightDirection, ref CascadedShadowComponent cascadedShadow, int updateMask)
    {
        var cascadeSplits = cascadedShadow.CascadeDistances;

        // Стартуем с кэша последнего рендера: лейны пропущенных каскадов обязаны сохранить размер
        // и near того кадра, которым их карта нарисована (от CascadeSizes масштабируется PCF-ядро).
        var cascadeSizes = _cachedCascadeSizes;
        var cascadeNearPlanes = _cachedCascadeNearPlanes;

        Vector3 lightUp = Vector3.UnitY;
        if (Math.Abs(Vector3.Dot(lightDirection, lightUp)) > 0.99f) lightUp = Vector3.UnitX;

        Matrix4x4.Invert(camera.renderCamera.view, out Matrix4x4 cameraWorld);

        // Скретч углов среза фрустума - НА СТЕКЕ и ВНЕ петли: прежний new Vector3[8] внутри неё
        // аллоцировал на каждый каскад каждый кадр.
        Span<Vector3> cornersViewSpace = stackalloc Vector3[8];

        for (int i = 0; i < ShadowRenderer.MaxCascades; i++)
        {
            if ((updateMask & (1 << i)) == 0)
            {
                // Каскад в этом кадре не перерисовывается (см. ComputeCascadeUpdateMask) - его
                // камера Cascade0..3 не трогается: матрица сэмплинга обязана остаться той, которой
                // карта нарисована, рефит без рендера дал бы рассинхрон тени с её картой.
                continue;
            }

            float n = cascadeSplits[i];
            float f = cascadeSplits[i + 1];

            float tanHalfFov = MathF.Tan(camera.data.fovRad * 0.5f);
            float nearY = n * tanHalfFov;
            float nearX = nearY * camera.data.aspect;
            float farY = f * tanHalfFov;
            float farX = farY * camera.data.aspect;

            // Камера LH, forward = +Z (см. MakePerspectiveReversedZ: clip.w = +z_view; тот же
            // приём в SimpleCullingAndRenderSystem.FitFrustumSlice - pos + forward*dist).
            // Прежние -n/-f (RH-конвенция) фитили каскады к срезам ПОЗАДИ камеры: внутри сцены
            // (превью Sponza) геометрия всё равно попадала в сферы и это маскировалось, а в
            // Scene View с орбитальной камерой снаружи мелкие каскады висели в пустоте за спиной
            // и вся видимая геометрия доставалась одному крупному.
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

            // Кайма ВОКРУГ сферы каскада - см. ShadowRenderer.CascadeMarginTexels. Орто-матрица
            // ниже строится по этому радиусу, то есть сфера ложится в карту не впритык, а с
            // отступом от края: тот самый отступ, который шейдер требует пройти внутрь, прежде чем
            // взять каскад, - иначе он отъедался у объёма изнутри и по границам каскадов шли
            // просветы. Снап тоже по НОВОМУ размеру текселя, иначе сетка снапа разъедется с картой.
            radius /= 1f - 2f * ShadowRenderer.CascadeMarginTexels / ShadowRenderer.ShadowMapSize;

            float worldUnitsPerTexel = (radius * 2.0f) / ShadowRenderer.ShadowMapSize;
            Matrix4x4 tempLightView = Matrix4x4.CreateLookAt(Vector3.Zero, lightDirection, lightUp);
            Vector3 lightSpaceCenter = Vector3.Transform(center, tempLightView);
            lightSpaceCenter.X = MathF.Floor(lightSpaceCenter.X / worldUnitsPerTexel) * worldUnitsPerTexel;
            lightSpaceCenter.Y = MathF.Floor(lightSpaceCenter.Y / worldUnitsPerTexel) * worldUnitsPerTexel;
            Matrix4x4.Invert(tempLightView, out Matrix4x4 tempLightViewInv);
            center = Vector3.Transform(lightSpaceCenter, tempLightViewInv);

            // Глаз оттянут от сферы каскада к свету на её диаметр, а far расширен на столько же:
            // кастеры МЕЖДУ солнцем и объёмом каскада (высокая геометрия над срезом фрустума -
            // башня за спиной камеры) иначе режутся near-плоскостью и не отбрасывают тень.
            // Тот же фикс, что в SimpleCullingAndRenderSystem.BuildLightData.
            float casterExtension = radius * 2.0f;
            Vector3 lightPos = center - lightDirection * (radius + casterExtension);
            float znear = 0.01f;

            // ...а far - ЕЩЁ на половину радиуса за сферу. Без этого запаса дальняя плоскость
            // касалась задней стенки сферы ровно (znear-glaz + 2r + casterExtension), то есть у
            // приёмников на задней полусфере ndc.z выходил ровно 1 или чуть больше, а
            // UnlitInstancedPS.SampleWorldLightShadow такой каскад ОТБРАСЫВАЕТ (lightNdc.z >= 1.0 ->
            // continue). Выталкивают за единицу три вещи разом: normal-offset сдвигает точку выборки
            // на полтора текселя ОТ поверхности, снап центра к сетке текселей двигает саму сферу, и
            // сверху ложится погрешность float. Точка проваливалась в следующий каскад (грубее и с
            // другим байасом), а на последнем - в «освещено», что и читается как просветы.
            // Цена запаса - только точность глубины: диапазон 4r -> 4.5r, на D32_FLOAT это ничто.
            float receiverExtension = radius * 0.5f;
            float zfar = radius * 2.0f + casterExtension + receiverExtension;

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

        // Лейны перефиченных каскадов уходят в кэш: следующий кадр стартует с них, чтобы
        // пропущенные каскады сохранили размер и near своего последнего рендера.
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
