using System.Reflection;
var dll = @"C:\Users\rfgf89\.nuget\packages\diligentgraphics.diligentengine.core\2.5.6\lib\net6.0\Diligent-GraphicsEngine.NET.dll";
var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
var paths = new List<string> { dll };
paths.AddRange(Directory.GetFiles(runtimeDir, "*.dll"));
foreach (var pkg in new[] { "sharpgen.runtime", "sharpgen.runtime.com" })
{
    var dir = Path.Combine(@"C:\Users\rfgf89\.nuget\packages", pkg);
    if (!Directory.Exists(dir)) continue;
    var best = Directory.GetFiles(dir, "SharpGen*.dll", SearchOption.AllDirectories)
        .Where(p => p.Contains("net6") || p.Contains("net5") || p.Contains("netstandard"))
        .OrderByDescending(p => System.Diagnostics.FileVersionInfo.GetVersionInfo(p).FileVersion)
        .FirstOrDefault();
    if (best != null) paths.Add(best);
}
var resolver = new PathAssemblyResolver(paths);
using var mlc = new MetadataLoadContext(resolver);
var asm = mlc.LoadFromAssemblyPath(dll);
void DumpMatch(string typeName, string pattern)
{
    var t = asm.GetType(typeName);
    Console.WriteLine($"=== {typeName} (filter {pattern}) : found={t != null} ===");
    if (t == null) return;
    foreach (var m in t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
    {
        try
        {
            if (pattern == "*" || m.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                Console.WriteLine("  " + m);
        }
        catch (Exception e) { Console.WriteLine("  <err " + m.Name + ": " + e.Message + ">"); }
    }
}
DumpMatch("Diligent.IDeviceContext", "Map");
DumpMatch("Diligent.IDeviceContext", "Copy");
DumpMatch("Diligent.MappedTextureSubresource", "*");
DumpMatch("Diligent.CopyTextureAttribs", "*");
DumpMatch("Diligent.Box", "*");
