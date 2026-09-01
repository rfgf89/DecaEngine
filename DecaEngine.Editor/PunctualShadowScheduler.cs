using System;
using System.Collections.Generic;
using System.Numerics;
using DecaEngine.Core;
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

    // Сколько светов не влезло в бюджет на прошлом отчёте: исчерпание бюджета иначе НЕМОЕ - свет
    // просто светит сквозь стены (ShadowParams.x = -1), и снаружи это неотличимо от сломанных теней.
    // Печатаем только при ИЗМЕНЕНИИ числа, иначе строка сыпалась бы каждый кадр.
    private static int _lastReportedSkipped;

    /// <summary>DECA_PUNCTUAL_CULL=0 выключает фрустум-кулинг кастеров в теневых слайсах punctual-света
    /// (слайс рисует ВСЮ сцену). Диагностический тумблер: эти плоскости - единственный потребитель
    /// GPU-фрустум-кулинга во всём движке (у камер и каскадов cullFrustum = 0), поэтому при жалобе
    /// "тень неполная/лишняя геометрия пропала" сравнение с выключенным кулингом отделяет ошибку
    /// отсечения от ошибки самой карты за один запуск.</summary>
    private static readonly bool FrustumCullCasters =
        Environment.GetEnvironmentVariable("DECA_PUNCTUAL_CULL") != "0";

    /// <summary>Тот же тумблер, что у <see cref="LightCulling.DumpPunctualLight"/> - две половины
    /// одной диагностики печатаются вместе или не печатаются вовсе.</summary>
    private static readonly bool DumpPunctual =
        Environment.GetEnvironmentVariable("DECA_PUNCTUAL_DUMP") == "1";

    private static readonly Dictionary<int, string> LastSchedulerDump = new();

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

            // Мировая позиция, не сырой локальный Position: у света под родителем (вложенная
            // иерархия) локальный Position - это смещение относительно родителя, а не место в мире
            // (см. LightCulling.GetWorldPositionRotation).
            LightCulling.GetWorldPositionRotation(entity, out var worldPos, out _);
            float distSq = Vector3.DistanceSquared(worldPos, cameraPos);
            Candidates.Add((entity, light, distSq));
        });

        Candidates.Sort(static (a, b) => a.DistSq.CompareTo(b.DistSq));

        int nextSlice = 0;
        int skipped = 0;
        foreach (var (entity, light, _) in Candidates)
        {
            int sliceCount = light.Type == LightType.Point ? 6 : 1;
            if (nextSlice + sliceCount > LightClusters.MaxShadowSlices)
            {
                // Точечный не влез - следующий спот ещё может (один слайс), продолжаем перебор.
                skipped++;
                continue;
            }

            // Мировые позиция/поворот (не сырой локальный TRS) - см. комментарий у распределения
            // кандидатов выше и LightCulling.GetWorldPositionRotation: слайс должен рендериться из
            // точки/ориентации света В МИРЕ, иначе тень вложенного света рвётся с его же освещением
            // (то читает мировую позицию, это - локальную) и выглядит "сдвинутой/перекошенной"
            // относительно каcтующей геометрии.
            LightCulling.GetWorldPositionRotation(entity, out var position, out var rotation);
            float range = light.Range;
            float near = SliceNearPlane(range);

            if (light.Type == LightType.Spot)
            {
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

            // См. LightCulling.DumpPunctualLight - вторая половина той же диагностики. Планировщик и
            // сборщик пула читают ОДИН И ТОТ ЖЕ LightComponent, но решения принимают разные (здесь -
            // сколько слайсов и с какой проекцией, там - тип для шейдера), и разойтись им нельзя:
            // шесть граней куба при DirType.w = 1 в шейдере означают, что пять из них не будут
            // прочитаны никогда. Печатаем тип ЗДЕСЬ, чтобы сравнение было прямым.
            if (DumpPunctual)
            {
                // w-столбец viewProj слайса - это ось грани в мире плюс -dot(eye, ось): именно он
                // даёт shadowClip.w в шейдере, то есть глубину приёмника вдоль оси грани. Печатается
                // потому, что единичная/нулевая матрица в буфере НЕОТЛИЧИМА от рабочей по всем
                // прочим дампам, а в шейдере выглядит как "точка позади ближней плоскости" (w <= 0)
                // или "UV далеко за квадратом слайса" - то есть как ошибка выбора грани, которой нет.
                // Ожидание: у грани f ось обязана совпасть с FaceDirs[f].
                var axes = new System.Text.StringBuilder();
                for (int face = 0; face < sliceCount; face++)
                {
                    var m = *UnsafeArray.GetPtr<Matrix4x4>(target.punctualShadowMatrices, nextSlice + face);
                    axes.Append($" [{nextSlice + face}]=({m.M14:F3},{m.M24:F3},{m.M34:F3}|{m.M44:F3})");
                }

                var line = $"[punctual] entity={entity.Id} scheduler type={light.Type} " +
                    $"slices={sliceCount} base={nextSlice} pos=({position.X:F4},{position.Y:F4},{position.Z:F4}) " +
                    $"range={range:F4} near={near:F4} sliceAxis:{axes}";
                if (!LastSchedulerDump.TryGetValue(entity.Id, out var prev) || prev != line)
                {
                    LastSchedulerDump[entity.Id] = line;
                    Console.WriteLine(line);
                }
            }

            nextSlice += sliceCount;
        }

        if (skipped != _lastReportedSkipped)
        {
            _lastReportedSkipped = skipped;
            if (skipped > 0)
            {
                Console.WriteLine($"[shadows] {skipped} punctual light(s) with ShadowStrength > 0 got no shadow " +
                    $"slice: the budget of {LightClusters.MaxShadowSlices} slices is taken (a point light costs 6 " +
                    "slices, a spot 1; the nearest lights to the camera win). Those lights shine through geometry.");
            }
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

    /// <summary>Ближняя плоскость теневого слайса света с дальностью <paramref name="range"/> (far
    /// слайса = range). ЕДИНСТВЕННЫЙ источник этого числа: его берёт и <see cref="AddSlice"/>,
    /// строя проекцию слайса, и <see cref="LightCulling.TryBuildPunctualLight"/>, кладя его в
    /// PunctualLight.ShadowParams.z для шейдера. Раньше шейдер выводил near сам, повторяя формулу
    /// в HLSL, и та уже разошлась: потолок 0.25 добавили здесь, а в UnlitInstancedPS осталось
    /// max(0.05, range*0.001), так что на Range = 20000 шейдер считал near = 20 вместо 0.25 и
    /// ошибался в производной d(ndc)/dz (а значит и в депф-байасе тени) в восемьдесят раз.
    ///
    /// near обязан остаться СТРОГО меньше far: CreatePerspectiveFieldOfViewLeftHanded кидает
    /// ArgumentOutOfRangeException при near >= far. Для Range &lt;= 0.05 (за пределами разумного, но
    /// встречается - декой-свет, тестовая сцена) прежнее max(0.05, range*0.001) давало near == far
    /// и роняло ВЕСЬ кадр (сборка слайсов идёт до отрисовки, так что одна такая лампа убивала все
    /// тени, включая чужие) - отсюда множитель range*0.5 снизу.
    ///
    /// Сверху near ограничен абсолютной величиной, а не только долей range: слагаемое range*0.001
    /// задумано как рост точности глубины вместе с дальностью, но без потолка оно рубит саму тень -
    /// при Range = 20000 near становится 20 единиц, и КАЖДЫЙ кастер ближе 20 единиц к лампе
    /// отсекается ближней плоскостью, не попадая в карту вовсе (здание вокруг источника перестаёт
    /// отбрасывать тень, свет идёт сквозь стены). Потолок 0.25 достигается только на range >= 250;
    /// до range = 50 формула даёт ровно 0.05, так что обычные лампы ведут себя без изменений.</summary>
    public static float SliceNearPlane(float range) =>
        MathF.Min(Math.Clamp(range * 0.001f, 0.05f, 0.25f), range * 0.5f);

    private static void AddSlice(ref RenderCamerasData target, Vector3 eye, Vector3 lookAt, Vector3 up,
        float fov, float near, float far, int drawCount)
    {
        var view = Matrix4x4.CreateLookAtLeftHanded(eye, lookAt, up);
        // System.Numerics мапит глубину near->0, far->1 - та же конвенция ОБЫЧНОГО Z, что у
        // ортокаскадов солнца (запись Less, сравнение LessEqual, clear 1.0).
        var proj = Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(fov, 1f, near, far);
        var viewProj = view * proj;

        // Компактный фрустум - БУКВАЛЬНО тем же выражением, что CameraComponent.CreateCullData:
        // плоскость собирается из СТРОК транспонированной проекции. Раньше здесь стояли её СТОЛБЦЫ
        // (M14+M11, M24+M21, ...) - лишняя транспозиция, из-за которой z-компонента левой плоскости
        // выходила -n*f/(f-n) вместо 1. На спот-конусе это давало условие "кастер виден, только если
        // его радиус больше смещения от оси света", то есть из карты теней выбрасывало почти всю
        // геометрию в пятне света, и карта оставалась пустой. Заметить было негде: у всех камер и
        // каскадов cullFrustum = 0 (см. CreateCullData), так что эти плоскости читает ТОЛЬКО
        // пунктуальный слот.
        var projT = Matrix4x4.Transpose(proj);
        var frustumX = projT[3] + projT[0];
        var frustumY = projT[3] + projT[1];
        MathUtils.NormalizePlane(ref frustumX);
        MathUtils.NormalizePlane(ref frustumY);

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
            cullFrustum = FrustumCullCasters ? 1 : 0,
        };

        int slice = target.punctualShadowCullData.Count;
        target.punctualShadowCullData.Add(cullData);
        target.punctualShadowLightData.Add(new LightData { CascadeMatrix0 = viewProj });
        UnsafeArray.Set(target.punctualShadowMatrices, slice, viewProj);
    }
}
