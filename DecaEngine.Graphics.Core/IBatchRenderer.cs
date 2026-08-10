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

/// <summary>Which materials <see cref="IBatchRenderer.ExecuteDrawBatching(ICommandBuffer, ICullResult, BatchDrawFilter)"/>
/// draws - the split lets a pass render opaque geometry, snapshot the color target, then render
/// transmissive materials that sample the snapshot for refraction (see <see cref="ForwardPass"/>).</summary>
public enum BatchDrawFilter
{
	All,
	OpaqueOnly,
	TransparentOnly,
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

	/// <summary>Привязывает общий View-кбуфер (обновляемый SetupViewData) к материалу, который НЕ
	/// регистрируется как батч-материал - например, фуллскрин-скай/SSAO материалы (см. ForwardPass,
	/// SsaoPass): им нужна камера, но Register() тянет за собой лишние ресурсы (инстанс-буферы, тени).</summary>
	void BindViewConstants(IMaterialObject material);

	void SetupViewData(ICommandBuffer cmd, ref ViewData viewData);
	void SetupCullData(ICommandBuffer cmd, ref CullData cullData);
	void SetupLightData(ICommandBuffer cmd, ref LightData lightData);

	/// <summary>Dispatches the GPU culling compute shader for the currently bound view/cull data.</summary>
	ICullResult ExecuteComputeCulling(ICommandBuffer cmd, int cascadeIndex = -1);

	/// <summary>Renders the depth-only shadow map for the given cascade using a previous culling result.</summary>
	void ExecuteDrawShadows(ICommandBuffer cmd, ICullResult cullResult, int cascadeIndex);

	/// <summary>Renders the main color pass using a previous culling result.</summary>
	void ExecuteDrawBatching(ICommandBuffer cmd, ICullResult cullResult);

	/// <summary>Renders only the materials selected by <paramref name="filter"/> - see <see cref="BatchDrawFilter"/>.</summary>
	void ExecuteDrawBatching(ICommandBuffer cmd, ICullResult cullResult, BatchDrawFilter filter);

	/// <summary>Marks a registered material as transparent/transmissive for <see cref="BatchDrawFilter"/>
	/// purposes (raw material id - <c>MaterialId.materialId</c>). Materials default to opaque.</summary>
	void SetMaterialTransparent(int materialId, bool transparent);
}

