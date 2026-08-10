using System.Numerics;
using DecaEngine.Graphics.Core;
using UnsafeCollections.Collections.Native;
using UnsafeCollections.Collections.Unsafe;

namespace DecaEngine.Core;

public interface ICommandBuffer
{
	void TransitionResource(IBufferHandle buffer, ResourceState newState);
	void TransitionResource(IGpuTexture texture, ResourceState newState);

	/// <summary>Полная копия текстуры (все мипы/слои, форматы должны совпадать) - например, снятие
	/// сэмплируемой копии color-таргета между opaque- и transmissive-дроу для рефракции (см.
	/// <see cref="ForwardPass"/>). Переходы состояний src→CopySource / dst→CopyDest берёт на себя.</summary>
	void CopyTexture(IGpuTexture src, IGpuTexture dst);

	/// <summary>Резолв MSAA-таргета в одиночный (усреднение сэмплов) - завершение MSAA-кадра
	/// превью (см. <see cref="ForwardPass"/>). Форматы должны совпадать; переходы состояний
	/// берёт на себя.</summary>
	void ResolveTexture(IGpuTexture src, IGpuTexture dst);

	void SetBackBufferTarget(IGraphicsApi api);
	void SetRenderTarget(IGpuTexture rtv, IGpuTexture dsv, uint rtvSlice = 0, uint dsvSlice = 0);
	void ClearRenderTarget(IGpuTexture rtv, Vector4 color, uint slice = 0);
	void ClearDepthStencil(IGpuTexture dsv, ClearDepthStencilFlags flags, float depth, byte stencil, uint slice = 0);

	void ClearBackBufferTarget(IGraphicsApi api, Vector4 clearColor);

	void SetVertexBuffers(uint startSlot, IBufferHandle[] buffers, ulong[] offsets, SetVertexBuffersFlags flags = SetVertexBuffersFlags.None);
	void SetIndexBuffer(IBufferHandle buffer, ulong byteOffset = 0);

	void SetViewport(uint width, uint height);
	void SetViewport(Ref<Vector2> size);

	void SetPipelineState(IMaterialObject material);
	void CommitShaderResources(IMaterialObject material);

	void SetPipelineState(IComputeMaterial material);
	void CommitShaderResources(IComputeMaterial material);
	void Draw(uint vertexCount, uint startVertex = 0);
	void DrawIndexed(uint indicesStart, uint indicesCount, uint vertexStart, uint instanceStart, uint instanceCount, IndexType indexType);
	void DrawIndexedIndirect(IBufferHandle args, MaterialDrawRange drawRange, IndexType indexType);
	void DispatchCompute(uint threadGroupCountX, uint threadGroupCountY = 1, uint threadGroupCountZ = 1);

	unsafe void UpdateBuffer<T>(IBufferHandle buffer, uint offset, T* data) where T : unmanaged;
	void UpdateBuffer(IBufferHandle buffer, uint offset, uint size, IntPtr data);
	void UpdateBuffer<T>(IBufferHandle buffer, NativeArray<T> data) where T : unmanaged;
	unsafe void UpdateBuffer<T>(IBufferHandle buffer, UnsafeArray* data) where T : unmanaged;

	void BeginRecording();
	void EndRecording();
	void Execute();
	void Freeze();
}