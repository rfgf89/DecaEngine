using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DecaEngine.Core;
using DecaEngine.Graphics.Core;
using DecaEngine.Graphics.Diligent;
using Diligent;
using UnsafeCollections.Collections.Native;
using UnsafeCollections.Collections.Unsafe;
using ClearDepthStencilFlags = Diligent.ClearDepthStencilFlags;
using ResourceState = Diligent.ResourceState;
using SetVertexBuffersFlags = Diligent.SetVertexBuffersFlags;
using ValueType = Diligent.ValueType;

namespace DecaEngine.Graphics.Diligent.RenderGraph
{
    public unsafe class DiligentCommandBuffer : ICommandBuffer
    {
        private enum CommandType : byte
        {
            None,
            SetBackBufferTarget,
            SetRenderTarget,
            ClearRenderTarget,
            ClearDepthStencil,
            SetVertexBuffers,
            SetIndexBuffer,
            SetViewport,
            SetPipelineState,
            CommitShaderResources,
            SetComputePipelineState,
            CommitComputeShaderResources,
            Draw,
            DrawIndexed,
            DrawIndexedIndirect,
            DispatchCompute,
            TransitionResource,
            UpdateBuffer,
            ClearBackBufferTarget,
            CopyTexture,
            ResolveTexture
        }

        private const int MaxVertexBuffers = 4;

        private struct Command
        {
            public CommandType Type;
            public object Obj1;
            public object Obj2;
            public IntPtr Pt1;
            public uint U1;
            public uint U2;
            public uint U3;
            public ulong L1;
            public float F1;
            public Vector4 Vec;
            public fixed ulong VBOffsets[MaxVertexBuffers];

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Reset()
            {
                Type = CommandType.None;
                Obj1 = null;
                Obj2 = null;
            }
        }

        private IDeviceContext _context;
        private readonly ResourceStateTracker _stateTracker;
        
        private Command[] _commandsArray = new Command[256];
        private int _commandCount = 0;
        private bool _isRecording = true;
        private bool _isFrozen = false;

        private readonly ITextureView[] _rtvHelper = new ITextureView[1];
        private readonly Viewport[] _viewportHelper = new Viewport[1];
        private readonly ulong[] _offsetHelper = new ulong[MaxVertexBuffers];

        private readonly List<IBuffer[]> _vbArrayPool = new();
        private int _vbArrayPoolIdx = 0;

        public DiligentCommandBuffer(IDeviceContext context, ResourceStateTracker? tracker = null)
        {
            _context = context;
            _stateTracker = tracker ?? new ResourceStateTracker();
        }

        public void BeginRecording()
        {
            for (int i = 0; i < _commandCount; i++)
            {
                _commandsArray[i].Reset();
            }

            _commandCount = 0;
            _vbArrayPoolIdx = 0;
            _isRecording = true;
            _isFrozen = false;
            _stateTracker.Clear();
        }

        /// <summary>
        /// Re-points this command buffer at a different device context.
        /// </summary>
        public void Retarget(IDeviceContext context)
        {
            _context = context;
        }

