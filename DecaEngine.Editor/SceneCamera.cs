using System;
using System.Numerics;
using Hexa.NET.ImGui;
using Hexa.NET.ImGuizmo;

namespace DecaEngine.Editor
{
	/// <summary>
	/// Полноценная редакторская камера Scene View: полёт (RMB + WASD/QE), орбита вокруг точки
	/// интереса (Alt+ЛКМ), пан вдоль базиса камеры (СКМ), долли колесом и кадрирование (F).
	/// Замена прежней орбитальной камеры <see cref="PrefabSceneViewport"/> (та же орбита у
	/// <see cref="ModelPreviewViewport"/> - правильная, НЕ трогать).
	///
	/// Состояние - eye/yaw/pitch/focusDistance, а не eye/target: цель (<see cref="Target"/>) для
	/// полёта не имеет смысла как независимая переменная (камера смотрит туда, куда повёрнута, а не
	/// на фиксированную точку) - она считается ОТ eye и направления взгляда, focusDistance лишь
	/// определяет, НАСКОЛЬКО ДАЛЕКО эта условная точка интереса (нужна орбите/каскадам probe-GI,
	/// см. PrefabSceneViewport.PollSceneCascadeRecenter).
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

		// Скорость взгляда мышью - тот же коэффициент, что был у прежней орбитальной камеры (rad/px).
		private const float LookSensitivity = 0.01f;

		// Множители WASD-скорости под Shift/Ctrl - стандартная для fly-камер редакторов раскладка.
		private const float SpeedBoost = 4f;
		private const float SpeedSlow = 0.25f;

		// Насколько один "щелчок" колеса меняет базовую скорость полёта (мультипликативно) и на какую
		// долю ТЕКУЩЕЙ дистанции фокуса сдвигает камеру при долли - те же 10%, что у прежнего зума.
		private const float WheelSpeedFactor = 0.1f;
		private const float DollyFactor = 0.1f;

		// Масштаб пана - доля дистанции фокуса на пиксель мыши (та же формула, что у прежнего
		// _distance * 0.001f, но привязана к focusDistance, а не к дистанции орбиты).
		private const float PanFactor = 0.001f;

		private Vector3 _eye;
		private float _yaw;
		private float _pitch;
		private float _focusDistance;
		private float _flySpeed;

		// Защёлки кнопок мыши - тем же приёмом, что у прежней камеры (PrefabSceneViewport.HandleCameraInput):
		// старт только под курсором над вьюпортом, но драг продолжается и за его пределами, иначе
		// быстрый свайп мышью «отпускает» вращение на кромке окна.
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

		/// <summary>Позиция камеры - тот же смысл, что был у _lastEye: под ней рендерился последний
		/// кадр, гизмо и пикинг обязаны использовать её же (см. PrefabSceneViewport).</summary>
		public Vector3 Eye => _eye;

		/// <summary>Направление взгляда (единичный вектор) - используется и WASD-полётом, и
		/// восстановлением Target, и панорамированием (правый/верхний вектора базиса камеры).</summary>
		public Vector3 Forward
		{
			get
			{
				float cp = MathF.Cos(_pitch);
				return new Vector3(-cp * MathF.Sin(_yaw), -MathF.Sin(_pitch), -cp * MathF.Cos(_yaw));
			}
		}

		/// <summary>Условная точка интереса перед камерой - на неё смотрит LookAt (Eye,Target,UnitY) и
		/// вокруг неё вращает Alt+ЛКМ; каскады probe-GI сцены следят именно за ней (см.
		/// PrefabSceneViewport.PollSceneCascadeRecenter - раньше это был _orbitTarget).</summary>
		public Vector3 Target => _eye + Forward * _focusDistance;

		/// <summary>Дистанция до точки интереса - управляет и радиусом орбиты (Alt+ЛКМ), и шагом
		/// долли/пана (дальше от сцены - крупнее шаг, иначе на большом удалении сцены управление было
		/// бы вязким). Кламп - те же границы, что у прежнего _distance.</summary>
		public float FocusDistance
		{
			get => _focusDistance;
			private set => _focusDistance = Math.Clamp(value, MinFocusDistance, MaxFocusDistance);
		}

