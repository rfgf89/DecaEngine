using System.Numerics;

namespace DecaEngine.Core;

/// <summary>Position, rotation, scale; matrix assembly via <see cref="MathUtils.CreateTrs"/>.</summary>
public struct Transform
{
	public Vector3 position;
	public Quaternion rotation;
	public Vector3 scale;
}
