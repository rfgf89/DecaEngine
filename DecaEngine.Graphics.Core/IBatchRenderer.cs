using System.Collections.Generic;
using DecaEngine.Graphics.Diligent;
using UnsafeCollections.Collections.Unsafe;

namespace DecaEngine.Graphics;

/// <summary>Opaque handle to a compute-culling dispatch result; pass it back into the draw calls.</summary>
public interface ICullResult
{
}

/// <summary>Material filter for draw batching; lets a pass draw opaque, snapshot, then transmissive.</summary>
public enum BatchDrawFilter
{
	All,
	OpaqueOnly,
	TransparentOnly,
}

/// <summary>Backend-agnostic surface over the GPU-driven instanced batch renderer.</summary>
public interface IBatchRenderer
{
	/// <summary>True when cached indirect draw commands / GPU buffers need to be rebuilt.</summary>
	bool IsDirty { get; }

	/// <summary>Instance contents changed (matrices, DrawData) without composition change: re-upload buffers, keep commands.</summary>
	void MarkInstancesContentDirty();

	/// <summary>Pins the live instance subset; the renderer reads its arrays directly on every upload.</summary>
	void PinInstances(BatchSubset subset);

	/// <summary>Number of shadow-map cascades supported by the shadow renderer.</summary>
	int ShadowCascadeCount { get; }

	/// <summary>(Re)allocates GPU buffers if the number of instances/batches changed since last frame.</summary>
	void CheckAndReallocateBuffers();

	/// <summary>Resets the indirect draw argument buffers/counters before a new culling pass.</summary>
	void ClearIndirectDrawBuffers(ICommandBuffer cmd);

	/// <summary>Binds the shared View cbuffer to a material NOT registered as a batch material (fullscreen passes).</summary>
	void BindViewConstants(IMaterialObject material);

	/// <summary>Binds the Light cbuffer and shadow map array to a non-batch material that reads scene shadows.</summary>
	void BindShadowResources(IMaterialObject material);

	/// <summary>Transitions shadow maps to shader-read; needed by passes reading shadows outside geometry
	/// draws, since a disabled draw pass performs no transitions of its own.</summary>
	void TransitionShadowMapsForRead(ICommandBuffer cmd);

	void SetupViewData(ICommandBuffer cmd, ref ViewData viewData);
	void SetupCullData(ICommandBuffer cmd, ref CullData cullData);
	void SetupLightData(ICommandBuffer cmd, ref LightData lightData);

	/// <summary>Uploads the frame's punctual light pool. Frozen command: the raw pointer is re-read on
	/// every replay, so the pool must live in stable unmanaged memory. Call once per frame before the
	/// per-camera <see cref="ExecuteLightClustering"/> dispatches.</summary>
	unsafe void SetupPunctualLights(ICommandBuffer cmd, UnsafeArray* lights);

	/// <summary>Dispatches light clustering (LightClusterCS.hlsl) for the current camera's pool segment.</summary>
	void ExecuteLightClustering(ICommandBuffer cmd);

	/// <summary>Uploads punctual shadow slice viewProj matrices. Frozen command: memory must be stable.</summary>
	unsafe void SetupPunctualShadowMatrices(ICommandBuffer cmd, UnsafeArray* matrices);

	/// <summary>Renders one punctual shadow texture array slice from a slice culling result.</summary>
	void ExecuteDrawPunctualShadow(ICommandBuffer cmd, ICullResult cullResult, int sliceIndex);

	/// <summary>Transitions punctual shadows to shader-read; call ALWAYS, even when no slice was drawn:
	/// the texture is declared unconditionally in PS and the layout must be valid.</summary>
	void TransitionPunctualShadowsForRead(ICommandBuffer cmd);

	/// <summary>Dispatches the GPU culling compute shader for the currently bound view/cull data.</summary>
	ICullResult ExecuteComputeCulling(ICommandBuffer cmd, int cascadeIndex = -1);

	/// <summary>Renders the depth-only shadow map for the given cascade using a previous culling result.</summary>
	void ExecuteDrawShadows(ICommandBuffer cmd, ICullResult cullResult, int cascadeIndex);

	/// <summary>Renders the main color pass using a previous culling result.</summary>
	void ExecuteDrawBatching(ICommandBuffer cmd, ICullResult cullResult);

	/// <summary>Renders only the materials selected by <paramref name="filter"/> - see <see cref="BatchDrawFilter"/>.</summary>
	void ExecuteDrawBatching(ICommandBuffer cmd, ICullResult cullResult, BatchDrawFilter filter);

	/// <summary>Enables alpha-tested shadow writes for cut-out geometry (foliage, grates). Costs a draw
	/// per material per cascade, so only mark genuinely sparse materials: glTF alphaMode=MASK alone is
	/// not enough (exporters mark opaque materials with it; see the average-alpha criterion in ProbeGi).</summary>
	void SetMaterialAlphaTestedShadow(int materialId, DecaEngine.Graphics.ModelLoader.BaseColorBinding baseColor,
		float alphaCutoff);

	/// <summary>Removes a material from shadow casters entirely (raw material id). Needed for BLEND
	/// decal overlays lying millimeters from the surface they decorate: any shadow they cast lands on
	/// that surface and doubles their own pattern. Not for MASK foliage - that shadow is wanted.</summary>
	void SetMaterialShadowCasting(int materialId, bool casts);

	/// <summary>Marks a registered material as transparent/transmissive for <see cref="BatchDrawFilter"/>
	/// purposes (raw material id - <c>MaterialId.materialId</c>). Materials default to opaque.</summary>
	void SetMaterialTransparent(int materialId, bool transparent);

	// Per-model unregistration (DiligentBatchRenderer.UnregisterModel) is not declared here: it uses
	// BatchId/MaterialId/MeshId types from Graphics.Diligent, which Graphics.Core cannot reference.
}
