using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using SharpGLTF.Schema2;
using DecaEngine.Core;
using DecaEngine.Graphics.Assets;
using System.Runtime.InteropServices;
using Diligent;
using MeshOptimizer;
using StbImageSharp;
using UnsafeCollections.Collections.Unsafe;
using DecaEngine.Animation;

namespace DecaEngine.Graphics;

/// <summary>CPU-side glTF decode: root reading, texture and quality-ladder decode, average base
/// color. GPU finalization and the load entry points live in <see cref="ModelLoader"/>.</summary>
public static partial class ModelImporter
{
	// Fallback for nodes whose world matrix will not decompose to TRS: bake it into a vertex copy.
	// The matrix arrives in glTF RH and is conjugated to engine LH by M*W*M, M = diag(1,1,-1).
	private static int BakeMeshWithMatrix(PreparedModel prepared, int meshId, Matrix4x4 worldRh)
	{
		var source = prepared.Meshes[meshId];
		var mirrorZ = Matrix4x4.CreateScale(1f, 1f, -1f);
		var world = mirrorZ * worldRh * mirrorZ;

		// Normals need inverse-transpose: non-uniform scale/shear breaks direct multiplication.
		Matrix4x4.Invert(world, out var inverse);
		var normalMatrix = Matrix4x4.Transpose(inverse);

		var vertices = new Vertex[source.Vertices.Length];
		var min = new Vector3(float.MaxValue);
		var max = new Vector3(float.MinValue);
		for (int i = 0; i < vertices.Length; i++)
		{
			var vertex = source.Vertices[i];
			vertex.Position = Vector3.Transform(vertex.Position, world);
			vertex.Normal = SafeNormalize(Vector3.TransformNormal(vertex.Normal, normalMatrix));
			var tangent = SafeNormalize(Vector3.TransformNormal(
				new Vector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z), world));
			vertex.Tangent = new Vector4(tangent, vertex.Tangent.W);
			vertices[i] = vertex;
			min = Vector3.Min(min, vertex.Position);
			max = Vector3.Max(max, vertex.Position);
		}

		// A mirroring matrix flips winding; without the index swap culling turns geometry inside out.
		var indices = source.Indices;
		if (world.GetDeterminant() < 0f && source.Topology == ModelLoader.MeshTopologyTriangles)
		{
			indices = (uint[])indices.Clone();
			for (int i = 0; i + 2 < indices.Length; i += 3)
			{
				(indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
			}
			for (int i = 0; i < vertices.Length; i++)
			{
				vertices[i].Tangent.W = -vertices[i].Tangent.W;
			}
		}

		prepared.Meshes.Add(new PreparedMesh
		{
			Name = source.Name + " (baked transform)",
			Vertices = vertices,
			Indices = indices,
			LodLevels = source.LodLevels,
			BoundsCenter = (min + max) * 0.5f,
			BoundsRadius = MathF.Max(0.0001f, (max - min).Length() * 0.5f),
			HasUv = source.HasUv,
			Topology = source.Topology,
		});
		return prepared.Meshes.Count - 1;
	}

	private static Vector3 SafeNormalize(Vector3 v)
	{
		return v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : v;
	}

