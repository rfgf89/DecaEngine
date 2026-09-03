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

/// <summary>Per-triangle attributes for probe GI: albedo from textures, tiles, packed material channels.</summary>
public static partial class ModelImporter
{
	/// <summary>Per-triangle linear albedo (and metal/rough) sampled at UV centroids, keyed by
	/// meshId. Meshes without CPU pixels fall back to MaterialPbrFactors.AverageBaseColor.</summary>
	internal static void ComputeTriangleAlbedoFromTextures(ModelLoader result, PreparedModel prepared)
	{
		var materialByLogical = new Dictionary<int, PreparedMaterial>();
		foreach (var pm in prepared.Materials)
		{
			materialByLogical[pm.LogicalIndex] = pm;

			// Albedo tile is built in the same pass, while CPU pixels are still alive.
			var tileSource = pm.BaseColorTexture;
			if (tileSource?.Pixels != null && tileSource.Width > 0 && tileSource.Height > 0 &&
				!result.MaterialAlbedoTile.ContainsKey(pm.LogicalIndex))
			{
				result.MaterialAlbedoTile[pm.LogicalIndex] = BuildAlbedoTile(tileSource);
			}
		}

		// Cooked path: no pixels, attributes arrive prepacked from .dmdl (5 bytes/triangle,
		// see EnsureTriangleAttributes) - unpack and return.
		if (prepared.TriangleAttributes.Count > 0)
		{
			foreach (var (meshId, packed) in prepared.TriangleAttributes)
			{
				int count = packed.Length / 5;
				var albedoOut = new Vector3[count];
				var metalOut = new float[count];
				var roughOut = new float[count];
				for (int t = 0; t < count; t++)
				{
					int b = t * 5;
					albedoOut[t] = new Vector3(
						MathF.Pow(packed[b] / 255f, 2.2f),
						MathF.Pow(packed[b + 1] / 255f, 2.2f),
						MathF.Pow(packed[b + 2] / 255f, 2.2f));
					metalOut[t] = packed[b + 3] / 255f;
					roughOut[t] = packed[b + 4] / 255f;
				}

				result.TriangleAlbedo[meshId] = albedoOut;
				result.TriangleMetalness[meshId] = metalOut;
				result.TriangleRoughness[meshId] = roughOut;
			}

			return;
		}

		foreach (var inst in prepared.Instances)
		{
			if (inst.meshId < 0 || inst.meshId >= prepared.Meshes.Count ||
				result.TriangleAlbedo.ContainsKey(inst.meshId))
			{
				continue;
			}

			if (!materialByLogical.TryGetValue(inst.materialId, out var pm))
			{
				continue;
			}

			// Missing base-color pixels must not skip the mesh: metal/rough come from their own
			// texture below, and albedo falls back to the material average.
			var texture = pm.BaseColorTexture;
			bool hasBasePixels = texture?.Pixels != null && texture.Width > 0 && texture.Height > 0;

			var mesh = prepared.Meshes[inst.meshId];
			if (!mesh.HasUv || mesh.Vertices == null || mesh.Indices == null || mesh.Indices.Length < 3)
			{
				continue;
			}

			// Fallback matches the consumer's source: MaterialPbrFactors.AverageBaseColor.
			var factor = hasBasePixels
				? new Vector3(pm.BaseColorFactor.X, pm.BaseColorFactor.Y, pm.BaseColorFactor.Z)
				: new Vector3(ComputeAverageBaseColor(pm).X, ComputeAverageBaseColor(pm).Y,
					ComputeAverageBaseColor(pm).Z);
			int triCount = mesh.Indices.Length / 3;
			var albedo = new Vector3[triCount];

			// One tap buffer per mesh: stackalloc inside the triangle loop accumulates stack
			// (frame is not released until return) and overflows on Sponza-sized models.
			Span<Vector2> taps = stackalloc Vector2[7];

			// glTF MR packing: G = roughness, B = metallic; data is linear, no sRGB decode.
			var mrTexture = pm.MetallicRoughnessTexture;

			// If MR pixels are absent (streaming/cooked) but the material may be metallic,
			// decode a small (256px) copy just for per-triangle metal/rough; otherwise the
			// factor fallback makes the whole material rough metal and RT bounces never start.
			// Decoded pixels stay in locals, not PreparedTexture: the same instance may go to
			// the asset baker, and swapping its pixels would bake 256px into .dtex.
			var mrPixels = mrTexture?.Pixels;
			int mrWidth = mrTexture?.Width ?? 0;
			int mrHeight = mrTexture?.Height ?? 0;

			if (mrPixels == null && mrTexture?.StreamSource != null && pm.MetallicFactor > 0.5f)
			{
				try
				{
					var encoded = mrTexture.StreamSource.EncodedBytes
						?? (mrTexture.StreamSource.FilePath != null && File.Exists(mrTexture.StreamSource.FilePath)
							? File.ReadAllBytes(mrTexture.StreamSource.FilePath)
							: null);
					if (encoded != null)
					{
						var levels = DecodeEncodedImageLadder(encoded, 256, 256, 2);
						if (levels.Count > 0)
						{
							var top = levels[levels.Count - 1];
							mrPixels = top.Pixels;
							mrWidth = top.Width;
							mrHeight = top.Height;
						}
					}
				}
				catch (Exception)
				{
					// Quality optimization only: on failure fall back to material factors.
				}
			}

			bool hasMrPixels = mrPixels != null && mrWidth > 0 && mrHeight > 0;
			var metalness = hasMrPixels ? new float[triCount] : null;
			var roughness = hasMrPixels ? new float[triCount] : null;

			for (int t = 0; t < triCount; t++)
			{
				uint i0 = mesh.Indices[t * 3], i1 = mesh.Indices[t * 3 + 1], i2 = mesh.Indices[t * 3 + 2];
				if (i0 >= mesh.Vertices.Length || i1 >= mesh.Vertices.Length || i2 >= mesh.Vertices.Length)
				{
					albedo[t] = factor;
					if (metalness != null)
					{
						metalness[t] = pm.MetallicFactor;
						roughness![t] = pm.RoughnessFactor;
					}
					continue;
				}

				// Seven taps per triangle instead of one centroid: real MR maps are speckled,
				// and a single tap turned lone triangles into metallic outliers in RT reflections.
				var uvA = mesh.Vertices[i0].TexCoord;
				var uvB = mesh.Vertices[i1].TexCoord;
				var uvC = mesh.Vertices[i2].TexCoord;
				var uvCentroid = (uvA + uvB + uvC) / 3f;
				taps[0] = uvCentroid;
				taps[1] = Vector2.Lerp(uvA, uvCentroid, 0.25f);
				taps[2] = Vector2.Lerp(uvB, uvCentroid, 0.25f);
				taps[3] = Vector2.Lerp(uvC, uvCentroid, 0.25f);
				taps[4] = Vector2.Lerp((uvA + uvB) * 0.5f, uvCentroid, 0.25f);
				taps[5] = Vector2.Lerp((uvB + uvC) * 0.5f, uvCentroid, 0.25f);
				taps[6] = Vector2.Lerp((uvC + uvA) * 0.5f, uvCentroid, 0.25f);

				var albedoSum = Vector3.Zero;
				float metalSum = 0f, roughSum = 0f;
				int albedoTaps = 0, mrTaps = 0;

				foreach (var tap in taps)
				{
					// Wrap UV like a Wrap sampler (negative values too).
					float u = tap.X - MathF.Floor(tap.X);
					float v = tap.Y - MathF.Floor(tap.Y);

					if (hasBasePixels)
					{
						int px = Math.Clamp((int)(u * texture!.Width), 0, texture.Width - 1);
						int py = Math.Clamp((int)(v * texture.Height), 0, texture.Height - 1);
						int idx = (py * texture.Width + px) * 4;
						if (idx + 2 < texture.Pixels!.Length)
						{
							// sRGB -> linear via the same pow(2.2) as UnlitInstancedPS.hlsl.
							albedoSum += new Vector3(
								MathF.Pow(texture.Pixels[idx] / 255f, 2.2f),
								MathF.Pow(texture.Pixels[idx + 1] / 255f, 2.2f),
								MathF.Pow(texture.Pixels[idx + 2] / 255f, 2.2f));
							albedoTaps++;
						}
					}

					if (metalness != null)
					{
						int mx = Math.Clamp((int)(u * mrWidth), 0, mrWidth - 1);
						int my = Math.Clamp((int)(v * mrHeight), 0, mrHeight - 1);
						int mBase = (my * mrWidth + mx) * 4;
						if (mBase + 2 < mrPixels!.Length)
						{
							// glTF packing: G = roughness, B = metallic; linear data.
							metalSum += mrPixels[mBase + 2] / 255f;
							roughSum += mrPixels[mBase + 1] / 255f;
							mrTaps++;
						}
					}
				}

				albedo[t] = albedoTaps > 0 ? albedoSum / albedoTaps * factor : factor;

				if (metalness != null)
				{
					metalness[t] = mrTaps > 0 ? metalSum / mrTaps * pm.MetallicFactor : pm.MetallicFactor;
					roughness![t] = mrTaps > 0 ? roughSum / mrTaps * pm.RoughnessFactor : pm.RoughnessFactor;
				}
			}

			result.TriangleAlbedo[inst.meshId] = albedo;
			if (metalness != null)
			{
				result.TriangleMetalness[inst.meshId] = metalness;
				result.TriangleRoughness[inst.meshId] = roughness!;
			}
		}
	}

