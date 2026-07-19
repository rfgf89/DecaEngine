using DecaEngine.Core;
using Diligent;

namespace DecaEngine.Graphics.Diligent;

public class ClearRenderTargetPass : RenderGraphPass<ClearRenderTargetPass.PassData>
{
	public override string Name { get; }

	public struct PassData
	{
		public TextureResource textureResource;
	}

	public ClearRenderTargetPass(string name)
	{
		Name = name;
	}

	public override PassData Setup(IRenderGraphBuilder builder)
	{
		var texture = builder.PinTexture(
			new RenderTargetInfo()
			{
				name = Name,
				width = 1024,
				height = 1024,
				textureFormat = RenderTargetInfo.Format.R8G8B8A8_UNORM
			});

		return new PassData()
		{
			textureResource = builder.WriteTarget(texture)
		};
	}

	public override void Execute(in PassData value, in IRenderGraphContext context)
	{
		context.SetRenderTargets(value.textureResource);
		context.ClearRenderTarget(value.textureResource, 0.0f, 0.0f, 1.0f, 1.0f);
	}
}