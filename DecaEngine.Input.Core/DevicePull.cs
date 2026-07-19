public class DevicePull
{
	public enum DeviceType
	{
		Mouse,
		Keyboard,
		Gamepad
	}

	protected readonly Dictionary<DeviceType, List<IInputDevice>> _inputDevices = new ();

	public Action<DeviceType> OnDeviceConnected;

	public IInputDevice GetFirstInputDevice(DeviceType deviceType)
	{
		return _inputDevices[deviceType].FirstOrDefault();
	}
}