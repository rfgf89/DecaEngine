using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Core.Diagnostics;
using StbImageSharp;
using DecaEngine.Animation;
using DecaEngine.Graphics;

namespace DecaEngine.Editor;

/// <summary>Preview environment: texture, direction TOWARD its dominant light, and Radiance - a thread-safe CPU twin of the shader's SampleEnvironment (linear, no user yaw) used by the probe-GI baker.</summary>
public readonly record struct EnvironmentMapResult(IGpuTexture Texture, Vector3 SunDirection,
	Func<Vector3, Vector3> Radiance);

/// <summary>Procedural equirect environment for PBR preview (_EnvMap): each mip is regenerated at its own resolution with roughness-blurred sun/horizon (not downsampled), so SampleLevel(mip = roughness * MipMax) approximates a GGX prefilter. Values linear 0..1.</summary>
public static class PreviewEnvironmentMap
{
	public const int BaseWidth = 256;
	public const int BaseHeight = 128;

	/// <summary>Mip level count; the shader constant EnvMipMax must equal MipCount - 1.</summary>
	public const int MipCount = 7;

	// Fixed world direction toward the sun: up and to the side, so the reflection lives on upper
	// hemispheres but does not coincide on screen with the camera key light.
	private static readonly Vector3 SunDirection = Vector3.Normalize(new Vector3(-0.45f, 0.72f, 0.35f));

	/// <summary>Creates the preview environment; loads the equirect .hdr when given, any failure logs and falls back to the procedural sky.</summary>
	public static EnvironmentMapResult Create(IGraphicsApi api, string hdrPath = null)
	{
		if (!string.IsNullOrEmpty(hdrPath))
		{
			try
			{
				var hdr = TryCreateFromHdr(api, hdrPath);
				if (hdr.HasValue)
				{
					return hdr.Value;
				}

				EngineLog.Add(LogLevel.Warning,
					$"Preview environment: '{hdrPath}' not found or not a readable .hdr - using procedural sky.");
			}
			catch (Exception ex)
			{
				EngineLog.Add(LogLevel.Warning,
					$"Preview environment: failed to load '{hdrPath}' ({ex.Message}) - using procedural sky.");
			}
		}

		var mips = new List<byte[]>(MipCount);
		for (int mip = 0; mip < MipCount; mip++)
		{
			int width = Math.Max(1, BaseWidth >> mip);
			int height = Math.Max(1, BaseHeight >> mip);
			float roughness = mip / (float)(MipCount - 1);
			mips.Add(GenerateLevel(width, height, roughness));
		}

		return new EnvironmentMapResult(
			api.CreateTexture2DWithMips("Preview Environment", mips, BaseWidth, BaseHeight),
			SunDirection,
			dir => Shade(dir, 0f));
	}

