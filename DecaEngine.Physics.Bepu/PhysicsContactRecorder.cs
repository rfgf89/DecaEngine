using System;
using System.Collections.Generic;
using System.Numerics;

namespace DecaEngine.Physics;

/// <summary>Debug collector of contact points; narrow phase runs on workers, so buffers are per-worker.</summary>
public sealed class PhysicsContactRecorder
{
	public struct Contact
	{
		public Vector3 Position;
		public Vector3 Normal;
		public float Depth;

		public bool AgainstStatic;
	}

	// Per-worker cap: contacts accumulate faster than the debug view can draw them.
	private const int MaxPerWorker = 512;

	private readonly List<Contact>[] _perWorker;
	private readonly List<Contact> _merged = new();

	/// <summary>Off by default: recording reads both collider poses on every pair.</summary>
	public bool Enabled;

	public PhysicsContactRecorder()
	{
		// Sized by core count: that is the worker ceiling of IThreadDispatcher.
		_perWorker = new List<Contact>[Math.Max(1, Environment.ProcessorCount)];
		for (int i = 0; i < _perWorker.Length; i++)
		{
			_perWorker[i] = new List<Contact>();
		}
	}

	/// <summary>Snapshot taken by the last <see cref="Flush"/>, not a live list.</summary>
	public IReadOnlyList<Contact> Contacts => _merged;

	/// <summary>Contacts discarded by the per-worker cap during the last flush.</summary>
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

	/// <summary>Merges worker buffers; call between simulation steps, from one thread.</summary>
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

	/// <summary>Drops everything without merging; used when recording is turned off.</summary>
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
