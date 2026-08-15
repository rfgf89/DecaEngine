using System.Numerics;
using System.Runtime.InteropServices;
using DecaEngine;
using Diligent;

namespace DecaEngine.Editor;

/// <summary>
/// Аппаратные структуры ускорения под трассировку probe-GI: BLAS на МЕШ в объектном пространстве
/// плюс TLAS из матриц инстансов (см. <see cref="ProbeInstancedGeometry"/>).
///
/// Двухуровневая схема - это и есть ответ на «движущийся мир». Прежняя версия строила один BLAS из
/// мировой треугольной похлёбки бейкера: атрибуты в шейдере брались прямо по
/// CommittedPrimitiveIndex, без таблиц и без пересчёта нормали, - но любое движение объекта означало
/// пересборку всей структуры, то есть динамики не было по построению. Теперь геометрия от позы не
/// зависит: BLAS-ы строятся один раз, а движение стоит <see cref="Rebuild"/> одного TLAS.
///
/// Плата за это - индирекция при попадании: шейдеру нужны таблица инстансов
/// (<see cref="Instances"/>) и общий буфер атрибутов (<see cref="MeshTriangles"/>), а нормаль
/// приходится переносить в мир матрицей инстанса (см. SceneTrace.hlsl).
///
/// BLAS строятся по СВОЕМУ буферу позиций, развёрнутому из тех же треугольников, что читает шейдер,
/// а не по вершинным буферам мешей: только так CommittedPrimitiveIndex гарантированно индексирует
/// массив атрибутов: бейкер выбрасывает вырожденные треугольники, и нумерация примитивов после этого
/// с индексным буфером меша уже не совпадает.
/// </summary>
public sealed class ProbeSceneAccel : IDisposable
{
    /// <summary>Запись таблицы инстансов - зеркало SceneInstance в SceneTrace.hlsl.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct InstanceGpu
    {
        public Vector3 Albedo;

        /// <summary>Индекс первого треугольника МЕША инстанса в общем буфере атрибутов: к нему
        /// шейдер прибавляет CommittedPrimitiveIndex.</summary>
        public uint FirstTriangle;
    }

    private readonly IDeviceContext _context;
    private readonly IBuffer _vertices;
    private readonly IBuffer _triangles;
    private readonly IBuffer _instanceTable;
    private readonly IBuffer _instanceBuffer;
    private readonly IBuffer _blasScratch;
    private readonly IBuffer _tlasScratch;
    private readonly IBottomLevelAS[] _blas;
    private readonly ITopLevelAS _tlas;
    private readonly TLASBuildInstanceData[] _tlasInstances;

    public ITopLevelAS Tlas => _tlas;

    /// <summary>Атрибуты треугольников в объектном пространстве - шейдер читает их по
    /// InstanceID+PrimitiveIndex (см. SceneTrace.hlsl).</summary>
    public IBuffer MeshTriangles => _triangles;

    /// <summary>Таблица инстансов: альбедо и база нумерации треугольников.</summary>
    public IBuffer Instances => _instanceTable;

    public int InstanceCount => _tlasInstances.Length;
    public int MeshCount => _blas.Length;

    /// <summary>Во что обошлась ПЕРВАЯ сборка (BLAS-ы + TLAS), мс - для диагностики.</summary>
    public long BuildMs { get; }

    /// <summary>Во что обошлась последняя пересборка TLAS, мс. Это цена движения объекта - ради
    /// того, чтобы она была на порядки меньше <see cref="BuildMs"/>, схема и двухуровневая.</summary>
    public long RebuildMs { get; private set; }

