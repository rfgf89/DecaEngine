using System;
using System.Collections.Generic;
using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Core;

namespace DecaEngine.Graphics.ProbeGi;

/// <summary>
/// Набор base color текстур сцены для текстурного альбедо RT-хитов SSR (см.
/// SsrPassResources.SetHitTextures). Строится из <see cref="ProbeInstancedGeometry.HitTextureKeys"/>
/// поверх ЖИВЫХ ModelLoader-ов (ключи переживают дисковый кеш BVH, GPU-объекты - нет) и живёт
/// рядом с accel-ом: пересоздание accel-а пересоздаёт и набор.
///
/// Два режима потребления:
///   - атлас (<see cref="GetAtlas"/>) - Texture2DArray из плиток 128² на материал
///     (ModelLoader.MaterialAlbedoTile; без CPU-пикселей - плитка среднего цвета текстуры).
///     Владение атласом здесь: перед Dispose вызывающий ОБЯЗАН вернуть слоты SSR на плейсхолдер;
///   - bindless (<see cref="GetFullTextures"/>) - полноразмерные GPU-текстуры материалов КАК ЕСТЬ
///     (владение у моделей). Под стримингом слот начинается 1x1-филлером и дорастает с апгрейдами -
///     <see cref="RefreshStreams"/> говорит, когда привязку пора перепушить.
/// </summary>
public sealed class SsrHitTextures : IDisposable
{
	private readonly (ModelLoader Model, int MaterialId)[] _keys;
	private readonly IGraphicsApi _api;
	private IGpuTexture? _atlas;
	private bool _atlasFailed;
	private long _streamStamp;

	/// <summary>Промежуточные плитки bindless-режима (по одной на ключ, ленивые): пока стриминг
	/// текстуры не завершён, её слот держит ЭТУ текстуру, а не филлер модели - 1x1 белый филлер
	/// красил бы отражение в белое. Данные - те же, что у слоя атласа.</summary>
	private IGpuTexture?[]? _tileTextures;

	private SsrHitTextures(IGraphicsApi api, (ModelLoader Model, int MaterialId)[] keys)
	{
		_api = api;
		_keys = keys;
		_streamStamp = ComputeStreamStamp();
	}

	/// <summary>null - у геометрии нет текстурных ключей (или индексы моделей не сшиваются со
	/// списком - геометрия из чужого кеша), текстурный режим честно остаётся на потриугольном
	/// альбедо.</summary>
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

	/// <summary>Атлас плиток (ленивая сборка, кешируется). null - бэкенд без Texture2DArray.</summary>
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

	/// <summary>Живые полноразмерные текстуры для bindless-массива, в порядке индексов. null-слот
	/// (материал без привязки - не должно случаться, бейкер проверял) добьёт плейсхолдером сам
	/// SetHitTextures.
	///
	/// Стримовая текстура биндится ТОЛЬКО завершённой (Completed): промежуточные ступени стример
	/// ЗАМЕНЯЕТ, а старую освобождает через считанные тики (ModelStore._retiredTextures,
	/// RetireTicks = 8), пере-биндя лишь SRB из СВОЕГО списка stream.Bindings - SRB SSR-трейса в
	/// нём нет, и его дескриптор протухал («невалидные текстуры» в отражениях). Финальная текстура
	/// больше не заменяется - её держать безопасно; до неё слот стоит на стабильном 1x1-филлере
	/// binding.Texture (живёт всё время жизни модели).</summary>
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
				// Не стримится - текстура создана целиком и живёт со своей моделью.
				textures[i] = binding.Texture;
				continue;
			}

			if (binding.Stream is { Completed: true, Texture: not null })
			{
				textures[i] = binding.Stream.Texture;
				continue;
			}

			// Стриминг ещё идёт - показываем СВОЮ плитку (правильный средний цвет материала
			// вместо белого 1x1-филлера модели), она переживёт все ступени апгрейда.
			_tileTextures[i] ??= _api.CreateTexture2DWithMips($"SSR HitTex Tile {i}",
				new[] { TilePixels(model, materialId) },
				ModelLoader.AlbedoTileSize, ModelLoader.AlbedoTileSize);
			textures[i] = _tileTextures[i];
		}

		return textures;
	}

	/// <summary>true - какой-то из стримов ДОЗРЕЛ с прошлой проверки: bindless-привязку пора
	/// перепушить (слот переезжает с филлера на финальную текстуру). Атласу безразлично.</summary>
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

	/// <summary>Человекочитаемый дамп ключей набора - диагностика «какой индекс какой текстурой
	/// красится» (CLI-стенд и консоль редактора).</summary>
	public IEnumerable<string> DescribeKeys()
	{
		for (int i = 0; i < _keys.Length; i++)
		{
			var (model, materialId) = _keys[i];
			string state;
			if (!model.MaterialBaseColor.TryGetValue(materialId, out var binding))
			{
				state = "НЕТ ПРИВЯЗКИ";
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

	/// <summary>Пиксели плитки материала: честный даунсемпл base color текстуры, если CPU-пиксели
	/// были живы при загрузке, иначе - сплошной средний цвет.</summary>
	private static byte[] TilePixels(ModelLoader model, int materialId) =>
		model.MaterialAlbedoTile.TryGetValue(materialId, out var tile) ? tile : SolidTile(model, materialId);

	/// <summary>Плитка сплошного среднего цвета ТЕКСТУРЫ (без фактора - его умножает шейдер):
	/// путь стриминга/cooked, где CPU-пикселей для честной плитки не было. AverageBaseColor
	/// хранится уже умноженным на фактор - делим назад с гардом от нуля.</summary>
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

	/// <summary>Отпускает атлас. Вызывающий ОБЯЗАН сперва вернуть слоты SSR-трейса на плейсхолдер
	/// (SetHitTextures(null, null)) - SRB иначе держал бы мёртвый view.</summary>
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
