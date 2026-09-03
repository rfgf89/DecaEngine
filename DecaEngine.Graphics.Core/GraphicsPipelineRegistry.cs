namespace DecaEngine.Graphics;

/// <summary>Registry of live <see cref="IGraphicsPipeline"/> instances; each pipeline registers
/// itself in its constructor. Entries hold weak references, so the registry never extends a
/// pipeline's lifetime. Thread-safe: pipelines are created on the render thread, UI reads.</summary>
public static class GraphicsPipelineRegistry
{
	/// <summary>Live entry: pipeline + display name. <see cref="Id"/> is stable for the whole run;
	/// UI must remember selection by it, not by list index.</summary>
	public readonly struct Entry(int id, string name, IGraphicsPipeline pipeline)
	{
		public int Id { get; } = id;
		public string Name { get; } = name;
		public IGraphicsPipeline Pipeline { get; } = pipeline;
	}

	private sealed class Slot(int id, string name, IGraphicsPipeline pipeline)
	{
		public readonly int Id = id;
		public readonly string Name = name;
		public readonly WeakReference<IGraphicsPipeline> Pipeline = new(pipeline);
	}

	private static readonly object Gate = new();
	private static readonly List<Slot> Slots = new();
	private static int _nextId = 1;
	private static int _version;

	/// <summary>Change counter; UI can cache its list and rebuild only when this differs.</summary>
	public static int Version => Volatile.Read(ref _version);

	/// <summary>Registers a pipeline; duplicate names get a " #N" suffix.</summary>
	public static void Register(IGraphicsPipeline pipeline, string? name)
	{
		ArgumentNullException.ThrowIfNull(pipeline);

		var baseName = string.IsNullOrWhiteSpace(name) ? pipeline.GetType().Name : name!.Trim();

		lock (Gate)
		{
			PruneDead();

			var unique = baseName;
			for (int suffix = 2; IsNameTaken(unique); suffix++)
			{
				unique = $"{baseName} #{suffix}";
			}

			Slots.Add(new Slot(_nextId++, unique, pipeline));
			_version++;
		}
	}

	/// <summary>Removes a pipeline; optional (weak refs self-clean), but makes it leave UI immediately.</summary>
	public static void Unregister(IGraphicsPipeline pipeline)
	{
		if (pipeline is null)
		{
			return;
		}

		lock (Gate)
		{
			for (int i = Slots.Count - 1; i >= 0; i--)
			{
				if (!Slots[i].Pipeline.TryGetTarget(out var target) || ReferenceEquals(target, pipeline))
				{
					Slots.RemoveAt(i);
					_version++;
				}
			}
		}
	}

	/// <summary>Fills <paramref name="destination"/> with live entries and returns the
	/// <see cref="Version"/> at collection time.</summary>
	public static int CollectLive(List<Entry> destination)
	{
		ArgumentNullException.ThrowIfNull(destination);

		destination.Clear();

		lock (Gate)
		{
			PruneDead();

			foreach (var slot in Slots)
			{
				if (slot.Pipeline.TryGetTarget(out var pipeline))
				{
					destination.Add(new Entry(slot.Id, slot.Name, pipeline));
				}
			}

			return _version;
		}
	}

	// Must be called under Gate.
	private static void PruneDead()
	{
		for (int i = Slots.Count - 1; i >= 0; i--)
		{
			if (!Slots[i].Pipeline.TryGetTarget(out _))
			{
				Slots.RemoveAt(i);
				_version++;
			}
		}
	}

	// Must be called under Gate, after PruneDead.
	private static bool IsNameTaken(string name)
	{
		foreach (var slot in Slots)
		{
			if (string.Equals(slot.Name, name, StringComparison.Ordinal))
			{
				return true;
			}
		}

		return false;
	}
}