	/// <summary>Packs PreparedModel.TriangleAttributes while texture pixels are still alive;
	/// called by the asset baker before writing .dmdl, since cooked models have no pixels.
	/// Costs 5 bytes per triangle in the cache.</summary>
	internal static void EnsureTriangleAttributes(PreparedModel prepared)
	{
		if (prepared.TriangleAttributes.Count > 0)
		{
			return;
		}

		// Reuse the regular-load code path via a scratch container.
		var scratch = new ModelLoader();
		ComputeTriangleAlbedoFromTextures(scratch, prepared);

		foreach (var (meshId, albedo) in scratch.TriangleAlbedo)
		{
			scratch.TriangleMetalness.TryGetValue(meshId, out var metal);
			scratch.TriangleRoughness.TryGetValue(meshId, out var rough);

			var packed = new byte[albedo.Length * 5];
			for (int t = 0; t < albedo.Length; t++)
			{
				int b = t * 5;
				packed[b] = EncodeUnitSrgb(albedo[t].X);
				packed[b + 1] = EncodeUnitSrgb(albedo[t].Y);
				packed[b + 2] = EncodeUnitSrgb(albedo[t].Z);
				packed[b + 3] = EncodeUnit(metal != null && t < metal.Length ? metal[t] : 0f);
				packed[b + 4] = EncodeUnit(rough != null && t < rough.Length ? rough[t] : 1f);
			}

			prepared.TriangleAttributes[meshId] = packed;
		}
	}

