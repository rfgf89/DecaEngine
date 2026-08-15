using System;
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

	/// <summary>
	/// CPU-считывание всех слайсов 32-битной однокомпонентной текстуры (D32Float/R32Float массива -
	/// например shadow map каскадов) как float[слайс][y * width + x]. Тот же синхронный
	/// Flush + WaitForIdle путь, что и <see cref="ReadRgba8"/> - только для отладочных дампов.
	/// </summary>
	public static unsafe float[][] ReadFloatSlices(DiligentGraphicsApi api, DiligentRenderTarget target,
		out int width, out int height)
	{
		var device = api.Device;
		var ctx = api.ImmediateContext;

		var sourceDesc = target.Texture.GetDesc();
		width = (int)sourceDesc.Width;
		height = (int)sourceDesc.Height;
		int sliceCount = (int)Math.Max(1u, sourceDesc.ArraySizeOrDepth);

		// Депт-формат staging-текстурой быть не может (D3D12 не даёт Staging+D32) - берём
		// байт-совместимый R32Float, CopyTexture копирует субресурсы побайтно.
		//
		// Staging НЕ массив, а одиночный Tex2d, слайсы гоняются по одному (копия -> Flush +
		// WaitForIdle -> Map): на D3D12 array-staging с Map по субресурсам возвращал данные не тех
		// слайсов (слайс 1 читался как 0, 3 - как 2; на Vulkan тот же код работал). Плюс Map
		// staging-текстуры на D3D12 без синхронизации ругается валидацией "must use fences" -
		// WaitForIdle перед КАЖДЫМ Map закрывает и это.
		var stagingDesc = new TextureDesc
		{
			Name = "Depth Readback Staging",
			Type = ResourceDimension.Tex2d,
			Width = (uint)width,
			Height = (uint)height,
			ArraySizeOrDepth = 1,
			Format = sourceDesc.Format == TextureFormat.D32_Float ? TextureFormat.R32_Float : sourceDesc.Format,
			BindFlags = BindFlags.None,
			Usage = Usage.Staging,
			CPUAccessFlags = CpuAccessFlags.Read,
			MipLevels = 1,
		};

		using var staging = device.CreateTexture(stagingDesc);

		var slices = new float[sliceCount][];
		for (uint slice = 0; slice < sliceCount; slice++)
		{
			var copyAttribs = new CopyTextureAttribs
			{
				SrcTexture = target.Texture,
				SrcSlice = slice,
				SrcTextureTransitionMode = ResourceStateTransitionMode.Transition,
				DstTexture = staging,
				DstSlice = 0,
				DstTextureTransitionMode = ResourceStateTransitionMode.Transition,
			};
			ctx.CopyTexture(copyAttribs);

			ctx.Flush();
			ctx.WaitForIdle();

			var mapped = ctx.MapTextureSubresource(staging, 0, 0, MapType.Read, MapFlags.None, null);
			try
			{
				var data = new float[width * height];
				var stride = (long)mapped.Stride;
				fixed (float* dst = data)
				{
					for (int y = 0; y < height; y++)
					{
						System.Buffer.MemoryCopy(
							(byte*)mapped.Data + y * stride,
							(byte*)dst + (long)y * width * 4,
							width * 4,
							width * 4);
					}
				}

				slices[slice] = data;
			}
			finally
			{
				ctx.UnmapTextureSubresource(staging, 0, 0);
			}
		}

		return slices;
	}
}
