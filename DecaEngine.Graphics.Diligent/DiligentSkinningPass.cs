using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using DecaEngine.Core;
using DecaEngine.Graphics;
using DecaEngine.Animation;

namespace DecaEngine.Graphics.Diligent;

/// <summary>GPU skinning: one dispatch per frame writes deformed vertices into the mega vertex buffer.</summary>
public sealed class DiligentSkinningPass
{
	// Mirrors numthreads in SkinningCS.hlsl - change both together.
	private const int GroupSize = 64;

	// Mirrors SkinRegion in SkinningCS.hlsl. One region per skinned instance.
	[StructLayout(LayoutKind.Sequential)]
	private struct SkinRegion
	{
		public uint SourceBaseVertex;
		public uint DestBaseVertex;
		public uint VertexCount;
		public uint SkinBase;

		public uint PaletteOffset;
		public uint FirstThread;
		public uint Pad0, Pad1;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct SkinConstants
	{
		public uint RegionCount;
		public uint ThreadCount;
		public uint Pad0, Pad1;
	}

	private readonly DiligentGraphicsApi _api;
	private readonly DiligentComputeMaterial _material;
	private readonly DiligentBufferHandle _constantsBuffer;

	// Skin streams of all meshes concatenated: one buffer per scene, each mesh knows its offset.
	private readonly List<SkinVertex> _skinStreamCpu = new();
	private DiligentBufferHandle _skinStreamBuffer;
	private int _skinStreamCapacity;
	private int _skinStreamUploaded;

	private readonly List<SkinRegion> _regions = new();
	private DiligentBufferHandle _regionsBuffer;
	private int _regionsCapacity;
	private bool _regionsDirty = true;

	// All instance palettes concatenated, four Vector4 per matrix (see SkinPalette in the shader).
	private readonly List<Vector4> _paletteCpu = new();
	private DiligentBufferHandle _paletteBuffer;
	private int _paletteCapacity;
	private bool _paletteDirty;

	private int _threadCount;

	public DiligentSkinningPass(DiligentGraphicsApi api)
	{
		_api = api;

		var shader = new DiligentShader(_api, "Skinning CS", "EditorAssets/shader", "SkinningCS.hlsl",
			ShaderObjectType.Compute, "CSMain");

		_material = new DiligentComputeMaterial("Skinning CS", _api);
		_material.SetShader(shader);

		_constantsBuffer = (DiligentBufferHandle)_api.CreateBuffer<SkinConstants>(new BufferInfo
		{
			name = "Skin Constants",
			type = BufferHandleType.Constant,
		});

		_material.SetBuffer("SkinConstants", _constantsBuffer);
	}

	/// <summary>Whether this frame has anything to skin; a static scene must cost exactly zero.</summary>
	public bool HasWork => _regions.Count > 0 && _threadCount > 0;

	/// <summary>Appends a mesh skin stream and returns its base; call once per mesh, not per instance.</summary>
	public int RegisterSkinStream(ReadOnlySpan<SkinVertex> skin)
	{
		int skinBase = _skinStreamCpu.Count;
		foreach (var vertex in skin)
		{
			_skinStreamCpu.Add(vertex);
		}

		return skinBase;
	}

	/// <summary>Registers a skinned instance and returns its palette offset, measured in matrices.</summary>
	public int AddInstance(int sourceBaseVertex, int destBaseVertex, int vertexCount, int skinBase, int jointCount)
	{
		int paletteOffset = _paletteCpu.Count / 4;

		// Reserve identity matrices up front: an unwritten palette slot would collapse the mesh.
		for (int i = 0; i < jointCount; i++)
		{
			AppendMatrix(Matrix4x4.Identity);
		}

		_regions.Add(new SkinRegion
		{
			SourceBaseVertex = (uint)sourceBaseVertex,
			DestBaseVertex = (uint)destBaseVertex,
			VertexCount = (uint)vertexCount,
			SkinBase = (uint)skinBase,
			PaletteOffset = (uint)paletteOffset,
			// FirstThread is filled by RebuildThreadOffsets: it depends on all regions at once.
		});

		_regionsDirty = true;
		_paletteDirty = true;

		// Must run here, not lazily in Execute: HasWork gates Execute on _threadCount.
		RebuildThreadOffsets();
		return paletteOffset;
	}

