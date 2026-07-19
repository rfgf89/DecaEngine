public class InputAction
{
	public Action onPressed;
	public Action onReleased;
	public Action onPerform;

	private object _value;

	public T Read<T>()
	{
		if (_value == null)
		{
			return default;
		}
		return (T)_value;
	}

	public void Pressed<T>(T value)
	{
		_value = value;
		onPressed?.Invoke();
	}

	public void Perform<T>(T value)
	{
		_value = value;
		onPerform.Invoke();
	}

	public void Released<T>(T value)
	{
		_value = value;
		onReleased?.Invoke();
	}
}