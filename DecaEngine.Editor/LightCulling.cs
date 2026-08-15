using System;
using System.Collections.Generic;
using System.Numerics;
using DecaEngine.Graphics.Diligent;
using Friflo.Engine.ECS;

namespace DecaEngine.Editor.ECS;

/// <summary>
/// CPU-кулинг punctual-светов против фрустума камеры - у КАЖДОГО типа света свой метод со своей
/// геометрией теста: точечный - сфера радиуса действия, спот - конус, направленный - виден всегда.
/// Работает во view-пространстве камеры на компактном представлении фрустума из
/// <see cref="CullData"/> (frustum = (fx.X, fx.Z, fy.Y, fy.Z), те же плоскости, что у GPU-кулинга
/// геометрии в BatchingInstancingCS.hlsl::isVisible). Зовётся из <see cref="CullingAndRenderSystem"/>
/// при сборке пер-камерного пула видимых светов; вторая, GPU-половина кулинга (свет против
/// фроксел-кластеров, тоже по-типовая) живёт в LightClusterCS.hlsl.
/// </summary>
public static class LightCulling
{
    /// <summary>Точечный свет: сфера радиуса действия против фрустума - ровно та же математика, что
    /// у GPU-теста сфер геометрии (BatchingInstancingCS.hlsl::isVisible): симметричные боковые
    /// плоскости через компактный frustum-вектор плюс диапазон глубины.</summary>
    public static bool IsPointLightVisible(in CullData cullData, in Vector3 viewPos, float range)
    {
        Vector4 f = cullData.frustum;
        bool visible = viewPos.Z * f.Y - MathF.Abs(viewPos.X) * f.X > -range;
        visible &= viewPos.Z * f.W - MathF.Abs(viewPos.Y) * f.Z > -range;
        visible &= viewPos.Z + range > cullData.znear && viewPos.Z - range < cullData.zfar;
        return visible;
    }

