using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DecaEngine.Core;
using DecaEngine.Graphics.Core;
using Diligent;
using UnsafeCollections.Collections.Native;
using UnsafeCollections.Collections.Unsafe;
using ValueType = Diligent.ValueType;

namespace DecaEngine.Graphics.Diligent.RenderGraph
{
    public class DiligentCommandBuffer : ICommandBuffer
    {
        private enum CommandType : byte
        {
            None,
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
            DrawIndexedIndirect,
            DispatchCompute,
            TransitionResource,
            UpdateBuffer
        }

        private const int MaxVertexBuffers = 4;

        private unsafe struct Command
        {
            public CommandType Type;
            public object Obj1;
            public object Obj2;
            public uint U1;
            public uint U2;
            public uint U3;
            public ulong L1;
            public float F1;
            public Vector4 Vec;
            public fixed ulong VBOffsets[MaxVertexBuffers];
            public IntPtr Data;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Reset()
            {
                Type = CommandType.None;
                Obj1 = null;
                Obj2 = null;
                Data = IntPtr.Zero;
            }
        }

        private readonly IDeviceContext _context;
        private readonly ResourceStateTracker _stateTracker;
        
        private Command[] _commandsArray = new Command[256];
        private readonly List<StateTransitionDesc> _recordedTransitions = new(64);
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
            _commandCount = 0;
            _recordedTransitions.Clear();
            _vbArrayPoolIdx = 0;
            _isRecording = true;
            _isFrozen = false;
            // Clear state cache to pick up external changes (like UpdateBuffer) via GetState()
            _stateTracker.Clear();
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

                var count = _recordedTransitions.Count;
                for (int i = count - 1; i >= 0 && i >= count - 4; i--)
                {
                    var last = _recordedTransitions[i];
                    if (ReferenceEquals(last.Resource, res))
                    {
                        if (last.NewState == state && state != ResourceState.UnorderedAccess) return;
                        break;
                    }
                }

