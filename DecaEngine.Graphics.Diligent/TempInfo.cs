using System;
using System.Reflection;
using Diligent;

namespace DecaEngine.Graphics.Diligent;

public static class TempInfo
{
	public static void Print()
	{
		var t = typeof(DrawIndexedIndirectAttribs);
		foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
		{
			Console.WriteLine($"{f.FieldType.Name} {f.Name}");
		}
		foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
		{
			Console.WriteLine($"{p.PropertyType.Name} {p.Name}");
		}
	}
}