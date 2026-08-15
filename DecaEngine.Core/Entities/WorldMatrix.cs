using System.Numerics;
using Friflo.Engine.ECS;

namespace DecaEngine.Core.Entities
{
	/// <summary>
	/// World-space model matrix of an entity, maintained by <see cref="TransformSystem"/> for
	/// entities that have a parent (root entities render straight from their own
	/// Position/Rotation/Scale3, so no component is stored for them). Renderers should prefer this
	/// over the entity's local TRS when the component is present.
	/// </summary>
	public struct WorldMatrix : IComponent
	{
		public Matrix4x4 value;
	}

	/// <summary>
	/// Tag added by <see cref="TransformSystem"/> when an entity's world transform was recomputed
	/// this frame (local TRS changed, entity was re-parented, or an ancestor moved). GPU
	/// instance-upload systems consume it alongside their own update tags and remove it after
	/// re-uploading the instance data.
	/// </summary>
	public struct WorldTransformDirtyTag : ITag
	{
	}
}