        public void EndRecording() => _isRecording = false;
        public void Freeze() { _isRecording = false; _isFrozen = true; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ref Command NextCommand(CommandType type)
        {
            if (_commandCount >= _commandsArray.Length)
            {
                Array.Resize(ref _commandsArray, _commandsArray.Length * 2);
            }
            ref var cmd = ref _commandsArray[_commandCount++];
            cmd.Type = type;
            return ref cmd;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AddTransitionInternal(IDeviceObject res, ResourceState state, bool explicitTransition = false)
        {
            if (res == null) return;
            
            if (!_isRecording)
            {
                _stateTracker.AddTransition(res, state);
                if (explicitTransition)
                {
                    _stateTracker.Flush(_context);
                }
            }
            else
            {
                if (explicitTransition) return;

                ref var transition = ref NextCommand(CommandType.TransitionResource);
                transition.Obj1 = res;
                transition.U1 = (uint)state;
            }
        }

        public void TransitionResource(IBufferHandle buffer, DecaEngine.Core.ResourceState newState)
        {
            var nativeState = newState.ToNative();
            var res = ((DiligentBufferHandle)buffer).Buffer;
            AddTransitionInternal(res, nativeState, true);
            if (!_isRecording) return;
            ref var cmd = ref NextCommand(CommandType.TransitionResource);
            cmd.Obj1 = res; cmd.U1 = (uint)nativeState;
        }

        public void TransitionResource(IGpuTexture texture, DecaEngine.Core.ResourceState newState)
        {
            var nativeState = newState.ToNative();
            IDeviceObject res = GetNativeTexture(texture);
            AddTransitionInternal(res, nativeState, true);
            if (!_isRecording) return;
            ref var cmd = ref NextCommand(CommandType.TransitionResource);
            cmd.Obj1 = res; cmd.U1 = (uint)nativeState;
        }

        public void CopyTexture(IGpuTexture src, IGpuTexture dst)
        {
            ITexture srcTex = GetNativeTexture(src);
            ITexture dstTex = GetNativeTexture(dst);

            // Не-explicit транзишены: при записи они лягут в буфер ПЕРЕД командой копии и при
            // реплее пройдут через state tracker, как у SetRenderTarget.
            AddTransitionInternal(srcTex, ResourceState.CopySource);
            AddTransitionInternal(dstTex, ResourceState.CopyDest);

            if (!_isRecording) return;
            ref var cmd = ref NextCommand(CommandType.CopyTexture);
            cmd.Obj1 = srcTex; cmd.Obj2 = dstTex;
        }

        public void ResolveTexture(IGpuTexture src, IGpuTexture dst)
        {
            ITexture srcTex = GetNativeTexture(src);
            ITexture dstTex = GetNativeTexture(dst);

            AddTransitionInternal(srcTex, ResourceState.ResolveSource);
            AddTransitionInternal(dstTex, ResourceState.ResolveDest);

            if (!_isRecording) return;
            ref var cmd = ref NextCommand(CommandType.ResolveTexture);
            cmd.Obj1 = srcTex; cmd.Obj2 = dstTex;
        }

        private ITexture GetNativeTexture(IGpuTexture texture)
        {
            if (texture is DiligentGpuTexture dilTex) return dilTex.Texture;
            if (texture is DiligentRenderTarget dilRT) return dilRT.Texture;
            if (texture is DiligentRenderHandle dilHandle) return dilHandle.Texture;
            return null;
        }

        private ITextureView GetTextureView(IGpuTexture texture, TextureViewType type, uint slice)
        {
            if (texture is DiligentGpuTexture dilTex) return dilTex.GetView(type, slice);
            if (texture is DiligentRenderTarget dilRT) return dilRT.GetView(type, slice);
            if (texture is DiligentRenderHandle dilHandle) return dilHandle.GetView(type, slice);
            return null;
        }

        public void SetBackBufferTarget(IGraphicsApi api)
        {
            if (!_isRecording) return;
            ref var cmd = ref NextCommand(CommandType.SetBackBufferTarget);
            cmd.Obj1 = api;
        }

        public void ClearBackBufferTarget(IGraphicsApi api, Vector4 clearColor)
        {
            if (!_isRecording) return;
            ref var cmd = ref NextCommand(CommandType.ClearBackBufferTarget);
            cmd.Obj1 = api;
            cmd.Vec = clearColor;
        }

        public void SetRenderTarget(IGpuTexture rtv, IGpuTexture dsv, uint rtvSlice = 0, uint dsvSlice = 0)
        {
            var rtvView = GetTextureView(rtv, TextureViewType.RenderTarget, rtvSlice);
            var dsvView = GetTextureView(dsv, TextureViewType.DepthStencil, dsvSlice);

            if (rtv != null) AddTransitionInternal(rtvView?.GetTexture(), ResourceState.RenderTarget);
            if (dsv != null) AddTransitionInternal(dsvView?.GetTexture(), ResourceState.DepthWrite);
            if (!_isRecording) return;
            ref var cmd = ref NextCommand(CommandType.SetRenderTarget);
            cmd.Obj1 = rtvView; cmd.Obj2 = dsvView;
        }

        public void ClearRenderTarget(IGpuTexture rtv, Vector4 color, uint slice = 0)
        {
            var rtvView = GetTextureView(rtv, TextureViewType.RenderTarget, slice);
            AddTransitionInternal(rtvView?.GetTexture(), ResourceState.RenderTarget);
            if (!_isRecording) return;
            ref var cmd = ref NextCommand(CommandType.ClearRenderTarget);
            cmd.Obj1 = rtvView; cmd.Vec = color;
        }

        public void ClearDepthStencil(IGpuTexture dsv, DecaEngine.Core.ClearDepthStencilFlags flags, float depth, byte stencil, uint slice = 0)
        {
            var dsvView = GetTextureView(dsv, TextureViewType.DepthStencil, slice);
            AddTransitionInternal(dsvView?.GetTexture(), ResourceState.DepthWrite);
            if (!_isRecording) return;
            ref var cmd = ref NextCommand(CommandType.ClearDepthStencil);
            cmd.Obj1 = dsvView; cmd.U1 = (uint)flags.ToNative(); cmd.F1 = depth; cmd.U2 = stencil;
        }

        public void SetVertexBuffers(uint startSlot, IBufferHandle[] bufferHandles, ulong[] offsets, DecaEngine.Core.SetVertexBuffersFlags flags = DecaEngine.Core.SetVertexBuffersFlags.None)
        {
            IBuffer[] buffersForCmd = null;
            if (_isRecording)
            {
                if (_vbArrayPoolIdx >= _vbArrayPool.Count) _vbArrayPool.Add(new IBuffer[MaxVertexBuffers]);
                buffersForCmd = _vbArrayPool[_vbArrayPoolIdx++];
            }

            int count = Math.Min(bufferHandles.Length, MaxVertexBuffers);
            for (int i = 0; i < count; i++)
            {
                var buf = ((DiligentBufferHandle)bufferHandles[i]).Buffer;
                if (_isRecording) buffersForCmd[i] = buf;
                AddTransitionInternal(buf, ResourceState.VertexBuffer);
            }

            if (!_isRecording) return;
            ref var cmd = ref NextCommand(CommandType.SetVertexBuffers);
            cmd.U1 = startSlot;
            cmd.Obj1 = buffersForCmd;
            cmd.U2 = (uint)flags.ToNative();
            cmd.U3 = (uint)count;
            
            for (int i = 0; i < count; i++)
                cmd.VBOffsets[i] = offsets[i];
        }

        public void SetIndexBuffer(IBufferHandle bufferHandle, ulong byteOffset = 0)
        {
            var buffer = ((DiligentBufferHandle)bufferHandle).Buffer;
            AddTransitionInternal(buffer, ResourceState.IndexBuffer);
            if (!_isRecording) return;
            ref var cmd = ref NextCommand(CommandType.SetIndexBuffer);
            cmd.Obj1 = buffer; cmd.L1 = byteOffset;
        }

        public void SetViewport(uint width, uint height)
        {
            if (!_isRecording) return;
            ref var cmd = ref NextCommand(CommandType.SetViewport);
            cmd.U1 = width;
            cmd.U2 = height;
            cmd.U3 = 0; // Not a pointer
        }

        public void SetViewport(Ref<Vector2> size)
        {
            if (!_isRecording) return;
            ref var cmd = ref NextCommand(CommandType.SetViewport);
            fixed (Vector2* sizePtr = &size.Value)
            {
                cmd.L1 = (ulong)sizePtr;
            }
            cmd.U3 = 1; // It's a pointer
        }

        public void SetPipelineState(IMaterialObject material)
        {
            if (!_isRecording) return;
            ref var cmd = ref NextCommand(CommandType.SetPipelineState);
            cmd.Obj1 = material;
        }

        public void CommitShaderResources(IMaterialObject material)
        {
            if (!_isRecording) return;
            ref var cmd = ref NextCommand(CommandType.CommitShaderResources);
            cmd.Obj1 = material;
        }

        public void SetPipelineState(IComputeMaterial material)
        {
            if (!_isRecording) return;
            ref var cmd = ref NextCommand(CommandType.SetComputePipelineState);
            cmd.Obj1 = material;
        }

        public void CommitShaderResources(IComputeMaterial material)
        {
            if (!_isRecording) return;
            ref var cmd = ref NextCommand(CommandType.CommitComputeShaderResources);
            cmd.Obj1 = material;
        }

        public void Draw(uint vertexCount, uint startVertex = 0)
        {
            if (!_isRecording) return;
            ref var cmd = ref NextCommand(CommandType.Draw);
            cmd.U1 = vertexCount;
            cmd.U2 = startVertex;
        }

        public void DrawIndexed(uint indicesStart, uint indicesCount, uint vertexStart, uint instanceStart, uint instanceCount, IndexType indexType)
        {
            if (!_isRecording) return;
            ref var cmd = ref NextCommand(CommandType.DrawIndexed);
            cmd.U1 = indicesStart;
            cmd.U2 = indicesCount;
            cmd.U3 = vertexStart;
            cmd.L1 = ((ulong)instanceStart << 32) | instanceCount;
            cmd.F1 = (int)indexType;
        }

        public void DrawIndexedIndirect(IBufferHandle args, MaterialDrawRange drawRange, IndexType indexType)
        {
            var buffer = ((DiligentBufferHandle)args).Buffer;
            AddTransitionInternal(buffer, ResourceState.IndirectArgument);
            if (!_isRecording) return;
            ref var cmd = ref NextCommand(CommandType.DrawIndexedIndirect);
            cmd.Obj1 = buffer; 
            cmd.U1 = (uint)indexType;
            cmd.U2 = drawRange.FirstDrawIndex;
            cmd.U3 = drawRange.DrawCount;
            cmd.L1 = (ulong)drawRange.FirstDrawIndex * (ulong)Unsafe.SizeOf<DrawIndexedIndirectCommand>();
        }

        public void DispatchCompute(uint threadGroupCountX, uint threadGroupCountY = 1, uint threadGroupCountZ = 1)
        {
            if (!_isRecording) return;
            ref var cmd = ref NextCommand(CommandType.DispatchCompute);
            cmd.U1 = threadGroupCountX; cmd.U2 = threadGroupCountY; cmd.U3 = threadGroupCountZ;
        }

        public void UpdateBuffer<T>(IBufferHandle buffer, uint offset, T* data) where T : unmanaged
        {
            UpdateBuffer(buffer, offset, (uint)sizeof(T), new IntPtr(data));
        }

        public void UpdateBuffer(IBufferHandle buffer, uint offset, uint size, IntPtr data)
        {
            var res = ((DiligentBufferHandle)buffer).Buffer;
            AddTransitionInternal(res, ResourceState.CopyDest);
            if (!_isRecording)
            {
                DiligentGraphicsUtility.LogLargeUpload(res, size, "CommandBuffer.UpdateBuffer");
                _context.UpdateBuffer(res, offset, size, data, ResourceStateTransitionMode.None);
                return;
            }

            ref var cmd = ref NextCommand(CommandType.UpdateBuffer);
            cmd.Obj1 = res;
            cmd.Pt1 = data;
            cmd.U1 = offset;
            cmd.U2 = size;
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

        public void Execute()
        {
            if (_commandCount == 0) return;

            // A frozen buffer may share resources with other passes that changed their
            // states since the previous frame. Start from Unknown and let Diligent use
            // the resource's tracked native state for every replay.
            _stateTracker.Clear();

            var cmdArray = _commandsArray;
            for (int i = 0; i < _commandCount; i++)
            {
                ref var cmd = ref cmdArray[i];
                switch (cmd.Type)
                {
                    case CommandType.ClearBackBufferTarget:
                        {
                            var pipeline = (DiligentGraphicsApi)cmd.Obj1;
                            var rtv = pipeline.SwapChain.GetCurrentBackBufferRTV();
                            var dsv = pipeline.SwapChain.GetDepthBufferDSV();

                            _stateTracker.Flush(_context);

                            _context.ClearRenderTarget(rtv, cmd.Vec, ResourceStateTransitionMode.None);
                            if (dsv != null)
                            {
                                _context.ClearDepthStencil(
                                    dsv,
                                    ClearDepthStencilFlags.Depth,
                                    0.0f,
                                    0,
                                    ResourceStateTransitionMode.None);
                            }
                        }
                        break;
                    case CommandType.SetBackBufferTarget:
                        {
                            var pipeline = (DiligentGraphicsApi)cmd.Obj1;
                            var rtv = pipeline.SwapChain.GetCurrentBackBufferRTV();
                            var dsv = pipeline.SwapChain.GetDepthBufferDSV();

                            _stateTracker.AddTransition(rtv.GetTexture(), ResourceState.RenderTarget);
                            if (dsv != null)
                            {
                                _stateTracker.AddTransition(dsv.GetTexture(), ResourceState.DepthWrite);
                            }
                            _stateTracker.Flush(_context);

                            _rtvHelper[0] = rtv;
                            _context.SetRenderTargets(_rtvHelper, dsv, ResourceStateTransitionMode.None);
                        }
                        break;
                    case CommandType.TransitionResource:
                        // Transitions from explicit TransitionResource commands in the buffer
                        _stateTracker.AddTransition((IDeviceObject)cmd.Obj1, (ResourceState)cmd.U1);
                        _stateTracker.Flush(_context);
                        break;
                    case CommandType.SetRenderTarget:
                        {
                            ITextureView rtv = (ITextureView)cmd.Obj1;
                            ITextureView[] rtvs = null;
                            if (rtv != null) { _rtvHelper[0] = rtv; rtvs = _rtvHelper; }
                            _context.SetRenderTargets(rtvs, (ITextureView)cmd.Obj2, ResourceStateTransitionMode.None);
                        }
                        break;
                    case CommandType.ClearRenderTarget:
                        _context.ClearRenderTarget((ITextureView)cmd.Obj1, cmd.Vec, ResourceStateTransitionMode.None);
                        break;
                    case CommandType.CopyTexture:
                        _context.CopyTexture(new CopyTextureAttribs
                        {
                            SrcTexture = (ITexture)cmd.Obj1,
                            SrcTextureTransitionMode = ResourceStateTransitionMode.None,
                            DstTexture = (ITexture)cmd.Obj2,
                            DstTextureTransitionMode = ResourceStateTransitionMode.None,
                        });
                        break;
                    case CommandType.ResolveTexture:
                        _context.ResolveTextureSubresource((ITexture)cmd.Obj1, (ITexture)cmd.Obj2,
                            new ResolveTextureSubresourceAttribs
                            {
                                SrcTextureTransitionMode = ResourceStateTransitionMode.None,
                                DstTextureTransitionMode = ResourceStateTransitionMode.None,
                            });
                        break;
                    case CommandType.ClearDepthStencil:
                        _context.ClearDepthStencil((ITextureView)cmd.Obj1, (ClearDepthStencilFlags)cmd.U1, cmd.F1, (byte)cmd.U2, ResourceStateTransitionMode.None);
                        break;
                    case CommandType.SetVertexBuffers:
                        {
                            uint count = cmd.U3;
                            for (int v = 0; v < count; v++) _offsetHelper[v] = cmd.VBOffsets[v];
                            _context.SetVertexBuffers(cmd.U1, (IBuffer[])cmd.Obj1, _offsetHelper, ResourceStateTransitionMode.None, (SetVertexBuffersFlags)cmd.U2);
                        }
                        break;
                    case CommandType.SetIndexBuffer:
                        _context.SetIndexBuffer((IBuffer)cmd.Obj1, cmd.L1, ResourceStateTransitionMode.None);
                        break;
                    case CommandType.SetViewport:
                        if (cmd.U3 == 1) // It's a pointer
                        {
                            var sizePtr = (Vector2*)cmd.L1;
                            _viewportHelper[0] = new Viewport { Width = (uint)sizePtr->X, Height = (uint)sizePtr->Y, MaxDepth = 1f};
                            _context.SetViewports(_viewportHelper, (uint)sizePtr->X, (uint)sizePtr->Y);
                        }
                        else
                        {
                            _viewportHelper[0] = new Viewport { Width = cmd.U1, Height = cmd.U2, MaxDepth = 1f};
                            _context.SetViewports(_viewportHelper, cmd.U1, cmd.U2);
                        }
                        break;
                    case CommandType.SetPipelineState:
                        ((DiligentMaterial)cmd.Obj1).SetPipelineState(_context);
                        break;
                    case CommandType.CommitShaderResources:
                        ((DiligentMaterial)cmd.Obj1).CommitShaderResources(_context, ResourceStateTransitionMode.None);
                        break;
                    case CommandType.SetComputePipelineState:
                        ((DiligentComputeMaterial)cmd.Obj1).SetPipelineState(_context);
                        break;
                    case CommandType.CommitComputeShaderResources:
                        ((DiligentComputeMaterial)cmd.Obj1).CommitShaderResources(_context);
                        break;
                    case CommandType.Draw:
                        _context.Draw(new DrawAttribs
                        {
                            NumVertices = cmd.U1,
                            StartVertexLocation = cmd.U2,
                            Flags = DrawFlags.VerifyAll
                        });
                        break;
                    case CommandType.DrawIndexed:
                        _context.DrawIndexed(new DrawIndexedAttribs
                        {
                            IndexType = (ValueType)(int)cmd.F1,
                            BaseVertex = cmd.U3,
                            FirstIndexLocation = cmd.U1,
                            FirstInstanceLocation = (uint)(cmd.L1 >> 32),
                            Flags = DrawFlags.VerifyAll,
                            NumIndices = cmd.U2,
                            NumInstances = (uint)cmd.L1
                        });
                        break;
                    case CommandType.DrawIndexedIndirect:
                        _context.DrawIndexedIndirect(new DrawIndexedIndirectAttribs
                        {
                            IndexType = (ValueType)cmd.U1,
                            Flags = DrawFlags.VerifyAll,
                            AttribsBuffer = (IBuffer)cmd.Obj1,
                            DrawArgsOffset = (uint)cmd.L1,
                            DrawCount = cmd.U3
                        });
                        break;
                    case CommandType.DispatchCompute:
                        _context.DispatchCompute(new DispatchComputeAttribs { ThreadGroupCountX = cmd.U1, ThreadGroupCountY = cmd.U2, ThreadGroupCountZ = cmd.U3 });
                        break;
                    case CommandType.UpdateBuffer:
                        {
                            DiligentGraphicsUtility.LogLargeUpload((IBuffer)cmd.Obj1, cmd.U2, "CommandBuffer.Replay");
                            _context.UpdateBuffer(
                                (IBuffer)cmd.Obj1,
                                cmd.U1,
                                cmd.U2,
                                cmd.Pt1,
                                ResourceStateTransitionMode.None);
                        }
                        break;
                }
                if (!_isFrozen) cmd.Reset();
            }

            if (!_isFrozen) _commandCount = 0;
            _stateTracker.ResetTransitions();
        }

#if DEBUG
        /// <summary>
        /// Debug-only summary of the recorded (and typically frozen/reused) command list: draw call
        /// count, compute dispatch count, resource transition count and an approximate triangle
        /// count. Cheap: scans the already-recorded command array once, no GPU readback involved.
        /// Indirect draw triangle counts cannot be known on the CPU (the count lives in a GPU buffer),
        /// so they are not included.
        /// </summary>
        public (int drawCalls, int dispatchCalls, int transitionCount, long triangles) GetDebugStats()
        {
            int draws = 0, dispatches = 0, transitions = 0;
            long triangles = 0;

            for (int i = 0; i < _commandCount; i++)
            {
                ref readonly var cmd = ref _commandsArray[i];
                switch (cmd.Type)
                {
                    case CommandType.Draw:
                        draws++;
                        triangles += cmd.U1 / 3;
                        break;
                    case CommandType.DrawIndexed:
                        draws++;
                        triangles += cmd.U2 / 3;
                        break;
                    case CommandType.DrawIndexedIndirect:
                        draws++;
                        // Actual index/instance counts live in a GPU buffer, unknown on the CPU.
                        break;
                    case CommandType.DispatchCompute:
                        dispatches++;
                        break;
                    case CommandType.TransitionResource:
                        transitions++;
                        break;
                }
            }

            return (draws, dispatches, transitions, triangles);
        }
#endif
    }
}