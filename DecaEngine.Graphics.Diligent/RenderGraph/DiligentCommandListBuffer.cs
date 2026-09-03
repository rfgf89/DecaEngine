using System.Numerics;
using System.Runtime.CompilerServices;
using DecaEngine.Core;
using DecaEngine.Graphics.Diligent;
using Diligent;
using UnsafeCollections.Collections.Native;
using UnsafeCollections.Collections.Unsafe;
using ValueType = Diligent.ValueType;

namespace DecaEngine.Graphics.Diligent.RenderGraph
{
    /// <summary>
    /// ICommandBuffer recording into a deferred Diligent context; only for multi-threaded
    /// recording (single-threaded passes should use DiligentCommandBuffer). Deferred contexts
    /// forbid TRANSITION mode, so states are flushed explicitly via ResourceStateTracker; a
    /// finished ICommandList is one-shot, and Execute() must run on the immediate-context thread.
    /// </summary>
    public unsafe class DiligentCommandListBuffer : ICommandBuffer
    {
        private const int MaxVertexBuffers = 4;

        private IDeviceContext _deferredContext;
        private readonly IDeviceContext _immediateContext;
        private readonly ResourceStateTracker _stateTracker;

        private readonly ITextureView[] _rtvHelper = new ITextureView[1];
        private readonly Viewport[] _viewportHelper = new Viewport[1];
        private readonly ulong[] _offsetHelper = new ulong[MaxVertexBuffers];
        private readonly IBuffer[] _vbHelper = new IBuffer[MaxVertexBuffers];
        private readonly ICommandList[] _cmdListHelper = new ICommandList[1];

        private bool _isRecording;
        private ICommandList? _pendingCommandList;

        public DiligentCommandListBuffer(IDeviceContext deferredContext, IDeviceContext immediateContext, ResourceStateTracker? tracker = null)
        {
            _deferredContext = deferredContext;
            _immediateContext = immediateContext;
            _stateTracker = tracker ?? new ResourceStateTracker();
        }

        public void BeginRecording()
        {
            // A recorded-but-never-executed list cannot be salvaged; drop it, don't leak it.
            _pendingCommandList?.Dispose();
            _pendingCommandList = null;

            _stateTracker.Clear();
            _deferredContext.Begin(0);
            _isRecording = true;
        }

        /// <summary>Re-points recording at another deferred context; the submit context is fixed.</summary>
        public void Retarget(IDeviceContext deferredContext)
        {
            _deferredContext = deferredContext;
        }

        public void EndRecording()
        {
            if (!_isRecording) return;
            _isRecording = false;

            _pendingCommandList = _deferredContext.FinishCommandList();

            // Bound state does not survive FinishCommandList; reset for the next recording.
            _deferredContext.InvalidateState();
        }

        /// <summary>Not supported: a native ICommandList is one-shot; use DiligentCommandBuffer.Freeze.</summary>
        public void Freeze()
        {
            throw new NotSupportedException(
                "DiligentCommandListBuffer cannot be frozen: a native Diligent ICommandList " +
                "is one-shot (record once, execute once, dispose) and cannot be cached or " +
                "replayed across frames. Use DiligentCommandBuffer if you need freeze/replay " +
                "semantics.");
        }

