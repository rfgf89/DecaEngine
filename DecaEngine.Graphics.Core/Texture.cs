using DecaEngine.Core;

namespace DecaEngine.Graphics.Core
{
    /// <summary>
    /// High-level texture class that manages both CPU and GPU resources.
    /// This is what the user/scene should interact with.
    /// </summary>
    public class Texture : IReleaseObject
    {
        public string Name { get; }
        public CpuTextureData CpuData { get; private set; }
        public IGpuTexture GpuHandle { get; private set; }

        public bool IsUploaded => GpuHandle != null;

        public Texture(string name, CpuTextureData cpuData)
        {
            Name = name;
            CpuData = cpuData;
        }

        /// <summary>
        /// Uploads the texture data from RAM to VRAM.
        /// </summary>
        /// <param name="pipeline">The graphics pipeline to use for the upload.</param>
        /// <param name="freeCpuMemory">If true, the CPU-side pixel data will be freed after upload.</param>
        public void Upload(IGraphicsPipeline pipeline, bool freeCpuMemory = true)
        {
            if (IsUploaded) return;

            GpuHandle = pipeline.CreateTexture(CpuData);

            if (freeCpuMemory)
            {
                // We can now free the CPU memory as the data is on the GPU
                CpuData.Image = null; 
                CpuData = null;
            }
        }

        public void Release()
        {
            GpuHandle?.Release();
            GpuHandle = null;
            CpuData = null;
        }
    }
}