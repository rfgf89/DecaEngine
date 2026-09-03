using System;
using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Editor.ECS;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using Hexa.NET.ImGui;
using DecaEngine.Scene;

namespace DecaEngine.Editor
{
    public class FlyCameraSystem : QuerySystem<Position, Rotation, CameraComponent>
    {
        private readonly DevicePull _devicePull;
        private InputAction _moveActionW;
        private InputAction _moveActionA;
        private InputAction _moveActionS;
        private InputAction _moveActionD;
        private InputAction _moveActionCtrl;
        private InputAction _moveActionSpace;
        private InputAction _lookActionRightMouse;
        private InputAction _lookActionMouseDelta;

        private Vector2 _mouseDelta;
        private bool _isRightMouseDown;

        private float _yaw = 0f;
        private float _pitch = 0f;

        public float MoveSpeed { get; set; } = 10f;
        public float LookSpeed { get; set; } = 0.005f;

        private readonly Entity[] _entities;

        public FlyCameraSystem(Entity[] entities, DevicePull devicePull)
        {
            _devicePull = devicePull;
            _entities = entities;
        }

        protected override void OnUpdate()
        {
            float deltaTime = Tick.deltaTime;

            if (_moveActionW == null)
            {
                var keyboard = _devicePull.GetFirstInputDevice(DevicePull.DeviceType.Keyboard);
                var mouse = _devicePull.GetFirstInputDevice(DevicePull.DeviceType.Mouse);

                if (keyboard != null && mouse != null)
                {
                    _moveActionW = new InputAction();
                    _moveActionA = new InputAction();
                    _moveActionS = new InputAction();
                    _moveActionD = new InputAction();
                    _moveActionCtrl = new InputAction();
                    _moveActionSpace = new InputAction();

                    _lookActionRightMouse = new InputAction();
                    _lookActionMouseDelta = new InputAction();

                    keyboard.AddListener(KeyboardKeys.Keys.W, _moveActionW);
                    keyboard.AddListener(KeyboardKeys.Keys.A, _moveActionA);
                    keyboard.AddListener(KeyboardKeys.Keys.S, _moveActionS);
                    keyboard.AddListener(KeyboardKeys.Keys.D, _moveActionD);
                    keyboard.AddListener(KeyboardKeys.Keys.LeftCtrl, _moveActionCtrl);
                    keyboard.AddListener(KeyboardKeys.Keys.Space, _moveActionSpace);

                    mouse.AddListener(InputDevice.MouseEvent.RightButton, _lookActionRightMouse);
                    mouse.AddListener(InputDevice.MouseEvent.PositionDelta, _lookActionMouseDelta);

                    _lookActionRightMouse.onPressed += () => _isRightMouseDown = true;
                    _lookActionRightMouse.onReleased += () => _isRightMouseDown = false;

                    _lookActionMouseDelta.onPerform += () =>
                    {
                        if (_isRightMouseDown)
                        {
                            _mouseDelta = _lookActionMouseDelta.Read<Vector2>();
                        }
                    };
                }

                return;
            }

            // This system reads input directly from the devices, bypassing ImGui, so it must gate
            // on Game View focus. io.WantCaptureKeyboard cannot be used: NavEnableKeyboard makes
            // it true for any focused window, including Game View itself.
            if (!GameViewWindow.IsAnyFocused || ImGui.GetIO().WantTextInput)
            {
                _mouseDelta = Vector2.Zero;
                return;
            }

            Vector3 moveInput = Vector3.Zero;
            {
                moveInput.Z += _moveActionW.Read<float>();
                moveInput.Z -= _moveActionS.Read<float>();
                moveInput.X -= _moveActionA.Read<float>();
                moveInput.X += _moveActionD.Read<float>();
                moveInput.Y -= _moveActionCtrl.Read<float>();
                moveInput.Y += _moveActionSpace.Read<float>();
            }

            if (moveInput.LengthSquared() > 0)
            {
                moveInput = Vector3.Normalize(moveInput);
            }

            foreach (var entity in _entities)
            {
                ref var position = ref entity.GetComponent<Position>();
                ref var rotation = ref entity.GetComponent<Rotation>();

                if (_isRightMouseDown)
                {
                    _yaw -= _mouseDelta.X * LookSpeed;
                    _pitch -= _mouseDelta.Y * LookSpeed;

                    _pitch = Math.Clamp(_pitch, -MathF.PI / 2f + 0.01f, MathF.PI / 2f - 0.01f);

                    rotation.value = Quaternion.CreateFromYawPitchRoll(_yaw, _pitch, 0f);
                }

                if (moveInput.LengthSquared() > 0)
                {
                    Vector3 forward = Vector3.Transform(Vector3.UnitZ, rotation.value);
                    Vector3 right = Vector3.Transform(Vector3.UnitX, rotation.value);
                    Vector3 up = Vector3.Transform(Vector3.UnitY, rotation.value);

                    Vector3 moveDir = (forward * moveInput.Z) + (right * moveInput.X) + (up * moveInput.Y);
                    position.value += moveDir * MoveSpeed * deltaTime;
                }
            }
            
            _mouseDelta = Vector2.Zero;
        }
    }
}