        public void Execute()
        {
            if (_pendingCommandList == null) return;

            _cmdListHelper[0] = _pendingCommandList;
            _immediateContext.ExecuteCommandLists(_cmdListHelper);
            _cmdListHelper[0] = null;

            _pendingCommandList.Dispose();
            _pendingCommandList = null;

            // Required so the deferred context releases its frame-scoped resources.
            _deferredContext.FinishFrame();
            _stateTracker.ResetTransitions();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void TransitionAndFlush(IDeviceObject? res, global::Diligent.ResourceState state)
        {
            if (res == null) return;
            _stateTracker.AddTransition(res, state);
            _stateTracker.Flush(_deferredContext);
        }

        public void TransitionResource(IBufferHandle buffer, DecaEngine.Graphics.ResourceState newState)
        {
            TransitionAndFlush(((DiligentBufferHandle)buffer).Buffer, newState.ToNative());
        }

        public void TransitionResource(IGpuTexture texture, DecaEngine.Graphics.ResourceState newState)
        {
            TransitionAndFlush(GetNativeTexture(texture), newState.ToNative());
        }

        public void ResolveTexture(IGpuTexture src, IGpuTexture dst)
        {
            var srcTex = GetNativeTexture(src);
            var dstTex = GetNativeTexture(dst);

            TransitionAndFlush(srcTex, global::Diligent.ResourceState.ResolveSource);
            TransitionAndFlush(dstTex, global::Diligent.ResourceState.ResolveDest);

            _deferredContext.ResolveTextureSubresource(srcTex, dstTex, new ResolveTextureSubresourceAttribs
            {
                SrcTextureTransitionMode = ResourceStateTransitionMode.None,
                DstTextureTransitionMode = ResourceStateTransitionMode.None,
            });
        }

        /// <summary>Not supported: native callbacks need the frozen immediate-context buffer.</summary>
        public void Callback(Action callback) =>
            throw new NotSupportedException("Callback commands are only supported by the frozen immediate-context buffer.");

        /// <summary>Not supported: frozen immediate-context replay only; see <see cref="Freeze"/>.</summary>
        public void ExecuteNested(ICommandBuffer nested, ShadowCascadeSchedule schedule, int cascadeIndex) =>
            throw new NotSupportedException("ExecuteNested commands are only supported by the frozen immediate-context buffer.");

        public void CopyTexture(IGpuTexture src, IGpuTexture dst)
        {
            var srcTex = GetNativeTexture(src);
            var dstTex = GetNativeTexture(dst);

            TransitionAndFlush(srcTex, global::Diligent.ResourceState.CopySource);
            TransitionAndFlush(dstTex, global::Diligent.ResourceState.CopyDest);

            _deferredContext.CopyTexture(new CopyTextureAttribs
            {
                SrcTexture = srcTex,
                SrcTextureTransitionMode = ResourceStateTransitionMode.None,
                DstTexture = dstTex,
                DstTextureTransitionMode = ResourceStateTransitionMode.None,
            });
        }

        private static ITexture? GetNativeTexture(IGpuTexture texture)
        {
            if (texture is DiligentGpuTexture dilTex) return dilTex.Texture;
            if (texture is DiligentRenderTarget dilRt) return dilRt.Texture;
            if (texture is DiligentRenderHandle dilHandle) return dilHandle.Texture;
            return null;
        }

        private static ITextureView? GetTextureView(IGpuTexture texture, TextureViewType type, uint slice)
        {
            if (texture is DiligentGpuTexture dilTex) return dilTex.GetView(type, slice);
            if (texture is DiligentRenderTarget dilRt) return dilRt.GetView(type, slice);
            if (texture is DiligentRenderHandle dilHandle) return dilHandle.GetView(type, slice);
            return null;
        }

        public void SetBackBufferTarget(IGraphicsApi api)
        {
            var pipeline = (DiligentGraphicsApi)api;
            var rtv = pipeline.SwapChain.GetCurrentBackBufferRTV();
            var dsv = pipeline.SwapChain.GetDepthBufferDSV();

            _stateTracker.AddTransition(rtv.GetTexture(), global::Diligent.ResourceState.RenderTarget);
            if (dsv != null) _stateTracker.AddTransition(dsv.GetTexture(), global::Diligent.ResourceState.DepthWrite);
            _stateTracker.Flush(_deferredContext);

            _rtvHelper[0] = rtv;
            _deferredContext.SetRenderTargets(_rtvHelper, dsv, ResourceStateTransitionMode.None);
        }

        public void ClearBackBufferTarget(IGraphicsApi api, Vector4 clearColor)
        {
            var pipeline = (DiligentGraphicsApi)api;
            var rtv = pipeline.SwapChain.GetCurrentBackBufferRTV();
            var dsv = pipeline.SwapChain.GetDepthBufferDSV();

            _stateTracker.Flush(_deferredContext);

            _deferredContext.ClearRenderTarget(rtv, clearColor, ResourceStateTransitionMode.None);
            if (dsv != null)
            {
                _deferredContext.ClearDepthStencil(dsv, global::Diligent.ClearDepthStencilFlags.Depth, 0.0f, 0, ResourceStateTransitionMode.None);
            }
        }

        public void SetRenderTarget(IGpuTexture rtv, IGpuTexture dsv, uint rtvSlice = 0, uint dsvSlice = 0)
        {
            var rtvView = GetTextureView(rtv, TextureViewType.RenderTarget, rtvSlice);
            var dsvView = GetTextureView(dsv, TextureViewType.DepthStencil, dsvSlice);

            if (rtv != null) TransitionAndFlush(rtvView?.GetTexture(), global::Diligent.ResourceState.RenderTarget);
            if (dsv != null) TransitionAndFlush(dsvView?.GetTexture(), global::Diligent.ResourceState.DepthWrite);

            ITextureView[]? rtvs = null;
            if (rtvView != null) { _rtvHelper[0] = rtvView; rtvs = _rtvHelper; }
            _deferredContext.SetRenderTargets(rtvs, dsvView, ResourceStateTransitionMode.None);
        }

        public void SetRenderTargets(IGpuTexture[] rtvs, IGpuTexture dsv)
        {
            var views = new ITextureView?[rtvs.Length];
            for (int i = 0; i < rtvs.Length; i++)
            {
                views[i] = GetTextureView(rtvs[i], TextureViewType.RenderTarget, 0);
                TransitionAndFlush(views[i]?.GetTexture(), global::Diligent.ResourceState.RenderTarget);
            }

            var dsvView = GetTextureView(dsv, TextureViewType.DepthStencil, 0);
            if (dsv != null) TransitionAndFlush(dsvView?.GetTexture(), global::Diligent.ResourceState.DepthWrite);
            _deferredContext.SetRenderTargets(views, dsvView, ResourceStateTransitionMode.None);
        }

        public void ClearRenderTarget(IGpuTexture rtv, Vector4 color, uint slice = 0)
        {
            var rtvView = GetTextureView(rtv, TextureViewType.RenderTarget, slice);
            TransitionAndFlush(rtvView?.GetTexture(), global::Diligent.ResourceState.RenderTarget);
            _deferredContext.ClearRenderTarget(rtvView, color, ResourceStateTransitionMode.None);
        }

        public void ClearDepthStencil(IGpuTexture dsv, DecaEngine.Graphics.ClearDepthStencilFlags flags, float depth, byte stencil, uint slice = 0)
        {
            var dsvView = GetTextureView(dsv, TextureViewType.DepthStencil, slice);
            TransitionAndFlush(dsvView?.GetTexture(), global::Diligent.ResourceState.DepthWrite);
            _deferredContext.ClearDepthStencil(dsvView, flags.ToNative(), depth, stencil, ResourceStateTransitionMode.None);
        }

        public void SetVertexBuffers(uint startSlot, IBufferHandle[] bufferHandles, ulong[] offsets, DecaEngine.Graphics.SetVertexBuffersFlags flags = DecaEngine.Graphics.SetVertexBuffersFlags.None)
        {
            int count = Math.Min(bufferHandles.Length, MaxVertexBuffers);
            for (int i = 0; i < count; i++)
            {
                var buf = ((DiligentBufferHandle)bufferHandles[i]).Buffer;
                _vbHelper[i] = buf;
                _offsetHelper[i] = offsets?[i] ?? 0;
                TransitionAndFlush(buf, global::Diligent.ResourceState.VertexBuffer);
            }

            _deferredContext.SetVertexBuffers(startSlot, _vbHelper, _offsetHelper, ResourceStateTransitionMode.None, flags.ToNative());
        }

        public void SetIndexBuffer(IBufferHandle bufferHandle, ulong byteOffset = 0)
        {
            var buffer = ((DiligentBufferHandle)bufferHandle).Buffer;
            TransitionAndFlush(buffer, global::Diligent.ResourceState.IndexBuffer);
            _deferredContext.SetIndexBuffer(buffer, byteOffset, ResourceStateTransitionMode.None);
        }

        public void SetViewport(uint width, uint height)
        {
            _viewportHelper[0] = new Viewport { Width = width, Height = height, MaxDepth = 1f };
            _deferredContext.SetViewports(_viewportHelper, width, height);
        }

        public void SetViewport(Ref<Vector2> size)
        {
            var s = size.Value;
            _viewportHelper[0] = new Viewport { Width = (uint)s.X, Height = (uint)s.Y, MaxDepth = 1f };
            _deferredContext.SetViewports(_viewportHelper, (uint)s.X, (uint)s.Y);
        }

        public void SetPipelineState(IMaterialObject material)
        {
            ((DiligentMaterial)material).SetPipelineState(_deferredContext);
        }

        public void CommitShaderResources(IMaterialObject material)
        {
            ((DiligentMaterial)material).CommitShaderResources(_deferredContext, ResourceStateTransitionMode.None);
        }

        public void SetPipelineState(IComputeMaterial material)
        {
            ((DiligentComputeMaterial)material).SetPipelineState(_deferredContext);
        }

        public void CommitShaderResources(IComputeMaterial material)
        {
            ((DiligentComputeMaterial)material).CommitShaderResources(_deferredContext);
        }

        public void Draw(uint vertexCount, uint startVertex = 0)
        {
            _deferredContext.Draw(new DrawAttribs
            {
                NumVertices = vertexCount,
                StartVertexLocation = startVertex,
                Flags = DrawFlags.VerifyAll
            });
        }

        public void DrawIndexed(uint indicesStart, uint indicesCount, uint vertexStart, uint instanceStart, uint instanceCount, IndexType indexType)
        {
            _deferredContext.DrawIndexed(new DrawIndexedAttribs
            {
                IndexType = (ValueType)(int)indexType,
                BaseVertex = vertexStart,
                FirstIndexLocation = indicesStart,
                FirstInstanceLocation = instanceStart,
                Flags = DrawFlags.VerifyAll,
                NumIndices = indicesCount,
                NumInstances = instanceCount
            });
        }

        public void DrawIndexedIndirect(IBufferHandle args, MaterialDrawRange drawRange, IndexType indexType)
        {
            var buffer = ((DiligentBufferHandle)args).Buffer;
            TransitionAndFlush(buffer, global::Diligent.ResourceState.IndirectArgument);

            var offset = (uint)(drawRange.FirstDrawIndex * (ulong)Unsafe.SizeOf<DrawIndexedIndirectCommand>());
            _deferredContext.DrawIndexedIndirect(new DrawIndexedIndirectAttribs
            {
                IndexType = (ValueType)(uint)indexType,
                Flags = DrawFlags.VerifyAll,
                AttribsBuffer = buffer,
                DrawArgsOffset = offset,
                DrawCount = drawRange.DrawCount
            });
        }

        public void DispatchCompute(uint threadGroupCountX, uint threadGroupCountY = 1, uint threadGroupCountZ = 1)
        {
            _deferredContext.DispatchCompute(new DispatchComputeAttribs
            {
                ThreadGroupCountX = threadGroupCountX,
                ThreadGroupCountY = threadGroupCountY,
                ThreadGroupCountZ = threadGroupCountZ
            });
        }

        public void UpdateBuffer<T>(IBufferHandle buffer, uint offset, T* data) where T : unmanaged
        {
            UpdateBuffer(buffer, offset, (uint)sizeof(T), new IntPtr(data));
        }

        public void UpdateBuffer(IBufferHandle buffer, uint offset, uint size, IntPtr data)
        {
            var res = ((DiligentBufferHandle)buffer).Buffer;
            TransitionAndFlush(res, global::Diligent.ResourceState.CopyDest);
            DiligentGraphicsUtility.LogLargeUpload(res, size, "CommandListBuffer.UpdateBuffer");
            _deferredContext.UpdateBuffer(res, offset, size, data, ResourceStateTransitionMode.None);
        }

        public void UpdateBuffer<T>(IBufferHandle buffer, NativeArray<T> data) where T : unmanaged
        {
            UpdateBuffer<T>(buffer, data.GetNative());
        }

        public void UpdateBuffer<T>(IBufferHandle buffer, UnsafeArray* data) where T : unmanaged
        {
            var size = (uint)(UnsafeArray.GetLength(data) * Unsafe.SizeOf<T>());
            UpdateBuffer(buffer, 0, size, new IntPtr(UnsafeArray.GetPtr<T>(data, 0)));
        }
    }
}



