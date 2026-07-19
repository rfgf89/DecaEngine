using System;
using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Graphics.Core;
using Diligent;
using UnsafeCollections.Collections.Native;
using UnsafeCollections.Collections.Unsafe;

namespace DecaEngine.Graphics.Diligent.RenderGraph
{
    public interface ICommandBuffer
    {
        void TransitionResource(ISamplerObject buffer, ResourceState newState);
        void TransitionResource(IBufferHandle buffer, ResourceState newState);
        void TransitionResource(IGpuTexture texture, ResourceState newState);
        void TransitionResource(IGpuTexture texture, ResourceState newState, uint slice);

        void SetRenderTarget(IGpuTexture rtv, IGpuTexture dsv, uint rtvSlice = 0, uint dsvSlice = 0);
        void ClearRenderTarget(IGpuTexture rtv, Vector4 color, uint slice = 0);
        void ClearDepthStencil(IGpuTexture dsv, ClearDepthStencilFlags flags, float depth, byte stencil, uint slice = 0);
        
        void SetVertexBuffers(uint startSlot, IBufferHandle[] buffers, ulong[] offsets, SetVertexBuffersFlags flags = SetVertexBuffersFlags.None);
        void SetIndexBuffer(IBufferHandle buffer, ulong byteOffset = 0);

        void SetViewport(uint width, uint height);

        void SetPipelineState(IMaterialObject material);
        void CommitShaderResources(IMaterialObject material);

        void SetPipelineState(IComputeMaterial material);
        void CommitShaderResources(IComputeMaterial material);
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
}