	/// <summary>Loads an equirect .hdr into RGBA16F - RGBA8 would clip the sun's HDR range and specular reflections would lose energy.</summary>
	private static EnvironmentMapResult? TryCreateFromHdr(IGraphicsApi api, string hdrPath)
	{
		if (!File.Exists(hdrPath))
		{
			return null;
		}

		var decoded = ImageResultFloat.FromMemory(File.ReadAllBytes(hdrPath), ColorComponents.RedGreenBlueAlpha);
		if (decoded?.Data == null || decoded.Width < 2 || decoded.Height < 2)
		{
			return null;
		}

		// Block-average downsample - source equirect is typically 1-4k, preview base is 256x128.
		var basePixels = new Vector3[BaseWidth * BaseHeight];
		double luminanceSum = 0;
		for (int y = 0; y < BaseHeight; y++)
		{
			int srcY0 = y * decoded.Height / BaseHeight;
			int srcY1 = Math.Max(srcY0 + 1, (y + 1) * decoded.Height / BaseHeight);

			for (int x = 0; x < BaseWidth; x++)
			{
				int srcX0 = x * decoded.Width / BaseWidth;
				int srcX1 = Math.Max(srcX0 + 1, (x + 1) * decoded.Width / BaseWidth);

				var sum = Vector3.Zero;
				int count = 0;
				for (int sy = srcY0; sy < srcY1; sy++)
				{
					for (int sx = srcX0; sx < srcX1; sx++)
					{
						int idx = (sy * decoded.Width + sx) * 4;
						sum += new Vector3(decoded.Data[idx], decoded.Data[idx + 1], decoded.Data[idx + 2]);
						count++;
					}
				}

				var pixel = sum / count;
				basePixels[y * BaseWidth + x] = pixel;
				luminanceSum += 0.299f * pixel.X + 0.587f * pixel.Y + 0.114f * pixel.Z;
			}
		}

		// Auto-exposure: mean luminance is normalized to the procedural sky's (~0.35) so any
		// absolute HDR scale matches the analytic lights. Peaks are clamped AFTER exposure at 6x
		// mean: HDR suns punch through the tonemapper into pure white in glossy reflections.
		float averageLuminance = (float)(luminanceSum / basePixels.Length);
		float exposure = averageLuminance > 1e-5f ? 0.35f / averageLuminance : 1f;
		for (int i = 0; i < basePixels.Length; i++)
		{
			basePixels[i] = Vector3.Min(basePixels[i] * exposure, new Vector3(6f));
		}

		// Dominant light direction: lum^4-weighted texel directions with sin(theta) equirect-area
		// correction; the preview key light and shadows are built from it.
		var weightedDirection = Vector3.Zero;
		for (int y = 0; y < BaseHeight; y++)
		{
			float theta = (y + 0.5f) / BaseHeight * MathF.PI;
			float dirY = MathF.Cos(theta);
			float sinTheta = MathF.Sin(theta);

			for (int x = 0; x < BaseWidth; x++)
			{
				var p = basePixels[y * BaseWidth + x];
				float lum = 0.299f * p.X + 0.587f * p.Y + 0.114f * p.Z;
				float weight = lum * lum * lum * lum * sinTheta;

				float phi = (x + 0.5f) / BaseWidth * (2f * MathF.PI) - MathF.PI;
				weightedDirection += new Vector3(sinTheta * MathF.Cos(phi), dirY, sinTheta * MathF.Sin(phi)) * weight;
			}
		}

		var sunDirection = weightedDirection.LengthSquared() > 1e-8f
			? Vector3.Normalize(weightedDirection)
			: SunDirection;

		// Light from below the horizon reads wrong as top-down shadows - clamp to the upper hemisphere.
		if (sunDirection.Y < 0.15f)
		{
			sunDirection = Vector3.Normalize(new Vector3(sunDirection.X, 0.15f, sunDirection.Z));
		}

		// Mip chain: 2x downsample + horizontally wrapping blur widening per level - a cheap
		// GGX-prefilter stand-in, consistent with the procedural path.
		var mips = new List<byte[]>(MipCount);
		var level = basePixels;
		int levelWidth = BaseWidth, levelHeight = BaseHeight;
		mips.Add(ToHalfPixels(level, levelWidth, levelHeight));

		for (int mip = 1; mip < MipCount; mip++)
		{
			(level, levelWidth, levelHeight) = Downsample2x(level, levelWidth, levelHeight);
			for (int pass = 0; pass < mip; pass++)
			{
				level = BlurWrapped(level, levelWidth, levelHeight);
			}

			mips.Add(ToHalfPixels(level, levelWidth, levelHeight));
		}

		// CPU radiance for probe-GI: bilinear lookup of the base level (exposure/clamp applied).
		var radiancePixels = basePixels;
		Vector3 SampleHdr(Vector3 dir)
		{
			dir = Vector3.Normalize(dir);
			float u = MathF.Atan2(dir.Z, dir.X) / (2f * MathF.PI) + 0.5f;
			float v = MathF.Acos(Math.Clamp(dir.Y, -1f, 1f)) / MathF.PI;

			float fx = u * BaseWidth - 0.5f;
			float fy = v * BaseHeight - 0.5f;
			int x0 = (int)MathF.Floor(fx);
			int y0 = Math.Clamp((int)MathF.Floor(fy), 0, BaseHeight - 1);
			int y1 = Math.Clamp(y0 + 1, 0, BaseHeight - 1);
			float tx = fx - MathF.Floor(fx);
			float ty = fy - y0;

			int xa = ((x0 % BaseWidth) + BaseWidth) % BaseWidth;
			int xb = (xa + 1) % BaseWidth;

			var top = Vector3.Lerp(radiancePixels[y0 * BaseWidth + xa], radiancePixels[y0 * BaseWidth + xb], tx);
			var bottom = Vector3.Lerp(radiancePixels[y1 * BaseWidth + xa], radiancePixels[y1 * BaseWidth + xb], tx);
			return Vector3.Lerp(top, bottom, ty);
		}

		return new EnvironmentMapResult(
			api.CreateTexture2DWithMips($"Preview Environment ({Path.GetFileName(hdrPath)})",
				mips, BaseWidth, BaseHeight, floatFormat: true),
			sunDirection,
			SampleHdr);
	}

	private static (Vector3[] Pixels, int Width, int Height) Downsample2x(Vector3[] src, int width, int height)
	{
		int newWidth = Math.Max(1, width / 2);
		int newHeight = Math.Max(1, height / 2);
		var dst = new Vector3[newWidth * newHeight];

		for (int y = 0; y < newHeight; y++)
		{
			int y0 = Math.Min(y * 2, height - 1);
			int y1 = Math.Min(y * 2 + 1, height - 1);
			for (int x = 0; x < newWidth; x++)
			{
				int x0 = Math.Min(x * 2, width - 1);
				int x1 = Math.Min(x * 2 + 1, width - 1);
				dst[y * newWidth + x] = (src[y0 * width + x0] + src[y0 * width + x1] +
					src[y1 * width + x0] + src[y1 * width + x1]) * 0.25f;
			}
		}

		return (dst, newWidth, newHeight);
	}

