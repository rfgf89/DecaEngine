using System.Numerics;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;

namespace DecaEngine.Core.Entities
{
	/// <summary>
	/// Propagates transforms down entity hierarchies: world = local TRS composed with the parent's
	/// world matrix (System.Numerics row-vector convention, <c>local * parent</c> - same composition
	/// as the editor's PrefabSceneViewport), walking every tree parent-first.
	/// </summary>
	/// <remarks>
	/// Dirty tracking: an entity is recomputed only when its local TRS or parent id changed since
	/// the last frame, or when an ancestor was recomputed. Recomputed entities that have a parent
	/// get their <see cref="WorldMatrix"/> component written and every recomputed entity is tagged
	/// with <see cref="WorldTransformDirtyTag"/> so downstream GPU instance-buffer systems know to
	/// re-upload it. Root entities keep NO <see cref="WorldMatrix"/> - renderers fall back to the
	/// entity's own TRS for them, so flat scenes see zero archetype changes from this system.
	/// Must be registered BEFORE any system consuming <see cref="WorldMatrix"/> or
	/// <see cref="WorldTransformDirtyTag"/> (see EditorManager's SystemRoot).
	/// </remarks>
	public class TransformSystem : QuerySystem
	{
		private struct CachedLocal
		{
			public Vector3 Position;
			public Quaternion Rotation;
			public Vector3 Scale;
			public int ParentId;
		}

		/// <summary>Last frame's local TRS + parent per entity id - the dirty-check baseline.</summary>
		private readonly Dictionary<int, CachedLocal> _localCache = new(256);
		private readonly HashSet<int> _seenThisFrame = new(256);
		private readonly List<Entity> _rootsScratch = new(64);
		private readonly List<int> _removeScratch = new(64);

		protected override void OnUpdate()
		{
			var store = Query.Store;

			_seenThisFrame.Clear();
			_rootsScratch.Clear();

			// Collect roots first: UpdateRecursive performs structural changes (AddComponent/AddTag)
			// and must not run inside the store enumeration.
			foreach (var entity in store.Entities)
			{
				if (entity.IsNull)
				{
					continue;
				}

				if (entity.Parent.IsNull)
				{
					_rootsScratch.Add(entity);
				}
			}

			foreach (var root in _rootsScratch)
			{
				UpdateRecursive(root, Matrix4x4.Identity, parentDirty: false, hasParent: false, parentId: 0);
			}

			// Drop cache entries of entities deleted since last frame.
			if (_localCache.Count != _seenThisFrame.Count)
			{
				_removeScratch.Clear();
				foreach (var id in _localCache.Keys)
				{
					if (!_seenThisFrame.Contains(id))
					{
						_removeScratch.Add(id);
					}
				}

				foreach (var id in _removeScratch)
				{
					_localCache.Remove(id);
				}
			}
		}

		private void UpdateRecursive(Entity entity, in Matrix4x4 parentWorld, bool parentDirty, bool hasParent, int parentId)
		{
			_seenThisFrame.Add(entity.Id);

			Vector3 pos = entity.HasPosition ? entity.Position.value : Vector3.Zero;
			Quaternion rot = entity.HasRotation ? entity.Rotation.value : Quaternion.Identity;
			Vector3 scale = entity.HasScale3 ? entity.Scale3.value : Vector3.One;

			bool localChanged =
				!_localCache.TryGetValue(entity.Id, out var cached) ||
				cached.Position != pos ||
				cached.Rotation != rot ||
				cached.Scale != scale ||
				cached.ParentId != parentId;

			bool dirty = parentDirty || localChanged || (hasParent && !entity.HasComponent<WorldMatrix>());

			var world = Matrix4x4.Identity;
			if (dirty)
			{
				world = CreateTrs(pos, rot, scale);
				if (hasParent)
				{
					world *= parentWorld;
					entity.AddComponent(new WorldMatrix { value = world });
				}

				entity.AddTag<WorldTransformDirtyTag>();

				_localCache[entity.Id] = new CachedLocal
				{
					Position = pos,
					Rotation = rot,
					Scale = scale,
					ParentId = parentId,
				};
			}

			if (entity.ChildCount == 0)
			{
				return;
			}

			if (!dirty)
			{
				// Clean entity with children: its world is still needed further down the tree.
				world = hasParent ? entity.GetComponent<WorldMatrix>().value : CreateTrs(pos, rot, scale);
			}

			foreach (var child in entity.ChildEntities)
			{
				UpdateRecursive(child, world, dirty, hasParent: true, parentId: entity.Id);
			}
		}

		/// <summary>Same result as DecaEngine.Graphics.Diligent.MathUtils.CreateTrs (Core cannot
		/// reference the graphics assembly): scale, then rotation, then translation.</summary>
		private static Matrix4x4 CreateTrs(Vector3 translate, Quaternion rotation, Vector3 scale)
		{
			return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translate);
		}
	}
}
