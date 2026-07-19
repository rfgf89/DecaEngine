using System.Numerics;

public interface IInputEventPull
{
	public bool PullEvent();

	public event Action<Vector2> OnSurfaceResize;
}