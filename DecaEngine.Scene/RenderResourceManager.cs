using System.Numerics;
using DecaEngine.Graphics;
using Friflo.Engine.ECS;
using UnsafeCollections.Collections.Native;
using UnsafeCollections.Collections.Unsafe;

namespace DecaEngine.Scene;

public class RenderResourceManager
{
	private readonly IBatchRenderer _batchRenderer;
	private readonly NativeStack<int> _freeSlots;
	private readonly EntityStore _store;
	private int[] _batchCounts;

	public int totalInstances;
	public int totalFreeSlot;
	public bool cullFrustum;

	private BatchSubset _renderSubset;

	// Highest occupied slot INDEX + 1, not the occupied count: slots are sparse after churn,
	// and the culling shader (BatchingInstancingCS) walks Instances[0..drawCount) skipping
	// holes by batchId/objectId < 0, so drawCount must be an index bound.
	private int _slotHighWaterMark;

	/// <summary>Instance-array bound the culling shader must walk; goes into CullData.drawCount.</summary>
	public int DrawInstanceCount => _slotHighWaterMark;

	public RenderResourceManager(int totalInstances, int totalBatch, EntityStore store,
		IBatchRenderer batchRenderer)
	{
		_store = store;
		_batchRenderer = batchRenderer;
		this.totalInstances = totalInstances;
		_batchCounts = new int[totalBatch];

		_renderSubset = new BatchSubset
		{
			instances = new NativeArray<IndirectInstance>(totalInstances),
			gpuData = new NativeArray<GPURenderInstance>(totalInstances),
			drawData = new NativeArray<DrawData>(totalInstances),

			countData = new NativeArray<int>(totalBatch)
		};

		for (var i = 0; i < totalInstances; i++)
		{
			_renderSubset.instances[i] = new IndirectInstance { batchId = new BatchId(-1), objectId = -1 };
		}

		_batchRenderer.PinInstances(_renderSubset);

		_freeSlots = new NativeStack<int>(totalInstances);

		for (var i = 0; i < totalInstances; i++)
		{
			_freeSlots.Push(totalInstances - 1 - i);
		}

		// Reclaims GPU slots when an entity with BatchRenderInfo is deleted directly, bypassing
		// UnregisterRenderable. OnEntityDelete fires BEFORE deletion (components still readable,
		// per Friflo docs); already-unregistered entities fail TryGetComponent, so no double free.
		_store.OnEntityDelete += OnEntityDeleting;
	}

	private void OnEntityDeleting(EntityDelete args)
	{
		if (args.Entity.TryGetComponent<BatchRenderInfo>(out var batchInfo))
		{
			FreeSlot(batchInfo.GpuSlotIndex, batchInfo.BatchId);
		}
	}

	/// <summary>Shrinks the dispatch bound over the freed tail; idempotent, called from FreeSlot.</summary>
	public void RecycleDeadHandles()
	{
		while (_slotHighWaterMark > 0 && _renderSubset.instances[_slotHighWaterMark - 1].batchId.batchId < 0)
		{
			_slotHighWaterMark--;
		}
	}

	// Single release path for one GPU instance slot; the instance record must be reset here,
	// otherwise the culling shader keeps reading a stale instance at that index.
	private unsafe void FreeSlot(int slotIndex, BatchId batchId)
	{
		_freeSlots.Push(slotIndex);
		totalFreeSlot = _freeSlots.Count;

		_renderSubset.instances[slotIndex] = new IndirectInstance { batchId = new BatchId(-1), objectId = -1 };

		if (batchId.batchId >= 0 && batchId.batchId < _batchCounts.Length)
		{
			_batchCounts[batchId.batchId]--;
			for (var i = batchId.batchId + 1; i < _batchCounts.Length; i++)
			{
				_renderSubset.countData.GetRef(i)--;
			}
		}

		RecycleDeadHandles();

		_batchRenderer.MarkInstancesContentDirty();
	}

