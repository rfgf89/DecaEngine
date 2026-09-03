using System;
using System.Numerics;
using Hexa.NET.ImGui;
using Hexa.NET.ImGuizmo;

namespace DecaEngine.Editor
{
	/// <summary>
	/// Scene View editor camera: fly (RMB + WASD/QE), orbit (Alt+LMB), pan (MMB), dolly, frame (F).
	/// </summary>
	public sealed class SceneCamera
	{
		public const float DefaultYaw = -0.6f;
		public const float DefaultPitch = 0.35f;
		public const float DefaultFocusDistance = 8f;
		public const float DefaultFlySpeed = 6f;

		private const float MinFocusDistance = 0.2f;
		private const float MaxFocusDistance = 1500f;
		private const float PitchClamp = 1.5f;
		private const float MinFlySpeed = 0.05f;
		private const float MaxFlySpeed = 500f;

		// rad per mouse pixel.
		private const float LookSensitivity = 0.01f;

		private const float SpeedBoost = 4f;
		private const float SpeedSlow = 0.25f;

		private const float WheelSpeedFactor = 0.1f;
		private const float DollyFactor = 0.1f;

		// Fraction of focus distance per mouse pixel.
		private const float PanFactor = 0.001f;

		private Vector3 _eye;
		private float _yaw;
		private float _pitch;
		private float _focusDistance;
		private float _flySpeed;

		// Latched: a drag starts only over the viewport but continues outside it, so a fast swipe
		// does not drop the rotation at the window edge.
		private bool _looking;
		private bool _panning;
		private bool _orbiting;

		public SceneCamera(float initialFlySpeed = DefaultFlySpeed)
		{
			_yaw = DefaultYaw;
			_pitch = DefaultPitch;
			_focusDistance = DefaultFocusDistance;
			_eye = ComputeEyeForTarget(Vector3.Zero, _focusDistance, _yaw, _pitch);
			FlySpeed = initialFlySpeed;
		}

		/// <summary>Camera position the last frame was rendered from; gizmos and picking must use it.</summary>
		public Vector3 Eye => _eye;

		/// <summary>Unit view direction.</summary>
		public Vector3 Forward
		{
			get
			{
				float cp = MathF.Cos(_pitch);
				return new Vector3(-cp * MathF.Sin(_yaw), -MathF.Sin(_pitch), -cp * MathF.Cos(_yaw));
			}
		}

		/// <summary>Point of interest in front of the camera; orbit pivot and probe-GI cascade center.</summary>
		public Vector3 Target => _eye + Forward * _focusDistance;

		/// <summary>Distance to the point of interest; scales orbit radius and dolly/pan step.</summary>
		public float FocusDistance
		{
			get => _focusDistance;
			private set => _focusDistance = Math.Clamp(value, MinFocusDistance, MaxFocusDistance);
		}

		/// <summary>Base fly speed in units/sec; persisted by the caller in EditorSettings.</summary>
		public float FlySpeed
		{
			get => _flySpeed;
			set => _flySpeed = Math.Clamp(value, MinFlySpeed, MaxFlySpeed);
		}

		private Vector3 Right
		{
			get
			{
				var forward = Forward;
				var right = Vector3.Cross(Vector3.UnitY, forward);
				// Looking straight up or down degenerates the right vector.
				return right.LengthSquared() > 1e-8f ? Vector3.Normalize(right) : Vector3.UnitX;
			}
		}

		private Vector3 Up => Vector3.Normalize(Vector3.Cross(Forward, Right));

		private static Vector3 ComputeEyeForTarget(Vector3 target, float distance, float yaw, float pitch)
		{
			float cp = MathF.Cos(pitch);
			var forward = new Vector3(-cp * MathF.Sin(yaw), -MathF.Sin(pitch), -cp * MathF.Cos(yaw));
			return target - forward * distance;
		}

		/// <summary>Resets the camera to the default angle around world origin.</summary>
		public void ResetToDefaults()
		{
			_yaw = DefaultYaw;
			_pitch = DefaultPitch;
			FocusDistance = DefaultFocusDistance;
			_eye = ComputeEyeForTarget(Vector3.Zero, _focusDistance, _yaw, _pitch);
		}