	/// <summary>Updates the skinning palette of one instance for the current frame.</summary>
	public void SetPalette(int paletteOffset, ReadOnlySpan<Matrix4x4> matrices)
	{
		int start = paletteOffset * 4;
		int needed = start + matrices.Length * 4;

		// Throw rather than clamp: an overlong palette would corrupt the neighbouring instance.
		if (needed > _paletteCpu.Count)
		{
			throw new ArgumentException(
				$"Skinning palette overflow: instance at {paletteOffset} needs {matrices.Length} matrices, " +
				$"reserved up to {_paletteCpu.Count / 4 - paletteOffset}.");
		}

		for (int i = 0; i < matrices.Length; i++)
		{
			var m = matrices[i];
			int slot = start + i * 4;
			_paletteCpu[slot + 0] = new Vector4(m.M11, m.M12, m.M13, m.M14);
			_paletteCpu[slot + 1] = new Vector4(m.M21, m.M22, m.M23, m.M24);
			_paletteCpu[slot + 2] = new Vector4(m.M31, m.M32, m.M33, m.M34);
			_paletteCpu[slot + 3] = new Vector4(m.M41, m.M42, m.M43, m.M44);
		}

		_paletteDirty = true;
	}

	/// <summary>Drops all registrations; required whenever the mega vertex buffer is rebuilt.</summary>
	public void Reset()
	{
		_skinStreamCpu.Clear();
		_regions.Clear();
		_paletteCpu.Clear();

		_skinStreamUploaded = 0;
		_regionsDirty = true;
		_paletteDirty = true;
		_threadCount = 0;
	}

	private void AppendMatrix(Matrix4x4 m)
	{
		_paletteCpu.Add(new Vector4(m.M11, m.M12, m.M13, m.M14));
		_paletteCpu.Add(new Vector4(m.M21, m.M22, m.M23, m.M24));
		_paletteCpu.Add(new Vector4(m.M31, m.M32, m.M33, m.M34));
		_paletteCpu.Add(new Vector4(m.M41, m.M42, m.M43, m.M44));
	}

	// Prefix sums of vertex counts; the shader binary-searches them to find its region.
	private void RebuildThreadOffsets()
	{
		uint thread = 0;
		for (int i = 0; i < _regions.Count; i++)
		{
			var region = _regions[i];
			region.FirstThread = thread;
			_regions[i] = region;
			thread += region.VertexCount;
		}

		_threadCount = (int)thread;
	}

	// Dispatched on the immediate context, bypassing the frozen graph: group count varies per frame.
	/// <summary>Uploads dirty buffers and dispatches skinning; must run before any geometry read.</summary>
	public void Execute(DiligentBufferHandle megaVertexBuffer)
	{
		if (megaVertexBuffer == null || !HasWork)
		{
			return;
		}

		UploadSkinStream();
		UploadRegions();
		UploadPalette();

		_material.SetBuffer("MegaVertices", megaVertexBuffer);

		UploadConstants(new SkinConstants
		{
			RegionCount = (uint)_regions.Count,
			ThreadCount = (uint)_threadCount,
		});

		// Diligent reports buffer creation failure via the log, not an exception; an unbound SRB
		// variable would crash DispatchCompute, so skip the frame instead.
		if (_skinStreamBuffer?.Buffer == null || _regionsBuffer?.Buffer == null ||
			_paletteBuffer?.Buffer == null || megaVertexBuffer.Buffer == null)
		{
			Console.WriteLine("[skinning] pass skipped: one of the buffers was not created " +
				$"(stream={_skinStreamBuffer?.Buffer != null}, regions={_regionsBuffer?.Buffer != null}, " +
				$"palette={_paletteBuffer?.Buffer != null}, mega={megaVertexBuffer.Buffer != null})");
			return;
		}

		// Dispatch transitions the mega buffer to UnorderedAccess; SetVertexBuffers moves it back.
		_material.Dispatch((uint)((_threadCount + GroupSize - 1) / GroupSize), 1, 1);
	}

