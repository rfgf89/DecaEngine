using System.Numerics;
using UnsafeCollections.Collections.Native;
using UnsafeCollections.Collections.Unsafe;
using DecaEngine.Core;

namespace DecaEngine.Graphics;

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

	/// <summary>MRT-вариант <see cref="SetRenderTarget"/>: несколько цветовых таргетов разом (нулевые
	/// мипы/слои) - тонкий G-buffer отражений в <see cref="ForwardPass"/> (нормаль/шероховатость +
	/// множитель env-спекуляра для SSR, см. SsrPass). PSO дроу обязан быть создан с тем же списком
	/// форматов - на Vulkan несовпадение числа аттачментов с пайплайном ломает рендер-пасс.</summary>
	void SetRenderTargets(IGpuTexture[] rtvs, IGpuTexture dsv);
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

	/// <summary>Управляемый колбэк ВНУТРИ реплея заморожённого буфера - точка врезки нативных
	/// библиотек (FSR/DLSS), которые пишут в командный лист кадра мимо Diligent (см.
	/// NativeUpscalePass). Исполняется на КАЖДОМ реплее, в позиции записи. Колбэк, трогающий
	/// командный лист напрямую, обязан после себя вернуть контекст в согласованное состояние
	/// (InvalidateState) - это забота самого колбэка.</summary>
	void Callback(Action callback);

	/// <summary>Условно реплеит чужой ЗАМОРОЖЕННЫЙ буфер <paramref name="nested"/> - ТОЛЬКО если
	/// <c>schedule.ShouldRender(cascadeIndex)</c> на момент реплея ЭТОГО буфера (см.
	/// <see cref="ShadowPass"/>/<see cref="ShadowCascadeSchedule"/>). Это ЯВНАЯ команда, а не
	/// колбэк-замыкание над внешним массивом: <paramref name="nested"/>, <paramref name="schedule"/>
	/// и <paramref name="cascadeIndex"/> хранятся как данные внутри записи команды, поэтому
	/// переживают ровно столько же, сколько любая другая команда этого буфера - не дольше. Если
	/// вызывающий пасс не перезапишет эту команду при следующей <c>WriteCommands</c>, она не может
	/// остаться в буфере как-то иначе, чем вместе со всем остальным его содержимым (тот же выбор
	/// заморозить/перезаписать, что у <see cref="SetPipelineState(IMaterialObject)"/> и прочих).</summary>
	void ExecuteNested(ICommandBuffer nested, ShadowCascadeSchedule schedule, int cascadeIndex);

	void BeginRecording();
	void EndRecording();
	void Execute();
	void Freeze();
}