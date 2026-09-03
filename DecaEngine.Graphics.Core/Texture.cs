using DecaEngine.Core;

namespace DecaEngine.Graphics
{
    /// <summary>High-level texture that manages both CPU and GPU resources.</summary>
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

        /// <summary>Uploads the texture data from RAM to VRAM.</summary>
        public void Upload(IGraphicsApi api, bool freeCpuMemory = true)
        {
            if (IsUploaded) return;

            GpuHandle = api.CreateTexture(CpuData);

            if (freeCpuMemory)
            {
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