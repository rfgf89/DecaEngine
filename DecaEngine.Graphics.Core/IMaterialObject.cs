using DecaEngine.Graphics.Core;
using Diligent;

namespace DecaEngine.Core;

public interface ISamplerObject : IReleaseObject { }

public struct MaterialDrawRange
{
	public uint FirstDrawIndex;
	public uint DrawCount;
}

public interface IMaterialObject : IStateObject
{
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
}

// ?????: ????????? ????????? ??? Compute
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
