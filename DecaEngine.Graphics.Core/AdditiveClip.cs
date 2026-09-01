using System;
using System.Numerics;

namespace DecaEngine.Graphics;

/// <summary>
/// Построение ДЕЛЬТА-клипа для аддитивного бленда - портированный AdditiveAnimationBuilder ozz:
/// каждый ключ переписывается отклонением от ОПОРЫ - первого ключа своей же дорожки (трансляция -
/// разностью, поворот - Conjugate(опора)*значение, масштаб - отношением). Такой клип хранит не
/// позу, а «на сколько поза отклоняется от нейтрали», и в BlendingJob идёт аддитивным слоем -
/// суммируется поверх любой базы, не стирая её (в отличие от обычного бленда-усреднения).
///
/// Специально авторский клип не нужен: дельтой становится ЛЮБОЙ обычный клип - Survey лисы минус
/// его первый кадр даёт «поводит головой и ушами», применимое поверх ходьбы и бега.
/// </summary>
public static class AdditiveClip
{
	public static PreparedAnimation Build(PreparedAnimation source, PreparedSkeleton skeleton)
	{
		var tracks = new JointTrack[skeleton.JointCount];

		for (int joint = 0; joint < tracks.Length; joint++)
		{
			var sourceTrack = joint < source.Tracks.Length ? source.Tracks[joint] : null;
			var track = new JointTrack();
			tracks[joint] = track;

			// Пустой канал дельты обязан быть ЕДИНИЦЕЙ, и полагаться на досев шима нельзя: тот
			// сеет rest-позой (правильно для обычных клипов - там пустой канал значит «bind»), а
			// rest-поза, прочитанная КАК ДЕЛЬТА, сдвигает кость на всю её bind-трансформацию.
			// Поэтому каждый канал каждого джойнта заполняется явно - тождественным ключом, если
			// дорожки нет.

			if (sourceTrack != null && sourceTrack.TranslationTimes.Length > 0)
			{
				var reference = sourceTrack.Translations[0];
				track.TranslationTimes = (float[])sourceTrack.TranslationTimes.Clone();
				track.Translations = new Vector3[sourceTrack.Translations.Length];

				for (int i = 0; i < track.Translations.Length; i++)
				{
					track.Translations[i] = sourceTrack.Translations[i] - reference;
				}
			}
			else
			{
				track.TranslationTimes = [0f];
				track.Translations = [Vector3.Zero];
			}

			if (sourceTrack != null && sourceTrack.RotationTimes.Length > 0)
			{
				var reference = Quaternion.Conjugate(Quaternion.Normalize(sourceTrack.Rotations[0]));
				track.RotationTimes = (float[])sourceTrack.RotationTimes.Clone();
				track.Rotations = new Quaternion[sourceTrack.Rotations.Length];

				for (int i = 0; i < track.Rotations.Length; i++)
				{
					track.Rotations[i] = Quaternion.Normalize(reference * sourceTrack.Rotations[i]);
				}
			}
			else
			{
				track.RotationTimes = [0f];
				track.Rotations = [Quaternion.Identity];
			}

			if (sourceTrack != null && sourceTrack.ScaleTimes.Length > 0)
			{
				var reference = sourceTrack.Scales[0];
				track.ScaleTimes = (float[])sourceTrack.ScaleTimes.Clone();
				track.Scales = new Vector3[sourceTrack.Scales.Length];

				for (int i = 0; i < track.Scales.Length; i++)
				{
					var value = sourceTrack.Scales[i];
					track.Scales[i] = new Vector3(
						SafeRatio(value.X, reference.X),
						SafeRatio(value.Y, reference.Y),
						SafeRatio(value.Z, reference.Z));
				}
			}
			else
			{
				track.ScaleTimes = [0f];
				track.Scales = [Vector3.One];
			}
		}

		return new PreparedAnimation
		{
			Name = source.Name + "#additive",
			Duration = source.Duration,
			Tracks = tracks,
		};
	}

	private static float SafeRatio(float value, float reference) =>
		MathF.Abs(reference) > 1e-8f ? value / reference : 1f;
}
