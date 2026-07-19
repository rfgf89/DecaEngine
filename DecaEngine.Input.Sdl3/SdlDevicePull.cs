using SDL;
using System.Collections.Generic;
using System;

public class SdlDevicePull : DevicePull, IPerformSdlEvent
{
	private readonly Dictionary<DeviceType, Func<uint, IInputDevice>> _inputDevicesFactory = new()
	{
		{ DeviceType.Mouse, deviceId => new MouseSdlDevice { deviceId = deviceId } },
		{ DeviceType.Keyboard, deviceId => new KeyboardSdlDevice { deviceId = deviceId } }
	};

	public void PerformSdlEvent(SDL_Event sdlEvent)
	{
		var keyboards = SDL3.SDL_GetKeyboards();

		if (keyboards is { Count: > 0 })
		{
			for (var i = 0; i < keyboards.Count; i++)
			{
				TryingDeviceAdded(DeviceType.Keyboard, (uint)keyboards[i]);
			}
		}
		else if (_inputDevices.TryGetValue(DeviceType.Keyboard, out var keyboardsList))
		{
			keyboardsList?.Clear();
		}

		if (SDL3.SDL_HasMouse())
		{
			// First let's get the generic mouse ID which is usually 1, or something specific if it's an event
			uint defaultMouseId = 1;
			var mice = SDL3.SDL_GetMice();
			if (mice is { Count: > 0 })
			{
				defaultMouseId = (uint)mice[0];
			}

			uint mouseId = sdlEvent.type switch
			{
				(uint)SDL_EventType.SDL_EVENT_MOUSE_MOTION => (uint)sdlEvent.motion.which,
				(uint)SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN => (uint)sdlEvent.button.which,
				(uint)SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP => (uint)sdlEvent.button.which,
				(uint)SDL_EventType.SDL_EVENT_MOUSE_WHEEL => (uint)sdlEvent.wheel.which,
				_ => defaultMouseId
			};

			if (mouseId == 0)
			{
				mouseId = defaultMouseId;
			}
			if (mouseId == 0)
			{
				mouseId = 1; // Super fallback
			}

			TryingDeviceAdded(DeviceType.Mouse, mouseId);
		}
		else if (_inputDevices.TryGetValue(DeviceType.Mouse, out var mouseList))
		{
			mouseList?.Clear();
		}

		foreach (var inputDeviceList in _inputDevices.Values)
		{
			foreach (var inputDevice in inputDeviceList)
			{
				if (inputDevice is IPerformSdlEvent performSdlEvent)
				{
					// We'll roll back the aggressive ID checking because SDL3 IDs might be somewhat
					// different for some events or some default states (like text input vs key down).
					performSdlEvent.PerformSdlEvent(sdlEvent);
				}
			}
		}
	}

	private void TryingDeviceAdded(DeviceType deviceType, uint deviceId)
	{
		if (deviceId == 0)
		{
			return;
		}

		if (_inputDevices.TryGetValue(deviceType, out var inputDevices))
		{
			if (inputDevices.FindIndex(device => device.deviceId == deviceId) != -1)
			{
				return;
			}
		}
		else
		{
			_inputDevices.Add(deviceType, inputDevices = new List<IInputDevice>());
		}

		inputDevices.Add(_inputDevicesFactory[deviceType].Invoke(deviceId));
		OnDeviceConnected?.Invoke(deviceType);
	}
}