	// The glTF sampler is optional; absent means wrap + linear per spec.
	private static PreparedTexture DecodeTexture(SharpGLTF.Schema2.Texture texture, int maxSize,
		Dictionary<SharpGLTF.Schema2.Image, (byte[] Pixels, int Width, int Height)> decodedImages,
		Dictionary<SharpGLTF.Schema2.Image, TextureStreamSource> streamSources,
		Dictionary<int, string> externalImagePaths = null)
	{
		var sampler = texture.Sampler;
		var prepared = new PreparedTexture
		{
			AddressMode = sampler != null ? ModelLoader.ToAddressMode(sampler.WrapS) : TextureAddress.Wrap,
			FilterMode = sampler != null ? ModelLoader.ToFilter(sampler.MinFilter, sampler.MagFilter) : TextureFilter.Linear,
			SourceImage = texture.PrimaryImage,
		};

		if (streamSources != null)
		{
			// Streaming: no pixels in this phase; the slot gets a 1x1 filler until ModelStreamer runs.
			if (!streamSources.TryGetValue(texture.PrimaryImage, out var streamSource))
			{
				streamSource = CreateStreamSource(texture.PrimaryImage, externalImagePaths);
				streamSources[texture.PrimaryImage] = streamSource;
			}

			prepared.StreamSource = streamSource;
			return prepared;
		}

		if (!decodedImages.TryGetValue(texture.PrimaryImage, out var decoded))
		{
			// Channel missed by the usedImages pre-pass: decode it here.
			decoded = DecodeImagePixels(texture.PrimaryImage, maxSize);
			decodedImages[texture.PrimaryImage] = decoded;
		}

		prepared.Pixels = decoded.Pixels;
		prepared.Width = decoded.Width;
		prepared.Height = decoded.Height;
		return prepared;
	}

	// Prefer a file path over embedded bytes: holding hundreds of 4K sources in the managed heap
	// for the whole session costs gigabytes.
	private static TextureStreamSource CreateStreamSource(SharpGLTF.Schema2.Image image,
		Dictionary<int, string> externalImagePaths)
	{
		if (externalImagePaths != null && externalImagePaths.TryGetValue(image.LogicalIndex, out var path))
		{
			return new TextureStreamSource { FilePath = path };
		}

		var sourcePath = image.Content.SourcePath;
		if (!string.IsNullOrEmpty(sourcePath) && File.Exists(sourcePath))
		{
			return new TextureStreamSource { FilePath = sourcePath };
		}

		return new TextureStreamSource { EncodedBytes = image.Content.Content.ToArray() };
	}

	// Minimal valid 1x1 PNG, substituted for external image content while streaming.
	private static readonly byte[] StubPng = Convert.FromBase64String(
		"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

	// SharpGLTF eagerly loads every image while parsing, so in streaming mode external files are
	// stubbed out and returned as an index -> path map for the streamer to read on demand.
	private static ModelRoot LoadModelRoot(string modelPath, ModelLoadOptions options,
		out Dictionary<int, string> externalImagePaths)
	{
		externalImagePaths = null;

		var settings = new ReadSettings { Validation = SharpGLTF.Validation.ValidationMode.TryFix };

		// Text .gltf only: in .glb the images live inside the file, so there is nothing to stub.
		if (!options.StreamTextures ||
			!string.Equals(Path.GetExtension(modelPath), ".gltf", StringComparison.OrdinalIgnoreCase))
		{
			return ModelRoot.Load(modelPath, settings);
		}

		// Image URIs come straight from JSON; "images" order matches ModelRoot.LogicalImages.
		var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(modelPath)) ?? Environment.CurrentDirectory;
		var pathsByIndex = new Dictionary<int, string>();
		var stubbedUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		try
		{
			using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(modelPath));
			if (json.RootElement.TryGetProperty("images", out var images) &&
				images.ValueKind == System.Text.Json.JsonValueKind.Array)
			{
				int index = 0;
				foreach (var image in images.EnumerateArray())
				{
					if (image.TryGetProperty("uri", out var uriElement) &&
						uriElement.GetString() is { Length: > 0 } uri &&
						!uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
					{
						var relative = Uri.UnescapeDataString(uri).Replace('/', Path.DirectorySeparatorChar);
						var fullPath = Path.Combine(baseDirectory, relative);
						if (File.Exists(fullPath))
						{
							pathsByIndex[index] = fullPath;
							stubbedUris.Add(uri);
							stubbedUris.Add(Uri.UnescapeDataString(uri));
						}
					}

					index++;
				}
			}
		}
		catch (Exception)
		{
			return ModelRoot.Load(modelPath, settings);
		}

		if (pathsByIndex.Count == 0)
		{
			return ModelRoot.Load(modelPath, settings);
		}

		var context = ReadContext
			.Create(uri =>
			{
				if (stubbedUris.Contains(uri))
				{
					return new ArraySegment<byte>(StubPng);
				}

				var candidate = Path.Combine(baseDirectory, Uri.UnescapeDataString(uri)
					.Replace('/', Path.DirectorySeparatorChar));
				if (!File.Exists(candidate))
				{
					candidate = Path.Combine(baseDirectory, uri);
				}

				return new ArraySegment<byte>(File.ReadAllBytes(candidate));
			})
			.WithSettingsFrom(settings);

		externalImagePaths = pathsByIndex;
		return context.ReadSchema2(Path.GetFileName(modelPath));
	}