		/// <summary>Базовая скорость полёта (единиц/сек) - персистится в EditorSettings.SceneCameraSpeed
		/// вызывающей стороной (см. PrefabSceneViewport), меняется колесом мыши при зажатой RMB.</summary>
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
				// Взгляд строго вертикально - правый вектор вырождается (тот же случай, что у тумана
				// в ModelViewportEnvironment.SetCameraTransform).
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

		/// <summary>Сбрасывает камеру к дефолтному ракурсу вокруг мирового нуля - тот же фолбэк, что
		/// был у FrameAll(), пока в сцене ещё нет геометрии (см. PrefabSceneViewport.FrameAll).</summary>
		public void ResetToDefaults()
		{
			_yaw = DefaultYaw;
			_pitch = DefaultPitch;
			FocusDistance = DefaultFocusDistance;
			_eye = ComputeEyeForTarget(Vector3.Zero, _focusDistance, _yaw, _pitch);
		}

		/// <summary>Кадрирует камеру на сферу (center, radius) под заданным полем зрения.
		/// <paramref name="resetAngle"/> - сбросить ли яв/питч к дефолтному «красивому» ракурсу (Frame
		/// All/первое открытие префаба) или сохранить текущее направление взгляда (фокус по F на
		/// выделении - камера должна остаться развёрнутой туда же, куда и смотрела).</summary>
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

		/// <summary>Покадровый ввод камеры - смотр/полёт по RMB, орбита по Alt+ЛКМ, пан по СКМ, долли
		/// колесом. Как и у прежней камеры, обрабатывается ТОЛЬКО пока открыт вьюпорт (см.
		/// PrefabSceneViewport.Render), а защёлки кнопок продолжают драг за пределами наведённого
		/// прямоугольника.</summary>
		public void HandleInput(bool hovered, float deltaTime)
		{
			var io = ImGui.GetIO();

			// Alt+ЛКМ - орбита вокруг Target. !ImGuizmo.IsOver() уступает клик самому гизмо (Maya-style
			// приоритет гизмо над орбитой при наведении на его рукоятки), иначе Alt+клик по стрелке
			// гизмо одновременно и крутил бы камеру, и путал ImGuizmo.
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
				// Target держим НЕПОДВИЖНЫМ (орбита вокруг точки интереса), эту роль в прежней камере
				// играл _orbitTarget - здесь пивот считается ДО поворота и eye пересобирается под него.
				var pivot = Target;
				ApplyLook(io.MouseDelta);
				_eye = ComputeEyeForTarget(pivot, _focusDistance, _yaw, _pitch);
			}
			else if (_looking)
			{
				ApplyLook(io.MouseDelta);
				ApplyFlyMovement(deltaTime, io);

				// Колесо при зажатой RMB - это НЕ долли, а перенастройка базовой скорости полёта
				// (стандартный приём Unity/UE fly-камеры): без него скорость либо слишком вялая рядом с
				// объектом, либо неуправляемо быстрая на просторной сцене, а тянуться к слайдеру
				// настроек на каждый пролёт неудобно.
				if (io.MouseWheel != 0f)
				{
					FlySpeed *= 1f + io.MouseWheel * WheelSpeedFactor;
				}
			}
			else if (_panning)
			{
				var delta = io.MouseDelta;
				var panScale = MathF.Max(0.01f, _focusDistance * PanFactor);
				// Право/верх БАЗИСА КАМЕРЫ, а не мировой Y - раньше пан по мировому Y «плыл» боком при
				// наклонённом взгляде (см. задачу: старый код панорамировал по Vector3.UnitY).
				_eye -= Right * delta.X * panScale;
				_eye += Up * delta.Y * panScale;
			}
			else if (hovered && io.MouseWheel != 0f)
			{
				// Долли вдоль взгляда, шаг - от текущей дистанции фокуса (иначе вблизи объекта шаг
				// колеса был бы слишком грубым, а на удалении сцены - неощутимо медленным).
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
			// Не воровать WASD/QE у текстовых полей (см. задачу) - RMB зажат в момент фокуса на
			// текстовом виджете практически невозможен (курсор занят вводом), но проверка дешёвая и
			// защищает на будущее (например, поле поиска, которое ловит фокус по хоткею без клика).
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
