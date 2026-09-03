using System.Text.Json.Serialization;

namespace DecaEngine.Editor;

/// <summary>Which animation debug layers the viewport draws.</summary>
public struct AnimationDebugOptions()
{
	public bool Skeleton = false;

	/// <summary>Per-joint axes: X red, Y green, Z blue.</summary>
	public bool JointAxes = false;

	public bool JointNames = false;

	/// <summary>Bind pose ghosted over the current one.</summary>
	public bool BindPose = false;

	/// <summary>Foot IK rays, hit points with normals and joint targets.</summary>
	public bool FootIk = false;

	public bool SpringChains = false;

	public bool LookAt = false;

	/// <summary>Draw without depth test: the skeleton sits inside the mesh.</summary>
	public bool OnTop = true;

	[JsonIgnore]
	public bool AnyEnabled =>
		Skeleton || JointAxes || JointNames || BindPose || FootIk || SpringChains || LookAt;
}

/// <summary>Which physics debug layers the viewport draws.</summary>
public struct PhysicsDebugOptions()
{
	/// <summary>Color codes state: dynamic orange, kinematic blue, sleeping grey.</summary>
	public bool Colliders = false;

	/// <summary>Named inverted: a missing JSON key must default to drawing on top.</summary>
	public bool CollidersDepthTested = false;

	public bool Statics = false;

	/// <summary>Contact recording costs narrow-phase work, so it follows this flag.</summary>
	public bool Contacts = false;

	public bool Rays = false;

	public bool Velocities = false;

	public bool RagdollJoints = false;

	/// <summary>Depth-test bypass for every layer except colliders.</summary>
	public bool OnTop = false;

	[JsonIgnore]
	public bool AnyEnabled => Colliders || Statics || Contacts || Rays || Velocities || RagdollJoints;

	/// <summary>The only layer that costs work before drawing.</summary>
	[JsonIgnore]
	public bool NeedsContactRecording => Contacts;
}
