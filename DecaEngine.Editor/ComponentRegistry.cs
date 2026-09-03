using DecaEngine.Editor.ECS;
using Friflo.Engine.ECS;
using DecaEngine.Scene;

namespace DecaEngine.Editor
{
	public enum ComponentRegistryKind
	{
		Component,
		Script
	}

	/// <summary>One "Add Component" menu entry: menu path, CLR type and kind.</summary>
	public sealed record ComponentRegistryEntry(string MenuPath, Type ClrType, ComponentRegistryKind Kind)
	{
		public string DisplayName
		{
			get
			{
				int slash = MenuPath.LastIndexOf('/');
				return slash < 0 ? MenuPath : MenuPath[(slash + 1)..];
			}
		}
	}

	/// <summary>Registry of components/scripts offered by the inspector's "Add Component" menu; explicit registration keeps internal render components out of the menu.</summary>
	public static class ComponentRegistry
	{
		private static readonly List<ComponentRegistryEntry> _entries = new();

		// Lazily built caches: menu tree (rebuilt after a new registration, see InvalidateCaches)
		// and per-schema CLR-type -> Friflo-type lookups (rebuilt when a different schema arrives).
		private static MenuNode? _cachedTree;
		private static EntitySchema? _resolveSchema;
		private static readonly Dictionary<Type, ComponentType> _componentTypeByClr = new();
		private static readonly Dictionary<Type, ScriptType> _scriptTypeByClr = new();

		public static IReadOnlyList<ComponentRegistryEntry> Entries => _entries;

		static ComponentRegistry()
		{
			RegisterDefaults();
		}

		/// <summary>Registers a data component (struct : IComponent) under the given menu path.</summary>
		public static void RegisterComponent<T>(string menuPath) where T : struct, IComponent
			=> Register(menuPath, typeof(T), ComponentRegistryKind.Component);

		/// <summary>Registers a script component (class : Script) under the given menu path.</summary>
		public static void RegisterScript<T>(string menuPath) where T : Script, new()
			=> Register(menuPath, typeof(T), ComponentRegistryKind.Script);

		private static void Register(string menuPath, Type type, ComponentRegistryKind kind)
		{
			if (string.IsNullOrWhiteSpace(menuPath))
			{
				throw new ArgumentException("menuPath must not be null/empty.", nameof(menuPath));
			}
			if (_entries.Any(e => e.ClrType == type))
			{
				return;
			}
			_entries.Add(new ComponentRegistryEntry(menuPath, type, kind));
			InvalidateCaches();
		}

		/// <summary>Drops all caches so runtime-registered types show up and stale reflection data from an unloaded assembly is never reused.</summary>
		public static void InvalidateCaches()
		{
			_cachedTree = null;
			_resolveSchema = null;
			_componentTypeByClr.Clear();
			_scriptTypeByClr.Clear();
			ComponentFieldEditor.InvalidateCaches();
		}

		private static void RegisterDefaults()
		{
			RegisterComponent<Position>("Transform/Position");
			RegisterComponent<Rotation>("Transform/Rotation");
			RegisterComponent<Scale3>("Transform/Scale");

			RegisterComponent<BoundingBoxInfo>("Rendering/Bounding Box");
			RegisterComponent<ModelRenderer>("Rendering/Model Renderer");
			RegisterComponent<LightComponent>("Rendering/Light");

			RegisterComponent<RotateComponent>("Gameplay/Rotate");
			RegisterComponent<CircleMoveComponent>("Gameplay/Circle Move");
			RegisterComponent<PlayerMoveComponent>("Gameplay/Player Move");
			RegisterComponent<FallRecoverComponent>("Gameplay/Fall & Recover");

			// Character body lives under "Physics", not "Gameplay": it's what the entity is,
			// not how it behaves, and it pairs with any movement script.
			RegisterComponent<CharacterBodyComponent>("Physics/Character Body");

			// Animation: authoring data only - the runtime pose/ozz/ragdoll state lives in a side
			// registry, see AnimationComponents.cs for why it cannot be an ECS component.
			RegisterComponent<Animator>("Animation/Animator");
			RegisterComponent<LocomotionComponent>("Animation/Locomotion");
			RegisterComponent<OverlayClipComponent>("Animation/Overlay Clip");
			RegisterComponent<AdditiveClipComponent>("Animation/Additive Clip");
			RegisterComponent<FootIkComponent>("Animation/Foot IK");
			RegisterComponent<SpringBoneComponent>("Animation/Spring Bone Chain");
			RegisterComponent<LookAtComponent>("Animation/Look At");
			RegisterComponent<RagdollComponent>("Animation/Ragdoll");
		}

		public sealed class MenuNode
		{
			public string Name = string.Empty;
			public readonly Dictionary<string, MenuNode> Children = new(StringComparer.Ordinal);
			public readonly List<ComponentRegistryEntry> Leaves = new();

			public bool IsEmpty => Children.Count == 0 && Leaves.Count == 0;
		}

		/// <summary>Builds (or returns the cached) menu tree from <see cref="Entries"/>.</summary>
		public static MenuNode BuildTree()
		{
			return _cachedTree ??= BuildTreeUncached();
		}

		private static MenuNode BuildTreeUncached()
		{
			var root = new MenuNode();
			foreach (var entry in _entries)
			{
				var segments = entry.MenuPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
				if (segments.Length == 0)
				{
					continue;
				}

				var node = root;
				for (int i = 0; i < segments.Length - 1; i++)
				{
					if (!node.Children.TryGetValue(segments[i], out var child))
					{
						child = new MenuNode { Name = segments[i] };
						node.Children[segments[i]] = child;
					}
					node = child;
				}
				node.Leaves.Add(entry);
			}
			return root;
		}

		/// <summary>Finds the Friflo component type in <see cref="EntitySchema"/> for a registered CLR type.</summary>
		public static ComponentType? ResolveComponentType(EntitySchema schema, Type clrType)
		{
			EnsureResolveCaches(schema);
			return _componentTypeByClr.TryGetValue(clrType, out var ct) ? ct : null;
		}

		/// <summary>Finds the Friflo script type in <see cref="EntitySchema"/> for a registered CLR type.</summary>
		public static ScriptType? ResolveScriptType(EntitySchema schema, Type clrType)
		{
			EnsureResolveCaches(schema);
			return _scriptTypeByClr.TryGetValue(clrType, out var st) ? st : null;
		}

		// Rebuilt only for a different schema instance; TryAdd keeps first-match semantics.
		private static void EnsureResolveCaches(EntitySchema schema)
		{
			if (ReferenceEquals(_resolveSchema, schema))
			{
				return;
			}

			_componentTypeByClr.Clear();
			_scriptTypeByClr.Clear();
			foreach (var ct in schema.Components)
			{
				if (ct != null && ct.Type != null)
				{
					_componentTypeByClr.TryAdd(ct.Type, ct);
				}
			}
			foreach (var st in schema.Scripts)
			{
				if (st != null && st.Type != null)
				{
					_scriptTypeByClr.TryAdd(st.Type, st);
				}
			}
			_resolveSchema = schema;
		}
	}
}
