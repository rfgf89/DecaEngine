using System.Numerics;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics.Diligent;
using Friflo.Engine.ECS;
using UnsafeCollections.Collections.Native;
using UnsafeCollections.Collections.Unsafe;

namespace DecaEngine.Editor;

public class RenderResourceManager
{
	private readonly DiligentBatchRenderer _batchRenderer;
	private readonly NativeStack<int> _freeSlots;
	private readonly EntityStore _store;
	private readonly ArchetypeQuery<BatchRenderInfo> _deadEntitiesQuery;
	private readonly int[] _batchCounts;

	public int totalInstances;
	public int totalFreeSlot;
	public bool cullFrustum;

	private BatchSubset _renderSubset;

	public RenderResourceManager(int totalInstances, int totalBatch, EntityStore store,
		DiligentBatchRenderer batchRenderer)
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

		_deadEntitiesQuery = _store.Query<BatchRenderInfo>();
	}

	public void RecycleDeadHandles()
	{
		/*_deadEntitiesQuery.ForEach((chunk, entities) =>
		{
		    ref RenderHandleComponent handle = ref chunk[0];
		    entities.
		    _freeSlots.Push(handle.GpuSlotIndex);
		    entity.Remove<RenderHandleComponent>();
		});*/
	}

	public unsafe bool RegisterRenderable(Entity entity, BatchId batchId)
	{
		if (_freeSlots.Count == 0)
		{
			Console.WriteLine("RenderResourceManager: No free slots available!");
			return false;
		}

		var slotIndex = _freeSlots.Pop();
		totalFreeSlot = _freeSlots.Count;

		_renderSubset.instances[slotIndex] = new IndirectInstance
		{
			batchId = batchId,
			objectId = slotIndex
		};

		if (batchId.batchId >= 0 && batchId.batchId < _batchCounts.Length)
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

		return true;
	}
}