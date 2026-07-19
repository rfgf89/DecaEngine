using System.Numerics;

namespace DecaEngine.Core;

public interface IWindowHandle : IReleaseObject
{
	public IntPtr Handle { get; }

	public Vector2 Size { get; set; }

	public void Initialize(string title, int vsync, Vector2 size);

	public void LoadAndSetIcon(string path);

	public event Action OnWindowResize;

	public float GetScale();
}