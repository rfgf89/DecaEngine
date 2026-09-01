using System;
using System.Collections.Generic;
using System.Numerics;

namespace DecaEngine.Physics;

/// <summary>
/// Сборщик точек контакта для дебага. Контакты - единственное, что показывает, ПОЧЕМУ тело ведёт
/// себя так, а не иначе: висящее в воздухе тело с контактом в пустоте и лежащее тело без единого
/// контакта выглядят одинаково, пока контакты не нарисованы.
///
/// Живёт отдельным классом, а не полем в колбэках, по двум причинам. Во-первых,
/// <see cref="PhysicsNarrowPhaseCallbacks"/> - структура, которую Bepu копирует к себе, и любое
/// состояние в ней пришлось бы читать обратно через симуляцию. Во-вторых, узкая фаза идёт НА
/// ВОРКЕРАХ: общий список сюда писать нельзя, поэтому у каждого воркера свой, а слияние делает уже
/// один поток между шагами (см. <see cref="Flush"/>).
/// </summary>
public sealed class PhysicsContactRecorder
{
	public struct Contact
	{
		public Vector3 Position;
		public Vector3 Normal;
		public float Depth;

		/// <summary>Участвует ли в паре статик. Контакт со статикой (пол) и контакт двух тел
		/// (конечности рэгдолла между собой) - разные диагнозы, и различать их надо на глаз.</summary>
		public bool AgainstStatic;
	}

	/// <summary>Потолок на воркер. Сцена с рэгдоллом и мешем пола даёт сотни контактов за шаг, а за
	/// кадр шагов до восьми; без потолка список рос бы быстрее, чем его успевают рисовать.</summary>
	private const int MaxPerWorker = 512;

	private readonly List<Contact>[] _perWorker;
	private readonly List<Contact> _merged = new();

	/// <summary>Пишутся ли контакты вообще. Выключено - узкая фаза не платит ничего, кроме одной
	/// проверки поля: сбор контактов стоит чтения поз обоих коллайдеров на КАЖДУЮ пару, и держать
	/// его включённым постоянно нельзя.</summary>
	public bool Enabled;

	public PhysicsContactRecorder()
	{
		// По числу ядер: столько воркеров максимум заводит IThreadDispatcher, а лишние пустые списки
		// не стоят ничего. Индекс воркера всё равно проверяется - см. Record.
		_perWorker = new List<Contact>[Math.Max(1, Environment.ProcessorCount)];
		for (int i = 0; i < _perWorker.Length; i++)
		{
			_perWorker[i] = new List<Contact>();
		}
	}

	/// <summary>Контакты последнего <see cref="Flush"/>. Между шагами симуляции содержимое не
	/// обновляется - это снимок, а не живой список.</summary>
	public IReadOnlyList<Contact> Contacts => _merged;

	/// <summary>Сколько контактов отброшено потолком на последнем сливе - чтобы окно дебага могло
	/// честно сказать «показано не всё», а не тихо соврать полнотой картины.</summary>
	public int Dropped { get; private set; }

	internal void Record(int workerIndex, in Contact contact)
	{
		if ((uint)workerIndex >= (uint)_perWorker.Length)
		{
			return;
		}

		var list = _perWorker[workerIndex];
		if (list.Count >= MaxPerWorker)
		{
			return;
		}

		list.Add(contact);
	}

	/// <summary>Сливает буферы воркеров в один список и очищает их под следующий шаг. Звать между
	/// шагами симуляции, из ОДНОГО потока.</summary>
	public void Flush()
	{
		_merged.Clear();
		Dropped = 0;

		foreach (var list in _perWorker)
		{
			if (list.Count >= MaxPerWorker)
			{
				Dropped += list.Count - MaxPerWorker + 1;
			}

			_merged.AddRange(list);
			list.Clear();
		}
	}

	/// <summary>Забывает всё, не сливая. Нужен при выключении сбора: иначе на экране навсегда
	/// остался бы снимок последнего шага, в котором галочка ещё была включена.</summary>
	public void Clear()
	{
		_merged.Clear();
		Dropped = 0;

		foreach (var list in _perWorker)
		{
			list.Clear();
		}
	}
}
