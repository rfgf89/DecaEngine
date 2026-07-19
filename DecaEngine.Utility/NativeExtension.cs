using System.Runtime.InteropServices;

public static class NativeExtension
{
	public static T ToStructure<T>(this byte[] byteArray, int start) where T : struct
	{
		var size = Marshal.SizeOf<T>();
		if (byteArray.Length - start < size)
		{
			Console.WriteLine($"Marshal out of range{typeof(T).Name}");
			return default;
		}

		T str = default;
		IntPtr ptr = Marshal.AllocHGlobal(size);

		try
		{
			Marshal.Copy(byteArray, start, ptr, size);
			str = Marshal.PtrToStructure<T>(ptr);
		}
		catch (Exception e)
		{
			Console.WriteLine(e.Message);
		}
		finally
		{
			Marshal.FreeHGlobal(ptr);
		}

		return str;
	}

	public static object ToStructure(this byte[] byteArray, int start, Type type)
	{
		var size = Marshal.SizeOf(type);
		if (byteArray.Length - start < size)
		{
			Console.WriteLine($"Marshal out of range{type.Name}");
			return default;
		}

		object str = null;
		IntPtr ptr = Marshal.AllocHGlobal(size);

		try
		{
			Marshal.Copy(byteArray, start, ptr, size);
			str = Marshal.PtrToStructure(ptr, type);
		}
		catch (Exception e)
		{
			Console.WriteLine(e.Message);
		}
		finally
		{
			Marshal.FreeHGlobal(ptr);
		}

		return str;
	}

	public static byte[] ToByteArray<T>(this T structure)
	{
		int size = Marshal.SizeOf(structure);
		byte[] byteArray = new byte[size];

		IntPtr ptr = Marshal.AllocHGlobal(size);
		try
		{
			Marshal.StructureToPtr(structure, ptr, true);
			Marshal.Copy(ptr, byteArray, 0, size);
		}
		catch (Exception e)
		{
			Console.WriteLine(e.Message);
		}
		finally
		{
			Marshal.FreeHGlobal(ptr);
		}

		return byteArray;
	}
}