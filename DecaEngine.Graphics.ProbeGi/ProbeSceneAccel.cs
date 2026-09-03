using System.Numerics;
using System.Runtime.InteropServices;
using Diligent;
using DecaEngine.Graphics.Diligent;

namespace DecaEngine.Graphics.ProbeGi;

/// <summary>Ray tracing acceleration structures for probe GI: one BLAS per mesh in object space
/// plus a TLAS of instance transforms, so moving an object only costs a TLAS rebuild.</summary>
public sealed class ProbeSceneAccel : IDisposable
{
    // Mirrors SceneInstance in SceneTrace.hlsl.
    [StructLayout(LayoutKind.Sequential)]
    private struct InstanceGpu
    {
        public Vector3 Albedo;

        // Base for the shader's CommittedPrimitiveIndex into the shared attribute buffer.
        public uint FirstTriangle;

        // Linear BaseColorFactor; the texture itself does not include it.
        public Vector3 BaseColorFactor;

        // Index into the hit texture set; -1 means per-triangle albedo only.
        public int TextureIndex;
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

    /// <summary>Object-space triangle attributes, indexed by InstanceID plus PrimitiveIndex.</summary>
    public IBuffer MeshTriangles => _triangles;

    /// <summary>Instance table: albedo and the triangle numbering base.</summary>
    public IBuffer Instances => _instanceTable;

    public int InstanceCount => _tlasInstances.Length;
    public int MeshCount => _blas.Length;

    /// <summary>Cost of the initial BLAS + TLAS build, in milliseconds.</summary>
    public long BuildMs { get; }

    /// <summary>Cost of the last TLAS rebuild, in milliseconds.</summary>
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

        // No index buffer: three unshared vertices per primitive keep PrimitiveIndex a direct
        // index into the attribute array, which the baker's degenerate culling would otherwise break.
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
                BaseColorFactor = instance.BaseColorFactor,
                TextureIndex = instance.TextureIndex,
            };
        }

        _instanceTable = CreateStructured(device, "ProbeSceneInstances", table, sizeof(InstanceGpu));

        // One geometry per BLAS: material is a property of the instance, not of the primitive.
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
                // Built once, traced by millions of rays: pay build time for traversal speed.
                Flags = RaytracingBuildAsFlags.PreferFastTrace,
            });

            blasScratch = Math.Max(blasScratch, _blas[slot].GetScratchBufferSizes().Build);
        }

        _tlas = device.CreateTLAS(new TopLevelASDesc
        {
            Name = "ProbeSceneTLAS",
            MaxInstanceCount = (uint)geometry.Instances.Length,
            // AllowUpdate lets per-frame motion use a refit instead of a full rebuild.
            Flags = RaytracingBuildAsFlags.PreferFastTrace | RaytracingBuildAsFlags.AllowUpdate,
        });

        // BLAS and TLAS need separate scratch buffers: their builds share one command list and
        // would race. BLAS builds share one buffer safely, since each barriers its own scratch.
        _blasScratch = device.CreateBuffer(new BufferDesc
        {
            Name = "ProbeSceneScratchBlas",
            Usage = Usage.Default,
            BindFlags = BindFlags.RayTracing,
            Size = Math.Max(blasScratch, 1024),
        });

        // Sized for both modes: the spec does not guarantee refit needs less than a full build.
        var tlasScratchSizes = _tlas.GetScratchBufferSizes();
        _tlasScratch = device.CreateBuffer(new BufferDesc
        {
            Name = "ProbeSceneScratchTlas",
            Usage = Usage.Default,
            BindFlags = BindFlags.RayTracing,
            Size = Math.Max(tlasScratchSizes.Build, tlasScratchSizes.Update),
        });

        _instanceBuffer = device.CreateBuffer(new BufferDesc
        {
            Name = "ProbeSceneInstanceBuffer",
            Usage = Usage.Default,
            BindFlags = BindFlags.RayTracing,
            // Instance record size is fixed by the runtime: 64 bytes on both DXR and Vulkan.
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
                        // Byte offset of the mesh's first vertex; the buffer spans the whole scene.
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

        // Only the transform changes between frames, so the record array is kept alive.
        _tlasInstances = new TLASBuildInstanceData[geometry.Instances.Length];
        for (int i = 0; i < _tlasInstances.Length; i++)
        {
            var instance = geometry.Instances[i];
            _tlasInstances[i] = new TLASBuildInstanceData
            {
                InstanceName = $"instance{i}",
                Blas = _blas[instance.MeshSlot],
                Transform = ToMatrix3x4(instance.Transform),
                // Reaches the shader as InstanceID(), which indexes the instance table.
                CustomId = (uint)i,
                Mask = 0xFF,
                Flags = RaytracingInstanceFlags.None,
            };
        }

        BuildTlas();
        BuildMs = sw.ElapsedMilliseconds;
        RebuildMs = 0;
    }

    // Refit traversal quality decays as instances drift from the last full build's poses.
    private const int RebuildsPerFullBuild = 64;
    private int _rebuildsSinceFullBuild;

    /// <summary>Rebuilds the TLAS for new instance poses, in ProbeInstancedGeometry order.</summary>
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

        bool refit = ++_rebuildsSinceFullBuild < RebuildsPerFullBuild;
        if (!refit)
        {
            _rebuildsSinceFullBuild = 0;
        }

        BuildTlas(refit);
        RebuildMs = sw.ElapsedMilliseconds;
    }

    private void BuildTlas(bool update = false) =>
        _context.BuildTLAS(new BuildTLASAttribs
        {
            Tlas = _tlas,
            Instances = _tlasInstances,
            InstanceBuffer = _instanceBuffer,
            ScratchBuffer = _tlasScratch,
            Update = update,
            // Inline tracing (RayQuery): no shader binding table, so no hit groups.
            HitGroupStride = 0,
            BindingMode = HitGroupBindingMode.PerInstance,
            TLASTransitionMode = ResourceStateTransitionMode.Transition,
            BLASTransitionMode = ResourceStateTransitionMode.Transition,
            InstanceBufferTransitionMode = ResourceStateTransitionMode.Transition,
            ScratchBufferTransitionMode = ResourceStateTransitionMode.Transition,
        });

    // Engine matrices are row-major with translation in the last row; instance ones use a column.
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
