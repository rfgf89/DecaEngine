using System.Collections.Generic;
using Diligent;
using DecaEngine.Core;

namespace DecaEngine.Graphics;

public interface ISamplerObject : IReleaseObject { }

public struct MaterialDrawRange
{
	public uint FirstDrawIndex;
	public uint DrawCount;
}

public interface IMaterialObject : IStateObject
{
	/// <summary>Whether Release also releases this material's shaders. Must be false when shaders
	/// are shared across materials (model loader) or the native refcount goes negative and later
	/// releases hit freed memory; the shader owner releases them once instead.</summary>
	bool OwnsShaders { get; set; }

	PipelineStateType IStateObject.StateType => PipelineStateType.Graphics;

	public void SetState(IStateObject stateObject);

	public void SetShader(IShaderObject shaderObject);
	public void SetShader(params IShaderObject[] shaders);

	public void SetConstant<T>(string name, ref T data, HandleAccess access = HandleAccess.Pixel) where T : unmanaged;

	public void SetConstant<T>(int ctx, string name, ref T data, HandleAccess access = HandleAccess.Pixel) where T : unmanaged;

	public void SetBuffer(string name, IBufferHandle bufferHandle, HandleAccess access = HandleAccess.Pixel);

	public void SetTexture(string name, IGpuTexture texture, HandleAccess access = HandleAccess.Pixel);

	public void SetSampler(string name, ISamplerObject sampler, HandleAccess access = HandleAccess.Pixel);
	public void SetImmutableSampler(string name, ISamplerObject sampler, HandleAccess access = HandleAccess.Pixel);

	/// <summary>Binds a TLAS for inline ray queries in the pixel stage; default is a no-op,
	/// only the Diligent backend implements it.</summary>
	public void SetAccelStructure(string name, ITopLevelAS tlas) { }

	/// <summary>Binds a raw structured buffer as a pixel-stage SRV; default is a no-op.</summary>
	public void SetStructuredBufferSrv(string name, IBuffer buffer) { }

	/// <summary>Binds a texture array to one `Texture2D name[N]` variable; element count must equal
	/// N in the shader and every slot must be a live texture. Default is a no-op.</summary>
	public void SetTextureSrvArray(string name, IReadOnlyList<IGpuTexture> textures) { }
}

public interface IComputeMaterial : IStateObject
{
	PipelineStateType IStateObject.StateType => PipelineStateType.Compute;

	public void SetState(IStateObject stateObject);

	public void SetShader(IShaderObject computeShader);

	public void SetConstant<T>(string name, ref T data) where T : unmanaged;

	public void SetConstant<T>(int ctx, string name, ref T data) where T : unmanaged;

	public void SetBuffer(string name, IBufferHandle bufferHandle);

	public void SetTexture(string name, IGpuTexture texture, bool isUnorderedAccess = false);

	public void SetSampler(string name, ISamplerObject sampler);

	public void Dispatch(uint threadGroupCountX, uint threadGroupCountY, uint threadGroupCountZ);
}
