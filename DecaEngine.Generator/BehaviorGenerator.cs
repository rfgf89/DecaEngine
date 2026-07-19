using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DecaEngine.Generator;

/*[Generator(LanguageNames.CSharp)]
public sealed class BehaviorGenerator : IIncrementalGenerator
{
	public void Initialize(IncrementalGeneratorInitializationContext ctx)
	{
		var candidates = ctx.SyntaxProvider.CreateSyntaxProvider(
				static (node, _) => node is StructDeclarationSyntax { AttributeLists.Count: > 0 },
				static (context, _) =>
				{
					var sds = (StructDeclarationSyntax)context.Node;
					var type = context.SemanticModel.GetDeclaredSymbol(sds);
					if (type is null)
					{
						return null;
					}

					foreach (var a in type.GetAttributes())
					{
						if (a.AttributeClass?.ToDisplayString() == "Engine.BehaviorAttribute")
						{
							return type;
						}
					}
					return null;
				})
			.Where(s => s is not null)
			.Collect();

		ctx.RegisterSourceOutput(ctx.CompilationProvider.Combine(candidates), (spc, pair) =>
		{
			var behaviors = pair.Right
				.OfType<INamedTypeSymbol>()
				.Select(BuildModel)
				.ToList();

			foreach (var b in behaviors)
			{
				spc.AddSource($"{b.SafeName}.g.cs", GenBehaviorSystems(b));
			}

			if (behaviors.Count > 0)
			{
				spc.AddSource("BehaviorsRegistration.g.cs", GenRegistration(behaviors));
			}
		});
	}

	private static BehaviorModel BuildModel(INamedTypeSymbol type)
	{
		var ns = type.ContainingNamespace.IsGlobalNamespace ? "Engine" : type.ContainingNamespace.ToDisplayString();
		var methods = type.GetMembers()
			.OfType<IMethodSymbol>()
			.Select(m => (Method: m, Stage: GetStage(m)))
			.Where(x => x.Stage is not null)
			.Select(x => new StageMethod
			{
				Stage = x.Stage!.Value,
				IsStatic = x.Method.IsStatic,
				MethodContainer = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
				MethodName = x.Method.Name,
				Filters = GetFilters(x.Method),
				RunIf = GetRunIf(x.Method, type),
				ToggleKey = GetToggleKey(x.Method)
			})
			.ToList();

		return new BehaviorModel
		{
			Namespace = ns,
			Name = type.Name,
			SafeName = type.Name + "_Generated",
			BehaviorFqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
			StageMethods = methods
		};
	}

	private static Stage? GetStage(IMethodSymbol m)
	{
		foreach (var a in m.GetAttributes())
			switch (a.AttributeClass?.ToDisplayString())
			{
				case "Engine.OnStartupAttribute": return Stage.Startup;
				case "Engine.OnFirstAttribute": return Stage.First;
				case "Engine.OnPreUpdateAttribute": return Stage.PreUpdate;
				case "Engine.OnUpdateAttribute": return Stage.Update;
				case "Engine.OnPostUpdateAttribute": return Stage.PostUpdate;
				case "Engine.OnRenderAttribute": return Stage.Render;
				case "Engine.OnLastAttribute": return Stage.Last;
				case "Engine.OnCleanupAttribute": return Stage.Cleanup;
			}

		return null;
	}

	private static Filters GetFilters(IMethodSymbol m)
	{
		var with = new List<string>();
		var without = new List<string>();
		var changed = new List<string>();
		foreach (var a in m.GetAttributes())
		{
			var bucket = a.AttributeClass?.ToDisplayString() switch
			{
				"Engine.WithAttribute" => with,
				"Engine.WithoutAttribute" => without,
				"Engine.ChangedAttribute" => changed,
				_ => null
			};

			if (bucket is null || a.ConstructorArguments.Length == 0)
			{
				continue;
			}

			foreach (var v in a.ConstructorArguments[0].Values)
			{
				if (v.Value is ITypeSymbol ts)
				{
					bucket.Add(ts.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
				}
			}
		}

		return new Filters(with, without, changed);
	}

	private static (string Name, MemberKind Kind)? GetRunIf(IMethodSymbol method, INamedTypeSymbol behaviorType)
	{
		string? attrName = null;
		foreach (var a in method.GetAttributes())
		{
			if (a.AttributeClass?.ToDisplayString() != "Engine.RunIfAttribute" || a.ConstructorArguments.Length <= 0 || a.ConstructorArguments[0].Value is not string name)
			{
				continue;
			}

			attrName = name;
			break;
		}

		if (attrName is null)
		{
			return null;
		}

		foreach (var member in behaviorType.GetMembers())
		{
			if (member.Name != attrName)
			{
				continue;
			}

			switch (member)
			{
				case IMethodSymbol:
					return (attrName, MemberKind.Method);
				case IPropertySymbol:
					return (attrName, MemberKind.Property);
				case IFieldSymbol:
					return (attrName, MemberKind.Field);
			}
		}

		return null;
	}

	private enum MemberKind
	{
		Method,
		Property,
		Field
	}

	private static (int Key, int Modifier, bool DefaultEnabled)? GetToggleKey(IMethodSymbol m)
	{
		foreach (var a in m.GetAttributes())
		{
			if (a.AttributeClass?.ToDisplayString() != "Engine.ToggleKeyAttribute") continue;
			var key = a.ConstructorArguments.Length > 0 && a.ConstructorArguments[0].Value is int k ? k : 0;
			var mod = a.ConstructorArguments.Length > 1 && a.ConstructorArguments[1].Value is int mo ? mo : 0;
			var def = true;
			foreach (var na in a.NamedArguments)
			{
				if (na is { Key: "DefaultEnabled", Value.Value: bool bb })
				{
					def = bb;
				}
			}
			return (key, mod, def);
		}

		return null;
	}

	private static string GenBehaviorSystems(BehaviorModel b)
	{
		var registerCalls = string.Concat(b.StageMethods
			.GroupBy(m => m.Stage)
			.Select(g =>
				$"        app.AddSystem(Engine.Stage.{g.Key}, {BuildDescriptor(b, g.Key, g.First(), g.Any(m => !m.IsStatic))});\n"));

		var stageMethods = string.Concat(b.StageMethods.Select(m => "\n" + GenStageMethod(b, m)));

		return
			$$"""
			  // <auto-generated />
			  namespace {{b.Namespace}};

			  internal static class {{b.SafeName}}
			  {
			      public static void Register(Engine.App app)
			      {
			          {{registerCalls}}    
			      }
			      {{stageMethods}}
			  }

			  """;
	}

	private static string BuildDescriptor(BehaviorModel b, Stage stage, StageMethod first, bool hasInstanceMethod)
	{
		var systemId = $"{b.SafeName}_{stage}";
		var access = hasInstanceMethod
			? $".Write<{b.BehaviorFqn}>()"
			: ".Read<global::Engine.EcsWorld>()";
		var ctor = $"new global::Engine.SystemDescriptor({systemId}, \"{systemId}\")";

		if (first.ToggleKey is { } tk)
		{
			var def = tk.DefaultEnabled ? "true" : "false";
			return
				$"{ctor}.RunIf(global::Engine.BehaviorConditions.KeyToggle(\"{systemId}\", (global::Engine.Key){tk.Key}, (global::Engine.KeyModifier){tk.Modifier}, {def})){access}";
		}

		if (first.RunIf is { } ri)
		{
			var expr = ri.Kind == MemberKind.Method
				? $"{b.BehaviorFqn}.{ri.Name}"
				: $"_ => {b.BehaviorFqn}.{ri.Name}";
			return $"{ctor}.RunIf({expr}){access}";
		}

		return $"{ctor}{access}";
	}

	private static string GenStageMethod(BehaviorModel b, StageMethod m)
	{
		var name = $"{b.SafeName}_{m.Stage}";

		if (m.IsStatic)
			return
				$$"""
				      private static void {{name}}(Engine.World world)
				      {
				          var ctx = new Engine.BehaviorContext(world);
				          {{m.MethodContainer}}.{{m.MethodName}}(ctx);
				      }
				  """;

		var hasFilters = m.Filters.With.Count + m.Filters.Without.Count + m.Filters.Changed.Count > 0;
		var hoist = hasFilters ? GenFilterHoist(m.Filters, "        ") : "";
		var parChecks = hasFilters ? GenFilterChecks(m.Filters, "                        ") : "";
		var seqChecks = hasFilters ? GenFilterChecks(m.Filters, "                ") : "";

		return
			$$"""
			      private static void {{name}}(Engine.World world)
			      {
			          var ecs = world.Resource<Engine.EcsWorld>();
			          var __cmd = world.Resource<Engine.EcsCommands>();
			          var __time = world.Resource<Engine.Time>();
			          var __input = world.Resource<Engine.Input>();
			          var __physics = world.Resource<Engine.PhysicsWorld>();
			          var __store = ecs.GetStorePublic<{{b.BehaviorFqn}}>();
			          var __count = __store.Count;
			          if (__count == 0) return;
			          var __entities = __store.EntitiesArray;
			          var __components = __store.ComponentsArray;
			          {{hoist}}        
			          if (__count >= 4096)
			          {
			              System.Threading.Tasks.Parallel.ForEach(
			                  System.Collections.Concurrent.Partitioner.Create(0, __count,
			                      System.Math.Max(256, __count / (System.Environment.ProcessorCount * 4))),
			                  __range =>
			                  {
			                      var ctx = new Engine.BehaviorContext(world, ecs, __cmd, __time, __input, __physics);
			                      for (int __i = __range.Item1; __i < __range.Item2; __i++)
			                      {
			                          int entity = __entities[__i];
			                          {{parChecks}}                        
			                          ctx.EntityId = entity;
			                          ref var behv = ref __components[__i];
			                          behv.{{m.MethodName}}(ctx);
			                      }
			                  }
			              );
			          }
			          else
			          {
			              var ctx = new Engine.BehaviorContext(world, ecs, __cmd, __time, __input, __physics);
			              for (int __i = 0; __i < __count; __i++)
			              {
			                  int entity = __entities[__i];
			                  {{seqChecks}}                
			                  ctx.EntityId = entity;
			                  ref var behv = ref __components[__i];
			                  behv.{{m.MethodName}}(ctx);
			              }
			          }
			      }
			  """;
	}

	private static string GenFilterHoist(Filters f, string indent)
	{
		var lines = new List<string>();
		for (var i = 0; i < f.With.Count; i++)
		{
			lines.Add($"{indent}var __fWith{i} = ecs.GetStorePublic<{f.With[i]}>();");
		}

		for (var i = 0; i < f.Without.Count; i++)
		{
			lines.Add($"{indent}var __fWout{i} = ecs.GetStorePublic<{f.Without[i]}>();");
		}

		for (var i = 0; i < f.Changed.Count; i++)
		{
			lines.Add($"{indent}var __fChg{i} = ecs.GetStorePublic<{f.Changed[i]}>();");
		}
		return lines.Count == 0 ? "" : string.Join("\n", lines) + "\n";
	}

	private static string GenFilterChecks(Filters f, string indent, string skip = "continue")
	{
		var lines = new List<string>();
		for (var i = 0; i < f.With.Count; i++)
		{
			lines.Add($"{indent}if (!__fWith{i}.Has(entity)) {skip};");
		}

		for (var i = 0; i < f.Without.Count; i++)
		{
			lines.Add($"{indent}if (__fWout{i}.Has(entity)) {skip};");
		}

		for (var i = 0; i < f.Changed.Count; i++)
		{
			lines.Add($"{indent}if (!__fChg{i}.ChangedThisFrame(entity, 0)) {skip};");
		}

		return lines.Count == 0 ? "" : string.Join("\n", lines) + "\n";
	}


	private static string GenRegistration(IEnumerable<BehaviorModel> behaviors)
	{
		var calls = string.Concat(behaviors.Select(b =>
			$"        global::{b.Namespace}.{b.SafeName}.Register(app);\n"));

		return
			$$"""
			  // <auto-generated />
			  namespace Engine;

			  public static class BehaviorRegistration
			  {
			      [global::Engine.GeneratedBehaviorRegistration]
			      public static void Register(global::Engine.App app)
			      {
			          {{calls}}    
			      }
			  }

			  """;
	}

	private enum Stage
	{
		Startup,
		First,
		PreUpdate,
		Update,
		PostUpdate,
		Render,
		Last,
		Cleanup
	}

	private sealed record Filters(
		IReadOnlyList<string> With,
		IReadOnlyList<string> Without,
		IReadOnlyList<string> Changed)
	{
		public IReadOnlyList<string> With { get; } = With;
		public IReadOnlyList<string> Without { get; } = Without;
		public IReadOnlyList<string> Changed { get; } = Changed;
	}

	private sealed record StageMethod
	{
		public Stage Stage { get; set; }
		public bool IsStatic { get; set; }
		public string MethodContainer { get; set; } = string.Empty;
		public string MethodName { get; set; } = string.Empty;

		public Filters Filters { get; set; } = new([], [], []);

		public (string Name, MemberKind Kind)? RunIf { get; set; }
		public (int Key, int Modifier, bool DefaultEnabled)? ToggleKey { get; set; }
	}

	private sealed record BehaviorModel
	{
		public string Namespace { get; set; } = "Engine";
		public string Name { get; set; } = string.Empty;
		public string SafeName { get; set; } = string.Empty;
		public string BehaviorFqn { get; set; } = string.Empty;
		public List<StageMethod> StageMethods { get; set; } = new();
	}
}*/