	private static byte EncodeUnit(float value) =>
		(byte)Math.Clamp((int)(Math.Clamp(value, 0f, 1f) * 255f + 0.5f), 0, 255);

	private static byte EncodeUnitSrgb(float linear) =>
		EncodeUnit(MathF.Pow(Math.Clamp(linear, 0f, 1f), 1f / 2.2f));

	// Box-downsamples base color into an AlbedoTileSize^2 tile. Averaged in linear space but on
	// a sparse grid (<=4x4 samples per tile texel): a full pass over 2K textures would cost
	// hundreds of millions of samples per model.
	private static byte[] BuildAlbedoTile(PreparedTexture texture)
	{
		const int size = ModelLoader.AlbedoTileSize;

		// sRGB -> linear via lookup table: per-sample pow dominates the whole pass otherwise.
		Span<float> toLinear = stackalloc float[256];
		for (int i = 0; i < 256; i++)
		{
			toLinear[i] = MathF.Pow(i / 255f, 2.2f);
		}

		var tile = new byte[size * size * 4];
		var pixels = texture.Pixels!;
		for (int ty = 0; ty < size; ty++)
		{
			int y0 = (int)((long)ty * texture.Height / size);
			int y1 = Math.Max(y0 + 1, (int)((long)(ty + 1) * texture.Height / size));
			int strideY = Math.Max(1, (y1 - y0) / 4);
			for (int tx = 0; tx < size; tx++)
			{
				int x0 = (int)((long)tx * texture.Width / size);
				int x1 = Math.Max(x0 + 1, (int)((long)(tx + 1) * texture.Width / size));
				int strideX = Math.Max(1, (x1 - x0) / 4);

				float r = 0f, g = 0f, b = 0f;
				int count = 0;
				for (int y = y0; y < y1; y += strideY)
				{
					int row = y * texture.Width;
					for (int x = x0; x < x1; x += strideX)
					{
						int idx = (row + x) * 4;
						if (idx + 2 >= pixels.Length)
						{
							continue;
						}

						r += toLinear[pixels[idx]];
						g += toLinear[pixels[idx + 1]];
						b += toLinear[pixels[idx + 2]];
						count++;
					}
				}

				int outIdx = (ty * size + tx) * 4;
				if (count > 0)
				{
					float inv = 1f / count;
					tile[outIdx] = (byte)Math.Clamp((int)(MathF.Pow(r * inv, 1f / 2.2f) * 255f + 0.5f), 0, 255);
					tile[outIdx + 1] = (byte)Math.Clamp((int)(MathF.Pow(g * inv, 1f / 2.2f) * 255f + 0.5f), 0, 255);
					tile[outIdx + 2] = (byte)Math.Clamp((int)(MathF.Pow(b * inv, 1f / 2.2f) * 255f + 0.5f), 0, 255);
				}

				tile[outIdx + 3] = 255;
			}
		}

		return tile;
	}

}
