using DecaEngine.Core;

namespace DecaEngine.Graphics.Diligent;

public class DrawGreenSquarePass : RenderGraphPass<DrawGreenSquarePass.PassData>
{
	public override string Name { get; }
	private readonly string _psoName;

	public struct PassData
	{
		public TextureResource textureResource;
		public PsoResource psoResource;
	}

	public DrawGreenSquarePass(string name, string psoName)
	{
		Name = name;
		_psoName = psoName;
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

		//var psoResource = builder.ReadPso(new PsoResource(new GraphId(_psoName)));

		return new PassData()
		{
			textureResource = builder.WriteTarget(texture),
			//psoResource = psoResource
		};
	}

	public override void Execute(in PassData value, in IRenderGraphContext context)
	{
		context.SetRenderTargets(value.textureResource);
		
		// Очищаем фон (например, черным цветом)
		context.ClearRenderTarget(value.textureResource, 0.0f, 0.0f, 0.0f, 1.0f);
		
		// Устанавливаем PipelineState для отрисовки зеленого квадрата по центру
		context.SetPipelineState(value.psoResource);
		
		// Отрисовываем квадрат (2 треугольника, 6 индексов)
		context.DrawIndexed(0, 6, 0, 0, 1, IndexType.UInt16);
	}
}