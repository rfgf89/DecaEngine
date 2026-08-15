using System;
using System.Collections.Generic;
using System.Numerics;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
using Friflo.Engine.ECS;
using UnsafeCollections.Collections.Unsafe;

namespace DecaEngine.Editor.ECS;

/// <summary>
/// Раздаёт кадровый бюджет теневых слайсов (<see cref="LightClusters.MaxShadowSlices"/> в texture
/// array) punctual-светам с ShadowStrength &gt; 0 и строит данные каждого слайса: спот занимает один
/// слайс (перспективная проекция по внешнему углу конуса), точечный - шесть (грани куба, fov 90 с
/// небольшим перехлёстом под PCF на швах). Бюджет уходит ближайшим к камере светам; не влезшие
/// светят без тени (ShadowParams.x = -1).
///
/// Список слайсов ВСЕГДА заполняется до полной ёмкости: замороженный ForwardPass пишет фиксированную
/// петлю по всем слайсам, и мёртвый слайс обязан несть drawCount = 0 (кулинг никого не пропускает,
/// indirect-дроу рисуют пусто). Зовётся обеими системами сборки камер - основной
/// <see cref="CullingAndRenderSystem"/> и превью/Scene View <see cref="SimpleCullingAndRenderSystem"/>.
/// </summary>
public static unsafe class PunctualShadowScheduler
{
    // Грани куба точечного света: индексация ОБЯЗАНА совпадать с выбором грани по доминирующей оси
    // в UnlitInstancedPS.hlsl (+X,-X,+Y,-Y,+Z,-Z). Up-вектора произвольны (лишь бы не коллинеарны
    // оси) - ориентация внутри слайса запечена в его матрице.
    private static readonly Vector3[] FaceDirs =
    [
        Vector3.UnitX, -Vector3.UnitX,
        Vector3.UnitY, -Vector3.UnitY,
        Vector3.UnitZ, -Vector3.UnitZ,
    ];

    private static readonly Vector3[] FaceUps =
    [
        Vector3.UnitY, Vector3.UnitY,
        Vector3.UnitZ, Vector3.UnitZ,
        Vector3.UnitY, Vector3.UnitY,
    ];

    // Скретчи кадра - рендер-системы работают на главном потоке, статики безопасны.
    private static readonly List<(Entity Entity, LightComponent Light, float DistSq)> Candidates = new();

    /// <summary>Заполняет слайсы теней кадра в <paramref name="target"/> (cull/light-данные для
    /// записи shadow map + матрицы для сэмплинга) и раскладку "id сущности света - первый слайс" в
    /// <paramref name="assignments"/> (её читает LightCulling.TryBuildPunctualLight, собирая
    /// ShadowParams). Списки target.punctualShadow* обязаны быть пустыми (после Clear).</summary>
    public static void BuildShadowSlices(ArchetypeQuery<LightComponent> punctualLights, Vector3 cameraPos,
        int drawCount, ref RenderCamerasData target, Dictionary<int, int> assignments)
    {
        assignments.Clear();
        Candidates.Clear();

        punctualLights.ForEachEntity((ref LightComponent light, Entity entity) =>
        {
            if (light.ShadowStrength <= 0f || light.Intensity <= 0f || light.Range <= 0f ||
                light.Type is not (LightType.Point or LightType.Spot) ||
                !entity.HasComponent<Position>())
            {
                return;
            }

            float distSq = Vector3.DistanceSquared(entity.Position.value, cameraPos);
            Candidates.Add((entity, light, distSq));
        });

        Candidates.Sort(static (a, b) => a.DistSq.CompareTo(b.DistSq));

        int nextSlice = 0;
        foreach (var (entity, light, _) in Candidates)
        {
            int sliceCount = light.Type == LightType.Point ? 6 : 1;
            if (nextSlice + sliceCount > LightClusters.MaxShadowSlices)
            {
                // Точечный не влез - следующий спот ещё может (один слайс), продолжаем перебор.
                continue;
            }

            var position = entity.Position.value;
            float range = light.Range;
            float near = MathF.Max(0.05f, range * 0.001f);

            if (light.Type == LightType.Spot)
            {
                var rotation = entity.HasComponent<Rotation>() ? entity.Rotation.value : Quaternion.Identity;
                var dir = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, rotation));
                var up = MathF.Abs(dir.Y) > 0.95f ? Vector3.UnitX : Vector3.UnitY;

                // Полный внешний угол конуса и есть fov слайса: кромка конуса ложится на край карты,
                // а там спад по углу уже погасил свет - краевых артефактов не видно.
                float fov = Math.Clamp(light.SpotAngle, 1f, 179f) * (MathF.PI / 180f);
                AddSlice(ref target, position, position + dir, up, fov, near, range, drawCount);
            }
            else
            {
                // Перехлёст 2% над 90 градусами: PCF-тапы у кромки грани остаются внутри её карты,
                // выбор грани по доминирующей оси при этом всегда попадает в её (расширенный) фрустум.
                float fov = MathF.PI * 0.5f * 1.02f;
                for (int face = 0; face < 6; face++)
                {
                    AddSlice(ref target, position, position + FaceDirs[face], FaceUps[face],
                        fov, near, range, drawCount);
                }
            }

