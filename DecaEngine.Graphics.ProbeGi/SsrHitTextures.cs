using System;
using System.Collections.Generic;
using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Graphics;

namespace DecaEngine.Graphics.ProbeGi;

/// <summary>Base color textures for SSR ray-hit albedo, tied to the lifetime of the accel.</summary>
public sealed class SsrHitTextures : IDisposable
{
	private readonly (ModelLoader Model, int MaterialId)[] _keys;
	private readonly IGraphicsApi _api;
	private IGpuTexture? _atlas;
	private bool _atlasFailed;
	private long _streamStamp;

	// Stand-in while a texture streams: the model's 1x1 white filler would whiten reflections.
	private IGpuTexture?[]? _tileTextures;

	private SsrHitTextures(IGraphicsApi api, (ModelLoader Model, int MaterialId)[] keys)
	{
		_api = api;
		_keys = keys;
		_streamStamp = ComputeStreamStamp();
	}

	/// <summary>Returns null when the geometry has no usable texture keys.</summary>
	public static SsrHitTextures? Build(IGraphicsApi api, ProbeInstancedGeometry geometry,
		IReadOnlyList<ModelLoader> models)
	{
		if (geometry.HitTextureKeys.Length == 0)
		{
			return null;
		}

		var keys = new (ModelLoader, int)[geometry.HitTextureKeys.Length];
		for (int i = 0; i < keys.Length; i++)
		{
			var (modelIndex, materialId) = geometry.HitTextureKeys[i];
			if (modelIndex < 0 || modelIndex >= models.Count)
			{
				return null;
			}

			keys[i] = (models[modelIndex], materialId);
		}

		return new SsrHitTextures(api, keys);
	}

	/// <summary>Lazily built tile atlas; null on backends without Texture2DArray.</summary>
	public IGpuTexture? GetAtlas()
	{
		if (_atlas != null || _atlasFailed)
		{
			return _atlas;
		}

		var layers = new List<byte[]>(_keys.Length);
		foreach (var (model, materialId) in _keys)
		{
			layers.Add(TilePixels(model, materialId));
		}

		_atlas = _api.CreateTextureArray("SSR HitTex Atlas",
			ModelLoader.AlbedoTileSize, ModelLoader.AlbedoTileSize, layers);
		_atlasFailed = _atlas == null;
		return _atlas;
	}

	/// <summary>Full-size material textures for the bindless array, in key order.</summary>
	// Only Completed streams may be bound: the streamer retires intermediate stages and rebinds
	// only its own SRBs, so the SSR trace SRB would keep a stale descriptor.
	public IReadOnlyList<IGpuTexture?> GetFullTextures()
	{
		_tileTextures ??= new IGpuTexture?[_keys.Length];

		var textures = new IGpuTexture?[_keys.Length];
		for (int i = 0; i < _keys.Length; i++)
		{
			var (model, materialId) = _keys[i];
			if (!model.MaterialBaseColor.TryGetValue(materialId, out var binding))
			{
				continue;
			}

			if (binding.Stream == null)
			{
				textures[i] = binding.Texture;
				continue;
			}

			if (binding.Stream is { Completed: true, Texture: not null })
			{
				textures[i] = binding.Stream.Texture;
				continue;
			}

			// Still streaming: our own tile survives every upgrade stage, the model filler does not.
			_tileTextures[i] ??= _api.CreateTexture2DWithMips($"SSR HitTex Tile {i}",
				new[] { TilePixels(model, materialId) },
				ModelLoader.AlbedoTileSize, ModelLoader.AlbedoTileSize);
			textures[i] = _tileTextures[i];
		}

		return textures;
	}

	/// <summary>True when a stream completed since the last call: rebind the bindless array.</summary>
	public bool RefreshStreams()
	{
		long stamp = ComputeStreamStamp();
		if (stamp == _streamStamp)
		{
			return false;
		}

		_streamStamp = stamp;
		return true;
	}

	private long ComputeStreamStamp()
	{
		long stamp = 0;
		foreach (var (model, materialId) in _keys)
		{
			stamp *= 31;
			if (model.MaterialBaseColor.TryGetValue(materialId, out var binding) &&
				binding.Stream is { Completed: true, Texture: not null })
			{
				stamp += 1;
			}
		}

		return stamp;
	}

	/// <summary>Human-readable dump of the key table for diagnostics.</summary>
	public IEnumerable<string> DescribeKeys()
	{
		for (int i = 0; i < _keys.Length; i++)
		{
			var (model, materialId) = _keys[i];
			string state;
			if (!model.MaterialBaseColor.TryGetValue(materialId, out var binding))
			{
				state = "NO BINDING";
			}
			else if (binding.Stream == null)
			{
				state = $"'{binding.Texture?.Name}' {binding.Texture?.Info.width}x{binding.Texture?.Info.height}";
			}
			else
			{
				state = $"stream {(binding.Stream.Completed ? "done" : $"{binding.Stream.CurrentSize}/{binding.Stream.TargetSize}")}"
					+ $" '{binding.Stream.Texture?.Name ?? "filler"}'";
			}

			bool tile = model.MaterialAlbedoTile.ContainsKey(materialId);
			yield return $"[{i}] mat {materialId}: {state}, tile={(tile ? "cpu" : "avg")}";
		}
	}

	private static byte[] TilePixels(ModelLoader model, int materialId) =>
		model.MaterialAlbedoTile.TryGetValue(materialId, out var tile) ? tile : SolidTile(model, materialId);

	// Texture average without the base color factor: the shader applies that itself, and
	// AverageBaseColor is stored pre-multiplied by it.
	private static byte[] SolidTile(ModelLoader model, int materialId)
	{
		var linear = new Vector3(0.5f);
		if (model.MaterialPbr.TryGetValue(materialId, out var pbr))
		{
			var f = pbr.BaseColorFactor;
			linear = new Vector3(
				f.X > 1e-3f ? pbr.AverageBaseColor.X / f.X : pbr.AverageBaseColor.X,
				f.Y > 1e-3f ? pbr.AverageBaseColor.Y / f.Y : pbr.AverageBaseColor.Y,
				f.Z > 1e-3f ? pbr.AverageBaseColor.Z / f.Z : pbr.AverageBaseColor.Z);
		}

		byte r = EncodeSrgb(linear.X);
		byte g = EncodeSrgb(linear.Y);
		byte b = EncodeSrgb(linear.Z);

		const int size = ModelLoader.AlbedoTileSize;
		var tile = new byte[size * size * 4];
		for (int i = 0; i < tile.Length; i += 4)
		{
			tile[i] = r;
			tile[i + 1] = g;
			tile[i + 2] = b;
			tile[i + 3] = 255;
		}

		return tile;
	}

	private static byte EncodeSrgb(float linear) =>
		(byte)Math.Clamp((int)(MathF.Pow(Math.Max(linear, 0f), 1f / 2.2f) * 255f + 0.5f), 0, 255);

	/// <summary>Caller must first reset the SSR trace slots via SetHitTextures(null, null).</summary>
	public void Dispose()
	{
		_atlas?.Release();
		_atlas = null;

		if (_tileTextures != null)
		{
			foreach (var tile in _tileTextures)
			{
				tile?.Release();
			}

			_tileTextures = null;
		}
	}
}
