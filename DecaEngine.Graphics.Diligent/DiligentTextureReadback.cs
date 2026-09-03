using System;
using Diligent;

namespace DecaEngine.Graphics.Diligent;

/// <summary>Synchronous CPU readback of a render target; stalls the GPU, not for per-frame use.</summary>
public static class DiligentTextureReadback
{
	public static unsafe byte[] ReadRgba8(DiligentGraphicsApi api, DiligentRenderTarget target, out int width, out int height)
	{
		width = (int)target.Size.X;
		height = (int)target.Size.Y;

		var device = api.Device;
		var ctx = api.ImmediateContext;

		// Staging format must match the source byte for byte: CopyTexture cannot convert.
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

		// Flush must precede WaitForIdle, else the copy is still unsubmitted when Map reads.
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

	/// <summary>Reads every slice of a D32/R32 float texture array as float[slice][y * width + x].</summary>
	public static unsafe float[][] ReadFloatSlices(DiligentGraphicsApi api, DiligentRenderTarget target,
		out int width, out int height)
	{
		var device = api.Device;
		var ctx = api.ImmediateContext;

		var sourceDesc = target.Texture.GetDesc();
		width = (int)sourceDesc.Width;
		height = (int)sourceDesc.Height;
		int sliceCount = (int)Math.Max(1u, sourceDesc.ArraySizeOrDepth);

		// D3D12 rejects Staging + D32, so read through the byte-compatible R32Float.
		// Staging is a single Tex2d copied slice by slice: D3D12 array staging maps the wrong slice.
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
