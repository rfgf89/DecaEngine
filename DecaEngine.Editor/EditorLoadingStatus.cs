using System.Numerics;
using Hexa.NET.ImGui;

namespace DecaEngine.Editor
{
	/// <summary>
	/// Thread-safe registry of in-progress background operations (currently: async model loads driven
	/// by <see cref="DecaEngine.Graphics.ModelLoader.BeginLoadAsync"/>), rendered as a small status bar
	/// pinned to the bottom of the main viewport by <see cref="Render"/> (called once per frame from
	/// <see cref="EditorManager.OnUpdate"/>). Any window can register a task with <see cref="Begin"/> and
	/// must always pair it with <see cref="End"/> (typically in a try/finally or once polling shows the
	/// task finished), even on failure, so a stuck entry doesn't linger in the bar forever.
	/// </summary>
	public static class EditorLoadingStatus
	{
		public sealed class Handle
		{
			internal readonly int Id;
			public string Label;
			public float Progress; // 0..1, or negative for indeterminate

			internal Handle(int id, string label)
			{
				Id = id;
				Label = label;
			}
		}

		private static readonly object _sync = new();
		private static readonly List<Handle> _active = new();
		private static int _nextId;

		public static bool HasActiveTasks
		{
			get
			{
				lock (_sync) { return _active.Count > 0; }
			}
		}

		public static Handle Begin(string label)
		{
			lock (_sync)
			{
				var handle = new Handle(_nextId++, label) { Progress = -1f };
				_active.Add(handle);
				return handle;
			}
		}

		public static void End(Handle handle)
		{
			if (handle is null)
			{
				return;
			}

			lock (_sync)
			{
				_active.RemoveAll(h => h.Id == handle.Id);
			}
		}

		public static void Render(float uiScale)
		{
			List<Handle> snapshot;
			lock (_sync)
			{
				if (_active.Count == 0)
				{
					return;
				}
				snapshot = new List<Handle>(_active);
			}

			var viewport = ImGui.GetMainViewport();
			float barHeight = 28f * uiScale;
			var pos = new Vector2(viewport.Pos.X, viewport.Pos.Y + viewport.Size.Y - barHeight * 2 - 16f);
			var size = new Vector2(viewport.Size.X, barHeight);

			ImGui.SetNextWindowPos(pos);
			ImGui.SetNextWindowSize(size);
			ImGui.SetNextWindowBgAlpha(0.95f);
			// С ImGuiConfigFlags.ViewportsEnable (см. ImGuiManager) любое недокнутое top-level окно по
			// умолчанию превращается в СВОЁ отдельное нативное OS-окно вместо отрисовки поверх главного.
			// Явно привязываем окно к главному viewport, чтобы оно рисовалось внутри него, а не улетало
			// отдельным окном за пределы основного.
			ImGui.SetNextWindowViewport(viewport.ID);
			// Пересоздаётся только пока есть активные задачи (см. ранний return выше), а не каждый
			// кадр - поэтому ImGui не считает её "новым" окном при повторном появлении и не поднимает
			// её в топ z-порядка сама. Без явного фокуса любой клик по докнутым окнам редактора поднимал
			// их группу выше, и бар пропадал за основным UI.
			ImGui.SetNextWindowFocus();

			const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
				ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking |
				ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing;

			if (ImGui.Begin("##LoadingStatusBar", flags))
			{
				for (int i = 0; i < snapshot.Count; i++)
				{
					var handle = snapshot[i];
					if (i > 0)
					{
						ImGui.SameLine();
						ImGui.TextDisabled("|");
						ImGui.SameLine();
					}

					ImGui.Text(handle.Label);
					ImGui.SameLine();

					var barSize = new Vector2(320f * uiScale, ImGui.GetTextLineHeight());
					if (handle.Progress < 0f)
					{
						ImGui.ProgressBar(0f, barSize, "...");
					}
					else
					{
						ImGui.ProgressBar(handle.Progress, barSize);
					}
				}
			}
			ImGui.End();
		}
	}
}
