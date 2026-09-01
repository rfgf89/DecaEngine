#if DEBUG
using System.Numerics;
using DecaEngine.Core;
using Hexa.NET.ImGui;
using DecaEngine.Graphics;

namespace DecaEngine.Editor;

/// <summary>
/// Debug-only editor window that visualizes a <see cref="IGraphicsPipeline"/>'s render graph:
/// per-pass CPU timing, draw call / triangle counts, and a resource lifetime matrix (a table with
/// one row per resource and one column per pass, showing exactly which passes keep it alive).
/// Never compiled into Release builds.
///
/// Конвейер не передаётся снаружи, а ВЫБИРАЕТСЯ в самом окне из <see cref="GraphicsPipelineRegistry"/>:
/// в редакторе их одновременно живёт несколько (основная сцена на swap chain, превью модели в
/// инспекторе, вьюпорт префаба, запекание иконок, офскрин-пробы), и каждый регистрируется сам в
/// своём конструкторе. Список пересобирается, только когда состав реестра изменился, а выбор
/// запоминается по стабильному Id записи, а не по индексу - иначе пересоздание любого превью
/// (RecreateEnvironment) молча переключало бы окно на чужой граф.
/// </summary>
public class RenderGraphDebugWindow : ImGuiDockingWindow
{
	private readonly List<RenderGraphDebugSnapshot> _historyBuffer = new(256);
	private readonly List<float> _frameTimesMs = new(256);

	// Живые конвейеры реестра + версия состава, под которую этот список набран.
	private readonly List<GraphicsPipelineRegistry.Entry> _pipelines = new(8);
	private int _registryVersion = -1;

	private int _selectedId;
	private IGraphicsPipeline? _selectedPipeline;
	private string _selectedName = "";

	/// <summary>Конвейер, который надо выбрать первым, если он есть в реестре (иначе - первый живой).</summary>
	private readonly IGraphicsPipeline? _preferredPipeline;
	private bool _preferenceApplied;

	// The underlying render graph refreshes its debug snapshot every single frame, which makes the
	// per-pass ms numbers flicker too fast to actually read. Instead we only "adopt" a new snapshot
	// for display every _refreshIntervalSec seconds, so the numbers hold still long enough to read.
	private RenderGraphDebugSnapshot? _displaySnapshot;
	private float _refreshIntervalSec = 0.5f;
	private float _timeSinceRefresh;
	private bool _freeze;

	/// <param name="preferredPipeline">Необязательный конвейер, на котором окно откроется, если он
	/// зарегистрирован. Null - откроется на первом живом из реестра.</param>
	public RenderGraphDebugWindow(string name, ImGuiRender imGuiRender, IGraphicsPipeline? preferredPipeline = null)
		: base(name, imGuiRender)
	{
		_preferredPipeline = preferredPipeline;
	}

	protected override void OnRender(uint dockId)
	{
		RefreshPipelineList();
		DrawPipelineSelector();

		if (_selectedPipeline == null)
		{
			ImGui.TextDisabled("Ни одного конвейера не зарегистрировано.");
			ImGui.TextWrapped("Конвейеры встают в реестр сами, в своём конструкторе (см. GraphicsPipelineRegistry) - " +
			                  "пустой список означает, что ни один ещё не создан.");
			return;
		}

		ImGui.Separator();

		var liveSnap = _selectedPipeline.DebugSnapshot;
		if (liveSnap == null)
		{
			ImGui.TextDisabled("No frame recorded yet.");
			return;
		}

		_timeSinceRefresh += ImGui.GetIO().DeltaTime;

		// Первый снимок берётся ДАЖЕ при включённом Freeze: показывать нечего, а переключение
		// конвейера обнуляет показанный снимок (он принадлежал чужому графу). Дальше Freeze работает
		// как и раньше - удерживает то, что уже на экране.
		if (_displaySnapshot == null || (!_freeze && _timeSinceRefresh >= _refreshIntervalSec))
		{
			_displaySnapshot = liveSnap;
			_timeSinceRefresh = 0f;
		}

		var snap = _displaySnapshot;

		DrawOverviewPanel(snap);

		ImGui.Spacing();

		if (ImGui.BeginTabBar("RenderGraphTabs"))
		{
			if (ImGui.BeginTabItem("Passes"))
			{
				ImGui.Spacing();
				DrawPassTable(snap);
				ImGui.Spacing();
				ImGui.TextUnformatted("Relative CPU cost per pass:");
				DrawPassBars(snap);
				ImGui.EndTabItem();
			}

			if (ImGui.BeginTabItem("Resource Lifetimes"))
			{
				ImGui.Spacing();
				DrawResourceLifetimeTable(snap);
				ImGui.EndTabItem();
			}

			ImGui.EndTabBar();
		}
	}