    /// <summary>Спот: конус против шести плоскостей фрустума. Сначала дешёвый отсев ограничивающей
    /// сферой конуса (минимальная охватывающая: до 45 градусов - через апекс и кромку основания,
    /// дальше - вокруг основания), затем точный по-плоскостной тест конуса: конус целиком за
    /// плоскостью, только если за ней И апекс, И ближайшая к плоскости точка кромки основания
    /// (тест из майкрософтовского ForwardPlus-сэмпла).</summary>
    public static bool IsSpotLightVisible(in CullData cullData, in Vector3 apexView, in Vector3 dirView,
        float range, float outerHalfAngleRad)
    {
        float cosOuter = MathF.Cos(outerHalfAngleRad);
        float sinOuter = MathF.Sin(outerHalfAngleRad);

        // Минимальная охватывающая сфера конуса высоты range с полууглом a:
        // a <= 45°: центр на оси на t = range/(2cos^2 a), радиус t (сфера проходит через апекс);
        // a  > 45°: центр в основании, радиус - радиус основания range*tan a.
        float sphereRadius;
        Vector3 sphereCenter;
        if (outerHalfAngleRad <= MathF.PI * 0.25f)
        {
            float t = range / MathF.Max(2.0f * cosOuter * cosOuter, 1e-4f);
            sphereCenter = apexView + dirView * t;
            sphereRadius = t;
        }
        else
        {
            sphereCenter = apexView + dirView * range;
            sphereRadius = range * (sinOuter / MathF.Max(cosOuter, 1e-4f));
        }

        if (!IsPointLightVisible(in cullData, in sphereCenter, sphereRadius))
        {
            return false;
        }

        // Точный тест: шесть плоскостей (нормали ВНУТРЬ фрустума, боковые проходят через начало
        // координат view-пространства). Раскладка компактного frustum-вектора - см. CreateCullData.
        Vector4 f = cullData.frustum;
        float baseRadius = range * (sinOuter / MathF.Max(cosOuter, 1e-4f));

        Span<Vector4> planes =
        [
            new Vector4(f.X, 0f, f.Y, 0f),             // левая
            new Vector4(-f.X, 0f, f.Y, 0f),            // правая
            new Vector4(0f, f.Z, f.W, 0f),             // нижняя
            new Vector4(0f, -f.Z, f.W, 0f),            // верхняя
            new Vector4(0f, 0f, 1f, -cullData.znear),  // ближняя
            new Vector4(0f, 0f, -1f, cullData.zfar),   // дальняя
        ];

        foreach (ref readonly var plane in planes)
        {
            if (ConeBehindPlane(in apexView, in dirView, range, baseRadius, in plane))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Направленный свет: бесконечный, фрустумом не отсекается - виден всегда. (В кластерный
    /// пул он и не попадает - солнце идёт своим путём через каскадные тени, см. CullingAndRenderSystem.)</summary>
    public static bool IsDirectionalLightVisible() => true;

    /// <summary>Кулит один punctual-свет против фрустума камеры МЕТОДОМ ЕГО ТИПА (точечный - сферой
    /// радиуса действия, спот - конусом) и, если свет видим, собирает его GPU-запись. Направленные
    /// света в кластерный пул не идут - направленный свет сцены обслуживается каскадами. Общая для
    /// обеих систем сборки камер: основной <see cref="CullingAndRenderSystem"/> и превью/Scene View
    /// <see cref="SimpleCullingAndRenderSystem"/>. <paramref name="shadowSlices"/> - кадровая
    /// раскладка теневых слайсов (id сущности - первый слайс, см.
    /// <see cref="PunctualShadowScheduler"/>); свет вне раскладки светит без тени.</summary>
    public static bool TryBuildPunctualLight(ref LightComponent light, Entity lightEntity,
        in CullData cullData, IReadOnlyDictionary<int, int> shadowSlices, out PunctualLight punctualLight)
    {
        punctualLight = default;

        if (light.Intensity <= 0f || light.Range <= 0f || !lightEntity.HasComponent<Position>())
        {
            return false;
        }

        Vector3 worldPos = lightEntity.Position.value;
        Vector3 viewPos = Vector3.Transform(worldPos, cullData.view);

        var shadowParams = shadowSlices.TryGetValue(lightEntity.Id, out var firstSlice)
            ? new Vector4(firstSlice, Math.Clamp(light.ShadowStrength, 0f, 1f), 0f, 0f)
            : new Vector4(-1f, 0f, 0f, 0f);

        switch (light.Type)
        {
            case LightType.Point:
            {
                if (!IsPointLightVisible(in cullData, in viewPos, light.Range))
                {
                    return false;
                }

                punctualLight = new PunctualLight
                {
                    PositionRange = new Vector4(worldPos, light.Range),
                    ColorIntensity = new Vector4(light.Color, light.Intensity),
                    DirectionType = new Vector4(0f, 0f, 0f, 0f),
                    SpotAngles = Vector4.Zero,
                    ShadowParams = shadowParams,
                };
                return true;
            }

            case LightType.Spot:
            {
                // Конвенция направления - как у солнца: локальный +Z энтити (камера LH, forward = +Z).
                var rotation = lightEntity.HasComponent<Rotation>() ? lightEntity.Rotation.value : Quaternion.Identity;
                Vector3 dirWorld = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, rotation));
                Vector3 dirView = Vector3.Normalize(Vector3.TransformNormal(dirWorld, cullData.view));

                // SpotAngle - ПОЛНЫЙ внешний угол в градусах; тесты и спад работают с полууглом.
                float outerHalfRad = Math.Clamp(light.SpotAngle, 1f, 179f) * (MathF.PI / 360f);
                if (!IsSpotLightVisible(in cullData, in viewPos, in dirView, light.Range, outerHalfRad))
                {
                    return false;
                }

                float innerFullDeg = light.InnerSpotAngle > 0f
                    ? MathF.Min(light.InnerSpotAngle, light.SpotAngle)
                    : light.SpotAngle * 0.8f;
                float cosOuter = MathF.Cos(outerHalfRad);
                float cosInner = MathF.Cos(Math.Clamp(innerFullDeg, 0f, 179f) * (MathF.PI / 360f));

                punctualLight = new PunctualLight
                {
                    PositionRange = new Vector4(worldPos, light.Range),
                    ColorIntensity = new Vector4(light.Color, light.Intensity),
                    DirectionType = new Vector4(dirWorld, 1f),
                    SpotAngles = new Vector4(cosOuter, 1f / MathF.Max(cosInner - cosOuter, 1e-4f),
                        MathF.Sin(outerHalfRad), 0f),
                    ShadowParams = shadowParams,
                };
                return true;
            }

            default:
                return false;
        }
    }

    /// <summary>Конус (апекс, ось dir, высота range, радиус основания baseRadius) целиком за
    /// плоскостью (xyz - нормаль внутрь, w - смещение)? За ней должны оказаться и апекс, и самая
    /// выступающая к плоскости точка кромки основания Q = apex + dir*range - m*baseRadius, где m -
    /// проекция нормали на плоскость основания конуса.</summary>
    private static bool ConeBehindPlane(in Vector3 apex, in Vector3 dir, float range, float baseRadius,
        in Vector4 plane)
    {
        var n = new Vector3(plane.X, plane.Y, plane.Z);

        float apexDist = Vector3.Dot(n, apex) + plane.W;
        if (apexDist >= 0f)
        {
            return false;
        }

        Vector3 m = Vector3.Cross(Vector3.Cross(n, dir), dir);
        float mLen = m.Length();
        // Вырожденный m - ось конуса параллельна нормали: кромка основания равноудалена от
        // плоскости, достаточно точки центра основания.
        Vector3 q = mLen > 1e-6f
            ? apex + dir * range - (m / mLen) * baseRadius
            : apex + dir * range;

        return Vector3.Dot(n, q) + plane.W < 0f;
    }
}