		/// <summary>Frames the sphere (center, radius) at the given FOV, optionally resetting yaw/pitch.</summary>
		public void Frame(Vector3 center, float radius, float fovDegrees, bool resetAngle)
		{
			if (resetAngle)
			{
				_yaw = DefaultYaw;
				_pitch = DefaultPitch;
			}

			FocusDistance = ModelViewportGeometry.ComputeFramingDistance(MathF.Max(0.05f, radius), fovDegrees);
			_eye = ComputeEyeForTarget(center, _focusDistance, _yaw, _pitch);
		}

		/// <summary>Per-frame camera input; must be called only while the viewport is open.</summary>
		public void HandleInput(bool hovered, float deltaTime)
		{
			var io = ImGui.GetIO();

			// !ImGuizmo.IsOver() yields the click to the gizmo (Maya-style priority over orbit).
			bool altDown = ImGui.IsKeyDown(ImGuiKey.LeftAlt);
			if (hovered && altDown && !ImGuizmo.IsOver() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
			{
				_orbiting = true;
			}
			if (_orbiting && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
			{
				_orbiting = false;
			}

			if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
			{
				_looking = true;
			}
			if (_looking && ImGui.IsMouseReleased(ImGuiMouseButton.Right))
			{
				_looking = false;
			}

			if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Middle))
			{
				_panning = true;
			}
			if (_panning && ImGui.IsMouseReleased(ImGuiMouseButton.Middle))
			{
				_panning = false;
			}

			if (_orbiting)
			{
				// Pivot is captured BEFORE the rotation, then eye is rebuilt around it.
				var pivot = Target;
				ApplyLook(io.MouseDelta);
				_eye = ComputeEyeForTarget(pivot, _focusDistance, _yaw, _pitch);
			}
			else if (_looking)
			{
				ApplyLook(io.MouseDelta);
				ApplyFlyMovement(deltaTime, io);

				// Wheel while RMB is held retunes fly speed, it is not a dolly (Unity/UE convention).
				if (io.MouseWheel != 0f)
				{
					FlySpeed *= 1f + io.MouseWheel * WheelSpeedFactor;
				}
			}
			else if (_panning)
			{
				var delta = io.MouseDelta;
				var panScale = MathF.Max(0.01f, _focusDistance * PanFactor);
				// Camera-basis right/up, not world Y: world Y drifts sideways on a tilted view.
				_eye -= Right * delta.X * panScale;
				_eye += Up * delta.Y * panScale;
			}
			else if (hovered && io.MouseWheel != 0f)
			{
				// Dolly step scales with focus distance to stay usable at any range.
				_eye += Forward * io.MouseWheel * _focusDistance * DollyFactor;
			}
		}

		private void ApplyLook(Vector2 mouseDelta)
		{
			_yaw += mouseDelta.X * LookSensitivity;
			_pitch = Math.Clamp(_pitch + mouseDelta.Y * LookSensitivity, -PitchClamp, PitchClamp);
		}

		private void ApplyFlyMovement(float deltaTime, ImGuiIOPtr io)
		{
			// Do not steal WASD/QE from focused text fields.
			if (io.WantTextInput)
			{
				return;
			}

			float speed = _flySpeed;
			if (ImGui.IsKeyDown(ImGuiKey.LeftShift))
			{
				speed *= SpeedBoost;
			}
			else if (ImGui.IsKeyDown(ImGuiKey.LeftCtrl))
			{
				speed *= SpeedSlow;
			}

			var forward = Forward;
			var right = Right;
			var move = Vector3.Zero;
			if (ImGui.IsKeyDown(ImGuiKey.W)) move += forward;
			if (ImGui.IsKeyDown(ImGuiKey.S)) move -= forward;
			if (ImGui.IsKeyDown(ImGuiKey.D)) move += right;
			if (ImGui.IsKeyDown(ImGuiKey.A)) move -= right;
			if (ImGui.IsKeyDown(ImGuiKey.E)) move += Vector3.UnitY;
			if (ImGui.IsKeyDown(ImGuiKey.Q)) move -= Vector3.UnitY;

			if (move.LengthSquared() > 0f)
			{
				_eye += Vector3.Normalize(move) * speed * deltaTime;
			}
		}
	}
}
