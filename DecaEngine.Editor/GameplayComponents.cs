using System.Numerics;
using Friflo.Engine.ECS;

namespace DecaEngine.Editor.ECS
{
	/// <summary>
	/// Spins the entity continuously around <see cref="Axis"/> while Play Mode is running (see
	/// <see cref="InspectorWindow.Play"/>/<see cref="InspectorWindow.Stop"/> and <see cref="RotateSystem"/>).
	/// First test component for the Play Mode pipeline - purely a data component, driven by
	/// <see cref="RotateSystem"/> only while playing; edits made to <see cref="Rotation"/> while
	/// playing are reverted on Stop like every other component.
	/// </summary>
	public struct RotateComponent : IComponent
	{
		public Vector3 Axis;
		public float DegreesPerSecond;
	}
}
