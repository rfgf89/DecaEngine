using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
using UnsafeCollections.Collections.Native;
using UnsafeCollections.Collections.Unsafe;

namespace DecaEngine.Graphics.ProbeGi;

/// <summary>GPU side of probe GI: the atlases, their material bindings and grid parameters.</summary>
public sealed class ProbeGiTextures : IReleaseObject
{
	public IGpuTexture Sh0 { get; }
	public IGpuTexture Sh1 { get; }
	public IGpuTexture Sh2 { get; }
	public IGpuTexture Sh3 { get; }

	/// <summary>Octahedral visibility atlas (DDGI depth).</summary>
	public IGpuTexture Vis { get; }

	/// <summary>Relocation atlas: probe offset from its grid node.</summary>
	public IGpuTexture Offset { get; }

	/// <summary>World corner of the volume; not constant, a scrolling volume follows the camera.</summary>
	public Vector4 GridOrigin { get; private set; }

	public Vector4 GridCell { get; }
	public Vector4 GridCounts { get; }

	/// <summary>Smallest grid step; the base for the sample normal bias.</summary>
	public float MinCellSize { get; }

	private readonly IGraphicsApi _api;

	/// <summary>Atlases carry a UAV and are written by the compute round; Update is unused then.</summary>
	public bool GpuWritable { get; }

	public ProbeGiTextures(IGraphicsApi api, ProbeGiBakeResult result, string namePrefix,
		bool gpuWritable = false)
	{
		_api = api;
		GpuWritable = gpuWritable;

		// Atlas layout: width is the X axis, height stacks the Z planes.
		int width = result.ShWidth;
		int height = result.ShHeight;

		// Mutable, not immutable: the progressive bake refills the atlases every round.
		Sh0 = api.CreateTexture2DMutable($"{namePrefix} ProbeSH0", width, height, true, gpuWritable);
		Sh1 = api.CreateTexture2DMutable($"{namePrefix} ProbeSH1", width, height, true, gpuWritable);
		Sh2 = api.CreateTexture2DMutable($"{namePrefix} ProbeSH2", width, height, true, gpuWritable);
		Sh3 = api.CreateTexture2DMutable($"{namePrefix} ProbeSH3", width, height, true, gpuWritable);
		Vis = api.CreateTexture2DMutable($"{namePrefix} ProbeVis",
			width * ProbeGiBakeResult.VisRes, height * ProbeGiBakeResult.VisRes, true, gpuWritable);
		Offset = api.CreateTexture2DMutable($"{namePrefix} ProbeOffset", width, height, true, gpuWritable);

		GridOrigin = new Vector4(result.Origin, 1f);

		// w = sample normal bias in world units (0.3 of a cell), against leaks through thin walls.
		var cell = result.Cell;
		MinCellSize = MathF.Min(cell.X, MathF.Min(cell.Y, cell.Z));
		GridCell = new Vector4(cell, MinCellSize * 0.3f);
		GridCounts = new Vector4(result.CountX, result.CountY, result.CountZ, 0f);

		// In GPU mode the first round fills the atlases, so there is nothing to upload.
		if (!gpuWritable)
		{
			// Must follow GridCounts: Update validates against them.
			Update(result);
		}
	}

	/// <summary>Uploads a fresh bake into the existing atlases; the grid must still match.</summary>
	public void Update(ProbeGiBakeResult result)
	{
		if (!Matches(result))
		{
			throw new ArgumentException("Probe grid size does not match the allocated atlases", nameof(result));
		}

		_api.UpdateTexture2D(Sh0, result.Sh0);
		_api.UpdateTexture2D(Sh1, result.Sh1);
		_api.UpdateTexture2D(Sh2, result.Sh2);
		_api.UpdateTexture2D(Sh3, result.Sh3);
		_api.UpdateTexture2D(Vis, result.Vis);
		_api.UpdateTexture2D(Offset, result.Offset);
	}

	/// <summary>Whether the bake uses the same grid as the allocated atlases.</summary>
	public bool Matches(ProbeGiBakeResult result) =>
		result.CountX == (int)GridCounts.X
		&& result.CountY == (int)GridCounts.Y
		&& result.CountZ == (int)GridCounts.Z;

	/// <summary>Binds the atlases to a model's PRIMARY material set; only for sole owners.</summary>
	public void Bind(ModelLoader model, string slotSuffix = "") => Bind(model.materialObjects, slotSuffix);

	/// <summary>Binds the atlases to one environment's own material set.</summary>
	// slotSuffix: "" for the base volume, "_C1"/"_C2" for the finer cascades. The set is passed in
	// explicitly because a shared model's primary set belongs to its first owner.
	public void Bind(OrderedDictionary<int, IMaterialObject> materials, string slotSuffix = "")
	{
		for (int i = 0; i < materials.Count; i++)
		{
			var material = materials.GetAt(i).Value;
			material.SetTexture($"_ProbeSh0{slotSuffix}", Sh0);
			material.SetTexture($"_ProbeSh1{slotSuffix}", Sh1);
			material.SetTexture($"_ProbeSh2{slotSuffix}", Sh2);
			material.SetTexture($"_ProbeSh3{slotSuffix}", Sh3);
			material.SetTexture($"_ProbeVis{slotSuffix}", Vis);
			material.SetTexture($"_ProbeOffset{slotSuffix}", Offset);
		}
	}

	public void Release()
	{
		Sh0.Release();
		Sh1.Release();
		Sh2.Release();
		Sh3.Release();
		Vis.Release();
		Offset.Release();
	}
}
