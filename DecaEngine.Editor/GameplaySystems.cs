using System.Numerics;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;

namespace DecaEngine.Editor.ECS
{
	/// <summary>
	/// Play-Mode-only system that spins every entity with a <see cref="RotateComponent"/> around its
	/// configured axis. Registered into a dedicated <see cref="SystemRoot"/> that InspectorWindow only
	/// updates while Play Mode is running (see <see cref="InspectorWindow.Play"/>).
	/// </summary>
	public class RotateSystem : QuerySystem<RotateComponent, Rotation>
	{
		protected override void OnUpdate()
		{
			float deltaTime = Tick.deltaTime;
			Query.ForEachEntity((ref RotateComponent rotate, ref Rotation rotation, Entity entity) =>
			{
				if (rotate.DegreesPerSecond == 0f || rotate.Axis == Vector3.Zero)
				{
					return;
				}
				var angle = rotate.DegreesPerSecond * (MathF.PI / 180f) * deltaTime;
				var delta = Quaternion.CreateFromAxisAngle(Vector3.Normalize(rotate.Axis), angle);
				rotation.value = Quaternion.Normalize(rotation.value * delta);
			});
		}
	}

	/// <summary>
	/// Play-Mode-only система движения по окружности ПРЯМЫМ ЗАДАНИЕМ ТРАНСФОРМА (см.
	/// <see cref="CircleMoveComponent"/>).
	///
	/// Ведёт только персонажей БЕЗ <see cref="CharacterBodyComponent"/>. У остальных позицию задаёт
	/// тело в физическом мире сцены (CharacterMotionDriver), и запись сюда же перетирала бы результат
	/// симуляции - персонаж проходил бы сквозь стены, имея при этом честную капсулу и честные
	/// контакты, что диагностируется на порядок хуже, чем отсутствие физики вовсе.
	///
	/// Позиция считается ИЗ ФАЗЫ, а не приращением к текущей позиции. Приращение накапливало бы
	/// ошибку шага: за минуту на 60 FPS это 3600 сложений, и круг превращается в спираль. Из фазы же
	/// радиус точен по построению, а накапливается только сама фаза - величина, у которой дрейф
	/// означает лишь сдвиг по кругу, а не изменение его формы.
	/// </summary>
	public class CircleMoveSystem : QuerySystem<CircleMoveComponent, Position, Rotation>
	{
		protected override void OnUpdate()
		{
			float deltaTime = Tick.deltaTime;
			Query.ForEachEntity((ref CircleMoveComponent move, ref Position position, ref Rotation rotation,
				Entity entity) =>
			{
				// Нулевой радиус - не «стоять на месте», а деление на ноль в угловой скорости.
				if (!move.Enabled || move.Radius <= 1e-4f || entity.HasComponent<CharacterBodyComponent>())
				{
					return;
				}

				// Угловая скорость выводится из линейной: скорость шага - величина линейная, и связывать
				// её дальше придётся именно с ней (клип Walk/Run).
				move.Angle = CircleMotion.Wrap(move.Angle + move.Speed / move.Radius * deltaTime);

				position.value = CircleMotion.PointAt(move, move.Angle);

				if (move.FaceMotion)
				{
					rotation.value = CircleMotion.FacingFor(move, CircleMotion.TangentAt(move, move.Angle));
				}
			});
		}
	}
}
