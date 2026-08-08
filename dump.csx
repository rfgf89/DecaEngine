using System;
using System.Reflection;
using System.Linq;
var asm = Assembly.LoadFrom(args[0]);
var entityType = asm.GetType("Friflo.Engine.ECS.Entity");
Console.WriteLine("== Entity methods ==");
foreach (var m in entityType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
{
    if (m.Name.Contains("Component") || m.Name.Contains("Tag"))
        Console.WriteLine($"{m.ReturnType} {m.Name}({string.Join(",", m.GetParameters().Select(p=>p.ParameterType.Name))}) IsGeneric={m.IsGenericMethodDefinition}");
}
var euType = asm.GetType("Friflo.Engine.ECS.EntityUtils");
Console.WriteLine("== EntityUtils methods ==");
foreach (var m in euType.GetMethods(BindingFlags.Public | BindingFlags.Static))
{
    Console.WriteLine($"{m.ReturnType} {m.Name}({string.Join(",", m.GetParameters().Select(p=>p.ParameterType.Name))})");
}
var ctType = asm.GetType("Friflo.Engine.ECS.ComponentType");
Console.WriteLine("== ComponentType members ==");
foreach (var m in ctType.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
{
    Console.WriteLine($"{m.MemberType} {m}");
}
