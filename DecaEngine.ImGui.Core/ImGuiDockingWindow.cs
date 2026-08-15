using System.ComponentModel;
using System.Numerics;
using System.Text.RegularExpressions;
using Hexa.NET.ImGui;

public abstract class ImGuiDockingWindow : ImGuiWindow
{
	private bool _undock;

	private static Dictionary<string, HashSet<ImGuiDockingWindow>> _instanceIds = new ();

	public ImGuiDockingWindow(string title, ImGuiRender imGuiRender) : base(title, imGuiRender)
	{

	}

	protected override void OnAdd()
	{
		base.OnAdd();

		var output = Regex.Replace(_title, @"[\d-]", string.Empty).Trim();
		if (!_instanceIds.ContainsKey(output))
		{
			_instanceIds[output] = new HashSet<ImGuiDockingWindow>();
		}

		_instanceIds[output].Add(this);

		for (int i = 0; i < _instanceIds[output].Count; i++)
		{
			if (i == 0)
			{
				_title = output;
				continue;
			}

			_title = $"{output} {i}";
		}
	}

	protected override void OnRemove()
	{
		base.OnRemove();

		var output = Regex.Replace(_title, @"[\d-]", string.Empty).Trim();
		_instanceIds[output].Remove(this);

		for (int i = 0; i < _instanceIds[output].Count; i++)
		{
			if (i == 0)
			{
				_title = output;
				continue;
			}

			_title = $"{output} {i}";
		}
	}

	/// <summary>Позволяет наследникам запросить дополнительные флаги для ImGui.Begin (например, ImGuiWindowFlags.MenuBar для окон со своей шапкой-тулбаром).</summary>
	protected virtual ImGuiWindowFlags AdditionalWindowFlags => ImGuiWindowFlags.None;

	/// <summary>Было ли окно (вместе с дочерними) в фокусе на ПОСЛЕДНЕЙ отрисовке. Нужно системам,
	/// которые читают ввод в обход ImGui и обязаны молчать, пока активно чужое окно: io.WantCaptureKeyboard
	/// для этого не годится - с NavEnableKeyboard он true при фокусе ЛЮБОГО окна, включая то самое,
	/// которому ввод и предназначен (см. FlyCameraSystem).</summary>
	public bool IsFocused { get; private set; }

	public override void Render(uint dockId)
	{
		if (!IsOpen && !_forceDraw)
		{
			return;
		}

		base.Render(dockId);

		bool open = IsOpen || _forceDraw;
		ImGui.Begin(_title, ref open, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings | AdditionalWindowFlags);

		IsFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
		if (IsFocused)
		{
			DrawFocusHighlight();
		}

		OnRender(dockId);

		ImGui.End();

		IsOpen = open;
	}

	private static void DrawFocusHighlight()
	{
		var drawList = ImGui.GetWindowDrawList();
		var min = ImGui.GetWindowPos();
		var max = min + ImGui.GetWindowSize();
		var style = ImGui.GetStyle();

		// GetColorU32 возвращает "упакованный" ARGB uint - лерп таких значений напрямую через
		// float.Lerp смешивает не каналы цвета, а сырые числа целиком, что даёт визуальный мусор
		// (каналы интерферируют друг с другом). Поэтому распаковываем оба цвета в Vector4 (RGBA
		// по 0..1) и лерпим покомпонентно - так каналы смешиваются независимо и корректно.
		var overline = ImGui.ColorConvertU32ToFloat4(ImGui.GetColorU32(ImGuiCol.TabSelectedOverline));
		var windowBg = ImGui.ColorConvertU32ToFloat4(ImGui.GetColorU32(ImGuiCol.WindowBg));

		// "Слегка" - подмешиваем цвет выделения совсем небольшой долей (18%) поверх фона окна,
		// сохраняя альфу самого WindowBg (а не альфу overline-цвета, который обычно непрозрачен) -
		// иначе заливка могла бы стать более непрозрачной, чем исходный фон, и выглядеть как
		// сплошная перекраска, а не лёгкая подсветка.
		var blended = Vector4.Lerp(windowBg, overline, 0.05f);
		blended.W = windowBg.W;
		var color = ImGui.GetColorU32(blended);

		drawList.AddRectFilled(min, max, color, style.WindowRounding);
	}
}