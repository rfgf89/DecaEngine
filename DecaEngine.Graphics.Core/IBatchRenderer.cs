using DecaEngine.Graphics.Diligent;

namespace DecaEngine.Core;

/// <summary>
/// Opaque handle to the result of a compute-culling dispatch (<see cref="IBatchRenderer.ExecuteComputeCulling"/>).
/// Backend implementations return a concrete type implementing this marker interface; callers should
/// only ever pass it back into <see cref="IBatchRenderer.ExecuteDrawShadows"/> / <see cref="IBatchRenderer.ExecuteDrawBatching"/>.
/// </summary>
public interface ICullResult
{
}

/// <summary>
/// Backend-agnostic surface over the GPU-driven instanced batch renderer. This is what
/// <see cref="IGraphicsPipeline"/> uses to actually cull and draw the scene for every camera
/// and shadow cascade view it is given.
/// </summary>
public interface IBatchRenderer
{
	/// <summary>True when cached indirect draw commands / GPU buffers need to be rebuilt.</summary>
	bool IsDirty { get; }

	/// <summary>Number of shadow-map cascades supported by the shadow renderer.</summary>
	int ShadowCascadeCount { get; }

	/// <summary>(Re)allocates GPU buffers if the number of instances/batches changed since last frame.</summary>
	void CheckAndReallocateBuffers();

	/// <summary>Resets the indirect draw argument buffers/counters before a new culling pass.</summary>
	void ClearIndirectDrawBuffers(ICommandBuffer cmd);

	void SetupViewData(ICommandBuffer cmd, ref ViewData viewData);
	void SetupCullData(ICommandBuffer cmd, ref CullData cullData);
	void SetupLightData(ICommandBuffer cmd, ref LightData lightData);

	/// <summary>Dispatches the GPU culling compute shader for the currently bound view/cull data.</summary>
	ICullResult ExecuteComputeCulling(ICommandBuffer cmd, int cascadeIndex = -1);

	/// <summary>Renders the depth-only shadow map for the given cascade using a previous culling result.</summary>
	void ExecuteDrawShadows(ICommandBuffer cmd, ICullResult cullResult, int cascadeIndex);

	/// <summary>Renders the main color pass using a previous culling result.</summary>
	void ExecuteDrawBatching(ICommandBuffer cmd, ICullResult cullResult);
}