                _recordedTransitions.Add(new StateTransitionDesc { Resource = res, NewState = state });
            }
        }

        public void TransitionResource(ISamplerObject buffer, ResourceState newState)
        {
            var res = ((DiligentSamplerObject)buffer).Sampler;
            AddTransitionInternal(res, newState, true);
            if (!_isRecording) return;
            ref var cmd = ref NextCommand(CommandType.TransitionResource);
            cmd.Obj1 = res;
            cmd.U1 = (uint)newState;
        }

        public void TransitionResource(IBufferHandle buffer, ResourceState newState)
        {
            var res = ((DiligentBufferHandle)buffer).Buffer;
            AddTransitionInternal(res, newState, true);
            if (!_isRecording) return;
            ref var cmd = ref NextCommand(CommandType.TransitionResource);
            cmd.Obj1 = res; cmd.U1 = (uint)newState;
        }

        public void TransitionResource(IGpuTexture texture, ResourceState newState)
        {
            IDeviceObject res = GetNativeTexture(texture);
            AddTransitionInternal(res, newState, true);
            if (!_isRecording) return;
            ref var cmd = ref NextCommand(CommandType.TransitionResource);
            cmd.Obj1 = res; cmd.U1 = (uint)newState;
        }
        
        public void TransitionResource(IGpuTexture texture, ResourceState newState, uint slice)
        {
            var view = GetTextureView(texture, TextureViewType.ShaderResource, slice);
            AddTransitionInternal(view, newState, true);
            if (!_isRecording) return;
            ref var cmd = ref NextCommand(CommandType.TransitionResource);
            cmd.Obj1 = view; cmd.U1 = (uint)newState;
        }

        private ITexture GetNativeTexture(IGpuTexture texture)
        {
            if (texture is DiligentGpuTexture dilTex) return dilTex.Texture;
            if (texture is DiligentRenderTarget dilRT) return dilRT.Texture;
            return null;
        }

        private ITextureView GetTextureView(IGpuTexture texture, TextureViewType type, uint slice)
        {
            if (texture is DiligentGpuTexture dilTex) return dilTex.GetView(type, slice);
            if (texture is DiligentRenderTarget dilRT) return dilRT.GetView(type, slice);
            return null;
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

        public void ClearDepthStencil(IGpuTexture dsv, ClearDepthStencilFlags flags, float depth, byte stencil, uint slice = 0)
        {
            var dsvView = GetTextureView(dsv, TextureViewType.DepthStencil, slice);
            AddTransitionInternal(dsvView?.GetTexture(), ResourceState.DepthWrite);
            if (!_isRecording) return;
            ref var cmd = ref NextCommand(CommandType.ClearDepthStencil);
            cmd.Obj1 = dsvView; cmd.U1 = (uint)flags; cmd.F1 = depth; cmd.U2 = stencil;
        }

        public unsafe void SetVertexBuffers(uint startSlot, IBufferHandle[] bufferHandles, ulong[] offsets, SetVertexBuffersFlags flags = SetVertexBuffersFlags.None)
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
            cmd.U2 = (uint)flags;
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
            cmd.U1 = width; cmd.U2 = height;
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

        public unsafe void UpdateBuffer<T>(IBufferHandle buffer, uint offset, T* data) where T : unmanaged
        {
            UpdateBuffer(buffer, offset, (uint)sizeof(T), new IntPtr(data));
        }

        public unsafe void UpdateBuffer(IBufferHandle buffer, uint offset, uint size, IntPtr data)
        {
            var res = ((DiligentBufferHandle)buffer).Buffer;
            AddTransitionInternal(res, ResourceState.CopyDest);
            if (!_isRecording)
            {
                _context.UpdateBuffer(res, offset, size, data, ResourceStateTransitionMode.None);
                return;
            }

            ref var cmd = ref NextCommand(CommandType.UpdateBuffer);
            cmd.Obj1 = res;
            cmd.U1 = offset;
            cmd.U2 = size;
            cmd.Data = data;
        }

        public unsafe void UpdateBuffer<T>(IBufferHandle buffer, NativeArray<T> data) where T : unmanaged
        {
            UpdateBuffer<T>(buffer, data.GetNative());
        }

        public unsafe void UpdateBuffer<T>(IBufferHandle buffer, UnsafeArray* data) where T : unmanaged
        {
            var size = (uint)(UnsafeArray.GetLength(data) * Unsafe.SizeOf<T>());
            UpdateBuffer(buffer, 0, size, new IntPtr(UnsafeArray.GetPtr<T>(data, 0)));
        }

        public unsafe void Execute()
        {
            if (_commandCount == 0) return;

            // 1. Apply transitions recorded during Begin...End
            if (!_isRecording || _isFrozen)
            {
                var transSpan = CollectionsMarshal.AsSpan(_recordedTransitions);
                for (int i = 0; i < transSpan.Length; i++)
                {
                    ref var t = ref transSpan[i];
                    _stateTracker.AddTransition(t.Resource, t.NewState);
                }
            }
            
            _stateTracker.Flush(_context);

            var cmdArray = _commandsArray;
            for (int i = 0; i < _commandCount; i++)
            {
                ref var cmd = ref cmdArray[i];
                switch (cmd.Type)
                {
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
                        _viewportHelper[0] = new Viewport { Width = cmd.U1, Height = cmd.U2, MaxDepth = 1f};
                        _context.SetViewports(_viewportHelper, cmd.U1, cmd.U2);
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
                        _context.UpdateBuffer((IBuffer)cmd.Obj1, cmd.U1, cmd.U2, cmd.Data, ResourceStateTransitionMode.None);
                        break;
                }
                if (!_isFrozen) cmd.Reset();
            }

            if (!_isFrozen) _commandCount = 0;
            _stateTracker.ResetTransitions();
        }
    }
}