	/// <summary>Пересобирает список живых конвейеров, только если состав реестра изменился, и следит,
	/// чтобы выбор остался валидным: выбранный конвейер могли освободить (пересоздание превью-окружения
	/// освобождает старый и создаёт новый) - тогда падаем на первый живой.</summary>
	private void RefreshPipelineList()
	{
		var version = GraphicsPipelineRegistry.Version;
		if (version != _registryVersion)
		{
			_registryVersion = GraphicsPipelineRegistry.CollectLive(_pipelines);
		}

		// Предпочтительный конвейер применяем один раз - и только когда он реально доехал до реестра
		// (окно могли открыть раньше, чем конвейер создан).
		if (!_preferenceApplied && _preferredPipeline != null)
		{
			foreach (var entry in _pipelines)
			{
				if (ReferenceEquals(entry.Pipeline, _preferredPipeline))
				{
					SelectPipeline(entry);
					_preferenceApplied = true;
					break;
				}
			}
		}

		foreach (var entry in _pipelines)
		{
			if (entry.Id == _selectedId)
			{
				// Имя записи могло измениться (реестр разводит одинаковые имена суффиксом).
				_selectedPipeline = entry.Pipeline;
				_selectedName = entry.Name;
				return;
			}
		}

		if (_pipelines.Count > 0)
		{
			SelectPipeline(_pipelines[0]);
		}
		else
		{
			_selectedId = 0;
			_selectedPipeline = null;
			_selectedName = "";
			_displaySnapshot = null;
		}
	}

	private void SelectPipeline(GraphicsPipelineRegistry.Entry entry)
	{
		if (entry.Id == _selectedId)
		{
			return;
		}

		_selectedId = entry.Id;
		_selectedPipeline = entry.Pipeline;
		_selectedName = entry.Name;

		// Снимок и график принадлежат КОНКРЕТНОМУ графу - иначе после переключения кадр чужого
		// конвейера продолжал бы висеть на экране до следующего обновления.
		_displaySnapshot = null;
		_timeSinceRefresh = _refreshIntervalSec;
		_frameTimesMs.Clear();
	}

	private void DrawPipelineSelector()
	{
		ImGui.SetNextItemWidth(320 * _scale);
		if (ImGui.BeginCombo("Pipeline", _pipelines.Count > 0 ? _selectedName : "<нет конвейеров>"))
		{
			foreach (var entry in _pipelines)
			{
				bool selected = entry.Id == _selectedId;
				if (ImGui.Selectable(entry.Name, selected))
				{
					SelectPipeline(entry);
				}

				if (selected)
				{
					ImGui.SetItemDefaultFocus();
				}
			}

			ImGui.EndCombo();
		}

		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip("Какой конвейер показывать. Список ведёт GraphicsPipelineRegistry:\n" +
			                 "каждый конвейер регистрируется в нём сам при создании.");
		}

