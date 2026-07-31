using System.Reflection;
var dll = @"C:\Users\rfgf89\.nuget\packages\diligent-engine-net\1.0.4\lib\net8.0\DiligentCore.dll";
var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
var paths = new List<string> { dll };
paths.AddRange(Directory.GetFiles(runtimeDir, "*.dll"));
var resolver = new PathAssemblyResolver(paths);
using var mlc = new MetadataLoadContext(resolver);
var asm = mlc.LoadFromAssemblyPath(dll);
void Dump(string typeName)
{
    var t = asm.GetType(typeName);
    Console.WriteLine($"=== {typeName} : found={t != null} ===");
    if (t == null) return;
    Console.WriteLine("Interfaces: " + string.Join(", ", t.GetInterfaces().Select(i => i.FullName)));
    foreach (var m in t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
    {
        Console.WriteLine("  " + m);
    }
}
Dump("Diligent.ICommandList");
Dump("Diligent.IDeviceContext");
Dump("Diligent.IDeviceObject");
