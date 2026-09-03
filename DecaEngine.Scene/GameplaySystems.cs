using System.Numerics;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;

namespace DecaEngine.Scene
{
	/// <summary>Play-Mode-only system spinning entities with <see cref="RotateComponent"/> around their axis.</summary>
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

	/// <summary>Play-Mode-only circular motion via direct transform writes; skips entities with
	/// <see cref="CharacterBodyComponent"/> (physics owns those), and derives position from phase,
	/// not per-step increments, so error cannot accumulate into a spiral.</summary>
	public class CircleMoveSystem : QuerySystem<CircleMoveComponent, Position, Rotation>
	{
		protected override void OnUpdate()
		{
			float deltaTime = Tick.deltaTime;
			Query.ForEachEntity((ref CircleMoveComponent move, ref Position position, ref Rotation rotation,
				Entity entity) =>
			{
				// Zero radius would divide by zero in the angular velocity, not "stand still".
				if (!move.Enabled || move.Radius <= 1e-4f || entity.HasComponent<CharacterBodyComponent>())
				{
					return;
				}

				// Speed is linear (m/s); angular velocity is derived so Walk/Run clips can bind to it.
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
