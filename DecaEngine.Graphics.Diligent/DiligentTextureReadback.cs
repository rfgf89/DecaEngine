using Diligent;

namespace DecaEngine.Graphics.Diligent;

/// <summary>
/// CPU-считывание пикселей из <see cref="DiligentRenderTarget"/> (например, для сохранения
/// превью-иконок ассетов на диск, см. DecaEngine.Editor.ModelIconBaker). Синхронно: делает
/// Flush + WaitForIdle, поэтому предназначено для редких оффлайн-операций (бейк иконок),
/// а не для per-frame чтения.
/// </summary>
public static class DiligentTextureReadback
{
	public static unsafe byte[] ReadRgba8(DiligentGraphicsApi api, DiligentRenderTarget target, out int width, out int height)
	{
		width = (int)target.Size.X;
		height = (int)target.Size.Y;

		var device = api.Device;
		var ctx = api.ImmediateContext;

		// Формат staging-текстуры должен байт-в-байт совпадать с исходной (CopyTexture не умеет
		// конвертировать) - берём его из описания реальной GPU-текстуры, а не предполагаем RGBA8.
		var sourceDesc = target.Texture.GetDesc();

		var stagingDesc = new TextureDesc
		{
			Name = "Readback Staging",
			Type = ResourceDimension.Tex2d,
			Width = (uint)width,
			Height = (uint)height,
			Format = sourceDesc.Format,
			BindFlags = BindFlags.None,
			Usage = Usage.Staging,
			CPUAccessFlags = CpuAccessFlags.Read,
			MipLevels = 1,
		};

		using var staging = device.CreateTexture(stagingDesc);

		var copyAttribs = new CopyTextureAttribs
		{
			SrcTexture = target.Texture,
			SrcTextureTransitionMode = ResourceStateTransitionMode.Transition,
			DstTexture = staging,
			DstTextureTransitionMode = ResourceStateTransitionMode.Transition,
		};
		ctx.CopyTexture(copyAttribs);

		// Flush обязан идти перед WaitForIdle - см. аналогичную пару в DiligentGraphicsUtility
		// (буферный readback): без него записанные, но не отправленные команды ещё не начаты GPU
		// к моменту возврата WaitForIdle, и Map прочитал бы недокопированные данные.
		ctx.Flush();
		ctx.WaitForIdle();

		var mapped = ctx.MapTextureSubresource(staging, 0, 0, MapType.Read, MapFlags.None, null);
		try
		{
			var rowBytes = width * 4;
			var pixels = new byte[height * rowBytes];
			var stride = (long)mapped.Stride;

			fixed (byte* dst = pixels)
			{
				for (int y = 0; y < height; y++)
				{
					System.Buffer.MemoryCopy(
						(byte*)mapped.Data + y * stride,
						dst + (long)y * rowBytes,
						rowBytes,
						rowBytes);
				}
			}

			return pixels;
		}
		finally
		{
			ctx.UnmapTextureSubresource(staging, 0, 0);
		}
	}
}
