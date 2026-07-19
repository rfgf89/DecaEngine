public interface IInputDevice
{
	public uint deviceId {get; set;}

	public void AddListener(Enum actionEvent, InputAction inputDevice);
	public void RemoveListener(Enum actionEvent, InputAction inputDevice);
}