	public unsafe bool RegisterRenderable(Entity entity, BatchId batchId)
	{
		if (_freeSlots.Count == 0)
		{
			int newInstances = totalInstances == 0 ? 128 : totalInstances * 2;
			var newInstancesArray = new NativeArray<IndirectInstance>(newInstances);
			var newGpuData = new NativeArray<GPURenderInstance>(newInstances);
			var newDrawData = new NativeArray<DrawData>(newInstances);

			if (totalInstances > 0)
			{
				NativeArray<IndirectInstance>.Copy(_renderSubset.instances, newInstancesArray);
				NativeArray<GPURenderInstance>.Copy(_renderSubset.gpuData, newGpuData);
				NativeArray<DrawData>.Copy(_renderSubset.drawData, newDrawData);

				_renderSubset.instances.Dispose();
				_renderSubset.gpuData.Dispose();
				_renderSubset.drawData.Dispose();
			}

			for (var i = totalInstances; i < newInstances; i++)
			{
				newInstancesArray[i] = new IndirectInstance { batchId = new BatchId(-1), objectId = -1 };
				_freeSlots.Push(newInstances - 1 - (i - totalInstances));
			}

			_renderSubset.instances = newInstancesArray;
			_renderSubset.gpuData = newGpuData;
			_renderSubset.drawData = newDrawData;
			totalInstances = newInstances;

			if (totalInstances > 0)
			{
				_store.Query<BatchRenderInfo, LinkDrawInfo>().ForEachEntity((ref BatchRenderInfo batchInfo, ref LinkDrawInfo linkInfo, Entity ent) =>
				{
					linkInfo.renderInstance = UnsafeArray.GetPtr<GPURenderInstance>(_renderSubset.gpuData.GetNative(), batchInfo.GpuSlotIndex);
					linkInfo.drawData = UnsafeArray.GetPtr<DrawData>(_renderSubset.drawData.GetNative(), batchInfo.GpuSlotIndex);
				});
			}

			_batchRenderer.PinInstances(_renderSubset);
		}

		var slotIndex = _freeSlots.Pop();
		totalFreeSlot = _freeSlots.Count;

		if (slotIndex + 1 > _slotHighWaterMark)
		{
			_slotHighWaterMark = slotIndex + 1;
		}

		_renderSubset.instances[slotIndex] = new IndirectInstance
		{
			batchId = batchId,
			objectId = slotIndex
		};

		if (batchId.batchId >= 0 && batchId.batchId >= _batchCounts.Length)
		{
			int oldBatchCountsLength = _batchCounts.Length;
			int newBatchCountsLength = _batchCounts.Length == 0 ? 128 : _batchCounts.Length;
			while (batchId.batchId >= newBatchCountsLength)
			{
				newBatchCountsLength *= 2;
			}

			Array.Resize(ref _batchCounts, newBatchCountsLength);
			var newCountData = new NativeArray<int>(newBatchCountsLength);

			if (_renderSubset.countData.Length > 0)
			{
				NativeArray<int>.Copy(_renderSubset.countData, newCountData);
				_renderSubset.countData.Dispose();
			}

			// countData is a prefix sum (start slot of batch i = instance count of batches < i).
			// New tail entries must start at the current total, not zero, or new batches would
			// get offsets overlapping the first batches' ranges.
			int registeredTotal = 0;
			for (int i = 0; i < oldBatchCountsLength; i++)
			{
				registeredTotal += _batchCounts[i];
			}
			for (int i = oldBatchCountsLength; i < newBatchCountsLength; i++)
			{
				newCountData.GetRef(i) = registeredTotal;
			}

			_renderSubset.countData = newCountData;
			_batchRenderer.PinInstances(_renderSubset);
		}

		if (batchId.batchId >= 0)
		{
			_batchCounts[batchId.batchId]++;

			for (var i = batchId.batchId + 1; i < _batchCounts.Length; i++)
			{
				_renderSubset.countData.GetRef(i)++;
			}
		}

		entity.Add(new BatchRenderInfo
			{
				BatchId = batchId,
				GpuSlotIndex = slotIndex
			},
			new LinkDrawInfo
			{
				renderInstance = UnsafeArray.GetPtr<GPURenderInstance>(_renderSubset.gpuData.GetNative(), slotIndex),
				drawData = UnsafeArray.GetPtr<DrawData>(_renderSubset.drawData.GetNative(), slotIndex)
			});

		_batchRenderer.MarkInstancesContentDirty();

		return true;
	}

	public unsafe void UnregisterRenderable(Entity entity)
	{
		if (entity.TryGetComponent<BatchRenderInfo>(out var batchInfo))
		{
			FreeSlot(batchInfo.GpuSlotIndex, batchInfo.BatchId);

			entity.RemoveComponent<BatchRenderInfo>();
			entity.RemoveComponent<LinkDrawInfo>();
		}
	}
}
