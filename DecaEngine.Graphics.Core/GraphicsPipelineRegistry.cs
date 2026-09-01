namespace DecaEngine.Graphics;

/// <summary>Реестр ЖИВЫХ конвейеров (<see cref="IGraphicsPipeline"/>). Каждый конвейер регистрируется
/// в нём САМ, в своём конструкторе - поэтому окну отладки рендер-графа
/// (<c>RenderGraphDebugWindow</c>) не нужно ничего знать о том, кто и когда создаёт конвейеры: оно
/// просто показывает список и даёт выбрать, чей граф смотреть. А их в редакторе много и живут они
/// по-разному: основная сцена на swap chain, превью модели в инспекторе, вьюпорт префаба, запекание
/// иконок, офскрин-пробы.
///
/// Записи держат конвейер СЛАБОЙ ссылкой: реестр не продлевает жизнь ни одному из них и не требует
/// дисциплины отписки - выброшенный сборщиком конвейер сам исчезает из списка. Явный
/// <see cref="Unregister"/> в <c>Release</c> нужен лишь для того, чтобы освобождённый конвейер
/// пропал из UI сразу, а не когда-нибудь.
///
/// Потокобезопасен: конвейеры создаются с потока рендера, а читает список UI.</summary>
public static class GraphicsPipelineRegistry
{
	/// <summary>Живая запись реестра: конвейер + его отображаемое имя. <see cref="Id"/> стабилен и
	/// уникален на весь запуск - именно по нему UI помнит выбор, а не по индексу в списке (список
	/// перестраивается на каждое создание/уничтожение конвейера).</summary>
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

	/// <summary>Счётчик изменений состава реестра. UI может держать свою копию списка и перестраивать
	/// её только когда версия изменилась, вместо аллокации на каждый кадр.</summary>
	public static int Version => Volatile.Read(ref _version);

	/// <summary>Ставит конвейер в реестр. Зовётся конвейером из собственного конструктора.
	/// <paramref name="name"/> - то, что увидит пользователь в выпадающем списке; повторяющиеся имена
	/// разводятся суффиксом (" #2", " #3", ...), чтобы два превью с одинаковым именем не выглядели в
	/// UI одним и тем же.</summary>
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

	/// <summary>Убирает конвейер из реестра. Не обязателен для корректности (слабые ссылки вычистятся
	/// сами), но позволяет освобождённому конвейеру исчезнуть из UI немедленно - см. <c>Release</c>.</summary>
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

	/// <summary>Складывает живые записи в <paramref name="destination"/> (список очищается) и
	/// возвращает <see cref="Version"/> на момент сбора - вызывающий сохраняет её и повторяет сбор
	/// только когда версия разошлась.</summary>
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

	/// <summary>Выкидывает записи, чей конвейер уже собран сборщиком мусора. Зовётся под <see cref="Gate"/>.</summary>
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

	/// <summary>Зовётся под <see cref="Gate"/>, уже после <see cref="PruneDead"/>.</summary>
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
