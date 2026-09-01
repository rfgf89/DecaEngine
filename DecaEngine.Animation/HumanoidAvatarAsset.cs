using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DecaEngine.Core;

namespace DecaEngine.Animation;

/// <summary>
/// Чтение и запись аватара рядом с моделью: <c>Fox.glb</c> -> <c>Fox.avatar.json</c>.
///
/// Отдельным файлом рядом, а НЕ внутри .dmdl: аватар - авторские данные, их правят руками, смотрят
/// в диффе и мержат, а .dmdl - производное от модели, которое пересобирается кешем при любом
/// изменении опций загрузки. Аватар, запечённый в кук, терялся бы на каждой инвалидации кеша, и
/// разметка рига жила бы ровно до следующей смены версии формата.
///
/// Формат - словарь «слот -> имя кости», а не массив: при добавлении нового слота (пальцы, глаза,
/// челюсть) старые файлы обязаны читаться дальше, а массив с фиксированными позициями это ломает
/// молча - слоты сдвигаются, и разметка съезжает на соседнюю кость.
/// </summary>
public static class HumanoidAvatarAsset
{
	private static readonly JsonSerializerOptions Options = new()
	{
		WriteIndented = true,
	};

	/// <summary>Путь к аватару по пути модели. Расширение целиком, а не суффикс: так файл виден
	/// рядом с моделью и сортируется вместе с ней.</summary>
	public static string PathFor(string modelPath) =>
		Path.ChangeExtension(modelPath, ".avatar.json");

	public static bool Exists(string modelPath) =>
		!string.IsNullOrEmpty(modelPath) && File.Exists(PathFor(modelPath));

	/// <summary>Кость референсной позы на диске. Массивами, а не объектами с полями x/y/z: файл
	/// правят руками, и строка <c>"p":[0,0.12,0]</c> читается, а три строки на компоненту - нет.</summary>
	private sealed class ReferenceBone
	{
		public float[]? P { get; set; }
		public float[]? R { get; set; }
		public float[]? S { get; set; }
	}

	private sealed class AvatarFile
	{
		public Dictionary<string, string>? Bones { get; set; }
		public Dictionary<string, ReferenceBone>? Reference { get; set; }
	}

	public static void Save(HumanoidAvatar avatar, string modelPath)
	{
		var file = new AvatarFile { Bones = new Dictionary<string, string>() };

		foreach (var info in HumanoidBones.All)
		{
			// Пустые слоты в файл не пишутся: файл читается человеком, и два десятка пустых строк в
			// нём скрывают те несколько, что действительно назначены.
			if (avatar.IsAssigned(info.Bone))
			{
				file.Bones[info.Bone.ToString()] = avatar[info.Bone];
			}
		}

		if (avatar.HasReferencePose)
		{
			file.Reference = new Dictionary<string, ReferenceBone>(StringComparer.Ordinal);

			foreach (var pair in avatar.ReferenceLocals)
			{
				var local = pair.Value;
				file.Reference[pair.Key] = new ReferenceBone
				{
					P = [local.position.X, local.position.Y, local.position.Z],
					R = [local.rotation.X, local.rotation.Y, local.rotation.Z, local.rotation.W],
					S = [local.scale.X, local.scale.Y, local.scale.Z],
				};
			}
		}

		string path = PathFor(modelPath);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, JsonSerializer.Serialize(file, Options));
	}

	/// <summary>Читает аватар модели; null - файла нет или он не читается. Неизвестные слоты
	/// ПРОПУСКАЮТСЯ, а не роняют чтение: файл, записанный будущей версией редактора, обязан
	/// открываться в текущей, пусть и не целиком.</summary>
	public static HumanoidAvatar? Load(string modelPath)
	{
		string path = PathFor(modelPath);

		if (!File.Exists(path))
		{
			return null;
		}

		try
		{
			string text = File.ReadAllText(path);
			var avatar = new HumanoidAvatar();

			var file = JsonSerializer.Deserialize<AvatarFile>(text);

			// СТАРЫЙ формат - плоская карта «слот -> кость», без обёртки. Читается как есть:
			// аватары, размеченные до появления референсной позы, обязаны открываться дальше, а не
			// молча превращаться в пустые (это выглядело бы как «разметка слетела сама»).
			var bones = file?.Bones;
			if (bones == null)
			{
				bones = JsonSerializer.Deserialize<Dictionary<string, string>>(text);
			}

			if (bones == null)
			{
				return null;
			}

			foreach (var pair in bones)
			{
				if (Enum.TryParse<HumanoidBone>(pair.Key, out var bone) && bone < HumanoidBone.Count)
				{
					avatar[bone] = pair.Value;
				}
			}

			if (file?.Reference != null)
			{
				foreach (var pair in file.Reference)
				{
					var value = pair.Value;
					if (value?.P is not { Length: 3 } p || value.R is not { Length: 4 } r ||
						value.S is not { Length: 3 } s)
					{
						// Обрезанная запись пропускается, а не подставляется нулями: кость с нулевым
						// масштабом схлопывает всё поддерево, и такая «поза» хуже её отсутствия.
						continue;
					}

					avatar.ReferenceLocals[pair.Key] = new Transform
					{
						position = new System.Numerics.Vector3(p[0], p[1], p[2]),
						rotation = new System.Numerics.Quaternion(r[0], r[1], r[2], r[3]),
						scale = new System.Numerics.Vector3(s[0], s[1], s[2]),
					};
				}
			}

			return avatar;
		}
		catch (Exception)
		{
			// Битый или чужой json - это «аватара нет», а не падение редактора: модель обязана
			// открыться и без разметки.
			return null;
		}
	}
}