            assignments[entity.Id] = nextSlice;
            nextSlice += sliceCount;
        }

        // Добить до полной ёмкости мёртвыми слайсами: drawCount = 0 - кулинг никого не пропускает.
        while (target.punctualShadowCullData.Count < LightClusters.MaxShadowSlices)
        {
            int slice = target.punctualShadowCullData.Count;
            target.punctualShadowCullData.Add(default);
            target.punctualShadowLightData.Add(default);
            UnsafeArray.Set(target.punctualShadowMatrices, slice, Matrix4x4.Identity);
        }
    }

    private static void AddSlice(ref RenderCamerasData target, Vector3 eye, Vector3 lookAt, Vector3 up,
        float fov, float near, float far, int drawCount)
    {
        var view = Matrix4x4.CreateLookAtLeftHanded(eye, lookAt, up);
        // System.Numerics мапит глубину near->0, far->1 - та же конвенция ОБЫЧНОГО Z, что у
        // ортокаскадов солнца (запись Less, сравнение LessEqual, clear 1.0).
        var proj = Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(fov, 1f, near, far);
        var viewProj = view * proj;

        // Компактный фрустум - той же формулой, что CameraComponent.CreateCullData.
        var projT = Matrix4x4.Transpose(proj);
        var frustumX = new Vector4(projT.M14 + projT.M11, projT.M24 + projT.M21,
            projT.M34 + projT.M31, projT.M44 + projT.M41);
        var frustumY = new Vector4(projT.M14 + projT.M12, projT.M24 + projT.M22,
            projT.M34 + projT.M32, projT.M44 + projT.M42);
        DecaEngine.Graphics.Diligent.MathUtils.NormalizePlane(ref frustumX);
        DecaEngine.Graphics.Diligent.MathUtils.NormalizePlane(ref frustumY);

        var cullData = new CullData
        {
            view = view,
            frustum = new Vector4(frustumX.X, frustumX.Z, frustumY.Y, frustumY.Z),
            P00 = proj.M11,
            P11 = proj.M22,
            znear = near,
            zfar = far,
            drawCount = drawCount,
            // Бит 0 - фрустум-кулинг (кастеры вне конуса света в его карту не попадают), без LOD:
            // тень геометрии обязана совпадать с тем, что нарисовано в основном виде.
            cullFrustum = 1,
        };

        int slice = target.punctualShadowCullData.Count;
        target.punctualShadowCullData.Add(cullData);
        target.punctualShadowLightData.Add(new LightData { CascadeMatrix0 = viewProj });
        UnsafeArray.Set(target.punctualShadowMatrices, slice, viewProj);
    }
}
