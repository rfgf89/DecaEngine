using DecaEngine.Core;

namespace DecaEngine.Graphics;

/// <summary>
/// Minimal demo/debug pass: declares a read dependency on a texture pinned by an earlier pass
/// (e.g. <see cref="ClearRenderTargetPass"/>) via <see cref="IRenderGraphBuilder.ReadTarget"/>,
/// without doing any actual GPU work.
///
/// This exists purely so the render graph has at least one genuine write -> ... -> read chain to
/// visualize: without a consumer, a pinned-and-written-but-never-read resource correctly (and
/// expectedly) shows a single-pass lifetime in <c>RenderGraphDebugWindow</c> - that's not a bug,
/// there's just nothing downstream using it. Add this pass after whichever pass writes
/// <paramref name="targetName"/> to see its lifetime correctly span multiple passes instead.
/// </summary>
public sealed class ReadRenderTargetPass : RenderGraphPass<ReadRenderTargetPass.PassData>
{
	public override string Name { get; }

	private readonly string _targetName;
	private readonly uint _width;
	private readonly uint _height;

	public struct PassData
	{
		public TextureResource textureResource;
	}

	public ReadRenderTargetPass(string name, string targetName, uint width = 1024, uint height = 1024)
	{
		Name = name;
		_targetName = targetName;
		_width = width;
		_height = height;
	}

	public override PassData Setup(IRenderGraphBuilder builder)
	{
		var texture = builder.PinTexture(new TextureInfo
		{
			name = _targetName,
			width = _width,
			height = _height,
			format = TextureObjectFormat.R8G8B8A8UNorm
		});

		return new PassData
		{
			textureResource = builder.ReadTarget(texture)
		};
	}

	public override void WriteCommands(in PassData value, in IRenderGraphContext context)
	{
		// Touch the resource so it's a real usage (and so tooling like RenderDoc/PIX sees a
		// meaningful access), but otherwise this pass intentionally does no rendering.
		_ = context.GetTexture(value.textureResource);
	}
}

