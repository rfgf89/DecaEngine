using System.Numerics;
using System.Runtime.CompilerServices;
using DecaEngine.Core;
using DecaEngine.Graphics.Core;
using Diligent;
using UnsafeCollections.Collections.Native;
using UnsafeCollections.Collections.Unsafe;
using ValueType = Diligent.ValueType;

namespace DecaEngine.Graphics.Diligent.RenderGraph
{
    /// <summary>
    /// ICommandBuffer implementation that records directly into a native Diligent
    /// *deferred* IDeviceContext instead of a managed command-struct array
    /// (compare with <see cref="DiligentCommandBuffer"/>), then submits the
    /// resulting native <see cref="ICommandList"/> on <see cref="Execute"/>.
    ///
    /// Use this instead of <see cref="DiligentCommandBuffer"/> ONLY when you are
    /// actually recording from multiple threads into separate deferred contexts
    /// (e.g. the pattern in MultiThreadSample.cs). For the render graph's
    /// single-threaded, ImmediateContext-based passes, DiligentCommandBuffer is
    /// already the right tool - it doesn't need a native command list at all.
    ///
    /// IMPORTANT Diligent constraints this type must respect:
    ///  - Deferred contexts must not use RESOURCE_STATE_TRANSITION_MODE_TRANSITION;
    ///    all state transitions are issued explicitly via TransitionResourceStates
    ///    through the same ResourceStateTracker DiligentCommandBuffer uses, just
    ///    flushed immediately since there is no later "replay" step here.
    ///  - A native ICommandList is a ONE-SHOT object (record once -> execute once
    ///    -> dispose). Diligent's own samples always Dispose() it right after
    ///    ExecuteCommandLists and call InvalidateState()/FinishFrame() on the
    ///    deferred context afterwards. Holding on to a finished list across a
    ///    further Begin() on the same deferred context, or executing it more than
    ///    once, is undefined behavior in the native API.
    ///    => Freeze() is intentionally NOT supported here; it throws. If you need
    ///       "record once, replay every frame" semantics, use
    ///       DiligentCommandBuffer.Freeze() instead (it replays on a real
    ///       IDeviceContext each frame from a managed command list, which IS safe).
    ///  - Recording (BeginRecording..EndRecording) may happen on a worker thread
    ///    that owns the deferred context. Execute() calls ExecuteCommandLists on
    ///    the immediate context and MUST be invoked from whichever thread drives
    ///    that immediate context (mirrors MultiThreadSample.cs: workers finish
    ///    their command lists, the main thread executes+disposes them all).
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
            // A previous list that was recorded but never Execute()'d cannot be
            // salvaged (see class docs) - drop it rather than leaking the native handle.
            _pendingCommandList?.Dispose();
            _pendingCommandList = null;

