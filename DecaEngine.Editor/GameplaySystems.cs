using System.Numerics;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;

namespace DecaEngine.Editor.ECS
{
	/// <summary>
	/// Play-Mode-only system that spins every entity with a <see cref="RotateComponent"/> around its
	/// configured axis. Registered into a dedicated <see cref="SystemRoot"/> that InspectorWindow only
	/// updates while Play Mode is running (see <see cref="InspectorWindow.Play"/>).
	/// </summary>
	public class RotateSystem : QuerySystem<RotateComponent, Rotation>
	{
		protected override void OnUpdate()
		{
			float deltaTime = Tick.deltaTime;
			Query.ForEachEntity((ref RotateComponent rotate, ref Rotation rotation, Entity entity) =>
			{
				if (rotate.DegreesPerSecond == 0f || rotate.Axis == Vector3.Zero)
				{
					return;
				}
				var angle = rotate.DegreesPerSecond * (MathF.PI / 180f) * deltaTime;
				var delta = Quaternion.CreateFromAxisAngle(Vector3.Normalize(rotate.Axis), angle);
				rotation.value = Quaternion.Normalize(rotation.value * delta);
			});
		}
	}
}
