using UnsafeCollections.Collections.Native;

namespace DecaEngine.Core;

public readonly struct Ref<T> : IReleaseObject where T : unmanaged
{
	private readonly NativeArray<T> _value;
	public ref T Value => ref _value.GetRef(0);

	public void Set(T value)
	{
		_value.GetRef(0) = value;
	}

	public Ref(T value)
	{
		_value = new NativeArray<T>(1);
		Set(value);
	}

	public void Release()
	{
		_value.Dispose();
	}
}