	private void UploadSkinStream()
	{
		if (_skinStreamCpu.Count == 0 || _skinStreamUploaded == _skinStreamCpu.Count)
		{
			return;
		}

		// Skin data never changes after registration, so grow x2 and upload only the new tail.
		if (_skinStreamBuffer == null || _skinStreamCpu.Count > _skinStreamCapacity)
		{
			_skinStreamCapacity = Math.Max(_skinStreamCpu.Count, Math.Max(1024, _skinStreamCapacity * 2));
			_skinStreamBuffer?.Release();
			_skinStreamBuffer = (DiligentBufferHandle)_api.CreateBuffer<SkinVertex>(_skinStreamCapacity,
				new BufferInfo
				{
					name = "Skin Stream Buffer",
					type = BufferHandleType.Structured,
					access = HandleAccess.Compute,
				});

			_skinStreamUploaded = 0;
			_material.SetBuffer("SkinStream", _skinStreamBuffer);
		}

		UploadList(_skinStreamBuffer, CollectionsMarshal.AsSpan(_skinStreamCpu), _skinStreamUploaded);
		_skinStreamUploaded = _skinStreamCpu.Count;
	}

	private void UploadRegions()
	{
		if (!_regionsDirty)
		{
			return;
		}

		if (_regionsBuffer == null || _regions.Count > _regionsCapacity)
		{
			_regionsCapacity = Math.Max(_regions.Count, Math.Max(16, _regionsCapacity * 2));
			_regionsBuffer?.Release();
			_regionsBuffer = (DiligentBufferHandle)_api.CreateBuffer<SkinRegion>(_regionsCapacity,
				new BufferInfo
				{
					name = "Skin Regions Buffer",
					type = BufferHandleType.Structured,
					access = HandleAccess.Compute,
				});

			_material.SetBuffer("SkinRegions", _regionsBuffer);
		}

		UploadList(_regionsBuffer, CollectionsMarshal.AsSpan(_regions), 0);
		_regionsDirty = false;
	}

	private void UploadPalette()
	{
		if (!_paletteDirty || _paletteCpu.Count == 0)
		{
			return;
		}

		if (_paletteBuffer == null || _paletteCpu.Count > _paletteCapacity)
		{
			_paletteCapacity = Math.Max(_paletteCpu.Count, Math.Max(1024, _paletteCapacity * 2));
			_paletteBuffer?.Release();
			_paletteBuffer = (DiligentBufferHandle)_api.CreateBuffer<Vector4>(_paletteCapacity,
				new BufferInfo
				{
					name = "Skin Palette Buffer",
					type = BufferHandleType.Structured,
					access = HandleAccess.Compute,
				});

			_material.SetBuffer("SkinPalette", _paletteBuffer);
		}

		// Whole palette is re-uploaded: tracking dirty ranges costs more than a few dozen KB.
		UploadList(_paletteBuffer, CollectionsMarshal.AsSpan(_paletteCpu), 0);
		_paletteDirty = false;
	}

	private unsafe void UploadConstants(SkinConstants constants)
	{
		_api.ImmediateContext.UpdateBuffer(_constantsBuffer.Buffer, 0, (uint)sizeof(SkinConstants),
			new IntPtr(&constants), global::Diligent.ResourceStateTransitionMode.Transition);
	}

	private unsafe void UploadList<T>(DiligentBufferHandle buffer, ReadOnlySpan<T> items, int from) where T : unmanaged
	{
		int count = items.Length - from;
		if (count <= 0)
		{
			return;
		}

		fixed (T* ptr = items.Slice(from))
		{
			// Immediate context: the upload must happen before the frozen commands are replayed.
			_api.ImmediateContext.UpdateBuffer(buffer.Buffer,
				(uint)(from * sizeof(T)), (uint)(count * sizeof(T)), new IntPtr(ptr),
				global::Diligent.ResourceStateTransitionMode.Transition);
		}
	}
}
