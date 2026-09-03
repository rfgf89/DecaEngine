using Friflo.Engine.ECS;

namespace DecaEngine.Core.Entities
{
	/// <summary>Serializable entity reference stored as the persistent id (Pid), which unlike the
	/// runtime Id survives prefab save/load; a single long field so Fliox serializes it as-is.</summary>
	public struct EntityRef : IEquatable<EntityRef>, IComponent
	{
		/// <summary>ImGui drag&amp;drop payload type shared with ComponentFieldEditor; ImGui caps
		/// payload type strings at 32 bytes.</summary>
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
