using System;
using System.Numerics;

namespace DecaEngine.Scene
{
	/// <summary>Circle-motion geometry and steering shared by the direct-transform path
	/// (CircleMoveSystem, Play Mode) and the physics path (CharacterMotionDriver); one copy so
	/// both paths trace the same circle and GameplayProbe validates both against it.</summary>
	public static class CircleMotion
	{
		/// <summary>Return-to-circle gain in 1/s; in inverse seconds (not a speed fraction) so
		/// the radius-error decay time is independent of run speed.</summary>
		public const float RadialGain = 4f;

		/// <summary>Point on the circle at the given phase; phase runs from +X toward +Z.</summary>
		public static Vector3 PointAt(in CircleMoveComponent move, float angle) => new(
			move.Center.X + move.Radius * MathF.Cos(angle),
			move.Center.Y,
			move.Center.Z + move.Radius * MathF.Sin(angle));

		/// <summary>Unit tangent at the phase, flipped by the sign of Speed.</summary>
		public static Vector3 TangentAt(in CircleMoveComponent move, float angle)
		{
			float direction = move.Speed >= 0f ? 1f : -1f;
			return new Vector3(-MathF.Sin(angle) * direction, 0f, MathF.Cos(angle) * direction);
		}

		/// <summary>Phase of a point about the center: atan2(z, x), inverse of PointAt.</summary>
		public static float AngleOf(in CircleMoveComponent move, Vector3 position) =>
			MathF.Atan2(position.Z - move.Center.Z, position.X - move.Center.X);

		/// <summary>Wraps phase into one turn; unbounded phase loses float precision over time.</summary>
		public static float Wrap(float angle) => MathF.IEEERemainder(angle, MathF.Tau);

		/// <summary>Steering velocity for a PHYSICS body, computed from its real position (not the
		/// integrated phase, which ignores obstacles). Result length is always |Speed| except at
		/// the degenerate center, so step speed stays tied to the animation clip.</summary>
		public static Vector3 SteerVelocity(in CircleMoveComponent move, Vector3 position, out float angle)
		{
			float dx = position.X - move.Center.X;
			float dz = position.Z - move.Center.Z;
			float distance = MathF.Sqrt(dx * dx + dz * dz);

			// At the exact center "outward" is undefined; any picked radius would be a random jump.
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

			// Blend directions and renormalize; clamp to 1 = worst case walk straight to the circle.
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

		/// <summary>Rotation bringing the model's forward to the given direction. Yaw is
		/// atan2(x, z): rotating +Z around +Y by a yields (sin a, 0, cos a). Vertical or zero
		/// directions give yaw 0, not NaN.</summary>
		public static Quaternion FacingFor(in CircleMoveComponent move, Vector3 direction) =>
			FacingFor(move.Forward, direction);

		/// <summary>Same for any motion script: facing is a model property, not a circle one.</summary>
		public static Quaternion FacingFor(Vector3 modelForward, Vector3 direction) =>
			Quaternion.CreateFromAxisAngle(Vector3.UnitY,
				MathF.Atan2(direction.X, direction.Z) - MathF.Atan2(modelForward.X, modelForward.Z));
	}
}
