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
	private int[] _batchCounts;

	public int totalInstances;
	public int totalFreeSlot;
	public bool cullFrustum;

	private BatchSubset _renderSubset;

	// Верхняя граница ЗАНЯТЫХ слотов (максимальный выданный индекс + 1), а не их количество.
	// Слоты выдаются из стека свободных, поэтому занятые индексы РАЗРЕЖЕНЫ: после освобождения
	// пачки слотов стек отдаёт их в обратном порядке, и следующая, более мелкая пачка инстансов
	// садится в ХВОСТ диапазона (например слоты 8..9 при 10 освобождённых). Culling-шейдер
	// (BatchingInstancingCS) обходит Instances[0..drawCount) и пропускает дырки по batchId/objectId
	// < 0, так что drawCount - это граница ИНДЕКСА. Если подставить туда количество занятых слотов
	// (totalInstances - totalFreeSlot), шейдер обойдёт только начало массива, где после churn-а
	// лежат одни дырки, и не нарисует ничего - именно так пропадали превью сабмешей в
	// AssetBrowser/Inspector после бейка целой модели (см. ModelIconBaker.CreateStageEntities).
	private int _slotHighWaterMark;

	/// <summary>
	/// Сколько первых слотов массива инстансов должен обойти culling-шейдер - см.
	/// <see cref="_slotHighWaterMark"/>. Подставляется в CullData.drawCount
	/// (<see cref="DecaEngine.Editor.ECS.SimpleCullingAndRenderSystem"/>,
	/// <see cref="DecaEngine.Editor.ECS.CullingAndRenderSystem"/>).
	/// </summary>
	public int DrawInstanceCount => _slotHighWaterMark;

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
			_freeSlots.Push(batchInfo.GpuSlotIndex);
			totalFreeSlot = _freeSlots.Count;

			// Опустить границу можно только когда занятых слотов не осталось вовсе - в общем случае
			// дырка в середине не даёт её уменьшить (см. _slotHighWaterMark). Этого достаточно для
			// сценариев превью/бейка, где сцена между этапами очищается целиком
			// (ModelIconBaker.ClearStageEntities, ModelPreviewViewport.ClearInstances): следующий этап
			// снова начинает набирать слоты с нуля, а не тянет за собой границу от прошлой модели.
			if (_freeSlots.Count == totalInstances)
			{
				_slotHighWaterMark = 0;
			}

			_renderSubset.instances[batchInfo.GpuSlotIndex] = new IndirectInstance { batchId = new BatchId(-1), objectId = -1 };

			if (batchInfo.BatchId.batchId >= 0 && batchInfo.BatchId.batchId < _batchCounts.Length)
			{
				_batchCounts[batchInfo.BatchId.batchId]--;
				for (var i = batchInfo.BatchId.batchId + 1; i < _batchCounts.Length; i++)
				{
					_renderSubset.countData.GetRef(i)--;
				}
			}

			entity.RemoveComponent<BatchRenderInfo>();
			entity.RemoveComponent<LinkDrawInfo>();

			_batchRenderer.MarkInstancesContentDirty();
		}
	}
}