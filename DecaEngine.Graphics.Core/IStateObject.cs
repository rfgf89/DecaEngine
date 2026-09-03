using DecaEngine.Core;

namespace DecaEngine.Graphics;

/// <summary>Pipeline state subtype described by <see cref="IStateObject"/>.</summary>
public enum PipelineStateType
{
	Unknown = 0,
	Graphics,
	Compute,
	RayTracing,
	Mesh,
	Tile,
}

/// <summary>Backend-agnostic contract for a Pipeline State Object.</summary>
public interface IStateObject : IReleaseObject
{
	public string Name { get; }

	public PipelineStateType StateType { get; }
}
