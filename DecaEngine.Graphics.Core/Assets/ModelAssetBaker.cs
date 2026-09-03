
namespace DecaEngine.Graphics.Assets;

/// <summary>Bakes a prepared model's textures into .dtex and fills in their cache keys.</summary>
internal static class ModelAssetBaker
{
	// Dedupe by (source image, import settings): glTF ORM maps share one image across slots.
	public static void BakeTextures(PreparedModel prepared, AssetCache cache,
		ModelLoadOptions options, CancellationToken cancellationToken)
	{
		cache.EnsureDirectories();

		var bakedByImage = new Dictionary<(SharpGLTF.Schema2.Image, string), string>();

		foreach (var material in prepared.Materials)
		{
			if (material.IsNull)
			{
				continue;
			}

			cancellationToken.ThrowIfCancellationRequested();

			BakeSlot(material.BaseColorTexture, TextureSlotKind.BaseColor);
			BakeSlot(material.MetallicRoughnessTexture, TextureSlotKind.MetallicRoughness);
			BakeSlot(material.NormalTexture, TextureSlotKind.Normal);
			BakeSlot(material.OcclusionTexture, TextureSlotKind.Occlusion);
			BakeSlot(material.ThicknessTexture, TextureSlotKind.Thickness);
			BakeSlot(material.EmissiveTexture, TextureSlotKind.Emissive);
		}

		void BakeSlot(PreparedTexture texture, TextureSlotKind kind)
		{
			if (texture?.Pixels == null || texture.SourceImage == null)
			{
				// Empty slot or streaming mode: no key, so the loader substitutes a filler.
				return;
			}

			var settings = TextureImportSettings.AutoFor(kind, options.MaxTextureSize, options.BakeQuality);
			var dedupeKey = (texture.SourceImage, settings.CacheKey());

			if (bakedByImage.TryGetValue(dedupeKey, out var existingKey))
			{
				texture.CacheKey = existingKey;
				return;
			}

			var encoded = texture.SourceImage.Content.Content;
			if (encoded.IsEmpty)
			{
				return;
			}

			var key = AssetCache.TextureKey(encoded.Span, settings);
			var path = cache.TexturePath(key);

			// Assign the key on BOTH paths: cooked models reference textures only by it.
			texture.CacheKey = key;
			bakedByImage[dedupeKey] = key;

			if (!File.Exists(path))
			{
				// One image at a time: a thread per image holds a full-size copy each.
				var payload = TextureBaker.Bake(texture.Pixels, texture.Width, texture.Height, settings);
				DtexFile.Write(path, payload);

				// Size comes from the baked mip 0: the clamp scales both axes proportionally.
				texture.Width = payload.Width;
				texture.Height = payload.Height;
			}
			else if (DtexFile.TryReadHeader(path, out var header))
			{
				// Same reason: the slot still holds source size, the file holds clamped mip 0.
				texture.Width = header.Width;
				texture.Height = header.Height;
			}
		}
	}

	/// <summary>Checks every .dtex a cooked model references still exists on disk.</summary>
	public static bool AllTexturesPresent(PreparedModel prepared, AssetCache cache)
	{
		foreach (var material in prepared.Materials)
		{
			if (material.IsNull)
			{
				continue;
			}

			if (!SlotPresent(material.BaseColorTexture) ||
				!SlotPresent(material.MetallicRoughnessTexture) ||
				!SlotPresent(material.NormalTexture) ||
				!SlotPresent(material.OcclusionTexture) ||
				!SlotPresent(material.ThicknessTexture) ||
				!SlotPresent(material.EmissiveTexture))
			{
				return false;
			}
		}

		return true;

		bool SlotPresent(PreparedTexture texture) =>
			texture?.CacheKey == null || File.Exists(cache.TexturePath(texture.CacheKey));
	}
}
