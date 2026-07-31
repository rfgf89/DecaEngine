using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Graphics.Core;

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
			new TextureInfo()
			{
				name = Name,
				width = 1024,
				height = 1024,
				format = TextureObjectFormat.R8G8B8A8UNorm
			});

		return new PassData()
		{
			textureResource = builder.WriteTarget(texture)
		};
	}

	public override void WriteCommands(in PassData value, in IRenderGraphContext context)
	{
		var cmd = context.cmd;
		var target = context.GetTexture(value.textureResource);
		cmd.SetRenderTarget(target, null);
		cmd.ClearRenderTarget(target, new Vector4(0.0f, 0.0f, 1.0f, 1.0f));
	}
}