	private static (byte[] Pixels, int Width, int Height) DecodeImagePixels(SharpGLTF.Schema2.Image image, int maxSize)
		=> DecodeEncodedImage(image.Content.Content.ToArray(), maxSize);

	/// <summary>Decodes a PNG/JPG and downscales to maxSize (0 = no limit); thread-safe. stb decodes
	/// at full resolution first, so peak memory is ~64 MB per 4K source.</summary>
	public static (byte[] Pixels, int Width, int Height) DecodeEncodedImage(byte[] encodedBytes, int maxSize)
	{
		var decoded = ImageResult.FromMemory(encodedBytes, ColorComponents.RedGreenBlueAlpha);

		var pixels = decoded.Data;
		int width = decoded.Width;
		int height = decoded.Height;
		while (maxSize > 0 && (width > maxSize || height > maxSize))
		{
			(pixels, width, height) = DownscaleHalf(pixels, width, height);
		}

		return (pixels, width, height);
	}

	/// <summary>Decodes a whole quality ladder in one pass, ascending from firstSize to maxSize by
	/// stepFactor. The file is decoded exactly once; lower levels come from the same halving chain.</summary>
	public static List<(byte[] Pixels, int Width, int Height)> DecodeEncodedImageLadder(
		byte[] encodedBytes, int maxSize, int firstSize, int stepFactor)
	{
		var decoded = ImageResult.FromMemory(encodedBytes, ColorComponents.RedGreenBlueAlpha);

		var pixels = decoded.Data;
		int width = decoded.Width;
		int height = decoded.Height;
		while (maxSize > 0 && (width > maxSize || height > maxSize))
		{
			(pixels, width, height) = DownscaleHalf(pixels, width, height);
		}

		// Levels are collected largest-first and reversed, since the list must be ascending.
		var levels = new List<(byte[] Pixels, int Width, int Height)> { (pixels, width, height) };
		var halvings = 1;
		for (int step = Math.Max(2, stepFactor); step > 2; step >>= 1)
		{
			halvings++;
		}

		while (firstSize > 0 && Math.Max(width, height) > firstSize)
		{
			for (int i = 0; i < halvings && Math.Max(width, height) > 1; i++)
			{
				(pixels, width, height) = DownscaleHalf(pixels, width, height);
			}

			levels.Add((pixels, width, height));

			if (Math.Max(width, height) <= 1)
			{
				break;
			}
		}

		levels.Reverse();
		return levels;
	}

	// 2x2 box filter, matching GPU GenerateMips; odd sizes clamp to the edge.
	private static (byte[] pixels, int width, int height) DownscaleHalf(byte[] pixels, int width, int height)
	{
		int newWidth = Math.Max(1, width / 2);
		int newHeight = Math.Max(1, height / 2);
		var result = new byte[newWidth * newHeight * 4];

		for (int y = 0; y < newHeight; y++)
		{
			int srcY0 = Math.Min(height - 1, y * 2);
			int srcY1 = Math.Min(height - 1, y * 2 + 1);
			for (int x = 0; x < newWidth; x++)
			{
				int srcX0 = Math.Min(width - 1, x * 2);
				int srcX1 = Math.Min(width - 1, x * 2 + 1);
				int p00 = (srcY0 * width + srcX0) * 4;
				int p01 = (srcY0 * width + srcX1) * 4;
				int p10 = (srcY1 * width + srcX0) * 4;
				int p11 = (srcY1 * width + srcX1) * 4;
				int dst = (y * newWidth + x) * 4;
				for (int c = 0; c < 4; c++)
				{
					result[dst + c] = (byte)((pixels[p00 + c] + pixels[p01 + c] + pixels[p10 + c] + pixels[p11 + c] + 2) >> 2);
				}
			}
		}

		return (result, newWidth, newHeight);
	}