    public unsafe ProbeSceneAccel(DiligentGraphicsApi api, ProbeInstancedGeometry geometry)
    {
        if (geometry.Instances.Length == 0 || geometry.Meshes.Length == 0)
        {
            throw new InvalidOperationException(
                "Probe scene has no instanced geometry to build acceleration structures from");
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var device = api.Device;
        _context = api.ImmediateContext;

        // Позиции для сборки: треугольник хранится как вершина плюс два ребра, трассировке нужны
        // три точки. Индексного буфера нет - развёртка по три вершины на примитив и делает
        // PrimitiveIndex прямым индексом в массив атрибутов.
        var source = geometry.Triangles;
        var positions = new Vector3[(long)source.Length * 3];
        for (int i = 0; i < source.Length; i++)
        {
            ref var tri = ref source[i];
            positions[i * 3 + 0] = tri.A;
            positions[i * 3 + 1] = tri.A + tri.E1;
            positions[i * 3 + 2] = tri.A + tri.E2;
        }

        fixed (Vector3* ptr = positions)
        {
            _vertices = device.CreateBuffer(new BufferDesc
            {
                Name = "ProbeSceneVertices",
                Usage = Usage.Immutable,
                BindFlags = BindFlags.RayTracing,
                Size = (ulong)((long)positions.Length * sizeof(Vector3)),
            }, new BufferData
            {
                Data = new IntPtr(ptr),
                DataSize = (ulong)((long)positions.Length * sizeof(Vector3)),
            });
        }

        _triangles = CreateStructured(device, "ProbeSceneMeshTriangles", source, sizeof(BvhTriangleGpu));

        var table = new InstanceGpu[geometry.Instances.Length];
        for (int i = 0; i < table.Length; i++)
        {
            var instance = geometry.Instances[i];
            table[i] = new InstanceGpu
            {
                Albedo = instance.Albedo,
                FirstTriangle = (uint)geometry.Meshes[instance.MeshSlot].First,
            };
        }

        _instanceTable = CreateStructured(device, "ProbeSceneInstances", table, sizeof(InstanceGpu));

        // BLAS на меш. Одна геометрия в каждом - разбивать меш по материалам незачем: материал
        // здесь свойство инстанса, а не примитива.
        _blas = new IBottomLevelAS[geometry.Meshes.Length];
        ulong blasScratch = 0;
        for (int slot = 0; slot < geometry.Meshes.Length; slot++)
        {
            var (first, count) = geometry.Meshes[slot];
            _blas[slot] = device.CreateBLAS(new BottomLevelASDesc
            {
                Name = $"ProbeSceneBLAS{slot}",
                Triangles =
                [
                    new BLASTriangleDesc
                    {
                        GeometryName = "geometry",
                        MaxVertexCount = (uint)(count * 3),
                        VertexValueType = global::Diligent.ValueType.Float32,
                        VertexComponentCount = 3,
                        MaxPrimitiveCount = (uint)count,
                        IndexType = global::Diligent.ValueType.Undefined,
                    }
                ],
                // Строим один раз, трассируем миллионами лучей - берём самый быстрый обход ценой
                // более долгой сборки.
                Flags = RaytracingBuildAsFlags.PreferFastTrace,
            });

            blasScratch = Math.Max(blasScratch, _blas[slot].GetScratchBufferSizes().Build);
        }

        _tlas = device.CreateTLAS(new TopLevelASDesc
        {
            Name = "ProbeSceneTLAS",
            MaxInstanceCount = (uint)geometry.Instances.Length,
            Flags = RaytracingBuildAsFlags.PreferFastTrace,
        });

        // Раздельные scratch-буферы под BLAS и TLAS. Общий буфер - гонка: сборки идут подряд в
        // одном списке команд, и вторая пишет в память, которую ещё читает первая. Барьер между
        // ними тоже решил бы, но раздельные буферы дешевле по коду и не зависят от того, какие
        // барьеры вставит бэкенд.
        //
        // Скретч BLAS-ов один на всех и по САМОМУ большому: сборки идут последовательно в одном
        // списке команд... а значит переиспользование памяти между ними - это та же гонка. Здесь
        // она безопасна ровно потому, что каждая сборка сама дожидается своей области: BuildBLAS
        // выставляет барьеры по скретчу (ScratchBufferTransitionMode). Экономия существенна: на
        // сотне мешей отдельные скретчи - это сотня буферов на десятки мегабайт.
        _blasScratch = device.CreateBuffer(new BufferDesc
        {
            Name = "ProbeSceneScratchBlas",
            Usage = Usage.Default,
            BindFlags = BindFlags.RayTracing,
            Size = Math.Max(blasScratch, 1024),
        });

        _tlasScratch = device.CreateBuffer(new BufferDesc
        {
            Name = "ProbeSceneScratchTlas",
            Usage = Usage.Default,
            BindFlags = BindFlags.RayTracing,
            Size = _tlas.GetScratchBufferSizes().Build,
        });

        _instanceBuffer = device.CreateBuffer(new BufferDesc
        {
            Name = "ProbeSceneInstanceBuffer",
            Usage = Usage.Default,
            BindFlags = BindFlags.RayTracing,
            // Размер записи инстанса задаёт рантайм: 64 байта и в DXR, и в Vulkan.
            Size = (ulong)geometry.Instances.Length * 64UL,
        });

        for (int slot = 0; slot < geometry.Meshes.Length; slot++)
        {
            var (first, count) = geometry.Meshes[slot];
            _context.BuildBLAS(new BuildBLASAttribs
            {
                Blas = _blas[slot],
                TriangleData =
                [
                    new BLASBuildTriangleData
                    {
                        GeometryName = "geometry",
                        VertexBuffer = _vertices,
                        // Смещение В БАЙТАХ до первой вершины меша: буфер общий на всю сцену.
                        VertexOffset = (uint)((long)first * 3 * sizeof(Vector3)),
                        VertexStride = (uint)sizeof(Vector3),
                        VertexCount = (uint)(count * 3),
                        VertexValueType = global::Diligent.ValueType.Float32,
                        VertexComponentCount = 3,
                        PrimitiveCount = (uint)count,
                        Flags = RaytracingGeometryFlags.Opaque,
                    }
                ],
                ScratchBuffer = _blasScratch,
                BLASTransitionMode = ResourceStateTransitionMode.Transition,
                GeometryTransitionMode = ResourceStateTransitionMode.Transition,
                ScratchBufferTransitionMode = ResourceStateTransitionMode.Transition,
            });
        }

        // Заготовки записей TLAS: от кадра к кадру в них меняется ТОЛЬКО матрица, поэтому массив
        // живёт вместе с объектом, а не пересобирается на каждое движение.
        _tlasInstances = new TLASBuildInstanceData[geometry.Instances.Length];
        for (int i = 0; i < _tlasInstances.Length; i++)
        {
            var instance = geometry.Instances[i];
            _tlasInstances[i] = new TLASBuildInstanceData
            {
                InstanceName = $"instance{i}",
                Blas = _blas[instance.MeshSlot],
                Transform = ToMatrix3x4(instance.Transform),
                // CustomId уезжает в шейдер как InstanceID() - по нему берётся запись таблицы.
                CustomId = (uint)i,
                Mask = 0xFF,
                Flags = RaytracingInstanceFlags.None,
            };
        }

        BuildTlas();
        BuildMs = sw.ElapsedMilliseconds;
        RebuildMs = 0;
    }

    /// <summary>Пересобирает TLAS под новые позы инстансов - покадровая операция динамического мира.
    /// BLAS-ы не трогаются: геометрия в объектном пространстве от позы не зависит.
    ///
    /// Порядок матриц - это порядок <see cref="ProbeInstancedGeometry.Instances"/>, он же нумерация
    /// InstanceID в шейдере. Длина обязана совпадать: инстанс, появившийся или исчезнувший в сцене,
    /// меняет и таблицу атрибутов, то есть требует пересоздания всего объекта.</summary>
    public void Rebuild(ReadOnlySpan<Matrix4x4> transforms)
    {
        if (transforms.Length != _tlasInstances.Length)
        {
            throw new ArgumentException(
                $"Expected {_tlasInstances.Length} instance transforms, got {transforms.Length}",
                nameof(transforms));
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < _tlasInstances.Length; i++)
        {
            _tlasInstances[i].Transform = ToMatrix3x4(transforms[i]);
        }

        BuildTlas();
        RebuildMs = sw.ElapsedMilliseconds;
    }

    private void BuildTlas() =>
        _context.BuildTLAS(new BuildTLASAttribs
        {
            Tlas = _tlas,
            Instances = _tlasInstances,
            InstanceBuffer = _instanceBuffer,
            ScratchBuffer = _tlasScratch,
            // Таблиц привязки шейдеров нет: трассировка inline (RayQuery), hit-группы не участвуют -
            // луч возвращает попадание прямо в вызывающий шейдер.
            HitGroupStride = 0,
            BindingMode = HitGroupBindingMode.PerInstance,
            TLASTransitionMode = ResourceStateTransitionMode.Transition,
            BLASTransitionMode = ResourceStateTransitionMode.Transition,
            InstanceBufferTransitionMode = ResourceStateTransitionMode.Transition,
            ScratchBufferTransitionMode = ResourceStateTransitionMode.Transition,
        });

    /// <summary>Матрица движка (строковая, перенос в последней СТРОКЕ) в матрицу инстанса
    /// трассировки (3x4, перенос в последнем СТОЛБЦЕ).</summary>
    private static Matrix3x4 ToMatrix3x4(Matrix4x4 m) => new(
        m.M11, m.M21, m.M31, m.M41,
        m.M12, m.M22, m.M32, m.M42,
        m.M13, m.M23, m.M33, m.M43);

    private static unsafe IBuffer CreateStructured<T>(IRenderDevice device, string name, T[] data,
        int stride) where T : unmanaged
    {
        fixed (T* ptr = data)
        {
            return device.CreateBuffer(new BufferDesc
            {
                Name = name,
                Usage = Usage.Immutable,
                BindFlags = BindFlags.ShaderResource,
                Mode = BufferMode.Structured,
                ElementByteStride = (uint)stride,
                Size = (ulong)((long)data.Length * sizeof(T)),
            }, new BufferData
            {
                Data = new IntPtr(ptr),
                DataSize = (ulong)((long)data.Length * sizeof(T)),
            });
        }
    }

    public void Dispose()
    {
        _tlas.Dispose();
        foreach (var blas in _blas)
        {
            blas.Dispose();
        }

        _instanceBuffer.Dispose();
        _instanceTable.Dispose();
        _triangles.Dispose();
        _tlasScratch.Dispose();
        _blasScratch.Dispose();
        _vertices.Dispose();
    }
}
