using System.Numerics;
using Hexa.NET.ImGui;

public abstract class ImGuiWindow
{
	private static int _idNext;
	private readonly int _idx;
	protected readonly ImGuiRender _imGuiRender;
	protected string _title;
	protected float _scale;

	protected ImGuiWindow(string title, ImGuiRender imGuiRender)
	{
		_imGuiRender = imGuiRender;
		_idx = _idNext++;
		_title = title;
	}

	public string Title => _title;

	public bool IsOpen
	{
		get;
		set
		{
			if (field != value && value)
			{
				_imGuiRender.AddWindow(this);
				OnAdd();
			}

			if (field != value && !value)
			{
				_imGuiRender.RemoveWindow(this);
				OnRemove();
			}
			field = value;
		}
	} = false;

	public void Show()
	{
		IsOpen = true;
	}

	public void Hide()
	{
		IsOpen = false;
	}

	public void Toggle()
	{
		IsOpen = !IsOpen;
	}

	public virtual void FirstFrame(uint dockId)
	{

	}

	public virtual void EndFirstFrame(uint dockId)
	{

	}

	public virtual void Render(uint dockId)
	{
		_scale = _imGuiRender.GraphicsApi.WindowHandle.GetScale();
		ImGui.SetNextWindowSizeConstraints(new Vector2(200, 100) * _scale, new Vector2(float.MaxValue, float.MaxValue));
	}

	protected abstract void OnRender(uint dockId);

	protected virtual void OnAdd()
	{

	}

	protected virtual void OnRemove()
	{

	}

	public override int GetHashCode()
	{
		return _idx;
	}
}