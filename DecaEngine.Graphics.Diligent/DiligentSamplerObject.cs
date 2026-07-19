using DecaEngine.Core;
using Diligent;

namespace DecaEngine;

public class DiligentSamplerObject : ISamplerObject
{
	public ISampler Sampler { get; }
	public SamplerDesc Desc { get; }

	public DiligentSamplerObject(ISampler sampler, SamplerDesc desc)
	{
		Sampler = sampler;
		Desc = desc;
	}

	public void Release()
	{
		Sampler?.Dispose();
	}
}