	// Sparse mean of the base color texture in linear space, times the linear factor.
	// Alpha stays linear (no sRGB): the probe-GI baker uses it to detect cutout materials.
	internal static Vector4 ComputeAverageBaseColor(PreparedMaterial pm)
	{
		EnsureAverageBaseColor(pm);
		return pm.AverageBaseColorRgba.Value;
	}

	// Must be called while the base color pixels are still alive, including before writing .dmdl.
	internal static void EnsureAverageBaseColor(PreparedMaterial pm)
	{
		if (pm.AverageBaseColorRgba.HasValue)
		{
			return;
		}

		pm.AverageBaseColorRgba = ComputeAverageBaseColorCore(pm);
		pm.SoftAlphaFraction = ComputeSoftAlphaFraction(pm);
	}

	// Fraction of base color texels with alpha in 0.1..0.9, i.e. how binary the alpha channel is.
	// Separates cutout foliage from soft decals, which alphaMode alone does not. -1 = no pixels.
	private static float ComputeSoftAlphaFraction(PreparedMaterial pm)
	{
		var texture = pm.BaseColorTexture;
		if (texture?.Pixels == null || texture.Width <= 0 || texture.Height <= 0)
		{
			return -1f;
		}

		int pixelCount = texture.Width * texture.Height;
		int stride = Math.Max(1, pixelCount / 4096);
		int soft = 0;
		int count = 0;
		for (int i = 0; i < pixelCount; i += stride)
		{
			int idx = i * 4;
			if (idx + 3 >= texture.Pixels.Length)
			{
				break;
			}

			float a = texture.Pixels[idx + 3] / 255f;
			if (a > 0.1f && a < 0.9f)
			{
				soft++;
			}

			count++;
		}

		return count > 0 ? (float)soft / count : -1f;
	}

	private static Vector4 ComputeAverageBaseColorCore(PreparedMaterial pm)
	{
		var factor = new Vector3(pm.BaseColorFactor.X, pm.BaseColorFactor.Y, pm.BaseColorFactor.Z);
		var texture = pm.BaseColorTexture;
		if (texture?.Pixels == null || texture.Width <= 0 || texture.Height <= 0)
		{
			// No texture at all means the factor IS the mean. But a texture with no pixels yet
			// (streaming, cache miss) must report alpha 0, i.e. "assume cutout", or the shadow
			// alpha test silently switches off for all MASK/BLEND geometry. RGB stays the factor.
			float unknownAlpha = texture != null && pm.AlphaCutoff > 0f ? 0f : pm.BaseColorFactor.W;
			return new Vector4(factor, unknownAlpha);
		}

		// Roughly every 16th pixel: enough for a mean without stalling on huge atlases.
		int pixelCount = texture.Width * texture.Height;
		int stride = Math.Max(1, pixelCount / 4096);
		var sum = Vector3.Zero;
		float alphaSum = 0f;
		int count = 0;
		for (int i = 0; i < pixelCount; i += stride)
		{
			int idx = i * 4;
			if (idx + 3 >= texture.Pixels.Length)
			{
				break;
			}

			// sRGB to linear via the same pow(2.2) the shader uses.
			sum += new Vector3(
				MathF.Pow(texture.Pixels[idx] / 255f, 2.2f),
				MathF.Pow(texture.Pixels[idx + 1] / 255f, 2.2f),
				MathF.Pow(texture.Pixels[idx + 2] / 255f, 2.2f));
			alphaSum += texture.Pixels[idx + 3] / 255f;
			count++;
		}

		return count > 0
			? new Vector4(sum / count * factor, alphaSum / count * pm.BaseColorFactor.W)
			: new Vector4(factor, pm.BaseColorFactor.W);
	}


}