		ImGui.SameLine();
		ImGui.TextDisabled($"({_pipelines.Count} live)");
	}

	/// <summary>
	/// Top summary block: frame stats, refresh-rate controls and the CPU-time history graph, all
	/// grouped into a single bordered panel so they read as one "at a glance" unit instead of being
	/// scattered loose lines above the tabbed tables.
	/// </summary>
	private void DrawOverviewPanel(RenderGraphDebugSnapshot snap)
	{
		ImGui.BeginChild("OverviewPanel", new Vector2(-1, 150 * _scale), ImGuiChildFlags.Borders);

		ImGui.Text($"Passes: {snap.Passes.Length}");
		ImGui.SameLine();
		ImGui.TextDisabled("|");
		ImGui.SameLine();
		ImGui.Text($"CPU record time: {snap.TotalCpuMs:F3} ms");
		ImGui.SameLine();
		ImGui.TextDisabled("|");
		ImGui.SameLine();
		ImGui.Text($"Graph VRAM (approx): {snap.TotalResourceMemoryBytes / (1024.0 * 1024.0):F2} MB");

		// Ресурсы выключенных фич конвейер держит наготове, чтобы повторное включение было
		// бесплатным (см. GraphicsPipelineSimple.SetFeatures) - здесь их можно отдать обратно.
		if (_selectedPipeline is GraphicsPipelineSimple simple)
		{
			ImGui.SameLine();
			ImGui.TextDisabled("|");
			ImGui.SameLine();
			if (ImGui.SmallButton("Release disabled features"))
			{
				simple.ReleaseDisabledResources();
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Освобождает VRAM выключенных пост-эффектов и пул ресурсов графа.\n" +
				                 "Следующее включение такой фичи снова создаст её ресурсы и шейдеры.");
			}
		}

		ImGui.SetNextItemWidth(160 * _scale);
		ImGui.SliderFloat("Refresh interval (s)", ref _refreshIntervalSec, 0.1f, 2f, "%.2f");
		ImGui.SameLine();
		ImGui.Checkbox("Freeze", ref _freeze);
		ImGui.SameLine();
		ImGui.TextDisabled(_freeze ? "(paused)" : $"(next update in {MathF.Max(0f, _refreshIntervalSec - _timeSinceRefresh):F2}s)");

		DrawFrameHistoryGraph();

		ImGui.EndChild();
	}

	private void DrawFrameHistoryGraph()
	{
		var history = _selectedPipeline?.DebugHistory;
		if (history == null || history.Count == 0)
		{
			return;
		}

		history.CopyTo(_historyBuffer);
		_frameTimesMs.Clear();
		foreach (var s in _historyBuffer)
		{
			_frameTimesMs.Add((float)s.TotalCpuMs);
		}

		if (_frameTimesMs.Count > 0)
		{
			var values = _frameTimesMs.ToArray();
			ImGui.PlotLines(
				"##FrameHistory",
				ref values[0],
				values.Length,
				0,
				$"CPU record time (last {values.Length} frames)",
				0f,
				float.MaxValue,
				new Vector2(-1, 80 * _scale));
		}
	}

	private void DrawPassTable(RenderGraphDebugSnapshot snap)
	{
		const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
		                               ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY |
		                               ImGuiTableFlags.SizingStretchProp;

		ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(8, 6) * _scale);

		if (!ImGui.BeginTable("RenderGraphPassTable", 6, flags, new Vector2(0, 260 * _scale)))
		{
			ImGui.PopStyleVar();
			return;
		}

		ImGui.TableSetupColumn("Pass", ImGuiTableColumnFlags.WidthStretch, 3f);
		ImGui.TableSetupColumn("CPU ms", ImGuiTableColumnFlags.WidthStretch, 1.2f);
		ImGui.TableSetupColumn("Draws", ImGuiTableColumnFlags.WidthStretch, 1.2f);
		ImGui.TableSetupColumn("Dispatches", ImGuiTableColumnFlags.WidthStretch, 1.2f);
		ImGui.TableSetupColumn("Transitions", ImGuiTableColumnFlags.WidthStretch, 1.2f);
		ImGui.TableSetupColumn("Triangles", ImGuiTableColumnFlags.WidthStretch, 1.2f);
		ImGui.TableHeadersRow();

		foreach (var p in snap.Passes)
		{
			ImGui.TableNextRow(ImGuiTableRowFlags.None, 26f * _scale);
			ImGui.TableSetColumnIndex(0);
			ImGui.TextUnformatted(p.Name);
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip($"Reads: {(p.ReadResources.Length > 0 ? string.Join(", ", p.ReadResources) : "-")}\n" +
				                  $"Writes: {(p.WriteResources.Length > 0 ? string.Join(", ", p.WriteResources) : "-")}");
			}

			ImGui.TableSetColumnIndex(1); ImGui.Text($"{p.CpuMs:F3}");
			ImGui.TableSetColumnIndex(2); ImGui.Text($"{p.DrawCalls}");
			ImGui.TableSetColumnIndex(3); ImGui.Text($"{p.DispatchCalls}");
			ImGui.TableSetColumnIndex(4); ImGui.Text($"{p.TransitionCount}");
			ImGui.TableSetColumnIndex(5); ImGui.Text($"{p.TriangleCount}");
		}

		ImGui.EndTable();
		ImGui.PopStyleVar();
	}

	/// <summary>
	/// Draws a single horizontal stacked bar (a "flame bar") instead of one progress bar per pass:
	/// every pass eats from the same total CPU record time, so a stack of segments whose widths sum
	/// to 100% communicates the relative cost far more directly than N separate 0-100% bars.
	/// </summary>
	private void DrawPassBars(RenderGraphDebugSnapshot snap)
	{
		if (snap.Passes.Length == 0)
		{
			return;
		}

		float barHeight = 28f * _scale;
		var drawList = ImGui.GetWindowDrawList();
		var origin = ImGui.GetCursorScreenPos();
		float availWidth = MathF.Max(ImGui.GetContentRegionAvail().X, 1f);

		drawList.AddRectFilled(origin, origin + new Vector2(availWidth, barHeight), ImGui.GetColorU32(ImGuiCol.FrameBg));

		float x = 0f;
		bool anyHovered = false;
		var mouse = ImGui.GetMousePos();

		for (int i = 0; i < snap.Passes.Length; i++)
		{
			var p = snap.Passes[i];
			float frac = snap.TotalCpuMs > 0 ? (float)(p.CpuMs / snap.TotalCpuMs) : 0f;
			float segWidth = frac * availWidth;
			if (segWidth <= 0f)
			{
				continue;
			}

			var min = origin + new Vector2(x, 0f);
			var max = origin + new Vector2(x + segWidth, barHeight);

			uint color = PassSegmentColor(i);
			drawList.AddRectFilled(min, max, color);
			drawList.AddRect(min, max, ImGui.GetColorU32(ImGuiCol.Border));

			// Only draw the label if the segment is wide enough to hold it, otherwise rely on the tooltip.
			var label = $"{p.Name} {frac * 100f:F0}%";
			var labelSize = ImGui.CalcTextSize(label);
			if (labelSize.X < segWidth - 4f)
			{
				var textPos = min + new Vector2((segWidth - labelSize.X) * 0.5f, (barHeight - labelSize.Y) * 0.5f);
				drawList.AddText(textPos, ImGui.GetColorU32(ImGuiCol.Text), label);
			}

			if (mouse.X >= min.X && mouse.X < max.X && mouse.Y >= min.Y && mouse.Y < max.Y)
			{
				anyHovered = true;
				ImGui.SetTooltip($"{p.Name}\n{p.CpuMs:F3} ms ({frac * 100f:F1}% of frame)");
			}

			x += segWidth;
		}

		ImGui.Dummy(new Vector2(availWidth, barHeight));

		if (!anyHovered)
		{
			ImGui.TextDisabled("Hover a segment to see the exact pass / ms / percentage.");
		}
	}

	private static uint PassSegmentColor(int index)
	{
		// A small fixed, high-contrast, colorblind-friendlyish palette, cycling per pass index.
		Span<Vector4> palette =
		[
			new Vector4(0.30f, 0.55f, 0.90f, 0.85f),
			new Vector4(0.90f, 0.55f, 0.20f, 0.85f),
			new Vector4(0.35f, 0.75f, 0.40f, 0.85f),
			new Vector4(0.85f, 0.35f, 0.55f, 0.85f),
			new Vector4(0.55f, 0.45f, 0.85f, 0.85f),
			new Vector4(0.85f, 0.80f, 0.25f, 0.85f),
			new Vector4(0.30f, 0.75f, 0.75f, 0.85f),
		];

		return ImGui.GetColorU32(palette[index % palette.Length]);
	}

	/// <summary>
	/// Renders resource lifetimes as a single table: one row per resource, one fixed-width column
	/// per pass. The cell range between a resource's first and last used pass is highlighted, with
	/// a distinct color/marker for the birth (first use / allocation) and death (last use) cells,
	/// so the "lifeline" is unambiguous at a glance instead of relying on hand-placed pixel rects.
	/// All sizes are scaled by the current editor DPI scale so cells stay legible on any monitor.
	/// </summary>
	private void DrawResourceLifetimeTable(RenderGraphDebugSnapshot snap)
	{
		int passCount = snap.Passes.Length;
		if (passCount == 0 || snap.Resources.Length == 0)
		{
			ImGui.TextColored(new Vector4(0.95f, 0.75f, 0.2f, 1f), "No resources are declared in this render graph.");
			ImGui.TextWrapped(
				"Expected for the swap-chain pipeline: ShadowPass and ForwardPass draw straight to the " +
				"back buffer and to shared batch-renderer buffers, which the graph does not own. " +
				"Nothing is being \"leaked\" or left un-deallocated - there is simply no tracked resource " +
				"to show a lifetime for. The off-screen pipeline declares its targets via " +
				"builder.ImportTexture(...) + ReadTarget/WriteTarget(...) (see ForwardPass.Setup), and " +
				"graph-owned transients would use builder.PinTexture(...) the same way.");
			return;
		}

		ImGui.BeginChild("LifetimeLegend", new Vector2(-1, 52 * _scale), ImGuiChildFlags.Borders);
		ImGui.TextUnformatted("Legend:");
		ImGui.SameLine();
		ImGui.TextColored(new Vector4(0.25f, 0.85f, 0.35f, 1f), "\u25B6 first use / allocated");
		ImGui.SameLine();
		ImGui.TextColored(new Vector4(0.95f, 0.35f, 0.3f, 1f), "\u25A0 last use");
		ImGui.TextColored(new Vector4(0.7f, 0.4f, 0.9f, 1f), "\u25C6 allocated and last used in the same pass");
		ImGui.SameLine();
		ImGui.TextColored(new Vector4(0.35f, 0.6f, 0.95f, 1f), "\u2591 alive in between");
		ImGui.EndChild();

		ImGui.Spacing();

		const int fixedColumns = 2;
		int totalColumns = fixedColumns + passCount;

		const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
		                               ImGuiTableFlags.ScrollX | ImGuiTableFlags.ScrollY |
		                               ImGuiTableFlags.SizingFixedFit;

		ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(6, 8) * _scale);

		if (!ImGui.BeginTable("ResourceLifetimeTable", totalColumns, flags, new Vector2(0, 320 * _scale)))
		{
			ImGui.PopStyleVar();
			return;
		}

		ImGui.TableSetupScrollFreeze(fixedColumns, 1);
		ImGui.TableSetupColumn("Resource", ImGuiTableColumnFlags.WidthFixed, 260f * _scale);
		ImGui.TableSetupColumn("Refs", ImGuiTableColumnFlags.WidthFixed, 70f * _scale);
		for (int c = 0; c < passCount; c++)
		{
			ImGui.TableSetupColumn($"P{c}", ImGuiTableColumnFlags.WidthFixed, 46f * _scale);
		}

		ImGui.TableNextRow(ImGuiTableRowFlags.Headers, 30f * _scale);
		ImGui.TableSetColumnIndex(0); ImGui.TableHeader("Resource");
		ImGui.TableSetColumnIndex(1); ImGui.TableHeader("Refs");
		for (int c = 0; c < passCount; c++)
		{
			ImGui.TableSetColumnIndex(fixedColumns + c);
			ImGui.TableHeader($"{c}");
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip(snap.Passes[c].Name);
			}
		}

		uint aliveColor = ImGui.GetColorU32(new Vector4(0.35f, 0.6f, 0.95f, 0.45f));
		uint startColor = ImGui.GetColorU32(new Vector4(0.25f, 0.85f, 0.35f, 0.85f));
		uint endColor = ImGui.GetColorU32(new Vector4(0.95f, 0.35f, 0.3f, 0.85f));
		uint startEndColor = ImGui.GetColorU32(new Vector4(0.7f, 0.4f, 0.9f, 0.85f));

		foreach (var r in snap.Resources)
		{
			ImGui.TableNextRow(ImGuiTableRowFlags.None, 28f * _scale);

			ImGui.TableSetColumnIndex(0);
			ImGui.TextUnformatted(r.Name);
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip($"Type: {(r.IsBuffer ? "Buffer" : "Texture")}\nSize: {r.SizeInBytes / 1024.0:F1} KB");
			}

			ImGui.TableSetColumnIndex(1);
			ImGui.Text($"{r.RefCount}");

			int first = Math.Clamp(r.FirstPassIndex, 0, passCount - 1);
			int last = Math.Clamp(r.LastPassIndex, first, passCount - 1);

			for (int c = 0; c < passCount; c++)
			{
				ImGui.TableSetColumnIndex(fixedColumns + c);
				if (c < first || c > last)
				{
					continue;
				}

				bool isStart = c == first;
				bool isEnd = c == last;
				uint cellColor = isStart && isEnd ? startEndColor : isStart ? startColor : isEnd ? endColor : aliveColor;
				ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, cellColor);

				// Center the marker glyph within the (now much wider) cell.
				var cellWidth = ImGui.GetColumnWidth();
				string glyph = isStart && isEnd ? "\u25C6" : isStart ? "\u25B6" : isEnd ? "\u25A0" : "";
				if (glyph.Length > 0)
				{
					var textWidth = ImGui.CalcTextSize(glyph).X;
					ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (cellWidth - textWidth) * 0.5f));
					ImGui.TextUnformatted(glyph);
				}
			}
		}

		ImGui.EndTable();
		ImGui.PopStyleVar();
	}
}
#endif

