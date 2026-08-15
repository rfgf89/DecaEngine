using Friflo.Engine.ECS;

namespace DecaEngine.Core.Entities
{
	/// <summary>
	/// Serializable reference to another entity in the same <see cref="EntityStore"/>, stored as that
	/// entity's persistent id (<see cref="Entity.Pid"/>) rather than its runtime <see cref="Entity.Id"/>
	/// (which is only stable for the lifetime of a single store/session).
	/// </summary>
	/// <remarks>
	/// Intended to be used as a component/script field instead of a raw <see cref="Entity"/> or int id:
	/// the editor's ComponentFieldEditor recognizes this type and renders a dedicated widget (target
	/// entity name + a drag&amp;drop target) instead of a plain int box, so users can drag an entity
	/// straight from the editor's hierarchy (InspectorWindow) onto the field. It is a plain struct with
	/// a single public long field, so Friflo's Fliox JSON serializer round-trips it like any other
	/// component value type - no custom converter needed. Pid survives prefab save/load and
	/// instantiation (see <see cref="DecaEngine.Core.Prefabs.PrefabAsset"/>), unlike the runtime Id.
	/// At runtime, resolve <see cref="Pid"/> against the relevant <see cref="EntityStore"/> via
	/// <see cref="Resolve"/> (this type intentionally has no store of its own).
	/// </remarks>
	public struct EntityRef : IEquatable<EntityRef>, IComponent
	{
		/// <summary>
		/// ImGui drag&amp;drop payload type identifier shared between the editor's hierarchy view
		/// (drag source) and ComponentFieldEditor (drop target). ImGui payload type strings must be
		/// at most 32 bytes - keep this short.
		/// </summary>
		public const string DragDropPayloadType = "DECA_ENTITY_PID";

		/// <summary>Persistent id of the referenced entity (see <see cref="Entity.Pid"/>). 0 when unassigned.</summary>
		public long Pid;

		public EntityRef(long pid)
		{
			Pid = pid;
		}

		public readonly bool IsEmpty => Pid == 0;

		/// <summary>Resolves this reference against <paramref name="store"/>; returns a null <see cref="Entity"/> if unassigned or not found.</summary>
		public readonly Entity Resolve(EntityStore store)
		{
			if (IsEmpty || store == null)
			{
				return default;
			}
			return store.TryGetEntityByPid(Pid, out var entity) ? entity : default;
		}

		public static implicit operator EntityRef(long pid) => new(pid);
		public static implicit operator EntityRef(Entity entity) => new(entity.Pid);

		public readonly bool Equals(EntityRef other) => Pid == other.Pid;
		public readonly override bool Equals(object? obj) => obj is EntityRef other && Equals(other);
		public readonly override int GetHashCode() => Pid.GetHashCode();
		public readonly override string ToString() => IsEmpty ? "(None)" : $"Entity {Pid}";
	}
}