	/// <summary>3x3 blur: wraps in X (equirect seam), clamps in Y (poles).</summary>
	private static Vector3[] BlurWrapped(Vector3[] src, int width, int height)
	{
		var dst = new Vector3[src.Length];
		for (int y = 0; y < height; y++)
		{
			int yUp = Math.Max(y - 1, 0);
			int yDown = Math.Min(y + 1, height - 1);
			for (int x = 0; x < width; x++)
			{
				int xLeft = (x - 1 + width) % width;
				int xRight = (x + 1) % width;

				dst[y * width + x] =
					(src[yUp * width + xLeft] + src[yUp * width + x] + src[yUp * width + xRight] +
					 src[y * width + xLeft] + src[y * width + x] * 2f + src[y * width + xRight] +
					 src[yDown * width + xLeft] + src[yDown * width + x] + src[yDown * width + xRight]) / 10f;
			}
		}

		return dst;
	}

	private static byte[] ToHalfPixels(Vector3[] pixels, int width, int height)
	{
		var bytes = new byte[width * height * 8];
		for (int i = 0; i < pixels.Length; i++)
		{
			WriteHalf(bytes, i * 8 + 0, pixels[i].X);
			WriteHalf(bytes, i * 8 + 2, pixels[i].Y);
			WriteHalf(bytes, i * 8 + 4, pixels[i].Z);
			WriteHalf(bytes, i * 8 + 6, 1f);
		}

		return bytes;
	}

	private static void WriteHalf(byte[] bytes, int offset, float value)
	{
		ushort bits = BitConverter.HalfToUInt16Bits((Half)value);
		bytes[offset] = (byte)bits;
		bytes[offset + 1] = (byte)(bits >> 8);
	}

	private static byte[] GenerateLevel(int width, int height, float roughness)
	{
		var pixels = new byte[width * height * 4];

		for (int y = 0; y < height; y++)
		{
			// Equirect: v 0 -> zenith (+Y), v 1 -> nadir (-Y).
			float theta = (y + 0.5f) / height * MathF.PI;
			float dirY = MathF.Cos(theta);
			float sinTheta = MathF.Sin(theta);

			for (int x = 0; x < width; x++)
			{
				float phi = (x + 0.5f) / width * (2f * MathF.PI) - MathF.PI;
				var dir = new Vector3(sinTheta * MathF.Cos(phi), dirY, sinTheta * MathF.Sin(phi));

				var color = Shade(dir, roughness);

				int idx = (y * width + x) * 4;
				pixels[idx + 0] = ToByte(color.X);
				pixels[idx + 1] = ToByte(color.Y);
				pixels[idx + 2] = ToByte(color.Z);
				pixels[idx + 3] = 255;
			}
		}

		return pixels;
	}

	private static Vector3 Shade(Vector3 dir, float roughness)
	{
		var skyZenith = new Vector3(0.50f, 0.60f, 0.78f);
		var skyHorizon = new Vector3(0.88f, 0.84f, 0.76f);
		var groundHorizon = new Vector3(0.38f, 0.34f, 0.30f);
		var groundDeep = new Vector3(0.16f, 0.15f, 0.14f);

		var sky = Vector3.Lerp(skyHorizon, skyZenith, MathF.Pow(MathF.Max(dir.Y, 0f), 0.55f));
		var ground = Vector3.Lerp(groundHorizon, groundDeep, MathF.Pow(MathF.Max(-dir.Y, 0f), 0.5f));

		// Horizon transition widens with roughness - a blurred reflection must not keep a sharp
		// line that a real prefilter would have smeared.
		float horizonWidth = 0.04f + 0.5f * roughness;
		float t = SmoothStep(-horizonWidth, horizonWidth, dir.Y);
		var color = Vector3.Lerp(ground, sky, t);

		// Gaussian-like sun spot that spreads and dims with roughness (rough energy conservation).
		float sunSharpness = Lerp(180f, 3f, roughness);
		float sunIntensity = Lerp(1.6f, 0.25f, roughness);
		float alignment = Vector3.Dot(dir, SunDirection);
		float sun = MathF.Exp((alignment - 1f) * sunSharpness) * sunIntensity;
		color += new Vector3(1.0f, 0.96f, 0.88f) * sun;

		return color;
	}

	private static float Lerp(float a, float b, float t) => a + (b - a) * t;

	private static float SmoothStep(float edge0, float edge1, float x)
	{
		float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
		return t * t * (3f - 2f * t);
	}

	private static byte ToByte(float value) => (byte)(Math.Clamp(value, 0f, 1f) * 255f + 0.5f);
}
