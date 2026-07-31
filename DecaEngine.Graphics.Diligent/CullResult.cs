using System.Collections.Generic;
using DecaEngine.Core;
using Diligent;

namespace DecaEngine.Graphics.Diligent
{
    public readonly struct CullResult : ICullResult
    {
        public readonly DiligentBufferHandle FinallyInstancesBuffer;
        public readonly DiligentBufferHandle IndirectArgsBuffers;
        public readonly DiligentBufferHandle GpuInstancesDataBuffer;
        public readonly IReadOnlyDictionary<int, MaterialDrawRange> MaterialDrawRanges;

        public CullResult(DiligentBufferHandle finallyInstancesBuffer,
            DiligentBufferHandle indirectArgsBuffers,
            DiligentBufferHandle gpuInstancesDataBuffer,
            IReadOnlyDictionary<int, MaterialDrawRange> materialDrawRanges)
        {
            FinallyInstancesBuffer = finallyInstancesBuffer;
            IndirectArgsBuffers = indirectArgsBuffers;
            GpuInstancesDataBuffer = gpuInstancesDataBuffer;
            MaterialDrawRanges = materialDrawRanges;
        }
    }
}