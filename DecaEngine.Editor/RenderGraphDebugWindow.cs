#if DEBUG
using System.Numerics;
using DecaEngine.Core;
using Hexa.NET.ImGui;
using DecaEngine.Graphics;

namespace DecaEngine.Editor;

/// <summary>Debug-only window showing per-pass timings and resource lifetimes of a render graph.</summary>
public class RenderGraphDebugWindow : ImGuiDockingWindow
{
	private readonly List<RenderGraphDebugSnapshot> _historyBuffer = new(256);
	private readonly List<float> _frameTimesMs = new(256);

	private readonly List<GraphicsPipelineRegistry.Entry> _pipelines = new(8);
	private int _registryVersion = -1;

	private int _selectedId;
	private IGraphicsPipeline? _selectedPipeline;
	private string _selectedName = "";

	private readonly IGraphicsPipeline? _preferredPipeline;
	private bool _preferenceApplied;

	// Snapshot is adopted only every _refreshIntervalSec: per-frame numbers flicker unreadably.
	private RenderGraphDebugSnapshot? _displaySnapshot;
	private float _refreshIntervalSec = 0.5f;
	private float _timeSinceRefresh;
	private bool _freeze;

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
			ImGui.TextDisabled("No pipelines registered.");
			ImGui.TextWrapped("Pipelines add themselves to the registry in their own constructor (see GraphicsPipelineRegistry) - " +
			                  "an empty list means none has been created yet.");
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

		// The first snapshot is taken even while frozen: there is nothing on screen to hold yet.
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

	// Selection is tracked by registry Id, not index: previews are freed and recreated at runtime.
	private void RefreshPipelineList()
	{
		var version = GraphicsPipelineRegistry.Version;
		if (version != _registryVersion)
		{
			_registryVersion = GraphicsPipelineRegistry.CollectLive(_pipelines);
		}

		// Applied once, and only once it reaches the registry: the window may open before it exists.
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

		// Snapshot and history belong to one graph: dropped so another pipeline's frame never shows.
		_displaySnapshot = null;
		_timeSinceRefresh = _refreshIntervalSec;
		_frameTimesMs.Clear();
	}

	private void DrawPipelineSelector()
	{
		ImGui.SetNextItemWidth(320 * _scale);
		if (ImGui.BeginCombo("Pipeline", _pipelines.Count > 0 ? _selectedName : "<no pipelines>"))
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
			ImGui.SetTooltip("Which pipeline to show. The list is kept by GraphicsPipelineRegistry:\n" +
			                 "every pipeline registers itself there on creation.");
		}

		ImGui.SameLine();
		ImGui.TextDisabled($"({_pipelines.Count} live)");
	}

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

		// Disabled features keep their resources alive so re-enabling is free; this frees them.
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
				ImGui.SetTooltip("Frees the VRAM of disabled post effects and the graph's resource pool.\n" +
				                 "Enabling such a feature again recreates its resources and shaders.");
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