            _stateTracker.Clear();
            _deferredContext.Begin(0);
            _isRecording = true;
        }

        /// <summary>
        /// Re-points this buffer's *recording* target at a different deferred
        /// context. The immediate context used for submission in Execute() is
        /// fixed at construction and does not change.
        /// </summary>
        public void Retarget(IDeviceContext deferredContext)
        {
            _deferredContext = deferredContext;
        }

        public void EndRecording()
        {
            if (!_isRecording) return;
            _isRecording = false;

            _pendingCommandList = _deferredContext.FinishCommandList();

            // Bound PSO/RTVs/etc. do not carry over across FinishCommandList; reset
            // so the next BeginRecording() on this context starts from a clean slate.
            _deferredContext.InvalidateState();
        }

        /// <summary>
        /// NOT SUPPORTED. See class remarks: a finished Diligent ICommandList is a
        /// one-shot object and cannot be cached/replayed across frames the way
        /// DiligentCommandBuffer's managed command array can.
        /// </summary>
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

            // Required after FinishCommandList/ExecuteCommandLists so the deferred
            // context can release its frame-scoped resources (dynamic allocations,
            // etc.) - mirrors the pattern used in MultiThreadSample.cs.
            _deferredContext.FinishFrame();
            _stateTracker.ResetTransitions();
        }

        // ---- Resource transitions --------------------------------------------------

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void TransitionAndFlush(IDeviceObject? res, ResourceState state)
        {
            if (res == null) return;
            _stateTracker.AddTransition(res, state);
            _stateTracker.Flush(_deferredContext);
        }

        public void TransitionResource(ISamplerObject buffer, ResourceState newState)
        {
            TransitionAndFlush(((DiligentSamplerObject)buffer).Sampler, newState);
        }

        public void TransitionResource(IBufferHandle buffer, ResourceState newState)
        {
            TransitionAndFlush(((DiligentBufferHandle)buffer).Buffer, newState);
        }

        public void TransitionResource(IGpuTexture texture, ResourceState newState)
        {
            TransitionAndFlush(GetNativeTexture(texture), newState);
        }

        public void TransitionResource(IGpuTexture texture, ResourceState newState, uint slice)
        {
            TransitionAndFlush(GetTextureView(texture, TextureViewType.ShaderResource, slice), newState);
        }

        private static ITexture? GetNativeTexture(IGpuTexture texture)
        {
            if (texture is DiligentGpuTexture dilTex) return dilTex.Texture;
            if (texture is DiligentRenderTarget dilRt) return dilRt.Texture;
            return null;
        }

        private static ITextureView? GetTextureView(IGpuTexture texture, TextureViewType type, uint slice)
        {
            if (texture is DiligentGpuTexture dilTex) return dilTex.GetView(type, slice);
            if (texture is DiligentRenderTarget dilRt) return dilRt.GetView(type, slice);
            return null;
        }

        // ---- Render targets ---------------------------------------------------------

        public void SetBackBufferTarget(IGraphicsApi api)
        {
            var pipeline = (DiligentGraphicsApi)api;
            var rtv = pipeline.SwapChain.GetCurrentBackBufferRTV();
            var dsv = pipeline.SwapChain.GetDepthBufferDSV();

            _stateTracker.AddTransition(rtv.GetTexture(), ResourceState.RenderTarget);
            if (dsv != null) _stateTracker.AddTransition(dsv.GetTexture(), ResourceState.DepthWrite);
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
                _deferredContext.ClearDepthStencil(dsv, ClearDepthStencilFlags.Depth, 0.0f, 0, ResourceStateTransitionMode.None);
            }
        }

        public void SetRenderTarget(IGpuTexture rtv, IGpuTexture dsv, uint rtvSlice = 0, uint dsvSlice = 0)
        {
            var rtvView = GetTextureView(rtv, TextureViewType.RenderTarget, rtvSlice);
            var dsvView = GetTextureView(dsv, TextureViewType.DepthStencil, dsvSlice);

            if (rtv != null) TransitionAndFlush(rtvView?.GetTexture(), ResourceState.RenderTarget);
            if (dsv != null) TransitionAndFlush(dsvView?.GetTexture(), ResourceState.DepthWrite);

            ITextureView[]? rtvs = null;
            if (rtvView != null) { _rtvHelper[0] = rtvView; rtvs = _rtvHelper; }
            _deferredContext.SetRenderTargets(rtvs, dsvView, ResourceStateTransitionMode.None);
        }

        public void ClearRenderTarget(IGpuTexture rtv, Vector4 color, uint slice = 0)
        {
            var rtvView = GetTextureView(rtv, TextureViewType.RenderTarget, slice);
            TransitionAndFlush(rtvView?.GetTexture(), ResourceState.RenderTarget);
            _deferredContext.ClearRenderTarget(rtvView, color, ResourceStateTransitionMode.None);
        }

        public void ClearDepthStencil(IGpuTexture dsv, ClearDepthStencilFlags flags, float depth, byte stencil, uint slice = 0)
        {
            var dsvView = GetTextureView(dsv, TextureViewType.DepthStencil, slice);
            TransitionAndFlush(dsvView?.GetTexture(), ResourceState.DepthWrite);
            _deferredContext.ClearDepthStencil(dsvView, flags, depth, stencil, ResourceStateTransitionMode.None);
        }

        public void ClearRenderTarget(ITextureView rtv, Vector4 color)
        {
            TransitionAndFlush(rtv?.GetTexture(), ResourceState.RenderTarget);
            _deferredContext.ClearRenderTarget(rtv, color, ResourceStateTransitionMode.None);
        }

        public void ClearDepthStencil(ITextureView dsv, ClearDepthStencilFlags flags, float depth, byte stencil)
        {
            TransitionAndFlush(dsv?.GetTexture(), ResourceState.DepthWrite);
            _deferredContext.ClearDepthStencil(dsv, flags, depth, stencil, ResourceStateTransitionMode.None);
        }

        // ---- Vertex/index buffers ----------------------------------------------------

        public void SetVertexBuffers(uint startSlot, IBufferHandle[] bufferHandles, ulong[] offsets, SetVertexBuffersFlags flags = SetVertexBuffersFlags.None)
        {
            int count = Math.Min(bufferHandles.Length, MaxVertexBuffers);
            for (int i = 0; i < count; i++)
            {
                var buf = ((DiligentBufferHandle)bufferHandles[i]).Buffer;
                _vbHelper[i] = buf;
                _offsetHelper[i] = offsets?[i] ?? 0;
                TransitionAndFlush(buf, ResourceState.VertexBuffer);
            }

            _deferredContext.SetVertexBuffers(startSlot, _vbHelper, _offsetHelper, ResourceStateTransitionMode.None, flags);
        }

        public void SetIndexBuffer(IBufferHandle bufferHandle, ulong byteOffset = 0)
        {
            var buffer = ((DiligentBufferHandle)bufferHandle).Buffer;
            TransitionAndFlush(buffer, ResourceState.IndexBuffer);
            _deferredContext.SetIndexBuffer(buffer, byteOffset, ResourceStateTransitionMode.None);
        }

        // ---- Viewport ------------------------------------------------------------------

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

        // ---- Pipeline / shader resources -----------------------------------------------

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

        // ---- Draw / dispatch -------------------------------------------------------------

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
            TransitionAndFlush(buffer, ResourceState.IndirectArgument);

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

        // ---- Buffer updates ------------------------------------------------------------

        public void UpdateBuffer<T>(IBufferHandle buffer, uint offset, T* data) where T : unmanaged
        {
            UpdateBuffer(buffer, offset, (uint)sizeof(T), new IntPtr(data));
        }

        public void UpdateBuffer(IBufferHandle buffer, uint offset, uint size, IntPtr data)
        {
            var res = ((DiligentBufferHandle)buffer).Buffer;
            TransitionAndFlush(res, ResourceState.CopyDest);
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



