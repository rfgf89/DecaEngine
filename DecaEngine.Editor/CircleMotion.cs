using System;
using System.Numerics;

namespace DecaEngine.Editor.ECS
{
	/// <summary>
	/// Геометрия и рулевое движения по кругу (см. <see cref="CircleMoveComponent"/>) - ОДНА копия на
	/// оба пути: и на прямое задание трансформа (<see cref="CircleMoveSystem"/>, Play Mode), и на
	/// физический (CharacterMotionDriver, капсула в мире сцены).
	///
	/// Общий код здесь не ради краткости. Пути живут в разных сборочных слоях и запускаются в разные
	/// моменты кадра, и разъехавшись, они дали бы два РАЗНЫХ круга у одного и того же компонента:
	/// персонаж менял бы траекторию от того, есть в сцене физика или нет. Плюс проверяется тогда
	/// тоже одно - см. GameplayProbe, который гоняет обе ветки против этих же функций.
	/// </summary>
	public static class CircleMotion
	{
		/// <summary>
		/// Сила возврата на окружность, 1/с. Физическое тело сходит с круга постоянно - его толкают
		/// контакты, ступени, собственная инерция, - и без возврата оно после первого же столкновения
		/// уезжает по касательной навсегда.
		///
		/// Задаётся в обратных секундах, а не в долях скорости: смысл величины - «за сколько времени
		/// ошибка радиуса схлопывается», и от скорости бега она не зависит.
		/// </summary>
		public const float RadialGain = 4f;

		/// <summary>Точка на окружности в заданной фазе. Фаза отсчитывается от +X в сторону +Z.</summary>
		public static Vector3 PointAt(in CircleMoveComponent move, float angle) => new(
			move.Center.X + move.Radius * MathF.Cos(angle),
			move.Center.Y,
			move.Center.Z + move.Radius * MathF.Sin(angle));

		/// <summary>Единичная касательная в фазе, развёрнутая знаком скорости.</summary>
		public static Vector3 TangentAt(in CircleMoveComponent move, float angle)
		{
			float direction = move.Speed >= 0f ? 1f : -1f;
			return new Vector3(-MathF.Sin(angle) * direction, 0f, MathF.Cos(angle) * direction);
		}

		/// <summary>Фаза точки относительно центра. Обратная к <see cref="PointAt"/>: угол
		/// отсчитывается от +X в сторону +Z, то есть atan2(z, x), а не (x, z).</summary>
		public static float AngleOf(in CircleMoveComponent move, Vector3 position) =>
			MathF.Atan2(position.Z - move.Center.Z, position.X - move.Center.X);

		/// <summary>Сворачивает фазу в один оборот. Без этого за час непрерывной ходьбы она вырастает
		/// настолько, что float перестаёт различать соседние кадры, и движение начинает дёргаться.</summary>
		public static float Wrap(float angle) => MathF.IEEERemainder(angle, MathF.Tau);

		/// <summary>
		/// Рулевая скорость для ФИЗИЧЕСКОГО тела: куда и с какой скоростью его гнать, чтобы оно шло по
		/// кругу из того места, где оно сейчас находится.
		///
		/// Считается от РЕАЛЬНОЙ позиции тела, а не от накопленной фазы, и это принципиально. Фаза,
		/// проинтегрированная временем, - это положение тела в мире БЕЗ препятствий; тело, упёршееся в
		/// ступень, отстаёт от неё, и через пару секунд «цель» оказывается на другой стороне круга.
		/// Персонаж после такого либо телепортируется, либо идёт к цели напрямик через середину круга.
		///
		/// Длина результата всегда равна |Speed| (кроме вырожденного центра): скорость шага дальше
		/// связывается с анимационным клипом, и добавка на возврат к радиусу означала бы, что
		/// персонаж, сошедший с круга, ещё и скользит ногами.
		/// </summary>
		public static Vector3 SteerVelocity(in CircleMoveComponent move, Vector3 position, out float angle)
		{
			float dx = position.X - move.Center.X;
			float dz = position.Z - move.Center.Z;
			float distance = MathF.Sqrt(dx * dx + dz * dz);

			// Тело ровно в центре: направления «наружу» не существует. Гнать его в этом случае некуда,
			// и любой выбранный наугад радиус был бы просто скачком в случайную сторону.
			if (distance < 1e-4f)
			{
				angle = move.Angle;
				return Vector3.Zero;
			}

			angle = MathF.Atan2(dz, dx);

			float speed = MathF.Abs(move.Speed);
			float direction = move.Speed >= 0f ? 1f : -1f;

			var radial = new Vector2(dx / distance, dz / distance);
			var tangent = new Vector2(-radial.Y * direction, radial.X * direction);

			// Доля возврата, а не добавка к скорости: смешивание идёт направлениями, и результат потом
			// нормируется. Потолок в единицу означает «в худшем случае идти прямо к окружности».
			float correction = Math.Clamp((move.Radius - distance) * RadialGain / MathF.Max(speed, 1e-4f), -1f, 1f);

			var blended = tangent + radial * correction;
			float length = blended.Length();

			if (length < 1e-4f)
			{
				return Vector3.Zero;
			}

			blended *= speed / length;
			return new Vector3(blended.X, 0f, blended.Y);
		}

		/// <summary>
		/// Поворот, приводящий «вперёд» МОДЕЛИ к заданному направлению (см.
		/// <see cref="CircleMoveComponent.Forward"/>). Рыскание - это atan2(x, z), а не (z, x): поворот
		/// вокруг +Y на угол a переводит +Z в (sin a, 0, cos a).
		///
		/// У вертикального или нулевого направления atan2(0, 0) даёт ноль, то есть поведение по
		/// умолчанию, а не NaN.
		/// </summary>
		public static Quaternion FacingFor(in CircleMoveComponent move, Vector3 direction) =>
			FacingFor(move.Forward, direction);

		/// <summary>То же для любого скрипта движения: доворот - свойство модели, а не круга, и игрок
		/// (<see cref="PlayerMoveComponent"/>) пользуется ровно той же формулой.</summary>
		public static Quaternion FacingFor(Vector3 modelForward, Vector3 direction) =>
			Quaternion.CreateFromAxisAngle(Vector3.UnitY,
				MathF.Atan2(direction.X, direction.Z) - MathF.Atan2(modelForward.X, modelForward.Z));
	}
}
